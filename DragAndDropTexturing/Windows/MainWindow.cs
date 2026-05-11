using System;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System.Collections.Generic;

namespace DragAndDropTexturing.Windows;

public class MainWindow : Window, IDisposable
{
    private Plugin Plugin;
    private List<Lumina.Excel.Sheets.Emote> _emotes = new();
    private string[] _emoteNames = new string[0];
    private string _emoteSearchFilter = "";

    public MainWindow(Plugin plugin)
        : base("Drag And Drop Texturing Config", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(400, 300),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        Plugin = plugin;

        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Emote>();
        if (sheet != null) {
            _emotes = sheet.Where(x => !string.IsNullOrEmpty(x.Name.ExtractText())).OrderBy(x => x.Name.ExtractText()).ToList();
            _emoteNames = _emotes.Select(x => x.Name.ExtractText()).ToArray();
        }
    }

    public void Dispose() { }

    public override void Draw()
    {
        bool isDownloading = Plugin.DragAndDropTextures != null && Plugin.DragAndDropTextures.IsDownloadingDLC;
        if (isDownloading)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), "Background DLC download in progress. Settings locked.");
            ImGui.Spacing();
            
            float progress = Plugin.DragAndDropTextures.DLCDownloadProgress;
            if (progress > 0f && progress < 1f)
            {
                ImGui.ProgressBar(progress, new Vector2(-1, 0), $"Downloading DLC: {(progress * 100):0.0}%");
            }
            else
            {
                float bounce = (float)Math.Abs(Math.Sin(ImGui.GetTime() * 2.0));
                ImGui.ProgressBar(bounce, new Vector2(-1, 0), "Fetching DLC (Please wait)...");
            }
            ImGui.Spacing();

            ImGui.BeginDisabled();
        }

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
        ImGui.TextWrapped("When enabled, textures are generated for all possible body types at once (Potentially slower generation)");
        
        ImGui.Spacing();
        var options = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes.Select(x => x.Name).ToArray();
        int selectedIndex = Math.Max(0, Array.IndexOf(options, Plugin.Configuration.DefaultUnderlaySkinType));
        if (ImGui.Combo("Default Underlay Skin Type", ref selectedIndex, options, options.Length))
        {
            Plugin.Configuration.DefaultUnderlaySkinType = options[selectedIndex];
            Plugin.Configuration.Save();
        }
        ImGui.TextWrapped("Selects the base skin underlay type when a custom transparent tattoo is dropped. If the character's base body doesn't support the specific skin variant, it will fall back to its own default.");
        
        ImGui.Spacing();
        bool usePriorityMod = Plugin.Configuration.UsePriorityBodyMod;
        if (ImGui.Checkbox("Use Textures From Priority Body Mod", ref usePriorityMod))
        {
            Plugin.Configuration.UsePriorityBodyMod = usePriorityMod;
            FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OverrideMode = usePriorityMod;
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
        ImGui.TextWrapped("When enabled, the compiler will scan your Penumbra modlist and automatically inherit the body texture of your highest priority active skin mod as the underlay for transparent overlays.");
        
        if (usePriorityMod)
        {
            ImGui.Spacing();
            ImGui.Text("Active Body Overrides:");
            ImGui.Indent();
            var ddtForUI = Plugin.DragAndDropTextures;
            if (ddtForUI != null && ddtForUI.ActiveBodyOverrides.Count > 0)
            {
                foreach (var kvp in ddtForUI.ActiveBodyOverrides)
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), $"{kvp.Key}: {kvp.Value}");
                }
            }
            else
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "None detected (or scan pending)");
            }
            if (ImGui.Button("Scan For Overrides"))
            {
                ddtForUI?.RefreshActiveOverrides();
            }
            ImGui.Unindent();
        }
        
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
                        if (ImGui.IsItemHovered())
                        {
                            if (Plugin.DragDropManager.CreateImGuiTarget("TextureDropTarget", out var files, out _))
                            {
                                if (files.Count > 0)
                                {
                                    list[i] = files[0];
                                    changed = true;
                                }
                            }
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

        ImGui.Separator();
        ImGui.Text("Contextual Layers");

        if (ImGui.Button("Add Contextual Layer"))
        {
            Plugin.Configuration.ContextualLayers.Add(new ContextualLayer());
            Plugin.Configuration.Save();
        }

        for (int i = 0; i < Plugin.Configuration.ContextualLayers.Count; i++)
        {
            var layer = Plugin.Configuration.ContextualLayers[i];
            
            if (ImGui.TreeNode($"Layer {i + 1}: {layer.Name}##ContextLayer_{i}"))
            {
                bool changed = false;

                string name = layer.Name;
                if (ImGui.InputText($"Name##ContextName_{i}", ref name, 255))
                {
                    layer.Name = name;
                    changed = true;
                }

                int triggerType = (int)layer.Trigger;
                string[] triggerNames = Enum.GetNames(typeof(TriggerType));
                if (ImGui.Combo($"Trigger Type##ContextTrigger_{i}", ref triggerType, triggerNames, triggerNames.Length))
                {
                    layer.Trigger = (TriggerType)triggerType;
                    changed = true;
                }

                if (layer.Trigger == TriggerType.Emote)
                {
                    int emoteId = layer.EmoteId;
                    var currentEmote = _emotes.FirstOrDefault(x => x.RowId == emoteId);
                    string currentEmoteName = currentEmote.RowId != 0 ? currentEmote.Name.ExtractText() : $"ID: {emoteId}";

                    if (ImGui.BeginCombo($"Emote##ContextEmote_{i}", currentEmoteName))
                    {
                        ImGui.InputText("Search##EmoteSearch", ref _emoteSearchFilter, 255);
                        string filter = _emoteSearchFilter.ToLower();

                        for (int eIndex = 0; eIndex < _emotes.Count; eIndex++)
                        {
                            var e = _emotes[eIndex];
                            string eName = _emoteNames[eIndex];
                            if (string.IsNullOrEmpty(filter) || eName.ToLower().Contains(filter))
                            {
                                bool isSelected = e.RowId == emoteId;
                                if (ImGui.Selectable($"{eName}##{e.RowId}", isSelected))
                                {
                                    layer.EmoteId = (ushort)e.RowId;
                                    changed = true;
                                    ImGui.CloseCurrentPopup();
                                }
                                if (isSelected) ImGui.SetItemDefaultFocus();
                            }
                        }
                        ImGui.EndCombo();
                    }
                }
                else if (layer.Trigger == TriggerType.HP_Threshold)
                {
                    int hpThresh = layer.HPThresholdPercentage;
                    if (ImGui.SliderInt($"HP Threshold %##ContextHP_{i}", ref hpThresh, 1, 99))
                    {
                        layer.HPThresholdPercentage = hpThresh;
                        changed = true;
                    }
                }

                int duration = layer.DurationSeconds;
                if (ImGui.InputInt($"Duration (Seconds)##ContextDur_{i}", ref duration))
                {
                    layer.DurationSeconds = Math.Max(1, duration);
                    changed = true;
                }

                string[] bodyParts = { "body", "face", "eyes", "eyebrows" };
                int partIndex = Math.Max(0, Array.IndexOf(bodyParts, layer.TargetBodyPart));
                if (ImGui.Combo($"Target Body Part##ContextPart_{i}", ref partIndex, bodyParts, bodyParts.Length))
                {
                    layer.TargetBodyPart = bodyParts[partIndex];
                    changed = true;
                }

                string path = layer.TexturePath;
                if (ImGui.InputText($"Texture Path##ContextPath_{i}", ref path, 1024))
                {
                    layer.TexturePath = path;
                    changed = true;
                }
                if (ImGui.IsItemHovered())
                {
                    if (Plugin.DragDropManager.CreateImGuiTarget("TextureDropTargetContext", out var files, out _))
                    {
                        if (files.Count > 0)
                        {
                            layer.TexturePath = files[0];
                            changed = true;
                        }
                    }
                }

                if (ImGui.Button($"Remove Layer##ContextRemove_{i}"))
                {
                    Plugin.Configuration.ContextualLayers.RemoveAt(i);
                    i--;
                    changed = true;
                }

                if (changed)
                {
                    Plugin.Configuration.Save();
                }

                ImGui.TreePop();
            }
        }

        if (isDownloading)
        {
            ImGui.EndDisabled();
        }
    }
}
