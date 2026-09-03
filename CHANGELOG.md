# Changelog

## v1.0.0 — 2026-09-03

First stable release. The version number covers the **browser extension and native host**,
which are complete and tested end to end. The desktop app and MSIX packaging are still early;
see [ROADMAP.md](ROADMAP.md).

### Resumability

- Four-tier classification from a real range request, not advertised headers. A server that
  answers `200 OK` to a `Range` request is reported as non-resumable even when it advertises
  `Accept-Ranges: bytes` — trusting that header is how download managers corrupt files.
- `HEAD` is treated as an optional metadata hint. It is never allowed to decide the tier on its
  own, because tokenised CDN URLs commonly refuse `HEAD` while serving ranged `GET`s.
- Crash-safe resume: URL, tier, ETag and byte offset are written before the first byte and
  checkpointed every two seconds, so a killed process still leaves resumable state.
- Tier 0 resumes are guarded by `If-Range`; a mismatched ETag returns `200`, which the host
  detects and restarts cleanly rather than splicing mismatched bytes.

### Transfers

- Pause keeps the partial for resuming. Cancel discards it. Pause is only offered where the
  server actually honours ranges.
- Unfinished downloads are listed from disk, so they survive a browser restart, a service-worker
  shutdown and a crash.
- A second download to a path already being written is refused rather than racing for the file
  handle.
- Progress is reported roughly twice a second, which also keeps the MV3 service worker alive for
  the duration of a transfer.

### Browser integration

- A long-lived native messaging port. `sendNativeMessage` terminates the host after its first
  reply, which killed every download before any bytes landed.
- Optional takeover of browser-initiated downloads, forwarding the browser's cookies, referrer
  and user-agent so session-bound mirror URLs authorise. This is what makes sites with no direct
  link work at all.
- The context menu never guesses: if there is no downloadable URL where you clicked, it says so.
- Notifications only when the popup is closed, since the popup is the better surface when open.

### Packaging

- `install.ps1` derives the extension ID from the extension folder's path, registers Chrome,
  Brave and Edge, removes stale registrations, and verifies each browser points at a manifest
  that exists.
- The native messaging manifest is written to `%LOCALAPPDATA%\WindowsDownloader`, outside the
  repository, so a clean or a fresh clone cannot break a working install.
- It refuses to rebuild while a download is in progress, because stopping the host to unlock the
  binary destroys the transfer.

### Known limitations

- Tier 2 sources cannot be resumed. This is an HTTP-level fact, not an implementation gap.
- Single connection; no parallel segmentation yet.
- A single-use mirror token may not survive a later resume.
- Windows only.
