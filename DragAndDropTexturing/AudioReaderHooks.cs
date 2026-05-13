using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Resource.Handle;
using System;
using System.Runtime.InteropServices;

namespace DragAndDropTexturing {
    public unsafe class AudioReaderHooks : IDisposable {
        public Action<string> OnSoundPlayed;

        internal const string LoadSoundFile = "E8 ?? ?? ?? ?? 48 85 C0 75 12 B0 F6";
        private delegate IntPtr LoadSoundFileDelegate(IntPtr resourceHandle, uint a2);
        private Hook<LoadSoundFileDelegate>? LoadSoundFileHook { get; set; }

        public bool IsValid = false;

        public AudioReaderHooks(IGameInteropProvider interopProvider, ISigScanner sigScanner) {
            try {
                if (sigScanner.TryScanText(LoadSoundFile, out var soundPtr)) {
                    LoadSoundFileHook = interopProvider.HookFromAddress<LoadSoundFileDelegate>(soundPtr, LoadSoundFileDetour);
                    LoadSoundFileHook.Enable();
                    IsValid = true;
                }
            } catch { }
        }

        public void Dispose() {
            LoadSoundFileHook?.Dispose();
            IsValid = false;
        }

        private IntPtr LoadSoundFileDetour(IntPtr resourceHandle, uint a2) {
            var ret = LoadSoundFileHook!.Original(resourceHandle, a2);
            try {
                var handle = (ResourceHandle*)resourceHandle;
                var name = handle->FileName.ToString();
                if (name.EndsWith(".scd")) {
                    OnSoundPlayed?.Invoke(name);
                }
            } catch { }
            return ret;
        }
    }
}
