# Invariants

Things that must stay true. Each one exists because breaking it caused a real failure, and most
of those failures were silent — a download that vanished, a file that was the right size and
wrong inside, a registration that pointed at nothing.

If a change here looks like harmless simplification, read the reason before making it.

## Browser extension

**Use `chrome.runtime.connectNative`, never `chrome.runtime.sendNativeMessage`.**
`sendNativeMessage` terminates the host after its *first* reply. The host answers `started`
before streaming a byte, so a one-shot send kills every download before anything lands — with no
error anywhere. This cost days of debugging.

**Keep sending `progress` roughly twice a second.**
It is not only for the progress bar. An MV3 service worker sleeps after ~30s idle, and when it
sleeps the port closes and the host dies. Progress traffic keeps the worker alive for the
duration of a transfer. Removing it to "reduce noise" breaks long downloads.

**Never remove `key` from `extension/manifest.json`.**
It fixes the extension ID (`febdocdjpdhmfddcddbobidgpjhckemo`). Without it Chromium derives the
ID from the install *path*, so it changes per machine and no distributed build can work. The
private half lives at `%LOCALAPPDATA%\WindowsDownloader\extension-signing-key.pem`, is
gitignored, and must be backed up: losing it means a new ID and a broken install for every user.

**The content script must never start a download.**
An early version scanned every page on load and on DOM mutation, then sent whatever link it
guessed at — starting real downloads with no user action. It now reports only on right-click.

**Report right-click misses too.**
Staying silent on a miss leaves the previous candidate in place, so clicking the menu item on a
non-link acts on a URL from another page. Candidates are scoped to the originating tab and
expire after 15s for the same reason.

**Guess nothing.** If there is no http(s) URL where the user clicked, say so. Sites that download
via POST forms genuinely have no URL to scrape, and inventing one is worse than reporting none.

**Tolerate an orphaned content script.** Reloading the extension leaves old content scripts
running in open tabs with a dead `chrome.runtime`. Check `chrome.runtime?.id`, pass a callback
that reads `lastError`, and detach on failure.

## Native host

**`HttpClient.Timeout` must be `Timeout.InfiniteTimeSpan`.**
The default 100s covers the response *body*, so any download longer than that aborts mid-stream.

**`HEAD` never decides resumability. The ranged `GET` does.**
Tokenised CDN URLs commonly answer 403 to `HEAD` while serving ranges fine. Bailing out on a
failed `HEAD` reported such servers as non-resumable without ever asking for a range.

**A `200 OK` to a `Range` request means NOT resumable** — even when the server advertises
`Accept-Ranges: bytes`. Trusting that header is how download managers corrupt files.

**Write `.part.meta` before the first byte and checkpoint it every ~2s.**
Crash recovery depends on it. A `.part` with no metadata cannot be resumed safely.

**Take the resume offset from the `.part` file's actual length**, not from the metadata, which
may be up to a checkpoint stale.

**Use `If-Range` with the ETag, and treat a `200` reply to a ranged resume as "the file
changed".** Delete the partial and restart. Never append to bytes from a different file.

**One shared stdout, guarded by a semaphore.**
Native messaging frames are length-prefixed. Two concurrent writers interleave and desynchronise
the browser's parser for the rest of the session.

**`UseCookies = false` on the HTTP handler.** The host forwards the browser's `Cookie` header
verbatim; a handler cookie container would overwrite it and session-bound URLs would 403.

**Probe once per download.** Pass the probed metadata into `DownloadAsync`. It used to probe
again internally, costing an extra `HEAD` plus ranged `GET` on every download.

**Pause and cancel are different.** Pause keeps the `.part` for resuming; cancel deletes it.
They were once the same operation with different wording, which made cancel a synonym for pause
and left abandoned downloads in the resume list forever.

**Refuse a second download to a path already being written.** Two writers race for one file
handle and the loser dies on a sharing violation.

**`list_partials` must exclude downloads that are currently running.** A running transfer always
has a `.part`, so including it makes one download appear twice — once live, once as a stale
resume candidate.

**Filename order: caller, then `Content-Disposition`, then the URL.** URLs often carry no
filename at all; `/download_subtitle/en` was saved as a file named `en`.

**Logging is on by default.** A browser cannot pass flags to a native messaging host, so a silent
host is undiagnosable.

**Logs, settings and the host manifest live in `%LOCALAPPDATA%\WindowsDownloader`, never in the
repository.** See the registration section.

## Registration and packaging

**The native messaging manifest must live outside the repo.**
It was generated into `packaging/`, and browsers hold an absolute path to it in the registry.
Deleting it during a cleanup broke every browser at once, with nothing obviously wrong.

**Chromium requires `allowed_origins`.** `allowed_extensions` is the Firefox spelling and is
silently ignored — no error, just a host that never authorises.

**Brave's registry key is `BraveSoftware\Brave-Browser`,** not `BraveSoftware\Brave`.

**A packaged app must register the host at runtime.** Declaring the keys in the MSIX package puts
them in the virtualised registry, where browsers outside the container cannot see them.

**Register the stable copy under `%LOCALAPPDATA%\WindowsDownloader\host`, never the package
path.** A package installs to a version-stamped folder, so every update relocates the host and
deletes the old one, leaving the registration pointing at nothing.

**MSIX cannot deploy from exFAT.** Stage package layouts on NTFS. The same drive property makes
git report "detected dubious ownership".

## Working on this repo

**Never blanket-kill `DownloaderHost` to unlock the binary.**
A running host is often serving a live browser download, not a stale orphan. Check for `*.part`
files in the download folder modified in the last 30 seconds first. Doing this without checking
destroyed 1.47 GB of a download from a non-resumable source, which could not be recovered.
`install.ps1` and `build-msix-layout.ps1` enforce the check; ad-hoc commands must too.

**Verify resume with SHA-256 against the upstream file, never by size.**
A file spliced from mismatched bytes has exactly the right size. Size proves nothing.

**Run the PowerShell test scripts in a real PowerShell console.** Invoking
`powershell.exe -File` from a bash shell with redirected stdio makes `test_host_e2e.ps1` hang;
the script is fine.

**Do not add site-specific handling for particular websites.** The extension resolves URLs
generically and hands them to the host. Sites that defeat that do so deliberately.
