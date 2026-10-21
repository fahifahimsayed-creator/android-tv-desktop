using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AndroidTVDesktop.Core
{
    public sealed class MonitorInfo
    {
        /// <summary>Stable id: the monitor device path when Windows reports one, else the GDI name.</summary>
        public string Id = "";
        public string GdiName = "";        // \\.\DISPLAY1 (changes when monitors are re-plugged)
        public string FriendlyName = "";   // from EDID, e.g. "LG ULTRAGEAR"
        public RECT Bounds;                // physical pixels, virtual-desktop coordinates
        public RECT WorkArea;
        public bool IsPrimary;
        public int ModeWidth, ModeHeight;  // current display mode (native desktop resolution)
        public double RefreshHz;
        public uint DpiX = 96;
        public bool HdrSupported, HdrEnabled, HdrKnown;
        public int BitsPerChannel;

        public double Scale => DpiX / 96.0;
        public string Resolution => $"{Bounds.Width} x {Bounds.Height}";
        public string Label => $"{(string.IsNullOrEmpty(FriendlyName) ? GdiName : FriendlyName)} - {Bounds.Width}x{Bounds.Height}" +
                               (RefreshHz > 0 ? $" @ {RefreshHz:0.#} Hz" : "") + (IsPrimary ? " (primary)" : "");
        public string HdrText => !HdrKnown ? "Unknown" : HdrSupported ? (HdrEnabled ? "Supported, ON in Windows" : "Supported, OFF in Windows") : "Not supported";
        public override string ToString() => $"{Label} bounds={Bounds} work={WorkArea} scale={Scale:P0} HDR={HdrText} id={Id}";
    }

    public static class MonitorService
    {
        /// <summary>Enumerates active monitors with physical bounds, DPI, refresh rate and HDR state.</summary>
        public static List<MonitorInfo> Enumerate()
        {
            var list = new List<MonitorInfo>();
            Dictionary<string, DisplayConfig.Target> targets;
            try { targets = DisplayConfig.Query(); }
            catch (Exception ex) { Log.Warn("DisplayConfig query failed: " + ex.Message); targets = new Dictionary<string, DisplayConfig.Target>(); }

            Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref RECT r, IntPtr data) =>
            {
                var mi = new Native.MONITORINFOEX { cbSize = Marshal.SizeOf(typeof(Native.MONITORINFOEX)) };
                if (!Native.GetMonitorInfo(h, ref mi)) return true;
                var m = new MonitorInfo
                {
                    GdiName = mi.szDevice, Bounds = mi.rcMonitor, WorkArea = mi.rcWork, IsPrimary = (mi.dwFlags & 1) != 0,
                };
                if (Native.GetDpiForMonitor(h, 0 /*MDT_EFFECTIVE_DPI*/, out uint dx, out uint _) == 0) m.DpiX = dx;
                var dm = new Native.DEVMODE { dmSize = (short)Marshal.SizeOf(typeof(Native.DEVMODE)) };
                if (Native.EnumDisplaySettings(mi.szDevice, -1 /*ENUM_CURRENT_SETTINGS*/, ref dm))
                {
                    m.ModeWidth = dm.dmPelsWidth; m.ModeHeight = dm.dmPelsHeight; m.RefreshHz = dm.dmDisplayFrequency;
                }
                if (targets.TryGetValue(mi.szDevice, out var t))
                {
                    m.FriendlyName = t.FriendlyName;
                    if (t.RefreshHz > 0) m.RefreshHz = Math.Round(t.RefreshHz, 2);
                    m.HdrKnown = t.HdrQueryOk; m.HdrSupported = t.HdrSupported; m.HdrEnabled = t.HdrEnabled; m.BitsPerChannel = t.BitsPerChannel;
                    m.Id = string.IsNullOrEmpty(t.DevicePath) ? mi.szDevice : t.DevicePath;
                }
                else m.Id = mi.szDevice;
                list.Add(m);
                return true;
            }, IntPtr.Zero);
            return list.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Bounds.Left).ToList();
        }
    }

    public static class MonitorSelector
    {
        /// <summary>
        /// Picks the TV monitor. Auto => primary. Otherwise the preferred id (device path), then a monitor with
        /// the same GDI name, then the primary. Returns null only when there are no monitors.
        /// </summary>
        public static MonitorInfo Select(IList<MonitorInfo> monitors, bool auto, string preferredId, out string reason)
        {
            reason = "";
            if (monitors == null || monitors.Count == 0) { reason = "no monitors reported"; return null; }
            var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
            if (auto || string.IsNullOrEmpty(preferredId)) { reason = "automatic: primary monitor"; return primary; }
            var exact = monitors.FirstOrDefault(m => string.Equals(m.Id, preferredId, StringComparison.OrdinalIgnoreCase));
            if (exact != null) { reason = "preferred monitor"; return exact; }
            var byGdi = monitors.FirstOrDefault(m => string.Equals(m.GdiName, preferredId, StringComparison.OrdinalIgnoreCase));
            if (byGdi != null) { reason = "preferred monitor (matched by display name)"; return byGdi; }
            reason = "preferred monitor is not connected; using the primary monitor";
            return primary;
        }

        /// <summary>A normal (windowed) placement for the emulator centred on a monitor's work area, 16:9, ~60% of the width.</summary>
        public static RECT NormalPlacement(MonitorInfo m, double aspect = 16.0 / 9.0)
        {
            var wa = m.WorkArea.IsEmpty ? m.Bounds : m.WorkArea;
            int w = (int)(wa.Width * 0.6), h = (int)(w / aspect);
            if (h > wa.Height * 0.8) { h = (int)(wa.Height * 0.8); w = (int)(h * aspect); }
            int x = wa.Left + (wa.Width - w) / 2, y = wa.Top + (wa.Height - h) / 2;
            return new RECT(x, y, x + w, y + h);
        }
    }
}
