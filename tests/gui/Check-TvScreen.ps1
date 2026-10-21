# Independent GUI check used by Run-GuiTests.ps1 (and usable by hand while the TV is on).
# Measures what is really on screen, without trusting the app's own log:
#  window rect + window region (visible area), WindowFromPoint hit tests on a 5x5 grid and the taskbar,
#  title-bar style bits, visible emulator toolbars, backdrop-coloured pixels on every monitor edge,
#  plus a screenshot. Prints one line "FULLSCREEN-OK=True|False ..." and saves the PNG.
param([string]$Out = (Join-Path $env:TEMP 'tv-check.png'), [string]$AvdName = 'Television_4K')
Add-Type -AssemblyName System.Drawing
if (-not ('TvCheck' -as [type])) {
Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class TvCheck {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; public override string ToString() { return String.Format("({0},{1}) {2}x{3}", L, T, R-L, B-T); } }
  [StructLayout(LayoutKind.Sequential)] public struct MI { public int cb; public RECT mon; public RECT work; public uint flags; }
  [StructLayout(LayoutKind.Sequential)] public struct PT { public int X, Y; }
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] public static extern bool EnumWindows(EP cb, IntPtr l);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint c);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern int GetWindowRgnBox(IntPtr h, out RECT r);
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern bool GetMonitorInfo(IntPtr m, ref MI mi);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(PT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string n);
  public static string Title(IntPtr h) { var s = new StringBuilder(512); GetWindowText(h, s, 512); return s.ToString(); }
  public static string Class(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static uint Pid(IntPtr h) { uint p; GetWindowThreadProcessId(h, out p); return p; }
  public static List<IntPtr> All() { var l = new List<IntPtr>(); EnumWindows((h,x) => { l.Add(h); return true; }, IntPtr.Zero); return l; }
  public static RECT Mon(IntPtr h) { var mi = new MI(); mi.cb = Marshal.SizeOf(typeof(MI)); GetMonitorInfo(MonitorFromWindow(h, 2), ref mi); return mi.mon; }
  public static IntPtr Root(int x, int y) { var p = new PT(); p.X = x; p.Y = y; return GetAncestor(WindowFromPoint(p), 2); }
}
'@ }
[void][TvCheck]::SetThreadDpiAwarenessContext([IntPtr](-4))
$qemu = @{}; Get-Process 'qemu-system-*' -ErrorAction SilentlyContinue | ForEach-Object { $qemu[[uint32]$_.Id] = $true }
$appPids = @(Get-Process AndroidTVDesktop -ErrorAction SilentlyContinue | ForEach-Object { [uint32]$_.Id })
$h = [TvCheck]::All() | Where-Object { [TvCheck]::IsWindowVisible($_) -and [TvCheck]::GetWindow($_, 4) -eq [IntPtr]::Zero -and $qemu.ContainsKey([TvCheck]::Pid($_)) -and [TvCheck]::Title($_).StartsWith("Android Emulator - ${AvdName}:") } | Select-Object -First 1
if (-not $h) { Write-Output 'FULLSCREEN-OK=False no emulator window'; return }
$r = New-Object TvCheck+RECT; [void][TvCheck]::GetWindowRect($h, [ref]$r); $mon = [TvCheck]::Mon($h)
$rg = New-Object TvCheck+RECT; $rt = [TvCheck]::GetWindowRgnBox($h, [ref]$rg)
$vis = @([Math]::Max($r.L, $mon.L), [Math]::Max($r.T, $mon.T), [Math]::Min($r.R, $mon.R), [Math]::Min($r.B, $mon.B))
if ($rt -ge 2) { $vis = @([Math]::Max($vis[0], $r.L + $rg.L), [Math]::Max($vis[1], $r.T + $rg.T), [Math]::Min($vis[2], $r.L + $rg.R), [Math]::Min($vis[3], $r.T + $rg.B)) }
$visibleIsMonitor = $vis[0] -eq $mon.L -and $vis[1] -eq $mon.T -and $vis[2] -eq $mon.R -and $vis[3] -eq $mon.B
$emuPid = [TvCheck]::Pid($h); $foreign = @(); $taskbarOnTop = $false
$pts = foreach ($fy in 0.001, 0.25, 0.5, 0.75, 0.999) { foreach ($fx in 0.001, 0.25, 0.5, 0.75, 0.999) { , @(($mon.L + [int](($mon.R - $mon.L - 1) * $fx)), ($mon.T + [int](($mon.B - $mon.T - 1) * $fy))) } }
$tb = [TvCheck]::FindWindow('Shell_TrayWnd', $null); $tr = New-Object TvCheck+RECT
if ($tb -ne [IntPtr]::Zero -and [TvCheck]::GetWindowRect($tb, [ref]$tr)) { $pts += , @([int](($tr.L + $tr.R) / 2), [int](($tr.T + $tr.B) / 2)) }
foreach ($p in $pts) {
    if ($p[0] -lt $mon.L -or $p[0] -ge $mon.R -or $p[1] -lt $mon.T -or $p[1] -ge $mon.B) { continue }
    $root = [TvCheck]::Root($p[0], $p[1]); $pp = [TvCheck]::Pid($root)
    if ($pp -ne $emuPid -and $appPids -notcontains $pp) { $c = [TvCheck]::Class($root); $foreign += "$c@$($p[0]),$($p[1])"; if ($c -like 'Shell_*TrayWnd') { $taskbarOnTop = $true } }
}
$noTitleBar = ([TvCheck]::GetWindowLongPtr($h, -16).ToInt64() -band 0x00C00000) -eq 0
$tools = @([TvCheck]::All() | Where-Object { [TvCheck]::IsWindowVisible($_) -and [TvCheck]::GetWindow($_, 4) -eq $h }).Count
$monW = $mon.R - $mon.L; $monH = $mon.B - $mon.T
$bmp = New-Object Drawing.Bitmap $monW, $monH; $g = [Drawing.Graphics]::FromImage($bmp); $g.CopyFromScreen($mon.L, $mon.T, 0, 0, $bmp.Size)
$n = 0; $key = 0
function IsKey($c) { [Math]::Abs($c.R - 3) -le 1 -and [Math]::Abs($c.G - 1) -le 1 -and [Math]::Abs($c.B - 4) -le 1 }
for ($x = 0; $x -lt $monW; $x += 4) { foreach ($y in 1, ($monH - 2)) { $n++; if (IsKey $bmp.GetPixel($x, $y)) { $key++ } } }
for ($y = 0; $y -lt $monH; $y += 4) { foreach ($x in 1, ($monW - 2)) { $n++; if (IsKey $bmp.GetPixel($x, $y)) { $key++ } } }
$s = New-Object Drawing.Bitmap $bmp, ([int]($monW / 2)), ([int]($monH / 2)); $s.Save($Out, [Drawing.Imaging.ImageFormat]::Png); $s.Dispose(); $g.Dispose(); $bmp.Dispose()
$ok = $visibleIsMonitor -and $foreign.Count -eq 0 -and $noTitleBar -and $tools -eq 0 -and $key -eq 0
Write-Output ("FULLSCREEN-OK=$ok monitor=$mon window=$r region=$(if($rt -ge 2){$rg}else{'none'}) visibleArea=($($vis -join ',')) visibleIsMonitor=$visibleIsMonitor " +
              "windowsAboveTV=$(if($foreign){$foreign -join ';'}else{'none'}) taskbarOnTop=$taskbarOnTop noTitleBar=$noTitleBar visibleToolbars=$tools uncoveredEdgePixels=$key/$n topmost=$((([TvCheck]::GetWindowLongPtr($h, -20).ToInt64()) -band 8) -ne 0) screenshot=$Out")
