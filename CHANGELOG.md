# Changelog

## 1.0.0 — 2026-10-20

First release of **Android TV Desktop**, a WPF desktop app that replaces the script launchers.

- Launch TV: starts or reuses the `Television_4K` emulator without the TV bezel, waits for Android
  (`sys.boot_completed`), then enters a borderless fullscreen that is verified on screen.
- Monitor selection: automatic (primary) or a preferred monitor by stable device path. Fullscreen uses that
  monitor's physical bounds and DPI. Display changes trigger a re-placement.
- Display info: refresh rate, scaling and HDR capability (read-only, through the DisplayConfig API).
- Guest display: an optional Android resolution and density through `wm size` / `wm density`. It is
  reversible and leaves the AVD files untouched.
- Keyboard: F11, Esc, Ctrl+Left Alt+Q (emergency exit), and Backspace/Home sent once as Android Back/Home over
  ADB. All shortcuts are configurable and validated.
- Mouse: right-click can be mapped to Android Back; the pointer is confined to the TV monitor while the TV
  has focus.
- Recovery: a restore record, restoration on every exit path (keys, close, crash on next start, sign-out),
  `Restore-Desktop.bat`, and a Restore button in the app.
- Pages: Home, Display, Controls, System (AVD, SDK, emulator close/restart, network and ADB checks, sign-in
  start, logs) and Diagnostics (report, live log, zip export), plus About.
- Rotating log (1 MB × 4 files), validated JSON settings with atomic save, and single-instance handover.
- Per-user installer and uninstaller with Desktop and Start Menu shortcuts. No admin rights, no registry.
- 99 unit tests and a live GUI acceptance suite (A1–A14 pass).
- Fixed before release: Ctrl+Alt+Q and Exit TV no longer leave a stale restore record behind, which would
  have moved the emulator window on the next start.

### Added 2026-10-21 (same version, reinstalled)

- **Phone web remote** (Controls page): a PIN-protected page served on port 8765 for phone browsers on the
  same Wi-Fi. It offers the D-pad, OK, Back, Home, media, volume and text input, all sent through ADB. It's
  off by default and has an optional, removable firewall rule (local subnet only, admin approval). 21 new
  unit tests (120 total).
- Diagnosis recorded in the README: the Google TV phone app can't pair with the emulator (no Android TV Remote
  Service, NAT network).
- Phone remote: **Fullscreen** and **Esc** buttons, which control the app's TV mode.
- Phone remote: buttons fire on touch, and arrows, volume and delete repeat while held.
- Phone remote: about 14 ms per press instead of about 650 ms. The emulator connection is cached, so
  windows are no longer scanned on every press.
- Fixed: closing the app while TV mode was on but not fullscreen crashed the close handler, leaving the app
  open with an error box. 126 unit tests.
- **Netflix (PC)** button (Home): opens netflix.com in an Edge app window, moves it to the TV monitor and
  switches it to fullscreen, then measures the result (live test: 1920×1080 = monitor, foreground).
  131 unit tests.
- **Remote redesign:** status pill, app tiles, round D-pad, icon buttons, toast messages, fold-out keyboard,
  and installable as a home-screen web app (manifest and icon).
- **Remote app tiles:** NETFLIX (PC) plus the TV apps installed on the TV (Crunchyroll, JioHotstar, YouTube,
  Prime Video, SonyLIV, ZEE5, Sun NXT, Spotify), opened through their TV launch activity.
- **Remote TV button:** minimizes Netflix (PC) and starts or fronts the TV.
- **Netflix (PC) control:** while Netflix in Edge is the foreground window, the remote's keys, text and
  Full/Esc go to it.
- Netflix (PC) reuses an open Netflix window.
- Fixed: injected PC keys had scan code 0, so Edge ignored them (F11 did nothing). Keys now include real
  scan codes.
- Fixed: the launcher could press F11 twice and undo fullscreen; it now waits up to 5 s per press.
- All of the above was live-tested end to end. 146 unit tests.
- **Tray mode:** the window's X hides the app to a tray icon (Open / Launch TV / Netflix (PC) / remote address
  and PIN / Exit), so the phone remote keeps working. **System → Quit** exits.
- **Sign-in choice:** do nothing / start in the background (`--background`: tray only, remote ready) / start
  the TV. 149 unit tests.

### Replaced

- The v1–v4 script launchers (`Start-TV-Fullscreen.bat`, `TV-Fullscreen.ps1`, `TVFullscreenController.cs`
  and related scripts) are kept in `backup-original-v3\` and `backup-v4-script-launcher\`.
