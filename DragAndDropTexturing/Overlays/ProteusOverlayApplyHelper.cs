using DragAndDropTexturing.Overlays;
using FFXIVLooseTextureCompiler.ImageProcessing;
using FFXIVLooseTextureCompiler.PathOrganization;
using System;
using System.Collections.Generic;
using System.Numerics;

namespace DragAndDropTexturing.Overlays
{
    /// <summary>
    /// Bakes Proteus overlay sidecars into memory textures before they enter the DDT layer stack.
    /// </summary>
    public static class ProteusOverlayApplyHelper
    {
        public static bool TryPrepareOverlayPaths(
            ResolvedAdvancedOverlay overlay,
            Dictionary<string, Vector4> overlayTints,
            Dictionary<string, Vector4> glowTints,
            Dictionary<string, List<AdvancedColorTableRow>> colorRowOverrides,
            out string diffusePath,
            out string normalPath,
            out string maskPath,
            out string emissivePath,
            out Vector4 tintColor,
            out Vector4 glowTintColor,
            out string overlayKey)
        {
            diffusePath = overlay.DiffusePath;
            normalPath = overlay.NormalPath;
            maskPath = overlay.MaskPath;
            emissivePath = null;

            overlayKey = ProteusColorTableHelper.GetOverlayKey(overlay);

            List<AdvancedColorTableRow> userRows = null;
            if (overlayKey != null && colorRowOverrides != null
                && colorRowOverrides.TryGetValue(overlayKey, out var savedRows))
            {
                userRows = savedRows;
            }

            var effectiveRows = ProteusColorTableHelper.GetEffectiveRows(overlay.ColorTableRows, userRows);
            var rows = ProteusColorTableHelper.ToCompositorRows(effectiveRows);
            bool needsBake = rows.Count > 0
                || !string.IsNullOrEmpty(overlay.IndexPath)
                || (overlay.CoverageMaskPaths != null && overlay.CoverageMaskPaths.Count > 0)
                || (string.IsNullOrEmpty(diffusePath) && overlay.GenerateDiffuse && !string.IsNullOrEmpty(normalPath))
                || userRows != null;

            string cacheKey = BuildCacheKey(overlay, effectiveRows);

            if (needsBake)
            {
                var baked = ProteusOverlayCompositor.BakeResolvedOverlay(
                    overlay.DiffusePath,
                    overlay.NormalPath,
                    overlay.IndexPath,
                    overlay.GenerateDiffuse,
                    rows,
                    overlay.CoverageMaskPaths,
                    cacheKey);

                if (baked != null)
                {
                    if (!string.IsNullOrEmpty(baked.DiffuseMemoryPath))
                        diffusePath = baked.DiffuseMemoryPath;
                    emissivePath = baked.EmissiveMemoryPath;
                }

                if (userRows != null || rows.Count > 0)
                    tintColor = Vector4.One;
            }

            overlayKey = ProteusColorTableHelper.GetOverlayKey(overlay);

            tintColor = ProteusColorTableHelper.Row16DefaultTint(effectiveRows);
            glowTintColor = new Vector4(0, 0, 0, 1f);

            if (overlayKey != null && overlayTints != null && overlayTints.TryGetValue(overlayKey, out var savedTint)
                && (savedTint.X != 0 || savedTint.Y != 0 || savedTint.Z != 0 || savedTint.W != 0))
            {
                tintColor = savedTint;
            }

            if (overlayKey != null && glowTints != null && glowTints.TryGetValue(overlayKey, out var savedGlow)
                && (savedGlow.X != 0 || savedGlow.Y != 0 || savedGlow.Z != 0))
            {
                glowTintColor = savedGlow with { W = 1f };
            }
            else if (!string.IsNullOrEmpty(emissivePath))
            {
                var row16 = ProteusColorTableHelper.Row16DefaultTint(effectiveRows);
                glowTintColor = new Vector4(row16.X, row16.Y, row16.Z, 1f);
            }

            return !string.IsNullOrEmpty(diffusePath) || !string.IsNullOrEmpty(normalPath) || !string.IsNullOrEmpty(maskPath);
        }

        public static void ApplyToTextureSet(
            TextureSet item,
            ResolvedAdvancedOverlay overlay,
            string diffusePath,
            string normalPath,
            string maskPath,
            string emissivePath,
            Vector4 tintColor,
            Vector4 glowTintColor)
        {
            if (!string.IsNullOrEmpty(diffusePath))
            {
                MergeNormalAlphaIntoDiffuse(ref diffusePath, ref normalPath, overlay.NormalPath, overlay.DiffusePath);

                if (string.IsNullOrEmpty(item.Base))
                {
                    item.Base = diffusePath;
                    item.BaseUV = overlay.UVType;
                    item.BaseTint = tintColor;
                }
                else if (!item.BaseOverlays.Contains(diffusePath))
                {
                    item.BaseOverlays.Add(diffusePath);
                    item.BaseOverlayUVs.Add(overlay.UVType);
                    item.BaseOverlayTints.Add(tintColor);
                }
            }

            if (!string.IsNullOrEmpty(normalPath))
            {
                if (string.IsNullOrEmpty(item.Normal))
                {
                    item.Normal = normalPath;
                    item.NormalUV = overlay.UVType;
                }
                else if (!item.NormalOverlays.Contains(normalPath))
                {
                    item.NormalOverlays.Add(normalPath);
                    item.NormalOverlayUVs.Add(overlay.UVType);
                }
            }

            if (!string.IsNullOrEmpty(maskPath))
            {
                MergeMaskCoverage(ref maskPath, overlay.MaskPath, overlay.DiffusePath ?? diffusePath);

                if (string.IsNullOrEmpty(item.Mask))
                {
                    item.Mask = maskPath;
                    item.MaskUV = overlay.UVType;
                }
                else if (!item.MaskOverlays.Contains(maskPath))
                {
                    item.MaskOverlays.Add(maskPath);
                    item.MaskOverlayUVs.Add(overlay.UVType);
                }
            }

            if (!string.IsNullOrEmpty(emissivePath))
            {
                if (string.IsNullOrEmpty(item.Glow))
                {
                    item.Glow = emissivePath;
                    item.GlowUV = overlay.UVType;
                    item.GlowTint = glowTintColor;
                }
                else if (!item.GlowOverlays.Contains(emissivePath))
                {
                    item.GlowOverlays.Add(emissivePath);
                    item.GlowOverlayUVs.Add(overlay.UVType);
                    item.GlowOverlayTints.Add(glowTintColor);
                }
            }
        }

        private static void MergeNormalAlphaIntoDiffuse(ref string diffusePath, ref string normalPath,
            string sourceNormalPath, string sourceDiffusePath)
        {
            if (string.IsNullOrEmpty(sourceNormalPath) || string.IsNullOrEmpty(diffusePath))
                return;

            string memoryPath = "memory:\\" + sourceNormalPath.GetHashCode() + "_" + (sourceDiffusePath ?? diffusePath).GetHashCode() + "_masked";
            if (TexIO.VirtualFileSystem.ContainsKey(memoryPath))
            {
                normalPath = memoryPath;
                return;
            }

            var dims = ComputeSharpLayering.GetImageDimensions(sourceNormalPath);
            if (dims.Width <= 0 || dims.Height <= 0)
                return;

            using (var merged = ComputeSharpLayering.MergeAlphaChannelToRGBGpuFromPaths(
                sourceNormalPath, sourceDiffusePath ?? diffusePath, dims.Width, dims.Height, false))
            {
                TexIO.SaveMemoryBitmap(merged, memoryPath);
            }

            normalPath = memoryPath;
        }

        private static void MergeMaskCoverage(ref string maskPath, string sourceMaskPath, string coverageDiffusePath)
        {
            if (string.IsNullOrEmpty(sourceMaskPath) || string.IsNullOrEmpty(coverageDiffusePath))
                return;

            string memoryPath = "memory:\\" + sourceMaskPath.GetHashCode() + "_" + coverageDiffusePath.GetHashCode() + "_masked_grayscale";
            if (TexIO.VirtualFileSystem.ContainsKey(memoryPath))
            {
                maskPath = memoryPath;
                return;
            }

            var dims = ComputeSharpLayering.GetImageDimensions(sourceMaskPath);
            if (dims.Width <= 0 || dims.Height <= 0)
                return;

            using (var merged = ComputeSharpLayering.MergeAlphaChannelToRGBGpuFromPaths(
                sourceMaskPath, coverageDiffusePath, dims.Width, dims.Height, false))
            using (var grayscale = Grayscale.MakeGrayscale(merged))
            {
                TexIO.SaveMemoryBitmap(grayscale, memoryPath);
            }

            maskPath = memoryPath;
        }

        private static string BuildCacheKey(ResolvedAdvancedOverlay overlay, IList<AdvancedColorTableRow> effectiveRows)
        {
            int colorHash = effectiveRows?.Count ?? 0;
            if (effectiveRows != null)
            {
                foreach (var row in effectiveRows)
                    colorHash = HashCode.Combine(colorHash, row.Row, row.SubRowA?.Diffuse, row.SubRowB?.Diffuse, row.SubRowA?.Emissive, row.SubRowB?.Emissive, row.SubRowA?.Opacity, row.SubRowB?.Opacity);
            }

            int maskHash = overlay.CoverageMaskPaths?.Count ?? 0;
            if (overlay.CoverageMaskPaths != null)
            {
                foreach (var m in overlay.CoverageMaskPaths)
                    maskHash = HashCode.Combine(maskHash, m);
            }

            return HashCode.Combine(
                overlay.DiffusePath,
                overlay.NormalPath,
                overlay.IndexPath,
                overlay.GenerateDiffuse,
                colorHash,
                maskHash).ToString();
        }
    }
}
