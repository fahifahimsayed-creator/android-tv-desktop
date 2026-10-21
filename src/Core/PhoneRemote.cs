using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace AndroidTVDesktop.Core
{
    /// <summary>
    /// Phone web remote: a tiny HTTP server (raw TcpListener, so no URL ACL / admin rights are needed to listen)
    /// that serves a remote-control page to phones on the same Wi-Fi and turns button presses into Android key
    /// events over ADB. Off by default. Every API call needs the 6-digit PIN shown in the app; repeated wrong
    /// PINs lock the client out. Windows Firewall must allow the port for other devices to reach it (the app
    /// offers a one-click, admin-approved rule limited to the local subnet).
    /// </summary>
    public sealed class PhoneRemoteServer : IDisposable
    {
        public const string FirewallRuleName = "Android TV Desktop - phone remote";

        /// <summary>Remote button name -> Android key code. Only these can be sent.</summary>
        public static readonly IReadOnlyDictionary<string, string> Keys = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["up"] = "KEYCODE_DPAD_UP", ["down"] = "KEYCODE_DPAD_DOWN", ["left"] = "KEYCODE_DPAD_LEFT", ["right"] = "KEYCODE_DPAD_RIGHT",
            ["ok"] = "KEYCODE_DPAD_CENTER", ["back"] = "KEYCODE_BACK", ["home"] = "KEYCODE_HOME", ["menu"] = "KEYCODE_MENU",
            ["playpause"] = "KEYCODE_MEDIA_PLAY_PAUSE", ["rewind"] = "KEYCODE_MEDIA_REWIND", ["forward"] = "KEYCODE_MEDIA_FAST_FORWARD",
            ["volup"] = "KEYCODE_VOLUME_UP", ["voldown"] = "KEYCODE_VOLUME_DOWN", ["mute"] = "KEYCODE_VOLUME_MUTE",
            ["search"] = "KEYCODE_SEARCH", ["settings"] = "KEYCODE_SETTINGS", ["del"] = "KEYCODE_DEL", ["enter"] = "KEYCODE_ENTER",
        };

        readonly string avdName, adbExe;
        readonly PinGuard guard;
        TcpListener listener;
        CancellationTokenSource cts;
        AdbClient adb;
        readonly object adbLock = new object();

        public int Port { get; }
        public string Pin { get; }
        public bool Running => listener != null;
        public string LastError { get; private set; } = "";
        public event Action<string> Activity;
        /// <summary>
        /// App-level actions from the phone, run by Android TV Desktop: "fullscreen" (toggle / start TV), "exitfullscreen" (Esc),
        /// "netflix" (open Netflix (PC) in Edge), "showtv" (bring the Android TV to the front). Returns a status text, or null when not handled.
        /// </summary>
        public Func<string, string> AppAction;
        public static readonly string[] AppActions = { "fullscreen", "exitfullscreen", "netflix", "showtv" };
        /// <summary>When it returns true the key was handled on the PC (Netflix in Edge is the active window), not sent to Android.</summary>
        public Func<string, bool> PcKey;
        /// <summary>Same for typed text.</summary>
        public Func<string, bool> PcText;
        /// <summary>Name of the PC target currently controlled ("Netflix (PC)"), or null when keys go to the Android TV.</summary>
        public Func<string> PcTarget;
        DateTime adbResolvedAt = DateTime.MinValue;

        /// <summary>TV apps the remote can open (shown only when installed). "netflix" is Netflix (PC) in Edge.</summary>
        public sealed class TvApp { public string Name, Label, Package, Color; }
        public static readonly TvApp[] TvApps =
        {
            new TvApp { Name = "crunchyroll", Label = "Crunchyroll", Package = "com.crunchyroll.crunchyroid", Color = "#F47521" },
            new TvApp { Name = "jiohotstar", Label = "JioHotstar", Package = "in.startv.hotstar", Color = "#1F3BD1" },
            new TvApp { Name = "youtube", Label = "YouTube", Package = "com.google.android.youtube.tv", Color = "#E62117" },
            new TvApp { Name = "primevideo", Label = "Prime Video", Package = "com.amazon.amazonvideo.livingroom", Color = "#1A98FF" },
            new TvApp { Name = "sonyliv", Label = "SonyLIV", Package = "com.sonyliv", Color = "#5B2C83" },
            new TvApp { Name = "zee5", Label = "ZEE5", Package = "com.graymatrix.did", Color = "#8230C6" },
            new TvApp { Name = "sunnxt", Label = "Sun NXT", Package = "com.suntv.sunnxt", Color = "#D4145A" },
            new TvApp { Name = "spotify", Label = "Spotify", Package = "com.spotify.tv.android", Color = "#1DB954" },
        };
        readonly Dictionary<string, string> launchActivity = new Dictionary<string, string>();
        List<TvApp> installedApps; DateTime appsCheckedAt = DateTime.MinValue;

        public PhoneRemoteServer(int port, string pin, string avdName, string adbExe)
        {
            Port = port; Pin = pin; this.avdName = avdName; this.adbExe = adbExe;
            guard = new PinGuard(pin);
        }

        public static string NewPin()
        {
            var b = new byte[4];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(b);
            return (BitConverter.ToUInt32(b, 0) % 1000000).ToString("D6");
        }

        /// <summary>LAN addresses a phone can use (IPv4, up interfaces, no loopback/link-local).</summary>
        public static List<string> LanAddresses()
        {
            var list = new List<string>();
            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    if (ni.Description.IndexOf("virtual", StringComparison.OrdinalIgnoreCase) >= 0 || ni.Description.IndexOf("Hyper-V", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !ua.Address.ToString().StartsWith("169.254.")) list.Add(ua.Address.ToString());
                }
            }
            catch { }
            return list.Distinct().ToList();
        }

        public bool Start()
        {
            if (Running) return true;
            try
            {
                listener = new TcpListener(IPAddress.Any, Port);
                listener.Start();
                cts = new CancellationTokenSource();
                _ = AcceptLoop(cts.Token);
                Log.Info($"Phone remote listening on port {Port} ({string.Join(", ", LanAddresses().Select(a => "http://" + a + ":" + Port))}).");
                LastError = "";
                return true;
            }
            catch (Exception ex)
            {
                LastError = ex.GetBaseException().Message;
                Log.Error("Phone remote could not start on port " + Port + ": " + LastError);
                listener = null;
                return false;
            }
        }

        public void Stop()
        {
            try { cts?.Cancel(); listener?.Stop(); } catch { }
            listener = null;
            lock (adbLock) { adb?.Dispose(); adb = null; }
            Log.Info("Phone remote stopped.");
        }

        public void Dispose() => Stop();

        async Task AcceptLoop(CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                TcpClient c;
                try { c = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
                catch { if (ct.IsCancellationRequested) return; await Task.Delay(200).ConfigureAwait(false); continue; }
                _ = Task.Run(() => Handle(c));
            }
        }

        void Handle(TcpClient client)
        {
            using (client)
            {
                try
                {
                    client.ReceiveTimeout = 5000; client.SendTimeout = 5000;
                    var stream = client.GetStream();
                    var req = HttpRequest.Read(stream);
                    string ip = ((IPEndPoint)client.Client.RemoteEndPoint).Address.ToString();
                    if (req == null) { Respond(stream, 400, "text/plain", "bad request"); return; }
                    var (status, type, body) = Route(req, ip);
                    Respond(stream, status, type, body);
                }
                catch (Exception ex) { Log.Warn("Phone remote request failed: " + ex.GetBaseException().Message); }
            }
        }

        /// <summary>Request routing (separate from sockets so it can be unit tested).</summary>
        public (int status, string type, string body) Route(HttpRequest req, string clientIp)
        {
            if (req.Method == "GET" && (req.Path == "/" || req.Path == "/index.html")) return (200, "text/html; charset=utf-8", RemotePage.Html);
            if (req.Method == "GET" && req.Path == "/manifest.webmanifest") return (200, "application/manifest+json", RemotePage.Manifest);
            if (req.Method == "GET" && (req.Path == "/icon.svg" || req.Path == "/favicon.ico")) return (200, "image/svg+xml", RemotePage.Icon);
            if (!req.Path.StartsWith("/api/")) return (404, "text/plain", "not found");
            if (req.Method != "POST") return (405, "text/plain", "POST only");
            req.Headers.TryGetValue("x-pin", out var pin);
            var verdict = guard.Check(clientIp, pin, DateTime.UtcNow);
            if (verdict == PinGuard.Result.LockedOut) return (429, "text/plain", "Too many wrong PINs. Wait a minute.");
            if (verdict == PinGuard.Result.Wrong) { Log.Warn($"Phone remote: wrong PIN from {clientIp}."); return (403, "text/plain", "Wrong PIN"); }

            if (req.Path == "/api/ping") return (200, "text/plain", TvStatus());
            if (req.Path == "/api/apps")
            {
                // "name|label|color" per line; Netflix (PC) first, then TV apps that are installed.
                var lines = new List<string> { "netflix|Netflix|#E50914" };
                lines.AddRange(InstalledApps().Select(t => $"{t.Name}|{t.Label}|{t.Color}"));
                return (200, "text/plain", string.Join("\n", lines));
            }
            if (req.Path == "/api/launch")
            {
                string name = req.Body.Trim().ToLowerInvariant();
                if (name == "netflix")
                {
                    string r = AppAction?.Invoke("netflix");
                    Activity?.Invoke($"{clientIp}: open Netflix (PC)");
                    return r == null ? (503, "text/plain", "Android TV Desktop is not ready.") : (200, "text/plain", r);
                }
                var app = TvApps.FirstOrDefault(t => t.Name == name);
                if (app == null) return (400, "text/plain", "unknown app");
                var a = Adb(); if (a == null) return (503, "text/plain", "The TV (emulator) is not running.");
                AppAction?.Invoke("showtv");   // TV to the front (minimise Netflix (PC), start TV mode if off)
                string act = LaunchActivity(a, app.Package);
                if (act == null) return (404, "text/plain", app.Label + " is not installed on the TV.");
                var res = a.ShellAsync("am start -n " + act, 10000).GetAwaiter().GetResult();
                Activity?.Invoke($"{clientIp}: open {app.Label}");
                return res.code == 0 && !res.output.Contains("Error") ? (200, "text/plain", "Opening " + app.Label) : (502, "text/plain", "Could not open " + app.Label);
            }
            if (req.Path == "/api/key")
            {
                string name = req.Body.Trim();
                if (!Keys.TryGetValue(name, out var code)) return (400, "text/plain", "unknown key");
                if (PcKey != null && PcKey(name.ToLowerInvariant())) { Activity?.Invoke($"{clientIp}: {name} (PC)"); return (200, "text/plain", "ok"); }
                var a = Adb(); if (a == null) return (503, "text/plain", "The TV (emulator) is not running.");
                a.SendKeyEvent(code);
                Activity?.Invoke($"{clientIp}: {name}");
                return (200, "text/plain", "ok");
            }
            if (req.Path == "/api/app")
            {
                string name = req.Body.Trim().ToLowerInvariant();
                if (!AppActions.Contains(name)) return (400, "text/plain", "unknown action");
                string result = AppAction?.Invoke(name);
                if (result == null) return (503, "text/plain", "Android TV Desktop is not ready.");
                Activity?.Invoke($"{clientIp}: {name}");
                return (200, "text/plain", result);
            }
            if (req.Path == "/api/text")
            {
                if (PcText != null && !string.IsNullOrEmpty(req.Body) && req.Body.Length <= 200 && PcText(req.Body)) { Activity?.Invoke($"{clientIp}: typed {req.Body.Length} characters (PC)"); return (200, "text/plain", "ok"); }
                string arg = AndroidTextArg(req.Body);
                if (arg == null) return (400, "text/plain", "text must be 1-200 printable characters");
                var a = Adb(); if (a == null) return (503, "text/plain", "The TV (emulator) is not running.");
                var r = a.ShellAsync("input text " + arg, 8000).GetAwaiter().GetResult();
                Activity?.Invoke($"{clientIp}: typed {req.Body.Length} characters");
                return r.code == 0 ? (200, "text/plain", "ok") : (502, "text/plain", "adb: " + r.output);
            }
            return (404, "text/plain", "not found");
        }

        /// <summary>"TV on" / "TV off", or the PC target name when keys currently go to Netflix (PC).</summary>
        string TvStatus() => PcTarget?.Invoke() ?? (Adb() != null ? "TV on" : "TV off");

        /// <summary>Allow-listed TV apps that are installed (checked at most once a minute).</summary>
        List<TvApp> InstalledApps()
        {
            lock (launchActivity)
            {
                if (installedApps != null && (DateTime.UtcNow - appsCheckedAt).TotalSeconds < 60) return installedApps;
                var a = Adb();
                if (a == null) return installedApps ?? new List<TvApp>();
                var r = a.ShellAsync("pm list packages", 8000).GetAwaiter().GetResult();
                if (r.code != 0) return installedApps ?? new List<TvApp>();
                var pk = new HashSet<string>(r.output.Split('\n').Select(l => l.Trim().Replace("package:", "")));
                installedApps = TvApps.Where(t => pk.Contains(t.Package)).ToList();
                appsCheckedAt = DateTime.UtcNow;
                return installedApps;
            }
        }

        /// <summary>The app's TV (leanback) launch activity, cached.</summary>
        string LaunchActivity(AdbClient a, string package)
        {
            lock (launchActivity)
            {
                if (launchActivity.TryGetValue(package, out var cached)) return cached;
                var r = a.ShellAsync($"cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LEANBACK_LAUNCHER {package}", 8000).GetAwaiter().GetResult();
                var line = r.output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(package + "/"));
                if (line == null)
                {
                    r = a.ShellAsync($"cmd package resolve-activity --brief -a android.intent.action.MAIN -c android.intent.category.LAUNCHER {package}", 8000).GetAwaiter().GetResult();
                    line = r.output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith(package + "/"));
                }
                if (line != null && line.All(c => char.IsLetterOrDigit(c) || c == '.' || c == '/' || c == '_' || c == '$')) launchActivity[package] = line;
                else line = null;
                return line;
            }
        }

        /// <summary>
        /// ADB client for the running emulator of this AVD. Cached for 15 s: resolving it used to scan every window on
        /// the desktop (~600 ms per button press); now it reads only qemu processes' main window titles, rarely.
        /// </summary>
        AdbClient Adb()
        {
            lock (adbLock)
            {
                if (adb != null && (DateTime.UtcNow - adbResolvedAt).TotalSeconds < 15) return adb;
                string serial = FindSerial(avdName);
                adbResolvedAt = DateTime.UtcNow;
                if (serial == null) { adb?.Dispose(); adb = null; return null; }
                if (adb == null || adb.Serial != serial) { adb?.Dispose(); adb = new AdbClient(adbExe, serial); }
                return adb;
            }
        }

        static string FindSerial(string avd)
        {
            string prefix = WindowFinder.TitlePrefix(avd);
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    if (!p.ProcessName.StartsWith("qemu-system", StringComparison.OrdinalIgnoreCase)) continue;
                    if (p.MainWindowTitle.StartsWith(prefix, StringComparison.Ordinal)) return WindowFinder.AdbSerial(p.MainWindowTitle);
                }
                catch { }
                finally { p.Dispose(); }
            }
            return null;
        }

        /// <summary>Quotes text for "adb shell input text": spaces become %s; only printable ASCII is allowed.</summary>
        public static string AndroidTextArg(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 200) return null;
            var sb = new StringBuilder();
            foreach (char c in text)
            {
                if (c < 0x20 || c > 0x7E) return null;
                if (c == ' ') sb.Append("%s");
                else if (char.IsLetterOrDigit(c) || "@._-+,:/=".IndexOf(c) >= 0) sb.Append(c);
                else sb.Append('\\').Append(c);   // escape shell metacharacters
            }
            return sb.ToString();
        }

        static void Respond(Stream s, int status, string type, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body ?? "");
            string reason = status == 200 ? "OK" : status == 403 ? "Forbidden" : status == 404 ? "Not Found" : status == 429 ? "Too Many Requests" : status == 503 ? "Service Unavailable" : "Error";
            var head = $"HTTP/1.1 {status} {reason}\r\nContent-Type: {type}\r\nContent-Length: {bytes.Length}\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
            var hb = Encoding.ASCII.GetBytes(head);
            s.Write(hb, 0, hb.Length); s.Write(bytes, 0, bytes.Length); s.Flush();
        }

        // ---------------------------------------------------------------- firewall (needs the user's admin approval)
        public static bool FirewallRuleExists()
        {
            try
            {
                var psi = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{FirewallRuleName}\"")
                { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
                using (var p = Process.Start(psi)) { string o = p.StandardOutput.ReadToEnd(); p.WaitForExit(5000); return p.ExitCode == 0 && o.Contains(FirewallRuleName); }
            }
            catch { return false; }
        }

        /// <summary>Adds (or removes) an inbound TCP rule for the port, limited to the local subnet. Shows a UAC prompt.</summary>
        public static bool SetFirewallRule(bool add, int port)
        {
            string cmd = add
                ? $"advfirewall firewall add rule name=\"{FirewallRuleName}\" dir=in action=allow protocol=TCP localport={port} remoteip=localsubnet profile=any"
                : $"advfirewall firewall delete rule name=\"{FirewallRuleName}\"";
            try
            {
                var psi = new ProcessStartInfo("netsh", cmd) { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (var p = Process.Start(psi)) { p.WaitForExit(20000); Log.Info($"Firewall rule {(add ? "add" : "delete")} exit code {p.ExitCode}."); return p.ExitCode == 0; }
            }
            catch (Exception ex) { Log.Warn("Firewall change cancelled or failed: " + ex.Message); return false; }
        }
    }

    /// <summary>PIN check with per-client lockout: 8 wrong PINs within 2 minutes block that client for 2 minutes.</summary>
    public sealed class PinGuard
    {
        public enum Result { Ok, Wrong, LockedOut }
        readonly string pin;
        readonly Dictionary<string, List<DateTime>> failures = new Dictionary<string, List<DateTime>>();
        readonly Dictionary<string, DateTime> lockedUntil = new Dictionary<string, DateTime>();
        public PinGuard(string pin) { this.pin = pin ?? ""; }

        public Result Check(string client, string attempt, DateTime now)
        {
            lock (failures)
            {
                if (lockedUntil.TryGetValue(client, out var until) && now < until) return Result.LockedOut;
                if (pin.Length == 6 && attempt != null && FixedTimeEquals(attempt.Trim(), pin)) { failures.Remove(client); return Result.Ok; }
                if (!failures.TryGetValue(client, out var list)) failures[client] = list = new List<DateTime>();
                list.Add(now); list.RemoveAll(t => (now - t).TotalMinutes > 2);
                if (list.Count >= 8) { lockedUntil[client] = now.AddMinutes(2); list.Clear(); return Result.LockedOut; }
                return Result.Wrong;
            }
        }

        static bool FixedTimeEquals(string a, string b)
        {
            if (a.Length != b.Length) return false;
            int d = 0; for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }
    }

    /// <summary>Minimal HTTP/1.1 request reader (request line, headers, small body).</summary>
    public sealed class HttpRequest
    {
        public string Method = "", Path = "", Body = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static HttpRequest Read(Stream s)
        {
            var head = new MemoryStream();
            int matched = 0;
            while (head.Length < 8192)
            {
                int b = s.ReadByte();
                if (b < 0) return null;
                head.WriteByte((byte)b);
                matched = (b == "\r\n\r\n"[matched]) ? matched + 1 : (b == '\r' ? 1 : 0);
                if (matched == 4) break;
            }
            if (matched != 4) return null;
            var req = Parse(Encoding.ASCII.GetString(head.ToArray()));
            if (req == null) return null;
            if (req.Headers.TryGetValue("content-length", out var cl) && int.TryParse(cl, out int len) && len > 0)
            {
                if (len > 4096) return null;
                var buf = new byte[len]; int got = 0;
                while (got < len) { int n = s.Read(buf, got, len - got); if (n <= 0) break; got += n; }
                req.Body = Encoding.UTF8.GetString(buf, 0, got);
            }
            return req;
        }

        public static HttpRequest Parse(string head)
        {
            var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
            var first = lines[0].Split(' ');
            if (first.Length < 2) return null;
            var r = new HttpRequest { Method = first[0].ToUpperInvariant(), Path = first[1].Split('?')[0] };
            foreach (var l in lines.Skip(1))
            {
                int c = l.IndexOf(':');
                if (c > 0) r.Headers[l.Substring(0, c).Trim()] = l.Substring(c + 1).Trim();
            }
            return r;
        }
    }

    /// <summary>The phone remote web app (single page, no external resources; works offline on the LAN).</summary>
    static class RemotePage
    {
        public const string Manifest = @"{""name"":""TV Remote"",""short_name"":""TV Remote"",""start_url"":""/"",""display"":""standalone"",""background_color"":""#0B0D12"",""theme_color"":""#0B0D12"",""icons"":[{""src"":""/icon.svg"",""sizes"":""any"",""type"":""image/svg+xml""}]}";

        public const string Icon = @"<svg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 64 64'><defs><linearGradient id='g' x1='0' y1='0' x2='1' y2='1'><stop offset='0' stop-color='#266EF5'/><stop offset='1' stop-color='#743FE6'/></linearGradient></defs><rect width='64' height='64' rx='14' fill='url(#g)'/><rect x='12' y='16' width='40' height='26' rx='3' fill='none' stroke='#fff' stroke-width='4'/><path d='M24 50h16M32 42v8' stroke='#fff' stroke-width='4' stroke-linecap='round'/><path d='M28 23l9 6-9 6z' fill='#fff'/></svg>";

        public const string Html = @"<!doctype html><html lang=en><head><meta charset=utf-8>
<meta name=viewport content='width=device-width,initial-scale=1,maximum-scale=1,user-scalable=no,viewport-fit=cover'>
<meta name=theme-color content='#0B0D12'><meta name=mobile-web-app-capable content=yes><meta name=apple-mobile-web-app-capable content=yes>
<link rel=manifest href='/manifest.webmanifest'><link rel=icon href='/icon.svg'><link rel=apple-touch-icon href='/icon.svg'>
<title>TV Remote</title><style>
:root{color-scheme:dark;--bg:#0B0D12;--card:#161A22;--card2:#1E2430;--line:#2A3140;--fg:#EEF1F6;--fg2:#8E98A8;--acc:#3B82F6;--ok:#22C55E;--nf:#E50914}
*{box-sizing:border-box;-webkit-tap-highlight-color:transparent;-webkit-user-select:none;user-select:none}
html,body{margin:0;background:radial-gradient(120% 60% at 50% 0%,#1A2233 0%,var(--bg) 60%) fixed;color:var(--fg);font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif}
main{max-width:430px;margin:0 auto;padding:calc(env(safe-area-inset-top) + 14px) 16px calc(env(safe-area-inset-bottom) + 24px)}
header{display:flex;align-items:center;justify-content:space-between;margin-bottom:14px}
h1{font-size:20px;margin:0;font-weight:700;letter-spacing:.2px}
.pill{display:flex;align-items:center;gap:7px;background:var(--card);border:1px solid var(--line);border-radius:999px;padding:6px 12px;font-size:13px;color:var(--fg2)}
.dot{width:8px;height:8px;border-radius:50%;background:#555}.on .dot{background:var(--ok);box-shadow:0 0 8px var(--ok)}.pc .dot{background:var(--nf);box-shadow:0 0 8px var(--nf)}
.hint{font-size:12px;color:var(--fg2);margin:-6px 0 12px;min-height:16px}
.apps{display:flex;gap:10px;overflow-x:auto;padding:2px 2px 10px;scrollbar-width:none;margin:0 -2px}.apps::-webkit-scrollbar{display:none}
.app{flex:0 0 auto;min-width:104px;height:58px;border-radius:14px;border:0;color:#fff;font-weight:800;font-size:15px;padding:0 14px;box-shadow:0 6px 16px rgba(0,0,0,.35);letter-spacing:.3px}
.app.nf{font-family:Arial Black,Arial,sans-serif;letter-spacing:1px;color:var(--nf);background:#000;border:1px solid #2a0a0c}
.app small{display:block;font-size:10px;font-weight:600;opacity:.8;letter-spacing:0}
.row{display:grid;gap:10px;margin:10px 0}.r3{grid-template-columns:repeat(3,1fr)}.r2{grid-template-columns:1fr 1fr}
button{font:inherit;color:var(--fg);background:var(--card);border:1px solid var(--line);border-radius:16px;min-height:52px;display:flex;align-items:center;justify-content:center;gap:8px;touch-action:manipulation;transition:transform .06s,background .1s}
button:active,button.pressed{transform:scale(.95);background:var(--card2)}
button svg{width:22px;height:22px;fill:none;stroke:currentColor;stroke-width:2.2;stroke-linecap:round;stroke-linejoin:round}
.lbl{font-size:13px;color:var(--fg2)}
.ring{position:relative;width:min(78vw,300px);aspect-ratio:1;margin:18px auto;border-radius:50%;background:conic-gradient(from 45deg,#1B2130,#151a24,#1B2130,#151a24,#1B2130);border:1px solid var(--line);box-shadow:inset 0 2px 18px rgba(0,0,0,.6),0 10px 30px rgba(0,0,0,.4)}
.ring button{position:absolute;background:transparent;border:0;border-radius:50%;width:30%;height:30%;min-height:0}
.ring button:active,.ring button.pressed{background:rgba(59,130,246,.18)}
.ring .u{top:3%;left:35%}.ring .d{bottom:3%;left:35%}.ring .l{left:3%;top:35%}.ring .r{right:3%;top:35%}
.ring .ok{left:30%;top:30%;width:40%;height:40%;background:linear-gradient(160deg,#4B8DF8,#2563EB);border:1px solid #5B9BFF;font-weight:800;font-size:19px;box-shadow:0 6px 18px rgba(37,99,235,.45)}
.ring .ok:active,.ring .ok.pressed{background:#1D4ED8}
.round{border-radius:50%;width:62px;height:62px;min-height:0;justify-self:center}
.media{grid-template-columns:1fr 1.3fr 1fr}.media .pp{background:var(--card2);border-color:#3a4458}
.vol{display:grid;grid-template-columns:1fr auto 1fr;gap:10px;align-items:center}
.vol button{min-height:56px}.vol .mute{width:56px;border-radius:50%}
details{background:var(--card);border:1px solid var(--line);border-radius:16px;padding:10px 12px;margin-top:12px}
summary{list-style:none;cursor:pointer;color:var(--fg2);font-size:14px;display:flex;align-items:center;gap:8px}summary::-webkit-details-marker{display:none}
input{width:100%;padding:14px;border-radius:12px;border:1px solid var(--line);background:#0F131A;color:var(--fg);font-size:16px;margin:10px 0;-webkit-user-select:text;user-select:text}
#login{display:none;padding-top:12vh;text-align:center}#login input{text-align:center;font-size:28px;letter-spacing:10px}
#login .logo{width:72px;height:72px;margin:0 auto 14px;display:block}
.primary{background:linear-gradient(160deg,#4B8DF8,#2563EB);border-color:#5B9BFF;font-weight:700}
#remote{display:none}.small{font-size:12px;color:var(--fg2);text-align:center;margin-top:16px}.small a{color:var(--fg2)}
#toast{position:fixed;left:50%;bottom:calc(env(safe-area-inset-bottom) + 18px);transform:translateX(-50%) translateY(120px);background:#222a38;border:1px solid var(--line);color:var(--fg);padding:10px 16px;border-radius:999px;font-size:14px;transition:transform .25s;max-width:90vw;text-align:center;z-index:5}
#toast.show{transform:translateX(-50%) translateY(0)}
</style></head><body><main>
<section id=login><img class=logo src='/icon.svg' alt=''><h1>TV Remote</h1>
<p class=lbl>Enter the 6-digit PIN shown in Android TV Desktop &rarr; Controls &rarr; Phone remote.</p>
<input id=pin inputmode=numeric maxlength=6 placeholder='&bull;&bull;&bull;&bull;&bull;&bull;' autocomplete=off>
<button class=primary style='width:100%' onclick='login()'>Connect</button><p id=err class=lbl></p></section>

<section id=remote>
<header><h1>TV Remote</h1><div id=pill class=pill><span class=dot></span><span id=st>&hellip;</span></div></header>
<div id=hint class=hint></div>
<div id=apps class=apps></div>

<div class='row r3'>
 <button data-a=showtv><svg viewBox='0 0 24 24'><rect x='3' y='5' width='18' height='12' rx='2'/><path d='M8 21h8M12 17v4'/></svg><span>TV</span></button>
 <button data-a=fullscreen><svg viewBox='0 0 24 24'><path d='M4 9V4h5M20 9V4h-5M4 15v5h5M20 15v5h-5'/></svg><span>Full</span></button>
 <button data-a=exitfullscreen><svg viewBox='0 0 24 24'><path d='M9 4v5H4M15 4v5h5M9 20v-5H4M15 20v-5h5'/></svg><span>Esc</span></button>
</div>

<div class=ring>
 <button class=u data-k=up data-r=1 aria-label=Up><svg viewBox='0 0 24 24'><path d='M6 15l6-6 6 6'/></svg></button>
 <button class=l data-k=left data-r=1 aria-label=Left><svg viewBox='0 0 24 24'><path d='M15 6l-6 6 6 6'/></svg></button>
 <button class=ok data-k=ok>OK</button>
 <button class=r data-k=right data-r=1 aria-label=Right><svg viewBox='0 0 24 24'><path d='M9 6l6 6-6 6'/></svg></button>
 <button class=d data-k=down data-r=1 aria-label=Down><svg viewBox='0 0 24 24'><path d='M6 9l6 6 6-6'/></svg></button>
</div>

<div class='row r3'>
 <button class=round data-k=back aria-label=Back><svg viewBox='0 0 24 24'><path d='M9 14L4 9l5-5'/><path d='M4 9h10a6 6 0 010 12h-3'/></svg></button>
 <button class=round data-k=home aria-label=Home><svg viewBox='0 0 24 24'><path d='M3 11l9-8 9 8'/><path d='M5 10v10h14V10'/></svg></button>
 <button class=round data-k=menu aria-label=Menu><svg viewBox='0 0 24 24'><path d='M4 6h16M4 12h16M4 18h16'/></svg></button>
</div>

<div class='row media'>
 <button data-k=rewind data-r=1 aria-label=Rewind><svg viewBox='0 0 24 24'><path d='M11 18l-7-6 7-6zM20 18l-7-6 7-6z'/></svg></button>
 <button class=pp data-k=playpause aria-label='Play or pause'><svg viewBox='0 0 24 24'><path d='M6 5l8 7-8 7z'/><path d='M17 5v14M21 5v14'/></svg></button>
 <button data-k=forward data-r=1 aria-label='Fast forward'><svg viewBox='0 0 24 24'><path d='M13 18l7-6-7-6zM4 18l7-6-7-6z'/></svg></button>
</div>

<div class=vol>
 <button data-k=voldown data-r=1><svg viewBox='0 0 24 24'><path d='M4 9v6h4l5 4V5L8 9z'/><path d='M17 12h4'/></svg><span>Vol</span></button>
 <button class=mute data-k=mute aria-label=Mute><svg viewBox='0 0 24 24'><path d='M4 9v6h4l5 4V5L8 9z'/><path d='M17 9l4 6M21 9l-4 6'/></svg></button>
 <button data-k=volup data-r=1><svg viewBox='0 0 24 24'><path d='M4 9v6h4l5 4V5L8 9z'/><path d='M17 12h4M19 10v4'/></svg><span>Vol</span></button>
</div>

<details><summary><svg viewBox='0 0 24 24' style='width:20px;height:20px;fill:none;stroke:currentColor;stroke-width:2'><rect x='2' y='6' width='20' height='12' rx='2'/><path d='M6 10h.01M10 10h.01M14 10h.01M18 10h.01M7 14h10'/></svg>Keyboard, search &amp; settings</summary>
 <input id=txt maxlength=200 placeholder='Type text into the TV or Netflix' autocomplete=off>
 <div class='row r3' style='margin-top:0'><button onclick=sendText()>Send</button><button data-k=del data-r=1>&#9003; Del</button><button data-k=enter>Enter</button></div>
 <div class='row r2'><button data-k=search><svg viewBox='0 0 24 24'><circle cx='11' cy='11' r='7'/><path d='M20 20l-4-4'/></svg><span>Search</span></button><button data-k=settings><svg viewBox='0 0 24 24'><circle cx='12' cy='12' r='3'/><path d='M12 2v3M12 19v3M2 12h3M19 12h3M4.9 4.9l2.1 2.1M17 17l2.1 2.1M4.9 19.1L7 17M17 7l2.1-2.1'/></svg><span>Settings</span></button></div>
</details>
<p class=small>Tip: add this page to your home screen. &middot; <a href=# onclick='localStorage.removeItem(""pin"");location.reload()'>Forget PIN</a></p>
</section>
<div id=toast></div>

<script>
let pin=localStorage.getItem('pin');const $=id=>document.getElementById(id);let toastT;
function toast(t){const e=$('toast');e.textContent=t;e.classList.add('show');clearTimeout(toastT);toastT=setTimeout(()=>e.classList.remove('show'),1800)}
async function api(p,b){try{const r=await fetch('/api/'+p,{method:'POST',headers:{'X-Pin':pin||''},body:b||''});return{ok:r.ok,status:r.status,text:await r.text()}}catch(e){return{ok:false,status:0,text:'PC not reachable - same Wi-Fi?'}}}
function status(t){const p=$('pill');$('st').textContent=t;p.className='pill'+(t=='TV off'||t.indexOf('reachable')>=0?'':t.indexOf('Netflix')>=0?' pc':' on');
 $('hint').textContent=t.indexOf('Netflix')>=0?'Buttons control Netflix on the PC. Tap TV to go back to Android TV.':''}
async function show(){if(!pin){$('login').style.display='block';$('remote').style.display='none';return}
 const r=await api('ping');if(r.status==403||r.status==429){localStorage.removeItem('pin');pin=null;$('err').textContent=r.text;$('login').style.display='block';$('remote').style.display='none';return}
 $('login').style.display='none';$('remote').style.display='block';status(r.ok?r.text:r.text);loadApps()}
async function login(){pin=$('pin').value.trim();localStorage.setItem('pin',pin);show()}
$('pin').addEventListener('keydown',e=>{if(e.key=='Enter')login()});
async function loadApps(){const r=await api('apps');if(!r.ok)return;const box=$('apps');box.innerHTML='';
 r.text.split('\n').filter(x=>x).forEach(l=>{const [n,label,color]=l.split('|');const b=document.createElement('button');b.className='app'+(n=='netflix'?' nf':'');
  if(n=='netflix'){b.innerHTML='NETFLIX<small>on PC</small>'}else{b.textContent=label;b.style.background='linear-gradient(160deg,'+color+','+color+'cc)'}
  b.onclick=async()=>{vib(20);toast('Opening '+label+'...');const x=await api('launch',n);toast(x.text);setTimeout(ping,1500)};box.appendChild(b)})}
function vib(n){if(navigator.vibrate)navigator.vibrate(n)}
async function k(n){vib(10);const r=await api('key',n);if(!r.ok)toast(r.text)}
async function app(n){vib(18);const r=await api('app',n);toast(r.text);setTimeout(ping,1200)}
async function sendText(){const t=$('txt');if(!t.value)return;const r=await api('text',t.value);toast(r.ok?'Sent':r.text);if(r.ok)t.value=''}
async function ping(){if(!pin||$('remote').style.display!='block')return;const r=await api('ping');if(r.ok||r.status==0)status(r.text)}
let hold=null,rep=null,held=null;function stopHold(){clearTimeout(hold);clearInterval(rep);hold=rep=null;if(held){held.classList.remove('pressed');held=null}}
document.querySelectorAll('[data-k],[data-a]').forEach(b=>{
 b.addEventListener('pointerdown',e=>{e.preventDefault();stopHold();held=b;b.classList.add('pressed');
  if(b.dataset.a){app(b.dataset.a);return}
  k(b.dataset.k);if(b.dataset.r)hold=setTimeout(()=>{rep=setInterval(()=>k(b.dataset.k),130)},380)});
 ['pointerup','pointercancel','pointerleave'].forEach(t=>b.addEventListener(t,stopHold));
 b.addEventListener('contextmenu',e=>e.preventDefault())});
setInterval(ping,5000);document.addEventListener('visibilitychange',()=>{if(!document.hidden)ping()});
show();
</script></main></body></html>";
    }
}
