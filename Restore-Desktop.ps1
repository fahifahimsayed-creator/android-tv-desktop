# EMERGENCY desktop recovery for Android TV Desktop. Needs neither the app nor a working emulator.
#  1. asks a running Android TV Desktop to leave TV mode (and the old script controller to stop),
#  2. restores the emulator window from the restore record, or un-fullscreens any "Android Emulator"
#     window that is topmost / covers a monitor, and removes the monitor clip region,
#  3. re-shows hidden emulator toolbars and the Windows taskbar, and releases the pointer clip.
# No processes are killed and nothing is deleted except the restore record.
$ErrorActionPreference = 'Continue'
$dataDirs = @((Join-Path $env:LOCALAPPDATA 'AndroidTVDesktop'), (Join-Path $env:LOCALAPPDATA 'AndroidTVFullscreen'))
$logPath = Join-Path $env:LOCALAPPDATA 'AndroidTVDesktop\logs\app.log'
function Say([string]$m) {
    Write-Output $m
    try { New-Item -ItemType Directory -Path (Split-Path $logPath) -Force | Out-Null; Add-Content -LiteralPath $logPath -Value ('{0:yyyy-MM-dd HH:mm:ss.fff} INFO  Restore-Desktop.ps1: {1}' -f (Get-Date), $m) } catch { }
}

Add-Type @'
using System;
using System.Text;
using System.Threading;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class TVRestore {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] public struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    [DllImport("user32.dll")] public static extern bool ClipCursor(IntPtr r);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint="SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr after, string c, string n);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
    [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    public static string Title(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
    public static List<IntPtr> Windows() { var l = new List<IntPtr>(); EnumWindows((h, p) => { l.Add(h); return true; }, IntPtr.Zero); return l; }
    public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
    public static bool Exists(string name) {
        try { using (Mutex.OpenExisting(name)) return true; } catch (WaitHandleCannotBeOpenedException) { return false; } catch (UnauthorizedAccessException) { return true; }
    }
    public static bool Signal(string name) { try { using (var e = EventWaitHandle.OpenExisting(name)) { e.Set(); return true; } } catch { return false; } }
}
'@
[void][TVRestore]::SetThreadDpiAwarenessContext([IntPtr](-4))
Say 'Emergency restore started.'

# 1a. Android TV Desktop app: leave TV mode (same channel a second app launch uses).
if ([TVRestore]::Exists('Local\AndroidTVDesktop_Instance')) {
    Set-Content -LiteralPath (Join-Path ([IO.Path]::GetTempPath()) 'AndroidTVDesktop_command.txt') -Value '--exit-tv'
    if ([TVRestore]::Signal('Local\AndroidTVDesktop_Signal')) { Say 'Asked the running Android TV Desktop app to leave TV mode.'; Start-Sleep -Seconds 3 }
}
# 1b. Old script controller (v4 Start-TV-Fullscreen.bat), if one is still running.
if ([TVRestore]::Exists('Local\AndroidTVFullscreen_Controller')) {
    if ([TVRestore]::Signal('Local\AndroidTVFullscreen_Stop')) { Say 'Asked the old script controller to stop.'; Start-Sleep -Seconds 3 }
}

# 2a. Exact restore from a restore record.
$restored = @{}
foreach ($dir in $dataDirs) {
    $statePath = Join-Path $dir 'state.ini'
    if (-not (Test-Path -LiteralPath $statePath)) { continue }
    $s = @{}
    foreach ($line in Get-Content -LiteralPath $statePath) { if ($line -match '^([A-Za-z]+)=(.*)$') { $s[$Matches[1]] = $Matches[2] } }
    if ($s.hwnd) {
        $h = [IntPtr][Int64]$s.hwnd
        if ([TVRestore]::IsWindow($h) -and [TVRestore]::Pid($h) -eq [uint32]$s.pid) {
            [void][TVRestore]::SetWindowRgn($h, [IntPtr]::Zero, $true)
            [void][TVRestore]::SetWindowLongPtr($h, -16, [IntPtr][Int64]$s.style)
            [void][TVRestore]::SetWindowLongPtr($h, -20, [IntPtr][Int64]$s.exstyle)
            $after = if ($s.wasTopmost -eq '1') { [IntPtr](-1) } else { [IntPtr](-2) }
            $ok = [TVRestore]::SetWindowPos($h, $after, [int]$s.left, [int]$s.top, [int]$s.width, [int]$s.height, 0x0060)
            $restored[$h.ToInt64()] = $true
            Say "Restored emulator window $('0x{0:X}' -f $h.ToInt64()) to ($($s.left),$($s.top)) $($s.width)x$($s.height) (SetWindowPos=$ok)."
        }
    }
    if ($s.hiddenTools) { foreach ($t in $s.hiddenTools.Split(',')) { if ($t) { $th = [IntPtr][Int64]$t; if ([TVRestore]::IsWindow($th)) { [void][TVRestore]::ShowWindow($th, 4) } } } }
    Remove-Item -LiteralPath $statePath -Force -ErrorAction SilentlyContinue
}

# 2b. Any emulator window still topmost or covering its monitor.
$qemu = @{}
Get-Process -Name 'qemu-system-*' -ErrorAction SilentlyContinue | ForEach-Object { $qemu[[uint32]$_.Id] = $true }
foreach ($h in [TVRestore]::Windows()) {
    if (-not [TVRestore]::IsWindowVisible($h) -or -not $qemu.ContainsKey([TVRestore]::Pid($h))) { continue }
    if ([TVRestore]::GetWindow($h, 4) -ne [IntPtr]::Zero -or -not [TVRestore]::Title($h).StartsWith('Android Emulator')) { continue }
    $r = New-Object TVRestore+RECT; [void][TVRestore]::GetWindowRect($h, [ref]$r)
    $mi = New-Object TVRestore+MONITORINFO; $mi.cbSize = [Runtime.InteropServices.Marshal]::SizeOf([type][TVRestore+MONITORINFO])
    [void][TVRestore]::GetMonitorInfo([TVRestore]::MonitorFromWindow($h, 2), [ref]$mi)
    $topmost = ([TVRestore]::GetWindowLongPtr($h, -20).ToInt64() -band 8) -ne 0
    $covers = ($r.R - $r.L) -ge ($mi.rcMonitor.R - $mi.rcMonitor.L) -and ($r.B - $r.T) -ge ($mi.rcMonitor.B - $mi.rcMonitor.T)
    if ($restored.ContainsKey($h.ToInt64()) -and -not $covers) { continue }
    if ($topmost -or $covers) {
        [void][TVRestore]::SetWindowRgn($h, [IntPtr]::Zero, $true)
        $w = [int](($mi.rcWork.R - $mi.rcWork.L) * 0.6); $hh = [int]($w * 9 / 16)
        $x = $mi.rcWork.L + [int](($mi.rcWork.R - $mi.rcWork.L - $w) / 2); $y = $mi.rcWork.T + [int](($mi.rcWork.B - $mi.rcWork.T - $hh) / 2)
        $ok = [TVRestore]::SetWindowPos($h, [IntPtr](-2), $x, $y, $w, $hh, 0x0060)
        Say "Un-fullscreened emulator window $('0x{0:X}' -f $h.ToInt64()) to ($x,$y) ${w}x$hh (SetWindowPos=$ok)."
    }
}
# Hidden emulator toolbars (owned windows titled 'Emulator').
foreach ($h in [TVRestore]::Windows()) {
    if ([TVRestore]::IsWindowVisible($h) -or -not $qemu.ContainsKey([TVRestore]::Pid($h))) { continue }
    $owner = [TVRestore]::GetWindow($h, 4)
    if ($owner -ne [IntPtr]::Zero -and [TVRestore]::IsWindowVisible($owner) -and [TVRestore]::Title($h) -eq 'Emulator') {
        [void][TVRestore]::ShowWindow($h, 4); Say "Re-showed emulator toolbar $('0x{0:X}' -f $h.ToInt64())."
    }
}

# 3. Taskbar(s) and pointer.
foreach ($cls in 'Shell_TrayWnd', 'Shell_SecondaryTrayWnd') {
    $h = [IntPtr]::Zero
    while (($h = [TVRestore]::FindWindowEx([IntPtr]::Zero, $h, $cls, $null)) -ne [IntPtr]::Zero) {
        if (-not [TVRestore]::IsWindowVisible($h)) { [void][TVRestore]::ShowWindow($h, 5); Say "Taskbar window $cls re-shown." }
    }
}
[void][TVRestore]::ClipCursor([IntPtr]::Zero)
if (-not (Get-Process -Name explorer -ErrorAction SilentlyContinue)) { Say 'Windows Explorer is not running; starting it so the taskbar returns.'; Start-Process explorer.exe }
Say 'Emergency restore finished. If anything still looks wrong, press Ctrl+Shift+Esc to open Task Manager.'
