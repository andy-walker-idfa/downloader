const HOST_NAME = "com.downloader.host";
const MENU_DOWNLOAD = "download_with_downloader";
const LOG_KEY = "eventLog";
const INTERCEPT_KEY = "interceptEnabled";
const MAX_LOG = 150;

// Off by default: taking over every browser download without being asked would be hostile.
let interceptEnabled = false;

chrome.storage.local.get(INTERCEPT_KEY).then((stored) => {
  interceptEnabled = stored[INTERCEPT_KEY] === true;
});

// A single long-lived port for the whole session.
//
// chrome.runtime.sendNativeMessage() cannot be used here: it delivers one message, waits for
// exactly ONE reply, then terminates the host process. Our host replies "started" first and
// streams the file afterwards, so a one-shot send kills every download before any bytes land.
// connectNative keeps the process alive until we disconnect.
let port = null;
let nextRequestId = 1;

/** In-flight and recently finished requests, keyed by request id. */
const requests = new Map();

/** Unfinished downloads the host found on disk, refreshed while the popup is open. */
let partials = [];
let lastPartialsAt = 0;

// The popup polls popup_state roughly every 700ms while it is open, so a recent poll is a
// reliable "the user is looking at the UI right now" signal.
let lastPopupPollAt = 0;
const POPUP_OPEN_WINDOW_MS = 1500;

function isPopupOpen() {
  return Date.now() - lastPopupPollAt < POPUP_OPEN_WINDOW_MS;
}

// Last URL the content script resolved under the cursor. Scoped to the tab it came from and
// short-lived on purpose: a global, never-expiring value meant that right-clicking anything
// without an <a href> reused whatever link was hovered last -- on a completely different page.
let lastCandidate = null;
const CANDIDATE_TTL_MS = 15000;

function freshCandidateFor(tabId) {
  if (!lastCandidate) return null;
  if (lastCandidate.tabId !== tabId) return null;
  if (Date.now() - lastCandidate.at > CANDIDATE_TTL_MS) return null;
  return lastCandidate;
}

function connect() {
  if (port) return port;

  try {
    port = chrome.runtime.connectNative(HOST_NAME);
  } catch (err) {
    logEvent("error", `connectNative threw: ${err.message}`);
    port = null;
    return null;
  }

  port.onMessage.addListener(onHostMessage);
  port.onDisconnect.addListener(() => {
    const reason = chrome.runtime.lastError?.message || "port closed";
    port = null;

    // Anything still running died with the port.
    let orphaned = 0;
    for (const req of requests.values()) {
      if (req.status === "started" || req.status === "progress" || req.status === "sent") {
        req.status = "error";
        req.error = `host disconnected: ${reason}`;
        orphaned += 1;
      }
    }

    logEvent(orphaned ? "error" : "info", `Native host disconnected (${reason})`);
    updateBadge();
  });

  logEvent("info", "Connected to native host");
  return port;
}

function send(message) {
  const p = connect();
  if (!p) return false;

  try {
    p.postMessage(message);
    return true;
  } catch (err) {
    logEvent("error", `postMessage failed: ${err.message}`);
    port = null;
    return false;
  }
}

function onHostMessage(msg) {
  if (!msg) return;

  const req = msg.id != null ? requests.get(String(msg.id)) : null;

  if (["finished", "paused", "cancelled", "discarded", "error"].includes(msg.status)) {
    lastPartialsAt = 0;
  }

  if (req) {
    req.status = msg.status;
    req.lastUpdate = Date.now();
    if (msg.tier) req.tier = msg.tier;
    if (msg.resumable != null) req.resumable = msg.resumable;
    if (msg.path) req.path = msg.path;
    if (msg.received != null) req.received = msg.received;
    if (msg.total != null) req.total = msg.total;
    if (msg.contentLength != null) req.total = msg.contentLength;
    if (msg.message) req.error = msg.message;
  }

  switch (msg.status) {
    case "started":
      logEvent("info", `Started ${msg.tier} — ${formatBytes(msg.contentLength)} → ${msg.path}`);
      notify(
        "Download started",
        `${fileNameOf(msg.path)}\n${formatBytes(msg.contentLength)} — ${msg.tier}

${tierAdvice(msg.tier)}`,
        "start"
      );
      break;
    case "progress":
      // Deliberately not logged or notified: progress fires twice a second.
      break;
    case "finished":
      logEvent("ok", `Finished ${formatBytes(msg.bytes)} → ${msg.path}`);
      notify("Download complete", `${fileNameOf(msg.path)}\n${formatBytes(msg.bytes)}`, "done");
      break;
    case "partials":
      partials = Array.isArray(msg.items) ? msg.items : [];
      break;
    case "discarded":
      logEvent("info", `Discarded partial: ${fileNameOf(msg.path)}`);
      partials = partials.filter((p) => p.path !== msg.path);
      break;
    case "paused":
      logEvent("ok", `Paused at ${formatBytes(msg.bytes)} — ${fileNameOf(msg.path)}`);
      notify(
        "Download paused",
        `${fileNameOf(msg.path)}
Paused at ${formatBytes(msg.bytes)}.

Resume it from the Unfinished list in the popup.`,
        "pause"
      );
      break;
    case "cancelled":
      logEvent("warn", `Cancelled at ${formatBytes(msg.bytes)} — ${fileNameOf(msg.path)}`);
      notify(
        "Download cancelled",
        `${fileNameOf(msg.path)}\nStopped at ${formatBytes(msg.bytes)}.${msg.resumable ? " Download it again to resume from here." : ""}`,
        "cancel"
      );
      break;
    case "error":
      logEvent("error", `Host error: ${msg.message}`);
      notify("Download failed", msg.message, "error");
      break;
    default:
      logEvent("info", `Host: ${JSON.stringify(msg).slice(0, 200)}`);
  }

  updateBadge();
}

function request(cmd, url, extra = {}) {
  if (!url || !/^https?:\/\//i.test(url)) {
    logEvent("warn", `Ignored non-http URL: ${url || "(empty)"}`);
    return null;
  }

  // One file, one download. Two requests for the same URL race for the same .part file, and
  // the loser dies on a file-sharing violation -- which is what produced two entries in the
  // transfer list, only one of which was real.
  if (cmd === "download") {
    const live = [...requests.values()].find(
      (r) => r.cmd === "download" && r.url === url &&
             ["sent", "started", "progress", "pausing", "cancelling"].includes(r.status)
    );
    if (live) {
      logEvent("warn", `Already downloading ${fileNameOf(url)} — ignored a duplicate request (${extra.source || "unknown"})`);
      return live.id;
    }
  }

  const id = String(nextRequestId++);
  const entry = {
    id,
    cmd,
    url: url || "",
    status: "sent",
    started: Date.now(),
    lastUpdate: Date.now(),
    received: 0,
    total: null,
    tier: null,
    path: null,
    error: null
  };
  requests.set(id, entry);
  pruneRequests();

  const ok = send({ cmd, id, url, source: "extension", timestamp: Date.now(), ...extra });
  if (!ok) {
    entry.status = "error";
    entry.error = "could not reach native host — is it registered?";
    notify(
      "Native host unreachable",
      "Chrome could not start com.downloader.host. Re-run packaging\\install.ps1 and reload the extension.",
      "error"
    );
  }

  updateBadge();
  return id;
}

function pruneRequests() {
  if (requests.size <= 40) return;
  const done = [...requests.values()]
    .filter((r) => r.status === "finished" || r.status === "error" || r.status === "cancelled")
    .sort((a, b) => a.lastUpdate - b.lastUpdate);
  for (const r of done.slice(0, requests.size - 40)) requests.delete(r.id);
}

function activeCount() {
  let n = 0;
  for (const r of requests.values()) {
    if (r.status === "started" || r.status === "progress" || r.status === "cancelling" || r.status === "pausing" || (r.status === "sent" && r.cmd === "download")) n += 1;
  }
  return n;
}

function updateBadge() {
  const n = activeCount();
  chrome.action.setBadgeText({ text: n ? String(n) : "" });
  chrome.action.setBadgeBackgroundColor({ color: "#1a73e8" });
}

function fileNameOf(pathOrUrl) {
  if (!pathOrUrl) return "(unknown)";
  try {
    const withoutQuery = pathOrUrl.split("?")[0];
    return decodeURIComponent(withoutQuery.split(/[\\/]/).pop()) || withoutQuery;
  } catch {
    return pathOrUrl;
  }
}

function tierAdvice(tier) {
  switch (tier) {
    case "FullyResumable":
      return "Range requests honoured with a strong ETag. Safe to resume.";
    case "ResumableUnverified":
      return "Range requests honoured but no strong validator. Resume works, but the server could swap the file underneath.";
    case "NotResumable":
      return "Server ignores Range requests. An interrupted download must restart from zero.";
    case "UnboundedStream":
      return "No Content-Length. Size is unknown and resume is impossible.";
    default:
      return "";
  }
}

function shortUrl(url) {
  if (!url) return "";
  return url.length > 70 ? `${url.slice(0, 67)}...` : url;
}

function formatBytes(n) {
  if (n == null) return "unknown size";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

// Desktop notification, for outcomes the user cannot already see.
//
// If the popup is open it is the better UI -- live progress, tier, errors and the event log are
// all right there -- so a notification would just be duplicate noise. Notifications exist for
// the cases with no visible surface: a context-menu download, or a takeover of a download the
// user started by clicking a link on a page, with the popup closed.
function notify(title, message, tag = "downloader") {
  if (isPopupOpen()) return;

  try {
    chrome.notifications.create(`${tag}-${Date.now()}`, {
      type: "basic",
      iconUrl: chrome.runtime.getURL("icons/icon128.png"),
      title,
      message: message.length > 300 ? `${message.slice(0, 297)}...` : message,
      priority: 0
    });
  } catch (err) {
    console.warn("notification failed", err);
  }
}

async function logEvent(level, text) {
  const entry = { level, text, at: Date.now() };
  console.log(`[downloader:${level}]`, text);

  try {
    const stored = await chrome.storage.local.get(LOG_KEY);
    const log = stored[LOG_KEY] || [];
    log.push(entry);
    await chrome.storage.local.set({ [LOG_KEY]: log.slice(-MAX_LOG) });
  } catch {
    // Storage failures must never break a download.
  }
}

// --- Taking over downloads the browser starts --------------------------------------------
//
// This is what makes link-less sites work. Pages that download via a POST form or JS navigation
// (gamepressure, for one) expose no URL to scrape from the DOM -- the file only exists as the
// response to a request the page makes. Rather than guessing, we let the page do its thing and
// grab the download once Chrome has resolved it.

/** Chrome's own cookies for this URL, so session-bound mirror links still authorise. */
async function collectHeaders(url, referrer) {
  const headers = {};

  try {
    const cookies = await chrome.cookies.getAll({ url });
    if (cookies.length) {
      headers.Cookie = cookies.map((c) => `${c.name}=${c.value}`).join("; ");
    }
  } catch (err) {
    console.warn("cookie lookup failed", err);
  }

  if (referrer) headers.Referer = referrer;
  headers["User-Agent"] = navigator.userAgent;

  return headers;
}

chrome.downloads.onCreated.addListener(async (item) => {
  if (!interceptEnabled) return;

  const url = item.finalUrl || item.url || "";
  if (!/^https?:\/\//i.test(url)) return; // blob:/data:/file: cannot be handed off

  // Cancel Chrome's copy and erase the shelf entry, so there is exactly one download running.
  try {
    await chrome.downloads.cancel(item.id);
    await chrome.downloads.erase({ id: item.id });
  } catch (err) {
    logEvent("warn", `Could not cancel Chrome's download: ${err.message}`);
    return;
  }

  const filename = item.filename ? item.filename.split(/[\\/]/).pop() : "";
  const headers = await collectHeaders(url, item.referrer);

  logEvent("info", `Took over browser download: ${fileNameOf(filename || url)}`);
  request("download", url, { source: "intercept", filename, referrer: item.referrer || "", headers });
});

chrome.runtime.onInstalled.addListener(() => {
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: MENU_DOWNLOAD,
      title: "Download with Downloader",
      contexts: ["link", "page", "selection", "image", "video", "audio"]
    });
  });
  updateBadge();
});

function resolveUrlFromContext(info, tab) {
  // Strictly ordered: what Chrome says was under the cursor always wins. The content script's
  // guess is only a fallback for JS-driven buttons, and only from this tab, and only if recent.
  const direct = [info.linkUrl, info.srcUrl].find((c) => c && /^https?:\/\//i.test(c));
  if (direct) return direct;

  const selected = (info.selectionText || "").trim();
  if (/^https?:\/\/\S+$/i.test(selected)) return selected;

  const candidate = freshCandidateFor(tab?.id);
  if (candidate) return candidate.url;

  // No link anywhere: fall back to the page itself only when the user explicitly asked for a
  // page-context action. Never silently reuse an unrelated URL.
  return "";
}

chrome.contextMenus.onClicked.addListener((info, tab) => {
  const url = resolveUrlFromContext(info, tab);
  if (!url) {
    logEvent("warn", "No downloadable URL found at the click target");
    notify("Nothing to download", "No http(s) link was found where you right-clicked.", "warn");
    return;
  }

  if (info.menuItemId === MENU_DOWNLOAD) {
    request("download", url, { source: "contextmenu", pageUrl: info.pageUrl || "", referrer: info.pageUrl || "", title: tab?.title || "" });
  }
});

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!message) return false;

  switch (message.type) {
    // The content script only *records* what is under the cursor. It must never trigger a
    // download on its own -- an earlier version auto-scanned every page and fired downloads
    // for whatever link it guessed at, with no user action.
    case "context_candidate":
      lastCandidate = message.url
        ? { url: message.url, title: message.title, tabId: sender?.tab?.id, at: Date.now() }
        : null;
      sendResponse({ ok: true });
      return false;

    case "popup_download":
      sendResponse({ ok: true, id: request("download", message.url, { source: "popup" }) });
      return false;

    case "popup_pause": {
      const entry = requests.get(String(message.id));
      if (entry) entry.status = "pausing";
      const ok = send({ cmd: "pause", id: `pause-${message.id}`, target: String(message.id) });
      if (!ok) logEvent("error", "Pause could not be delivered — host is not connected");
      updateBadge();
      sendResponse({ ok });
      return false;
    }

    case "popup_cancel": {
      const entry = requests.get(String(message.id));
      if (entry) entry.status = "cancelling";
      // 'target' names the download to stop; the message itself is a separate request.
      const ok = send({ cmd: "cancel", id: `cancel-${message.id}`, target: String(message.id) });
      if (!ok) logEvent("error", "Cancel could not be delivered — host is not connected");
      updateBadge();
      sendResponse({ ok });
      return false;
    }

    case "popup_state":
      lastPopupPollAt = Date.now();
      // Refresh the on-disk resume list, but throttled: the popup polls continuously and an
      // unthrottled scan meant a directory walk and a log line every single tick.
      if (Date.now() - lastPartialsAt > 3000) {
        lastPartialsAt = Date.now();
        send({ cmd: "list_partials", id: `lp-${Date.now()}` });
      }
      sendResponse({
        connected: !!port,
        intercept: interceptEnabled,
        // Second line of defence against showing one transfer twice: the host already omits
        // running downloads, but its list is up to 3s stale, so drop anything live right now.
        partials: partials.filter(
          (p) =>
            ![...requests.values()].some(
              (r) =>
                r.cmd === "download" &&
                r.path === p.path &&
                ["sent", "started", "progress", "pausing", "cancelling"].includes(r.status)
            )
        ),
        requests: [...requests.values()]
          .filter((r) => r.cmd === "download")
          .sort((a, b) => b.started - a.started)
          .slice(0, 12)
      });
      return false;

    case "popup_resume":
      logEvent("info", `Resuming ${fileNameOf(message.path)}`);
      sendResponse({ ok: true, id: request("download", message.url, { source: "resume", path: message.path }) });
      return false;

    case "popup_discard":
      send({ cmd: "discard", id: `disc-${Date.now()}`, path: message.path });
      sendResponse({ ok: true });
      return false;

    case "popup_set_intercept":
      interceptEnabled = !!message.enabled;
      chrome.storage.local.set({ [INTERCEPT_KEY]: interceptEnabled });
      logEvent("info", `Take over browser downloads: ${interceptEnabled ? "ON" : "OFF"}`);
      sendResponse({ ok: true, intercept: interceptEnabled });
      return false;

    default:
      return false;
  }
});

updateBadge();
