using FFXIVLooseTextureCompiler;
using FFXIVLooseTextureCompiler.ImageProcessing;
using FFXIVLooseTextureCompiler.PathOrganization;
using LooseTextureCompilerCore.ProjectCreation;
using System;
using System.Collections.Generic;
using System.IO;

namespace DragAndDropTexturing.Onion;

/// <summary>
/// Routes Onion overlay layers into a DDT TextureSet with layout/map metadata.
/// </summary>
public static class OnionLayerApplicator
{
    private static readonly HashSet<string> StagedPaths = new(StringComparer.OrdinalIgnoreCase);

    public static void ApplyToTextureSet(
        TextureSet item,
        OnionModData mod,
        Action<TextureSet, string, string, System.Numerics.Vector4?, int> addToTextureSet,
        string playerRaceCode = null)
    {
        if (mod == null || !mod.Enabled || item == null || addToTextureSet == null)
            return;

        var effectiveLayers = mod.Meta.EffectiveLayers(mod.Settings.GroupSelections);
        foreach (var (layer, scope) in effectiveLayers)
        {
            if (!IsLayerApplicableForRace(layer, playerRaceCode))
                continue;

            string sourcePath = mod.ResolveLayerPath(layer);
            if (string.IsNullOrEmpty(sourcePath) || !File.Exists(sourcePath))
                continue;

            string stagedPath = StageLayerFile(sourcePath, layer);
            if (string.IsNullOrEmpty(stagedPath) || !File.Exists(stagedPath))
                continue;

            string overrideType = !string.IsNullOrWhiteSpace(layer.MapOverrideType)
                ? layer.MapOverrideType
                : InferOverrideType(stagedPath);

            System.Numerics.Vector4? tint = null;
            addToTextureSet(item, stagedPath, overrideType, tint, layer.DdtBlendMode);
        }
    }

    private static bool IsLayerApplicableForRace(OnionOverlayLayer layer, string playerRaceCode)
    {
        if (layer.Races == null || layer.Races.Count == 0) return true;
        if (string.IsNullOrEmpty(playerRaceCode)) return true;
        foreach (var race in layer.Races)
        {
            if (string.Equals(race, playerRaceCode, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string InferOverrideType(string stagedPath)
    {
        return ProjectHelper.SortUVTexture(new TextureSet(), stagedPath) switch
        {
            ImageManipulation.UVMapType.Normal => "Normal",
            ImageManipulation.UVMapType.Mask => "Mask",
            _ => "Base"
        };
    }

    private static string StageLayerFile(string sourcePath, OnionOverlayLayer layer)
    {
        try
        {
            string cacheRoot = Path.Combine(
                Plugin.PluginInterface.ConfigDirectory.FullName,
                "OnionLayerCache");
            Directory.CreateDirectory(cacheRoot);

            string ext = Path.GetExtension(sourcePath);
            string mapTag = layer.Map.ToLowerInvariant() switch
            {
                "norm" => "norm",
                "mask" => "mask",
                _ => "base"
            };
            string fileName = $"onion_{layer.LayoutUvTag}_{mapTag}_{Path.GetFileName(sourcePath)}";
            string dest = Path.Combine(cacheRoot, fileName);

            if (!File.Exists(dest) || File.GetLastWriteTimeUtc(sourcePath) > File.GetLastWriteTimeUtc(dest))
                File.Copy(sourcePath, dest, true);

            StagedPaths.Add(dest);
            return dest;
        }
        catch
        {
            return sourcePath;
        }
    }

    public static void ClearStagedCache()
    {
        string cacheRoot = Path.Combine(
            Plugin.PluginInterface.ConfigDirectory.FullName,
            "OnionLayerCache");
        if (!Directory.Exists(cacheRoot)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(cacheRoot))
            {
                try { File.Delete(file); } catch { }
            }
        }
        catch { }
        StagedPaths.Clear();
    }
}
