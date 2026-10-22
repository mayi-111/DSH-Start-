# Dev helper: hold an exclusive lock on a file for N seconds.
# Usage: powershell -File hold-lock.ps1 -Path <file> -Seconds 60
# NOTE: keep this file ASCII-only so Windows PowerShell 5.1 reads it correctly.

param(
    [Parameter(Mandatory=$true)][string]$Path,
    [int]$Seconds = 60
)

$fs = [System.IO.File]::Open($Path, 'Open', 'ReadWrite', 'None')
Write-Host ("locked {0} for {1}s" -f $Path, $Seconds)
Start-Sleep -Seconds $Seconds
$fs.Close()
Write-Host 'released'
