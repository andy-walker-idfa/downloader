#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Stage an MSIX package layout for the desktop app plus the native host.

.DESCRIPTION
    Produces a folder that can be registered directly (Add-AppxPackage -Register, developer
    mode) or packed into an .msix with makeappx. The host is placed beside the app because
    DownloaderService.ResolveHostPath looks there first -- that is the packaged layout.

    This is a spike, not a release build. The payload is framework-dependent, so it needs the
    .NET 8 desktop runtime on the machine; a shipping package should be self-contained or
    declare the framework dependency.
#>

param(
    # Defaults to LOCALAPPDATA, not the repo: Windows refuses to deploy an MSIX from exFAT, and
    # a working tree on a data drive is often exFAT. Failure mode is
    # "0x80073CFD ... cannot deploy to path layout of file system type exFAT".
    [string]$LayoutDir = (Join-Path $env:LOCALAPPDATA "WindowsDownloader\msix\layout")
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step($t) { Write-Host "`n=== $t" -ForegroundColor Cyan }

# --- Deployment needs NTFS ----------------------------------------------------
$targetRoot = [System.IO.Path]::GetPathRoot((Split-Path -Parent $LayoutDir))
$volume = Get-Volume -DriveLetter $targetRoot.Substring(0,1) -ErrorAction SilentlyContinue
if ($volume -and $volume.FileSystemType -ne 'NTFS') {
    throw @"
$targetRoot is $($volume.FileSystemType); Windows can only deploy MSIX packages from NTFS.
Pass -LayoutDir pointing at an NTFS volume, e.g.
  -LayoutDir "$env:LOCALAPPDATA\WindowsDownloader\msix\layout"
"@
}
Write-Host "  target volume: $targetRoot ($(if ($volume) { $volume.FileSystemType } else { 'unknown' }))" -ForegroundColor Gray

Write-Step "Staging layout: $LayoutDir"
if (Test-Path $LayoutDir) { Remove-Item $LayoutDir -Recurse -Force }
New-Item -ItemType Directory -Path $LayoutDir -Force | Out-Null

# A browser-connected host locks the exe. Same rule as install.ps1: never stop one that is
# mid-download, because a non-resumable source loses every byte.
$running = @(Get-Process DownloaderHost -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $active = @(Get-ChildItem (Join-Path $env:USERPROFILE "Downloads") -Filter "*.part" -ErrorAction SilentlyContinue |
        Where-Object { (New-TimeSpan -Start $_.LastWriteTime -End (Get-Date)).TotalSeconds -lt 30 })
    if ($active.Count -gt 0) {
        throw "A download is in progress ($($active[0].Name)). Wait for it to finish before rebuilding."
    }
    Write-Step "Stopping $($running.Count) idle host process(es) holding the binary"
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 500
}

Write-Step "Publishing the desktop app"
dotnet publish (Join-Path $RepoRoot "app\DownloaderAppWpf\DownloaderAppWpf.csproj") `
    -c Release -o $LayoutDir --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "app publish failed" }

Write-Step "Publishing the native host beside it"
# Beside the app on purpose: ResolveHostPath checks its own directory first, so the packaged
# app finds the host without any path configuration.
dotnet publish (Join-Path $RepoRoot "native-host\DownloaderHost\DownloaderHost.csproj") `
    -c Release -o $LayoutDir --nologo | Out-Null
if ($LASTEXITCODE -ne 0) { throw "host publish failed" }

Write-Step "Copying package assets and manifest"
Copy-Item (Join-Path $PSScriptRoot "Assets") (Join-Path $LayoutDir "Assets") -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "AppxManifest.xml") (Join-Path $LayoutDir "AppxManifest.xml") -Force

foreach ($required in @("DownloaderAppWpf.exe", "DownloaderHost.exe", "AppxManifest.xml", "Assets\StoreLogo.png")) {
    $p = Join-Path $LayoutDir $required
    if (-not (Test-Path $p)) { throw "missing from layout: $required" }
}

$size = [math]::Round(((Get-ChildItem $LayoutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "  layout ready: $size MB, $((Get-ChildItem $LayoutDir -Recurse -File).Count) files" -ForegroundColor Green
Write-Host ""
Write-Host "Register it (requires Developer Mode):" -ForegroundColor Cyan
Write-Host "  Add-AppxPackage -Register `"$LayoutDir\AppxManifest.xml`""
