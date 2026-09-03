# Testing guide

How to verify the downloader end to end, and how to tell which layer is broken when it isn't
working.

## Test the host without a browser first

This is the fastest way to separate "the downloader is broken" from "the browser wiring is
broken". These scripts speak the same native messaging protocol a browser does, including reading
*every* message a download emits.

```powershell
cd packaging

.\test_host_e2e.ps1 -PingOnly          # handshake only
.\test_host_e2e.ps1                     # full download with live progress
.\test_host_e2e.ps1 -Pause 6           # graceful pause after 6s (partial kept)
.\test_host_e2e.ps1 -Interrupt 6        # kill the process mid-stream (crash simulation)
.\test-probe.ps1 -Url "https://..."     # classify a URL
```

After `-Pause` or `-Interrupt`, re-run with no flag to confirm it resumes from where it stopped
rather than starting over.

If these pass but the browser doesn't, the fault is registration or the extension.

## Set up the browser

1. `chrome://extensions` → Developer mode → **Load unpacked** → the repo's `extension/` folder.
   Load it from the repo folder: an unpacked extension's ID comes from its directory path, so a
   copy elsewhere gets a different ID and won't match the registered host.
2. ```powershell
   cd packaging
   .\install.ps1
   ```
   It derives the extension ID from the folder path, so you don't copy it by hand. Read its
   output — it reports which folder each browser is actually loading.
3. Reload the extension, open the popup. The header should read **host connected**.

## Run a real download

Right-click a file link → **Download with Downloader**, or tick **Take over browser downloads**
and use the site's own button.

Good test URLs:

| URL | Expected |
|-----|----------|
| `https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.0.tar.xz` | Tier 1, 127.7 MB |
| A GitHub release asset | Tier 0 or 2 depending on the CDN it redirects to |

## Verify resume

1. Start a download of the kernel.org file.
2. Click **Pause** in the popup — or, to simulate a crash, `Stop-Process -Name DownloaderHost -Force`.
3. Inspect what survived:
   ```powershell
   Get-ChildItem "$HOME\Downloads\*.part*"
   Get-Content "$HOME\Downloads\linux-6.0.tar.xz.part.meta" | ConvertFrom-Json |
       Select-Object url, tier, bytesDownloaded, contentLength
   ```
4. Hit **Resume** in the popup's *Unfinished* section.
5. Confirm the result is not corrupt:
   ```powershell
   (Get-FileHash "$HOME\Downloads\linux-6.0.tar.xz" -Algorithm SHA256).Hash.ToLower()
   ```
   Compare against `https://cdn.kernel.org/pub/linux/kernel/v6.x/sha256sums.asc`. For
   `linux-6.0.tar.xz` it is
   `5c2443a5538de52688efb55c27ab0539c1f5eb58c0cfd16a2b9fbb08fd81788e`.

A hash match after an interrupted-and-resumed download is the test that actually matters. A file
of the right *size* can still be spliced wrong.

## Unit tests

```powershell
dotnet test native-host/DownloaderHost.Tests/DownloaderHost.Tests.csproj
```

Covers tier classification, the `200 OK`-to-a-range-request trap, and resume via range request.

## Reading the log

Logging is on by default; a browser can't pass flags to a native messaging host, so a silent host
would be undiagnosable.

```powershell
Get-Content "$env:LOCALAPPDATA\WindowsDownloader\host.log" -Wait -Tail 5
```

Each line is JSON with an `event` and a plain-English `reason`. Useful ones:

| Event | Meaning |
|-------|---------|
| `startup` | `args` shows the caller origin the browser passed |
| `probe_range` | The authoritative resumability test — `status=206` resumable, `status=200` not |
| `download_begin` / `download_finished` | Transfer boundaries |
| `resume_allowed` | Continued from existing bytes |
| `download_reset` | Tier 2 source, partial discarded and restarted |
| `duplicate_download` | A second download to a path already being written was refused |

A `download_begin` with no matching `download_finished`, `download_cancelled` or `stdin_closed`
means the process was killed outright.

## Troubleshooting

**"Specified native messaging host not found"** — the loaded extension's ID isn't in the host
manifest. Re-run `install.ps1` and read what it reports.

```powershell
Get-Content "$env:LOCALAPPDATA\WindowsDownloader\com.downloader.host.json"
Get-ItemProperty "HKCU:\Software\Google\Chrome\NativeMessagingHosts\com.downloader.host"
```

Brave's key is `BraveSoftware\Brave-Browser`, not `BraveSoftware\Brave`.

**Nothing in the log at all** — the host was never launched, so it's a registration problem rather
than a download problem.

**Errors in the page console after reloading the extension** — reloading orphans content scripts
in already-open tabs. Refresh those tabs.

**Download starts then stalls** — check the log. If the popup is open, the event log shows the
host's error verbatim.
