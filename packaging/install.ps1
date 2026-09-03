#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Build, register and verify the native messaging host for Chrome, Brave and Edge.

.DESCRIPTION
    One script that replaces the earlier register_native_host.ps1 / verify_install.ps1 pair and
    the hand-copied snippets in the testing docs. It:

      1. publishes the host in Release,
      2. writes the native messaging manifest to %LOCALAPPDATA%\WindowsDownloader,
      3. registers it under the correct per-browser registry key,
      4. removes stale registrations from earlier attempts,
      5. verifies the host answers a real native-messaging handshake.

.PARAMETER ExtensionId
    The unpacked extension's ID from chrome://extensions. If omitted, the ID is reused from a
    previously written manifest.

.EXAMPLE
    .\install.ps1 -ExtensionId abcdefghijklmnopabcdefghijklmnop
#>

param(
    [string]$ExtensionId,
    [switch]$SkipBuild,
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$RepoRoot     = Split-Path -Parent $PSScriptRoot
$HostProject  = Join-Path $RepoRoot "native-host\DownloaderHost\DownloaderHost.csproj"
$PublishDir   = Join-Path $RepoRoot "native-host\DownloaderHost\bin\Release\net8.0"
$HostExe      = Join-Path $PublishDir "DownloaderHost.exe"
# The manifest lives outside the repo on purpose. Browsers hold an absolute path to it in the
# registry, so keeping it in the working tree means a git clean, a fresh clone, or any tidy-up
# deletes it and every registered browser starts reporting
# "Specified native messaging host not found" with nothing obviously wrong.
$ManifestDir  = Join-Path $env:LOCALAPPDATA "WindowsDownloader"
$ManifestPath = Join-Path $ManifestDir "com.downloader.host.json"
$LegacyManifestPath = Join-Path $PSScriptRoot "native_host.json"
$HostName     = "com.downloader.host"

function Write-Step($text) { Write-Host "`n=== $text" -ForegroundColor Cyan }

# Chromium derives an unpacked extension's ID from the SHA-256 of its absolute directory path
# (encoded UTF-16LE on Windows), taking the first 16 bytes and mapping each hex nibble to a-p.
# Deriving it here removes the copy-the-ID-from-chrome://extensions step that previously let the
# registered origin drift out of sync with the extension actually loaded.
function Get-UnpackedExtensionId([string]$path) {
    $full = (Get-Item -LiteralPath $path).FullName
    $sha = [System.Security.Cryptography.SHA256]::Create().ComputeHash([System.Text.Encoding]::Unicode.GetBytes($full))
    $hex = ($sha[0..15] | ForEach-Object { $_.ToString('x2') }) -join ''
    ($hex.ToCharArray() | ForEach-Object { [char]([int][char]'a' + [Convert]::ToInt32($_, 16)) }) -join ''
}

# --- Resolve the extension origins --------------------------------------------
$ExtensionDir = Join-Path $RepoRoot "extension"
if (-not (Test-Path $ExtensionDir)) { throw "Extension folder not found: $ExtensionDir" }

$DerivedId = Get-UnpackedExtensionId $ExtensionDir
$ids = [System.Collections.Generic.List[string]]::new()
$ids.Add($DerivedId)

if ($ExtensionId) {
    if ($ExtensionId -notmatch '^[a-p]{32}$') { throw "Not a valid extension ID: $ExtensionId" }
    if (-not $ids.Contains($ExtensionId)) { $ids.Add($ExtensionId) }
}

# Keep any origin already authorised so re-running does not lock out a working setup.
$carryOverFrom = if (Test-Path $ManifestPath) { $ManifestPath } elseif (Test-Path $LegacyManifestPath) { $LegacyManifestPath } else { $null }
if ($carryOverFrom) {
    $existing = Get-Content $carryOverFrom -Raw | ConvertFrom-Json
    foreach ($origin in @($existing.allowed_origins)) {
        if ($origin -match 'chrome-extension://([a-p]{32})/' -and -not $ids.Contains($Matches[1])) {
            $ids.Add($Matches[1])
        }
    }
}

# --- Build --------------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Step "Publishing native host (Release)"

    # A host left running holds a lock on the exe. But it may also be serving a live download,
    # and killing it loses every byte on a non-resumable source -- so check before killing.
    $running = @(Get-Process DownloaderHost -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        $downloads = Join-Path $env:USERPROFILE "Downloads"
        $active = @(Get-ChildItem $downloads -Filter "*.part" -ErrorAction SilentlyContinue |
            Where-Object { (New-TimeSpan -Start $_.LastWriteTime -End (Get-Date)).TotalSeconds -lt 30 })

        if ($active.Count -gt 0 -and -not $Force) {
            Write-Host ""
            Write-Host "REFUSING TO BUILD: a download appears to be in progress." -ForegroundColor Red
            foreach ($f in $active) {
                Write-Host ("  {0}  {1:N0} bytes, written {2:N0}s ago" -f $f.Name, $f.Length,
                    (New-TimeSpan -Start $f.LastWriteTime -End (Get-Date)).TotalSeconds) -ForegroundColor Red
            }
            Write-Host ""
            Write-Host "Stopping the host now would abort it, and a non-resumable source cannot" -ForegroundColor Yellow
            Write-Host "recover those bytes. Wait for it to finish, or re-run with -Force." -ForegroundColor Yellow
            exit 1
        }

        foreach ($proc in $running) {
            Write-Host "  stopping host pid $($proc.Id)" -ForegroundColor Yellow
            Stop-Process -Id $proc.Id -Force
        }
        Start-Sleep -Milliseconds 400
    }

    dotnet publish $HostProject -c Release -o $PublishDir --nologo | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}

if (-not (Test-Path $HostExe)) { throw "Host executable not found at $HostExe" }
Write-Host "  host: $HostExe" -ForegroundColor Gray

# --- Manifest -----------------------------------------------------------------
Write-Step "Writing native messaging manifest"

# NOTE: Chromium requires "allowed_origins" (with the chrome-extension:// scheme).
# "allowed_extensions" is the Firefox spelling and is silently ignored by Chrome/Brave/Edge.
$manifest = [ordered]@{
    name            = $HostName
    description     = "Windows Downloader native host"
    path            = $HostExe
    type            = "stdio"
    allowed_origins = @($ids | ForEach-Object { "chrome-extension://$_/" })
}

New-Item -ItemType Directory -Path $ManifestDir -Force | Out-Null
$manifest | ConvertTo-Json -Depth 5 | Set-Content -Path $ManifestPath -Encoding UTF8

# A manifest left in the repo from an older install would be stale and misleading.
if (Test-Path $LegacyManifestPath) {
    Remove-Item $LegacyManifestPath -Force
    Write-Host "  removed stale in-repo manifest: $LegacyManifestPath" -ForegroundColor Yellow
}
Write-Host "  manifest: $ManifestPath" -ForegroundColor Gray
foreach ($id in $ids) {
    $note = if ($id -eq $DerivedId) { "  <- $ExtensionDir" } else { "" }
    Write-Host "  origin:   chrome-extension://$id/$note" -ForegroundColor Gray
}

# --- Registration -------------------------------------------------------------
Write-Step "Registering with browsers"

# Brave's key is 'Brave-Browser', NOT 'Brave' -- an earlier version of this repo registered
# the wrong path, so Brave never saw the host.
$targets = [ordered]@{
    "Chrome" = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$HostName"
    "Brave"  = "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\$HostName"
    "Edge"   = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$HostName"
}

foreach ($name in $targets.Keys) {
    $path = $targets[$name]
    New-Item -Path $path -Force | Out-Null
    New-ItemProperty -Path $path -Name '(Default)' -Value $ManifestPath -PropertyType String -Force | Out-Null
    Write-Host "  $name -> $ManifestPath" -ForegroundColor Gray
}

# --- Clean up stale registrations ---------------------------------------------
Write-Step "Removing stale registrations"

$stale = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.downloader.nativehost",
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\com.downloader.nativehost",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\com.downloader.nativehost",
    "HKCU:\Software\BraveSoftware\Brave\NativeMessagingHosts\$HostName"
)

$removed = 0
foreach ($path in $stale) {
    if (Test-Path $path) {
        Remove-Item -Path $path -Recurse -Force
        Write-Host "  removed $path" -ForegroundColor Yellow
        $removed++
    }
}
if ($removed -eq 0) { Write-Host "  none found" -ForegroundColor Gray }

# --- Warn about extensions loaded from somewhere other than the repo ----------
Write-Step "Checking which folder the browsers actually load"

$browserProfiles = @(
    @{ Name = "Chrome"; Root = "$env:LOCALAPPDATA\Google\Chrome\User Data" },
    @{ Name = "Brave";  Root = "$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\User Data" },
    @{ Name = "Edge";   Root = "$env:LOCALAPPDATA\Microsoft\Edge\User Data" }
)

$foundLoaded = $false
foreach ($browser in $browserProfiles) {
    if (-not (Test-Path $browser.Root)) { continue }

    $profileDirs = Get-ChildItem $browser.Root -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -eq 'Default' -or $_.Name -like 'Profile*' }

    foreach ($dir in $profileDirs) {
        $prefsFile = Join-Path $dir.FullName "Secure Preferences"
        if (-not (Test-Path $prefsFile)) { continue }

        try { $prefs = Get-Content $prefsFile -Raw | ConvertFrom-Json } catch { continue }
        $settings = $prefs.extensions.settings
        if (-not $settings) { continue }

        foreach ($id in $ids) {
            $entry = $settings.$id
            if (-not $entry -or -not $entry.path) { continue }

            $foundLoaded = $true
            if ($entry.path -ieq $ExtensionDir) {
                Write-Host "  $($browser.Name)/$($dir.Name): loaded from the repo folder (id $id)" -ForegroundColor Green
            } else {
                Write-Host "  $($browser.Name)/$($dir.Name): loaded from $($entry.path)" -ForegroundColor Yellow
                Write-Host "     This is NOT the repo folder, so edits under $ExtensionDir have no effect." -ForegroundColor Yellow
                Write-Host "     Remove that extension and 'Load unpacked' -> $ExtensionDir" -ForegroundColor Yellow
            }
        }
    }
}

if (-not $foundLoaded) {
    Write-Host "  No matching extension is loaded yet." -ForegroundColor Gray
    Write-Host "  Load unpacked -> $ExtensionDir (it will get id $DerivedId)" -ForegroundColor Gray
}

# --- Verify -------------------------------------------------------------------
Write-Step "Verifying every registered browser points at a manifest that exists"

foreach ($name in $targets.Keys) {
    $value = (Get-ItemProperty -Path $targets[$name] -ErrorAction SilentlyContinue).'(default)'
    if (-not $value) {
        Write-Host "  $name : NOT REGISTERED" -ForegroundColor Red
    } elseif (-not (Test-Path $value)) {
        Write-Host "  $name : points at a missing file -> $value" -ForegroundColor Red
    } else {
        Write-Host "  $name : ok" -ForegroundColor Green
    }
}

Write-Step "Verifying the host answers a native-messaging handshake"

& (Join-Path $PSScriptRoot "test_host_e2e.ps1") -ExtensionId $DerivedId -PingOnly
if ($LASTEXITCODE -ne 0) { throw "Host handshake verification failed" }

Write-Step "Done"
Write-Host "Next: reload the extension at chrome://extensions, then click the toolbar icon" -ForegroundColor Green
Write-Host "The popup header should read 'host connected'." -ForegroundColor Green
