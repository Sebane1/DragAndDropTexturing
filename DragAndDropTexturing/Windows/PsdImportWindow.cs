using System;
using DragAndDropTexturing.LanguageHelpers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Bindings.ImGui;
using PsdSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using ImageMagick;
using Point = SixLabors.ImageSharp.Point;

namespace DragAndDropTexturing.Windows
{
    public class PsdImportLayer
    {
        public string OriginalName;
        public string PngPath;
        public bool Selected = true;
        public int BodyPartIndex = 0; // 0=body, 1=face, 2=eyes, 3=eyebrows
        public int OverrideTypeIndex = 0; // 0=Base, 1=Normal
        public int BodyTypeIndex = 0; // 0=None, 1=bibo, 2=gen3, 3=tbse, 4=otopop, 5=vanilla
        public Dalamud.Interface.Textures.ISharedImmediateTexture PreviewTexture;
        public bool IsPreviewLoading = false;
    }

    public class PsdImportWindow : Window, IDisposable
    {
        private readonly Plugin _plugin;
        private string _tempDir;
        private string _importDir;
        private bool _isProcessing = false;
        private float _progress = 0f;
        private string _statusText = "";
        private Action<List<string>> _onComplete;
        private Func<List<string>, Task> _onCompleteAsync;
        
        private ModelRenderer _renderer;
        private bool _rendererInitialized = false;
        private Vector2 _lastMousePos = Vector2.Zero;
        private bool _isDragging = false;
        private bool _isPanning = false;

        private string _topModelDiskPath = "";
        private string _botModelDiskPath = "";
        private string _activeBaseTexturePng = "";
        private string _activeNormalTexturePng = "";
        private bool _previewDirty = false;
        private bool _isPreviewUpdating = false;
        private bool _isGen3Preview = false;
        private bool _isBiboPreview = false;

        public List<PsdImportLayer> Layers = new();
        private readonly string[] _bodyParts = { "body", "face", "eyes", "eyebrows" };
        private readonly string[] _overrideTypes = { "Base", "Normal" };
        private readonly string[] _bodyTypes = { "None", "Bibo+", "Gen3", "TBSE", "Otopop", "Vanilla" };

        public PsdImportWindow(Plugin plugin) : base("PSD Import Manager", ImGuiWindowFlags.NoScrollbar)
        {
            _plugin = plugin;
            Size = new Vector2(800, 400);
            SizeCondition = ImGuiCond.FirstUseEver;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(800, 400),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };
        }

        public void StartImport(string psdPath, Func<List<string>, Task> onComplete = null)
        {
            _onCompleteAsync = onComplete;
            Layers.Clear();
            _isProcessing = true;
            _progress = 0f;
            _statusText = "Loading PSD...";
            IsOpen = true;

            _rendererInitialized = false;
            _renderer?.Dispose();
            _renderer = null;

            _topModelDiskPath = "";
            _botModelDiskPath = "";
            _activeBaseTexturePng = "";
            _activeNormalTexturePng = "";
            _isGen3Preview = false;
            _isBiboPreview = false;
            _previewDirty = false;

            _tempDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "PSD_Temp");
            _importDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "SavedOverlays");

            Task.Run(() => ProcessPsd(psdPath));
        }

        private void ProcessPsd(string psdPath)
        {
            try
            {
                if (!Directory.Exists(_tempDir)) Directory.CreateDirectory(_tempDir);
                // Clean temp dir
                foreach (var f in Directory.GetFiles(_tempDir, "*.png")) File.Delete(f);

                using var collection = new MagickImageCollection(psdPath);
                
                int startIndex = collection.Count > 1 ? 1 : 0;
                int totalLayers = collection.Count - startIndex;
                int currentLayer = 0;
                
                uint compositeWidth = collection[0].Width;
                uint compositeHeight = collection[0].Height;

                for (int i = startIndex; i < collection.Count; i++)
                {
                    currentLayer++;
                    var layer = collection[i];
                    string layerName = layer.GetAttribute("label") ?? $"Layer_{i}";

                    _progress = (float)currentLayer / totalLayers;
                    _statusText = $"Extracting layer: {layerName}";

                    if (layer.Width <= 1 || layer.Height <= 1)
                        continue; // Skip empty layers or folders

                    try
                    {
                        using var fullImg = new MagickImage(MagickColors.Transparent, compositeWidth, compositeHeight);
                        int xOffset = layer.Page != null ? layer.Page.X : 0;
                        int yOffset = layer.Page != null ? layer.Page.Y : 0;
                        
                        fullImg.Composite(layer, xOffset, yOffset, CompositeOperator.Over);

                        string safeName = string.Join("_", layerName.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
                        string outPath = Path.Combine(_tempDir, $"{safeName}.png");
                        fullImg.Write(outPath, MagickFormat.Png);

                        var importLayer = new PsdImportLayer
                        {
                            OriginalName = layerName,
                            PngPath = outPath,
                            Selected = true
                        };

                        // Auto-detect Body Part
                        string lowerName = layerName.ToLower();
                        if (lowerName.Contains("face")) importLayer.BodyPartIndex = 1;
                        else if (lowerName.Contains("eye")) importLayer.BodyPartIndex = 2;
                        else if (lowerName.Contains("eyebrow") || lowerName.Contains("lash")) importLayer.BodyPartIndex = 3;

                        // Auto-detect Map Type
                        if (lowerName.Contains("norm") || lowerName.Contains("bump")) importLayer.OverrideTypeIndex = 1;

                        // Auto-detect Body Type
                        if (lowerName.Contains("bibo") || lowerName.Contains("b+")) importLayer.BodyTypeIndex = 1;
                        else if (lowerName.Contains("gen3")) importLayer.BodyTypeIndex = 2;
                        else if (lowerName.Contains("tbse")) importLayer.BodyTypeIndex = 3;
                        else if (lowerName.Contains("otopop")) importLayer.BodyTypeIndex = 4;
                        else if (lowerName.Contains("vanilla") || lowerName.Contains("gen2")) importLayer.BodyTypeIndex = 5;

                        Layers.Add(importLayer);
                    }
                    catch (Exception ex)
                    {
                        _plugin.PluginLog.Error($"Failed to extract PSD layer {layerName}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error($"Failed to process PSD {psdPath}: {ex.Message}");
                _statusText = "Error processing PSD!";
            }
            finally
            {
                _isProcessing = false;
                _statusText = "Ready to import.";
                _previewDirty = true;
            }
        }

        public override void Draw()
        {
            try
            {
                if (!_rendererInitialized)
                {
                    _renderer = new ModelRenderer(800, 600);
                    _rendererInitialized = true;
                    LoadPlayerModels();
                }
            }
            catch { }

            if (_isProcessing)
            {
                ImGui.Text(_statusText);
                ImGui.ProgressBar(_progress, new Vector2(-1, 0));
                return;
            }

            if (Layers.Count == 0)
            {
                ImGui.Text(Translator.LocalizeUI("No valid image layers found in PSD."));
                if (ImGui.Button(Translator.LocalizeUI("Close"))) IsOpen = false;
                return;
            }

            ImGui.Columns(2, "PsdLayout", false);
            ImGui.SetColumnWidth(0, ImGui.GetWindowWidth() * 0.65f);

            var locBodyParts = Translator.LocalizeTextArray(_bodyParts);
            var locOverrideTypes = Translator.LocalizeTextArray(_overrideTypes);
            var locBodyTypes = Translator.LocalizeTextArray(_bodyTypes);

            ImGui.Text(Translator.LocalizeUI("Select layers to import:"));
            ImGui.Separator();

            ImGui.BeginChild("PsdLayersList", new Vector2(0, ImGui.GetContentRegionAvail().Y - 40), true);
            foreach (var layer in Layers)
            {
                ImGui.PushID(layer.OriginalName);
                
                bool selected = layer.Selected;
                if (ImGui.Checkbox("##Select", ref selected))
                {
                    layer.Selected = selected;
                    _previewDirty = true;
                }
                
                ImGui.SameLine();
                ImGui.Text(layer.OriginalName);

                ImGui.SameLine(250);
                ImGui.SetNextItemWidth(100);
                int part = layer.BodyPartIndex;
                if (ImGui.Combo("##BodyPart", ref part, locBodyParts, locBodyParts.Length))
                {
                    layer.BodyPartIndex = part;
                    _previewDirty = true;
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(80);
                int type = layer.OverrideTypeIndex;
                if (ImGui.Combo("##OverrideType", ref type, locOverrideTypes, locOverrideTypes.Length))
                {
                    layer.OverrideTypeIndex = type;
                    _previewDirty = true;
                }

                if (layer.BodyPartIndex == 0)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80);
                    int bType = layer.BodyTypeIndex;
                    if (ImGui.Combo("##BodyType", ref bType, locBodyTypes, locBodyTypes.Length))
                    {
                        layer.BodyTypeIndex = bType;
                        _previewDirty = true;
                    }
                }

                if (layer.Selected)
                {
                    // Load texture if needed
                    if (layer.PreviewTexture == null && !layer.IsPreviewLoading && File.Exists(layer.PngPath))
                    {
                        layer.IsPreviewLoading = true;
                        Task.Run(async () =>
                        {
                            try
                            {
                                var tex = Plugin.TextureProvider.GetFromFile(layer.PngPath);
                                layer.PreviewTexture = tex;
                            }
                            catch { }
                            layer.IsPreviewLoading = false;
                        });
                    }

                        var wrap = layer.PreviewTexture?.GetWrapOrDefault();
                        if (wrap != null)
                        {
                            ImGui.SameLine();
                            ImGui.Image(wrap.Handle, new Vector2(80, 80));
                        }
                }

                ImGui.PopID();
                ImGui.Separator();
            }
            ImGui.EndChild();

            if (ImGui.Button(Translator.LocalizeUI("Finalize & Import Selected"), new Vector2(-1, 30)))
            {
                FinalizeImport();
            }

            if (_previewDirty && !_isPreviewUpdating)
            {
                _previewDirty = false;
                UpdatePreviewTextures();
            }

            ImGui.NextColumn();

            // Right column: 3D Preview
            if (_renderer != null)
            {
                var region = ImGui.GetContentRegionAvail();
                if (region.X > 0 && region.Y > 0 && (region.X != _renderer.Width || region.Y != _renderer.Height))
                {
                    _renderer.Resize((int)region.X, (int)region.Y);
                }

                _renderer.Render();

                if (_renderer.ShaderResourceViewHandle != IntPtr.Zero)
                {
                    var cursorPos = ImGui.GetCursorScreenPos();
                    var drawList = ImGui.GetWindowDrawList();
                    drawList.AddImage(new ImTextureID(_renderer.ShaderResourceViewHandle), cursorPos, cursorPos + region);

                    ImGui.InvisibleButton("##viewportPsd", region);
                    bool isHovered = ImGui.IsItemHovered();
                    bool isActive = ImGui.IsItemActive();

                    if (isHovered || isActive)
                    {
                        var mousePos = ImGui.GetMousePos();
                        if (ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                        {
                            if (!_isDragging)
                            {
                                _isDragging = true;
                                _lastMousePos = mousePos;
                            }
                            var delta = mousePos - _lastMousePos;
                            _renderer.RotateCamera(delta.X * 0.005f, delta.Y * 0.005f);
                            _lastMousePos = mousePos;
                        }
                        else _isDragging = false;

                        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || ImGui.IsMouseDragging(ImGuiMouseButton.Right))
                        {
                            if (!_isPanning)
                            {
                                _isPanning = true;
                                _lastMousePos = mousePos;
                            }
                            var delta = mousePos - _lastMousePos;
                            _renderer.PanCamera(delta.X, delta.Y);
                            _lastMousePos = mousePos;
                        }
                        else _isPanning = false;

                        float wheel = ImGui.GetIO().MouseWheel;
                        if (wheel != 0) _renderer.ZoomCamera(wheel);
                    }
                    else
                    {
                        _isDragging = false;
                        _isPanning = false;
                    }
                }
            }

            ImGui.Columns(1);
        }

        private void LoadPlayerModels()
        {
            try
            {
                var character = _plugin.SafeGameObjectManager.LocalPlayer;
                if (character == null)
                {
                    _plugin.PluginLog.Warning("[PSD Preview] LocalPlayer is null!");
                    return;
                }

                _plugin.PluginLog.Info($"[PSD Preview] Attempting to load models for {character.Name}");

                var stateBase64Result = PenumbraAndGlamourerIpcWrapper.Instance.GetStateBase64.Invoke(character.ObjectIndex);
                var customization = PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.CharacterCustomization.ReadCustomization(stateBase64Result.Item2);
                
                // We always want to preview on the naked body. Since users use Penumbra to replace
                // the Emperor's New clothes with naked bodies, we hardcode the equipment ID to e0279.
                _plugin.PluginLog.Info($"[PSD Preview] Auto-stripping to Emperor's New gear (e0279) for preview.");

                int ffxivRace = customization.Customize.Race.Value;
                int ffxivClan = customization.Customize.Clan.Value;
                int ffxivGender = customization.Customize.Gender.Value;

                string GetFfxivModelRaceCode(int race, int clan, int gender)
                {
                    int code = 101;
                    switch (race)
                    {
                        case 1: // Hyur
                            code = clan == 2 ? 301 : 101; break;
                        case 2: // Elezen
                        case 4: // Miqo'te
                        case 6: // Au Ra
                        case 8: // Viera
                            code = 101; break; // Gear for these races shares the Midlander base mesh
                        case 3: // Lalafell
                            code = 1101; break;
                        case 5: // Roegadyn
                            code = 901; break;
                        case 7: // Hrothgar
                            code = 1501; break;
                    }
                    if (gender == 1) code += 100; // Female is +100
                    return $"c{code:D4}";
                }

                string trueRaceCode = GetFfxivModelRaceCode(ffxivRace, ffxivClan, ffxivGender);
                _plugin.PluginLog.Info($"[PSD Preview] True FFXIV Model RaceCode resolved to: {trueRaceCode}");

                string topPath = $"chara/equipment/e0279/model/{trueRaceCode}e0279_top.mdl";
                string botPath = $"chara/equipment/e0279/model/{trueRaceCode}e0279_dwn.mdl";

                Guid collectionId = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;
                _plugin.PluginLog.Info($"[PSD Preview] Collection ID: {collectionId}");

                LoadModelIntoSlot("Top", topPath, collectionId);
                LoadModelIntoSlot("Bottom", botPath, collectionId);

                bool prevOverrideMode = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OverrideMode;
                FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OverrideMode = true;
                PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.PopulateOmniOverrides(collectionId, ffxivGender, ffxivRace, _plugin);
                FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OverrideMode = prevOverrideMode;

                string lowerPath = _topModelDiskPath.ToLower();
                bool isGen3 = lowerPath.Contains("gen3") || lowerPath.Contains("tfgen3") || lowerPath.Contains("pythia") || lowerPath.Contains("exqb") || System.Text.RegularExpressions.Regex.IsMatch(lowerPath, @"(^|[^a-z])eve([^a-z]|$)") || lowerPath.Contains("gaia");
                bool isBibo = lowerPath.Contains("bibo") || lowerPath.Contains("b+");

                if (!isGen3 && !isBibo)
                {
                    int bodyIndex = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(collectionId, ffxivGender, out string detectedName, _plugin);
                    if (bodyIndex == 2) isGen3 = true;
                    if (bodyIndex == 1) isBibo = true;
                    _plugin.PluginLog.Info($"[PSD Preview] Path didn't contain 'gen3' or 'bibo'. Fallback detection returned: {detectedName} ({(isGen3 ? "Gen3" : isBibo ? "Bibo+" : "Unknown")})");
                }

                _isGen3Preview = isGen3;
                _isBiboPreview = isBibo;

                string baseTexPath = null;
                string normTexPath = null;
                if (isGen3 && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override != null)
                {
                    baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.Base;
                    normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.Normal;
                }
                else if (isBibo && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride != null)
                {
                    baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.Base;
                    normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.Normal;
                }
                else
                {
                    if (FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride != null)
                    {
                        baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.Base;
                        normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.Normal;
                        isBibo = true;
                    }
                    else if (FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override != null)
                    {
                        baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.Base;
                        normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.Normal;
                        isGen3 = true;
                    }
                }
                _plugin.PluginLog.Info($"[PSD Preview] Resolved BaseTexture: {baseTexPath ?? "NULL"}");

                bool baseIsBlack = false;
                bool normIsBlack = false;
                _activeBaseTexturePng = TexToTempPng(baseTexPath, out baseIsBlack);
                _activeNormalTexturePng = TexToTempPng(normTexPath, out normIsBlack);

                if (_activeBaseTexturePng == null || baseIsBlack)
                {
                    _plugin.PluginLog.Info("[PSD Preview] Base texture from priority mod was missing or fully black. Falling back to DLC underlay skin type.");
                    string modPath = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
                    string dlcPath = Path.Combine(modPath, "LooseTextureCompilerDLC");
                    string dlcBase = null;
                    if (isBibo && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes.Count > 0)
                        dlcBase = Path.Combine(dlcPath, FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes[0].BackupTextures[0].Base.TrimStart('\\'));
                    else if (isGen3 && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes.Count > 0)
                        dlcBase = Path.Combine(dlcPath, FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes[0].BackupTextures[0].Base.TrimStart('\\'));

                    _activeBaseTexturePng = TexToTempPng(dlcBase, out baseIsBlack);

                    if (_activeBaseTexturePng == null || baseIsBlack)
                    {
                        _plugin.PluginLog.Info("[PSD Preview] DLC fallback failed. Extracting vanilla texture via Lumina.");
                        int ffxivGenderInt = ffxivGender == 1 ? 1 : 0;
                        string vanillaBodyTexPath = FFXIVLooseTextureCompiler.Racial.RacePaths.GetBodyTexturePath(0, ffxivGenderInt, 0, ffxivRace, 0, false);
                        string vanillaBasePng = ExtractVanillaTexViaLumina(vanillaBodyTexPath);
                        if (!string.IsNullOrEmpty(vanillaBasePng))
                        {
                            _activeBaseTexturePng = vanillaBasePng;
                        }
                    }
                }

                if (_activeNormalTexturePng == null || normIsBlack)
                {
                    string modPath = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
                    string dlcPath = Path.Combine(modPath, "LooseTextureCompilerDLC");
                    string dlcNorm = null;
                    if (isBibo && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes.Count > 0)
                        dlcNorm = Path.Combine(dlcPath, FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes[0].BackupTextures[0].Normal.TrimStart('\\'));
                    else if (isGen3 && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes.Count > 0)
                        dlcNorm = Path.Combine(dlcPath, FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes[0].BackupTextures[0].Normal.TrimStart('\\'));

                    _activeNormalTexturePng = TexToTempPng(dlcNorm, out normIsBlack);

                    if (_activeNormalTexturePng == null || normIsBlack)
                    {
                        int ffxivGenderInt = ffxivGender == 1 ? 1 : 0;
                        string vanillaNormTexPath = FFXIVLooseTextureCompiler.Racial.RacePaths.GetBodyTexturePath(1, ffxivGenderInt, 0, ffxivRace, 0, false);
                        string vanillaNormPng = ExtractVanillaTexViaLumina(vanillaNormTexPath);
                        if (!string.IsNullOrEmpty(vanillaNormPng))
                        {
                            _activeNormalTexturePng = vanillaNormPng;
                        }
                    }
                }

                UpdatePreviewTextures();
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error(ex, "Failed to load player models for PSD 3D preview");
            }
        }

        private void LoadModelIntoSlot(string slot, string path, Guid collectionId)
        {
            try
            {
                _plugin.PluginLog.Info($"[PSD Preview] Loading slot '{slot}' with GamePath: {path}");
                
                // Try resolving via Penumbra first
                string diskPath = path;
                try 
                {
                    PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collectionId, path, out string resolvedPath);
                    if (!string.IsNullOrEmpty(resolvedPath))
                    {
                        diskPath = resolvedPath;
                        _plugin.PluginLog.Info($"[PSD Preview] Penumbra resolved '{path}' -> '{diskPath}'");
                    }
                    else
                    {
                        _plugin.PluginLog.Info($"[PSD Preview] Penumbra did not resolve '{path}'. Returning original.");
                    }
                }
                catch (Exception ex)
                {
                    _plugin.PluginLog.Error(ex, $"[PSD Preview] Penumbra IPC ResolvePath failed for '{path}'");
                }

                System.Collections.Generic.List<ExtractedMesh> meshes = null;

                if (diskPath != path && System.IO.File.Exists(diskPath))
                {
                    _plugin.PluginLog.Info($"[PSD Preview] Reading external file from disk: {diskPath}");
                    meshes = MdlParser.ParseFromDisk(diskPath, out var loadStatus);
                    _plugin.PluginLog.Info($"[PSD Preview] Disk parse status: {loadStatus}");
                }
                else
                {
                    _plugin.PluginLog.Warning($"[PSD Preview] Penumbra did not resolve a custom disk path for {path}. Skipping Lumina as requested.");
                }

                if (slot == "Top") _topModelDiskPath = diskPath;
                if (slot == "Bottom") _botModelDiskPath = diskPath;

                if (meshes != null && meshes.Count > 0)
                {
                    _plugin.PluginLog.Info($"[PSD Preview] Successfully loaded {meshes.Count} meshes into slot '{slot}'. Slicing base to '{slot}', extras to '{slot}_N'.");
                    _renderer.LoadMeshes(slot, new System.Collections.Generic.List<ExtractedMesh> { meshes[0] });
                    for (int i = 1; i < meshes.Count; i++)
                    {
                        _renderer.LoadMeshes($"{slot}_{i}", new System.Collections.Generic.List<ExtractedMesh> { meshes[i] });
                    }
                }
                else
                {
                    _plugin.PluginLog.Warning($"[PSD Preview] No meshes parsed for '{slot}'. Falling back to dummy cube.");
                    // Fall back to dummy cube if missing
                    _renderer.LoadMeshes(slot, MdlParser.GetDummyCube());
                }
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error(ex, $"[PSD Preview] Unhandled exception loading slot '{slot}'");
            }
        }

        private string TexToTempPng(string texPath, out bool isBlack)
        {
            isBlack = false;
            if (string.IsNullOrEmpty(texPath) || !File.Exists(texPath)) return null;
            if (texPath.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    using (var bitmap = new System.Drawing.Bitmap(texPath))
                    {
                        isBlack = IsImageBlack(bitmap);
                    }
                }
                catch { }
                return texPath;
            }
            
            try
            {
                string outPath = Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(texPath) + "_base.png");
                
                using (var bitmap = FFXIVLooseTextureCompiler.ImageProcessing.TexIO.ResolveBitmap(texPath))
                {
                    if (bitmap != null)
                    {
                        isBlack = IsImageBlack(bitmap);
                        if (!File.Exists(outPath) || isBlack)
                        {
                            bitmap.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        return outPath;
                    }
                }
            }
            catch (Exception ex) { _plugin.PluginLog.Error(ex, $"Failed to convert tex to png: {texPath}"); }
            return null;
        }

        private bool IsImageBlack(System.Drawing.Bitmap bitmap)
        {
            var data = bitmap.LockBits(new System.Drawing.Rectangle(0, 0, bitmap.Width, bitmap.Height), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            int bytes = Math.Abs(data.Stride) * bitmap.Height;
            byte[] rgbValues = new byte[bytes];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, rgbValues, 0, bytes);
            bitmap.UnlockBits(data);
            
            for (int i = 0; i < rgbValues.Length; i += 4)
            {
                if (rgbValues[i + 3] > 0) // if not fully transparent
                {
                    if (rgbValues[i] > 5 || rgbValues[i + 1] > 5 || rgbValues[i + 2] > 5) // threshold for black
                    {
                        return false;
                    }
                }
            }
            return true;
        }

        private string ExtractVanillaTexViaLumina(string internalGamePath)
        {
            try
            {
                var texFile = Plugin.DataManager.GetFile<Lumina.Data.Files.TexFile>(internalGamePath);
                if (texFile == null) return null;

                using (var stream = new MemoryStream(texFile.Data))
                {
                    var bitmap = FFXIVLooseTextureCompiler.ImageProcessing.TexIO.TexToBitmap(stream);
                    if (bitmap != null)
                    {
                        string tempDir = Path.Combine(Path.GetTempPath(), "DragAndDropTexturing", "vanilla_cache");
                        Directory.CreateDirectory(tempDir);
                        string safeName = internalGamePath.Replace("/", "_").Replace("\\", "_");
                        string tempPath = Path.Combine(tempDir, safeName + ".png");
                        if (!File.Exists(tempPath))
                        {
                            bitmap.Save(tempPath, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        return tempPath;
                    }
                }
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error(ex, $"[PSD Preview] Failed to extract vanilla tex from {internalGamePath}");
            }
            return null;
        }

        private System.Drawing.Bitmap CompositeLayersToBitmap(int overrideTypeIndex, string activeBaseTexPng, bool isGen3, bool isBibo)
        {
            System.Drawing.Bitmap baseBitmap = null;
            int width = 1024, height = 1024;
            
            if (!string.IsNullOrEmpty(activeBaseTexPng) && File.Exists(activeBaseTexPng))
            {
                baseBitmap = new System.Drawing.Bitmap(activeBaseTexPng);
                width = baseBitmap.Width;
                height = baseBitmap.Height;
            }

            var composite = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = System.Drawing.Graphics.FromImage(composite);
            g.Clear(System.Drawing.Color.Transparent);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            g.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighQuality;

            if (baseBitmap != null)
            {
                g.DrawImage(baseBitmap, 0, 0, width, height);
                baseBitmap.Dispose();
            }

            foreach (var layer in Layers)
            {
                if (layer.Selected && layer.OverrideTypeIndex == overrideTypeIndex && layer.BodyPartIndex == 0) // Body only for Top/Bottom slots
                {
                    string inputPng = layer.PngPath;
                    bool convertToGen3 = isGen3 && layer.BodyTypeIndex == 1; // Bibo+ -> Gen3
                    bool convertToBibo = isBibo && layer.BodyTypeIndex == 2; // Gen3 -> Bibo+
                    
                    if (convertToGen3 || convertToBibo)
                    {
                        string convertedPath = Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(layer.PngPath) + (convertToGen3 ? "_gen3" : "_bibo") + ".png");
                        if (!File.Exists(convertedPath))
                        {
                            if (convertToGen3) FFXIVLooseTextureCompiler.FastUVTransfer.BiboToGen3(inputPng, convertedPath);
                            if (convertToBibo) FFXIVLooseTextureCompiler.FastUVTransfer.Gen3ToBibo(inputPng, convertedPath);
                        }
                        inputPng = convertedPath;
                    }

                    if (File.Exists(inputPng))
                    {
                        using var overlay = new System.Drawing.Bitmap(inputPng);
                        g.DrawImage(overlay, 0, 0, width, height);
                    }
                }
            }

            return composite;
        }

        public void UpdatePreviewTextures()
        {
            if (!_rendererInitialized || _renderer == null) return;
            _isPreviewUpdating = true;
            Task.Run(() => {
                try
                {
                    using var bitmap = CompositeLayersToBitmap(0, _activeBaseTexturePng, _isGen3Preview, _isBiboPreview);
                    
                    int w = bitmap.Width;
                    int h = bitmap.Height;
                    byte[] pixels = new byte[w * h * 4];
                    var rect = new System.Drawing.Rectangle(0, 0, w, h);
                    var data = bitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    unsafe
                    {
                        byte* ptr = (byte*)data.Scan0;
                        for (int y = 0; y < h; y++)
                        {
                            byte* row = ptr + (y * data.Stride);
                            for (int x = 0; x < w; x++)
                            {
                                int sIdx = x * 4;
                                int dIdx = (y * w + x) * 4;
                                pixels[dIdx + 0] = row[sIdx + 2]; // R
                                pixels[dIdx + 1] = row[sIdx + 1]; // G
                                pixels[dIdx + 2] = row[sIdx + 0]; // B
                                pixels[dIdx + 3] = row[sIdx + 3]; // A
                            }
                        }
                    }
                    bitmap.UnlockBits(data);
                    
                    _renderer.LoadTexture("Top", pixels, w, h);
                    _renderer.LoadTexture("Bottom", pixels, w, h);
                }
                catch (Exception ex) { _plugin.PluginLog.Error(ex, "Failed to update preview textures"); }
                finally { _isPreviewUpdating = false; }
            });
        }

        private void FinalizeImport()
        {
            if (!Directory.Exists(_importDir)) Directory.CreateDirectory(_importDir);

            List<string> extractedFiles = new();
            foreach (var layer in Layers.Where(l => l.Selected))
            {
                try
                {
                    if (File.Exists(layer.PngPath))
                    {
                        string targetPart = _bodyParts[layer.BodyPartIndex];
                        string targetType = _overrideTypes[layer.OverrideTypeIndex].ToLower();
                        string bodyTypeStr = (layer.BodyPartIndex == 0 && layer.BodyTypeIndex > 0) ? _bodyTypes[layer.BodyTypeIndex].ToLower().Replace("+", "") + "_" : "";
                        
                        string finalName = $"{bodyTypeStr}{targetPart}_{targetType}_{layer.OriginalName}.png";
                        // Sanitize filename
                        finalName = string.Join("_", finalName.Split(Path.GetInvalidFileNameChars()));
                        
                        string destPath = Path.Combine(_importDir, finalName);
                        File.Copy(layer.PngPath, destPath, true);
                        extractedFiles.Add(destPath);
                        _plugin.PluginLog.Information($"Imported PSD layer: {destPath}");
                    }
                }
                catch (Exception ex)
                {
                    _plugin.PluginLog.Error($"Error importing {layer.OriginalName}: {ex.Message}");
                }
            }
            
            // Clean up previews
            foreach (var layer in Layers)
            {
                layer.PreviewTexture = null;
            }
            
            if (_onCompleteAsync != null)
            {
                _isProcessing = true;
                _statusText = "Compiling and injecting textures... Please wait.";
                Task.Run(async () =>
                {
                    await _onCompleteAsync.Invoke(extractedFiles);
                    IsOpen = false;
                    _isProcessing = false;
                });
            }
            else
            {
                IsOpen = false;
            }
        }

        public void Dispose()
        {
            Layers.Clear();
        }
    }
}
