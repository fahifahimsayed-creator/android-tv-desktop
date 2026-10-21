# App flow

## Start

```
AndroidTVDesktop.exe [args]
  ├─ --restore-desktop → ask running instance to exit TV → DesktopRestore → exit
  ├─ another instance running? → hand over args (--tv-mode / --exit-tv) → exit
  ├─ state.ini left by a crash? → DesktopRestore first
  ├─ load + validate settings (defaults for anything invalid, each fix logged)
  ├─ TvEngine: locate SDK/AVD, enumerate monitors, find an existing emulator window
  └─ MainWindow (Home); if --tv-mode or "Launch TV when this app opens" → Launch TV
```

## Launch TV (TvStateMachine)

```
Idle ──Launch TV──► Starting
   Starting: AVD found? emulator window already there? → reuse
             else emulator process for AVD running? → wait for its window (no second emulator)
             else start emulator.exe -avd X -skin WxH
             timeout / early exit ──► Faulted (desktop untouched, error on Home)
   ──window──► WaitingForBoot: poll sys.boot_completed (timeout → continue without ADB features)
   ──ready──► apply guest display (if configured), install keyboard hook, start 500 ms monitor
   ──► Entering ──verified/unverified──► Fullscreen     (or Windowed if "Start in fullscreen" is off)
```

## While TV mode is on

```
Fullscreen ──F11 / Esc──► Exiting ──► Windowed ──F11──► Entering ──► Fullscreen
Fullscreen/Windowed ──Ctrl+Alt+Q / Exit TV / close app──► (restore) ──► Idle
Any ──window destroyed / handle reused──► Starting (backdrop hidden at once) ──window returns──► WaitingForBoot ──► Entering
Fullscreen ──display change──► (1.5 s debounce) Exiting → Entering on the re-selected monitor
Fullscreen ──window moved / lost topmost──► re-place (≤5/min, then Windowed)
Entering ──exception──► restore ──► Windowed
```

## Exit paths and what each restores

| Trigger | Window rect/styles/z-order | Region clip | Toolbar | Backdrop | Taskbar | Pointer | Hooks |
|---|---|---|---|---|---|---|---|
| F11 / Esc | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | stays (TV mode on) |
| Ctrl+Alt+Q, Exit TV, close app | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | released |
| Unhandled exception / sign-out | ✔ | ✔ | ✔ | ✔ | ✔ | ✔ | released on exit |
| App killed | next start or Restore-Desktop.bat | ✔ | ✔ | (window died) | ✔ | ✔ | (process gone) |
| Emulator closed | nothing to restore | — | — | ✔ | ✔ | ✔ | stay, wait 10 min for reopen |

## Pages

Home (status, Launch/Exit TV, F11, Android settings) · Display (monitors, TV mode, guest display, HDR,
diagnostics) · Controls (shortcuts, native keys, mouse, emergency) · System (AVD, SDK, emulator
close/restart, network/ADB, sign-in start, logs, restore, uninstall) · Diagnostics (report, live log,
export) · About.
