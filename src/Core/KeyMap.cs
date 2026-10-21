using System;
using System.Collections.Generic;
using System.Linq;

namespace AndroidTVDesktop.Core
{
    /// <summary>A key plus modifiers, e.g. "Ctrl+Alt+Q", "F11", "Backspace".</summary>
    public struct KeyChord
    {
        public int Vk; public bool Ctrl, Alt, Shift;

        static readonly Dictionary<string, int> Names = BuildNames();
        static Dictionary<string, int> BuildNames()
        {
            var d = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Escape"] = 0x1B, ["Esc"] = 0x1B, ["Backspace"] = 0x08, ["Tab"] = 0x09, ["Enter"] = 0x0D, ["Space"] = 0x20,
                ["PageUp"] = 0x21, ["PageDown"] = 0x22, ["End"] = 0x23, ["Home"] = 0x24, ["Left"] = 0x25, ["Up"] = 0x26,
                ["Right"] = 0x27, ["Down"] = 0x28, ["Insert"] = 0x2D, ["Delete"] = 0x2E, ["Pause"] = 0x13,
                ["BrowserBack"] = 0xA6, ["BrowserHome"] = 0xAC,
            };
            for (int i = 1; i <= 24; i++) d["F" + i] = 0x6F + i;
            for (char c = 'A'; c <= 'Z'; c++) d[c.ToString()] = c;
            for (char c = '0'; c <= '9'; c++) d[c.ToString()] = c;
            return d;
        }

        public static bool TryParse(string text, out KeyChord chord)
        {
            chord = default;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Split('+').Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
            if (parts.Count == 0) return false;
            foreach (var p in parts.Take(parts.Count - 1))
            {
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || p.Equals("Control", StringComparison.OrdinalIgnoreCase)) chord.Ctrl = true;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) chord.Alt = true;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) chord.Shift = true;
                else return false;
            }
            if (!Names.TryGetValue(parts.Last(), out chord.Vk)) return false;
            return true;
        }

        public override string ToString()
        {
            int vk = Vk;
            var name = Names.Where(kv => kv.Value == vk).Select(kv => kv.Key).OrderBy(k => k == "Esc" ? 1 : 0).FirstOrDefault() ?? ("VK" + Vk);
            return (Ctrl ? "Ctrl+" : "") + (Alt ? "Alt+" : "") + (Shift ? "Shift+" : "") + name;
        }

        public bool Matches(int vk, bool ctrl, bool alt, bool shift) => vk == Vk && ctrl == Ctrl && alt == Alt && shift == Shift;
    }

    public enum TvAction { None, ToggleFullscreen, ExitFullscreen, EmergencyExit, AndroidBack, AndroidHome }

    public struct KeyDecision
    {
        public bool Swallow; public TvAction Action;
        public KeyDecision(bool swallow, TvAction action) { Swallow = swallow; Action = action; }
        public override string ToString() => $"{Action}{(Swallow ? " (swallowed)" : "")}";
    }

    /// <summary>
    /// Decides what a low-level key event means. Pure logic (no Win32) so it can be unit tested.
    ///  * Emergency exit works everywhere (its chord always contains Ctrl or Alt).
    ///  * Everything else only when the emulator (or our backdrop) has focus; other apps are never touched.
    ///  * Exit-fullscreen only while fullscreen, so Esc still reaches Android (Back) otherwise.
    ///  * Keys mapped to Android Back/Home are swallowed and sent once through ADB, so the guest never
    ///    receives the key twice.
    ///  * Auto-repeat of a handled key never produces another action; its key-up is swallowed too.
    /// Alt for the emergency chord must be the LEFT Alt so AltGr (Ctrl+RightAlt) layouts are unaffected.
    /// </summary>
    public sealed class KeyFilter
    {
        readonly HashSet<int> held = new HashSet<int>();
        KeyChord toggle, exit, emergency, back, home;
        bool hasBack, hasHome;

        public KeyFilter(AppSettings s) { Configure(s); }

        public void Configure(AppSettings s)
        {
            KeyChord.TryParse(s.KeyToggleFullscreen, out toggle);
            KeyChord.TryParse(s.KeyExitFullscreen, out exit);
            KeyChord.TryParse(s.KeyEmergencyExit, out emergency);
            hasBack = KeyChord.TryParse(s.KeyAndroidBack, out back) && s.KeyAndroidBack != "None";
            hasHome = KeyChord.TryParse(s.KeyAndroidHome, out home) && s.KeyAndroidHome != "None";
            held.Clear();
        }

        public KeyDecision Decide(int vk, bool down, bool ctrl, bool leftAlt, bool shift, bool emulatorForeground, bool fullscreen, bool adbReady)
        {
            if (!down) return new KeyDecision(held.Remove(vk), TvAction.None);
            if (held.Contains(vk)) return new KeyDecision(true, TvAction.None);
            if (emergency.Matches(vk, ctrl, leftAlt, shift)) { held.Add(vk); return new KeyDecision(true, TvAction.EmergencyExit); }
            if (!emulatorForeground) return new KeyDecision(false, TvAction.None);
            if (toggle.Matches(vk, ctrl, leftAlt, shift)) { held.Add(vk); return new KeyDecision(true, TvAction.ToggleFullscreen); }
            if (fullscreen && exit.Matches(vk, ctrl, leftAlt, shift)) { held.Add(vk); return new KeyDecision(true, TvAction.ExitFullscreen); }
            if (adbReady && hasBack && back.Matches(vk, ctrl, leftAlt, shift)) { held.Add(vk); return new KeyDecision(true, TvAction.AndroidBack); }
            if (adbReady && hasHome && home.Matches(vk, ctrl, leftAlt, shift)) { held.Add(vk); return new KeyDecision(true, TvAction.AndroidHome); }
            return new KeyDecision(false, TvAction.None);
        }
    }
}
