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
live progress in a grid. It is a shell, not a product.

- [ ] Multiple concurrent downloads with per-row pause / resume / cancel
- [ ] Surface the unfinished-downloads list (`list_partials`) so resumes survive an app restart
- [ ] Keep one long-lived host process instead of one per download
- [ ] Settings: download folder, concurrency, whether to take over browser downloads
- [ ] Proper error surfaces — currently an exception lands in a grid cell
- [ ] Tray integration and notifications

## Store packaging

`packaging/AppxManifest.xml` targets `DownloaderAppWpf.exe` with `runFullTrust`, and
`packaging/Assets/` holds the tile images. Not yet submitted, and these are the known blockers:

- [ ] **Identity.** `Name="DownloaderPrototype"` / `Publisher="CN=LocalDev"` are placeholders and
      must be replaced with the values Partner Center assigns.
- [ ] **Native messaging registration from a packaged app.** The host is currently registered by
      writing `HKCU\Software\<Browser>\NativeMessagingHosts`. An MSIX app cannot run
      `install.ps1`; it must declare the registration in the manifest, and the manifest must point
      at the host inside the package. This is the main unsolved packaging problem.
- [ ] Ship `DownloaderHost.exe` beside the app in the package. `DownloaderService.ResolveHostPath`
      already looks next to the executable first, so the layout is ready.
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

## Known limitations that will not be fixed

- Tier 2 servers cannot be resumed. This is an HTTP-level fact, not an implementation gap.
- A single-use mirror token may not survive a later resume.
