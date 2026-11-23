# Installable web app (PWA), Step 12l

Covers FR-AF07 (installable web app; push notifications are NOT part of this step) and part of NFR-003 (repeat visits load static files and pictures from the device).

## What is built

| Piece | File |
| --- | --- |
| Manifest (`/manifest.webmanifest`) | `src/app/manifest.ts`, colours in `src/lib/pwa/colors.ts` |
| Icons 192, 512, maskable 512 | already in `public/brand/` (made by `scripts/generate_brand_assets.py`); no new image work or package |
| Service worker, scope `/` | `public/sw.js` (plain JavaScript, no package) |
| Registration, update toast, install prompt, iOS hint | `src/modules/pwa/pwa-client.tsx`, mounted only by `src/app/(storefront)/layout.tsx` |
| Pure rules (prompt timing, quiet paths, iOS detection, storage) | `src/lib/pwa/rules.ts` |
| Offline page `/offline` (public, English and Bengali together) | `src/app/offline/page.tsx`, `src/modules/pwa/retry-button.tsx` |
| Texts | `src/messages/pwa.en.json`, `pwa.bn.json` (namespace `pwa`, added to the storefront scope) |
| Headers for `/sw.js` and the manifest | `next.config.ts` |

Manifest: `id /`, `name Juvo BD`, `short_name Juvo`, `start_url /`, `scope /`, `display standalone`, `lang en`, `theme_color #DB0100` (brand-primary), `background_color #EBE5DB` (brand-accent), icons any 192 and 512 plus maskable 512, shortcuts Shop, Track order, Cart.

Service worker strategies:

- Navigation to a shop page: network first; if the network fails, the stored `/offline` page.
- `/_next/static` and `/brand`: stale-while-revalidate (max 120 entries).
- `/media` product pictures: cache first, max 60 entries, max age 7 days (the age is stamped on the stored copy in an `x-sw-cached-at` header; oldest entries are removed first).
- Anything else is not touched: the worker does not answer it, the browser handles it as without a worker.
- At install the worker stores `/offline` (fetched without cookies) and the scripts, styles and logo that page refers to, so the retry button works offline.
- Cache names are `juvo-<static|media|offline>-<VERSION>`; on activate every `juvo-*` cache of another version is deleted. Change `VERSION` in `public/sw.js` whenever the cache rules change.
- No `skipWaiting` at install. A new worker waits; the page shows a toast "A new version is ready" with a Reload button; only that click sends `SKIP_WAITING` and reloads. An open checkout is never swapped under the customer.

Install experience (shop only, never on checkout, cart, payment, order, tracking, sign-in, account, admin, offline):

- Chrome and Android: after the second visit (one visit = one browser session, counted in localStorage) or after a meaningful action (adding to the cart, `markMeaningfulAction()` in `useAddToCart`), a small card with Install and Not now. It uses the real `beforeinstallprompt` event, so it only appears when the browser says the site is installable. Dismissal hides it for 30 days. An accepted install or running standalone hides it for good.
- iPhone/iPad Safari (other iOS browsers excluded): the same eligibility rule, a hint "Tap Share, then Add to Home Screen", shown once (a flag in localStorage), not shown when already standalone.
- All storage access is wrapped in try/catch; without storage the prompt simply has no memory.
- 44 px targets, tokens only, `section` with an accessible name, not modal and not focus-trapping.

## The never-cache list and why

The worker never stores or answers, whatever the response says: anything under `/api` (so `/api/bff`), `/admin`, `/account`, `/checkout`, `/cart`, `/login`, `/first-login`, `/forgot-password`, `/reset-password`, `/pay`, `/order`, `/track` and `/sw.js` (whole path segments, so `/orders-guide` is not `/order`); every non-GET request; every request with an `Authorization` header; other origins. Responses are also refused when the status is not 200, when opaque or partial, when they carry `Set-Cookie`, `Authorization` or `WWW-Authenticate`, `Vary: *`, or `Cache-Control` with `no-store` or `private`.

Why: carts, orders, tracking, payments, sessions and every admin screen hold personal or financial data and change by the second; a stale copy would show wrong stock, wrong totals or another person's data on a shared phone. The app also cannot place orders offline by design (checkout needs the server for stock reservation and payment verification).

Consequence worth knowing: Next sends every dynamic page, including all shop pages (the layout reads the session and cookies), with `Cache-Control: private, no-store`, so the rule above means shop HTML is never stored. "Network first" therefore equals "network, then the offline page". Only static files, brand files, pictures and the offline page are on the device.

The rules are written twice on purpose: `public/sw.js` cannot import TypeScript. They live in `sw.js` as plain functions and the file exports them (CommonJS, only when `module` exists) so vitest loads the real file; the page-side rules (`src/lib/pwa/rules.ts`) are separate. The simpler option was chosen over generating `sw.js` at build time.

## Decisions (how to change)

| Decision | Reason | Change |
| --- | --- | --- |
| Hand-written worker, no Workbox/serwist | No new dependency or licence check; about 250 lines | none needed |
| Worker registered only by the storefront layout and only when `NODE_ENV=production` | Admin and account never register one; the dev server must not be cached. The worker's scope is `/`, so once registered it also sees admin requests, but it ignores them | move `<PwaClient />` |
| `/offline` is outside the storefront layout | The stored copy must not contain the visitor's session, cart or menu | edit `src/app/offline/page.tsx` |
| Offline page shows both languages | A stored page cannot follow the language cookie | n/a |
| Manifest in English only | A manifest holds one language; the default is used (the pages follow the cookie) | `manifest.ts` could read the locale cookie later |
| Manifest colours: red and cream tokens | Spec; the page `themeColor` meta in `layout.tsx` still follows the light/dark canvas | `src/lib/pwa/colors.ts` (a unit test compares with `tokens.css`) |
| Prompt after 2nd visit or a cart action, 30-day silence | Respectful rule from the brief | `MIN_VISITS`, `DISMISS_DAYS` in `rules.ts` |
| `/offline` not added to robots disallow | The page carries `noindex`; robots stays unchanged | `PRIVATE_PATHS` in `src/lib/seo.ts` |
| `sw.js` header `no-store`, manifest `max-age=3600` | A new worker must reach visitors at once | `next.config.ts` |

No `proxy.ts` change was needed: its matcher covers only `/admin`, `/account` and `/first-login`, so `/sw.js`, `/manifest.webmanifest` and `/offline` are untouched. The app sets no CSP header, so nothing to allow. robots.txt allows `/`.

## Gaps and not built

- Push notifications (VAPID keys, subscription storage, sending, permission prompt, `push` and `notificationclick` handlers) need a backend step; none of it exists. FR-AF07 is therefore Partial.
- Offline ordering, offline browsing of products and background sync are not supported by design.
- Manifest and shortcut titles are English only; no screenshots/`screenshots` manifest field (richer install UI) and no `share_target`.
- iOS has no `beforeinstallprompt`; the hint is text only and cannot be verified here (no iPhone Safari).
- Pictures cache (`/media`) is covered by unit tests of the rules only; the browser spec does not load a product picture through the worker.
- The browser spec ran on the desktop project only (no phone project).

## Installability criteria

Checked (in a real Chromium production build, by the spec and by reading the served files): manifest served as `application/manifest+json`, with name, short_name, start_url, scope, display standalone, theme and background colour, 192 and 512 PNG icons (right pixel sizes, HTTP 200) and a maskable icon; service worker registered with a `fetch` handler and controlling the page after reload; served from `http://localhost` (a secure context for service workers; the production site needs HTTPS).

Not checked: the Lighthouse PWA audit and Chrome's own "installable" verdict (Lighthouse is not available offline here; the real `beforeinstallprompt` event was simulated with a synthetic event in the test), installation on a real Android or iOS device, HTTPS on the real host, the maskable icon safe-zone by eye.

## Tests

- `tests/pwa-sw.test.ts`: loads the real `public/sw.js` with no `self`; classification, never-cache decision (`shouldCache`) for every listed path and header rule, whole-segment matching, cache naming and old-cache detection, media age, offline asset discovery.
- `tests/pwa.test.ts`: prompt timing (2nd visit, action, 30 days, installed), stored state parsing, quiet paths, iPhone Safari detection, manifest fields, colours equal to `tokens.css`, icon files exist.
- `e2e/pwa.spec.ts` (desktop project, production build): manifest JSON and icon sizes, `sw.js` headers and type, offline page (both languages, noindex), worker registers and controls after reload and stores offline page and static files, offline navigation to an uncached shop page shows the offline page, offline cart/checkout/track/login/account/admin are not served, no personal URL in any cache after visiting private pages and calling `/api/bff`, install prompt hidden on the first visit and in checkout, shown on the second, hidden after dismissal and on a later visit, admin has no registration or prompt.

Traceability suggestions: FR-AF07 Partial (install, icon, offline page done; push not built), NFR-003 Partial (repeat-visit caching of static files and pictures; speed targets still to be measured in Step 13).
