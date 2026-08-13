using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using Lumina.Data.Files;
using Lumina.Data.Parsing;

namespace DragAndDropTexturing.Windows
{
    public class ExtractedMesh
    {
        public List<Vector3> Positions = new();
        public List<Vector2> UVs = new();
        public List<Vector3> Normals = new();
        public List<Vector4> BoneWeights = new();
        public List<Vector4> BoneIndices = new();
        public List<uint> Indices = new();
        public string MaterialPath = "";
        public ushort[] BoneTable = Array.Empty<ushort>();
        /// <summary>MDL bone name for each entry in the model header bone list (bone table values index into this).</summary>
        public string[] MdlBoneNames = Array.Empty<string>();
        /// <summary>Maps mesh bone index to skeleton bone index (from MDL element IDs).</summary>
        public ushort[] ElementSkeletonBones = Array.Empty<ushort>();
        public bool HasSkinning
        {
            get
            {
                if (BoneWeights.Count == 0 || BoneIndices.Count != BoneWeights.Count)
                    return false;
                for (int i = 0; i < BoneWeights.Count; i++)
                {
                    var w = BoneWeights[i];
                    if (w.X + w.Y + w.Z + w.W > 0.001f)
                        return true;
                }
                return false;
            }
        }
    }

    public class MdlParser
    {
        /// <summary>
        /// Parses an FFXIV MdlFile and extracts the LOD0 meshes into a format ready for D3D11 rendering.
        /// Unpacks Half2 UVs and Dec3N4 Normals into standard floating point Vectors.
        /// </summary>
        public static List<ExtractedMesh> Parse(MdlFile mdlFile)
        {
            var extractedMeshes = new List<ExtractedMesh>();

            try
            {
                // The model can have multiple LODs. LOD0 is the highest quality.
                if (mdlFile.Lods.Length == 0) return GetDummyCube();

                var lod0 = mdlFile.Lods[0];
                int meshIndexStart = lod0.MeshIndex;
                int meshCount = lod0.MeshCount;

                for (int m = 0; m < meshCount; m++)
                {
                    var meshStruct = mdlFile.Meshes[meshIndexStart + m];
                    var extracted = new ExtractedMesh();

                    // 1. Extract Indices
                    uint indexOffset = mdlFile.FileHeader.IndexOffset[0] + (meshStruct.StartIndex * 2);
                    using (var ms = new MemoryStream(mdlFile.Data))
                    using (var reader = new BinaryReader(ms))
                    {
                        if (indexOffset < ms.Length)
                        {
                            ms.Position = indexOffset;
                            for (int i = 0; i < meshStruct.IndexCount; i++)
                            {
                                if (ms.Position + 2 > ms.Length) break;
                                extracted.Indices.Add(reader.ReadUInt16());
                            }
                        }

                        // 2. Extract Vertices
                        var declarations = mdlFile.VertexDeclarations[meshIndexStart + m].VertexElements;

                        for (int v = 0; v < meshStruct.VertexCount; v++)
                        {
                            Vector3 pos = Vector3.Zero;
                            Vector3 norm = Vector3.Zero;
                            Vector2 uv = Vector2.Zero;
                            Vector4 boneWeights = Vector4.Zero;
                            Vector4 boneIndices = Vector4.Zero;
                            bool hasWeights = false;
                            bool hasIndices = false;

                            foreach (var decl in declarations)
                            {
                                if (decl.Stream >= meshStruct.VertexBufferOffset.Length) continue;

                                uint currentStreamOffset = mdlFile.FileHeader.VertexOffset[0] + meshStruct.VertexBufferOffset[decl.Stream];
                                byte currentStride = meshStruct.VertexBufferStride[decl.Stream];

                                long targetPos = currentStreamOffset + (v * currentStride) + decl.Offset;
                                if (targetPos < 0 || targetPos >= ms.Length) continue;

                                ms.Position = targetPos;

                                try
                                {
                                    // Usage 0 = Position
                                    if (decl.Usage == 0)
                                    {
                                        if (decl.Type == 2 && ms.Position + 12 <= ms.Length) // Single3
                                        {
                                            pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                                        }
                                        else if (decl.Type == 3 && ms.Position + 16 <= ms.Length) // Single4
                                        {
                                            pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                                            reader.ReadSingle(); // Discard W
                                        }
                                    }
                                    // Usage 3 = Normal
                                    else if (decl.Usage == 3) 
                                    {
                                        if (decl.Type == 8 && ms.Position + 4 <= ms.Length) // ByteFloat4 (Dec3N4)
                                        {
                                            uint packed = reader.ReadUInt32();
                                            int x = (int)(packed & 0x3FF);
                                            if ((x & 0x200) != 0) x |= unchecked((int)0xFFFFFC00);
                                            
                                            int y = (int)((packed >> 10) & 0x3FF);
                                            if ((y & 0x200) != 0) y |= unchecked((int)0xFFFFFC00);
                                            
                                            int z = (int)((packed >> 20) & 0x3FF);
                                            if ((z & 0x200) != 0) z |= unchecked((int)0xFFFFFC00);

                                            norm = new Vector3(x / 511.0f, y / 511.0f, z / 511.0f);
                                        }
                                        else if (decl.Type == 2 && ms.Position + 12 <= ms.Length) // Single3
                                        {
                                            norm = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                                        }
                                    }
                                    // Usage 1 = BlendWeight
                                    else if (decl.Usage == 1)
                                    {
                                        if (TryReadBoneWeights(reader, ms, decl.Type, ref boneWeights))
                                            hasWeights = true;
                                    }
                                    // Usage 2 = BlendIndices
                                    else if (decl.Usage == 2)
                                    {
                                        if (TryReadBoneIndices(reader, ms, decl.Type, ref boneIndices))
                                            hasIndices = true;
                                    }
                                    // Usage 4 = TexCoord/UV
                                    else if (decl.Usage == 4)
                                    {
                                        if (decl.Type == 13 && ms.Position + 4 <= ms.Length) // Half2
                                        {
                                            ushort hX = reader.ReadUInt16();
                                            ushort hY = reader.ReadUInt16();
                                            uv = new Vector2((float)BitConverter.Int16BitsToHalf((short)hX), (float)BitConverter.Int16BitsToHalf((short)hY));
                                        }
                                        else if (decl.Type == 14 && ms.Position + 8 <= ms.Length) // Half4
                                        {
                                            ushort hX = reader.ReadUInt16();
                                            ushort hY = reader.ReadUInt16();
                                            reader.ReadUInt32(); // Discard Z, W
                                            uv = new Vector2((float)BitConverter.Int16BitsToHalf((short)hX), (float)BitConverter.Int16BitsToHalf((short)hY));
                                        }
                                    }
                                }
                                catch (EndOfStreamException)
                                {
                                    // Safely catch any accidental overreads
                                }
                            }

                            extracted.Positions.Add(pos);
                            extracted.Normals.Add(norm);
                            extracted.UVs.Add(uv);
                            if (!hasWeights || !hasIndices)
                            {
                                TryExtractSkinningFixedLayouts(
                                    ms, reader,
                                    Array.ConvertAll(meshStruct.VertexBufferOffset, x => (int)x),
                                    meshStruct.VertexBufferStride, v,
                                    mdlFile.FileHeader.VertexOffset[0],
                                    ref boneWeights, ref boneIndices, ref hasWeights, ref hasIndices);
                            }
                            if (!hasWeights || !hasIndices)
                            {
                                TryReadSkinningHeuristic(
                                    ms, reader,
                                    Array.ConvertAll(meshStruct.VertexBufferOffset, x => (int)x),
                                    meshStruct.VertexBufferStride, v,
                                    mdlFile.FileHeader.VertexOffset[0],
                                    ref boneWeights, ref boneIndices, ref hasWeights, ref hasIndices);
                            }

                            extracted.BoneWeights.Add(hasWeights ? NormalizeBoneWeights(boneWeights) : Vector4.Zero);
                            extracted.BoneIndices.Add(hasIndices ? boneIndices : Vector4.Zero);
                        }
                    }

                    extractedMeshes.Add(extracted);
                }

                return extractedMeshes.Count > 0 ? extractedMeshes : GetDummyCube();
            }
            catch (Exception)
            {
                // If Lumina's parsing throws due to Dawntrail changes, return a fallback cube
                return GetDummyCube();
            }
        }

        public static List<ExtractedMesh> GetDummyCube()
        {
            var mesh = new ExtractedMesh();
            
            // Front face
            mesh.Positions.Add(new Vector3(-0.5f, -0.5f, -0.5f)); mesh.Normals.Add(new Vector3(0, 0, -1)); mesh.UVs.Add(new Vector2(0, 1));
            mesh.Positions.Add(new Vector3(-0.5f,  0.5f, -0.5f)); mesh.Normals.Add(new Vector3(0, 0, -1)); mesh.UVs.Add(new Vector2(0, 0));
            mesh.Positions.Add(new Vector3( 0.5f,  0.5f, -0.5f)); mesh.Normals.Add(new Vector3(0, 0, -1)); mesh.UVs.Add(new Vector2(1, 0));
            mesh.Positions.Add(new Vector3( 0.5f, -0.5f, -0.5f)); mesh.Normals.Add(new Vector3(0, 0, -1)); mesh.UVs.Add(new Vector2(1, 1));
            
            // Back face
            mesh.Positions.Add(new Vector3(-0.5f, -0.5f,  0.5f)); mesh.Normals.Add(new Vector3(0, 0, 1)); mesh.UVs.Add(new Vector2(1, 1));
            mesh.Positions.Add(new Vector3( 0.5f, -0.5f,  0.5f)); mesh.Normals.Add(new Vector3(0, 0, 1)); mesh.UVs.Add(new Vector2(0, 1));
            mesh.Positions.Add(new Vector3( 0.5f,  0.5f,  0.5f)); mesh.Normals.Add(new Vector3(0, 0, 1)); mesh.UVs.Add(new Vector2(0, 0));
            mesh.Positions.Add(new Vector3(-0.5f,  0.5f,  0.5f)); mesh.Normals.Add(new Vector3(0, 0, 1)); mesh.UVs.Add(new Vector2(1, 0));

            uint[] indices = {
                0, 1, 2, 0, 2, 3, // Front
                4, 5, 6, 4, 6, 7, // Back
                1, 7, 6, 1, 6, 2, // Top
                0, 3, 5, 0, 5, 4, // Bottom
                3, 2, 6, 3, 6, 5, // Right
                0, 4, 7, 0, 7, 1  // Left
            };
            
            mesh.Indices.AddRange(indices);
            return new List<ExtractedMesh> { mesh };
        }
        /// <summary>
        /// Parses an FFXIV .mdl file directly from raw bytes on disk, bypassing Lumina entirely.
        /// Based on the TexTools xivModdingFramework implementation (MdlModelData.Read, Mdl.cs).
        /// 
        /// File layout:
        ///   [0x00] ModelFileHeader (0x44 = 68 bytes)
        ///   [0x44] VertexDeclarations (136 bytes each × vertexDeclCount)
        ///   [....]  PathCount(4) + PathBlockSize(4) + PathBlock(PathBlockSize)
        ///   [....]  MdlModelData (56 bytes)
        ///   [....]  ElementIds (32 bytes each)
        ///   [....]  LODs (56 bytes each × 3)
        ///   [....]  ExtraLODs (if Flags2.HasExtraMeshes)
        ///   [....]  MeshStructs (36 bytes each × meshCount)
        ///   [....]  AttributeOffsets, TerrainShadow, Submeshes, etc.
        ///
        /// Vertex/Index data offsets are in the FileHeader as absolute positions in the file.
        /// </summary>
        public static List<ExtractedMesh> ParseFromDisk(string filePath, out string statusMessage)
        {
            try
            {
                return ParseFromBytes(File.ReadAllBytes(filePath), out statusMessage);
            }
            catch (Exception ex)
            {
                statusMessage = "File read error: " + ex.Message;
                return GetDummyCube();
            }
        }

        public static List<ExtractedMesh> ParseFromBytes(byte[] fileData, out string statusMessage)
        {
            statusMessage = "";
            try
            {
                using var ms = new MemoryStream(fileData);
                using var reader = new BinaryReader(ms);

                // ModelFileHeader (0x44 = 68 bytes)
                // TexTools reads version as ushort at offset 0, but Lumina reads uint32.
                // We read it as uint32 to stay consistent. The low bits are the version number.
                uint versionRaw = reader.ReadUInt32();
                int mdlVersion = (int)(versionRaw & 0xFFFF);
                if (mdlVersion >= 6) mdlVersion = 6; // Dawntrail+

                uint stackSize = reader.ReadUInt32();
                uint runtimeSize = reader.ReadUInt32();
                ushort vertexDeclCount = reader.ReadUInt16();
                ushort headerMaterialCount = reader.ReadUInt16();

                uint[] vertexOffset = { reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32() };
                uint[] indexOffset = { reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32() };
                uint[] vertexBufferSize = { reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32() };
                uint[] indexBufferSize = { reader.ReadUInt32(), reader.ReadUInt32(), reader.ReadUInt32() };

                byte lodCount = reader.ReadByte();
                reader.ReadByte(); // EnableIndexBufferStreaming
                reader.ReadByte(); // EnableEdgeGeometry
                reader.ReadByte(); // Padding

                // Now at 0x44 (68) — start of vertex declarations

                //VertexDeclarations (136 bytes each)
                const int VERTEX_ELEMENT_SIZE = 8;
                const int MAX_VERTEX_ELEMENTS = 17; // 17 * 8 = 136

                var vertexDeclarations = new List<List<(byte stream, byte offset, byte type, byte usage)>>();
                for (int d = 0; d < vertexDeclCount; d++)
                {
                    var elements = new List<(byte stream, byte offset, byte type, byte usage)>();
                    for (int e = 0; e < MAX_VERTEX_ELEMENTS; e++)
                    {
                        byte eStream = reader.ReadByte();
                        byte eOffset = reader.ReadByte();
                        byte eType = reader.ReadByte();
                        byte eUsage = reader.ReadByte();
                        reader.ReadUInt32(); // usageIndex(1) + padding(3)

                        if (eStream != 0xFF)
                            elements.Add((eStream, eOffset, eType, eUsage));
                    }
                    vertexDeclarations.Add(elements);
                }

                // Path Data (TexTools: PathCount + PathBlockSize + PathBlock)
                int pathCount = reader.ReadInt32();
                int pathBlockSize = reader.ReadInt32();
                byte[] pathBlock = reader.ReadBytes(pathBlockSize);

                var pathOffsets = new List<uint>();
                var pathStrings = new List<string>();
                var mtrlStrings = new List<string>();
                int strStart = 0;
                for (int i = 0; i < pathBlockSize; i++)
                {
                    if (pathBlock[i] == 0)
                    {
                        if (i > strStart)
                        {
                            pathOffsets.Add((uint)strStart);
                            string s = System.Text.Encoding.UTF8.GetString(pathBlock, strStart, i - strStart);
                            pathStrings.Add(s);
                            if (s.EndsWith(".mtrl", StringComparison.OrdinalIgnoreCase))
                                mtrlStrings.Add(s);
                        }
                        strStart = i + 1;
                    }
                }

                //MdlModelData (56 bytes — exact TexTools MdlModelData.Read)
                float radius = reader.ReadSingle();         // 4
                short meshCount = reader.ReadInt16();         // 2
                short attributeCount = reader.ReadInt16();    // 2
                short meshPartCount = reader.ReadInt16();     // 2
                short materialCount = reader.ReadInt16();     // 2
                short boneCount = reader.ReadInt16();         // 2
                short boneSetCount = reader.ReadInt16();      // 2
                short shapeCount = reader.ReadInt16();        // 2
                short shapePartCount = reader.ReadInt16();    // 2
                ushort shapeDataCount = reader.ReadUInt16();  // 2
                byte lodCountModel = reader.ReadByte();       // 1
                byte flags1 = reader.ReadByte();              // 1
                ushort elementIdCount = reader.ReadUInt16();  // 2
                byte terrainShadowMeshCount = reader.ReadByte(); // 1
                byte flags2 = reader.ReadByte();              // 1
                float modelClipOutDist = reader.ReadSingle(); // 4
                float shadowClipOutDist = reader.ReadSingle();// 4
                ushort furniturePartBBCount = reader.ReadUInt16(); // 2
                short terrainShadowPartCount = reader.ReadInt16(); // 2
                byte flags3 = reader.ReadByte();              // 1
                byte bgChangeMaterialIdx = reader.ReadByte(); // 1
                byte bgCrestChangeMaterialIdx = reader.ReadByte(); // 1
                byte neckMorphTableSize = reader.ReadByte();  // 1
                short boneSetSize = reader.ReadInt16();        // 2
                reader.ReadInt16(); // Unknown13                // 2
                reader.ReadInt16(); // Patch72TableSize         // 2
                reader.ReadInt16(); // Unknown15                // 2
                reader.ReadInt16(); // Unknown16                // 2
                reader.ReadInt16(); // Unknown17                // 2
                // Total: 56 bytes ✓

                //ElementIds (32 bytes each: uint ElementId, uint ParentBone, float3 Translate, float3 Rotate)
                var elementSkeletonBones = new List<ushort>();
                for (int i = 0; i < elementIdCount; i++)
                {
                    uint elementBone = reader.ReadUInt32();
                    reader.ReadUInt32(); // ParentBone
                    reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); // Translate
                    reader.ReadSingle(); reader.ReadSingle(); reader.ReadSingle(); // Rotate
                    elementSkeletonBones.Add((ushort)Math.Min(elementBone, ushort.MaxValue));
                }

                //LOD structs (56 bytes each × 3)
                // TexTools reads exactly: MeshIndex(2), MeshCount(2), ModelLodRange(4), TextureLodRange(4),
                //   WaterMesh(4), ShadowMesh(4), TerrainShadowMesh(4), FogMesh(4),
                //   EdgeGeoSize(4), EdgeGeoOffset(4), PolygonCount(4), Unknown1(4),
                //   VertexBufferSize(4), IndexBufferSize(4), VertexDataOffset(4), IndexDataOffset(4) = 56
                ushort lod0MeshIndex = 0, lod0MeshCount = 0;
                uint lod0VertexDataOffset = 0, lod0IndexDataOffset = 0;

                for (int i = 0; i < 3; i++)
                {
                    ushort lodMeshIdx = reader.ReadUInt16();
                    ushort lodMeshCnt = reader.ReadUInt16();
                    reader.ReadSingle(); // ModelLodRange
                    reader.ReadSingle(); // TextureLodRange
                    reader.ReadUInt16(); reader.ReadUInt16(); // WaterMesh
                    reader.ReadUInt16(); reader.ReadUInt16(); // ShadowMesh
                    reader.ReadUInt16(); reader.ReadUInt16(); // TerrainShadowMesh
                    reader.ReadUInt16(); reader.ReadUInt16(); // FogMesh
                    reader.ReadInt32();  // EdgeGeometrySize
                    reader.ReadInt32();  // EdgeGeometryOffset
                    reader.ReadInt32();  // PolygonCount
                    reader.ReadInt32();  // Unknown1
                    reader.ReadInt32();  // VertexBufferSize
                    reader.ReadInt32();  // IndexBufferSize
                    uint lodVtxDataOff = reader.ReadUInt32(); // VertexDataOffset
                    uint lodIdxDataOff = reader.ReadUInt32(); // IndexDataOffset

                    if (i == 0)
                    {
                        lod0MeshIndex = lodMeshIdx;
                        lod0MeshCount = lodMeshCnt;
                        lod0VertexDataOffset = lodVtxDataOff;
                        lod0IndexDataOffset = lodIdxDataOff;
                    }
                }

                //ExtraLods (if HasExtraMeshes flag is set in Flags2 bit 0x10)
                bool hasExtraMeshes = (flags2 & 0x10) != 0;
                if (hasExtraMeshes)
                {
                    // 3 LODs × 12 extra mesh type pairs (each 4 bytes = ushort+ushort)
                    reader.ReadBytes(3 * 12 * 4);
                }

                //Mesh Structs (36 bytes each)
                // TexTools: VertexCount(4!), IndexCount(4), MaterialIndex(2), SubMeshIndex(2),
                //           SubMeshCount(2), BoneTableIndex(2), IndexDataOffset(4),
                //           VertexDataOffset[3](12), VertexDataEntrySize[3](3), VertexStreamCount(1) = 36
                // IMPORTANT: TexTools reads VertexCount as Int32 (not UInt16+padding)!
                var meshStructs = new List<(int vertexCount, int indexCount, int startIndex,
                    int[] vbOffset, byte[] vbStride, short materialIndex, short boneTableIndex)>();

                for (int m = 0; m < meshCount; m++)
                {
                    int vtxCount = reader.ReadInt32();        // 4 (TexTools uses ReadInt32 for this)
                    int idxCount = reader.ReadInt32();         // 4
                    short matIndex = reader.ReadInt16();                        // MaterialIndex
                    reader.ReadInt16();                        // SubMeshIndex
                    reader.ReadInt16();                        // SubMeshCount
                    short boneTableIndex = reader.ReadInt16();  // BoneTableIndex
                    int startIdx = reader.ReadInt32();          // 4
                    int[] vbOff = { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() }; // 12
                    byte[] vbStr = { reader.ReadByte(), reader.ReadByte(), reader.ReadByte() };   // 3
                    reader.ReadByte();                          // VertexStreamCount                 // 1
                    // Total: 36 ✓

                    meshStructs.Add((vtxCount, idxCount, startIdx, vbOff, vbStr, matIndex, boneTableIndex));
                }

                var boneTables = ReadMdlBoneTables(
                    reader,
                    mdlVersion,
                    boneSetCount,
                    boneSetSize,
                    terrainShadowMeshCount,
                    attributeCount,
                    meshPartCount,
                    terrainShadowPartCount,
                    materialCount,
                    boneCount,
                    pathBlock,
                    pathOffsets,
                    pathStrings,
                    out string[] mdlBoneNames);

                //Extract geometry from data region
                // The vertex/index data offsets in the FileHeader (vertexOffset[], indexOffset[])
                // are absolute byte positions within the file.
                var extractedMeshes = new List<ExtractedMesh>();

                for (int m = 0; m < lod0MeshCount && m < meshStructs.Count; m++)
                {
                    int meshIdx = lod0MeshIndex + m;
                    if (meshIdx >= meshStructs.Count) break;

                    var mesh = meshStructs[meshIdx];
                    var extracted = new ExtractedMesh();
                    extracted.MdlBoneNames = mdlBoneNames;
                    extracted.ElementSkeletonBones = elementSkeletonBones.Count > 0
                        ? elementSkeletonBones.ToArray()
                        : Array.Empty<ushort>();
                    if (mesh.boneTableIndex >= 0 && mesh.boneTableIndex < boneTables.Count)
                        extracted.BoneTable = boneTables[mesh.boneTableIndex];
                    else if (boneTables.Count > 0 && boneTables[0].Length > 0)
                        extracted.BoneTable = boneTables[0];
                    
                    if (mesh.materialIndex >= 0 && mesh.materialIndex < mtrlStrings.Count)
                    {
                        extracted.MaterialPath = mtrlStrings[mesh.materialIndex];
                    }

                    // Read indices (16-bit unsigned, 2 bytes each)
                    // startIndex is an index into the index buffer (not byte offset)
                    long idxByteOffset = indexOffset[0] + (mesh.startIndex * 2L);

                    if (idxByteOffset >= 0 && idxByteOffset + mesh.indexCount * 2L <= fileData.Length)
                    {
                        ms.Position = idxByteOffset;
                        for (int i = 0; i < mesh.indexCount; i++)
                            extracted.Indices.Add(reader.ReadUInt16());
                    }

                    // Read vertices using vertex declarations
                    var decl = (meshIdx < vertexDeclarations.Count) ? vertexDeclarations[meshIdx] : vertexDeclarations[0];

                    for (int v = 0; v < mesh.vertexCount; v++)
                    {
                        Vector3 pos = Vector3.Zero;
                        Vector3 norm = Vector3.UnitY;
                        Vector2 uv = Vector2.Zero;
                        Vector4 boneWeights = Vector4.Zero;
                        Vector4 boneIndices = Vector4.Zero;
                        bool uvRead = false;
                        bool hasWeights = false;
                        bool hasIndices = false;

                        foreach (var elem in decl)
                        {
                            if (elem.stream >= 3) continue;
                            long streamBase = vertexOffset[0] + mesh.vbOffset[elem.stream];
                            int stride = mesh.vbStride[elem.stream];
                            if (stride == 0) continue;
                            
                            long targetPos = streamBase + ((long)v * stride) + elem.offset;
                            if (targetPos < 0 || targetPos >= fileData.Length) continue;
                            ms.Position = targetPos;

                            try
                            {
                                if (elem.usage == 0) // Position
                                {
                                    if (elem.type == 2 && ms.Position + 12 <= fileData.Length) // Float3
                                        pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                                    else if (elem.type == 3 && ms.Position + 16 <= fileData.Length) // Float4
                                    {
                                        pos = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                                        reader.ReadSingle(); // w
                                    }
                                    else if (elem.type == 14 && ms.Position + 8 <= fileData.Length) // Half4
                                    {
                                        pos = new Vector3(
                                            (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16()),
                                            (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16()),
                                            (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16()));
                                        reader.ReadUInt16(); // w
                                    }
                                }
                                else if (elem.usage == 3) // Normal
                                {
                                    if (elem.type == 8 && ms.Position + 4 <= fileData.Length) // Dec3N
                                    {
                                        uint packed = reader.ReadUInt32();
                                        int x = (int)(packed & 0x3FF); if ((x & 0x200) != 0) x |= unchecked((int)0xFFFFFC00);
                                        int y = (int)((packed >> 10) & 0x3FF); if ((y & 0x200) != 0) y |= unchecked((int)0xFFFFFC00);
                                        int z = (int)((packed >> 20) & 0x3FF); if ((z & 0x200) != 0) z |= unchecked((int)0xFFFFFC00);
                                        norm = new Vector3(x / 511.0f, y / 511.0f, z / 511.0f);
                                    }
                                    else if (elem.type == 2 && ms.Position + 12 <= fileData.Length) // Float3
                                        norm = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                                    else if (elem.type == 3 && ms.Position + 16 <= fileData.Length) // Float4
                                        norm = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                                }
                                else if (elem.usage == 1)
                                {
                                    if (TryReadBoneWeights(reader, ms, elem.type, ref boneWeights))
                                        hasWeights = true;
                                }
                                else if (elem.usage == 2)
                                {
                                    if (TryReadBoneIndices(reader, ms, elem.type, ref boneIndices))
                                        hasIndices = true;
                                }
                                else if (elem.usage == 4 && !uvRead) // UV (TexCoord)
                                {
                                    if (elem.type == 13 && ms.Position + 4 <= fileData.Length) // Half2
                                    {
                                        uv = new Vector2(
                                            (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16()),
                                            (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16()));
                                    }
                                    else if (elem.type == 14 && ms.Position + 8 <= fileData.Length) // Half4
                                    {
                                        uv = new Vector2(
                                            (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16()),
                                            (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16()));
                                    }
                                    else if (elem.type == 1 && ms.Position + 8 <= fileData.Length) // Float2
                                    {
                                        uv = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                                    }
                                    else if (elem.type == 2 && ms.Position + 12 <= fileData.Length) // Float3
                                    {
                                        uv = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                                    }
                                    else if (elem.type == 3 && ms.Position + 16 <= fileData.Length) // Float4
                                    {
                                        uv = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                                    }
                                    uvRead = true;
                                }
                            }
                            catch (EndOfStreamException) { }
                        }

                        extracted.Positions.Add(pos);
                        extracted.Normals.Add(norm);
                        extracted.UVs.Add(uv);
                        if (!hasWeights || !hasIndices)
                        {
                            TryExtractSkinningFixedLayouts(
                                ms, reader, mesh.vbOffset, mesh.vbStride, v,
                                vertexOffset[0],
                                ref boneWeights, ref boneIndices, ref hasWeights, ref hasIndices);
                        }
                        if (!hasWeights || !hasIndices)
                        {
                            TryReadSkinningHeuristic(
                                ms, reader, mesh.vbOffset, mesh.vbStride, v,
                                vertexOffset[0],
                                ref boneWeights, ref boneIndices, ref hasWeights, ref hasIndices);
                        }

                        extracted.BoneWeights.Add(hasWeights ? NormalizeBoneWeights(boneWeights) : Vector4.Zero);
                        extracted.BoneIndices.Add(hasIndices ? boneIndices : Vector4.Zero);
                    }

                    if (extracted.Positions.Count > 0 && extracted.Indices.Count > 0)
                        extractedMeshes.Add(extracted);
                }

                if (extractedMeshes.Count > 0)
                {
                    int totalVerts = 0, totalIdx = 0;
                    int skinVerts = 0;
                    int uvNonZero = 0;
                    float uvMinX = float.MaxValue, uvMinY = float.MaxValue;
                    float uvMaxX = float.MinValue, uvMaxY = float.MinValue;
                    foreach (var em in extractedMeshes)
                    {
                        totalVerts += em.Positions.Count;
                        totalIdx += em.Indices.Count;
                        if (em.HasSkinning)
                            skinVerts += em.BoneWeights.Count(w => w.X + w.Y + w.Z + w.W > 0.001f);
                        foreach (var uv in em.UVs)
                        {
                            if (uv.X != 0 || uv.Y != 0) uvNonZero++;
                            if (uv.X < uvMinX) uvMinX = uv.X;
                            if (uv.Y < uvMinY) uvMinY = uv.Y;
                            if (uv.X > uvMaxX) uvMaxX = uv.X;
                            if (uv.Y > uvMaxY) uvMaxY = uv.Y;
                        }
                    }
                    int tableEntries = boneTables.Sum(t => t.Length);
                    statusMessage = $"Loaded {extractedMeshes.Count} mesh(es) ({totalVerts} verts, {totalIdx / 3} tris) from disk. " +
                                    $"[v{mdlVersion}, vtxOff=0x{vertexOffset[0]:X}, idxOff=0x{indexOffset[0]:X}] " +
                                    $"Skin: {skinVerts}/{totalVerts} verts, boneTables={boneTables.Count} ({tableEntries} entries), mdlBones={mdlBoneNames.Length}, elements={elementSkeletonBones.Count}. " +
                                    $"UVs: {uvNonZero}/{totalVerts} nonzero, range ({uvMinX:F3},{uvMinY:F3})-({uvMaxX:F3},{uvMaxY:F3})";
                    // Append vertex declaration info for first mesh
                    if (vertexDeclarations.Count > 0)
                    {
                        var declInfo = new System.Text.StringBuilder(" | VtxDecl[0]: ");
                        foreach (var e in vertexDeclarations[0])
                            declInfo.Append($"[s{e.stream} off{e.offset} t{e.type} u{e.usage}] ");
                        statusMessage += declInfo.ToString();
                    }
                    if (mdlBoneNames.Length > 0)
                    {
                        statusMessage += $" | sampleBones: {string.Join(", ", mdlBoneNames.Take(3))}";
                    }
                    return extractedMeshes;
                }
                else
                {
                    statusMessage = $"Parsed header (v{mdlVersion}, {meshCount} meshes, {lodCount} LODs, " +
                                    $"lod0: mesh {lod0MeshIndex}×{lod0MeshCount}) but no vertex data in file. " +
                                    $"VtxOff=0x{vertexOffset[0]:X}, IdxOff=0x{indexOffset[0]:X}, " +
                                    $"VtxSize={vertexBufferSize[0]}, IdxSize={indexBufferSize[0]}, FileSize={fileData.Length}";
                    return GetDummyCube();
                }
            }
            catch (Exception ex)
            {
                statusMessage = "Raw MDL parse error: " + ex.Message;
                return GetDummyCube();
            }
        }

        private static List<ushort[]> ReadMdlBoneTables(
            BinaryReader reader,
            int mdlVersion,
            short boneTableCount,
            short boneTableArrayCountTotal,
            byte terrainShadowMeshCount,
            short attributeCount,
            short submeshCount,
            short terrainShadowSubmeshCount,
            short materialCount,
            short boneCount,
            byte[] pathBlock,
            List<uint> pathOffsets,
            List<string> pathStrings,
            out string[] mdlBoneNames)
        {
            mdlBoneNames = Array.Empty<string>();
            var tables = new List<ushort[]>();
            try
            {
                for (int i = 0; i < attributeCount; i++)
                    reader.ReadUInt32();

                reader.ReadBytes(terrainShadowMeshCount * 20);
                reader.ReadBytes(submeshCount * 16);
                reader.ReadBytes(terrainShadowSubmeshCount * 12);

                for (int i = 0; i < materialCount; i++)
                    reader.ReadUInt32();

                mdlBoneNames = new string[Math.Max(boneCount, (short)0)];
                for (int i = 0; i < boneCount; i++)
                {
                    uint offset = reader.ReadUInt32();
                    mdlBoneNames[i] = ResolvePathString(pathBlock, pathOffsets, pathStrings, offset);
                }

                if (boneTableCount <= 0)
                    return tables;

                if (mdlVersion >= 6)
                {
                    for (int i = 0; i < boneTableCount; i++)
                    {
                        long tableHeaderStart = reader.BaseStream.Position;
                        ushort offset = reader.ReadUInt16();
                        ushort size = reader.ReadUInt16();
                        long returnPos = reader.BaseStream.Position;

                        if (size == 0)
                        {
                            tables.Add(Array.Empty<ushort>());
                            continue;
                        }

                        long indexPos = tableHeaderStart + (long)offset * 4;
                        if (indexPos < 0 || indexPos + size * 2L > reader.BaseStream.Length)
                        {
                            tables.Add(Array.Empty<ushort>());
                            reader.BaseStream.Position = returnPos;
                            continue;
                        }

                        reader.BaseStream.Position = indexPos;
                        var slice = new ushort[size];
                        for (int j = 0; j < size; j++)
                            slice[j] = reader.ReadUInt16();
                        tables.Add(slice);
                        reader.BaseStream.Position = returnPos;
                    }

                    if (boneTableArrayCountTotal > 0)
                        reader.ReadBytes(boneTableArrayCountTotal * 2);
                }
                else
                {
                    for (int i = 0; i < boneTableCount; i++)
                    {
                        var block = reader.ReadBytes(132);
                        if (block.Length < 2)
                        {
                            tables.Add(Array.Empty<ushort>());
                            continue;
                        }
                        int count = BitConverter.ToUInt16(block, 0);
                        count = Math.Min(count, (block.Length - 2) / 2);
                        var slice = new ushort[count];
                        for (int j = 0; j < count; j++)
                            slice[j] = BitConverter.ToUInt16(block, 2 + j * 2);
                        tables.Add(slice);
                    }
                }
            }
            catch (EndOfStreamException)
            {
                // Bone tables are optional for rendering; rigid fallback remains available.
            }

            return tables;
        }

        private static string ResolvePathString(byte[] pathBlock, List<uint> pathOffsets, List<string> pathStrings, uint offset)
        {
            int idx = pathOffsets.IndexOf(offset);
            if (idx >= 0)
                return pathStrings[idx];

            if (pathBlock != null && offset < pathBlock.Length)
            {
                int start = (int)offset;
                int end = start;
                while (end < pathBlock.Length && pathBlock[end] != 0)
                    end++;
                if (end > start)
                    return System.Text.Encoding.UTF8.GetString(pathBlock, start, end - start);
            }

            return string.Empty;
        }

        private static bool TryExtractSkinningFixedLayouts(
            Stream ms,
            BinaryReader reader,
            int[] vbOffset,
            byte[] vbStride,
            int vertexIndex,
            uint vertexFileOffset,
            ref Vector4 boneWeights,
            ref Vector4 boneIndices,
            ref bool hasWeights,
            ref bool hasIndices)
        {
            if (hasWeights && hasIndices)
                return true;

            if (vbOffset == null || vbOffset.Length == 0 || vbStride == null || vbStride.Length == 0)
                return false;

            int stride = vbStride[0];
            long basePos = vertexFileOffset + vbOffset[0] + ((long)vertexIndex * stride);
            if (stride < 16 || basePos < 0 || basePos + stride > ms.Length)
                return false;

            (int strideMin, int weightOffset, int indexOffset, bool indexIsUShort4)[] profiles =
            {
                (20, 12, 16, false), // s0: float3 + ubyte4 weights + ubyte4 indices
                (48, 16, 32, false),
                (56, 16, 32, false),
                (64, 16, 32, false),
                (72, 16, 32, false),
                (52, 16, 32, false),
                (32, 12, 28, false),
                (44, 12, 28, false),
                (48, 12, 28, false),
                (64, 24, 40, false),
                (64, 16, 48, false),
                (72, 16, 40, false),
            };

            foreach (var profile in profiles)
            {
                if (stride < profile.strideMin)
                    continue;
                if (profile.weightOffset + 16 > stride || profile.indexOffset + (profile.indexIsUShort4 ? 8 : 4) > stride)
                    continue;

                if (!hasWeights && basePos + profile.weightOffset + 16 <= ms.Length)
                {
                    ms.Position = basePos + profile.weightOffset;
                    var w = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    if (IsPlausibleWeights(w))
                    {
                        boneWeights = w;
                        hasWeights = true;
                    }
                    else if (basePos + profile.weightOffset + 8 <= ms.Length)
                    {
                        ms.Position = basePos + profile.weightOffset;
                        w = new Vector4(
                            (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16()),
                            (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16()),
                            (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16()),
                            (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16()));
                        if (IsPlausibleWeights(w))
                        {
                            boneWeights = w;
                            hasWeights = true;
                        }
                    }
                    else if (basePos + profile.weightOffset + 4 <= ms.Length)
                    {
                        ms.Position = basePos + profile.weightOffset;
                        w = new Vector4(
                            reader.ReadByte() / 255f,
                            reader.ReadByte() / 255f,
                            reader.ReadByte() / 255f,
                            reader.ReadByte() / 255f);
                        if (IsPlausibleWeights(w))
                        {
                            boneWeights = w;
                            hasWeights = true;
                        }
                    }
                }

                if (!hasIndices)
                {
                    if (profile.indexIsUShort4 && basePos + profile.indexOffset + 8 <= ms.Length)
                    {
                        ms.Position = basePos + profile.indexOffset;
                        var idx = new Vector4(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16());
                        if (IsPlausibleIndices(idx))
                        {
                            boneIndices = idx;
                            hasIndices = true;
                        }
                    }
                    else if (basePos + profile.indexOffset + 4 <= ms.Length)
                    {
                        ms.Position = basePos + profile.indexOffset;
                        var idx = new Vector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                        if (IsPlausibleIndices(idx))
                        {
                            boneIndices = idx;
                            hasIndices = true;
                        }
                    }
                }

                if (hasWeights && hasIndices)
                    return true;
            }

            return hasWeights && hasIndices;
        }

        private static bool IsPlausibleWeights(Vector4 w)
        {
            float sum = w.X + w.Y + w.Z + w.W;
            if (sum <= 0.01f || sum > 4f)
                return false;
            if (w.X < 0f || w.Y < 0f || w.Z < 0f || w.W < 0f)
                return false;
            return w.X <= 1.05f || w.Y <= 1.05f || w.Z <= 1.05f || w.W <= 1.05f;
        }

        private static bool IsPlausibleIndices(Vector4 idx)
        {
            if (idx.X >= 256 || idx.Y >= 256 || idx.Z >= 256 || idx.W >= 256)
                return false;
            if (idx.X < 0 || idx.Y < 0 || idx.Z < 0 || idx.W < 0)
                return false;
            return true;
        }

        private static void TryReadSkinningHeuristic(
            Stream ms,
            BinaryReader reader,
            int[] vbOffset,
            byte[] vbStride,
            int vertexIndex,
            uint vertexFileOffset,
            ref Vector4 boneWeights,
            ref Vector4 boneIndices,
            ref bool hasWeights,
            ref bool hasIndices)
        {
            for (int stream = 0; stream < vbOffset.Length; stream++)
            {
                int stride = vbStride[stream];
                if (stride < 16)
                    continue;

                long basePos = vertexFileOffset + vbOffset[stream] + ((long)vertexIndex * stride);
                if (basePos < 0 || basePos >= ms.Length)
                    continue;

                if (!hasWeights)
                {
                    int[] weightOffsets = { 12, 16, 20, 24, 28, 32 };
                    foreach (int wOff in weightOffsets)
                    {
                        if (wOff + 4 > stride || basePos + wOff + 4 > ms.Length)
                            continue;

                        ms.Position = basePos + wOff;
                        var wBytes = new Vector4(
                            reader.ReadByte() / 255f,
                            reader.ReadByte() / 255f,
                            reader.ReadByte() / 255f,
                            reader.ReadByte() / 255f);
                        if (IsPlausibleWeights(wBytes))
                        {
                            boneWeights = wBytes;
                            hasWeights = true;
                            break;
                        }

                        if (wOff + 16 > stride || basePos + wOff + 16 > ms.Length)
                            continue;

                        ms.Position = basePos + wOff;
                        var w = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        if (IsPlausibleWeights(w))
                        {
                            boneWeights = w;
                            hasWeights = true;
                            break;
                        }
                    }
                }

                if (!hasIndices)
                {
                    int[] indexOffsets = { 16, 32, 28, 40, 48, stride - 4, 44, 56, 64, 24, 20 };
                    foreach (int off in indexOffsets)
                    {
                        if (off < 0 || off + 4 > stride || basePos + off + 4 > ms.Length)
                            continue;

                        ms.Position = basePos + off;
                        var idx = new Vector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                        if (idx.X <= 127 && idx.Y <= 127 && idx.Z <= 127 && idx.W <= 127)
                        {
                            boneIndices = idx;
                            hasIndices = true;
                            break;
                        }
                    }
                }

                if (!hasIndices && stride >= 36 && basePos + 36 <= ms.Length)
                {
                    ms.Position = basePos + 28;
                    var idx = new Vector4(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16());
                    if (idx.X < 256 && idx.Y < 256 && idx.Z < 256 && idx.W < 256)
                    {
                        boneIndices = idx;
                        hasIndices = true;
                    }
                }

                if (!hasWeights && boneWeights.X + boneWeights.Y + boneWeights.Z + boneWeights.W > 0.001f && hasIndices)
                    hasWeights = true;
            }
        }

        private static bool TryReadBoneWeights(BinaryReader reader, Stream ms, byte type, ref Vector4 boneWeights)
        {
            if (type == 3 && ms.Position + 16 <= ms.Length)
            {
                boneWeights = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                return true;
            }
            if (type == 2 && ms.Position + 12 <= ms.Length)
            {
                float w0 = reader.ReadSingle();
                float w1 = reader.ReadSingle();
                float w2 = reader.ReadSingle();
                float w3 = MathF.Max(0f, 1f - (w0 + w1 + w2));
                boneWeights = new Vector4(w0, w1, w2, w3);
                return true;
            }
            if (type == 14 && ms.Position + 8 <= ms.Length)
            {
                float w0 = (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16());
                float w1 = (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16());
                float w2 = (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16());
                float w3 = (float)BitConverter.Int16BitsToHalf((short)reader.ReadUInt16());
                boneWeights = new Vector4(w0, w1, w2, w3);
                return true;
            }
            if (type == 1 && ms.Position + 8 <= ms.Length)
            {
                float w0 = reader.ReadSingle();
                float w1 = reader.ReadSingle();
                boneWeights = new Vector4(w0, w1, MathF.Max(0f, 1f - w0 - w1), 0f);
                return w0 + w1 > 0.001f;
            }
            if ((type == 6 || type == 8 || type == 12) && ms.Position + 4 <= ms.Length)
            {
                // Type 8 at usage 1 = UByte4N blend weights (not Dec3N — that is usage 3).
                boneWeights = new Vector4(
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f,
                    reader.ReadByte() / 255f);
                return boneWeights.X + boneWeights.Y + boneWeights.Z + boneWeights.W > 0.001f;
            }
            return false;
        }

        private static bool TryReadBoneIndices(BinaryReader reader, Stream ms, byte type, ref Vector4 boneIndices)
        {
            if ((type == 5 || type == 6 || type == 7 || type == 10 || type == 11 || type == 12 || type == 15) && ms.Position + 4 <= ms.Length)
            {
                boneIndices = new Vector4(reader.ReadByte(), reader.ReadByte(), reader.ReadByte(), reader.ReadByte());
                return true;
            }
            if (type == 9 && ms.Position + 8 <= ms.Length)
            {
                boneIndices = new Vector4(reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadUInt16());
                return true;
            }
            if (type == 4 && ms.Position + 16 <= ms.Length)
            {
                boneIndices = new Vector4(reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32());
                return true;
            }
            if (type == 3 && ms.Position + 16 <= ms.Length)
            {
                boneIndices = new Vector4(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                return true;
            }
            return false;
        }

        private static Vector4 NormalizeBoneWeights(Vector4 weights)
        {
            float sum = weights.X + weights.Y + weights.Z + weights.W;
            if (sum <= 0.0001f)
                return Vector4.Zero;
            return weights / sum;
        }
    }
}
