# Tier Detection — HTTP Resume Capability Classification

This downloader implements a **tier-based classification system** to automatically detect whether a remote server supports true byte-range resume. This is critical because many servers advertise `Accept-Ranges: bytes` but do not actually honor range requests properly.

## The Four Resumability Tiers

### Tier 0: Fully Resumable ✓
**Definition:** Server returns `206 Partial Content` with a strong ETag and known `Content-Length`.

**Behavior:**
- Full resume support with `If-Range` validation
- Safe parallel segmentation (future feature)
- Crash recovery via ETag + byte offsets

**Example:**
```
HEAD /file.iso
→ Accept-Ranges: bytes
→ ETag: "abc123"
→ Content-Length: 5000000

GET /file.iso with Range: bytes=0-0
→ 206 Partial Content
→ Content-Range: bytes 0-0/5000000
→ ETag: "abc123"
```

---

### Tier 1: Resumable but Unverified
**Definition:** Server returns `206 Partial Content` and `Content-Length`, but no strong ETag.

**Behavior:**
- Resume is possible but less safe
- Uses an overlap check (re-fetch last 64 KB) before appending to verify file identity
- Single-stream download

**When This Happens:**
- File servers that don't track ETags
- Some CDNs that use weak validators only
- Servers that omit ETag entirely

---

### Tier 2: Not Resumable ✗
**Definition:** Server responds `200 OK` to a `Range` request, OR advertises `Accept-Ranges: none`.

**Critical Gotcha:** A server that claims `Accept-Ranges: bytes` but ignores the `Range` header and sends the full file (HTTP 200) is **NOT resumable**, even though the header looks good.

**Behavior:**
- No resume on interruption — starts from byte 0
- Discard partial file if interrupted
- Retry with backoff if connection lost

**Example (The Gotcha Case):**
```
HEAD /file
→ Accept-Ranges: bytes  (looks good!)

GET /file with Range: bytes=1048576-1049575
→ 200 OK  (GOTCHA! Ignoring the range request)
→ Content-Length: 5000000
→ Body: full file from byte 0

Result: TIER 2, not resumable
```

This is the case that motivated the tier system: a real file host that advertises nothing,
ignores ranges, and returns the whole file for any request.

---

### Tier 3: Unbounded Stream
**Definition:** No `Content-Length`, `Transfer-Encoding: chunked`, or response is streaming.

**Behavior:**
- No progress bar (size unknown)
- No resume (can't seek to prior position)
- Single-stream only

---

## How It Works: The Probe Algorithm

When you start a download, the native host runs this sequence:

1. **HEAD request** — check advertised headers (`Accept-Ranges`, `ETag`, `Content-Length`)
2. **GET with `Range: bytes=0-0`** — the ground-truth test
   - If `206 Partial Content` → server honors ranges
   - If `200 OK` → server ignores ranges (gotcha!)
3. **Validate `Content-Range`** — ensure start byte is correct
4. **Extract total size** from `Content-Range: bytes 0-0/[SIZE]`
5. **Classify tier** based on validator presence (strong ETag, weak ETag, none)

All this happens **before** the download starts, so the UI can report whether resume is possible.

---

## Native Messaging Protocol Changes

### New "probe" Command

```json
{
  "cmd": "probe",
  "url": "https://example.com/file.iso"
}
```

**Response:**
```json
{
  "status": "probed",
  "url": "https://example.com/file.iso",
  "tier": "FullyResumable",
  "resumable": true,
  "contentLength": 5000000,
  "etag": "\"abc123\"",
  "lastModified": "Wed, 29 Aug 2024 10:00:00 GMT"
}
```

### Updated "download" Command

Now includes tier information in responses:

```json
{
  "status": "started",
  "url": "https://example.com/file.iso",
  "path": "C:\\Users\\User\\Downloads\\file.iso",
  "tier": "FullyResumable",
  "resumable": true,
  "contentLength": 5000000
}
```

---

## Metadata File (`.part.meta`)

Each partial download stores its probe results and state in a JSON file:

```json
{
  "url": "https://example.com/file.iso",
  "contentLength": 5000000,
  "etag": "\"abc123\"",
  "lastModified": "Wed, 29 Aug 2024 10:00:00 GMT",
  "tier": "FullyResumable",
  "bytesDownloaded": 2500000,
  "createdAt": "2024-08-29T15:30:00Z",
  "lastAttempt": "2024-08-29T15:35:00Z"
}
```

On resume, if the URL is the same, the cached tier is reused. This avoids re-probing on every resume.

---

## Behavior by Tier

| Tier | Resume? | Strategy | On Interrupt |
|------|---------|----------|--------------|
| **0** | ✓ Full | If-Range with ETag, append | Resume from last byte |
| **1** | ◐ Safe | Overlap check, then append | Resume with verification |
| **2** | ✗ None | Restart-only | Delete .part, start from 0 |
| **3** | ✗ None | Stream to disk | Restart from 0 |

---

## Real-World Examples

### Example 1: GitHub Releases (Tier 0)
```
$ curl -I https://github.com/torvalds/linux/archive/refs/tags/v6.0.tar.gz
200 OK
Accept-Ranges: bytes
ETag: "12345"
Content-Length: 234567890

$ curl -H "Range: bytes=0-0" https://...
206 Partial Content
Content-Range: bytes 0-0/234567890
```
→ **Tier 0: Fully Resumable**

---

### Example 2: A non-resumable file host (Tier 2)
```
$ curl -I https://gamepressure.com/...
200 OK
(no Accept-Ranges header)
Content-Length: 11191440831

$ curl -H "Range: bytes=0-0" https://...
200 OK
Content-Length: 11191440831
Content-Range: (none)
```
→ **Tier 2: Not Resumable** — server ignores ranges, sends full file

Solution: Use mirror fallback, site-specific resolver, or accept single-stream download.

---

### Example 3: CDN without ETag (Tier 1)
```
$ curl -I https://cdn.example.com/file.zip
200 OK
Accept-Ranges: bytes
Content-Length: 100000000

$ curl -H "Range: bytes=0-0" https://...
206 Partial Content
Content-Range: bytes 0-0/100000000
(no ETag)
```
→ **Tier 1: Resumable Unverified** — resume possible, but requires overlap check

---

## Testing Tier Detection

See [TESTING.md](TESTING.md) for validation commands.

Quick test against your target URL:

```powershell
# Probe with native host
curl.exe -X POST --data '{"cmd":"probe","url":"https://your-url"}' http://localhost:9999

# Or manual curl tests
curl.exe -sS -H "Accept-Encoding: identity" -r 0-0 -L "https://your-url"

# Look for:
# - Status 206 → range-capable
# - Status 200 → not range-capable (gotcha!)
# - Content-Range: bytes 0-0/[SIZE] → confirms total size
```

---

## What's Next

This implementation handles:
- ✓ Tier detection on start
- ✓ Tier-aware resume logic
- ✓ Metadata persistence
- ✓ Gotcha detection (200 OK to range)
- ✓ Proper fallback for tier 2 (restart)

Planned engine work is tracked in [../ROADMAP.md](../ROADMAP.md).
