using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Management;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AndroidTVDesktop.Core
{
    public sealed class SdkInfo
    {
        public string SdkRoot = "", EmulatorExe = "", AdbExe = "";
        public bool EmulatorFound => File.Exists(EmulatorExe);
        public bool AdbFound => File.Exists(AdbExe);

        public static SdkInfo Locate(string configured)
        {
            var roots = new List<string>();
            if (!string.IsNullOrWhiteSpace(configured)) roots.Add(configured);
            foreach (var v in new[] { "ANDROID_SDK_ROOT", "ANDROID_HOME" })
            {
                var e = Environment.GetEnvironmentVariable(v);
                if (!string.IsNullOrWhiteSpace(e)) roots.Add(e);
            }
            roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Android", "Sdk"));
            foreach (var r in roots)
            {
                var emu = Path.Combine(r, "emulator", "emulator.exe");
                if (File.Exists(emu)) return new SdkInfo { SdkRoot = r, EmulatorExe = emu, AdbExe = Path.Combine(r, "platform-tools", "adb.exe") };
            }
            var first = roots.Last();
            return new SdkInfo { SdkRoot = first, EmulatorExe = Path.Combine(first, "emulator", "emulator.exe"), AdbExe = Path.Combine(first, "platform-tools", "adb.exe") };
        }
    }

    /// <summary>Read-only view of the AVD's config.ini. This app never writes AVD files.</summary>
    public sealed class AvdInfo
    {
        public string Name = "", ConfigPath = "", Folder = "";
        public int LcdWidth, LcdHeight, LcdDensity, RamMb;
        public string SystemImage = "", Tag = "", Abi = "", Skin = "", DeviceName = "";
        public bool ShowDeviceFrame;
        public bool Found => File.Exists(ConfigPath);

        public static AvdInfo Read(string avd)
        {
            var info = new AvdInfo { Name = avd };
            info.ConfigPath = FindConfig(avd) ?? "";
            if (!info.Found) return info;
            info.Folder = Path.GetDirectoryName(info.ConfigPath);
            Parse(File.ReadAllText(info.ConfigPath), info);
            return info;
        }

        public static void Parse(string text, AvdInfo info)
        {
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim(); int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                int.TryParse(v, out int n);
                switch (k)
                {
                    case "hw.lcd.width": info.LcdWidth = n; break;
                    case "hw.lcd.height": info.LcdHeight = n; break;
                    case "hw.lcd.density": info.LcdDensity = n; break;
                    case "hw.ramSize": info.RamMb = n > 0 ? n : (int.TryParse(v.TrimEnd('M', 'm'), out var m) ? m : 0); break;
                    case "image.sysdir.1": info.SystemImage = v; break;
                    case "tag.id": info.Tag = v; break;
                    case "abi.type": info.Abi = v; break;
                    case "skin.name": info.Skin = v; break;
                    case "hw.device.name": info.DeviceName = v; break;
                    case "showDeviceFrame": info.ShowDeviceFrame = v.Equals("yes", StringComparison.OrdinalIgnoreCase); break;
                }
            }
        }

        public static string FindConfig(string avd)
        {
            var dirs = new List<string>();
            var home = Environment.GetEnvironmentVariable("ANDROID_AVD_HOME");
            if (!string.IsNullOrEmpty(home)) dirs.Add(home);
            var user = Environment.GetEnvironmentVariable("ANDROID_USER_HOME");
            if (!string.IsNullOrEmpty(user)) dirs.Add(Path.Combine(user, "avd"));
            dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd"));
            foreach (var d in dirs)
            {
                var ini = Path.Combine(d, avd + ".ini");
                if (File.Exists(ini))
                    foreach (var line in File.ReadAllLines(ini))
                        if (line.StartsWith("path=")) { var p = Path.Combine(line.Substring(5).Trim(), "config.ini"); if (File.Exists(p)) return p; }
                var direct = Path.Combine(d, avd + ".avd", "config.ini");
                if (File.Exists(direct)) return direct;
            }
            return null;
        }

        /// <summary>
        /// Picks the AVD to use on a PC where the configured one does not exist (e.g. a fresh install on someone else's PC):
        /// the first Android TV / Google TV image (tag.id android-tv/google-tv), then the first AVD with "TV" in its name,
        /// then any AVD. Returns null when the PC has no AVDs at all. Pure apart from reading the AVD list.
        /// </summary>
        public static string PickTvAvd(IEnumerable<string> names, Func<string, AvdInfo> read)
        {
            var list = names.ToList();
            foreach (var n in list) { var i = read(n); if (i.Found && (i.Tag.IndexOf("tv", StringComparison.OrdinalIgnoreCase) >= 0)) return n; }
            var byName = list.FirstOrDefault(n => n.IndexOf("tv", StringComparison.OrdinalIgnoreCase) >= 0 || n.IndexOf("television", StringComparison.OrdinalIgnoreCase) >= 0);
            return byName ?? list.FirstOrDefault();
        }

        public static List<string> ListAvds()
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".android", "avd");
            if (!Directory.Exists(dir)) return new List<string>();
            return Directory.GetFiles(dir, "*.ini").Select(Path.GetFileNameWithoutExtension).OrderBy(n => n).ToList();
        }
    }

    public sealed class EmulatorProcessInfo
    {
        public int Pid; public string Name = ""; public string CommandLine = ""; public long WorkingSetMb, PrivateMb; public bool Responding = true;
        public DateTime? StartTime;
    }

    public static class EmulatorProcesses
    {
        /// <summary>emulator.exe / qemu-system-* processes whose command line names this AVD.</summary>
        public static List<EmulatorProcessInfo> ForAvd(string avd)
        {
            var list = new List<EmulatorProcessInfo>();
            try
            {
                using (var s = new ManagementObjectSearcher("SELECT ProcessId, Name, CommandLine FROM Win32_Process WHERE Name LIKE 'qemu-system%' OR Name = 'emulator.exe'"))
                    foreach (ManagementObject mo in s.Get())
                    {
                        string cmd = mo["CommandLine"] as string ?? "";
                        if (!MatchesAvd(cmd, avd)) continue;
                        var info = new EmulatorProcessInfo { Pid = Convert.ToInt32(mo["ProcessId"]), Name = mo["Name"] as string ?? "", CommandLine = cmd };
                        try
                        {
                            var p = Process.GetProcessById(info.Pid);
                            info.WorkingSetMb = p.WorkingSet64 / (1024 * 1024); info.PrivateMb = p.PrivateMemorySize64 / (1024 * 1024);
                            info.StartTime = p.StartTime;
                            if (p.MainWindowHandle != IntPtr.Zero) info.Responding = p.Responding;
                        }
                        catch { }
                        list.Add(info);
                    }
            }
            catch (Exception ex) { Log.Warn("WMI process query failed: " + ex.Message); }
            return list;
        }

        public static bool MatchesAvd(string commandLine, string avd)
        {
            if (string.IsNullOrEmpty(commandLine)) return false;
            var tokens = commandLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).Select(t => t.Trim('"')).ToList();
            for (int i = 0; i < tokens.Count; i++)
            {
                if (tokens[i].Equals("-avd", StringComparison.OrdinalIgnoreCase) && i + 1 < tokens.Count && tokens[i + 1].Equals(avd, StringComparison.OrdinalIgnoreCase)) return true;
                if (tokens[i].Equals("@" + avd, StringComparison.OrdinalIgnoreCase)) return true;
            }
            return false;
        }

        /// <summary>Command line used to start the AVD: frameless skin at the AVD's own panel size (no device bezel). Nothing is written to the AVD.</summary>
        public static string BuildArguments(AvdInfo avd, bool frameless)
        {
            var args = "-avd " + avd.Name;
            if (frameless && avd.LcdWidth > 0 && avd.LcdHeight > 0) args += $" -skin {avd.LcdWidth}x{avd.LcdHeight}";
            return args;
        }

        public static Process Launch(SdkInfo sdk, AvdInfo avd, bool frameless)
        {
            string args = BuildArguments(avd, frameless);
            Log.Info($"Starting emulator: \"{sdk.EmulatorExe}\" {args}");
            var psi = new ProcessStartInfo(sdk.EmulatorExe, args)
            {
                UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = Path.GetDirectoryName(sdk.EmulatorExe)
            };
            return Process.Start(psi);
        }

        /// <summary>Normal close (same as the window's X button: the emulator saves its Quick Boot snapshot).</summary>
        public static bool RequestClose(IntPtr mainWindow) => mainWindow != IntPtr.Zero && Native.PostMessage(mainWindow, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Startup timing rules, kept pure for testing.</summary>
    public sealed class StartupPolicy
    {
        public TimeSpan WindowTimeout, BootTimeout;
        public StartupPolicy(int windowSeconds, int bootSeconds) { WindowTimeout = TimeSpan.FromSeconds(windowSeconds); BootTimeout = TimeSpan.FromSeconds(bootSeconds); }

        public enum Verdict { KeepWaiting, WindowTimedOut, EmulatorExited, BootTimedOut }

        public Verdict CheckWindow(TimeSpan elapsed, bool processAlive, bool launchedByUs, TimeSpan sinceLaunch)
        {
            if (launchedByUs && !processAlive && sinceLaunch > TimeSpan.FromSeconds(8)) return Verdict.EmulatorExited;
            return elapsed > WindowTimeout ? Verdict.WindowTimedOut : Verdict.KeepWaiting;
        }

        public Verdict CheckBoot(TimeSpan elapsed) => elapsed > BootTimeout ? Verdict.BootTimedOut : Verdict.KeepWaiting;
    }

    /// <summary>
    /// Minimal ADB client (platform-tools adb.exe). Every call has a timeout. Key events go through one
    /// persistent "adb shell" so each key costs milliseconds instead of a new process.
    /// </summary>
    public sealed class AdbClient : IDisposable
    {
        readonly string adb;
        public string Serial { get; set; }
        Process shell;
        readonly object shellLock = new object();

        public AdbClient(string adbExe, string serial) { adb = adbExe; Serial = serial; }
        public bool Available => File.Exists(adb);

        public async Task<(int code, string output)> RunAsync(string args, int timeoutMs = 15000, bool serial = true)
        {
            if (!Available) return (-1, "adb.exe not found");
            string full = (serial && !string.IsNullOrEmpty(Serial) ? "-s " + Serial + " " : "") + args;
            var psi = new ProcessStartInfo(adb, full)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            try
            {
                using (var p = new Process { StartInfo = psi })
                {
                    var sb = new StringBuilder();
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (sb) sb.AppendLine(e.Data); };
                    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    var done = await Task.Run(() => p.WaitForExit(timeoutMs)).ConfigureAwait(false);
                    if (!done) { try { p.Kill(); } catch { } return (-2, "timeout after " + timeoutMs + " ms: adb " + full); }
                    p.WaitForExit();
                    lock (sb) return (p.ExitCode, sb.ToString().Trim());
                }
            }
            catch (Exception ex) { return (-3, ex.Message); }
        }

        public Task<(int code, string output)> ShellAsync(string cmd, int timeoutMs = 15000) => RunAsync("shell " + cmd, timeoutMs);

        public async Task<bool> IsDeviceOnlineAsync()
        {
            var (code, outp) = await RunAsync("get-state", 5000).ConfigureAwait(false);
            return code == 0 && outp.Trim() == "device";
        }

        public async Task<bool> IsBootCompletedAsync()
        {
            var (code, outp) = await ShellAsync("getprop sys.boot_completed", 5000).ConfigureAwait(false);
            return code == 0 && outp.Trim() == "1";
        }

        public static (int w, int h, int ow, int oh) ParseWmSize(string output)
        {
            int w = 0, h = 0, ow = 0, oh = 0;
            foreach (var line in (output ?? "").Split('\n'))
            {
                var t = line.Trim(); int c = t.IndexOf(':');
                if (c < 0) continue;
                var parts = t.Substring(c + 1).Trim().Split('x');
                if (parts.Length != 2 || !int.TryParse(parts[0], out int a) || !int.TryParse(parts[1], out int b)) continue;
                if (t.StartsWith("Physical")) { w = a; h = b; } else if (t.StartsWith("Override")) { ow = a; oh = b; }
            }
            return (w, h, ow, oh);
        }

        public static (int physical, int over) ParseWmDensity(string output)
        {
            int p = 0, o = 0;
            foreach (var line in (output ?? "").Split('\n'))
            {
                var t = line.Trim(); int c = t.IndexOf(':');
                if (c < 0 || !int.TryParse(t.Substring(c + 1).Trim(), out int v)) continue;
                if (t.StartsWith("Physical")) p = v; else if (t.StartsWith("Override")) o = v;
            }
            return (p, o);
        }

        /// <summary>Sends a key through a persistent adb shell (falls back to one-shot).</summary>
        public void SendKeyEvent(string keycode)
        {
            lock (shellLock)
            {
                try
                {
                    if (shell == null || shell.HasExited)
                    {
                        shell?.Dispose();
                        var psi = new ProcessStartInfo(adb, (string.IsNullOrEmpty(Serial) ? "" : "-s " + Serial + " ") + "shell")
                        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
                        shell = Process.Start(psi);
                        shell.OutputDataReceived += (s, e) => { };
                        shell.ErrorDataReceived += (s, e) => { };
                        shell.BeginOutputReadLine(); shell.BeginErrorReadLine();
                    }
                    shell.StandardInput.WriteLine("input keyevent " + keycode);
                    shell.StandardInput.Flush();
                }
                catch (Exception ex)
                {
                    Log.Warn("Persistent adb shell failed (" + ex.Message + "); using one-shot adb.");
                    _ = ShellAsync("input keyevent " + keycode, 5000);
                }
            }
        }

        public void Dispose()
        {
            lock (shellLock)
            {
                try { if (shell != null && !shell.HasExited) { shell.StandardInput.WriteLine("exit"); if (!shell.WaitForExit(1000)) shell.Kill(); } } catch { }
                shell?.Dispose(); shell = null;
            }
        }
    }
}
