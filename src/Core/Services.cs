using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AndroidTVDesktop.Core
{
    /// <summary>Diagnostics report, network/ADB checks and log export. Nothing is uploaded anywhere.</summary>
    public static class Diagnostics
    {
        public static string BuildReport(TvEngine engine, AppSettings s)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{AppPaths.AppName} {AppPaths.Version} diagnostics - {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"Windows: {Environment.OSVersion.VersionString} 64-bit={Environment.Is64BitOperatingSystem} .NET CLR {Environment.Version}");
            try
            {
                var ci = new Microsoft.VisualBasic.Devices.ComputerInfo();
                sb.AppendLine($"Memory: {ci.AvailablePhysicalMemory / (1024 * 1024)} MB free of {ci.TotalPhysicalMemory / (1024 * 1024)} MB");
            }
            catch { }
            sb.AppendLine();
            sb.AppendLine("Monitors:");
            foreach (var m in MonitorService.Enumerate()) sb.AppendLine("  " + m);
            sb.AppendLine($"Selected TV monitor: {engine.Monitor?.Label ?? "none"} ({engine.MonitorReason})");
            sb.AppendLine();
            sb.AppendLine($"SDK: {engine.Sdk.SdkRoot}");
            sb.AppendLine($"  emulator.exe: {engine.Sdk.EmulatorExe} found={engine.Sdk.EmulatorFound}");
            sb.AppendLine($"  adb.exe: {engine.Sdk.AdbExe} found={engine.Sdk.AdbFound}");
            var a = engine.Avd;
            sb.AppendLine($"AVD: {a.Name} found={a.Found} config={a.ConfigPath}");
            if (a.Found) sb.AppendLine($"  panel {a.LcdWidth}x{a.LcdHeight} @ {a.LcdDensity} dpi, RAM {a.RamMb} MB, image {a.SystemImage} ({a.Tag}, {a.Abi}), skin {a.Skin}, device frame={a.ShowDeviceFrame}");
            sb.AppendLine();
            sb.AppendLine($"TV mode state: {engine.Machine.State}");
            sb.AppendLine($"Emulator window: {(engine.Target == IntPtr.Zero ? "none" : Native.Hex(engine.Target) + " PID " + engine.TargetPid + " rect " + Native.Rect(engine.Target))}");
            sb.AppendLine($"ADB serial: {engine.Adb?.Serial ?? "-"} ready={engine.AdbReady} boot completed={engine.BootCompleted}");
            sb.AppendLine($"Guest display: {engine.GuestDisplayStatus}");
            sb.AppendLine($"Last startup time: {(engine.StartupSeconds.HasValue ? engine.StartupSeconds.Value.ToString("0.0") + " s" : "-")}");
            sb.AppendLine($"Last coverage check: {engine.LastCoverage}");
            sb.AppendLine($"Last error: {engine.LastError}");
            sb.AppendLine("Emulator processes:");
            foreach (var p in EmulatorProcesses.ForAvd(s.AvdName))
                sb.AppendLine($"  {p.Name} PID {p.Pid}: working set {p.WorkingSetMb} MB, private {p.PrivateMb} MB, responding={p.Responding}, started {p.StartTime:HH:mm:ss}");
            sb.AppendLine();
            sb.AppendLine("Settings: " + s.ToJson().Replace(Environment.NewLine, " "));
            return sb.ToString();
        }

        public sealed class NetworkResult { public string Name; public bool Ok; public string Detail; public override string ToString() => $"{(Ok ? "OK  " : "FAIL")} {Name}: {Detail}"; }

        public static async Task<List<NetworkResult>> CheckNetworkAsync(TvEngine engine)
        {
            var list = new List<NetworkResult>();
            list.Add(await HostPing("Windows -> internet (8.8.8.8)", "8.8.8.8"));
            list.Add(await HostPing("Windows -> DNS (www.google.com)", "www.google.com"));
            var adb = engine.Adb;
            if (adb == null || !adb.Available) { list.Add(new NetworkResult { Name = "ADB", Ok = false, Detail = "adb.exe not found" }); return list; }
            bool online = await adb.IsDeviceOnlineAsync();
            list.Add(new NetworkResult { Name = "ADB connection", Ok = online, Detail = online ? $"{adb.Serial} online" : $"{adb.Serial ?? "no emulator"} not reachable (start the emulator)" });
            if (!online) return list;
            var p1 = await adb.ShellAsync("ping -c 2 -W 3 8.8.8.8", 12000);
            list.Add(new NetworkResult { Name = "Android -> internet (8.8.8.8)", Ok = p1.output.Contains(" 0% packet loss") || p1.output.Contains("bytes from"), Detail = LastLine(p1.output) });
            var p2 = await adb.ShellAsync("ping -c 2 -W 3 www.google.com", 12000);
            list.Add(new NetworkResult { Name = "Android -> DNS (www.google.com)", Ok = p2.output.Contains("bytes from"), Detail = LastLine(p2.output) });
            var t = await adb.ShellAsync("date", 5000);
            list.Add(new NetworkResult { Name = "Android clock (wrong time breaks TLS/Google sign-in)", Ok = t.code == 0, Detail = t.output + $" (Windows: {DateTime.UtcNow:ddd MMM d HH:mm:ss} UTC)" });
            var net = await adb.ShellAsync("dumpsys connectivity | grep -m 3 -E \"NetworkAgentInfo|Active default network\"", 8000);
            list.Add(new NetworkResult { Name = "Android active network", Ok = net.output.Length > 0, Detail = net.output.Length > 200 ? net.output.Substring(0, 200) + "..." : net.output });
            return list;
        }

        static string LastLine(string s) => (s ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).LastOrDefault() ?? "";

        static async Task<NetworkResult> HostPing(string name, string host)
        {
            try
            {
                using (var p = new Ping())
                {
                    var r = await p.SendPingAsync(host, 3000);
                    return new NetworkResult { Name = name, Ok = r.Status == IPStatus.Success, Detail = r.Status == IPStatus.Success ? $"{r.RoundtripTime} ms" : r.Status.ToString() };
                }
            }
            catch (Exception ex) { return new NetworkResult { Name = name, Ok = false, Detail = ex.GetBaseException().Message }; }
        }

        /// <summary>Zip with the report, logs and settings (no AVD data, no account data). Returns the zip path.</summary>
        public static string Export(string report, string settingsJson, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            string zip = Path.Combine(targetDir, $"AndroidTVDesktop-diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            using (var fs = new FileStream(zip, FileMode.CreateNew))
            using (var z = new ZipArchive(fs, ZipArchiveMode.Create))
            {
                void Add(string name, string text) { var e = z.CreateEntry(name); using (var w = new StreamWriter(e.Open(), new UTF8Encoding(false))) w.Write(text); }
                Add("report.txt", report);
                Add("settings.json", settingsJson);
                foreach (var f in Log.AllLogFiles())
                {
                    string text;
                    using (var r = new StreamReader(new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))) text = r.ReadToEnd();
                    Add("logs/" + Path.GetFileName(f), text);
                }
            }
            Log.Info("Diagnostics exported to " + zip);
            return zip;
        }
    }

    /// <summary>One app instance per user session. A second start passes its command to the first and exits.</summary>
    public sealed class SingleInstance : IDisposable
    {
        readonly Mutex mutex;
        readonly EventWaitHandle signal;
        readonly string commandFile;
        readonly RegisteredWaitHandle registration;
        public bool IsFirst { get; }

        public SingleInstance(string name, Action<string> onCommand)
        {
            mutex = new Mutex(true, @"Local\" + name + "_Instance", out bool created);
            IsFirst = created;
            signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\" + name + "_Signal");
            commandFile = Path.Combine(Path.GetTempPath(), name + "_command.txt");
            if (IsFirst && onCommand != null)
                registration = ThreadPool.RegisterWaitForSingleObject(signal, (o, t) =>
                {
                    string cmd = "";
                    try { if (File.Exists(commandFile)) { cmd = File.ReadAllText(commandFile); File.Delete(commandFile); } } catch { }
                    onCommand(cmd);
                }, null, Timeout.Infinite, false);
        }

        /// <summary>Called by a second instance: hands over its command line and wakes the first instance.</summary>
        public void SendToFirst(string command)
        {
            try { File.WriteAllText(commandFile, command ?? ""); } catch { }
            signal.Set();
        }

        public void Dispose()
        {
            registration?.Unregister(null);
            if (IsFirst) { try { mutex.ReleaseMutex(); } catch { } }
            mutex.Dispose(); signal.Dispose();
        }
    }

    /// <summary>Per-user .lnk shortcuts (Desktop, Start Menu, Startup) via WScript.Shell. No registry, no admin rights.</summary>
    public static class Shortcuts
    {
        public const string Name = "Android TV Desktop.lnk";
        public static string StartupPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), Name);

        public static void Create(string lnkPath, string target, string args, string description, string icon = null)
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(shellType);
            dynamic sc = shell.CreateShortcut(lnkPath);
            sc.TargetPath = target; sc.Arguments = args ?? ""; sc.WorkingDirectory = Path.GetDirectoryName(target);
            sc.Description = description; if (icon != null) sc.IconLocation = icon + ",0";
            sc.Save();
        }

        public static bool StartupEnabled => File.Exists(StartupPath);

        public enum SignIn { Nothing, Background, Tv }

        /// <summary>Command-line arguments for each sign-in mode (pure; unit tested).</summary>
        public static string ArgsFor(SignIn mode) =>
            mode == SignIn.Background ? "--background" : mode == SignIn.Tv ? "--tv-mode --startup-delay 20" : null;

        /// <summary>What the Startup-folder shortcut currently does.</summary>
        public static SignIn CurrentSignIn()
        {
            if (!File.Exists(StartupPath)) return SignIn.Nothing;
            try
            {
                dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell"));
                string args = shell.CreateShortcut(StartupPath).Arguments;
                return args.IndexOf("--background", StringComparison.OrdinalIgnoreCase) >= 0 ? SignIn.Background : SignIn.Tv;
            }
            catch { return SignIn.Tv; }
        }

        /// <summary>Creates/replaces/removes the per-user Startup shortcut. No registry, no admin rights.</summary>
        public static void SetSignIn(SignIn mode, string exePath)
        {
            if (mode == SignIn.Nothing)
            {
                if (File.Exists(StartupPath)) { File.Delete(StartupPath); Log.Info("Sign-in start disabled."); }
                return;
            }
            Create(StartupPath, exePath, ArgsFor(mode),
                   mode == SignIn.Background ? "Android TV Desktop in the background at sign-in (phone remote ready)" : "Start Android TV at sign-in (Android TV Desktop)", exePath);
            Log.Info($"Sign-in start set to {mode}: {StartupPath} {ArgsFor(mode)}");
        }

        public static void SetStartup(bool enable, string exePath) => SetSignIn(enable ? SignIn.Tv : SignIn.Nothing, exePath);
    }

    /// <summary>Desktop recovery from the restore record, independent of the engine (used after a crash, and by the Restore button).</summary>
    public static class DesktopRestore
    {
        public static List<string> Run(string avdName)
        {
            var done = new List<string>();
            var path = RestoreRecord.DefaultPath;
            var restoredHwnds = new HashSet<long>();
            if (File.Exists(path) && RestoreRecord.TryParse(File.ReadAllText(path), out var rec))
            {
                var h = new IntPtr(rec.Hwnd);
                if (Native.IsWindow(h) && Native.Pid(h) == rec.Pid)
                {
                    Native.SetWindowRgn(h, IntPtr.Zero, true);
                    Native.SetLong(h, Native.GWL_STYLE, rec.Style);
                    Native.SetLong(h, Native.GWL_EXSTYLE, rec.ExStyle);
                    Native.SetWindowPos(h, rec.WasTopmost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST, rec.Rect.Left, rec.Rect.Top, rec.Rect.Width, rec.Rect.Height, Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
                    restoredHwnds.Add(rec.Hwnd);
                    done.Add($"Emulator window restored to {rec.Rect} from the restore record.");
                }
                foreach (var t in rec.HiddenTools) { var th = new IntPtr(t); if (Native.IsWindow(th)) Native.ShowWindow(th, Native.SW_SHOWNOACTIVATE); }
                try { File.Delete(path); } catch { }
            }
            // Any emulator window still topmost or covering a monitor.
            foreach (var w in WindowFinder.EnumerateTopLevel().Where(w => w.Visible && w.Owner == IntPtr.Zero && w.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase) && w.Title.StartsWith("Android Emulator")))
            {
                var mon = MonitorService.Enumerate().OrderByDescending(m => m.Bounds.Intersect(w.Rect).Area).FirstOrDefault();
                bool covers = mon != null && w.Rect.Width >= mon.Bounds.Width && w.Rect.Height >= mon.Bounds.Height;
                if (restoredHwnds.Contains(w.Hwnd.ToInt64()) && !covers) continue;
                if (Native.IsTopmost(w.Hwnd) || covers)
                {
                    Native.SetWindowRgn(w.Hwnd, IntPtr.Zero, true);
                    var np = mon != null ? MonitorSelector.NormalPlacement(mon) : new RECT(100, 100, 1380, 820);
                    Native.SetWindowPos(w.Hwnd, Native.HWND_NOTOPMOST, np.Left, np.Top, np.Width, np.Height, Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
                    done.Add($"Emulator window {Native.Hex(w.Hwnd)} un-fullscreened to {np}.");
                }
            }
            // Hidden emulator toolbars.
            foreach (var w in WindowFinder.EnumerateTopLevel().Where(w => !w.Visible && w.Owner != IntPtr.Zero && w.Title == "Emulator" && w.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase) && Native.IsWindowVisible(w.Owner)))
            { Native.ShowWindow(w.Hwnd, Native.SW_SHOWNOACTIVATE); done.Add("Emulator toolbar re-shown."); }
            foreach (var cls in new[] { "Shell_TrayWnd", "Shell_SecondaryTrayWnd" })
            {
                IntPtr h = IntPtr.Zero;
                while ((h = Native.FindWindowEx(IntPtr.Zero, h, cls, null)) != IntPtr.Zero)
                    if (!Native.IsWindowVisible(h)) { Native.ShowWindow(h, Native.SW_SHOW); done.Add(cls + " re-shown."); }
            }
            Native.ClipCursorNull(IntPtr.Zero);
            if (done.Count == 0) done.Add("Nothing needed restoring; the desktop is in its normal state.");
            foreach (var d in done) Log.Info("Desktop restore: " + d);
            return done;
        }
    }
}
