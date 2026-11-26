<#
.SYNOPSIS
  Builds AL-76 for Windows from the source and, if Inno Setup 6 is installed, its installer.

.DESCRIPTION
  Runs the core tests, publishes a self-contained app (no .NET needed on the target PC) into windows\out\<rid>
  and compiles windows\packaging\AL-76.iss into windows\out\<AppName>-Setup-<version>-<arch>.exe.
  Works on Windows (PowerShell 5.1 or 7) and on macOS/Linux with PowerShell 7 (the installer step needs Windows).

  Needs: the .NET 10 SDK (winget install Microsoft.DotNet.SDK.10) and, for the installer,
  Inno Setup 6 (winget install JRSoftware.InnoSetup).

.EXAMPLE
  .\windows\scripts\build.ps1
  Tests, app for x64 and installer AL-76-Setup-0.1.4-x64.exe.

.EXAMPLE
  .\windows\scripts\build.ps1 -AppName "AL-76 Lab" -Publisher "Your name" -AppId ([guid]::NewGuid())
  Your own build: another name, publisher and installer id, so it installs next to the official AL-76
  instead of replacing it. Make the id once with [guid]::NewGuid() and keep it for your updates.
#>
param(
    [ValidateSet('x64', 'arm64')] [string] $Arch = 'x64',
    [switch] $SkipTests,
    [switch] $NoInstaller,
    [string] $AppName = '',
    [string] $Publisher = '',
    [string] $AppId = ''
)
$ErrorActionPreference = 'Stop'
$windows = Split-Path $PSScriptRoot -Parent
Set-Location $windows
$rid = "win-$Arch"
$out = Join-Path $windows "out\$rid"
function Step($text) { Write-Host "`n== $text" -ForegroundColor Cyan }
function Check($what) { if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)." } }

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK not found. Install the .NET 10 SDK: winget install Microsoft.DotNet.SDK.10'
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'; $env:DOTNET_NOLOGO = '1'

if (-not $SkipTests) {
    Step 'Tests'
    dotnet test tests/AL76.Core.Tests --nologo -v q; Check 'The tests'
}

Step "App for $rid"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }
dotnet publish src/AL76.App -c Release -r $rid --self-contained -o $out --nologo -v q; Check 'dotnet publish'

# Whisper.net copies whisper.cpp for every system: keep only this one (it loads from runtimes\<rid>).
Get-ChildItem (Join-Path $out 'runtimes') -Directory -ErrorAction SilentlyContinue | Where-Object Name -ne $rid | Remove-Item -Recurse -Force
Remove-Item (Join-Path $out 'ggml-metal.metal') -ErrorAction SilentlyContinue
Get-ChildItem $out -Filter *.lib | Remove-Item

# Kokoro voices: the Italian (i*) and English (a*, b*) ones and their licence, from the NuGet package.
$nuget = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $HOME '.nuget/packages' }
$source = Get-ChildItem (Join-Path $nuget 'kokorosharp*') -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { Get-ChildItem $_.FullName -Directory } |
    ForEach-Object { Join-Path $_.FullName 'content/voices' } | Where-Object { Test-Path $_ } | Select-Object -Last 1
$voices = Join-Path $out 'voices'
New-Item $voices -ItemType Directory -Force | Out-Null
if ($source) {
    Get-ChildItem $source -File | Where-Object { $_.Name -match '^[abi]._.*\.npy$' -or $_.Name -eq 'LICENSE' } | Copy-Item -Destination $voices
}
Get-ChildItem $voices | Where-Object { $_.PSIsContainer -or ($_.Extension -eq '.npy' -and $_.Name -notmatch '^[abi]._') } | Remove-Item -Recurse -Force
if (-not (Get-ChildItem $voices -Filter *.npy)) { Write-Warning 'No Kokoro voices found: AL-76 will use the voices built into Windows.' }
Write-Host "OK  $out\AL-76.exe"

if ($NoInstaller) { return }
Step 'Installer'
if (-not $IsWindows -and $PSVersionTable.PSEdition -eq 'Core') { Write-Warning 'The installer is built on Windows only (Inno Setup).'; return }
$iscc = @(
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it (winget install JRSoftware.InnoSetup) or run with -NoInstaller.' }
$defines = @("/DRid=$rid", "/DArch=$Arch")
if ($AppName) { $defines += "/DAppName=$AppName" }
if ($Publisher) { $defines += "/DPublisher=$Publisher" }
if ($AppId) { $defines += "/DAppId=$($AppId.Trim('{}'))" }
& $iscc @defines (Join-Path $windows 'packaging\AL-76.iss'); Check 'Inno Setup'
Get-ChildItem (Join-Path $windows 'out') -Filter '*-Setup-*.exe' | Sort-Object LastWriteTime | Select-Object -Last 1 |
    ForEach-Object { Write-Host "OK  $($_.FullName)" -ForegroundColor Green }
