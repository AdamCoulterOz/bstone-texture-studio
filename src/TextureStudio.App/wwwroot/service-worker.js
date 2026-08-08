// Development worker: never caches, but does mirror production's update lifecycle.
//
// Caching during development is actively harmful — net11 fingerprints framework assets and a
// cached worker would keep serving the previous build after every rebuild. So every fetch
// goes straight to the network.
//
// It deliberately does NOT skipWaiting on install. Waiting is the whole mechanism the update
// prompt is built on, and a dev worker that activated immediately would make that flow
// impossible to exercise before it reached users. Publish swaps in
// service-worker.published.js under this same name.
self.addEventListener("install", () => { /* wait, exactly as the published worker does */ });
self.addEventListener("activate", () => self.clients.claim());
self.addEventListener("fetch", () => { /* straight to the network */ });

// The app asks a waiting worker to take over when the user accepts an update.
self.addEventListener("message", event => {
  if (event.data && event.data.type === "SKIP_WAITING") {
    self.skipWaiting();
  }
});


