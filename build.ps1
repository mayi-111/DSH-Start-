# Build script for the DSH launcher.
#   1. transcode the UTF-8 C# source into the system ANSI code page
#      (CP936 on Simplified Chinese Windows) so the legacy csc.exe reads the
#      Chinese string literals correctly
#   2. compile it with the .NET Framework compiler into a windowless DSH.exe
#   3. create the desktop shortcut named "DSH"
#
# Usage: powershell -ExecutionPolicy Bypass -File build.ps1 [-NoShortcut]
# NOTE: keep this file ASCII-only so Windows PowerShell 5.1 reads it correctly.

param(
    [switch]$NoShortcut,
    [switch]$NoIcon,
    [string]$ShortcutName = 'DSH'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$src  = Join-Path $root 'src\DSH.cs'
$dist = Join-Path $root 'dist'
$exe  = Join-Path $dist 'DSH.exe'
$ico  = Join-Path $dist 'DSH.ico'
$iconScript = Join-Path $root 'make-icon.ps1'

if (-not (Test-Path $src)) { throw "source not found: $src" }

# --- 0. icon: regenerate it from a logo image when one is present ----------
# [System.IO.File]::Exists is used because plain Test-Path reports a Hidden file
# as missing. Drop a new image in this folder and rebuild to change the icon.
if (-not $NoIcon -and (Test-Path $iconScript)) {
    $logo = @('logo.png', 'LOGO.png', 'logo.jpg', 'LOGO.jpg', 'logo.jpeg', 'LOGO.jpeg',
              'tp.png', 'TP.png', 'tp.jpg') |
        ForEach-Object { Join-Path $root $_ } |
        Where-Object { [System.IO.File]::Exists($_) } |
        Select-Object -First 1
    if ($logo) {
        Write-Host "regenerating icon from $logo"
        & powershell -NoProfile -ExecutionPolicy Bypass -File $iconScript -Source $logo
    } else {
        Write-Host 'no logo image found (logo.png / LOGO.png / tp.png ...); keeping the existing icon' -ForegroundColor Yellow
    }
}

$csc = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw 'csc.exe not found (needs .NET Framework 4.x, shipped with Windows)' }

if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }

# --- 1. source encoding: UTF-8 -> ANSI/CP936 -------------------------------
$text  = [System.IO.File]::ReadAllText($src, [System.Text.Encoding]::UTF8)
$ansi  = [System.Text.Encoding]::GetEncoding(936)
$build = Join-Path $env:TEMP 'DSH.build.cs'
[System.IO.File]::WriteAllText($build, $text, $ansi)
Write-Host "source transcoded: $src -> $build"

# --- 2. compile ------------------------------------------------------------
$refs = @('System.dll', 'System.Drawing.dll', 'System.Windows.Forms.dll', 'System.Core.dll')
$cscArgs = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/warn:2', "/out:$exe")
foreach ($r in $refs) { $cscArgs += "/reference:$r" }
if (Test-Path $ico) { $cscArgs += "/win32icon:$ico" }
$cscArgs += $build

Write-Host "compiling with $csc"
& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "compile failed, exit code $LASTEXITCODE" }
if (-not (Test-Path $exe)) { throw 'DSH.exe was not produced' }
Write-Host "built: $exe" -ForegroundColor Green

# --- 3. install to an ASCII-only path ---------------------------------------
# A CJK path (like this project folder "DSH启动器") breaks process launch in some
# shells/launchers -- the exe then exits with code 0 without ever entering Main.
# So the SHORTCUT target always lives under %LOCALAPPDATA%\DSH-Launcher.
$installDir = Join-Path $env:LOCALAPPDATA 'DSH-Launcher'
if (-not (Test-Path $installDir)) { New-Item -ItemType Directory -Path $installDir | Out-Null }
$installExe = Join-Path $installDir 'DSH.exe'
$installIco = Join-Path $installDir 'DSH.ico'
$installLogo = Join-Path $installDir 'logo.png'

try {
    [System.IO.File]::Copy($exe, $installExe, $true)
} catch {
    throw ('cannot replace {0} -- close the running launcher (or press its X) and build again' -f $installExe)
}
if ([System.IO.File]::Exists($ico)) { [System.IO.File]::Copy($ico, $installIco, $true) }
# the title-row icon is loaded from logo.png next to the exe.
# Raw .NET IO: the file may be Hidden, which confuses Test-Path/Copy-Item.
$distLogo = Join-Path $dist 'logo.png'
if ([System.IO.File]::Exists($distLogo)) {
    [System.IO.File]::Copy($distLogo, $installLogo, $true)
    (Get-Item $installLogo -Force).Attributes = [System.IO.FileAttributes]::Archive
    Write-Host ("title icon: {0} ({1} bytes)" -f $installLogo, (Get-Item $installLogo -Force).Length)
} else {
    Write-Host 'no dist\logo.png -- the panel will fall back to the exe icon' -ForegroundColor Yellow
}
Write-Host "installed: $installExe" -ForegroundColor Green

# --- 3b. never leave a second runnable copy behind --------------------------
# dist\DSH.exe would be a second, clickable program whose log goes to dist\logs.
# That is confusing (and made log-based diagnosis ambiguous), so after a
# successful install the runnable copies in dist are removed. dist\logo.png is
# kept because make-icon.ps1 and the "title icon" step read it.
foreach ($stale in @((Join-Path $dist 'DSH.exe'), (Join-Path $dist 'DSH.ico'), (Join-Path $dist 'logo.ico'))) {
    if (Test-Path $stale) {
        try {
            Remove-Item $stale -Force -ErrorAction Stop
        } catch {
            Write-Host ("could not remove {0} (still running? close it and rebuild)" -f $stale) -ForegroundColor Yellow
        }
    }
}
$distLogs = Join-Path $dist 'logs'
if (Test-Path $distLogs) { Remove-Item $distLogs -Recurse -Force -ErrorAction SilentlyContinue }

# --- 4. desktop shortcut ---------------------------------------------------
if (-not $NoShortcut) {
    $desktop = [Environment]::GetFolderPath('Desktop')
    $lnkPath = Join-Path $desktop "$ShortcutName.lnk"

    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut($lnkPath)
    $lnk.TargetPath       = $installExe
    $lnk.WorkingDirectory = $installDir
    $lnk.Description      = 'DSH - launch DeepSeek Harness with a status panel'
    $lnk.WindowStyle      = 1
    if (Test-Path $installIco) { $lnk.IconLocation = "$installIco,0" }
    $lnk.Hotkey           = 'CTRL+ALT+D'
    $lnk.Save()
    Write-Host "shortcut created: $lnkPath" -ForegroundColor Green
}

Write-Host ''
Write-Host ('Build finished. Logs: {0}' -f (Join-Path $installDir 'logs\dsh-launcher.log')) -ForegroundColor Cyan
Write-Host 'Double-click the desktop shortcut "DSH" to launch.' -ForegroundColor Cyan
