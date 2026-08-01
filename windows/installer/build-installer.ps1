<#
.SYNOPSIS
    Builds the Conduit Windows installer (ConduitSetup-<version>.exe).

.DESCRIPTION
    1. Publishes Conduit.App as a self-contained win-x64 app (no .NET needed on the
       target PC) into windows\artifacts\publish.
    2. Compiles installer\Conduit.iss with Inno Setup into
       windows\artifacts\installer\ConduitSetup-<version>.exe.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
    powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1 -Version 1.1.0
#>
param(
    [string]$Version = "1.0.0",
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"

# Paths (this script lives in windows\installer).
$InstallerDir = $PSScriptRoot
$WindowsDir   = Split-Path $InstallerDir -Parent
$Project      = Join-Path $WindowsDir "src\Conduit.App\Conduit.App.csproj"
$PublishDir   = Join-Path $WindowsDir "artifacts\publish"
$IssFile      = Join-Path $InstallerDir "Conduit.iss"

Write-Host "==> Publishing $Runtime self-contained ($Configuration) ..." -ForegroundColor Cyan
if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
dotnet publish $Project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=false `
    -o $PublishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)." }

# Locate the Inno Setup compiler (ISCC.exe).
$IsccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
)
$Iscc = $IsccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $Iscc) {
    throw "Inno Setup (ISCC.exe) not found. Install it with:  winget install JRSoftware.InnoSetup"
}
Write-Host "==> Using compiler: $Iscc" -ForegroundColor Cyan

Write-Host "==> Compiling installer (v$Version) ..." -ForegroundColor Cyan
& $Iscc "/DAppVersion=$Version" $IssFile
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed (exit $LASTEXITCODE)." }

$Setup = Join-Path $WindowsDir "artifacts\installer\ConduitSetup-$Version.exe"
Write-Host ""
Write-Host "Done. Installer:" -ForegroundColor Green
Write-Host "  $Setup" -ForegroundColor Green
