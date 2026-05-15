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
        private bool _isDrawing = false;
        private bool _isGen3Preview = false;
        private bool _isBiboPreview = false;
        private bool _isTbsePreview = false;

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
        private float _brushFlow = 1.0f;
        private float _brushSpacing = 0.15f;      // % of brush diameter between dabs
        private float _brushScatter = 0.0f;        // perpendicular scatter 0-1
        private float _brushAngle = 0.0f;          // rotation in degrees (displayed), stored as radians internally
        private float _brushNoiseScale = 0.0f;     // texture grain frequency
        private float _brushNoiseAmount = 0.0f;    // texture grain strength 0-1
        private float _brushSizeJitter = 0.0f;     // random size variation per dab 0-1
        private float _brushSmoothing = 0.75f;     // stroke stabilizer
        private Vector2 _smoothedMousePos = Vector2.Zero;
        private bool _wasPaintingLastFrame = false;
        private int _brushBlendMode = 0;           // 0=Normal, 1=Eraser, 2=Multiply, 3=Screen, 4=Overlay, 5=SoftLight
        private float _strokeSeed = 0f;            // re-seeded per stroke for noise variation
        private float _strokeDistance = 0f;        // accumulated distance for spacing
        private static readonly Random _rng = new Random();

        private enum PaintTool { Brush, Eraser, Fill, Eyedropper }
        private enum PaintShape { Circle, Square }

        private PaintTool _activeTool = PaintTool.Brush;
        private PaintShape _activeShape = PaintShape.Circle;

        // ── Brush Presets ──
        private int _activePresetIndex = 0;
        private static readonly string[] BlendModeNames = new[] { "Normal", "Eraser", "Multiply", "Screen", "Overlay", "Soft Light" };

        private struct BrushPreset
        {
            public string Name;
            public float Size;
            public float Hardness;
            public float Opacity;
            public float Flow;
            public float Spacing;
            public float Scatter;
            public float Angle;
            public float NoiseScale;
            public float NoiseAmount;
            public float SizeJitter;
            public int BlendMode;
            public PaintShape Shape;
        }

        private static readonly BrushPreset[] Presets = new[]
        {
            new BrushPreset { Name = "Hard Round",   Size = 10f, Hardness = 1.0f, Opacity = 1.0f, Flow = 1.0f,  Spacing = 0.05f, Scatter = 0f,    Angle = 0f, NoiseScale = 0f,   NoiseAmount = 0f,   SizeJitter = 0f,   BlendMode = 0, Shape = PaintShape.Circle },
            new BrushPreset { Name = "Soft Round",   Size = 15f, Hardness = 0.3f, Opacity = 1.0f, Flow = 0.8f,  Spacing = 0.10f, Scatter = 0f,    Angle = 0f, NoiseScale = 0f,   NoiseAmount = 0f,   SizeJitter = 0f,   BlendMode = 0, Shape = PaintShape.Circle },
            new BrushPreset { Name = "Airbrush",     Size = 25f, Hardness = 0.1f, Opacity = 0.6f, Flow = 0.15f, Spacing = 0.05f, Scatter = 0f,    Angle = 0f, NoiseScale = 0f,   NoiseAmount = 0f,   SizeJitter = 0f,   BlendMode = 0, Shape = PaintShape.Circle },
            new BrushPreset { Name = "Pencil",       Size = 3f,  Hardness = 0.9f, Opacity = 1.0f, Flow = 1.0f,  Spacing = 0.02f, Scatter = 0f,    Angle = 0f, NoiseScale = 0f,   NoiseAmount = 0f,   SizeJitter = 0f,   BlendMode = 0, Shape = PaintShape.Circle },
            new BrushPreset { Name = "Chalk",        Size = 12f, Hardness = 0.7f, Opacity = 0.8f, Flow = 0.6f,  Spacing = 0.25f, Scatter = 0.2f,  Angle = 0f, NoiseScale = 0.08f, NoiseAmount = 0.7f, SizeJitter = 0.15f, BlendMode = 0, Shape = PaintShape.Square },
            new BrushPreset { Name = "Splatter",     Size = 20f, Hardness = 0.8f, Opacity = 0.9f, Flow = 1.0f,  Spacing = 0.50f, Scatter = 1.0f,  Angle = 0f, NoiseScale = 0.05f, NoiseAmount = 0.5f, SizeJitter = 0.5f,  BlendMode = 0, Shape = PaintShape.Circle },
            new BrushPreset { Name = "Marker",       Size = 8f,  Hardness = 0.5f, Opacity = 0.7f, Flow = 0.4f,  Spacing = 0.08f, Scatter = 0f,    Angle = 0f, NoiseScale = 0f,   NoiseAmount = 0f,   SizeJitter = 0f,   BlendMode = 0, Shape = PaintShape.Square },
            new BrushPreset { Name = "Stipple",      Size = 6f,  Hardness = 1.0f, Opacity = 1.0f, Flow = 1.0f,  Spacing = 0.60f, Scatter = 0.5f,  Angle = 0f, NoiseScale = 0.12f, NoiseAmount = 0.8f, SizeJitter = 0.3f,  BlendMode = 0, Shape = PaintShape.Circle },
        };
        private int _advancedBrushIndex = 0;
        private bool _hideExtraMeshes = true;
        public string EditSourcePath { get; private set; } = null;  // When non-null, we're editing an existing layer file
        private bool _editLayerLoaded = false;   // Whether we've loaded the source into the paint layer
        private volatile bool _isLoadingModels = false;
        private volatile bool _modelsLoaded = false;

        private class FloatingLayer : IDisposable
        {
            public Vortice.Direct3D11.ID3D11ShaderResourceView SRV;
            public int Width;
            public int Height;
            public Vector2 Position = new Vector2(0f, 0f);
            public Vector2 Scale = new Vector2(0.5f, 0.5f);
            
            public bool Is3DProjected = false;
            public Vector3 DecalCenter = Vector3.Zero;
            public Vector3 DecalNormal = Vector3.UnitY;
            public Vector3 DecalTangent = Vector3.UnitX;
            public Vector3 DecalBitangent = Vector3.UnitZ;

            public void Dispose()
            {
                SRV?.Dispose();
            }
        }
        private FloatingLayer _floatingLayer = null;
        private int _dragHandle = -1;
        private string _importPath = "";

        // ── ImGui File Browser State ──
        private bool _showFileBrowser = false;
        private string _browserCurrentDir = "";
        private string _browserSelectedFile = "";
        private string[] _browserDirs = Array.Empty<string>();
        private string[] _browserFiles = Array.Empty<string>();
        private int _browserSelectedIndex = -1;
        private static readonly HashSet<string> _imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".png", ".jpg", ".jpeg", ".bmp", ".tga", ".gif" };

        private List<(string ModName, string DiskPath)> _overrideTopPathList = new();
        private List<(string ModName, string DiskPath)> _overrideBotPathList = new();
        private int _overrideTopSelectedIndex = 0;
        private int _overrideBotSelectedIndex = 0;
        private string _targetKeyword = null;

        public TexturePaintingWindow(Plugin plugin) : base("Texture Painter", ImGuiWindowFlags.NoScrollbar)
        {
            _plugin = plugin;
            Size = new Vector2(1000, 600);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        public override void OnClose()
        {
            base.OnClose();
            _plugin.WindowSystem.RemoveWindow(this);
            _plugin.TexturePaintingWindows.Remove(this);
            // Defer disposal slightly if needed, but Dalamud handles removed windows safely.
            Dispose();
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
            _isTbsePreview = false;
            _editLayerLoaded = EditSourcePath != null ? false : true; // Need to load if editing
            _needsComposite = true;
            _modelsLoaded = false;
            _isLoadingModels = false;
        }

        /// <summary>
        /// Opens the painter in edit mode, loading the specified image file as the initial paint layer content.
        /// Committing will overwrite this file instead of creating a new one.
        /// </summary>
        public void OpenForEditing(string filePath)
        {
            EditSourcePath = filePath;
            _editLayerLoaded = false;
            IsOpen = true;
        }

        public override void Draw()
        {
            try
            {
                if (!_rendererInitialized)
                {
                    _renderer = new ModelRenderer(800, 600);
                    _rendererInitialized = true;
                    if (!_isLoadingModels && !_modelsLoaded)
                    {
                        _isLoadingModels = true;
                        Task.Run(() => {
                            try {
                                LoadPlayerModels();
                            } catch (Exception ex) {
                                _plugin.PluginLog.Error(ex, "[TexturePainter] Background model load failed");
                            } finally {
                                _modelsLoaded = true;
                                _isLoadingModels = false;
                            }
                        });
                    }
                }
            }
            catch { }

            if (_isLoadingModels)
            {
                float bounce = (float)Math.Abs(Math.Sin(ImGui.GetTime() * 2.0));
                ImGui.ProgressBar(bounce, new Vector2(-1, 0), "Loading player models and textures...");
                ImGui.Spacing();
            }

            // Top side controls
            // ── Preset Selector ──
            ImGui.Text("Brush Preset:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140);
            if (ImGui.BeginCombo("##BrushPreset", Presets[_activePresetIndex].Name))
            {
                for (int i = 0; i < Presets.Length; i++)
                {
                    bool isSelected = (i == _activePresetIndex);
                    if (ImGui.Selectable(Presets[i].Name, isSelected))
                    {
                        _activePresetIndex = i;
                        ApplyPreset(Presets[i]);
                    }
                    if (isSelected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            // ── Tools ──
            ImGui.Text("Tools:");
            ImGui.SameLine();
            if (ImGui.RadioButton("Brush", _activeTool == PaintTool.Brush)) _activeTool = PaintTool.Brush;
            ImGui.SameLine();
            if (ImGui.RadioButton("Eraser", _activeTool == PaintTool.Eraser)) _activeTool = PaintTool.Eraser;
            ImGui.SameLine();
            if (ImGui.RadioButton("Fill", _activeTool == PaintTool.Fill)) _activeTool = PaintTool.Fill;
            ImGui.SameLine();
            if (ImGui.RadioButton("Eyedropper", _activeTool == PaintTool.Eyedropper)) _activeTool = PaintTool.Eyedropper;

            // ── Shape + Blend Mode ──
            ImGui.Text("Shape:");
            ImGui.SameLine();
            if (ImGui.RadioButton("Circle", _activeShape == PaintShape.Circle)) _activeShape = PaintShape.Circle;
            ImGui.SameLine();
            if (ImGui.RadioButton("Square", _activeShape == PaintShape.Square)) _activeShape = PaintShape.Square;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            if (ImGui.BeginCombo("Blend", BlendModeNames[_brushBlendMode]))
            {
                for (int i = 0; i < BlendModeNames.Length; i++)
                {
                    if (ImGui.Selectable(BlendModeNames[i], i == _brushBlendMode))
                        _brushBlendMode = i;
                }
                ImGui.EndCombo();
            }

            ImGui.Separator();

            // ── Primary Sliders ──
            ImGui.ColorEdit4("Brush Color", ref _paintColor, ImGuiColorEditFlags.NoInputs);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.SliderFloat("Size", ref _brushSize, 1f, 50f, "%.1f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.SliderFloat("Hardness", ref _brushHardness, 0f, 1f, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.SliderFloat("Opacity", ref _brushOpacity, 0f, 1f, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.SliderFloat("Flow", ref _brushFlow, 0.01f, 1f, "%.2f");

            // ── Advanced Settings (collapsible) ──
            if (ImGui.TreeNode("Advanced Brush Settings"))
            {
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat("Spacing (%)", ref _brushSpacing, 0.01f, 1f, "%.2f");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat("Scatter", ref _brushScatter, 0f, 1f, "%.2f");

                float angleDeg = _brushAngle * (180f / (float)Math.PI);
                ImGui.SetNextItemWidth(120);
                if (ImGui.SliderFloat("Angle", ref angleDeg, 0f, 360f, "%.0f°"))
                    _brushAngle = angleDeg * ((float)Math.PI / 180f);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat("Size Jitter", ref _brushSizeJitter, 0f, 1f, "%.2f");

                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat("Noise Scale", ref _brushNoiseScale, 0f, 0.3f, "%.3f");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat("Noise Amt", ref _brushNoiseAmount, 0f, 1f, "%.2f");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat("Smoothing", ref _brushSmoothing, 0f, 0.99f, "%.2f");

                ImGui.TreePop();
            }
            
            ImGui.Separator();
            string commitLabel = EditSourcePath != null
                ? "Save & Close"
                : "Commit Paint to Active Layers";
            
            if (ImGui.Button(commitLabel))
            {
                CommitPaintLayer();
            }
            if (EditSourcePath != null)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), $"Editing: {Path.GetFileName(EditSourcePath)}");
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

            // ── Mesh Visibility Toggle ──
            ImGui.SameLine();
            ImGui.Text("  ");
            ImGui.SameLine();
            bool hideExtras = _hideExtraMeshes;
            if (ImGui.Checkbox("Hide Extra Meshes", ref hideExtras))
            {
                _hideExtraMeshes = hideExtras;
                UpdateMeshVisibility();
            }

            // ── Model Override Selectors ──
            if (_overrideTopPathList.Count > 1 || _overrideBotPathList.Count > 1)
            {
                ImGui.Separator();
                if (ImGui.TreeNode("Target Model Selection"))
                {
                    if (_overrideTopPathList.Count > 1)
                    {
                        ImGui.SetNextItemWidth(250);
                        if (ImGui.BeginCombo("Top Model", _overrideTopPathList[_overrideTopSelectedIndex].ModName))
                        {
                            for (int i = 0; i < _overrideTopPathList.Count; i++)
                            {
                                bool isSelected = (i == _overrideTopSelectedIndex);
                                if (ImGui.Selectable(_overrideTopPathList[i].ModName, isSelected))
                                {
                                    _overrideTopSelectedIndex = i;
                                    _renderer?.PushUndoSnapshot();
                                    _modelsLoaded = false;
                                    _isLoadingModels = true;
                                    Task.Run(() => {
                                        try { LoadPlayerModels(); }
                                        catch (Exception ex) { _plugin.PluginLog.Error(ex, "[TexturePainter] Background top model load failed"); }
                                        finally { _modelsLoaded = true; _isLoadingModels = false; }
                                    });
                                    _needsComposite = true;
                                }
                                if (isSelected) ImGui.SetItemDefaultFocus();
                            }
                            ImGui.EndCombo();
                        }
                    }

                    if (_overrideBotPathList.Count > 1)
                    {
                        ImGui.SetNextItemWidth(250);
                        if (ImGui.BeginCombo("Bottom Model", _overrideBotPathList[_overrideBotSelectedIndex].ModName))
                        {
                            for (int i = 0; i < _overrideBotPathList.Count; i++)
                            {
                                bool isSelected = (i == _overrideBotSelectedIndex);
                                if (ImGui.Selectable(_overrideBotPathList[i].ModName, isSelected))
                                {
                                    _overrideBotSelectedIndex = i;
                                    _renderer?.PushUndoSnapshot();
                                    _modelsLoaded = false;
                                    _isLoadingModels = true;
                                    Task.Run(() => {
                                        try { LoadPlayerModels(); }
                                        catch (Exception ex) { _plugin.PluginLog.Error(ex, "[TexturePainter] Background bottom model load failed"); }
                                        finally { _modelsLoaded = true; _isLoadingModels = false; }
                                    });
                                    _needsComposite = true;
                                }
                                if (isSelected) ImGui.SetItemDefaultFocus();
                            }
                            ImGui.EndCombo();
                        }
                    }
                    ImGui.TreePop();
                }
            }

            if (_floatingLayer != null)
            {
                ImGui.Separator();
                if (ImGui.Button("Stamp Floating Layer"))
                {
                    _renderer.PushUndoSnapshot();
                    if (_floatingLayer.Is3DProjected)
                        _renderer.GpuStampTexture(_floatingLayer.SRV, _floatingLayer.Position, _floatingLayer.Scale, true, _floatingLayer.DecalCenter, _floatingLayer.DecalNormal, _floatingLayer.DecalTangent, _floatingLayer.DecalBitangent, _floatingLayer.Scale.X * 0.5f, 0.5f);
                    else
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
                if (ImGui.Button("Load"))
                {
                    string path = _importPath.Trim('\"', ' ', '\'');
                    if (File.Exists(path))
                    {
                        LoadFloatingImage(path);
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button("Browse..."))
                {
                    _showFileBrowser = true;
                    if (string.IsNullOrEmpty(_browserCurrentDir))
                    {
                        _browserCurrentDir = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
                        if (string.IsNullOrEmpty(_browserCurrentDir))
                            _browserCurrentDir = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                    }
                    RefreshBrowserEntries();
                    ImGui.OpenPopup("ImageFileBrowser");
                }

                // ── ImGui File Browser Popup ──
                ImGui.SetNextWindowSize(new Vector2(600, 400), ImGuiCond.FirstUseEver);
                if (ImGui.BeginPopupModal("ImageFileBrowser", ref _showFileBrowser, ImGuiWindowFlags.NoScrollbar))
                {
                    // Navigation bar
                    if (ImGui.Button("^ Up"))
                    {
                        var parent = Directory.GetParent(_browserCurrentDir);
                        if (parent != null)
                        {
                            _browserCurrentDir = parent.FullName;
                            RefreshBrowserEntries();
                        }
                    }
                    ImGui.SameLine();
                    ImGui.Text(_browserCurrentDir);

                    ImGui.Separator();

                    // File list
                    ImGui.BeginChild("BrowserFileList", new Vector2(0, ImGui.GetContentRegionAvail().Y - 35), true);

                    // Show drives if at root
                    if (_browserDirs.Length == 0 && _browserFiles.Length == 0)
                    {
                        foreach (var drive in DriveInfo.GetDrives())
                        {
                            if (!drive.IsReady) continue;
                            string label = $"{drive.Name}  ({drive.DriveType})";
                            if (ImGui.Selectable(label, false, ImGuiSelectableFlags.AllowDoubleClick))
                            {
                                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                                {
                                    _browserCurrentDir = drive.RootDirectory.FullName;
                                    RefreshBrowserEntries();
                                }
                            }
                        }
                    }
                    else
                    {
                        // Directories
                        for (int i = 0; i < _browserDirs.Length; i++)
                        {
                            string dirName = Path.GetFileName(_browserDirs[i]);
                            if (string.IsNullOrEmpty(dirName)) dirName = _browserDirs[i];
                            if (ImGui.Selectable("[DIR] " + dirName, false, ImGuiSelectableFlags.AllowDoubleClick))
                            {
                                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                                {
                                    _browserCurrentDir = _browserDirs[i];
                                    RefreshBrowserEntries();
                                    _browserSelectedFile = "";
                                    _browserSelectedIndex = -1;
                                }
                            }
                        }

                        // Image files
                        for (int i = 0; i < _browserFiles.Length; i++)
                        {
                            string fileName = Path.GetFileName(_browserFiles[i]);
                            bool isSelected = (i == _browserSelectedIndex);
                            if (ImGui.Selectable(fileName, isSelected, ImGuiSelectableFlags.AllowDoubleClick))
                            {
                                _browserSelectedIndex = i;
                                _browserSelectedFile = _browserFiles[i];

                                if (ImGui.IsMouseDoubleClicked(ImGuiMouseButton.Left))
                                {
                                    _importPath = _browserSelectedFile;
                                    LoadFloatingImage(_browserSelectedFile);
                                    _showFileBrowser = false;
                                    ImGui.CloseCurrentPopup();
                                }
                            }
                        }
                    }
                    ImGui.EndChild();

                    // Bottom bar
                    ImGui.Text("Selected: " + (string.IsNullOrEmpty(_browserSelectedFile) ? "(none)" : Path.GetFileName(_browserSelectedFile)));
                    ImGui.SameLine();
                    float buttonWidth = 80;
                    ImGui.SetCursorPosX(ImGui.GetWindowWidth() - buttonWidth * 2 - 20);
                    ImGui.BeginDisabled(string.IsNullOrEmpty(_browserSelectedFile));
                    if (ImGui.Button("Open", new Vector2(buttonWidth, 0)))
                    {
                        _importPath = _browserSelectedFile;
                        LoadFloatingImage(_browserSelectedFile);
                        _showFileBrowser = false;
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    if (ImGui.Button("Cancel", new Vector2(buttonWidth, 0)))
                    {
                        _showFileBrowser = false;
                        ImGui.CloseCurrentPopup();
                    }

                    ImGui.EndPopup();
                }
            }

            float remainingWidth = ImGui.GetWindowWidth() - ImGui.GetStyle().ItemSpacing.X * 2;
            ImGui.BeginChild("MiddlePanel", new Vector2(remainingWidth * 0.5f, 0), true);

            // Middle column: 3D Preview
            if (_renderer != null && _modelsLoaded)
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
                    
                    if (Plugin.DragDropManager.CreateImGuiTarget("TextureDragDrop", out var files, out _))
                    {
                        var file = System.Linq.Enumerable.FirstOrDefault(files, f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase));
                        if (file != null)
                        {
                            _plugin.PluginLog.Information($"[TexturePainter] Dropped file in 3D: {file}");
                            LoadFloatingImage(file);
                            
                            // Try to project immediately at mouse position
                            var mousePos = ImGui.GetMousePos();
                            Vector2 localMousePos = mousePos - cursorPos;
                            if (_renderer.Raycast(localMousePos, out Vector2 uvHit, out string hitSlot, out Vector3 worldHit, out Vector3 hitNormal, _primarySlots))
                            {
                                if (_floatingLayer != null)
                                {
                                    if (ImGui.IsKeyDown(ImGuiKey.ModShift))
                                    {
                                        worldHit.X = 0f;
                                        hitNormal.X = 0f;
                                        if (hitNormal.LengthSquared() > 0.001f) hitNormal = Vector3.Normalize(hitNormal);
                                        else hitNormal = Vector3.UnitZ;
                                    }

                                    _floatingLayer.Position = uvHit - (_floatingLayer.Scale / 2.0f);
                                    _floatingLayer.Is3DProjected = true;
                                    _floatingLayer.DecalCenter = worldHit;
                                    _floatingLayer.DecalNormal = hitNormal;
                                    
                                    Vector3 up = Vector3.UnitY;
                                    if (Math.Abs(Vector3.Dot(hitNormal, up)) > 0.99f) up = Vector3.UnitZ;
                                    _floatingLayer.DecalTangent = Vector3.Normalize(Vector3.Cross(up, hitNormal));
                                    _floatingLayer.DecalBitangent = Vector3.Cross(hitNormal, _floatingLayer.DecalTangent);
                                    
                                    _needsComposite = true;
                                }
                            }
                        }
                    }
                    
                    bool isHovered = ImGui.IsItemHovered();
                    bool isActive = ImGui.IsItemActive();

                    if (isHovered || isActive)
                    {
                        var rawMousePos = ImGui.GetMousePos();
                        if (isActive && ImGui.IsMouseDown(ImGuiMouseButton.Left) && _floatingLayer == null)
                        {
                            if (!_wasPaintingLastFrame) _smoothedMousePos = rawMousePos;
                            else _smoothedMousePos = Vector2.Lerp(_smoothedMousePos, rawMousePos, 1.0f - _brushSmoothing);
                            _wasPaintingLastFrame = true;
                        }
                        else
                        {
                            _wasPaintingLastFrame = false;
                            _smoothedMousePos = rawMousePos;
                        }
                        var mousePos = _smoothedMousePos;
                        if (isHovered && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                        {
                            _renderer.PushUndoSnapshot();
                        }
                        
                        if (isActive)
                        {
                            Vector2 localMousePos = mousePos - cursorPos;
                            if (_renderer.Raycast(localMousePos, out Vector2 uvHit, out string hitSlot, out Vector3 worldHit, out Vector3 hitNormal, _primarySlots))
                            {
                                if (_floatingLayer != null)
                                {
                                    if (ImGui.IsKeyDown(ImGuiKey.ModShift))
                                    {
                                        worldHit.X = 0f;
                                        hitNormal.X = 0f;
                                        if (hitNormal.LengthSquared() > 0.001f) hitNormal = Vector3.Normalize(hitNormal);
                                        else hitNormal = Vector3.UnitZ;
                                    }

                                    _floatingLayer.Position = uvHit - (_floatingLayer.Scale / 2.0f);
                                    _floatingLayer.Is3DProjected = true;
                                    _floatingLayer.DecalCenter = worldHit;
                                    _floatingLayer.DecalNormal = hitNormal;
                                    
                                    Vector3 up = Vector3.UnitY;
                                    if (Math.Abs(Vector3.Dot(hitNormal, up)) > 0.99f) up = Vector3.UnitZ;
                                    _floatingLayer.DecalTangent = Vector3.Normalize(Vector3.Cross(up, hitNormal));
                                    _floatingLayer.DecalBitangent = Vector3.Cross(hitNormal, _floatingLayer.DecalTangent);
                                    
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

            ImGui.EndChild();
            ImGui.SameLine();
            ImGui.BeginChild("RightPanel", new Vector2(0, 0), true);

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

                    bool isHovered2D = ImGui.IsItemHovered();
                    bool isActive2D = ImGui.IsItemActive();
                    
                    if (isHovered2D || isActive2D)
                    {
                        var rawMousePos = ImGui.GetMousePos();
                        if (isActive2D && ImGui.IsMouseDown(ImGuiMouseButton.Left) && _dragHandle == -1 && _floatingLayer == null)
                        {
                            if (!_wasPaintingLastFrame) _smoothedMousePos = rawMousePos;
                            else _smoothedMousePos = Vector2.Lerp(_smoothedMousePos, rawMousePos, 1.0f - _brushSmoothing);
                            _wasPaintingLastFrame = true;
                        }
                        else
                        {
                            _wasPaintingLastFrame = false;
                            _smoothedMousePos = rawMousePos;
                        }
                        var mousePos = _smoothedMousePos;
                        Vector2 localMousePos = mousePos - cursorPos;
                        Vector2 uv = new Vector2(localMousePos.X / canvasSize, localMousePos.Y / canvasSize);

                        if (isHovered2D && ImGui.IsMouseClicked(ImGuiMouseButton.Left))
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
                        
                        if (isActive2D)
                        {
                            if (_dragHandle == 0 && _floatingLayer != null)
                            {
                                var delta = ImGui.GetIO().MouseDelta;
                                if (ImGui.IsKeyDown(ImGuiKey.ModShift)) delta.X = 0f;
                                _floatingLayer.Position += new Vector2(delta.X / canvasSize, delta.Y / canvasSize);
                                _floatingLayer.Is3DProjected = false; // Move in 2D disabled 3D projection
                                _needsComposite = true;
                            }
                            else if (_dragHandle == 1 && _floatingLayer != null)
                            {
                                var delta = ImGui.GetIO().MouseDelta;
                                if (ImGui.IsKeyDown(ImGuiKey.ModShift))
                                {
                                    float aspect = (float)_floatingLayer.Width / _floatingLayer.Height;
                                    float maxDelta = Math.Abs(delta.X) > Math.Abs(delta.Y) ? delta.X : delta.Y;

                                    if (aspect >= 1.0f) 
                                    {
                                        _floatingLayer.Scale.X += maxDelta / canvasSize;
                                        _floatingLayer.Scale.Y = _floatingLayer.Scale.X / aspect;
                                    }
                                    else 
                                    {
                                        _floatingLayer.Scale.Y += maxDelta / canvasSize;
                                        _floatingLayer.Scale.X = _floatingLayer.Scale.Y * aspect;
                                    }
                                }
                                else
                                {
                                    _floatingLayer.Scale += new Vector2(delta.X / canvasSize, delta.Y / canvasSize);
                                }
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

            ImGui.EndChild();

            // GPU composite every frame (microseconds)
            if (_renderer != null && _gpuPaintInitialized && _needsComposite)
            {
                _renderer.GpuComposite(_primarySlotArray);
                if (_floatingLayer != null && _floatingLayer.SRV != null)
                {
                    if (_floatingLayer.Is3DProjected)
                        _renderer.GpuPreviewStampTexture(_floatingLayer.SRV, _floatingLayer.Position, _floatingLayer.Scale, true, _floatingLayer.DecalCenter, _floatingLayer.DecalNormal, _floatingLayer.DecalTangent, _floatingLayer.DecalBitangent, _floatingLayer.Scale.X * 0.5f, 0.5f);
                    else
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

        private void RefreshBrowserEntries()
        {
            _browserSelectedIndex = -1;
            _browserSelectedFile = "";
            try
            {
                if (!Directory.Exists(_browserCurrentDir))
                {
                    _browserDirs = Array.Empty<string>();
                    _browserFiles = Array.Empty<string>();
                    return;
                }
                _browserDirs = Directory.GetDirectories(_browserCurrentDir)
                    .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                _browserFiles = Directory.GetFiles(_browserCurrentDir)
                    .Where(f => _imageExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }
            catch
            {
                // Access denied or other IO error — just show empty
                _browserDirs = Array.Empty<string>();
                _browserFiles = Array.Empty<string>();
            }
        }

        private void ApplyPreset(BrushPreset p)
        {
            _brushSize = p.Size;
            _brushHardness = p.Hardness;
            _brushOpacity = p.Opacity;
            _brushFlow = p.Flow;
            _brushSpacing = p.Spacing;
            _brushScatter = p.Scatter;
            _brushAngle = p.Angle;
            _brushNoiseScale = p.NoiseScale;
            _brushNoiseAmount = p.NoiseAmount;
            _brushSizeJitter = p.SizeJitter;
            _brushBlendMode = p.BlendMode;
            _activeShape = p.Shape;
        }

        private void UpdateMeshVisibility()
        {
            if (_renderer == null) return;
            _renderer.HiddenSlots.Clear();
            if (_hideExtraMeshes)
            {
                foreach (var slot in _renderer.GetAllSlotNames())
                {
                    if (!_primarySlots.Contains(slot))
                        _renderer.HiddenSlots.Add(slot);
                }
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
            
            int blendMode = _activeTool == PaintTool.Eraser ? 1 : _brushBlendMode;
            int shapeMode = _activeTool == PaintTool.Fill ? 2 : (_activeShape == PaintShape.Square ? 1 : 0);
            float finalAlpha = _paintColor.W * _brushOpacity;

            // Seed noise per stroke (reset when mouse is first pressed)
            if (!prev.HasValue)
            {
                _strokeSeed = (float)(_rng.NextDouble() * 1000.0);
                _strokeDistance = 0f;
            }

            // ── CPU-side dab loop with spacing ──
            float diameter = _brushSize * 2f;
            float spacingStep = Math.Max(diameter * _brushSpacing, 0.5f);
            // Convert spacing from pixel to UV space (approximate)
            float texSize = Math.Max(_renderer.PaintTexWidth, _renderer.PaintTexHeight);
            float spacingUV = spacingStep / texSize;

            if (prev.HasValue)
            {
                Vector2 delta = uvHit - prev.Value;
                float segLen = delta.Length();
                if (segLen < 0.0001f)
                {
                    _lastUvHit = uvHit;
                    return;
                }
                Vector2 dir = delta / segLen;
                // Perpendicular for scatter
                Vector2 perp = new Vector2(-dir.Y, dir.X);

                float t = 0f;
                while (t <= segLen)
                {
                    Vector2 dabPos = prev.Value + dir * t;

                    // Scatter offset
                    if (_brushScatter > 0.001f)
                    {
                        float scatterOffset = ((float)_rng.NextDouble() * 2f - 1f) * _brushScatter * _brushSize / texSize;
                        dabPos += perp * scatterOffset;
                    }

                    // Size jitter
                    float dabRadius = _brushSize;
                    if (_brushSizeJitter > 0.001f)
                    {
                        float jitter = 1f - _brushSizeJitter * (float)_rng.NextDouble();
                        dabRadius *= jitter;
                    }

                    float dabSeed = _strokeSeed + _strokeDistance;
                    _renderer.GpuPaintStroke(
                        dabPos, null, dabRadius, _brushHardness,
                        new Vector4(_paintColor.X, _paintColor.Y, _paintColor.Z, finalAlpha),
                        blendMode, shapeMode, _brushFlow, _brushAngle,
                        _brushNoiseScale, _brushNoiseAmount, dabSeed);

                    t += spacingUV;
                    _strokeDistance += spacingStep;
                }
            }
            else
            {
                // First dab of a new stroke
                float dabRadius = _brushSize;
                if (_brushSizeJitter > 0.001f)
                    dabRadius *= (1f - _brushSizeJitter * (float)_rng.NextDouble());

                _renderer.GpuPaintStroke(
                    uvHit, null, dabRadius, _brushHardness,
                    new Vector4(_paintColor.X, _paintColor.Y, _paintColor.Z, finalAlpha),
                    blendMode, shapeMode, _brushFlow, _brushAngle,
                    _brushNoiseScale, _brushNoiseAmount, _strokeSeed);
            }

            _lastUvHit = uvHit;
            _needsComposite = true;
        }

        private void CommitPaintLayer()
        {
            if (_renderer == null || !_gpuPaintInitialized) return;

            bool isEditMode = !string.IsNullOrEmpty(EditSourcePath);
            string outPath;

            if (isEditMode)
            {
                // Edit mode: overwrite the source file
                outPath = EditSourcePath;
                _plugin.PluginLog.Info($"[Texture Painter] Edit mode — will overwrite: {outPath}");
            }
            else
            {
                // New layer mode: create a new file
                string importDir = Path.Combine(_plugin.ContextualLayerManager.RootDirectory, "Imports");
                if (!Directory.Exists(importDir)) Directory.CreateDirectory(importDir);
                string bodyTag = _isGen3Preview ? "gen3" : _isBiboPreview ? "bibo" : _isTbsePreview ? "tbse" : "vanilla";
                outPath = Path.Combine(importDir, $"{bodyTag}_base_{Guid.NewGuid().ToString().Substring(0, 8)}.png");
                _plugin.PluginLog.Info($"[Texture Painter] New layer mode. BodyTag={bodyTag}, OutPath={outPath}");
            }
            
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
                        if (isEditMode)
                        {
                            // Edit mode: rebuild all categories since the file was updated in-place
                            _plugin.PluginLog.Info($"[Texture Painter] Edit mode — triggering full rebuild for '{targetChar.Name.TextValue}'");
                            _plugin.DragAndDropTextures.InjectFilesAndRebuild(
                                new List<string> { outPath },
                                new KeyValuePair<string, Dalamud.Game.ClientState.Objects.Types.ICharacter>(targetChar.Name.TextValue, characterGameObject),
                                PenumbraAndGlamourerHelpers.BodyDragPart.Body);
                        }
                        else
                        {
                            _plugin.PluginLog.Info($"[Texture Painter] Calling InjectFilesAndRebuild for '{targetChar.Name.TextValue}' with BodyDragPart.Body");
                            _plugin.DragAndDropTextures.InjectFilesAndRebuild(
                                new List<string> { outPath },
                                new KeyValuePair<string, Dalamud.Game.ClientState.Objects.Types.ICharacter>(targetChar.Name.TextValue, characterGameObject),
                                PenumbraAndGlamourerHelpers.BodyDragPart.Body);
                        }
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
            EditSourcePath = null;
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

                string overrideTopPath = null;
                string overrideBotPath = null;
                if (!string.IsNullOrEmpty(EditSourcePath))
                {
                    if (_overrideTopPathList.Count == 0 && _overrideBotPathList.Count == 0)
                    {
                        string lowerEditPath = EditSourcePath.ToLower();
                        _targetKeyword = null;
                        if (lowerEditPath.Contains("bibo") || lowerEditPath.Contains("b+") || lowerEditPath.Contains("turali bod") || lowerEditPath.Contains("lavabod") || lowerEditPath.Contains("rue") || lowerEditPath.Contains("yab") || lowerEditPath.Contains("yet another body") || lowerEditPath.Contains("lithe"))
                            _targetKeyword = "bibo";
                        else if (lowerEditPath.Contains("gen3") || lowerEditPath.Contains("tfgen3") || lowerEditPath.Contains("pythia") || lowerEditPath.Contains("exqb") || System.Text.RegularExpressions.Regex.IsMatch(lowerEditPath, @"(^|[^a-z])eve([^a-z]|$)") || lowerEditPath.Contains("gaia") || lowerEditPath.Contains("RiderThicc"))
                            _targetKeyword = "gen3";
                        else if (lowerEditPath.Contains("tbse") || lowerEditPath.Contains("the body se") || lowerEditPath.Contains("hrbody"))
                            _targetKeyword = "tbse";

                        if (_targetKeyword != null)
                        {
                            string checkRaceCode = _targetKeyword == "tbse" ? "c0101" : "c0201";
                            string relativeTop = $"chara/equipment/e0279/model/{checkRaceCode}e0279_top.mdl";
                            string relativeBot = $"chara/equipment/e0279/model/{checkRaceCode}e0279_dwn.mdl";

                            _overrideTopPathList = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.FindAllMeshDiskPathsInModDirectory(_targetKeyword, relativeTop);
                            _overrideBotPathList = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.FindAllMeshDiskPathsInModDirectory(_targetKeyword, relativeBot);
                            int activeBodyType = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(collectionId, ffxivGender, out string _, _plugin);
                            bool activeMatchesLayer = false;
                            if (activeBodyType == 1 && _targetKeyword == "bibo") activeMatchesLayer = true;
                            if (activeBodyType == 2 && _targetKeyword == "gen3") activeMatchesLayer = true;
                            if (activeBodyType == 3 && _targetKeyword == "tbse") activeMatchesLayer = true;

                            if (activeMatchesLayer)
                            {
                                PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collectionId, topPath, out string resolvedTopPath);
                                PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collectionId, botPath, out string resolvedBotPath);
                                
                                _overrideTopSelectedIndex = Math.Max(0, _overrideTopPathList.FindIndex(x => string.Equals(x.DiskPath, resolvedTopPath, StringComparison.OrdinalIgnoreCase)));
                                _overrideBotSelectedIndex = Math.Max(0, _overrideBotPathList.FindIndex(x => string.Equals(x.DiskPath, resolvedBotPath, StringComparison.OrdinalIgnoreCase)));
                            }
                            else
                            {
                                _overrideTopSelectedIndex = 0;
                                _overrideBotSelectedIndex = 0;
                            }
                        }
                    }
                }

                if (_overrideTopPathList.Count > 0 && _overrideTopSelectedIndex >= 0 && _overrideTopSelectedIndex < _overrideTopPathList.Count)
                    overrideTopPath = _overrideTopPathList[_overrideTopSelectedIndex].DiskPath;
                if (_overrideBotPathList.Count > 0 && _overrideBotSelectedIndex >= 0 && _overrideBotSelectedIndex < _overrideBotPathList.Count)
                    overrideBotPath = _overrideBotPathList[_overrideBotSelectedIndex].DiskPath;

                // Load both model slots in parallel since they're independent
                var topSlotPath = overrideTopPath ?? topPath;
                var botSlotPath = overrideBotPath ?? botPath;
                System.Threading.Tasks.Parallel.Invoke(
                    () => LoadModelIntoSlot("Top", topSlotPath, collectionId),
                    () => LoadModelIntoSlot("Bottom", botSlotPath, collectionId)
                );
                UpdateMeshVisibility();

                bool prevOverrideMode = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OverrideMode;
                FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OverrideMode = true;
                PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.PopulateOmniOverrides(collectionId, ffxivGender, ffxivRace, _plugin);
                FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OverrideMode = prevOverrideMode;

                string lowerPath = _topModelDiskPath.ToLower();
                bool isGen3 = lowerPath.Contains("gen3") || lowerPath.Contains("tfgen3") || lowerPath.Contains("pythia") || lowerPath.Contains("exqb") 
                || System.Text.RegularExpressions.Regex.IsMatch(lowerPath, @"(^|[^a-z])eve([^a-z]|$)") || lowerPath.Contains("gaia") || lowerPath.Contains("riderthicc");
                bool isBibo = lowerPath.Contains("bibo") || lowerPath.Contains("b+") || lowerPath.Contains("turali bod") || lowerPath.Contains("lavabod") 
               || lowerPath.Contains("rue") || lowerPath.Contains("yab") || lowerPath.Contains("yet another body") || lowerPath.Contains("lithe");
                bool isTbse = lowerPath.Contains("tbse") || lowerPath.Contains("the body se") || lowerPath.Contains("hrbody");

                if (!isGen3 && !isBibo && !isTbse)
                {
                    int bodyIndex = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(collectionId, ffxivGender, out string detectedName, _plugin);
                    if (bodyIndex == 3) isTbse = true;
                    if (bodyIndex == 2) isGen3 = true;
                    if (bodyIndex == 1) isBibo = true;
                    _plugin.PluginLog.Info($"[PSD Preview] Path didn't contain 'gen3', 'bibo', or 'tbse'. Fallback detection returned: {detectedName} ({(isGen3 ? "Gen3" : isBibo ? "Bibo+" : isTbse ? "TBSE" : "Unknown")})");
                }

                _isGen3Preview = isGen3;
                _isBiboPreview = isBibo;
                _isTbsePreview = isTbse;

                if (string.IsNullOrEmpty(EditSourcePath) && _overrideTopPathList.Count == 0 && _overrideBotPathList.Count == 0)
                {
                    _targetKeyword = null;
                    if (isBibo) _targetKeyword = "bibo";
                    else if (isGen3) _targetKeyword = "gen3";
                    else if (isTbse) _targetKeyword = "tbse";

                    if (_targetKeyword != null)
                    {
                        string checkRaceCode = _targetKeyword == "tbse" ? "c0101" : "c0201";
                        string relativeTop = $"chara/equipment/e0279/model/{checkRaceCode}e0279_top.mdl";
                        string relativeBot = $"chara/equipment/e0279/model/{checkRaceCode}e0279_dwn.mdl";

                        _overrideTopPathList = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.FindAllMeshDiskPathsInModDirectory(_targetKeyword, relativeTop);
                        _overrideBotPathList = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.FindAllMeshDiskPathsInModDirectory(_targetKeyword, relativeBot);
                        
                        _overrideTopSelectedIndex = Math.Max(0, _overrideTopPathList.FindIndex(x => string.Equals(x.DiskPath, _topModelDiskPath, StringComparison.OrdinalIgnoreCase)));
                        _overrideBotSelectedIndex = Math.Max(0, _overrideBotPathList.FindIndex(x => string.Equals(x.DiskPath, _botModelDiskPath, StringComparison.OrdinalIgnoreCase)));
                    }
                }

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
                else if (isTbse && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride != null)
                {
                    baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.Base;
                    normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.Normal;
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
                    else if (FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride != null)
                    {
                        baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.Base;
                        normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.Normal;
                        isTbse = true;
                    }
                }
                bool isEditingNormal = !string.IsNullOrEmpty(EditSourcePath) && 
                    (EditSourcePath.IndexOf("norm", StringComparison.OrdinalIgnoreCase) >= 0 || 
                     EditSourcePath.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0 || 
                     EditSourcePath.EndsWith("_n.png", StringComparison.OrdinalIgnoreCase) ||
                     EditSourcePath.EndsWith("_n.tex", StringComparison.OrdinalIgnoreCase));

                if (isEditingNormal)
                {
                    baseTexPath = normTexPath;
                }

                _plugin.PluginLog.Info($"[PSD Preview] Resolved BaseTexture: {baseTexPath ?? "NULL"}");

                // Load both textures in parallel
                bool baseIsBlack = false;
                bool normIsBlack = false;
                string baseResult = null;
                string normResult = null;
                System.Threading.Tasks.Parallel.Invoke(
                    () => baseResult = TexToTempPng(baseTexPath, out baseIsBlack),
                    () => normResult = TexToTempPng(normTexPath, out normIsBlack)
                );
                _activeBaseTexturePng = baseResult;
                _activeNormalTexturePng = normResult;

                if (_activeBaseTexturePng == null || baseIsBlack)
                {
                    _plugin.PluginLog.Info("[PSD Preview] Base texture from priority mod was missing or fully black. Falling back to DLC underlay skin type.");
                    string modPath = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
                    string dlcPath = Path.Combine(modPath, "LooseTextureCompilerDLC");
                    string dlcBase = null;
                    if (isBibo && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes.Count > 0)
                        dlcBase = Path.Combine(dlcPath, isEditingNormal ? FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes[0].BackupTextures[0].Normal.TrimStart('\\') : FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes[0].BackupTextures[0].Base.TrimStart('\\'));
                    else if (isGen3 && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes.Count > 0)
                        dlcBase = Path.Combine(dlcPath, isEditingNormal ? FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes[0].BackupTextures[0].Normal.TrimStart('\\') : FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes[0].BackupTextures[0].Base.TrimStart('\\'));
                    else if (isTbse && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseSkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseSkinTypes.Count > 0)
                        dlcBase = Path.Combine(dlcPath, isEditingNormal ? FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseSkinTypes[0].BackupTextures[0].Normal.TrimStart('\\') : FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseSkinTypes[0].BackupTextures[0].Base.TrimStart('\\'));

                    _activeBaseTexturePng = TexToTempPng(dlcBase, out baseIsBlack);

                    if (_activeBaseTexturePng == null || baseIsBlack)
                    {
                        _plugin.PluginLog.Info("[PSD Preview] DLC fallback failed. Extracting vanilla texture via Lumina.");
                        int ffxivGenderInt = ffxivGender == 1 ? 1 : 0;
                        string vanillaBodyTexPath = FFXIVLooseTextureCompiler.Racial.RacePaths.GetBodyTexturePath(isEditingNormal ? 1 : 0, ffxivGenderInt, 0, ffxivRace, 0, false);
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
                _plugin.PluginLog.Info($"[PSD Preview] Loading slot '{slot}' with Path: {path}");
                
                string diskPath = path;

                // Try resolving via Penumbra first if it's a game path
                if (!Path.IsPathRooted(path))
                {
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
                }

                System.Collections.Generic.List<ExtractedMesh> meshes = null;

                if (System.IO.File.Exists(diskPath))
                {
                    _plugin.PluginLog.Info($"[PSD Preview] Reading external file from disk: {diskPath}");
                    meshes = MdlParser.ParseFromDisk(diskPath, out var loadStatus);
                    _plugin.PluginLog.Info($"[PSD Preview] Disk parse status: {loadStatus}");
                }
                else
                {
                    _plugin.PluginLog.Warning($"[PSD Preview] Could not find physical file for '{diskPath}'. Skipping Lumina.");
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
                    // Quick IsBlack check by sampling a few pixels instead of loading entire 4K bitmap
                    using (var fs = new FileStream(texPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                    using (var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(fs))
                    {
                        isBlack = IsImageBlackFast(image);
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
                            FFXIVLooseTextureCompiler.ImageProcessing.TexIO.SaveBitmapFast(bitmap, outPath);
                        }
                        return outPath;
                    }
                }
            }
            catch (Exception ex) { _plugin.PluginLog.Error(ex, $"Failed to convert tex to png: {texPath}"); }
            return null;
        }

private bool IsImageBlackFast(SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32> image)
        {
            // Sample a grid of pixels instead of scanning every single one
            int stepX = Math.Max(1, image.Width / 32);
            int stepY = Math.Max(1, image.Height / 32);
            for (int y = 0; y < image.Height; y += stepY)
            {
                for (int x = 0; x < image.Width; x += stepX)
                {
                    var pixel = image[x, y];
                    if (pixel.A > 0 && (pixel.R > 5 || pixel.G > 5 || pixel.B > 5))
                        return false;
                }
            }
            return true;
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

                // Load existing layer into paint layer if editing
                if (!_editLayerLoaded && EditSourcePath != null && File.Exists(EditSourcePath))
                {
                    try
                    {
                        using var editBmp = new System.Drawing.Bitmap(EditSourcePath);
                        // Resize to match paint texture if needed
                        using var resized = new System.Drawing.Bitmap(editBmp, w, h);
                        var editRect = new System.Drawing.Rectangle(0, 0, w, h);
                        var editData = resized.LockBits(editRect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        byte[] rgba = new byte[w * h * 4];
                        unsafe
                        {
                            byte* src = (byte*)editData.Scan0;
                            for (int i = 0; i < rgba.Length; i += 4)
                            {
                                rgba[i + 0] = src[i + 2]; // R (from BGRA B)
                                rgba[i + 1] = src[i + 1]; // G
                                rgba[i + 2] = src[i + 0]; // B (from BGRA R)
                                rgba[i + 3] = src[i + 3]; // A
                            }
                        }
                        resized.UnlockBits(editData);
                        _renderer.LoadPaintLayerFromRgba(rgba, w, h);
                        _plugin.PluginLog.Info($"[Texture Painter] Loaded edit source into paint layer: {EditSourcePath}");
                    }
                    catch (Exception editEx)
                    {
                        _plugin.PluginLog.Error(editEx, $"[Texture Painter] Failed to load edit source: {EditSourcePath}");
                    }
                    _editLayerLoaded = true;
                }
                
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
