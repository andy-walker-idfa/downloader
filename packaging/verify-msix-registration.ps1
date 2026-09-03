#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Answer the go/no-go question for the Store route: can a packaged app register a native
    messaging host that a browser running OUTSIDE the package can actually use?

.DESCRIPTION
    Three things have to be true, and each is checked separately so a failure says which:

      1. the app runs with package identity and its HKCU write lands in the REAL user hive
         (MSIX virtualises the registry, so this is the part that could fail);
      2. the registered manifest points at the host inside the package;
      3. that host can be launched and will answer a native messaging handshake -- which is
         exactly what the browser will do, from outside the container.
#>

param([string]$PackageName = "DownloaderPrototype")

$ErrorActionPreference = "Stop"
function Step($t) { Write-Host "`n=== $t" -ForegroundColor Cyan }
function Pass($t) { Write-Host "  PASS  $t" -ForegroundColor Green }
function Fail($t) { Write-Host "  FAIL  $t" -ForegroundColor Red; $script:failed = $true }

$script:failed = $false

Step "Is the package registered?"
$pkg = Get-AppxPackage -Name $PackageName -ErrorAction SilentlyContinue
if (-not $pkg) {
    Fail "no package named $PackageName is registered"
    Write-Host "  Register the layout first (Developer Mode required):" -ForegroundColor Yellow
    Write-Host "    Add-AppxPackage -Register `"$env:LOCALAPPDATA\WindowsDownloader\msix\layout\AppxManifest.xml`"" -ForegroundColor Yellow
    exit 1
}
Pass "$($pkg.PackageFullName)"
Write-Host "  install location: $($pkg.InstallLocation)" -ForegroundColor Gray

Step "Launching the packaged app so it registers on startup"
$logPath = Join-Path $env:LOCALAPPDATA "WindowsDownloader\app.log"
Remove-Item $logPath -ErrorAction SilentlyContinue

$appId = (Get-AppxPackageManifest $pkg).Package.Applications.Application.Id
Start-Process "shell:AppsFolder\$($pkg.PackageFamilyName)!$appId"
Start-Sleep -Seconds 6
Get-Process DownloaderAppWpf -ErrorAction SilentlyContinue | Stop-Process -Force

if (-not (Test-Path $logPath)) {
    Fail "the app wrote no log -- it may not have started"
} else {
    $log = Get-Content $logPath -Raw
    Write-Host $log -ForegroundColor Gray
    if ($log -match 'packaged\s*:\s*True') { Pass "app reports package identity" }
    else { Fail "app did not report package identity" }
    if ($log -match 'registered\s*:\s*(?!none)') { Pass "app registered at least one browser" }
    else { Fail "app registered no browsers" }
}

Step "Did the HKCU write reach the real hive, outside the container?"
# This script is not packaged, so what it reads here is what a browser would read.
$hostName = "com.downloader.host"
$seen = @{}
foreach ($b in @(
    @{ n = "Chrome"; p = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName" },
    @{ n = "Brave";  p = "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\$hostName" },
    @{ n = "Edge";   p = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName" })) {
    $v = (Get-ItemProperty -Path $b.p -ErrorAction SilentlyContinue).'(default)'
    if ($v) { Pass "$($b.n) -> $v"; $seen[$b.n] = $v } else { Fail "$($b.n): no registry entry visible outside the package" }
}

Step "Does the registered manifest point into the package?"
$manifestPath = $seen.Values | Select-Object -First 1
if (-not $manifestPath -or -not (Test-Path $manifestPath)) {
    Fail "registered manifest missing: $manifestPath"
} else {
    $hostExe = (Get-Content $manifestPath -Raw | ConvertFrom-Json).path
    Write-Host "  host path: $hostExe" -ForegroundColor Gray
    if ($hostExe -like "*WindowsApps*") { Pass "points inside the package" }
    else { Write-Host "  NOTE  points outside the package: $hostExe" -ForegroundColor Yellow }

    Step "Can that host be launched and answer a handshake, as the browser would?"
    if (-not (Test-Path $hostExe)) {
        Fail "host executable not readable at that path"
    } else {
        $psi = [System.Diagnostics.ProcessStartInfo]::new()
        $psi.FileName = $hostExe
        $psi.Arguments = "chrome-extension://febdocdjpdhmfddcddbobidgpjhckemo/ --parent-window=0"
        $psi.UseShellExecute = $false
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.CreateNoWindow = $true
        try {
            $p = [System.Diagnostics.Process]::Start($psi)
            $msg = '{"cmd":"ping","id":"1"}'
            $bytes = [System.Text.Encoding]::UTF8.GetBytes($msg)
            $p.StandardInput.BaseStream.Write([BitConverter]::GetBytes($bytes.Length), 0, 4)
            $p.StandardInput.BaseStream.Write($bytes, 0, $bytes.Length)
            $p.StandardInput.BaseStream.Flush()

            $lenBuf = New-Object byte[] 4
            if ($p.StandardOutput.BaseStream.Read($lenBuf, 0, 4) -eq 4) {
                $len = [BitConverter]::ToInt32($lenBuf, 0)
                $buf = New-Object byte[] $len
                $p.StandardOutput.BaseStream.Read($buf, 0, $len) | Out-Null
                $reply = [System.Text.Encoding]::UTF8.GetString($buf) | ConvertFrom-Json
                if ($reply.status -eq "pong") { Pass "host answered pong (pid $($reply.pid))" }
                else { Fail "unexpected reply: $($reply | ConvertTo-Json -Compress)" }
            } else { Fail "no reply from the packaged host" }
            $p.StandardInput.BaseStream.Close()
            if (-not $p.WaitForExit(5000)) { $p.Kill() }
        } catch {
            Fail "could not launch the packaged host: $($_.Exception.Message)"
        }
    }
}

Write-Host ""
if ($script:failed) {
    Write-Host "VERDICT: the Store route has a blocker -- see the FAIL lines above." -ForegroundColor Red
    exit 1
}
Write-Host "VERDICT: a packaged app can register a native messaging host that browsers can use." -ForegroundColor Green
