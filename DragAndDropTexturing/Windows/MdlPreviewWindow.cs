using System;
using System.IO;
using System.Numerics;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace DragAndDropTexturing.Windows
{
    public class MdlPreviewWindow : Window, IDisposable
    {
        private ModelRenderer _renderer;
        private bool _rendererInitialized = false;
        private string _errorMessage = null;
        private string _modelPath = "chara/human/c0101/obj/face/f0001/model/c0101f0001_fac.mdl";
        private string _loadStatus = "";
        private string _texturePath = "";
        private string _textureStatus = "";

        // Mouse state for camera controls
        private Vector2 _lastMousePos = Vector2.Zero;
        private bool _isDragging = false;
        private bool _isPanning = false;

        public MdlPreviewWindow() : base("3D Model Preview", ImGuiWindowFlags.NoScrollbar)
        {
            Size = new Vector2(800, 600);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        private void LoadModel(string path, bool fromDisk)
        {
            _loadStatus = "";
            try
            {
                System.Collections.Generic.List<ExtractedMesh> meshes;

                if (fromDisk)
                {
                    // Bypass Lumina entirely for disk files — read raw bytes ourselves
                    if (!System.IO.File.Exists(path))
                    {
                        _loadStatus = "File not found on disk.";
                        return;
                    }
                    meshes = MdlParser.ParseFromDisk(path, out var diskStatus);
                    _loadStatus = diskStatus;
                }
                else
                {
                    // For game SqPack paths, try Lumina but fall back to cube on failure
                    Lumina.Data.Files.MdlFile mdlFile = null;
                    try
                    {
                        mdlFile = Plugin.DataManager.GetFile<Lumina.Data.Files.MdlFile>(path);
                    }
                    catch (Exception)
                    {
                        mdlFile = null;
                    }

                    if (mdlFile != null)
                    {
                        meshes = MdlParser.Parse(mdlFile);
                    }
                    else
                    {
                        meshes = MdlParser.GetDummyCube();
                        _loadStatus = "Lumina MDL parse failed (Dawntrail format). Showing test cube.";
                    }
                }

                if (meshes.Count > 0)
                {
                    _renderer.LoadMeshes(meshes);
                    _renderer.ResetCamera();
                    if (string.IsNullOrEmpty(_loadStatus))
                        _loadStatus = $"Loaded {meshes.Count} meshes successfully.";
                }
                else
                {
                    _loadStatus = "No mesh data available.";
                }
            }
            catch (Exception ex)
            {
                _loadStatus = "Error: " + ex.ToString();
            }
        }

        public override void Draw()
        {
            if (!_rendererInitialized && _errorMessage == null)
            {
                try
                {
                    _renderer = new ModelRenderer(800, 600);
                    _rendererInitialized = true;
                }
                catch (Exception ex)
                {
                    _errorMessage = ex.Message;
                }
            }

            if (_errorMessage != null)
            {
                ImGui.TextColored(new Vector4(1, 0, 0, 1), "Failed to initialize D3D11 Renderer:");
                ImGui.TextWrapped(_errorMessage);
                return;
            }

            var region = ImGui.GetContentRegionAvail();
            
            // UI to load models
            ImGui.InputText("Model Path", ref _modelPath, 1024);
            if (ImGui.Button("Load Game Path"))
            {
                LoadModel(_modelPath, false);
            }
            ImGui.SameLine();
            if (ImGui.Button("Load From Disk"))
            {
                LoadModel(_modelPath, true);
            }
            ImGui.SameLine();
            if (ImGui.Button("Reset Camera"))
            {
                _renderer?.ResetCamera();
            }
            if (_renderer != null)
            {
                ImGui.SameLine();
                bool ortho = _renderer.UseOrthographic;
                if (ImGui.Button(ortho ? "Orthographic" : "Perspective"))
                {
                    _renderer.UseOrthographic = !ortho;
                }
            }

            // Texture loading row
            ImGui.InputText("Texture Path", ref _texturePath, 1024);
            if (ImGui.Button("Load Texture"))
            {
                LoadTextureFromDisk(_texturePath);
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear Texture"))
            {
                _renderer?.ClearTexture();
                _textureStatus = "Texture cleared.";
            }
            if (_renderer != null && _renderer.HasTexture)
            {
                ImGui.SameLine();
                ImGui.TextColored(new Vector4(0.3f, 1f, 0.3f, 1f), "[Textured]");
            }
            if (!string.IsNullOrEmpty(_textureStatus))
            {
                ImGui.TextWrapped(_textureStatus);
            }

            if (!string.IsNullOrEmpty(_loadStatus))
            {
                ImGui.TextWrapped(_loadStatus);
            }

            ImGui.Spacing();
            ImGui.Separator();

            region = ImGui.GetContentRegionAvail();
            if (region.X > 0 && region.Y > 0 && (region.X != _renderer.Width || region.Y != _renderer.Height))
            {
                _renderer.Resize((int)region.X, (int)region.Y);
            }

            _renderer.Render();

            if (!string.IsNullOrEmpty(_renderer.RenderError))
            {
                ImGui.TextColored(new Vector4(1, 0.5f, 0, 1), _renderer.RenderError);
            }

            if (_renderer.ShaderResourceViewHandle != IntPtr.Zero)
            {
                // Draw image via draw list, then overlay an InvisibleButton
                // to capture mouse input and prevent window dragging
                var cursorPos = ImGui.GetCursorScreenPos();
                var drawList = ImGui.GetWindowDrawList();
                drawList.AddImage(
                    new ImTextureID(_renderer.ShaderResourceViewHandle),
                    cursorPos,
                    cursorPos + region);
                
                // InvisibleButton eats clicks so the window doesn't drag
                ImGui.InvisibleButton("##viewport", region);
                bool isHovered = ImGui.IsItemHovered();
                bool isActive = ImGui.IsItemActive();

                // Process mouse input when hovering/active on the viewport
                if (isHovered || isActive)
                {
                    var mousePos = ImGui.GetMousePos();

                    // Left-drag: orbit rotate
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
                    else
                    {
                        _isDragging = false;
                    }

                    // Middle-drag or Right-drag: pan
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
                    else
                    {
                        _isPanning = false;
                    }

                    // Scroll: zoom
                    float wheel = ImGui.GetIO().MouseWheel;
                    if (wheel != 0)
                    {
                        _renderer.ZoomCamera(wheel);
                    }
                }
                else
                {
                    _isDragging = false;
                    _isPanning = false;
                }
            }

            // Help text
            ImGui.TextDisabled("LMB: Rotate | MMB/RMB: Pan | Scroll: Zoom | Reset Camera button to reset");
        }
        private void LoadTextureFromDisk(string path)
        {
            _textureStatus = "";
            try
            {
                if (!File.Exists(path))
                {
                    _textureStatus = "Texture file not found.";
                    return;
                }

                string ext = Path.GetExtension(path).ToLowerInvariant();
                byte[] rgbaPixels;
                int texWidth, texHeight;

                if (ext == ".tex")
                {
                    // FFXIV .tex format — read via Lumina's TexFile structure
                    var texData = File.ReadAllBytes(path);
                    var texFile = new Lumina.Data.Files.TexFile();
                    // Manually load the tex file from raw bytes
                    using var ms = new MemoryStream(texData);
                    using var reader = new BinaryReader(ms);

                    // TexFile header: Type(4), Format(4), Width(2), Height(2), Layers(2), MipLevels(2), ...
                    // Then mip offset table, then pixel data
                    // For simplicity, use Lumina's built-in RGBA converter via reflection or just
                    // load as standard image if available
                    _textureStatus = ".tex loading: Use PNG/BMP/DDS export from TexTools for now.";
                    return;
                }
                else
                {
                    // Standard image formats via System.Drawing
                    using var bitmap = new System.Drawing.Bitmap(path);
                    texWidth = bitmap.Width;
                    texHeight = bitmap.Height;
                    rgbaPixels = new byte[texWidth * texHeight * 4];

                    var lockRect = new System.Drawing.Rectangle(0, 0, texWidth, texHeight);
                    var bmpData = bitmap.LockBits(lockRect,
                        System.Drawing.Imaging.ImageLockMode.ReadOnly,
                        System.Drawing.Imaging.PixelFormat.Format32bppArgb);

                    unsafe
                    {
                        byte* src = (byte*)bmpData.Scan0;
                        for (int y = 0; y < texHeight; y++)
                        {
                            byte* row = src + y * bmpData.Stride;
                            for (int x = 0; x < texWidth; x++)
                            {
                                int srcIdx = x * 4;
                                int dstIdx = (y * texWidth + x) * 4;
                                // BGRA → RGBA
                                rgbaPixels[dstIdx + 0] = row[srcIdx + 2]; // R
                                rgbaPixels[dstIdx + 1] = row[srcIdx + 1]; // G
                                rgbaPixels[dstIdx + 2] = row[srcIdx + 0]; // B
                                rgbaPixels[dstIdx + 3] = row[srcIdx + 3]; // A
                            }
                        }
                    }

                    bitmap.UnlockBits(bmpData);
                }

                _renderer.LoadTexture(rgbaPixels, texWidth, texHeight);
                _textureStatus = $"Texture loaded: {texWidth}×{texHeight} from {Path.GetFileName(path)}";
            }
            catch (Exception ex)
            {
                _textureStatus = "Texture load error: " + ex.Message;
            }
        }

        public void Dispose()
        {
            _renderer?.Dispose();
        }
    }
}

