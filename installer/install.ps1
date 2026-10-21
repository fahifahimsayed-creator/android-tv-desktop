# Per-user install of Android TV Desktop. No administrator rights, no registry changes.
#   Copies the app to %LOCALAPPDATA%\Programs\Android TV Desktop
#   Creates shortcuts: Desktop (optional) and Start Menu (app, Restore desktop, Uninstall)
#   Records every created file in install-manifest.txt so uninstall removes exactly those.
# Sign-in start is NOT enabled here; turn it on later in the app (System page) after testing TV mode.
param([switch]$NoDesktopShortcut, [switch]$Quiet)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA 'Programs\Android TV Desktop'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) 'Android TV Desktop'
$desktop = [Environment]::GetFolderPath('Desktop')
$version = (Get-Content (Join-Path $src 'VERSION') -Raw).Trim()

if (Get-Process AndroidTVDesktop -ErrorAction SilentlyContinue) {
    Write-Output 'Android TV Desktop is running. Close it (it restores the desktop when closing), then run install again.'
    if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }
    exit 1
}
New-Item -ItemType Directory -Path $dest, $startMenu -Force | Out-Null
$manifest = New-Object System.Collections.Generic.List[string]
foreach ($f in 'AndroidTVDesktop.exe', 'AndroidTVDesktop.Core.dll', 'AndroidTVDesktop.ico', 'Restore-Desktop.ps1', 'Restore-Desktop.bat',
               'uninstall.ps1', 'uninstall.bat', 'README.md', 'CHANGELOG.md', 'VERSION') {
    Copy-Item -LiteralPath (Join-Path $src $f) -Destination $dest -Force
    $manifest.Add((Join-Path $dest $f))
}
$exe = Join-Path $dest 'AndroidTVDesktop.exe'
$ico = Join-Path $dest 'AndroidTVDesktop.ico'
$shell = New-Object -ComObject WScript.Shell
function New-Shortcut([string]$path, [string]$target, [string]$arguments, [string]$desc, [int]$windowStyle = 1) {
    $sc = $shell.CreateShortcut($path)
    $sc.TargetPath = $target; $sc.Arguments = $arguments; $sc.WorkingDirectory = $dest
    $sc.IconLocation = "$ico,0"; $sc.Description = $desc; $sc.WindowStyle = $windowStyle; $sc.Save()
    $manifest.Add($path)
}
New-Shortcut (Join-Path $startMenu 'Android TV Desktop.lnk') $exe '' 'Android TV on your monitor'
New-Shortcut (Join-Path $startMenu 'Restore desktop (emergency).lnk') (Join-Path $dest 'Restore-Desktop.bat') '' 'Leave TV mode and restore the Windows desktop'
New-Shortcut (Join-Path $startMenu 'Uninstall Android TV Desktop.lnk') (Join-Path $dest 'uninstall.bat') '' 'Remove Android TV Desktop'
if (-not $NoDesktopShortcut) { New-Shortcut (Join-Path $desktop 'Android TV Desktop.lnk') $exe '' 'Android TV on your monitor' }
$manifest.Add($startMenu)
Set-Content -LiteralPath (Join-Path $dest 'install-manifest.txt') -Value $manifest -Encoding UTF8
Write-Output "Android TV Desktop $version installed to $dest"
Write-Output "Shortcuts: Start Menu > Android TV Desktop$(if (-not $NoDesktopShortcut) { ', Desktop' })"
Write-Output 'Settings: %APPDATA%\AndroidTVDesktop   Logs: %LOCALAPPDATA%\AndroidTVDesktop\logs'
if (-not $Quiet) { Read-Host 'Press Enter to close' | Out-Null }
