using System;
using System.IO;
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

        if (ImGui.BeginTabBar("MainWindowTabs"))
        {
            if (ImGui.BeginTabItem("Active Layers"))
            {
                DrawActiveLayers();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Contextual Layers"))
            {
                DrawContextualLayers();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem("Settings"))
            {
                DrawSettings();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        if (isDownloading)
        {
            ImGui.EndDisabled();
        }
    }

    private void DrawSettings()
    {
        ImGui.Spacing();

        ImGui.Separator();
        ImGui.Text("Current Body Type Detection:");
        var localPlayer = Plugin.SafeGameObjectManager.LocalPlayer;
        if (localPlayer != null)
        {
            var character = localPlayer as Dalamud.Game.ClientState.Objects.Types.ICharacter;
            if (character != null)
            {
                var customization = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
                Guid collectionId = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(localPlayer.ObjectIndex).Item3.Id;
                int gender = customization.Customize.Gender.Value;
                int bodyType = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(collectionId, gender, out string modName, Plugin);
                
                string bodyString = "Vanilla / Unknown";
                if (bodyType == 1) bodyString = "Bibo+";
                else if (bodyType == 2) bodyString = "Gen3 / Eve / Pythia";
                else if (bodyType == 3) bodyString = "TBSE";

                if (bodyType != -1)
                    ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), $"Detected: {bodyString}");
                else
                    ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.2f, 1.0f), "Detected: Vanilla (No body mod found)");
                
                if (!string.IsNullOrEmpty(modName))
                {
                    ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), $"Detected From Mod: {modName}");
                }
            }
        }
        else
        {
            ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), "Player not loaded.");
        }
        ImGui.Separator();
        
        ImGui.Spacing();

        if (ImGui.Button("Open 3D Model Preview (Experimental)"))
        {
            Plugin.MdlPreviewWindow.IsOpen = !Plugin.MdlPreviewWindow.IsOpen;
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
                ddtForRebuild.RebuildAllCategories();
            }
        }
        ImGui.TextWrapped("When enabled, textures are generated for all possible body types at once (Potentially slower generation)");
        
        ImGui.Spacing();
        bool generateNormals = Plugin.Configuration.GenerateNormals;
        if (ImGui.Checkbox("Generate Normals", ref generateNormals))
        {
            Plugin.Configuration.GenerateNormals = generateNormals;
            Plugin.Configuration.Save();
            
            var ddtForRebuild = Plugin.DragAndDropTextures;
            if (ddtForRebuild != null && ddtForRebuild.TextureHistory != null)
            {
                ddtForRebuild.RebuildAllCategories();
            }
        }
        ImGui.TextWrapped("When enabled, normal maps will be automatically generated from base textures if they are missing.");

        ImGui.Spacing();
        int exportCompression = Plugin.Configuration.ExportCompression;
        string[] compressionOptions = { "Speed (Uncompressed)", "Sync Friendly (Mode 6 BC7)" };
        if (ImGui.Combo("Export Compression", ref exportCompression, compressionOptions, compressionOptions.Length))
        {
            Plugin.Configuration.ExportCompression = exportCompression;
            Plugin.Configuration.Save();
            
            var ddtForRebuild = Plugin.DragAndDropTextures;
            if (ddtForRebuild != null && ddtForRebuild.TextureHistory != null)
            {
                ddtForRebuild.RebuildAllCategories();
            }
        }
        ImGui.TextWrapped("Selects the texture compression method used for exports. Speed is faster to generate but results in larger file sizes. BC7 offers the lowest file sizes for Dawntrail, but is performance heavy exporting.");
        
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
                ddtForRebuild.RebuildAllCategories();
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
    }

    private int _selectedActiveLayerIndex = 0;
    private Dictionary<string, Dalamud.Interface.Textures.ISharedImmediateTexture> _textureCache = new();

    private Dalamud.Interface.Textures.ISharedImmediateTexture GetPreviewTexture(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;
        if (!_textureCache.ContainsKey(path))
        {
            _textureCache[path] = Plugin.TextureProvider.GetFromFile(path);
        }
        return _textureCache[path];
    }

    private void DrawActiveLayers()
    {
        ImGui.Spacing();
        var ddt = Plugin.DragAndDropTextures;
        if (ddt != null && ddt.TextureHistory != null)
        {
            var keys = ddt.TextureHistory.Keys.Where(k => ddt.TextureHistory[k].Count > 0).ToList();
            if (keys.Count == 0)
            {
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "No active textures dropped yet.");
                ImGui.Spacing();
                if (ImGui.Button("Open Texture Painter"))
                {
                    Plugin.OpenPaintWindow();
                }
                return;
            }

            ImGui.BeginChild("ActiveLayersList", new Vector2(200, 0), true);
            for (int i = 0; i < keys.Count; i++)
            {
                bool isSelected = _selectedActiveLayerIndex == i;
                if (ImGui.Selectable($"{keys[i]}##SelectActiveLayer_{i}", isSelected))
                {
                    _selectedActiveLayerIndex = i;
                }
            }
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginChild("ActiveLayerDetails", new Vector2(0, 0), true);
            if (_selectedActiveLayerIndex >= 0 && _selectedActiveLayerIndex < keys.Count)
            {
                string key = keys[_selectedActiveLayerIndex];
                var list = ddt.TextureHistory[key];
                
                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), $"Active Layers for: {key}");
                ImGui.Separator();
                
                if (ImGui.Button("Clear All##" + key))
                {
                    list.Clear();
                    ddt.RebuildCategory(key);
                    Plugin.Configuration.Save();
                    // Keep index bounded if list clears and we want to prevent out of bounds next frame
                }
                
                bool changed = false;
                for (int i = 0; i < list.Count; i++)
                {
                    string path = list[i] ?? "";
                    
                    var tex = GetPreviewTexture(path);
                    var wrap = tex?.GetWrapOrDefault();
                    
                    if (wrap != null)
                    {
                        ImGui.Image(wrap.Handle, new Vector2(40, 40));
                        ImGui.SameLine();
                        // Adjust Y cursor to vertically align the InputText with the image
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 10);
                        ImGui.SetNextItemWidth(ImGui.GetWindowWidth() - 200);
                    }
                    else
                    {
                        ImGui.SetNextItemWidth(ImGui.GetWindowWidth() - 150);
                    }
                    
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
                                if (Path.GetExtension(files[0]).Equals(".psd", StringComparison.OrdinalIgnoreCase))
                                {
                                    Plugin.PsdImportWindow.StartImport(files[0]);
                                }
                                else
                                {
                                    list[i] = files[0];
                                    changed = true;
                                }
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
                        i--;
                        changed = true;
                    }

                    // Edit button — opens the Texture Painter with this layer loaded
                    if (!string.IsNullOrEmpty(path) && File.Exists(path))
                    {
                        ImGui.SameLine();
                        bool canEdit = true;
                        string lowerPath = path.ToLower();
                        if (lowerPath.Contains("bibo") || lowerPath.Contains("b+") || lowerPath.Contains("turali bod") || lowerPath.Contains("lavabod") || lowerPath.Contains("rue") || lowerPath.Contains("yab") || lowerPath.Contains("yet another body") || lowerPath.Contains("lithe"))
                            canEdit = Plugin.IsBodyAvailable("bibo");
                        else if (lowerPath.Contains("gen3") || lowerPath.Contains("tfgen3") || lowerPath.Contains("pythia") || lowerPath.Contains("exqb") || System.Text.RegularExpressions.Regex.IsMatch(lowerPath, @"(^|[^a-z])eve([^a-z]|$)") || lowerPath.Contains("gaia"))
                            canEdit = Plugin.IsBodyAvailable("gen3");
                        else if (lowerPath.Contains("tbse") || lowerPath.Contains("the body se") || lowerPath.Contains("hrbody"))
                            canEdit = Plugin.IsBodyAvailable("tbse");

                        if (!canEdit) ImGui.BeginDisabled();
                        if (ImGui.Button("Edit##" + key + i))
                        {
                            Plugin.OpenPaintWindow(path);
                        }
                        if (!canEdit)
                        {
                            ImGui.EndDisabled();
                            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            {
                                ImGui.SetTooltip("This layer requires a body mod that is not currently available in your Penumbra directory.");
                            }
                        }
                    }
                }

                if (ImGui.Button("Add New Layer (Open Painter)##" + key))
                {
                    Plugin.OpenPaintWindow();
                }

                if (changed)
                {
                    ddt.RebuildCategory(key);
                    Plugin.Configuration.Save();
                }
            }
            ImGui.EndChild();
        }
    }

    private int _selectedContextualLayerIndex = 0;

    private void DrawContextualLayers()
    {
        ImGui.Spacing();
        if (ImGui.Button("Add Contextual Layer"))
        {
            Plugin.ContextualLayerManager.CreateNewLayer();
            _selectedContextualLayerIndex = Plugin.ContextualLayerManager.ContextualLayers.Count - 1;
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Open Imports Folder"))
        {
            string importFolder = System.IO.Path.Combine(Plugin.ContextualLayerManager.RootDirectory, "Imports");
            if (!System.IO.Directory.Exists(importFolder)) System.IO.Directory.CreateDirectory(importFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
            {
                FileName = importFolder,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        
        ImGui.SameLine();
        if (ImGui.Button("Scan for Imports"))
        {
            Plugin.ContextualLayerManager.ImportLayersFromImportsFolder();
            if (Plugin.ContextualLayerManager.ContextualLayers.Count > 0)
                _selectedContextualLayerIndex = Plugin.ContextualLayerManager.ContextualLayers.Count - 1;
        }

        ImGui.Spacing();

        if (Plugin.ContextualLayerManager.ContextualLayers.Count == 0)
        {
            ImGui.Text("No contextual layers configured.");
            return;
        }

        ImGui.BeginChild("ContextLayersList", new Vector2(200, 0), true);
        for (int i = 0; i < Plugin.ContextualLayerManager.ContextualLayers.Count; i++)
        {
            var layer = Plugin.ContextualLayerManager.ContextualLayers[i];
            bool isSelected = _selectedContextualLayerIndex == i;
            if (ImGui.Selectable($"{layer.Name}##SelectLayer_{i}", isSelected))
            {
                _selectedContextualLayerIndex = i;
            }
        }
        ImGui.EndChild();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("You can drop .clmp files directly here to import them!");
        }

        if (Plugin.DragDropManager.CreateImGuiTarget("ContextualLayerImportTarget", out var droppedFiles, out _))
        {
            foreach (var f in droppedFiles)
            {
                if (f.EndsWith(".clmp", StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.ContextualLayerManager.ImportLayerFromFile(f);
                }
            }
            if (Plugin.ContextualLayerManager.ContextualLayers.Count > 0)
                _selectedContextualLayerIndex = Plugin.ContextualLayerManager.ContextualLayers.Count - 1;
        }

        ImGui.SameLine();

        ImGui.BeginChild("ContextLayerDetails", new Vector2(0, 0), true);
        if (_selectedContextualLayerIndex >= 0 && _selectedContextualLayerIndex < Plugin.ContextualLayerManager.ContextualLayers.Count)
        {
            var layer = Plugin.ContextualLayerManager.ContextualLayers[_selectedContextualLayerIndex];
            bool changed = false;

            string name = layer.Name;
            if (ImGui.InputText("Name##ContextName", ref name, 255))
            {
                layer.Name = name;
                changed = true;
            }

            int triggerType = (int)layer.Trigger;
            string[] triggerNames = Enum.GetNames(typeof(TriggerType));
            if (ImGui.Combo("Trigger Type##ContextTrigger", ref triggerType, triggerNames, triggerNames.Length))
            {
                layer.Trigger = (TriggerType)triggerType;
                changed = true;
            }

            int clearType = (int)layer.ClearTrigger;
            string[] clearNames = Enum.GetNames(typeof(ClearCondition));
            if (ImGui.Combo("Clear Condition##ContextClear", ref clearType, clearNames, clearNames.Length))
            {
                layer.ClearTrigger = (ClearCondition)clearType;
                changed = true;
            }

            if (layer.Trigger == TriggerType.Emote)
            {
                int emoteId = layer.EmoteId;
                var currentEmote = _emotes.FirstOrDefault(x => x.RowId == emoteId);
                string currentEmoteName = currentEmote.RowId != 0 ? currentEmote.Name.ExtractText() : $"ID: {emoteId}";

                if (ImGui.BeginCombo("Emote##ContextEmote", currentEmoteName))
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
                if (ImGui.SliderInt("HP Threshold %##ContextHP", ref hpThresh, 1, 99))
                {
                    layer.HPThresholdPercentage = hpThresh;
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Audio_Path_Load)
            {
                string audioPath = layer.AudioTriggerPath;
                if (ImGui.InputText("Audio Path / Name (.scd)##ContextAudio", ref audioPath, 255))
                {
                    layer.AudioTriggerPath = audioPath;
                    changed = true;
                }
                
                int reqSounds = layer.RequiredSoundsPerStack;
                if (ImGui.InputInt("Required Sounds per Stack##ContextSounds", ref reqSounds))
                {
                    layer.RequiredSoundsPerStack = Math.Max(1, reqSounds);
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Chat_Message)
            {
                string chatRegex = layer.ChatRegex;
                if (ImGui.InputText("Chat Regex Pattern##ContextChat", ref chatRegex, 255))
                {
                    layer.ChatRegex = chatRegex;
                    changed = true;
                }
                
                bool emoteOnly = layer.ChatFilterCustomEmotesOnly;
                if (ImGui.Checkbox("Only trigger on Emotes (/em or standard)##ContextChatEmote", ref emoteOnly))
                {
                    layer.ChatFilterCustomEmotesOnly = emoteOnly;
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Enemy_Nearby)
            {
                string enemyName = layer.TargetEnemyName;
                if (ImGui.InputText("Target Enemy Name##ContextEnemy", ref enemyName, 255))
                {
                    layer.TargetEnemyName = enemyName;
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Territory_ID)
            {
                int territoryId = (int)layer.TargetTerritoryId;
                if (ImGui.InputInt("Territory ID##ContextTerritory", ref territoryId))
                {
                    layer.TargetTerritoryId = (uint)Math.Max(0, territoryId);
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Weather_ID)
            {
                int weatherId = (int)layer.TargetWeatherId;
                if (ImGui.InputInt("Weather ID##ContextWeather", ref weatherId))
                {
                    layer.TargetWeatherId = (uint)Math.Max(0, weatherId);
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.In_Game_Time)
            {
                int startHour = layer.TargetTimeStartHour;
                int endHour = layer.TargetTimeEndHour;
                
                if (ImGui.SliderInt("Start Hour (ET)##ContextTimeStart", ref startHour, 0, 23))
                {
                    layer.TargetTimeStartHour = startHour;
                    changed = true;
                }
                if (ImGui.SliderInt("End Hour (ET)##ContextTimeEnd", ref endHour, 0, 23))
                {
                    layer.TargetTimeEndHour = endHour;
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Kill_Count || layer.Trigger == TriggerType.Action_Used)
            {
                int reqKills = layer.RequiredKillsPerStack;
                string stackLabel = layer.Trigger == TriggerType.Kill_Count ? "Required Kills per Stack" : "Required Actions per Stack";
                if (ImGui.InputInt($"{stackLabel}##ContextKills", ref reqKills))
                {
                    layer.RequiredKillsPerStack = Math.Max(1, reqKills);
                    changed = true;
                }
                
                int decay = layer.DecayIntervalSeconds;
                if (ImGui.InputInt("Decay Interval (Seconds)##ContextDecay", ref decay))
                {
                    layer.DecayIntervalSeconds = Math.Max(0, decay);
                    changed = true;
                }
            }

            if (layer.Trigger == TriggerType.Emote || 
                layer.Trigger == TriggerType.Audio_Path_Load || 
                layer.Trigger == TriggerType.Chat_Message)
            {
                int duration = layer.DurationSeconds;
                if (ImGui.InputInt("Duration (Seconds)##ContextDur", ref duration))
                {
                    layer.DurationSeconds = Math.Max(1, duration);
                    changed = true;
                }
            }

            string[] bodyParts = { "body", "face", "eyes", "eyebrows" };
            int partIndex = Math.Max(0, Array.IndexOf(bodyParts, layer.TargetBodyPart));
            if (ImGui.Combo("Target Body Part##ContextPart", ref partIndex, bodyParts, bodyParts.Length))
            {
                layer.TargetBodyPart = bodyParts[partIndex];
                changed = true;
            }

            ImGui.Spacing();
            if (ImGui.Button("Open Folder##ContextFolder"))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = layer.DirectoryPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }

            ImGui.SameLine();

            if (ImGui.Button("Export Layer##ContextExport"))
            {
                Plugin.ContextualLayerManager.ExportLayer(layer);
            }

            ImGui.SameLine();

            if (ImGui.Button("Remove Layer##ContextRemove"))
            {
                Plugin.ContextualLayerManager.DeleteLayer(layer);
                _selectedContextualLayerIndex = Math.Max(0, _selectedContextualLayerIndex - 1);
            }
            else if (changed)
            {
                layer.Save();
            }
        }
        ImGui.EndChild();
    }
}
