using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using System.Collections.Generic;
using Dalamud.Interface.ImGuiFileDialog;
using DragAndDropTexturing.LanguageHelpers;

namespace DragAndDropTexturing.Windows;

public class MainWindow : Window, IDisposable
{
    private Plugin Plugin;
    private List<Lumina.Excel.Sheets.Emote> _emotes = new();
    private string[] _emoteNames = new string[0];
    private string _emoteSearchFilter = "";
    private readonly FileDialogManager _fileDialogManager = new();

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
        _fileDialogManager.Draw();
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
            if (ImGui.BeginTabItem(Translator.LocalizeUI("Active Layers")))
            {
                DrawActiveLayers();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Translator.LocalizeUI("Layer History")))
            {
                DrawLayerHistory();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Translator.LocalizeUI("Contextual Layers")))
            {
                DrawContextualLayers();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Translator.LocalizeUI("Settings")))
            {
                DrawSettings();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Translator.LocalizeUI("Diagnostics")))
            {
                DrawDiagnostics();
                ImGui.EndTabItem();
            }
            ImGui.EndTabBar();
        }

        if (isDownloading)
        {
            ImGui.EndDisabled();
        }
    }

    private int _cachedBodyType = -2;
    private string _cachedBodyModName = null;
    private DateTime _lastBodyTypeCheck = DateTime.MinValue;

    private void DrawSettings()
    {
        ImGui.Spacing();
        ImGui.Separator();
        
        ImGui.Text(Translator.LocalizeUI("Language Override:"));
        int langOverride = Plugin.Configuration.LanguageOverride;
        string[] languagesWithAuto = new string[Translator.LanguageStrings.Length + 1];
        languagesWithAuto[0] = "Auto Detect";
        for (int i = 0; i < Translator.LanguageStrings.Length; i++)
        {
            languagesWithAuto[i + 1] = Translator.LanguageStrings[i];
        }
        
        int comboIndex = langOverride + 1;
        if (ImGui.Combo("##LanguageOverride", ref comboIndex, languagesWithAuto, languagesWithAuto.Length))
        {
            Plugin.Configuration.LanguageOverride = comboIndex - 1;
            Plugin.Configuration.Save();
            
            if (Plugin.Configuration.LanguageOverride >= 0)
            {
                Translator.UiLanguage = (LanguageEnum)Plugin.Configuration.LanguageOverride;
            }
            else
            {
                Translator.UiLanguage = Plugin.ClientState.ClientLanguage switch
                {
                    Dalamud.Game.ClientLanguage.Japanese => LanguageEnum.Japanese,
                    Dalamud.Game.ClientLanguage.French => LanguageEnum.French,
                    Dalamud.Game.ClientLanguage.German => LanguageEnum.German,
                    _ => LanguageEnum.English,
                };
            }
        }
        ImGui.TextWrapped(Translator.LocalizeUI("Overrides the detected game language. Changes apply to UI immediately, but translating entirely new text requires network requests."));

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text(Translator.LocalizeUI("Current Body Type Detection:"));
        if ((DateTime.Now - _lastBodyTypeCheck).TotalSeconds > 5)
        {
            var localPlayer = Plugin.SafeGameObjectManager.LocalPlayer;
            if (localPlayer != null)
            {
                var character = localPlayer as Dalamud.Game.ClientState.Objects.Types.ICharacter;
                if (character != null)
                {
                    var customization = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
                    Guid collectionId = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(localPlayer.ObjectIndex).Item3.Id;
                    int gender = customization.Customize.Gender.Value;
                    _cachedBodyType = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(collectionId, gender, out _cachedBodyModName, Plugin);
                }
            }
            else
            {
                _cachedBodyType = -2;
            }
            _lastBodyTypeCheck = DateTime.Now;
        }

        if (_cachedBodyType != -2)
        {
            string bodyString = "Vanilla / Unknown";
            if (_cachedBodyType == 1) bodyString = "Bibo+";
            else if (_cachedBodyType == 2) bodyString = "Gen3 / Eve / Pythia";
            else if (_cachedBodyType == 3) bodyString = "TBSE";

            if (_cachedBodyType != -1)
                ImGui.TextColored(new Vector4(0.2f, 1.0f, 0.2f, 1.0f), Translator.LocalizeUI("Detected:") + $" {bodyString}");
            else
                ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.2f, 1.0f), Translator.LocalizeUI("Detected: Vanilla (No body mod found)"));
            
            if (!string.IsNullOrEmpty(_cachedBodyModName))
            {
                ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.7f, 1.0f), Translator.LocalizeUI("Detected From Mod:") + $" {_cachedBodyModName}");
            }
        }
        else if (_cachedBodyType == -2)
        {
            ImGui.TextColored(new Vector4(1.0f, 0.5f, 0.0f, 1.0f), Translator.LocalizeUI("Player not loaded."));
        }
        ImGui.Separator();
        
        ImGui.Spacing();

        if (ImGui.Button(Translator.LocalizeUI("Open 3D Model Preview (Experimental)")))
        {
            Plugin.MdlPreviewWindow.IsOpen = !Plugin.MdlPreviewWindow.IsOpen;
        }
        


        ImGui.Spacing();
        bool enableStacking = Plugin.Configuration.EnableTextureStacking;
        if (ImGui.Checkbox(Translator.LocalizeUI("Enable Texture Stacking"), ref enableStacking))
        {
            Plugin.Configuration.EnableTextureStacking = enableStacking;
            Plugin.Configuration.Save();
        }
        ImGui.TextWrapped(Translator.LocalizeUI("When enabled, dragging multiple textures over time will stack them (layering). When disabled, dragging a new texture replaces the previous one."));
        
        ImGui.Spacing();
        bool autoConvert = Plugin.Configuration.AutoUniversalConvert;
        if (ImGui.Checkbox(Translator.LocalizeUI("Auto Universal Convert"), ref autoConvert))
        {
            Plugin.Configuration.AutoUniversalConvert = autoConvert;
            Plugin.Configuration.Save();
            
            var ddtForRebuild = Plugin.DragAndDropTextures;
            if (ddtForRebuild != null && ddtForRebuild.TextureHistory != null)
            {
                ddtForRebuild.RebuildAllCategories();
            }
        }
        ImGui.TextWrapped(Translator.LocalizeUI("When enabled, textures are generated for all possible body types at once (Potentially slower generation)"));
        
        ImGui.Spacing();
        bool generateNormals = Plugin.Configuration.GenerateNormals;
        if (ImGui.Checkbox(Translator.LocalizeUI("Generate Normals"), ref generateNormals))
        {
            Plugin.Configuration.GenerateNormals = generateNormals;
            Plugin.Configuration.Save();
            
            var ddtForRebuild = Plugin.DragAndDropTextures;
            if (ddtForRebuild != null && ddtForRebuild.TextureHistory != null)
            {
                ddtForRebuild.RebuildAllCategories();
            }
        }
        ImGui.TextWrapped(Translator.LocalizeUI("When enabled, normal maps will be automatically generated from base textures if they are missing."));

        ImGui.Spacing();
        int exportCompression = Plugin.Configuration.ExportCompression;
        string[] compressionOptions = { Translator.LocalizeUI("Speed (Uncompressed)"), Translator.LocalizeUI("Sync Friendly (Mode 6 BC7)") };
        if (ImGui.Combo(Translator.LocalizeUI("Export Compression"), ref exportCompression, compressionOptions, compressionOptions.Length))
        {
            Plugin.Configuration.ExportCompression = exportCompression;
            Plugin.Configuration.Save();
            
            var ddtForRebuild = Plugin.DragAndDropTextures;
            if (ddtForRebuild != null && ddtForRebuild.TextureHistory != null)
            {
                ddtForRebuild.RebuildAllCategories();
            }
        }
        ImGui.TextWrapped(Translator.LocalizeUI("Selects the texture compression method used for exports. Speed is faster to generate but results in larger file sizes. BC7 offers the lowest file sizes for Dawntrail, but is performance heavy exporting."));
        
        ImGui.Spacing();
        var options = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes.Select(x => x.Name).ToArray();
        var locOptions = Translator.LocalizeTextArray(options);
        int selectedIndex = Math.Max(0, Array.IndexOf(options, Plugin.Configuration.DefaultUnderlaySkinType));
        if (ImGui.Combo(Translator.LocalizeUI("Default Underlay Skin Type"), ref selectedIndex, locOptions, locOptions.Length))
        {
            Plugin.Configuration.DefaultUnderlaySkinType = options[selectedIndex];
            Plugin.Configuration.Save();
        }
        ImGui.TextWrapped(Translator.LocalizeUI("Selects the base skin underlay type when a custom transparent tattoo is dropped. If the character's base body doesn't support the specific skin variant, it will fall back to its own default."));
        
        ImGui.Spacing();
        bool usePriorityMod = Plugin.Configuration.UsePriorityBodyMod;
        if (ImGui.Checkbox(Translator.LocalizeUI("Use Textures From Priority Body Mod"), ref usePriorityMod))
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
        ImGui.TextWrapped(Translator.LocalizeUI("When enabled, the compiler will scan your Penumbra modlist and automatically inherit the body texture of your highest priority active skin mod as the underlay for transparent overlays."));
        
        if (usePriorityMod)
        {
            ImGui.Spacing();
            ImGui.Text(Translator.LocalizeUI("Active Body Overrides:"));
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
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Translator.LocalizeUI("None detected (or scan pending)"));
            }
            if (ImGui.Button(Translator.LocalizeUI("Scan For Overrides")))
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

    private void OpenImportDialog()
    {
        _fileDialogManager.OpenFileDialog(
            Translator.LocalizeUI("Select textures to apply to your character"), 
            "Texture Files{.png,.dds,.tex,.bmp,.psd}", 
            (b, files) => 
            {
                if (b && files != null && files.Count > 0)
                {
                    var localPlayer = Plugin.SafeGameObjectManager.LocalPlayer;
                    if (localPlayer != null && localPlayer is Dalamud.Game.ClientState.Objects.Types.ICharacter chara)
                    {
                        Plugin.DragAndDropTextures?.InjectFilesAndRebuild(
                            files, 
                            new KeyValuePair<string, Dalamud.Game.ClientState.Objects.Types.ICharacter>(localPlayer.Name.TextValue, chara), 
                            PenumbraAndGlamourerHelpers.BodyDragPart.Body);
                    }
                }
            }, 
            0, null, true);
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
                ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Translator.LocalizeUI("No active textures dropped yet."));
                ImGui.Spacing();
                if (ImGui.Button(Translator.LocalizeUI("Import Textures (File Dialog)")))
                {
                    OpenImportDialog();
                }
                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Open Texture Painter")))
                {
                    Plugin.OpenPaintWindow();
                }
                return;
            }

            if (ImGui.Button(Translator.LocalizeUI("Import Textures (File Dialog)")))
            {
                OpenImportDialog();
            }
            ImGui.Spacing();

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
                
                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), Translator.LocalizeUI("Active Layers for:") + $" {key}");
                ImGui.Separator();
                
                ImGui.BeginDisabled(!ImGui.IsKeyDown(ImGuiKey.ModShift));
                if (ImGui.Button(Translator.LocalizeUI("Clear All") + "##" + key))
                {
                    list.Clear();
                    ddt.RebuildCategory(key, false);
                    Plugin.Configuration.Save();
                    // Keep index bounded if list clears and we want to prevent out of bounds next frame
                }
                ImGui.EndDisabled();
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                {
                    ImGui.SetTooltip(Translator.LocalizeUI("Hold SHIFT to Clear All"));
                }
                
                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Export to PSD") + "##" + key))
                {
                    ExportCategoryToPsd(key, list);
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
                    
                    if (ImGui.Button(Translator.LocalizeUI("Up") + "##" + key + i) && i > 0)
                    {
                        var temp = list[i - 1];
                        list[i - 1] = list[i];
                        list[i] = temp;
                        changed = true;
                    }
                    
                    ImGui.SameLine();
                    if (ImGui.Button(Translator.LocalizeUI("Down") + "##" + key + i) && i < list.Count - 1)
                    {
                        var temp = list[i + 1];
                        list[i + 1] = list[i];
                        list[i] = temp;
                        changed = true;
                    }

                    ImGui.SameLine();
                    ImGui.BeginDisabled(!ImGui.IsKeyDown(ImGuiKey.ModShift));
                    if (ImGui.Button(Translator.LocalizeUI("Remove") + "##" + key + i))
                    {
                        list.RemoveAt(i);
                        i--;
                        changed = true;
                    }
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    {
                        ImGui.SetTooltip(Translator.LocalizeUI("Hold SHIFT to Remove"));
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
                        if (ImGui.Button(Translator.LocalizeUI("Edit") + "##" + key + i))
                        {
                            Plugin.OpenPaintWindow(path);
                        }
                        if (!canEdit)
                        {
                            ImGui.EndDisabled();
                            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                            {
                                ImGui.SetTooltip(Translator.LocalizeUI("This layer requires a body mod that is not currently available in your Penumbra directory."));
                            }
                        }
                    }
                }

                if (ImGui.Button(Translator.LocalizeUI("Add New Layer (Open Painter)##") + key))
                {
                    Plugin.OpenPaintWindow();
                }

                if (changed)
                {
                    ddt.RebuildCategory(key, false);
                    Plugin.Configuration.Save();
                }
            }
            ImGui.EndChild();
        }
    }

    private void ExportCategoryToPsd(string key, System.Collections.Generic.List<string> files)
    {
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                string exportFolder = System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Exports");
                if (!System.IO.Directory.Exists(exportFolder)) System.IO.Directory.CreateDirectory(exportFolder);
                
                string safeName = string.Join("_", key.Split(System.IO.Path.GetInvalidFileNameChars()));
                string psdPath = System.IO.Path.Combine(exportFolder, $"{safeName}.psd");
                
                int targetBody = -1;
                if (key.EndsWith("_body", StringComparison.OrdinalIgnoreCase))
                {
                    var character = Plugin.SafeGameObjectManager.LocalPlayer;
                    if (character != null)
                    {
                        var stateBase64Result = global::PenumbraAndGlamourerIpcWrapper.Instance.GetStateBase64.Invoke(character.ObjectIndex);
                        var customization = PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.CharacterCustomization.ReadCustomization(stateBase64Result.Item2);
                        int ffxivGender = customization.Customize.Gender.Value;
                        Guid collectionId = global::PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;
                        targetBody = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(collectionId, ffxivGender, out string _, Plugin);
                    }
                }

                using var collection = new ImageMagick.MagickImageCollection();
                bool added = false;
                
                for (int i = 0; i < files.Count; i++)
                {
                    string f = files[i];
                    if (System.IO.File.Exists(f))
                    {
                        if (targetBody != -1)
                        {
                            int sourceBody = -1;
                            string fileName = System.IO.Path.GetFileNameWithoutExtension(f).ToLower();
                            if (fileName.Contains("bibo") || fileName.Contains("b+")) sourceBody = 1;
                            else if (fileName.Contains("gen3") || System.Text.RegularExpressions.Regex.IsMatch(fileName, @"(^|[^a-z])eve([^a-z]|$)") || fileName.Contains("exqb") || fileName.Contains("pythia") || fileName.Contains("gaia")) sourceBody = 2;
                            else
                            {
                                switch (FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation.FemaleBodyUVClassifier(f))
                                {
                                    case FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation.BodyUVType.Bibo: sourceBody = 1; break;
                                    case FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation.BodyUVType.Gen3: sourceBody = 2; break;
                                    case FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation.BodyUVType.Gen2: sourceBody = 0; break;
                                }
                            }
                            if (sourceBody == -1) sourceBody = 2; // Default to Gen3
                            
                            if (sourceBody != targetBody)
                            {
                                string convertedPath = System.IO.Path.Combine(exportFolder, System.IO.Path.GetFileNameWithoutExtension(f) + "_converted.png");
                                if (sourceBody == 1 && targetBody == 2)
                                {
                                    if (!System.IO.File.Exists(convertedPath)) FFXIVLooseTextureCompiler.FastUVTransfer.BiboToGen3(f, convertedPath);
                                    f = convertedPath;
                                }
                                else if (sourceBody == 2 && targetBody == 1)
                                {
                                    if (!System.IO.File.Exists(convertedPath)) FFXIVLooseTextureCompiler.FastUVTransfer.Gen3ToBibo(f, convertedPath);
                                    f = convertedPath;
                                }
                            }
                        }

                        if (System.IO.File.Exists(f))
                        {
                            var img = new ImageMagick.MagickImage(f);
                            if (!added)
                            {
                                var composite = new ImageMagick.MagickImage(ImageMagick.MagickColors.Transparent, img.Width, img.Height);
                                collection.Add(composite);
                            }
                            img.Label = $"Layer {i + 1} - " + System.IO.Path.GetFileNameWithoutExtension(f);
                            collection.Add(img);
                            added = true;
                        }
                    }
                }
                
                if (added)
                {
                    var composite = collection[0];
                    for (int i = 1; i < collection.Count; i++)
                    {
                        composite.Composite(collection[i], ImageMagick.CompositeOperator.Over);
                    }

                    collection.Write(psdPath, ImageMagick.MagickFormat.Psd);
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                    {
                        FileName = exportFolder,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
                else
                {
                    Plugin.PluginLog.Warning($"No valid files found to export for category {key}");
                }
            }
            catch (Exception ex)
            {
                Plugin.PluginLog.Error(ex, $"Failed to export category {key} to PSD");
            }
        });
    }

    private void DrawLayerHistory()
    {
        ImGui.Spacing();
        var recentLayers = Plugin.Configuration.RecentLayers;
        if (recentLayers == null || recentLayers.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "No previously dropped layers found.");
            return;
        }

        ImGui.Text(Translator.LocalizeUI("History of all dropped textures (Newest first):"));
        ImGui.Separator();

        ImGui.BeginChild("LayerHistoryList", new Vector2(0, 0), true);
        for (int i = 0; i < recentLayers.Count; i++)
        {
            string path = recentLayers[i];
            var tex = GetPreviewTexture(path);
            var wrap = tex?.GetWrapOrDefault();
            
            float originalY = ImGui.GetCursorPosY();
            
            if (wrap != null)
            {
                ImGui.Image(wrap.Handle, new Vector2(40, 40));
                ImGui.SameLine();
            }
            
            // Align all text and buttons to the middle of the 40px image
            if (wrap != null) ImGui.SetCursorPosY(originalY + 10);
            
            ImGui.Text(Path.GetFileName(path));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(path);
            }
            
            ImGui.SameLine(ImGui.GetWindowWidth() - 200);
            
            if (ImGui.Button(Translator.LocalizeUI("Apply") + $"##history_{i}"))
            {
                var localPlayer = Plugin.SafeGameObjectManager.LocalPlayer;
                if (localPlayer != null && localPlayer is Dalamud.Game.ClientState.Objects.Types.ICharacter character)
                {
                    Plugin.DragAndDropTextures.InjectFilesAndRebuild(new System.Collections.Generic.List<string> { path }, new System.Collections.Generic.KeyValuePair<string, Dalamud.Game.ClientState.Objects.Types.ICharacter>(localPlayer.Name.TextValue, character), PenumbraAndGlamourerHelpers.BodyDragPart.Unknown);
                }
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Translator.LocalizeUI("Directly apply this layer to your own character."));
            }

            ImGui.SameLine();
            if (ImGui.Button(Translator.LocalizeUI("Copy Path") + $"##history_{i}"))
            {
                ImGui.SetClipboardText(path);
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Translator.LocalizeUI("Copy the full file path to your clipboard to paste into an Active Layer input box."));
            }
            
            ImGui.SameLine();
            if (ImGui.Button($"X##remove_history_{i}"))
            {
                recentLayers.RemoveAt(i);
                Plugin.Configuration.Save();
                i--;
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Translator.LocalizeUI("Remove from history"));
            }
            
            ImGui.SetCursorPosY(originalY + 45); // move to next line accounting for image height
        }
        ImGui.EndChild();
    }

    private int _selectedContextualLayerIndex = 0;

    private string _cachedErrorLog = null;
    private string _cachedBenchmarkLog = null;
    private DateTime _lastErrorLogCheck = DateTime.MinValue;

    private void DrawDiagnostics()
    {
        ImGui.Spacing();
        ImGui.Text(Translator.LocalizeUI("GPU Fallback Diagnostic Log:"));
        
        string logPath = Path.Combine(Path.GetTempPath(), "GPU_Fallback_Error.txt");
        string benchPath = Path.Combine(Path.GetTempPath(), "GPU_Benchmark.txt");
        
        if ((DateTime.Now - _lastErrorLogCheck).TotalSeconds > 2)
        {
            if (File.Exists(logPath))
            {
                try { _cachedErrorLog = File.ReadAllText(logPath); } catch { }
            }
            else
            {
                _cachedErrorLog = Translator.LocalizeUI("No GPU fallback errors detected. (GPU acceleration is working fine!)");
            }
            
            if (File.Exists(benchPath))
            {
                try { _cachedBenchmarkLog = File.ReadAllText(benchPath); } catch { }
            }
            else
            {
                _cachedBenchmarkLog = Translator.LocalizeUI("No benchmark data recorded yet.");
            }
            
            _lastErrorLogCheck = DateTime.Now;
        }

        if (_cachedErrorLog != null)
        {
            ImGui.BeginChild("ErrorLogChild", new Vector2(-1, 100), true);
            ImGui.TextWrapped(_cachedErrorLog);
            ImGui.EndChild();
        }

        if (ImGui.Button(Translator.LocalizeUI("Clear Error Log")))
        {
            if (File.Exists(logPath)) { try { File.Delete(logPath); } catch { } }
            _cachedErrorLog = Translator.LocalizeUI("No GPU fallback errors detected. (GPU acceleration is working fine!)");
        }
        
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text(Translator.LocalizeUI("MergeImageLayers Performance Benchmarks:"));
        
        if (_cachedBenchmarkLog != null)
        {
            ImGui.BeginChild("BenchmarkLogChild", new Vector2(-1, ImGui.GetContentRegionAvail().Y - 40), true);
            ImGui.TextUnformatted(_cachedBenchmarkLog);
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                ImGui.SetScrollHereY(1.0f);
            ImGui.EndChild();
        }
        
        if (ImGui.Button(Translator.LocalizeUI("Clear Benchmark Log")))
        {
            if (File.Exists(benchPath)) { try { File.Delete(benchPath); } catch { } }
            _cachedBenchmarkLog = Translator.LocalizeUI("No benchmark data recorded yet.");
        }
    }

    private void DrawContextualLayers()
    {
        ImGui.Spacing();
        if (ImGui.Button(Translator.LocalizeUI("Add Contextual Layer")))
        {
            Plugin.ContextualLayerManager.CreateNewLayer();
            _selectedContextualLayerIndex = Plugin.ContextualLayerManager.ContextualLayers.Count - 1;
        }
        
        ImGui.SameLine();
        if (ImGui.Button(Translator.LocalizeUI("Open Saved Overlays Folder")))
        {
            string importFolder = System.IO.Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "SavedOverlays");
            if (!System.IO.Directory.Exists(importFolder)) System.IO.Directory.CreateDirectory(importFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
            {
                FileName = importFolder,
                UseShellExecute = true,
                Verb = "open"
            });
        }
        
        ImGui.SameLine();
        if (ImGui.Button(Translator.LocalizeUI("Scan for Saved Overlays")))
        {
            Plugin.ContextualLayerManager.ImportLayersFromSavedOverlaysFolder();
            if (Plugin.ContextualLayerManager.ContextualLayers.Count > 0)
                _selectedContextualLayerIndex = Plugin.ContextualLayerManager.ContextualLayers.Count - 1;
        }

        ImGui.Spacing();

        if (Plugin.ContextualLayerManager.ContextualLayers.Count == 0)
        {
            ImGui.Text(Translator.LocalizeUI("No contextual layers configured."));
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
            ImGui.SetTooltip(Translator.LocalizeUI("You can drop .clmp files directly here to import them!"));
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
            if (ImGui.InputText(Translator.LocalizeUI("Name") + "##ContextName", ref name, 255))
            {
                layer.Name = name;
                changed = true;
            }

            int triggerType = (int)layer.Trigger;
            string[] triggerNames = Enum.GetNames(typeof(TriggerType)).Select(n => n.Replace("_", " ")).ToArray();
            var locTriggerNames = Translator.LocalizeTextArray(triggerNames);
            if (ImGui.Combo(Translator.LocalizeUI("Trigger Type") + "##ContextTrigger", ref triggerType, locTriggerNames, locTriggerNames.Length))
            {
                layer.Trigger = (TriggerType)triggerType;
                changed = true;
            }

            int clearType = (int)layer.ClearTrigger;
            string[] clearNames = Enum.GetNames(typeof(ClearCondition)).Select(n => n.Replace("_", " ")).ToArray();
            var locClearNames = Translator.LocalizeTextArray(clearNames);
            if (ImGui.Combo(Translator.LocalizeUI("Clear Condition") + "##ContextClear", ref clearType, locClearNames, locClearNames.Length))
            {
                layer.ClearTrigger = (ClearCondition)clearType;
                changed = true;
            }

            if (layer.Trigger == TriggerType.Emote)
            {
                int emoteId = layer.EmoteId;
                var currentEmote = _emotes.FirstOrDefault(x => x.RowId == emoteId);
                string currentEmoteName = currentEmote.RowId != 0 ? currentEmote.Name.ExtractText() : $"ID: {emoteId}";

                if (ImGui.BeginCombo(Translator.LocalizeUI("Emote") + "##ContextEmote", currentEmoteName))
                {
                    ImGui.InputText(Translator.LocalizeUI("Search") + "##EmoteSearch", ref _emoteSearchFilter, 255);
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
                if (ImGui.SliderInt(Translator.LocalizeUI("HP Threshold %") + "##ContextHP", ref hpThresh, 1, 99))
                {
                    layer.HPThresholdPercentage = hpThresh;
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Audio_Path_Load)
            {
                string audioPath = layer.AudioTriggerPath;
                if (ImGui.InputText(Translator.LocalizeUI("Audio Path / Name (.scd)") + "##ContextAudio", ref audioPath, 255))
                {
                    layer.AudioTriggerPath = audioPath;
                    changed = true;
                }
                
                int reqSounds = layer.RequiredSoundsPerStack;
                if (ImGui.InputInt(Translator.LocalizeUI("Required Sounds per Stack") + "##ContextSounds", ref reqSounds))
                {
                    layer.RequiredSoundsPerStack = Math.Max(1, reqSounds);
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Chat_Message)
            {
                string chatRegex = layer.ChatRegex;
                if (ImGui.InputText(Translator.LocalizeUI("Chat Regex Pattern") + "##ContextChat", ref chatRegex, 255))
                {
                    layer.ChatRegex = chatRegex;
                    changed = true;
                }
                
                bool emoteOnly = layer.ChatFilterCustomEmotesOnly;
                if (ImGui.Checkbox(Translator.LocalizeUI("Only trigger on Emotes (/em or standard)") + "##ContextChatEmote", ref emoteOnly))
                {
                    layer.ChatFilterCustomEmotesOnly = emoteOnly;
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Enemy_Nearby)
            {
                string enemyName = layer.TargetEnemyName;
                if (ImGui.InputText(Translator.LocalizeUI("Target Enemy Name") + "##ContextEnemy", ref enemyName, 255))
                {
                    layer.TargetEnemyName = enemyName;
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Territory_ID)
            {
                int territoryId = (int)layer.TargetTerritoryId;
                if (ImGui.InputInt(Translator.LocalizeUI("Territory ID") + "##ContextTerritory", ref territoryId))
                {
                    layer.TargetTerritoryId = (uint)Math.Max(0, territoryId);
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Weather_ID)
            {
                int weatherId = (int)layer.TargetWeatherId;
                if (ImGui.InputInt(Translator.LocalizeUI("Weather ID") + "##ContextWeather", ref weatherId))
                {
                    layer.TargetWeatherId = (uint)Math.Max(0, weatherId);
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.In_Game_Time)
            {
                int startHour = layer.TargetTimeStartHour;
                int endHour = layer.TargetTimeEndHour;
                
                if (ImGui.SliderInt(Translator.LocalizeUI("Start Hour (ET)") + "##ContextTimeStart", ref startHour, 0, 23))
                {
                    layer.TargetTimeStartHour = startHour;
                    changed = true;
                }
                if (ImGui.SliderInt(Translator.LocalizeUI("End Hour (ET)") + "##ContextTimeEnd", ref endHour, 0, 23))
                {
                    layer.TargetTimeEndHour = endHour;
                    changed = true;
                }
            }
            else if (layer.Trigger == TriggerType.Kill_Count || layer.Trigger == TriggerType.Action_Used)
            {
                int reqKills = layer.RequiredKillsPerStack;
                string stackLabel = Translator.LocalizeUI(layer.Trigger == TriggerType.Kill_Count ? "Required Kills per Stack" : "Required Actions per Stack");
                if (ImGui.InputInt(stackLabel + "##ContextKills", ref reqKills))
                {
                    layer.RequiredKillsPerStack = Math.Max(1, reqKills);
                    changed = true;
                }
                
                int decay = layer.DecayIntervalSeconds;
                if (ImGui.InputInt(Translator.LocalizeUI("Decay Interval (Seconds)") + "##ContextDecay", ref decay))
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
                if (ImGui.InputInt(Translator.LocalizeUI("Duration (Seconds)") + "##ContextDur", ref duration))
                {
                    layer.DurationSeconds = Math.Max(1, duration);
                    changed = true;
                }
            }

            string[] bodyParts = { "body", "face", "eyes", "eyebrows" };
            var locBodyParts = Translator.LocalizeTextArray(bodyParts);
            int partIndex = Math.Max(0, Array.IndexOf(bodyParts, layer.TargetBodyPart));
            if (ImGui.Combo(Translator.LocalizeUI("Target Body Part") + "##ContextPart", ref partIndex, locBodyParts, locBodyParts.Length))
            {
                layer.TargetBodyPart = bodyParts[partIndex];
                changed = true;
            }
            
            bool decalMode = layer.ProceduralDecalMode;
            if (ImGui.Checkbox(Translator.LocalizeUI("Procedural Decal Mode") + "##ContextDecal", ref decalMode))
            {
                layer.ProceduralDecalMode = decalMode;
                changed = true;
            }
            ImGui.TextWrapped(Translator.LocalizeUI("When enabled, the textures in this folder will be treated as decals (e.g. blood/dirt splatters) and procedurally stamped onto random locations of the player's 3D model instead of overriding the entire body."));

            ImGui.Spacing();
            if (ImGui.Button(Translator.LocalizeUI("Open Folder") + "##ContextFolder"))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = layer.DirectoryPath,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }

            ImGui.SameLine();

            if (ImGui.Button(Translator.LocalizeUI("Export Layer") + "##ContextExport"))
            {
                Plugin.ContextualLayerManager.ExportLayer(layer);
            }

            ImGui.SameLine();

            if (ImGui.Button(Translator.LocalizeUI("Remove Layer") + "##ContextRemove"))
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
