// Dependency-free unit tests for AndroidTVDesktop.Core (no GUI, no emulator).
// Run: build\bin\AndroidTVDesktop.Tests.exe   (exit code 0 = all passed)
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using AndroidTVDesktop.Core;

static class UnitTests
{
    static int pass, fail;
    static string section = "";

    static void Check(string name, bool ok, string detail = "")
    {
        if (ok) { pass++; Console.WriteLine($"PASS  [{section}] {name}"); }
        else { fail++; Console.WriteLine($"FAIL  [{section}] {name} {detail}"); }
    }

    static int Main()
    {
        var tmp = Path.Combine(Path.GetTempPath(), "atvd-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        Log.Init(Path.Combine(tmp, "logs"), 16 * 1024, 2);
        try
        {
            Monitors(); Dpi(); Windows(); States(); Keys(); SingleInstanceTest(); Restore(); Config(); Startup(); Logging(tmp); AdbParsing(); Guest(); PhoneRemote();
        }
        catch (Exception ex) { fail++; Console.WriteLine("FAIL  test runner crashed: " + ex); }
        Console.WriteLine();
        Console.WriteLine($"Unit tests: {pass} passed, {fail} failed.");
        try { Directory.Delete(tmp, true); } catch { }
        return fail == 0 ? 0 : 1;
    }

    static MonitorInfo Mon(string id, int x, int y, int w, int h, bool primary, uint dpi = 96, int taskbar = 48) => new MonitorInfo
    {
        Id = id, GdiName = id.StartsWith(@"\\.\") ? id : @"\\.\" + id, FriendlyName = id, Bounds = new RECT(x, y, x + w, y + h),
        WorkArea = new RECT(x, y, x + w, y + h - taskbar), IsPrimary = primary, DpiX = dpi, RefreshHz = 60
    };

    // ---------------------------------------------------------------- monitors
    static void Monitors()
    {
        section = "monitor selection";
        var laptop = Mon("LAPTOP", 0, 0, 1920, 1080, true, 120);
        var ext = Mon("EXT1440", 1920, -180, 2560, 1440, false, 96);
        var list = new List<MonitorInfo> { laptop, ext };
        Check("auto picks primary (1920x1080)", MonitorSelector.Select(list, true, ext.Id, out _) == laptop);
        Check("preferred 2560x1440 picked by id", MonitorSelector.Select(list, false, "EXT1440", out _) == ext);
        Check("preferred 1920x1080 picked by id", MonitorSelector.Select(list, false, "LAPTOP", out _) == laptop);
        Check("preferred matched by GDI name", MonitorSelector.Select(list, false, @"\\.\EXT1440", out _) == ext);
        var chosen = MonitorSelector.Select(new List<MonitorInfo> { laptop }, false, "EXT1440", out var why);
        Check("disconnected preferred monitor falls back to primary", chosen == laptop && why.Contains("not connected"), why);
        Check("primary external monitor is chosen in auto mode", MonitorSelector.Select(new List<MonitorInfo> { Mon("A", 0, 0, 1920, 1080, false), Mon("B", 1920, 0, 2560, 1440, true) }, true, "", out _).Id == "B");
        Check("no monitors -> null", MonitorSelector.Select(new List<MonitorInfo>(), true, "", out _) == null);
        var np = MonitorSelector.NormalPlacement(ext);
        Check("normal placement inside work area of 2560x1440", ext.WorkArea.Contains(np) && Math.Abs(np.Width / (double)np.Height - 16.0 / 9) < 0.01, np.ToString());
        Check("label shows resolution and refresh", ext.Label.Contains("2560x1440") && ext.Label.Contains("60 Hz"), ext.Label);
        var live = MonitorService.Enumerate();
        Check("live enumeration returns at least one monitor with non-empty bounds", live.Count > 0 && live.All(m => !m.Bounds.IsEmpty), string.Join(" | ", live));
        Check("live enumeration has exactly one primary", live.Count(m => m.IsPrimary) == 1);
    }

    // ---------------------------------------------------------------- DPI / geometry
    static void Dpi()
    {
        section = "DPI geometry";
        var model = ContentModel.Fallback;
        foreach (var (name, mon) in new[] {
            ("1920x1080 primary", new RECT(0, 0, 1920, 1080)),
            ("2560x1440 right of laptop", new RECT(1920, -180, 4480, 1260)),
            ("2560x1440 left (negative x)", new RECT(-2560, 0, 0, 1440)),
            ("3840x2160 future", new RECT(0, 0, 3840, 2160)) })
        {
            var want = Geometry.Overscan(mon, model, 16.0 / 9.0, 2, 1.0);
            var painted = model.Painted(want);
            Check($"overscan picture covers {name}", painted.Contains(mon), $"want={want} painted={painted}");
            Check($"overscan crop <= 3% per side on {name}", (mon.Left - painted.Left) <= mon.Width * 0.03 && (painted.Bottom - mon.Bottom) <= mon.Height * 0.03, painted.ToString());
            var clip = Geometry.ClipFor(mon, want);
            Check($"clip region maps back to monitor on {name}", new RECT(want.Left + clip.Left, want.Top + clip.Top, want.Left + clip.Right, want.Top + clip.Bottom).Near(mon, 0));
        }
        var (x, y, w, h) = Geometry.ToDips(new RECT(0, 0, 1920, 1080), 1.25);
        Check("125% scaling: 1920x1080 px = 1536x864 DIPs", w == 1536 && h == 864 && x == 0 && y == 0, $"{w}x{h}");
        // TV frame (bezel) mode: model measured with the tv_4k skin (window 1798x1080, framed TV at (60,36)-(1730,1010))
        var framed = new ContentModel(60 / 1798.0, 36 / 1080.0, 1730 / 1798.0, 1010 / 1080.0);
        foreach (var (fname, fm) in new[] { ("1920x1080", new RECT(0, 0, 1920, 1080)), ("2560x1440 at x=1920", new RECT(1920, 0, 4480, 1440)) })
        {
            var fw = Geometry.FitInside(fm, framed, 1798.0 / 1080.0, 0, 1.0);
            var tv = framed.Painted(fw);
            Check($"frame mode: whole framed TV fits inside {fname}", fm.Contains(new RECT(tv.Left + 1, tv.Top + 1, tv.Right - 1, tv.Bottom - 1)), $"window={fw} tv={tv}");
            Check($"frame mode: TV is as large as possible on {fname} (touches two edges)", (Math.Abs(tv.Top - fm.Top) <= 2 && Math.Abs(tv.Bottom - fm.Bottom) <= 2) || (Math.Abs(tv.Left - fm.Left) <= 2 && Math.Abs(tv.Right - fm.Right) <= 2), tv.ToString());
            Check($"frame mode: TV centred on {fname}", Math.Abs((tv.Left - fm.Left) - (fm.Right - tv.Right)) <= 2 && Math.Abs((tv.Top - fm.Top) - (fm.Bottom - tv.Bottom)) <= 2, tv.ToString());
        }
        var (x2, _, w2, h2) = Geometry.ToDips(new RECT(1920, 0, 4480, 1440), 1.0);
        Check("100% scaling: 2560x1440 px = 2560x1440 DIPs", w2 == 2560 && h2 == 1440 && x2 == 1920);
        bool ok = ContentModel.TryFrom(new RECT(0, 0, 1920, 1080), new RECT(60, 39, 1858, 1080), out var m);
        Check("content model from measured 1920x1080 data", ok && Math.Abs(m.A - 0.03125) < 0.001, m.ToString());
        var pred = m.Painted(new RECT(-64, -36, 1982, 1115));
        Check("content model predicts independent measurement within 3 px", Math.Abs(pred.Left) <= 3 && Math.Abs(pred.Top - 5) <= 3 && Math.Abs(pred.Right - 1915) <= 3, pred.ToString());
        Check("content model rejects tiny measurement", !ContentModel.TryFrom(new RECT(0, 0, 1920, 1080), new RECT(0, 0, 10, 10), out _));
    }

    // ---------------------------------------------------------------- window discovery / handle changes
    static void Windows()
    {
        section = "window discovery";
        var main = new WindowInfo { Hwnd = new IntPtr(0x1000), Pid = 50, Title = "Android Emulator - Television_4K:5554", Class = "Qt653QWindowIcon", Visible = true, ProcessName = "qemu-system-x86_64", Rect = new RECT(125, 125, 1325, 800) };
        var tool = new WindowInfo { Hwnd = new IntPtr(0x2000), Pid = 50, Title = "Emulator", Visible = true, Owner = main.Hwnd, ProcessName = "qemu-system-x86_64", Rect = new RECT(1330, 150, 1398, 675) };
        var gl = new WindowInfo { Hwnd = new IntPtr(0x3000), Pid = 50, Title = "NVOGLDC invisible", Visible = false, ProcessName = "qemu-system-x86_64", Rect = new RECT(0, 0, 1440, 737) };
        var other = new WindowInfo { Hwnd = new IntPtr(0x4000), Pid = 77, Title = "Android Emulator - Television_4K:5554 - notes.txt", Visible = true, ProcessName = "notepad", Rect = new RECT(0, 0, 3000, 2000) };
        var otherAvd = new WindowInfo { Hwnd = new IntPtr(0x5000), Pid = 60, Title = "Android Emulator - Pixel_7:5556", Visible = true, ProcessName = "qemu-system-x86_64", Rect = new RECT(0, 0, 2000, 2000) };
        var all = new[] { gl, tool, other, otherAvd, main };
        Check("picks the visible unowned qemu window for the AVD", WindowFinder.PickMain(all, "Television_4K")?.Hwnd == main.Hwnd);
        Check("ignores toolbar, hidden GL windows, other processes, other AVDs", WindowFinder.PickMain(new[] { gl, tool, other, otherAvd }, "Television_4K") == null);
        Check("no window during startup -> null", WindowFinder.PickMain(new WindowInfo[0], "Television_4K") == null);
        var reopened = new WindowInfo { Hwnd = new IntPtr(0x9000), Pid = 99, Title = main.Title, Visible = true, ProcessName = "qemu-system-x86_64", Rect = main.Rect };
        Check("handle change: new window after restart is found, stale one absent", WindowFinder.PickMain(new[] { reopened, gl }, "Television_4K")?.Hwnd == reopened.Hwnd);
        Check("adb serial from title", WindowFinder.AdbSerial(main.Title) == "emulator-5554");
        Check("adb serial absent for generic title", WindowFinder.AdbSerial("Emulator") == null);
        Check("command line match -avd", EmulatorProcesses.MatchesAvd("\"C:\\sdk\\emulator.exe\" -avd Television_4K -skin 3840x2160", "Television_4K"));
        Check("command line match @avd", EmulatorProcesses.MatchesAvd("qemu-system-x86_64.exe @Television_4K", "Television_4K"));
        Check("command line does not match prefix AVD names", !EmulatorProcesses.MatchesAvd("emulator.exe -avd Television_4K_old", "Television_4K"));
        var avd = new AvdInfo { Name = "Television_4K", LcdWidth = 3840, LcdHeight = 2160 };
        Check("launch args use frameless skin at AVD panel size", EmulatorProcesses.BuildArguments(avd, true) == "-avd Television_4K -skin 3840x2160");
        Check("launch args without frameless", EmulatorProcesses.BuildArguments(avd, false) == "-avd Television_4K");
        var info = new AvdInfo(); AvdInfo.Parse("hw.lcd.width=3840\nhw.lcd.height = 2160\nhw.lcd.density=640\nhw.ramSize=2048M\nshowDeviceFrame=yes\ntag.id=google-tv", info);
        Check("AVD config parsed", info.LcdWidth == 3840 && info.LcdHeight == 2160 && info.LcdDensity == 640 && info.RamMb == 2048 && info.ShowDeviceFrame && info.Tag == "google-tv");
        // Another PC: pick its TV AVD automatically
        AvdInfo Fake(string n) => n == "Pixel_8" ? new AvdInfo { Name = n, Tag = "google_apis_playstore" } : n == "My_GTV" ? new AvdInfo { Name = n, Tag = "google-tv" } : new AvdInfo { Name = n };
        Check("auto-pick prefers a Google TV / Android TV image", AvdInfo.PickTvAvd(new[] { "Pixel_8", "My_GTV" }, n => { var a = Fake(n); a.ConfigPath = Path.GetTempFileName(); return a; }) == "My_GTV");
        Check("auto-pick falls back to an AVD named like a TV", AvdInfo.PickTvAvd(new[] { "Pixel_8", "Android_TV_1080p" }, n => new AvdInfo { Name = n }) == "Android_TV_1080p");
        Check("auto-pick: any AVD, or none", AvdInfo.PickTvAvd(new[] { "Pixel_8" }, n => new AvdInfo { Name = n }) == "Pixel_8" && AvdInfo.PickTvAvd(new string[0], n => new AvdInfo()) == null);
    }

    // ---------------------------------------------------------------- state machine
    static void States()
    {
        section = "TV state transitions";
        var sm = new TvStateMachine();
        var seen = new List<string>();
        sm.Changed += (a, b, r) => seen.Add($"{a}->{b}");
        Check("Idle -> Fullscreen directly is refused", !sm.TryMove(TvState.Fullscreen, "x") && sm.State == TvState.Idle);
        Check("normal launch path", sm.TryMove(TvState.Starting, "launch") && sm.TryMove(TvState.WaitingForBoot, "w") && sm.TryMove(TvState.Entering, "e") && sm.TryMove(TvState.Fullscreen, "ok"));
        Check("IsFullscreen / IsTvModeActive", sm.IsFullscreen && sm.IsTvModeActive);
        Check("Fullscreen -> Windowed must go through Exiting", !sm.TryMove(TvState.Windowed, "skip") && sm.TryMove(TvState.Exiting, "esc") && sm.TryMove(TvState.Windowed, "done"));
        Check("Windowed -> Entering (F11) -> Fullscreen", sm.TryMove(TvState.Entering, "f11") && sm.TryMove(TvState.Fullscreen, "ok"));
        Check("window lost while fullscreen -> Starting (re-acquire)", sm.TryMove(TvState.Starting, "lost"));
        Check("startup failure -> Faulted, then Idle", sm.TryMove(TvState.Faulted, "timeout") && !sm.IsTvModeActive && sm.TryMove(TvState.Idle, "ack"));
        Check("every transition reported once", seen.Count == 11, string.Join(",", seen));
    }

    // ---------------------------------------------------------------- keyboard
    static void Keys()
    {
        section = "keyboard";
        var s = new AppSettings();
        var k = new KeyFilter(s);
        const int F11 = 0x7A, ESC = 0x1B, Q = 0x51, BS = 0x08, HOME = 0x24, A = 0x41;
        KeyDecision D(int vk, bool down, bool ctrl = false, bool alt = false, bool fg = true, bool fs = true, bool adb = true) => k.Decide(vk, down, ctrl, alt, false, fg, fs, adb);
        var d = D(F11, true, fs: false); Check("F11 toggles when emulator focused", d.Action == TvAction.ToggleFullscreen && d.Swallow);
        Check("F11 auto-repeat ignored", D(F11, true).Action == TvAction.None && D(F11, true).Swallow);
        Check("F11 key-up swallowed", D(F11, false).Swallow);
        Check("F11 in another app untouched", !D(F11, true, fg: false).Swallow); D(F11, false, fg: false);
        d = D(ESC, true); Check("Escape exits fullscreen", d.Action == TvAction.ExitFullscreen && d.Swallow); D(ESC, false);
        d = D(ESC, true, fs: false); Check("Escape outside fullscreen reaches Android", !d.Swallow && d.Action == TvAction.None); D(ESC, false, fs: false);
        d = D(Q, true, ctrl: true, alt: true, fg: false, fs: false); Check("Ctrl+Alt+Q works from any app", d.Action == TvAction.EmergencyExit && d.Swallow); D(Q, false);
        Check("Ctrl+Q alone is not emergency", D(Q, true, ctrl: true).Action == TvAction.None); D(Q, false, ctrl: true);
        d = D(BS, true); Check("Backspace -> Android Back via ADB (swallowed, no duplicate)", d.Action == TvAction.AndroidBack && d.Swallow); D(BS, false);
        d = D(BS, true, adb: false); Check("Backspace passes through when ADB is not ready", !d.Swallow); D(BS, false, adb: false);
        d = D(HOME, true); Check("Home -> Android Home", d.Action == TvAction.AndroidHome); D(HOME, false);
        Check("letters pass through for text input", !D(A, true).Swallow); D(A, false);
        Check("arrow/Enter/Tab/Space pass through to the emulator", new[] { 0x25, 0x26, 0x27, 0x28, 0x0D, 0x09, 0x20 }.All(v => { var x = !D(v, true).Swallow; D(v, false); return x; }));
        s.KeyAndroidBack = "None"; k.Configure(s);
        Check("Back mapping can be disabled", !D(BS, true).Swallow); D(BS, false);
        s.KeyToggleFullscreen = "Ctrl+Shift+F"; k.Configure(s);
        Check("custom toggle chord", k.Decide(0x46, true, true, false, true, true, false, true).Action == TvAction.ToggleFullscreen);
        Check("chord parse/format round-trip", KeyChord.TryParse("ctrl+alt+q", out var c) && c.ToString() == "Ctrl+Alt+Q");
        Check("invalid chord rejected", !KeyChord.TryParse("Ctrl+Banana", out _) && !KeyChord.TryParse("", out _));
    }

    // ---------------------------------------------------------------- duplicate prevention
    static void SingleInstanceTest()
    {
        section = "single instance";
        string name = "ATVDTest" + Guid.NewGuid().ToString("N");
        string received = null;
        var gate = new System.Threading.ManualResetEventSlim();
        using (var first = new SingleInstance(name, cmd => { received = cmd; gate.Set(); }))
        {
            Check("first instance owns the app", first.IsFirst);
            var t = new System.Threading.Thread(() =>
            {
                using (var second = new SingleInstance(name, null))
                {
                    Check("second instance is refused", !second.IsFirst);
                    second.SendToFirst("--tv-mode");
                }
            });
            t.Start(); t.Join();
            Check("second instance's command reaches the first", gate.Wait(3000) && received == "--tv-mode", received ?? "(none)");
        }
        using (var again = new SingleInstance(name, null)) Check("instance slot is free after exit", again.IsFirst);
    }

    // ---------------------------------------------------------------- restore record
    static void Restore()
    {
        section = "cleanup / restore";
        var rec = new RestoreRecord { ControllerPid = 42, Fullscreen = true, TaskbarHidden = true, Hwnd = 0x110F94, Pid = 35244, Rect = new RECT(125, 125, 1325, 800), Style = 0x96000000, ExStyle = 0, WasTopmost = false, Clipped = true, HiddenTools = new List<long> { 0x7F0294 } };
        Check("restore record round-trip", RestoreRecord.TryParse(rec.Serialize(), out var r) && r.Hwnd == rec.Hwnd && r.Rect.Near(rec.Rect, 0) && r.Style == rec.Style && r.TaskbarHidden && r.Clipped && r.HiddenTools.Single() == 0x7F0294);
        Check("garbage restore record rejected", !RestoreRecord.TryParse("hello", out _) && !RestoreRecord.TryParse("hwnd=0\nwidth=0", out _));
        var done = DesktopRestore.Run("Television_4K_does_not_exist");
        Check("desktop restore is safe to run when nothing is wrong", done.Count >= 1, string.Join(" | ", done));
    }

    // ---------------------------------------------------------------- configuration
    static void Config()
    {
        section = "configuration";
        var s = AppSettings.FromJson("{\"AvdName\":\"bad name/..\",\"GuestDensity\":123,\"StartupTimeoutSeconds\":5,\"MouseRight\":\"Explode\",\"GuestDisplay\":\"8K\",\"KeyEmergencyExit\":\"Q\"}", out var fixes);
        Check("invalid values replaced by defaults", s.AvdName == "Television_4K" && s.GuestDensity == 0 && s.StartupTimeoutSeconds == 240 && s.MouseRight == "Normal" && s.GuestDisplay == "AvdDefault", string.Join("; ", fixes));
        Check("emergency exit without Ctrl/Alt rejected", s.KeyEmergencyExit == "Ctrl+Alt+Q");
        Check("each fix is reported", fixes.Count >= 6, fixes.Count.ToString());
        var missing = AppSettings.FromJson("{\"AvdName\":\"Television_4K\"}", out _);
        Check("missing fields get defaults (not null/0)", missing.KeyToggleFullscreen == "F11" && missing.BootTimeoutSeconds == 240 && missing.StartInFullscreen && missing.AutoMonitor);
        var broken = AppSettings.FromJson("{ this is not json", out var f2);
        Check("unreadable JSON -> defaults", broken.AvdName == "Television_4K" && f2.Any(x => x.Contains("unreadable")));
        var dup = AppSettings.FromJson("{\"KeyAndroidBack\":\"F11\"}", out var f3);
        Check("duplicate shortcuts reset", dup.KeyAndroidBack == "Backspace" && f3.Any(x => x.StartsWith("Duplicate")), string.Join("; ", f3));
        var path = Path.Combine(Path.GetTempPath(), "atvd-settings-" + Guid.NewGuid().ToString("N") + ".json");
        var store = new SettingsStore(path);
        var a = new AppSettings { PreferredMonitorId = @"\\?\DISPLAY#GSM5B9F#5&1", AutoMonitor = false, GuestDisplay = "R2560x1440" };
        store.Save(a); store.Save(a);
        var b = store.Load();
        Check("settings persist across restarts (atomic save)", b.PreferredMonitorId == a.PreferredMonitorId && !b.AutoMonitor && b.GuestDisplayValue == GuestResolution.R2560x1440);
        File.Delete(path);
        Check("rect parse", AppSettings.TryParseRect("125,125,1200,675", out var rr) && rr.Width == 1200 && !AppSettings.TryParseRect("1,2,3", out _) && !AppSettings.TryParseRect("0,0,5,5", out _));
    }

    // ---------------------------------------------------------------- startup timeouts
    static void Startup()
    {
        section = "startup timeout";
        var p = new StartupPolicy(240, 240);
        Check("keeps waiting at 30 s", p.CheckWindow(TimeSpan.FromSeconds(30), true, true, TimeSpan.FromSeconds(30)) == StartupPolicy.Verdict.KeepWaiting);
        Check("window timeout after 240 s", p.CheckWindow(TimeSpan.FromSeconds(241), true, true, TimeSpan.FromSeconds(241)) == StartupPolicy.Verdict.WindowTimedOut);
        Check("emulator exit detected early", p.CheckWindow(TimeSpan.FromSeconds(10), false, true, TimeSpan.FromSeconds(10)) == StartupPolicy.Verdict.EmulatorExited);
        Check("no false exit in the first seconds", p.CheckWindow(TimeSpan.FromSeconds(3), false, true, TimeSpan.FromSeconds(3)) == StartupPolicy.Verdict.KeepWaiting);
        Check("reused emulator never reported as exited", p.CheckWindow(TimeSpan.FromSeconds(20), false, false, TimeSpan.FromSeconds(20)) == StartupPolicy.Verdict.KeepWaiting);
        Check("boot timeout", p.CheckBoot(TimeSpan.FromSeconds(300)) == StartupPolicy.Verdict.BootTimedOut && p.CheckBoot(TimeSpan.FromSeconds(60)) == StartupPolicy.Verdict.KeepWaiting);
    }

    // ---------------------------------------------------------------- logging / export
    static void Logging(string tmp)
    {
        section = "logging";
        for (int i = 0; i < 600; i++) Log.Info($"line {i} " + new string('x', 80));
        var files = Log.AllLogFiles().ToList();
        Check("log rotates by size and keeps a bounded number of files", files.Count >= 2 && files.Count <= 3 && files.All(f => new FileInfo(f).Length < 20 * 1024), string.Join(",", files.Select(f => Path.GetFileName(f) + ":" + new FileInfo(f).Length)));
        Log.Info("same"); Log.Info("same"); Log.Info("same"); Log.Info("different");
        var text = File.ReadAllText(Log.FilePath);
        Check("identical lines collapsed", text.Contains("repeated 2 more times"));
        var zip = Diagnostics.Export("report body", new AppSettings().ToJson(), Path.Combine(tmp, "export"));
        using (var z = ZipFile.OpenRead(zip))
            Check("diagnostic export contains report, settings and logs", z.Entries.Any(e => e.FullName == "report.txt") && z.Entries.Any(e => e.FullName == "settings.json") && z.Entries.Any(e => e.FullName.StartsWith("logs/")));
    }

    static void AdbParsing()
    {
        section = "ADB parsing";
        var (w, h, ow, oh) = AdbClient.ParseWmSize("Physical size: 3840x2160\r\nOverride size: 1920x1080\r\n");
        Check("wm size parse", w == 3840 && h == 2160 && ow == 1920 && oh == 1080);
        var (pd, od) = AdbClient.ParseWmDensity("Physical density: 640\nOverride density: 320");
        Check("wm density parse", pd == 640 && od == 320);
        Check("wm size without override", AdbClient.ParseWmSize("Physical size: 3840x2160").ow == 0);
    }

    static void PhoneRemote()
    {
        section = "phone remote";
        var g = new PinGuard("123456"); var t0 = DateTime.UtcNow;
        Check("correct PIN accepted", g.Check("1.2.3.4", "123456", t0) == PinGuard.Result.Ok);
        Check("wrong / missing PIN rejected", g.Check("1.2.3.4", "000000", t0) == PinGuard.Result.Wrong && g.Check("1.2.3.4", null, t0) == PinGuard.Result.Wrong);
        var g2 = new PinGuard("123456"); PinGuard.Result last = PinGuard.Result.Ok;
        for (int i = 0; i < 8; i++) last = g2.Check("5.5.5.5", "111111", t0);
        Check("8 wrong PINs lock the client out, even with the right PIN", last == PinGuard.Result.LockedOut && g2.Check("5.5.5.5", "123456", t0.AddSeconds(30)) == PinGuard.Result.LockedOut);
        Check("lockout is per client and expires", g2.Check("6.6.6.6", "123456", t0) == PinGuard.Result.Ok && g2.Check("5.5.5.5", "123456", t0.AddMinutes(3)) == PinGuard.Result.Ok);
        Check("empty server PIN never matches", new PinGuard("").Check("x", "", t0) != PinGuard.Result.Ok);
        var pin = PhoneRemoteServer.NewPin();
        Check("generated PIN is 6 digits", pin.Length == 6 && pin.All(char.IsDigit), pin);

        var req = HttpRequest.Parse("POST /api/key?x=1 HTTP/1.1\r\nHost: tv\r\nX-Pin: 123456\r\nContent-Length: 2\r\n\r\n");
        Check("HTTP request line and headers parsed", req.Method == "POST" && req.Path == "/api/key" && req.Headers["x-pin"] == "123456");
        using (var ms = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("POST /api/key HTTP/1.1\r\nContent-Length: 2\r\n\r\nup")))
            Check("HTTP body read", HttpRequest.Read(ms)?.Body == "up");
        using (var ms = new MemoryStream(System.Text.Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: x")))
            Check("truncated request rejected", HttpRequest.Read(ms) == null);

        var srv = new PhoneRemoteServer(8765, "123456", "NoSuchAvd_UnitTest", "adb.exe");
        HttpRequest R(string m, string path, string pinHdr, string body) { var r = new HttpRequest { Method = m, Path = path, Body = body ?? "" }; if (pinHdr != null) r.Headers["X-Pin"] = pinHdr; return r; }
        Check("remote page served without PIN", srv.Route(R("GET", "/", null, null), "c").status == 200);
        Check("API without PIN -> 403", srv.Route(R("POST", "/api/key", null, "up"), "c").status == 403);
        Check("API via GET refused", srv.Route(R("GET", "/api/key", "123456", "up"), "c").status == 405);
        Check("unknown key -> 400", srv.Route(R("POST", "/api/key", "123456", "reboot"), "c").status == 400);
        Check("known key with TV off -> 503 (nothing sent)", srv.Route(R("POST", "/api/key", "123456", "up"), "c").status == 503);
        Check("ping reports TV off", srv.Route(R("POST", "/api/ping", "123456", ""), "c").body == "TV off");
        Check("app action without handler -> 503", srv.Route(R("POST", "/api/app", "123456", "fullscreen"), "c").status == 503);
        string asked = null; srv.AppAction = n => { asked = n; return "Fullscreen"; };
        // Netflix (PC) launcher helpers
        var args = NetflixLauncher.Arguments(new RECT(1920, 0, 4480, 1440));
        Check("Netflix opens netflix.com as an Edge app window on the TV monitor", args.Contains("--app=https://www.netflix.com/browse") && args.Contains("--window-position=1920,0"), args);
        var seen = new System.Collections.Generic.HashSet<IntPtr> { new IntPtr(1) };
        var edgeNew = new WindowInfo { Hwnd = new IntPtr(2), Visible = true, ProcessName = "msedge", Class = "Chrome_WidgetWin_1", Rect = new RECT(0, 0, 960, 540) };
        Check("new Edge window detected", NetflixLauncher.IsNewEdgeWindow(edgeNew, seen));
        Check("existing Edge window ignored", !NetflixLauncher.IsNewEdgeWindow(new WindowInfo { Hwnd = new IntPtr(1), Visible = true, ProcessName = "msedge", Class = "Chrome_WidgetWin_1", Rect = new RECT(0, 0, 960, 540) }, seen));
        Check("Edge helper/popup windows ignored", !NetflixLauncher.IsNewEdgeWindow(new WindowInfo { Hwnd = new IntPtr(3), Visible = true, ProcessName = "msedge", Class = "Chrome_WidgetWin_1", Rect = new RECT(0, 0, 100, 50) }, seen) &&
                                                 !NetflixLauncher.IsNewEdgeWindow(new WindowInfo { Hwnd = new IntPtr(4), Visible = true, ProcessName = "chrome", Class = "Chrome_WidgetWin_1", Rect = new RECT(0, 0, 960, 540) }, seen));
        Check("Edge is found on this PC", NetflixLauncher.FindEdge() != null, NetflixLauncher.FindEdge() ?? "not found");

        var fa = srv.Route(R("POST", "/api/app", "123456", "fullscreen"), "c");
        Check("Fullscreen button reaches the app", fa.status == 200 && fa.body == "Fullscreen" && asked == "fullscreen");
        Check("Esc button reaches the app", srv.Route(R("POST", "/api/app", "123456", "exitfullscreen"), "c").status == 200 && asked == "exitfullscreen");
        Check("unknown app action refused", srv.Route(R("POST", "/api/app", "123456", "shutdown"), "c").status == 400 && asked == "exitfullscreen");
        Check("app actions need the PIN", srv.Route(R("POST", "/api/app", "999999", "fullscreen"), "d").status == 403);

        // App tiles, launching and Netflix (PC) control
        var appsList = srv.Route(R("POST", "/api/apps", "123456", ""), "c");
        Check("app list always offers Netflix (PC) first", appsList.status == 200 && appsList.body.StartsWith("netflix|Netflix|"));
        Check("Netflix tile opens Netflix (PC) through the app", srv.Route(R("POST", "/api/launch", "123456", "netflix"), "c").status == 200 && asked == "netflix");
        Check("unknown app refused", srv.Route(R("POST", "/api/launch", "123456", "evil.app"), "c").status == 400);
        Check("TV app with TV off -> 503", srv.Route(R("POST", "/api/launch", "123456", "crunchyroll"), "c").status == 503);
        Check("Crunchyroll is in the allow-list", PhoneRemoteServer.TvApps.Any(t => t.Name == "crunchyroll" && t.Package == "com.crunchyroll.crunchyroid"));
        Check("TV button is an app action", PhoneRemoteServer.AppActions.Contains("showtv") && PhoneRemoteServer.AppActions.Contains("netflix"));
        string pcKey = null; srv.PcKey = n => { pcKey = n; return true; };
        Check("keys go to Netflix (PC) when it is in front", srv.Route(R("POST", "/api/key", "123456", "playpause"), "c").status == 200 && pcKey == "playpause");
        srv.PcKey = n => false;
        Check("keys go to the TV otherwise", srv.Route(R("POST", "/api/key", "123456", "up"), "c").status == 503);
        // Background / tray start
        Check("sign-in 'background' starts hidden with the remote", Shortcuts.ArgsFor(Shortcuts.SignIn.Background) == "--background");
        Check("sign-in 'TV' starts the TV after a delay", Shortcuts.ArgsFor(Shortcuts.SignIn.Tv).Contains("--tv-mode") && Shortcuts.ArgsFor(Shortcuts.SignIn.Nothing) == null);
        Check("keep running in the tray is on by default (also for old settings files)", new AppSettings().KeepRunningInTray && AppSettings.FromJson("{\"AvdName\":\"Television_4K\"}", out _).KeepRunningInTray);
        string pcText = null; srv.PcText = t => { pcText = t; return true; };
        Check("typing goes to Netflix (PC) when it is in front", srv.Route(R("POST", "/api/text", "123456", "Stranger Things"), "c").status == 200 && pcText == "Stranger Things");
        srv.PcTarget = () => "Netflix (PC)";
        Check("status shows Netflix (PC) as the target", srv.Route(R("POST", "/api/ping", "123456", ""), "c").body == "Netflix (PC)");
        srv.PcTarget = () => null;
        Check("Netflix window detection", PcControl.IsNetflixWindow(new WindowInfo { Visible = true, ProcessName = "msedge", Class = "Chrome_WidgetWin_1", Title = "Netflix" }) &&
                                         !PcControl.IsNetflixWindow(new WindowInfo { Visible = true, ProcessName = "msedge", Class = "Chrome_WidgetWin_1", Title = "Inbox - Outlook" }) &&
                                         !PcControl.IsNetflixWindow(new WindowInfo { Visible = true, ProcessName = "chrome", Class = "Chrome_WidgetWin_1", Title = "Netflix" }));
        Check("PC key map: play/pause = Space, back = Alt+Left, volume = Windows volume keys", PcControl.KeyMap["playpause"].vk == 0x20 && PcControl.KeyMap["back"].alt && PcControl.KeyMap["volup"].vk == 0xAF);
        Check("PC keys are not sent when Netflix is not in front", !PcControl.SendKey("playpause") || PcControl.ForegroundNetflix() != IntPtr.Zero);
        var page = srv.Route(R("GET", "/", null, null), "c").body;
        Check("new remote page: app tiles, round D-pad, TV button, installable", page.Contains("id=apps") && page.Contains("class=ring") && page.Contains("data-a=showtv") && page.Contains("manifest.webmanifest"));
        Check("manifest and icon served", srv.Route(R("GET", "/manifest.webmanifest", null, null), "c").body.Contains("\"display\":\"standalone\"") && srv.Route(R("GET", "/icon.svg", null, null), "c").body.StartsWith("<svg"));
        Check("remote page has Fullscreen and Esc buttons and press-to-fire", srv.Route(R("GET", "/", null, null), "c").body.Contains("data-a=fullscreen") && srv.Route(R("GET", "/", null, null), "c").body.Contains("data-a=exitfullscreen") && srv.Route(R("GET", "/", null, null), "c").body.Contains("pointerdown"));
        Check("only safe Android key codes are mapped", PhoneRemoteServer.Keys.Values.All(v => v.StartsWith("KEYCODE_")) && !PhoneRemoteServer.Keys.ContainsKey("power"));
        Check("text: spaces -> %s", PhoneRemoteServer.AndroidTextArg("hello world") == "hello%sworld");
        Check("text: shell metacharacters escaped", PhoneRemoteServer.AndroidTextArg("a;b&c|d$e`f'g\"h") == "a\\;b\\&c\\|d\\$e\\`f\\'g\\\"h");
        Check("text: non-ASCII / control / too long rejected", PhoneRemoteServer.AndroidTextArg("नमस्") == null && PhoneRemoteServer.AndroidTextArg("a\nb") == null && PhoneRemoteServer.AndroidTextArg(new string('a', 201)) == null);
        var s = AppSettings.FromJson("{\"PhoneRemotePort\":80,\"PhoneRemotePin\":\"12ab\"}", out var fixes);
        Check("invalid port/PIN corrected", s.PhoneRemotePort == 8765 && s.PhoneRemotePin.Length == 6 && s.PhoneRemotePin.All(char.IsDigit) && !s.PhoneRemoteEnabled, string.Join("; ", fixes));
        var keep = AppSettings.FromJson("{\"PhoneRemotePin\":\"482913\"}", out _);
        Check("valid PIN is kept across loads", keep.PhoneRemotePin == "482913");
    }

    static void Guest()
    {
        section = "guest display";
        var m1080 = Mon("A", 0, 0, 1920, 1080, true); var m1440 = Mon("B", 0, 0, 2560, 1440, false); var m1200 = Mon("C", 0, 0, 1920, 1200, false);
        Check("match monitor 1080p", Geometry.GuestSize(GuestResolution.MatchMonitor, m1080, 3840, 2160) == (1920, 1080));
        Check("match monitor 1440p", Geometry.GuestSize(GuestResolution.MatchMonitor, m1440, 3840, 2160) == (2560, 1440));
        Check("16:10 monitor -> 16:9 fit", Geometry.GuestSize(GuestResolution.MatchMonitor, m1200, 3840, 2160) == (1920, 1080));
        Check("never larger than AVD panel", Geometry.GuestSize(GuestResolution.R3840x2160, m1080, 1920, 1080) == (1920, 1080));
        Check("AVD default unchanged", Geometry.GuestSize(GuestResolution.AvdDefault, m1080, 3840, 2160) == (3840, 2160));
        Check("density scales with height (4K@640 -> 1080p@320)", Geometry.GuestDensity(0, 640, 2160, 1080) == 320 && Geometry.GuestDensity(0, 640, 2160, 1440) == 427);
        Check("explicit density kept", Geometry.GuestDensity(240, 640, 2160, 1080) == 240);
    }
}
