# Test results — Android TV Desktop 1.0.0 (2026-10-20)

PC: Windows 11, one 1920×1080 monitor at 125 % (HDR not supported), 8 GB RAM
(about 700–800 MB free during tests), emulator 37.2.12, AVD Television_4K (3840×2160).

## Unit tests — `build.ps1` → `AndroidTVDesktop.Tests.exe`

**99 passed, 0 failed** (last run inside `publish.ps1` for the 1.0.0 package). Covered areas: monitor
enumeration and selection (1920×1080, 2560×1440, fallback), DPI and overscan geometry, window discovery and
handle changes, state transitions, keyboard, single instance, restore record, configuration validation,
startup timeouts, logging and export, ADB parsing, guest display.

## FINAL live GUI results (fixed build)

Two undisturbed runs of `tests\gui\Run-GuiTests.ps1`:
- `results\20261020-122908\` was a full run including a cold start. 13 of 14 passed; A10 found the bug below.
- `results\20261020-123323\` re-ran the suite after the fix, reusing the running emulator.

| ID | Requirement | Final result |
|---|---|---|
| A1 | Launch TV: cold start (run 122908, 15 s, Android ready in 10 s) and reuse of a running emulator (run 123323, 8 s) | **PASS** |
| A2 | Whole monitor covered. Visible area = monitor; nothing above the TV (taskbar included); no title bar or toolbar; 0/1500 uncovered edge pixels; topmost; screenshot | **PASS** |
| A3 | Still fullscreen after 20 s; controller alive | **PASS** |
| A4 | F11 out (exact saved rect, not topmost, toolbar, taskbar, clip removed) and F11 back in | **PASS** |
| A5 | Esc leaves fullscreen; app stays | **PASS** |
| A6 | Held F11 (8 key-downs) gives exactly one transition | **PASS** |
| A7 | Two extra launches: 1 app, 1 emulator, still fullscreen | **PASS** |
| A8 | Home, Backspace (Back) and right-click (Back) each sent once via ADB; still fullscreen | **PASS** |
| A9 | Emulator closed then reopened: stale handle dropped, backdrop gone and taskbar usable while closed, new window fullscreened | **PASS** |
| A10 | Ctrl+Alt+Q leaves TV mode and restores the desktop; app keeps running; hooks released; no restore record left | **PASS** after fix |
| A11a | App killed while fullscreen, then `Restore-Desktop.ps1` restores the desktop | **PASS** |
| A11b | App killed while fullscreen, then the next app start restores from the record | **PASS** |
| A12 | Guest 1920×1080: Android reported `Override size: 1920x1080` while the window still covered the monitor; returning to AVD default removed the override | **PASS** |
| A13 | Unknown AVD gives a clear error and Faulted state; the desktop is unchanged | **PASS** |
| A14 | Sign-in shortcut command run twice: one app, verified fullscreen | **PASS** |
| A15 | Second monitor 2560×1440, monitor switching, HDR display | **NOT VERIFIED** (one monitor connected) |
| A16 | Real Windows sign-in | **NOT VERIFIED** (needs a sign-out) |

**Bug found and fixed by A10.** After Ctrl+Alt+Q (and Exit TV), the engine rewrote `state.ini` because its
saved-window data wasn't cleared. On the next start, the app would then have moved the emulator window back
to an old position unasked. `EmergencyRestore` and `ExitTvAsync` now clear it. The re-run confirms
`restoreRecordGone=True`, and the fixed build is the one installed.

The sections below are earlier, superseded runs, kept for history.

## Live GUI tests — `tests\gui\Run-GuiTests.ps1` (results in `tests\gui\results\20261020-121906\`)

Each item is checked independently by `Check-TvScreen.ps1`: window and region geometry, `WindowFromPoint`
hit tests on a 5×5 grid and the taskbar, title-bar bits, toolbar visibility, backdrop-coloured edge pixels,
and a screenshot.

| ID | Requirement | Result | Evidence |
|---|---|---|---|
| A1 | Launch TV starts Television_4K (cold) and goes fullscreen | **PASS** | Emulator closed first. Launched frameless; Android ready in 10.9 s; verified fullscreen 16 s after launch |
| A2 | Whole monitor covered: no desktop, taskbar, wallpaper, title bar or toolbar | **PASS** | Visible area (0,0)–(1920,1080) = monitor; nothing above the TV (taskbar included); 0/1500 uncovered edge pixels; `A2-fullscreen.png` |
| A3 | Stays fullscreen; controller alive | **PASS** | Same result after 20 s; one app process |
| A4 | F11 exits fullscreen, F11 re-enters | **PASS** | Window back to the saved (125,125) 1200×675, not topmost, toolbar and taskbar visible, monitor clip removed; re-entry verified |
| A5 | Escape exits fullscreen; controller stays | **PASS** | `Exit fullscreen (keyboard)`; app alive; exact rect restored |
| A6 | Held F11 (8 key-downs) = one transition | **PASS** | exits=1, enters=0 |
| A7 | Repeated launches: no duplicates | **PASS** | 2 extra launches handed over; 1 app, 1 qemu; still fullscreen |
| A8–A14 | Back/Home/right-click, emulator close+reopen, Ctrl+Alt+Q, crash recovery, guest resolution, startup failure, sign-in command | **NOT RUN** | The suite stopped at A8 on a harness bug (`$home` is read-only in PowerShell). The fixed script was then blocked by Microsoft Defender as "potentially unwanted software" (synthetic keystrokes plus screen capture). Allow it in Windows Security → Protection history and re-run |
| A15 | 2560×1440 second monitor, monitor switching, HDR display | **NOT VERIFIED** | Only one monitor connected; covered by unit tests only |
| A16 | Real Windows sign-in start | **NOT VERIFIED** | Requires sign-out; manual checklist item 6 |

The topmost flag in the A2–A7 lines reads `False` because of a bug in `Check-TvScreen.ps1` (`$H` overwrote
`$h`), found and fixed afterwards. Direct checks during that run and the installed-build check below show
the TV topmost (`exstyle 0x8`).

## Re-run after allowing the script in Defender — `tests\gui\results\20261020-122610\` (stopped, void)

| ID | Result | Notes |
|---|---|---|
| A1 | **PASS** | Cold start, frameless launch; Android ready in about 11 s; verified fullscreen 17 s after launch |
| A2 | **PASS** | Visible area = monitor, region = monitor clip, nothing above the TV, 0/1500 uncovered edge pixels |
| A3, A4, A6 | **VOID** | Real input reached the app during the run, not from the harness. The app log shows Esc at 12:26:50, F11 at 12:26:53 and Esc at 12:26:58 while A3 was only sleeping; the emulator was minimized before 12:27:18; the fullscreen window was dragged at 12:27:23–24 |
| A5 | PASS (low confidence) | Same contaminated run |
| A7–A14 | **NOT RUN** | Run stopped cleanly; desktop restored with `Restore-Desktop.ps1`; app closed |

The app reacted correctly to every one of those real inputs:
- Esc and F11 left and re-entered fullscreen.
- A minimized emulator was restored (`SW_RESTORE`) before re-entering.
- A dragged window was snapped back by the drift monitor.

The suite still needs one undisturbed run: about 12 minutes with no keyboard or mouse use.

## Other live checks done during development (not via the blocked suite)

| Check | Result | Evidence |
|---|---|---|
| Second launch with `--tv-mode` hands over to the running app and starts TV (cold start) | **PASS** | 35 s from launch to `FULLSCREEN VERIFIED`; one app process; screenshot showed Google TV edge to edge |
| Engine repairs an externally disturbed window (moved to 800×450, not topmost) | **PASS** | Re-applied within 500 ms: `(-67,-59) 2055x1156 topmost=True` |
| Qt replaces the monitor clip; engine re-applies it before `VERIFIED` | **PASS** | Log line `re-applying monitor clip` before `FULLSCREEN VERIFIED`; checker sees region = monitor |
| Closing the app window while fullscreen | **PASS** | Exact rect restored, not topmost, toolbar back, hooks released, no restore record left |
| Installed build (`%LOCALAPPDATA%\Programs\Android TV Desktop`) via `--tv-mode` | **PASS** | Verified in 8 s; checker `FULLSCREEN-OK=True topmost=True`; app 138 MB, qemu 542 MB working set |
| Start Menu → Restore desktop (emergency) while fullscreen | **PASS** | App left TV mode through `--exit-tv`; window back to (125,125) 1200×675; toolbar and taskbar visible |
| Installer: files, Desktop and 3 Start Menu shortcuts, sign-in start left off | **PASS** | All shortcut targets exist; no Startup shortcut |
| Uninstaller | **NOT RUN** | Not run, so the installed app stays in place for you |

## Known issues

- The emulator itself hung once on a cold start with about 700 MB free RAM. The app handles this with a
  timeout and a clear message.
- Antivirus software can flag the GUI test scripts (synthetic input), not the app itself.
