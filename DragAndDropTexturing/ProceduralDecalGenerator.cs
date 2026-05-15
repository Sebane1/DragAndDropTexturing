using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Types;
using DragAndDropTexturing.Windows;
using PenumbraAndGlamourerHelpers;

namespace DragAndDropTexturing
{
    public static class ProceduralDecalGenerator
    {
        private static ModelRenderer _headlessRenderer = null;
        private static Random _random = new Random();

        public static string GenerateProceduralOverlay(Plugin plugin, IGameObject character, string bodyPart, List<string> decalPaths, int numStamps)
        {
            if (character == null || decalPaths.Count == 0 || numStamps <= 0) return null;

            try
            {
                int width = 2048;
                int height = 2048;

                if (_headlessRenderer == null)
                {
                    _headlessRenderer = new ModelRenderer(width, height);
                }
                else if (_headlessRenderer.PaintTexWidth != width || _headlessRenderer.PaintTexHeight != height)
                {
                    _headlessRenderer.Dispose();
                    _headlessRenderer = new ModelRenderer(width, height);
                }

                _headlessRenderer.InitGpuPaint(width, height);
                _headlessRenderer.GpuClearPaint();

                // Build the game path
                // For body part, we need the race code
                var pChar = (Dalamud.Game.ClientState.Objects.Types.ICharacter)character;
                var customize = PenumbraAndGlamourerHelperFunctions.GetCustomization(pChar);
                string raceCode = PenumbraAndGlamourerHelperFunctions.ModelRaceToRaceCode(customize.Customize.Race.Value, customize.Customize.Clan.Value, customize.Customize.Gender.Value);

                // Detect redirected race if applicable
                var collectionId = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke((int)character.ObjectIndex).Item3.Id;
                int redirected = PenumbraAndGlamourerHelperFunctions.DetectRedirectedRace(collectionId, customize.Customize.Gender.Value, customize.Customize.Race.Value, plugin);
                if (redirected != -1)
                {
                    int redirectedRace = redirected / 10;
                    int redirectedClan = (redirected % 10) == 1 ? 1 : 0;
                    raceCode = PenumbraAndGlamourerHelperFunctions.ModelRaceToRaceCode(redirectedRace, redirectedClan, customize.Customize.Gender.Value);
                }

                string mdlGamePath = "";

                if (bodyPart.Equals("body", StringComparison.OrdinalIgnoreCase))
                {
                    // Body is typically equipment slot Body.
                    mdlGamePath = $"chara/human/{raceCode}/obj/body/b0001/model/{raceCode}b0001_top.mdl";
                }
                else if (bodyPart.Equals("face", StringComparison.OrdinalIgnoreCase) || bodyPart.Equals("eyes", StringComparison.OrdinalIgnoreCase) || bodyPart.Equals("eyebrows", StringComparison.OrdinalIgnoreCase))
                {
                    string faceStr = customize.Customize.Face.Value.ToString("D4");
                    mdlGamePath = $"chara/human/{raceCode}/obj/face/f{faceStr}/model/{raceCode}f{faceStr}_fac.mdl";
                }
                else
                {
                    return null;
                }

                // Resolve path using Penumbra IPC directly
                PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collectionId, mdlGamePath, out string resolvedMdlPath);
                
                string diskPath = resolvedMdlPath;
                if (string.IsNullOrEmpty(diskPath) || !File.Exists(diskPath))
                {
                    // Fallback to searching the mod directory
                    diskPath = PenumbraAndGlamourerHelperFunctions.FindMeshDiskPathInModDirectory("", mdlGamePath);
                }

                if (string.IsNullOrEmpty(diskPath) || !File.Exists(diskPath))
                {
                    plugin.PluginLog.Warning($"[ProceduralDecalGenerator] Could not resolve MDL path for {mdlGamePath}.");
                    return null;
                }

                // Parse the MDL and load it into the headless renderer
                var extracted = MdlParser.ParseFromDisk(diskPath, out string status);
                _headlessRenderer.LoadMeshes("Target", extracted);
                
                // Generate Position & Normal maps in UV space for 3D stamping
                // We actually don't have this method publicly on ModelRenderer?
                // ModelRenderer.cs has `BakeUVMaps()` which calls `BakePositionNormalMaps()`!
                _headlessRenderer.BakeUVMaps();

                for (int i = 0; i < numStamps; i++)
                {
                    // Pick a random decal
                    string decalPath = decalPaths[_random.Next(decalPaths.Count)];
                    if (!File.Exists(decalPath)) continue;

                    using var bmp = new System.Drawing.Bitmap(decalPath);
                    var bounds = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
                    var data = bmp.LockBits(bounds, System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    
                    byte[] rgba = new byte[bmp.Width * bmp.Height * 4];
                    unsafe
                    {
                        byte* src = (byte*)data.Scan0;
                        for (int y = 0; y < bmp.Height; y++)
                        {
                            for (int x = 0; x < bmp.Width; x++)
                            {
                                int idx = (y * bmp.Width + x) * 4;
                                rgba[idx + 0] = src[idx + 2]; // R
                                rgba[idx + 1] = src[idx + 1]; // G
                                rgba[idx + 2] = src[idx + 0]; // B
                                rgba[idx + 3] = src[idx + 3]; // A
                            }
                        }
                    }
                    bmp.UnlockBits(data);

                    using var srv = _headlessRenderer.CreateSrvFromRgba(rgba, bmp.Width, bmp.Height);
                    
                    // Generate a random spherical coordinate to raycast from
                    // To ensure even coverage, we randomize longitude (yaw) [0, 2pi]
                    // and latitude (pitch) using acos(2*v - 1) or simply [-pi/2, pi/2]
                    float yaw = (float)(_random.NextDouble() * Math.PI * 2.0);
                    // Bias pitch slightly so it doesn't just hit the top/bottom of the cylinder as often
                    float pitch = (float)(_random.NextDouble() * Math.PI - (Math.PI / 2.0)) * 0.5f;

                    // Set camera to orbit around the bounding box center
                    // We don't need to actually rotate the renderer camera UI, we can just manipulate the view matrix
                    _headlessRenderer.ResetCamera();
                    _headlessRenderer.RotateCamera(yaw, pitch);
                    // Force a render pass so the WVP matrices update
                    _headlessRenderer.Render();

                    // The model generally fills a portion of the screen height. 
                    // We can randomize the Y axis to hit higher or lower on the body.
                    float maxOffsetY = height * 0.35f; // Up to 35% of screen height up/down
                    float offsetY = (float)((_random.NextDouble() * 2.0 - 1.0) * maxOffsetY);

                    // Raycast from the offset center of the screen
                    bool hit = _headlessRenderer.Raycast(new Vector2(width / 2.0f, height / 2.0f + offsetY), out Vector2 uvHit, out string hitSlot, out Vector3 worldPos, out Vector3 worldNormal);
                    
                    if (hit)
                    {
                        // Generate random stamp properties
                        float scale = (float)(_random.NextDouble() * 0.5 + 0.5); // 0.5 to 1.0x size
                        float radius = 0.1f * scale; // Adjust based on model bounding box size
                        
                        // Construct tangent and bitangent for 3D rotation
                        float angle = (float)(_random.NextDouble() * Math.PI * 2.0);
                        Vector3 tangent = Vector3.Normalize(Vector3.Cross(worldNormal, Vector3.UnitY));
                        if (tangent.LengthSquared() < 0.001f) tangent = Vector3.Normalize(Vector3.Cross(worldNormal, Vector3.UnitX));
                        Vector3 bitangent = Vector3.Cross(worldNormal, tangent);

                        // Rotate tangent/bitangent by random angle
                        float cosA = (float)Math.Cos(angle);
                        float sinA = (float)Math.Sin(angle);
                        Vector3 rotTangent = tangent * cosA - bitangent * sinA;
                        Vector3 rotBitangent = tangent * sinA + bitangent * cosA;

                        _headlessRenderer.GpuStampTexture(
                            srv, 
                            uvHit, // Vector2 position (not used much if is3D is true)
                            new Vector2(1, 1), 
                            true, // is3D
                            worldPos, // center
                            worldNormal, // normal
                            rotTangent, // tangent
                            rotBitangent, // bitangent
                            radius, // radius
                            radius * 2.0f // depth
                        );
                    }
                }

                // Readback the paint layer
                byte[] generatedPixels = _headlessRenderer.ReadbackPaintLayer();
                if (generatedPixels == null) return null;

                string outPath = Path.Combine(Plugin.PluginInterface.AssemblyLocation.Directory?.FullName!, $"temp_decal_{bodyPart}_{Guid.NewGuid()}.png");
                
                using var outBmp = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var outRect = new System.Drawing.Rectangle(0, 0, width, height);
                var outData = outBmp.LockBits(outRect, System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                
                unsafe
                {
                    byte* dst = (byte*)outData.Scan0;
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            int idx = (y * width + x) * 4;
                            dst[idx + 2] = generatedPixels[idx + 0]; // R
                            dst[idx + 1] = generatedPixels[idx + 1]; // G
                            dst[idx + 0] = generatedPixels[idx + 2]; // B
                            dst[idx + 3] = generatedPixels[idx + 3]; // A
                        }
                    }
                }
                outBmp.UnlockBits(outData);
                outBmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);

                return outPath;
            }
            catch (Exception ex)
            {
                plugin.PluginLog.Error(ex, "[ProceduralDecalGenerator] Failed to generate procedural overlay.");
                return null;
            }
        }
    }
}