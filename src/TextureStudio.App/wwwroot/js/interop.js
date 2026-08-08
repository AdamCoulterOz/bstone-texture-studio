// Browser-side helpers: PNG codecs via canvas, clipboard, downloads, paste capture.
// Core stays codec-free; everything crosses the interop boundary as raw RGBA or PNG bytes.
window.studioInterop = {
  async decodePng(bytes) {
    const blob = new Blob([bytes], { type: "image/png" });
    const bitmap = await createImageBitmap(blob);
    const canvas = new OffscreenCanvas(bitmap.width, bitmap.height);
    const ctx = canvas.getContext("2d");
    ctx.drawImage(bitmap, 0, 0);
    const data = ctx.getImageData(0, 0, bitmap.width, bitmap.height);
    bitmap.close();
    return { width: data.width, height: data.height, pixels: new Uint8Array(data.data.buffer) };
  },

  async encodePng(width, height, pixels) {
    const canvas = new OffscreenCanvas(width, height);
    const ctx = canvas.getContext("2d");
    const imageData = new ImageData(new Uint8ClampedArray(pixels), width, height);
    ctx.putImageData(imageData, 0, 0);
    const blob = await canvas.convertToBlob({ type: "image/png" });
    return new Uint8Array(await blob.arrayBuffer());
  },

  async copyPngToClipboard(bytes) {
    const blob = new Blob([bytes], { type: "image/png" });
    await navigator.clipboard.write([new ClipboardItem({ "image/png": blob })]);
  },

  downloadFile(name, bytes, mime) {
    const blob = new Blob([bytes], { type: mime || "application/octet-stream" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = name;
    a.click();
    setTimeout(() => URL.revokeObjectURL(url), 5000);
  },

  // Feature detection for the capability gate. Detection only — which of these are
  // required and what to tell the user about them is the app's call (BrowserSupport.cs).
  // Probes are wrapped because merely touching some of these throws in hardened or
  // private-mode contexts rather than reporting absent.
  probeCapabilities() {
    const ok = (probe) => { try { return probe() === true; } catch { return false; } };
    return {
      fileSystemAccess: ok(() => typeof window.showDirectoryPicker === "function"),
      fileSystemWrite: ok(() =>
        typeof FileSystemFileHandle !== "undefined" &&
        typeof FileSystemFileHandle.prototype.createWritable === "function"),
      offscreenCanvas: ok(() =>
        typeof OffscreenCanvas === "function" &&
        typeof OffscreenCanvas.prototype.convertToBlob === "function"),
      imageBitmap: ok(() => typeof createImageBitmap === "function"),
      indexedDb: ok(() => !!window.indexedDB),
      clipboardImage: ok(() =>
        typeof ClipboardItem === "function" &&
        typeof navigator.clipboard?.write === "function"),
    };
  },

  // ---- Service worker / installed-app update flow ----
  //
  // A new build produces a byte-different service-worker.js, which the browser installs
  // alongside the running one and parks in "waiting" until every tab using the old one is
  // gone. Reloading is not enough to escape that, which is why the app has to offer an
  // explicit update rather than silently picking one up.
  async registerServiceWorker(dotnetRef) {
    if (!("serviceWorker" in navigator)) {
      return false;
    }
    try {
      const registration = await navigator.serviceWorker.register("service-worker.js");
      this._swRegistration = registration;
      const announce = () => dotnetRef.invokeMethodAsync("OnUpdateAvailable");
      // A worker can already be waiting from an earlier visit or another tab.
      if (registration.waiting && navigator.serviceWorker.controller) {
        announce();
      }
      registration.addEventListener("updatefound", () => {
        const incoming = registration.installing;
        if (!incoming) return;
        incoming.addEventListener("statechange", () => {
          // Without a controller this is the first-ever install, not an update.
          if (incoming.state === "installed" && navigator.serviceWorker.controller) {
            announce();
          }
        });
      });
      const check = () => registration.update().catch(() => { /* offline is fine */ });
      document.addEventListener("visibilitychange", () => { if (!document.hidden) check(); });
      setInterval(check, 30 * 60 * 1000);
      return true;
    } catch {
      return false; // unsupported, or blocked by the browser's storage settings
    }
  },

  // Hand over to the waiting worker, then reload once it has taken control. Reloading first
  // would just re-run the old version.
  async applyUpdate() {
    const registration = this._swRegistration;
    if (!registration || !registration.waiting) {
      location.reload();
      return;
    }
    navigator.serviceWorker.addEventListener(
      "controllerchange", () => location.reload(), { once: true });
    registration.waiting.postMessage({ type: "SKIP_WAITING" });
  },

  registerPasteHandler(dotnetRef) {
    window.addEventListener("paste", async (e) => {
      for (const item of e.clipboardData?.items ?? []) {
        if (item.type.startsWith("image/")) {
          e.preventDefault();
          const buf = new Uint8Array(await item.getAsFile().arrayBuffer());
          await dotnetRef.invokeMethodAsync("OnImagePasted", buf);
          return;
        }
      }
    });
  },
};

// ---- Workspace: a user-picked real directory via the File System Access API. ----
// The directory handle persists in IndexedDB so the workspace survives reloads.
(function () {
  const DB = "texture-studio", STORE = "handles", KEY = "workspace";

  function idb() {
    return new Promise((resolve, reject) => {
      const req = indexedDB.open(DB, 1);
      req.onupgradeneeded = () => req.result.createObjectStore(STORE);
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  }

  async function idbPut(value) {
    const db = await idb();
    return new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, "readwrite");
      tx.objectStore(STORE).put(value, KEY);
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
    });
  }

  async function idbGet() {
    const db = await idb();
    return new Promise((resolve, reject) => {
      const req = db.transaction(STORE).objectStore(STORE).get(KEY);
      req.onsuccess = () => resolve(req.result ?? null);
      req.onerror = () => reject(req.error);
    });
  }

  let root = null;

  async function dir(path, create) {
    if (!root) throw new Error("no workspace");
    let d = root;
    for (const part of path.split("/").filter(p => p)) {
      d = await d.getDirectoryHandle(part, { create });
    }
    return d;
  }

  Object.assign(window.studioInterop, {
    async pickWorkspace() {
      try {
        root = await window.showDirectoryPicker({ mode: "readwrite" });
        await idbPut(root);
        return root.name;
      } catch {
        return null; // user cancelled or API unavailable
      }
    },

    async wsHasStored() {
      try { return (await idbGet()) !== null; } catch { return false; }
    },

    // interactive=false: only silent restore (already-granted permission).
    // interactive=true: may show the browser's permission prompt (needs a user gesture).
    async restoreWorkspace(interactive) {
      try {
        const handle = await idbGet();
        if (!handle) return null;
        let perm = await handle.queryPermission({ mode: "readwrite" });
        if (perm !== "granted" && interactive) {
          perm = await handle.requestPermission({ mode: "readwrite" });
        }
        if (perm !== "granted") return null;
        root = handle;
        return handle.name;
      } catch {
        return null;
      }
    },

    async wsWrite(path, bytes) {
      const parts = path.split("/");
      const name = parts.pop();
      const d = await dir(parts.join("/"), true);
      const file = await d.getFileHandle(name, { create: true });
      const writable = await file.createWritable();
      await writable.write(bytes);
      await writable.close();
    },

    async wsRead(path) {
      try {
        const parts = path.split("/");
        const name = parts.pop();
        const d = await dir(parts.join("/"), false);
        const file = await (await d.getFileHandle(name)).getFile();
        return new Uint8Array(await file.arrayBuffer());
      } catch {
        return null;
      }
    },

    async wsList(path) {
      try {
        const d = await dir(path, false);
        const names = [];
        for await (const [name, handle] of d.entries()) {
          if (handle.kind === "file") names.push(name);
        }
        return names;
      } catch {
        return [];
      }
    },
  });
})();

// ---- Content search root: a second, READ-ONLY directory the user grants so the game
// locator can find installed copies. Separate from the workspace on purpose — it is
// somewhere like Applications or a Steam library, which must never be written to.
// Remembered under its own IndexedDB key, so a return visit re-scans without asking.
(function () {
  const DB = "texture-studio", STORE = "handles", KEY = "content-root";

  function idb() {
    return new Promise((resolve, reject) => {
      const req = indexedDB.open(DB, 1);
      req.onupgradeneeded = () => req.result.createObjectStore(STORE);
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => reject(req.error);
    });
  }

  function idbSet(value) {
    return idb().then(db => new Promise((resolve, reject) => {
      const tx = db.transaction(STORE, "readwrite");
      value === null ? tx.objectStore(STORE).delete(KEY) : tx.objectStore(STORE).put(value, KEY);
      tx.oncomplete = resolve;
      tx.onerror = () => reject(tx.error);
    }));
  }

  function idbGet() {
    return idb().then(db => new Promise((resolve, reject) => {
      const req = db.transaction(STORE).objectStore(STORE).get(KEY);
      req.onsuccess = () => resolve(req.result ?? null);
      req.onerror = () => reject(req.error);
    }));
  }

  let root = null;

  async function dir(path) {
    if (!root) throw new Error("no content root");
    let d = root;
    for (const part of path.split("/").filter(p => p)) {
      d = await d.getDirectoryHandle(part);
    }
    return d;
  }

  Object.assign(window.studioInterop, {
    async pickContentRoot() {
      try {
        root = await window.showDirectoryPicker({ mode: "read", id: "game-content" });
        await idbSet(root);
        return root.name;
      } catch {
        return null; // user cancelled or API unavailable
      }
    },

    async contentHasStored() {
      try { return (await idbGet()) !== null; } catch { return false; }
    },

    // interactive=false: silent restore only (permission already granted).
    async restoreContentRoot(interactive) {
      try {
        const handle = await idbGet();
        if (!handle) return null;
        let perm = await handle.queryPermission({ mode: "read" });
        if (perm !== "granted" && interactive) {
          perm = await handle.requestPermission({ mode: "read" });
        }
        if (perm !== "granted") return null;
        root = handle;
        return handle.name;
      } catch {
        return null;
      }
    },

    // Files and sub-directories of one directory. An unreadable directory answers empty:
    // a locator walks whatever it is given, and a refused branch is a normal outcome.
    async contentList(path) {
      try {
        const d = await dir(path);
        const files = [], dirs = [];
        for await (const [name, handle] of d.entries()) {
          (handle.kind === "file" ? files : dirs).push(name);
        }
        return { files, dirs };
      } catch {
        return { files: [], dirs: [] };
      }
    },

    async contentRead(path) {
      try {
        const parts = path.split("/");
        const name = parts.pop();
        const d = await dir(parts.join("/"));
        const file = await (await d.getFileHandle(name)).getFile();
        return new Uint8Array(await file.arrayBuffer());
      } catch {
        return null;
      }
    },

    async contentForget() {
      root = null;
      try { await idbSet(null); } catch { /* best effort */ }
    },
  });
})();

// Forget the stored workspace handle (added after the pick/restore block).
window.studioInterop.wsForget = async function () {
  try {
    const req = indexedDB.open("texture-studio", 1);
    await new Promise((resolve, reject) => {
      req.onupgradeneeded = () => req.result.createObjectStore("handles");
      req.onsuccess = () => {
        const tx = req.result.transaction("handles", "readwrite");
        tx.objectStore("handles").delete("workspace");
        tx.oncomplete = resolve;
        tx.onerror = () => reject(tx.error);
      };
      req.onerror = () => reject(req.error);
    });
  } catch { /* best effort */ }
};

// Write probe: proves the workspace handle can actually create files in this browser
// context (some embedded/restored-handle contexts grant "readwrite" but refuse writes).
// Leaves a 1-byte hidden marker file; harmless and overwritten on every probe.
window.studioInterop.wsProbeWrite = async function () {
  try {
    await window.studioInterop.wsWrite(".studio-write-probe", new Uint8Array([1]));
    return true;
  } catch {
    return false;
  }
};

// Cmd/Ctrl+A → select all visible items, unless the user is typing in a field.
// Single window listener; the current .NET target is swappable (panes re-mount on collapse).
window.studioInterop.registerSelectAllHandler = function (dotnetRef) {
  window.studioInterop._selectAllRef = dotnetRef;
  if (!window.studioInterop._selectAllBound) {
    window.studioInterop._selectAllBound = true;
    window.addEventListener("keydown", (e) => {
      if (!(e.metaKey || e.ctrlKey) || (e.key !== "a" && e.key !== "A")) return;
      const t = document.activeElement;
      const editing = t && (t.tagName === "INPUT" || t.tagName === "TEXTAREA" ||
        t.tagName === "SELECT" || t.isContentEditable);
      if (editing) return;
      e.preventDefault();
      window.studioInterop._selectAllRef?.invokeMethodAsync("OnSelectAll");
    });
  }
};
window.studioInterop.unregisterSelectAllHandler = function () {
  window.studioInterop._selectAllRef = null;
};

// Rendered bounding rect of an element — for exact pointer→image coordinate mapping.
window.studioInterop.getRect = function (el) {
  const r = el.getBoundingClientRect();
  return { left: r.left, top: r.top, width: r.width, height: r.height };
};

// ---- Heavy pixel work lives here: canvas is native-speed and nothing big crosses the
// JS/.NET boundary. The WASM side stays free to render. ----

// Compose a sheet from small source tiles (RGBA bytes). Nearest-neighbor upscale per cell.
window.studioInterop.composeSheet = async function (spec) {
  const canvas = new OffscreenCanvas(spec.width, spec.height);
  const ctx = canvas.getContext("2d");
  ctx.fillStyle = spec.background;
  ctx.fillRect(0, 0, spec.width, spec.height);
  ctx.imageSmoothingEnabled = false;
  const tileCanvas = new OffscreenCanvas(1, 1);
  const tileCtx = tileCanvas.getContext("2d");
  for (const t of spec.tiles) {
    tileCanvas.width = t.srcSize;
    tileCanvas.height = t.srcSize;
    if (t.matte) {
      tileCtx.fillStyle = "#ff00ff";
      tileCtx.fillRect(0, 0, t.srcSize, t.srcSize);
    } else {
      tileCtx.clearRect(0, 0, t.srcSize, t.srcSize);
    }
    const data = new ImageData(new Uint8ClampedArray(t.rgba), t.srcSize, t.srcSize);
    const bmp = await createImageBitmap(data);
    tileCtx.drawImage(bmp, 0, 0);
    bmp.close();
    ctx.drawImage(tileCanvas, t.x, t.y, t.size, t.size);
  }
  const blob = await canvas.convertToBlob({ type: "image/png" });
  return new Uint8Array(await blob.arrayBuffer());
};

window.studioInterop.copyText = async function (text) {
  await navigator.clipboard.writeText(text);
};

// Compose a square grid from already-encoded PNGs (identity-reference sheets):
// only compressed bytes cross interop, never raw pixels.
window.studioInterop.composePngGrid = async function (pngs, tilePx) {
  const side = Math.max(1, Math.ceil(Math.sqrt(pngs.length)));
  const canvas = new OffscreenCanvas(side * tilePx, side * tilePx);
  const ctx = canvas.getContext("2d");
  ctx.imageSmoothingEnabled = true;
  for (let i = 0; i < pngs.length; i++) {
    const bmp = await createImageBitmap(new Blob([pngs[i]], { type: "image/png" }));
    const x = (i % side) * tilePx;
    const y = Math.floor(i / side) * tilePx;
    ctx.drawImage(bmp, x, y, tilePx, tilePx);
    bmp.close();
  }
  const blob = await canvas.convertToBlob({ type: "image/png" });
  return new Uint8Array(await blob.arrayBuffer());
};

// Downscaled preview as a data URL — the full-size pixels never enter .NET.
window.studioInterop.pngPreviewDataUrl = async function (bytes, maxWidth) {
  const bmp = await createImageBitmap(new Blob([bytes], { type: "image/png" }));
  const scale = Math.min(1, maxWidth / bmp.width);
  const w = Math.max(1, Math.round(bmp.width * scale));
  const h = Math.max(1, Math.round(bmp.height * scale));
  const canvas = new OffscreenCanvas(w, h);
  canvas.getContext("2d").drawImage(bmp, 0, 0, w, h);
  bmp.close();
  const blob = await canvas.convertToBlob({ type: "image/png" });
  return await new Promise((resolve) => {
    const r = new FileReader();
    r.onload = () => resolve(r.result);
    r.readAsDataURL(blob);
  });
};

// PNG dimensions straight from the IHDR header — no decode at all.
window.studioInterop.pngSize = function (bytes) {
  const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  return { width: v.getUint32(16), height: v.getUint32(20) };
};

// Draw colored revision outlines onto a PNG without round-tripping pixels through .NET.
window.studioInterop.annotatePng = async function (bytes, regions) {
  const bmp = await createImageBitmap(new Blob([bytes], { type: "image/png" }));
  const canvas = new OffscreenCanvas(bmp.width, bmp.height);
  const ctx = canvas.getContext("2d");
  ctx.drawImage(bmp, 0, 0);
  bmp.close();
  const thickness = Math.max(2, Math.round(bmp.width / 300));
  for (const r of regions) {
    ctx.strokeStyle = r.color;
    ctx.lineWidth = thickness;
    ctx.strokeRect(r.x, r.y, r.w, r.h);
  }
  const blob = await canvas.convertToBlob({ type: "image/png" });
  return new Uint8Array(await blob.arrayBuffer());
};

// Format-agnostic image dimensions: PNG header fast-path, decoder fallback for JPEG/WebP
// (Gemini doesn't always return PNG despite the request).
window.studioInterop.imageSize = async function (bytes) {
  if (bytes.length > 24 && bytes[0] === 0x89 && bytes[1] === 0x50 &&
      bytes[2] === 0x4e && bytes[3] === 0x47) {
    const v = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
    return { width: v.getUint32(16), height: v.getUint32(20) };
  }
  const bmp = await createImageBitmap(new Blob([bytes]));
  const size = { width: bmp.width, height: bmp.height };
  bmp.close();
  return size;
};

// Auto-size the group prompt textarea between 3 and 10 lines of content. A manual
// corner-drag (height differing from the last height we set) wins until the next
// forced sizing (opening a different group).
window.studioInterop.autoSizePrompt = function (el, force) {
  if (!el) return;
  const cs = getComputedStyle(el);
  const line = parseFloat(cs.lineHeight) || 18;
  const padV = parseFloat(cs.paddingTop) + parseFloat(cs.paddingBottom);
  const borderV = parseFloat(cs.borderTopWidth) + parseFloat(cs.borderBottomWidth);
  if (!force && el.dataset.autoH &&
      Math.abs(el.getBoundingClientRect().height - parseFloat(el.dataset.autoH)) > 3) {
    return;
  }
  el.style.height = "auto";
  const h = Math.min(
    Math.max(el.scrollHeight + borderV, line * 3 + padV + borderV),
    line * 10 + padV + borderV);
  el.style.height = h + "px";
  el.dataset.autoH = h;
};
