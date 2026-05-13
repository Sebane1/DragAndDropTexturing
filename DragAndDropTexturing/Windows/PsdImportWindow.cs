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
                ImGui.SetNextItemWidth(80);
                int type = layer.OverrideTypeIndex;
                if (ImGui.Combo("##OverrideType", ref type, _overrideTypes, _overrideTypes.Length)) layer.OverrideTypeIndex = type;

                if (layer.BodyPartIndex == 0)
                {
                    ImGui.SameLine();
                    ImGui.SetNextItemWidth(80);
                    int bType = layer.BodyTypeIndex;
                    if (ImGui.Combo("##BodyType", ref bType, _bodyTypes, _bodyTypes.Length)) layer.BodyTypeIndex = bType;
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

            if (ImGui.Button("Finalize & Import Selected", new Vector2(-1, 30)))
            {
                FinalizeImport();
            }
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
