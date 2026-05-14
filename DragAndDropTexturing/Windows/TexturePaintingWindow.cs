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

        private float _brushSize = 10f;
        private System.Numerics.Vector4 _paintColor = new System.Numerics.Vector4(1, 0, 0, 1);
        private Vector2? _lastUvHit = null;
        private bool _gpuPaintInitialized = false;
        private bool _needsComposite = true;
        private System.Drawing.Bitmap _cachedBaseBitmap = null;
        private string _cachedBaseBitmapPath = "";
        private static readonly HashSet<string> _primarySlots = new HashSet<string> { "Top", "Bottom" };
        private static readonly string[] _primarySlotArray = new[] { "Top", "Bottom" };

        private float _brushHardness = 0.5f;
        private float _brushOpacity = 1.0f;
        private enum PaintTool { Brush, Eraser, Fill, Eyedropper }
        private enum PaintShape { Circle, Square }

        private PaintTool _activeTool = PaintTool.Brush;
        private PaintShape _activeShape = PaintShape.Circle;

        private class FloatingLayer : IDisposable
        {
            public Vortice.Direct3D11.ID3D11ShaderResourceView SRV;
            public int Width;
            public int Height;
            public Vector2 Position = new Vector2(0f, 0f);
            public Vector2 Scale = new Vector2(0.5f, 0.5f);

            public void Dispose()
            {
                SRV?.Dispose();
            }
        }
        private FloatingLayer _floatingLayer = null;
        private int _dragHandle = -1;
        private string _importPath = "";

        public TexturePaintingWindow(Plugin plugin) : base("Texture Painter", ImGuiWindowFlags.NoScrollbar)
        {
            _plugin = plugin;
            Size = new Vector2(1000, 600);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        public override void OnOpen()
        {
            _tempDir = Path.Combine(_plugin.ContextualLayerManager.RootDirectory, "Paint_Temp");
            Directory.CreateDirectory(_tempDir);

            _rendererInitialized = false;
            _gpuPaintInitialized = false;
            _renderer?.Dispose();
            _renderer = null;

            _topModelDiskPath = "";
            _botModelDiskPath = "";
            _activeBaseTexturePng = "";
            _activeNormalTexturePng = "";
            _isGen3Preview = false;
            _isBiboPreview = false;
            _needsComposite = true;
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
            ImGui.Text("Tools:");
            ImGui.SameLine();
            if (ImGui.RadioButton("Brush", _activeTool == PaintTool.Brush)) _activeTool = PaintTool.Brush;
            ImGui.SameLine();
            if (ImGui.RadioButton("Eraser", _activeTool == PaintTool.Eraser)) _activeTool = PaintTool.Eraser;
            ImGui.SameLine();
            if (ImGui.RadioButton("Fill", _activeTool == PaintTool.Fill)) _activeTool = PaintTool.Fill;
            ImGui.SameLine();
            if (ImGui.RadioButton("Eyedropper", _activeTool == PaintTool.Eyedropper)) _activeTool = PaintTool.Eyedropper;
            
            ImGui.Text("Shape:");
            ImGui.SameLine();
            if (ImGui.RadioButton("Circle", _activeShape == PaintShape.Circle)) _activeShape = PaintShape.Circle;
            ImGui.SameLine();
            if (ImGui.RadioButton("Square", _activeShape == PaintShape.Square)) _activeShape = PaintShape.Square;

            ImGui.Separator();

            ImGui.ColorEdit4("Brush Color", ref _paintColor, ImGuiColorEditFlags.NoInputs);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.SliderFloat("Size", ref _brushSize, 1f, 50f, "%.1f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.SliderFloat("Hardness", ref _brushHardness, 0f, 1f, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            ImGui.SliderFloat("Opacity", ref _brushOpacity, 0f, 1f, "%.2f");
            
            if (ImGui.Button("Commit Paint to Active Character Overlays"))
            {
                CommitPaintLayer();
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear Paint"))
            {
                _renderer?.PushUndoSnapshot();
                _renderer?.GpuClearPaint();
                _needsComposite = true;
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(_renderer == null || !_renderer.CanUndo);
            if (ImGui.Button("Undo"))
            {
                _renderer?.Undo();
                _needsComposite = true;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(_renderer == null || !_renderer.CanRedo);
            if (ImGui.Button("Redo"))
            {
                _renderer?.Redo();
                _needsComposite = true;
            }
            ImGui.EndDisabled();

            if (_floatingLayer != null)
            {
                ImGui.Separator();
                if (ImGui.Button("Stamp Floating Layer"))
                {
                    _renderer.PushUndoSnapshot();
                    _renderer.GpuStampTexture(_floatingLayer.SRV, _floatingLayer.Position, _floatingLayer.Scale);
                    _needsComposite = true;
                    _floatingLayer.Dispose();
                    _floatingLayer = null;
                }
                ImGui.SameLine();
                if (ImGui.Button("Discard Floating Layer"))
                {
                    _floatingLayer.Dispose();
                    _floatingLayer = null;
                }
            }
            else
            {
                ImGui.Separator();
                ImGui.Text("Import Image as Floating Layer:");
                ImGui.InputText("##importpath", ref _importPath, 512);
                ImGui.SameLine();
                if (ImGui.Button("Load File"))
                {
                    string path = _importPath.Trim('\"', ' ', '\'');
                    if (File.Exists(path))
                    {
                        LoadFloatingImage(path);
                    }
                    else
                    {
                        _plugin.PluginLog.Error($"[TexturePainter] File does not exist: {path}");
                    }
                }
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
                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            _renderer.PushUndoSnapshot();
                        }
                        
                        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                        {
                            Vector2 localMousePos = mousePos - cursorPos;
                            if (_renderer.Raycast(localMousePos, out Vector2 uvHit, out string hitSlot, _primarySlots))
                            {
                                if (_floatingLayer != null)
                                {
                                    _floatingLayer.Position = uvHit - (_floatingLayer.Scale / 2.0f);
                                    _needsComposite = true;
                                }
                                else
                                {
                                    PaintAtUV(uvHit);
                                }
                            }
                        }
                        else if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                        {
                            _lastUvHit = null;
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
                            if (ImGui.IsKeyDown(ImGuiKey.ModShift))
                            {
                                _renderer.PanCamera(delta.X, delta.Y);
                            }
                            else
                            {
                                _renderer.RotateCamera(delta.X * 0.01f, delta.Y * 0.01f);
                            }
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
            
            if (_renderer != null)
            {
                IntPtr srvHandle = _renderer.GetCompositeSrvHandle();
                if (srvHandle != IntPtr.Zero)
                {
                    var cursorPos = ImGui.GetCursorScreenPos();
                    ImGui.Image(new ImTextureID(srvHandle), new Vector2(canvasSize, canvasSize));
                    
                    if (_floatingLayer != null && _floatingLayer.SRV != null)
                    {
                        var drawList = ImGui.GetWindowDrawList();
                        Vector2 min = cursorPos + _floatingLayer.Position * canvasSize;
                        Vector2 max = min + _floatingLayer.Scale * canvasSize;
                        drawList.AddRect(min, max, 0xFF00FF00); // Green box
                        drawList.AddCircleFilled(max, 5f, 0xFF00FF00); // Scale handle
                    }

                    ImGui.SetCursorScreenPos(cursorPos);
                    ImGui.InvisibleButton("##viewport2d", new Vector2(canvasSize, canvasSize));
                    
                    if (Plugin.DragDropManager.CreateImGuiTarget("TextureDragDrop", out var files, out _))
                    {
                        var file = files.FirstOrDefault(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
                        if (file != null)
                        {
                            _plugin.PluginLog.Information($"[TexturePainter] Dropped file: {file}");
                            LoadFloatingImage(file);
                        }
                    }

                    if (ImGui.IsItemHovered() || ImGui.IsItemActive())
                    {
                        var mousePos = ImGui.GetMousePos();
                        Vector2 localMousePos = mousePos - cursorPos;
                        Vector2 uv = new Vector2(localMousePos.X / canvasSize, localMousePos.Y / canvasSize);

                        if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            if (_floatingLayer != null)
                            {
                                Vector2 min = _floatingLayer.Position;
                                Vector2 max = min + _floatingLayer.Scale;
                                if (Vector2.Distance(uv, max) < (10f / canvasSize))
                                {
                                    _dragHandle = 1; // Scale
                                }
                                else if (uv.X >= min.X && uv.X <= max.X && uv.Y >= min.Y && uv.Y <= max.Y)
                                {
                                    _dragHandle = 0; // Move
                                }
                                else _dragHandle = -1;
                            }

                            if (_dragHandle == -1) _renderer.PushUndoSnapshot();
                        }
                        
                        if (ImGui.IsMouseDown(ImGuiMouseButton.Left))
                        {
                            if (_dragHandle == 0 && _floatingLayer != null)
                            {
                                var delta = ImGui.GetIO().MouseDelta;
                                _floatingLayer.Position += new Vector2(delta.X / canvasSize, delta.Y / canvasSize);
                            }
                            else if (_dragHandle == 1 && _floatingLayer != null)
                            {
                                var delta = ImGui.GetIO().MouseDelta;
                                _floatingLayer.Scale += new Vector2(delta.X / canvasSize, delta.Y / canvasSize);
                            }
                            else if (_dragHandle == -1)
                            {
                                PaintAtUV(uv);
                            }
                        }
                        else if (ImGui.IsMouseReleased(ImGuiMouseButton.Left))
                        {
                            _lastUvHit = null;
                            _dragHandle = -1;
                        }
                        else
                        {
                            _lastUvHit = null;
                        }
                    }
                }
            }

            ImGui.Columns(1);

            // GPU composite every frame (microseconds)
            if (_renderer != null && _gpuPaintInitialized && _needsComposite)
            {
                _renderer.GpuComposite(_primarySlotArray);
                if (_floatingLayer != null && _floatingLayer.SRV != null)
                {
                    _renderer.GpuPreviewStampTexture(_floatingLayer.SRV, _floatingLayer.Position, _floatingLayer.Scale);
                }
                _needsComposite = false;
            }
        }

        private void LoadFloatingImage(string path)
        {
            if (!File.Exists(path)) return;
            try
            {
                using var bmp = new System.Drawing.Bitmap(path);
                var rgba = new byte[bmp.Width * bmp.Height * 4];
                var bounds = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                var data = bmp.LockBits(bounds, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                
                unsafe
                {
                    byte* src = (byte*)data.Scan0;
                    for (int i = 0; i < rgba.Length; i += 4)
                    {
                        rgba[i + 0] = src[i + 2]; // R
                        rgba[i + 1] = src[i + 1]; // G
                        rgba[i + 2] = src[i + 0]; // B
                        rgba[i + 3] = src[i + 3]; // A
                    }
                }
                bmp.UnlockBits(data);
                
                _floatingLayer?.Dispose();
                _floatingLayer = new FloatingLayer
                {
                    Width = bmp.Width,
                    Height = bmp.Height,
                    SRV = _renderer.CreateSrvFromRgba(rgba, bmp.Width, bmp.Height),
                    Position = new Vector2(0f, 0f),
                    Scale = new Vector2(0.5f, 0.5f)
                };
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error(ex, "Failed to load floating image.");
            }
        }

        private void PaintAtUV(Vector2 uvHit)
        {
            if (_renderer == null || !_gpuPaintInitialized) return;
            
            if (_activeTool == PaintTool.Eyedropper)
            {
                var color = _renderer.ReadCompositePixel(uvHit);
                _paintColor = new Vector4(color.X, color.Y, color.Z, 1.0f);
                return;
            }

            // Break the stroke if the UV gap is too large (crossing UV island seams)
            Vector2? prev = _lastUvHit;
            if (prev.HasValue && Vector2.Distance(uvHit, prev.Value) > 0.1f)
                prev = null;
            
            int blendMode = _activeTool == PaintTool.Eraser ? 1 : 0;
            int shapeMode = _activeTool == PaintTool.Fill ? 2 : (_activeShape == PaintShape.Square ? 1 : 0);

            float finalAlpha = _paintColor.W * _brushOpacity;
            _renderer.GpuPaintStroke(uvHit, prev, _brushSize, _brushHardness, new Vector4(_paintColor.X, _paintColor.Y, _paintColor.Z, finalAlpha), blendMode, shapeMode);
            _lastUvHit = uvHit;
            _needsComposite = true;
        }

        private void CommitPaintLayer()
        {
            if (_renderer == null || !_gpuPaintInitialized) return;
            
            string importDir = Path.Combine(_plugin.ContextualLayerManager.RootDirectory, "Imports");
            if (!Directory.Exists(importDir)) Directory.CreateDirectory(importDir);
            
            string bodyTag = _isGen3Preview ? "gen3" : _isBiboPreview ? "bibo" : "vanilla";
            string outPath = Path.Combine(importDir, $"{bodyTag}_base_{Guid.NewGuid().ToString().Substring(0, 8)}.png");
            
            _plugin.PluginLog.Info($"[Texture Painter] CommitPaintLayer starting. BodyTag={bodyTag}, OutPath={outPath}");
            
            // Read paint layer back from GPU and save as PNG
            byte[] pixels = _renderer.ReadbackPaintLayer();
            if (pixels != null)
            {
                _plugin.PluginLog.Info($"[Texture Painter] ReadbackPaintLayer returned {pixels.Length} bytes.");
                int w = _renderer.PaintTexWidth;
                int h = _renderer.PaintTexHeight;
                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var rect = new System.Drawing.Rectangle(0, 0, w, h);
                var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                System.Runtime.InteropServices.Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
                bmp.UnlockBits(data);
                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                _plugin.PluginLog.Info($"[Texture Painter] Paint layer saved to: {outPath}");
                
                var targetChar = _plugin.SafeGameObjectManager.LocalPlayer;
                _plugin.PluginLog.Info($"[Texture Painter] LocalPlayer={targetChar?.Name?.TextValue ?? "NULL"}, DragAndDropTextures={(_plugin.DragAndDropTextures != null ? "OK" : "NULL")}");
                if (targetChar != null && _plugin.DragAndDropTextures != null)
                {
                    var characterGameObject = targetChar as Dalamud.Game.ClientState.Objects.Types.ICharacter;
                    if (characterGameObject != null)
                    {
                        _plugin.PluginLog.Info($"[Texture Painter] Calling InjectFilesAndRebuild for '{targetChar.Name.TextValue}' with BodyDragPart.Body");
                        _plugin.DragAndDropTextures.InjectFilesAndRebuild(
                            new List<string> { outPath }, 
                            new KeyValuePair<string, Dalamud.Game.ClientState.Objects.Types.ICharacter>(targetChar.Name.TextValue, characterGameObject), 
                            PenumbraAndGlamourerHelpers.BodyDragPart.Body);
                    }
                    else
                    {
                        _plugin.PluginLog.Error("[Texture Painter] LocalPlayer could not be cast to ICharacter!");
                    }
                }
            }
            else
            {
                _plugin.PluginLog.Error("[Texture Painter] ReadbackPaintLayer returned null!");
            }
            
            _renderer.GpuClearPaint();
            _needsComposite = true;
            IsOpen = false;
        }

        public void Dispose()
        {
            _renderer?.Dispose();
            _cachedBaseBitmap?.Dispose();
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

                UploadBaseTextureToGpu();
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


        private void UploadBaseTextureToGpu()
        {
            if (_renderer == null || string.IsNullOrEmpty(_activeBaseTexturePng)) return;
            try
            {
                if (_cachedBaseBitmapPath != _activeBaseTexturePng || _cachedBaseBitmap == null)
                {
                    _cachedBaseBitmap?.Dispose();
                    _cachedBaseBitmap = new System.Drawing.Bitmap(_activeBaseTexturePng);
                    _cachedBaseBitmapPath = _activeBaseTexturePng;
                }
                int w = _cachedBaseBitmap.Width;
                int h = _cachedBaseBitmap.Height;
                var rect = new System.Drawing.Rectangle(0, 0, w, h);
                var data = _cachedBaseBitmap.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                
                // Init GPU paint system at the texture resolution
                if (!_gpuPaintInitialized)
                {
                    _renderer.InitGpuPaint(w, h);
                    _gpuPaintInitialized = true;
                }
                
                // Upload the base texture to GPU
                _renderer.SetBaseTexture(data.Scan0, w, h);
                _cachedBaseBitmap.UnlockBits(data);
                
                // Initial composite
                _renderer.GpuComposite(_primarySlotArray);
                _needsComposite = false;
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error(ex, "Failed to upload base texture to GPU");
            }
        }
    }
}
