using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace AndroidTVDesktop.Core
{
    public enum MouseRightButton { Normal, AndroidBack }
    public enum GuestResolution { AvdDefault, MatchMonitor, R1920x1080, R2560x1440, R3840x2160 }

    /// <summary>User settings, stored as JSON in %APPDATA%\AndroidTVDesktop\settings.json. Always validated after load.</summary>
    [DataContract]
    public sealed class AppSettings
    {
        public const int CurrentVersion = 1;

        [DataMember(Order = 0)] public int Version = CurrentVersion;
        [DataMember(Order = 1)] public string AvdName = "Television_4K";
        [DataMember(Order = 2)] public string SdkPath = "";            // empty = auto-detect
        [DataMember(Order = 3)] public bool AutoMonitor = true;         // use the primary monitor
        [DataMember(Order = 4)] public string PreferredMonitorId = "";  // MonitorInfo.Id
        [DataMember(Order = 5)] public bool StartInFullscreen = true;
        [DataMember(Order = 6)] public bool BorderlessTvMode = true;
        [DataMember(Order = 7)] public bool RememberWindowState = true;
        [DataMember(Order = 8)] public bool ShowToolbarInTvMode = false;
        [DataMember(Order = 9)] public string MouseRight = nameof(MouseRightButton.Normal);
        [DataMember(Order = 10)] public bool ConfinePointerToTv = true;
        [DataMember(Order = 11)] public bool ParkPointerOnEnter = false;
        [DataMember(Order = 12)] public string KeyToggleFullscreen = "F11";
        [DataMember(Order = 13)] public string KeyExitFullscreen = "Escape";
        [DataMember(Order = 14)] public string KeyEmergencyExit = "Ctrl+Alt+Q";
        [DataMember(Order = 15)] public string KeyAndroidBack = "Backspace";
        [DataMember(Order = 16)] public string KeyAndroidHome = "Home";
        [DataMember(Order = 17)] public string GuestDisplay = nameof(GuestResolution.AvdDefault);
        [DataMember(Order = 18)] public int GuestDensity = 0;            // 0 = automatic (scaled from AVD density)
        [DataMember(Order = 19)] public bool StartOnSignIn = false;
        [DataMember(Order = 20)] public bool LaunchTvOnAppStart = false;
        [DataMember(Order = 21)] public int StartupTimeoutSeconds = 240;
        [DataMember(Order = 22)] public int BootTimeoutSeconds = 240;
        [DataMember(Order = 23)] public bool LaunchFrameless = true;     // -skin <lcd WxH> (no device bezel)
        [DataMember(Order = 24)] public string LastNormalWindow = "";    // "x,y,w,h" of the emulator window before TV mode
        [DataMember(Order = 25)] public bool PhoneRemoteEnabled = false; // phone web remote (off by default)
        [DataMember(Order = 26)] public int PhoneRemotePort = 8765;
        [DataMember(Order = 27)] public string PhoneRemotePin = "";      // 6 digits; generated when empty
        [DataMember(Order = 28)] public bool KeepRunningInTray = true;   // window X hides to the tray (remote keeps working)

        // DataContract deserialization does not run field initializers; start from the defaults so
        // fields missing from an older/hand-edited file get sensible values instead of null/0.
        [OnDeserializing]
        void OnDeserializing(StreamingContext _)
        {
            var defaults = new AppSettings();
            foreach (var f in typeof(AppSettings).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                f.SetValue(this, f.GetValue(defaults));
        }

        public MouseRightButton MouseRightValue => Enum.TryParse(MouseRight, out MouseRightButton v) ? v : MouseRightButton.Normal;
        public GuestResolution GuestDisplayValue => Enum.TryParse(GuestDisplay, out GuestResolution v) ? v : GuestResolution.AvdDefault;

        public static readonly int[] AllowedDensities = { 0, 160, 213, 240, 320, 400, 480, 560, 640 };

        public AppSettings Clone() => FromJson(ToJson(), out _);

        /// <summary>Fixes invalid values in place and returns a description of each fix.</summary>
        public List<string> Validate()
        {
            var fixes = new List<string>();
            var d = new AppSettings();
            if (Version != CurrentVersion) { fixes.Add($"Version {Version} -> {CurrentVersion}"); Version = CurrentVersion; }
            if (string.IsNullOrWhiteSpace(AvdName) || AvdName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || AvdName.Contains(" "))
            { fixes.Add($"Invalid AVD name '{AvdName}' -> {d.AvdName}"); AvdName = d.AvdName; }
            if (SdkPath == null) SdkPath = "";
            if (SdkPath.Length > 0 && SdkPath.IndexOfAny(Path.GetInvalidPathChars()) >= 0) { fixes.Add("Invalid SDK path cleared"); SdkPath = ""; }
            if (PreferredMonitorId == null) PreferredMonitorId = "";
            if (!Enum.TryParse(MouseRight, out MouseRightButton _)) { fixes.Add($"Invalid MouseRight '{MouseRight}' -> Normal"); MouseRight = d.MouseRight; }
            if (!Enum.TryParse(GuestDisplay, out GuestResolution _)) { fixes.Add($"Invalid GuestDisplay '{GuestDisplay}' -> AvdDefault"); GuestDisplay = d.GuestDisplay; }
            if (!AllowedDensities.Contains(GuestDensity)) { fixes.Add($"Invalid GuestDensity {GuestDensity} -> 0 (auto)"); GuestDensity = 0; }
            if (StartupTimeoutSeconds < 30 || StartupTimeoutSeconds > 900) { fixes.Add($"StartupTimeoutSeconds {StartupTimeoutSeconds} -> 240"); StartupTimeoutSeconds = 240; }
            if (BootTimeoutSeconds < 30 || BootTimeoutSeconds > 900) { fixes.Add($"BootTimeoutSeconds {BootTimeoutSeconds} -> 240"); BootTimeoutSeconds = 240; }
            void Key(ref string value, string def, string name, bool allowNone)
            {
                if (allowNone && string.Equals(value, "None", StringComparison.OrdinalIgnoreCase)) { value = "None"; return; }
                if (!KeyChord.TryParse(value, out var _)) { fixes.Add($"Invalid key '{value}' for {name} -> {def}"); value = def; }
            }
            Key(ref KeyToggleFullscreen, d.KeyToggleFullscreen, "Toggle fullscreen", false);
            Key(ref KeyExitFullscreen, d.KeyExitFullscreen, "Exit fullscreen", false);
            Key(ref KeyEmergencyExit, d.KeyEmergencyExit, "Emergency exit", false);
            Key(ref KeyAndroidBack, d.KeyAndroidBack, "Android Back", true);
            Key(ref KeyAndroidHome, d.KeyAndroidHome, "Android Home", true);
            // The emergency exit must be a chord with Ctrl or Alt so it is never typed by accident.
            if (KeyChord.TryParse(KeyEmergencyExit, out var em) && !em.Ctrl && !em.Alt)
            { fixes.Add($"Emergency exit '{KeyEmergencyExit}' needs Ctrl or Alt -> Ctrl+Alt+Q"); KeyEmergencyExit = d.KeyEmergencyExit; }
            // No two actions may share a chord.
            var used = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, val) in new[] { ("Emergency exit", KeyEmergencyExit), ("Toggle fullscreen", KeyToggleFullscreen), ("Exit fullscreen", KeyExitFullscreen), ("Android Back", KeyAndroidBack), ("Android Home", KeyAndroidHome) })
            {
                if (val == "None") continue;
                var norm = KeyChord.TryParse(val, out var c) ? c.ToString() : val;
                if (used.ContainsKey(norm)) fixes.Add($"Duplicate key {norm} for {name} and {used[norm]}");
                else used[norm] = name;
            }
            if (fixes.Any(f => f.StartsWith("Duplicate key")))
            {
                KeyToggleFullscreen = d.KeyToggleFullscreen; KeyExitFullscreen = d.KeyExitFullscreen; KeyEmergencyExit = d.KeyEmergencyExit;
                KeyAndroidBack = d.KeyAndroidBack; KeyAndroidHome = d.KeyAndroidHome;
                fixes.Add("Keyboard shortcuts reset to defaults");
            }
            if (LastNormalWindow == null || (LastNormalWindow.Length > 0 && !TryParseRect(LastNormalWindow, out _))) { LastNormalWindow = ""; }
            if (PhoneRemotePort < 1024 || PhoneRemotePort > 65535 || PhoneRemotePort == 5037 || (PhoneRemotePort >= 5554 && PhoneRemotePort <= 5585))
            { fixes.Add($"Invalid phone remote port {PhoneRemotePort} -> 8765"); PhoneRemotePort = 8765; }
            if (PhoneRemotePin == null || !(PhoneRemotePin.Length == 6 && PhoneRemotePin.All(char.IsDigit)))
            { if (!string.IsNullOrEmpty(PhoneRemotePin)) fixes.Add("Invalid phone remote PIN replaced"); PhoneRemotePin = PhoneRemoteServer.NewPin(); }
            return fixes;
        }

        public static bool TryParseRect(string s, out RECT r)
        {
            r = default;
            var p = (s ?? "").Split(',');
            if (p.Length != 4) return false;
            if (!int.TryParse(p[0], out int x) || !int.TryParse(p[1], out int y) || !int.TryParse(p[2], out int w) || !int.TryParse(p[3], out int h)) return false;
            if (w < 100 || h < 100 || w > 20000 || h > 20000) return false;
            r = new RECT(x, y, x + w, y + h);
            return true;
        }

        public string ToJson()
        {
            var ser = new DataContractJsonSerializer(typeof(AppSettings));
            using (var ms = new MemoryStream())
            {
                using (var w = JsonReaderWriterFactory.CreateJsonWriter(ms, Encoding.UTF8, false, true, "  ")) ser.WriteObject(w, this);
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }

        public static AppSettings FromJson(string json, out List<string> fixes)
        {
            AppSettings s;
            try
            {
                var ser = new DataContractJsonSerializer(typeof(AppSettings));
                using (var ms = new MemoryStream(Encoding.UTF8.GetBytes(json ?? ""))) s = (AppSettings)ser.ReadObject(ms);
                if (s == null) throw new InvalidDataException("empty");
                fixes = s.Validate();
            }
            catch (Exception ex)
            {
                s = new AppSettings();
                fixes = new List<string> { "Settings file unreadable (" + ex.GetType().Name + "); defaults used" };
            }
            return s;
        }
    }

    public sealed class SettingsStore
    {
        public string FilePath { get; }
        public SettingsStore(string filePath) { FilePath = filePath; }

        public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AndroidTVDesktop", "settings.json");

        public AppSettings Load()
        {
            if (!File.Exists(FilePath)) { Log.Info("No settings file yet; using defaults."); return new AppSettings(); }
            var s = AppSettings.FromJson(File.ReadAllText(FilePath), out var fixes);
            foreach (var f in fixes) Log.Warn("Settings: " + f);
            if (fixes.Count > 0 && fixes.Any(f => f.StartsWith("Settings file unreadable")))
            {
                try { File.Copy(FilePath, FilePath + ".bad", true); Log.Warn("Unreadable settings kept as " + FilePath + ".bad"); } catch { }
            }
            return s;
        }

        /// <summary>Atomic save: write temp file then replace.</summary>
        public void Save(AppSettings s)
        {
            s.Validate();
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, s.ToJson(), new UTF8Encoding(false));
            if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
            else File.Move(tmp, FilePath);
        }
    }
}
