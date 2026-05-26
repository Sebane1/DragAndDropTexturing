using System;
using System.Runtime.InteropServices;

namespace DragAndDropTexturing.Input
{
    /// <summary>
    /// Reads drawing tablet pen pressure via Windows Pointer API polling.
    /// Does NOT open a WinTab context (which breaks mouse input on some Huion drivers).
    /// Requires "Windows Ink" to be enabled in the tablet driver settings.
    /// Call Poll() once per frame.
    /// </summary>
    public sealed class TabletInput : IDisposable
    {
        // --- Public state ---
        public float Pressure { get; private set; } = 1.0f;
        public bool IsPenActive { get; private set; } = false;
        public bool IsEraser { get; private set; } = false;
        public bool IsPenDown { get; private set; } = false;
        public string ActiveBackend { get; private set; } = "None";
        /// <summary>True if a WinTab-compatible tablet driver is installed.</summary>
        public bool TabletDriverDetected { get; private set; } = false;

        private bool _disposed = false;
        private uint _lastKnownPenId = 0;

        public TabletInput(IntPtr hwnd)
        {
            // Just check if a tablet driver is installed (don't open a context)
            try
            {
                IntPtr wintabLib = LoadLibrary("wintab32.dll");
                if (wintabLib != IntPtr.Zero)
                {
                    TabletDriverDetected = true;
                    FreeLibrary(wintabLib);
                }
            }
            catch { }
        }

        /// <summary>Call once per frame to read latest pen state. Zero-cost when no pen is known.</summary>
        public void Poll()
        {
            if (_disposed || _lastKnownPenId == 0) return;

            if (TryReadPenInfo(_lastKnownPenId))
                return;

            // Lost the pen
            _lastKnownPenId = 0;
            IsPenActive = false;
            IsPenDown = false;
            Pressure = 0f;
        }

        /// <summary>Scan for a pen device. Call from UI, not every frame.</summary>
        public bool TryScan()
        {
            for (uint id = 1; id <= 5; id++)
            {
                if (TryReadPenInfo(id))
                {
                    _lastKnownPenId = id;
                    return true;
                }
            }
            return false;
        }

        private bool TryReadPenInfo(uint pointerId)
        {
            if (!GetPointerType(pointerId, out uint pointerType))
                return false;

            if (pointerType != PT_PEN)
                return false;

            if (!GetPointerPenInfo(pointerId, out POINTER_PEN_INFO penInfo))
                return false;

            uint flags = penInfo.pointerInfo.pointerFlags;

            // Check if pointer is at least in range
            if ((flags & POINTER_FLAG_INRANGE) == 0 && (flags & POINTER_FLAG_INCONTACT) == 0)
                return false;

            IsPenActive = true;
            ActiveBackend = "Windows Ink";
            IsPenDown = (flags & POINTER_FLAG_INCONTACT) != 0;
            Pressure = IsPenDown ? Math.Clamp(penInfo.pressure / 1024.0f, 0.01f, 1f) : 0f;
            IsEraser = (penInfo.penFlags & PEN_FLAG_ERASER) != 0;

            return true;
        }

        public void Dispose()
        {
            _disposed = true;
        }

        // --- Win32 constants ---
        private const uint PT_PEN = 3;
        private const uint PEN_FLAG_ERASER = 0x00000004;
        private const uint POINTER_FLAG_INRANGE = 0x00000002;
        private const uint POINTER_FLAG_INCONTACT = 0x00000004;

        // --- Win32 structs ---
        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_INFO
        {
            public uint pointerType;
            public uint pointerId;
            public uint frameId;
            public uint pointerFlags;
            public IntPtr sourceDevice;
            public IntPtr hwndTarget;
            public POINT ptPixelLocation;
            public POINT ptHimetricLocation;
            public POINT ptPixelLocationRaw;
            public POINT ptHimetricLocationRaw;
            public uint dwTime;
            public uint historyCount;
            public int inputData;
            public ulong dwKeyStates;
            public ulong performanceCount;
            public uint ButtonChangeType;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINTER_PEN_INFO
        {
            public POINTER_INFO pointerInfo;
            public uint penFlags;
            public uint penMask;
            public uint pressure;
            public uint rotation;
            public int tiltX;
            public int tiltY;
        }

        // --- P/Invoke ---
        [DllImport("user32.dll")]
        private static extern bool GetPointerType(uint pointerId, out uint pointerType);

        [DllImport("user32.dll")]
        private static extern bool GetPointerPenInfo(uint pointerId, out POINTER_PEN_INFO penInfo);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpLibFileName);

        [DllImport("kernel32.dll")]
        private static extern bool FreeLibrary(IntPtr hModule);
    }
}
