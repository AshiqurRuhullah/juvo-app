/*
 * Juvo BD service worker (Step 12l). Plain JavaScript on purpose: public/ files are not compiled, and the rules below are loaded
 * as they are by tests/pwa-sw.test.ts (CommonJS export at the bottom, only when `module` exists).
 *
 * What it does:
 *   - navigations to shop pages: network first; when the network fails, the cached /offline page;
 *   - /_next/static and /brand: stale-while-revalidate;
 *   - /media product pictures: cache first, capped by entries and age;
 *   - everything else is NOT touched (the worker does not call respondWith, the browser handles it normally).
 *
 * What it never does: store or answer anything under the NEVER_PREFIXES, any non-GET request, any request that carries an
 * Authorization header, or any response with Set-Cookie, Authorization, or Cache-Control no-store/private. Personal data (carts,
 * orders, sessions, admin screens) therefore never lands in a cache. See docs/pwa-notes.md.
 */

var VERSION = "v1";
var KINDS = ["static", "media", "offline"];
var CACHE_PREFIX = "juvo-";
var OFFLINE_URL = "/offline";
var MEDIA_MAX_ENTRIES = 60;
var STATIC_MAX_ENTRIES = 120;
var MEDIA_MAX_AGE_MS = 7 * 24 * 60 * 60 * 1000;
var CACHED_AT_HEADER = "x-sw-cached-at";

/** Paths (and everything below them) that are personal, dynamic or part of a transaction. Matched on whole segments. */
var NEVER_PREFIXES = [
  "/api",
  "/admin",
  "/account",
  "/checkout",
  "/cart",
  "/login",
  "/first-login",
  "/forgot-password",
  "/reset-password",
  "/pay",
  "/order",
  "/track",
  "/sw.js",
];

function cacheName(kind, version) {
  return CACHE_PREFIX + kind + "-" + (version || VERSION);
}

function currentCacheNames(version) {
  return KINDS.map(function (k) {
    return cacheName(k, version);
  });
}

/** True when the cache belongs to this app but not to the current version (deleted on activate). */
function isStaleCache(name, version) {
  return name.indexOf(CACHE_PREFIX) === 0 && currentCacheNames(version).indexOf(name) === -1;
}

function underPrefix(pathname, prefix) {
  return pathname === prefix || pathname.indexOf(prefix + "/") === 0;
}

function isNeverPath(pathname) {
  for (var i = 0; i < NEVER_PREFIXES.length; i++) if (underPrefix(pathname, NEVER_PREFIXES[i])) return true;
  return false;
}

/**
 * What to do with a request:
 *   "navigate" shop page navigation (network first, offline fallback), "static", "media", or "ignore" (not handled at all).
 */
function classify(request, origin) {
  if (!request || request.method !== "GET") return "ignore";
  var url;
  try {
    url = new URL(request.url);
  } catch (e) {
    return "ignore";
  }
  if (url.origin !== origin) return "ignore";
  if (isNeverPath(url.pathname)) return "ignore";
  if (request.headers && typeof request.headers.get === "function" && request.headers.get("authorization")) return "ignore";
  if (request.mode === "navigate") return url.pathname === OFFLINE_URL ? "ignore" : "navigate";
  if (underPrefix(url.pathname, "/_next/static") || underPrefix(url.pathname, "/brand")) return "static";
  if (underPrefix(url.pathname, "/media")) return "media";
  return "ignore";
}

function headerOf(response, name) {
  return response && response.headers && typeof response.headers.get === "function" ? response.headers.get(name) : null;
}

/** The never-cache rules for a response to a request. Any doubt means no. */
function shouldCache(request, response, origin) {
  if (!request || !response) return false;
  if (classify(request, origin || new URL(request.url).origin) === "ignore" && request.mode !== "navigate") return false;
  var url = new URL(request.url);
  if (request.method !== "GET" || isNeverPath(url.pathname)) return false;
  if (request.headers && request.headers.get && request.headers.get("authorization")) return false;
  if (response.status !== 200 || response.type === "opaque" || response.type === "error") return false;
  if (headerOf(response, "set-cookie") || headerOf(response, "authorization") || headerOf(response, "www-authenticate")) return false;
  var cc = (headerOf(response, "cache-control") || "").toLowerCase();
  if (/(^|[\s,])(no-store|private)([\s,=]|$)/.test(cc)) return false;
  if (headerOf(response, "vary") === "*") return false;
  return true;
}

function isExpired(response, maxAgeMs, now) {
  var at = Number(headerOf(response, CACHED_AT_HEADER));
  if (!at) return true;
  return now - at > maxAgeMs;
}

/** Same-origin scripts, styles and brand files an HTML page refers to (stored next to the offline page). */
function offlineAssetUrls(html) {
  var found = {};
  var re = /\/(?:_next\/static|brand)\/[A-Za-z0-9_\-./%~]+\.(?:js|css|png|webp|woff2)/g;
  var m;
  while ((m = re.exec(html))) found[m[0]] = true;
  return Object.keys(found);
}

/* ------------------------------------------------------------------ worker wiring (not run in tests) */

function withStamp(response) {
  var headers = new Headers(response.headers);
  headers.set(CACHED_AT_HEADER, String(Date.now()));
  return response.blob().then(function (body) {
    return new Response(body, { status: response.status, statusText: response.statusText, headers: headers });
  });
}

function trim(cache, max) {
  return cache.keys().then(function (keys) {
    var extra = keys.length - max;
    var work = [];
    for (var i = 0; i < extra; i++) work.push(cache.delete(keys[i]));
    return Promise.all(work);
  });
}

function store(cacheKind, request, response, max) {
  return caches.open(cacheName(cacheKind)).then(function (cache) {
    return withStamp(response).then(function (stamped) {
      return cache.put(request, stamped).then(function () {
        return trim(cache, max);
      });
    });
  });
}

function staleWhileRevalidate(event) {
  var request = event.request;
  return caches.open(cacheName("static")).then(function (cache) {
    return cache.match(request).then(function (cached) {
      var network = fetch(request)
        .then(function (response) {
          if (shouldCache(request, response, self.location.origin)) {
            event.waitUntil(store("static", request, response.clone(), STATIC_MAX_ENTRIES));
          }
          return response;
        })
        .catch(function () {
          return cached || Response.error();
        });
      return cached || network;
    });
  });
}

function cacheFirstMedia(event) {
  var request = event.request;
  return caches.open(cacheName("media")).then(function (cache) {
    return cache.match(request).then(function (cached) {
      if (cached && !isExpired(cached, MEDIA_MAX_AGE_MS, Date.now())) return cached;
      return fetch(request)
        .then(function (response) {
          if (shouldCache(request, response, self.location.origin)) {
            event.waitUntil(store("media", request, response.clone(), MEDIA_MAX_ENTRIES));
          }
          return response;
        })
        .catch(function () {
          return cached || Response.error();
        });
    });
  });
}

function networkFirstNavigation(event) {
  return fetch(event.request).catch(function () {
    return caches.open(cacheName("offline")).then(function (cache) {
      return cache.match(OFFLINE_URL).then(function (page) {
        return page || Response.error();
      });
    });
  });
}

if (typeof self !== "undefined" && typeof self.addEventListener === "function" && typeof ServiceWorkerGlobalScope !== "undefined") {
  self.addEventListener("install", function (event) {
    // The offline page is stored explicitly (Next sends it with no-store, which the general rule would refuse). It is public and
    // fetched without cookies. Its own scripts, styles and logo are stored too so the page works without a connection. No
    // skipWaiting here: an open checkout must not be swapped under the customer.
    event.waitUntil(
      Promise.all([caches.open(cacheName("offline")), caches.open(cacheName("static"))]).then(function (opened) {
        var offline = opened[0];
        var assets = opened[1];
        return fetch(OFFLINE_URL, { credentials: "omit", cache: "reload" }).then(function (response) {
          if (response.status !== 200 || headerOf(response, "set-cookie")) return;
          var copy = response.clone();
          return offline.put(OFFLINE_URL, response).then(function () {
            return copy.text().then(function (html) {
              var urls = offlineAssetUrls(html);
              return Promise.all(
                urls.map(function (u) {
                  var req = new Request(u, { credentials: "omit" });
                  return fetch(req)
                    .then(function (r) {
                      if (shouldCache(req, r, self.location.origin)) return assets.put(req, r);
                    })
                    .catch(function () {});
                }),
              );
            });
          });
        });
      }),
    );
  });

  self.addEventListener("activate", function (event) {
    event.waitUntil(
      caches
        .keys()
        .then(function (names) {
          return Promise.all(
            names.filter(function (n) {
              return isStaleCache(n);
            }).map(function (n) {
              return caches.delete(n);
            }),
          );
        })
        .then(function () {
          return self.clients.claim();
        }),
    );
  });

  self.addEventListener("message", function (event) {
    if (event.data && event.data.type === "SKIP_WAITING") self.skipWaiting();
  });

  self.addEventListener("fetch", function (event) {
    var kind = classify(event.request, self.location.origin);
    if (kind === "navigate") event.respondWith(networkFirstNavigation(event));
    else if (kind === "static") event.respondWith(staleWhileRevalidate(event));
    else if (kind === "media") event.respondWith(cacheFirstMedia(event));
  });
}

if (typeof module !== "undefined" && module.exports) {
  module.exports = {
    VERSION: VERSION,
    NEVER_PREFIXES: NEVER_PREFIXES,
    MEDIA_MAX_ENTRIES: MEDIA_MAX_ENTRIES,
    MEDIA_MAX_AGE_MS: MEDIA_MAX_AGE_MS,
    cacheName: cacheName,
    currentCacheNames: currentCacheNames,
    isStaleCache: isStaleCache,
    isNeverPath: isNeverPath,
    classify: classify,
    shouldCache: shouldCache,
    isExpired: isExpired,
    offlineAssetUrls: offlineAssetUrls,
  };
}
