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
                    }
                    
                    for (int i = 0; i < list.Count; i++)
                    {
                        ImGui.Text(System.IO.Path.GetFileName(list[i]));
                        ImGui.SameLine();
                        if (ImGui.Button("Remove##" + key + i))
                        {
                            list.RemoveAt(i);
                            ddt.RebuildCategory(key);
                            i--; // Adjust index since we removed
                        }
                    }
                    ImGui.TreePop();
                }
            }
        }
    }
}
