using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace AndroidTVDesktop.Core
{
    /// <summary>Result of an on-screen coverage check (pixels + z-order hit tests).</summary>
    public sealed class Coverage
    {
        public bool PixelsOk, ZOrderOk, TaskbarAbove;
        public double UncoveredFraction;
        public string WindowsAbove = "";
        public bool Ok => PixelsOk && ZOrderOk;
        public override string ToString() =>
            $"uncoveredEdgePixels={UncoveredFraction:0.000%} pixelsOk={PixelsOk} zOrderOk={ZOrderOk}" + (WindowsAbove.Length > 0 ? $" windowsAbove=[{WindowsAbove}]" : "");
    }

    /// <summary>Black, borderless, non-activating, topmost window directly under the emulator so the desktop can never show through.</summary>
    sealed class Backdrop : Forms.Form
    {
        /// <summary>Almost black, but a value Android UI never paints exactly; screen pixels showing it mean "not covered by the TV picture".</summary>
        public static readonly Color Key = Color.FromArgb(3, 1, 4);
        public Backdrop()
        {
            FormBorderStyle = Forms.FormBorderStyle.None; ShowInTaskbar = false; BackColor = Key;
            StartPosition = Forms.FormStartPosition.Manual; Text = "Android TV Desktop backdrop";
        }
        protected override bool ShowWithoutActivation => true;
        protected override Forms.CreateParams CreateParams
        {
            get { var cp = base.CreateParams; cp.ExStyle |= (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST); return cp; }
        }
    }

    /// <summary>
    /// Launches/reuses the emulator and runs TV mode: borderless fullscreen on the selected monitor, verified on screen,
    /// keyboard/mouse mapping, monitor-change handling and guaranteed restoration. Runs on the UI dispatcher thread.
    /// </summary>
    public sealed class TvEngine : IDisposable
    {
        sealed class Saved
        {
            public IntPtr Hwnd; public uint Pid; public RECT Rect; public long Style, ExStyle, NewStyle, NewExStyle;
            public bool WasTopmost, Clipped; public List<IntPtr> HiddenTools = new List<IntPtr>();
        }

        readonly Dispatcher ui;
        AppSettings s;
        readonly KeyFilter keys;
        readonly DispatcherTimer timer;
        readonly Backdrop backdrop;
        readonly IntPtr backdropHwnd;
        readonly string statePath = RestoreRecord.DefaultPath;
        Native.HookProc kbProc, msProc;
        IntPtr kbHook = IntPtr.Zero, msHook = IntPtr.Zero;

        public TvStateMachine Machine { get; } = new TvStateMachine();
        public event Action StatusChanged;

        public SdkInfo Sdk { get; private set; }
        public AvdInfo Avd { get; private set; }
        public AdbClient Adb { get; private set; }
        public IntPtr Target { get; private set; }
        public uint TargetPid { get; private set; }
        public MonitorInfo Monitor { get; private set; }
        public string MonitorReason { get; private set; } = "";
        public string LastCoverage { get; private set; } = "";
        public string LastError { get; private set; } = "";
        public bool BootCompleted { get; private set; }
        public bool AdbReady { get; private set; }
        public double? StartupSeconds { get; private set; }
        public string GuestDisplayStatus { get; private set; } = "Unknown (emulator not running)";
        public bool IsFullscreen => Machine.IsFullscreen;

        Saved saved;
        RECT placed;
        bool taskbarHidden, cursorClipped, busy, disposed;
        TvAction pending = TvAction.None;
        int enterFailures, clipReapplied;
        readonly List<DateTime> drifts = new List<DateTime>();
        DateTime lastTransition = DateTime.MinValue, lastZCheck = DateTime.MinValue, displayChangedAt = DateTime.MinValue;
        Process launched;
        DateTime launchedAt;

        public TvEngine(AppSettings settings, Dispatcher dispatcher)
        {
            ui = dispatcher; s = settings.Clone();
            keys = new KeyFilter(s);
            backdrop = new Backdrop();
            backdropHwnd = backdrop.Handle;
            backdrop.MouseDown += (o, e) => { if (Target != IntPtr.Zero) ForceForeground(Target); };
            timer = new DispatcherTimer(DispatcherPriority.Background, ui) { Interval = TimeSpan.FromMilliseconds(500) };
            timer.Tick += async (o, e) => await TickAsync();
            Machine.Changed += (from, to, why) => OnStateChanged(from, to);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
            Refresh();
        }

        public AppSettings Settings => s.Clone();

        /// <summary>Re-reads SDK/AVD info and the current emulator window (cheap; no polling when idle).</summary>
        public void Refresh()
        {
            Sdk = SdkInfo.Locate(s.SdkPath);
            Avd = AvdInfo.Read(s.AvdName);
            var w = WindowFinder.PickMain(WindowFinder.EnumerateTopLevel(), s.AvdName);
            if (!Machine.IsTvModeActive)
            {
                // Outside TV mode just mirror what is on screen (window may have been closed/reopened by the user).
                Target = w?.Hwnd ?? IntPtr.Zero; TargetPid = w?.Pid ?? 0;
            }
            string serial = w != null ? WindowFinder.AdbSerial(w.Title) : null;
            if (Adb == null || (serial != null && Adb.Serial != serial))
            {
                Adb?.Dispose();
                Adb = new AdbClient(Sdk.AdbExe, serial);
            }
            SelectMonitor();
            Raise();
        }

        public void ApplySettings(AppSettings settings)
        {
            var old = s;
            s = settings.Clone();
            keys.Configure(s);
            Log.Info("Settings applied to engine.");
            if (Machine.IsFullscreen && (old.AutoMonitor != s.AutoMonitor || old.PreferredMonitorId != s.PreferredMonitorId))
                _ = ui.InvokeAsync(() => ReEnterAsync("monitor selection changed"));
            if (Machine.IsTvModeActive) UpdateMouseHook();
            SelectMonitor();
            Raise();
        }

        void SelectMonitor()
        {
            var list = MonitorService.Enumerate();
            Monitor = MonitorSelector.Select(list, s.AutoMonitor, s.PreferredMonitorId, out var why);
            MonitorReason = why;
        }

        void Raise() { try { StatusChanged?.Invoke(); } catch (Exception ex) { Log.Error("StatusChanged handler", ex); } }

        // ================================================================ TV mode lifecycle
        public async Task LaunchTvAsync(string reason)
        {
            if (Machine.IsTvModeActive)
            {
                Log.Info("Launch TV requested while TV mode is active (" + Machine.State + "); entering fullscreen.");
                if (!Machine.IsFullscreen && Target != IntPtr.Zero) await EnterFullscreenAsync("Launch TV pressed again");
                return;
            }
            LastError = ""; enterFailures = 0; BootCompleted = false; StartupSeconds = null;
            Machine.TryMove(TvState.Starting, reason);
            var sw = Stopwatch.StartNew();
            try
            {
                Refresh();
                if (!Avd.Found) { Fail($"AVD '{s.AvdName}' was not found (no config.ini). Check the name in Android Studio > Device Manager."); return; }
                if (Target == IntPtr.Zero)
                {
                    var procs = EmulatorProcesses.ForAvd(s.AvdName);
                    if (procs.Count > 0) Log.Info($"Emulator for {s.AvdName} already running (PIDs {string.Join(",", procs.Select(p => p.Pid))}); waiting for its window, not starting another.");
                    else
                    {
                        if (!Sdk.EmulatorFound) { Fail($"emulator.exe not found at {Sdk.EmulatorExe}. Set the SDK path on the System page."); return; }
                        launched = EmulatorProcesses.Launch(Sdk, Avd, s.LaunchFrameless);
                        launchedAt = DateTime.Now;
                        Log.Info("emulator.exe PID " + launched?.Id);
                    }
                    if (!await WaitForWindowAsync(sw)) return;
                }
                else Log.Info("Reusing running emulator window " + Native.Hex(Target));
                CheckFrameless();

                Machine.TryMove(TvState.WaitingForBoot, "emulator window ready");
                await WaitForBootAsync();
                if (!Machine.IsTvModeActive) return;
                StartupSeconds = sw.Elapsed.TotalSeconds;
                Log.Info($"Startup to Android ready: {StartupSeconds:0.0} s (boot completed={BootCompleted}).");
                await ApplyGuestDisplayAsync();
                InstallKeyboardHook();
                timer.Start();
                if (s.StartInFullscreen && s.BorderlessTvMode) await EnterFullscreenAsync("TV mode start");
                else Machine.TryMove(TvState.Windowed, "start in window (settings)");
            }
            catch (Exception ex)
            {
                Log.Error("Launch TV failed", ex);
                EmergencyRestore("launch error");
                Fail("Unexpected error while starting TV mode: " + ex.Message);
            }
        }

        async Task<bool> WaitForWindowAsync(Stopwatch sw)
        {
            var policy = new StartupPolicy(s.StartupTimeoutSeconds, s.BootTimeoutSeconds);
            DateTime lastLog = DateTime.MinValue;
            while (Machine.State == TvState.Starting)
            {
                var w = WindowFinder.PickMain(WindowFinder.EnumerateTopLevel(), s.AvdName);
                if (w != null)
                {
                    Target = w.Hwnd; TargetPid = w.Pid;
                    Adb?.Dispose(); Adb = new AdbClient(Sdk.AdbExe, WindowFinder.AdbSerial(w.Title));
                    Log.Info($"Emulator window: title='{w.Title}' hwnd={Native.Hex(w.Hwnd)} class={w.Class} PID={w.Pid} process={w.ProcessName} rect={w.Rect} adb={Adb.Serial}");
                    // Let Qt finish its initial resize/show before touching the window.
                    RECT r0 = Native.Rect(Target);
                    for (int i = 0; i < 6; i++) { await Task.Delay(500); var r = Native.Rect(Target); if (r.Near(r0, 0)) break; r0 = r; }
                    Raise();
                    return true;
                }
                bool alive = launched == null || !launched.HasExited || EmulatorProcesses.ForAvd(s.AvdName).Count > 0;
                var v = policy.CheckWindow(sw.Elapsed, alive, launched != null, DateTime.Now - launchedAt);
                if (v == StartupPolicy.Verdict.EmulatorExited)
                { Fail($"The emulator exited during startup (exit code {SafeExitCode()}). Check the AVD in Android Studio > Device Manager."); return false; }
                if (v == StartupPolicy.Verdict.WindowTimedOut)
                {
                    var stuck = EmulatorProcesses.ForAvd(s.AvdName).Any(p => p.Name.StartsWith("qemu", StringComparison.OrdinalIgnoreCase));
                    Fail($"The emulator window did not appear within {s.StartupTimeoutSeconds} s." +
                         (stuck ? " The emulator process is running but never finished starting (often too little free RAM). Close it from the System page or Task Manager, close other programs, and try again." : ""));
                    return false;
                }
                if ((DateTime.Now - lastLog).TotalSeconds >= 15) { Log.Info($"Waiting for emulator window ({sw.Elapsed.TotalSeconds:0} s)."); lastLog = DateTime.Now; }
                await Task.Delay(700);
            }
            return false;
        }

        string SafeExitCode() { try { return launched?.ExitCode.ToString() ?? "?"; } catch { return "?"; } }

        async Task WaitForBootAsync()
        {
            AdbReady = false;
            if (Adb == null || !Adb.Available) { Log.Warn("adb.exe not found; skipping the Android boot check (Back/Home keys need ADB)."); return; }
            var policy = new StartupPolicy(s.StartupTimeoutSeconds, s.BootTimeoutSeconds);
            var sw = Stopwatch.StartNew();
            DateTime lastLog = DateTime.MinValue;
            while (Machine.State == TvState.WaitingForBoot)
            {
                if (await Adb.IsBootCompletedAsync()) { BootCompleted = true; AdbReady = true; Log.Info("Android reports sys.boot_completed=1."); Raise(); return; }
                if (policy.CheckBoot(sw.Elapsed) == StartupPolicy.Verdict.BootTimedOut)
                { Log.Warn($"Android did not report boot completion within {s.BootTimeoutSeconds} s; continuing (ADB features may not work)."); return; }
                if (!Native.IsWindow(Target)) { Log.Warn("Emulator window closed while waiting for boot."); return; }
                if ((DateTime.Now - lastLog).TotalSeconds >= 15) { Log.Info($"Waiting for Android to boot ({sw.Elapsed.TotalSeconds:0} s)."); lastLog = DateTime.Now; }
                await Task.Delay(1500);
            }
        }

        void Fail(string message)
        {
            LastError = message;
            Log.Error(message);
            if (Machine.State != TvState.Faulted) Machine.TryMove(TvState.Faulted, message);
            Raise();
        }

        bool frameMode;

        /// <summary>True when the running emulator shows the AVD's device frame: started without "-skin" and the AVD has showDeviceFrame=yes.</summary>
        bool IsFrameMode()
        {
            if (!Avd.ShowDeviceFrame) return false;
            try
            {
                var p = EmulatorProcesses.ForAvd(s.AvdName).FirstOrDefault(x => x.Pid == TargetPid);
                if (p != null) return p.CommandLine.IndexOf("-skin", StringComparison.OrdinalIgnoreCase) < 0;
            }
            catch { }
            return !s.LaunchFrameless;
        }

        /// <summary>Whether the running emulator matches the "show TV frame" setting (false = restart needed to apply it).</summary>
        public bool FrameSettingApplied()
        {
            if (Target == IntPtr.Zero || !Avd.ShowDeviceFrame) return true;
            var p = EmulatorProcesses.ForAvd(s.AvdName).FirstOrDefault(x => x.Pid == TargetPid);
            if (p == null) return true;
            bool runningWithFrame = p.CommandLine.IndexOf("-skin", StringComparison.OrdinalIgnoreCase) < 0;
            return runningWithFrame == !s.LaunchFrameless;
        }

        void CheckFrameless()
        {
            try
            {
                var p = EmulatorProcesses.ForAvd(s.AvdName).FirstOrDefault(x => x.Pid == TargetPid);
                if (p != null && p.CommandLine.IndexOf("-skin", StringComparison.OrdinalIgnoreCase) < 0 && Avd.ShowDeviceFrame)
                    Log.Warn("This emulator was started without the frameless skin and the AVD shows a device frame; the TV bezel may be visible. Restart the emulator from this app to remove it.");
            }
            catch { }
        }

        /// <summary>Leaves TV mode: restores the window, releases hooks, stops monitoring. The emulator keeps running.</summary>
        public async Task ExitTvAsync(string reason)
        {
            if (!Machine.IsTvModeActive) { if (Machine.State == TvState.Faulted) Machine.TryMove(TvState.Idle, reason); return; }
            Log.Info("Exit TV mode: " + reason);
            if (Machine.IsFullscreen) await ExitFullscreenAsync(reason);
            timer.Stop();
            UninstallHooks();
            ReleaseCursor();
            if (!Machine.IsFullscreen) saved = null;   // window already restored; the restore record must go
            if (Machine.State == TvState.Starting || Machine.State == TvState.WaitingForBoot || Machine.State == TvState.Windowed || Machine.State == TvState.Exiting)
                Machine.TryMove(TvState.Idle, reason);
            WriteState();
            Raise();
        }

        public async Task ToggleFullscreenAsync()
        {
            if (Machine.IsFullscreen) await ExitFullscreenAsync("toggle");
            else if (Machine.State == TvState.Windowed) await EnterFullscreenAsync("toggle");
            else if (!Machine.IsTvModeActive) await LaunchTvAsync("toggle while idle");
        }

        // ================================================================ fullscreen
        public async Task EnterFullscreenAsync(string reason)
        {
            if (busy || Target == IntPtr.Zero || !Native.IsWindow(Target)) { if (Target == IntPtr.Zero) Log.Warn("Enter fullscreen ignored: no emulator window."); return; }
            if (!Machine.TryMove(TvState.Entering, reason)) return;
            busy = true; lastTransition = DateTime.Now;
            try
            {
                SelectMonitor();
                if (Monitor == null) throw new InvalidOperationException("No monitor available.");
                var mon = Monitor.Bounds;
                if (Native.IsIconic(Target) || Native.IsZoomed(Target)) { Log.Info("Window minimized/maximized; SW_RESTORE=" + Native.ShowWindow(Target, Native.SW_RESTORE)); await Task.Delay(600); }

                RECT cur = Native.Rect(Target);
                Native.GetClientRect(Target, out var client);
                int hr = Native.DwmGetWindowAttribute(Target, 9, out var frame, Marshal.SizeOf(typeof(RECT)));
                long style = Native.GetLong(Target, Native.GWL_STYLE), ex = Native.GetLong(Target, Native.GWL_EXSTYLE);
                Log.Info($"Before fullscreen: hwnd={Native.Hex(Target)} PID={TargetPid} outer={cur} client={client} dwmFrame={frame} (hr=0x{hr:X}) style=0x{style:X8} exstyle=0x{ex:X8}");
                Log.Info($"Target monitor ({MonitorReason}): {Monitor}");

                // Normal rect to restore later. If the window is already covering a monitor (e.g. after a crash), use the remembered one.
                RECT normal = cur;
                if (cur.Width >= mon.Width && cur.Height >= mon.Height && AppSettings.TryParseRect(s.LastNormalWindow, out var remembered)) normal = remembered;
                saved = new Saved { Hwnd = Target, Pid = TargetPid, Rect = normal, Style = style, ExStyle = ex, WasTopmost = (ex & Native.WS_EX_TOPMOST) != 0 };
                if (s.RememberWindowState && !(cur.Width >= mon.Width && cur.Height >= mon.Height)) s.LastNormalWindow = $"{cur.Left},{cur.Top},{cur.Width},{cur.Height}";

                // 1. Native frame (the emulator's Qt window is normally already WS_POPUP).
                long ns = style & ~(Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_SYSMENU | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX);
                long nx = ex & ~(Native.WS_EX_DLGMODALFRAME | Native.WS_EX_WINDOWEDGE | Native.WS_EX_CLIENTEDGE | Native.WS_EX_STATICEDGE);
                saved.NewStyle = ns; saved.NewExStyle = nx;
                if (ns != style) Log.Info($"SetWindowLongPtr(GWL_STYLE 0x{style:X8} -> 0x{ns:X8}) error={Native.SetLong(Target, Native.GWL_STYLE, ns)}");
                else Log.Info("Window already borderless; style unchanged.");
                if (nx != ex) Log.Info($"SetWindowLongPtr(GWL_EXSTYLE 0x{ex:X8} -> 0x{nx:X8}) error={Native.SetLong(Target, Native.GWL_EXSTYLE, nx)}");

                // 2. Toolbar (owned window, always above its owner).
                if (!s.ShowToolbarInTvMode) HideOwnedTools();
                WriteState();

                // 3. Move to the selected monitor first so Qt adopts that monitor's DPI before we measure.
                var wr = Native.Rect(Target);
                if (wr.Intersect(mon).Area < wr.Area / 2)
                {
                    var np = MonitorSelector.NormalPlacement(Monitor);
                    Native.SetWindowPos(Target, Native.HWND_NOTOPMOST, np.Left, np.Top, np.Width, np.Height, Native.SWP_SHOWWINDOW);
                    Log.Info($"Moved emulator onto {Monitor.GdiName} at {np} before fullscreen.");
                    await Task.Delay(900);
                }

                // 4. Backdrop + monitor-sized placement, then measure where the picture is painted.
                frameMode = IsFrameMode();
                if (frameMode) Log.Info("TV frame mode: the emulator shows the AVD's device frame (bezel); fitting the whole TV inside the monitor (letterboxed) instead of overscanning.");
                ShowBackdrop(mon);
                RECT actual = Place(mon, "initial monitor-sized placement");
                ForceForeground(Target);
                await Task.Delay(frameMode ? 1300 : 700);    // with a frame, Qt snaps the window back to the frame's aspect ratio (~0.5 s)
                actual = Native.Rect(Target);
                ContentModel model;
                if (MeasurePainted(actual, mon, out var painted) && ContentModel.TryFrom(actual, painted, out model))
                    Log.Info($"Measured painted picture {painted} in window {actual} -> {model}");
                else { model = ContentModel.Fallback; Log.Warn($"Could not measure the painted picture (got {painted}); using built-in model {model}."); }

                // 5. Overscan so the painted picture covers the monitor; verify on screen.
                double aspect = actual.Height > 0 ? actual.Width / (double)actual.Height : 16.0 / 9.0, scale = 1.0;
                Coverage cov = null;
                if (frameMode)
                {
                    // TV frame: fit the whole framed TV inside the monitor, centred; the black backdrop letterboxes it.
                    for (int attempt = 1; attempt <= 2; attempt++)
                    {
                        RECT want = Geometry.FitInside(mon, model, aspect, 0, attempt == 1 ? 1.0 : 0.97);
                        Place(want, "fit TV frame attempt " + attempt);
                        await Task.Delay(1300);
                        actual = Native.Rect(Target);
                        if (!actual.Near(want, 6)) Log.Info($"Emulator adjusted the window: requested {want}, actual {actual}");
                        var pic = model.Painted(actual);
                        cov = new Coverage { PixelsOk = true };   // black bars around the TV are intended in frame mode
                        HitTest(cov, mon);
                        Log.Info($"Frame fit {attempt}: window={actual} TV incl. frame={pic} {cov}");
                        if (cov.TaskbarAbove) { ForceForeground(Target); await Task.Delay(300); cov = new Coverage { PixelsOk = true }; HitTest(cov, mon); }
                        if (mon.Contains(pic) || attempt == 2) break;
                        if (actual.Height > 0) aspect = actual.Width / (double)actual.Height;
                    }
                }
                for (int attempt = 1; attempt <= 3 && !frameMode; attempt++)
                {
                    RECT want = Geometry.Overscan(mon, model, aspect, 2, scale);
                    Place(want, "overscan attempt " + attempt);
                    await Task.Delay(600);
                    actual = Native.Rect(Target);
                    if (!actual.Near(want, 6)) Log.Info($"Emulator adjusted the window: requested {want}, actual {actual}");
                    cov = VerifyCoverage(mon);
                    Log.Info($"Coverage check {attempt}: window={actual} expectedPicture={model.Painted(actual)} {cov}");
                    if (cov.TaskbarAbove)
                    {
                        ForceForeground(Target); await Task.Delay(300); cov = VerifyCoverage(mon);
                        if (cov.TaskbarAbove && attempt == 3) { HideTaskbar(); await Task.Delay(200); cov = VerifyCoverage(mon); }
                    }
                    if (cov.Ok) break;
                    if (!cov.PixelsOk) { scale *= 1.03; if (actual.Height > 0) aspect = actual.Width / (double)actual.Height; }
                }
                placed = Native.Rect(Target);
                clipReapplied = 0;
                await Task.Delay(500);                       // Qt applies its own window mask after a resize ...
                if (!frameMode && !EnsureClip(placed)) { await Task.Delay(300); EnsureClip(placed); }   // ... so our clip must be the final region
                drifts.Clear();
                ForceForeground(Target);
                ConfineCursor();
                if (s.ParkPointerOnEnter) Native.SetCursorPos(mon.Right - 2, mon.Bottom - 2);
                LastCoverage = cov?.ToString() ?? "";
                if (cov != null && cov.Ok && frameMode) { enterFailures = 0; Log.Info($"FULLSCREEN VERIFIED (TV frame, letterboxed) on {Monitor.GdiName}: window={placed} inside monitor {mon}; {cov}"); }
                else if (cov != null && cov.Ok) { enterFailures = 0; Log.Info($"FULLSCREEN VERIFIED on {Monitor.GdiName}: window={placed} covers monitor {mon}; {cov}"); }
                else { enterFailures++; Log.Warn($"Fullscreen active but NOT fully verified ({cov}). The black backdrop still hides the desktop."); }
                Machine.TryMove(TvState.Fullscreen, cov != null && cov.Ok ? "verified" : "unverified");
                WriteState();
                UpdateMouseHook();
            }
            catch (Exception exn)
            {
                enterFailures++;
                Log.Error("Entering fullscreen failed", exn);
                LastError = "Fullscreen failed: " + exn.Message;
                RestoreWindow("error while entering fullscreen");
                Machine.TryMove(TvState.Windowed, "fullscreen failed");
            }
            finally { busy = false; Raise(); RunPending(); }
        }

        public async Task ExitFullscreenAsync(string reason)
        {
            if (!Machine.IsFullscreen || busy) return;
            busy = true;
            try
            {
                Machine.TryMove(TvState.Exiting, reason);
                lastTransition = DateTime.Now;
                RestoreWindow(reason);
                await Task.Delay(400);
                ShowOwnedTools();
                saved = null;
                WriteState();   // nothing left to restore -> removes the restore record
                if (Target != IntPtr.Zero) ForceForeground(Target);
                Machine.TryMove(TvState.Windowed, reason);
            }
            finally { busy = false; UpdateMouseHook(); Raise(); RunPending(); }
        }

        async Task ReEnterAsync(string why)
        {
            if (!Machine.IsFullscreen) return;
            await ExitFullscreenAsync(why);
            await EnterFullscreenAsync(why);
        }

        RECT Place(RECT r, string what)
        {
            bool ok = Native.SetWindowPos(Target, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW | Native.SWP_NOOWNERZORDER);
            int err = Marshal.GetLastWin32Error();
            Log.Info($"SetWindowPos({what}, HWND_TOPMOST, {r}) returned {ok}" + (ok ? "" : $" error={err}"));
            KeepBackdropBelow();
            var actual = Native.Rect(Target);
            if (!frameMode) ClipToMonitor(actual, Monitor.Bounds);   // frame mode keeps the emulator's own (rounded) frame mask
            return actual;
        }

        void ClipToMonitor(RECT window, RECT mon)
        {
            var c = Geometry.ClipFor(mon, window);
            IntPtr rgn = Native.CreateRectRgn(c.Left, c.Top, c.Right, c.Bottom);
            if (Native.SetWindowRgn(Target, rgn, true) == 0) { Native.DeleteObject(rgn); Log.Warn("SetWindowRgn failed, error " + Marshal.GetLastWin32Error()); }
            else if (saved != null) saved.Clipped = true;
        }

        /// <summary>Qt replaces the window region with its own mask a few hundred ms after it handles a resize; put our monitor clip back.</summary>
        bool EnsureClip(RECT window)
        {
            int type = Native.GetWindowRgnBox(Target, out var box);
            var want = Geometry.ClipFor(Monitor.Bounds, window);
            if (type >= 2 && box.Near(want, 0)) return true;
            clipReapplied++;
            if (clipReapplied <= 3) Log.Info($"Window region is {(type < 2 ? "none" : box.ToString())} (emulator's own mask); re-applying monitor clip {want}.");
            ClipToMonitor(window, Monitor.Bounds);
            return false;
        }

        void RestoreWindow(string reason)
        {
            HideBackdrop();
            ReleaseCursor();
            if (saved != null && Native.IsWindow(saved.Hwnd) && Native.Pid(saved.Hwnd) == saved.Pid)
            {
                if (saved.NewStyle != saved.Style) Log.Info($"Restore GWL_STYLE 0x{saved.Style:X8} error={Native.SetLong(saved.Hwnd, Native.GWL_STYLE, saved.Style)}");
                if (saved.NewExStyle != saved.ExStyle) Log.Info($"Restore GWL_EXSTYLE 0x{saved.ExStyle:X8} error={Native.SetLong(saved.Hwnd, Native.GWL_EXSTYLE, saved.ExStyle)}");
                if (saved.Clipped) { Native.SetWindowRgn(saved.Hwnd, IntPtr.Zero, true); saved.Clipped = false; }
                var r = saved.Rect;
                bool ok = Native.SetWindowPos(saved.Hwnd, saved.WasTopmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
                var now = Native.Rect(saved.Hwnd);
                Log.Info($"Restored window ({reason}): SetWindowPos({r}) returned {ok}; now {now} topmost={Native.IsTopmost(saved.Hwnd)}" + (now.Near(r, 8) ? "" : " (DIFFERS from saved rect)"));
            }
            else Log.Info("Emulator window no longer valid; backdrop/taskbar restored only.");
            RestoreTaskbar();
            WriteState();
        }

        // ================================================================ monitoring
        async Task TickAsync()
        {
            if (busy || disposed) return;
            try
            {
                if (!Machine.IsTvModeActive) { timer.Stop(); return; }
                ValidateTarget();
                if (Target == IntPtr.Zero) { await ReacquireAsync(); return; }
                if (Machine.IsFullscreen)
                {
                    if (displayChangedAt != DateTime.MinValue && (DateTime.Now - displayChangedAt).TotalSeconds > 1.5)
                    {
                        displayChangedAt = DateTime.MinValue;
                        await ReEnterAsync("display configuration changed");
                        return;
                    }
                    Maintain();
                }
            }
            catch (Exception ex) { Log.Error("Monitoring tick failed", ex); }
        }

        void ValidateTarget()
        {
            if (Target == IntPtr.Zero) return;
            string why = null;
            if (!Native.IsWindow(Target)) why = "window destroyed";
            else if (Native.Pid(Target) != TargetPid) why = "handle now belongs to another process";
            else if (!Native.Text(Target).StartsWith(WindowFinder.TitlePrefix(s.AvdName), StringComparison.Ordinal)) why = "window title changed";
            if (why == null) return;
            Log.Warn($"Emulator window {Native.Hex(Target)} (PID {TargetPid}) lost: {why}. Releasing stale handle.");
            bool wasFull = Machine.IsFullscreen;
            HideBackdrop(); ReleaseCursor(); RestoreTaskbar();
            saved = null; Target = IntPtr.Zero; TargetPid = 0; BootCompleted = false; AdbReady = false;
            Machine.TryMove(TvState.Starting, "emulator window lost; waiting for it to return");
            reacquireSince = DateTime.Now; reacquireWantsFullscreen = wasFull || s.StartInFullscreen;
            UpdateMouseHook();
            WriteState();
            Raise();
        }

        DateTime reacquireSince; bool reacquireWantsFullscreen;
        async Task ReacquireAsync()
        {
            var w = WindowFinder.PickMain(WindowFinder.EnumerateTopLevel(), s.AvdName);
            if (w == null)
            {
                if ((DateTime.Now - reacquireSince).TotalSeconds > 600)
                { Log.Info("Emulator not reopened within 10 minutes; leaving TV mode."); timer.Stop(); UninstallHooks(); Machine.TryMove(TvState.Idle, "emulator closed"); Raise(); }
                return;
            }
            Target = w.Hwnd; TargetPid = w.Pid;
            Adb?.Dispose(); Adb = new AdbClient(Sdk.AdbExe, WindowFinder.AdbSerial(w.Title));
            Log.Info($"Re-acquired emulator window: hwnd={Native.Hex(w.Hwnd)} PID={w.Pid} rect={w.Rect}");
            busy = true;
            try { await Task.Delay(2500); } finally { busy = false; }
            Machine.TryMove(TvState.WaitingForBoot, "emulator window returned");
            await WaitForBootAsync();
            if (reacquireWantsFullscreen) await EnterFullscreenAsync("emulator reopened");
            else Machine.TryMove(TvState.Windowed, "emulator reopened");
        }

        void Maintain()
        {
            if (Native.IsIconic(Target)) { Log.Info("Emulator minimized while fullscreen; leaving fullscreen."); _ = ExitFullscreenAsync("emulator minimized"); return; }
            RECT cur = Native.Rect(Target);
            bool top = Native.IsTopmost(Target);
            if (!cur.Near(placed, 4) || !top)
            {
                drifts.Add(DateTime.Now); drifts.RemoveAll(d => (DateTime.Now - d).TotalSeconds > 60);
                Log.Warn($"Window moved/resized or lost topmost (now {cur}, topmost={top}, expected {placed}); re-applying ({drifts.Count} in last minute).");
                if (drifts.Count > 5) { _ = ExitFullscreenAsync("emulator keeps resetting its window"); return; }
                Place(placed, "re-apply after drift");
                placed = Native.Rect(Target);
                cur = placed;
            }
            if (!frameMode) EnsureClip(cur);
            if (!s.ShowToolbarInTvMode)
                foreach (var t in OwnedVisible(Target))
                {
                    Native.ShowWindow(t, Native.SW_HIDE);
                    if (saved != null && !saved.HiddenTools.Contains(t)) saved.HiddenTools.Add(t);
                    Log.Info("Emulator re-showed toolbar " + Native.Hex(t) + "; hidden again.");
                }
            // Keep the pointer confined only while the TV has focus (never trap it in another app).
            bool fg = IsEmulatorForeground();
            if (fg && s.ConfinePointerToTv && !cursorClipped) ConfineCursor();
            else if (!fg && cursorClipped) ReleaseCursor();
            if ((DateTime.Now - lastZCheck).TotalSeconds >= 5)
            {
                lastZCheck = DateTime.Now;
                var cov = new Coverage(); HitTest(cov, Monitor.Bounds);
                if (!cov.ZOrderOk) Log.Warn("Windows above the fullscreen TV: " + cov.WindowsAbove);
            }
        }

        void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Log.Info("Windows reported a display configuration change.");
            _ = ui.InvokeAsync(() =>
            {
                var before = Monitor?.ToString();
                SelectMonitor();
                Log.Info("Monitors now: " + string.Join(" | ", MonitorService.Enumerate().Select(m => m.Label)));
                if (Machine.IsFullscreen) displayChangedAt = DateTime.Now;   // debounce: re-enter after 1.5 s of quiet
                Raise();
            });
        }

        // ================================================================ verification
        Bitmap Capture(RECT mon)
        {
            var bmp = new Bitmap(mon.Width, mon.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(mon.Left, mon.Top, 0, 0, new Size(mon.Width, mon.Height));
            return bmp;
        }

        static int[] Pixels(Bitmap bmp)
        {
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var px = new int[bmp.Width * bmp.Height];
            for (int y = 0; y < bmp.Height; y++) Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), px, y * bmp.Width, bmp.Width);
            bmp.UnlockBits(data);
            return px;
        }

        public static bool IsKey(int argb)
        {
            int r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
            return Math.Abs(r - Backdrop.Key.R) <= 1 && Math.Abs(g - Backdrop.Key.G) <= 1 && Math.Abs(b - Backdrop.Key.B) <= 1;
        }

        bool MeasurePainted(RECT window, RECT mon, out RECT painted)
        {
            painted = new RECT();
            try
            {
                using (var bmp = Capture(mon))
                {
                    int[] px = Pixels(bmp); int W = bmp.Width, H = bmp.Height;
                    int x0 = Math.Max(0, window.Left - mon.Left), x1 = Math.Min(W, window.Right - mon.Left);
                    int y0 = Math.Max(0, window.Top - mon.Top), y1 = Math.Min(H, window.Bottom - mon.Top);
                    if (x1 - x0 < 50 || y1 - y0 < 50) return false;
                    int left = 0, right = W, top = 0, bottom = H;
                    foreach (double f in new[] { 0.3, 0.5, 0.7 })
                    {
                        int y = y0 + (int)((y1 - y0) * f);
                        int a = x0; while (a < x1 && IsKey(px[y * W + a])) a++;
                        int b = x1 - 1; while (b > a && IsKey(px[y * W + b])) b--;
                        if (a >= x1) return false;
                        left = Math.Max(left, a); right = Math.Min(right, b + 1);
                        int x = x0 + (int)((x1 - x0) * f);
                        int c = y0; while (c < y1 && IsKey(px[c * W + x])) c++;
                        int d = y1 - 1; while (d > c && IsKey(px[d * W + x])) d--;
                        if (c >= y1) return false;
                        top = Math.Max(top, c); bottom = Math.Min(bottom, d + 1);
                    }
                    painted = new RECT(left + mon.Left, top + mon.Top, right + mon.Left, bottom + mon.Top);
                    return true;
                }
            }
            catch (Exception ex) { Log.Warn("Screen capture for measurement failed: " + ex.Message); return false; }
        }

        Coverage VerifyCoverage(RECT mon)
        {
            var cov = new Coverage();
            try
            {
                using (var bmp = Capture(mon))
                {
                    int[] px = Pixels(bmp); int W = bmp.Width, H = bmp.Height, n = 0, key = 0;
                    for (int x = 0; x < W; x += 2) foreach (int y in new[] { 2, H - 3 }) { n++; if (IsKey(px[y * W + x])) key++; }
                    for (int y = 0; y < H; y += 2) foreach (int x in new[] { 2, W - 3 }) { n++; if (IsKey(px[y * W + x])) key++; }
                    for (int y = H / 10; y < H; y += H / 10) for (int x = W / 10; x < W; x += W / 10) { n++; if (IsKey(px[y * W + x])) key++; }
                    cov.UncoveredFraction = n == 0 ? 1 : key / (double)n;
                    cov.PixelsOk = cov.UncoveredFraction <= 0.002;
                }
            }
            catch (Exception ex) { Log.Warn("Screen capture for verification failed: " + ex.Message); cov.PixelsOk = false; cov.UncoveredFraction = 1; }
            HitTest(cov, mon);
            return cov;
        }

        void HitTest(Coverage cov, RECT mon)
        {
            var foreign = new List<string>();
            var pts = new List<Native.POINT>();
            foreach (double fy in new[] { 0.002, 0.25, 0.5, 0.75, 0.998 })
                foreach (double fx in new[] { 0.002, 0.25, 0.5, 0.75, 0.998 })
                    pts.Add(new Native.POINT(mon.Left + (int)(mon.Width * fx), mon.Top + (int)(mon.Height * fy)));
            foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr tray = IntPtr.Zero;
                while ((tray = Native.FindWindowEx(IntPtr.Zero, tray, cls, null)) != IntPtr.Zero)
                    if (Native.GetWindowRect(tray, out var tr) && !tr.IsEmpty) pts.Add(new Native.POINT(tr.Left + tr.Width / 2, tr.Top + tr.Height / 2));
            }
            foreach (var p in pts)
            {
                if (p.X < mon.Left || p.X >= mon.Right || p.Y < mon.Top || p.Y >= mon.Bottom) continue;
                IntPtr root = Native.GetAncestor(Native.WindowFromPoint(p), 2);
                if (root == IntPtr.Zero || root == Target || root == backdropHwnd || Native.Pid(root) == TargetPid) continue;
                string cls = Native.Class(root);
                if (cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") cov.TaskbarAbove = true;
                string pn = "?"; try { pn = Process.GetProcessById((int)Native.Pid(root)).ProcessName; } catch { }
                var d = $"{cls}/{pn}@{p.X},{p.Y}";
                if (!foreign.Contains(d)) foreign.Add(d);
            }
            cov.ZOrderOk = foreign.Count == 0;
            cov.WindowsAbove = string.Join("; ", foreign);
        }

        // ================================================================ helpers
        void ShowBackdrop(RECT r)
        {
            Native.ShowWindow(backdropHwnd, Native.SW_SHOWNOACTIVATE);
            bool ok = Native.SetWindowPos(backdropHwnd, Native.HWND_TOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);
            Log.Info($"Backdrop shown at {r} (SetWindowPos {ok})");
        }
        void KeepBackdropBelow()
        {
            if (Native.IsWindowVisible(backdropHwnd) && Target != IntPtr.Zero)
                Native.SetWindowPos(backdropHwnd, Target, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        }
        void HideBackdrop() { if (Native.IsWindowVisible(backdropHwnd)) { Native.ShowWindow(backdropHwnd, Native.SW_HIDE); Log.Info("Backdrop hidden."); } }

        static List<IntPtr> OwnedVisible(IntPtr owner)
        {
            var list = new List<IntPtr>();
            Native.EnumWindows((h, l) => { if (Native.IsWindowVisible(h) && Native.Owner(h) == owner) list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }
        void HideOwnedTools()
        {
            foreach (var t in OwnedVisible(Target)) { Native.ShowWindow(t, Native.SW_HIDE); saved.HiddenTools.Add(t); Log.Info($"Hid emulator toolbar {Native.Hex(t)} rect={Native.Rect(t)}"); }
        }
        void ShowOwnedTools()
        {
            if (saved == null) return;
            foreach (var t in saved.HiddenTools) if (Native.IsWindow(t)) Native.ShowWindow(t, Native.SW_SHOWNOACTIVATE);
            if (saved.HiddenTools.Count > 0) Log.Info($"Re-showed {saved.HiddenTools.Count} emulator toolbar window(s).");
            saved.HiddenTools.Clear();
        }

        void HideTaskbar()
        {
            var tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray == IntPtr.Zero || !Native.IsWindowVisible(tray)) return;
            Native.ShowWindow(tray, Native.SW_HIDE); taskbarHidden = true; WriteState();
            Log.Warn("The taskbar stayed above the TV after retries; hidden temporarily (restored on every exit path and by Restore-Desktop).");
        }
        void RestoreTaskbar()
        {
            if (!taskbarHidden) return;
            var tray = Native.FindWindow("Shell_TrayWnd", null);
            if (tray != IntPtr.Zero) Native.ShowWindow(tray, Native.SW_SHOW);
            taskbarHidden = false; Log.Info("Taskbar restored.");
        }

        void ConfineCursor()
        {
            if (!s.ConfinePointerToTv || Monitor == null) return;
            var r = Monitor.Bounds;
            cursorClipped = Native.ClipCursor(ref r);
        }
        void ReleaseCursor() { if (cursorClipped) { Native.ClipCursorNull(IntPtr.Zero); cursorClipped = false; } }

        bool IsEmulatorForeground()
        {
            var fg = Native.GetForegroundWindow();
            return TargetPid != 0 && (Native.Pid(fg) == TargetPid || fg == backdropHwnd);
        }

        static void ForceForeground(IntPtr h)
        {
            var fg = Native.GetForegroundWindow();
            if (fg == h) return;
            uint fgThread = Native.GetWindowThreadProcessId(fg, out _), me = Native.GetCurrentThreadId();
            bool attached = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
            Native.BringWindowToTop(h);
            bool ok = Native.SetForegroundWindow(h);
            if (attached) Native.AttachThreadInput(me, fgThread, false);
            Log.Info($"SetForegroundWindow({Native.Hex(h)}) returned {ok}; foreground now {Native.Hex(Native.GetForegroundWindow())}");
        }

        void WriteState()
        {
            try
            {
                if (saved == null && !Machine.IsFullscreen && !taskbarHidden) { if (File.Exists(statePath)) File.Delete(statePath); return; }
                var rec = new RestoreRecord
                {
                    ControllerPid = Process.GetCurrentProcess().Id, Fullscreen = Machine.IsFullscreen || Machine.State == TvState.Entering, TaskbarHidden = taskbarHidden
                };
                if (saved != null)
                {
                    rec.Hwnd = saved.Hwnd.ToInt64(); rec.Pid = saved.Pid; rec.Rect = saved.Rect; rec.Style = saved.Style; rec.ExStyle = saved.ExStyle;
                    rec.WasTopmost = saved.WasTopmost; rec.Clipped = saved.Clipped; rec.HiddenTools = saved.HiddenTools.Select(t => t.ToInt64()).ToList();
                }
                Directory.CreateDirectory(Path.GetDirectoryName(statePath));
                File.WriteAllText(statePath, rec.Serialize());
            }
            catch (Exception ex) { Log.Warn("Could not write restore record: " + ex.Message); }
        }

        void OnStateChanged(TvState from, TvState to) => Raise();

        // ================================================================ input hooks
        void InstallKeyboardHook()
        {
            if (kbHook != IntPtr.Zero) return;
            kbProc = KeyboardCallback;
            kbHook = Native.SetWindowsHookEx(13 /*WH_KEYBOARD_LL*/, kbProc, Native.GetModuleHandle(null), 0);
            if (kbHook == IntPtr.Zero) Log.Error("SetWindowsHookEx(WH_KEYBOARD_LL) failed, error " + Marshal.GetLastWin32Error());
            else Log.Info("Keyboard hook installed (TV mode active).");
        }

        void UpdateMouseHook()
        {
            bool want = Machine.IsFullscreen && s.MouseRightValue == MouseRightButton.AndroidBack;
            if (want && msHook == IntPtr.Zero)
            {
                msProc = MouseCallback;
                msHook = Native.SetWindowsHookEx(14 /*WH_MOUSE_LL*/, msProc, Native.GetModuleHandle(null), 0);
                Log.Info(msHook != IntPtr.Zero ? "Mouse hook installed (right-click = Android Back)." : "Mouse hook failed, error " + Marshal.GetLastWin32Error());
            }
            else if (!want && msHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(msHook); msHook = IntPtr.Zero; Log.Info("Mouse hook released."); }
        }

        void UninstallHooks()
        {
            if (kbHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(kbHook); kbHook = IntPtr.Zero; Log.Info("Keyboard hook released."); }
            if (msHook != IntPtr.Zero) { Native.UnhookWindowsHookEx(msHook); msHook = IntPtr.Zero; Log.Info("Mouse hook released."); }
        }

        IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    bool down = msg == 0x100 || msg == 0x104, up = msg == 0x101 || msg == 0x105;
                    if (down || up)
                    {
                        var k = (Native.KBDLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.KBDLLHOOKSTRUCT));
                        int vk = (int)k.vkCode;
                        if (vk < 0xA0 || vk > 0xA5) // ignore modifier keys themselves
                        {
                            bool ctrl = (Native.GetAsyncKeyState(0x11) & 0x8000) != 0, lalt = (Native.GetAsyncKeyState(0xA4) & 0x8000) != 0,
                                 shift = (Native.GetAsyncKeyState(0x10) & 0x8000) != 0;
                            var d = keys.Decide(vk, down, ctrl, lalt, shift, IsEmulatorForeground(), Machine.IsFullscreen, AdbReady && Adb != null);
                            if (d.Action != TvAction.None) { var a = d.Action; ui.BeginInvoke(new Action(() => OnAction(a, "keyboard"))); }
                            if (d.Swallow) return new IntPtr(1);
                        }
                    }
                }
            }
            catch { }
            return Native.CallNextHookEx(kbHook, nCode, wParam, lParam);
        }

        bool rightSwallowed;
        IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode >= 0)
                {
                    int msg = wParam.ToInt32();
                    if (msg == 0x204 /*WM_RBUTTONDOWN*/ && Machine.IsFullscreen && IsEmulatorForeground() && AdbReady)
                    {
                        var m = (Native.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(Native.MSLLHOOKSTRUCT));
                        if (Monitor != null && m.pt.X >= Monitor.Bounds.Left && m.pt.X < Monitor.Bounds.Right && m.pt.Y >= Monitor.Bounds.Top && m.pt.Y < Monitor.Bounds.Bottom)
                        {
                            rightSwallowed = true;
                            ui.BeginInvoke(new Action(() => OnAction(TvAction.AndroidBack, "right mouse button")));
                            return new IntPtr(1);
                        }
                    }
                    if (msg == 0x205 /*WM_RBUTTONUP*/ && rightSwallowed) { rightSwallowed = false; return new IntPtr(1); }
                }
            }
            catch { }
            return Native.CallNextHookEx(msHook, nCode, wParam, lParam);
        }

        /// <summary>Raised when the user presses the emergency-exit chord (the app decides whether to also close itself).</summary>
        public event Action EmergencyExitRequested;

        async void OnAction(TvAction a, string source)
        {
            if (a == TvAction.EmergencyExit)
            {
                Log.Info($"Emergency exit ({source}): leaving TV mode and restoring the desktop.");
                EmergencyRestore("emergency exit");
                await ExitTvAsync("emergency exit");
                EmergencyExitRequested?.Invoke();
                return;
            }
            if (a == TvAction.AndroidBack) { Log.Info($"Android Back ({source}) via ADB."); Adb?.SendKeyEvent("KEYCODE_BACK"); return; }
            if (a == TvAction.AndroidHome) { Log.Info($"Android Home ({source}) via ADB."); Adb?.SendKeyEvent("KEYCODE_HOME"); return; }
            if (busy) { pending = a; Log.Info($"{a} queued (transition in progress)."); return; }
            if (a == TvAction.ToggleFullscreen)
            {
                if ((DateTime.Now - lastTransition).TotalMilliseconds < 400) { Log.Info("Toggle ignored (debounce)."); return; }
                Log.Info($"Toggle fullscreen ({source}).");
                await ToggleFullscreenAsync();
            }
            else if (a == TvAction.ExitFullscreen) { Log.Info($"Exit fullscreen ({source})."); await ExitFullscreenAsync("exit key"); }
        }

        void RunPending()
        {
            if (pending == TvAction.None || busy) return;
            var a = pending; pending = TvAction.None;
            ui.BeginInvoke(new Action(() => OnAction(a, "queued")));
        }

        // ================================================================ guest display (ADB, reversible)
        static string OverrideMarker => Path.Combine(AppPaths.DataDir, "guest-display-override.txt");

        public async Task<string> ReadGuestDisplayAsync()
        {
            if (Adb == null || !Adb.Available || Target == IntPtr.Zero) return GuestDisplayStatus = "Unknown (emulator not running)";
            var (c1, size) = await Adb.ShellAsync("wm size", 8000);
            var (c2, dens) = await Adb.ShellAsync("wm density", 8000);
            if (c1 != 0) return GuestDisplayStatus = "Unknown (ADB: " + size + ")";
            var (pw, ph, ow, oh) = AdbClient.ParseWmSize(size);
            var (pd, od) = AdbClient.ParseWmDensity(dens);
            GuestDisplayStatus = $"Android panel {pw}x{ph} @ {pd} dpi" + (ow > 0 ? $"; override {ow}x{oh}" : "") + (od > 0 ? $" @ {od} dpi" : "");
            Raise();
            return GuestDisplayStatus;
        }

        /// <summary>
        /// Applies the configured guest resolution with "wm size/density" (reversible; AVD files untouched).
        /// AvdDefault removes only an override this app applied earlier.
        /// </summary>
        public async Task<string> ApplyGuestDisplayAsync()
        {
            if (Adb == null || !Adb.Available || !AdbReady) { Log.Info("Guest display not changed (ADB not ready)."); return await ReadGuestDisplayAsync(); }
            var mode = s.GuestDisplayValue;
            if (mode == GuestResolution.AvdDefault)
            {
                if (File.Exists(OverrideMarker))
                {
                    Log.Info("Removing guest display override applied earlier by this app: " + (await Adb.ShellAsync("wm size reset")).output + " " + (await Adb.ShellAsync("wm density reset")).output);
                    File.Delete(OverrideMarker);
                }
                return await ReadGuestDisplayAsync();
            }
            var (w, h) = Geometry.GuestSize(mode, Monitor, Avd.LcdWidth, Avd.LcdHeight);
            int dpi = Geometry.GuestDensity(s.GuestDensity, Avd.LcdDensity, Avd.LcdHeight, h);
            var r1 = await Adb.ShellAsync($"wm size {w}x{h}");
            var r2 = dpi > 0 ? await Adb.ShellAsync($"wm density {dpi}") : (0, "");
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(OverrideMarker, $"{w}x{h}@{dpi} applied {DateTime.Now:s}");
            Log.Info($"Guest display override {w}x{h} density {dpi}: size rc={r1.code} {r1.output} density rc={r2.Item1} {r2.Item2}");
            var status = await ReadGuestDisplayAsync();
            Log.Info("Guest display now: " + status);
            return status;
        }

        // ================================================================ emulator control
        public async Task CloseEmulatorAsync()
        {
            if (Machine.IsTvModeActive) await ExitTvAsync("closing emulator");
            var w = Target != IntPtr.Zero ? Target : WindowFinder.PickMain(WindowFinder.EnumerateTopLevel(), s.AvdName)?.Hwnd ?? IntPtr.Zero;
            if (w == IntPtr.Zero) { Log.Info("Close emulator: no emulator window."); return; }
            Log.Info("Closing emulator normally (Quick Boot snapshot is saved by the emulator).");
            EmulatorProcesses.RequestClose(w);
            for (int i = 0; i < 120 && Native.IsWindow(w); i++) await Task.Delay(500);
            Target = IntPtr.Zero; TargetPid = 0; AdbReady = false; BootCompleted = false;
            Raise();
        }

        public async Task RestartEmulatorAsync()
        {
            await CloseEmulatorAsync();
            for (int i = 0; i < 60 && EmulatorProcesses.ForAvd(s.AvdName).Count > 0; i++) await Task.Delay(500);
            await LaunchTvAsync("restart emulator");
        }

        public async Task<string> OpenAndroidSettingsAsync(bool network)
        {
            if (Adb == null || !AdbReady) return "ADB not ready (start TV mode first).";
            string action = network ? "android.settings.WIFI_SETTINGS" : "android.settings.SETTINGS";
            var r = await Adb.ShellAsync("am start -a " + action, 8000);
            if (network && r.output.Contains("Error")) r = await Adb.ShellAsync("am start -a android.settings.SETTINGS", 8000);
            Log.Info($"Open Android {(network ? "network " : "")}settings: {r.output}");
            return r.code == 0 ? "Opened on the TV." : r.output;
        }

        // ================================================================ restoration
        /// <summary>Synchronous, thread-agnostic restore that does not wait for the emulator. Safe to call repeatedly.</summary>
        public void EmergencyRestore(string reason)
        {
            try
            {
                Log.Info("Emergency restore: " + reason);
                ReleaseCursor();
                if (saved != null && Native.IsWindow(saved.Hwnd) && Native.Pid(saved.Hwnd) == saved.Pid && (Machine.IsFullscreen || Machine.State == TvState.Entering || saved.Clipped))
                {
                    if (saved.NewStyle != saved.Style) Native.SetLong(saved.Hwnd, Native.GWL_STYLE, saved.Style);
                    if (saved.NewExStyle != saved.ExStyle) Native.SetLong(saved.Hwnd, Native.GWL_EXSTYLE, saved.ExStyle);
                    if (saved.Clipped) { Native.SetWindowRgn(saved.Hwnd, IntPtr.Zero, true); saved.Clipped = false; }
                    var r = saved.Rect;
                    Native.SetWindowPos(saved.Hwnd, saved.WasTopmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, r.Left, r.Top, r.Width, r.Height, Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW | Native.SWP_NOACTIVATE);
                    foreach (var t in saved.HiddenTools) if (Native.IsWindow(t)) Native.ShowWindow(t, Native.SW_SHOWNOACTIVATE);
                    saved.HiddenTools.Clear();
                    Log.Info("Emergency restore: emulator window returned to " + r);
                }
                saved = null;   // nothing left to restore; prevents a later WriteState from recreating the record
                if (backdropHwnd != IntPtr.Zero) Native.ShowWindow(backdropHwnd, Native.SW_HIDE);
                RestoreTaskbar();
                if (Machine.IsFullscreen || Machine.State == TvState.Entering) Machine.TryMove(TvState.Exiting, reason);
                if (Machine.State == TvState.Exiting) Machine.TryMove(TvState.Windowed, reason);
                if (File.Exists(statePath)) File.Delete(statePath);
            }
            catch (Exception ex) { Log.Error("Emergency restore error", ex); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            EmergencyRestore("application closing");
            timer.Stop();
            UninstallHooks();
            Adb?.Dispose();
            try { backdrop.Dispose(); } catch { }
            Log.Info("Engine disposed: hooks, timer, backdrop and ADB shell released.");
        }

        /// <summary>Settings the engine changed itself (e.g. remembered window rect) that the app should persist.</summary>
        public string RememberedNormalWindow => s.LastNormalWindow;
    }
}
