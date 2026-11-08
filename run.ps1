# Runs Buzzcord while it is unsigned. Smart App Control blocks unsigned .exe files, so this
# loads the exe into PowerShell (which is signed) instead of starting it directly.
#   powershell -STA -WindowStyle Hidden -ExecutionPolicy Bypass -File run.ps1 [--background]
$env:BUZZCORD_HOME = $PSScriptRoot
$exe = Join-Path $PSScriptRoot 'bin\Buzzcord.exe'
$asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($exe))
[void]$asm.EntryPoint.Invoke($null, (, [string[]]$args))
