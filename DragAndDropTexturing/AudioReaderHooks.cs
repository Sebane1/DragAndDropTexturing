using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using System;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;

namespace DragAndDropTexturing {
    public unsafe class AudioReaderHooks : IDisposable {
        public Action<string> OnSoundPlayed;

        // Signature for when an SCD file is loaded into memory
        internal const string LoadSoundFileSig = "E8 ?? ?? ?? ?? 48 85 C0 75 12 B0 F6";
        private delegate IntPtr LoadSoundFileDelegate(IntPtr resourceHandle, uint a2);
        private Hook<LoadSoundFileDelegate>? LoadSoundFileHook { get; set; }

        // Signature for when a specific sound index is played from a loaded SCD
        // Reference: https://github.com/Cytraen/SoundFilter
        internal const string PlaySpecificSoundSig = "48 89 5C 24 ?? 48 89 74 24 ?? 57 48 83 EC 20 33 F6 8B DA 48 8B F9 0F BA E2 0F";
        private delegate void* PlaySpecificSoundDelegate(long a1, int idx);
        private Hook<PlaySpecificSoundDelegate>? PlaySpecificSoundHook { get; set; }

        // GetResourceSync/Async - catches ALL resource loads including SCD files
        // Reference: https://github.com/Ottermandias/Penumbra.GameData/blob/main/Signatures.cs
        internal const string GetResourceSyncSig = "E8 ?? ?? ?? ?? 48 8B C8 8B C3 F0 0F C0 81";
        internal const string GetResourceAsyncSig = "E8 ?? ?? ?? 00 48 8B D8 EB ?? F0 FF 83 ?? ?? 00 00";

        private delegate ResourceHandle* GetResourceSyncDelegate(
            nint resourceManager, uint* categoryId, uint* resourceType,
            int* resourceHash, byte* path, nint getResParams, nint unk7, uint unk8);
        private delegate ResourceHandle* GetResourceAsyncDelegate(
            nint resourceManager, uint* categoryId, uint* resourceType,
            int* resourceHash, byte* path, nint getResParams, byte isUnk, nint unk8, uint unk9);

        private Hook<GetResourceSyncDelegate>? GetResourceSyncHook { get; set; }
        private Hook<GetResourceAsyncDelegate>? GetResourceAsyncHook { get; set; }

        // Updated: based on FFXIVClientStructs ResourceHandle layout
        private const int ResourceDataPointerOffset = 0xB0;

        // Cache SCD data pointers to their file paths
        private ConcurrentDictionary<IntPtr, string> _scdPaths = new();

        public bool IsValid = false;

        public AudioReaderHooks(IGameInteropProvider interopProvider, ISigScanner sigScanner) {
            int hooksInstalled = 0;

            // Hook LoadSoundFile - fires when an SCD file is loaded into memory
            try {
                if (sigScanner.TryScanText(LoadSoundFileSig, out var soundPtr)) {
                    LoadSoundFileHook = interopProvider.HookFromAddress<LoadSoundFileDelegate>(soundPtr, LoadSoundFileDetour);
                    LoadSoundFileHook.Enable();
                    hooksInstalled++;
                    Serilog.Log.Information("[AudioReaderHooks] LoadSoundFile hook initialized.");
                } else {
                    Serilog.Log.Warning("[AudioReaderHooks] LoadSoundFile signature scan failed.");
                }
            } catch (Exception ex) {
                Serilog.Log.Warning($"[AudioReaderHooks] LoadSoundFile hook exception: {ex.Message}");
            }

            // Hook PlaySpecificSound - fires when any sound is actually played
            try {
                if (sigScanner.TryScanText(PlaySpecificSoundSig, out var playPtr)) {
                    PlaySpecificSoundHook = interopProvider.HookFromAddress<PlaySpecificSoundDelegate>(playPtr, PlaySpecificSoundDetour);
                    PlaySpecificSoundHook.Enable();
                    hooksInstalled++;
                    Serilog.Log.Information("[AudioReaderHooks] PlaySpecificSound hook initialized.");
                } else {
                    Serilog.Log.Warning("[AudioReaderHooks] PlaySpecificSound signature scan failed.");
                }
            } catch (Exception ex) {
                Serilog.Log.Warning($"[AudioReaderHooks] PlaySpecificSound hook exception: {ex.Message}");
            }

            // Hook GetResourceSync - catches resource loads including SCDs already cached by the game
            try {
                if (sigScanner.TryScanText(GetResourceSyncSig, out var syncPtr)) {
                    GetResourceSyncHook = interopProvider.HookFromAddress<GetResourceSyncDelegate>(syncPtr, GetResourceSyncDetour);
                    GetResourceSyncHook.Enable();
                    hooksInstalled++;
                    Serilog.Log.Information("[AudioReaderHooks] GetResourceSync hook initialized.");
                } else {
                    Serilog.Log.Warning("[AudioReaderHooks] GetResourceSync signature scan failed.");
                }
            } catch (Exception ex) {
                Serilog.Log.Warning($"[AudioReaderHooks] GetResourceSync hook exception: {ex.Message}");
            }

            // Hook GetResourceAsync - catches async resource loads including SCDs
            try {
                if (sigScanner.TryScanText(GetResourceAsyncSig, out var asyncPtr)) {
                    GetResourceAsyncHook = interopProvider.HookFromAddress<GetResourceAsyncDelegate>(asyncPtr, GetResourceAsyncDetour);
                    GetResourceAsyncHook.Enable();
                    hooksInstalled++;
                    Serilog.Log.Information("[AudioReaderHooks] GetResourceAsync hook initialized.");
                } else {
                    Serilog.Log.Warning("[AudioReaderHooks] GetResourceAsync signature scan failed.");
                }
            } catch (Exception ex) {
                Serilog.Log.Warning($"[AudioReaderHooks] GetResourceAsync hook exception: {ex.Message}");
            }

            IsValid = hooksInstalled > 0;
            Serilog.Log.Information($"[AudioReaderHooks] {hooksInstalled}/4 sound hooks active.");
        }

        public void Dispose() {
            LoadSoundFileHook?.Dispose();
            PlaySpecificSoundHook?.Dispose();
            GetResourceSyncHook?.Dispose();
            GetResourceAsyncHook?.Dispose();
            IsValid = false;
        }

        private static string ReadNullTerminatedString(byte* ptr) {
            if (ptr == null) return "";
            int len = 0;
            while (ptr[len] != 0 && len < 512) len++;
            return Encoding.UTF8.GetString(ptr, len);
        }

        private IntPtr LoadSoundFileDetour(IntPtr resourceHandle, uint a2) {
            var ret = LoadSoundFileHook!.Original(resourceHandle, a2);
            try {
                var handle = (ResourceHandle*)resourceHandle;
                var name = handle->FileName.ToString();
                if (name.EndsWith(".scd")) {
                    var dataPtr = Marshal.ReadIntPtr(resourceHandle + ResourceDataPointerOffset);
                    if (dataPtr != IntPtr.Zero) {
                        _scdPaths[dataPtr] = name;
                    }
                    OnSoundPlayed?.Invoke(name);
                }
            } catch { }
            return ret;
        }

        private void* PlaySpecificSoundDetour(long a1, int idx) {
            try {
                if (a1 != 0) {
                    var scdData = *(byte**)(a1 + 8);
                    if (scdData != null && _scdPaths.TryGetValue((IntPtr)scdData, out var path)) {
                        OnSoundPlayed?.Invoke($"{path}/{idx}");
                    }
                }
            } catch { }
            return PlaySpecificSoundHook!.Original(a1, idx);
        }

        private ResourceHandle* GetResourceSyncDetour(
            nint resourceManager, uint* categoryId, uint* resourceType,
            int* resourceHash, byte* path, nint getResParams, nint unk7, uint unk8) {
            var ret = GetResourceSyncHook!.Original(resourceManager, categoryId, resourceType, resourceHash, path, getResParams, unk7, unk8);
            CacheResourceIfScd(ret, path);
            return ret;
        }

        private ResourceHandle* GetResourceAsyncDetour(
            nint resourceManager, uint* categoryId, uint* resourceType,
            int* resourceHash, byte* path, nint getResParams, byte isUnk, nint unk8, uint unk9) {
            var ret = GetResourceAsyncHook!.Original(resourceManager, categoryId, resourceType, resourceHash, path, getResParams, isUnk, unk8, unk9);
            CacheResourceIfScd(ret, path);
            return ret;
        }

        private void CacheResourceIfScd(ResourceHandle* ret, byte* path) {
            try {
                if (ret == null || path == null) return;
                var strPath = ReadNullTerminatedString(path);
                if (strPath.EndsWith(".scd")) {
                    var dataPtr = Marshal.ReadIntPtr((IntPtr)ret + ResourceDataPointerOffset);
                    if (dataPtr != IntPtr.Zero) {
                        _scdPaths[dataPtr] = strPath;
                    }
                }
            } catch { }
        }
    }
}
