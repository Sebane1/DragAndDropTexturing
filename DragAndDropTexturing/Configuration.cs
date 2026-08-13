using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

using System.Collections.Generic;
using System.Numerics;

namespace DragAndDropTexturing;

[Serializable]
public class ActiveLayerPreset
{
    public string Name { get; set; } = "New Preset";
    public uint LinkedJobId { get; set; } = 0; // 0 = none
    
    public Dictionary<string, List<string>> TextureHistory { get; set; } = new();
    public Dictionary<string, List<Vector4>> TextureHistoryTints { get; set; } = new();
    public Dictionary<string, List<int>> TextureHistoryBlendModes { get; set; } = new();
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;
    public bool EnableTextureStacking { get; set; } = true;
    public bool AutoUniversalConvert { get; set; } = false;
    public bool GenerateNormals { get; set; } = false;
    public int ExportCompression { get; set; } = 0; // 0 = Speed (Uncompressed), 1 = BC7 High Quality
    public float ExportScale { get; set; } = 1.0f;
    public bool AutoDistanceExportQuality { get; set; } = false;
    public string DefaultUnderlaySkinType { get; set; } = "Bibo Detailed";
    public bool UsePriorityBodyMod { get; set; } = true;
    public int FallbackBodyType { get; set; } = 0; // 0 = Auto-detect, 1 = Vanilla, 2 = Bibo+, 3 = Gen3, 4 = TBSE, 5 = Otopop
    public int LastKnownRace { get; set; } = -1;
    public int LastKnownClan { get; set; } = -1;
    public int LastKnownGender { get; set; } = -1;
    public int LastKnownFace { get; set; } = -1;
    public Dictionary<string, List<string>> TextureHistory { get; set; } = new Dictionary<string, List<string>>();
    public Dictionary<string, List<Vector4>> TextureHistoryTints { get; set; } = new Dictionary<string, List<Vector4>>();
    public Dictionary<string, List<int>> TextureHistoryBlendModes { get; set; } = new Dictionary<string, List<int>>();
    public Dictionary<string, Dictionary<string, List<string>>> CollectionSortedTextureHistory { get; set; } = new();
    public Dictionary<string, Dictionary<string, List<Vector4>>> CollectionSortedTextureHistoryTints { get; set; } = new();
    public Dictionary<string, Dictionary<string, List<int>>> CollectionSortedTextureHistoryBlendModes { get; set; } = new();
    public Dictionary<string, int> PersistedContextualStacks { get; set; } = new Dictionary<string, int>();
    public string PersistedProceduralCanvasPath { get; set; } = null;
    public List<string> RecentLayers { get; set; } = new List<string>();
    public List<DragAndDropTexturing.VideoPlayback.AnimatedLayerDefinition> AnimatedLayers { get; set; } = new List<DragAndDropTexturing.VideoPlayback.AnimatedLayerDefinition>();
    public List<ActiveLayerPreset> ActiveLayerPresets { get; set; } = new();
    public int LanguageOverride { get; set; } = -1; // -1 = Auto
    public Dictionary<string, Vector4> PenumbraOverlayTints { get; set; } = new Dictionary<string, Vector4>();
    public Dictionary<string, Vector4> PenumbraOverlayGlowTints { get; set; } = new Dictionary<string, Vector4>();

    public Dictionary<string, Dictionary<string, Vector4>> CollectionSortedPenumbraOverlayTints { get; set; } = new Dictionary<string, Dictionary<string, Vector4>>();
    public Dictionary<string, Dictionary<string, Vector4>> CollectionSortedPenumbraOverlayGlowTints { get; set; } = new Dictionary<string, Dictionary<string, Vector4>>();
    /// <summary>User-edited Proteus color table rows per overlay (collection → overlay path key → rows 1–16).</summary>
    public Dictionary<string, Dictionary<string, List<Overlays.AdvancedColorTableRow>>> CollectionSortedPenumbraOverlayColorRows { get; set; } = new();

    /// <summary>0 = match texture UV, 1 = match worn body, 2 = pick body mod manually.</summary>
    public int PainterPreviewMeshMode { get; set; } = 0;
    /// <summary>1 = Bibo+, 2 = Gen3, 3 = TBSE, 0 = Vanilla when PainterPreviewMeshMode is manual.</summary>
    public int PainterManualPreviewBodyType { get; set; } = 2;
    public bool PainterUvBridgeEnabled { get; set; } = true;
    /// <summary>Paint-only 3D preview: hides skin base and companion body meshes.</summary>
    public bool PainterTransparentSkinPreview { get; set; } = false;
    /// <summary>Project painter preview onto the live target character using game camera + bones.</summary>
    public bool PainterOverlayOnCharacter { get; set; } = false;

    // the below exist just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
