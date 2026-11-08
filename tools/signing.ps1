# Authenticode signing for build.ps1 -Sign. Two methods, chosen in signing.json:
#   "ArtifactSigning"  Microsoft Azure Artifact Signing (formerly Trusted Signing), through SignTool and its dlib.
#   "Certificate"      Any code-signing certificate in your Windows certificate store, picked by thumbprint.
#                      This covers OV/EV certificates on a USB token or in a CA's cloud HSM (SSL.com eSigner,
#                      Certum SimplySign, DigiCert KeyLocker...), whose drivers expose the certificate there.
# See SIGNING.md for setup.

# Folders worth searching: tools\signing (get-signing-tools.ps1), Windows SDKs, and anything named
# like the Artifact Signing Client Tools install (winget/MSI) under the usual program folders.
function Get-ToolRoots([string]$ToolsDir) {
    $roots = @($ToolsDir, "${env:ProgramFiles(x86)}\Windows Kits\10\bin")
    foreach ($parent in "$env:LOCALAPPDATA\Microsoft", "$env:LOCALAPPDATA\Programs", "$env:ProgramFiles",
                        "$env:ProgramFiles\Microsoft", "${env:ProgramFiles(x86)}", "${env:ProgramFiles(x86)}\Microsoft") {
        if (Test-Path $parent) {
            $roots += Get-ChildItem $parent -Directory -Filter '*Signing*' -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
        }
    }
    $roots | Where-Object { $_ -and (Test-Path $_) }
}

# The newest x64 copy of a file under the tool folders.
function Find-Tool([string]$ToolsDir, [string]$Override, [string]$FileName, [string]$Setting, [string]$Hint) {
    if ($Override) {
        if (Test-Path $Override) { return (Resolve-Path $Override).Path }
        throw "$Setting in signing.json does not exist: $Override"
    }
    $found = foreach ($r in Get-ToolRoots $ToolsDir) {
        Get-ChildItem $r -Recurse -Filter $FileName -ErrorAction SilentlyContinue | Where-Object { $_.Directory.Name -eq 'x64' }
    }
    $best = $found | Sort-Object { [version]$_.VersionInfo.FileVersionRaw } -Descending | Select-Object -First 1
    if (-not $best) { throw "$FileName (x64) was not found. $Hint" }
    return $best.FullName
}

function Find-SignTool([string]$ToolsDir, [string]$Override) {
    Find-Tool $ToolsDir $Override 'signtool.exe' 'SignToolPath' 'Run tools\get-signing-tools.ps1, install a Windows SDK, or set SignToolPath in signing.json.'
}

function Find-Dlib([string]$ToolsDir, [string]$Override) {
    Find-Tool $ToolsDir $Override 'Azure.CodeSigning.Dlib.dll' 'DlibPath' 'Run tools\get-signing-tools.ps1 or set DlibPath in signing.json.'
}

function Invoke-CodeSigning([string]$File, [string]$ConfigPath, [string]$ToolsDir) {
    if (-not (Test-Path $ConfigPath)) {
        throw "No signing.json. Copy signing.sample.json to signing.json and fill it in (see SIGNING.md)."
    }
    $config = Get-Content $ConfigPath -Raw | ConvertFrom-Json

    $common = @('sign', '/v', '/fd', 'SHA256', '/td', 'SHA256')
    switch ($config.Method) {
        'ArtifactSigning' {
            $a = $config.ArtifactSigning
            foreach ($key in 'Endpoint', 'CodeSigningAccountName', 'CertificateProfileName') {
                if (-not $a.$key -or $a.$key -like '<*') { throw "Set ArtifactSigning.$key in signing.json." }
            }
            $signtool = Find-SignTool $ToolsDir $config.SignToolPath
            Write-Host "SignTool: $signtool"
            $dlib = Find-Dlib $ToolsDir $config.DlibPath
            Write-Host "Artifact Signing dlib: $dlib"
            # The dlib reads its own metadata file. Write it next to the build output, outside source control.
            $metadata = @{
                Endpoint               = $a.Endpoint
                CodeSigningAccountName = $a.CodeSigningAccountName
                CertificateProfileName = $a.CertificateProfileName
                CorrelationId          = "Buzzcord-$((Get-Item $File).VersionInfo.ProductVersion)"
            }
            if ($a.ExcludeCredentials) { $metadata.ExcludeCredentials = @($a.ExcludeCredentials) }
            $metadataPath = Join-Path (Split-Path $File) 'artifact-signing.metadata.json'
            $metadata | ConvertTo-Json | Set-Content -Encoding UTF8 $metadataPath
            $timestamp = if ($config.TimestampUrl) { $config.TimestampUrl } else { 'http://timestamp.acs.microsoft.com' }
            try {
                & $signtool @common /tr $timestamp /dlib $dlib /dmdf $metadataPath $File
                if ($LASTEXITCODE -ne 0) { throw "SignTool failed ($LASTEXITCODE). See SIGNING.md > Troubleshooting." }
            }
            finally { Remove-Item $metadataPath -ErrorAction SilentlyContinue }
        }
        'Certificate' {
            $thumbprint = ($config.Certificate.Thumbprint -replace '[^0-9A-Fa-f]', '')
            if ($thumbprint.Length -ne 40) {
                # Help pick one: list the code-signing certificates Windows can see right now.
                $certs = @(Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue)
                if ($certs.Count -eq 0) {
                    throw 'Set Certificate.Thumbprint in signing.json. No code-signing certificate is installed yet: log in to your CA''s signing app (for Certum, SimplySign Desktop) or plug in the token.'
                }
                $list = ($certs | ForEach-Object { "  $($_.Thumbprint)  $($_.Subject)  (expires $($_.NotAfter.ToString('yyyy-MM-dd')))" }) -join "`n"
                throw "Set Certificate.Thumbprint in signing.json to one of these:`n$list"
            }
            $signtool = Find-SignTool $ToolsDir $config.SignToolPath
            Write-Host "SignTool: $signtool"
            $timestamp = if ($config.TimestampUrl) { $config.TimestampUrl } else { 'http://timestamp.digicert.com' }
            $store = if ($config.Certificate.MachineStore) { @('/sm') } else { @() }
            & $signtool @common /tr $timestamp /sha1 $thumbprint @store $File
            if ($LASTEXITCODE -ne 0) { throw "SignTool failed ($LASTEXITCODE). Is the token plugged in / the cloud signing app signed in?" }
        }
        default { throw "signing.json Method must be ArtifactSigning or Certificate, not '$($config.Method)'." }
    }

    # Check the result the way Windows will: chain to a trusted root, plus a timestamp.
    & $signtool verify /pa /tw $File
    if ($LASTEXITCODE -ne 0) { throw "The signature on $File did not verify." }
    $sig = Get-AuthenticodeSignature $File
    Write-Host "Signed by: $($sig.SignerCertificate.Subject)"
    Write-Host "Timestamp: $($sig.TimeStamperCertificate.Subject)"
}
