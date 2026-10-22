# Temporary verification (ASCII only, CJK path assembled from code points):
#   1. does dist\logo.png exist and is it non-empty?
#   2. does the installed logo.png match it?
#   3. does the launcher load the logo when it runs?
# Delete after use.

$root = 'C:\Users\MSI\Desktop\DSH' + [char]0x542F + [char]0x52A8 + [char]0x5668
Write-Host "root: $root  (exists: $([System.IO.Directory]::Exists($root)))"

$distLogo = Join-Path $root 'dist\logo.png'
$distIco = Join-Path $root 'dist\DSH.ico'
$install = Join-Path $env:LOCALAPPDATA 'DSH-Launcher'
$installLogo = Join-Path $install 'logo.png'

foreach ($pair in @(
    @{ p = $distLogo; label = 'dist\logo.png' },
    @{ p = $distIco; label = 'dist\DSH.ico' },
    @{ p = $installLogo; label = 'install\logo.png' },
    @{ p = (Join-Path $install 'DSH.exe'); label = 'install\DSH.exe' }
)) {
    $exists = [System.IO.File]::Exists($pair.p)
    $len = 0
    if ($exists) { $len = (Get-Item $pair.p -Force).Length }
    Write-Host ("{0,-20} exists={1} bytes={2}" -f $pair.label, $exists, $len)
}

# run the launcher briefly and see whether the logo loads
$log = Join-Path $install 'logs\dsh-launcher.log'
Remove-Item $log -Force -ErrorAction SilentlyContinue
Start-Process -FilePath (Join-Path $install 'DSH.exe') -ArgumentList '--autoclose', '4', '--no-auto-open'
Start-Sleep -Seconds 8
Write-Host '--- launcher log ---'
Get-Content $log -ErrorAction SilentlyContinue | Where-Object { $_ -notmatch '\[err\]' }
