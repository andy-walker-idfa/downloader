# Packaging

## install.ps1

Builds the native host, registers it with Chrome, Brave and Edge, and verifies it responds.

```powershell
cd packaging
.\install.ps1
```

It derives the extension ID from `extension/`'s absolute path — Chromium computes an unpacked
extension's ID as the first 16 bytes of the SHA-256 of that path (UTF-16LE on Windows), mapped to
`a`–`p` — so there is no ID to copy by hand. Pass `-ExtensionId <id>` to authorise an additional
one, e.g. a packed build.

What it writes:

- `%LOCALAPPDATA%\WindowsDownloader\com.downloader.host.json` — the native messaging manifest.
  It is written **outside the repo on purpose**: browsers store an absolute path to it in the
  registry, so keeping it in the working tree meant a `git clean`, a fresh clone or any tidy-up
  deleted it, and every registered browser then reported "Specified native messaging host not
  found" with nothing visibly wrong. It also holds absolute paths and machine-specific extension
  IDs, so it must never be committed.
- `HKCU\Software\Google\Chrome\NativeMessagingHosts\com.downloader.host` and the Brave and Edge
  equivalents, each pointing at that manifest.

It also removes registrations from older layouts, and reports which folder each browser is
actually loading the extension from — a mismatch there is the usual reason nothing works.

The script refuses to rebuild while a download is in progress, because stopping the host to unlock
the binary aborts the transfer, and on a non-resumable source those bytes are gone. `-Force`
overrides.

Two details that are easy to get wrong:

- Chromium requires `allowed_origins`. `allowed_extensions` is the Firefox spelling and is
  silently ignored.
- Brave's registry key is `BraveSoftware\Brave-Browser`, not `BraveSoftware\Brave`.

## test_host_e2e.ps1

Drives the host exactly as a browser does — launched with the caller origin as `argv[0]`, stdin
held open, reading every framed reply.

```powershell
.\test_host_e2e.ps1 -PingOnly
.\test_host_e2e.ps1
.\test_host_e2e.ps1 -Cancel 6      # graceful pause
.\test_host_e2e.ps1 -Interrupt 6   # kill mid-stream
```

## test-probe.ps1

Classifies a single URL and explains the tier.

```powershell
.\test-probe.ps1 -Url "https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.0.tar.xz"
```

## AppxManifest.xml and Assets/

MSIX packaging for the WPF app, as a full-trust desktop package. Not yet submittable — the
identity is a placeholder and native messaging registration from inside a package is unsolved.
See [../ROADMAP.md](../ROADMAP.md).
