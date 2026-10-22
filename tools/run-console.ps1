# Dev helper: load the launcher assembly in-process and call its Main,
# so startup failures surface as text instead of a silent windowless exit.
# Usage: powershell -File run-console.ps1 <exe> [args...]
# NOTE: keep this file ASCII-only so Windows PowerShell 5.1 reads it correctly.

param(
    [Parameter(Mandatory=$true)][string]$Exe,
    [string]$AppArgs = ''
)

$Extra = @()
if ($AppArgs -ne '') { $Extra = $AppArgs.Split(' ') }

$ErrorActionPreference = 'Continue'
try {
    $bytes = [System.IO.File]::ReadAllBytes($Exe)
    $asm = [System.Reflection.Assembly]::Load($bytes)
    $t = $asm.GetType('DshLauncher.Program')
    if ($t -eq $null) { throw 'type DshLauncher.Program not found' }
    $m = $t.GetMethod('Main', [System.Reflection.BindingFlags]'Static,Public,NonPublic')
    if ($m -eq $null) { throw 'Main not found' }
    Write-Host ("invoking Main with {0} arg(s)" -f $Extra.Count)
    $result = $m.Invoke($null, @(,[string[]]$Extra))
    Write-Host ("Main returned: {0}" -f $result)
    if ($m.GetCustomAttributes([System.STAThreadAttribute], $false).Count -eq 0) { }
} catch {
    Write-Host '=== EXCEPTION ==='
    $e = $_.Exception
    while ($e -ne $null) {
        Write-Host ($e.GetType().FullName + ': ' + $e.Message)
        if ($e.StackTrace -ne $null) { Write-Host $e.StackTrace }
        $e = $e.InnerException
    }
}
Write-Host 'invoke finished'
