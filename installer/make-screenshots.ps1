# Builds ScreenDeck and captures store screenshots in demo mode (fictional data, nothing from this PC).
# Output: screenshots\01-main-window.png, 02-live-view.png, 03-monitor-selected.png (1920x1080)
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$out = Join-Path $root 'screenshots'
New-Item -ItemType Directory -Force $out | Out-Null

Get-Process ScreenDeck -ErrorAction SilentlyContinue | Stop-Process
dotnet build "$root\src\ScreenDeck" -c Debug -v q
if ($LASTEXITCODE -ne 0) { throw "build failed" }
$exe = "$root\src\ScreenDeck\bin\Debug\net9.0-windows\ScreenDeck.exe"

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class WinCap {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public delegate bool EnumProc(IntPtr h, IntPtr l);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr v);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int a, out RECT r, int size);
    public static IntPtr Find(uint pid, string like) {
        IntPtr found = IntPtr.Zero;
        EnumWindows((h, l) => {
            uint p; GetWindowThreadProcessId(h, out p);
            if (p != pid || !IsWindowVisible(h)) return true;
            var sb = new StringBuilder(512); GetWindowText(h, sb, 512);
            if (sb.ToString().Contains(like)) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }
}
"@
[void][WinCap]::SetProcessDpiAwarenessContext([IntPtr]-4)   # per-monitor v2: physical pixels

# PrintWindow renders the window even if other windows cover it; crop to the visible frame.
function Capture([IntPtr]$h, [string]$file) {
    $wr = New-Object WinCap+RECT; [void][WinCap]::GetWindowRect($h, [ref]$wr)
    $fr = New-Object WinCap+RECT; [void][WinCap]::DwmGetWindowAttribute($h, 9, [ref]$fr, 16)
    $full = New-Object System.Drawing.Bitmap ($wr.R - $wr.L), ($wr.B - $wr.T)
    $g = [System.Drawing.Graphics]::FromImage($full)
    $hdc = $g.GetHdc(); [void][WinCap]::PrintWindow($h, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
    $crop = New-Object System.Drawing.Rectangle ($fr.L - $wr.L), ($fr.T - $wr.T), ($fr.R - $fr.L), ($fr.B - $fr.T)
    $img = $full.Clone($crop, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
    $img.Save($file, [System.Drawing.Imaging.ImageFormat]::Png)
    "{0}  {1}x{2}" -f $file, $img.Width, $img.Height
    $img.Dispose(); $full.Dispose()
}

$shots = @(
    @{ Args = '--demo';             Title = 'ScreenDeck v'; File = '01-main-window.png' },
    @{ Args = '--demo --live 1';    Title = 'Live View';    File = '02-live-view.png' },
    @{ Args = '--demo --select 3';  Title = 'ScreenDeck v'; File = '03-monitor-selected.png' }
)
foreach ($s in $shots) {
    $p = Start-Process $exe -ArgumentList $s.Args -PassThru
    Start-Sleep -Seconds 5
    $h = [WinCap]::Find([uint32]$p.Id, $s.Title)
    if ($h -eq [IntPtr]::Zero) { Stop-Process -Id $p.Id; throw "window '$($s.Title)' not found" }
    Capture $h (Join-Path $out $s.File)
    Stop-Process -Id $p.Id
}
