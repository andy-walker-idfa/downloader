#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Test the native host probe functionality against a real URL.

.DESCRIPTION
    This script tests the tier detection by sending a probe command to the native host
    and displaying the results in human-readable format.

.PARAMETER Url
    The URL to probe. Defaults to a known Tier 0 (fully resumable) GitHub release.

.EXAMPLE
    .\test-probe.ps1 -Url "https://github.com/torvalds/linux/archive/refs/tags/v6.0.tar.gz"

.NOTES
    Requires: .NET 8, native host built with Release configuration
#>

param(
    [string]$Url = "https://github.com/torvalds/linux/archive/refs/tags/v6.0.tar.gz"
)

$ErrorActionPreference = "Stop"

# Setup paths
$RepoRoot = if ($PSScriptRoot) { $PSScriptRoot } else { Get-Location }
$HostExe = Join-Path $RepoRoot "native-host\DownloaderHost\bin\Release\net8.0\DownloaderHost.exe"

Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host "  RESUMABILITY TIER DETECTION TEST" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

# Validate host exists
if (-not (Test-Path $HostExe)) {
    Write-Host "ERROR: Native host not found!" -ForegroundColor Red
    Write-Host "Expected at: $HostExe" -ForegroundColor Red
    Write-Host ""
    Write-Host "Fix: Run 'dotnet publish -c Release' in native-host/DownloaderHost" -ForegroundColor Yellow
    exit 1
}

Write-Host "Target URL:" -ForegroundColor Green
Write-Host "  $Url" -ForegroundColor White
Write-Host ""

# Create probe command
$probeCmd = @{
    cmd = "probe"
    url = $Url
} | ConvertTo-Json -Compress

Write-Host "Sending probe request..." -ForegroundColor Gray

# Prepare native messaging format (4-byte little-endian length + JSON)
$jsonBytes = [System.Text.Encoding]::UTF8.GetBytes($probeCmd)
$lengthBytes = [System.BitConverter]::GetBytes($jsonBytes.Length)

# Start process with redirected I/O
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $HostExe
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$process = [System.Diagnostics.Process]::Start($psi)

# Write request to stdin
$stdin = $process.StandardInput.BaseStream
try {
    $stdin.Write($lengthBytes, 0, 4)
    $stdin.Write($jsonBytes, 0, $jsonBytes.Length)
    $stdin.Flush()
    $stdin.Close()
} catch {
    Write-Host "ERROR: Failed to write to host stdin" -ForegroundColor Red
    $process.Kill()
    exit 1
}

# Read response from stdout
$stdout = $process.StandardOutput.BaseStream
$stderr = $process.StandardError

try {
    # Read 4-byte length prefix
    $lenBuffer = New-Object byte[] 4
    $bytesRead = $stdout.Read($lenBuffer, 0, 4)
    
    if ($bytesRead -ne 4) {
        Write-Host "ERROR: Failed to read response length" -ForegroundColor Red
        $process.Kill()
        exit 1
    }

    $responseLength = [System.BitConverter]::ToInt32($lenBuffer, 0)
    
    # Read JSON response
    $respBuffer = New-Object byte[] $responseLength
    $stdout.Read($respBuffer, 0, $responseLength) | Out-Null
    $responseJson = [System.Text.Encoding]::UTF8.GetString($respBuffer)

    # Wait for process to exit
    $process.WaitForExit()

    # Parse response
    try {
        $response = $responseJson | ConvertFrom-Json
    } catch {
        Write-Host "ERROR: Failed to parse response JSON" -ForegroundColor Red
        Write-Host "Raw response: $responseJson" -ForegroundColor Red
        exit 1
    }

    # Display results
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host "  RESULTS" -ForegroundColor Cyan
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan
    Write-Host ""

    if ($response.status -eq "error") {
        Write-Host "Status: " -ForegroundColor Red -NoNewline
        Write-Host "ERROR" -ForegroundColor Red
        Write-Host "Message: $($response.message)" -ForegroundColor Red
        exit 1
    }

    # Color-code tier
    $tierColor = switch ($response.tier) {
        "FullyResumable" { "Green" }
        "ResumableUnverified" { "Yellow" }
        "NotResumable" { "Red" }
        "UnboundedStream" { "Red" }
        default { "White" }
    }

    $resumableText = if ($response.resumable) { "YES ✓" } else { "NO ✗" }
    $resumableColor = if ($response.resumable) { "Green" } else { "Red" }

    Write-Host "Tier: " -NoNewline
    Write-Host $response.tier -ForegroundColor $tierColor
    
    Write-Host "Resumable: " -NoNewline
    Write-Host $resumableText -ForegroundColor $resumableColor
    
    if ($response.contentLength) {
        $sizeGB = [math]::Round($response.contentLength / 1GB, 2)
        $sizeMB = [math]::Round($response.contentLength / 1MB, 2)
        if ($sizeGB -ge 1) {
            Write-Host "Content-Length: $sizeGB GB"
        } else {
            Write-Host "Content-Length: $sizeMB MB"
        }
    } else {
        Write-Host "Content-Length: (unknown)"
    }

    if ($response.etag) {
        $etagType = if ($response.etag.StartsWith('W/')) { "Weak" } else { "Strong" }
        Write-Host "ETag: $($response.etag) ($etagType)"
    } else {
        Write-Host "ETag: (none)"
    }

    if ($response.lastModified) {
        Write-Host "Last-Modified: $($response.lastModified)"
    }

    Write-Host ""
    Write-Host "─────────────────────────────────────────────────────────────" -ForegroundColor Gray
    Write-Host ""

    # Explain the tier
    $explanation = switch ($response.tier) {
        "FullyResumable" {
            "TIER 0: Fully Resumable`n" +
            "  • Server supports HTTP range requests (206 Partial Content)`n" +
            "  • Has a strong ETag for validation`n" +
            "  • Resume is safe and reliable`n" +
            "  • Can use parallel segments (future feature)"
        }
        "ResumableUnverified" {
            "TIER 1: Resumable but Unverified`n" +
            "  • Server supports HTTP range requests (206 Partial Content)`n" +
            "  • No strong ETag (or missing validator)`n" +
            "  • Resume is possible but less safe`n" +
            "  • Should verify with overlap check before resuming"
        }
        "NotResumable" {
            "TIER 2: Not Resumable`n" +
            "  • Server does NOT support HTTP range requests`n" +
            "  • Returns 200 OK instead of 206 Partial Content`n" +
            "  • OR explicitly advertises 'Accept-Ranges: none'`n" +
            "  • On interrupt: must restart from byte 0`n" +
            "  • Consider using mirror fallback"
        }
        "UnboundedStream" {
            "TIER 3: Unbounded Stream`n" +
            "  • Server does not provide Content-Length`n" +
            "  • Uses chunked transfer encoding`n" +
            "  • Progress bar cannot be shown`n" +
            "  • Resume not possible`n" +
            "  • Must restart from byte 0 on interrupt"
        }
        default {
            "Unknown tier: $($response.tier)"
        }
    }

    Write-Host $explanation -ForegroundColor Gray
    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════════" -ForegroundColor Cyan

} catch {
    Write-Host "ERROR: $_" -ForegroundColor Red
    $process.Kill()
    exit 1
}
