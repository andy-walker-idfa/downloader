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

## new-signing-key.ps1

Generates the key pair that fixes the extension's ID.

```powershell
.
ew-signing-key.ps1
```

An extension's ID is the first 16 bytes of the SHA-256 of its public key, mapped to `a`-`p`.
The public half goes into `extension/manifest.json` as `key` and is committed; the private half
is written to `%LOCALAPPDATA%\WindowsDownloader\extension-signing-key.pem`, owner-only, and must
never be committed. `*.pem` is gitignored.

**Back the private key up.** It is what proves a later update comes from you. Losing it means a
new ID, which means a broken install for everyone who has it.

Without a key, Chromium falls back to hashing the extension's absolute directory path, so the ID
changes with the install location. That is fine for sideloading on one machine and impossible to
distribute. `install.ps1` prefers the key and reports which source it used.

Regenerating the key changes the ID, so the script refuses unless given `-Force`.

## test_host_e2e.ps1

Drives the host exactly as a browser does — launched with the caller origin as `argv[0]`, stdin
held open, reading every framed reply.

```powershell
.\test_host_e2e.ps1 -PingOnly
.\test_host_e2e.ps1
.\test_host_e2e.ps1 -Pause 6      # graceful pause, partial kept
.\test_host_e2e.ps1 -Interrupt 6   # kill mid-stream
```

## test-probe.ps1

Classifies a single URL and explains the tier.

```powershell
.\test-probe.ps1 -Url "https://cdn.kernel.org/pub/linux/kernel/v6.x/linux-6.0.tar.xz"
```

## build-msix-layout.ps1

Stages the app with the host in a `host\` subfolder beside it, which is the arrangement a
packaged build uses and the first place `HostLocator` looks.

```powershell
.uild-msix-layout.ps1                    # framework-dependent, for MSIX registration here
.uild-msix-layout.ps1 -SelfContained     # portable: bundles the .NET runtime
```

`-SelfContained` produces a folder that runs on a machine with no .NET installed - no installer,
no admin, no Developer Mode. It is around 230 MB because WPF cannot be trimmed. Both the app and
the host are published self-contained: they are separate processes with separate runtime
resolution, so a self-contained app beside a framework-dependent host still fails on a clean
machine.

Copy the folder to a local disk before running it. Started from removable media, the app
registers the host at that path, and unplugging the drive leaves every browser pointing at
nothing.

It stages onto an NTFS volume by default and refuses otherwise, because Windows cannot deploy
MSIX packages from exFAT.

## AppxManifest.xml and Assets/

MSIX packaging for the WPF app, as a full-trust desktop package. Not yet submittable — the
identity is a placeholder and native messaging registration from inside a package is unsolved.
See [../ROADMAP.md](../ROADMAP.md).
