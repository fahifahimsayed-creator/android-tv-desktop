@echo off
rem Android TV Desktop - checks what is ready / missing on this PC (read-only, changes nothing).
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0Check-Setup.ps1"
pause
