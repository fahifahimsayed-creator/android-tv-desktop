@echo off
rem EMERGENCY RECOVERY for Android TV Desktop: leaves TV mode, un-fullscreens the emulator,
rem re-shows its toolbar and the Windows taskbar, releases the pointer. Works even if the app
rem or the emulator is frozen. Kills nothing, needs no admin rights.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Restore-Desktop.ps1"
pause
