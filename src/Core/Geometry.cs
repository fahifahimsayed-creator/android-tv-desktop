using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace AndroidTVDesktop.Core
{
    /// <summary>
    /// Where the emulator paints its picture inside its own window, as fractions of the window rect:
    /// picture = (X + A*W, Y + B*H) .. (X + C*W, Y + D*H).
    /// Measured on emulator 37.2 the Qt window paints the device picture inset (~W/32 left, ~H/28 top) with a
    /// transparent margin, so a window the size of the monitor still shows the desktop around the picture.
    /// </summary>
    public struct ContentModel
    {
        public double A, B, C, D;
        public ContentModel(double a, double b, double c, double d) { A = a; B = b; C = c; D = d; }
        /// <summary>Measured on this project's reference PC; used only when on-screen measurement fails.</summary>
        public static readonly ContentModel Fallback = new ContentModel(0.03125, 0.0361, 0.9672, 1.0);

        public static bool TryFrom(RECT window, RECT painted, out ContentModel m)
        {
            m = Fallback;
            if (window.Width < 50 || window.Height < 50 || painted.Width < 25 || painted.Height < 25) return false;
            m = new ContentModel(
                (painted.Left - window.Left) / (double)window.Width, (painted.Top - window.Top) / (double)window.Height,
                (painted.Right - window.Left) / (double)window.Width, (painted.Bottom - window.Top) / (double)window.Height);
            return m.A >= 0 && m.B >= 0 && m.C <= 1.0001 && m.D <= 1.0001 && m.C - m.A > 0.5 && m.D - m.B > 0.5;
        }

        public RECT Painted(RECT w) => new RECT(
            (int)Math.Round(w.Left + A * w.Width), (int)Math.Round(w.Top + B * w.Height),
            (int)Math.Round(w.Left + C * w.Width), (int)Math.Round(w.Top + D * w.Height));

        public override string ToString() =>
            string.Format(CultureInfo.InvariantCulture, "A={0:0.0000} B={1:0.0000} C={2:0.0000} D={3:0.0000}", A, B, C, D);
    }

    public static class Geometry
    {
        /// <summary>
        /// Window rect (keeping the emulator's aspect ratio) whose painted picture covers the monitor by at least
        /// <paramref name="pad"/> pixels on every side, centred on the monitor. Works for any monitor position/size.
        /// </summary>
        public static RECT Overscan(RECT monitor, ContentModel m, double aspect, int pad, double extraScale)
        {
            double cw = m.C - m.A, ch = m.D - m.B;
            double needW = (monitor.Width + 2.0 * pad) / cw;
            double needH = (monitor.Height + 2.0 * pad) / ch;
            double w = Math.Max(needW, needH * aspect) * extraScale;
            double h = w / aspect;
            double x = monitor.Left + (monitor.Width - cw * w) / 2.0 - m.A * w;
            double y = monitor.Top + (monitor.Height - ch * h) / 2.0 - m.B * h;
            int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
            return new RECT(ix, iy, ix + (int)Math.Ceiling(w), iy + (int)Math.Ceiling(h));
        }

        /// <summary>Monitor rect expressed in window-relative coordinates (for SetWindowRgn).</summary>
        public static RECT ClipFor(RECT monitor, RECT window) =>
            new RECT(monitor.Left - window.Left, monitor.Top - window.Top, monitor.Right - window.Left, monitor.Bottom - window.Top);

        /// <summary>Converts physical pixels on a monitor to WPF device-independent units (96 DPI = 1.0).</summary>
        public static (double x, double y, double w, double h) ToDips(RECT r, double scale) =>
            (r.Left / scale, r.Top / scale, r.Width / scale, r.Height / scale);

        /// <summary>Guest (Android) display size for a requested mode; always 16:9, never larger than the AVD panel.</summary>
        public static (int w, int h) GuestSize(GuestResolution mode, MonitorInfo monitor, int avdWidth, int avdHeight)
        {
            (int, int) target;
            switch (mode)
            {
                case GuestResolution.R1920x1080: target = (1920, 1080); break;
                case GuestResolution.R2560x1440: target = (2560, 1440); break;
                case GuestResolution.R3840x2160: target = (3840, 2160); break;
                case GuestResolution.MatchMonitor:
                    if (monitor == null) return (avdWidth, avdHeight);
                    int mw = monitor.Bounds.Width, mh = monitor.Bounds.Height;
                    // Use the monitor's 16:9 fit (e.g. 2560x1440, 1920x1080; 1920x1200 -> 1920x1080).
                    int h = Math.Min(mh, (int)Math.Round(mw * 9.0 / 16.0));
                    target = ((int)Math.Round(h * 16.0 / 9.0), h);
                    break;
                default: return (avdWidth, avdHeight);
            }
            if (avdWidth > 0 && avdHeight > 0 && (target.Item1 > avdWidth || target.Item2 > avdHeight)) return (avdWidth, avdHeight);
            // Even dimensions only (Android requirement for some codecs/surfaces).
            return (target.Item1 & ~1, target.Item2 & ~1);
        }

        /// <summary>Density that keeps the same UI size when the panel resolution changes (4K@640 -> 1080p@320).</summary>
        public static int GuestDensity(int requested, int avdDensity, int avdHeight, int guestHeight)
        {
            if (requested > 0) return requested;
            if (avdDensity <= 0 || avdHeight <= 0 || guestHeight <= 0) return 0;
            return (int)Math.Round(avdDensity * (guestHeight / (double)avdHeight));
        }
    }

    public sealed class WindowInfo
    {
        public IntPtr Hwnd; public uint Pid; public string Title = ""; public string Class = "";
        public bool Visible; public IntPtr Owner; public RECT Rect; public string ProcessName = "";
    }

    public static class WindowFinder
    {
        public static string TitlePrefix(string avd) => "Android Emulator - " + avd + ":";

        /// <summary>
        /// The emulator's real main window: visible, unowned, title "Android Emulator - AVD:port", owned by a
        /// qemu-system-* process. qemu also creates ~60 hidden GL/IME helper windows and an owned toolbar,
        /// which must never be chosen (the old launcher used Process.MainWindowHandle, which can pick them).
        /// </summary>
        public static WindowInfo PickMain(IEnumerable<WindowInfo> windows, string avd)
        {
            string prefix = TitlePrefix(avd);
            return windows
                .Where(w => w.Visible && w.Owner == IntPtr.Zero && w.Title.StartsWith(prefix, StringComparison.Ordinal) &&
                            w.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(w => w.Rect.Area)
                .FirstOrDefault();
        }

        /// <summary>Console port from the window title ("...:5554") -> adb serial "emulator-5554".</summary>
        public static string AdbSerial(string title)
        {
            int i = title?.LastIndexOf(':') ?? -1;
            if (i < 0) return null;
            return int.TryParse(title.Substring(i + 1).Trim(), out int port) && port > 0 ? "emulator-" + port : null;
        }

        public static List<WindowInfo> EnumerateTopLevel()
        {
            var list = new List<WindowInfo>();
            var names = new Dictionary<uint, string>();
            Native.EnumWindows((h, l) =>
            {
                uint pid = Native.Pid(h);
                if (!names.TryGetValue(pid, out var pn))
                {
                    try { pn = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch { pn = ""; }
                    names[pid] = pn;
                }
                list.Add(new WindowInfo
                {
                    Hwnd = h, Pid = pid, Title = Native.Text(h), Class = Native.Class(h), Visible = Native.IsWindowVisible(h),
                    Owner = Native.Owner(h), Rect = Native.Rect(h), ProcessName = pn
                });
                return true;
            }, IntPtr.Zero);
            return list;
        }
    }
}
