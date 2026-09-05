# Desktop app architecture

The plan for turning `app/DownloaderAppWpf` from a shell into a real download manager.

Read this with [PROTOCOL.md](PROTOCOL.md) (what the host can do) and
[INVARIANTS.md](INVARIANTS.md) (what must not break). The invariants matter more than this
document: they were each paid for with a real failure.

## Where things stand

The engine is finished. The window barely uses it.

| Host command | Used by the extension | Used by the app |
|---|---|---|
| `download` | yes | yes |
| `pause` / `cancel` | yes | **no** |
| `list_partials` / `discard` | yes | **no** |
| `get_settings` / `set_settings` | yes | **no** |
| `probe` / `ping` | — | **no** |

So the work is mostly wiring, not new engine code. The browser extension already does all of
this correctly and is the reference implementation to copy — particularly `background.js`,
which keeps one long-lived connection and matches replies to requests by id.

### What exists in the app today

- `App.xaml.cs` — registers the native host at startup via `NativeHostRegistrar`, logs to
  `%LOCALAPPDATA%\WindowsDownloader\app.log`. **This works and is load-bearing** (it is what
  makes the packaged/Store build viable). Do not disturb it.
- `NativeHostRegistrar.cs` — idempotent host registration, copies the host to a stable path
  when packaged. **Works. Leave alone.**
- `DownloaderService.cs` — starts a host process, sends one `download`, streams progress back,
  closes stdin. Correct as far as it goes, but one process per download.
- `MainWindow.xaml.cs` — one download at a time; `Pause_Click` and `Resume_Click` only relabel
  the row and never contact the host. `ChooseFolder_Click` uses a file dialog as a folder
  picker, shows a message box, and keeps the folder in a field that is lost on exit.

### The thing that forces the redesign

`DownloaderService.StartDownloadAsync` starts a process, writes one message, then closes stdin.
Closing stdin is what lets the host exit — correct for a one-shot, fatal for a manager. One
process per download means no shared state, no cancelling from elsewhere, and no way to hold
several transfers at once.

## Target architecture

```
MainWindow (UI)
   |
   v
DownloadManagerViewModel        one row per transfer, INotifyPropertyChanged
   |
   v
HostConnection                  ONE long-lived host process
   |  - request(cmd, args) -> awaits the reply with the matching id
   |  - raises events for unsolicited messages (progress, finished, paused...)
   |  - reconnects if the host dies
   v
DownloaderHost.exe              stdio, length-prefixed JSON (see PROTOCOL.md)
```

### HostConnection

The single new component. Responsibilities:

- Start the host once (path from `NativeHostRegistrar.FindHost()`), keep stdin open for the
  process lifetime.
- Give every outgoing message a unique `id`; keep a map of id to `TaskCompletionSource` so a
  caller can `await` the terminal reply.
- Route messages by id, and raise an event for interim ones (`progress`) so rows update live.
- A message with no matching id is a host-initiated event — log it, never throw.
- Reconnect on unexpected exit and surface it in the UI. Do not silently swallow a dead host.
- Serialise writes; the host serialises its own writes already.

The reply pattern per download is `started` → `progress`* → one of `finished` / `paused` /
`cancelled` / `error`. Only the last completes the request.

### What must stay true

- One host process for the app. Never one per download.
- Never close stdin while transfers are running — that terminates the host.
- Requests are correlated by `id`, never by assuming reply order.

## Phases

Each phase must leave the app building, the tests passing, and the browser extension working.
Verify with [TESTING.md](TESTING.md) after each.

### Phase 1 — HostConnection — DONE

- [x] `HostConnection`: start/restart, id correlation, message routing, disconnect reporting
- [x] Multiple concurrent downloads, each with its own live progress
- [x] `App` owns one instance for the process lifetime and disposes it only on exit
- [x] Host path resolution moved to `HostLocator`, shared with `NativeHostRegistrar`
- [x] `DownloaderService` deleted; the fake `Pause_Click` / `Resume_Click` deleted with it
- [x] `app/DownloaderAppWpf.Tests` added and wired into CI

Covered by tests against a local HTTP server, so they need no network:

| Test | Proves |
|---|---|
| `TwoDownloads_RunConcurrently_WithIndependentProgress` | both transfers' active windows overlap in time, each reports its own progress, one process serves both |
| `Replies_AreMatchedById_NotByOrder` | a fast `ping` issued after a slow download does not consume the download's reply |
| `HostDeath_FailsPendingRequests_AndRaisesDisconnected` | a killed host surfaces as an error rather than a UI that waits for ever |

The server is throttled deliberately: unthrottled, loopback transfers finish so fast that
"concurrent" would pass without anything actually overlapping.

### Phase 2 — Real buttons — DONE

- [x] Per-row **Pause** (shown only when `resumable`) and **Cancel**, wired to the host
- [x] `DownloadHandle` exposes a transfer's id, so pause and cancel can name one specific
      download. `SendAsync` allocates the id internally and never returned it
- [x] `DownloadItem` notifies on any type, so button state binds; `CanPause` / `CanCancel`
      derive from status, resumability and whether a stop is already in flight
- [x] Row state comes from the host's replies, never invented locally
- [x] The fake `Pause_Click` / `Resume_Click` were already deleted in phase 1

| Test | Proves |
|---|---|
| `Pause_StopsTheTransfer_AndKeepsThePartialFile` | `.part` and `.part.meta` survive, no final file yet |
| `Cancel_StopsTheTransfer_AndDiscardsThePartialFile` | both are removed |
| `PausedDownload_ResumesFromWhereItStopped` | resuming to the same path continues and completes |

A note for whoever extends the test server: handle each request on its own task and tolerate the
client aborting. Pause aborts a response mid-write, and handling requests inline meant that threw
out of the accept loop and silently stopped the server, so the *next* request hung for ever.

### Phase 3 — Unfinished list and settings — DONE

- [x] `list_partials` on startup, after any transfer ends, and after the folder changes
- [x] An Unfinished panel with Resume/Discard, hidden when there is nothing to resume
- [x] Resume sends `download` with the **same `path`**, which is what continues a transfer
      rather than starting it again
- [x] The button reads **Restart**, not Resume, on a non-resumable source, with the tier as the
      tooltip. Calling it Resume there would be a lie
- [x] Discard confirms first, then removes the partial and its metadata
- [x] The folder comes from `get_settings` and is shown in the status bar; `set_settings` saves
      it, shared with the extension. The `OpenFileDialog` hack was replaced with a real folder
      picker in phase 1

| Test | Proves |
|---|---|
| `PausedDownload_IsListedByAFreshConnection` | a transfer interrupted in one session is listed by a brand-new host process, so it survives an app restart |
| `RunningDownload_IsNotListedAsUnfinished` | a live transfer is not also shown as resumable |
| `Discard_RemovesThePartialFromDiskAndTheList` | discard clears both files and the listing |
| `DownloadFolder_RoundTripsAndIsUsed` | a download with no path lands in the configured folder |
| `SetDownloadFolder_RejectsAPathThatCannotBeUsed` | a bad folder is refused up front, not mid-download |

The resume list is per-folder, because `list_partials` scans the download folder. Changing the
folder therefore changes what can be resumed, and the app refreshes the list when it changes.

### Phase 4 — Polish — DONE, except the tray

- [x] Errors have their own `Message` field and a **Details** column. They were being written
      into `TierText`, so a failure was shown to the user under the heading "Tier"
- [x] Empty state on the transfers grid
- [x] Buttons disable while a stop is in flight (`IsStopping`, from phase 2)
- [x] Tier tooltip carrying the same explanation the extension gives
- [x] A registration failure shows as a banner. It used to be appended to the window title,
      where nobody would read it -- and it is the one failure that stops the browser extension
      working at all
- [x] Closing with transfers running asks first. Closing disposes the connection, which closes
      the host's stdin and ends every transfer it is running; on a non-resumable source those
      bytes are gone

**Tray icon and close-to-tray: deliberately not done.** Not because it is hard, but because it
is not only a widget. WPF has no built-in tray icon, so it means WinForms interop or a third
party package, and "close to tray" implies the app keeps running and keeps the host alive so
downloads continue. That is a product decision about what closing the window means, and it
interacts with the point above. Worth deciding explicitly rather than implying it with an icon.

## Deliberately out of scope

- Reimplementing any download logic in the app. The host owns it. Two implementations would
  drift and one would be wrong.
- Duplicating the extension's browser takeover. The app manages transfers; the extension
  captures them.
- Torrents. Different protocol, different engine, no shared code — a separate decision.

## Store context

The app is what a Store user installs first, and it is what registers the native host with the
browsers. That is its real product role: set everything up, manage transfers, and point the user
at the browser extension. Packaging is already proven — see ROADMAP.md.
