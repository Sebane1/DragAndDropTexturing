using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using Ktisis.Interop;
using Ktisis.Structs;
using Ktisis.Structs.Extensions;

namespace DragAndDropTexturing.Windows;

/// <summary>
/// CPU linear blend skinning for the character overlay preview.
/// Reads live ModelPose read-only, never writes skeleton state.
/// </summary>
internal static unsafe class PainterLivePoseSkinner
{
    private const int MaxPaletteSize = 256;

    private static readonly string[] ProbeBoneSuffixes =
    [
        "j_te_l", "j_syo_l", "j_ko_l", "n_hk_l",
        "j_te_r", "j_syo_r", "j_ko_r", "n_hk_r",
        "j_ude_l", "j_kata_l"
    ];

    public static float LastMaxVertexDelta { get; private set; }
    public static int LastResolvedBoneCount { get; private set; }
    public static int LastUnresolvedBoneCount { get; private set; }
    public static int LastUsedPartial { get; private set; }
    public static string LastBindDebugLine { get; private set; } = string.Empty;
    public static string LastBindDebugLine2 { get; private set; } = string.Empty;
    public static string LastExtremityDebugLine { get; private set; } = string.Empty;

    public static bool BindDebugEnabled { get; set; } = false;
    public static OverlayBindExperiment BindExperiment { get; set; } = OverlayBindExperiment.ReferencePose;

    private static nint _cachedNameMapSkeleton;
    private static int _cachedNameMapPartial = -1;
    private static readonly Dictionary<string, SkelBoneRef> _cachedNameMap = new(StringComparer.OrdinalIgnoreCase);

    private static nint _cachedSkinMatSkeleton;
    private static int _cachedSkinMatPartial = -1;
    private static readonly Dictionary<int, Matrix4x4> _cachedSkinMatrices = new();

    private static nint _cachedBestPartialSkeleton;
    private static string _cachedBestPartialBoneKey = string.Empty;
    private static int _cachedBestPartialResult;

    private static readonly HashSet<string> _scoreOverlapScratch = new(StringComparer.OrdinalIgnoreCase);

    private readonly struct SkelBoneRef
    {
        public SkelBoneRef(int partial, int index)
        {
            Partial = partial;
            Index = index;
        }

        public int Partial { get; }
        public int Index { get; }
    }

    public static void SkinModel(ModelRenderer.RenderModel model, Skeleton* skeleton, int partialIndex = 0)
    {
        LastMaxVertexDelta = 0f;
        LastResolvedBoneCount = 0;
        LastUnresolvedBoneCount = 0;
        LastUsedPartial = partialIndex;

        if (model == null || !model.HasSkinning || skeleton == null)
            return;
        if (model.BindVertices == null || model.BindVertices.Length == 0)
            return;

        int bestPartial = GetCachedBestPartial(skeleton, model.MdlBoneNames);
        LastUsedPartial = bestPartial;

        var nameMap = GetCachedNameMap(skeleton, bestPartial);
        var skinMatrices = BuildSkinMatricesForPartialCached(skeleton, bestPartial);

        if (model.SkinnedVertices == null || model.SkinnedVertices.Length != model.BindVertices.Length)
            model.SkinnedVertices = new ModelRenderer.Vertex[model.BindVertices.Length];

        var boneTable = model.BoneTable;
        var mdlBoneNames = model.MdlBoneNames;
        var elementBones = model.ElementSkeletonBones;
        float maxDelta = 0f;

        for (int v = 0; v < model.BindVertices.Length; v++)
        {
            var bind = model.BindVertices[v];
            Vector3 pos = Vector3.Zero;
            Vector3 norm = Vector3.Zero;
            float weightSum = 0f;

            Vector4 weights = v < model.BoneWeights.Length ? model.BoneWeights[v] : Vector4.Zero;
            Vector4 indices = v < model.BoneIndices.Length ? model.BoneIndices[v] : Vector4.Zero;

            AccumulateBone(ref pos, ref norm, ref weightSum, bind.Position, bind.Normal, weights.X, (int)indices.X, boneTable, mdlBoneNames, elementBones, nameMap, skinMatrices);
            AccumulateBone(ref pos, ref norm, ref weightSum, bind.Position, bind.Normal, weights.Y, (int)indices.Y, boneTable, mdlBoneNames, elementBones, nameMap, skinMatrices);
            AccumulateBone(ref pos, ref norm, ref weightSum, bind.Position, bind.Normal, weights.Z, (int)indices.Z, boneTable, mdlBoneNames, elementBones, nameMap, skinMatrices);
            AccumulateBone(ref pos, ref norm, ref weightSum, bind.Position, bind.Normal, weights.W, (int)indices.W, boneTable, mdlBoneNames, elementBones, nameMap, skinMatrices);

            if (weightSum > 0.0001f)
            {
                pos /= weightSum;
                norm /= weightSum;
            }
            else
            {
                pos = bind.Position;
                norm = bind.Normal;
            }

            maxDelta = MathF.Max(maxDelta, Vector3.Distance(bind.Position, pos));

            float nLen = norm.Length();
            if (nLen > 0.0001f)
                norm /= nLen;
            else
                norm = bind.Normal;

            model.SkinnedVertices[v] = new ModelRenderer.Vertex
            {
                Position = pos,
                Normal = norm,
                UV = bind.UV
            };
        }

        LastMaxVertexDelta = maxDelta;
        model.Vertices = model.SkinnedVertices;
    }

    private static void AccumulateBone(
        ref Vector3 pos,
        ref Vector3 norm,
        ref float weightSum,
        Vector3 bindPos,
        Vector3 bindNorm,
        float weight,
        int blendIndex,
        ushort[] boneTable,
        string[] mdlBoneNames,
        ushort[] elementBones,
        Dictionary<string, SkelBoneRef> nameMap,
        Dictionary<int, Matrix4x4> skinMatrices)
    {
        if (weight <= 0.0001f || blendIndex < 0)
            return;

        if (!TryResolveSkeletonBone(blendIndex, boneTable, mdlBoneNames, elementBones, nameMap, out var skelBone))
        {
            LastUnresolvedBoneCount++;
            return;
        }

        LastResolvedBoneCount++;
        if (!skinMatrices.TryGetValue(skelBone.Index, out Matrix4x4 skin))
            return;

        pos += weight * TransformPoint(skin, bindPos);
        norm += weight * TransformNormal(skin, bindNorm);
        weightSum += weight;
    }

    // Row-vector convention (matches HLSL mul(vector, matrix) and VS WorldViewProj).
    private static Vector3 TransformPoint(Matrix4x4 m, Vector3 v)
    {
        return new Vector3(
            v.X * m.M11 + v.Y * m.M21 + v.Z * m.M31 + m.M41,
            v.X * m.M12 + v.Y * m.M22 + v.Z * m.M32 + m.M42,
            v.X * m.M13 + v.Y * m.M23 + v.Z * m.M33 + m.M43);
    }

    private static Vector3 TransformNormal(Matrix4x4 m, Vector3 n)
    {
        return new Vector3(
            n.X * m.M11 + n.Y * m.M21 + n.Z * m.M31,
            n.X * m.M12 + n.Y * m.M22 + n.Z * m.M32,
            n.X * m.M13 + n.Y * m.M23 + n.Z * m.M33);
    }

    private static bool TryResolveSkeletonBone(
        int blendIndex,
        ushort[] boneTable,
        string[] mdlBoneNames,
        ushort[] elementBones,
        Dictionary<string, SkelBoneRef> nameMap,
        out SkelBoneRef skelBone)
    {
        skelBone = default;
        int mapped = blendIndex;

        if (boneTable != null && boneTable.Length > 0)
        {
            if (blendIndex < 0 || blendIndex >= boneTable.Length)
                return false;
            mapped = boneTable[blendIndex];
        }

        if (mdlBoneNames != null && mdlBoneNames.Length > 0)
        {
            if (mapped < 0 || mapped >= mdlBoneNames.Length)
                return false;

            string boneName = NormalizeBoneName(mdlBoneNames[mapped]);
            if (string.IsNullOrEmpty(boneName))
                return false;

            if (nameMap.TryGetValue(boneName, out skelBone))
                return true;

            return false;
        }

        if (elementBones != null && mapped >= 0 && mapped < elementBones.Length)
        {
            int skelIndex = elementBones[mapped];
            if (skelIndex >= 0 && skelIndex < MaxPaletteSize)
            {
                skelBone = new SkelBoneRef(0, skelIndex);
                return true;
            }
        }

        if (mapped >= 0 && mapped < MaxPaletteSize)
        {
            skelBone = new SkelBoneRef(0, mapped);
            return true;
        }

        return false;
    }

    private static string NormalizeBoneName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        int slash = Math.Max(name.LastIndexOf('/'), name.LastIndexOf('\\'));
        if (slash >= 0 && slash + 1 < name.Length)
            name = name[(slash + 1)..];

        return name.Trim().ToLowerInvariant();
    }

    private static int GetCachedBestPartial(Skeleton* skeleton, string[] mdlBoneNames)
    {
        nint skeletonPtr = (nint)skeleton;
        string boneKey = mdlBoneNames.Length > 0 ? string.Join('\0', mdlBoneNames) : string.Empty;
        if (skeletonPtr == _cachedBestPartialSkeleton && boneKey == _cachedBestPartialBoneKey)
            return _cachedBestPartialResult;

        int result = FindBestPartial(skeleton, mdlBoneNames);
        _cachedBestPartialSkeleton = skeletonPtr;
        _cachedBestPartialBoneKey = boneKey;
        _cachedBestPartialResult = result;
        return result;
    }

    private static int FindBestPartial(Skeleton* skeleton, string[] mdlBoneNames)
    {
        if (skeleton == null || mdlBoneNames == null || mdlBoneNames.Length == 0)
            return 0;

        int bestPartial = 0;
        int bestScore = -1;
        for (int partial = 0; partial < skeleton->PartialSkeletonCount; partial++)
        {
            int score = ScorePartialBoneOverlap(skeleton, partial, mdlBoneNames);
            if (score > bestScore || (score == bestScore && score > 0 && partial > bestPartial))
            {
                bestScore = score;
                bestPartial = partial;
            }
        }

        return bestPartial;
    }

    private static int ScorePartialBoneOverlap(Skeleton* skeleton, int partial, string[] mdlBoneNames)
    {
        var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
        if (pose == null)
            return 0;

        var names = _scoreOverlapScratch;
        names.Clear();
        for (int i = 0; i < pose->Skeleton->Bones.Length; i++)
        {
            string? boneName = pose->Skeleton->Bones[i].Name.String;
            if (string.IsNullOrWhiteSpace(boneName))
                continue;
            names.Add(NormalizeBoneName(boneName));
        }

        int score = 0;
        foreach (string mdlBone in mdlBoneNames)
        {
            string key = NormalizeBoneName(mdlBone);
            if (!string.IsNullOrEmpty(key) && names.Contains(key))
                score++;
        }

        return score;
    }

    private static Dictionary<string, SkelBoneRef> GetCachedNameMap(Skeleton* skeleton, int partial)
    {
        nint skeletonPtr = (nint)skeleton;
        if (skeletonPtr == _cachedNameMapSkeleton && partial == _cachedNameMapPartial)
            return _cachedNameMap;

        _cachedNameMap.Clear();
        _cachedNameMapSkeleton = skeletonPtr;
        _cachedNameMapPartial = partial;

        if (skeleton == null || partial < 0 || partial >= skeleton->PartialSkeletonCount)
            return _cachedNameMap;

        var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
        if (pose == null)
            return _cachedNameMap;

        for (int i = 0; i < pose->Skeleton->Bones.Length; i++)
        {
            string? name = pose->Skeleton->Bones[i].Name.String;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            string key = NormalizeBoneName(name);
            if (string.IsNullOrEmpty(key))
                continue;

            _cachedNameMap[key] = new SkelBoneRef(partial, i);
        }

        return _cachedNameMap;
    }

    private static Dictionary<string, SkelBoneRef> BuildSkeletonBoneNameMap(Skeleton* skeleton, int partial)
    {
        var map = new Dictionary<string, SkelBoneRef>(StringComparer.OrdinalIgnoreCase);
        if (skeleton == null || partial < 0 || partial >= skeleton->PartialSkeletonCount)
            return map;

        var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
        if (pose == null)
            return map;

        for (int i = 0; i < pose->Skeleton->Bones.Length; i++)
        {
            string? name = pose->Skeleton->Bones[i].Name.String;
            if (string.IsNullOrWhiteSpace(name))
                continue;

            string key = NormalizeBoneName(name);
            if (string.IsNullOrEmpty(key))
                continue;

            map[key] = new SkelBoneRef(partial, i);
        }

        return map;
    }

    /// <summary>
    /// Compare ReferencePose inverse bind vs .sklb resource matrices for extremity bones (debug only).
    /// </summary>
    public static void UpdateBindDebug(Skeleton* skeleton, int partial)
    {
        LastBindDebugLine = string.Empty;
        LastBindDebugLine2 = string.Empty;
        if (!BindDebugEnabled || skeleton == null || partial < 0 || partial >= skeleton->PartialSkeletonCount)
            return;

        var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
        if (pose == null || pose->ModelPose.Data == null)
        {
            LastBindDebugLine = "bind: pose unavailable";
            return;
        }

        if (!TryGetSklbResource(skeleton, partial, out SkeletonResourceHandle* sklbHandle, out FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4* resourceInvBind, out int resourceBoneCount))
        {
            LastBindDebugLine = "bind: no sklb InverseBoneMatrix";
            return;
        }

        int boneCount = pose->Skeleton->Bones.Length;
        var bindModel = stackalloc hkQsTransformf[boneCount];
        BuildBindModelTransforms(pose, bindModel, boneCount);

        if (!TryFindProbeBone(pose, out int probeIndex, out string probeName))
        {
            LastBindDebugLine = "bind: no probe bone found";
            return;
        }

        int sklbIndex = ResolveSklbBoneIndex(pose->Skeleton, sklbHandle->HavokSkeleton, probeIndex);
        if (sklbIndex < 0 || sklbIndex >= resourceBoneCount)
        {
            LastBindDebugLine = $"bind: {probeName} sklbIdx={sklbIndex} out of range ({resourceBoneCount})";
            return;
        }

        Matrix4x4 bind = Alloc.GetMatrix(bindModel + probeIndex);
        if (!Matrix4x4.Invert(bind, out Matrix4x4 refInvBind))
        {
            LastBindDebugLine = $"bind: {probeName} refInv singular";
            return;
        }

        Matrix4x4 sklbInv = ToNumericsMatrix(resourceInvBind[sklbIndex]);
        float invDeltaRaw = MaxAbsDelta(refInvBind, sklbInv);
        float invDeltaPoseIdx = invDeltaRaw;
        if (probeIndex != sklbIndex && probeIndex < resourceBoneCount)
            invDeltaPoseIdx = MaxAbsDelta(refInvBind, ToNumericsMatrix(resourceInvBind[probeIndex]));

        float bindDelta = MaxAbsDelta(bind, sklbInv);
        float invProductDelta = MaxAbsDelta(Matrix4x4.Identity, sklbInv * bind);
        bool sklbInvertible = Matrix4x4.Invert(sklbInv, out Matrix4x4 sklbAsBind);
        float invSklbDelta = sklbInvertible ? MaxAbsDelta(refInvBind, sklbAsBind) : float.NaN;

        string liveLabel = BindExperiment switch
        {
            OverlayBindExperiment.SklbInvTimesCurrent => "LIVE iv×c",
            OverlayBindExperiment.SklbCurrentTimesInv => "LIVE c×iv",
            OverlayBindExperiment.SklbTransposeInvTimesCurrent => "LIVE ivT×c",
            OverlayBindExperiment.SklbCurrentTimesTransposeInv => "LIVE c×ivT",
            _ => "LIVE ref"
        };

        var raw = resourceInvBind[sklbIndex];
        LastBindDebugLine =
            $"bind[{probeName}]: invΔ={invDeltaRaw:F3} bindΔ={bindDelta:F3} iv×bindΔ={invProductDelta:F3} inv(sklb)={(sklbInvertible ? $"{invSklbDelta:F3}" : "singular")} {liveLabel}";

        if (invProductDelta < 0.05f)
            LastBindDebugLine += " | sklb matches inv(refBind)";
        else if (bindDelta < 0.05f)
            LastBindDebugLine += " | sklb IS bind (not inverse)";
        else if (invDeltaRaw > 0.5f)
            LastBindDebugLine += " | mesh-binding offset";
        else if (invDeltaRaw < 0.001f)
            LastBindDebugLine += " | bind paths identical";

        LastBindDebugLine2 =
            $"idx pose={probeIndex} sklb={sklbIndex} bones={boneCount}/{resourceBoneCount} sameSkel={(pose->Skeleton == sklbHandle->HavokSkeleton ? 1 : 0)} rawM44={raw.M44:F3} fixM44={SanitizeSklbMatrix(raw).M44:F1}";
        if (probeIndex != sklbIndex)
            LastBindDebugLine2 += $" invΔ@poseIdx={invDeltaPoseIdx:F3}";
    }

    /// <summary>
    /// Compare skinned fingertip vertex vs live bone position in model and screen space.
    /// </summary>
    public static void UpdateExtremityProbeDebug(
        Skeleton* skeleton,
        int partial,
        ModelRenderer.RenderModel model,
        Matrix4x4 worldMatrix,
        PainterCharacterOverlay.WorldToScreenFn worldToScreen)
    {
        LastExtremityDebugLine = string.Empty;
        if (!BindDebugEnabled || skeleton == null || model == null || !model.HasSkinning || worldToScreen == null)
            return;
        if (partial < 0 || partial >= skeleton->PartialSkeletonCount)
            return;

        var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
        if (pose == null || pose->ModelPose.Data == null)
            return;

        if (!TryFindProbeBone(pose, out int boneIndex, out string probeName))
            return;

        Matrix4x4 boneMatrix = Alloc.GetMatrix(pose->ModelPose.Data + boneIndex);
        Vector3 boneModelPos = new(boneMatrix.M41, boneMatrix.M42, boneMatrix.M43);

        var nameMap = BuildSkeletonBoneNameMap(skeleton, partial);
        if (!TryFindStrongestWeightedVertex(model, nameMap, probeName, boneIndex, out int vertIndex, out float boneWeight))
        {
            LastExtremityDebugLine = $"probe[{probeName}]: no weighted vert";
            return;
        }

        Vector3 skinPos = model.SkinnedVertices != null && vertIndex < model.SkinnedVertices.Length
            ? model.SkinnedVertices[vertIndex].Position
            : model.Vertices[vertIndex].Position;

        float modelDelta = Vector3.Distance(skinPos, boneModelPos);

        Vector3 boneWorld = Vector3.Transform(boneModelPos, worldMatrix);
        Vector3 skinWorld = Vector3.Transform(skinPos, worldMatrix);
        float screenDelta = 0f;
        if (worldToScreen(boneWorld, out Vector2 boneScreen) && worldToScreen(skinWorld, out Vector2 skinScreen))
            screenDelta = Vector2.Distance(boneScreen, skinScreen);

        LastExtremityDebugLine =
            $"probe[{probeName}]: w={boneWeight:F2} modelΔ={modelDelta:F4} screenΔ={screenDelta:F1}px (bone vs skinned vert)";
    }

    private static bool TryFindStrongestWeightedVertex(
        ModelRenderer.RenderModel model,
        Dictionary<string, SkelBoneRef> nameMap,
        string probeBoneSuffix,
        int targetBoneIndex,
        out int vertexIndex,
        out float bestWeight)
    {
        vertexIndex = -1;
        bestWeight = 0f;
        if (model.BindVertices == null || model.BindVertices.Length == 0)
            return false;

        var boneTable = model.BoneTable;
        var mdlBoneNames = model.MdlBoneNames;
        var elementBones = model.ElementSkeletonBones;

        for (int v = 0; v < model.BindVertices.Length; v++)
        {
            Vector4 weights = v < model.BoneWeights.Length ? model.BoneWeights[v] : Vector4.Zero;
            Vector4 indices = v < model.BoneIndices.Length ? model.BoneIndices[v] : Vector4.Zero;

            float w = SumWeightForBone(weights.X, (int)indices.X, targetBoneIndex, boneTable, mdlBoneNames, elementBones, nameMap, probeBoneSuffix)
                    + SumWeightForBone(weights.Y, (int)indices.Y, targetBoneIndex, boneTable, mdlBoneNames, elementBones, nameMap, probeBoneSuffix)
                    + SumWeightForBone(weights.Z, (int)indices.Z, targetBoneIndex, boneTable, mdlBoneNames, elementBones, nameMap, probeBoneSuffix)
                    + SumWeightForBone(weights.W, (int)indices.W, targetBoneIndex, boneTable, mdlBoneNames, elementBones, nameMap, probeBoneSuffix);

            if (w > bestWeight)
            {
                bestWeight = w;
                vertexIndex = v;
            }
        }

        return vertexIndex >= 0 && bestWeight > 0.01f;
    }

    private static float SumWeightForBone(
        float weight,
        int blendIndex,
        int targetBoneIndex,
        ushort[] boneTable,
        string[] mdlBoneNames,
        ushort[] elementBones,
        Dictionary<string, SkelBoneRef> nameMap,
        string probeBoneSuffix)
    {
        if (weight <= 0.0001f || blendIndex < 0)
            return 0f;

        if (!TryResolveSkeletonBone(blendIndex, boneTable, mdlBoneNames, elementBones, nameMap, out var skelBone))
            return 0f;

        if (skelBone.Index == targetBoneIndex)
            return weight;

        return 0f;
    }

    private static bool TryFindProbeBone(hkaPose* pose, out int index, out string shortName)
    {
        index = -1;
        shortName = string.Empty;
        if (pose == null)
            return false;

        foreach (string suffix in ProbeBoneSuffixes)
        {
            for (int i = 0; i < pose->Skeleton->Bones.Length; i++)
            {
                string? name = pose->Skeleton->Bones[i].Name.String;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string normalized = NormalizeBoneName(name);
                if (normalized.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    shortName = suffix;
                    return true;
                }
            }
        }

        return false;
    }

    private static float MaxAbsDelta(Matrix4x4 a, Matrix4x4 b)
    {
        float max = 0f;
        max = MathF.Max(max, MathF.Abs(a.M11 - b.M11));
        max = MathF.Max(max, MathF.Abs(a.M12 - b.M12));
        max = MathF.Max(max, MathF.Abs(a.M13 - b.M13));
        max = MathF.Max(max, MathF.Abs(a.M14 - b.M14));
        max = MathF.Max(max, MathF.Abs(a.M21 - b.M21));
        max = MathF.Max(max, MathF.Abs(a.M22 - b.M22));
        max = MathF.Max(max, MathF.Abs(a.M23 - b.M23));
        max = MathF.Max(max, MathF.Abs(a.M24 - b.M24));
        max = MathF.Max(max, MathF.Abs(a.M31 - b.M31));
        max = MathF.Max(max, MathF.Abs(a.M32 - b.M32));
        max = MathF.Max(max, MathF.Abs(a.M33 - b.M33));
        max = MathF.Max(max, MathF.Abs(a.M34 - b.M34));
        max = MathF.Max(max, MathF.Abs(a.M41 - b.M41));
        max = MathF.Max(max, MathF.Abs(a.M42 - b.M42));
        max = MathF.Max(max, MathF.Abs(a.M43 - b.M43));
        max = MathF.Max(max, MathF.Abs(a.M44 - b.M44));
        return max;
    }

    private static bool TryBuildExperimentalSkinMatrix(
        Matrix4x4 current,
        Matrix4x4 refInvBind,
        Matrix4x4 sklbInvBind,
        bool hasSklb,
        out Matrix4x4 skin)
    {
        skin = refInvBind * current;
        if (BindExperiment == OverlayBindExperiment.ReferencePose || !hasSklb)
            return true;

        Matrix4x4 sklbInvT = Matrix4x4.Transpose(sklbInvBind);
        skin = BindExperiment switch
        {
            OverlayBindExperiment.SklbInvTimesCurrent => sklbInvBind * current,
            OverlayBindExperiment.SklbCurrentTimesInv => current * sklbInvBind,
            OverlayBindExperiment.SklbTransposeInvTimesCurrent => sklbInvT * current,
            OverlayBindExperiment.SklbCurrentTimesTransposeInv => current * sklbInvT,
            _ => refInvBind * current
        };
        return true;
    }

    private static Dictionary<int, Matrix4x4> BuildSkinMatricesForPartialCached(Skeleton* skeleton, int partial)
    {
        nint skeletonPtr = (nint)skeleton;
        if (skeletonPtr != _cachedSkinMatSkeleton || partial != _cachedSkinMatPartial)
        {
            _cachedSkinMatrices.Clear();
            _cachedSkinMatSkeleton = skeletonPtr;
            _cachedSkinMatPartial = partial;
        }
        else
        {
            _cachedSkinMatrices.Clear();
        }

        if (skeleton == null || partial < 0 || partial >= skeleton->PartialSkeletonCount)
            return _cachedSkinMatrices;

        var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
        if (pose == null || pose->ModelPose.Data == null)
            return _cachedSkinMatrices;

        int boneCount = pose->Skeleton->Bones.Length;
        if (boneCount <= 0 || boneCount > MaxPaletteSize)
            return _cachedSkinMatrices;

        var bindModel = stackalloc hkQsTransformf[boneCount];
        BuildBindModelTransforms(pose, bindModel, boneCount);
        bool hasSklb = TryGetSklbResource(skeleton, partial, out SkeletonResourceHandle* sklbHandle, out FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4* resourceInvBind, out int resourceBoneCount);
        hkaSkeleton* sklbSkeleton = hasSklb ? sklbHandle->HavokSkeleton : null;

        for (int i = 0; i < boneCount; i++)
        {
            Matrix4x4 current = Alloc.GetMatrix(pose->ModelPose.Data + i);
            Matrix4x4 bind = Alloc.GetMatrix(bindModel + i);
            if (!Matrix4x4.Invert(bind, out Matrix4x4 refInvBind))
            {
                _cachedSkinMatrices[i] = Matrix4x4.Identity;
                continue;
            }

            int sklbIndex = hasSklb ? ResolveSklbBoneIndex(pose->Skeleton, sklbSkeleton, i) : i;
            bool hasSklbForBone = hasSklb && sklbIndex >= 0 && sklbIndex < resourceBoneCount;
            Matrix4x4 sklbInv = hasSklbForBone
                ? ToNumericsMatrix(resourceInvBind[sklbIndex])
                : Matrix4x4.Identity;

            TryBuildExperimentalSkinMatrix(current, refInvBind, sklbInv, hasSklbForBone, out Matrix4x4 skin);
            _cachedSkinMatrices[i] = skin;
        }

        return _cachedSkinMatrices;
    }

    private static Dictionary<int, Matrix4x4> BuildSkinMatricesForPartial(Skeleton* skeleton, int partial)
    {
        var result = new Dictionary<int, Matrix4x4>();
        if (skeleton == null || partial < 0 || partial >= skeleton->PartialSkeletonCount)
            return result;

        var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
        if (pose == null || pose->ModelPose.Data == null)
            return result;

        int boneCount = pose->Skeleton->Bones.Length;
        if (boneCount <= 0 || boneCount > MaxPaletteSize)
            return result;

        var bindModel = stackalloc hkQsTransformf[boneCount];
        BuildBindModelTransforms(pose, bindModel, boneCount);
        bool hasSklb = TryGetSklbResource(skeleton, partial, out SkeletonResourceHandle* sklbHandle, out FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4* resourceInvBind, out int resourceBoneCount);
        hkaSkeleton* sklbSkeleton = hasSklb ? sklbHandle->HavokSkeleton : null;

        for (int i = 0; i < boneCount; i++)
        {
            Matrix4x4 current = Alloc.GetMatrix(pose->ModelPose.Data + i);
            Matrix4x4 bind = Alloc.GetMatrix(bindModel + i);
            if (!Matrix4x4.Invert(bind, out Matrix4x4 refInvBind))
            {
                result[i] = Matrix4x4.Identity;
                continue;
            }

            int sklbIndex = hasSklb ? ResolveSklbBoneIndex(pose->Skeleton, sklbSkeleton, i) : i;
            bool hasSklbForBone = hasSklb && sklbIndex >= 0 && sklbIndex < resourceBoneCount;
            Matrix4x4 sklbInv = hasSklbForBone
                ? ToNumericsMatrix(resourceInvBind[sklbIndex])
                : Matrix4x4.Identity;

            TryBuildExperimentalSkinMatrix(current, refInvBind, sklbInv, hasSklbForBone, out Matrix4x4 skin);
            result[i] = skin;
        }

        return result;
    }

    private static int ResolveSklbBoneIndex(hkaSkeleton* poseSkeleton, hkaSkeleton* sklbSkeleton, int poseBoneIndex)
    {
        if (poseBoneIndex < 0)
            return poseBoneIndex;
        if (sklbSkeleton == null || poseSkeleton == null)
            return poseBoneIndex;
        if (poseBoneIndex >= poseSkeleton->Bones.Length)
            return poseBoneIndex;

        string? name = poseSkeleton->Bones[poseBoneIndex].Name.String;
        if (string.IsNullOrWhiteSpace(name))
            return poseBoneIndex;

        int byName = FindBoneIndexByNormalizedName(sklbSkeleton, NormalizeBoneName(name));
        return byName >= 0 ? byName : poseBoneIndex;
    }

    private static int FindBoneIndexByNormalizedName(hkaSkeleton* skeleton, string normalizedName)
    {
        if (skeleton == null || string.IsNullOrEmpty(normalizedName))
            return -1;

        for (int i = 0; i < skeleton->Bones.Length; i++)
        {
            string? name = skeleton->Bones[i].Name.String;
            if (string.IsNullOrWhiteSpace(name))
                continue;
            if (NormalizeBoneName(name).Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                return i;
        }

        return -1;
    }

    private static bool TryGetSklbResource(
        Skeleton* skeleton,
        int partial,
        out SkeletonResourceHandle* handle,
        out FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4* invBind,
        out int boneCount)
    {
        handle = null;
        invBind = null;
        boneCount = 0;
        if (skeleton->SkeletonResourceHandles == null)
            return false;

        handle = skeleton->SkeletonResourceHandles[partial];
        if (handle == null || handle->InverseBoneMatrix == null || handle->BoneCount == 0)
            return false;

        invBind = handle->InverseBoneMatrix;
        boneCount = (int)handle->BoneCount;
        return true;
    }

    private static Matrix4x4 ToNumericsMatrix(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 m)
        => SanitizeSklbMatrix(m);

    /// <summary>
    /// .sklb InverseBoneMatrix entries are 3×4 GPU matrices, M44 is often 0, which breaks System.Numerics.Invert.
    /// Force a proper affine row: (0,0,0,1) in the bottom row for row-vector skinning.
    /// </summary>
    private static Matrix4x4 SanitizeSklbMatrix(FFXIVClientStructs.FFXIV.Common.Math.Matrix4x4 m)
    {
        Matrix4x4 matrix = m;
        if (MathF.Abs(matrix.M44) < 0.0001f)
        {
            matrix.M14 = 0f;
            matrix.M24 = 0f;
            matrix.M34 = 0f;
            matrix.M44 = 1f;
        }

        return matrix;
    }

    private static void BuildBindModelTransforms(hkaPose* pose, hkQsTransformf* outModel, int boneCount)
    {
        var refPose = pose->Skeleton->ReferencePose;
        for (int i = 0; i < boneCount; i++)
        {
            var local = refPose[i];
            int parent = pose->Skeleton->ParentIndices[i];
            if (i == 0 || parent < 0)
                outModel[i] = local;
            else
                outModel[i] = MultiplyQs(outModel[parent], local);
        }
    }

    private static hkQsTransformf MultiplyQs(hkQsTransformf parent, hkQsTransformf local)
    {
        var parentScale = parent.Scale.ToVector3();
        var localScale = local.Scale.ToVector3();
        var scaledLocalTranslation = local.Translation.ToVector3() * parentScale;

        return new hkQsTransformf
        {
            Translation = (parent.Translation.ToVector3() + Vector3.Transform(scaledLocalTranslation, parent.Rotation.ToQuat())).ToHavok(),
            Rotation = (parent.Rotation.ToQuat() * local.Rotation.ToQuat()).ToHavok(),
            Scale = new FFXIVClientStructs.Havok.Common.Base.Math.Vector.hkVector4f
            {
                X = parentScale.X * localScale.X,
                Y = parentScale.Y * localScale.Y,
                Z = parentScale.Z * localScale.Z,
                W = 1f
            }
        };
    }
}
