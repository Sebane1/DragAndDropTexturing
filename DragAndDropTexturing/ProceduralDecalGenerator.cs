using System;
using System.IO;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Vortice.Direct3D11;
using Dalamud.Game.ClientState.Objects.Types;
using DragAndDropTexturing.Windows;
using PenumbraAndGlamourerHelpers;

namespace DragAndDropTexturing
{
    public class ProceduralDecalGenerator
    {
        private static ModelRenderer _headlessRenderer = null;
        private static Random _random = new Random();
        
        private static string _lastMeshPath = null;
        private static List<ExtractedMesh> _lastExtracted = null;
        private static Dictionary<string, ID3D11ShaderResourceView> _decalSrvCache = new();
        private static readonly SemaphoreSlim _generationLock = new SemaphoreSlim(1, 1);
        public static bool IsGenerating { get; private set; }

        public static string GenerateProceduralOverlay(Plugin plugin, IGameObject character, string bodyPart, List<string> decalPaths, int numStamps, ConcurrentQueue<Action> mainThreadActions = null)
        {
            if (!_generationLock.Wait(0)) return null; // Skip if already generating
            IsGenerating = true;
            try
            {
                return GenerateProceduralOverlayCore(plugin, character, bodyPart, decalPaths, numStamps, mainThreadActions);
            }
            finally
            {
                IsGenerating = false;
                _generationLock.Release();
            }
        }

        private static string GenerateProceduralOverlayCore(Plugin plugin, IGameObject character, string bodyPart, List<string> decalPaths, int numStamps, ConcurrentQueue<Action> mainThreadActions)
        {
            if (character == null || decalPaths.Count == 0 || numStamps <= 0) return null;

            // Helper: dispatch an action to the main thread and block until it completes
            void RunOnMainThread(Action action)
            {
                if (mainThreadActions == null) { action(); return; }
                using var done = new ManualResetEventSlim(false);
                Exception caught = null;
                mainThreadActions.Enqueue(() => { try { action(); } catch (Exception ex) { caught = ex; } finally { done.Set(); } });
                done.Wait();
                if (caught != null) throw caught;
            }

            T RunOnMainThreadFunc<T>(Func<T> func)
            {
                if (mainThreadActions == null) return func();
                T result = default;
                using var done = new ManualResetEventSlim(false);
                Exception caught = null;
                mainThreadActions.Enqueue(() => { try { result = func(); } catch (Exception ex) { caught = ex; } finally { done.Set(); } });
                done.Wait();
                if (caught != null) throw caught;
                return result;
            }

            try
            {
                int width = 2048;
                int height = 2048;

                RunOnMainThread(() => {
                    if (_headlessRenderer == null)
                    {
                        _headlessRenderer = new ModelRenderer(width, height);
                        _headlessRenderer.InitGpuPaint(width, height);
                    }
                    else if (_headlessRenderer.PaintTexWidth != width || _headlessRenderer.PaintTexHeight != height)
                    {
                        _headlessRenderer.Dispose();
                        _headlessRenderer = new ModelRenderer(width, height);
                        _headlessRenderer.InitGpuPaint(width, height);
                        _lastMeshPath = null;
                        _decalSrvCache.Clear();
                    }
                    _headlessRenderer.GpuClearPaint();
                });

                // Reuse cached mesh if available — skip all Penumbra IPC on subsequent kills
                List<ExtractedMesh> extracted;
                if (_lastMeshPath != null && _lastExtracted != null)
                {
                    extracted = _lastExtracted;
                    plugin.PluginLog.Information($"[ProceduralDecal] Using cached mesh: {_lastMeshPath}");
                }
                else
                {
                    // First call: resolve the mesh path via Penumbra IPC
                    var pChar = (Dalamud.Game.ClientState.Objects.Types.ICharacter)character;
                    var customize = PenumbraAndGlamourerHelperFunctions.GetCustomization(pChar);
                    int ffxivRace = customize.Customize.Race.Value;
                    int ffxivClan = customize.Customize.Clan.Value;
                    int ffxivGender = customize.Customize.Gender.Value;

                    var collectionId = PenumbraAndGlamourerIpcWrapper.Instance.GetCollectionForObject.Invoke((int)character.ObjectIndex).Item3.Id;

                    string GetFfxivModelRaceCode(int race, int clan, int gender)
                    {
                        int code = 101;
                        switch (race)
                        {
                            case 1: code = clan == 2 ? 301 : 101; break;
                            case 2: case 4: case 6: case 8: code = 101; break;
                            case 3: code = 1101; break;
                            case 5: code = 901; break;
                            case 7: code = 1501; break;
                        }
                        if (gender == 1) code += 100;
                        return $"c{code:D4}";
                    }

                    string raceCode = GetFfxivModelRaceCode(ffxivRace, ffxivClan, ffxivGender);
                    string diskPath = null;

                    if (bodyPart.Equals("body", StringComparison.OrdinalIgnoreCase))
                    {
                        string topPath = $"chara/equipment/e0279/model/{raceCode}e0279_top.mdl";
                        PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collectionId, topPath, out string resolvedPath);
                        if (!string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath))
                        {
                            diskPath = resolvedPath;
                        }
                        else
                        {
                            int bodyType = PenumbraAndGlamourerHelperFunctions.DetectBaseBodyFromPenumbra(collectionId, ffxivGender, out string bodyName, plugin);
                            string keyword = bodyType switch { 1 => "bibo", 2 => "gen3", 3 => "tbse", _ => "" };
                            if (!string.IsNullOrEmpty(keyword))
                            {
                                diskPath = PenumbraAndGlamourerHelperFunctions.FindMeshDiskPathInModDirectory(keyword, topPath);
                            }
                        }
                        plugin.PluginLog.Information($"[ProceduralDecal] Body mesh: raceCode={raceCode}, resolved={diskPath ?? "NULL"}");
                    }
                    else if (bodyPart.Equals("face", StringComparison.OrdinalIgnoreCase) || bodyPart.Equals("eyes", StringComparison.OrdinalIgnoreCase) || bodyPart.Equals("eyebrows", StringComparison.OrdinalIgnoreCase))
                    {
                        string faceStr = customize.Customize.Face.Value.ToString("D4");
                        string facePath = $"chara/human/{raceCode}/obj/face/f{faceStr}/model/{raceCode}f{faceStr}_fac.mdl";
                        PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collectionId, facePath, out string resolvedPath);
                        diskPath = !string.IsNullOrEmpty(resolvedPath) && File.Exists(resolvedPath) ? resolvedPath : null;
                        plugin.PluginLog.Information($"[ProceduralDecal] Face mesh: path={facePath}, resolved={diskPath ?? "NULL"}");
                    }
                    else
                    {
                        return null;
                    }

                    if (string.IsNullOrEmpty(diskPath) || !File.Exists(diskPath))
                    {
                        plugin.PluginLog.Warning($"[ProceduralDecalGenerator] Could not resolve MDL disk path. raceCode={raceCode}, bodyPart={bodyPart}");
                        return null;
                    }

                    // Parse MDL and load into GPU
                    extracted = MdlParser.ParseFromDisk(diskPath, out string status);
                    RunOnMainThread(() => {
                        _headlessRenderer.LoadMeshes("Target", extracted);
                        _headlessRenderer.BakeUVMaps();
                    });
                    _lastMeshPath = diskPath;
                    _lastExtracted = extracted;
                }

                // Pre-load or fetch cached decals
                var srvs = new List<ID3D11ShaderResourceView>();
                foreach (string decalPath in decalPaths)
                {
                    if (!File.Exists(decalPath)) continue;

                    if (!_decalSrvCache.TryGetValue(decalPath, out var cachedSrv))
                    {
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
                        int bmpW = bmp.Width, bmpH = bmp.Height;
                        byte[] srvRgba = rgba;
                        cachedSrv = RunOnMainThreadFunc(() => _headlessRenderer.CreateSrvFromRgba(srvRgba, bmpW, bmpH));
                        _decalSrvCache[decalPath] = cachedSrv;
                    }
                    srvs.Add(cachedSrv);
                }

                if (srvs.Count == 0) return null;

                for (int i = 0; i < numStamps; i++)
                {
                    var srv = srvs[_random.Next(srvs.Count)];
                    
                    // Pick a random mesh from the extracted list
                    var targetMesh = extracted[_random.Next(extracted.Count)];
                    int numTriangles = targetMesh.Indices.Count / 3;
                    if (numTriangles == 0) continue;

                    // Pick a random triangle on the mesh
                    int triIndex = _random.Next(numTriangles) * 3;
                    uint idx0 = targetMesh.Indices[triIndex];
                    uint idx1 = targetMesh.Indices[triIndex + 1];
                    uint idx2 = targetMesh.Indices[triIndex + 2];

                    // Safely bounds check
                    if (idx0 >= targetMesh.Positions.Count || idx1 >= targetMesh.Positions.Count || idx2 >= targetMesh.Positions.Count) continue;

                    Vector3 p0 = targetMesh.Positions[(int)idx0];
                    Vector3 p1 = targetMesh.Positions[(int)idx1];
                    Vector3 p2 = targetMesh.Positions[(int)idx2];

                    Vector2 uv0 = targetMesh.UVs[(int)idx0];
                    Vector2 uv1 = targetMesh.UVs[(int)idx1];
                    Vector2 uv2 = targetMesh.UVs[(int)idx2];

                    Vector3 n0 = targetMesh.Normals[(int)idx0];
                    Vector3 n1 = targetMesh.Normals[(int)idx1];
                    Vector3 n2 = targetMesh.Normals[(int)idx2];

                    // Generate random barycentric coordinates for a uniform point inside the triangle
                    float r1 = (float)_random.NextDouble();
                    float r2 = (float)_random.NextDouble();
                    if (r1 + r2 > 1.0f)
                    {
                        r1 = 1.0f - r1;
                        r2 = 1.0f - r2;
                    }
                    float w = 1.0f - r1 - r2;

                    Vector3 worldPos = p0 * r1 + p1 * r2 + p2 * w;
                    Vector2 uvHit = uv0 * r1 + uv1 * r2 + uv2 * w;
                    Vector3 worldNormal = Vector3.Normalize(n0 * r1 + n1 * r2 + n2 * w);

                    plugin.PluginLog.Information($"[ProceduralDecal] Stamping at: {worldPos.X}, {worldPos.Y}, {worldPos.Z} on mesh {triIndex}");

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

                    // Capture locals for lambda
                    var _srv = srv; var _uvHit = uvHit; var _worldPos = worldPos;
                    var _worldNormal = worldNormal; var _rotTangent = rotTangent;
                    var _rotBitangent = rotBitangent; var _radius = radius;
                    RunOnMainThread(() => _headlessRenderer.GpuStampTexture(
                        _srv, _uvHit, new Vector2(1, 1), 1,
                        _worldPos, _worldNormal, _rotTangent, _rotBitangent,
                        _radius, _radius * 2.0f
                    ));
                }

                // Readback the paint layer
                byte[] generatedPixels = RunOnMainThreadFunc(() => _headlessRenderer.ReadbackPaintLayer());
                
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