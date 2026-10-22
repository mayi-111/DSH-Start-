# Dev helper: capture a window (by title) or the full screen into a PNG.
# Usage:
#   powershell -File capture.ps1 -Title DSH -Out shot.png
#   powershell -File capture.ps1 -Out full.png
# NOTE: keep this file ASCII-only so Windows PowerShell 5.1 reads it correctly.

param(
    [string]$Title = '',
    [string]$Out = 'capture.png'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -Namespace ScreenCap -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr FindWindow(string lpClassName, string lpWindowName);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT lpRect);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr hWnd);
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

$x = 0; $y = 0; $w = 0; $h = 0
if ($Title -ne '') {
    $hWnd = [ScreenCap.Native]::FindWindow($null, $Title)
    if ($hWnd -eq [System.IntPtr]::Zero) { throw "window not found: $Title" }
    [void][ScreenCap.Native]::SetForegroundWindow($hWnd)
    Start-Sleep -Milliseconds 400
    $r = New-Object ScreenCap.Native+RECT
    [void][ScreenCap.Native]::GetWindowRect($hWnd, [ref]$r)
    $x = $r.Left; $y = $r.Top
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
} else {
    $b = [System.Windows.Forms.SystemInformation]::VirtualScreen
    $x = $b.X; $y = $b.Y; $w = $b.Width; $h = $b.Height
}

if ($w -le 0 -or $h -le 0) { throw "bad capture rect ${w}x${h}" }

$bmp = New-Object System.Drawing.Bitmap($w, $h)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen($x, $y, 0, 0, (New-Object System.Drawing.Size($w, $h)))
$g.Dispose()
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

Write-Host ("captured {0}x{1} at ({2},{3}) -> {4}" -f $w, $h, $x, $y, $Out) -ForegroundColor Green
