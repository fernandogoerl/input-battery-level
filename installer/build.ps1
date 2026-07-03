<#
.SYNOPSIS
  Publishes the self-contained app and builds the Windows installer.

.DESCRIPTION
  1. dotnet publish -> a single self-contained BatteryTray.exe in .\publish
  2. Inno Setup (ISCC) -> .\dist\BatteryTray-Setup-<version>.exe

  No admin required. Outputs the path to the finished installer.
#>
param(
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

# --- locate dotnet -------------------------------------------------------
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = 'C:\Program Files\dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { throw "dotnet SDK not found. Install Microsoft.DotNet.SDK.8." }

# --- locate ISCC (Inno Setup compiler) -----------------------------------
$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "ISCC.exe not found. Install with: winget install JRSoftware.InnoSetup" }

# --- publish -------------------------------------------------------------
$publishDir = Join-Path $repo 'publish'
Write-Host "Publishing ($Configuration / $Runtime)..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $repo 'src\BatteryTray\BatteryTray.csproj') `
    -c $Configuration -r $Runtime --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)." }

# --- compile installer ---------------------------------------------------
Write-Host "Building installer with $iscc..." -ForegroundColor Cyan
& $iscc (Join-Path $PSScriptRoot 'BatteryTray.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

$setup = Get-ChildItem (Join-Path $repo 'dist') -Filter 'BatteryTray-Setup-*.exe' |
         Sort-Object LastWriteTime -Descending | Select-Object -First 1
Write-Host "`nInstaller ready: $($setup.FullName)" -ForegroundColor Green
Write-Host "Size: $([Math]::Round($setup.Length/1MB,1)) MB"
