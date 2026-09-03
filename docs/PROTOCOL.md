# Native messaging protocol

`DownloaderHost.exe` is a standalone stdio program. Every message is a 4-byte little-endian
length followed by that many bytes of UTF-8 JSON — the Chromium native messaging framing.

The host is launched by the browser with the caller origin as `argv[0]`, e.g.
`chrome-extension://<id>/ --parent-window=0`. It runs until stdin closes.

Requests carry an `id`; every reply about that request echoes it. A single request may produce
several replies, so **the reader must loop**. This is why the extension uses
`chrome.runtime.connectNative` and not `chrome.runtime.sendNativeMessage`, which terminates the
host after one reply.

## Requests

### `download`

```json
{
  "cmd": "download",
  "id": "7",
  "url": "https://example.com/file.iso",
  "path": "C:\Users\me\Downloads\file.iso",
  "filename": "file.iso",
  "headers": { "Cookie": "session=...", "Referer": "https://example.com/", "User-Agent": "..." }
}
```

`path` is optional; without it the host saves to the user's Downloads folder using a filename
derived from the URL. `headers` is optional and is replayed on every request the host makes,
which is what lets a captured mirror URL authorise. If the target exists and no `.part` is in
progress, the host picks `name (1).ext` the way browsers do.

Replies, in order: `started`, then `progress` about twice a second, then one of `finished`,
`paused`, `cancelled` or `error`.

```json
{ "id":"7", "status":"started",  "url":"...", "path":"...", "tier":"ResumableUnverified",
  "resumable":true, "contentLength":133886176 }
{ "id":"7", "status":"progress", "received":23092660, "total":133886176, "tier":"..." }
{ "id":"7", "status":"finished", "path":"...", "bytes":133886176 }
```

### `pause` / `cancel`

```json
{ "cmd": "pause", "id": "p1", "target": "7" }
```

`target` names the download to stop. They differ in what happens to the partial data:

- **`pause`** keeps `.part` and `.meta`, so the transfer stays resumable and appears in
  `list_partials`. Replies `paused`.
- **`cancel`** deletes both, abandoning the download. Replies `cancelled`, with `bytes` reporting
  how much was discarded.

Stopping a download that is not running replies `error`.

### `list_partials`

```json
{ "cmd": "list_partials", "id": "l1", "dir": "C:\Users\me\Downloads" }
```

Scans a directory (default: Downloads) for `.part.meta` files and reports what can be resumed.
Downloads currently being written are **excluded** — they are in progress, not unfinished.

```json
{ "id":"l1", "status":"partials", "dir":"...", "items":[
  { "url":"...", "path":"...", "fileName":"linux-6.0.tar.xz", "bytesOnDisk":23092660,
    "contentLength":133886176, "tier":"ResumableUnverified", "resumable":true } ] }
```

This reads from disk, so it survives a browser restart, a service-worker shutdown and a crash.

### `discard`

```json
{ "cmd": "discard", "id": "d1", "path": "C:\Users\me\Downloads\file.iso" }
```

Deletes `path.part` and `path.part.meta`. Replies `discarded`.

### `probe`

Classifies a URL without downloading. Accepts `headers` like `download`.

```json
{ "cmd": "probe", "id": "1", "url": "https://example.com/file.iso" }
{ "id":"1", "status":"probed", "tier":"FullyResumable", "resumable":true,
  "contentLength":5000000, "etag":"\"abc\"", "lastModified":"..." }
```

### `ping`

Handshake check. Replies `pong` with the host's pid and log path.

## On-disk state

A download in progress is `file.part` plus `file.part.meta`:

```json
{ "url": "...", "contentLength": 133886176, "etag": null, "tier": 1,
  "bytesDownloaded": 23092660, "createdAt": "...", "lastAttempt": "..." }
```

`tier` is the numeric enum (0–3). The metadata is written before the first byte and checkpointed
every two seconds, so a killed process still leaves a resumable state. On completion the `.part`
is renamed to the final name and the `.meta` is deleted.

To resume, send `download` again with the same `path`. The host reads the `.part` length from
disk, so the byte offset is always accurate even if the checkpoint is a couple of seconds stale.
It re-probes when the URL differs from the cached one, and Tier 0 sources are protected by
`If-Range` — a mismatched ETag returns `200`, which the host detects and restarts cleanly rather
than splicing mismatched bytes.

## Errors

```json
{ "id": "7", "status": "error", "message": "already downloading to file.iso" }
```

The host refuses a second download to a path already being written, rather than letting two
writers race for the same file handle.

## Command-line flags

Logging is on by default, because a browser cannot pass flags to a native messaging host and a
silent host is undiagnosable.

| Flag | Effect |
|------|--------|
| `--no-log` | Disable logging |
| `--log-path <path>` | Log somewhere other than `%LOCALAPPDATA%\WindowsDownloader\host.log` |
| `--debug`, `--trace-http`, `--trace-browser` | Explicitly enable tracing |
