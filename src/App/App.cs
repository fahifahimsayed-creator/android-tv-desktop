using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using AndroidTVDesktop.Core;

namespace AndroidTVDesktop
{
    /// <summary>Shared state for the UI: engine, settings and persistence.</summary>
    sealed class AppState
    {
        public TvEngine Engine;
        public AppSettings Settings;
        public SettingsStore Store;
        public string ExePath = System.Reflection.Assembly.GetExecutingAssembly().Location;
        public PhoneRemoteServer Remote;
        public Tray Tray;
        /// <summary>True once the user chose Exit; until then closing the window just hides it to the tray.</summary>
        public bool ExitRequested;

        /// <summary>Change settings, validate, save atomically and push to the engine and the phone remote.</summary>
        public void Update(Action<AppSettings> change)
        {
            change(Settings);
            foreach (var f in Settings.Validate()) Log.Warn("Setting corrected: " + f);
            try { Store.Save(Settings); } catch (Exception ex) { Log.Error("Saving settings failed", ex); }
            Engine?.ApplySettings(Settings);
            SyncRemote();
        }

        /// <summary>Phone "Fullscreen" / "Esc" buttons (called on the server thread; work runs on the UI thread).</summary>
        string RemoteAppAction(string name)
        {
            var disp = System.Windows.Application.Current?.Dispatcher;
            if (disp == null || Engine == null) return null;
            string msg = null;
            disp.Invoke(new Action(() =>
            {
                var m = Engine.Machine;
                bool netflixInFront = PcControl.ForegroundNetflix() != IntPtr.Zero;
                if (name == "netflix")
                {
                    msg = "Opening Netflix on the PC...";
                    _ = OpenNetflixAsync();
                }
                else if (name == "showtv")
                {
                    bool hid = PcControl.MinimiseNetflix();
                    if (!m.IsTvModeActive) { msg = "Starting the TV..."; _ = Engine.LaunchTvAsync("phone remote: TV"); }
                    else if (m.State == TvState.Windowed) { msg = "TV"; _ = Engine.EnterFullscreenAsync("phone remote: TV"); }
                    else msg = hid ? "Back to the TV" : "TV";
                }
                else if (netflixInFront && (name == "fullscreen" || name == "exitfullscreen"))
                {
                    var nf = PcControl.ForegroundNetflix();
                    bool full = Engine.Monitor != null && Native.Rect(nf).Near(Engine.Monitor.Bounds, 1);
                    if (name == "fullscreen" || full) { PcControl.PressF11(); msg = full ? "Netflix: window" : "Netflix: fullscreen"; }
                    else msg = "Netflix is not fullscreen";
                }
                else if (name == "fullscreen")
                {
                    msg = !m.IsTvModeActive ? "Starting the TV..." : m.IsFullscreen ? "Window mode" : m.State == TvState.Windowed ? "Fullscreen" : "TV is starting, please wait";
                    _ = Engine.ToggleFullscreenAsync();
                }
                else if (name == "exitfullscreen")
                {
                    if (m.IsFullscreen) { msg = "Window mode"; _ = Engine.ExitFullscreenAsync("phone remote (Esc)"); }
                    else msg = "Not in fullscreen";
                }
                Log.Info($"Phone remote app action '{name}': {msg}");
            }));
            return msg;
        }

        /// <summary>Netflix (PC): leave TV fullscreen first so Netflix is not hidden behind the TV, then open/reuse it.</summary>
        public async System.Threading.Tasks.Task<string> OpenNetflixAsync()
        {
            if (Engine.IsFullscreen) await Engine.ExitFullscreenAsync("Netflix (PC) opened");
            Engine.Refresh();
            var msg = await NetflixLauncher.LaunchAsync(Engine.Monitor);
            Log.Info("Netflix (PC): " + msg);
            return msg;
        }

        /// <summary>Starts/stops/restarts the phone remote to match the settings.</summary>
        public void SyncRemote()
        {
            var s = Settings;
            bool want = s.PhoneRemoteEnabled;
            if (Remote != null && (!want || Remote.Port != s.PhoneRemotePort || Remote.Pin != s.PhoneRemotePin)) { Remote.Dispose(); Remote = null; }
            if (want && Remote == null)
            {
                Remote = new PhoneRemoteServer(s.PhoneRemotePort, s.PhoneRemotePin, s.AvdName, Engine.Sdk.AdbExe);
                Remote.AppAction = RemoteAppAction;
                Remote.PcKey = PcControl.SendKey;          // only acts while Netflix (PC) is the foreground window
                Remote.PcText = PcControl.SendText;
                Remote.PcTarget = () => PcControl.ForegroundNetflix() != IntPtr.Zero ? "Netflix (PC)" : null;
                Remote.Start();
            }
        }
    }

    static class Program
    {
        const string InstanceName = "AndroidTVDesktop";
        static AppState state;
        static MainWindow window;
        static SingleInstance instance;

        [STAThread]
        static int Main(string[] args)
        {
            Log.Init(AppPaths.LogDir);
            string cmd = string.Join(" ", args);

            if (args.Contains("--restore-desktop", StringComparer.OrdinalIgnoreCase))
            {
                // Ask a running instance to leave TV mode first (otherwise its monitor would re-apply fullscreen).
                using (var probe = new SingleInstance(InstanceName, null))
                    if (!probe.IsFirst) { probe.SendToFirst("--exit-tv"); System.Threading.Thread.Sleep(3000); }
                foreach (var d in DesktopRestore.Run("Television_4K")) Console.WriteLine(d);
                return 0;
            }

            instance = new SingleInstance(InstanceName, c => window?.Dispatcher.BeginInvoke(new Action(() => OnSecondInstance(c))));
            if (!instance.IsFirst)
            {
                Log.Info("Second app instance started (" + cmd + "); handing over to the running instance and exiting.");
                instance.SendToFirst(cmd);
                instance.Dispose();
                return 0;
            }

            Log.Info($"==== {AppPaths.AppName} {AppPaths.Version} starting (PID {System.Diagnostics.Process.GetCurrentProcess().Id}) args='{cmd}'");
            var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
            app.Resources.MergedDictionaries.Add(Theme.Load());
            app.DispatcherUnhandledException += (s, e) =>
            {
                Log.Error("Unhandled UI exception", e.Exception);
                state?.Engine?.EmergencyRestore("unhandled UI exception");
                MessageBox.Show("An unexpected error occurred. The desktop was restored.\n\n" + e.Exception.Message + "\n\nLog: " + Log.FilePath,
                                AppPaths.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += (s, e) =>
            {
                Log.Error("Fatal exception: " + e.ExceptionObject);
                state?.Engine?.EmergencyRestore("fatal exception");
            };
            TaskScheduler.UnobservedTaskException += (s, e) => { Log.Error("Unobserved task exception", e.Exception); e.SetObserved(); };
            Microsoft.Win32.SystemEvents.SessionEnding += (s, e) => { Log.Info("Windows session ending; restoring desktop."); state?.Engine?.EmergencyRestore("session ending"); };

            // A previous run that died while fullscreen leaves a restore record behind.
            if (File.Exists(RestoreRecord.DefaultPath))
            {
                Log.Warn("Restore record from a previous run found; restoring the desktop first.");
                foreach (var d in DesktopRestore.Run("Television_4K")) Log.Info(d);
            }

            int si = Array.FindIndex(args, a => a.Equals("--settings", StringComparison.OrdinalIgnoreCase));
            var store = new SettingsStore(si >= 0 && si + 1 < args.Length ? Path.GetFullPath(args[si + 1]) : SettingsStore.DefaultPath);
            if (si >= 0) Log.Info("Using settings file " + store.FilePath);
            state = new AppState { Store = store, Settings = store.Load() };
            if (!AvdInfo.Read(state.Settings.AvdName).Found)
            {
                // First run on another PC: use that PC's Android TV AVD instead of the default name.
                var pick = AvdInfo.PickTvAvd(AvdInfo.ListAvds(), AvdInfo.Read);
                if (pick != null) { Log.Info($"AVD '{state.Settings.AvdName}' not found on this PC; using '{pick}' (change it on the System page)."); state.Settings.AvdName = pick; }
                else Log.Warn("No Android Virtual Device found. Create an Android TV device in Android Studio > Device Manager.");
            }
            state.Engine = new TvEngine(state.Settings, app.Dispatcher);
            state.Engine.EmergencyExitRequested += () => { window?.ShowFromTray(); };
            try { store.Save(state.Settings); } catch (Exception ex) { Log.Warn("Could not save settings: " + ex.Message); }  // persists validated values (e.g. a generated PIN)
            state.SyncRemote();

            window = new MainWindow(state);
            // The app lives until Exit (tray / System page), not until the window closes: with "keep running in the
            // tray" the window's X only hides it so the phone remote keeps working.
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            window.Closed += (s, e) => app.Shutdown();
            state.Tray = new Tray(state, () => window);
            int delay = ParseDelay(args);
            bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
            bool tvMode = args.Contains("--tv-mode", StringComparer.OrdinalIgnoreCase) || state.Settings.LaunchTvOnAppStart;
            app.Dispatcher.BeginInvoke(new Action(async () =>
            {
                if (background) Log.Info("Started in the background (tray); phone remote " + (state.Remote?.Running == true ? "on" : "off") + ".");
                else window.Show();
                if (tvMode)
                {
                    if (delay > 0) { Log.Info($"Waiting {delay} s before starting TV mode (sign-in start)."); await Task.Delay(delay * 1000); }
                    await state.Engine.LaunchTvAsync(args.Contains("--tv-mode", StringComparer.OrdinalIgnoreCase) ? "command line --tv-mode" : "launch TV on app start (setting)");
                }
            }));
            int code;
            try { code = app.Run(); }
            finally
            {
                try { state.Tray?.Dispose(); } catch { }
                try { state.Remote?.Dispose(); } catch (Exception ex) { Log.Error("Phone remote dispose", ex); }
                try { state.Engine.Dispose(); } catch (Exception ex) { Log.Error("Engine dispose", ex); }
                instance.Dispose();
                Log.Info("==== Application exited cleanly.");
            }
            return code;
        }

        static int ParseDelay(string[] args)
        {
            int i = Array.FindIndex(args, a => a.Equals("--startup-delay", StringComparison.OrdinalIgnoreCase));
            return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int d) ? Math.Max(0, Math.Min(300, d)) : 0;
        }

        static async void OnSecondInstance(string cmd)
        {
            Log.Info("Another launch requested: '" + cmd + "'");
            if (cmd.IndexOf("--exit-tv", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                state.Engine.EmergencyRestore("--exit-tv requested (Restore-Desktop)");
                await state.Engine.ExitTvAsync("--exit-tv requested");
                return;
            }
            window.ShowFromTray();
            if (cmd.IndexOf("--tv-mode", StringComparison.OrdinalIgnoreCase) >= 0) await state.Engine.LaunchTvAsync("second launch with --tv-mode");
        }
    }
}
