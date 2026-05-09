using System;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace DragAndDropTexturing.Windows;

public class MainWindow : Window, IDisposable
{
    private Plugin Plugin;

    public MainWindow(Plugin plugin)
        : base("Drag And Drop Texturing Config", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        Plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        ImGui.Spacing();
        bool enableStacking = Plugin.Configuration.EnableTextureStacking;
        if (ImGui.Checkbox("Enable Texture Stacking", ref enableStacking))
        {
            Plugin.Configuration.EnableTextureStacking = enableStacking;
            Plugin.Configuration.Save();
        }
        ImGui.TextWrapped("When enabled, dragging multiple textures over time will stack them (layering). When disabled, dragging a new texture replaces the previous one.");
        
        ImGui.Spacing();
        bool autoConvert = Plugin.Configuration.AutoUniversalConvert;
        if (ImGui.Checkbox("Auto Universal Convert", ref autoConvert))
        {
            Plugin.Configuration.AutoUniversalConvert = autoConvert;
            Plugin.Configuration.Save();
            
            var ddtForRebuild = Plugin.DragAndDropTextures;
            if (ddtForRebuild != null && ddtForRebuild.TextureHistory != null)
            {
                foreach (var key in ddtForRebuild.TextureHistory.Keys.ToList())
                {
                    ddtForRebuild.RebuildCategory(key);
                }
            }
        }
        ImGui.TextWrapped("When enabled, dropping a texture automatically applies universal conversion across all layers without holding the shift key. Warning: This can be slow.");
        
        ImGui.Spacing();
        var options = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes.Select(x => x.Name).ToArray();
        int selectedIndex = Math.Max(0, Array.IndexOf(options, Plugin.Configuration.DefaultUnderlaySkinType));
        if (ImGui.Combo("Default Underlay Skin Type", ref selectedIndex, options, options.Length))
        {
            Plugin.Configuration.DefaultUnderlaySkinType = options[selectedIndex];
            Plugin.Configuration.Save();
        }
        ImGui.TextWrapped("Selects the base skin underlay type when a custom transparent tattoo is dropped. If the character's base body doesn't support the specific skin variant, it will fall back to its own default.");
        
        ImGui.Separator();
        ImGui.Text("Active Texture Layers");
        
        var ddt = Plugin.DragAndDropTextures;
        if (ddt != null && ddt.TextureHistory != null)
        {
            var keys = ddt.TextureHistory.Keys.ToList();
            if (keys.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "No active textures dropped yet.");
            }
            foreach (var key in keys)
            {
                var list = ddt.TextureHistory[key];
                if (list.Count == 0) continue;
                
                if (ImGui.TreeNode(key))
                {
                    if (ImGui.Button("Clear All##" + key))
                    {
                        list.Clear();
                        ddt.RebuildCategory(key);
                        Plugin.Configuration.Save();
                    }
                    
                    bool changed = false;
                    for (int i = 0; i < list.Count; i++)
                    {
                        string path = list[i] ?? "";
                        ImGui.SetNextItemWidth(ImGui.GetWindowWidth() - 150);
                        if (ImGui.InputText("##path_" + key + i, ref path, 1024))
                        {
                            list[i] = path;
                        }
                        if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;
                        
                        if (ImGui.Button("Up##" + key + i) && i > 0)
                        {
                            var temp = list[i - 1];
                            list[i - 1] = list[i];
                            list[i] = temp;
                            changed = true;
                        }
                        
                        ImGui.SameLine();
                        if (ImGui.Button("Down##" + key + i) && i < list.Count - 1)
                        {
                            var temp = list[i + 1];
                            list[i + 1] = list[i];
                            list[i] = temp;
                            changed = true;
                        }

                        ImGui.SameLine();
                        if (ImGui.Button("Remove##" + key + i))
                        {
                            list.RemoveAt(i);
                            i--; // Adjust index since we removed
                            changed = true;
                        }
                    }

                    if (ImGui.Button("Add New Layer##" + key))
                    {
                        list.Add("");
                        changed = true;
                    }

                    if (changed)
                    {
                        ddt.RebuildCategory(key);
                        Plugin.Configuration.Save();
                    }
                    ImGui.TreePop();
                }
            }
        }
    }
}
