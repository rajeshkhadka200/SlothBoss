# Captures the primary screen (or one window by process id) to a PNG. Development aid for UI verification.
# Usage: powershell -File scripts/screenshot.ps1 -Out shot.png [-ProcessId 1234]
param([Parameter(Mandatory = $true)][string]$Out, [int]$ProcessId = 0)

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32Shot {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
"@
[Win32Shot]::SetProcessDPIAware() | Out-Null

if ($ProcessId -gt 0) {
    $p = Get-Process -Id $ProcessId
    $h = $p.MainWindowHandle
    [Win32Shot]::SetForegroundWindow($h) | Out-Null
    Start-Sleep -Milliseconds 400
    $r = New-Object Win32Shot+RECT
    [Win32Shot]::GetWindowRect($h, [ref]$r) | Out-Null
    $x = $r.Left; $y = $r.Top; $w = $r.Right - $r.Left; $hgt = $r.Bottom - $r.Top
} else {
    $b = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $x = $b.X; $y = $b.Y; $w = $b.Width; $hgt = $b.Height
}
$bmp = New-Object System.Drawing.Bitmap $w, $hgt
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, $bmp.Size)
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output "saved $Out ($w x $hgt)"
