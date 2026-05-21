using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Penumbra.LTCImport.Dds;
using PenumbraAndGlamourerHelpers;

namespace DragAndDropTexturing.VideoPlayback
{
    /// <summary>
    /// Manages animated texture layers that passively loop on character meshes.
    /// Each animated layer pre-renders its frames composited onto the current static
    /// base texture, then cycles through them via rapid Penumbra JSON pointer swaps.
    /// </summary>
    public class AnimatedLayerManager
    {
        private readonly Plugin _plugin;
        private readonly ConcurrentDictionary<string, AnimatedLayerState> _activeLayers = new();
        private CancellationTokenSource _globalCts;
        private Thread _playbackThread;
        private bool _running;

        public bool IsRunning => _running;
        public IReadOnlyDictionary<string, AnimatedLayerState> ActiveLayers => _activeLayers;

        public AnimatedLayerManager(Plugin plugin)
        {
            _plugin = plugin;
        }

        /// <summary>
        /// Prepares and starts an animated layer. If the layer was already active, it is
        /// stopped, re-prepared (e.g. when base textures changed), and restarted.
        /// </summary>
        public async Task ActivateLayer(AnimatedLayerDefinition def, string characterName, ICharacter character)
        {
            string layerId = $"{characterName}_{def.TargetCategory}_{def.Name}";

            // Stop existing if re-preparing
            if (_activeLayers.TryGetValue(layerId, out var existing))
            {
                existing.Active = false;
                _activeLayers.TryRemove(layerId, out _);
            }

            var state = new AnimatedLayerState
            {
                Definition = def,
                CharacterName = characterName,
                LayerId = layerId,
                Status = "Preparing..."
            };
            _activeLayers[layerId] = state;

            try
            {
                await PrepareFrames(state, character);
                state.Active = true;
                state.Status = $"Playing ({state.FrameCount} frames @ {def.Fps}fps)";

                // Ensure the global playback loop is running
                EnsurePlaybackRunning();
            }
            catch (Exception ex)
            {
                state.Status = $"Error: {ex.Message}";
                state.ErrorStackTrace = ex.StackTrace;
                _plugin.PluginLog.Error($"[AnimatedLayers] Failed to activate '{layerId}': {ex.Message}");
            }
        }

        /// <summary>
        /// Stops and removes an animated layer.
        /// </summary>
        public void DeactivateLayer(string layerId)
        {
            if (_activeLayers.TryRemove(layerId, out var state))
            {
                state.Active = false;
                state.ProducerCts?.Cancel();
                state.Status = "Stopped";

                // Disable the Penumbra mod
                try
                {
                    var character = FindCharacter(state.CharacterName);
                    if (character != null)
                    {
                        Guid collection = PenumbraAndGlamourerIpcWrapper.Instance
                            .GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;
                        PenumbraAndGlamourerIpcWrapper.Instance.TrySetMod.Invoke(collection, state.ModName, false, state.ModName);
                    }
                }
                catch { }
            }

            // Stop global loop if no layers remain
            if (_activeLayers.IsEmpty)
                StopPlayback();
        }

        /// <summary>
        /// Called when static layers change for a category. Re-prepares any animated layers
        /// targeting that category so they composite onto the new base.
        /// </summary>
        public void OnStaticLayersChanged(string characterName, string categorySuffix)
        {
            foreach (var kvp in _activeLayers)
            {
                var state = kvp.Value;
                if (state.CharacterName == characterName &&
                    state.Definition.TargetCategory.Equals(categorySuffix, StringComparison.OrdinalIgnoreCase))
                {
                    var character = FindCharacter(characterName);
                    if (character != null)
                    {
                        // Re-prepare on a background thread
                        Task.Run(async () =>
                        {
                            try
                            {
                                state.Active = false; // Pause playback during re-render
                                state.Status = "Re-rendering (base changed)...";
                                state.ErrorStackTrace = null;
                                await PrepareFrames(state, character);
                                state.Active = true;
                                state.Status = $"Playing ({state.FrameCount} frames @ {state.Definition.Fps}fps)";
                            }
                            catch (Exception ex)
                            {
                                state.Status = $"Error: {ex.Message}";
                                state.ErrorStackTrace = ex.StackTrace;
                                _plugin.PluginLog.Error($"[AnimatedLayers] Re-render failed for '{state.LayerId}': {ex.Message}");
                            }
                        });
                    }
                }
            }
        }

        public void Shutdown()
        {
            foreach (var kvp in _activeLayers)
            {
                kvp.Value.Active = false;
                kvp.Value.ProducerCts?.Cancel();
            }
            StopPlayback();
            _activeLayers.Clear();
        }

        #region Frame Preparation

        private async Task PrepareFrames(AnimatedLayerState state, ICharacter character)
        {
            var def = state.Definition;

            // Discover frame images
            var frameFiles = Directory.GetFiles(def.FrameFolder)
                .Where(f => f.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                            f.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase))
                .OrderBy(f => f)
                .ToArray();

            state.FrameCount = frameFiles.Length;
            if (state.FrameCount == 0)
                throw new InvalidOperationException("No image frames found in folder.");

            state.FrameSourceFiles = frameFiles;

            // Set up mod directory
            string modDir = PenumbraAndGlamourerIpcWrapper.Instance.GetModDirectory.Invoke();
            state.ModName = $"{state.CharacterName} Animated {def.Name}";
            state.ModPath = Path.Combine(modDir, state.ModName);
            string framesDir = Path.Combine(state.ModPath, "frames");
            Directory.CreateDirectory(framesDir);

            // Get the base texture from the current static export to composite onto
            Guid collection = PenumbraAndGlamourerIpcWrapper.Instance
                .GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;

            // Resolve the internal game path for this category's base texture
            var customization = PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
            state.InternalGamePath = ResolveInternalPath(def.TargetCategory, customization, character);

            if (string.IsNullOrEmpty(state.InternalGamePath))
                throw new InvalidOperationException($"Cannot resolve game path for category '{def.TargetCategory}'.");

            // Get the current resolved texture (what Penumbra currently maps this path to)
            string resolvedBasePath = null;
            try
            {
                PenumbraAndGlamourerIpcWrapper.Instance.ResolvePath.Invoke(collection, state.InternalGamePath, out resolvedBasePath);
            }
            catch { }

            // Load base texture as Bitmap (what the skin currently looks like)
            Bitmap baseBitmap = null;
            if (!string.IsNullOrEmpty(resolvedBasePath) && File.Exists(resolvedBasePath))
            {
                baseBitmap = FFXIVLooseTextureCompiler.ImageProcessing.TexIO.ResolveBitmap(resolvedBasePath);
            }

            // Apply 25% resolution scaling for performance (16x smaller files, faster Penumbra reloads)
            float outputScale = 0.25f;
            int baseW = (int)((baseBitmap?.Width ?? 1024) * outputScale);
            int baseH = (int)((baseBitmap?.Height ?? 1024) * outputScale);

            // Resize the base bitmap to the scaled dimensions
            if (baseBitmap != null)
            {
                var scaled = new Bitmap(baseW, baseH, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                    g.DrawImage(baseBitmap, 0, 0, baseW, baseH);
                }
                baseBitmap.Dispose();
                baseBitmap = scaled;
            }

            state.BaseW = baseW;
            state.BaseH = baseH;

            // Calculate pixel-space position/size from UV coordinates (at scaled resolution)
            state.StampX = (int)(def.UVPosition.X * baseW);
            state.StampY = (int)(def.UVPosition.Y * baseH);
            state.StampW = (int)(def.UVSize.X * baseW);
            state.StampH = (int)(def.UVSize.Y * baseH);

            // Extract base texture pixels as BGRA byte array for GPU compositing
            if (baseBitmap != null)
            {
                var bmpData = baseBitmap.LockBits(
                    new System.Drawing.Rectangle(0, 0, baseBitmap.Width, baseBitmap.Height),
                    System.Drawing.Imaging.ImageLockMode.ReadOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                state.BasePixels = new byte[baseW * baseH * 4];
                System.Runtime.InteropServices.Marshal.Copy(bmpData.Scan0, state.BasePixels, 0, state.BasePixels.Length);
                baseBitmap.UnlockBits(bmpData);
            }
            else
            {
                state.BasePixels = new byte[baseW * baseH * 4];
            }
            baseBitmap?.Dispose();

            // PRE-CACHE SCALED FRAMES! 
            // Loading 2048x2048 PNGs from disk in the tight producer loop is fatal on Linux/Wine.
            state.CachedFramePixels = new byte[state.FrameCount][];
            var po = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2) };
            Parallel.For(0, state.FrameCount, po, i =>
            {
                using (var ms = new MemoryStream(File.ReadAllBytes(state.FrameSourceFiles[i])))
                using (var bmp = new Bitmap(ms))
                {
                    using (var scaled = new Bitmap(state.StampW, state.StampH, System.Drawing.Imaging.PixelFormat.Format32bppArgb))
                    {
                        using (var g = Graphics.FromImage(scaled))
                        {
                            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                            g.DrawImage(bmp, 0, 0, state.StampW, state.StampH);
                        }
                        var fd = scaled.LockBits(new System.Drawing.Rectangle(0, 0, state.StampW, state.StampH), System.Drawing.Imaging.ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        byte[] pixels = new byte[state.StampW * state.StampH * 4];
                        System.Runtime.InteropServices.Marshal.Copy(fd.Scan0, pixels, 0, pixels.Length);
                        scaled.UnlockBits(fd);
                        state.CachedFramePixels[i] = pixels;
                    }
                }
            });

            // Pre-compute the 80-byte .tex header (same for every frame)
            state.TexHeader = new byte[80];
            using (var hdrMs = new MemoryStream(state.TexHeader))
            using (var hdrBw = new BinaryWriter(hdrMs))
            {
                hdrBw.Write((uint)Lumina.Data.Files.TexFile.Attribute.TextureType2D);
                hdrBw.Write((uint)Lumina.Data.Files.TexFile.TextureFormat.B8G8R8A8);
                hdrBw.Write((ushort)baseW);
                hdrBw.Write((ushort)baseH);
                hdrBw.Write((ushort)1);
                hdrBw.Write((ushort)1);
                hdrBw.Write(0);
                hdrBw.Write(1);
                hdrBw.Write(2);
                hdrBw.Write(80);
                for (int h = 1; h < 13; h++) hdrBw.Write(0);
            }

            state.PixelBytes = baseW * baseH * 4;

            // Ring buffer: only RING_SIZE .tex files on disk at once
            int ringSize = AnimatedLayerState.RING_SIZE;
            state.FrameTexPaths = new string[ringSize];
            state.FrameReady = new bool[ringSize];
            state.FramesDir = framesDir;
            state.FileCounter = 0;

            state.PreparationProgress = 0;

            // Pre-render a small initial buffer so playback starts quickly
            int initialCount = Math.Min(60, Math.Min(ringSize, state.FrameCount));
            await Task.Run(() =>
            {
                for (int i = 0; i < initialCount; i++)
                {
                    RenderFrameToSlot(state, i, i % ringSize);
                    state.PreparationProgress = (float)(i + 1) / initialCount;
                }
            });

            state.ProducerIndex = initialCount; // next frame to render
            state.CurrentFrame = 0;

            // Write Penumbra mod metadata
            LooseTextureCompilerCore.ProjectCreation.ProjectHelper.ExportMeta(
                Path.Combine(state.ModPath, "meta.json"), state.ModName);

            // Write initial JSON pointing at ring slot 0
            WriteFrameJson(state, 0);

            // Register mod with Penumbra
            var existingMods = PenumbraAndGlamourerIpcWrapper.Instance.GetModList.Invoke();
            if (existingMods == null || !existingMods.ContainsKey(state.ModName))
            {
                PenumbraAndGlamourerIpcWrapper.Instance.AddMod.Invoke(state.ModName);
                Thread.Sleep(200); // First-time compaction
            }
            PenumbraAndGlamourerIpcWrapper.Instance.ReloadMod.Invoke(state.ModName, state.ModName);

            // Enable mod at high priority
            PenumbraAndGlamourerIpcWrapper.Instance.TrySetMod.Invoke(collection, state.ModName, true, state.ModName);
            PenumbraAndGlamourerIpcWrapper.Instance.TrySetModPriority.Invoke(collection, state.ModName, 200, state.ModName);
            try
            {
                var settings = PenumbraAndGlamourerIpcWrapper.Instance.GetCurrentModSettings.Invoke(collection, state.ModName, state.ModName, true);
                foreach (var group in settings.Item2.Value.Item3)
                    PenumbraAndGlamourerIpcWrapper.Instance.TrySetModSetting.Invoke(collection, state.ModName, group.Key, "Enable", state.ModName);
            }
            catch { }

            // Start background producer thread to stay ahead of playback
            state.ProducerCts = new CancellationTokenSource();
            Task.Run(() => ProducerLoop(state, state.ProducerCts.Token));

            _plugin.PluginLog.Information($"[AnimatedLayers] Prepared '{state.LayerId}': {state.FrameCount} frames (ring buffer={ringSize}), game path: {state.InternalGamePath}");
        }

        /// <summary>
        /// Renders a single source frame to a uniquely-named .tex file and updates the ring slot.
        /// Old files in the slot are cleaned up to avoid disk bloat.
        /// </summary>
        private void RenderFrameToSlot(AnimatedLayerState state, int sourceFrameIndex, int ringSlot)
        {
            var def = state.Definition;
            state.FrameReady[ringSlot] = false;

            // Generate a unique filename — never collides with Penumbra's file lock
            string newTexPath = Path.Combine(state.FramesDir,
                $"{Guid.NewGuid():N}.tex");

            // Remember old path for cleanup
            string oldTexPath = state.FrameTexPaths[ringSlot];

            try
            {
                // Grab the pre-scaled bytes directly from memory cache
                byte[] framePixels = state.CachedFramePixels[sourceFrameIndex];
                int frameW = state.StampW;
                int frameH = state.StampH;

                byte[] composited = null;
                try
                {
                    composited = FFXIVLooseTextureCompiler.ImageProcessing.ComputeSharpLayering
                        .CompositeFrameGpu(state.BasePixels, state.BaseW, state.BaseH,
                            framePixels, frameW, frameH,
                            state.StampX, state.StampY, state.StampW, state.StampH, def.Opacity);
                }
                catch (Exception gpuEx)
                {
                    // Fallback to pure C# byte array blending for Linux/Proton
                    // Since we pre-scaled the frame to exactly StampW x StampH, we can do 1:1 pixel mapping.
                    composited = new byte[state.BasePixels.Length];
                    Buffer.BlockCopy(state.BasePixels, 0, composited, 0, state.BasePixels.Length);

                    int bW = state.BaseW;
                    int bH = state.BaseH;
                    float opac = def.Opacity;
                    int sW = state.StampW;
                    int sH = state.StampH;
                    int sX = state.StampX;
                    int sY = state.StampY;

                    // Direct BGRA alpha blending
                    for (int fy = 0; fy < sH; fy++)
                    {
                        int by = sY + fy;
                        if (by < 0 || by >= bH) continue;

                        for (int fx = 0; fx < sW; fx++)
                        {
                            int bx = sX + fx;
                            if (bx < 0 || bx >= bW) continue;

                            int fIdx = (fy * sW + fx) * 4;
                            int bIdx = (by * bW + bx) * 4;

                            byte fA = framePixels[fIdx + 3];
                            if (fA == 0) continue;

                            float alpha = (fA / 255f) * opac;
                            if (alpha >= 0.99f)
                            {
                                composited[bIdx] = framePixels[fIdx];
                                composited[bIdx + 1] = framePixels[fIdx + 1];
                                composited[bIdx + 2] = framePixels[fIdx + 2];
                                composited[bIdx + 3] = 255;
                            }
                            else
                            {
                                float invAlpha = 1f - alpha;
                                composited[bIdx] = (byte)(framePixels[fIdx] * alpha + composited[bIdx] * invAlpha);
                                composited[bIdx + 1] = (byte)(framePixels[fIdx + 1] * alpha + composited[bIdx + 1] * invAlpha);
                                composited[bIdx + 2] = (byte)(framePixels[fIdx + 2] * alpha + composited[bIdx + 2] * invAlpha);
                                composited[bIdx + 3] = (byte)Math.Min(255, composited[bIdx + 3] + (fA * opac));
                            }
                        }
                    }
                }

                // Write to NEW file (Penumbra can't lock what doesn't exist yet)
                using (var fs = new FileStream(newTexPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
                {
                    fs.Write(state.TexHeader, 0, 80);
                    fs.Write(composited, 0, state.PixelBytes);
                }

                state.FrameTexPaths[ringSlot] = newTexPath;
                state.FrameReady[ringSlot] = true;

                // Clean up old file (Penumbra may have released it by now)
                if (!string.IsNullOrEmpty(oldTexPath) && oldTexPath != newTexPath)
                {
                    try { File.Delete(oldTexPath); }
                    catch { } // Still locked — will be cleaned up on shutdown
                }
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error($"[AnimatedLayers] Frame {sourceFrameIndex}→slot {ringSlot} error: {ex.Message}");
            }
        }

        /// <summary>
        /// Background producer: renders frames ahead of playback into ring buffer slots.
        /// Pauses when the buffer is full (all slots ahead of playback are rendered).
        /// </summary>
        private void ProducerLoop(AnimatedLayerState state, CancellationToken ct)
        {
            var frameSw = new Stopwatch();
            int logCounter = 0;

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    int currentPlayback = state.CurrentFrame;
                    int nextToProduce = state.ProducerIndex;

                    // Don't overwrite slots playback hasn't consumed yet
                    int ahead = nextToProduce - currentPlayback;
                    if (ahead < 0) ahead += state.FrameCount;

                    if (ahead >= AnimatedLayerState.RING_SIZE - 1)
                    {
                        // Buffer is full, wait for playback to free a slot
                        Thread.Sleep(5);
                        continue;
                    }

                    if (nextToProduce >= state.FrameCount)
                    {
                        // All frames rendered — idle until loop wraps or cancellation
                        Thread.Sleep(50);
                        continue;
                    }

                    int slot = nextToProduce % AnimatedLayerState.RING_SIZE;

                    frameSw.Restart();
                    RenderFrameToSlot(state, nextToProduce, slot);
                    frameSw.Stop();

                    state.ProducerIndex = nextToProduce + 1;

                    if (++logCounter % 10 == 0)
                    {
                        int lead = nextToProduce - currentPlayback;
                        _plugin.PluginLog.Information(
                            $"[AnimatedLayers Producer] Frame {nextToProduce}: {frameSw.ElapsedMilliseconds}ms | " +
                            $"Lead: {lead} | Playback: {currentPlayback}");
                    }
                }
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error($"[AnimatedLayers] Producer loop error: {ex.Message}");
            }
            finally
            {
                FFXIVLooseTextureCompiler.ImageProcessing.ComputeSharpLayering.ReleaseStampResources();
            }
        }

        #endregion

        #region Playback Loop

        private void EnsurePlaybackRunning()
        {
            if (_running) return;

            _globalCts = new CancellationTokenSource();
            _running = true;
            _playbackThread = new Thread(() => PlaybackLoop(_globalCts.Token))
            {
                IsBackground = true,
                Name = "AnimatedLayerPlayback"
            };
            _playbackThread.Start();
        }

        private void StopPlayback()
        {
            _globalCts?.Cancel();
            _running = false;
            _playbackThread?.Join(2000);
            _playbackThread = null;
        }

        private void PlaybackLoop(CancellationToken ct)
        {
            var sw = new Stopwatch();
            // Track per-layer timing
            var lastFrameTime = new ConcurrentDictionary<string, DateTime>();

            try
            {
                while (!ct.IsCancellationRequested)
                {
                    bool anyActive = false;

                    foreach (var kvp in _activeLayers)
                    {
                        var state = kvp.Value;
                        if (!state.Active || state.FrameTexPaths == null || state.FrameReady == null) continue;
                        anyActive = true;

                        double msPerFrame = 1000.0 / state.Definition.Fps;
                        var now = DateTime.UtcNow;
                        var lastTime = lastFrameTime.GetOrAdd(state.LayerId, DateTime.MinValue);

                        if ((now - lastTime).TotalMilliseconds < msPerFrame)
                            continue;

                        lastFrameTime[state.LayerId] = now;

                        // Advance frame (wrap for looping)
                        if (state.CurrentFrame >= state.FrameCount)
                            state.CurrentFrame = 0;

                        int ringSlot = state.CurrentFrame % AnimatedLayerState.RING_SIZE;

                        // Wait for the producer to have this slot ready
                        if (!state.FrameReady[ringSlot])
                        {
                            // Producer hasn't caught up yet, skip this tick
                            continue;
                        }

                        try
                        {
                            // Rewrite JSON pointer to this ring slot's .tex
                            WriteFrameJson(state, ringSlot);

                            // Reload mod (synchronous)
                            PenumbraAndGlamourerIpcWrapper.Instance.ReloadMod.Invoke(state.ModName, state.ModName);

                            // Double framework yield + Glamourer refresh
                            var tcs = new TaskCompletionSource<bool>();
                            Plugin.Framework.RunOnFrameworkThread(() =>
                            {
                                Plugin.Framework.RunOnFrameworkThread(() =>
                                {
                                    try
                                    {
                                        var character = FindCharacter(state.CharacterName);
                                        if (character != null)
                                        {
                                            var cust = PenumbraAndGlamourerHelperFunctions.GetCustomization(character);
                                            if (cust?.Equipment != null)
                                            {
                                                // Refresh equipment slots to force texture reload
                                                var ipc = PenumbraAndGlamourerIpcWrapper.Instance;
                                                ipc.SetItem.Invoke(
                                                    character.ObjectIndex,
                                                    Glamourer.Api.Enums.ApiEquipSlot.Body,
                                                    (ulong)cust.Equipment.Body.ItemId,
                                                    new List<byte> { (byte)cust.Equipment.Body.Stain });
                                                
                                                ipc.SetItem.Invoke(
                                                    character.ObjectIndex,
                                                    Glamourer.Api.Enums.ApiEquipSlot.Legs,
                                                    (ulong)cust.Equipment.Legs.ItemId,
                                                    new List<byte> { (byte)cust.Equipment.Legs.Stain });

                                                ipc.SetItem.Invoke(
                                                    character.ObjectIndex,
                                                    Glamourer.Api.Enums.ApiEquipSlot.Hands,
                                                    (ulong)cust.Equipment.Hands.ItemId,
                                                    new List<byte> { (byte)cust.Equipment.Hands.Stain });

                                                ipc.SetItem.Invoke(
                                                    character.ObjectIndex,
                                                    Glamourer.Api.Enums.ApiEquipSlot.Feet,
                                                    (ulong)cust.Equipment.Feet.ItemId,
                                                    new List<byte> { (byte)cust.Equipment.Feet.Stain });
                                            }
                                        }
                                    }
                                    catch { }
                                    tcs.TrySetResult(true);
                                });
                            });

                            tcs.Task.Wait(TimeSpan.FromMilliseconds(500));

                            // Mark this slot as consumed so the producer can overwrite it
                            state.FrameReady[ringSlot] = false;
                        }
                        catch (Exception ex)
                        {
                            _plugin.PluginLog.Error($"[AnimatedLayers] Playback tick error for '{state.LayerId}': {ex.Message}");
                        }

                        state.CurrentFrame++;
                    }

                    if (!anyActive)
                        Thread.Sleep(50); // Idle when no layers need advancing
                    else
                        Thread.Sleep(1); // Tight loop when active
                }
            }
            catch (Exception ex)
            {
                _plugin.PluginLog.Error($"[AnimatedLayers] Playback loop error: {ex.Message}");
            }
            finally
            {
                _running = false;
            }
        }

        #endregion

        #region Helpers

        private static void WriteFrameJson(AnimatedLayerState state, int ringSlot)
        {
            if (string.IsNullOrEmpty(state.FrameTexPaths[ringSlot])) return;
            string relativePath = Path.GetRelativePath(state.ModPath, state.FrameTexPaths[ringSlot]).Replace('\\', '/');
            string gamePath = state.InternalGamePath.Replace("\\", "/");
            string json = $@"{{
  ""Name"": """",
  ""Priority"": 0,
  ""Files"": {{ ""{gamePath}"": ""{relativePath}"" }},
  ""FileSwaps"": {{}},
  ""Manipulations"": []
}}";
            File.WriteAllText(Path.Combine(state.ModPath, "default_mod.json"), json);
        }

        private ICharacter FindCharacter(string name)
        {
            if (_plugin.SafeGameObjectManager?.LocalPlayer != null &&
                _plugin.SafeGameObjectManager.LocalPlayer.Name.TextValue == name)
            {
                return _plugin.SafeGameObjectManager.LocalPlayer as ICharacter;
            }

            foreach (var obj in _plugin.GetNearestObjects())
            {
                var c = obj as ICharacter;
                if (c != null && c.Name.TextValue == name)
                    return c;
            }
            return null;
        }

        /// <summary>
        /// Resolves the FFXIV internal game path for a body category's base diffuse texture.
        /// </summary>
        private string ResolveInternalPath(string category, 
            PenumbraAndGlamourerHelpers.IPC.ThirdParty.Glamourer.CharacterCustomization customization,
            ICharacter character = null)
        {
            if (customization == null) return null;

            int race = customization.Customize.Race.Value - 1;
            int clan = customization.Customize.Clan.Value - 1;
            int gender = customization.Customize.Gender.Value;
            int face = customization.Customize.Face.Value - 1;
            int mainRace = FFXIVLooseTextureCompiler.Racial.RaceInfo.SubRaceToMainRace(clan);

            switch (category.ToLower())
            {
                case "body":
                    // Detect which body mod is active via Penumbra
                    int bodyType = gender == 0 ? 3 : 1; // default: TBSE male, Bibo+ female
                    if (character != null)
                    {
                        try
                        {
                            Guid collectionId = PenumbraAndGlamourerIpcWrapper.Instance
                                .GetCollectionForObject.Invoke(character.ObjectIndex).Item3.Id;
                            int detected = PenumbraAndGlamourerHelperFunctions
                                .DetectBaseBodyFromPenumbra(collectionId, gender, out string _, _plugin);
                            // DetectBaseBodyFromPenumbra returns: 1=Bibo, 2=Gen3, 3=TBSE, 5=Otopop
                            if (detected == 1) bodyType = 1;      // Bibo+
                            else if (detected == 2) bodyType = 2;  // Gen3
                            else if (detected == 3) bodyType = 3;  // TBSE
                            else if (detected == 5) bodyType = 5;  // Otopop
                        }
                        catch (Exception ex)
                        {
                            _plugin.PluginLog.Warning($"[AnimatedLayers] Body detection failed, using default: {ex.Message}");
                        }
                    }
                    var bodyTs = LooseTextureCompilerCore.ProjectCreation.ProjectHelper.CreateBodyTextureSet(
                        gender, bodyType, mainRace, 0, false);
                    _plugin.PluginLog.Information($"[AnimatedLayers] Resolved body path: bodyType={bodyType}, path={bodyTs?.InternalBasePath}");
                    return bodyTs?.InternalBasePath;

                case "face":
                    var faceTs = LooseTextureCompilerCore.ProjectCreation.ProjectHelper.CreateFaceTextureSet(
                        face, 0, 0, gender, race, clan, 0, false);
                    return faceTs?.InternalBasePath;

                default:
                    return null;
            }
        }

        #endregion
    }

    /// <summary>
    /// Runtime state for a single active animated layer.
    /// </summary>
    public class AnimatedLayerState
    {
        public AnimatedLayerDefinition Definition { get; set; }
        public string LayerId { get; set; }
        public string CharacterName { get; set; }
        public string ModName { get; set; }
        public string ModPath { get; set; }
        public string InternalGamePath { get; set; }
        public string[] FrameTexPaths { get; set; }
        public int FrameCount { get; set; }
        public int CurrentFrame { get; set; }
        public bool Active { get; set; }
        public float PreparationProgress { get; set; }
        public string Status { get; set; }
        public string ErrorStackTrace { get; set; }

        // Ring buffer support
        public const int RING_SIZE = 1000;
        public string[] FrameSourceFiles { get; set; }
        public byte[][] CachedFramePixels { get; set; }
        public byte[] BasePixels { get; set; }
        public int BaseW { get; set; }
        public int BaseH { get; set; }
        public int StampX { get; set; }
        public int StampY { get; set; }
        public int StampW { get; set; }
        public int StampH { get; set; }
        public byte[] TexHeader { get; set; }
        public int PixelBytes { get; set; }
        public bool[] FrameReady { get; set; }
        public int ProducerIndex { get; set; }
        public CancellationTokenSource ProducerCts { get; set; }
        public string FramesDir { get; set; }
        public int FileCounter;
    }
}
