@echo off
rem Installs Android TV Desktop for the current user (no admin rights, no registry changes).
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0install.ps1" %*
