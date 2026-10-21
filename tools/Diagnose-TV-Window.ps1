# Read-only Win32 diagnostics for the Android Emulator windows.
# Prints every top-level window owned by emulator/qemu processes, with outer
# rect, client rect, DWM extended frame bounds, styles, owner and monitor.
# Changes nothing. Usage: powershell -ExecutionPolicy Bypass -File Diagnose-TV-Window.ps1 [-All] [-OutFile path]
# -All also lists the many hidden GL/IME helper windows qemu creates.
param([string]$OutFile, [switch]$All)

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class TVDiag {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential, CharSet=CharSet.Unicode)]
    public struct MONITORINFOEX { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst=32)] public string szDevice; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr h, uint cmd);
    [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, uint f);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFOEX mi);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
    [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern IntPtr FindWindow(string c, string n);

    static string R(RECT r) { return String.Format("({0},{1}) {2}x{3}", r.L, r.T, r.R - r.L, r.B - r.T); }

    public static string Dump(uint[] pids, bool all) {
        SetThreadDpiAwarenessContext(new IntPtr(-4)); // per-monitor v2: physical pixels
        var set = new HashSet<uint>(pids);
        var sb = new StringBuilder();
        IntPtr fg = GetForegroundWindow();
        int z = 0;
        EnumWindows((h, l) => {
            z++;
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (!set.Contains(pid)) return true;
            if (!all && !IsWindowVisible(h)) return true; // skip hidden GL/IME helper windows
            var t = new StringBuilder(512); GetWindowText(h, t, 512);
            var c = new StringBuilder(256); GetClassName(h, c, 256);
            RECT wr, cr, fr; GetWindowRect(h, out wr); GetClientRect(h, out cr);
            int hr = DwmGetWindowAttribute(h, 9, out fr, Marshal.SizeOf(typeof(RECT)));
            var mi = new MONITORINFOEX(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
            GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
            sb.AppendFormat("hwnd=0x{0:X} pid={1} z={2} visible={3} fg={4} class='{5}' title='{6}'\n",
                h.ToInt64(), pid, z, IsWindowVisible(h), h == fg, c, t);
            sb.AppendFormat("   outer={0} client={1} dwmFrame={2} (hr=0x{3:X}) dpi={4}\n", R(wr), R(cr), R(fr), hr, GetDpiForWindow(h));
            sb.AppendFormat("   style=0x{0:X8} exstyle=0x{1:X8} owner=0x{2:X} parent=0x{3:X}\n",
                GetWindowLongPtr(h, -16).ToInt64() & 0xFFFFFFFF, GetWindowLongPtr(h, -20).ToInt64() & 0xFFFFFFFF,
                GetWindow(h, 4).ToInt64(), GetWindowLongPtr(h, -8).ToInt64());
            sb.AppendFormat("   monitor={0} bounds={1} work={2}\n", mi.szDevice, R(mi.rcMonitor), R(mi.rcWork));
            return true;
        }, IntPtr.Zero);
        IntPtr tb = FindWindow("Shell_TrayWnd", null);
        RECT tr; GetWindowRect(tb, out tr);
        sb.AppendFormat("Taskbar hwnd=0x{0:X} visible={1} rect={2}\n", tb.ToInt64(), IsWindowVisible(tb), R(tr));
        var fgt = new StringBuilder(512); GetWindowText(fg, fgt, 512);
        sb.AppendFormat("Foreground hwnd=0x{0:X} title='{1}'\n", fg.ToInt64(), fgt);
        return sb.ToString();
    }
}
'@

$procs = Get-Process -Name 'emulator', 'qemu-system-*' -ErrorAction SilentlyContinue
$lines = @("Diagnostics at $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')")
foreach ($p in $procs) { $lines += "Process $($p.ProcessName) PID=$($p.Id) MainWindowHandle=0x$('{0:X}' -f $p.MainWindowHandle.ToInt64()) MainWindowTitle='$($p.MainWindowTitle)'" }
if (-not $procs) { $lines += 'No emulator/qemu process is running.' }
$lines += [TVDiag]::Dump([uint32[]]@($procs | ForEach-Object { [uint32]$_.Id }), [bool]$All)
$text = $lines -join "`r`n"
Write-Output $text
if ($OutFile) { Set-Content -LiteralPath $OutFile -Value $text -Encoding UTF8 }
