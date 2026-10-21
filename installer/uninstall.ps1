# Removes Android TV Desktop: exactly the files/shortcuts listed in install-manifest.txt, the sign-in
# shortcut (if enabled) and the program folder. Settings/logs are kept unless -RemoveUserData.
# Never touches the Android SDK, the emulator or the AVD.
param([switch]$RemoveUserData, [switch]$Quiet)
$ErrorActionPreference = 'Continue'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Android TV Desktop'
if (Get-Process AndroidTVDesktop -ErrorAction SilentlyContinue) {
    Write-Output 'Android TV Desktop is running. Close it first (it restores the desktop when closing), then run uninstall again.'
    if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}
$ruleName = 'Android TV Desktop - phone remote'
& netsh advfirewall firewall show rule name="$ruleName" *> $null
if ($LASTEXITCODE -eq 0) {
    Write-Output 'Removing the phone-remote Windows Firewall rule (Windows will ask for administrator approval)...'
    try { Start-Process netsh -ArgumentList "advfirewall firewall delete rule name=`"$ruleName`"" -Verb RunAs -WindowStyle Hidden -Wait } catch { Write-Output "Firewall rule not removed ($($_.Exception.Message)). Remove '$ruleName' in Windows Defender Firewall > Inbound Rules." }
}
$startup = Join-Path ([Environment]::GetFolderPath('Startup')) 'Android TV Desktop.lnk'
if (Test-Path -LiteralPath $startup) { Remove-Item -LiteralPath $startup -Force; Write-Output "Removed sign-in shortcut $startup" }
$manifestPath = Join-Path $dest 'install-manifest.txt'
if (Test-Path -LiteralPath $manifestPath) {
    $items = Get-Content -LiteralPath $manifestPath
    foreach ($p in $items) {
        if ((Test-Path -LiteralPath $p -PathType Leaf) -and $p -notlike "$dest\uninstall.*") { Remove-Item -LiteralPath $p -Force; Write-Output "Removed $p" }
    }
    foreach ($p in $items) { if ((Test-Path -LiteralPath $p -PathType Container) -and -not (Get-ChildItem -LiteralPath $p -Force)) { Remove-Item -LiteralPath $p -Force; Write-Output "Removed $p" } }
    Remove-Item -LiteralPath $manifestPath -Force
} else { Write-Output "No install manifest found in $dest; nothing else removed." }
if ($RemoveUserData -or (-not $Quiet -and (Read-Host 'Also delete settings and logs (%APPDATA%\AndroidTVDesktop, %LOCALAPPDATA%\AndroidTVDesktop)? (y/N)') -match '^(y|yes)$')) {
    foreach ($d in (Join-Path $env:APPDATA 'AndroidTVDesktop'), (Join-Path $env:LOCALAPPDATA 'AndroidTVDesktop')) {
        if (Test-Path -LiteralPath $d) { Remove-Item -LiteralPath $d -Recurse -Force; Write-Output "Removed $d" }
    }
}
# The running uninstall script itself is removed last (cmd deletes it after this script exits).
Write-Output 'Android TV Desktop uninstalled. The Android emulator and your AVD were not touched.'
if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }
