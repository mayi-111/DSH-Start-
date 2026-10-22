# Build DSH.ico (multi-size) from a source logo image (PNG/JPG).
# Usage:
#   powershell -File make-icon.ps1                        # uses logo.png / LOGO.png next to this script
#   powershell -File make-icon.ps1 -Source C:\path\logo.png
# NOTE: keep this file ASCII-only so Windows PowerShell 5.1 reads it correctly.

param(
    [string]$Source = '',
    [string]$Out = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$dist = Join-Path $root 'dist'
if (-not (Test-Path $dist)) { New-Item -ItemType Directory -Path $dist | Out-Null }
if ($Out -eq '') { $Out = Join-Path $dist 'DSH.ico' }

# find the source logo: explicit -Source, else logo.png / LOGO.png next to this script
# [System.IO.File]::Exists is used because Test-Path treats a Hidden file as missing.
if ($Source -eq '') {
    foreach ($name in @('logo.png', 'LOGO.png', 'logo.jpg', 'LOGO.jpg', 'logo.jpeg', 'LOGO.jpeg')) {
        $candidate = Join-Path $root $name
        if ([System.IO.File]::Exists($candidate)) { $Source = $candidate; break }
    }
}
if ($Source -eq '' -or -not [System.IO.File]::Exists($Source)) {
    throw 'source logo not found; pass -Source <path>'
}
$Source = (Resolve-Path $Source).Path
Write-Host "source logo: $Source"

$script:srcImage = [System.Drawing.Image]::FromFile($Source)

# Center-crop to a square, scale down, then round the corners with transparency.
# Cropping (instead of padding) keeps small sizes readable; rounding avoids a
# hard white rectangle sitting on the dark status panel.
$script:side = [Math]::Min($script:srcImage.Width, $script:srcImage.Height)
$script:offsetX = [int](($script:srcImage.Width - $script:side) / 2)
$script:offsetY = [int](($script:srcImage.Height - $script:side) / 2)

function New-PngBytes([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    # rounded-rectangle clip
    $radius = [int]($size * 0.22)
    if ($radius -lt 1) { $radius = 1 }
    $d = $radius * 2
    $clip = New-Object System.Drawing.Drawing2D.GraphicsPath
    $clip.AddArc(0, 0, $d, $d, 180, 90)
    $clip.AddArc(($size - $d), 0, $d, $d, 270, 90)
    $clip.AddArc(($size - $d), ($size - $d), $d, $d, 0, 90)
    $clip.AddArc(0, ($size - $d), $d, $d, 90, 90)
    $clip.CloseFigure()
    $g.SetClip($clip)

    $srcRect = New-Object System.Drawing.Rectangle($script:offsetX, $script:offsetY, $script:side, $script:side)
    $dstRect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $g.DrawImage($script:srcImage, $dstRect, $srcRect, [System.Drawing.GraphicsUnit]::Pixel)

    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bytes = $ms.ToArray()
    $ms.Dispose()
    $bmp.Dispose()
    $clip.Dispose()
    return ,$bytes
}

$sizes = @(16, 32, 48, 64, 128, 256)
$images = New-Object System.Collections.ArrayList
foreach ($s in $sizes) { [void]$images.Add((New-PngBytes $s)) }
$script:srcImage.Dispose()

$fs = [System.IO.File]::Create($Out)
$bw = New-Object System.IO.BinaryWriter($fs)

# ICONDIR
$bw.Write([UInt16]0)
$bw.Write([UInt16]1)
$bw.Write([UInt16]$sizes.Count)

$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $data = [byte[]]$images[$i]
    $dim = 0
    if ($s -lt 256) { $dim = $s }
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]$dim)
    $bw.Write([Byte]0)
    $bw.Write([Byte]0)
    $bw.Write([UInt16]1)
    $bw.Write([UInt16]32)
    $bw.Write([UInt32]$data.Length)
    $bw.Write([UInt32]$offset)
    $offset += $data.Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }

$bw.Flush()
$bw.Close()
$fs.Close()

# The launcher window loads logo.png next to its exe for the panel/bubble logo.
# Write a 256px CIRCULAR version: the collapsed待机 ball is drawn as a circle, so a
# rounded-square logo would make the whole ball look like a rounded square.
$circleSize = 256
$circleBmp = New-Object System.Drawing.Bitmap($circleSize, $circleSize)
$cg = [System.Drawing.Graphics]::FromImage($circleBmp)
$cg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$cg.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$cg.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
$cg.Clear([System.Drawing.Color]::Transparent)
$clipCircle = New-Object System.Drawing.Drawing2D.GraphicsPath
$clipCircle.AddEllipse(0, 0, $circleSize, $circleSize)
$cg.SetClip($clipCircle)
$circleSrc = [System.Drawing.Image]::FromFile($Source)
$srcRect = New-Object System.Drawing.Rectangle($script:offsetX, $script:offsetY, $script:side, $script:side)
$cg.DrawImage($circleSrc, (New-Object System.Drawing.Rectangle(0, 0, $circleSize, $circleSize)), $srcRect, [System.Drawing.GraphicsUnit]::Pixel)
$circleSrc.Dispose()
$cg.Dispose()
$distLogo = Join-Path $dist 'logo.png'
$circleBmp.Save($distLogo, [System.Drawing.Imaging.ImageFormat]::Png)
$circleBmp.Dispose()
$clipCircle.Dispose()
(Get-Item $distLogo -Force).Attributes = [System.IO.FileAttributes]::Archive
Write-Host ("bubble logo (circle) written: {0} ({1} bytes)" -f $distLogo, (Get-Item $distLogo -Force).Length)

Write-Host ("icon written: {0} ({1} bytes)" -f $Out, (Get-Item $Out).Length) -ForegroundColor Green
