# Roadmap

The goal is a real Windows application distributed through the Microsoft Store. This records
what works, what's missing, and what has to be true before a Store submission.

## Working

- Tier detection with a real range request, including the `200 OK`-to-a-range trap
- Crash-safe resume: metadata checkpointed to disk every 2s, verified bit-identical by SHA-256
  after interrupt-and-resume
- Pause / cancel / resume, with pause offered only where the server honours ranges
- Browser takeover for sites with no direct link, forwarding cookies, referrer and user-agent
- Chrome / Brave / Edge registration, with a stable extension ID derived from a signing key
- Unit tests and CI

## Desktop app

The WPF app in `app/DownloaderAppWpf` currently launches the host, sends one download, and shows
live progress in a grid. It is a shell, not a product: it uses one of the host's nine commands,
and its Pause/Resume buttons only relabel a row.

**The plan is in [docs/DESKTOP_APP.md](docs/DESKTOP_APP.md)** - current state, target
architecture, and four phases with an acceptance check each. Read
[docs/INVARIANTS.md](docs/INVARIANTS.md) before changing anything.

- [ ] Multiple concurrent downloads with per-row pause / resume / cancel
- [ ] Surface the unfinished-downloads list (`list_partials`) so resumes survive an app restart
- [ ] Keep one long-lived host process instead of one per download
- [ ] Use the shared download folder setting the host already exposes (`get_settings`);
      the app still has its own folder field
- [ ] Settings: concurrency, whether to take over browser downloads
- [ ] Proper error surfaces — currently an exception lands in a grid cell
- [ ] Tray integration and notifications

## Store packaging

`packaging/AppxManifest.xml` targets `DownloaderAppWpf.exe` with `runFullTrust`, and
`packaging/Assets/` holds the tile images. Not yet submitted, and these are the known blockers:

- [ ] **Identity.** `Name="DownloaderPrototype"` / `Publisher="CN=LocalDev"` are placeholders and
      must be replaced with the values Partner Center assigns.
- [x] **Native messaging registration from a packaged app - solved.** Declaring the keys in the
      package does not work: MSIX virtualises the registry and browsers outside the container
      cannot see them. Registering at *runtime* from the full-trust app does work. Verified with a
      registered package: the app reported package identity, its `HKCU` writes were visible to an
      unpackaged process, and the host launched and answered a handshake.
- [x] **Executing the host from `C:\Program Files\WindowsApps` - confirmed.** The folder is
      owned by TrustedInstaller and its listing is restricted, but `BUILTIN\Users` holds
      ReadAndExecute. Verified by launching a WindowsApps binary from an unpackaged process with
      redirected stdio, which is exactly how a browser starts a native messaging host.
- [x] **Register a stable host path, not the package path - done.** A package installs to a
      version-stamped folder, so every update relocates the host and deletes the old one. The
      app now copies the host to `%LOCALAPPDATA%\WindowsDownloader\host` and registers that,
      refreshing the copy only when the source build differs. Verified by updating a registered
      package 1.0.0 -> 1.0.1 at a new path and deleting the old folder: the host still answered a
      handshake before the updated app had even been launched.
- [ ] Build a signed `.msix` (makeappx + signtool, available via the
      `Microsoft.Windows.SDK.BuildTools` NuGet package rather than a full SDK install)
- [ ] Build the `.msix` in CI and sign it
- [ ] Store listing: description, screenshots, privacy policy, age rating
- [ ] Decide how the extension is distributed — the Chrome Web Store is a separate submission, and
      a Store-installed app cannot load an unpacked extension
- [ ] Reconcile the extension ID with the Chrome Web Store. The signing key fixes the ID for
      development and self-distribution, but CWS generates its own key for a new item and assigns
      the ID from that. Confirm the exact behaviour at submission time; if the published ID
      differs, take the CWS public key into `manifest.json` so development and production share
      one ID, and add both to the host manifest's `allowed_origins` during the transition.

## Downloader engine

- [ ] Parallel segmentation for Tier 0 sources (4–8 connections)
- [ ] Tier 1 overlap check: re-fetch the last 64 KB before appending, to catch a file that changed
- [ ] Mirror fallback for Tier 2 sources, which is the only real answer to a non-resumable server
- [ ] Detect expired signed URLs on resume and re-resolve where possible
- [ ] Bandwidth limiting
- [ ] Checksum verification when a site publishes one

## Extension

- [ ] Per-download UI in the popup rather than a flat list
- [ ] Optional per-site takeover rules instead of one global toggle
- [ ] Firefox port — the protocol is the same, but the manifest and `allowed_extensions` spelling
      differ

## Development notes

- **MSIX cannot deploy from exFAT.** `Add-AppxPackage` fails with
  `0x80073CFD ... cannot deploy to path layout of file system type exFAT`. If your working tree
  is on an exFAT data drive, stage the package layout on an NTFS volume;
  `build-msix-layout.ps1` defaults to `%LOCALAPPDATA%` and checks the filesystem first. The
  same drive property also makes git report "detected dubious ownership".
- Sideloading needs Developer Mode (or a trusted signing certificate), which requires one
  elevated command.

## Known limitations that will not be fixed

- Tier 2 servers cannot be resumed. This is an HTTP-level fact, not an implementation gap.
- A single-use mirror token may not survive a later resume.
