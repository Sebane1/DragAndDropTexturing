using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using DragAndDropTexturing.Equipment;
using DragAndDropTexturing.LanguageHelpers;
using DragAndDropTexturing.Overlays;
using DragAndDropTexturing.VideoPlayback;
using FFXIVLooseTextureCompiler.Racial;
using LooseTextureCompilerCore.ProjectCreation;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Numerics;
using static Penumbra.GameData.Files.ShpkFile;
using RoleplayingVoice;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;

namespace DragAndDropTexturing.Windows;

public class MainWindow : Window, IDisposable
{
    private Plugin Plugin;
    private List<Lumina.Excel.Sheets.Emote> _emotes = new();
    private string[] _emoteNames = new string[0];
    private string _emoteSearchFilter = "";
    private readonly FileDialogManager _fileDialogManager = new();

    public MainWindow(Plugin plugin)
        : base("Drag And Drop Texturing Config v" + System.Reflection.Assembly.GetExecutingAssembly().GetName().Version, ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse)
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

    public void RefreshLayerCollection(IGameObject targetGameObject)
    {
        if (targetGameObject == null) return;
        _layerTargetObjectIndex = targetGameObject.ObjectIndex;
        var collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(targetGameObject.ObjectIndex);
        _collectionId = collection.EffectiveCollection.Id.ToString();
        if (!Plugin.DragAndDropTextures.TextureCollectionHistory.ContainsKey(_collectionId))
        {
            Plugin.DragAndDropTextures.TextureCollectionHistory[_collectionId] = new Dictionary<string, List<string>>();
        }
        if (!Plugin.DragAndDropTextures.TextureCollectionHistoryTints.ContainsKey(_collectionId))
        {
            Plugin.DragAndDropTextures.TextureCollectionHistoryTints[_collectionId] = new Dictionary<string, List<Vector4>>();
        }
        if (!Plugin.DragAndDropTextures.TextureCollectionHistoryBlendModes.ContainsKey(_collectionId))
        {
            Plugin.DragAndDropTextures.TextureCollectionHistoryBlendModes[_collectionId] = new Dictionary<string, List<int>>();
        }
        if (!Plugin.DragAndDropTextures.CollectionSortedPenumbraOverlayTints.ContainsKey(_collectionId))
        {
            Plugin.DragAndDropTextures.CollectionSortedPenumbraOverlayTints[_collectionId] = new Dictionary<string, Vector4>();
        }
        if (!Plugin.DragAndDropTextures.CollectionSortedPenumbraOverlayGlowTints.ContainsKey(_collectionId))
        {
            Plugin.DragAndDropTextures.CollectionSortedPenumbraOverlayGlowTints[_collectionId] = new Dictionary<string, Vector4>();
        }
        if (Plugin.Configuration.CollectionSortedPenumbraOverlayColorRows == null)
            Plugin.Configuration.CollectionSortedPenumbraOverlayColorRows = new Dictionary<string, Dictionary<string, List<AdvancedColorTableRow>>>();
        if (!Plugin.Configuration.CollectionSortedPenumbraOverlayColorRows.ContainsKey(_collectionId))
            Plugin.Configuration.CollectionSortedPenumbraOverlayColorRows[_collectionId] = new Dictionary<string, List<AdvancedColorTableRow>>();
        if (Plugin.DragAndDropTextures.CollectionSortedPenumbraOverlayColorRows == null)
            Plugin.DragAndDropTextures.CollectionSortedPenumbraOverlayColorRows = Plugin.Configuration.CollectionSortedPenumbraOverlayColorRows;
    }

    public void TrySetLayerTargetFromDrop(IGameObject gameObject)
    {
        if (gameObject == null || !gameObject.IsValid()) return;
        if (_layerTargetObjectIndex == gameObject.ObjectIndex) return;

        var localPlayer = Plugin.SafeGameObjectManager.LocalPlayer;
        if (localPlayer == null) return;

        bool isLocalPlayer = gameObject.ObjectIndex == localPlayer.ObjectIndex;
        bool isNearbyTarget = gameObject.ObjectKind == ObjectKind.Pc || gameObject.ObjectKind == ObjectKind.Companion;
        if (!isLocalPlayer && !isNearbyTarget) return;

        if (!isLocalPlayer)
        {
            bool isNear = Plugin.GetNearestObjects().Any(o => o != null && o.ObjectIndex == gameObject.ObjectIndex);
            if (!isNear) return;
        }

        RefreshLayerCollection(gameObject);
        _selectedActiveLayerIndex = 0;
    }

    private ushort _layerTargetObjectIndex = ushort.MaxValue;
    private string _layerTargetSearchFilter = "";

    private IGameObject GetLayerTargetGameObject()
    {
        var localPlayer = Plugin.SafeGameObjectManager.LocalPlayer;
        if (localPlayer == null) return null;

        if (_layerTargetObjectIndex == ushort.MaxValue)
            _layerTargetObjectIndex = localPlayer.ObjectIndex;

        foreach (var candidate in EnumerateLayerTargetCandidates())
        {
            if (candidate.ObjectIndex == _layerTargetObjectIndex)
                return candidate;
        }

        _layerTargetObjectIndex = localPlayer.ObjectIndex;
        return localPlayer;
    }

    private ICharacter GetLayerTargetCharacter() => GetLayerTargetGameObject() as ICharacter;

    private string GetLayerTargetCharacterName() => GetLayerTargetGameObject()?.Name?.TextValue ?? "";

    private IEnumerable<IGameObject> EnumerateLayerTargetCandidates()
    {
        var localPlayer = Plugin.SafeGameObjectManager.LocalPlayer;
        if (localPlayer == null) yield break;

        yield return localPlayer;

        foreach (var obj in Plugin.GetNearestObjects())
        {
            if (obj == null || !obj.IsValid()) continue;
            if (obj.ObjectIndex == localPlayer.ObjectIndex) continue;
            if (obj.ObjectKind == ObjectKind.Pc || obj.ObjectKind == ObjectKind.Companion)
                yield return obj;
        }
    }

    private string FormatLayerTargetLabel(IGameObject obj)
    {
        var localPlayer = Plugin.SafeGameObjectManager.LocalPlayer;
        string name = obj.Name?.TextValue;
        if (string.IsNullOrEmpty(name))
            name = $"#{obj.ObjectIndex}";

        string role = obj.ObjectIndex == localPlayer?.ObjectIndex
            ? Translator.LocalizeUI("You")
            : obj.ObjectKind == ObjectKind.Companion
                ? Translator.LocalizeUI("Companion")
                : Translator.LocalizeUI("Player");

        float dist = localPlayer != null ? Vector3.Distance(localPlayer.Position, obj.Position) : 0f;
        return $"{role}: {name} ({dist:F1}y)";
    }

    private void DrawLayerTargetSelector()
    {
        if (GetLayerTargetGameObject() == null)
        {
            ImGui.TextColored(new Vector4(0.8f, 0.4f, 0.4f, 1f), Translator.LocalizeUI("No character available."));
            ImGui.Spacing();
            return;
        }

        ImGui.Text(Translator.LocalizeUI("Layer Target:"));
        ImGui.SetNextItemWidth(-1);
        if (ImGui.BeginCombo("##LayerTargetCombo", FormatLayerTargetLabel(GetLayerTargetGameObject())))
        {
            ImGui.InputText(Translator.LocalizeUI("Search") + "##LayerTargetSearch", ref _layerTargetSearchFilter, 128);
            string filter = _layerTargetSearchFilter.ToLower();

            foreach (var obj in EnumerateLayerTargetCandidates())
            {
                string label = FormatLayerTargetLabel(obj);
                if (!string.IsNullOrEmpty(filter) && !label.ToLower().Contains(filter))
                    continue;

                bool isSelected = obj.ObjectIndex == _layerTargetObjectIndex;
                if (ImGui.Selectable($"{label}##LT_{obj.ObjectIndex}", isSelected))
                {
                    if (_layerTargetObjectIndex != obj.ObjectIndex)
                    {
                        RefreshLayerCollection(obj);
                    }
                    ImGui.CloseCurrentPopup();
                }
                if (isSelected) ImGui.SetItemDefaultFocus();
            }
            ImGui.EndCombo();
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Translator.LocalizeUI("Switch Penumbra collection target. Shows you and nearby players or companions within 3 yalms."));
        }
        ImGui.Spacing();
    }

    private static bool LayerKeyMatchesTarget(string key, string charName)
    {
        if (string.IsNullOrEmpty(charName)) return true;
        return key.StartsWith(charName + "_", StringComparison.Ordinal);
    }

    public void Dispose() { }
    public override void OnOpen()
    {
        base.OnOpen();
        RefreshLayerCollection(Plugin.SafeGameObjectManager.LocalPlayer);
    }

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
            if (ImGui.BeginTabItem(Translator.LocalizeUI("Proteus Mods")))
            {
                DrawPenumbraFoundMods();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Translator.LocalizeUI("Animated Layers")))
            {
                DrawAnimatedLayers();
                ImGui.EndTabItem();
            }
            if (ImGui.BeginTabItem(Translator.LocalizeUI("Onion Mods")))
            {
                DrawOnionModsTab();
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
    private string _penumbraColorEditorOverlayKey = null;

    private void DrawSettings()
    {
        ImGui.Spacing();

        if (ImGui.Button(Translator.LocalizeUI("Re-Export All Textures")))
        {
            Plugin.DragAndDropTextures?.RebuildAllCategories();
            Plugin.Chat?.Print("[Drag And Drop Texturing] Re-exporting all active texture categories...");
        }
        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Translator.LocalizeUI("Manually re-exports all active texture layers. Also available via /ddt export"));
        }

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
            if (ddtForRebuild != null)
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
            if (ddtForRebuild != null)
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
            if (ddtForRebuild != null)
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
            if (ddtForRebuild != null)
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
            if (ddtForRebuild != null)
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
                    var targetChar = GetLayerTargetCharacter();
                    if (targetChar != null)
                    {
                        Plugin.DragAndDropTextures?.InjectFilesAndRebuild(
                            files,
                            new KeyValuePair<string, Dalamud.Game.ClientState.Objects.Types.ICharacter>(targetChar.Name.TextValue, targetChar),
                            PenumbraAndGlamourerHelpers.BodyDragPart.Body);
                    }
                }
            },
            0, null, true);
    }

    private void DrawWornGearQuickEdit()
    {
        var ddt = Plugin.DragAndDropTextures;
        var targetChar = GetLayerTargetCharacter();
        if (ddt == null || targetChar == null) return;

        ImGui.TextWrapped(Translator.LocalizeUI("Pull texture paths from gear the layer target is wearing. Each slot becomes an editable layer like body/face."));
        ImGui.Spacing();

        if (ImGui.Button(Translator.LocalizeUI("Scan Worn Gear")))
        {
            ddt.RefreshWornGearCache(targetChar);
        }

        ImGui.SameLine();
        if (ImGui.Button(Translator.LocalizeUI("Import All Slots")))
        {
            ddt.RefreshWornGearCache(targetChar);
            foreach (var piece in ddt.CachedWornGear)
                ddt.ImportWornGearSlot(piece, targetChar);
        }

        if (ddt.CachedWornGear == null || ddt.CachedWornGear.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Translator.LocalizeUI("No gear textures resolved yet. Click Scan while the target is wearing items (not Emperor's New)."));
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
                string charName = targetChar.Name.TextValue;
                string layerKey = charName + "_gear_" + piece.SlotKey + (string.IsNullOrEmpty(piece.MaterialName) ? "" : "_" + piece.MaterialName);
                var textureHistory = ddt.TextureCollectionHistory[_collectionId];
                bool hasLayer = textureHistory != null && textureHistory.ContainsKey(layerKey) && textureHistory[layerKey].Count > 0;
                if (!hasLayer)
                {
                    if (ImGui.Button(Translator.LocalizeUI("Import") + "##wg_" + btnIdSuffix))
                        ddt.ImportWornGearSlot(piece, targetChar);
                }
                if (hasLayer)
                {
                    ImGui.SameLine();
                    ImGui.LabelText("##importedLabel", "  Imported already!");
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
        if (ddt == null || string.IsNullOrEmpty(_collectionId)) return;
        if (!ddt.TextureCollectionHistory.TryGetValue(_collectionId, out var textureHistory) || textureHistory == null) return;

        DrawLayerTargetSelector();

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

    private void DrawPenumbraFoundMods()
    {
        ImGui.Spacing();
        ImGui.TextWrapped(Translator.LocalizeUI("Proteus overlay mods discovered from Penumbra. Use Colors to edit the full 1–16 color table (index maps use multiple rows; overlays without an index mainly use row 16). Simple Tint/Emissive are optional extras on top of baked rows."));
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var ddt = Plugin.DragAndDropTextures;
        if (ddt == null) return;
        if (!DragAndDropTexturing.Overlays.AdvancedOverlayParser.ActiveOverlays.ContainsKey(_collectionId))
        {
            DragAndDropTexturing.Overlays.AdvancedOverlayParser.ActiveOverlays[_collectionId] = new List<Overlays.ResolvedAdvancedOverlay>();
        }
        var overlays = DragAndDropTexturing.Overlays.AdvancedOverlayParser.ActiveOverlays[_collectionId];
        if (overlays == null || overlays.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Translator.LocalizeUI("No Penumbra mod overlays currently active/detected."));
            return;
        }

        if (!Plugin.Configuration.CollectionSortedPenumbraOverlayTints.ContainsKey(_collectionId))
            Plugin.Configuration.CollectionSortedPenumbraOverlayTints[_collectionId] = new Dictionary<string, Vector4>();
        if (!Plugin.Configuration.CollectionSortedPenumbraOverlayGlowTints.ContainsKey(_collectionId))
            Plugin.Configuration.CollectionSortedPenumbraOverlayGlowTints[_collectionId] = new Dictionary<string, Vector4>();
        if (Plugin.Configuration.CollectionSortedPenumbraOverlayColorRows == null)
            Plugin.Configuration.CollectionSortedPenumbraOverlayColorRows = new Dictionary<string, Dictionary<string, List<AdvancedColorTableRow>>>();
        if (!Plugin.Configuration.CollectionSortedPenumbraOverlayColorRows.ContainsKey(_collectionId))
            Plugin.Configuration.CollectionSortedPenumbraOverlayColorRows[_collectionId] = new Dictionary<string, List<AdvancedColorTableRow>>();

        bool changed = false;
        string rebuildCategory = null;

        if (ImGui.BeginTable("PenumbraFoundModsTable", 7, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn(Translator.LocalizeUI("Mod Name"), ImGuiTableColumnFlags.WidthFixed, 200);
            ImGui.TableSetupColumn(Translator.LocalizeUI("Part"), ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn(Translator.LocalizeUI("UV Type"), ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn(Translator.LocalizeUI("UV Preview"), ImGuiTableColumnFlags.WidthFixed, 180);
            ImGui.TableSetupColumn(Translator.LocalizeUI("Colors"), ImGuiTableColumnFlags.WidthFixed, 70);
            ImGui.TableSetupColumn(Translator.LocalizeUI("Tint"), ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableSetupColumn(Translator.LocalizeUI("Emissive"), ImGuiTableColumnFlags.WidthFixed, 60);
            ImGui.TableHeadersRow();

            for (int i = 0; i < overlays.Count; i++)
            {
                var overlay = overlays[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.Text(overlay.ModName);

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

                //ImGui.Text(fileName);
                //if (!string.IsNullOrEmpty(overlay.DiffusePath) && ImGui.IsItemHovered())
                //{
                //    ImGui.SetTooltip(overlay.DiffusePath);
                //}

                ImGui.TableNextColumn();
                string overlayKey = ProteusColorTableHelper.GetOverlayKey(overlay);
                if (!string.IsNullOrEmpty(overlayKey))
                {
                    bool editing = _penumbraColorEditorOverlayKey == overlayKey;
                    if (ImGui.Button(editing ? "Close##" + i : "Edit##" + i))
                    {
                        _penumbraColorEditorOverlayKey = editing ? null : overlayKey;
                        if (!editing)
                            EnsurePenumbraColorRowsInitialized(overlay, overlayKey);
                    }
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(Translator.LocalizeUI("Edit Proteus color table rows 1–16"));
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "N/A");
                }

                ImGui.TableNextColumn();
                // Optional extra tint (rows are baked separately)
                if (!string.IsNullOrEmpty(overlayKey))
                {
                    Vector4 col = Vector4.One;
                    if (Plugin.Configuration.CollectionSortedPenumbraOverlayTints[_collectionId].TryGetValue(overlayKey, out var savedCol)
                        && (savedCol.X != 0 || savedCol.Y != 0 || savedCol.Z != 0 || savedCol.W != 0))
                    {
                        col = savedCol;
                    }

                    ImGui.SetNextItemWidth(55);
                    if (ImGui.ColorEdit4($"##overlaytint_{i}", ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview))
                    {
                        Plugin.Configuration.CollectionSortedPenumbraOverlayTints[_collectionId][overlayKey] = col;
                        Plugin.Configuration.Save();
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Translator.LocalizeUI("Extra multiplier applied after color rows bake"));
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        _pendingTintRebuildCategory = overlay.TargetBodyPart;
                    }

                    ImGui.TableNextColumn();
                    Vector4 glowCol = Vector4.One;
                    if (Plugin.Configuration.CollectionSortedPenumbraOverlayGlowTints[_collectionId].TryGetValue(overlayKey, out var savedGlowCol)
                        && (savedGlowCol.X != 0 || savedGlowCol.Y != 0 || savedGlowCol.Z != 0))
                    {
                        glowCol = savedGlowCol;
                    }

                    ImGui.SetNextItemWidth(55);
                    if (ImGui.ColorEdit4($"##overlayglowtint_{i}", ref glowCol, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.NoAlpha))
                    {
                        glowCol.W = 1.0f;
                        Plugin.Configuration.CollectionSortedPenumbraOverlayGlowTints[_collectionId][overlayKey] = glowCol;
                        Plugin.Configuration.Save();
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Translator.LocalizeUI("Extra glow color multiplier after emissive bake"));
                    if (ImGui.IsItemDeactivatedAfterEdit())
                    {
                        _pendingTintRebuildCategory = overlay.TargetBodyPart;
                    }
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "N/A");
                    ImGui.TableNextColumn();
                    ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), "N/A");
                }
            }

            ImGui.EndTable();
        }

        if (!string.IsNullOrEmpty(_penumbraColorEditorOverlayKey))
        {
            var editedOverlay = overlays.Find(o => ProteusColorTableHelper.GetOverlayKey(o) == _penumbraColorEditorOverlayKey);
            if (editedOverlay != null)
            {
                if (DrawPenumbraColorRowEditor(editedOverlay, _penumbraColorEditorOverlayKey, ref rebuildCategory))
                    changed = true;
            }
            else
            {
                _penumbraColorEditorOverlayKey = null;
            }
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

    private void EnsurePenumbraColorRowsInitialized(ResolvedAdvancedOverlay overlay, string overlayKey)
    {
        var store = Plugin.Configuration.CollectionSortedPenumbraOverlayColorRows[_collectionId];
        if (store.ContainsKey(overlayKey))
            return;

        store[overlayKey] = ProteusColorTableHelper.CreateEditableRowSet(overlay.ColorTableRows);
        Plugin.Configuration.Save();
    }

    /// <returns>True if a rebuild should run.</returns>
    private bool DrawPenumbraColorRowEditor(ResolvedAdvancedOverlay overlay, string overlayKey, ref string rebuildCategory)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Text($"{overlay.ModName}, {Translator.LocalizeUI("Color Table Rows")}");
        if (string.IsNullOrEmpty(overlay.IndexPath))
        {
            ImGui.TextColored(new Vector4(0.7f, 0.7f, 0.4f, 1f),
                Translator.LocalizeUI("No index texture, row 16 is the main recolor row."));
        }
        else
        {
            ImGui.TextColored(new Vector4(0.6f, 0.8f, 0.6f, 1f),
                Translator.LocalizeUI("Index texture active, red selects row (÷17), green blends A/B."));
        }

        var store = Plugin.Configuration.CollectionSortedPenumbraOverlayColorRows[_collectionId];
        if (!store.TryGetValue(overlayKey, out var rows) || rows == null)
        {
            rows = ProteusColorTableHelper.CreateEditableRowSet(overlay.ColorTableRows);
            store[overlayKey] = rows;
        }

        bool rowChanged = false;

        if (ImGui.Button(Translator.LocalizeUI("Reset to mod defaults")))
        {
            store[overlayKey] = ProteusColorTableHelper.CreateEditableRowSet(overlay.ColorTableRows);
            Plugin.Configuration.Save();
            rowChanged = true;
        }
        ImGui.SameLine();
        if (ImGui.Button(Translator.LocalizeUI("Clear overrides")))
        {
            store.Remove(overlayKey);
            Plugin.Configuration.Save();
            rowChanged = true;
        }

        ImGui.Spacing();

        if (ImGui.BeginChild("PenumbraColorRowsEditor", new Vector2(0, 320), true))
        {
            if (ImGui.BeginTable("PenumbraColorRowsTable", 3, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
            {
                ImGui.TableSetupColumn(Translator.LocalizeUI("Row"), ImGuiTableColumnFlags.WidthFixed, 40);
                ImGui.TableSetupColumn(Translator.LocalizeUI("Sub A, Diffuse / Emissive / Opacity"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableSetupColumn(Translator.LocalizeUI("Sub B, Diffuse / Emissive / Opacity"), ImGuiTableColumnFlags.WidthStretch);
                ImGui.TableHeadersRow();

                bool hasIndex = !string.IsNullOrEmpty(overlay.IndexPath);
                for (int ri = 0; ri < rows.Count; ri++)
                {
                    var row = rows[ri];
                    bool authored = ProteusColorTableHelper.RowIsAuthoredInMetadata(row.Row, overlay.ColorTableRows);
                    bool isRow16 = row.Row == 16;
                    if (!hasIndex && !authored && !isRow16)
                        continue;

                    ImGui.TableNextRow();
                    if (!authored && !isRow16)
                        ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.55f, 0.55f, 0.55f, 1f));

                    ImGui.TableNextColumn();
                    ImGui.Text(row.Row.ToString());

                    ImGui.TableNextColumn();
                    rowChanged |= DrawPenumbraColorSubRowEditor(row, true, ri);

                    ImGui.TableNextColumn();
                    rowChanged |= DrawPenumbraColorSubRowEditor(row, false, ri);

                    if (!authored && !isRow16)
                        ImGui.PopStyleColor();
                }

                ImGui.EndTable();
            }
        }
        ImGui.EndChild();

        if (rowChanged)
        {
            Plugin.Configuration.Save();
            rebuildCategory = overlay.TargetBodyPart;
            return true;
        }

        return false;
    }

    private bool DrawPenumbraColorSubRowEditor(AdvancedColorTableRow row, bool subRowA, int rowIndex)
    {
        var sub = subRowA ? row.SubRowA : row.SubRowB;
        if (sub == null)
        {
            sub = ProteusColorTableHelper.CreateDefaultSubRow();
            if (subRowA) row.SubRowA = sub;
            else row.SubRowB = sub;
        }

        bool changed = false;
        string prefix = $"##proteusrow_{rowIndex}_{(subRowA ? "a" : "b")}";

        Vector4 tint4 = ProteusColorTableHelper.SubRowToTint(sub);
        Vector3 tint = new Vector3(tint4.X, tint4.Y, tint4.Z);
        ImGui.SetNextItemWidth(90);
        if (ImGui.ColorEdit3(prefix + "_diff", ref tint, ImGuiColorEditFlags.NoInputs))
        {
            ProteusColorTableHelper.TintToSubRowDiffuse(ref sub, new Vector4(tint.X, tint.Y, tint.Z, 1f));
            changed = true;
        }
        ImGui.SameLine();

        float emissive = sub.Emissive;
        ImGui.SetNextItemWidth(80);
        if (ImGui.SliderFloat(prefix + "_em", ref emissive, 0f, 1f, "E %.2f"))
        {
            sub.Emissive = emissive;
            changed = true;
        }
        ImGui.SameLine();

        int opacity = sub.Opacity;
        ImGui.SetNextItemWidth(100);
        if (ImGui.SliderInt(prefix + "_op", ref opacity, -100, 100, "Op %d"))
        {
            sub.Opacity = opacity;
            changed = true;
        }

        if (ImGui.IsItemDeactivatedAfterEdit())
            changed = true;

        return changed;
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
        list.Sort((a, b) =>
        {
            if (a.Key == 0) return -1;
            if (b.Key == 0) return 1;
            return a.Value.CompareTo(b.Value);
        });
        _jobIdsArray = list.Select(kv => kv.Key).ToArray();
        _jobNamesArray = list.Select(kv => kv.Value).ToArray();
    }

    private void DrawCombinedLayersTab(DragAndDropTextureWindow ddt)
    {
        var textureHistory = ddt.TextureCollectionHistory[_collectionId];
        var textureHistoryTints = ddt.TextureCollectionHistoryTints[_collectionId];
        if (ddt.TextureCollectionHistoryBlendModes == null)
            ddt.TextureCollectionHistoryBlendModes = new Dictionary<string, Dictionary<string, List<int>>>();
        if (!ddt.TextureCollectionHistoryBlendModes.ContainsKey(_collectionId))
            ddt.TextureCollectionHistoryBlendModes[_collectionId] = new Dictionary<string, List<int>>();
        var textureHistoryBlendModes = ddt.TextureCollectionHistoryBlendModes[_collectionId];
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
            foreach (var kvp in textureHistory)
                preset.TextureHistory[kvp.Key] = new List<string>(kvp.Value);
            if (textureHistoryTints != null)
            {
                foreach (var kvp in textureHistoryTints)
                    preset.TextureHistoryTints[kvp.Key] = new List<System.Numerics.Vector4>(kvp.Value);
            }
            if (textureHistoryBlendModes != null)
            {
                foreach (var kvp in textureHistoryBlendModes)
                    preset.TextureHistoryBlendModes[kvp.Key] = new List<int>(kvp.Value);
            }
            presets.Add(preset);
            Plugin.Configuration.Save();
            _selectedPresetIndex = presets.Count - 1;
        }

        ImGui.EndChild();
        ImGui.SameLine();

        ImGui.BeginChild("PresetDetailsColumn", new Vector2(0, 0), false);
        var targetHistory = _selectedPresetIndex == -1 ? textureHistory : presets[_selectedPresetIndex].TextureHistory;
        var targetTints = _selectedPresetIndex == -1 ? textureHistoryTints : presets[_selectedPresetIndex].TextureHistoryTints;
        var targetBlendModes = _selectedPresetIndex == -1 ? textureHistoryBlendModes : presets[_selectedPresetIndex].TextureHistoryBlendModes;

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
                ApplyPreset(preset, Plugin.SafeGameObjectManager.LocalPlayer);
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

        string layerFilterName = _selectedPresetIndex == -1 ? GetLayerTargetCharacterName() : null;
        var keys = targetHistory.Keys.Where(k => targetHistory[k].Count > 0 && LayerKeyMatchesTarget(k, layerFilterName)).ToList();
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
                        Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_body");
                    }
                    if (ImGui.Selectable(Translator.LocalizeUI("Face")))
                    {
                        Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_face");
                    }
                    if (ImGui.Selectable(Translator.LocalizeUI("Hair")))
                    {
                        Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_hair");
                    }
                    if (ImGui.Selectable(Translator.LocalizeUI("Tail")))
                    {
                        Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_tail");
                    }
                    if (ImGui.Selectable(Translator.LocalizeUI("Minion")))
                    {
                        Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_minion_body");
                    }
                    if (ImGui.Selectable(Translator.LocalizeUI("Mount")))
                    {
                        Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_mount_body");
                    }
                    ImGui.Separator();
                    if (ImGui.Selectable(Translator.LocalizeUI("Outfit (Top)")))
                    {
                        Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_gear_top");
                    }
                    if (ImGui.Selectable(Translator.LocalizeUI("Outfit (Bottom)")))
                    {
                        Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_gear_bottom");
                    }
                    if (ImGui.Selectable(Translator.LocalizeUI("Outfit (Hands)")))
                    {
                        Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_gear_hands");
                    }
                    if (ImGui.Selectable(Translator.LocalizeUI("Outfit (Feet)")))
                    {
                        Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_gear_feet");
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
                    Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_body");
                }
                if (ImGui.Selectable(Translator.LocalizeUI("Face")))
                {
                    Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_face");
                }
                if (ImGui.Selectable(Translator.LocalizeUI("Hair")))
                {
                    Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_hair");
                }
                if (ImGui.Selectable(Translator.LocalizeUI("Tail")))
                {
                    Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_tail");
                }
                if (ImGui.Selectable(Translator.LocalizeUI("Minion")))
                {
                    Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_minion_body");
                }
                if (ImGui.Selectable(Translator.LocalizeUI("Mount")))
                {
                    Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_mount_body");
                }
                ImGui.Separator();
                if (ImGui.Selectable(Translator.LocalizeUI("Outfit (Top)")))
                {
                    Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_gear_top");
                }
                if (ImGui.Selectable(Translator.LocalizeUI("Outfit (Bottom)")))
                {
                    Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_gear_bottom");
                }
                if (ImGui.Selectable(Translator.LocalizeUI("Outfit (Hands)")))
                {
                    Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_gear_hands");
                }
                if (ImGui.Selectable(Translator.LocalizeUI("Outfit (Feet)")))
                {
                    Plugin.OpenPaintWindow(GetLayerTargetCharacter(), null, GetLayerTargetCharacterName() + "_gear_feet");
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
                var blendList = targetBlendModes != null && targetBlendModes.ContainsKey(key) ? targetBlendModes[key] : null;
                if (blendList == null)
                {
                    if (_selectedPresetIndex == -1)
                    {
                        textureHistoryBlendModes[key] = new List<int>();
                        blendList = textureHistoryBlendModes[key];
                    }
                    else
                    {
                        var preset = presets[_selectedPresetIndex];
                        if (preset.TextureHistoryBlendModes == null)
                            preset.TextureHistoryBlendModes = new Dictionary<string, List<int>>();
                        preset.TextureHistoryBlendModes[key] = new List<int>();
                        blendList = preset.TextureHistoryBlendModes[key];
                    }
                }
                while (blendList.Count < list.Count)
                    blendList.Add(0);
                while (blendList.Count > list.Count)
                    blendList.RemoveAt(blendList.Count - 1);

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
                    if (blendList != null) blendList.Clear();
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

                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Export to Proteus") + "##proteus_" + key))
                {
                    ExportCategoryToProteus(key, list);
                }
                if (ImGui.IsItemHovered())
                    ImGui.SetTooltip(Translator.LocalizeUI("Creates a Proteus-ready Penumbra .pmp overlay mod for this body or equipped gear material."));

                if (_selectedPresetIndex == -1)
                {
                    ImGui.SameLine();
                    if (ImGui.Button(Translator.LocalizeUI("Re-Export") + "##reexport_" + key))
                    {
                        ddt.RebuildCategory(key, false);
                        Plugin.Chat?.Print($"[Drag And Drop Texturing] Re-exporting: {key}");
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip(Translator.LocalizeUI("Re-exports this category's textures. Also available via /ddt export for all categories."));
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
                        ImGui.SetNextItemWidth(ImGui.GetWindowWidth() - 490);
                    }
                    else
                    {
                        ImGui.SetNextItemWidth(ImGui.GetWindowWidth() - 440);
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
                                    if (blendList != null && i < blendList.Count) blendList[i] = 0;
                                    changed = true;
                                }
                            }
                        }
                    }
                    if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;

                    if (ImGui.Button(Translator.LocalizeUI("Up") + "##" + key + i) && i > 0)
                    {
                        var temp = list[i - 1]; list[i - 1] = list[i]; list[i] = temp;
                        if (tintList != null && i < tintList.Count && i - 1 < tintList.Count)
                        {
                            var tempTint = tintList[i - 1]; tintList[i - 1] = tintList[i]; tintList[i] = tempTint;
                        }
                        if (blendList != null && i < blendList.Count && i - 1 < blendList.Count)
                        {
                            var tempBlend = blendList[i - 1]; blendList[i - 1] = blendList[i]; blendList[i] = tempBlend;
                        }
                        changed = true;
                    }

                    ImGui.SameLine();
                    if (ImGui.Button(Translator.LocalizeUI("Down") + "##" + key + i) && i < list.Count - 1)
                    {
                        var temp = list[i + 1]; list[i + 1] = list[i]; list[i] = temp;
                        if (tintList != null && i < tintList.Count && i + 1 < tintList.Count)
                        {
                            var tempTint = tintList[i + 1]; tintList[i + 1] = tintList[i]; tintList[i] = tempTint;
                        }
                        if (blendList != null && i < blendList.Count && i + 1 < blendList.Count)
                        {
                            var tempBlend = blendList[i + 1]; blendList[i + 1] = blendList[i]; blendList[i] = tempBlend;
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
                        if (blendList != null && i < blendList.Count) blendList.RemoveAt(i);
                        removed = true;
                        changed = true;
                    }
                    ImGui.EndDisabled();
                    ImGui.PopStyleColor(3);
                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(Translator.LocalizeUI("Hold SHIFT to Remove"));

                    if (removed) { i--; continue; }

                    if (blendList != null && i < blendList.Count)
                    {
                        int blendMode = blendList[i];
                        ImGui.SameLine();
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(Translator.LocalizeUI("Blend"));
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(110);
                        if (ImGui.BeginCombo("##blend_" + key + i, Translator.LocalizeUI(FFXIVLooseTextureCompiler.ImageProcessing.LayerBlendModeNames.GetName(blendMode))))
                        {
                            for (int b = 0; b < FFXIVLooseTextureCompiler.ImageProcessing.LayerBlendModeNames.All.Length; b++)
                            {
                                string blendLabel = Translator.LocalizeUI(FFXIVLooseTextureCompiler.ImageProcessing.LayerBlendModeNames.All[b]);
                                if (ImGui.Selectable(blendLabel, b == blendMode))
                                {
                                    blendList[i] = b;
                                    changed = true;
                                }
                                if (ImGui.IsItemHovered())
                                {
                                    ImGui.SetTooltip(Translator.LocalizeUI(FFXIVLooseTextureCompiler.ImageProcessing.LayerBlendModeDescriptions.All[b]));
                                }
                            }
                            ImGui.EndCombo();
                        }
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip(Translator.LocalizeUI(FFXIVLooseTextureCompiler.ImageProcessing.LayerBlendModeDescriptions.GetDescription(blendMode)));
                        }
                        if (ImGui.IsItemDeactivatedAfterEdit()) changed = true;
                    }

                    if (tintList != null && i < tintList.Count)
                    {
                        System.Numerics.Vector4 col = tintList[i];
                        ImGui.SameLine();
                        ImGui.AlignTextToFramePadding();
                        ImGui.Text(Translator.LocalizeUI("Tint"));
                        ImGui.SameLine();
                        ImGui.SetNextItemWidth(40);
                        if (ImGui.ColorEdit4("##tint_" + key + i, ref col, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaBar | ImGuiColorEditFlags.AlphaPreview))
                        {
                            tintList[i] = col;
                        }
                        if (ImGui.IsItemHovered())
                        {
                            ImGui.SetTooltip(Translator.LocalizeUI("Multiplies this layer's color and opacity. White = no change."));
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
                        else if (lowerPath.Contains("tbse") || lowerPath.Contains("the body se") || lowerPath.Contains("hrbody") || lowerPath.Contains("swole") || lowerPath.Contains("hunk") || lowerPath.Contains("the body") ||
                                 (!lowerPath.Contains("obj/face") && !lowerPath.Contains("fac_") && !lowerPath.Contains("/face/") && !lowerPath.Contains("_face") &&
                                  (lowerPath.Contains("_b_d") || lowerPath.Contains("_b_n") || lowerPath.Contains("_b_s") || lowerPath.Contains("_b_m") || lowerPath.Contains("b_d.tex") || lowerPath.Contains("b_n.tex") || lowerPath.Contains("b_s.tex") || lowerPath.Contains("b_m.tex"))))
                            canEdit = Plugin.IsBodyAvailable("tbse");

                        if (!canEdit) ImGui.BeginDisabled();
                        if (ImGui.Button(Translator.LocalizeUI("Edit") + "##" + key + i))
                        {
                            Plugin.OpenPaintWindow(GetLayerTargetCharacter(), path, key);
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

    public void ApplyPreset(ActiveLayerPreset preset, IGameObject gameObject)
    {
        if (Plugin.DragAndDropTextures == null) return;
        var collection = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(gameObject.ObjectIndex);
        var collectionId = collection.EffectiveCollection.Id.ToString();
        if (!Plugin.DragAndDropTextures.TextureCollectionHistory.ContainsKey(collectionId))
        {
            Plugin.DragAndDropTextures.TextureCollectionHistory[collectionId] = new Dictionary<string, List<string>>();
        }
        if (!Plugin.DragAndDropTextures.TextureCollectionHistoryTints.ContainsKey(collectionId))
        {
            Plugin.DragAndDropTextures.TextureCollectionHistoryTints[collectionId] = new Dictionary<string, List<Vector4>>();
        }
        if (!Plugin.DragAndDropTextures.TextureCollectionHistoryBlendModes.ContainsKey(collectionId))
        {
            Plugin.DragAndDropTextures.TextureCollectionHistoryBlendModes[collectionId] = new Dictionary<string, List<int>>();
        }
        var textureHistory = Plugin.DragAndDropTextures.TextureCollectionHistory[collectionId];
        var textureHistoryTints = Plugin.DragAndDropTextures.TextureCollectionHistoryTints[collectionId];
        var textureHistoryBlendModes = Plugin.DragAndDropTextures.TextureCollectionHistoryBlendModes[collectionId];
        textureHistory.Clear();
        if (textureHistoryTints != null)
            textureHistoryTints.Clear();
        if (textureHistoryBlendModes != null)
            textureHistoryBlendModes.Clear();

        foreach (var kvp in preset.TextureHistory)
        {
            textureHistory[kvp.Key] = new List<string>(kvp.Value);
        }
        if (preset.TextureHistoryTints != null && textureHistoryTints != null)
        {
            foreach (var kvp in preset.TextureHistoryTints)
            {
                textureHistoryTints[kvp.Key] = new List<Vector4>(kvp.Value);
            }
        }
        if (preset.TextureHistoryBlendModes != null && textureHistoryBlendModes != null)
        {
            foreach (var kvp in preset.TextureHistoryBlendModes)
            {
                textureHistoryBlendModes[kvp.Key] = new List<int>(kvp.Value);
            }
        }

        Plugin.Configuration.CollectionSortedTextureHistory = Plugin.DragAndDropTextures.TextureCollectionHistory;
        Plugin.Configuration.CollectionSortedTextureHistoryTints = Plugin.DragAndDropTextures.TextureCollectionHistoryTints;
        Plugin.Configuration.CollectionSortedTextureHistoryBlendModes = Plugin.DragAndDropTextures.TextureCollectionHistoryBlendModes;
        Plugin.Configuration.Save();

        foreach (var category in textureHistory.Keys)
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

    private void ExportCategoryToProteus(string key, System.Collections.Generic.List<string> files)
    {
        Plugin.Chat?.Print("[DragAndDrop] Exporting Proteus mod... Please wait.");

        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                if (!TryGetProteusMaterialPath(key, out var materialGamePath, out var currentBodyType))
                {
                    Plugin.Chat?.Print("[DragAndDrop] Proteus export needs a resolved body or equipped gear material target.");
                    return;
                }

                string exportFolder = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Exports");
                Directory.CreateDirectory(exportFolder);
                string safeName = string.Join("_", key.Split(Path.GetInvalidFileNameChars()));
                string modName = $"Drag And Drop - {safeName}";
                string pmpPath = Path.Combine(exportFolder, $"{safeName}_proteus.pmp");

                if (currentBodyType >= 0 && global::PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory != null)
                {
                    string modDirectory = global::PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
                    LooseTextureCompilerCore.GlobalPathStorage.OriginalBaseDirectory = Path.Combine(modDirectory, "LooseTextureCompilerDLC");
                }

                string tempDirectory = Path.Combine(Path.GetTempPath(), "DragAndDropTexturing", Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(tempDirectory);
                var sourceOverlays = CreateProteusOverlays(files, currentBodyType, tempDirectory);
                if (sourceOverlays.Count == 0)
                {
                    Plugin.Chat?.Print("[DragAndDrop] Error: No valid image files found to export.");
                    return;
                }

                try
                {
                    if (File.Exists(pmpPath)) File.Delete(pmpPath);
                    using (var zip = ZipFile.Open(pmpPath, ZipArchiveMode.Create))
                    {
                        var identifier = Guid.NewGuid().ToString();
                        var penumbraMeta = new JObject
                        {
                            ["FileVersion"] = 4,
                            ["Identifier"] = identifier,
                            ["Name"] = modName,
                            ["Author"] = "Drag And Drop Texturing",
                            ["Description"] = "Overlay exported by Drag And Drop Texturing.",
                            ["Version"] = "1.0",
                            ["Website"] = "",
                            ["ModTags"] = new JArray("Proteus"),
                            ["DefaultData"] = new JObject { ["Files"] = new JObject(), ["FileSwaps"] = new JObject(), ["Manipulations"] = new JArray() },
                            ["Groups"] = new JArray(),
                        };
                        WriteZipJson(zip, "meta.json", penumbraMeta);

                        var overlays = new JArray();
                        if (currentBodyType >= 0)
                        {
                            foreach (var target in GetProteusBodyTargets(currentBodyType, materialGamePath))
                            {
                                overlays.Add(WriteProteusOverlay(zip, sourceOverlays, currentBodyType, target.BodyType, target.MaterialGamePath, tempDirectory, target.Name));
                            }
                        }
                        else
                        {
                            overlays.Add(WriteProteusOverlay(zip, sourceOverlays, -1, -1, materialGamePath, tempDirectory, "overlay"));
                        }

                        var proteusMeta = new JObject
                        {
                            ["FormatVersion"] = 1,
                            ["Name"] = modName,
                            ["Author"] = "Drag And Drop Texturing",
                            ["Overlays"] = overlays,
                            ["ColorTableRows"] = new JArray
                            {
                                new JObject
                                {
                                    ["Row"] = 16,
                                    ["SubRowA"] = new JObject { ["Diffuse"] = "#FFFFFF", ["Emissive"] = 0.0 },
                                },
                            },
                        };
                        WriteZipJson(zip, "Proteus/metadata.json", proteusMeta);
                    }
                }
                finally
                {
                    foreach (var image in sourceOverlays.Values) image.Dispose();
                    try { Directory.Delete(tempDirectory, true); } catch { }
                }

                Plugin.Chat?.Print($"[DragAndDrop] Successfully exported Proteus mod to: {pmpPath}");
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exportFolder,
                    UseShellExecute = true,
                    Verb = "open",
                });
            }
            catch (Exception ex)
            {
                Plugin.Chat?.Print("[DragAndDrop] Failed to export Proteus mod! Check the plugin log for details.");
                Plugin.PluginLog.Error(ex, $"Failed to export category {key} to a Proteus mod");
            }
        });
    }

    private ImageMagick.MagickImage? CreateProteusOverlay(IReadOnlyList<string> files, int targetBodyType, string tempDirectory)
    {
        ImageMagick.MagickImage? overlay = null;
        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) continue;

            string layerPath = ConvertLayerToBodyUv(file, targetBodyType, tempDirectory, i);
            using var layer = new ImageMagick.MagickImage(layerPath);
            overlay ??= new ImageMagick.MagickImage(ImageMagick.MagickColors.Transparent, layer.Width, layer.Height);
            overlay.Composite(layer, ImageMagick.CompositeOperator.Over);
        }
        return overlay;
    }

    private Dictionary<string, ImageMagick.MagickImage> CreateProteusOverlays(IReadOnlyList<string> files, int targetBodyType, string tempDirectory)
    {
        var result = new Dictionary<string, ImageMagick.MagickImage>();
        foreach (var group in files.Where(File.Exists).GroupBy(GetProteusMapType))
        {
            var image = CreateProteusOverlay(group.ToList(), targetBodyType, tempDirectory);
            if (image != null) result[group.Key] = image;
        }
        return result;
    }

    private static string GetProteusMapType(string file)
    {
        var textureSet = new FFXIVLooseTextureCompiler.PathOrganization.TextureSet();
        return ProjectHelper.SortUVTexture(textureSet, file) switch
        {
            FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation.UVMapType.Normal => "Normal",
            FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation.UVMapType.Mask => "Mask",
            _ => "Diffuse",
        };
    }

    private static JObject WriteProteusOverlay(ZipArchive zip, IReadOnlyDictionary<string, ImageMagick.MagickImage> sourceOverlays,
        int sourceBodyType, int targetBodyType, string materialGamePath, string tempDirectory, string prefix)
    {
        var descriptor = new JObject { ["MaterialGamePath"] = materialGamePath };
        foreach (var (mapType, sourceOverlay) in sourceOverlays)
        {
            string filename = $"{prefix}_{mapType.ToLowerInvariant()}.png";
            using var converted = sourceBodyType >= 0
                ? ConvertProteusOverlay(sourceOverlay, sourceBodyType, targetBodyType, tempDirectory, $"{prefix}_{mapType.ToLowerInvariant()}")
                : new ImageMagick.MagickImage(sourceOverlay.ToByteArray(ImageMagick.MagickFormat.Png));
            using var stream = zip.CreateEntry($"Proteus/{filename}", CompressionLevel.NoCompression).Open();
            converted.Write(stream, ImageMagick.MagickFormat.Png);
            descriptor[mapType] = filename;
        }
        return descriptor;
    }

    private bool TryGetProteusMaterialPath(string key, out string materialGamePath, out int bodyType)
    {
        materialGamePath = "";
        bodyType = -1;
        if (Plugin.DragAndDropTextures?.GearCategoryMeta.TryGetValue(key, out var gear) == true
            && !string.IsNullOrWhiteSpace(gear.InternalMaterialPath))
        {
            materialGamePath = gear.InternalMaterialPath;
            return true;
        }

        if (!key.EndsWith("_body", StringComparison.OrdinalIgnoreCase)) return false;

        var character = Plugin.SafeGameObjectManager.LocalPlayer;
        if (character == null || global::PenumbraAndGlamourerIpcWrapper.Instance.GetStateBase64 == null) return false;

        var state = global::PenumbraAndGlamourerIpcWrapper.Instance.GetStateBase64.Invoke(character.ObjectIndex);
        var customization = PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.CharacterCustomization.ReadCustomization(state.Item2);
        var collectionId = global::PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;
        bodyType = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(
            collectionId, customization.Customize.Gender.Value, out _, Plugin);
        if (bodyType < 0) return false;

        int race = RaceInfo.SubRaceToMainRace(customization.Customize.Clan.Value - 1);
        var textureSet = ProjectHelper.CreateBodyTextureSet(customization.Customize.Gender.Value, bodyType, race,
            customization.Customize.TailShape.Value - 1, false);
        materialGamePath = textureSet.InternalMaterialPath;
        return !string.IsNullOrWhiteSpace(materialGamePath);
    }

    private IEnumerable<(string Name, string MaterialGamePath, int BodyType)> GetProteusBodyTargets(int currentBodyType, string currentMaterial)
    {
        var character = Plugin.SafeGameObjectManager.LocalPlayer;
        var state = global::PenumbraAndGlamourerIpcWrapper.Instance.GetStateBase64.Invoke(character.ObjectIndex);
        var customization = PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.CharacterCustomization.ReadCustomization(state.Item2);
        int gender = customization.Customize.Gender.Value;
        int race = RaceInfo.SubRaceToMainRace(customization.Customize.Clan.Value - 1);
        int tail = customization.Customize.TailShape.Value - 1;
        int[] bodyTypes = gender == 1 ? new[] { 1, 2, 0 } : new[] { 3, 0 };

        foreach (int targetBodyType in bodyTypes)
        {
            string name = targetBodyType switch { 0 => "vanilla", 1 => "bibo", 2 => "gen3", 3 => "tbse", _ => "overlay" };
            string material = targetBodyType == currentBodyType
                ? currentMaterial
                : ProjectHelper.CreateBodyTextureSet(gender, targetBodyType, race, tail, false).InternalMaterialPath;
            yield return (name, material, targetBodyType);
        }
    }

    private string ConvertLayerToBodyUv(string sourcePath, int targetBodyType, string tempDirectory, int index)
    {
        if (targetBodyType < 0 || targetBodyType == 3) return sourcePath;

        int sourceBodyType = DetectFemaleBodyUv(sourcePath);
        if (sourceBodyType == targetBodyType || sourceBodyType < 0) return sourcePath;
        string outputPath = Path.Combine(tempDirectory, $"layer_{index}_{targetBodyType}.png");
        ConvertBodyUv(sourcePath, outputPath, sourceBodyType, targetBodyType);
        return outputPath;
    }

    private static ImageMagick.MagickImage ConvertProteusOverlay(ImageMagick.MagickImage overlay, int sourceBodyType, int targetBodyType, string tempDirectory, string name)
    {
        if (sourceBodyType == targetBodyType || sourceBodyType == 3 || targetBodyType == 3)
            return new ImageMagick.MagickImage(overlay.ToByteArray(ImageMagick.MagickFormat.Png));

        string sourcePath = Path.Combine(tempDirectory, $"source_{name}.png");
        string outputPath = Path.Combine(tempDirectory, $"{name}.png");
        overlay.Write(sourcePath, ImageMagick.MagickFormat.Png);
        ConvertBodyUv(sourcePath, outputPath, sourceBodyType, targetBodyType);
        return new ImageMagick.MagickImage(outputPath);
    }

    private static int DetectFemaleBodyUv(string path)
    {
        string fileName = Path.GetFileNameWithoutExtension(path).ToLowerInvariant();
        if (fileName.Contains("bibo") || fileName.Contains("b+")) return 1;
        if (fileName.Contains("gen3") || System.Text.RegularExpressions.Regex.IsMatch(fileName, @"(^|[^a-z])eve([^a-z]|$)")
            || fileName.Contains("exqb") || fileName.Contains("pythia") || fileName.Contains("gaia")) return 2;

        return FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation.FemaleBodyUVClassifier(path) switch
        {
            FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation.BodyUVType.Bibo => 1,
            FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation.BodyUVType.Gen3 => 2,
            FFXIVLooseTextureCompiler.ImageProcessing.ImageManipulation.BodyUVType.Gen2 => 0,
            _ => -1,
        };
    }

    private static void ConvertBodyUv(string sourcePath, string outputPath, int sourceBodyType, int targetBodyType)
    {
        if (sourceBodyType == 1 && targetBodyType == 2) FFXIVLooseTextureCompiler.FastUVTransfer.BiboToGen3(sourcePath, outputPath);
        else if (sourceBodyType == 1 && targetBodyType == 0) FFXIVLooseTextureCompiler.FastUVTransfer.BiboToGen2(sourcePath, outputPath);
        else if (sourceBodyType == 2 && targetBodyType == 1) FFXIVLooseTextureCompiler.FastUVTransfer.Gen3ToBibo(sourcePath, outputPath);
        else if (sourceBodyType == 2 && targetBodyType == 0) FFXIVLooseTextureCompiler.FastUVTransfer.Gen3ToGen2(sourcePath, outputPath);
        else if (sourceBodyType == 0 && targetBodyType == 1) FFXIVLooseTextureCompiler.FastUVTransfer.Gen2ToBibo(sourcePath, outputPath);
        else if (sourceBodyType == 0 && targetBodyType == 2) FFXIVLooseTextureCompiler.FastUVTransfer.Gen2ToGen3(sourcePath, outputPath);
    }

    private static void WriteZipJson(ZipArchive zip, string entryName, JObject value)
    {
        using var writer = new StreamWriter(zip.CreateEntry(entryName, CompressionLevel.Optimal).Open());
        writer.Write(value.ToString(Formatting.Indented));
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
    private int _selectedOnionModIndex = 0;

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

        if (ImGui.Button(Translator.LocalizeUI("Copy GPU Error Log Clipboard")))
        {
            if (_cachedErrorLog != null)
                ImGui.SetClipboardText(_cachedErrorLog);
        }
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

        ImGui.SameLine();
        if (ImGui.Button(Translator.LocalizeUI("Import .clmp File")))
        {
            _fileDialogManager.OpenFileDialog(
                Translator.LocalizeUI("Select a contextual layer preset"),
                "Contextual Layer Presets{.clmp}",
                (b, files) =>
                {
                    if (b && files != null && files.Count > 0)
                    {
                        foreach (var f in files)
                        {
                            if (f.EndsWith(".clmp", StringComparison.OrdinalIgnoreCase))
                            {
                                Plugin.ContextualLayerManager.ImportClmpLayersFromFile(f);
                            }
                        }
                        if (Plugin.ContextualLayerManager.ContextualLayers.Count > 0)
                            _selectedContextualLayerIndex = Plugin.ContextualLayerManager.ContextualLayers.Count - 1;
                    }
                },
                0, null, true);
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
                    Plugin.ContextualLayerManager.ImportClmpLayersFromFile(f);
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
    private string _collectionId;

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

                // .ltct files are XOR-encoded, decode to temp PNG first
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

    private void DrawOnionModsTab()
    {
        ImGui.Spacing();
        if (ImGui.Button(Translator.LocalizeUI("Import .omp File")))
        {
            _fileDialogManager.OpenFileDialog(
                Translator.LocalizeUI("Select an Onion Layer Mod package"),
                "Onion Layer Mod{.omp}",
                (b, files) =>
                {
                    if (b && files != null && files.Count > 0)
                    {
                        foreach (var f in files)
                        {
                            if (f.EndsWith(".omp", StringComparison.OrdinalIgnoreCase))
                            {
                                var imported = Plugin.OnionLayerModManager?.ImportFromFile(f, false);
                                if (imported != null && Plugin.OnionLayerModManager != null)
                                {
                                    _selectedOnionModIndex = Plugin.OnionLayerModManager.OnionLayerMods.Count - 1;
                                }
                            }
                        }
                    }
                },
                0, null, true);
        }

        ImGui.SameLine();
        if (ImGui.Button(Translator.LocalizeUI("Open Mods Folder")))
        {
            if (Plugin.OnionLayerModManager != null && Directory.Exists(Plugin.OnionLayerModManager.RootDirectory))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo()
                {
                    FileName = Plugin.OnionLayerModManager.RootDirectory,
                    UseShellExecute = true,
                    Verb = "open"
                });
            }
        }

        ImGui.SameLine();
        if (ImGui.Button(Translator.LocalizeUI("Refresh Mods")))
        {
            Plugin.OnionLayerModManager?.LoadMods();
            if (Plugin.OnionLayerModManager != null && Plugin.OnionLayerModManager.OnionLayerMods.Count > 0)
            {
                _selectedOnionModIndex = Math.Clamp(_selectedOnionModIndex, 0, Plugin.OnionLayerModManager.OnionLayerMods.Count - 1);
            }
        }

        ImGui.Spacing();

        var mgr = Plugin.OnionLayerModManager;
        if (mgr == null || mgr.OnionLayerMods.Count == 0)
        {
            ImGui.TextColored(new Vector4(0.5f, 0.5f, 0.5f, 1f), Translator.LocalizeUI("No Onion layer mods (.omp) currently installed."));
            return;
        }

        if (_selectedOnionModIndex >= mgr.OnionLayerMods.Count)
            _selectedOnionModIndex = Math.Max(0, mgr.OnionLayerMods.Count - 1);

        ImGui.BeginChild("OnionModsList", new Vector2(240, 0), true);
        for (int i = 0; i < mgr.OnionLayerMods.Count; i++)
        {
            var mod = mgr.OnionLayerMods[i];
            bool isSelected = _selectedOnionModIndex == i;

            if (!mod.Enabled) ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(0.5f, 0.5f, 0.5f, 1.0f));
            string displayName = mod.Enabled ? mod.Name : mod.Name + " " + Translator.LocalizeUI("(Disabled)");
            if (ImGui.Selectable($"{displayName}##OnionModSelect_{i}_{mod.Meta.Identifier}", isSelected))
            {
                _selectedOnionModIndex = i;
            }
            if (!mod.Enabled) ImGui.PopStyleColor();
        }
        ImGui.EndChild();

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(Translator.LocalizeUI("You can drop .omp files directly here to import them!"));
        }

        if (Plugin.DragDropManager.CreateImGuiTarget("OnionModImportTarget", out var droppedFiles, out _))
        {
            foreach (var f in droppedFiles)
            {
                if (f.EndsWith(".omp", StringComparison.OrdinalIgnoreCase))
                {
                    Plugin.OnionLayerModManager?.ImportFromFile(f, false);
                }
            }
            if (mgr.OnionLayerMods.Count > 0)
                _selectedOnionModIndex = mgr.OnionLayerMods.Count - 1;
        }

        ImGui.SameLine();

        ImGui.BeginChild("OnionModDetails", new Vector2(0, 0), true);
        if (_selectedOnionModIndex >= 0 && _selectedOnionModIndex < mgr.OnionLayerMods.Count)
        {
            var mod = mgr.OnionLayerMods[_selectedOnionModIndex];
            ImGui.PushID($"onion_mod_details_{mod.Meta.Identifier}");

            string modName = mod.Meta.Name;
            if (ImGui.InputText(Translator.LocalizeUI("Mod Name") + "##OnionName", ref modName, 255))
            {
                mod.Meta.Name = modName;
                mod.SaveMeta();
                mod.TryRenameDirectoryToName(mgr.RootDirectory, out _);
                mgr.TriggerHotswapRebuild();
            }

            bool enabled = mod.Settings.Enabled;
            if (ImGui.Checkbox(Translator.LocalizeUI("Enabled") + "##OnionEnabled", ref enabled))
            {
                mod.Settings.Enabled = enabled;
                mod.SaveSettings();
                mgr.TriggerHotswapRebuild();
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (!string.IsNullOrWhiteSpace(mod.Meta.Author))
                ImGui.TextDisabled(Translator.LocalizeUI($"Author: {mod.Meta.Author}"));
            if (!string.IsNullOrWhiteSpace(mod.Meta.Version))
                ImGui.TextDisabled(Translator.LocalizeUI($"Version: {mod.Meta.Version}"));
            if (!string.IsNullOrWhiteSpace(mod.Meta.Website))
                ImGui.TextDisabled(Translator.LocalizeUI($"Website: {mod.Meta.Website}"));
            ImGui.TextDisabled(Translator.LocalizeUI($"Total Layers: {mod.Meta.TotalLayerCount}"));

            if (!string.IsNullOrWhiteSpace(mod.Meta.Description))
            {
                ImGui.Spacing();
                ImGui.TextWrapped(mod.Meta.Description);
            }

            if (mod.Meta.Groups.Count > 0)
            {
                ImGui.Spacing();
                ImGui.TextUnformatted(Translator.LocalizeUI("Mod Options:"));
                foreach (var group in mod.Meta.Groups)
                {
                    if (group.Options.Count == 0) continue;
                    int current = mod.Settings.GroupSelections.TryGetValue(group.Name, out var sel) ? sel : group.DefaultSettings;
                    string currentName = (current >= 0 && current < group.Options.Count) ? group.Options[current].Name : $"Option {current}";

                    if (ImGui.BeginCombo(group.Name, currentName))
                    {
                        for (int optIdx = 0; optIdx < group.Options.Count; optIdx++)
                        {
                            var opt = group.Options[optIdx];
                            bool isSelected = optIdx == current;
                            if (ImGui.Selectable(opt.Name, isSelected))
                            {
                                mod.Settings.GroupSelections[group.Name] = optIdx;
                                mod.SaveSettings();
                                mgr.TriggerHotswapRebuild();
                            }
                        }
                        ImGui.EndCombo();
                    }
                }
            }

            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            if (ImGui.Button(Translator.LocalizeUI("Export .omp Package")))
            {
                mgr.ExportMod(mod);
            }

            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.8f, 0.2f, 0.2f, 1f));
            if (ImGui.Button(Translator.LocalizeUI("Delete Mod")))
            {
                mgr.DeleteMod(mod);
                _selectedOnionModIndex = Math.Max(0, _selectedOnionModIndex - 1);
                ImGui.PopStyleColor();
                ImGui.PopID();
                ImGui.EndChild();
                return;
            }
            ImGui.PopStyleColor();

            ImGui.PopID();
        }
        else
        {
            ImGui.Text(Translator.LocalizeUI("Select an Onion mod on the left to edit settings."));
        }
        ImGui.EndChild();
    }

    #endregion
}
