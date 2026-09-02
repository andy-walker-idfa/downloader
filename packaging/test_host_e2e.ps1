#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Drive the native host exactly the way a browser does: launch it with the caller-origin
    argument, keep the pipe open, and read every framed message until the download finishes.

.DESCRIPTION
    This is the missing piece for "real" testing. The older test scripts wrote one message,
    closed stdin and read exactly one reply -- which is what chrome.runtime.sendNativeMessage
    does, and why downloads appeared to start and then vanish. A download emits several
    messages (started -> progress... -> finished), so the reader must loop.

    Use this to confirm the host is healthy BEFORE blaming the browser wiring.

.PARAMETER Url
    File to download. Defaults to a small, reliably range-capable file.

.PARAMETER PingOnly
    Only perform the handshake check, no download.

.PARAMETER Interrupt
    Kill the host mid-download after N seconds to exercise crash recovery, then report what
    .part / .part.meta state was left behind.

.PARAMETER Cancel
    Send a graceful 'cancel' command after N seconds instead of killing the process, to verify
    the host stops cleanly and leaves resumable state behind.

.EXAMPLE
    .\test_host_e2e.ps1
    .\test_host_e2e.ps1 -Url "https://www.kernel.org/pub/linux/kernel/v6.x/linux-6.0.tar.xz" -Interrupt 5
#>

param(
    [string]$Url = "https://www.kernel.org/pub/linux/kernel/v6.x/linux-6.0.tar.xz",
    [string]$OutDir = "$env:TEMP\downloader-e2e",
    [string]$ExtensionId = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    [switch]$PingOnly,
    [int]$Interrupt = 0,
    [int]$Cancel = 0
)

$ErrorActionPreference = "Stop"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$HostExe  = Join-Path $RepoRoot "native-host\DownloaderHost\bin\Release\net8.0\DownloaderHost.exe"

if (-not (Test-Path $HostExe)) {
    Write-Host "ERROR: host not built. Run: dotnet publish -c Release -o bin\Release\net8.0" -ForegroundColor Red
    exit 1
}

New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# Chrome passes the caller origin as argv[0] (and a native window handle on Windows).
$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = $HostExe
$psi.Arguments = "chrome-extension://$ExtensionId/"
$psi.UseShellExecute = $false
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.CreateNoWindow = $true

$proc = [System.Diagnostics.Process]::Start($psi)
$stdin = $proc.StandardInput.BaseStream
$stdout = $proc.StandardOutput.BaseStream

function Send-HostMessage($obj) {
    $json = $obj | ConvertTo-Json -Compress
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $len = [System.BitConverter]::GetBytes($bytes.Length)
    $stdin.Write($len, 0, 4)
    $stdin.Write($bytes, 0, $bytes.Length)
    $stdin.Flush()
}

# Reads one length-prefixed frame, or $null if the pipe closed.
function Read-HostMessage {
    $lenBuf = New-Object byte[] 4
    $read = 0
    while ($read -lt 4) {
        $n = $stdout.Read($lenBuf, $read, 4 - $read)
        if ($n -eq 0) { return $null }
        $read += $n
    }

    $len = [System.BitConverter]::ToInt32($lenBuf, 0)
    if ($len -le 0) { return $null }

    $buf = New-Object byte[] $len
    $read = 0
    while ($read -lt $len) {
        $n = $stdout.Read($buf, $read, $len - $read)
        if ($n -eq 0) { return $null }
        $read += $n
    }

    return [System.Text.Encoding]::UTF8.GetString($buf) | ConvertFrom-Json
}

Write-Host "Host: $HostExe" -ForegroundColor Gray
Write-Host "Origin arg: chrome-extension://$ExtensionId/" -ForegroundColor Gray
Write-Host ""

$exitCode = 0

try {
    # --- Handshake ------------------------------------------------------------
    Write-Host "-> ping" -ForegroundColor Cyan
    Send-HostMessage @{ cmd = "ping"; id = "ping-1" }

    $pong = Read-HostMessage
    if (-not $pong -or $pong.status -ne "pong") {
        Write-Host "FAIL: no pong from host" -ForegroundColor Red
        $exitCode = 1
    } else {
        Write-Host "<- pong (pid $($pong.pid))" -ForegroundColor Green
        Write-Host "   log: $($pong.logPath)" -ForegroundColor Gray
    }

    if ($PingOnly) { exit $exitCode }

    # --- Download -------------------------------------------------------------
    $fileName = [System.IO.Path]::GetFileName(([Uri]$Url).LocalPath)
    if (-not $fileName) { $fileName = "download.bin" }
    $outPath = Join-Path $OutDir $fileName

    Write-Host ""
    Write-Host "-> download $Url" -ForegroundColor Cyan
    Write-Host "   into $outPath" -ForegroundColor Gray
    Send-HostMessage @{ cmd = "download"; id = "dl-1"; url = $Url; path = $outPath }

    $deadline = if ($Interrupt -gt 0) { (Get-Date).AddSeconds($Interrupt) } else { [DateTime]::MaxValue }
    $cancelAt = if ($Cancel -gt 0) { (Get-Date).AddSeconds($Cancel) } else { [DateTime]::MaxValue }
    $cancelSent = $false
    $lastPercent = -1
    $finished = $false

    while ($true) {
        if ((Get-Date) -gt $deadline) {
            Write-Host ""
            Write-Host "!! interrupting host after $Interrupt s (simulating a crash)" -ForegroundColor Yellow
            $proc.Kill()
            break
        }

        if (-not $cancelSent -and (Get-Date) -gt $cancelAt) {
            $cancelSent = $true
            Write-Host ""
            Write-Host "-> cancel dl-1 after $Cancel s" -ForegroundColor Yellow
            Send-HostMessage @{ cmd = "cancel"; id = "c-1"; target = "dl-1" }
        }

        $msg = Read-HostMessage
        if ($null -eq $msg) { break }

        switch ($msg.status) {
            "started" {
                Write-Host "<- started  tier=$($msg.tier)  resumable=$($msg.resumable)  size=$($msg.contentLength)" -ForegroundColor Green
            }
            "progress" {
                if ($msg.total) {
                    $pct = [int](($msg.received / $msg.total) * 100)
                    if ($pct -ne $lastPercent) {
                        $lastPercent = $pct
                        Write-Progress -Activity "Downloading $fileName" -Status "$pct% ($($msg.received) / $($msg.total))" -PercentComplete $pct
                    }
                }
            }
            "finished" {
                Write-Progress -Activity "Downloading $fileName" -Completed
                Write-Host "<- finished  bytes=$($msg.bytes)  path=$($msg.path)" -ForegroundColor Green
                $finished = $true
            }
            "cancelled" {
                Write-Progress -Activity "Downloading $fileName" -Completed
                Write-Host "<- cancelled  stopped at $($msg.bytes) bytes  resumable=$($msg.resumable)" -ForegroundColor Yellow
                $finished = $true
            }
            "error" {
                Write-Host "<- ERROR: $($msg.message)" -ForegroundColor Red
                $exitCode = 1
            }
        }

        if ($finished) { break }
    }

    # --- Report on-disk state -------------------------------------------------
    Write-Host ""
    Write-Host "Files in $OutDir :" -ForegroundColor Cyan
    Get-ChildItem $OutDir | ForEach-Object {
        Write-Host ("  {0,-45} {1,14:N0} bytes" -f $_.Name, $_.Length)
    }

    $metaFile = Get-ChildItem $OutDir -Filter "*.part.meta" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($metaFile) {
        Write-Host ""
        Write-Host "Resume state (.part.meta):" -ForegroundColor Cyan
        Get-Content $metaFile.FullName -Raw | ConvertFrom-Json |
            Select-Object url, tier, bytesDownloaded, contentLength, etag | Format-List
    }

    if ($Interrupt -gt 0 -or $Cancel -gt 0) {
        Write-Host "Re-run this script with no -Interrupt/-Cancel to verify it resumes from the byte above." -ForegroundColor Yellow
    } elseif (-not $finished) {
        Write-Host "FAIL: host closed the pipe before reporting 'finished'" -ForegroundColor Red
        $exitCode = 1
    }
}
finally {
    if (-not $proc.HasExited) {
        try { $stdin.Close() } catch {}
        if (-not $proc.WaitForExit(5000)) { $proc.Kill() }
    }

    $err = $proc.StandardError.ReadToEnd()
    if ($err) {
        Write-Host ""
        Write-Host "Host stderr:" -ForegroundColor Red
        Write-Host $err
    }
}

exit $exitCode
