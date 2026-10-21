# Android TV Desktop - setup checker (read-only; changes nothing).
# Tells you, step by step, what is ready and what is still missing on this PC.
# Usage: double-click Check-Setup.bat   (or: powershell -ExecutionPolicy Bypass -File Check-Setup.ps1)
$ErrorActionPreference = 'Continue'
$script:missing = 0
function Ok([string]$what, [string]$detail = '') { Write-Host ('  [OK]   ' + $what) -ForegroundColor Green -NoNewline; if ($detail) { Write-Host "  ($detail)" -ForegroundColor DarkGray } else { Write-Host '' } }
function Bad([string]$what, [string]$fix) { $script:missing++; Write-Host ('  [TODO] ' + $what) -ForegroundColor Yellow; Write-Host ('         -> ' + $fix) -ForegroundColor Gray }
function Info([string]$what) { Write-Host ('  [i]    ' + $what) -ForegroundColor Cyan }
function Section([string]$t) { Write-Host ''; Write-Host $t -ForegroundColor White }

Write-Host 'Android TV Desktop - setup check' -ForegroundColor Cyan
Write-Host 'Read-only: nothing on your PC is changed. Full guide: docs/SETUP.md' -ForegroundColor DarkGray

# ---------------------------------------------------------------- 1. PC
Section '1. Your PC'
$os = Get-CimInstance Win32_OperatingSystem
$build = [int]$os.BuildNumber
if ([Environment]::Is64BitOperatingSystem -and $build -ge 17763) { Ok "Windows 64-bit" "$($os.Caption), build $build" }
else { Bad "Windows 10 (1809+) or 11, 64-bit is required" "Update Windows / use a 64-bit Windows 10 or 11." }
$ramGb = [math]::Round($os.TotalVisibleMemorySize / 1MB, 1)
if ($ramGb -ge 15) { Ok "RAM" "$ramGb GB" }
elseif ($ramGb -ge 7) { Ok "RAM" "$ramGb GB - enough; close big apps (browsers, games) while the TV runs" }
else { Bad "RAM: $ramGb GB is too little" "The emulator needs about 3 GB on top of Windows. 8 GB or more is recommended." }
$sysDrive = Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='$($env:SystemDrive)'"
$freeGb = [math]::Round($sysDrive.FreeSpace / 1GB, 1)
if ($freeGb -ge 20) { Ok "Free disk space on $($env:SystemDrive)" "$freeGb GB" }
else { Bad "Only $freeGb GB free on $($env:SystemDrive)" "Android Studio + SDK + the Google TV image need about 15-20 GB. Free up space first." }
$cpu = Get-CimInstance Win32_Processor | Select-Object -First 1
$hv = (Get-CimInstance Win32_ComputerSystem).HypervisorPresent
if ($cpu.VirtualizationFirmwareEnabled -or $hv) { Ok "CPU virtualization (VT-x / AMD-V)" $cpu.Name.Trim() }
else { Bad "CPU virtualization is OFF" "Enable 'Intel Virtualization Technology (VT-x)' or 'SVM Mode (AMD-V)' in your BIOS/UEFI settings, then restart." }

# ---------------------------------------------------------------- 2. Android SDK
Section '2. Android Studio / Android SDK'
$roots = @($env:ANDROID_SDK_ROOT, $env:ANDROID_HOME, (Join-Path $env:LOCALAPPDATA 'Android\Sdk')) | Where-Object { $_ }
$sdk = $roots | Where-Object { Test-Path (Join-Path $_ 'emulator\emulator.exe') } | Select-Object -First 1
# Android Studio can be installed anywhere: ask Windows' installed-programs list first, then try the default folders.
$reg = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*' -ErrorAction SilentlyContinue |
       Where-Object { $_.DisplayName -like 'Android Studio*' } | Select-Object -First 1
$studio = $null
if ($reg) {
    $cand = @()
    if ($reg.DisplayIcon) { $cand += ($reg.DisplayIcon -replace ',\d+$', '').Trim('"') }
    if ($reg.InstallLocation) { $cand += (Join-Path $reg.InstallLocation 'bin\studio64.exe'), (Join-Path $reg.InstallLocation 'bin\studio.exe') }
    $studio = $cand | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $studio) { $studio = @("$env:ProgramFiles\Android\Android Studio\bin\studio64.exe", "$env:LOCALAPPDATA\Programs\Android Studio\bin\studio64.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1 }
if ($studio) { Ok "Android Studio" ("$(if ($reg.DisplayVersion) { 'version ' + $reg.DisplayVersion + ', ' })$studio") }
elseif ($sdk) { Info "Android Studio not found, but the Android SDK is present - that is enough to run the TV." }
else { Bad "Android Studio is not installed" "Install it (docs/SETUP.md step 1): winget install -e --id Google.AndroidStudio   or download from https://developer.android.com/studio" }
if ($sdk) {
    Ok "Android SDK" $sdk
    $emuVer = (& (Join-Path $sdk 'emulator\emulator.exe') -version 2>$null | Select-Object -First 1)
    Ok "Android Emulator" $emuVer
    if (Test-Path (Join-Path $sdk 'platform-tools\adb.exe')) { Ok "ADB (platform-tools)" }
    else { Bad "ADB (Android SDK Platform-Tools) is missing" "Android Studio > Tools > SDK Manager > SDK Tools tab > tick 'Android SDK Platform-Tools' > Apply." }
} else {
    Bad "Android SDK / Emulator not found" "Install Android Studio (docs/SETUP.md step 1): winget install -e --id Google.AndroidStudio  - then open it once and finish the Setup Wizard (Standard)."
}

# ---------------------------------------------------------------- 3. Acceleration
Section '3. Hardware acceleration (needed for a smooth emulator)'
if ($sdk -and (Test-Path (Join-Path $sdk 'emulator\emulator-check.exe'))) {
    $chk = & (Join-Path $sdk 'emulator\emulator-check.exe') accel 2>&1 | Out-String
    if ($LASTEXITCODE -eq 0) { Ok "Emulator acceleration" (($chk -split "`n" | Where-Object { $_ -match 'usable|installed' } | Select-Object -First 1).Trim()) }
    else { Bad "Emulator acceleration is not available" "Turn on 'Windows Hypervisor Platform': Start > 'Turn Windows features on or off' > tick Windows Hypervisor Platform > OK > restart. Also check CPU virtualization in BIOS (step 1)." }
} else { Info "Install the Android Emulator first, then run this check again." }

# ---------------------------------------------------------------- 4. Google TV emulator
Section '4. Google TV emulator (AVD)'
$tvImages = @()
if ($sdk -and (Test-Path (Join-Path $sdk 'system-images'))) {
    $tvImages = Get-ChildItem (Join-Path $sdk 'system-images') -Directory -ErrorAction SilentlyContinue | ForEach-Object {
        $api = $_.Name; Get-ChildItem $_.FullName -Directory -ErrorAction SilentlyContinue | Where-Object { $_.Name -match 'tv' } | ForEach-Object { "$api / $($_.Name)" } }
}
if ($tvImages) { Ok "TV system image(s) downloaded" ($tvImages -join ', ') }
else { Bad "No Google TV / Android TV system image yet" "Android Studio > Device Manager > + > Create Virtual Device > TV > Television (4K) > Next > download 'Google TV' (API 36, x86_64)." }
$avdDir = if ($env:ANDROID_AVD_HOME) { $env:ANDROID_AVD_HOME } else { Join-Path $env:USERPROFILE '.android\avd' }
$avds = @(Get-ChildItem $avdDir -Filter *.ini -ErrorAction SilentlyContinue | ForEach-Object { $_.BaseName })
$tvAvds = @()
foreach ($a in $avds) {
    $cfg = Join-Path $avdDir "$a.avd\config.ini"
    $tag = if (Test-Path $cfg) { ((Get-Content $cfg | Where-Object { $_ -like 'tag.id=*' }) -replace 'tag.id=', '') } else { '' }
    $res = if (Test-Path $cfg) { $w = (Get-Content $cfg | Where-Object { $_ -like 'hw.lcd.width=*' }) -replace '.*='; $h = (Get-Content $cfg | Where-Object { $_ -like 'hw.lcd.height=*' }) -replace '.*='; "${w}x$h" } else { '' }
    if ($tag -match 'tv' -or $a -match '(?i)tv|television') { $tvAvds += "$a ($tag, $res)" }
}
if ($tvAvds) { Ok "TV emulator(s)" ($tvAvds -join '; ') }
elseif ($avds) { Bad "You have emulators ($($avds -join ', ')), but none is a TV" "Create a TV one: Device Manager > + > TV > Television (4K) > Google TV image." }
else { Bad "No emulator (AVD) created yet" "Android Studio > Device Manager > + > Create Virtual Device > TV > Television (4K) > Google TV (API 36) > Finish. Start it once from Android Studio." }

# ---------------------------------------------------------------- 5. Extras
Section '5. Android TV Desktop and extras'
$app = Join-Path $env:LOCALAPPDATA 'Programs\Android TV Desktop\AndroidTVDesktop.exe'
if (Test-Path $app) { Ok "Android TV Desktop installed" $app }
else { Bad "Android TV Desktop is not installed yet" "Run install.bat from the downloaded release zip (docs/SETUP.md step 5)." }
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if ($edge) { Ok "Microsoft Edge (for Netflix (PC))" } else { Info "Microsoft Edge not found - only needed for the Netflix (PC) button." }

Write-Host ''
if ($script:missing -eq 0) { Write-Host 'All set! Open Android TV Desktop and press Launch TV.' -ForegroundColor Green }
else { Write-Host "$script:missing thing(s) still to do - follow the [TODO] lines above (in order), then run this check again." -ForegroundColor Yellow }
Write-Host ''
