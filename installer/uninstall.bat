@echo off
rem Removes Android TV Desktop (program files and shortcuts). Never touches the emulator or AVD.
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1" %*
rem Remove the uninstaller itself and the (now empty) program folder after PowerShell has finished.
if exist "%LOCALAPPDATA%\Programs\Android TV Desktop\install-manifest.txt" goto :eof
(goto) 2>nul & del /q "%~dp0uninstall.ps1" "%~dp0uninstall.bat" & rd "%~dp0" 2>nul
