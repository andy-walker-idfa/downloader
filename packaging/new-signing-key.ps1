#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Generate the extension signing key that fixes the extension's ID.

.DESCRIPTION
    A Chromium extension's ID is the first 16 bytes of the SHA-256 of its public key, mapped to
    a-p. Without a "key" in the manifest, an unpacked extension falls back to hashing its
    *directory path* -- which is why the ID changed with the install location and why
    install.ps1 had to derive it. That only ever works for sideloading on one machine.

    This writes the public key into extension/manifest.json (safe to commit) and the private key
    outside the repository (never commit it). Keep the private key: it is what proves a future
    update is from you, and losing it means a new ID and a broken install for every user.

.PARAMETER KeyPath
    Where to write the private key. Defaults to %LOCALAPPDATA%\WindowsDownloader.

.PARAMETER Force
    Overwrite an existing key. Refused by default -- a new key means a new extension ID.
#>

param(
    [string]$KeyPath = (Join-Path $env:LOCALAPPDATA "WindowsDownloader\extension-signing-key.pem"),
    [switch]$Force
)

$ErrorActionPreference = "Stop"

$RepoRoot     = Split-Path -Parent $PSScriptRoot
$ManifestFile = Join-Path $RepoRoot "extension\manifest.json"

if (-not (Test-Path $ManifestFile)) { throw "Extension manifest not found: $ManifestFile" }

if ((Test-Path $KeyPath) -and -not $Force) {
    Write-Host "A signing key already exists at:" -ForegroundColor Yellow
    Write-Host "  $KeyPath"
    Write-Host ""
    Write-Host "Generating a new one would change the extension ID and break every existing" -ForegroundColor Yellow
    Write-Host "install and registration. Pass -Force only if you are certain." -ForegroundColor Yellow
    exit 1
}

# --- Key pair -----------------------------------------------------------------
$rsa = [System.Security.Cryptography.RSA]::Create(2048)

$spki      = $rsa.ExportSubjectPublicKeyInfo()      # DER SubjectPublicKeyInfo -- the manifest "key"
$publicB64 = [Convert]::ToBase64String($spki)

# Chromium derives the ID from the SHA-256 of that same DER blob.
$sha = [System.Security.Cryptography.SHA256]::Create().ComputeHash($spki)
$hex = ($sha[0..15] | ForEach-Object { $_.ToString('x2') }) -join ''
$extensionId = ($hex.ToCharArray() | ForEach-Object { [char]([int][char]'a' + [Convert]::ToInt32($_, 16)) }) -join ''

# --- Private key, outside the repo -------------------------------------------
$keyDir = Split-Path -Parent $KeyPath
New-Item -ItemType Directory -Path $keyDir -Force | Out-Null

$pem = @(
    "-----BEGIN RSA PRIVATE KEY-----"
    [Convert]::ToBase64String($rsa.ExportRSAPrivateKey()) -replace '(.{64})', "`$1`n"
    "-----END RSA PRIVATE KEY-----"
) -join "`n"
Set-Content -Path $KeyPath -Value $pem -Encoding ASCII

# Owner-only, so the key is not readable by other accounts on a shared machine.
$acl = Get-Acl $KeyPath
$acl.SetAccessRuleProtection($true, $false)
$acl.SetAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule(
    [System.Security.Principal.WindowsIdentity]::GetCurrent().Name, "FullControl", "Allow")))
Set-Acl -Path $KeyPath -AclObject $acl

# --- Manifest -----------------------------------------------------------------
# Edited as text, not via ConvertTo-Json, so formatting and key order are preserved.
$manifestText = Get-Content $ManifestFile -Raw
if ($manifestText -match '"key"\s*:') {
    $manifestText = [regex]::Replace($manifestText, '"key"\s*:\s*"[^"]*"', '"key": "' + $publicB64 + '"')
} else {
    $manifestText = [regex]::Replace($manifestText, '("manifest_version"\s*:\s*\d+,)',
        '$1' + "`n  `"key`": `"$publicB64`",", 1)
}
Set-Content -Path $ManifestFile -Value $manifestText -NoNewline -Encoding UTF8

Write-Host ""
Write-Host "Extension ID is now fixed:" -ForegroundColor Green
Write-Host "  $extensionId" -ForegroundColor White
Write-Host ""
Write-Host "  public key  -> extension/manifest.json  (commit this)" -ForegroundColor Gray
Write-Host "  private key -> $KeyPath  (NEVER commit; back it up)" -ForegroundColor Yellow
Write-Host ""
Write-Host "Next:" -ForegroundColor Cyan
Write-Host "  1. Remove the old unpacked extension in each browser -- its ID has changed."
Write-Host "  2. Load unpacked again from $RepoRoot\extension"
Write-Host "  3. Run .\install.ps1 to register the new ID."
