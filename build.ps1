# Builds Buzzcord.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# No SDK or Visual Studio needed.
#   powershell -ExecutionPolicy Bypass -File build.ps1 [-Test] [-Live] [-Ui] [-Sign]
# -Sign code-signs the exe with the same certificate setup as OLED Saver (its tools\signing.ps1 and
# signing.json; see that project's SIGNING.md). A signed exe runs directly, without PowerShell.
# -Test runs the offline self-test; -Live adds checks that talk to Discord without signing in;
# -Ui renders the windows with made-up data into bin\ui\*.png.
# -Out builds somewhere else (e.g. while Buzzcord.exe is running and locked).
param([switch]$Test, [switch]$Live, [switch]$Ui, [switch]$Sign, [string]$Out)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) { $csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe' }
if (-not (Test-Path $csc)) { throw 'The .NET Framework 4.x C# compiler was not found.' }

$bin = Join-Path $root 'bin'
$icon = Join-Path $root 'assets\Buzzcord.ico'
New-Item -ItemType Directory -Force $bin, (Join-Path $root 'assets') | Out-Null
$sources = Get-ChildItem (Join-Path $root 'src') -Filter *.cs -Recurse | ForEach-Object { $_.FullName }
$common = @('/nologo', '/target:winexe', '/platform:anycpu', '/optimize+', '/codepage:65001', '/nowarn:1998',
            '/reference:System.dll', '/reference:System.Core.dll', '/reference:System.Drawing.dll', '/reference:System.Numerics.dll',
            '/reference:System.Windows.Forms.dll', '/reference:System.Net.Http.dll', '/reference:System.Security.dll',
            "/reference:$(Join-Path $root 'lib\Microsoft.Web.WebView2.Core.dll')", "/reference:$(Join-Path $root 'lib\Microsoft.Web.WebView2.WinForms.dll')",
            "/reference:$(Join-Path $root 'lib\Concentus.dll')", '/reference:System.Runtime.dll',
            "/win32manifest:$(Join-Path $root 'src\app.manifest')")

function Compile($out, $extra) {
    & $csc @common @extra "/out:$out" @sources
    if ($LASTEXITCODE -ne 0) { throw "Compile failed ($LASTEXITCODE)" }
}

# Smart App Control blocks unsigned .exe files, so helper runs load the exe in-process instead.
function Invoke-Exe($path, [string[]]$arguments) {
    $asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($path))
    return $asm.EntryPoint.Invoke($null, (, $arguments))
}

# The icon is drawn by the app itself; make it on the first build.
if (-not (Test-Path $icon)) {
    $tmp = Join-Path $bin 'iconexport.exe'
    Compile $tmp @()
    [void](Invoke-Exe $tmp @('--export-icon', $icon))
    Remove-Item $tmp
}

# Microsoft's WebView2 libraries (for the captcha window) live beside the exe.
# Concentus goes in bin\voice, where Windows won't load it from disk by itself: Buzzcord loads it
# from memory instead (it isn't code-signed, and Smart App Control blocks unsigned DLLs).
New-Item -ItemType Directory -Force (Join-Path $bin 'voice') | Out-Null
$stray = Join-Path $bin 'Concentus.dll'
if (Test-Path $stray) { Remove-Item $stray -Force }
foreach ($dll in Get-ChildItem (Join-Path $root 'lib') -Filter *.dll) {
    $dest = if ($dll.Name -eq 'Concentus.dll') { Join-Path $bin 'voice\Concentus.dll' } else { Join-Path $bin $dll.Name }
    # A running Buzzcord keeps these loaded; they rarely change, so skip identical copies.
    if ((Test-Path $dest) -and (Get-FileHash $dest).Hash -eq (Get-FileHash $dll.FullName).Hash) { continue }
    Copy-Item $dll.FullName $dest -Force
}
$env:BUZZCORD_HOME = $root

$exe = if ($Out) { $Out } else { Join-Path $bin 'Buzzcord.exe' }
Compile $exe @("/win32icon:$icon")
Write-Host "Built $exe ($([math]::Round((Get-Item $exe).Length / 1KB)) KB)"

if ($Sign) {
    $oled = Join-Path (Split-Path $root) 'OLED Saver for Windows'
    $config = Join-Path $root 'signing.json'
    if (-not (Test-Path $config)) { $config = Join-Path $oled 'signing.json' }
    if (-not (Test-Path $config)) { throw "No signing.json yet: set up the certificate as described in $oled\SIGNING.md" }
    . (Join-Path $oled 'tools\signing.ps1')
    Invoke-CodeSigning -File $exe -ConfigPath $config -ToolsDir (Join-Path $oled 'tools\signing')
}

if ($Test -or $Live -or $Ui) {
    $report = Join-Path $bin 'selftest.txt'
    $a = @('--selftest', $report)
    if ($Live) { $a += '--live' }
    if ($Ui) { $a += @('--ui', (Join-Path $bin 'ui')) }
    $code = Invoke-Exe $exe $a
    Get-Content $report
    if ($code -ne 0) { throw 'Self-test failed' }
}
