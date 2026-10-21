# TRD — Android TV Desktop (technical design)

## Technology choice

**C# + WPF on .NET Framework 4.8.1, compiled with Roslyn `csc` by `build.ps1`.**

- .NET Framework 4.8.1 ships with Windows 11, so there is no runtime to install. The app uses about 115 MB of
  RAM, compared with roughly 300 MB or more for Electron or a WebView app on an 8 GB laptop.
- WPF gives a modern dark UI with DPI scaling. Win32 interop (P/Invoke) is direct.
- This PC has no .NET SDK, and Visual Studio 18 Insiders' MSBuild hangs, so `build.ps1` calls the Roslyn
  compiler directly against the reference assemblies. The UI is built in C# with a loose-XAML theme parsed at
  runtime (`Theme.cs`), so no XAML/BAML compilation step is needed. No NuGet packages or downloads are used.
- Unit tests are a dependency-free console runner (`tests\UnitTests.cs`).

## Layout

```
src\Core\   AndroidTVDesktop.Core.dll   engine + services (no UI)
  Native.cs      Win32 interop: windows, regions, hooks, monitors, DPI, DisplayConfig (HDR, names, refresh)
  Monitors.cs    MonitorService.Enumerate, MonitorSelector (auto / preferred / fallback)
  Geometry.cs    ContentModel, Overscan, clip, DIP conversion, guest size/density; WindowFinder
  KeyMap.cs      KeyChord parsing, KeyFilter (pure decision logic)
  TvState.cs     TvStateMachine, RestoreRecord, AppPaths
  Emulator.cs    SdkInfo, AvdInfo (read-only), EmulatorProcesses, StartupPolicy, AdbClient
  TvEngine.cs    TV mode lifecycle, fullscreen, verification, monitoring, hooks, restore
  Services.cs    Diagnostics, SingleInstance, Shortcuts, DesktopRestore
  Settings.cs    AppSettings (validated JSON), SettingsStore (atomic save)
  Log.cs         rotating log
src\App\    AndroidTVDesktop.exe (WPF): App.cs, MainWindow.cs, Pages.cs, Ui.cs, Theme.cs, app.manifest
tests\      UnitTests.cs (99 tests); gui\Check-TvScreen.ps1 (read-only on-screen check; the live harness is not published)
```

## Diagnosis of the old launcher (why fullscreen "did not stick")

Measured on this PC with `Diagnose-TV-Window.ps1`, screen captures and pixel scans:

1. **Crash right after fullscreen.** v2 and v3 imported `GetConsoleWindow` from `user32.dll`, but it is in
   `kernel32.dll`. The resulting `EntryPointNotFoundException` ran the `finally` restore about 0.5 s later.
   v3's log shows the exception.
2. **The logged rectangle was not the visible picture.** With the AVD's `tv_4k` skin, Qt snaps the window back
   to the skin aspect ratio (1920×1080 → 1798×1080) about 500 ms after an external resize. The old check ran
   at 180 ms.
3. **Qt paints the picture inset.** The Qt main window is `WS_POPUP` (there is no native frame to remove), but
   the device picture is drawn at about (W/32, H/28) inside a transparent margin. Qt also sets its own window
   region (mask). A window exactly the size of the monitor therefore still shows the desktop around the
   picture.
4. **The toolbar** is a separate window *owned* by the main window, so it is always drawn above it.
5. **The wrong window could be picked.** qemu owns about 60 hidden GL/IME windows, and
   `Process.MainWindowHandle` is not a reliable way to find the main one. The handle is stable while the
   emulator runs and changes only when the emulator restarts. No duplicate launchers or hook interference
   were found.

## Fullscreen algorithm (`TvEngine.EnterFullscreenAsync`)

1. Select the monitor with `MonitorSelector`: automatic means the primary monitor; otherwise the stable
   device path from DisplayConfig, then the GDI name, then the primary as a fallback.
2. Save the restore data: rect, styles and topmost state. Strip `WS_CAPTION`/`WS_THICKFRAME` if present and
   log every `SetWindowLongPtr` result. Hide the owned toolbar. Write `state.ini`.
3. If most of the window is on another monitor, move it onto the target at normal size first, so Qt adopts
   that monitor's DPI.
4. Show a **backdrop**: a borderless, non-activating, topmost window with colour `#030104` (almost black,
   never painted exactly by Android). Place the emulator `HWND_TOPMOST` exactly on the monitor.
5. **Measure** the painted picture by screen capture: the first and last non-backdrop pixels along three
   rows and three columns. This gives fractions A, B, C, D of the window rect. If measuring fails, the
   built-in model measured on this PC is used.
6. **Overscan**: size the window (keeping its aspect ratio) so the painted picture covers the monitor plus
   2 px, centred. That's about 1 % crop per edge.
7. **Clip** the window to the monitor with `SetWindowRgn`, so nothing spills onto other monitors. Qt
   replaces the region with its own mask about 400 ms after a resize, so the engine waits and re-applies the
   clip before declaring success, and re-checks it every 500 ms.
8. **Verify**: (a) no backdrop-coloured pixels on the monitor border or a 9×9 grid; (b) `WindowFromPoint` at
   25 grid points and the taskbar centre must hit the emulator or the backdrop. If the taskbar stays on top,
   the engine retries foreground and z-order, and only as a last resort hides the taskbar (always restored).
   It then logs `FULLSCREEN VERIFIED`.
9. **Monitor** every 500 ms, and only while TV mode is active:
   - re-apply the placement after drift or lost topmost (at most 5 times a minute, then leave fullscreen);
   - re-apply the clip and re-hide the toolbar;
   - re-check z-order every 5 s;
   - debounce display changes (1.5 s), then re-enter;
   - detect stale handles, then re-acquire the window and re-enter fullscreen.

Restoring reverses steps 2–7: styles, region, rect and z-order, toolbar, backdrop, pointer clip and taskbar.

## Input

- `WH_KEYBOARD_LL` is installed only while TV mode is active. `KeyFilter` decides per event:
  - The emergency chord works from any app.
  - Every other mapping applies only while the emulator or backdrop is in the foreground.
  - Esc is handled only while fullscreen.
  - Auto-repeat and the matching key-up are swallowed.
  - Back and Home are swallowed and sent once through a persistent `adb shell` (`input keyevent`), so there
    are no duplicates.
  - Left Alt only, so AltGr is unaffected.
- `WH_MOUSE_LL` is installed only while fullscreen with right-click set to Back. Every other mouse event goes
  to the emulator unchanged.
- `ClipCursor` keeps the pointer on the TV monitor while the TV has focus. It is released on focus loss and
  on exit.

## Emulator and ADB

- Launch: `emulator.exe -avd <name> -skin <hw.lcd.width>x<hw.lcd.height>` with a hidden console. This is a
  frameless skin at the AVD's own resolution and applies to that launch only.
- Readiness: wait for the window (default 240 s), then `getprop sys.boot_completed` (default 240 s).
  `StartupPolicy` detects an emulator that exits early.
- Guest display: `wm size WxH` / `wm density D`. Resolutions are limited to 16:9 and never exceed the AVD
  panel. Density keeps the UI the same size (4K@640 becomes 1080p@320). A marker file means *AVD default*
  removes only an override this app applied. The current guest values are read back and shown.
- Close and restart: `WM_CLOSE` (the same as the X button, so the emulator saves its Quick Boot snapshot).
  The emulator is never killed.

## Robustness

- Single instance (named mutex plus event). A second launch hands its arguments over (`--tv-mode`,
  `--exit-tv`).
- Restoration runs on: F11/Esc, Ctrl+Alt+Q, Exit TV, window close, unhandled exceptions, `SessionEnding`,
  next start after a crash (`state.ini`), `--restore-desktop`, and `Restore-Desktop.ps1`.
- Everything that blocks has a timeout; the UI never waits synchronously on ADB or the emulator.
- Logs rotate at 1 MB with 3 old files kept, and identical lines are collapsed.

## Command line

| Argument | Effect |
|---|---|
| `--tv-mode` | Launch TV on start (or tell the running instance to) |
| `--startup-delay N` | Wait N seconds first (used by the sign-in shortcut) |
| `--exit-tv` | Tell the running instance to leave TV mode |
| `--restore-desktop` | Ask the running instance to leave TV mode, then run the desktop restore, then exit |
| `--settings <file>` | Use another settings file (tests) |
