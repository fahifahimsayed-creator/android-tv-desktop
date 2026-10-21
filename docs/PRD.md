# PRD — Android TV Desktop

## Problem

The owner has a Google TV AVD (`Television_4K`) in Android Studio and wants it to feel like a TV on a
Windows 11 laptop (1920×1080) and an external 2560×1440 HDR monitor: full screen, no desktop, taskbar or
emulator chrome, and an easy, safe way back to Windows. Earlier script launchers reported success but showed
a smaller window with the desktop and taskbar visible.

## Users

People on a typical home Windows PC (8 GB RAM is enough), using a keyboard, mouse or phone, often from the couch.

## Goals

1. One click (or sign-in) turns the TV on: start or reuse the emulator, wait for Android, and go fullscreen
   on the chosen monitor.
2. The TV picture covers the whole monitor, verified on screen and not just assumed from a rectangle.
3. One key always gets Windows back (F11/Esc for normal use, Ctrl+Alt+Q for emergencies, plus a standalone
   restore script).
4. It works on any monitor and resolution: no hard-coded 1080p or 1440p, and the choice survives monitor
   changes.
5. It never damages anything: the AVD, apps, account, Windows settings, HDR state and shell stay untouched.

## Non-goals

- Game features of any kind.
- A fake Google TV interface. The real emulator always renders the TV.
- Bypassing DRM, Google Play licensing, Play Protect or account security.
- Changing the Windows display resolution, HDR state or the shell.

## Requirements (summary)

| Area | Requirement |
|---|---|
| Launch | Start `Television_4K` if it isn't running; never start a second emulator; bounded timeouts with clear errors |
| Fullscreen | Borderless and topmost on the selected monitor; physical DPI-aware bounds; covers the taskbar; re-checked visible geometry; handles handle changes and monitor changes |
| Monitors | List with resolution, refresh rate, scaling and HDR; Auto (primary) or a preferred monitor; falls back to the primary if the preferred one is missing |
| Guest display | AVD default / match monitor / 1080p / 1440p / 4K, plus density, limited to the AVD's panel and verified through ADB |
| Keyboard | F11, Esc, Ctrl+Alt+Q, Back, Home, configurable, intercepted only while the TV has focus, no duplicate key events |
| Mouse | Native input via the emulator; optional right-click = Back; pointer confined only while the TV has focus |
| Recovery | Restore on every exit path; standalone restore script; restore after a crash on the next start |
| Settings | JSON in the user profile, validated, atomic save, persistent |
| Diagnostics | Log with rotation; report; network and ADB checks; zip export; startup time and process memory |
| Install | Per-user install, Desktop and Start Menu shortcuts, optional sign-in start, uninstaller |
| Safety | Least privilege, no telemetry, no registry or shell changes, confirmation before closing or restarting the emulator |

## Success criteria

- An independent on-screen check reports **0 uncovered edge pixels**, no window above the TV (taskbar
  included), no title bar and no toolbar, with screenshot evidence.
- F11, Esc and Ctrl+Alt+Q pass the live GUI tests. Repeated launches create no duplicates. Closing and
  reopening the emulator is handled.
- Untestable items (second monitor, real sign-in) are marked **NOT VERIFIED**, never assumed.
