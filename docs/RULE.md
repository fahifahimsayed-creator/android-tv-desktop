# Rules for working on this project

## Safety (never break these)

1. Never edit, wipe or reset the AVD (`~\.android\avd\Television_4K.avd`) or the system image. Guest changes
   go only through reversible `wm size/density`, and only when the user chose them.
2. Never change the Windows display resolution, HDR state, shell, Explorer, Task Manager, UAC or Defender.
   No registry writes. Never require administrator rights.
3. Never kill the emulator; close it with `WM_CLOSE`. Don't add aggressive process termination.
4. Every change to another window (style, rect, z-order, region, visibility), the taskbar or the pointer
   must be recorded in `state.ini` and undone on **every** exit path. Add new state to `RestoreRecord`,
   `TvEngine.EmergencyRestore`, `DesktopRestore` and `Restore-Desktop.ps1` together.
5. Keys are intercepted only while TV mode is active and the emulator is in the foreground (the emergency
   chord is the only exception). Never swallow keys in other apps. Never send a key to Android twice.
6. No telemetry. No network calls other than the user-started checks and ADB.

## Honesty in testing

- "Fullscreen works" needs on-screen evidence (pixels, hit tests, a screenshot). A matching rectangle is
  not proof.
- Anything that cannot be tested here (second monitor, real sign-in, HDR playback) is reported as
  **NOT VERIFIED**.
- Run `build.ps1` (unit tests) before any GUI test, and the live GUI harness (maintainer-only, not published because antivirus flags synthetic-input scripts) before a release.

## Code

- C# with `/langversion:latest` targeting .NET Framework 4.8.1. No NuGet packages without a real need.
- Keep logic that can be tested out of Win32 and into pure classes (`KeyFilter`, `MonitorSelector`,
  `Geometry`, `TvStateMachine`, `StartupPolicy`, `RestoreRecord`, settings validation) and unit test it.
- Physical pixels everywhere (Per-Monitor-V2 manifest). Never mix DIPs into Win32 calls.
- Every blocking call has a timeout. The UI thread never waits on ADB or the emulator.
- Log every Win32 call that changes another window, with its return value and error code.
- Bump `VERSION` and `CHANGELOG.md` for each release.
