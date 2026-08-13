using System;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using Ktisis.Structs.Actor;
using Ktisis.Structs.Extensions;

namespace DragAndDropTexturing.Windows;

/// <summary>
/// Builds camera/world matrices for overlaying the painter preview on the target character.
/// Uses read-only actor transform data only — never walks or modifies Havok skeleton state.
/// </summary>
internal static class PainterCharacterOverlay
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
            bool isValid)
        {
            WorldMatrix = worldMatrix;
            ViewMatrix = viewMatrix;
            ProjectionMatrix = projectionMatrix;
            ScreenMin = screenMin;
            ScreenMax = screenMax;
            IsValid = isValid;
        }

        public Matrix4x4 WorldMatrix { get; }
        public Matrix4x4 ViewMatrix { get; }
        public Matrix4x4 ProjectionMatrix { get; }
        public Vector2 ScreenMin { get; }
        public Vector2 ScreenMax { get; }
        public bool IsValid { get; }
    }

    public static unsafe OverlayState TryBuild(
        ICharacter character,
        Vector3 meshBoundsMin,
        Vector3 meshBoundsMax,
        WorldToScreenFn worldToScreen,
        bool skeletonSpaceSkinning = false)
    {
        if (character == null || !character.IsValid() || worldToScreen == null)
            return default;

        var actor = (Actor*)character.Address;
        if (actor == null)
            return default;

        var model = actor->Model;
        if (model == null)
            return default;

        float heightScale = model->Height;
        if (heightScale <= 0.001f)
            heightScale = 1f;

        Vector3 scale = model->Scale * heightScale;
        var position = character.Position;

        Matrix4x4 world;
        if (skeletonSpaceSkinning)
        {
            // Skinned verts are already in live skeleton model space — only apply the actor transform.
            world = Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(model->Rotation)
                * Matrix4x4.CreateTranslation(position);
        }
        else
        {
            // Rigid bind-pose mesh: align MDL footprint to the character root.
            var footprintOrigin = new Vector3(
                (meshBoundsMin.X + meshBoundsMax.X) * 0.5f,
                meshBoundsMin.Y,
                (meshBoundsMin.Z + meshBoundsMax.Z) * 0.5f);
            world = Matrix4x4.CreateTranslation(-footprintOrigin)
                * Matrix4x4.CreateScale(scale)
                * Matrix4x4.CreateFromQuaternion(model->Rotation)
                * Matrix4x4.CreateTranslation(position);
        }

        var cameraManager = CameraManager.Instance();
        if (cameraManager == null)
            return default;

        var camera = cameraManager->GetActiveCamera();
        if (camera == null)
            return default;

        Matrix4x4 view = camera->GetViewMatrix();
        Matrix4x4 proj = camera->GetProjectionMatrix();

        Vector2 screenMin;
        Vector2 screenMax;
        bool projected;
        if (skeletonSpaceSkinning)
        {
            float halfWidth = 0.45f * heightScale;
            float bodyHeight = MathF.Max(1.85f * heightScale, 0.5f);
            projected = TryProjectBounds(
                world,
                new Vector3(-halfWidth, 0f, -halfWidth),
                new Vector3(halfWidth, bodyHeight, halfWidth),
                worldToScreen,
                out screenMin,
                out screenMax);
        }
        else
        {
            projected = TryProjectBounds(world, meshBoundsMin, meshBoundsMax, worldToScreen, out screenMin, out screenMax);
        }

        if (!projected)
        {
            float halfWidth = 0.35f * heightScale;
            float bodyHeight = MathF.Max(1.6f * heightScale, 0.5f);
            var fallbackMin = new Vector3(-halfWidth, 0f, -halfWidth);
            var fallbackMax = new Vector3(halfWidth, bodyHeight, halfWidth);
            if (!TryProjectBounds(world, fallbackMin, fallbackMax, worldToScreen, out screenMin, out screenMax))
                return default;
        }

        if (screenMax.X <= screenMin.X || screenMax.Y <= screenMin.Y)
            return default;

        return new OverlayState(world, view, proj, screenMin, screenMax, true);
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

        return hits >= 4;
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
