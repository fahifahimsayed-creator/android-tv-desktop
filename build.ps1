# Builds Android TV Desktop with the Roslyn C# compiler against .NET Framework 4.8.1.
# No MSBuild, NuGet or downloads needed. Output: build\bin\  (AndroidTVDesktop.exe, AndroidTVDesktop.Core.dll, tests)
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-Configuration Release|Debug] [-SkipTests]
param([ValidateSet('Release', 'Debug')][string]$Configuration = 'Release', [switch]$SkipTests)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$out = Join-Path $root 'build\bin'
$obj = Join-Path $root 'build\obj'
New-Item -ItemType Directory -Path $out, $obj -Force | Out-Null

function Find-Csc {
    $candidates = @()
    foreach ($vsRoot in 'C:\Program Files\Microsoft Visual Studio', 'C:\Program Files (x86)\Microsoft Visual Studio') {
        if (Test-Path $vsRoot) { $candidates += Get-ChildItem $vsRoot -Recurse -Filter csc.exe -ErrorAction SilentlyContinue | Where-Object { $_.FullName -match '\\Roslyn\\csc\.exe$' } | ForEach-Object FullName }
    }
    if ($candidates) { return ($candidates | Sort-Object -Descending | Select-Object -First 1) }
    throw 'Roslyn csc.exe not found. Install "Visual Studio Build Tools" (C# compiler) or Visual Studio.'
}
function Find-RefDir {
    foreach ($v in 'v4.8.1', 'v4.8', 'v4.7.2') {
        $d = "C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\$v"
        if (Test-Path (Join-Path $d 'PresentationFramework.dll')) { return $d }
    }
    throw '.NET Framework 4.8.x reference assemblies not found (install the .NET Framework 4.8.1 Developer Pack).'
}

$csc = Find-Csc
$ref = Find-RefDir
Write-Output "Compiler:   $csc"
Write-Output "References: $ref"
$common = @('/nologo', '/langversion:latest', '/nostdlib+', '/platform:anycpu', '/utf8output', '/warn:4', '/nowarn:1591',
    "/reference:$ref\mscorlib.dll", "/reference:$ref\System.dll", "/reference:$ref\System.Core.dll")
if ($Configuration -eq 'Debug') { $common += '/debug:full', '/optimize-', '/define:DEBUG' }
else {
    # Release: no absolute paths in the binaries (they would reveal the builder's folder / user name).
    $common += '/debug:portable', '/optimize+', '/deterministic+', "/pathmap:$root=.", "/pathmap:$out=."
}

function Invoke-Csc([string[]]$CscArgs, [string]$What) {
    $rsp = Join-Path $obj "$What.rsp"
    Set-Content -LiteralPath $rsp -Value ($CscArgs | ForEach-Object { if ($_ -match '\s' -and $_ -notmatch '^"') { '"' + $_ + '"' } else { $_ } }) -Encoding UTF8
    $p = Start-Process -FilePath $csc -ArgumentList "@`"$rsp`"" -NoNewWindow -PassThru -Wait -RedirectStandardOutput "$rsp.out" -RedirectStandardError "$rsp.err"
    $o = (Get-Content "$rsp.out" -Raw) + (Get-Content "$rsp.err" -Raw)
    if ($o) { Write-Output $o.Trim() }
    if ($p.ExitCode -ne 0) { throw "$What failed to compile (exit $($p.ExitCode))." }
    Write-Output "$What OK"
}

$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
$asmInfo = Join-Path $obj 'AssemblyInfo.cs'
Set-Content -LiteralPath $asmInfo -Encoding UTF8 -Value @"
[assembly: System.Reflection.AssemblyVersion("$version.0")]
[assembly: System.Reflection.AssemblyFileVersion("$version.0")]
[assembly: System.Reflection.AssemblyProduct("Android TV Desktop")]
[assembly: System.Reflection.AssemblyTitle("Android TV Desktop")]
[assembly: System.Reflection.AssemblyCopyright("Personal use")]
"@

$coreRefs = 'System.Drawing', 'System.Windows.Forms', 'System.Management', 'System.Runtime.Serialization', 'System.Xml', 'WindowsBase',
            'System.IO.Compression', 'System.IO.Compression.FileSystem', 'Microsoft.CSharp', 'Microsoft.VisualBasic', 'System.Net.Http' |
            ForEach-Object { "/reference:$ref\$_.dll" }
$coreSrc = Get-ChildItem (Join-Path $root 'src\Core') -Filter *.cs | ForEach-Object FullName
Invoke-Csc ($common + $coreRefs + @('/target:library', "/out:$out\AndroidTVDesktop.Core.dll", $asmInfo) + $coreSrc) 'Core'

$wpfRefs = 'PresentationCore', 'PresentationFramework', 'System.Xaml', 'System.Xml' | ForEach-Object { "/reference:$ref\$_.dll" }
$appSrc = Get-ChildItem (Join-Path $root 'src\App') -Filter *.cs | ForEach-Object FullName
$appArgs = $common + $coreRefs + $wpfRefs + @("/reference:$out\AndroidTVDesktop.Core.dll", '/target:winexe', "/out:$out\AndroidTVDesktop.exe",
    "/win32manifest:$root\src\App\app.manifest", "/win32icon:$root\src\App\Assets\AndroidTVDesktop.ico",
    "/resource:$root\src\App\Assets\AndroidTVDesktop.ico,AndroidTVDesktop.ico", $asmInfo) + $appSrc
Invoke-Csc $appArgs 'App'

$testSrc = Get-ChildItem (Join-Path $root 'tests') -Filter *.cs | ForEach-Object FullName
Invoke-Csc ($common + $coreRefs + @("/reference:$out\AndroidTVDesktop.Core.dll", '/target:exe', "/out:$out\AndroidTVDesktop.Tests.exe", $asmInfo) + $testSrc) 'Tests'
Copy-Item (Join-Path $root 'Restore-Desktop.ps1'), (Join-Path $root 'Restore-Desktop.bat') $out -Force

if (-not $SkipTests) {
    Write-Output ''
    & "$out\AndroidTVDesktop.Tests.exe"
    if ($LASTEXITCODE -ne 0) { throw "Unit tests failed (exit $LASTEXITCODE)." }
}
Write-Output "Build $version ($Configuration) complete: $out"
