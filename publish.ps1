# Builds (unless -SkipBuild), then packages a self-contained distribution:
#   dist\AndroidTVDesktop-<version>\        ready-to-install folder
#   dist\AndroidTVDesktop-<version>.zip      the same, zipped, with a SHA-256 file
# The package needs only Windows 10/11 (.NET Framework 4.8.1 is part of Windows 11).
param([switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
if (-not $SkipBuild) { & (Join-Path $root 'build.ps1') -Configuration Release; if ($LASTEXITCODE) { throw 'Build failed.' } }
$bin = Join-Path $root 'build\bin'
$dist = Join-Path $root 'dist'
$pkg = Join-Path $dist "AndroidTVDesktop-$version"
if (Test-Path $pkg) { Remove-Item -LiteralPath $pkg -Recurse -Force }
New-Item -ItemType Directory -Path $pkg -Force | Out-Null

$files = @(
    @{ From = "$bin\AndroidTVDesktop.exe" }, @{ From = "$bin\AndroidTVDesktop.Core.dll" },
    @{ From = "$root\src\App\Assets\AndroidTVDesktop.ico" },
    @{ From = "$root\Restore-Desktop.ps1" }, @{ From = "$root\Restore-Desktop.bat" }, @{ From = "$root\Check-Setup.ps1" }, @{ From = "$root\Check-Setup.bat" }, @{ From = "$root\docs\SETUP.md" },
    @{ From = "$root\installer\install.ps1" }, @{ From = "$root\installer\install.bat" },
    @{ From = "$root\installer\uninstall.ps1" }, @{ From = "$root\installer\uninstall.bat" },
    @{ From = "$root\README.md" }, @{ From = "$root\CHANGELOG.md" }, @{ From = "$root\VERSION" }
)
foreach ($f in $files) { if (-not (Test-Path $f.From)) { throw "Missing package file: $($f.From)" }; Copy-Item -LiteralPath $f.From -Destination $pkg }
$zip = Join-Path $dist "AndroidTVDesktop-$version.zip"
if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($pkg, $zip, [IO.Compression.CompressionLevel]::Optimal, $true)
$hash = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash
Set-Content -LiteralPath "$zip.sha256" -Value "$hash  $(Split-Path -Leaf $zip)"
Write-Output "Package folder: $pkg"
Write-Output "Package zip:    $zip ($([int]((Get-Item $zip).Length / 1KB)) KB)  SHA256 $hash"
Write-Output "Install with:   $pkg\install.bat"
