using Dalamud.Configuration;
using Dalamud.Plugin;
using System;

namespace DragAndDropTexturing;

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
    public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> TextureHistory { get; set; } = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>();
    public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Numerics.Vector4>> TextureHistoryTints { get; set; } = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<System.Numerics.Vector4>>();
    public System.Collections.Generic.Dictionary<string, int> PersistedContextualStacks { get; set; } = new System.Collections.Generic.Dictionary<string, int>();
    public string PersistedProceduralCanvasPath { get; set; } = null;
    public System.Collections.Generic.List<string> RecentLayers { get; set; } = new System.Collections.Generic.List<string>();
    public System.Collections.Generic.List<DragAndDropTexturing.VideoPlayback.AnimatedLayerDefinition> AnimatedLayers { get; set; } = new System.Collections.Generic.List<DragAndDropTexturing.VideoPlayback.AnimatedLayerDefinition>();
    public int LanguageOverride { get; set; } = -1; // -1 = Auto
    // the below exist just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
