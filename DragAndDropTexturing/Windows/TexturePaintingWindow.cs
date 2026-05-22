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
using PenumbraAndGlamourerHelpers;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Tiff;

namespace DragAndDropTexturing.Windows
{
    public class TexturePaintingWindow : Window, IDisposable
    {
        private readonly Plugin _plugin;
        private string _tempDir;
        private readonly Dalamud.Interface.ImGuiFileDialog.FileDialogManager _fileDialogManager = new();
        
        private ModelRenderer _renderer;
        private bool _rendererInitialized = false;
        private System.Collections.Concurrent.ConcurrentQueue<Action> _mainThreadActions = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        private static System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<ExtractedMesh>> _meshCache = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Generic.List<ExtractedMesh>>();
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
        
        private System.Collections.Generic.List<string> _availableMaterials = new System.Collections.Generic.List<string>();
        private string _selectedMaterial = null;
        private string _customMaterialRegex = null;

        private readonly HashSet<string> _primarySlots = new HashSet<string> { "Top", "Bottom", "Hair", "Tail" };
        private string[] _primarySlotArray = new[] { "Top", "Bottom", "Hair", "Tail" };
        private static readonly HashSet<string> _mainModelSlots = new HashSet<string> {
            "Top", "Bottom", "PreviewTop", "PreviewBottom", "Shoes", "Gloves", "Head", "Face", "Hair", "Tail"
        };

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

        private enum PaintTool { Brush, Eraser, Fill, Eyedropper, Liquify, Warp }
        private enum PaintShape { Circle, Square }

        private PaintTool _activeTool = PaintTool.Brush;
        private PaintShape _activeShape = PaintShape.Circle;

        // Brush Presets
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
        private bool _filterWhiteBackgroundOnImport = false;
        public string EditSourcePath { get; set; } = null;  // When non-null, we're editing an existing layer file
        public string ContextCategoryKey { get; set; } = null; // Original category key from UI for precise slot targeting
        
        // Cache fields for thread-safe model loading
        private string _cachedCharacterName = "";
        private string _cachedStateBase64 = "";
        private Guid _cachedCollectionId = Guid.Empty;
        private int _cachedObjectIndex = 0;
        private byte[] _cachedCharacterCustomize = Array.Empty<byte>();
        private string _cachedResolvedTopPath = "";
        private string _cachedResolvedBotPath = "";
        private string _cachedModDirectory = "";
        private int _cachedActiveBodyType = 0;
        private List<DragAndDropTexturing.Equipment.WornEquipmentPiece> _cachedWornGear = null;
        private bool _cachedIsMinion = false;
        private uint _cachedMinionDataId = 0;
        private bool _editLayerLoaded = false;   // Whether we've loaded the source into the paint layer
        private volatile bool _isLoadingModels = false;
        private volatile bool _modelsLoaded = false;
        private List<ExtractedMesh> _loadedMeshes = new List<ExtractedMesh>();
        private volatile string _clothingTexturePngOverride = null; // Set by LoadModelIntoSlot to override body skin

        private class FloatingLayer : IDisposable
        {
            public Vortice.Direct3D11.ID3D11ShaderResourceView SRV;
            public byte[] OriginalRgba;
            public bool IsGlow;
            public int Width;
            public int Height;
            public Vector2 Position = new Vector2(0f, 0f);
            public Vector2 Scale = new Vector2(0.5f, 0.5f);
            
            public int ProjectionMode = 0;
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
        private int _default3DProjectionMode = 1;
        private int _dragHandle = -1;
        private string _importPath = "";
        private bool _vKeyPressedLastFrame = false;

        // ImGui File Browser State
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
        private string _newLayerType = "Base";
        private readonly string[] _newLayerTypes = { "Base", "Normal", "Mask", "Glow" };

        // Procedural Decal Stamp Queue
        private class DecalPixelData
        {
            public byte[] Rgba;
            public int Width;
            public int Height;
        }

        private class ProceduralStampRequest
        {
            public List<string> DecalPaths;
            public int NumStamps;
            public string BodyPart;
            public string UvType; // e.g., bibo, gen3, tbse
            public Action<string> OnComplete; // Called with output PNG path (or null on failure)
            public List<DecalPixelData> LoadedDecals;
        }
        private readonly System.Collections.Concurrent.ConcurrentQueue<ProceduralStampRequest> _pendingStampRequests = new();
        private ActiveProceduralStampJob _activeProceduralJob;

        private sealed class ActiveProceduralStampJob {
            public ProceduralStampRequest Request;
            public List<Vortice.Direct3D11.ID3D11ShaderResourceView> Srvs = new();
            public List<ExtractedMesh> Meshes;
            public int TotalTriangles;
            public int NextStampIndex;
            public enum Phase { Prepare, WaitUvMaps, Stamping, Readback, Complete }
            public Phase CurrentPhase = Phase.Prepare;
        }

        private bool _hasCachedRaycast;
        private Vector2 _cachedRaycastScreenPos;
        private Vector2 _cachedRaycastUv;
        private string _cachedRaycastSlot;
        private Vector3 _cachedRaycastWorldPos;
        private Vector3 _cachedRaycastWorldNormal;

        private bool _isHeadlessMode = false;
        public bool IsHeadlessMode
        {
            get => _isHeadlessMode;
            set
            {
                _isHeadlessMode = value;
                Flags = value 
                    ? ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoInputs | ImGuiWindowFlags.NoNav | ImGuiWindowFlags.NoScrollbar 
                    : ImGuiWindowFlags.NoScrollbar;
            }
        }
        private static readonly Random _proceduralRng = new Random();

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
            _mainThreadActions.Clear();
            _tempDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "Paint_Temp");
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
            lock (_loadedMeshes) { _loadedMeshes.Clear(); }
        }

        public void ClearCanvas()
        {
            _renderer?.GpuClearPaint();
            _needsComposite = true;
        }

        /// <summary>
        /// Opens the painter in edit mode, loading the specified image file as the initial paint layer content.
        /// Committing will overwrite this file instead of creating a new one.
        /// </summary>
        public void OpenForEditing(string filePath, string categoryKey = null)
        {
            EditSourcePath = filePath;
            ContextCategoryKey = categoryKey;
            _editLayerLoaded = false;
            IsOpen = true;
        }

        public override void Draw()
        {
            _fileDialogManager.Draw();
            
            while (_mainThreadActions.TryDequeue(out var action))
            {
                try { action(); }
                catch (Exception ex) { _plugin.PluginLog.Error(ex, "[TexturePainter] Action queue error"); }
            }

            try
            {
                if (!_rendererInitialized)
                {
                    _renderer = new ModelRenderer(800, 600);
                    _rendererInitialized = true;
                    if (!_isLoadingModels && !_modelsLoaded)
                    {
                        StartLoadPlayerModels();
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

            // Process any queued procedural stamp requests (GPU-safe: we're on the ImGui/D3D11 thread)
            _renderer?.ProcessUvBakeUpload();
            ProcessProceduralStamps();

            // In headless mode, skip all UI rendering - we only exist to process stamp requests
            if (IsHeadlessMode) return;

            // Top side controls
            // Preset Selector
            ImGui.Text(Translator.LocalizeUI("Brush Preset:"));
            ImGui.SameLine();
            ImGui.SetNextItemWidth(140);
            if (ImGui.BeginCombo("##BrushPreset", Translator.LocalizeUI(Presets[_activePresetIndex].Name)))
            {
                for (int i = 0; i < Presets.Length; i++)
                {
                    bool isSelected = (i == _activePresetIndex);
                    if (ImGui.Selectable(Translator.LocalizeUI(Presets[i].Name), isSelected))
                    {
                        _activePresetIndex = i;
                        ApplyPreset(Presets[i]);
                    }
                    if (isSelected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }

            // Tools
            ImGui.Text(Translator.LocalizeUI("Tools:"));
            ImGui.SameLine();
            if (ImGui.RadioButton(Translator.LocalizeUI("Brush"), _activeTool == PaintTool.Brush)) _activeTool = PaintTool.Brush;
            ImGui.SameLine();
            if (ImGui.RadioButton(Translator.LocalizeUI("Eraser"), _activeTool == PaintTool.Eraser)) _activeTool = PaintTool.Eraser;
            ImGui.SameLine();
            if (ImGui.RadioButton(Translator.LocalizeUI("Fill"), _activeTool == PaintTool.Fill)) _activeTool = PaintTool.Fill;
            ImGui.SameLine();
            if (ImGui.RadioButton(Translator.LocalizeUI("Eyedropper"), _activeTool == PaintTool.Eyedropper)) _activeTool = PaintTool.Eyedropper;
            ImGui.SameLine();
            if (ImGui.RadioButton(Translator.LocalizeUI("Liquify"), _activeTool == PaintTool.Liquify)) _activeTool = PaintTool.Liquify;
            ImGui.SameLine();
            if (ImGui.RadioButton(Translator.LocalizeUI("Warp"), _activeTool == PaintTool.Warp)) _activeTool = PaintTool.Warp;

            ImGui.Separator();

            // Shape + Blend Mode
            ImGui.Text(Translator.LocalizeUI("Shape:"));
            ImGui.SameLine();
            if (ImGui.RadioButton(Translator.LocalizeUI("Circle"), _activeShape == PaintShape.Circle)) _activeShape = PaintShape.Circle;
            ImGui.SameLine();
            if (ImGui.RadioButton(Translator.LocalizeUI("Square"), _activeShape == PaintShape.Square)) _activeShape = PaintShape.Square;
            ImGui.SameLine();
            ImGui.SetNextItemWidth(100);
            if (ImGui.BeginCombo(Translator.LocalizeUI("Blend"), Translator.LocalizeUI(BlendModeNames[_brushBlendMode])))
            {
                for (int i = 0; i < BlendModeNames.Length; i++)
                {
                    if (ImGui.Selectable(Translator.LocalizeUI(BlendModeNames[i]), i == _brushBlendMode))
                        _brushBlendMode = i;
                }
                ImGui.EndCombo();
            }

            ImGui.Separator();

            // Primary Sliders
            ImGui.ColorEdit4(Translator.LocalizeUI("Brush Color"), ref _paintColor, ImGuiColorEditFlags.NoInputs);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.SliderFloat(Translator.LocalizeUI("Size"), ref _brushSize, 1f, 50f, "%.1f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.SliderFloat(Translator.LocalizeUI("Hardness"), ref _brushHardness, 0f, 1f, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.SliderFloat(Translator.LocalizeUI("Opacity"), ref _brushOpacity, 0f, 1f, "%.2f");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(80);
            ImGui.SliderFloat(Translator.LocalizeUI("Flow"), ref _brushFlow, 0.01f, 1f, "%.2f");

            // Advanced Settings (collapsible)
            if (ImGui.TreeNode(Translator.LocalizeUI("Advanced Brush Settings")))
            {
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat(Translator.LocalizeUI("Spacing (%)"), ref _brushSpacing, 0.01f, 1f, "%.2f");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat(Translator.LocalizeUI("Scatter"), ref _brushScatter, 0f, 1f, "%.2f");

                float angleDeg = _brushAngle * (180f / (float)Math.PI);
                ImGui.SetNextItemWidth(120);
                if (ImGui.SliderFloat(Translator.LocalizeUI("Angle"), ref angleDeg, 0f, 360f, "%.0f°"))
                    _brushAngle = angleDeg * ((float)Math.PI / 180f);
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat(Translator.LocalizeUI("Size Jitter"), ref _brushSizeJitter, 0f, 1f, "%.2f");

                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat(Translator.LocalizeUI("Noise Scale"), ref _brushNoiseScale, 0f, 0.3f, "%.3f");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat(Translator.LocalizeUI("Noise Amt"), ref _brushNoiseAmount, 0f, 1f, "%.2f");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.SliderFloat(Translator.LocalizeUI("Smoothing"), ref _brushSmoothing, 0f, 0.99f, "%.2f");

                ImGui.TreePop();
            }
            
            ImGui.Separator();
            string commitLabel = EditSourcePath != null
                ? "Save & Close"
                : "Commit Paint to Active Layers";
            
            if (ImGui.Button(commitLabel))
            {
                if (_floatingLayer != null)
                {
                    _renderer.PushUndoSnapshot();
                    _renderer.GpuStampTexture(_floatingLayer.SRV, _floatingLayer.Position, _floatingLayer.Scale, _floatingLayer.ProjectionMode, _floatingLayer.DecalCenter, _floatingLayer.DecalNormal, _floatingLayer.DecalTangent, _floatingLayer.DecalBitangent, _floatingLayer.Scale.X * 0.5f, 1f);
                    _floatingLayer.SRV.Dispose();
                    _floatingLayer = null;
                    _needsComposite = true;
                }
                CommitPaintLayer();
            }

            ImGui.SameLine();
            if (ImGui.Button(Translator.LocalizeUI("Export 16-Bit TIFF")))
            {
                _fileDialogManager.SaveFileDialog("Save 16-Bit TIFF", ".tif", "conversion_map.tif", "tif", (b, s) => {
                    if (b) Save16BitTiff(s);
                });
            }

            if (EditSourcePath != null)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.5f, 1f, 0.5f, 1f), $"Editing: {Path.GetFileName(EditSourcePath)}");
            }
            else
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                if (ImGui.BeginCombo("Layer Type", _newLayerType))
                {
                    foreach (var type in _newLayerTypes)
                    {
                        bool isSelected = (_newLayerType == type);
                        if (ImGui.Selectable(type, isSelected))
                        {
                            if (_newLayerType != type)
                            {
                                _newLayerType = type;
                                _renderer?.PushUndoSnapshot();
                                StartLoadPlayerModels();
                                _needsComposite = true;
                            }
                        }
                        if (isSelected) ImGui.SetItemDefaultFocus();
                    }
                    ImGui.EndCombo();
                }
            }
            ImGui.SameLine();
            if (ImGui.Button(Translator.LocalizeUI("Clear Paint")))
            {
                _renderer?.PushUndoSnapshot();
                _renderer?.GpuClearPaint();
                _needsComposite = true;
            }
            ImGui.SameLine();
            ImGui.BeginDisabled(_renderer == null || !_renderer.CanUndo);
            if (ImGui.Button(Translator.LocalizeUI("Undo")))
            {
                _renderer?.Undo();
                _needsComposite = true;
            }
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.BeginDisabled(_renderer == null || !_renderer.CanRedo);
            if (ImGui.Button(Translator.LocalizeUI("Redo")))
            {
                _renderer?.Redo();
                _needsComposite = true;
            }
            ImGui.EndDisabled();

            // Mesh Visibility Toggle
            ImGui.SameLine();
            ImGui.Text(Translator.LocalizeUI("  "));
            ImGui.SameLine();
            bool hideExtras = _hideExtraMeshes;
            if (ImGui.Checkbox(Translator.LocalizeUI("Hide Extra Meshes"), ref hideExtras))
            {
                _hideExtraMeshes = hideExtras;
                UpdateMeshVisibility();
            }

            ImGui.SameLine();
            ImGui.Text(Translator.LocalizeUI("  "));
            ImGui.SameLine();
            if (ImGui.Checkbox(Translator.LocalizeUI("Filter White Background In Stamps"), ref _filterWhiteBackgroundOnImport))
            {
                RefreshFloatingLayerFilter();
            }

            // Model Override Selectors
            if (_overrideTopPathList.Count > 1 || _overrideBotPathList.Count > 1)
            {
                ImGui.Separator();
                if (ImGui.TreeNode(Translator.LocalizeUI("Target Model Selection")))
                {
                    if (_overrideTopPathList.Count > 1)
                    {
                        ImGui.SetNextItemWidth(250);
                        if (ImGui.BeginCombo(Translator.LocalizeUI("Top Model"), _overrideTopPathList[_overrideTopSelectedIndex].ModName))
                        {
                            for (int i = 0; i < _overrideTopPathList.Count; i++)
                            {
                                bool isSelected = (i == _overrideTopSelectedIndex);
                                if (ImGui.Selectable(_overrideTopPathList[i].ModName, isSelected))
                                {
                                    _overrideTopSelectedIndex = i;
                                    _renderer?.PushUndoSnapshot();
                                    StartLoadPlayerModels();
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
                        if (ImGui.BeginCombo(Translator.LocalizeUI("Bottom Model"), _overrideBotPathList[_overrideBotSelectedIndex].ModName))
                        {
                            for (int i = 0; i < _overrideBotPathList.Count; i++)
                            {
                                bool isSelected = (i == _overrideBotSelectedIndex);
                                if (ImGui.Selectable(_overrideBotPathList[i].ModName, isSelected))
                                {
                                    _overrideBotSelectedIndex = i;
                                    _renderer?.PushUndoSnapshot();
                                    StartLoadPlayerModels();
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
                
                ImGui.Text(Translator.LocalizeUI("Projection Mode:"));
                int pMode = _floatingLayer.ProjectionMode;
                if (ImGui.RadioButton(Translator.LocalizeUI("2D Canvas"), ref pMode, 0)) _floatingLayer.ProjectionMode = pMode;
                ImGui.SameLine();
                if (ImGui.RadioButton(Translator.LocalizeUI("3D Tangent"), ref pMode, 1)) { _floatingLayer.ProjectionMode = pMode; _default3DProjectionMode = pMode; }
                ImGui.SameLine();
                if (ImGui.RadioButton(Translator.LocalizeUI("3D Camera"), ref pMode, 2)) { _floatingLayer.ProjectionMode = pMode; _default3DProjectionMode = pMode; }

                if (ImGui.Button(Translator.LocalizeUI("Stamp Floating Layer")))
                {
                    _renderer.PushUndoSnapshot();
                    if (_floatingLayer.ProjectionMode > 0)
                        _renderer.GpuStampTexture(_floatingLayer.SRV, _floatingLayer.Position, _floatingLayer.Scale, _floatingLayer.ProjectionMode, _floatingLayer.DecalCenter, _floatingLayer.DecalNormal, _floatingLayer.DecalTangent, _floatingLayer.DecalBitangent, _floatingLayer.Scale.X * 0.5f, 0.5f);
                    else
                        _renderer.GpuStampTexture(_floatingLayer.SRV, _floatingLayer.Position, _floatingLayer.Scale, 0);
                    _needsComposite = true;
                    _floatingLayer.Dispose();
                    _floatingLayer = null;
                }
                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Discard Floating Layer")))
                {
                    _floatingLayer.Dispose();
                    _floatingLayer = null;
                }
            }
            else
            {
                ImGui.Separator();
                ImGui.Text(Translator.LocalizeUI("Import Image as Floating Layer:"));
                ImGui.InputText("##importpath", ref _importPath, 512);
                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Load")))
                {
                    string path = _importPath.Trim('\"', ' ', '\'');
                    if (File.Exists(path))
                    {
                        LoadFloatingImage(path);
                    }
                }
                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Browse...")))
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

                // ImGui File Browser Popup
                ImGui.SetNextWindowSize(new Vector2(600, 400), ImGuiCond.FirstUseEver);
                if (ImGui.BeginPopupModal("ImageFileBrowser", ref _showFileBrowser, ImGuiWindowFlags.NoScrollbar))
                {
                    // Navigation bar
                    if (ImGui.Button(Translator.LocalizeUI("^ Up")))
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
                    ImGui.Text(Translator.LocalizeUI("Selected: ") + (string.IsNullOrEmpty(_browserSelectedFile) ? "(none)" : Path.GetFileName(_browserSelectedFile)));
                    ImGui.SameLine();
                    float buttonWidth = 80;
                    ImGui.SetCursorPosX(ImGui.GetWindowWidth() - buttonWidth * 2 - 20);
                    ImGui.BeginDisabled(string.IsNullOrEmpty(_browserSelectedFile));
                    if (ImGui.Button(Translator.LocalizeUI("Open"), new Vector2(buttonWidth, 0)))
                    {
                        _importPath = _browserSelectedFile;
                        LoadFloatingImage(_browserSelectedFile);
                        _showFileBrowser = false;
                        ImGui.CloseCurrentPopup();
                    }
                    ImGui.EndDisabled();
                    ImGui.SameLine();
                    if (ImGui.Button(Translator.LocalizeUI("Cancel"), new Vector2(buttonWidth, 0)))
                    {
                        _showFileBrowser = false;
                        ImGui.CloseCurrentPopup();
                    }

                    ImGui.EndPopup();
                }
            }

            float remainingWidth = ImGui.GetWindowWidth() - ImGui.GetStyle().ItemSpacing.X * 2;
            ImGui.BeginChild("MiddlePanel", new Vector2(remainingWidth * 0.5f, 0), true);

            if (_renderer != null)
            {
                ImGui.Text(Translator.LocalizeUI("Snap:"));
                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Front"))) { _renderer.CameraYaw = 0f; _renderer.CameraPitch = 0f; }
                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Back"))) { _renderer.CameraYaw = MathF.PI; _renderer.CameraPitch = 0f; }
                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Left"))) { _renderer.CameraYaw = MathF.PI / 2f; _renderer.CameraPitch = 0f; }
                ImGui.SameLine();
                if (ImGui.Button(Translator.LocalizeUI("Right"))) { _renderer.CameraYaw = -MathF.PI / 2f; _renderer.CameraPitch = 0f; }

                if (_availableMaterials.Count > 0)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(250f);
                    string preview = string.IsNullOrEmpty(_selectedMaterial) ? "Auto-Detect Material" : _selectedMaterial;
                    if (ImGui.BeginCombo("##materialSelector", preview))
                    {
                        if (ImGui.Selectable("Auto-Detect Material", string.IsNullOrEmpty(_selectedMaterial)))
                        {
                            _selectedMaterial = null;
                            _renderer?.PushUndoSnapshot();
                            StartLoadPlayerModels();
                            _needsComposite = true;
                        }
                        
                        lock (_availableMaterials)
                        {
                            foreach (var mat in _availableMaterials)
                            {
                                if (ImGui.Selectable(mat, _selectedMaterial == mat))
                                {
                                    _selectedMaterial = mat;
                                    _renderer?.PushUndoSnapshot();
                                    StartLoadPlayerModels();
                                    _needsComposite = true;
                                }
                            }
                        }
                        ImGui.EndCombo();
                    }
                    if (ImGui.IsItemHovered()) ImGui.SetTooltip("Force the painter to bind to a specific material layer (bypassing filename auto-detection).");
                }

                ImGui.Separator();
            }

            // Middle column: 3D Preview
            if (_renderer != null && _modelsLoaded)
            {
                var region = ImGui.GetContentRegionAvail();
                if (region.X > 0 && region.Y > 0 && ((int)region.X != _renderer.Width || (int)region.Y != _renderer.Height))
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
                            if (TryRaycastCached(localMousePos, out Vector2 uvHit, out string hitSlot, out Vector3 worldHit, out Vector3 hitNormal))
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
                                    if (_floatingLayer.ProjectionMode == 0)
                                        _floatingLayer.ProjectionMode = _default3DProjectionMode;
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

                    bool vKeyPressed = Plugin.KeyState[Dalamud.Game.ClientState.Keys.VirtualKey.V];
                    if (ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows) && ImGui.GetIO().KeyCtrl && vKeyPressed && !_vKeyPressedLastFrame)
                    {
                        TryPasteClipboardImage(cursorPos);
                    }
                    _vKeyPressedLastFrame = vKeyPressed;

                    if (isHovered || isActive)
                    {
                        var rawMousePos = ImGui.GetMousePos();
                        if (isActive && ImGui.IsMouseDown(ImGuiMouseButton.Left) && _floatingLayer == null)
                        {
                            if (!_wasPaintingLastFrame) 
                            {
                                _smoothedMousePos = rawMousePos;
                                _renderer.PushUndoSnapshot();
                                if (_activeTool == PaintTool.Warp) _renderer.BeginWarpStroke();
                            }
                            else 
                            {
                                _smoothedMousePos = Vector2.Lerp(_smoothedMousePos, rawMousePos, 1.0f - _brushSmoothing);
                            }
                            _wasPaintingLastFrame = true;
                        }
                        else
                        {
                            _wasPaintingLastFrame = false;
                            _smoothedMousePos = rawMousePos;
                        }
                        var mousePos = _smoothedMousePos;
                        if (isActive)
                        {
                            Vector2 localMousePos = mousePos - cursorPos;
                            if (TryRaycastCached(localMousePos, out Vector2 uvHit, out string hitSlot, out Vector3 worldHit, out Vector3 hitNormal))
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
                                    if (_floatingLayer.ProjectionMode == 0)
                                        _floatingLayer.ProjectionMode = _default3DProjectionMode;
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
                            _hasCachedRaycast = false;
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
            ImGui.Text(Translator.LocalizeUI("2D UV Canvas"));
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
                            if (!_wasPaintingLastFrame) 
                            {
                                _smoothedMousePos = rawMousePos;
                                _renderer.PushUndoSnapshot();
                                if (_activeTool == PaintTool.Warp) _renderer.BeginWarpStroke();
                            }
                            else 
                            {
                                _smoothedMousePos = Vector2.Lerp(_smoothedMousePos, rawMousePos, 1.0f - _brushSmoothing);
                            }
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

                            if (_dragHandle == -1) 
                            {
                                _renderer.PushUndoSnapshot();
                                if (_activeTool == PaintTool.Warp) _renderer.BeginWarpStroke();
                            }
                        }
                        
                        if (isActive2D)
                        {
                            if (_dragHandle == 0 && _floatingLayer != null)
                            {
                                var delta = ImGui.GetIO().MouseDelta;
                                if (ImGui.IsKeyDown(ImGuiKey.ModShift)) delta.X = 0f;
                                _floatingLayer.Position += new Vector2(delta.X / canvasSize, delta.Y / canvasSize);
                                _floatingLayer.ProjectionMode = 0; // Move in 2D disabled 3D projection
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
                {                    if (_floatingLayer.ProjectionMode > 0)
                    {
                        _renderer.GpuPreviewStampTexture(_floatingLayer.SRV, _floatingLayer.Position, _floatingLayer.Scale, _floatingLayer.ProjectionMode, _floatingLayer.DecalCenter, _floatingLayer.DecalNormal, _floatingLayer.DecalTangent, _floatingLayer.DecalBitangent, _floatingLayer.Scale.X * 0.5f, 0.5f);
                    }
                    else
                    {
                        _renderer.GpuPreviewStampTexture(_floatingLayer.SRV, _floatingLayer.Position, _floatingLayer.Scale, 0);
                    } }
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
                
                bool isGlow = _newLayerType == "Glow" || path.Contains("glow", StringComparison.OrdinalIgnoreCase);
                byte[] originalRgba = new byte[rgba.Length];
                
                unsafe
                {
                    byte* src = (byte*)data.Scan0;
                    for (int i = 0; i < rgba.Length; i += 4)
                    {
                        originalRgba[i + 0] = src[i + 2]; // R
                        originalRgba[i + 1] = src[i + 1]; // G
                        originalRgba[i + 2] = src[i + 0]; // B
                        originalRgba[i + 3] = src[i + 3]; // A
                    }
                    Array.Copy(originalRgba, rgba, rgba.Length);

                    for (int i = 0; i < rgba.Length; i += 4)
                    {
                        if (isGlow)
                        {
                            // Use luminance as alpha so dark areas become transparent
                            // and bright glow areas stay opaque
                            byte lum = (byte)(0.299f * rgba[i + 0] + 0.587f * rgba[i + 1] + 0.114f * rgba[i + 2]);
                            rgba[i + 3] = lum;
                        }
                        else if (_filterWhiteBackgroundOnImport)
                        {
                            int a_r = 255 - rgba[i + 0];
                            int a_g = 255 - rgba[i + 1];
                            int a_b = 255 - rgba[i + 2];
                            int max_a = Math.Max(a_r, Math.Max(a_g, a_b));

                            if (max_a < 20)
                            {
                                rgba[i + 0] = 0;
                                rgba[i + 1] = 0;
                                rgba[i + 2] = 0;
                                rgba[i + 3] = 0;
                            }
                            else
                            {
                                float alphaF = (max_a - 20) / 235.0f;
                                float origAlphaF = max_a / 255.0f;
                                
                                rgba[i + 0] = (byte)Math.Max(0, Math.Min(255, 255 - (255 - rgba[i + 0]) / origAlphaF));
                                rgba[i + 1] = (byte)Math.Max(0, Math.Min(255, 255 - (255 - rgba[i + 1]) / origAlphaF));
                                rgba[i + 2] = (byte)Math.Max(0, Math.Min(255, 255 - (255 - rgba[i + 2]) / origAlphaF));
                                rgba[i + 3] = (byte)(Math.Max(0, Math.Min(255, alphaF * 255.0f)) * (rgba[i + 3] / 255.0f));
                            }
                        }
                    }
                }
                bmp.UnlockBits(data);
                
                _floatingLayer?.Dispose();
                _floatingLayer = new FloatingLayer
                {
                    OriginalRgba = originalRgba,
                    IsGlow = isGlow,
                    Width = bmp.Width,
                    Height = bmp.Height,
                    SRV = _renderer.CreateSrvFromRgba(rgba, bmp.Width, bmp.Height),
                    Position = new Vector2(0f, 0f),
                    Scale = new Vector2(0.5f, 0.5f)
                };
                
                _needsComposite = true;
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error(ex, "Failed to load floating image.");
            }
        }

        private void RefreshFloatingLayerFilter()
        {
            if (_floatingLayer == null || _floatingLayer.OriginalRgba == null) return;

            byte[] rgba = new byte[_floatingLayer.OriginalRgba.Length];
            Array.Copy(_floatingLayer.OriginalRgba, rgba, rgba.Length);

            for (int i = 0; i < rgba.Length; i += 4)
            {
                if (_floatingLayer.IsGlow)
                {
                    byte lum = (byte)(0.299f * rgba[i + 0] + 0.587f * rgba[i + 1] + 0.114f * rgba[i + 2]);
                    rgba[i + 3] = lum;
                }
                else if (_filterWhiteBackgroundOnImport)
                {
                    int a_r = 255 - rgba[i + 0];
                    int a_g = 255 - rgba[i + 1];
                    int a_b = 255 - rgba[i + 2];
                    int max_a = Math.Max(a_r, Math.Max(a_g, a_b));

                    if (max_a < 20)
                    {
                        rgba[i + 0] = 0;
                        rgba[i + 1] = 0;
                        rgba[i + 2] = 0;
                        rgba[i + 3] = 0;
                    }
                    else
                    {
                        float alphaF = (max_a - 20) / 235.0f;
                        float origAlphaF = max_a / 255.0f;
                        
                        rgba[i + 0] = (byte)Math.Max(0, Math.Min(255, 255 - (255 - rgba[i + 0]) / origAlphaF));
                        rgba[i + 1] = (byte)Math.Max(0, Math.Min(255, 255 - (255 - rgba[i + 1]) / origAlphaF));
                        rgba[i + 2] = (byte)Math.Max(0, Math.Min(255, 255 - (255 - rgba[i + 2]) / origAlphaF));
                        rgba[i + 3] = (byte)(Math.Max(0, Math.Min(255, alphaF * 255.0f)) * (rgba[i + 3] / 255.0f));
                    }
                }
            }

            var oldSrv = _floatingLayer.SRV;
            _floatingLayer.SRV = _renderer.CreateSrvFromRgba(rgba, _floatingLayer.Width, _floatingLayer.Height);
            oldSrv?.Dispose();
            
            _needsComposite = true;
        }

        private void TryPasteClipboardImage(Vector2 cursorPos)
        {
            System.Drawing.Image clipboardImage = null;
            var thread = new System.Threading.Thread(() =>
            {
                try
                {
                    if (System.Windows.Forms.Clipboard.ContainsImage())
                    {
                        clipboardImage = System.Windows.Forms.Clipboard.GetImage();
                    }
                    else if (System.Windows.Forms.Clipboard.ContainsFileDropList())
                    {
                        var files = System.Windows.Forms.Clipboard.GetFileDropList();
                        foreach (string file in files)
                        {
                            if (file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || 
                                file.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                file.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
                            {
                                try { clipboardImage = System.Drawing.Image.FromFile(file); break; }
                                catch { }
                            }
                        }
                    }
                }
                catch { }
            });
            thread.SetApartmentState(System.Threading.ApartmentState.STA);
            thread.Start();
            thread.Join();

            if (clipboardImage != null)
            {
                try
                {
                    string tempPath = Path.Combine(Path.GetTempPath(), "DragAndDropTexturing_Clipboard.png");
                    clipboardImage.Save(tempPath, System.Drawing.Imaging.ImageFormat.Png);
                    clipboardImage.Dispose();
                    
                    _plugin.PluginLog.Information($"[TexturePainter] Pasted image from clipboard to {tempPath}");
                    LoadFloatingImage(tempPath);
                    
                    // Try to project immediately at mouse position
                    var mousePos = ImGui.GetMousePos();
                    Vector2 localMousePos = mousePos - cursorPos;
                    if (TryRaycastCached(localMousePos, out Vector2 uvHit, out string hitSlot, out Vector3 worldHit, out Vector3 hitNormal))
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
                            if (_floatingLayer.ProjectionMode == 0)
                                _floatingLayer.ProjectionMode = _default3DProjectionMode;
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
                catch (Exception ex)
                {
                    _plugin.PluginLog.Error(ex, "Failed to paste clipboard image.");
                }
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
                // Access denied or other IO error - just show empty
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
                    if (!_mainModelSlots.Contains(slot))
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
            
            int blendMode = _activeTool == PaintTool.Eraser ? 1 : (_activeTool == PaintTool.Liquify ? 6 : (_activeTool == PaintTool.Warp ? 7 : _brushBlendMode));
            int shapeMode = _activeTool == PaintTool.Fill ? 2 : (_activeShape == PaintShape.Square ? 1 : 0);
            float finalAlpha = _paintColor.W * _brushOpacity;

            // Seed noise per stroke (reset when mouse is first pressed)
            if (!prev.HasValue)
            {
                _strokeSeed = (float)(_rng.NextDouble() * 1000.0);
                _strokeDistance = 0f;
            }

            // CPU-side dab loop with spacing
            float diameter = _brushSize * 2f;
            float spacingStep = Math.Max(diameter * _brushSpacing, 0.5f);
            // Convert spacing from pixel to UV space (approximate)
            float texSize = Math.Max(_renderer.PaintTexWidth, _renderer.PaintTexHeight);
            float spacingUV = spacingStep / texSize;
            
            if (blendMode == 7 && prev.HasValue)
            {
                // Warp tool: Do NOT interpolate into tiny dabs. 
                // We advect the displacement field once per frame to prevent bilinear blurring destroying the field.
                _renderer.GpuPaintStroke(
                    uvHit, prev.Value, _brushSize, _brushHardness,
                    new Vector4(_paintColor.X, _paintColor.Y, _paintColor.Z, finalAlpha),
                    blendMode, shapeMode, _brushFlow, _brushAngle,
                    _brushNoiseScale, _brushNoiseAmount, _strokeSeed);
                
                _lastUvHit = uvHit;
                _needsComposite = true;
                return;
            }

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

                    Vector2? dabPrev = null;
                    if (blendMode == 6 || blendMode == 7)
                        dabPrev = dabPos - dir * spacingUV;

                    float dabSeed = _strokeSeed + _strokeDistance;
                    _renderer.GpuPaintStroke(
                        dabPos, dabPrev, dabRadius, _brushHardness,
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

        private void Save16BitTiff(string outputPath)
        {
            if (_renderer == null) return;
            ushort[] raw16Bit = _renderer.ReadbackPaintLayer16BitRgba();
            if (raw16Bit == null) return;

            int w = _renderer.PaintTexWidth;
            int h = _renderer.PaintTexHeight;

            Task.Run(() =>
            {
                try
                {
                    using (var image = new SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba64>(w, h))
                    {
                        image.ProcessPixelRows(accessor => {
                            for (int y = 0; y < h; y++)
                            {
                                var span = accessor.GetRowSpan(y);
                                for (int x = 0; x < w; x++)
                                {
                                    int idx = (y * w + x) * 4;
                                    span[x] = new SixLabors.ImageSharp.PixelFormats.Rgba64(
                                        raw16Bit[idx + 0],
                                        raw16Bit[idx + 1],
                                        raw16Bit[idx + 2],
                                        raw16Bit[idx + 3]
                                    );
                                }
                            }
                        });
                        image.SaveAsTiff(outputPath);
                    }
                    _plugin.PluginLog.Info($"[Texture Painter] Successfully exported 16-bit TIFF to {outputPath}");
                }
                catch (Exception ex)
                {
                    _plugin.PluginLog.Error(ex, $"[Texture Painter] Failed to export 16-bit TIFF to {outputPath}");
                }
            });
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
                _plugin.PluginLog.Info($"[Texture Painter] Edit mode - will overwrite: {outPath}");
            }
            else
            {
                // New layer mode: create a new file
                string importDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "SavedOverlays");
                if (!Directory.Exists(importDir)) Directory.CreateDirectory(importDir);
                string bodyTag = _cachedIsMinion ? "minion" : _isGen3Preview ? "gen3" : _isBiboPreview ? "bibo" : _isTbsePreview ? "tbse" : "gen2";
                string suffix = "_base";
                if (_newLayerType == "Normal") suffix = "_n";
                else if (_newLayerType == "Mask") suffix = "_m";
                else if (_newLayerType == "Glow") suffix = "_glow";
                outPath = Path.Combine(importDir, $"{bodyTag}{suffix}_{Guid.NewGuid().ToString().Substring(0, 8)}.png");
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
                
                // Force a solid black background for Glow Maps to match external PNGs that load correctly
                if (_newLayerType == "Glow" || outPath.Contains("glow"))
                {
                    unsafe
                    {
                        byte* ptr = (byte*)data.Scan0;
                        int bytes = Math.Abs(data.Stride) * bmp.Height;
                        for (int i = 0; i < bytes; i += 4)
                        {
                            // If fully transparent, set to solid black
                            if (ptr[i + 3] == 0)
                            {
                                ptr[i] = 0;
                                ptr[i + 1] = 0;
                                ptr[i + 2] = 0;
                                ptr[i + 3] = 255;
                            }
                            // If partially transparent, alpha-blend onto black
                            else if (ptr[i + 3] < 255)
                            {
                                float a = ptr[i + 3] / 255f;
                                ptr[i] = (byte)(ptr[i] * a);
                                ptr[i + 1] = (byte)(ptr[i + 1] * a);
                                ptr[i + 2] = (byte)(ptr[i + 2] * a);
                                ptr[i + 3] = 255;
                            }
                        }
                    }
                }
                
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
                            _plugin.PluginLog.Info($"[Texture Painter COMMIT] Edit mode path. ContextCategoryKey={ContextCategoryKey}");
                            _plugin.PluginLog.Info($"[Texture Painter] Edit mode - triggering full rebuild for '{targetChar.Name.TextValue}'");
                            if (!string.IsNullOrEmpty(ContextCategoryKey) && ContextCategoryKey.StartsWith(targetChar.Name.TextValue))
                            {
                                string suffix = ContextCategoryKey.Substring(targetChar.Name.TextValue.Length);
                                _plugin.DragAndDropTextures.ScheduleRegeneration(targetChar.Name.TextValue, new[] { suffix }, true, false);
                            }
                            else
                            {
                                _plugin.DragAndDropTextures.InjectFilesAndRebuild(
                                    new List<string> { outPath },
                                    new KeyValuePair<string, Dalamud.Game.ClientState.Objects.Types.ICharacter>(targetChar.Name.TextValue, characterGameObject),
                                    PenumbraAndGlamourerHelpers.BodyDragPart.Unknown);
                            }
                        }
                        else
                        {
                            _plugin.PluginLog.Info($"[Texture Painter COMMIT] New layer path. ContextCategoryKey={ContextCategoryKey}, targetName={targetChar.Name.TextValue}, startsWith={ContextCategoryKey?.StartsWith(targetChar.Name.TextValue)}, IsMinion={_cachedIsMinion}");
                            if (!string.IsNullOrEmpty(ContextCategoryKey) && ContextCategoryKey.StartsWith(targetChar.Name.TextValue))
                            {
                                _plugin.PluginLog.Info($"[Texture Painter] Appending new layer precisely to {ContextCategoryKey}");
                                if (!_plugin.DragAndDropTextures.TextureHistory.ContainsKey(ContextCategoryKey))
                                {
                                    _plugin.DragAndDropTextures.TextureHistory[ContextCategoryKey] = new List<string>();
                                    _plugin.DragAndDropTextures.TextureHistoryTints[ContextCategoryKey] = new List<System.Numerics.Vector4>();
                                }
                                if (!_plugin.DragAndDropTextures.TextureHistory[ContextCategoryKey].Contains(outPath))
                                {
                                    _plugin.DragAndDropTextures.TextureHistory[ContextCategoryKey].Add(outPath);
                                    _plugin.DragAndDropTextures.TextureHistoryTints[ContextCategoryKey].Add(System.Numerics.Vector4.One);
                                }
                                _plugin.Configuration.Save();
                                
                                // For minions, cache the WornEquipmentPiece so the export path can find it
                                if (_cachedIsMinion && _cachedWornGear != null && _cachedWornGear.Count > 0)
                                {
                                    _plugin.DragAndDropTextures.GearCategoryMeta[ContextCategoryKey] = _cachedWornGear[0];
                                    _plugin.PluginLog.Info($"[Texture Painter] Cached minion WornEquipmentPiece for export: {ContextCategoryKey}");
                                }
                                
                                string suffix = ContextCategoryKey.Substring(targetChar.Name.TextValue.Length);
                                _plugin.DragAndDropTextures.ScheduleRegeneration(targetChar.Name.TextValue, new[] { suffix }, true, false);
                            }
                            else
                            {
                                PenumbraAndGlamourerHelpers.BodyDragPart targetPart = PenumbraAndGlamourerHelpers.BodyDragPart.Body;
                                if (!string.IsNullOrEmpty(ContextCategoryKey))
                                {
                                    if (ContextCategoryKey.Contains("_gear_")) targetPart = PenumbraAndGlamourerHelpers.BodyDragPart.Clothing;
                                    else if (ContextCategoryKey.Contains("_face")) targetPart = PenumbraAndGlamourerHelpers.BodyDragPart.Face;
                                    else if (ContextCategoryKey.Contains("_eyes")) targetPart = PenumbraAndGlamourerHelpers.BodyDragPart.Eyes;
                                    else if (ContextCategoryKey.Contains("_eyebrows")) targetPart = PenumbraAndGlamourerHelpers.BodyDragPart.EyebrowsAndLashes;
                                }
                                _plugin.PluginLog.Info($"[Texture Painter] Calling InjectFilesAndRebuild for '{targetChar.Name.TextValue}' with targetPart={targetPart}");
                                _plugin.DragAndDropTextures.InjectFilesAndRebuild(
                                    new List<string> { outPath },
                                    new KeyValuePair<string, Dalamud.Game.ClientState.Objects.Types.ICharacter>(targetChar.Name.TextValue, characterGameObject),
                                    targetPart);
                            }
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
            _mainThreadActions.Clear();
            var oldRenderer = _renderer;
            _renderer = null;
            
            var oldBitmap = _cachedBaseBitmap;
            _cachedBaseBitmap = null;
            
            var oldLayer = _floatingLayer;
            _floatingLayer = null;

            // Defer DirectX 11 resource disposal by 1 second.
            // This prevents the NVIDIA driver crash (C0000005 in nvwgf2umx.dll)
            // caused by ImGui holding onto the ShaderResourceView pointer 
            // from the last frame's draw list while the window is closing.
            Task.Delay(1000).ContinueWith(_ => 
            {
                oldRenderer?.Dispose();
                oldBitmap?.Dispose();
                oldLayer?.Dispose();
            });
        }

        private void StartLoadPlayerModels()
        {
            if (_isLoadingModels) return;

            Dalamud.Game.ClientState.Objects.Types.ICharacter character = null;
            _cachedIsMinion = false;
            _cachedMinionDataId = 0;
            if (ContextCategoryKey != null && ContextCategoryKey.Contains("_minion_"))
            {
                string charName = ContextCategoryKey.Split('_')[0];
                Dalamud.Game.ClientState.Objects.Types.ICharacter owner = null;
                foreach (var item in _plugin.SafeGameObjectManager)
                {
                    if (item is Dalamud.Game.ClientState.Objects.Types.ICharacter c && c.Name.TextValue == charName)
                    {
                        owner = c;
                        break;
                    }
                }

                if (owner != null)
                {
                    Dalamud.Game.ClientState.Objects.Types.IGameObject bestMinion = null;
                    foreach (var item in _plugin.SafeGameObjectManager)
                    {
                        if (item.ObjectKind == Dalamud.Game.ClientState.Objects.Enums.ObjectKind.Companion)
                        {
                            if (item.OwnerId == owner.GameObjectId)
                            {
                                bestMinion = item as Dalamud.Game.ClientState.Objects.Types.IGameObject;
                                break;
                            }
                            if (bestMinion == null)
                            {
                                bestMinion = item as Dalamud.Game.ClientState.Objects.Types.IGameObject; // Fallback to first found companion
                            }
                        }
                    }

                    if (bestMinion != null)
                    {
                        character = owner; // Keep character as owner for collection/customization inheritance
                        _cachedIsMinion = true;
                        _cachedMinionDataId = bestMinion.DataId;
                        
                        // Update ContextCategoryKey to use the actual minion name for per-minion history
                        try
                        {
                            var companionSheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Companion>();
                            if (companionSheet != null)
                            {
                                var companion = companionSheet.GetRow(bestMinion.DataId);
                                if (companion.RowId != 0)
                                {
                                    string minionName = companion.Singular.ToString().ToLower().Replace(" ", "").Replace("'", "").Replace("-", "");
                                    if (!string.IsNullOrEmpty(minionName))
                                    {
                                        string oldKey = ContextCategoryKey;
                                        ContextCategoryKey = owner.Name.TextValue + "_minion_" + minionName;
                                        _plugin.PluginLog.Info($"[Texture Painter] Updated minion category key: {oldKey} -> {ContextCategoryKey}");
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _plugin.PluginLog.Warning($"[Texture Painter] Failed to resolve minion name for key update: {ex.Message}");
                        }
                    }
                }
            }

            if (character == null)
            {
                character = _plugin.SafeGameObjectManager.LocalPlayer as Dalamud.Game.ClientState.Objects.Types.ICharacter;
                _cachedIsMinion = false;
                _cachedMinionDataId = 0;
            }

            if (character == null)
            {
                _plugin.PluginLog.Warning("[PSD Preview] LocalPlayer is null or not a character when starting load!");
                return;
            }

            try
            {
                _cachedCharacterName = character.Name.TextValue;
                _cachedObjectIndex = character.ObjectIndex;
                _cachedCharacterCustomize = character.Customize.ToArray();

                var stateBase64Result = PenumbraAndGlamourerIpcWrapper.Instance.GetStateBase64.Invoke(character.ObjectIndex);
                _cachedStateBase64 = stateBase64Result.Item2;

                _cachedCollectionId = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;
                _cachedModDirectory = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();

                var customization = PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.CharacterCustomization.ReadCustomization(_cachedStateBase64);
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

                string GetFaceRaceCode(int race, int clan, int gender)
                {
                    int code = 101; 
                    switch (race)
                    {
                        case 1: code = gender == 0 ? 101 : 201; break; // Hyur
                        case 2: code = gender == 0 ? 501 : 601; break; // Elezen
                        case 3: code = gender == 0 ? 1101 : 1201; break; // Lalafell
                        case 4: code = gender == 0 ? 701 : 801; break; // Miqo'te
                        case 5: code = gender == 0 ? 901 : 1011; break; // Roegadyn
                        case 6: code = gender == 0 ? 1301 : 1401; break; // Au Ra
                        case 7: code = gender == 0 ? 1501 : 1601; break; // Hrothgar
                        case 8: code = gender == 0 ? 1701 : 1801; break; // Viera
                    }
                    if (race == 1 && clan == 2) code = gender == 0 ? 301 : 401; // Highlander
                    return $"c{code:D4}";
                }

                string trueRaceCode = GetFfxivModelRaceCode(ffxivRace, ffxivClan, ffxivGender);
                string relativeTop = $"chara/equipment/e0279/model/{trueRaceCode}e0279_top.mdl";
                string relativeBot = $"chara/equipment/e0279/model/{trueRaceCode}e0279_dwn.mdl";

                try
                {
                    PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(_cachedCollectionId, relativeTop, out _cachedResolvedTopPath);
                }
                catch { _cachedResolvedTopPath = ""; }

                try
                {
                    PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(_cachedCollectionId, relativeBot, out _cachedResolvedBotPath);
                }
                catch { _cachedResolvedBotPath = ""; }

                // Detect base body type and populate omni overrides on main thread to be fully safe
                try
                {
                    _cachedActiveBodyType = PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(_cachedCollectionId, ffxivGender, out _, _plugin);
                }
                catch { _cachedActiveBodyType = 0; }

                try
                {
                    bool prevOverrideMode = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OverrideMode;
                    FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OverrideMode = true;
                    PenumbraAndGlamourerHelpers.PenumbraAndGlamourerHelperFunctions.PopulateOmniOverrides(_cachedCollectionId, ffxivGender, ffxivRace, _plugin);
                    FFXIVLooseTextureCompiler.Export.BackupTexturePaths.OverrideMode = prevOverrideMode;
                }
                catch { }
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error(ex, "Failed to cache player customization/collection on main thread");
                return;
            }

            _modelsLoaded = false;
            _isLoadingModels = true;

            Task.Run(() => {
                try 
                {
                    LoadPlayerModelsInternal();
                } 
                catch (Exception ex) 
                {
                    _plugin.PluginLog.Error(ex, "[TexturePainter] Background model load failed");
                } 
                finally 
                {
                    _modelsLoaded = true;
                    _isLoadingModels = false;
                }
            });
        }

        private void LoadPlayerModelsInternal()
        {
            try
            {
                lock (_availableMaterials) { _availableMaterials.Clear(); }

                _plugin.PluginLog.Info($"[PSD Preview] Attempting to load models for {_cachedCharacterName}");

                var customization = PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.CharacterCustomization.ReadCustomization(_cachedStateBase64);
                
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

                string GetFaceRaceCode(int race, int clan, int gender)
                {
                    int code = 101; 
                    switch (race)
                    {
                        case 1: code = gender == 0 ? 101 : 201; break; // Hyur
                        case 2: code = gender == 0 ? 501 : 601; break; // Elezen
                        case 3: code = gender == 0 ? 1101 : 1201; break; // Lalafell
                        case 4: code = gender == 0 ? 701 : 801; break; // Miqo'te
                        case 5: code = gender == 0 ? 901 : 1011; break; // Roegadyn
                        case 6: code = gender == 0 ? 1301 : 1401; break; // Au Ra
                        case 7: code = gender == 0 ? 1501 : 1601; break; // Hrothgar
                        case 8: code = gender == 0 ? 1701 : 1801; break; // Viera
                    }
                    if (race == 1 && clan == 2) code = gender == 0 ? 301 : 401; // Highlander
                    return $"c{code:D4}";
                }

                string trueRaceCode = GetFfxivModelRaceCode(ffxivRace, ffxivClan, ffxivGender);
                _plugin.PluginLog.Info($"[PSD Preview] True FFXIV Model RaceCode resolved to: {trueRaceCode}");

                string topPath = $"chara/equipment/e0279/model/{trueRaceCode}e0279_top.mdl";
                string botPath = $"chara/equipment/e0279/model/{trueRaceCode}e0279_dwn.mdl";
                string glvPath = $"chara/equipment/e0279/model/{trueRaceCode}e0279_glv.mdl";
                string shoPath = $"chara/equipment/e0279/model/{trueRaceCode}e0279_sho.mdl";

                string faceRaceCode = GetFaceRaceCode(ffxivRace, ffxivClan, ffxivGender);
                int faceId = customization.Customize.Face.Value;
                string fCode = $"f{faceId:D4}";
                string facePath = $"chara/human/{faceRaceCode}/obj/face/{fCode}/model/{faceRaceCode}{fCode}_fac.mdl";

                bool isFaceEditLocal = 
                    (!string.IsNullOrEmpty(EditSourcePath) && (EditSourcePath.IndexOf("face", System.StringComparison.OrdinalIgnoreCase) >= 0 || EditSourcePath.IndexOf("fac_", System.StringComparison.OrdinalIgnoreCase) >= 0)) ||
                    (!string.IsNullOrEmpty(ContextCategoryKey) && ContextCategoryKey.IndexOf("_face", System.StringComparison.OrdinalIgnoreCase) >= 0);
                
                if (_cachedIsMinion)
                {
                    topPath = null;
                    botPath = null;
                    glvPath = null;
                    shoPath = null;
                    facePath = null;
                }
                else if (isFaceEditLocal)
                {
                    topPath = null;
                    botPath = null;
                    glvPath = null;
                    shoPath = null;
                }

                Guid collectionId = _cachedCollectionId;
                _plugin.PluginLog.Info($"[PSD Preview] Collection ID: {collectionId}");

                string overrideTopPath = null;
                string overrideBotPath = null;
                bool isGear = false;
                string activeSuffix = null;
                string topSlotName = "Top";
                string botSlotName = "Bottom";
                string glvSlotName = "Gloves";
                string shoSlotName = "Shoes";

                List<DragAndDropTexturing.Equipment.WornEquipmentPiece> wornGear = null;
                try
                {
                    if (_cachedIsMinion)
                    {
                        wornGear = DragAndDropTexturing.Equipment.WornEquipmentResolver.ResolveMinion(_cachedMinionDataId, collectionId, _plugin);
                    }
                    else
                    {
                        wornGear = DragAndDropTexturing.Equipment.WornEquipmentResolver.ResolveWornGear(
                            _cachedCharacterName,
                            _cachedCharacterCustomize,
                            customization,
                            collectionId,
                            _plugin
                        );
                    }
                    _cachedWornGear = wornGear;
                }
                catch (Exception ex)
                {
                    _plugin.PluginLog.Warning($"Failed to pull worn gear: {ex.Message}");
                }

                DragAndDropTexturing.Equipment.WornEquipmentPiece matchedPiece = FindMatchingWornPiece(EditSourcePath, wornGear, collectionId);
                if (matchedPiece != null)
                {
                    isGear = true;
                    string suffix = matchedPiece.SlotKey == "body" ? "top" :
                                    matchedPiece.SlotKey == "legs" ? "dwn" :
                                    matchedPiece.SlotKey == "feet" ? "sho" :
                                    matchedPiece.SlotKey == "hands" ? "glv" :
                                    matchedPiece.SlotKey == "hair" ? "hir" :
                                    matchedPiece.SlotKey == "tail" ? "til" :
                                    matchedPiece.SlotKey == "head" ? "met" : "top";
                    activeSuffix = suffix;

                    string eCode = !string.IsNullOrEmpty(matchedPiece.EquipSetId) ? matchedPiece.EquipSetId : "e0279";

                    _plugin.PluginLog.Info($"[PSD Preview] Matched worn gear piece via helper: {matchedPiece.SlotKey} {eCode} {suffix}");

                    if (suffix == "top")
                    {
                        topSlotName = "Top";
                        topPath = !string.IsNullOrEmpty(matchedPiece?.InternalModelPath) ? matchedPiece.InternalModelPath : $"chara/equipment/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                        botSlotName = "PreviewBottom";
                        botPath = null;
                    }
                    else if (suffix == "dwn")
                    {
                        botSlotName = "Bottom";
                        botPath = !string.IsNullOrEmpty(matchedPiece?.InternalModelPath) ? matchedPiece.InternalModelPath : $"chara/equipment/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                        topSlotName = "PreviewTop";
                        topPath = null;
                    }
                    else if (suffix == "sho")
                    {
                        topSlotName = "Shoes";
                        topPath = !string.IsNullOrEmpty(matchedPiece?.InternalModelPath) ? matchedPiece.InternalModelPath : $"chara/equipment/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                        botPath = null;
                    }
                    else if (suffix == "glv")
                    {
                        topSlotName = "Gloves";
                        topPath = !string.IsNullOrEmpty(matchedPiece?.InternalModelPath) ? matchedPiece.InternalModelPath : $"chara/equipment/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                        botPath = null;
                    }
                    else if (suffix == "met")
                    {
                        topSlotName = "Head";
                        topPath = !string.IsNullOrEmpty(matchedPiece?.InternalModelPath) ? matchedPiece.InternalModelPath : $"chara/equipment/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                        botPath = null;
                    }
                    else if (suffix == "hir")
                    {
                        topSlotName = "Hair";
                        topPath = !string.IsNullOrEmpty(matchedPiece.InternalModelPath) 
                            ? matchedPiece.InternalModelPath 
                            : $"chara/human/{trueRaceCode}/obj/hair/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                        botPath = null;
                    }
                    else if (suffix == "til")
                    {
                        topSlotName = "Tail";
                        topPath = !string.IsNullOrEmpty(matchedPiece.InternalModelPath) 
                            ? matchedPiece.InternalModelPath 
                            : $"chara/human/{trueRaceCode}/obj/tail/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                        botPath = null;
                    }

                    // Load contextual background slots (e.g. legs when editing top, body when editing bottom)
                    if (suffix == "top" && wornGear != null)
                    {
                        var legsPiece = wornGear.FirstOrDefault(p => p.SlotKey == "legs");
                        if (legsPiece != null && !string.IsNullOrEmpty(legsPiece.InternalBasePath))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(legsPiece.InternalBasePath, @"equipment/(e\d+)");
                            if (match.Success)
                            {
                                string legsECode = match.Groups[1].Value;
                                botPath = $"chara/equipment/{legsECode}/model/{trueRaceCode}{legsECode}_dwn.mdl";
                            }
                        }
                    }
                    else if (suffix == "dwn" && wornGear != null)
                    {
                        var bodyPiece = wornGear.FirstOrDefault(p => p.SlotKey == "body");
                        if (bodyPiece != null && !string.IsNullOrEmpty(bodyPiece.InternalBasePath))
                        {
                            var match = System.Text.RegularExpressions.Regex.Match(bodyPiece.InternalBasePath, @"equipment/(e\d+)");
                            if (match.Success)
                            {
                                string bodyECode = match.Groups[1].Value;
                                topPath = $"chara/equipment/{bodyECode}/model/{trueRaceCode}{bodyECode}_top.mdl";
                            }
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(EditSourcePath))
                {
                    var gearMatch = System.Text.RegularExpressions.Regex.Match(Path.GetFileName(EditSourcePath), @"^v\d+_(c\d+)(e\d+)_([a-z]+)_([a-z]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (gearMatch.Success)
                    {
                        string cCode = gearMatch.Groups[1].Value;
                        string eCode = gearMatch.Groups[2].Value;
                        string suffix = gearMatch.Groups[3].Value;
                        
                        _plugin.PluginLog.Info($"[PSD Preview] Detected worn gear edit via fallback regex: {cCode} {eCode} {suffix}");
                        
                        isGear = true;
                        activeSuffix = suffix;
                        
                        if (suffix == "top")
                        {
                            topSlotName = "Top";
                            topPath = $"chara/equipment/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                            botSlotName = "PreviewBottom";
                            botPath = null;
                        }
                        else if (suffix == "dwn")
                        {
                            botSlotName = "Bottom";
                            botPath = $"chara/equipment/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                            topSlotName = "PreviewTop";
                            topPath = null;
                        }
                        else if (suffix == "sho")
                        {
                            topSlotName = "Shoes";
                            topPath = $"chara/equipment/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                            botPath = null;
                        }
                        else if (suffix == "glv")
                        {
                            topSlotName = "Gloves";
                            topPath = $"chara/equipment/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                            botPath = null;
                        }
                        else if (suffix == "met")
                        {
                            topSlotName = "Head";
                            topPath = $"chara/equipment/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                            botPath = null;
                        }
                        else if (suffix == "hir")
                        {
                            topSlotName = "Hair";
                            topPath = !string.IsNullOrEmpty(matchedPiece?.InternalModelPath) 
                                ? matchedPiece.InternalModelPath 
                                : $"chara/human/{trueRaceCode}/obj/hair/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                            botPath = null;
                        }
                        else if (suffix == "til")
                        {
                            topSlotName = "Tail";
                            topPath = !string.IsNullOrEmpty(matchedPiece?.InternalModelPath) 
                                ? matchedPiece.InternalModelPath 
                                : $"chara/human/{trueRaceCode}/obj/tail/{eCode}/model/{trueRaceCode}{eCode}_{suffix}.mdl";
                            botPath = null;
                        }

                        try
                        {
                            if (suffix == "top")
                            {
                                var legsPiece = wornGear?.FirstOrDefault(p => p.SlotKey == "legs");
                                if (legsPiece != null && !string.IsNullOrEmpty(legsPiece.InternalBasePath))
                                {
                                    var match = System.Text.RegularExpressions.Regex.Match(legsPiece.InternalBasePath, @"equipment/(e\d+)");
                                    if (match.Success)
                                    {
                                        string legsECode = match.Groups[1].Value;
                                        botPath = $"chara/equipment/{legsECode}/model/{trueRaceCode}{legsECode}_dwn.mdl";
                                    }
                                }
                            }
                            else if (suffix == "dwn")
                            {
                                var bodyPiece = wornGear?.FirstOrDefault(p => p.SlotKey == "body");
                                if (bodyPiece != null && !string.IsNullOrEmpty(bodyPiece.InternalBasePath))
                                {
                                    var match = System.Text.RegularExpressions.Regex.Match(bodyPiece.InternalBasePath, @"equipment/(e\d+)");
                                    if (match.Success)
                                    {
                                        string bodyECode = match.Groups[1].Value;
                                        topPath = $"chara/equipment/{bodyECode}/model/{trueRaceCode}{bodyECode}_top.mdl";
                                    }
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            _plugin.PluginLog.Warning($"Failed to resolve context slot: {ex.Message}");
                        }
                    }
                }

                if (!string.IsNullOrEmpty(EditSourcePath) && !isGear)
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
                            int activeBodyType = _cachedActiveBodyType;
                            bool activeMatchesLayer = false;
                            if (activeBodyType == 1 && _targetKeyword == "bibo") activeMatchesLayer = true;
                            if (activeBodyType == 2 && _targetKeyword == "gen3") activeMatchesLayer = true;
                            if (activeBodyType == 3 && _targetKeyword == "tbse") activeMatchesLayer = true;

                            if (activeMatchesLayer)
                            {
                                string resolvedTopPath = _cachedResolvedTopPath;
                                string resolvedBotPath = _cachedResolvedBotPath;
                                
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

                lock (_primarySlots)
                {
                    _primarySlots.Clear();
                    if (isGear && activeSuffix != null)
                    {
                        string primarySlot = activeSuffix == "top" ? "Top" :
                                             activeSuffix == "dwn" ? "Bottom" :
                                             activeSuffix == "sho" ? "Shoes" :
                                             activeSuffix == "glv" ? "Gloves" :
                                             activeSuffix == "met" ? "Head" :
                                             activeSuffix == "hir" ? "Hair" :
                                             activeSuffix == "til" ? "Tail" : "Top";
                        _primarySlots.Add(primarySlot);
                        _primarySlotArray = new[] { primarySlot };
                    }
                    else
                    {
                        if (isFaceEditLocal)
                        {
                            _primarySlots.Add("Face");
                            _primarySlotArray = new[] { "Face" };
                        }
                        else
                        {
                            _primarySlots.Add("Top");
                            _primarySlots.Add("Bottom");
                            _primarySlots.Add("Gloves");
                            _primarySlots.Add("Shoes");
                            // DO NOT add Face to primary body slots to avoid texture overrides
                            _primarySlotArray = new[] { "Top", "Bottom", "Gloves", "Shoes" };
                        }
                    }
                }

                bool isEditingNormal = (!string.IsNullOrEmpty(EditSourcePath) && 
                    (EditSourcePath.IndexOf("norm", StringComparison.OrdinalIgnoreCase) >= 0 || 
                     EditSourcePath.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0 || 
                     EditSourcePath.IndexOf("_n_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     EditSourcePath.EndsWith("_n.png", StringComparison.OrdinalIgnoreCase) ||
                     EditSourcePath.EndsWith("_n.tex", StringComparison.OrdinalIgnoreCase))) ||
                     (string.IsNullOrEmpty(EditSourcePath) && _newLayerType == "Normal");

                bool isEditingMask = (!string.IsNullOrEmpty(EditSourcePath) && 
                    (EditSourcePath.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 || 
                     EditSourcePath.IndexOf("_m_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                     EditSourcePath.EndsWith("_m.png", StringComparison.OrdinalIgnoreCase) ||
                     EditSourcePath.EndsWith("_m.tex", StringComparison.OrdinalIgnoreCase))) ||
                     (string.IsNullOrEmpty(EditSourcePath) && _newLayerType == "Mask");

                // Load both model slots in parallel since they're independent
                var topSlotPath = overrideTopPath ?? topPath;
                var botSlotPath = overrideBotPath ?? botPath;
                string matchedMatPath = matchedPiece?.InternalMaterialPath;
                
                System.Threading.Tasks.Parallel.Invoke(
                    () => { if (topSlotPath != null) LoadModelIntoSlot(topSlotName, topSlotPath, collectionId, matchedMatPath); },
                    () => { if (botSlotPath != null) LoadModelIntoSlot(botSlotName, botSlotPath, collectionId, matchedMatPath); },
                    () => { if (!isGear && glvPath != null) LoadModelIntoSlot(glvSlotName, glvPath, collectionId); },
                    () => { if (!isGear && shoPath != null) LoadModelIntoSlot(shoSlotName, shoPath, collectionId); },
                    () => { 
                        if (!isGear && facePath != null && isFaceEditLocal) 
                        {
                            LoadModelIntoSlot("Face", facePath, collectionId); 
                        }
                    }
                );
                _mainThreadActions.Enqueue(() => UpdateMeshVisibility());

                string lowerPath = _topModelDiskPath?.ToLower() ?? "";
                bool isGen3 = false, isBibo = false, isTbse = false;
                
                if (!isGear)
                {
                    isGen3 = lowerPath.Contains("gen3") || lowerPath.Contains("tfgen3") || lowerPath.Contains("pythia") || lowerPath.Contains("exqb") 
                             || System.Text.RegularExpressions.Regex.IsMatch(lowerPath, @"(^|[^a-z])eve([^a-z]|$)") || lowerPath.Contains("gaia") || lowerPath.Contains("riderthicc");
                    isBibo = lowerPath.Contains("bibo") || lowerPath.Contains("b+") || lowerPath.Contains("turali bod") || lowerPath.Contains("lavabod") 
                             || lowerPath.Contains("rue") || lowerPath.Contains("yab") || lowerPath.Contains("yet another body") || lowerPath.Contains("lithe");
                    isTbse = lowerPath.Contains("tbse") || lowerPath.Contains("the body se") || lowerPath.Contains("hrbody");

                    if (!isGen3 && !isBibo && !isTbse)
                    {
                        int bodyIndex = _cachedActiveBodyType;
                        if (bodyIndex == 3) isTbse = true;
                        if (bodyIndex == 2) isGen3 = true;
                        if (bodyIndex == 1) isBibo = true;
                        _plugin.PluginLog.Info($"[PSD Preview] Path didn't contain 'gen3', 'bibo', or 'tbse'. Fallback detection returned: {bodyIndex} ({(isGen3 ? "Gen3" : isBibo ? "Bibo+" : isTbse ? "TBSE" : "Unknown")})");
                    }
                }

                _isGen3Preview = isGen3;
                _isBiboPreview = isBibo;
                _isTbsePreview = isTbse;

                if (!isFaceEditLocal && string.IsNullOrEmpty(EditSourcePath) && _overrideTopPathList.Count == 0 && _overrideBotPathList.Count == 0)
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
                string maskTexPath = null;

                if (matchedPiece != null)
                {
                    baseTexPath = !string.IsNullOrEmpty(matchedPiece.ResolvedBaseDiskPath) ? matchedPiece.ResolvedBaseDiskPath : matchedPiece.InternalBasePath;
                    normTexPath = matchedPiece.InternalNormalPath;
                    maskTexPath = matchedPiece.InternalMaskPath;
                }

                bool isBodySlot = !isGear || (activeSuffix != "hir" && activeSuffix != "til" && activeSuffix != "met");
                
                // Do not forcefully override with human skin textures if we are working with a minion!
                if (!isFaceEditLocal && isBodySlot && !_cachedIsMinion)
                {
                    if (isGen3 && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override != null)
                    {
                        baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.Base;
                        normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.Normal;
                        maskTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.Mask;
                    }
                    else if (isBibo && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride != null)
                    {
                        baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.Base;
                        normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.Normal;
                        maskTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.Mask;
                    }
                    else if (isTbse && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride != null)
                    {
                        baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.Base;
                        normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.Normal;
                        maskTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.Mask;
                    }
                    else
                    {
                        if (FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride != null)
                        {
                            baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.Base;
                            normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.Normal;
                            maskTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboOverride.Mask;
                            isBibo = true;
                        }
                        else if (FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override != null)
                        {
                            baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.Base;
                            normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.Normal;
                            maskTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3Override.Mask;
                            isGen3 = true;
                        }
                        else if (FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride != null)
                        {
                            baseTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.Base;
                            normTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.Normal;
                            maskTexPath = FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseOverride.Mask;
                            isTbse = true;
                        }
                    }
                }
                if (isEditingNormal)
                {
                    baseTexPath = normTexPath;
                }
                else if (isEditingMask)
                {
                    baseTexPath = maskTexPath;
                }

                _plugin.PluginLog.Info($"[PSD Preview] Resolved BaseTexture: {baseTexPath ?? "NULL"}");

                // Load both textures in parallel
                bool baseIsBlack = false;
                bool normIsBlack = false;
                string baseResult = null;
                string normResult = null;
                bool shouldPadToSquare = isFaceEditLocal && ffxivRace == 6;

                System.Threading.Tasks.Parallel.Invoke(
                    () => baseResult = TexToTempPng(baseTexPath, out baseIsBlack, shouldPadToSquare),
                    () => normResult = TexToTempPng(normTexPath, out normIsBlack, shouldPadToSquare)
                );
                _activeBaseTexturePng = baseResult;
                _activeNormalTexturePng = normResult;

                if (_activeBaseTexturePng == null || baseIsBlack)
                {
                    if (!isFaceEditLocal && isBodySlot && !_cachedIsMinion)
                    {
                        _plugin.PluginLog.Info("[PSD Preview] Base texture from priority mod was missing or fully black. Falling back to DLC underlay skin type.");
                        string modPath = _cachedModDirectory;
                        string dlcPath = Path.Combine(modPath, "LooseTextureCompilerDLC");
                        string dlcBase = null;
                        if (isBibo && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes.Count > 0)
                            dlcBase = Path.Combine(dlcPath, isEditingNormal ? FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes[0].BackupTextures[0].Normal.TrimStart('\\') : (isEditingMask ? FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes[0].BackupTextures[0].Mask.TrimStart('\\') : FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes[0].BackupTextures[0].Base.TrimStart('\\')));
                        else if (isGen3 && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes.Count > 0)
                            dlcBase = Path.Combine(dlcPath, isEditingNormal ? FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes[0].BackupTextures[0].Normal.TrimStart('\\') : (isEditingMask ? FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes[0].BackupTextures[0].Mask.TrimStart('\\') : FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes[0].BackupTextures[0].Base.TrimStart('\\')));
                        else if (isTbse && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseSkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseSkinTypes.Count > 0)
                            dlcBase = Path.Combine(dlcPath, isEditingNormal ? FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseSkinTypes[0].BackupTextures[0].Normal.TrimStart('\\') : (isEditingMask ? FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseSkinTypes[0].BackupTextures[0].Mask.TrimStart('\\') : FFXIVLooseTextureCompiler.Export.BackupTexturePaths.TbseSkinTypes[0].BackupTextures[0].Base.TrimStart('\\')));

                        _activeBaseTexturePng = TexToTempPng(dlcBase, out baseIsBlack, shouldPadToSquare);
                    }

                    if (_activeBaseTexturePng == null || baseIsBlack)
                    {
                        if (_cachedIsMinion && !string.IsNullOrEmpty(baseTexPath))
                        {
                            _plugin.PluginLog.Info($"[PSD Preview] Minion base texture missing on disk. Extracting via Lumina: {baseTexPath}");
                            string minionBasePng = ExtractVanillaTexViaLumina(baseTexPath);
                            if (!string.IsNullOrEmpty(minionBasePng))
                            {
                                _activeBaseTexturePng = minionBasePng;
                            }
                        }
                        else if (!isFaceEditLocal && !isBodySlot)
                        {
                            _plugin.PluginLog.Info("[PSD Preview] Skipping vanilla base extraction for non-body slot.");
                        }
                        else
                        {
                            _plugin.PluginLog.Info("[PSD Preview] DLC fallback failed. Extracting vanilla texture via Lumina.");
                            int ffxivGenderInt = ffxivGender == 1 ? 1 : 0;
                            string vanillaBasePng = null;
                            bool isFaceEdit = 
                                (!string.IsNullOrEmpty(EditSourcePath) && (EditSourcePath.IndexOf("face", System.StringComparison.OrdinalIgnoreCase) >= 0 || EditSourcePath.IndexOf("fac_", System.StringComparison.OrdinalIgnoreCase) >= 0)) ||
                                (!string.IsNullOrEmpty(ContextCategoryKey) && ContextCategoryKey.IndexOf("_face", System.StringComparison.OrdinalIgnoreCase) >= 0);
                            if (isFaceEdit)
                            {
                                int subRaceValue = Math.Max(0, ffxivClan - 1);
                                int faceType = Math.Max(0, faceId - 1);
                                int materialType = isEditingNormal ? 1 : (isEditingMask ? 2 : 0);
                                string fpAsym = FFXIVLooseTextureCompiler.Racial.RacePaths.GetFacePath(materialType, ffxivGenderInt, subRaceValue, 0, faceType, 0, true);
                                vanillaBasePng = ExtractVanillaTexViaLumina(fpAsym, shouldPadToSquare);
                                if (string.IsNullOrEmpty(vanillaBasePng))
                                {
                                    string fpOld = FFXIVLooseTextureCompiler.Racial.RacePaths.GetFacePath(materialType, ffxivGenderInt, subRaceValue, 0, faceType, 0, false);
                                    vanillaBasePng = ExtractVanillaTexViaLumina(fpOld, shouldPadToSquare);
                                }
                            }
                            else
                            {
                                string vanillaBodyTexPath = FFXIVLooseTextureCompiler.Racial.RacePaths.GetBodyTexturePath(isEditingNormal ? 1 : (isEditingMask ? 2 : 0), ffxivGenderInt, 0, ffxivRace, 0, false);
                                vanillaBasePng = ExtractVanillaTexViaLumina(vanillaBodyTexPath);
                            }
                            if (!string.IsNullOrEmpty(vanillaBasePng))
                            {
                                _activeBaseTexturePng = vanillaBasePng;
                            }
                        }
                    }
                }

                if (_activeNormalTexturePng == null || normIsBlack)
                {
                    if (!isFaceEditLocal && isBodySlot && !_cachedIsMinion)
                    {
                        string modPath = _cachedModDirectory;
                        string dlcPath = Path.Combine(modPath, "LooseTextureCompilerDLC");
                        string dlcNorm = null;
                        if (isBibo && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes.Count > 0)
                            dlcNorm = Path.Combine(dlcPath, FFXIVLooseTextureCompiler.Export.BackupTexturePaths.BiboSkinTypes[0].BackupTextures[0].Normal.TrimStart('\\'));
                        else if (isGen3 && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes != null && FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes.Count > 0)
                            dlcNorm = Path.Combine(dlcPath, FFXIVLooseTextureCompiler.Export.BackupTexturePaths.Gen3SkinTypes[0].BackupTextures[0].Normal.TrimStart('\\'));

                        _activeNormalTexturePng = TexToTempPng(dlcNorm, out normIsBlack, shouldPadToSquare);
                    }

                    if (_activeNormalTexturePng == null || normIsBlack)
                    {
                        if (_cachedIsMinion && !string.IsNullOrEmpty(normTexPath))
                        {
                            _plugin.PluginLog.Info($"[PSD Preview] Minion normal texture missing on disk. Extracting via Lumina: {normTexPath}");
                            string minionNormPng = ExtractVanillaTexViaLumina(normTexPath);
                            if (!string.IsNullOrEmpty(minionNormPng))
                            {
                                _activeNormalTexturePng = minionNormPng;
                            }
                        }
                        else if (!isFaceEditLocal && !isBodySlot)
                        {
                            _plugin.PluginLog.Info("[PSD Preview] Skipping vanilla normal extraction for non-body slot.");
                        }
                        else
                        {
                            int ffxivGenderInt = ffxivGender == 1 ? 1 : 0;
                            string vanillaNormPng = null;
                            bool isFaceEdit = 
                                (!string.IsNullOrEmpty(EditSourcePath) && (EditSourcePath.IndexOf("face", System.StringComparison.OrdinalIgnoreCase) >= 0 || EditSourcePath.IndexOf("fac_", System.StringComparison.OrdinalIgnoreCase) >= 0)) ||
                                (!string.IsNullOrEmpty(ContextCategoryKey) && ContextCategoryKey.IndexOf("_face", System.StringComparison.OrdinalIgnoreCase) >= 0);
                            if (isFaceEdit)
                            {
                                int subRaceValue = Math.Max(0, ffxivClan - 1);
                                int faceType = Math.Max(0, faceId - 1);
                                string fpAsym = FFXIVLooseTextureCompiler.Racial.RacePaths.GetFacePath(1, ffxivGenderInt, subRaceValue, 0, faceType, 0, true);
                                vanillaNormPng = ExtractVanillaTexViaLumina(fpAsym, shouldPadToSquare);
                                if (string.IsNullOrEmpty(vanillaNormPng))
                                {
                                    string fpOld = FFXIVLooseTextureCompiler.Racial.RacePaths.GetFacePath(1, ffxivGenderInt, subRaceValue, 0, faceType, 0, false);
                                    vanillaNormPng = ExtractVanillaTexViaLumina(fpOld, shouldPadToSquare);
                                }
                            }
                            else
                            {
                                string vanillaNormTexPath = FFXIVLooseTextureCompiler.Racial.RacePaths.GetBodyTexturePath(1, ffxivGenderInt, 0, ffxivRace, 0, false);
                                vanillaNormPng = ExtractVanillaTexViaLumina(vanillaNormTexPath);
                            }
                            if (!string.IsNullOrEmpty(vanillaNormPng))
                            {
                                _activeNormalTexturePng = vanillaNormPng;
                            }
                        }
                    }
                }

                // If LoadModelIntoSlot found a clothing texture, use it instead of the body skin
                if (!string.IsNullOrEmpty(_clothingTexturePngOverride))
                {
                    _plugin.PluginLog.Info($"[PSD Preview] Overriding body skin with clothing texture: {_clothingTexturePngOverride}");
                    _activeBaseTexturePng = _clothingTexturePngOverride;
                    _clothingTexturePngOverride = null;
                }

                _mainThreadActions.Enqueue(() => {
                    UploadBaseTextureToGpu();
                });
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error(ex, "Failed to load player models for PSD 3D preview");
            }
        }

        private void LoadModelIntoSlot(string slot, string path, Guid collectionId, string internalMaterialPath = null)
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
                    if (_meshCache.TryGetValue(diskPath, out var cachedMeshes))
                    {
                        _plugin.PluginLog.Info($"[PSD Preview] Loaded external file from cache: {diskPath}");
                        meshes = cachedMeshes;
                    }
                    else
                    {
                        _plugin.PluginLog.Info($"[PSD Preview] Reading external file from disk: {diskPath}");
                        meshes = MdlParser.ParseFromDisk(diskPath, out var loadStatus);
                        _plugin.PluginLog.Info($"[PSD Preview] Disk parse status: {loadStatus}");
                        
                        if (meshes != null)
                        {
                            _meshCache[diskPath] = meshes;
                        }
                    }
                }
                else
                {
                    _plugin.PluginLog.Warning($"[PSD Preview] Could not find physical file for '{diskPath}'. Falling back to Lumina FFXIV data.");
                    var ffxivFile = DragAndDropTexturing.Plugin.DataManager.GetFile(path);
                    if (ffxivFile != null && ffxivFile.Data != null)
                    {
                        meshes = MdlParser.ParseFromBytes(ffxivFile.Data, out string parseStatus);
                        _plugin.PluginLog.Info($"[PSD Preview] Successfully parsed FFXIV native model from bytes: {parseStatus}");
                    }
                    else
                    {
                        _plugin.PluginLog.Error($"[PSD Preview] Could not find FFXIV native file for '{path}'.");
                    }
                }

                if (slot == "Top") _topModelDiskPath = diskPath;
                if (slot == "Bottom") _botModelDiskPath = diskPath;

                if (meshes != null && meshes.Count > 0)
                {
                    _plugin.PluginLog.Info($"[PSD Preview] Successfully loaded {meshes.Count} meshes into slot '{slot}'. Slicing base to '{slot}', extras to '{slot}_N'.");
                    var meshesCopy = new System.Collections.Generic.List<ExtractedMesh>(meshes);
                    // Store CPU mesh data for procedural stamp triangle selection
                    lock (_loadedMeshes) { _loadedMeshes.AddRange(meshesCopy); }
                    
                    lock (_availableMaterials)
                    {
                        foreach (var m in meshesCopy)
                        {
                            if (!string.IsNullOrEmpty(m.MaterialPath))
                            {
                                string cleanMat = m.MaterialPath.Replace("/mt_", "").Replace(".mtrl", "");
                                if (!_availableMaterials.Contains(cleanMat)) _availableMaterials.Add(cleanMat);
                            }
                        }
                    }

                    int primaryIndex = 0;
                    string searchPattern = null;
                    string autoDetectedMaterial = null;

                    if (!string.IsNullOrEmpty(_selectedMaterial))
                    {
                        searchPattern = _selectedMaterial.ToLower();
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(EditSourcePath))
                        {
                            _plugin.PluginLog.Info($"[PSD Preview] EditSourcePath provided: {EditSourcePath}");
                        }

                        if (string.IsNullOrEmpty(searchPattern))
                        {
                            var matchedPiece = FindMatchingWornPiece(EditSourcePath, _cachedWornGear, collectionId);
                            if (matchedPiece != null)
                            {
                                string eCode = !string.IsNullOrEmpty(matchedPiece.EquipSetId) ? matchedPiece.EquipSetId : "e0279";
                                string suffix = matchedPiece.SlotKey == "body" ? "top" :
                                                matchedPiece.SlotKey == "legs" ? "dwn" :
                                                matchedPiece.SlotKey == "feet" ? "sho" :
                                                matchedPiece.SlotKey == "hands" ? "glv" :
                                                matchedPiece.SlotKey == "hair" ? "hir" :
                                                matchedPiece.SlotKey == "tail" ? "til" :
                                                matchedPiece.SlotKey == "head" ? "met" : "top";
                                
                                // For hair and tail, TryResolveHairPieces sets DisplayName which includes the material (e.g. "Hair 126 (B)")
                                // But MaterialName might be null. Let's try to extract it from DisplayName if it's not set.
                                string matSuffix = !string.IsNullOrEmpty(matchedPiece.MaterialName) ? matchedPiece.MaterialName.ToLower() : "";
                                if (string.IsNullOrEmpty(matSuffix) && (matchedPiece.SlotKey == "hair" || matchedPiece.SlotKey == "tail"))
                                {
                                    var matMatch = System.Text.RegularExpressions.Regex.Match(matchedPiece.DisplayName, @"\((.+?)\)");
                                    if (matMatch.Success)
                                    {
                                        matSuffix = matMatch.Groups[1].Value.ToLower();
                                    }
                                }

                                if (!string.IsNullOrEmpty(matchedPiece.InternalMaterialPath))
                                {
                                    searchPattern = System.IO.Path.GetFileNameWithoutExtension(matchedPiece.InternalMaterialPath).ToLower();
                                }
                                else if (!string.IsNullOrEmpty(matSuffix))
                                {
                                    searchPattern = $"{eCode}_{suffix}_{matSuffix}".ToLower();
                                }
                                else
                                {
                                    searchPattern = $"{eCode}_{suffix}".ToLower();
                                }
                                autoDetectedMaterial = matSuffix;
                            }
                            
                            if (string.IsNullOrEmpty(autoDetectedMaterial) && !string.IsNullOrEmpty(EditSourcePath))
                            {
                                autoDetectedMaterial = System.IO.Path.GetFileNameWithoutExtension(EditSourcePath).Split('_').LastOrDefault();
                            }
                        }
                    }

                    if (slot == "Face")
                    {
                        searchPattern = "fac_a";
                    }

                    bool isEditingNormal = !string.IsNullOrEmpty(EditSourcePath) && 
                        (EditSourcePath.IndexOf("norm", StringComparison.OrdinalIgnoreCase) >= 0 || 
                         EditSourcePath.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0 || 
                         EditSourcePath.IndexOf("_n_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         EditSourcePath.EndsWith("_n.png", StringComparison.OrdinalIgnoreCase) ||
                         EditSourcePath.EndsWith("_n.tex", StringComparison.OrdinalIgnoreCase));

                    var primaryMeshes = new System.Collections.Generic.List<ExtractedMesh>();
                    var secondaryMeshes = new System.Collections.Generic.List<ExtractedMesh>();

                    if (searchPattern != null || autoDetectedMaterial != null)
                    {
                        for (int i = 0; i < meshesCopy.Count; i++)
                        {
                            string matPath = meshesCopy[i].MaterialPath;
                            bool isMatch = false;
                            
                            if (!string.IsNullOrEmpty(matPath))
                            {
                                if (searchPattern != null && matPath.ToLower().Contains(searchPattern))
                                {
                                    isMatch = true;
                                }
                                else if (!string.IsNullOrEmpty(autoDetectedMaterial))
                                {
                                    string[] parts = matPath.Replace(".mtrl", "").Split('_');
                                    if (parts.Length > 0 && parts.Last().Equals(autoDetectedMaterial, StringComparison.OrdinalIgnoreCase))
                                    {
                                        isMatch = true;
                                    }
                                }
                            }
                            
                            if (isMatch)
                            {
                                primaryMeshes.Add(meshesCopy[i]);
                            }
                            else
                            {
                                secondaryMeshes.Add(meshesCopy[i]);
                            }
                        }
                        
                        if (primaryMeshes.Count == 0)
                        {
                            _plugin.PluginLog.Warning($"[PSD Preview] Could not find any mesh matching '{searchPattern}' in MaterialPaths. Defaulting to Mesh[0].");
                            primaryMeshes.Add(meshesCopy[0]);
                            for (int i = 1; i < meshesCopy.Count; i++) secondaryMeshes.Add(meshesCopy[i]);
                        }

                        // Dynamically reload the texture from the active material's .mtrl file
                        if (primaryMeshes.Count > 0)
                        {
                            string rawMatPath = primaryMeshes[0].MaterialPath;
                            _plugin.PluginLog.Info($"[Texture Painter] Attempting dynamic texture reload from material: {rawMatPath}");
                            
                            // Strip leading slash
                            string matFileName = rawMatPath;
                            if (matFileName.StartsWith("/")) matFileName = matFileName.Substring(1);
                            
                            // Extract equipment code from the material filename
                            var mtrlMatch = System.Text.RegularExpressions.Regex.Match(matFileName, @"mt_c(\d+)([eht])(\d+)");
                            
                            // Build candidate mtrl paths to try
                            var mtrlCandidates = new List<string>();
                            if (mtrlMatch.Success)
                            {
                                string cCode = "c" + mtrlMatch.Groups[1].Value;
                                string typeCode = mtrlMatch.Groups[2].Value;
                                string codeStr = typeCode + mtrlMatch.Groups[3].Value;

                                if (typeCode == "e")
                                {
                                    for (int v = 1; v <= 10; v++)
                                    {
                                        mtrlCandidates.Add($"chara/equipment/{codeStr}/material/v{v:D4}/{matFileName}");
                                    }
                                    mtrlCandidates.Add($"chara/equipment/{codeStr}/material/{matFileName}");
                                }
                                else if (typeCode == "h")
                                {
                                    mtrlCandidates.Add($"chara/human/{cCode}/obj/hair/{codeStr}/material/v0001/{matFileName}");
                                    mtrlCandidates.Add($"chara/human/{cCode}/obj/hair/{codeStr}/material/{matFileName}");
                                }
                                else if (typeCode == "t")
                                {
                                    mtrlCandidates.Add($"chara/human/{cCode}/obj/tail/{codeStr}/material/v0001/{matFileName}");
                                    mtrlCandidates.Add($"chara/human/{cCode}/obj/tail/{codeStr}/material/{matFileName}");
                                }
                            }
                            // Also try the raw path directly
                            mtrlCandidates.Add(matFileName);
                            
                            if (!string.IsNullOrEmpty(internalMaterialPath))
                            {
                                mtrlCandidates.Insert(0, internalMaterialPath);
                                _plugin.PluginLog.Info($"[Texture Painter] Injecting exact material path: {internalMaterialPath}");
                            }
                            
                            string resolvedMtrlDisk = null;
                            string resolvedMtrlGamePath = null;
                            foreach (var candidate in mtrlCandidates)
                            {
                                if (DragAndDropTexturing.Equipment.WornEquipmentResolver.TryResolveGamePath(collectionId, candidate, out string disk))
                                {
                                    resolvedMtrlDisk = disk;
                                    resolvedMtrlGamePath = candidate;
                                    _plugin.PluginLog.Info($"[Texture Painter] Resolved mtrl: {candidate} -> {disk}");
                                    break;
                                }
                            }
                            
                            if (string.IsNullOrEmpty(resolvedMtrlDisk))
                            {
                                resolvedMtrlDisk = rawMatPath; // Fallback to raw game path
                                _plugin.PluginLog.Info($"[Texture Painter] Mtrl not overridden by Penumbra. Falling back to vanilla game path: {resolvedMtrlDisk}");
                            }
                            
                            if (DragAndDropTexturing.Equipment.WornEquipmentResolver.TryReadMtrlTexturePaths(resolvedMtrlDisk, out string baseP, out string normP, out string maskP))
                            {
                                _plugin.PluginLog.Info($"[Texture Painter] Mtrl textures — base: {baseP}, norm: {normP}, mask: {maskP}");
                                
                                // Resolve the correct texture map based on edit context
                                isEditingNormal = (!string.IsNullOrEmpty(EditSourcePath) && 
                                    (EditSourcePath.IndexOf("norm", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                     EditSourcePath.IndexOf("bump", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                     EditSourcePath.IndexOf("_n_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     EditSourcePath.EndsWith("_n.png", StringComparison.OrdinalIgnoreCase) ||
                                     EditSourcePath.EndsWith("_n.tex", StringComparison.OrdinalIgnoreCase))) ||
                                     (string.IsNullOrEmpty(EditSourcePath) && _newLayerType == "Normal");

                                bool isEditingMask = (!string.IsNullOrEmpty(EditSourcePath) && 
                                    (EditSourcePath.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                     EditSourcePath.IndexOf("_m_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                     EditSourcePath.EndsWith("_m.png", StringComparison.OrdinalIgnoreCase) ||
                                     EditSourcePath.EndsWith("_m.tex", StringComparison.OrdinalIgnoreCase))) ||
                                     (string.IsNullOrEmpty(EditSourcePath) && _newLayerType == "Mask");

                                string texToLoad = isEditingNormal ? normP : (isEditingMask ? maskP : baseP);
                                string resolvedTexDisk = null;
                                
                                if (!string.IsNullOrEmpty(texToLoad))
                                {
                                    if (DragAndDropTexturing.Equipment.WornEquipmentResolver.TryResolveGamePath(collectionId, texToLoad, out string texDisk))
                                    {
                                        resolvedTexDisk = texDisk;
                                    }
                                }
                                
                                // Export to PNG and set as the active base texture
                                string exportDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "WornGear");
                                string targetSlotKey = slot.Contains("Top") ? "body" :
                                                       slot.Contains("Bottom") ? "legs" :
                                                       slot == "Shoes" ? "feet" :
                                                       slot == "Gloves" ? "hands" :
                                                       slot == "Hair" ? "hair" :
                                                       slot == "Tail" ? "tail" :
                                                       slot == "Head" ? "head" : null;
                                string pngPath = DragAndDropTexturing.Equipment.WornEquipmentResolver.ExportResolvedTextureToPng(
                                    texToLoad, collectionId, exportDir, _plugin, targetSlotKey);
                                
                                if (!slot.Contains("Preview"))
                                {
                                    if (!string.IsNullOrEmpty(pngPath))
                                    {
                                        _plugin.PluginLog.Info($"[Texture Painter] Clothing texture resolved for override: {pngPath}");
                                        if (slot == "Top" || slot == "Bottom" || slot == "Shoes" || slot == "Gloves" || slot == "Head" || slot == "Hair" || slot == "Tail")
                                        {
                                            _clothingTexturePngOverride = pngPath;
                                            _editLayerLoaded = false;
                                            _needsComposite = true;
                                        }
                                    }
                                    else
                                    {
                                        _plugin.PluginLog.Warning($"[Texture Painter] Failed to export texture {texToLoad} to PNG");
                                    }
                                }
                                else
                                {
                                    if (!string.IsNullOrEmpty(pngPath) && File.Exists(pngPath))
                                    {
                                        try
                                        {
                                            using var bmp = new System.Drawing.Bitmap(pngPath);
                                            var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                                            var bmpData = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                            int bytes = Math.Abs(bmpData.Stride) * bmp.Height;
                                            byte[] rgba = new byte[bytes];
                                            System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, rgba, 0, bytes);
                                            bmp.UnlockBits(bmpData);
                                            
                                            int w = bmp.Width;
                                            int h = bmp.Height;
                                            _mainThreadActions.Enqueue(() => {
                                                _renderer.LoadTexture(slot, rgba, w, h);
                                            });
                                            _plugin.PluginLog.Info($"[Texture Painter] Loaded preview texture for slot {slot}: {pngPath}");
                                        }
                                        catch (Exception ex)
                                        {
                                            _plugin.PluginLog.Error(ex, $"[Texture Painter] Failed to load preview texture {pngPath} for slot {slot}");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                _plugin.PluginLog.Warning($"[Texture Painter] Could not read textures from mtrl path: {resolvedMtrlDisk}");
                            }
                        }

                        _plugin.PluginLog.Info($"[PSD Preview] Auto-selected {primaryMeshes.Count} meshes as paintable layer for '{searchPattern}'.");
                    }
                    else
                    {
                        primaryMeshes.Add(meshesCopy[0]);
                        for (int i = 1; i < meshesCopy.Count; i++) secondaryMeshes.Add(meshesCopy[i]);
                    }

                    _mainThreadActions.Enqueue(() => {
                        _renderer.LoadMeshes(slot, primaryMeshes);
                        
                        int counter = 1;
                        foreach (var sm in secondaryMeshes)
                        {
                            _renderer.LoadMeshes($"{slot}_{counter}", new System.Collections.Generic.List<ExtractedMesh> { sm });
                            counter++;
                        }
                    });

                    // Load secondary mesh textures on background thread, then queue GPU upload
                    int secCounter = 1;
                    foreach (var sm in secondaryMeshes)
                    {
                        if (string.IsNullOrEmpty(sm.MaterialPath))
                        {
                            secCounter++;
                            continue;
                        }
                        
                        string smMatFileName = sm.MaterialPath;
                        if (smMatFileName.StartsWith("/")) smMatFileName = smMatFileName.Substring(1);
                        
                        var smMtrlMatch = System.Text.RegularExpressions.Regex.Match(smMatFileName, @"mt_c(\d+)([eht])(\d+)");
                        var smMtrlCandidates = new List<string>();
                        if (smMtrlMatch.Success)
                        {
                            string cCode = "c" + smMtrlMatch.Groups[1].Value;
                            string typeCode = smMtrlMatch.Groups[2].Value;
                            string codeStr = typeCode + smMtrlMatch.Groups[3].Value;

                            if (typeCode == "e")
                            {
                                for (int v = 1; v <= 10; v++)
                                {
                                    smMtrlCandidates.Add($"chara/equipment/{codeStr}/material/v{v:D4}/{smMatFileName}");
                                }
                                smMtrlCandidates.Add($"chara/equipment/{codeStr}/material/{smMatFileName}");
                            }
                            else if (typeCode == "h")
                            {
                                smMtrlCandidates.Add($"chara/human/{cCode}/obj/hair/{codeStr}/material/v0001/{smMatFileName}");
                                smMtrlCandidates.Add($"chara/human/{cCode}/obj/hair/{codeStr}/material/{smMatFileName}");
                            }
                            else if (typeCode == "t")
                            {
                                smMtrlCandidates.Add($"chara/human/{cCode}/obj/tail/{codeStr}/material/v0001/{smMatFileName}");
                                smMtrlCandidates.Add($"chara/human/{cCode}/obj/tail/{codeStr}/material/{smMatFileName}");
                            }
                        }
                        smMtrlCandidates.Add(smMatFileName);
                        
                        string resolvedSmMtrlDisk = null;
                        foreach (var candidate in smMtrlCandidates)
                        {
                            if (DragAndDropTexturing.Equipment.WornEquipmentResolver.TryResolveGamePath(collectionId, candidate, out string disk))
                            {
                                resolvedSmMtrlDisk = disk;
                                break;
                            }
                        }
                        
                        if (string.IsNullOrEmpty(resolvedSmMtrlDisk))
                        {
                            resolvedSmMtrlDisk = smMatFileName; // Fallback to raw game path
                            _plugin.PluginLog.Info($"[Texture Painter] Secondary mesh mtrl not overridden by Penumbra. Falling back to vanilla game path: {resolvedSmMtrlDisk}");
                        }
                        
                        if (DragAndDropTexturing.Equipment.WornEquipmentResolver.TryReadMtrlTexturePaths(resolvedSmMtrlDisk, out string smBaseP, out string smNormP, out string smMaskP))
                        {
                            bool isEditingMask = (!string.IsNullOrEmpty(EditSourcePath) && 
                                (EditSourcePath.IndexOf("mask", StringComparison.OrdinalIgnoreCase) >= 0 || 
                                 EditSourcePath.IndexOf("_m_", StringComparison.OrdinalIgnoreCase) >= 0 ||
                                 EditSourcePath.EndsWith("_m.png", StringComparison.OrdinalIgnoreCase) ||
                                 EditSourcePath.EndsWith("_m.tex", StringComparison.OrdinalIgnoreCase))) ||
                                 (string.IsNullOrEmpty(EditSourcePath) && _newLayerType == "Mask");

                            string smTexToLoad = isEditingNormal ? smNormP : (isEditingMask ? smMaskP : smBaseP);
                            if (!string.IsNullOrEmpty(smTexToLoad))
                            {
                                string exportDir = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName, "WornGear");
                                string targetSlotKey = slot.Contains("Top") ? "body" :
                                                       slot.Contains("Bottom") ? "legs" :
                                                       slot == "Shoes" ? "feet" :
                                                       slot == "Gloves" ? "hands" :
                                                       slot == "Head" ? "head" : null;
                                string smPngPath = DragAndDropTexturing.Equipment.WornEquipmentResolver.ExportResolvedTextureToPng(
                                    smTexToLoad, collectionId, exportDir, _plugin, targetSlotKey);
                                    
                                if (!string.IsNullOrEmpty(smPngPath) && File.Exists(smPngPath))
                                {
                                    try
                                    {
                                        using var bmp = new System.Drawing.Bitmap(smPngPath);
                                        var smRect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                                        var smBmpData = bmp.LockBits(smRect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                                        int bytes = Math.Abs(smBmpData.Stride) * bmp.Height;
                                        byte[] rgba = new byte[bytes];
                                        System.Runtime.InteropServices.Marshal.Copy(smBmpData.Scan0, rgba, 0, bytes);
                                        bmp.UnlockBits(smBmpData);
                                        
                                        string targetSlot = $"{slot}_{secCounter}";
                                        int w = bmp.Width;
                                        int h = bmp.Height;
                                        _mainThreadActions.Enqueue(() => {
                                            _renderer.LoadTexture(targetSlot, rgba, w, h);
                                        });
                                        _plugin.PluginLog.Info($"[Texture Painter] Loaded secondary texture for slot {targetSlot}: {smPngPath}");
                                    }
                                    catch (Exception ex)
                                    {
                                        _plugin.PluginLog.Error(ex, $"[Texture Painter] Failed to load secondary texture {smPngPath}");
                                    }
                                }
                            }
                        }
                        secCounter++;
                    }
                }
                else
                {
                    _plugin.PluginLog.Warning($"[PSD Preview] No meshes parsed for '{slot}'. Falling back to dummy cube.");
                    // Fall back to dummy cube if missing
                    var dummy = MdlParser.GetDummyCube();
                    _mainThreadActions.Enqueue(() => {
                        _renderer.LoadMeshes(slot, dummy);
                    });
                }
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error(ex, $"[PSD Preview] Unhandled exception loading slot '{slot}'");
            }
        }

        private System.Drawing.Bitmap PadToSquareLeftAligned(System.Drawing.Bitmap original)
        {
            if (original == null || original.Width == original.Height)
                return original;

            int size = Math.Max(original.Width, original.Height);
            var padded = new System.Drawing.Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using (var g = System.Drawing.Graphics.FromImage(padded))
            {
                g.Clear(System.Drawing.Color.Transparent);
                g.DrawImageUnscaled(original, 0, 0);
            }
            return padded;
        }

        private string TexToTempPng(string texPath, out bool isBlack, bool padToSquare = false)
        {
            isBlack = false;
            if (string.IsNullOrEmpty(texPath)) return null;

            if (!File.Exists(texPath))
            {
                // If it's a relative path, it might be a Lumina game path (like chara/monster/...)
                if (!Path.IsPathRooted(texPath) || texPath.Contains("/"))
                {
                    string extPath = ExtractVanillaTexViaLumina(texPath, padToSquare);
                    if (!string.IsNullOrEmpty(extPath) && File.Exists(extPath))
                    {
                        return extPath;
                    }
                }
                return null;
            }

            try
            {
                string ext = Path.GetExtension(texPath).ToLower();
                string outPath = Path.Combine(_tempDir, Path.GetFileNameWithoutExtension(texPath) + (ext == ".png" ? "_padded.png" : "_base.png"));
                
                System.Drawing.Bitmap rawBitmap = null;
                if (ext == ".png")
                {
                    rawBitmap = new System.Drawing.Bitmap(texPath);
                }
                else
                {
                    rawBitmap = FFXIVLooseTextureCompiler.ImageProcessing.TexIO.ResolveBitmap(texPath);
                }

                if (rawBitmap != null)
                {
                    using (rawBitmap)
                    {
                        var bitmap = padToSquare ? PadToSquareLeftAligned(rawBitmap) : rawBitmap;
                        try
                        {
                            isBlack = IsImageBlack(bitmap);
                            if (!File.Exists(outPath) || isBlack)
                            {
                                FFXIVLooseTextureCompiler.ImageProcessing.TexIO.SaveBitmapFast(bitmap, outPath);
                            }
                            return outPath;
                        }
                        finally
                        {
                            if (bitmap != rawBitmap) bitmap.Dispose();
                        }
                    }
                }
            }
            catch (Exception ex) { _plugin.PluginLog.Error(ex, $"Failed to convert/pad tex to png: {texPath}"); }
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

private string ExtractVanillaTexViaLumina(string internalGamePath, bool padToSquare = true)
        {
            try
            {
                var texFile = Plugin.DataManager.GetFile<Lumina.Data.Files.TexFile>(internalGamePath);
                if (texFile == null) return null;

                using (var stream = new MemoryStream(texFile.Data))
                {
                    var rawBitmap = FFXIVLooseTextureCompiler.ImageProcessing.TexIO.TexToBitmap(stream);
                    if (rawBitmap != null)
                    {
                        using (rawBitmap)
                        {
                            var bitmap = padToSquare ? PadToSquareLeftAligned(rawBitmap) : rawBitmap;
                            try
                            {
                                string tempDir = Path.Combine(Path.GetTempPath(), "DragAndDropTexturing", "vanilla_cache");
                                Directory.CreateDirectory(tempDir);
                                string safeName = internalGamePath.Replace("/", "_").Replace("\\", "_");
                                string suffix = padToSquare ? "_padded.png" : "_raw.png";
                                string tempPath = Path.Combine(tempDir, safeName + suffix);
                                if (!File.Exists(tempPath))
                                {
                                    bitmap.Save(tempPath, System.Drawing.Imaging.ImageFormat.Png);
                                }
                                return tempPath;
                            }
                            finally
                            {
                                if (bitmap != rawBitmap) bitmap.Dispose();
                            }
                        }
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
                _plugin.PluginLog.Info($"[GPU Upload] UploadBaseTextureToGpu called with path: {_activeBaseTexturePng}");
                _plugin.PluginLog.Info($"[GPU Upload] Cached path: {_cachedBaseBitmapPath ?? "NULL"}, same={_cachedBaseBitmapPath == _activeBaseTexturePng}");
                if (_cachedBaseBitmapPath != _activeBaseTexturePng || _cachedBaseBitmap == null)
                {
                    _plugin.PluginLog.Info($"[GPU Upload] Cache miss — loading new bitmap from: {_activeBaseTexturePng}");
                    _cachedBaseBitmap?.Dispose();
                    _cachedBaseBitmap = new System.Drawing.Bitmap(_activeBaseTexturePng);
                    _cachedBaseBitmapPath = _activeBaseTexturePng;
                    _plugin.PluginLog.Info($"[GPU Upload] Loaded bitmap: {_cachedBaseBitmap.Width}x{_cachedBaseBitmap.Height}");
                }
                else
                {
                    _plugin.PluginLog.Info($"[GPU Upload] Cache hit — reusing existing bitmap");
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

                _renderer.RequestBakeUVMapsAsync();

                // Load existing layer into paint layer if editing
                if (!_editLayerLoaded && EditSourcePath != null && File.Exists(EditSourcePath))
                {
                    try
                    {
                        using var editBmp = new System.Drawing.Bitmap(EditSourcePath);
                        // Resize or pad to match paint texture if needed
                        using var resized = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var g = System.Drawing.Graphics.FromImage(resized))
                        {
                            g.Clear(System.Drawing.Color.Transparent);
                            if (editBmp.Width == w && editBmp.Height == h)
                            {
                                g.DrawImageUnscaled(editBmp, 0, 0);
                            }
                            else if (Math.Abs((float)editBmp.Width / editBmp.Height - (float)w / h) > 0.01f)
                            {
                                // Aspect ratios differ (e.g. 512x1024 onto 1024x1024). Pad left-aligned.
                                float scale = (float)h / editBmp.Height;
                                int drawWidth = (int)(editBmp.Width * scale);
                                int drawHeight = h;
                                g.DrawImage(editBmp, 0, 0, drawWidth, drawHeight);
                            }
                            else
                            {
                                g.DrawImage(editBmp, 0, 0, w, h);
                            }
                        }
                        var editRect = new System.Drawing.Rectangle(0, 0, w, h);
                        var editData = resized.LockBits(editRect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        byte[] rgba = new byte[w * h * 4];
                        bool isEditingGlow = EditSourcePath.IndexOf("_g.", StringComparison.OrdinalIgnoreCase) >= 0 || EditSourcePath.IndexOf("glow", StringComparison.OrdinalIgnoreCase) >= 0 || EditSourcePath.EndsWith("_g.png", StringComparison.OrdinalIgnoreCase) || EditSourcePath.IndexOf("_g_", StringComparison.OrdinalIgnoreCase) >= 0;
                        unsafe
                        {
                            byte* src = (byte*)editData.Scan0;
                            for (int i = 0; i < rgba.Length; i += 4)
                            {
                                rgba[i + 0] = src[i + 2]; // R (from BGRA B)
                                rgba[i + 1] = src[i + 1]; // G
                                rgba[i + 2] = src[i + 0]; // B (from BGRA R)
                                rgba[i + 3] = src[i + 3]; // A

                                if (isEditingGlow)
                                {
                                    // Use luminance as alpha so dark areas become transparent
                                    // and bright glow areas stay opaque
                                    byte lum = (byte)(0.299f * rgba[i + 0] + 0.587f * rgba[i + 1] + 0.114f * rgba[i + 2]);
                                    rgba[i + 3] = lum;
                                }
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

        /// <summary>
        /// Queue a procedural decal stamp request. The stamps will be processed during 
        /// the next Draw() cycle on the ImGui/D3D11 thread.
        /// Safe to call from any thread.
        /// </summary>
        public void QueueProceduralStamps(List<string> decalPaths, int numStamps, string bodyPart, string uvType, Action<string> onComplete)
        {
            Task.Run(() =>
            {
                var loadedDecals = new List<DecalPixelData>();
                foreach (string path in decalPaths)
                {
                    if (!File.Exists(path)) continue;
                    try
                    {
                        using var bmp = new System.Drawing.Bitmap(path);
                        int bmpW = bmp.Width, bmpH = bmp.Height;
                        byte[] rgba = new byte[bmpW * bmpH * 4];
                        var rect = new System.Drawing.Rectangle(0, 0, bmpW, bmpH);
                        var data = bmp.LockBits(rect, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        unsafe
                        {
                            byte* src = (byte*)data.Scan0;
                            for (int i = 0; i < bmpW * bmpH; i++)
                            {
                                rgba[i * 4 + 0] = src[i * 4 + 2]; // R
                                rgba[i * 4 + 1] = src[i * 4 + 1]; // G
                                rgba[i * 4 + 2] = src[i * 4 + 0]; // B
                                rgba[i * 4 + 3] = src[i * 4 + 3]; // A
                                
                                if (_filterWhiteBackgroundOnImport)
                                {
                                    int a_r = 255 - rgba[i * 4 + 0];
                                    int a_g = 255 - rgba[i * 4 + 1];
                                    int a_b = 255 - rgba[i * 4 + 2];
                                    int max_a = Math.Max(a_r, Math.Max(a_g, a_b));

                                    if (max_a < 20)
                                    {
                                        rgba[i * 4 + 0] = 0;
                                        rgba[i * 4 + 1] = 0;
                                        rgba[i * 4 + 2] = 0;
                                        rgba[i * 4 + 3] = 0;
                                    }
                                    else
                                    {
                                        float alphaF = (max_a - 20) / 235.0f;
                                        float origAlphaF = max_a / 255.0f;
                                        
                                        rgba[i * 4 + 0] = (byte)Math.Max(0, Math.Min(255, 255 - (255 - rgba[i * 4 + 0]) / origAlphaF));
                                        rgba[i * 4 + 1] = (byte)Math.Max(0, Math.Min(255, 255 - (255 - rgba[i * 4 + 1]) / origAlphaF));
                                        rgba[i * 4 + 2] = (byte)Math.Max(0, Math.Min(255, 255 - (255 - rgba[i * 4 + 2]) / origAlphaF));
                                        rgba[i * 4 + 3] = (byte)(Math.Max(0, Math.Min(255, alphaF * 255.0f)) * (rgba[i * 4 + 3] / 255.0f));
                                    }
                                }
                            }
                        }
                        bmp.UnlockBits(data);
                        loadedDecals.Add(new DecalPixelData { Rgba = rgba, Width = bmpW, Height = bmpH });
                    }
                    catch (Exception ex)
                    {
                        _plugin.PluginLog.Error(ex, $"[TexturePainter] Failed to load procedural decal off-thread: {path}");
                    }
                }

                _pendingStampRequests.Enqueue(new ProceduralStampRequest
                {
                    DecalPaths = decalPaths,
                    NumStamps = numStamps,
                    BodyPart = bodyPart,
                    UvType = uvType,
                    OnComplete = onComplete,
                    LoadedDecals = loadedDecals
                });
            });
        }

        private bool TryRaycastCached(Vector2 localMousePos, out Vector2 uvHit, out string hitSlot, out Vector3 worldHit, out Vector3 hitNormal) {
            if (_hasCachedRaycast && Vector2.DistanceSquared(localMousePos, _cachedRaycastScreenPos) < 1.0f) {
                uvHit = _cachedRaycastUv;
                hitSlot = _cachedRaycastSlot;
                worldHit = _cachedRaycastWorldPos;
                hitNormal = _cachedRaycastWorldNormal;
                return true;
            }

            if (_renderer.Raycast(localMousePos, out uvHit, out hitSlot, out worldHit, out hitNormal, _primarySlots)) {
                _hasCachedRaycast = true;
                _cachedRaycastScreenPos = localMousePos;
                _cachedRaycastUv = uvHit;
                _cachedRaycastSlot = hitSlot;
                _cachedRaycastWorldPos = worldHit;
                _cachedRaycastWorldNormal = hitNormal;
                return true;
            }

            _hasCachedRaycast = false;
            return false;
        }

        private static void DisposeStampSrvs(List<Vortice.Direct3D11.ID3D11ShaderResourceView> srvs) {
            foreach (var srv in srvs) srv?.Dispose();
            srvs.Clear();
        }

        private void ApplyRandomProceduralStamp(ActiveProceduralStampJob job) {
            if (job.Srvs.Count == 0 || job.Meshes == null || job.TotalTriangles <= 0) return;

            var srv = job.Srvs[_proceduralRng.Next(job.Srvs.Count)];
            int triIndex = _proceduralRng.Next(job.TotalTriangles);
            ExtractedMesh targetMesh = null;
            int localTri = triIndex;
            foreach (var mesh in job.Meshes) {
                int meshTris = mesh.Indices.Count / 3;
                if (localTri < meshTris) { targetMesh = mesh; break; }
                localTri -= meshTris;
            }
            if (targetMesh == null) return;

            uint idx0 = targetMesh.Indices[localTri * 3 + 0];
            uint idx1 = targetMesh.Indices[localTri * 3 + 1];
            uint idx2 = targetMesh.Indices[localTri * 3 + 2];

            Vector3 p0 = targetMesh.Positions[(int)idx0];
            Vector3 p1 = targetMesh.Positions[(int)idx1];
            Vector3 p2 = targetMesh.Positions[(int)idx2];
            Vector2 uv0 = targetMesh.UVs[(int)idx0];
            Vector2 uv1 = targetMesh.UVs[(int)idx1];
            Vector2 uv2 = targetMesh.UVs[(int)idx2];
            Vector3 n0 = targetMesh.Normals[(int)idx0];
            Vector3 n1 = targetMesh.Normals[(int)idx1];
            Vector3 n2 = targetMesh.Normals[(int)idx2];

            float r1 = (float)_proceduralRng.NextDouble();
            float r2 = (float)_proceduralRng.NextDouble();
            if (r1 + r2 > 1.0f) { r1 = 1.0f - r1; r2 = 1.0f - r2; }
            float w = 1.0f - r1 - r2;

            Vector3 worldPos = p0 * r1 + p1 * r2 + p2 * w;
            Vector2 uvHit = uv0 * r1 + uv1 * r2 + uv2 * w;
            Vector3 worldNormal = Vector3.Normalize(n0 * r1 + n1 * r2 + n2 * w);

            float scale = (float)(_proceduralRng.NextDouble() * 0.5 + 0.5);
            float radius = 0.1f * scale;
            float angle = (float)(_proceduralRng.NextDouble() * Math.PI * 2.0);

            Vector3 tangent = Vector3.Normalize(Vector3.Cross(worldNormal, Vector3.UnitY));
            if (tangent.LengthSquared() < 0.001f) tangent = Vector3.Normalize(Vector3.Cross(worldNormal, Vector3.UnitX));
            Vector3 bitangent = Vector3.Cross(worldNormal, tangent);

            float cosA = (float)Math.Cos(angle);
            float sinA = (float)Math.Sin(angle);
            Vector3 rotTangent = tangent * cosA - bitangent * sinA;
            Vector3 rotBitangent = tangent * sinA + bitangent * cosA;

            _renderer.GpuStampTexture(srv, uvHit, new Vector2(1, 1), _default3DProjectionMode,
                worldPos, worldNormal, rotTangent, rotBitangent, radius, radius * 2.0f);
        }

        private void FinishProceduralStampJob(ActiveProceduralStampJob job) {
            var request = job.Request;
            DisposeStampSrvs(job.Srvs);
            job.CurrentPhase = ActiveProceduralStampJob.Phase.Complete;
            _activeProceduralJob = null;

            byte[] generatedPixels = _renderer.ReadbackPaintLayer();
            if (generatedPixels == null) {
                request.OnComplete?.Invoke(null);
                return;
            }

            int width = _renderer.PaintTexWidth > 0 ? _renderer.PaintTexWidth : 2048;
            int height = _renderer.PaintTexHeight > 0 ? _renderer.PaintTexHeight : 2048;
            string uvSuffix = "_base";
            if (request.DecalPaths.Count > 0) {
                string firstFile = Path.GetFileNameWithoutExtension(request.DecalPaths[0]).ToLower();
                if (firstFile.EndsWith("_n") || firstFile.Contains("_n_") || firstFile.Contains("norm")) uvSuffix = "_n";
                else if (firstFile.EndsWith("_m") || firstFile.Contains("_m_") || firstFile.Contains("mask")) uvSuffix = "_m";
                else if (firstFile.EndsWith("_g") || firstFile.Contains("_g_") || firstFile.Contains("glow")) uvSuffix = "_g";
            }

            Task.Run(() => {
                try {
                    string bodyTag = _isGen3Preview ? "gen3" : _isBiboPreview ? "bibo" : _isTbsePreview ? "tbse" : "vanilla";
                    string finalUvType = string.IsNullOrEmpty(request.UvType) ? bodyTag : request.UvType;
                    string outPath = Path.Combine(Plugin.PluginInterface.ConfigDirectory.FullName,
                        $"temp_decal_{request.BodyPart}_{finalUvType}_{uvSuffix}_{Guid.NewGuid()}_base.png");

                    using var outBmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    var outRect = new System.Drawing.Rectangle(0, 0, width, height);
                    var outData = outBmp.LockBits(outRect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    
                    if (uvSuffix == "_g")
                    {
                        for (int i = 0; i < generatedPixels.Length; i += 4)
                        {
                            float alpha = generatedPixels[i + 3] / 255f;
                            generatedPixels[i + 0] = (byte)(generatedPixels[i + 0] * alpha); // B
                            generatedPixels[i + 1] = (byte)(generatedPixels[i + 1] * alpha); // G
                            generatedPixels[i + 2] = (byte)(generatedPixels[i + 2] * alpha); // R
                            generatedPixels[i + 3] = 255; // A (Fully Opaque)
                        }
                    }
                    
                    System.Runtime.InteropServices.Marshal.Copy(generatedPixels, 0, outData.Scan0, generatedPixels.Length);
                    outBmp.UnlockBits(outData);
                    outBmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                    _plugin.PluginLog.Information($"[TexturePainter] Procedural decal saved: {outPath}");
                    _needsComposite = true;
                    request.OnComplete?.Invoke(outPath);
                } catch (Exception ex) {
                    _plugin.PluginLog.Error(ex, "[TexturePainter] Failed to save procedural decal.");
                    request.OnComplete?.Invoke(null);
                }
            });
        }

        /// <summary>
        /// Processes queued procedural stamp requests incrementally during Draw().
        /// UV baking runs off-thread; stamps and readback are spread across frames to avoid hitches.
        /// </summary>
        private void ProcessProceduralStamps() {
            if (_renderer == null) return;

            if (_activeProceduralJob == null) {
                if (!_pendingStampRequests.TryDequeue(out var queuedRequest)) return;
                _activeProceduralJob = new ActiveProceduralStampJob { Request = queuedRequest };
            }

            var job = _activeProceduralJob;
            var request = job.Request;

            try {
                switch (job.CurrentPhase) {
                    case ActiveProceduralStampJob.Phase.Prepare:
                        if (!_modelsLoaded) return;

                        int width = _renderer.PaintTexWidth > 0 ? _renderer.PaintTexWidth : 2048;
                        int height = _renderer.PaintTexHeight > 0 ? _renderer.PaintTexHeight : 2048;
                        if (!_gpuPaintInitialized) {
                            _renderer.InitGpuPaint(width, height);
                            _gpuPaintInitialized = true;
                        }

                        if (request.LoadedDecals != null) {
                            foreach (var decal in request.LoadedDecals) {
                                var srv = _renderer.CreateSrvFromRgba(decal.Rgba, decal.Width, decal.Height);
                                if (srv != null) job.Srvs.Add(srv);
                            }
                        }

                        if (job.Srvs.Count == 0) {
                            _plugin.PluginLog.Warning("[TexturePainter] No valid decal SRVs loaded for procedural stamps.");
                            DisposeStampSrvs(job.Srvs);
                            _activeProceduralJob = null;
                            request.OnComplete?.Invoke(null);
                            return;
                        }

                        lock (_loadedMeshes) { job.Meshes = new List<ExtractedMesh>(_loadedMeshes); }
                        if (job.Meshes.Count == 0) {
                            _plugin.PluginLog.Warning("[TexturePainter] No meshes loaded for procedural stamps.");
                            DisposeStampSrvs(job.Srvs);
                            _activeProceduralJob = null;
                            request.OnComplete?.Invoke(null);
                            return;
                        }

                        job.TotalTriangles = 0;
                        foreach (var mesh in job.Meshes)
                            job.TotalTriangles += mesh.Indices.Count / 3;

                        if (!_renderer.HasBakedUvMaps && !_renderer.IsUvBakeInProgress)
                            _renderer.RequestBakeUVMapsAsync();

                        job.NextStampIndex = 0;
                        job.CurrentPhase = ActiveProceduralStampJob.Phase.WaitUvMaps;
                        break;

                    case ActiveProceduralStampJob.Phase.WaitUvMaps:
                        if (!_renderer.HasBakedUvMaps) return;
                        job.CurrentPhase = ActiveProceduralStampJob.Phase.Stamping;
                        goto case ActiveProceduralStampJob.Phase.Stamping;

                    case ActiveProceduralStampJob.Phase.Stamping:
                        if (job.NextStampIndex < request.NumStamps) {
                            ApplyRandomProceduralStamp(job);
                            job.NextStampIndex++;
                            return;
                        }
                        job.CurrentPhase = ActiveProceduralStampJob.Phase.Readback;
                        goto case ActiveProceduralStampJob.Phase.Readback;

                    case ActiveProceduralStampJob.Phase.Readback:
                        FinishProceduralStampJob(job);
                        break;
                }
            } catch (Exception ex) {
                _plugin.PluginLog.Error(ex, "[TexturePainter] Failed to process procedural stamps.");
                DisposeStampSrvs(job.Srvs);
                _activeProceduralJob = null;
                request.OnComplete?.Invoke(null);
            }
        }

        private static string CleanTexFilename(string filename)
        {
            if (string.IsNullOrEmpty(filename)) return "";
            string name = Path.GetFileNameWithoutExtension(filename).ToLowerInvariant();
            name = System.Text.RegularExpressions.Regex.Replace(name, @"_worn$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            name = System.Text.RegularExpressions.Regex.Replace(name, @"_[a-f0-9]{7,8}$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            name = System.Text.RegularExpressions.Regex.Replace(name, @"_[dnm]$", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            name = System.Text.RegularExpressions.Regex.Replace(name, @"^v\d+_", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return name;
        }

        private DragAndDropTexturing.Equipment.WornEquipmentPiece FindMatchingWornPiece(
            string editSourcePath,
            System.Collections.Generic.List<DragAndDropTexturing.Equipment.WornEquipmentPiece> wornGear,
            Guid collectionId)
        {
            if (wornGear == null)
            {
                return null;
            }

            _plugin.PluginLog.Info($"[FindMatchingWornPiece] Starting search. Path: '{editSourcePath}', ContextKey: '{ContextCategoryKey}', Collection: '{collectionId}', Worn Pieces: {wornGear.Count}");

            string normalizedEditPath = !string.IsNullOrEmpty(editSourcePath) ? editSourcePath.Replace("/", "\\").ToLowerInvariant() : "";
            string editFileName = !string.IsNullOrEmpty(editSourcePath) ? Path.GetFileNameWithoutExtension(normalizedEditPath) : "";

            string detectedSlotKey = null;
            
            if (!string.IsNullOrEmpty(ContextCategoryKey))
            {
                if (ContextCategoryKey.Contains("_gear_body")) detectedSlotKey = "body";
                else if (ContextCategoryKey.Contains("_gear_legs")) detectedSlotKey = "legs";
                else if (ContextCategoryKey.Contains("_gear_feet")) detectedSlotKey = "feet";
                else if (ContextCategoryKey.Contains("_gear_hands")) detectedSlotKey = "hands";
                else if (ContextCategoryKey.Contains("_gear_head")) detectedSlotKey = "head";
                else if (ContextCategoryKey.EndsWith("_hair", StringComparison.OrdinalIgnoreCase)) detectedSlotKey = "hair";
                else if (ContextCategoryKey.EndsWith("_tail", StringComparison.OrdinalIgnoreCase)) detectedSlotKey = "tail";
                else if (ContextCategoryKey.EndsWith("_face", StringComparison.OrdinalIgnoreCase)) return null;
                else if (ContextCategoryKey.Contains("_minion_", StringComparison.OrdinalIgnoreCase)) detectedSlotKey = "body";
                else if (ContextCategoryKey.EndsWith("_body", StringComparison.OrdinalIgnoreCase)) return null;
            }

            if (detectedSlotKey == null && !string.IsNullOrEmpty(editFileName))
            {
                if (editFileName.Contains("_worn_body"))
                    detectedSlotKey = "body";
                else if (editFileName.Contains("_worn_legs"))
                    detectedSlotKey = "legs";
                else if (editFileName.Contains("_worn_feet"))
                    detectedSlotKey = "feet";
                else if (editFileName.Contains("_worn_hands"))
                    detectedSlotKey = "hands";
                else if (editFileName.Contains("_worn_head"))
                    detectedSlotKey = "head";
            }

            if (detectedSlotKey != null)
            {
                var candidates = wornGear.Where(p => p.SlotKey == detectedSlotKey).ToList();
                if (candidates.Count > 0)
                {
                    // First try to match by material name since we have multiple candidates for the same slot
                    string editFileNameNoExt = !string.IsNullOrEmpty(editFileName) ? System.IO.Path.GetFileNameWithoutExtension(editFileName) : "";
                    string[] nameParts = editFileNameNoExt.Split('_');
                    string lastSection = nameParts.Length > 0 ? nameParts[nameParts.Length - 1] : "";

                    foreach (var c in candidates.OrderByDescending(p => p.MaterialName?.Length ?? 0))
                    {
                        if (!string.IsNullOrEmpty(c.MaterialName))
                        {
                            bool matchFile = editFileNameNoExt.EndsWith($"_{c.MaterialName}", StringComparison.OrdinalIgnoreCase) ||
                                             editFileNameNoExt.Contains($"_{c.MaterialName}_", StringComparison.OrdinalIgnoreCase) ||
                                             lastSection.Equals(c.MaterialName, StringComparison.OrdinalIgnoreCase);
                            
                            bool matchCategory = ContextCategoryKey != null && ContextCategoryKey.EndsWith($"_{c.MaterialName}", StringComparison.OrdinalIgnoreCase);

                            if (matchFile || matchCategory)
                            {
                                _plugin.PluginLog.Info($"[FindMatchingWornPiece] Match found via explicit slot key and MaterialName: {c.DisplayName}");
                                return c;
                            }
                        }
                    }

                    var flexibleMatchE = System.Text.RegularExpressions.Regex.Match(editFileName, @"e(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (flexibleMatchE.Success)
                    {
                        string eNum = flexibleMatchE.Groups[1].Value;
                        var matchedCandidate = candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p.EquipSetId) && p.EquipSetId.Contains(eNum));
                        if (matchedCandidate != null)
                        {
                            _plugin.PluginLog.Info($"[FindMatchingWornPiece] Match found via explicit slot key and eCode: {matchedCandidate.DisplayName}");
                            return matchedCandidate;
                        }
                    }
                    var flexibleMatchH = System.Text.RegularExpressions.Regex.Match(editFileName, @"h(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    if (flexibleMatchH.Success)
                    {
                        string hNum = flexibleMatchH.Groups[1].Value;
                        var matchedCandidate = candidates.FirstOrDefault(p => !string.IsNullOrEmpty(p.EquipSetId) && p.EquipSetId.Contains(hNum));
                        if (matchedCandidate != null)
                        {
                            _plugin.PluginLog.Info($"[FindMatchingWornPiece] Match found via explicit slot key and hCode: {matchedCandidate.DisplayName}");
                            return matchedCandidate;
                        }
                    }
                    var moddedCandidates = candidates.Where(c => !string.IsNullOrEmpty(c.ResolvedBaseDiskPath) || !string.IsNullOrEmpty(c.ResolvedMaterialDiskPath)).ToList();
                    var bestCandidate = moddedCandidates.Count > 0 ? moddedCandidates[0] : candidates[0];
                    _plugin.PluginLog.Info($"[FindMatchingWornPiece] Falling back to first candidate for detected slot '{detectedSlotKey}': {bestCandidate.DisplayName}");
                    return bestCandidate;
                }
            }

            // 1. Try regex match for gear filename convention (e.g. v01_c0101e0497_dwn_n_worn)
            var flexibleMatch = System.Text.RegularExpressions.Regex.Match(editFileName, @"e(\d+)_([a-z]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (flexibleMatch.Success)
            {
                string eNum = flexibleMatch.Groups[1].Value;
                string suffix = flexibleMatch.Groups[2].Value.ToLowerInvariant();
                string targetSlot = suffix == "top" ? "body" : suffix == "dwn" ? "legs" : suffix == "sho" ? "feet" : suffix == "glv" ? "hands" : suffix == "met" ? "head" : null;
                _plugin.PluginLog.Info($"[FindMatchingWornPiece] Flexible regex matched: eNum={eNum}, suffix={suffix}, targetSlot={targetSlot}");
                if (targetSlot != null)
                {
                    var candidates = wornGear.Where(p => p.SlotKey == targetSlot).ToList();
                    if (candidates.Count > 0)
                    {
                        foreach (var c in candidates)
                        {
                            if (!string.IsNullOrEmpty(c.MaterialName) && editFileName.Contains(c.MaterialName.ToLowerInvariant()))
                            {
                                _plugin.PluginLog.Info($"[FindMatchingWornPiece] Match found via suffix and material name: {c.DisplayName}");
                                return c;
                            }
                        }
                        _plugin.PluginLog.Info($"[FindMatchingWornPiece] Match found via suffix fallback (first candidate): {candidates[0].DisplayName}");
                        return candidates[0];
                    }
                }
            }

            var flexibleMatchHGlobal = System.Text.RegularExpressions.Regex.Match(editFileName, @"h(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (flexibleMatchHGlobal.Success)
            {
                string hNum = flexibleMatchHGlobal.Groups[1].Value;
                var candidates = wornGear.Where(p => p.SlotKey == "hair").ToList();
                if (candidates.Count > 0)
                {
                    foreach (var c in candidates)
                    {
                        if (!string.IsNullOrEmpty(c.EquipSetId) && c.EquipSetId.Contains(hNum))
                        {
                            _plugin.PluginLog.Info($"[FindMatchingWornPiece] Match found via global hNum: {c.DisplayName}");
                            return c;
                        }
                    }
                    _plugin.PluginLog.Info($"[FindMatchingWornPiece] Match found via global hNum fallback: {candidates[0].DisplayName}");
                    return candidates[0];
                }
            }

            // 1b. Fallback: Search for slot keywords in the filename if no eCode matches
            detectedSlotKey = null;
            if (editFileName.Contains("top") || editFileName.Contains("body") || editFileName.Contains("shirt"))
                detectedSlotKey = "body";
            else if (editFileName.Contains("legs") || editFileName.Contains("down") || editFileName.Contains("dwn") || editFileName.Contains("pants"))
                detectedSlotKey = "legs";
            else if (editFileName.Contains("shoes") || editFileName.Contains("boots") || editFileName.Contains("sho") || editFileName.Contains("feet"))
                detectedSlotKey = "feet";
            else if (editFileName.Contains("gloves") || editFileName.Contains("hands") || editFileName.Contains("glv"))
                detectedSlotKey = "hands";
            else if (editFileName.Contains("head") || editFileName.Contains("hat") || editFileName.Contains("met") || editFileName.Contains("visor"))
                detectedSlotKey = "head";
            else if (editFileName.Contains("hair") || editFileName.Contains("hir"))
                detectedSlotKey = "hair";

            _plugin.PluginLog.Info($"[FindMatchingWornPiece] Detected Slot Key from filename: '{detectedSlotKey}'");
            if (detectedSlotKey != null)
            {
                var candidates = wornGear.Where(p => p.SlotKey == detectedSlotKey).ToList();
                if (candidates.Count > 0)
                {
                    // Prefer candidates with modded textures
                    var moddedCandidates = candidates.Where(c => !string.IsNullOrEmpty(c.ResolvedBaseDiskPath) || !string.IsNullOrEmpty(c.ResolvedMaterialDiskPath)).ToList();
                    var bestCandidate = moddedCandidates.Count > 0 ? moddedCandidates[0] : candidates[0];
                    _plugin.PluginLog.Info($"[FindMatchingWornPiece] Match found via detected Slot Key: {bestCandidate.DisplayName}");
                    return bestCandidate;
                }
            }

            // 2. Scan resolved paths and match base, normal, mask paths
            string cleanEdit = CleanTexFilename(editFileName);
            _plugin.PluginLog.Info($"[FindMatchingWornPiece] Clean edit normalized: '{cleanEdit}'");
            foreach (var piece in wornGear)
            {
                string[] pathsToCheck = { piece.InternalBasePath, piece.InternalNormalPath, piece.InternalMaskPath };
                foreach (var path in pathsToCheck)
                {
                    if (string.IsNullOrEmpty(path)) continue;

                    string cleanPath = CleanTexFilename(path);
                    _plugin.PluginLog.Info($"[FindMatchingWornPiece] Comparing cleanEdit '{cleanEdit}' to cleanPath '{cleanPath}' (raw: '{path}')");
                    if (!string.IsNullOrEmpty(cleanEdit) && !string.IsNullOrEmpty(cleanPath))
                    {
                        if (cleanEdit == cleanPath || cleanEdit.Contains(cleanPath) || cleanPath.Contains(cleanEdit))
                        {
                            _plugin.PluginLog.Info($"[FindMatchingWornPiece] Match found via clean path comparison: {piece.DisplayName}");
                            return piece;
                        }
                    }

                    try
                    {
                        PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collectionId, path, out string resolved);
                        if (!string.IsNullOrEmpty(resolved))
                        {
                            string cleanResolved = CleanTexFilename(resolved);
                            _plugin.PluginLog.Info($"[FindMatchingWornPiece] Penumbra resolved path: '{resolved}', cleanResolved: '{cleanResolved}'");
                            if (!string.IsNullOrEmpty(cleanEdit) && !string.IsNullOrEmpty(cleanResolved))
                            {
                                if (cleanEdit == cleanResolved || cleanEdit.Contains(cleanResolved) || cleanResolved.Contains(cleanEdit))
                                {
                                    _plugin.PluginLog.Info($"[FindMatchingWornPiece] Match found via clean resolved path comparison: {piece.DisplayName}");
                                    return piece;
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _plugin.PluginLog.Warning($"[FindMatchingWornPiece] ResolvePath invocation failed for path '{path}': {ex.Message}");
                    }
                }
            }

            _plugin.PluginLog.Warning($"[FindMatchingWornPiece] No match found!");
            return null;
        }
    }
}
