using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace AndroidTVDesktop.Core
{
    public enum TvState
    {
        Idle,            // TV mode off, nothing managed
        Starting,        // launching / waiting for the emulator process and window
        WaitingForBoot,  // window found, waiting for Android (sys.boot_completed)
        Entering,        // applying fullscreen
        Fullscreen,      // TV mode on, verified
        Windowed,        // TV mode on, emulator shown as a normal window (F11/Esc)
        Exiting,         // restoring
        Faulted          // startup failed; desktop left as it was
    }

    /// <summary>Allowed TV-mode transitions. Pure; the engine asks it before every change and logs every transition.</summary>
    public sealed class TvStateMachine
    {
        static readonly Dictionary<TvState, TvState[]> Allowed = new Dictionary<TvState, TvState[]>
        {
            [TvState.Idle] = new[] { TvState.Starting },
            [TvState.Starting] = new[] { TvState.WaitingForBoot, TvState.Entering, TvState.Windowed, TvState.Faulted, TvState.Idle },
            [TvState.WaitingForBoot] = new[] { TvState.Entering, TvState.Windowed, TvState.Starting, TvState.Faulted, TvState.Idle },
            [TvState.Entering] = new[] { TvState.Fullscreen, TvState.Windowed, TvState.Starting, TvState.Exiting, TvState.Faulted },
            [TvState.Fullscreen] = new[] { TvState.Exiting, TvState.Entering, TvState.Starting },
            [TvState.Windowed] = new[] { TvState.Entering, TvState.Starting, TvState.Idle, TvState.Exiting },
            [TvState.Exiting] = new[] { TvState.Windowed, TvState.Idle, TvState.Starting },
            [TvState.Faulted] = new[] { TvState.Idle, TvState.Starting },
        };

        public TvState State { get; private set; } = TvState.Idle;
        public event Action<TvState, TvState, string> Changed;

        public bool CanMove(TvState to) => Allowed.TryGetValue(State, out var a) && a.Contains(to);

        /// <summary>Moves to <paramref name="to"/> if allowed. Returns false (and changes nothing) otherwise.</summary>
        public bool TryMove(TvState to, string reason)
        {
            if (!CanMove(to)) { Log.Warn($"Ignored state change {State} -> {to} ({reason})"); return false; }
            var from = State;
            State = to;
            Log.Info($"STATE {from} -> {to}: {reason}");
            Changed?.Invoke(from, to, reason);
            return true;
        }

        public bool IsTvModeActive => State != TvState.Idle && State != TvState.Faulted;
        public bool IsFullscreen => State == TvState.Fullscreen;
    }

    /// <summary>What must be restored if the app dies while fullscreen. Written next to the log; read by Restore-Desktop.ps1.</summary>
    public sealed class RestoreRecord
    {
        public int ControllerPid;
        public bool Fullscreen, TaskbarHidden, Clipped, WasTopmost;
        public long Hwnd; public uint Pid;
        public RECT Rect;
        public long Style, ExStyle;
        public List<long> HiddenTools = new List<long>();

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# Android TV Desktop restore record; read by Restore-Desktop.ps1");
            sb.AppendLine($"controllerPid={ControllerPid}");
            sb.AppendLine($"fullscreen={(Fullscreen ? 1 : 0)}");
            sb.AppendLine($"taskbarHidden={(TaskbarHidden ? 1 : 0)}");
            sb.AppendLine($"hwnd={Hwnd}");
            sb.AppendLine($"pid={Pid}");
            sb.AppendLine($"left={Rect.Left}"); sb.AppendLine($"top={Rect.Top}");
            sb.AppendLine($"width={Rect.Width}"); sb.AppendLine($"height={Rect.Height}");
            sb.AppendLine($"style={Style}"); sb.AppendLine($"exstyle={ExStyle}");
            sb.AppendLine($"wasTopmost={(WasTopmost ? 1 : 0)}");
            sb.AppendLine($"clipped={(Clipped ? 1 : 0)}");
            sb.AppendLine("hiddenTools=" + string.Join(",", HiddenTools));
            return sb.ToString();
        }

        public static bool TryParse(string text, out RestoreRecord r)
        {
            r = new RestoreRecord();
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in (text ?? "").Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith("#")) continue;
                int eq = line.IndexOf('=');
                if (eq > 0) d[line.Substring(0, eq)] = line.Substring(eq + 1);
            }
            long L(string k) => d.TryGetValue(k, out var v) && long.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var x) ? x : 0;
            if (!d.ContainsKey("hwnd") || !d.ContainsKey("width")) return false;
            r.ControllerPid = (int)L("controllerPid");
            r.Fullscreen = L("fullscreen") == 1; r.TaskbarHidden = L("taskbarHidden") == 1;
            r.Hwnd = L("hwnd"); r.Pid = (uint)L("pid");
            r.Rect = new RECT((int)L("left"), (int)L("top"), (int)(L("left") + L("width")), (int)(L("top") + L("height")));
            r.Style = L("style"); r.ExStyle = L("exstyle");
            r.WasTopmost = L("wasTopmost") == 1; r.Clipped = L("clipped") == 1;
            if (d.TryGetValue("hiddenTools", out var tools))
                r.HiddenTools = tools.Split(',').Select(t => long.TryParse(t, out var v) ? v : 0).Where(v => v != 0).ToList();
            return r.Hwnd != 0 && r.Rect.Width > 0 && r.Rect.Height > 0;
        }

        public static string DefaultPath => Path.Combine(AppPaths.DataDir, "state.ini");
    }

    public static class AppPaths
    {
        public const string AppName = "Android TV Desktop";
        public static string DataDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AndroidTVDesktop");
        public static string LogDir => Path.Combine(DataDir, "logs");
        public static string Version => typeof(AppPaths).Assembly.GetName().Version.ToString(3);
    }
}
