// Resolves the download URL under the cursor on right-click and reports it to the background
// worker, which stashes it for the next context-menu click.
//
// This script NEVER starts a download. A previous version scanned every page on load and on
// every DOM mutation, then messaged the background worker with whatever link it guessed at --
// which started real downloads with no user action at all.

const fileExtPattern = /\.(zip|exe|msi|dmg|rar|7z|gz|tar|xz|apk|bin|iso|pdf|mp4|mov|mkv|csv|json|xml)(\?.*)?$/i;

function absolutize(url) {
  if (!url) return "";
  try {
    return new URL(url, location.href).href;
  } catch {
    return "";
  }
}

function buildCandidateUrl(el) {
  if (!el || !(el instanceof Element)) return "";

  const raw = [
    el.href,
    el.getAttribute?.("data-url"),
    el.getAttribute?.("data-href"),
    el.getAttribute?.("data-file"),
    el.getAttribute?.("data-link"),
    el.getAttribute?.("data-src"),
    el.dataset?.url,
    el.dataset?.href,
    el.dataset?.file
  ].filter(Boolean);

  for (const candidate of raw) {
    const abs = absolutize(candidate);
    if (/^https?:\/\//i.test(abs)) return abs;
  }

  return absolutize(el.closest?.("a")?.href || "");
}

function findBestCandidate(target) {
  // Buttons are included because some sites hang the real URL off a <button>'s data attributes.
  // A node that resolves to no http(s) URL still yields null -- sites that download via a POST
  // form (gamepressure, for one) genuinely have no URL to scrape, and guessing one is worse
  // than reporting nothing.
  const node = target?.closest?.(
    "a[href], button, [data-url], [data-href], [data-file], [data-link], [data-src], [data-download], [download]"
  );
  if (!node) return null;

  const url = buildCandidateUrl(node);
  if (!/^https?:\/\//i.test(url)) return null;

  // Prefer links that actually look like a file, but still report a plain link so the user can
  // force a download through the context menu.
  const looksLikeFile = fileExtPattern.test(url) || /\/download|\/files?\/|\/dl\//i.test(url) || node.hasAttribute("download");

  return {
    url,
    title: (node.getAttribute("title") || node.textContent || document.title || "download").replace(/\s+/g, " ").trim().slice(0, 200),
    looksLikeFile
  };
}

// Reloading or updating the extension orphans the content scripts already injected into open
// tabs: they keep running, but their chrome.runtime is dead. Every sendMessage from an orphan
// then throws "Extension context invalidated" into that page's console. Detect it, stop
// listening, and stay quiet.
function isContextAlive() {
  try {
    return Boolean(chrome.runtime?.id);
  } catch {
    return false;
  }
}

function onContextMenu(event) {
  if (!isContextAlive()) {
    document.removeEventListener("contextmenu", onContextMenu, true);
    return;
  }

  const candidate = findBestCandidate(event.target);

  // Always report, even a miss (url: null). Staying silent would leave the previous
  // right-click's URL in place, so clicking the menu item on a non-link would act on it.
  try {
    chrome.runtime.sendMessage(
      {
        type: "context_candidate",
        url: candidate?.url || null,
        title: candidate?.title || "",
        looksLikeFile: candidate?.looksLikeFile || false,
        pageUrl: location.href
      },
      () => {
        // Touching lastError marks it handled. Without this callback Chrome logs an
        // "Unchecked runtime.lastError" whenever the service worker is not there to reply.
        void chrome.runtime.lastError;
      }
    );
  } catch {
    document.removeEventListener("contextmenu", onContextMenu, true);
  }
}

document.addEventListener("contextmenu", onContextMenu, true);
