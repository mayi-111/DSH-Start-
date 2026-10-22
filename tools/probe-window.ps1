# Dev helper: run a GUI exe, capture its window, then close it.
# Usage: powershell -File probe-window.ps1 -Exe <path> -Title <window title> [-Seconds 6] [-Out shot.png] [-Args '--x']
# NOTE: keep this file ASCII-only so Windows PowerShell 5.1 reads it correctly.

param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [Parameter(Mandatory=$true)][string]$Title,
    [int]$Seconds = 6,
    [string]$Out = '',
    [string]$ExeArgs = ''
)

$ErrorActionPreference = 'Continue'
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

Add-Type -Namespace ProbeWin -Name Native -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern System.IntPtr FindWindow(string lpClassName, string lpWindowName);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool GetWindowRect(System.IntPtr hWnd, out RECT lpRect);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr hWnd);
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool IsWindowVisible(System.IntPtr hWnd);
public struct RECT { public int Left; public int Top; public int Right; public int Bottom; }
'@

if ($ExeArgs -ne '') {
    $proc = Start-Process -FilePath $Exe -ArgumentList $ExeArgs -PassThru
} else {
    $proc = Start-Process -FilePath $Exe -PassThru
}
Write-Host ("started pid {0}" -f $proc.Id)

$hWnd = [System.IntPtr]::Zero
for ($i = 0; $i -lt ($Seconds * 4); $i++) {
    Start-Sleep -Milliseconds 250
    $hWnd = [ProbeWin.Native]::FindWindow($null, $Title)
    if ($hWnd -ne [System.IntPtr]::Zero) { break }
    if ($proc.HasExited) { Write-Host ("process exited early, code {0}" -f $proc.ExitCode); break }
}

if ($hWnd -eq [System.IntPtr]::Zero) {
    Write-Host 'WINDOW NOT FOUND'
    if (-not $proc.HasExited) { $proc.Kill() }
    exit 2
}

Write-Host ("window found, visible={0}" -f [ProbeWin.Native]::IsWindowVisible($hWnd))
[void][ProbeWin.Native]::SetForegroundWindow($hWnd)
Start-Sleep -Milliseconds 700

if ($Out -ne '') {
    $r = New-Object ProbeWin.Native+RECT
    [void][ProbeWin.Native]::GetWindowRect($hWnd, [ref]$r)
    $w = $r.Right - $r.Left
    $h = $r.Bottom - $r.Top
    if ($w -gt 0 -and $h -gt 0) {
        $bmp = New-Object System.Drawing.Bitmap($w, $h)
        $g = [System.Drawing.Graphics]::FromImage($bmp)
        $g.CopyFromScreen($r.Left, $r.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
        $g.Dispose()
        $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
        Write-Host ("captured {0}x{1} -> {2}" -f $w, $h, $Out)
    }
}

Start-Sleep -Milliseconds 500
$still = -not $proc.HasExited
Write-Host ("still running after capture: {0}" -f $still)
if ($still) { $proc.CloseMainWindow() | Out-Null; Start-Sleep -Milliseconds 800 }
if (-not $proc.HasExited) { $proc.Kill() }
Write-Host 'done'
