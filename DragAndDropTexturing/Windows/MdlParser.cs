using System;
using System.Collections.Generic;
using System.IO;
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
        public List<uint> Indices = new();
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
            statusMessage = "";
            try
            {
                byte[] fileData = File.ReadAllBytes(filePath);
                using var ms = new MemoryStream(fileData);
                using var reader = new BinaryReader(ms);

                // === ModelFileHeader (0x44 = 68 bytes) ===
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

                // === VertexDeclarations (136 bytes each) ===
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

                // === Path Data (TexTools: PathCount + PathBlockSize + PathBlock) ===
                int pathCount = reader.ReadInt32();
                int pathBlockSize = reader.ReadInt32();
                byte[] pathBlock = reader.ReadBytes(pathBlockSize);

                // === MdlModelData (56 bytes — exact TexTools MdlModelData.Read) ===
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

                // === ElementIds (32 bytes each: uint ElementId, uint ParentBone, float3 Translate, float3 Rotate) ===
                // TexTools: br.ReadBytes(mdlModelData.ElementIdCount * 32)
                reader.ReadBytes(elementIdCount * 32);

                // === LOD structs (56 bytes each × 3) ===
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

                // === ExtraLods (if HasExtraMeshes flag is set in Flags2 bit 0x10) ===
                bool hasExtraMeshes = (flags2 & 0x10) != 0;
                if (hasExtraMeshes)
                {
                    // 3 LODs × 12 extra mesh type pairs (each 4 bytes = ushort+ushort)
                    reader.ReadBytes(3 * 12 * 4);
                }

                // === Mesh Structs (36 bytes each) ===
                // TexTools: VertexCount(4!), IndexCount(4), MaterialIndex(2), SubMeshIndex(2),
                //           SubMeshCount(2), BoneTableIndex(2), IndexDataOffset(4),
                //           VertexDataOffset[3](12), VertexDataEntrySize[3](3), VertexStreamCount(1) = 36
                // IMPORTANT: TexTools reads VertexCount as Int32 (not UInt16+padding)!
                var meshStructs = new List<(int vertexCount, int indexCount, int startIndex,
                    int[] vbOffset, byte[] vbStride)>();

                for (int m = 0; m < meshCount; m++)
                {
                    int vtxCount = reader.ReadInt32();        // 4 (TexTools uses ReadInt32 for this)
                    int idxCount = reader.ReadInt32();         // 4
                    reader.ReadInt16();                        // MaterialIndex
                    reader.ReadInt16();                        // SubMeshIndex
                    reader.ReadInt16();                        // SubMeshCount
                    reader.ReadInt16();                        // BoneTableIndex
                    int startIdx = reader.ReadInt32();          // 4
                    int[] vbOff = { reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() }; // 12
                    byte[] vbStr = { reader.ReadByte(), reader.ReadByte(), reader.ReadByte() };   // 3
                    reader.ReadByte();                          // VertexStreamCount                 // 1
                    // Total: 36 ✓

                    meshStructs.Add((vtxCount, idxCount, startIdx, vbOff, vbStr));
                }

                // === Extract geometry from data region ===
                // The vertex/index data offsets in the FileHeader (vertexOffset[], indexOffset[])
                // are absolute byte positions within the file.
                var extractedMeshes = new List<ExtractedMesh>();

                for (int m = 0; m < lod0MeshCount && m < meshStructs.Count; m++)
                {
                    int meshIdx = lod0MeshIndex + m;
                    if (meshIdx >= meshStructs.Count) break;

                    var mesh = meshStructs[meshIdx];
                    var extracted = new ExtractedMesh();

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
                                else if (elem.usage == 4) // UV (TexCoord)
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
                                        reader.ReadUInt32(); // second UV pair
                                    }
                                    else if (elem.type == 2 && ms.Position + 12 <= fileData.Length) // Float3??
                                    {
                                        uv = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                                    }
                                }
                            }
                            catch (EndOfStreamException) { }
                        }

                        extracted.Positions.Add(pos);
                        extracted.Normals.Add(norm);
                        extracted.UVs.Add(uv);
                    }

                    if (extracted.Positions.Count > 0 && extracted.Indices.Count > 0)
                        extractedMeshes.Add(extracted);
                }

                if (extractedMeshes.Count > 0)
                {
                    int totalVerts = 0, totalIdx = 0;
                    foreach (var em in extractedMeshes) { totalVerts += em.Positions.Count; totalIdx += em.Indices.Count; }
                    statusMessage = $"Loaded {extractedMeshes.Count} mesh(es) ({totalVerts} verts, {totalIdx / 3} tris) from disk. " +
                                    $"[v{mdlVersion}, vtxOff=0x{vertexOffset[0]:X}, idxOff=0x{indexOffset[0]:X}]";
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
    }
}
