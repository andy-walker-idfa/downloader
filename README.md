# Windows Downloader

> ## Archived, September 2026
>
> **Not maintained, and not intended to be used.** Every major browser now ships a download
> manager that covers what this set out to do: downloads that pause and resume wherever the
> server allows it. A separate tool is not worth installing for that, so development stopped
> here rather than continuing to a Store submission.
>
> The code is complete and working at the point it was stopped, and the local installation has
> been removed from the machine it was developed on. Reviving it means running
> `packaging/install.ps1` again on a fresh checkout.
>
> **If you are reading this for anything, read [docs/INVARIANTS.md](docs/INVARIANTS.md).** It is
> the part with lasting value: roughly thirty rules about Chromium native messaging, HTTP range
> semantics and MSIX packaging, each recording a real failure and why it happened. Most of those
> failures were silent.
>
> The one idea here that browsers still do not do is tell you *before* a download starts whether
> the server can actually resume it. See [docs/TIER_DETECTION.md](docs/TIER_DETECTION.md).


A download manager for Windows that detects whether a server actually supports resuming, and
handles each source accordingly. It comes in three parts: a browser extension, a native
messaging host that does the downloading, and a WPF desktop app.

> **v1.0.0** was the last release. The browser extension and native host were complete and
> tested end to end; the desktop app reached feature parity with the extension popup across four
> phases (see [docs/DESKTOP_APP.md](docs/DESKTOP_APP.md)). MSIX packaging was proven but never
> submitted.

## Why

Most download managers claim to resume anything. They can't. Resuming is a *server* capability:
if a server ignores HTTP `Range` requests, no client can continue an interrupted transfer, and
pretending otherwise either silently restarts the download or corrupts the file.

This project probes each source first, classifies it, and tells you the truth up front — before
you're 15 GB into a download that can't be resumed.

## Resumability tiers

| Tier | Meaning | Resume | Typical source |
|------|---------|--------|----------------|
| 0 | `FullyResumable` — 206 responses, known length, strong ETag | Safe | GitHub releases, S3 |
| 1 | `ResumableUnverified` — 206 and known length, no strong validator | Works, unverified | Many CDNs, kernel.org |
| 2 | `NotResumable` — server ignores `Range` | None | Some file-host mirrors |
| 3 | `UnboundedStream` — no `Content-Length` | None | Chunked responses |

The critical case is Tier 2: a server that answers `200 OK` to a `Range` request is **not**
resumable, even when it advertises `Accept-Ranges: bytes`. Trusting that header is how download
managers corrupt files. Details in [docs/TIER_DETECTION.md](docs/TIER_DETECTION.md).

## What it does

- **Detects resumability** with a real range request, not just advertised headers
- **Resumes across crashes** — a `.part.meta` file checkpoints URL, tier, ETag and byte offset
  every two seconds, so an interrupted transfer survives a browser restart or a kill -9
- **Pause and resume**, offered only where the server can actually honour it
- **Takes over browser downloads**, so sites with no direct link still work — the page does its
  POST or JS navigation as normal and the extension captures the resolved URL, forwarding the
  browser's cookies and referrer so session-bound mirror links still authorise
- **Names files the way the server does** - honours `Content-Disposition` rather than guessing
  from the URL, which often has no filename in it at all
- **Configurable download folder**, shared by the extension and the desktop app
- **Never guesses a URL.** If there's nothing downloadable where you clicked, it says so

## Install

Requires Windows, .NET 8 SDK, and Chrome, Brave or Edge.

**1. Load the extension**

Open `chrome://extensions` (or `brave://extensions`, `edge://extensions`), enable Developer mode,
choose **Load unpacked**, and select the `extension/` folder in this repo.

Load it from the repo folder. An unpacked extension's ID is derived from its directory path, so a
copy elsewhere gets a different ID and won't match the registered host.

The extension's ID comes from the signing key in its manifest, so it is the same wherever you
install it. On a fresh clone with no key yet, generate one first with
`packaging
ew-signing-key.ps1`.

**2. Build and register the native host**

```powershell
cd packaging
.\install.ps1
```

No extension ID needed — the script derives it from the signing key in the extension manifest. It publishes the
host, writes the native messaging manifest to `%LOCALAPPDATA%\WindowsDownloader` (outside the
repo, so a clean or a fresh clone can't break a working install), registers Chrome/Brave/Edge,
removes stale
registrations, reports which folder each browser is actually loading, and verifies the host
answers a handshake.

**3. Use it**

Right-click a link → **Download with Downloader**. Or tick **Take over browser downloads** in the
popup and use the site's own download button.

Full guide, including resume and crash-recovery testing: [docs/TESTING.md](docs/TESTING.md).

## Architecture

```
Browser extension (MV3)
  ├─ context menu ─────────┐
  └─ downloads.onCreated ──┤   cancels the browser's copy, forwards
                           │   URL + Cookie + Referer + User-Agent
                           ▼
              chrome.runtime.connectNative  (long-lived port)
                           │
                           ▼
              DownloaderHost.exe  (C#, stdio native messaging)
                  ├─ probe    → HEAD, then GET Range: 0-0 → tier
                  └─ download → tier-aware resume
                       ├─ Tier 0  If-Range with the ETag
                       ├─ Tier 1  append from the last byte
                       └─ Tier 2  restart from zero
                           │
                           ▼
              file.part  +  file.part.meta   →   file
```

The port must be long-lived. `chrome.runtime.sendNativeMessage()` terminates the host after its
first reply, which kills every download before any bytes land — the host sends `started` first and
streams afterwards.

The protocol is documented in [docs/PROTOCOL.md](docs/PROTOCOL.md) and is usable on its own; the
host is a standalone stdio program.

## Extension permissions

The extension asks for broad permissions. Each one is load-bearing:

| Permission | Why |
|------------|-----|
| `nativeMessaging` | Talk to the download host. The whole point. |
| `downloads` | Detect and cancel browser-initiated downloads when takeover is on. |
| `cookies` | Forward your session cookie so token-bound mirror URLs authorise. Read only for the URL being downloaded, sent only to the local host. |
| `contextMenus` | The right-click entry. |
| `notifications` | Report outcomes when the popup is closed. |
| `storage` | Remember the takeover toggle and the event log. |
| `tabs` | Read the active tab's URL for the popup's download button. |
| `<all_urls>` | Downloads can start from any site. |

Nothing is sent anywhere except the native host on your machine. There is no telemetry and no
remote endpoint. The host logs to `%LOCALAPPDATA%\WindowsDownloader\host.log`.

See [CHANGELOG.md](CHANGELOG.md) for what landed in each release.

## Repository layout

```
extension/        Chromium MV3 extension
native-host/      C# native messaging host + xUnit tests
app/              WPF desktop app (early)
packaging/        install script, e2e test harness, MSIX manifest and assets
docs/             architecture, invariants, testing, protocol, tier detection
```

## Development

```powershell
dotnet build Downloader.sln -c Release
dotnet test native-host/DownloaderHost.Tests/DownloaderHost.Tests.csproj
```

To exercise the host without a browser — it speaks the same protocol a browser does, including
reading every message a download emits:

```powershell
cd packaging
.\test_host_e2e.ps1                 # full download with live progress
.\test_host_e2e.ps1 -Pause 6       # pause partway, then re-run to resume
.\test-probe.ps1 -Url "https://..." # classify a URL
```

If these pass but the browser doesn't, the problem is registration or the extension.

## Limitations

- **Tier 2 sources cannot be resumed.** Not a bug, and not fixable client-side. Mirrors vary, so
  another mirror for the same file may well be Tier 0 or 1.
- **Single connection.** No parallel segmentation yet.
- **Captured mirror URLs can expire.** If a token is single-use, resuming later may fail; the
  download restarts.
- Windows only.

## License

Not yet licensed. All rights reserved by default until a license is added, which means the code
can be read but not reused. Open an issue if you need it licensed.
