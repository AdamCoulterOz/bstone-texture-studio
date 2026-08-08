// Production worker: makes the studio installable and usable offline.
//
// The app is ~35 MB of runtime and assets, so it is cached wholesale on install and served
// cache-first afterwards. self.assetsManifest comes from service-worker-assets.js, which the
// build regenerates with a fresh hash every publish — that hash changing is what makes the
// browser treat this file as a new worker, which is how an update is noticed at all.
self.importScripts("./service-worker-assets.js");

self.addEventListener("install", event => event.waitUntil(onInstall()));
self.addEventListener("activate", event => event.waitUntil(onActivate()));
self.addEventListener("fetch", event => event.respondWith(onFetch(event)));

const cacheNamePrefix = "offline-cache-";
const cacheName = `${cacheNamePrefix}${self.assetsManifest.version}`;

// Everything the app needs to start. Source maps and the icons' design script are not it.
const includeExtensions = [
  ".html", ".js", ".mjs", ".json", ".css", ".woff", ".woff2",
  ".png", ".jpg", ".jpeg", ".svg", ".ico", ".webmanifest", ".wasm", ".dat", ".blat",
];
const excludePatterns = [/^service-worker\.js$/, /\.map$/];

async function onInstall() {
  // Do NOT skipWaiting here: a new worker must wait until the user accepts the update,
  // otherwise the running app would start fetching assets from a half-swapped cache.
  const assets = self.assetsManifest.assets
    .filter(asset => includeExtensions.some(ext => asset.url.endsWith(ext)))
    .filter(asset => !excludePatterns.some(pattern => pattern.test(asset.url)))
    .map(asset => new Request(asset.url, { integrity: asset.hash, cache: "no-cache" }));
  await caches.open(cacheName).then(cache => cache.addAll(assets));
}

async function onActivate() {
  // Drop every previous version's cache — they are whole copies of the app, not deltas.
  const keys = await caches.keys();
  await Promise.all(keys
    .filter(key => key.startsWith(cacheNamePrefix) && key !== cacheName)
    .map(key => caches.delete(key)));
  await self.clients.claim();
}

async function onFetch(event) {
  if (event.request.method !== "GET") {
    return fetch(event.request);
  }
  // Navigations always resolve to index.html: the app owns its own routing, and a deep link
  // must not 404 when offline.
  const isNavigation = event.request.mode === "navigate";
  const key = isNavigation ? "index.html" : event.request.url;
  const cached = await caches.match(key, { ignoreSearch: isNavigation });
  return cached || fetch(event.request);
}

// The app asks a waiting worker to take over when the user accepts an update.
self.addEventListener("message", event => {
  if (event.data && event.data.type === "SKIP_WAITING") {
    self.skipWaiting();
  }
});
