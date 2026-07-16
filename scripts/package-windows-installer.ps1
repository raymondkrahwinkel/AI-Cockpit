<#
.SYNOPSIS
    Packages the cockpit as a Windows installer: Setup.exe that installs the app, adds Start-menu and desktop
    shortcuts, and registers an uninstaller.

.DESCRIPTION
    The other half of the portable single-file exe the release already ships. This wraps that same published
    Cockpit.App.exe in an Inno Setup installer, so it appears in "Apps & features" and updates in place instead
    of being a loose file the operator keeps somewhere.

    Inno Setup's compiler (iscc) is used if it is on PATH or in its usual install location, and installed with
    Chocolatey otherwise — the same "fetch the tool if it is missing" approach package-appimage.sh takes with
    appimagetool, so a fresh runner needs nothing set up first.

    The version is display-only in the installer; the numeric VersionInfo is the VersionPrefix (the part before
    any '-nightly'/'-rc' suffix), because a Win32 file version has to be numeric.

.PARAMETER PublishDir
    The self-contained win-x64 publish folder that holds Cockpit.App.exe.

.PARAMETER Version
    The full display version, e.g. 0.3.0-nightly.42 or 1.2.3. Defaults to the project's VersionPrefix.

.PARAMETER OutputPath
    Where to write the Setup.exe. Defaults to artifacts/windows/AI-Cockpit-<version>-Setup.exe.

.EXAMPLE
    scripts/package-windows-installer.ps1 -PublishDir publish/win-x64 -Version 0.3.0-nightly.42
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [string]$Version,
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$project = Join-Path $repoRoot 'src/Cockpit.App/Cockpit.App.csproj'
$issScript = Join-Path $PSScriptRoot 'windows-installer.iss'

if (-not $Version) {
    $Version = (dotnet msbuild $project -getProperty:VersionPrefix).Trim()
}

# The numeric file version cannot carry a '-nightly.42'/'-rc.1' suffix — take the leading x.y.z only.
$numericVersion = ($Version -split '-', 2)[0]

$sourceExe = Join-Path $PublishDir 'Cockpit.App.exe'
if (-not (Test-Path $sourceExe)) {
    throw "Published exe not found at '$sourceExe'. Publish win-x64 first (see the release workflow)."
}
$sourceExe = (Resolve-Path $sourceExe).Path

if (-not $OutputPath) {
    $OutputPath = Join-Path $repoRoot "artifacts/windows/AI-Cockpit-$Version-Setup.exe"
}
$outputDir = Split-Path -Parent $OutputPath
$outputBase = [System.IO.Path]::GetFileNameWithoutExtension($OutputPath)
New-Item -ItemType Directory -Force -Path $outputDir | Out-Null

# Find the Inno Setup compiler, or install it. Prefer PATH, then the default install location, then Chocolatey.
function Resolve-Iscc {
    $onPath = Get-Command iscc.exe -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    foreach ($candidate in @(
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe")) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    return $null
}

$iscc = Resolve-Iscc
if (-not $iscc) {
    Write-Host 'Inno Setup not found — installing it with Chocolatey…'
    choco install innosetup -y --no-progress
    $iscc = Resolve-Iscc
}
if (-not $iscc) {
    throw 'Inno Setup compiler (ISCC.exe) is not available and could not be installed.'
}

Write-Host "Building installer for version $Version → $OutputPath"
& $iscc `
    "/DSourceExe=$sourceExe" `
    "/DAppVersion=$Version" `
    "/DAppVersionNumeric=$numericVersion" `
    "/DOutputDir=$outputDir" `
    "/DOutputBase=$outputBase" `
    $issScript

if ($LASTEXITCODE -ne 0) {
    throw "iscc failed with exit code $LASTEXITCODE."
}

Write-Host "Installer written to $OutputPath"
