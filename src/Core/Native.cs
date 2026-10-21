using System;
using System.Runtime.InteropServices;
using System.Text;

namespace AndroidTVDesktop.Core
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
        public int Width => Right - Left;
        public int Height => Bottom - Top;
        public bool IsEmpty => Width <= 0 || Height <= 0;
        public override string ToString() => $"({Left},{Top}) {Width}x{Height}";
        public bool Near(RECT o, int tol) =>
            Math.Abs(Left - o.Left) <= tol && Math.Abs(Top - o.Top) <= tol &&
            Math.Abs(Right - o.Right) <= tol && Math.Abs(Bottom - o.Bottom) <= tol;
        public bool Contains(RECT o) => Left <= o.Left && Top <= o.Top && Right >= o.Right && Bottom >= o.Bottom;
        public RECT Intersect(RECT o) => new RECT(Math.Max(Left, o.Left), Math.Max(Top, o.Top), Math.Min(Right, o.Right), Math.Min(Bottom, o.Bottom));
        public long Area => IsEmpty ? 0 : (long)Width * Height;
    }

    /// <summary>All Win32 interop used by the app. Coordinates are physical pixels (the app is Per-Monitor-V2 DPI aware).</summary>
    public static class Native
    {
        public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        public const long WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
                          WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000;
        public const long WS_EX_DLGMODALFRAME = 0x1, WS_EX_TOPMOST = 0x8, WS_EX_TOOLWINDOW = 0x80, WS_EX_WINDOWEDGE = 0x100,
                          WS_EX_CLIENTEDGE = 0x200, WS_EX_STATICEDGE = 0x20000, WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20,
                          SWP_SHOWWINDOW = 0x40, SWP_NOOWNERZORDER = 0x200;
        public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOW = 5, SW_RESTORE = 9;
        public const uint WM_CLOSE = 0x10;
        public static readonly IntPtr HWND_TOPMOST = new IntPtr(-1), HWND_NOTOPMOST = new IntPtr(-2);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        public delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

        [StructLayout(LayoutKind.Sequential)]
        public struct KBDLLHOOKSTRUCT { public uint vkCode, scanCode, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        public struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData, flags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct MONITORINFOEX
        {
            public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct DEVMODE
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
            public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
            public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
            public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
            public short dmLogPixels;
            public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
            public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
        }

        // ---- user32: windows
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr l);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)] static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("user32.dll", SetLastError = true)] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
        [DllImport("user32.dll")] public static extern int GetWindowRgnBox(IntPtr h, out RECT r);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int l, int t, int r, int b);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT r, int size);

        // ---- user32: monitors / DPI
        [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX mi);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr m, int type, out uint dpiX, out uint dpiY);

        // ---- input
        [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr hMod, uint thread);
        [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr h, int n, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
        [DllImport("user32.dll")] public static extern bool ClipCursor(ref RECT r);
        [DllImport("user32.dll", EntryPoint = "ClipCursor")] public static extern bool ClipCursorNull(IntPtr zero);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);

        // ---- kernel32
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll")] public static extern void SetLastError(uint e);

        // ---- helpers
        public static long GetLong(IntPtr h, int index) => GetWindowLongPtr(h, index).ToInt64() & 0xFFFFFFFFL;
        /// <summary>Returns the Win32 error (0 = success).</summary>
        public static int SetLong(IntPtr h, int index, long value)
        {
            SetLastError(0);
            long prev = SetWindowLongPtr(h, index, new IntPtr(value)).ToInt64();
            int err = Marshal.GetLastWin32Error();
            return (prev == 0 && err != 0) ? err : 0;
        }
        public static RECT Rect(IntPtr h) { GetWindowRect(h, out var r); return r; }
        public static string Text(IntPtr h) { var sb = new StringBuilder(512); GetWindowText(h, sb, 512); return sb.ToString(); }
        public static string Class(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
        public static uint Pid(IntPtr h) { GetWindowThreadProcessId(h, out var p); return p; }
        public static string Hex(IntPtr h) => "0x" + h.ToInt64().ToString("X");
        public static bool IsTopmost(IntPtr h) => (GetLong(h, GWL_EXSTYLE) & WS_EX_TOPMOST) != 0;
        public static IntPtr Owner(IntPtr h) => GetWindow(h, 4 /*GW_OWNER*/);
    }

    /// <summary>Windows DisplayConfig API: friendly monitor names, stable device paths and HDR (advanced colour) state. Read-only.</summary>
    public static class DisplayConfig
    {
        [StructLayout(LayoutKind.Sequential)] public struct LUID { public uint LowPart; public int HighPart; }
        [StructLayout(LayoutKind.Sequential)]
        struct PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }
        [StructLayout(LayoutKind.Sequential)]
        struct RATIONAL { public uint Numerator, Denominator; }
        [StructLayout(LayoutKind.Sequential)]
        struct PATH_TARGET_INFO
        {
            public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation;
            public uint scaling; public RATIONAL refreshRate; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct PATH_INFO { public PATH_SOURCE_INFO sourceInfo; public PATH_TARGET_INFO targetInfo; public uint flags; }
        [StructLayout(LayoutKind.Explicit, Size = 64)]
        struct MODE_INFO { [FieldOffset(0)] public uint infoType; }
        [StructLayout(LayoutKind.Sequential)]
        struct HEADER { public uint type; public uint size; public LUID adapterId; public uint id; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct SOURCE_DEVICE_NAME { public HEADER header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct TARGET_DEVICE_NAME
        {
            public HEADER header; public uint flags; public uint outputTechnology; public ushort edidManufactureId; public ushort edidProductCodeId;
            public uint connectorInstance;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct ADVANCED_COLOR_INFO { public HEADER header; public uint value; public uint colorEncoding; public uint bitsPerColorChannel; }

        [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
        [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PATH_INFO[] p, ref uint modes, [Out] MODE_INFO[] m, IntPtr topology);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SOURCE_DEVICE_NAME r);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_DEVICE_NAME r);
        [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref ADVANCED_COLOR_INFO r);

        public sealed class Target
        {
            public string GdiName = "", FriendlyName = "", DevicePath = "";
            public double RefreshHz;
            public bool HdrSupported, HdrEnabled, HdrQueryOk;
            public int BitsPerChannel;
        }

        /// <summary>Active display paths keyed by GDI device name (\\.\DISPLAYn).</summary>
        public static System.Collections.Generic.Dictionary<string, Target> Query()
        {
            var result = new System.Collections.Generic.Dictionary<string, Target>(StringComparer.OrdinalIgnoreCase);
            const uint QDC_ONLY_ACTIVE_PATHS = 2;
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return result;
            var paths = new PATH_INFO[np]; var modes = new MODE_INFO[nm];
            if (QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return result;
            for (int i = 0; i < np; i++)
            {
                var p = paths[i];
                var src = new SOURCE_DEVICE_NAME();
                src.header.type = 1; src.header.size = (uint)Marshal.SizeOf(typeof(SOURCE_DEVICE_NAME));
                src.header.adapterId = p.sourceInfo.adapterId; src.header.id = p.sourceInfo.id;
                if (DisplayConfigGetDeviceInfo(ref src) != 0) continue;
                var t = new Target { GdiName = src.viewGdiDeviceName };
                var tgt = new TARGET_DEVICE_NAME();
                tgt.header.type = 2; tgt.header.size = (uint)Marshal.SizeOf(typeof(TARGET_DEVICE_NAME));
                tgt.header.adapterId = p.targetInfo.adapterId; tgt.header.id = p.targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref tgt) == 0) { t.FriendlyName = tgt.monitorFriendlyDeviceName ?? ""; t.DevicePath = tgt.monitorDevicePath ?? ""; }
                if (p.targetInfo.refreshRate.Denominator != 0) t.RefreshHz = p.targetInfo.refreshRate.Numerator / (double)p.targetInfo.refreshRate.Denominator;
                var aci = new ADVANCED_COLOR_INFO();
                aci.header.type = 9; aci.header.size = (uint)Marshal.SizeOf(typeof(ADVANCED_COLOR_INFO));
                aci.header.adapterId = p.targetInfo.adapterId; aci.header.id = p.targetInfo.id;
                if (DisplayConfigGetDeviceInfo(ref aci) == 0)
                {
                    t.HdrQueryOk = true;
                    t.HdrSupported = (aci.value & 0x1) != 0;   // advancedColorSupported
                    t.HdrEnabled = (aci.value & 0x2) != 0;     // advancedColorEnabled
                    t.BitsPerChannel = (int)aci.bitsPerColorChannel;
                }
                if (!result.ContainsKey(t.GdiName)) result[t.GdiName] = t;
            }
            return result;
        }
    }
}
