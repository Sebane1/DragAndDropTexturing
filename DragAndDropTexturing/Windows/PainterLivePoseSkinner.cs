using System;
using System.Collections.Generic;
using System.Numerics;
using FFXIVClientStructs.Havok.Animation.Rig;
using FFXIVClientStructs.Havok.Common.Base.Math.QsTransform;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Ktisis.Interop;
using Ktisis.Structs;
using Ktisis.Structs.Extensions;
using static FFXIVClientStructs.Havok.Animation.Rig.hkaPose;

namespace DragAndDropTexturing.Windows;

/// <summary>
/// CPU linear blend skinning for the character overlay preview.
/// Reads live bone transforms read-only — never writes skeleton state.
/// </summary>
internal static unsafe class PainterLivePoseSkinner
{
    private const int MaxPaletteSize = 256;

    public static float LastMaxVertexDelta { get; private set; }
    public static int LastResolvedBoneCount { get; private set; }
    public static int LastUnresolvedBoneCount { get; private set; }
    public static int LastUsedPartial { get; private set; }

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

        int bestPartial = FindBestPartial(skeleton, model.MdlBoneNames);
        LastUsedPartial = bestPartial;

        var nameMap = BuildSkeletonBoneNameMap(skeleton, bestPartial);
        var skinMatrices = BuildSkinMatricesForPartial(skeleton, bestPartial);

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

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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

    private static Dictionary<int, Matrix4x4> BuildSkinMatricesForPartial(Skeleton* skeleton, int partial)
    {
        var result = new Dictionary<int, Matrix4x4>();
        if (skeleton == null || partial < 0 || partial >= skeleton->PartialSkeletonCount)
            return result;

        var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
        if (pose == null)
            return result;

        int boneCount = pose->Skeleton->Bones.Length;
        if (boneCount <= 0 || boneCount > MaxPaletteSize)
            return result;

        var bindModel = stackalloc hkQsTransformf[boneCount];
        BuildBindModelTransforms(pose, bindModel, boneCount);

        for (int i = 0; i < boneCount; i++)
        {
            var bone = skeleton->GetBone(partial, i);
            hkQsTransformf* liveTransform = bone.AccessModelSpace(PropagateOrNot.DontPropagate);
            if (liveTransform == null)
                continue;

            Matrix4x4 current = Alloc.GetMatrix(liveTransform);
            Matrix4x4 bind = Alloc.GetMatrix(bindModel + i);
            if (!Matrix4x4.Invert(bind, out Matrix4x4 invBind))
            {
                result[i] = Matrix4x4.Identity;
                continue;
            }

            // Row-vector skinning: v' = v * invBind * current
            result[i] = invBind * current;
        }

        return result;
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
