using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Bindings.ImGui;
using PenumbraAndGlamourerHelpers;

namespace DragAndDropTexturing.Windows
{
    public class TexturePaintingWindow : Window, IDisposable
    {
        private readonly Plugin _plugin;
        private string _tempDir;
        
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

        private System.Drawing.Bitmap _paintLayer;
        private System.Drawing.Graphics _paintGraphics;
        private System.Drawing.SolidBrush _paintBrush;
        private float _brushSize = 10f;
        private System.Numerics.Vector4 _paintColor = new System.Numerics.Vector4(1, 0, 0, 1);
        private int _paintStrokeCount = 0;
        private Vector2? _lastUvHit = null;
        private Dalamud.Interface.Textures.ISharedImmediateTexture _canvasTexture;
        private string _canvasTexturePath;

        public TexturePaintingWindow(Plugin plugin) : base("Texture Painter", ImGuiWindowFlags.NoScrollbar)
        {
            _plugin = plugin;
            Size = new Vector2(1000, 600);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        public override void OnOpen()
        {
            _tempDir = Path.Combine(_plugin.ContextualLayerManager.RootDirectory, "Paint_Temp");
            _canvasTexturePath = Path.Combine(_tempDir, "canvas_preview.png");
            Directory.CreateDirectory(_tempDir);
            
            if (_paintLayer == null)
            {
                _paintLayer = new System.Drawing.Bitmap(1024, 1024, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                _paintGraphics = System.Drawing.Graphics.FromImage(_paintLayer);
                _paintGraphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                _paintBrush = new System.Drawing.SolidBrush(System.Drawing.Color.Red);
            }

            _rendererInitialized = false;
            _renderer?.Dispose();
            _renderer = null;

            _topModelDiskPath = "";
            _botModelDiskPath = "";
            _activeBaseTexturePng = "";
            _activeNormalTexturePng = "";
            _isGen3Preview = false;
            _isBiboPreview = false;
            _previewDirty = true;
            _canvasTexture = null;
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

            ImGui.Columns(2, "PaintLayout", false);
            ImGui.SetColumnWidth(0, ImGui.GetWindowWidth() * 0.5f);

            // Left side controls
            ImGui.ColorEdit4("Brush Color", ref _paintColor, ImGuiColorEditFlags.NoInputs);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.SliderFloat("Size", ref _brushSize, 1f, 50f, "%.1f");
            
            if (ImGui.Button("Commit Paint to Active Character Overlays"))
            {
                CommitPaintLayer();
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear Paint"))
            {
                _paintGraphics.Clear(System.Drawing.Color.Transparent);
                _previewDirty = true;
            }

            ImGui.Separator();

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

                    ImGui.InvisibleButton("##viewport3d", region);
                    bool isHovered = ImGui.IsItemHovered();
                    bool isActive = ImGui.IsItemActive();

                    if (isHovered || isActive)
                    {
                        var mousePos = ImGui.GetMousePos();
                        
                        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                        {
                            Vector2 localMousePos = mousePos - cursorPos;
                            if (_renderer.Raycast(localMousePos, out Vector2 uvHit, out string hitSlot))
                            {
                                PaintAtUV(uvHit);
                                _paintStrokeCount++;
                                if (_paintStrokeCount % 5 == 0) _previewDirty = true;
                            }
                        }
                        else if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                        {
                            _lastUvHit = null;
                            _previewDirty = true;
                        }
                        else
                        {
                            _lastUvHit = null;
                        }

                        if (ImGui.IsMouseDragging(ImGuiMouseButton.Middle) || ImGui.IsMouseDragging(ImGuiMouseButton.Right))
                        {
                            if (!_isDragging)
                            {
                                _isDragging = true;
                                _lastMousePos = mousePos;
                            }
                            var delta = mousePos - _lastMousePos;
                            _renderer.PanCamera(delta.X, delta.Y);
                            _lastMousePos = mousePos;
                        }
                        else _isDragging = false;

                        float wheel = ImGui.GetIO().MouseWheel;
                        if (wheel != 0) _renderer.ZoomCamera(wheel);
                    }
                }
            }

            ImGui.NextColumn();

            // 2D Canvas View
            ImGui.Text("2D UV Canvas");
            ImGui.Separator();
            
            var canvasRegion = ImGui.GetContentRegionAvail();
            float canvasSize = Math.Min(canvasRegion.X, canvasRegion.Y);
            
            if (_canvasTexture != null)
            {
                var wrap = _canvasTexture.GetWrapOrDefault();
                if (wrap != null)
                {
                    var cursorPos = ImGui.GetCursorScreenPos();
                    ImGui.Image(wrap.Handle, new Vector2(canvasSize, canvasSize));
                    
                    if (ImGui.IsItemHovered() && ImGui.IsMouseDown(ImGuiMouseButton.Left))
                    {
                        var mousePos = ImGui.GetMousePos();
                        Vector2 localMousePos = mousePos - cursorPos;
                        Vector2 uv = new Vector2(localMousePos.X / canvasSize, localMousePos.Y / canvasSize);
                        if (uv.X >= 0 && uv.X <= 1 && uv.Y >= 0 && uv.Y <= 1)
                        {
                            PaintAtUV(uv);
                            _paintStrokeCount++;
                            if (_paintStrokeCount % 5 == 0) _previewDirty = true;
                        }
                    }
                    else if (ImGui.IsItemHovered() && ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                    {
                        _lastUvHit = null;
                        _previewDirty = true;
                    }
                }
            }

            ImGui.Columns(1);

            if (_previewDirty && !_isPreviewUpdating)
            {
                _previewDirty = false;
                UpdatePreviewTextures();
            }
        }

        private void PaintAtUV(Vector2 uvHit)
        {
            if (_paintGraphics == null || _paintBrush == null) return;
            
            _paintBrush.Color = System.Drawing.Color.FromArgb((int)(_paintColor.W * 255), (int)(_paintColor.X * 255), (int)(_paintColor.Y * 255), (int)(_paintColor.Z * 255));
            float x = uvHit.X * 1024;
            float y = uvHit.Y * 1024;
            
            if (_lastUvHit.HasValue)
            {
                float lastX = _lastUvHit.Value.X * 1024;
                float lastY = _lastUvHit.Value.Y * 1024;
                if (Vector2.Distance(uvHit, _lastUvHit.Value) < 0.1f)
                {
                    using (var pen = new System.Drawing.Pen(_paintBrush, _brushSize))
                    {
                        pen.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                        pen.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                        _paintGraphics.DrawLine(pen, lastX, lastY, x, y);
                    }
                }
            }
            _paintGraphics.FillEllipse(_paintBrush, x - _brushSize / 2f, y - _brushSize / 2f, _brushSize, _brushSize);
            _lastUvHit = uvHit;
        }

        private void CommitPaintLayer()
        {
            if (_paintLayer == null) return;
            
            string importDir = Path.Combine(_plugin.ContextualLayerManager.RootDirectory, "Imports");
            if (!Directory.Exists(importDir)) Directory.CreateDirectory(importDir);
            
            string outPath = Path.Combine(importDir, $"PaintedUV_{Guid.NewGuid().ToString().Substring(0, 8)}.png");
            _paintLayer.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            
            var targetChar = _plugin.SafeGameObjectManager.LocalPlayer;
            if (targetChar != null && _plugin.DragAndDropTextures != null)
            {
                var characterGameObject = targetChar as Dalamud.Game.ClientState.Objects.Types.ICharacter;
                if (characterGameObject != null)
                {
                    _plugin.DragAndDropTextures.InjectFilesAndRebuild(
                        new List<string> { outPath }, 
                        new KeyValuePair<string, Dalamud.Game.ClientState.Objects.Types.ICharacter>(targetChar.Name.TextValue, characterGameObject), 
                        PenumbraAndGlamourerHelpers.BodyDragPart.Body);
                }
            }
            
            _paintGraphics.Clear(System.Drawing.Color.Transparent);
            _previewDirty = true;
            IsOpen = false;
        }

        public void Dispose()
        {
            _paintLayer?.Dispose();
            _paintGraphics?.Dispose();
            _paintBrush?.Dispose();
            _renderer?.Dispose();
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
                bool isGen3 = lowerPath.Contains("gen3") || lowerPath.Contains("tfgen3") || lowerPath.Contains("pythia") || lowerPath.Contains("exqb") || lowerPath.Contains("eve") || lowerPath.Contains("gaia");
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


        private System.Drawing.Bitmap CompositeLayersToBitmap(string activeBaseTexPng)
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

            if (_paintLayer != null)
            {
                g.DrawImage(_paintLayer, 0, 0, width, height);
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
                    using var bitmap = CompositeLayersToBitmap(_activeBaseTexturePng);
                    
                    // Save to canvas path for 2D ImGui display
                    bitmap.Save(_canvasTexturePath, System.Drawing.Imaging.ImageFormat.Png);
                    _canvasTexture = Plugin.TextureProvider.GetFromFile(_canvasTexturePath);

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
    }
}
