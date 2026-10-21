using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace AndroidTVDesktop.Core
{
    /// <summary>
    /// Lets the phone remote control Netflix (PC) in Microsoft Edge. Keys are sent ONLY while the Netflix Edge window
    /// is the foreground window, so they can never land in another application.
    /// </summary>
    public static class PcControl
    {
        /// <summary>Remote button -> Windows virtual-key (with optional Alt). Netflix web player: Space = play/pause,
        /// Left/Right = seek 10 s, Up/Down = player volume, Enter = select, Alt+Left = browser back, F11 = fullscreen.</summary>
        public static readonly IReadOnlyDictionary<string, (ushort vk, bool alt)> KeyMap = new Dictionary<string, (ushort, bool)>(StringComparer.OrdinalIgnoreCase)
        {
            ["up"] = (0x26, false), ["down"] = (0x28, false), ["left"] = (0x25, false), ["right"] = (0x27, false),
            ["ok"] = (0x0D, false), ["enter"] = (0x0D, false), ["playpause"] = (0x20, false),
            ["rewind"] = (0x25, false), ["forward"] = (0x27, false),
            ["back"] = (0x25, true),                       // Alt+Left: back from the player to browsing
            ["volup"] = (0xAF, false), ["voldown"] = (0xAE, false), ["mute"] = (0xAD, false),   // Windows volume
            ["del"] = (0x08, false), ["search"] = (0xBF, false), ["menu"] = (0x1B, false),     // "/" opens search; Esc closes menus
        };

        /// <summary>True for a top-level Edge window showing Netflix (pure; unit tested).</summary>
        public static bool IsNetflixWindow(WindowInfo w) =>
            w != null && w.Visible && w.Owner == IntPtr.Zero && w.ProcessName.Equals("msedge", StringComparison.OrdinalIgnoreCase) &&
            w.Class == "Chrome_WidgetWin_1" && w.Title.IndexOf("Netflix", StringComparison.OrdinalIgnoreCase) >= 0;

        public static IntPtr FindNetflixWindow() =>
            WindowFinder.EnumerateTopLevel().Where(IsNetflixWindow).Select(w => w.Hwnd).FirstOrDefault();

        /// <summary>The Netflix window if it is the foreground window right now, else zero (cheap: no full enumeration).</summary>
        public static IntPtr ForegroundNetflix()
        {
            var fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return IntPtr.Zero;
            string pn = "";
            try { using (var p = System.Diagnostics.Process.GetProcessById((int)Native.Pid(fg))) pn = p.ProcessName; } catch { }
            var w = new WindowInfo { Hwnd = fg, Visible = Native.IsWindowVisible(fg), Owner = Native.Owner(fg), ProcessName = pn, Class = Native.Class(fg), Title = Native.Text(fg) };
            return IsNetflixWindow(w) ? fg : IntPtr.Zero;
        }

        /// <summary>Sends a remote button to Netflix (PC) if it is in front. Returns false (not handled) otherwise.</summary>
        public static bool SendKey(string name)
        {
            if (ForegroundNetflix() == IntPtr.Zero) return false;
            if (!KeyMap.TryGetValue(name, out var k)) return true;   // in Netflix mode, unmapped TV keys do nothing on the PC
            var inputs = new List<INPUT>();
            if (k.alt) inputs.Add(Key(0x12, false));
            inputs.Add(Key(k.vk, false)); inputs.Add(Key(k.vk, true));
            if (k.alt) inputs.Add(Key(0x12, true));
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            return true;
        }

        /// <summary>Types text into Netflix (PC) (e.g. its search box) if it is in front.</summary>
        public static bool SendText(string text)
        {
            if (ForegroundNetflix() == IntPtr.Zero || string.IsNullOrEmpty(text)) return false;
            var inputs = new List<INPUT>();
            foreach (char c in text)
            {
                if (char.IsControl(c)) continue;
                inputs.Add(Unicode(c, false)); inputs.Add(Unicode(c, true));
            }
            SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf(typeof(INPUT)));
            return true;
        }

        public static void PressF11() => SendInput(2, new[] { Key(0x7A, false), Key(0x7A, true) }, Marshal.SizeOf(typeof(INPUT)));

        /// <summary>Minimises the Netflix (PC) window so the Android TV is visible. Returns true if one was open.</summary>
        public static bool MinimiseNetflix()
        {
            var h = FindNetflixWindow();
            if (h == IntPtr.Zero) return false;
            Native.ShowWindow(h, 6 /*SW_MINIMIZE*/);
            Log.Info("Netflix (PC) minimised so the TV is visible.");
            return true;
        }

        // ---- SendInput
        [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }
        [StructLayout(LayoutKind.Explicit)] struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
        [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public InputUnion u; }
        [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint n, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern uint MapVirtualKey(uint code, uint mapType);

        /// <summary>
        /// Key event with BOTH the virtual-key and the hardware scan code: Edge/Chromium (and Netflix's web player, which
        /// reads KeyboardEvent.code) identify keys by scan code, so a scan code of 0 makes them ignore the key (F11 did nothing).
        /// </summary>
        static INPUT Key(ushort vk, bool up)
        {
            bool ext = vk >= 0x21 && vk <= 0x2E || vk >= 0xAD && vk <= 0xAF;   // arrows/navigation and media keys are extended
            ushort scan = (ushort)MapVirtualKey(vk, 0 /*MAPVK_VK_TO_VSC*/);
            return new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = (up ? 2u : 0u) | (ext ? 1u : 0u) } } };
        }
        static INPUT Unicode(char c, bool up) =>
            new INPUT { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wScan = c, dwFlags = 4u /*UNICODE*/ | (up ? 2u : 0u) } } };
    }
}
