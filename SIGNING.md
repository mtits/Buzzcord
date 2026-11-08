# Signing Buzzcord

Windows 11's Smart App Control blocks unsigned programs outright, so release builds of `Buzzcord.exe` are
code-signed. `build.ps1 -Sign` does it with SignTool, configured by `signing.json` (not committed; copy
`signing.sample.json`). `tools\signing.ps1` does the work and checks the result the way Windows will.

## Azure Artifact Signing (what official releases use)

1. Have an Azure Artifact Signing account (formerly Trusted Signing) with a validated identity and a
   **Public Trust** certificate profile.
2. Make sure your Azure user has the **Artifact Signing Certificate Profile Signer** role on that account.
3. Get the tools once: `powershell -ExecutionPolicy Bypass -File tools\get-signing-tools.ps1` (SignTool and
   the Artifact Signing dlib from nuget.org). The dlib also needs the .NET 8 runtime (x64).
4. Install the Azure CLI and run `az login` with that Azure user.
5. In `signing.json`, set `Method` to `ArtifactSigning` and fill in `Endpoint` (it must match the
   account's region, e.g. `https://eus.codesigning.azure.net`), `CodeSigningAccountName` and
   `CertificateProfileName`.
6. Build: `powershell -ExecutionPolicy Bypass -File build.ps1 -Sign -Test -Package`.

## Any other code-signing certificate

Set `Method` to `Certificate` and `Certificate.Thumbprint` to the certificate's SHA-1 thumbprint. It works with
OV/EV certificates on a USB token or in a CA's cloud signing service, as long as Windows can see the
certificate in the user's certificate store.

Microsoft's docs: [Artifact Signing quickstart](https://learn.microsoft.com/azure/artifact-signing/quickstart).
