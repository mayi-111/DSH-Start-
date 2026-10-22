# Bootstrap: pick up the logo placeholder dropped into the project root,
# regenerate the icon, then run the full build + install.
# The project folder name contains CJK characters, which an intermediary shell
# can mangle, so the root is DISCOVERED on the Desktop instead of typed.
# NOTE: keep this file ASCII-only so Windows PowerShell 5.1 reads it correctly.

$ErrorActionPreference = 'Stop'

# --- discover the project root: a Desktop folder holding src\DSH.cs ---------
$root = $null
foreach ($d in [System.IO.Directory]::GetDirectories([Environment]::GetFolderPath('Desktop'))) {
    if ([System.IO.File]::Exists((Join-Path $d 'src\DSH.cs'))) { $root = $d; break }
}
if (-not $root) { throw 'project root not found on the Desktop (needs src\DSH.cs)' }
Write-Host "project root: $root"

# --- locate the logo: logo*/LOGO*/tp.png, hidden files included -------------
$names = @('logo.png', 'LOGO.png', 'logo.jpg', 'LOGO.jpg', 'logo.jpeg', 'LOGO.jpeg',
           'tp.png', 'TP.png', 'tp.jpg', 'logo.ico')
$logo = $null
foreach ($name in $names) {
    $candidate = Join-Path $root $name
    if ([System.IO.File]::Exists($candidate) -and (Get-Item $candidate -Force).Length -gt 0) {
        $logo = $candidate
        break
    }
}
if (-not $logo) { throw 'no usable logo file found in the project root' }
Write-Host ("using logo: {0} ({1} bytes)" -f $logo, (Get-Item $logo -Force).Length)

# --- regenerate icon + title-row logo, then full build ---------------------
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'make-icon.ps1') -Source $logo
if ($LASTEXITCODE -ne 0) { throw "make-icon failed: $LASTEXITCODE" }

& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -NoIcon
if ($LASTEXITCODE -ne 0) { throw "build failed: $LASTEXITCODE" }

Write-Host 'done' -ForegroundColor Green
