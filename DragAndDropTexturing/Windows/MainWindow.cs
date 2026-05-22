using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DragAndDropTexturing.Equipment;
using DragAndDropTexturing.LanguageHelpers;
using DragAndDropTexturing.VideoPlayback;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using static Penumbra.GameData.Files.ShpkFile;
using RoleplayingVoice;

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
        if (sheet != null)
        {
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
            if (ImGui.BeginTabItem(Translator.LocalizeUI("Penumbra Found Mods")))
            {
                DrawPenumbraFoundMods();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Translator.LocalizeUI("Animated Layers")))
            {
                DrawAnimatedLayers();
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
    private string _pendingTintRebuildCategory = null;

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
            else if (_cachedBodyType == 5) bodyString = "Otopop";

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

        ImGui.Spacing();
        int fallbackBodyType = Plugin.Configuration.FallbackBodyType;
        string[] fallbackOptions = { Translator.LocalizeUI("Auto-Detect (Default)"), Translator.LocalizeUI("Vanilla"), "Bibo+", "Gen3 / Eve / Pythia", "TBSE", "Otopop" };
        if (ImGui.Combo(Translator.LocalizeUI("Manual Body Type Fallback"), ref fallbackBodyType, fallbackOptions, fallbackOptions.Length))
        {
            Plugin.Configuration.FallbackBodyType = fallbackBodyType;
            Plugin.Configuration.Save();
        }
        ImGui.TextWrapped(Translator.LocalizeUI("Forces a specific body type to be used when automatic detection via Penumbra fails (e.g., if Penumbra connection issues occur)."));

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
        int exportQuality = Plugin.Configuration.ExportCompression;
        string[] qualityOptions = { Translator.LocalizeUI("Speed (Uncompressed)"), Translator.LocalizeUI("High Quality (BC7 / Sync Friendly)") };
        if (ImGui.Combo(Translator.LocalizeUI("Export Quality"), ref exportQuality, qualityOptions, qualityOptions.Length))
        {
            Plugin.Configuration.ExportCompression = exportQuality;
            Plugin.Configuration.Save();

            var ddtForRebuild = Plugin.DragAndDropTextures;
            if (ddtForRebuild != null && ddtForRebuild.TextureHistory != null)
            {
                ddtForRebuild.RebuildAllCategories();
            }
        }
        ImGui.TextWrapped(Translator.LocalizeUI("Selects the texture quality used for exports. Speed is faster to generate but results in larger file sizes. High Quality (BC7) offers the lowest file sizes for Dawntrail, but is performance heavy."));

        ImGui.Spacing();
        float exportScale = Plugin.Configuration.ExportScale;
        int scaleIndex = exportScale == 1.0f ? 0 : exportScale == 0.5f ? 1 : 2;
        string[] scaleOptions = { Translator.LocalizeUI("100% (Native)"), Translator.LocalizeUI("50% (Half Resolution)"), Translator.LocalizeUI("25% (Quarter Resolution)") };
        if (ImGui.Combo(Translator.LocalizeUI("Export Resolution"), ref scaleIndex, scaleOptions, scaleOptions.Length))
        {
            Plugin.Configuration.ExportScale = scaleIndex == 0 ? 1.0f : scaleIndex == 1 ? 0.5f : 0.25f;
            Plugin.Configuration.Save();

            var ddtForRebuild = Plugin.DragAndDropTextures;
            if (ddtForRebuild != null && ddtForRebuild.TextureHistory != null)
            {
                ddtForRebuild.RebuildAllCategories();
            }
        }
        ImGui.TextWrapped(Translator.LocalizeUI("Downscales exported textures to save memory and file size at the cost of visual quality."));

        ImGui.Spacing();
        bool autoDistanceExportQuality = Plugin.Configuration.AutoDistanceExportQuality;
        if (ImGui.Checkbox(Translator.LocalizeUI("Auto Distance Export Quality (Experimental)"), ref autoDistanceExportQuality))
        {
            Plugin.Configuration.AutoDistanceExportQuality = autoDistanceExportQuality;
            Plugin.Configuration.Save();
        }
        ImGui.TextWrapped(Translator.LocalizeUI("Automatically scales the export resolution based on how close the camera is to your character during the drop."));

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

    private void DrawWornGearQuickEdit()
    {
        var ddt = Plugin.DragAndDropTextures;
        var localPlayer = Plugin.SafeGameObjectManager.LocalPlayer;
        if (ddt == null || localPlayer == null) return;

        ImGui.TextWrapped(Translator.LocalizeUI("Pull texture paths from gear your character is wearing. Each slot becomes an editable layer like body/face."));
        ImGui.Spacing();

        if (ImGui.Button(Translator.LocalizeUI("Scan Worn Gear")))
        {
            ddt.RefreshWornGearCache();
        }

        ImGui.SameLine();
        if (ImGui.Button(Translator.LocalizeUI("Import All Slots")))
        {
            ddt.RefreshWornGearCache();
            foreach (var piece in ddt.CachedWornGear)
                ddt.ImportWornGearSlot(piece);
        }

        if (ddt.CachedWornGear == null || ddt.CachedWornGear.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Translator.LocalizeUI("No gear textures resolved yet. Click Scan while wearing items (not Emperor's New)."));
            ImGui.Spacing();
            return;
        }

        ImGui.Spacing();
        if (ImGui.BeginTable("WornGearTable", 3, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(Translator.LocalizeUI("Slot"), ImGuiTableColumnFlags.WidthFixed, 120);
            ImGui.TableSetupColumn(Translator.LocalizeUI("Item"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Translator.LocalizeUI("Actions"), ImGuiTableColumnFlags.WidthFixed, 200);
            ImGui.TableHeadersRow();

            foreach (var piece in ddt.CachedWornGear)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(piece.SlotKey);
                ImGui.TableNextColumn();
                ImGui.TextWrapped(piece.DisplayName);
                if (!string.IsNullOrEmpty(piece.InternalBasePath) && ImGui.IsItemHovered())
                    ImGui.SetTooltip(piece.InternalBasePath);

                ImGui.TableNextColumn();
                string btnIdSuffix = piece.SlotKey + (string.IsNullOrEmpty(piece.MaterialName) ? "" : "_" + piece.MaterialName);
                string charName = localPlayer.Name.TextValue;
                string layerKey = charName + "_gear_" + piece.SlotKey + (string.IsNullOrEmpty(piece.MaterialName) ? "" : "_" + piece.MaterialName);
                bool hasLayer = ddt.TextureHistory != null && ddt.TextureHistory.ContainsKey(layerKey) && ddt.TextureHistory[layerKey].Count > 0;
                if (!hasLayer)
                {
                    if (ImGui.Button(Translator.LocalizeUI("Import") + "##wg_" + btnIdSuffix))
                        ddt.ImportWornGearSlot(piece);
                }
                if (hasLayer)
                {
                    ImGui.SameLine();
                    ImGui.LabelText("##importedLabel","  Imported already!");
                    //string editPath = ddt.TextureHistory[layerKey].LastOrDefault(f => !string.IsNullOrEmpty(f) && File.Exists(f));
                    //if (!string.IsNullOrEmpty(editPath) && ImGui.Button(Translator.LocalizeUI("Edit") + "##wge_" + btnIdSuffix))
                    //    Plugin.OpenPaintWindow(editPath);
                }
            }

            ImGui.EndTable();
        }

        ImGui.Spacing();
    }

    private void DrawActiveLayers()
    {
        ImGui.Spacing();
        var ddt = Plugin.DragAndDropTextures;
        if (ddt != null && ddt.TextureHistory != null)
        {
            if (ImGui.BeginTabBar("ActiveLayersSubTabs"))
            {
                if (ImGui.BeginTabItem(Translator.LocalizeUI("Presets & Layers")))
                {
                    ImGui.Spacing();
                    DrawCombinedLayersTab(ddt);
                    ImGui.EndTabItem();
                }

                if (ImGui.BeginTabItem(Translator.LocalizeUI("Worn Gear")))
                {
                    ImGui.Spacing();
                    DrawWornGearQuickEdit();
                    ImGui.EndTabItem();
                }

                ImGui.EndTabBar();
            }
        }
    }

    private void DrawPenumbraFoundMods()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(Translator.LocalizeUI("This tab shows advanced textures (overlays) discovered from active Penumbra mods that include raw .png file options. You can customize color tinting for these layers here, but they cannot be removed since they are controlled by Penumbra."));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var ddt = Plugin.DragAndDropTextures;
        if (ddt == null) return;

        var overlays = DragAndDropTexturing.Overlays.AdvancedOverlayParser.ActiveOverlays;
        if (overlays == null || overlays.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Translator.LocalizeUI("No Penumbra mod overlays currently active/detected."));
            return;
        }

        bool changed = false;
        string rebuildCategory = null;

        if (ImGui.BeginTable("PenumbraFoundModsTable", 4, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(Translator.LocalizeUI("Part"), ImGuiTableColumnFlags.WidthFixed, 100);
            ImGui.TableSetupColumn(Translator.LocalizeUI("UV Type"), ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableSetupColumn(Translator.LocalizeUI("Texture Path / Option Name"), ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn(Translator.LocalizeUI("Tint"), ImGuiTableColumnFlags.WidthFixed, 80);
            ImGui.TableHeadersRow();

            for (int i = 0; i < overlays.Count; i++)
            {
                var overlay = overlays[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(char.ToUpper(overlay.TargetBodyPart[0]) + overlay.TargetBodyPart.Substring(1));

                ImGui.TableNextColumn();
                ImGui.Text(overlay.UVType);

                ImGui.TableNextColumn();
                // Show preview and filename
                string fileName = string.IsNullOrEmpty(overlay.DiffusePath) ? "" : Path.GetFileName(overlay.DiffusePath);
                
                var tex = GetPreviewTexture(overlay.DiffusePath);
                var wrap = tex?.GetWrapOrDefault();
                if (wrap != null)
                {
                    ImGui.Image(wrap.Handle, new Vector2(30, 30));
                    ImGui.SameLine();
                    ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 5);
                }
                
                ImGui.Text(fileName);
                if (!string.IsNullOrEmpty(overlay.DiffusePath) && ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(overlay.DiffusePath);
                }

                ImGui.TableNextColumn();
                // Tint control
                string overlayKey = !string.IsNullOrEmpty(overlay.DiffusePath) ? overlay.DiffusePath : (!string.IsNullOrEmpty(overlay.NormalPath) ? overlay.NormalPath : overlay.MaskPath);
                if (!string.IsNullOrEmpty(overlayKey))
                {
                    Vector4 col = Vector4.One;
                    if (Plugin.Configuration.PenumbraOverlayTints.TryGetValue(overlayKey, out var savedCol))
                    {
                        col = savedCol;
                    }

                    ImGui.SetNextItemWidth(60);
                    if (ImGui.ColorEdit4($"##overlaytint_{i}", ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                    {
                        col.W = 1.0f;
                        Plugin.Configuration.PenumbraOverlayTints[overlayKey] = col;
                        Plugin.Configuration.Save();
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        _pendingTintRebuildCategory = overlay.TargetBodyPart;
                    }
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "N/A");
                }
            }

            ImGui.EndTable();
        }

        if (_pendingTintRebuildCategory != null && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootWindow))
        {
            changed = true;
            rebuildCategory = _pendingTintRebuildCategory;
            _pendingTintRebuildCategory = null;
        }

        if (changed && !string.IsNullOrEmpty(rebuildCategory))
        {
            var localPlayer = Plugin.SafeGameObjectManager.LocalPlayer;
            if (localPlayer != null)
            {
                string categoryKey = localPlayer.Name.TextValue + "_" + rebuildCategory.ToLower();
                ddt.RebuildCategory(categoryKey, false);
            }
        }
    }

    private int _selectedPresetIndex = -1;
    private System.Collections.Generic.Dictionary<uint, string> _jobNames = null;
    private string[] _jobNamesArray = null;
    private uint[] _jobIdsArray = null;

    private void InitJobNames()
    {
        if (_jobNames != null) return;
        _jobNames = new System.Collections.Generic.Dictionary<uint, string>();
        _jobNames[0] = "None";
        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
        if (sheet != null)
        {
            foreach (var job in sheet)
            {
                if (job.RowId == 0) continue;
                string name = job.Abbreviation.ToString();
                if (string.IsNullOrEmpty(name)) name = job.Name.ToString();
                if (string.IsNullOrEmpty(name)) name = $"Job {job.RowId}";
                _jobNames[job.RowId] = name;
            }
        }
        
        var list = _jobNames.ToList();
        list.Sort((a, b) => {
            if (a.Key == 0) return -1;
            if (b.Key == 0) return 1;
            return a.Value.CompareTo(b.Value);
        });
        _jobIdsArray = list.Select(kv => kv.Key).ToArray();
        _jobNamesArray = list.Select(kv => kv.Value).ToArray();
    }

    private void DrawCombinedLayersTab(DragAndDropTextureWindow ddt)
    {
        InitJobNames();
        var presets = Plugin.Configuration.ActiveLayerPresets;
        if (presets == null)
        {
            Plugin.Configuration.ActiveLayerPresets = new();
            presets = Plugin.Configuration.ActiveLayerPresets;
        }

        ImGui.BeginChild("PresetsListColumn", new Vector2(200, 0), true);
        if (ImGui.Selectable(Translator.LocalizeUI("Active Character State"), _selectedPresetIndex == -1))
        {
            _selectedPresetIndex = -1;
            _selectedActiveLayerIndex = 0;
        }

        ImGui.Separator();

        for (int i = 0; i < presets.Count; i++)
        {
            if (ImGui.Selectable($"{presets[i].Name}##Preset_{i}", _selectedPresetIndex == i))
            {
                _selectedPresetIndex = i;
                _selectedActiveLayerIndex = 0;
            }
        }

        ImGui.Spacing();
        if (ImGui.Button(Translator.LocalizeUI("Save State As Preset")))
        {
            var preset = new ActiveLayerPreset
            {
                Name = "New Preset " + (presets.Count + 1)
            };
            foreach (var kvp in ddt.TextureHistory)
                preset.TextureHistory[kvp.Key] = new System.Collections.Generic.List<string>(kvp.Value);
            if (ddt.TextureHistoryTints != null)
            {
                foreach (var kvp in ddt.TextureHistoryTints)
                    preset.TextureHistoryTints[kvp.Key] = new System.Collections.Generic.List<System.Numerics.Vector4>(kvp.Value);
            }
            presets.Add(preset);
            Plugin.Configuration.Save();
            _selectedPresetIndex = presets.Count - 1;
        }

        ImGui.EndChild();
        ImGui.SameLine();

        ImGui.BeginChild("PresetDetailsColumn", new Vector2(0, 0), false);

        var targetHistory = _selectedPresetIndex == -1 ? ddt.TextureHistory : presets[_selectedPresetIndex].TextureHistory;
        var targetTints = _selectedPresetIndex == -1 ? ddt.TextureHistoryTints : presets[_selectedPresetIndex].TextureHistoryTints;

        if (_selectedPresetIndex != -1)
        {
            var preset = presets[_selectedPresetIndex];
            
            string pName = preset.Name;
            if (ImGui.InputText("Preset Name##PresetName", ref pName, 128))
            {
                preset.Name = pName;
                Plugin.Configuration.Save();
            }

            int currentJobIndex = Array.IndexOf(_jobIdsArray, preset.LinkedJobId);
            if (currentJobIndex < 0) currentJobIndex = 0;
            
            ImGui.SetNextItemWidth(150);
            if (ImGui.Combo("Linked Job", ref currentJobIndex, _jobNamesArray, _jobNamesArray.Length))
            {
                preset.LinkedJobId = _jobIdsArray[currentJobIndex];
                Plugin.Configuration.Save();
            }
            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(Translator.LocalizeUI("If a Job ID is set, this preset will automatically load when you switch to that job."));
            }

            ImGui.Spacing();

            if (ImGui.Button(Translator.LocalizeUI("Load Preset to Character")))
            {
                ApplyPreset(preset);
                _selectedPresetIndex = -1; // Switch back to active view
            }

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.4f, 0.4f, 1f));
            ImGui.BeginDisabled(!ImGui.IsKeyDown(ImGuiKey.ModShift));
            if (ImGui.Button(Translator.LocalizeUI("Delete Preset")))
            {
                presets.RemoveAt(_selectedPresetIndex);
                Plugin.Configuration.Save();
                _selectedPresetIndex = -1;
            }
            ImGui.EndDisabled();
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            {
                ImGui.SetTooltip(Translator.LocalizeUI("Hold SHIFT to Delete Preset"));
            }
            ImGui.PopStyleColor(3);

            ImGui.Separator();
            ImGui.Spacing();
        }
        else
        {
            ImGui.TextColored(new Vector4(1f, 1f, 0f, 1f), Translator.LocalizeUI("You are editing the live character state. Changes apply immediately."));
            ImGui.Separator();
            ImGui.Spacing();
        }

        var keys = targetHistory.Keys.Where(k => targetHistory[k].Count > 0).ToList();
        if (keys.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Translator.LocalizeUI("No textures in this configuration."));
            ImGui.Spacing();
            if (_selectedPresetIndex == -1) // only show import for active
            {
                if (ImGui.Button(Translator.LocalizeUI("Import Textures (File Dialog)"))) OpenImportDialog();
                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Open Texture Painter"))) ImGui.OpenPopup("LayerTypePopup");

                if (ImGui.BeginPopup("LayerTypePopup"))
                {
                    ImGui.Text(Translator.LocalizeUI("Select Target Canvas"));
                    ImGui.Separator();
                    if (ImGui.Selectable(Translator.LocalizeUI("Body")))
                    {
                        Plugin.OpenPaintWindow(null, Plugin.SafeGameObjectManager.LocalPlayer?.Name.TextValue + "_body");
                    }
                    if (ImGui.Selectable(Translator.LocalizeUI("Face")))
                    {
                        Plugin.OpenPaintWindow(null, Plugin.SafeGameObjectManager.LocalPlayer?.Name.TextValue + "_face");
                    }
                    if (ImGui.Selectable(Translator.LocalizeUI("Hair")))
                    {
                        Plugin.OpenPaintWindow(null, Plugin.SafeGameObjectManager.LocalPlayer?.Name.TextValue + "_hair");
                    }
                    if (ImGui.Selectable(Translator.LocalizeUI("Tail")))
                    {
                        Plugin.OpenPaintWindow(null, Plugin.SafeGameObjectManager.LocalPlayer?.Name.TextValue + "_tail");
                    }
                    ImGui.EndPopup();
                }
            }
        }
        else
        {
            if (_selectedPresetIndex == -1)
            {
                if (ImGui.Button(Translator.LocalizeUI("Import Textures (File Dialog)"))) OpenImportDialog();
                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Add New Layer (Open Painter)"))) ImGui.OpenPopup("LayerTypePopup");
                ImGui.Spacing();
            }

            if (ImGui.BeginPopup("LayerTypePopup"))
            {
                ImGui.Text(Translator.LocalizeUI("Select Target Canvas"));
                ImGui.Separator();
                if (ImGui.Selectable(Translator.LocalizeUI("Body")))
                {
                    Plugin.OpenPaintWindow(null, Plugin.SafeGameObjectManager.LocalPlayer?.Name.TextValue + "_body");
                }
                if (ImGui.Selectable(Translator.LocalizeUI("Face")))
                {
                    Plugin.OpenPaintWindow(null, Plugin.SafeGameObjectManager.LocalPlayer?.Name.TextValue + "_face");
                }
                if (ImGui.Selectable(Translator.LocalizeUI("Hair")))
                {
                    Plugin.OpenPaintWindow(null, Plugin.SafeGameObjectManager.LocalPlayer?.Name.TextValue + "_hair");
                }
                if (ImGui.Selectable(Translator.LocalizeUI("Tail")))
                {
                    Plugin.OpenPaintWindow(null, Plugin.SafeGameObjectManager.LocalPlayer?.Name.TextValue + "_tail");
                }
                ImGui.EndPopup();
            }

            ImGui.BeginChild("LayerCategoriesList", new Vector2(200, 0), true);
            for (int i = 0; i < keys.Count; i++)
            {
                bool isSelected = _selectedActiveLayerIndex == i;
                string displayKey = keys[i];
                if (ddt.GearCategoryMeta != null && ddt.GearCategoryMeta.TryGetValue(keys[i], out var gearMeta))
                {
                    displayKey = $"{gearMeta.SlotKey}: {gearMeta.DisplayName}";
                    if (!string.IsNullOrEmpty(gearMeta.MaterialName)) displayKey += $" ({gearMeta.MaterialName})";
                }

                if (ImGui.Selectable($"{displayKey}##SelectCat_{i}", isSelected))
                {
                    _selectedActiveLayerIndex = i;
                }
            }
            ImGui.EndChild();

            ImGui.SameLine();

            ImGui.BeginChild("LayerTexturesList", new Vector2(0, 0), true);
            if (_selectedActiveLayerIndex >= 0 && _selectedActiveLayerIndex < keys.Count)
            {
                string key = keys[_selectedActiveLayerIndex];
                var list = targetHistory[key];
                var tintList = targetTints != null && targetTints.ContainsKey(key) ? targetTints[key] : null;

                string displayKey = key;
                if (ddt.GearCategoryMeta != null && ddt.GearCategoryMeta.TryGetValue(key, out var gearMetaDetail))
                {
                    displayKey = $"{gearMetaDetail.SlotKey}: {gearMetaDetail.DisplayName}";
                    if (!string.IsNullOrEmpty(gearMetaDetail.MaterialName)) displayKey += $" ({gearMetaDetail.MaterialName})";
                }

                ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), Translator.LocalizeUI("Layers for:") + $" {displayKey}");
                ImGui.Separator();

                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.4f, 0.4f, 1f));
                ImGui.BeginDisabled(!ImGui.IsKeyDown(ImGuiKey.ModShift));
                if (ImGui.Button(Translator.LocalizeUI("Clear All") + "##" + key))
                {
                    list.Clear();
                    if (tintList != null) tintList.Clear();
                    if (_selectedPresetIndex == -1) ddt.RebuildCategory(key, false);
                    Plugin.Configuration.Save();
                }
                ImGui.EndDisabled();
                ImGui.PopStyleColor(3);
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Translator.LocalizeUI("Hold SHIFT to Clear All"));

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
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 10);
                        ImGui.SetNextItemWidth(ImGui.GetWindowWidth() - 200);
                    }
                    else
                    {
                        ImGui.SetNextItemWidth(ImGui.GetWindowWidth() - 150);
                    }

                    if (ImGui.InputText("##path_" + key + i, ref path, 1024)) list[i] = path;
                    if (ImGui.IsItemHovered())
                    {
                        if (Plugin.DragDropManager.CreateImGuiTarget("TextureDropTarget", out var files, out _))
                        {
                            if (files.Count > 0)
                            {
                                if (Path.GetExtension(files[0]).Equals(".psd", StringComparison.OrdinalIgnoreCase))
                                    Plugin.PsdImportWindow.StartImport(files[0]);
                                else
                                {
                                    list[i] = files[0];
                                    if (tintList != null && i < tintList.Count) tintList[i] = System.Numerics.Vector4.One;
                                    changed = true;
                                }
                            }
                        }
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;

                    if (ImGui.Button(Translator.LocalizeUI("Up") + "##" + key + i) && i > 0)
                    {
                        var temp = list[i - 1]; list[i - 1] = list[i]; list[i] = temp;
                        if (tintList != null && i < tintList.Count && i - 1 < tintList.Count) {
                            var tempTint = tintList[i - 1]; tintList[i - 1] = tintList[i]; tintList[i] = tempTint;
                        }
                        changed = true;
                    }

                    ImGui.SameLine();
                    if (ImGui.Button(Translator.LocalizeUI("Down") + "##" + key + i) && i < list.Count - 1)
                    {
                        var temp = list[i + 1]; list[i + 1] = list[i]; list[i] = temp;
                        if (tintList != null && i < tintList.Count && i + 1 < tintList.Count) {
                            var tempTint = tintList[i + 1]; tintList[i + 1] = tintList[i]; tintList[i] = tempTint;
                        }
                        changed = true;
                    }

                    ImGui.SameLine();
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.4f, 0.4f, 1f));
                    ImGui.BeginDisabled(!ImGui.IsKeyDown(ImGuiKey.ModShift));
                    bool removed = false;
                    if (ImGui.Button(Translator.LocalizeUI("Remove") + "##" + key + i))
                    {
                        list.RemoveAt(i);
                        if (tintList != null && i < tintList.Count) tintList.RemoveAt(i);
                        removed = true;
                        changed = true;
                    }
                    ImGui.EndDisabled();
                    ImGui.PopStyleColor(3);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Translator.LocalizeUI("Hold SHIFT to Remove"));

                    if (removed) { i--; continue; }

                    if (tintList != null && i < tintList.Count) {
                        System.Numerics.Vector4 col = tintList[i];
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(40);
                        if (ImGui.ColorEdit4("##tint_" + key + i, ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha)) {
                            col.W = 1.0f;
                            tintList[i] = col;
                        }
                        if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;
                    }

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
                            Plugin.OpenPaintWindow(path, key);
                        }
                        if (!canEdit)
                        {
                            ImGui.EndDisabled();
                            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Translator.LocalizeUI("This layer requires a body mod that is not currently available in your Penumbra directory."));
                        }
                    }
                }

                if (changed)
                {
                    if (_selectedPresetIndex == -1) ddt.RebuildCategory(key, false);
                    Plugin.Configuration.Save();
                }
            }
            ImGui.EndChild(); // LayerTexturesList
        }

        ImGui.EndChild(); // PresetDetailsColumn
    }

    public void ApplyPreset(ActiveLayerPreset preset)
    {
        if (Plugin.DragAndDropTextures == null) return;

        Plugin.DragAndDropTextures.TextureHistory.Clear();
        if (Plugin.DragAndDropTextures.TextureHistoryTints != null)
            Plugin.DragAndDropTextures.TextureHistoryTints.Clear();

        foreach (var kvp in preset.TextureHistory)
        {
            Plugin.DragAndDropTextures.TextureHistory[kvp.Key] = new System.Collections.Generic.List<string>(kvp.Value);
        }
        if (preset.TextureHistoryTints != null && Plugin.DragAndDropTextures.TextureHistoryTints != null)
        {
            foreach (var kvp in preset.TextureHistoryTints)
            {
                Plugin.DragAndDropTextures.TextureHistoryTints[kvp.Key] = new System.Collections.Generic.List<System.Numerics.Vector4>(kvp.Value);
            }
        }

        Plugin.Configuration.TextureHistory = Plugin.DragAndDropTextures.TextureHistory;
        Plugin.Configuration.TextureHistoryTints = Plugin.DragAndDropTextures.TextureHistoryTints;
        Plugin.Configuration.Save();

        foreach (var category in Plugin.DragAndDropTextures.TextureHistory.Keys)
        {
            Plugin.DragAndDropTextures.RebuildCategory(category, false);
        }
    }

    private void ExportCategoryToPsd(string key, System.Collections.Generic.List<string> files)
    {
        if (Plugin.Chat != null)
            Plugin.Chat.Print("[DragAndDrop] Exporting to PSD... Please wait.");
            
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
                    if (character != null && global::PenumbraAndGlamourerIpcWrapper.Instance.GetStateBase64 != null)
                    {
                        try 
                        {
                            var stateBase64Result = global::PenumbraAndGlamourerIpcWrapper.Instance.GetStateBase64.Invoke(character.ObjectIndex);
                            var customization = PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.CharacterCustomization.ReadCustomization(stateBase64Result.Item2);
                            int ffxivGender = customization.Customize.Gender.Value;
                            Guid collectionId = global::PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;
                            targetBody = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(collectionId, ffxivGender, out string _, Plugin);
                            
                            // Initialize the path so FastUVTransfer maps can be found
                            if (global::PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory != null)
                            {
                                string modPath = global::PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
                                LooseTextureCompilerCore.GlobalPathStorage.OriginalBaseDirectory = modPath + @"\LooseTextureCompilerDLC";
                            }
                        }
                        catch (Exception innerEx)
                        {
                            Plugin.PluginLog.Warning($"[DragAndDrop] Could not determine target body via IPC: {innerEx.Message}");
                        }
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
                    if (Plugin.Chat != null)
                        Plugin.Chat.Print($"[DragAndDrop] Successfully exported PSD to: {psdPath}");
                        
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                    {
                        FileName = exportFolder,
                        UseShellExecute = true,
                        Verb = "open"
                    });
                }
                else
                {
                    if (Plugin.Chat != null)
                        Plugin.Chat.Print($"[DragAndDrop] Error: No valid image files found to export.");
                    Plugin.PluginLog.Warning($"No valid files found to export for category {key}");
                }
            }
            catch (Exception ex)
            {
                if (Plugin.Chat != null)
                    Plugin.Chat.Print($"[DragAndDrop] Failed to export PSD! Check the plugin log for details.");
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
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.4f, 0.4f, 1f));
            if (ImGui.Button($"X##remove_history_{i}"))
            {
                recentLayers.RemoveAt(i);
                Plugin.Configuration.Save();
                i--;
            }
            ImGui.PopStyleColor(3);
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
    private string _cachedExportBenchmarkLog = null;
    private DateTime _lastErrorLogCheck = DateTime.MinValue;

    private void DrawDiagnostics()
    {
        ImGui.Spacing();
        ImGui.Text(Translator.LocalizeUI("GPU Fallback Diagnostic Log:"));

        string logPath = Path.Combine(Path.GetTempPath(), "GPU_Fallback_Error.txt");
        string benchPath = Path.Combine(Path.GetTempPath(), "GPU_Benchmark.txt");
        string exportBenchPath = Path.Combine(Path.GetTempPath(), "Export_Benchmark.txt");

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

            if (File.Exists(exportBenchPath))
            {
                try { _cachedExportBenchmarkLog = File.ReadAllText(exportBenchPath); } catch { }
            }
            else
            {
                _cachedExportBenchmarkLog = Translator.LocalizeUI("No export benchmark data recorded yet.");
            }

            _lastErrorLogCheck = DateTime.Now;
        }

        if (_cachedErrorLog != null)
        {
            ImGui.BeginChild("ErrorLogChild", new Vector2(-1, 100), true);
            ImGui.TextWrapped(_cachedErrorLog);
            ImGui.EndChild();
        }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.4f, 0.4f, 1f));
        if (ImGui.Button(Translator.LocalizeUI("Clear Error Log")))
        {
            if (File.Exists(logPath)) { try { File.Delete(logPath); } catch { } }
            _cachedErrorLog = Translator.LocalizeUI("No GPU fallback errors detected. (GPU acceleration is working fine!)");
        }
        ImGui.PopStyleColor(3);

        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text(Translator.LocalizeUI("Export Performance Benchmarks:"));

        if (ImGui.Button(Translator.LocalizeUI("Copy Export Benchmark to Clipboard")))
        {
            if (_cachedExportBenchmarkLog != null)
                ImGui.SetClipboardText(_cachedExportBenchmarkLog);
        }

        if (_cachedExportBenchmarkLog != null)
        {
            ImGui.BeginChild("ExportBenchmarkLogChild", new Vector2(-1, 250), true);
            ImGui.TextUnformatted(_cachedExportBenchmarkLog);
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                ImGui.SetScrollHereY(1.0f);
            ImGui.EndChild();
        }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.4f, 0.4f, 1f));
        if (ImGui.Button(Translator.LocalizeUI("Clear Export Benchmark")))
        {
            if (File.Exists(exportBenchPath)) { try { File.Delete(exportBenchPath); } catch { } }
            _cachedExportBenchmarkLog = Translator.LocalizeUI("No export benchmark data recorded yet.");
        }
        ImGui.PopStyleColor(3);

        ImGui.Separator();
        ImGui.Spacing();
        ImGui.Text(Translator.LocalizeUI("MergeImageLayers GPU Benchmarks:"));

        if (ImGui.Button(Translator.LocalizeUI("Copy GPU Benchmark to Clipboard")))
        {
            if (_cachedBenchmarkLog != null)
                ImGui.SetClipboardText(_cachedBenchmarkLog);
        }

        if (_cachedBenchmarkLog != null)
        {
            ImGui.BeginChild("BenchmarkLogChild", new Vector2(-1, Math.Max(100, ImGui.GetContentRegionAvail().Y - 40)), true);
            ImGui.TextUnformatted(_cachedBenchmarkLog);
            if (ImGui.GetScrollY() >= ImGui.GetScrollMaxY())
                ImGui.SetScrollHereY(1.0f);
            ImGui.EndChild();
        }

        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.4f, 0.4f, 1f));
        if (ImGui.Button(Translator.LocalizeUI("Clear Benchmark Log")))
        {
            if (File.Exists(benchPath)) { try { File.Delete(benchPath); } catch { } }
            _cachedBenchmarkLog = Translator.LocalizeUI("No benchmark data recorded yet.");
        }
        ImGui.PopStyleColor(3);
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
            if (!layer.Enabled) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1.0f));
            string displayName = layer.Enabled ? layer.Name : layer.Name + " " + Translator.LocalizeUI("(Disabled)");
            if (ImGui.Selectable($"{displayName}##SelectLayer_{i}", isSelected))
            {
                _selectedContextualLayerIndex = i;
            }
            if (!layer.Enabled) ImGui.PopStyleColor();
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

            bool enabled = layer.Enabled;
            if (ImGui.Checkbox(Translator.LocalizeUI("Enabled") + "##ContextEnabled", ref enabled))
            {
                layer.Enabled = enabled;
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

            if (!layer.ProceduralDecalMode)
            {
                int clearType = (int)layer.ClearTrigger;
                string[] clearNames = Enum.GetNames(typeof(ClearCondition)).Select(n => n.Replace("_", " ")).ToArray();
                var locClearNames = Translator.LocalizeTextArray(clearNames);
                if (ImGui.Combo(Translator.LocalizeUI("Clear Condition") + "##ContextClear", ref clearType, locClearNames, locClearNames.Length))
                {
                    layer.ClearTrigger = (ClearCondition)clearType;
                    changed = true;
                }
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

                if (!layer.ProceduralDecalMode)
                {
                    int decay = layer.DecayIntervalSeconds;
                    if (ImGui.InputInt(Translator.LocalizeUI("Decay Interval (Seconds)") + "##ContextDecay", ref decay))
                    {
                        layer.DecayIntervalSeconds = Math.Max(0, decay);
                        changed = true;
                    }
                }
            }

            if (layer.Trigger == TriggerType.Emote ||
                layer.Trigger == TriggerType.Audio_Path_Load ||
                layer.Trigger == TriggerType.Chat_Message ||
                layer.Trigger == TriggerType.Swimming_State ||
                layer.Trigger == TriggerType.Combat_State ||
                layer.Trigger == TriggerType.Weapon_Drawn ||
                layer.Trigger == TriggerType.Mounted_State)
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
            ImGui.TextWrapped(Translator.LocalizeUI("When enabled, the textures in this folder will be treated as decals (e.g. blood/dirt splatters) and procedurally stamped onto random locations of the player's 3D model instead of overriding the entire body. (Experimental, may cause hitches)"));

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

            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
            ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.4f, 0.4f, 1f));
            ImGui.BeginDisabled(!ImGui.IsKeyDown(ImGuiKey.ModShift));
            bool removeClicked = ImGui.Button(Translator.LocalizeUI("Remove Layer") + "##ContextRemove");
            ImGui.EndDisabled();
            ImGui.PopStyleColor(3);
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Translator.LocalizeUI("Hold SHIFT to Remove Layer"));
            
            if (removeClicked)
            {
                Plugin.ContextualLayerManager.DeleteLayer(layer);
                _selectedContextualLayerIndex = Math.Max(0, _selectedContextualLayerIndex - 1);
            }
            else if (changed)
            {
                layer.Save();
            }
            
            ImGui.Separator();
            ImGui.Spacing();
            
            ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), Translator.LocalizeUI("Folder Contents"));
            ImGui.BeginChild("ContextualLayerTexturesList", new Vector2(0, 0), true);
            
            if (Directory.Exists(layer.DirectoryPath))
            {
                var files = Directory.GetFiles(layer.DirectoryPath, "*.png").OrderBy(f => f).ToList();
                
                if (files.Count == 0)
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Translator.LocalizeUI("No textures found in this layer's folder."));
                }
                
                for (int i = 0; i < files.Count; i++)
                {
                    string path = files[i];
                    var tex = GetPreviewTexture(path);
                    var wrap = tex?.GetWrapOrDefault();

                    if (wrap != null)
                    {
                        ImGui.Image(wrap.Handle, new Vector2(40, 40));
                        ImGui.SameLine();
                        ImGui.SetCursorPosY(ImGui.GetCursorPosY() + 10);
                        ImGui.SetNextItemWidth(ImGui.GetWindowWidth() - 150);
                    }
                    else
                    {
                        ImGui.SetNextItemWidth(ImGui.GetWindowWidth() - 100);
                    }

                    string displayPath = System.IO.Path.GetFileName(path);
                    ImGui.InputText("##ctxpath_" + i, ref displayPath, 1024, ImGuiInputTextFlags.ReadOnly);
                    
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.4f, 0.4f, 1f));
                    ImGui.BeginDisabled(!ImGui.IsKeyDown(ImGuiKey.ModShift));
                    if (ImGui.Button(Translator.LocalizeUI("Remove") + "##ctx" + i))
                    {
                        try { System.IO.File.Delete(path); } catch { }
                    }
                    ImGui.EndDisabled();
                    ImGui.PopStyleColor(3);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Translator.LocalizeUI("Hold SHIFT to Delete file from disk"));
                }
                
                ImGui.Spacing();
                ImGui.Button(Translator.LocalizeUI("Drop new .png files here to add them to the layer") + "##dropzone", new Vector2(-1, 40));
                if (ImGui.IsItemHovered())
                {
                    if (Plugin.DragDropManager.CreateImGuiTarget("TextureDropTarget", out var newDroppedFiles, out _))
                    {
                        foreach (var df in newDroppedFiles)
                        {
                            if (System.IO.Path.GetExtension(df).Equals(".png", StringComparison.OrdinalIgnoreCase))
                            {
                                try { System.IO.File.Copy(df, System.IO.Path.Combine(layer.DirectoryPath, System.IO.Path.GetFileName(df)), true); } catch { }
                            }
                        }
                    }
                }
            }
            ImGui.EndChild();
        }
        ImGui.EndChild();
    }

    #region Animated Layers UI

    private string _animatedLayerFrameFolder = "";
    private string _animatedLayerName = "Animation";
    private int _animatedLayerTarget = 0; // 0 = body, 1 = face
    private Vector2 _animatedLayerUVPos = new Vector2(0.3f, 0.3f);
    private Vector2 _animatedLayerUVSize = new Vector2(0.4f, 0.4f);
    private int _animatedLayerFps = 15;
    private float _animatedLayerOpacity = 1.0f;
    private string _uvPreviewCachedPath = null;
    private int _uvPreviewCachedTarget = -1;
    private Dalamud.Interface.Textures.ISharedImmediateTexture _uvPreviewTexture = null;
    private DateTime _uvPreviewLastResolve = DateTime.MinValue;

    /// <summary>
    /// Resolves and caches the current body/face underlay texture for UV preview display.
    /// Uses the default bundled DLC skin textures which are always available.
    /// </summary>
    private Dalamud.Interface.Textures.ISharedImmediateTexture GetUVPreviewTexture(string category)
    {
        // Only re-resolve when target dropdown changes
        if (_uvPreviewCachedTarget == _animatedLayerTarget && _uvPreviewTexture != null)
            return _uvPreviewTexture;

        _uvPreviewCachedTarget = _animatedLayerTarget;
        _uvPreviewLastResolve = DateTime.Now;

        try
        {
            string texturePath = null;
            var localPlayer = Plugin.SafeGameObjectManager?.LocalPlayer;
            if (localPlayer == null) return _uvPreviewTexture;

            string modPath = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
            string dlcPath = Path.Combine(modPath, "LooseTextureCompilerDLC");

            if (category == "body" && localPlayer is Dalamud.Game.ClientState.Objects.Types.ICharacter bodyChar)
            {
                var customization = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.GetCustomization(bodyChar);
                if (customization != null)
                {
                    int gender = customization.Customize.Gender.Value;
                    int clan = customization.Customize.Clan.Value - 1;
                    int mainRace = FFXIVLooseTextureCompiler.Racial.RaceInfo.SubRaceToMainRace(clan);

                    // Detect active body mod via Penumbra
                    int bodyType = gender == 0 ? 3 : 1; // default: TBSE male, Bibo+ female
                    try
                    {
                        Guid collectionId = PenumbraAndGlamourerIpcWrapper.Instance
                            .GetCollectionForObject.Invoke(localPlayer.ObjectIndex).Item3.Id;
                        int detected = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions
                            .DetectBaseBodyFromPenumbra(collectionId, gender, out string _, Plugin);
                        if (detected == 1) bodyType = 1;      // Bibo+
                        else if (detected == 2) bodyType = 2;  // Gen3
                        else if (detected == 3) bodyType = 3;  // TBSE
                        else if (detected == 5) bodyType = 5;  // Otopop
                    }
                    catch { }

                    var ts = LooseTextureCompilerCore.ProjectCreation.ProjectHelper.CreateBodyTextureSet(
                        gender, bodyType, mainRace, 0, false);
                    if (!string.IsNullOrEmpty(ts?.InternalBasePath))
                    {
                        Guid collection = PenumbraAndGlamourerIpcWrapper.Instance
                            .GetCollectionForObject.Invoke(localPlayer.ObjectIndex).Item3.Id;
                        PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collection, ts.InternalBasePath, out texturePath);
                    }
                }
            }
            else if (category == "face" && localPlayer is Dalamud.Game.ClientState.Objects.Types.ICharacter faceChar)
            {
                var customization = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.GetCustomization(faceChar);
                if (customization != null)
                {
                    int face = customization.Customize.Face.Value - 1;
                    int gender = customization.Customize.Gender.Value;
                    int race = customization.Customize.Race.Value - 1;
                    int clan = customization.Customize.Clan.Value - 1;
                    var ts = LooseTextureCompilerCore.ProjectCreation.ProjectHelper.CreateFaceTextureSet(
                        face, 0, 0, gender, race, clan, 0, false);
                    if (!string.IsNullOrEmpty(ts?.InternalBasePath))
                    {
                        Guid collection = PenumbraAndGlamourerIpcWrapper.Instance
                            .GetCollectionForObject.Invoke(localPlayer.ObjectIndex).Item3.Id;
                        PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collection, ts.InternalBasePath, out texturePath);
                    }
                }
            }

            // Load texture if path changed
            if (!string.IsNullOrEmpty(texturePath) && File.Exists(texturePath) && texturePath != _uvPreviewCachedPath)
            {
                _uvPreviewCachedPath = texturePath;

                // .ltct files are XOR-encoded — decode to temp PNG first
                if (texturePath.EndsWith(".ltct", StringComparison.OrdinalIgnoreCase))
                {
                    string tempPng = Path.Combine(Path.GetTempPath(), "ddt_uv_preview.png");
                    using (var bmp = FFXIVLooseTextureCompiler.ImageProcessing.TexIO.ResolveBitmap(texturePath))
                    {
                        if (bmp != null)
                        {
                            bmp.Save(tempPng, System.Drawing.Imaging.ImageFormat.Png);
                            _uvPreviewTexture = Plugin.TextureProvider.GetFromFile(tempPng);
                        }
                    }
                }
                else
                {
                    _uvPreviewTexture = Plugin.TextureProvider.GetFromFile(texturePath);
                }
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error($"[AnimatedLayers UV Preview] {ex.Message}");
        }

        return _uvPreviewTexture;
    }

    private void DrawAnimatedLayers()
    {
        ImGui.Spacing();
        var manager = Plugin.AnimatedLayerManager;
        if (manager == null)
        {
            ImGui.TextColored(new Vector4(1f, 0.5f, 0f, 1f), "Animated Layer Manager not initialized.");
            return;
        }

        // --- Add New Layer Section ---
        ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), Translator.LocalizeUI("Add Animated Layer"));
        ImGui.Separator();
        ImGui.Spacing();

        ImGui.SetNextItemWidth(200);
        ImGui.InputText("Name##AnimLayerName", ref _animatedLayerName, 128);

        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - 80);
        ImGui.InputText("##AnimLayerFolder", ref _animatedLayerFrameFolder, 512);
        ImGui.SameLine();
        if (ImGui.Button("Browse##AnimFolder"))
        {
            _fileDialogManager.OpenFolderDialog(
                "Select Frame Folder",
                (b, path) =>
                {
                    if (b && !string.IsNullOrEmpty(path))
                        _animatedLayerFrameFolder = path;
                },
                null, false);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Folder containing sequential frame images (PNG/JPG/BMP)");

        string[] targets = { "Body", "Face" };
        ImGui.SetNextItemWidth(120);
        ImGui.Combo("Target##AnimTarget", ref _animatedLayerTarget, targets, targets.Length);

        ImGui.SetNextItemWidth(200);
        ImGui.SliderFloat2("UV Position##AnimUVPos", ref _animatedLayerUVPos, 0f, 1f, "%.2f");
        ImGui.SetNextItemWidth(200);
        ImGui.SliderFloat2("UV Size##AnimUVSize", ref _animatedLayerUVSize, 0.01f, 1f, "%.2f");
        ImGui.SetNextItemWidth(120);
        ImGui.SliderInt("FPS##AnimFps", ref _animatedLayerFps, 1, 30);
        ImGui.SetNextItemWidth(120);
        ImGui.SliderFloat("Opacity##AnimOpacity", ref _animatedLayerOpacity, 0f, 1f, "%.2f");

        // --- UV Preview Canvas ---
        ImGui.Spacing();
        ImGui.Text("UV Preview:");
        float previewSize = 200f;
        Vector2 canvasPos = ImGui.GetCursorScreenPos();
        var drawList = ImGui.GetWindowDrawList();

        // Background (dark grey fallback)
        drawList.AddRectFilled(canvasPos, canvasPos + new Vector2(previewSize, previewSize),
            ImGui.GetColorU32(new Vector4(0.12f, 0.12f, 0.12f, 1f)));

        // Try to show the actual body/face texture as background
        string targetCategory = _animatedLayerTarget == 0 ? "body" : "face";
        var uvTex = GetUVPreviewTexture(targetCategory);
        var uvWrap = uvTex?.GetWrapOrDefault();
        if (uvWrap != null)
        {
            drawList.AddImage(uvWrap.Handle, canvasPos, canvasPos + new Vector2(previewSize, previewSize));
        }
        else
        {
            // Grid lines as fallback orientation (4x4)
            uint gridColor = ImGui.GetColorU32(new Vector4(0.3f, 0.3f, 0.3f, 0.5f));
            for (int g = 1; g < 4; g++)
            {
                float offset = (g / 4f) * previewSize;
                drawList.AddLine(canvasPos + new Vector2(offset, 0), canvasPos + new Vector2(offset, previewSize), gridColor);
                drawList.AddLine(canvasPos + new Vector2(0, offset), canvasPos + new Vector2(previewSize, offset), gridColor);
            }
        }

        // Overlay rectangle (animated layer placement)
        Vector2 rectMin = canvasPos + new Vector2(_animatedLayerUVPos.X * previewSize, _animatedLayerUVPos.Y * previewSize);
        Vector2 rectMax = rectMin + new Vector2(_animatedLayerUVSize.X * previewSize, _animatedLayerUVSize.Y * previewSize);
        rectMax = Vector2.Min(rectMax, canvasPos + new Vector2(previewSize, previewSize));

        // Translucent blue fill
        drawList.AddRectFilled(rectMin, rectMax,
            ImGui.GetColorU32(new Vector4(0.2f, 0.5f, 1f, 0.3f)));
        // Bright border
        drawList.AddRect(rectMin, rectMax,
            ImGui.GetColorU32(new Vector4(0.3f, 0.7f, 1f, 0.9f)), 0f, ImDrawFlags.None, 2f);

        // Canvas border
        drawList.AddRect(canvasPos, canvasPos + new Vector2(previewSize, previewSize),
            ImGui.GetColorU32(new Vector4(0.5f, 0.5f, 0.5f, 1f)));

        // Corner labels
        drawList.AddText(canvasPos + new Vector2(2, 2), ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f)), "0,0");
        drawList.AddText(canvasPos + new Vector2(previewSize - 22, previewSize - 16),
            ImGui.GetColorU32(new Vector4(1f, 1f, 1f, 0.6f)), "1,1");

        // Advance cursor past the preview
        ImGui.Dummy(new Vector2(previewSize, previewSize));

        ImGui.Spacing();
        bool canAdd = !string.IsNullOrEmpty(_animatedLayerFrameFolder) && Directory.Exists(_animatedLayerFrameFolder)
                      && !string.IsNullOrEmpty(_animatedLayerName);
        if (!canAdd) ImGui.BeginDisabled();
        if (ImGui.Button(Translator.LocalizeUI("Prepare & Activate")))
        {
            var def = new AnimatedLayerDefinition
            {
                Name = _animatedLayerName,
                FrameFolder = _animatedLayerFrameFolder,
                TargetCategory = targets[_animatedLayerTarget].ToLower(),
                UVPosition = _animatedLayerUVPos,
                UVSize = _animatedLayerUVSize,
                Fps = _animatedLayerFps,
                Opacity = _animatedLayerOpacity,
                IsActive = true
            };

            // Save to config
            var existing = Plugin.Configuration.AnimatedLayers.FindIndex(a => a.Name == def.Name);
            if (existing >= 0)
                Plugin.Configuration.AnimatedLayers[existing] = def;
            else
                Plugin.Configuration.AnimatedLayers.Add(def);
            Plugin.Configuration.Save();

            // Activate
            var localPlayer = Plugin.SafeGameObjectManager?.LocalPlayer;
            if (localPlayer != null && localPlayer is Dalamud.Game.ClientState.Objects.Types.ICharacter character)
            {
                System.Threading.Tasks.Task.Run(() => manager.ActivateLayer(def, localPlayer.Name.TextValue, character));
            }
        }
        if (!canAdd) ImGui.EndDisabled();

        // --- Active Layers Display ---
        ImGui.Spacing();
        ImGui.Spacing();
        ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), Translator.LocalizeUI("Active Animated Layers"));
        ImGui.Separator();
        ImGui.Spacing();

        if (manager.ActiveLayers.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "No animated layers active.");
        }
        else
        {
            if (ImGui.BeginTable("AnimLayerTable", 5, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn("Name", ImGuiTableColumnFlags.WidthFixed, 150);
                ImGui.TableSetupColumn("Target", ImGuiTableColumnFlags.WidthFixed, 60);
                ImGui.TableSetupColumn("Progress", ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthFixed, 200);
                ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 80);
                ImGui.TableHeadersRow();

                foreach (var kvp in manager.ActiveLayers)
                {
                    var state = kvp.Value;
                    ImGui.TableNextRow();

                    ImGui.TableNextColumn();
                    ImGui.Text(state.Definition.Name);

                    ImGui.TableNextColumn();
                    ImGui.Text(state.Definition.TargetCategory);

                    ImGui.TableNextColumn();
                    if (state.Active)
                    {
                        float progress = state.FrameCount > 0 ? (float)state.CurrentFrame / state.FrameCount : 0;
                        ImGui.ProgressBar(progress, new Vector2(-1, 0), $"{state.CurrentFrame}/{state.FrameCount}");
                    }
                    else
                    {
                        ImGui.ProgressBar(state.PreparationProgress, new Vector2(-1, 0),
                            state.PreparationProgress < 1f ? $"Preparing {(state.PreparationProgress * 100):F0}%" : "Ready");
                    }

                    ImGui.TableNextColumn();
                    if (!string.IsNullOrEmpty(state.ErrorStackTrace))
                    {
                        ImGui.TextColored(new Vector4(1f, 0.4f, 0.4f, 1f), state.Status ?? "Error");
                        if (ImGui.TreeNode($"View Stack Trace##{kvp.Key}"))
                        {
                            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.8f, 0.8f, 0.8f, 1f));
                            ImGui.TextWrapped(state.ErrorStackTrace);
                            ImGui.PopStyleColor();
                            ImGui.TreePop();
                        }
                    }
                    else
                    {
                        ImGui.TextWrapped(state.Status ?? "");
                    }

                    ImGui.TableNextColumn();
                    ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
                    ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.4f, 0.4f, 1f));
                    bool stopClicked = ImGui.Button("Stop##" + kvp.Key);
                    ImGui.PopStyleColor(3);
                    
                    if (stopClicked)
                    {
                        manager.DeactivateLayer(kvp.Key);
                        Plugin.Configuration.AnimatedLayers.RemoveAll(a => a.Name == state.Definition.Name);
                        Plugin.Configuration.Save();
                        break; // Collection modified
                    }
                }

                ImGui.EndTable();
            }
        }

        // --- Saved Definitions ---
        var savedDefs = Plugin.Configuration.AnimatedLayers;
        if (savedDefs.Count > 0)
        {
            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), Translator.LocalizeUI("Saved Animated Layers"));
            ImGui.Separator();

            for (int i = 0; i < savedDefs.Count; i++)
            {
                var def = savedDefs[i];
                bool isActive = manager.ActiveLayers.Values.Any(s => s.Definition.Name == def.Name && s.Active);

                ImGui.Text($"{def.Name} ({def.TargetCategory}, {def.Fps}fps)");
                ImGui.SameLine();

                if (!isActive)
                {
                    if (ImGui.Button(Translator.LocalizeUI("Activate") + "##saved_" + i))
                    {
                        var localPlayer = Plugin.SafeGameObjectManager?.LocalPlayer;
                        if (localPlayer != null && localPlayer is Dalamud.Game.ClientState.Objects.Types.ICharacter character)
                        {
                            System.Threading.Tasks.Task.Run(() => manager.ActivateLayer(def, localPlayer.Name.TextValue, character));
                        }
                    }
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.2f, 1f, 0.2f, 1f), "Active");
                }

                ImGui.SameLine();
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new Vector4(0.9f, 0.3f, 0.3f, 1f));
                ImGui.PushStyleColor(ImGuiCol.ButtonActive, new Vector4(1f, 0.4f, 0.4f, 1f));
                bool removeSavedClicked = ImGui.Button("X##removeSaved_" + i);
                ImGui.PopStyleColor(3);
                
                if (removeSavedClicked)
                {
                    savedDefs.RemoveAt(i);
                    Plugin.Configuration.Save();
                    break;
                }
            }
        }
    }

    #endregion
}
