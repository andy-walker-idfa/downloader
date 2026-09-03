const LOG_KEY = "eventLog";

const el = {
  conn: document.getElementById("conn"),
  tabUrl: document.getElementById("tabUrl"),
  transfers: document.getElementById("transfers"),
  log: document.getElementById("log"),
  intercept: document.getElementById("intercept"),
  partials: document.getElementById("partials")
};

let currentUrl = "";

function formatBytes(n) {
  if (n == null) return "?";
  if (n < 1024) return `${n} B`;
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`;
  if (n < 1024 * 1024 * 1024) return `${(n / 1024 / 1024).toFixed(1)} MB`;
  return `${(n / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

function ask(type, payload = {}) {
  return new Promise((resolve) => {
    chrome.runtime.sendMessage({ type, ...payload }, (response) => {
      if (chrome.runtime.lastError) return resolve(null);
      resolve(response);
    });
  });
}

function renderTransfers(requests) {
  el.transfers.replaceChildren();

  if (!requests || requests.length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty";
    empty.textContent = "Nothing yet. Right-click a link, or use the buttons above.";
    el.transfers.append(empty);
    return;
  }

  for (const r of requests) {
    const item = document.createElement("div");
    item.className = "item";

    const top = document.createElement("div");
    top.className = "top";

    const name = document.createElement("span");
    name.className = "name";
    name.textContent = r.path ? r.path.split("\\").pop() : r.url || r.cmd;

    const meta = document.createElement("span");
    meta.className = "meta";
    if (r.status === "progress" || r.status === "started") {
      meta.textContent = `${formatBytes(r.received)}${r.total ? ` / ${formatBytes(r.total)}` : ""}`;
    } else {
      meta.textContent = r.status;
    }

    top.append(name, meta);
    item.append(top);

    const isActive = r.cmd === "download" && ["sent", "started", "progress"].includes(r.status);
    const stopping = r.status === "cancelling" || r.status === "pausing";
    if (isActive || stopping) {
      const actions = document.createElement("div");
      actions.className = "actions";

      // Pause is only meaningful when the server honours ranges. On a non-resumable source
      // "pause" would silently mean "throw away everything downloaded so far", so it is not
      // offered -- Cancel is the honest word there.
      if (r.resumable) {
        const pause = document.createElement("button");
        pause.className = "pause";
        pause.textContent = r.status === "pausing" ? "Pausing…" : "Pause";
        pause.disabled = stopping;
        pause.title = "Stop now and keep the partial file; resume it later";
        pause.addEventListener("click", async () => {
          pause.disabled = true;
          pause.textContent = "Pausing…";
          await ask("popup_pause", { id: r.id });
          setTimeout(refresh, 200);
        });
        actions.append(pause);
      }

      const cancel = document.createElement("button");
      cancel.className = "cancel";
      cancel.textContent = r.status === "cancelling" ? "Cancelling…" : "Cancel";
      cancel.disabled = stopping;
      cancel.title = r.resumable
        ? "Stop and delete the partial file. Use Pause to keep it."
        : "Stop and delete the partial file";
      cancel.addEventListener("click", async () => {
        cancel.disabled = true;
        cancel.textContent = "Cancelling…";
        await ask("popup_cancel", { id: r.id });
        setTimeout(refresh, 200);
      });

      actions.append(cancel);
      item.append(actions);
    }

    const tags = document.createElement("div");
    if (r.tier) {
      const tag = document.createElement("span");
      tag.className = "tag";
      tag.textContent = r.tier;
      tags.append(tag);
    }
    if (r.error) {
      const err = document.createElement("span");
      err.className = "tag lvl-error";
      err.textContent = r.error;
      tags.append(" ", err);
    }
    if (tags.childNodes.length) item.append(tags);

    if (r.total && (r.status === "progress" || r.status === "started")) {
      const bar = document.createElement("div");
      bar.className = "bar";
      const fill = document.createElement("i");
      fill.style.width = `${Math.min(100, (r.received / r.total) * 100).toFixed(1)}%`;
      bar.append(fill);
      item.append(bar);
    }

    el.transfers.append(item);
  }
}

// Unfinished downloads are read from disk by the host, so this list survives a browser restart,
// a service-worker shutdown and a crash -- which is exactly when you need it.
function renderPartials(items) {
  el.partials.replaceChildren();

  if (!items || items.length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty";
    empty.textContent = "No unfinished downloads.";
    el.partials.append(empty);
    return;
  }

  for (const p of items) {
    const item = document.createElement("div");
    item.className = "item";

    const top = document.createElement("div");
    top.className = "top";

    const name = document.createElement("span");
    name.className = "name";
    name.textContent = p.fileName;

    const meta = document.createElement("span");
    meta.className = "meta";
    meta.textContent = `${formatBytes(p.bytesOnDisk)}${p.contentLength ? ` / ${formatBytes(p.contentLength)}` : ""}`;

    top.append(name, meta);
    item.append(top);

    if (p.contentLength) {
      const bar = document.createElement("div");
      bar.className = "bar";
      const fill = document.createElement("i");
      fill.style.width = `${Math.min(100, (p.bytesOnDisk / p.contentLength) * 100).toFixed(1)}%`;
      bar.append(fill);
      item.append(bar);
    }

    const actions = document.createElement("div");
    actions.className = "actions";

    const resume = document.createElement("button");
    resume.className = "resume";
    resume.textContent = p.resumable ? "Resume" : "Restart";
    resume.title = p.resumable
      ? `Continues from ${formatBytes(p.bytesOnDisk)}`
      : `${p.tier}: this server cannot resume, so it starts over`;
    resume.addEventListener("click", async () => {
      resume.disabled = true;
      await ask("popup_resume", { url: p.url, path: p.path });
      setTimeout(refresh, 300);
    });

    const discard = document.createElement("button");
    discard.className = "discard";
    discard.textContent = "Discard";
    discard.title = "Delete the partial file and its resume data";
    discard.addEventListener("click", async () => {
      discard.disabled = true;
      await ask("popup_discard", { path: p.path });
      setTimeout(refresh, 300);
    });

    actions.append(resume, discard);
    item.append(actions);
    el.partials.append(item);
  }
}

async function renderLog() {
  const stored = await chrome.storage.local.get(LOG_KEY);
  const entries = (stored[LOG_KEY] || []).slice(-60).reverse();

  el.log.replaceChildren();
  if (entries.length === 0) {
    const empty = document.createElement("div");
    empty.className = "empty";
    empty.textContent = "No events recorded.";
    el.log.append(empty);
    return;
  }

  for (const entry of entries) {
    const row = document.createElement("div");
    row.className = `lvl-${entry.level}`;
    const time = new Date(entry.at).toLocaleTimeString();
    row.textContent = `${time}  ${entry.text}`;
    el.log.append(row);
  }
}

async function refresh() {
  const state = await ask("popup_state");
  if (state) {
    el.conn.textContent = state.connected ? "host connected" : "host idle";
    el.conn.className = state.connected ? "up" : "";
    // Don't fight the user mid-click: only sync the checkbox when it disagrees.
    if (el.intercept.checked !== state.intercept) el.intercept.checked = state.intercept;
    renderPartials(state.partials);
    renderTransfers(state.requests);
  } else {
    el.conn.textContent = "worker unreachable";
    el.conn.className = "down";
  }
  await renderLog();
}

document.getElementById("download").addEventListener("click", async () => {
  if (!currentUrl) return;
  await ask("popup_download", { url: currentUrl });
  setTimeout(refresh, 150);
});

el.intercept.addEventListener("change", async () => {
  await ask("popup_set_intercept", { enabled: el.intercept.checked });
  await renderLog();
});

document.getElementById("clear").addEventListener("click", async () => {
  await chrome.storage.local.set({ [LOG_KEY]: [] });
  await renderLog();
});

(async () => {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  currentUrl = tab?.url || "";
  el.tabUrl.textContent = currentUrl || "(no URL in this tab)";
  await refresh();
  // Poll while the popup is open so progress is visible live.
  setInterval(refresh, 700);
})();
