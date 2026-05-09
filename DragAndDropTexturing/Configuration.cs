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
    public string DefaultUnderlaySkinType { get; set; } = "Bibo Detailed";
    public System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>> TextureHistory { get; set; } = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.List<string>>();

    // the below exist just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
