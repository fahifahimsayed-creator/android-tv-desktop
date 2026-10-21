using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace AndroidTVDesktop.Core
{
    /// <summary>
    /// "Netflix (PC)": Netflix does not support the emulator, so it is played by Microsoft Edge on Windows instead
    /// (Edge supports Netflix's DRM, up to 1080p/4K depending on plan and hardware). Opens netflix.com as an Edge app
    /// window (no tabs/address bar), moves it onto the TV monitor and switches it to Edge's own fullscreen (F11).
    /// A running Edge ignores start-up flags such as --start-fullscreen, so placement is done with Win32 afterwards
    /// and the result is measured. The user's normal Edge profile is used, so an existing Netflix sign-in is kept.
    /// </summary>
    public static class NetflixLauncher
    {
        public const string Url = "https://www.netflix.com/browse";

        public static string FindEdge()
        {
            var candidates = new List<string>();
            try
            {
                using (var k = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe"))
                    if (k?.GetValue(null) is string p) candidates.Add(p);
            }
            catch { }
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe"));
            candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Microsoft\Edge\Application\msedge.exe"));
            return candidates.FirstOrDefault(File.Exists);
        }

        /// <summary>Command-line arguments for the Edge app window (pure; unit tested).</summary>
        public static string Arguments(RECT monitor) =>
            $"--app={Url} --new-window --window-position={monitor.Left},{monitor.Top} --window-size={Math.Max(800, monitor.Width / 2)},{Math.Max(450, monitor.Height / 2)}";

        /// <summary>True when an Edge window is a candidate we just opened (visible top-level browser frame, not seen before).</summary>
        public static bool IsNewEdgeWindow(WindowInfo w, ISet<IntPtr> before) =>
            w.Visible && w.Owner == IntPtr.Zero && !before.Contains(w.Hwnd) &&
            w.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) && w.Class == "Chrome_WidgetWin_1" &&
            w.Rect.Width > 300 && w.Rect.Height > 200;

        public static async Task<string> LaunchAsync(MonitorInfo monitor)
        {
            string edge = FindEdge();
            if (edge == null) return "Microsoft Edge was not found. Install or repair Edge, then try again.";
            if (monitor == null) return "No monitor available.";
            var mon = monitor.Bounds;
            var all = WindowFinder.EnumerateTopLevel();
            // Reuse a Netflix window that is already open (e.g. minimised by the phone remote's TV button).
            WindowInfo win = all.FirstOrDefault(PcControl.IsNetflixWindow) ?? all.FirstOrDefault(w => w.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) && w.Title.IndexOf("netflix", StringComparison.OrdinalIgnoreCase) >= 0 && Native.IsIconic(w.Hwnd));
            if (win != null)
            {
                Log.Info($"Netflix (PC): reusing open window {Native.Hex(win.Hwnd)}");
                if (Native.IsIconic(win.Hwnd)) { Native.ShowWindow(win.Hwnd, Native.SW_RESTORE); await Task.Delay(400); }
                win.Rect = Native.Rect(win.Hwnd);
            }
            else
            {
                var before = new HashSet<IntPtr>(all.Where(w => w.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase)).Select(w => w.Hwnd));
                Log.Info($"Netflix (PC): starting \"{edge}\" {Arguments(mon)} for monitor {monitor.GdiName} {mon}");
                try { Process.Start(new ProcessStartInfo(edge, Arguments(mon)) { UseShellExecute = false }); }
                catch (Exception ex) { Log.Error("Netflix (PC): Edge did not start", ex); return "Edge could not be started: " + ex.Message; }
                var sw = Stopwatch.StartNew();
                while (sw.Elapsed.TotalSeconds < 20 && win == null)
                {
                    await Task.Delay(400);
                    win = WindowFinder.EnumerateTopLevel().FirstOrDefault(w => IsNewEdgeWindow(w, before));
                }
            }
            if (win == null) { Log.Warn("Netflix (PC): no new Edge window appeared within 20 s."); return "Edge started, but its Netflix window did not appear. Check Edge, then try again."; }
            Log.Info($"Netflix (PC): Edge window {Native.Hex(win.Hwnd)} '{win.Title}' at {win.Rect}");

            // Onto the TV monitor (normal size first so Edge adopts that monitor's DPI), then foreground + F11.
            if (!Native.Rect(win.Hwnd).Near(mon, 1))
            {
                var np = MonitorSelector.NormalPlacement(monitor);
                Native.ShowWindow(win.Hwnd, Native.SW_RESTORE);
                Native.SetWindowPos(win.Hwnd, Native.HWND_NOTOPMOST, np.Left, np.Top, np.Width, np.Height, Native.SWP_SHOWWINDOW);
                await Task.Delay(500);
            }
            bool fg = ForceForeground(win.Hwnd);
            await Task.Delay(300);
            // F11 toggles, so never press it twice in a row without first waiting long enough to see the first one work
            // (Edge can take a few seconds while Netflix loads).
            for (int attempt = 1; attempt <= 2 && !Native.Rect(win.Hwnd).Near(mon, 1); attempt++)
            {
                if (Native.GetForegroundWindow() != win.Hwnd) ForceForeground(win.Hwnd);
                await Task.Delay(300);
                PcControl.PressF11();
                for (int i = 0; i < 25 && !Native.Rect(win.Hwnd).Near(mon, 1); i++) await Task.Delay(200);   // up to 5 s
            }
            var r = Native.Rect(win.Hwnd);
            bool full = r.Near(mon, 1);
            Log.Info($"Netflix (PC): window now {r}, monitor {mon}, fullscreen={full}, foreground={(Native.GetForegroundWindow() == win.Hwnd)}");
            return full ? "Netflix is open fullscreen in Microsoft Edge. Press F11 in Edge to leave fullscreen."
                        : "Netflix is open in Microsoft Edge on the TV monitor. Press F11 in Edge for fullscreen.";
        }

        static bool ForceForeground(IntPtr h)
        {
            var fgw = Native.GetForegroundWindow();
            uint t = Native.GetWindowThreadProcessId(fgw, out _), me = Native.GetCurrentThreadId();
            bool attached = t != 0 && t != me && Native.AttachThreadInput(me, t, true);
            Native.BringWindowToTop(h);
            bool ok = Native.SetForegroundWindow(h);
            if (attached) Native.AttachThreadInput(me, t, false);
            return ok;
        }

    }
}
