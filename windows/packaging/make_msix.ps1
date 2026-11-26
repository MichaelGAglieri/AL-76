# Builds the MSIX package of AL-76 on Windows (needs the Windows SDK: makeappx.exe and signtool.exe).
#   .\make_msix.ps1 -Rid win-x64                       unsigned, for the Microsoft Store (the Store signs it)
#   .\make_msix.ps1 -Rid win-arm64 -TestCertificate    signed with a self-made certificate, to install and try it
# Input: ..\out\<rid> made by scripts/build.sh on the Mac (copied to the PC). Output: ..\out\AL-76_<version>_<arch>.msix
param(
    [ValidateSet("win-x64", "win-arm64")] [string]$Rid = "win-x64",
    [string]$Version = "0.1.5.0",
    [string]$Publisher = "CN=AL-76 Test",
    [string]$PublisherName = "AL-76",
    [switch]$TestCertificate
)
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$source = Join-Path $here "..\out\$Rid"
if (-not (Test-Path (Join-Path $source "AL-76.exe"))) { throw "Manca $source\AL-76.exe: prima scripts/build.sh sul Mac." }
$arch = if ($Rid -eq "win-arm64") { "arm64" } else { "x64" }

$sdk = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" -ErrorAction SilentlyContinue | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $sdk) { throw "makeappx.exe non trovato: installa il Windows SDK (winget install Microsoft.WindowsSDK.10.0.26100)." }
$makeappx = $sdk.FullName
$signtool = Join-Path $sdk.DirectoryName "signtool.exe"

# The package's folder: the app without development files, the logos, the manifest.
$layout = Join-Path $env:TEMP "al76-msix-$arch"
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
Copy-Item $source $layout -Recurse
Remove-Item (Join-Path $layout "dev.flag"), (Join-Path $layout "prove"), (Join-Path $layout "AL76.Bench.exe") -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item (Join-Path $here "assets") (Join-Path $layout "assets") -Recurse
(Get-Content (Join-Path $here "AppxManifest.xml") -Raw) `
    -replace '\$VERSION\$', $Version -replace '\$ARCH\$', $arch `
    -replace '\$PUBLISHER\$', $Publisher -replace '\$PUBLISHERNAME\$', $PublisherName |
    Set-Content (Join-Path $layout "AppxManifest.xml") -Encoding UTF8

$out = Join-Path $here "..\out\AL-76_${Version}_$arch.msix"
& $makeappx pack /o /d $layout /p $out
if ($LASTEXITCODE -ne 0) { throw "makeappx non è riuscito." }

if ($TestCertificate) {
    # A certificate only for this PC: trusted here, so the package installs; never for distribution.
    $cert = Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -eq $Publisher | Select-Object -First 1
    if (-not $cert) {
        $cert = New-SelfSignedCertificate -Type Custom -Subject $Publisher -KeyUsage DigitalSignature -FriendlyName "AL-76 test" `
            -CertStoreLocation Cert:\CurrentUser\My -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
    }
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint $out
    if ($LASTEXITCODE -ne 0) { throw "signtool non è riuscito." }
    Write-Host "Per installarlo: fidati del certificato (Persone attendibili, da amministratore), poi doppio clic sul .msix." -ForegroundColor Yellow
}
Write-Host "Pronto: $out" -ForegroundColor Green
