# Downloads SignTool (Microsoft.Windows.SDK.BuildTools) and, for Artifact Signing, its dlib
# (Microsoft.ArtifactSigning.Client) from nuget.org into tools\signing. No admin rights needed.
# Alternative with an installer (needs admin, also adds the VC++ runtime):
#   winget install -e --id Microsoft.Azure.ArtifactSigningClientTools
param([switch]$SignToolOnly)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$dest = Join-Path $PSScriptRoot 'signing'
New-Item -ItemType Directory -Force $dest | Out-Null

function Get-NuGetPackage([string]$Id) {
    $lower = $Id.ToLowerInvariant()
    $versions = (Invoke-RestMethod "https://api.nuget.org/v3-flatcontainer/$lower/index.json").versions |
        Where-Object { $_ -notmatch '-' }   # stable releases only
    $version = $versions[-1]
    $target = Join-Path $dest "$Id.$version"
    if (Test-Path $target) { Write-Host "$Id $version already present"; return $target }
    $nupkg = Join-Path $dest "$Id.$version.zip"
    Write-Host "Downloading $Id $version from nuget.org"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$lower/$version/$lower.$version.nupkg" -OutFile $nupkg -UseBasicParsing
    Expand-Archive $nupkg $target
    Remove-Item $nupkg
    return $target
}

$sdk = Get-NuGetPackage 'Microsoft.Windows.SDK.BuildTools'
Get-ChildItem $sdk -Recurse -Filter signtool.exe | Where-Object { $_.Directory.Name -eq 'x64' } |
    ForEach-Object { Write-Host "SignTool: $($_.FullName)" }

if (-not $SignToolOnly) {
    $client = Get-NuGetPackage 'Microsoft.ArtifactSigning.Client'
    Get-ChildItem $client -Recurse -Filter Azure.CodeSigning.Dlib.dll | Where-Object { $_.Directory.Name -eq 'x64' } |
        ForEach-Object { Write-Host "Dlib: $($_.FullName)" }
    Write-Host 'Artifact Signing also needs the .NET 8 runtime (x64) and the Visual C++ runtime.'
}
