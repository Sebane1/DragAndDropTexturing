using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Client.Graphics.Render;
using Ktisis.Interop;
using Ktisis.Structs;
using Ktisis.Structs.Actor;
using Ktisis.Structs.Extensions;

namespace DragAndDropTexturing.Windows;

/// <summary>
/// Builds camera/world matrices for overlaying the painter preview on the target character.
/// Skinned overlay root transform matches Ktisis BoneNode.CalcMatrixWorld: ModelPose space
/// verts multiplied by skeleton->Transform (Scale, Rotation, Position).
/// </summary>
public static class PainterCharacterOverlay
{
    public delegate bool WorldToScreenFn(Vector3 world, out Vector2 screen);

    public readonly struct OverlayState
    {
        public OverlayState(
            Matrix4x4 worldMatrix,
            Matrix4x4 viewMatrix,
            Matrix4x4 projectionMatrix,
            Vector2 screenMin,
            Vector2 screenMax,
            Vector3 rootScale,
            Vector3 skeletonScale,
            Vector3 skeletonPosition,
            bool isValid)
        {
            WorldMatrix = worldMatrix;
            ViewMatrix = viewMatrix;
            ProjectionMatrix = projectionMatrix;
            ScreenMin = screenMin;
            ScreenMax = screenMax;
            RootScale = rootScale;
            SkeletonScale = skeletonScale;
            SkeletonPosition = skeletonPosition;
            IsValid = isValid;
        }

        public Matrix4x4 WorldMatrix { get; }
        public Matrix4x4 ViewMatrix { get; }
        public Matrix4x4 ProjectionMatrix { get; }
        public Vector2 ScreenMin { get; }
        public Vector2 ScreenMax { get; }
        public Vector3 RootScale { get; }
        public Vector3 SkeletonScale { get; }
        public Vector3 SkeletonPosition { get; }
        public bool IsValid { get; }
    }

    public static unsafe OverlayState TryBuild(
        ICharacter character,
        Vector3 meshBoundsMin,
        Vector3 meshBoundsMax,
        WorldToScreenFn worldToScreen,
        bool skeletonSpaceSkinning = false,
        float scaleFineTune = 0f,
        float widthFineTune = 0f)
    {
        if (character == null || !character.IsValid() || worldToScreen == null)
            return default;

        var actor = (Actor*)character.Address;
        if (actor == null)
            return default;

        var model = actor->Model;
        if (model == null)
            return default;

        float modelHeight = model->Height > 0.001f ? model->Height : 1f;
        Vector3 bindScale = model->Scale * modelHeight;
        var position = character.Position;
        Skeleton* skeleton = model->Skeleton;

        Vector3 skeletonScale = Vector3.One;
        Vector3 skeletonPosition = position;

        Matrix4x4 world;
        Vector3 rootScale = Vector3.One;
        if (skeletonSpaceSkinning && skeleton != null)
        {
            world = BuildSkinnedRootWorld(
                skeleton,
                scaleFineTune,
                widthFineTune,
                out rootScale,
                out skeletonScale,
                out skeletonPosition);
        }
        else
        {
            var footprintOrigin = new Vector3(
                (meshBoundsMin.X + meshBoundsMax.X) * 0.5f,
                meshBoundsMin.Y,
                (meshBoundsMin.Z + meshBoundsMax.Z) * 0.5f);
            world = Matrix4x4.CreateTranslation(-footprintOrigin)
                * Matrix4x4.CreateScale(bindScale)
                * Matrix4x4.CreateFromQuaternion(model->Rotation)
                * Matrix4x4.CreateTranslation(position);
            rootScale = bindScale;
        }

        var cameraManager = CameraManager.Instance();
        if (cameraManager == null)
            return default;

        var camera = cameraManager->GetActiveCamera();
        if (camera == null)
            return default;

        Matrix4x4 view = camera->GetViewMatrix();
        Matrix4x4 proj = camera->GetProjectionMatrix();

        Vector2 screenMin = Vector2.Zero;
        Vector2 screenMax = Vector2.Zero;
        _ = TryProjectBoundsForDisplay(
            world,
            skeletonSpaceSkinning,
            skeleton,
            meshBoundsMin,
            meshBoundsMax,
            modelHeight,
            worldToScreen,
            ref screenMin,
            ref screenMax);

        // Rendering uses full viewport + live camera matrices, bounds projection is display-only.
        return new OverlayState(world, view, proj, screenMin, screenMax, rootScale, skeletonScale, skeletonPosition, true);
    }

    private static unsafe bool TryProjectBoundsForDisplay(
        Matrix4x4 world,
        bool skeletonSpaceSkinning,
        Skeleton* skeleton,
        Vector3 meshBoundsMin,
        Vector3 meshBoundsMax,
        float modelHeight,
        WorldToScreenFn worldToScreen,
        ref Vector2 screenMin,
        ref Vector2 screenMax)
    {
        if (skeletonSpaceSkinning && skeleton != null && TryMeasureSkeletonBounds(skeleton, out Vector3 skelMin, out Vector3 skelMax))
        {
            if (TryProjectBounds(world, skelMin, skelMax, worldToScreen, out screenMin, out screenMax))
                return true;
        }
        else if (skeletonSpaceSkinning)
        {
            float meshHeight = MathF.Max(meshBoundsMax.Y - meshBoundsMin.Y, 0.001f);
            float halfWidth = MathF.Max(0.35f, meshHeight * 0.24f);
            if (TryProjectBounds(
                    world,
                    new Vector3(-halfWidth, 0f, -halfWidth),
                    new Vector3(halfWidth, meshHeight, halfWidth),
                    worldToScreen,
                    out screenMin,
                    out screenMax))
                return true;
        }
        else if (TryProjectBounds(world, meshBoundsMin, meshBoundsMax, worldToScreen, out screenMin, out screenMax))
        {
            return true;
        }

        float fallbackHalfWidth = 0.35f * modelHeight;
        float bodyHeight = MathF.Max(1.6f * modelHeight, 0.5f);
        return TryProjectBounds(
            world,
            new Vector3(-fallbackHalfWidth, 0f, -fallbackHalfWidth),
            new Vector3(fallbackHalfWidth, bodyHeight, fallbackHalfWidth),
            worldToScreen,
            out screenMin,
            out screenMax);
    }

    /// <summary>
    /// Same root chain Ktisis uses in BoneNode.CalcMatrixWorld, skeleton->Transform only.
    /// </summary>
    private static unsafe Matrix4x4 BuildSkinnedRootWorld(
        Skeleton* skeleton,
        float heightFineTune,
        float widthFineTune,
        out Vector3 appliedScale,
        out Vector3 skeletonScale,
        out Vector3 skeletonPosition)
    {
        var skelTransform = skeleton->Transform;
        skeletonScale = skelTransform.Scale;
        skeletonPosition = skelTransform.Position;

        Vector3 scale = skeletonScale;
        if (scale.LengthSquared() < 0.0001f)
            scale = Vector3.One;

        scale.X *= Math.Clamp(1f + widthFineTune, 0.75f, 1.05f);
        scale.Z *= Math.Clamp(1f + widthFineTune, 0.75f, 1.05f);
        scale.Y *= Math.Clamp(1f + heightFineTune, 0.85f, 1.05f);

        appliedScale = scale;

        return Matrix4x4.CreateScale(scale)
            * Matrix4x4.CreateFromQuaternion(skelTransform.Rotation)
            * Matrix4x4.CreateTranslation(skelTransform.Position);
    }

    private static bool TryProjectBounds(
        Matrix4x4 world,
        Vector3 boundsMin,
        Vector3 boundsMax,
        WorldToScreenFn worldToScreen,
        out Vector2 screenMin,
        out Vector2 screenMax)
    {
        screenMin = new Vector2(float.MaxValue);
        screenMax = new Vector2(float.MinValue);
        int hits = 0;

        for (int ix = 0; ix < 2; ix++)
        {
            for (int iy = 0; iy < 2; iy++)
            {
                for (int iz = 0; iz < 2; iz++)
                {
                    var local = new Vector3(
                        ix == 0 ? boundsMin.X : boundsMax.X,
                        iy == 0 ? boundsMin.Y : boundsMax.Y,
                        iz == 0 ? boundsMin.Z : boundsMax.Z);

                    if (!TryTransformToScreen(world, local, worldToScreen, ref screenMin, ref screenMax))
                        continue;
                    hits++;
                }
            }
        }

        return hits >= 1;
    }

    private static readonly string[] FootBoneSuffixes = ["j_tsumasaki_l", "j_tsumasaki_r", "j_asin_l", "j_asin_r", "j_ashi_l", "j_ashi_r"];
    private static readonly string[] HeadBoneSuffixes = ["j_kao", "j_kubi", "j_zago", "j_ago"];
    private static readonly string[] ShoulderBoneSuffixes = ["j_kata_l", "j_kata_r", "j_mune_l", "j_mune_r", "j_ude_l", "j_ude_r"];

    private static unsafe bool TryMeasureSkeletonBounds(Skeleton* skeleton, out Vector3 min, out Vector3 max)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);
        int hits = 0;

        if (skeleton == null)
            return false;

        for (int partial = 0; partial < skeleton->PartialSkeletonCount; partial++)
        {
            var pose = skeleton->PartialSkeletons[partial].GetHavokPose(0);
            if (pose == null)
                continue;

            for (int i = 0; i < pose->Skeleton->Bones.Length; i++)
            {
                string? name = pose->Skeleton->Bones[i].Name.String;
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                string normalized = NormalizeBoneName(name);
                if (!IsFootBone(normalized) && !IsHeadBone(normalized) && !IsShoulderBone(normalized))
                    continue;

                if (!TryGetBoneModelPoint(pose, i, out Vector3 point))
                    continue;

                min = Vector3.Min(min, point);
                max = Vector3.Max(max, point);
                hits++;
            }
        }

        return hits >= 3 && max.X > min.X && max.Y > min.Y && max.Z > min.Z;
    }

    private static unsafe bool TryGetBoneModelPoint(FFXIVClientStructs.Havok.Animation.Rig.hkaPose* pose, int index, out Vector3 point)
    {
        point = default;
        if (pose == null || pose->ModelPose.Data == null)
            return false;
        if (index < 0 || index >= pose->ModelPose.Length)
            return false;

        Matrix4x4 matrix = Alloc.GetMatrix(pose->ModelPose.Data + index);
        point = new Vector3(matrix.M41, matrix.M42, matrix.M43);
        return true;
    }

    private static string NormalizeBoneName(string name)
    {
        int slash = name.LastIndexOf('/');
        if (slash >= 0 && slash + 1 < name.Length)
            name = name[(slash + 1)..];
        return name.Trim().ToLowerInvariant();
    }

    private static bool IsFootBone(string normalized)
    {
        foreach (string foot in FootBoneSuffixes)
        {
            if (normalized.EndsWith(foot, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsHeadBone(string normalized)
    {
        foreach (string head in HeadBoneSuffixes)
        {
            if (normalized.EndsWith(head, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool IsShoulderBone(string normalized)
    {
        foreach (string shoulder in ShoulderBoneSuffixes)
        {
            if (normalized.EndsWith(shoulder, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryTransformToScreen(
        Matrix4x4 world,
        Vector3 local,
        WorldToScreenFn worldToScreen,
        ref Vector2 screenMin,
        ref Vector2 screenMax)
    {
        Vector3 worldPos = Vector3.Transform(local, world);
        if (!worldToScreen(worldPos, out Vector2 screen))
            return false;

        screenMin = Vector2.Min(screenMin, screen);
        screenMax = Vector2.Max(screenMax, screen);
        return true;
    }
}
