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
using PsdSharp;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using PsdSharp.Images;
using PsdSharp.Images.DataConversion;
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
        
        public List<PsdImportLayer> Layers = new();
        private readonly string[] _bodyParts = { "body", "face", "eyes", "eyebrows" };
        private readonly string[] _overrideTypes = { "Base", "Normal" };

        public PsdImportWindow(Plugin plugin) : base("PSD Import Manager", ImGuiWindowFlags.NoScrollbar)
        {
            _plugin = plugin;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(600, 400),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };
        }

        public void StartImport(string psdPath)
        {
            Layers.Clear();
            _isProcessing = true;
            _progress = 0f;
            _statusText = "Loading PSD...";
            IsOpen = true;

            _tempDir = Path.Combine(_plugin.ContextualLayerManager.RootDirectory, "PSD_Temp");
            _importDir = Path.Combine(_plugin.ContextualLayerManager.RootDirectory, "Imports");

            Task.Run(() => ProcessPsd(psdPath));
        }

        private void ProcessPsd(string psdPath)
        {
            try
            {
                if (!Directory.Exists(_tempDir)) Directory.CreateDirectory(_tempDir);
                // Clean temp dir
                foreach (var f in Directory.GetFiles(_tempDir, "*.png")) File.Delete(f);

                using var stream = File.Open(psdPath, FileMode.Open, FileAccess.Read);
                var psd = PsdFile.Open(stream);

                int totalLayers = psd.Layers.Count;
                int currentLayer = 0;

                foreach (var layer in psd.Layers)
                {
                    currentLayer++;
                    _progress = (float)currentLayer / totalLayers;
                    _statusText = $"Extracting layer: {layer.Name}";

                    if (layer.ImageData == null || layer.Bounds.Width <= 0 || layer.Bounds.Height <= 0)
                        continue; // Skip empty layers or folders

                    try
                    {
                        var buf = PixelDataConverter.GetInterleavedBuffer(layer.ImageData, ColorType.Rgba8888);
                        if (buf == null || buf.Length == 0) continue;

                        using var layerImg = SixLabors.ImageSharp.Image.LoadPixelData<Rgba32>(buf, (int)layer.Bounds.Width, (int)layer.Bounds.Height);
                        using var fullImg = new Image<Rgba32>((int)psd.Header.WidthInPixels, (int)psd.Header.HeightInPixels);
                        
                        fullImg.Mutate(x => x.DrawImage(layerImg, new Point((int)layer.Bounds.TopLeft.X, (int)layer.Bounds.TopLeft.Y), 1f));

                        string safeName = string.Join("_", layer.Name.Split(Path.GetInvalidFileNameChars())).Replace(" ", "_");
                        string outPath = Path.Combine(_tempDir, $"{safeName}.png");
                        fullImg.SaveAsPng(outPath);

                        var importLayer = new PsdImportLayer
                        {
                            OriginalName = layer.Name,
                            PngPath = outPath,
                            Selected = true
                        };

                        // Auto-detect Body Part
                        string lowerName = layer.Name.ToLower();
                        if (lowerName.Contains("face")) importLayer.BodyPartIndex = 1;
                        else if (lowerName.Contains("eye")) importLayer.BodyPartIndex = 2;
                        else if (lowerName.Contains("eyebrow") || lowerName.Contains("lash")) importLayer.BodyPartIndex = 3;

                        // Auto-detect Map Type
                        if (lowerName.Contains("norm") || lowerName.Contains("bump")) importLayer.OverrideTypeIndex = 1;

                        Layers.Add(importLayer);
                    }
                    catch (Exception ex)
                    {
                        _plugin.PluginLog.Error($"Failed to extract PSD layer {layer.Name}: {ex.Message}");
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
            }
        }

        public override void Draw()
        {
            if (_isProcessing)
            {
                ImGui.Text(_statusText);
                ImGui.ProgressBar(_progress, new Vector2(-1, 0));
                return;
            }

            if (Layers.Count == 0)
            {
                ImGui.Text("No valid image layers found in PSD.");
                if (ImGui.Button("Close")) IsOpen = false;
                return;
            }

            ImGui.Text("Select layers to import:");
            ImGui.Separator();

            ImGui.BeginChild("PsdLayersList", new Vector2(0, ImGui.GetContentRegionAvail().Y - 40), true);
            foreach (var layer in Layers)
            {
                ImGui.PushID(layer.OriginalName);
                
                bool selected = layer.Selected;
                if (ImGui.Checkbox("##Select", ref selected)) layer.Selected = selected;
                
                ImGui.SameLine();
                ImGui.Text(layer.OriginalName);

                ImGui.SameLine(250);
                ImGui.SetNextItemWidth(100);
                int part = layer.BodyPartIndex;
                if (ImGui.Combo("##BodyPart", ref part, _bodyParts, _bodyParts.Length)) layer.BodyPartIndex = part;

                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                int type = layer.OverrideTypeIndex;
                if (ImGui.Combo("##OverrideType", ref type, _overrideTypes, _overrideTypes.Length)) layer.OverrideTypeIndex = type;

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
                            ImGui.Image(wrap.Handle, new Vector2(40, 40));
                        }
                }

                ImGui.PopID();
                ImGui.Separator();
            }
            ImGui.EndChild();

            if (ImGui.Button("Finalize & Import Selected", new Vector2(-1, 30)))
            {
                FinalizeImport();
                IsOpen = false;
            }
        }

        private void FinalizeImport()
        {
            if (!Directory.Exists(_importDir)) Directory.CreateDirectory(_importDir);

            foreach (var layer in Layers.Where(l => l.Selected))
            {
                try
                {
                    if (File.Exists(layer.PngPath))
                    {
                        string targetPart = _bodyParts[layer.BodyPartIndex];
                        string targetType = _overrideTypes[layer.OverrideTypeIndex].ToLower();
                        
                        string finalName = $"{targetPart}_{targetType}_{layer.OriginalName}.png";
                        // Sanitize filename
                        finalName = string.Join("_", finalName.Split(Path.GetInvalidFileNameChars()));
                        
                        string destPath = Path.Combine(_importDir, finalName);
                        File.Copy(layer.PngPath, destPath, true);
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
        }

        public void Dispose()
        {
            Layers.Clear();
        }
    }
}
