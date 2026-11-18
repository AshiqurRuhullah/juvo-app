# Step 11B front end: promotions admin (builder `promo`, module Z)

Branch `feature/11b-front-promo` on top of `feature/11b-front` (`c20234a`). Admin screens only: the storefront campaign pages, banners, card badges, cart and checkout promotion lines belong to builder B, coupon admin to Step 12. Contracts: `docs/promotions-api.md`, `docs/pricing-engine.md`, `docs/step11b-backend-promotions.md`.

## What was built

| Route | Screen |
| --- | --- |
| `/admin/promotions` | Two tabs. **Promotions**: summary strip (promotions, orders, units, revenue after discounts, discount cost), search, type and status filters, ten rows per page (cards on phones), status chips (Draft, Scheduled, Active, Ended, Archived) with icon and word. **Campaign report**: one row per promotion, type and Dhaka-day period filters, returns limitation shown. |
| `/admin/promotions/new` | The editor for a new promotion (all six types). Needs `promotions.promotion.create`. |
| `/admin/promotions/[id]` | The same editor on a saved promotion: status line, actions, price preview, per-promotion results. Needs `promotions.promotion.view`. |

Editor sections: basics (type is locked after creation, English and Bengali name and customer label), offer (fields per type: discount kind/value/cap, combo price, buy/get quantities and percent, free-delivery cart value and zones picked from the delivery zones list), targets (one box per role; products by search, variants through the inventory `VariantPicker`, categories, brands, "everything"), schedule (date-time typed as Dhaka time, usage limit, minimum cart value, priority -1000..1000, channels), stacking (stack mode radios plus a plain-language explanation from `docs/pricing-engine.md` section 3), campaign (slug, titles, texts, banner place and category, hero and banner pictures). Preview panel: pick sample items, price them with exactly this promotion (`POST /promotions/preview`, `promotionIds = [id]`, drafts included) and see unit prices, struck list prices, totals, applied promotions, free-delivery offers and the API's explanation notes in the current language.

Actions follow `allowedActions(status, permissions)`: Draft: save, activate, archive, duplicate; Scheduled/Active/Ended: save, switch off, archive, duplicate; Archived: duplicate only (the form is read-only). Save and activate need `edit`, archive needs `delete`, duplicate needs `create`. Activate asks first and is disabled while there are unsaved changes (the API activates the saved version).

Code: `src/modules/promotions/{logic,api,common,promotions-list,report-view,promotion-editor,promotion-fields,target-picker,preview-panel}`, pages under `src/app/(admin)/admin/promotions/`, messages `src/messages/promotions.{en,bn}.json`, tests `tests/promotions.test.ts`, specs `e2e/promotions.spec.ts`, `e2e/promotions-ui-checks.spec.ts`, helpers `e2e/support/promotions.ts`.

## Shared files touched (small additive edits)

- `src/lib/nav.ts`: the `promotions` item set to `ready: true` (one word).
- `src/messages/load.ts`: `"promotions"` appended to `MESSAGE_FRAGMENTS`.
- `src/messages/en.json`, `bn.json`: eight `PROMO_*` keys added in `errors`, right after `TICKET_BULK_LIMIT` (the likeliest merge conflict spot: keep all keys of every builder). `PREORDER_AWAITING_STOCK` is not added here (pre-order is not in this scope).
- `playwright.config.ts`: `promotions|promotions-ui-checks` added to the desktop `testMatch` regex.
- `.gitignore`: pattern for `docs/ui-checks/promotions/*.png` with five kept screenshots.
- `src/messages/scopes.ts` needs no change (the admin area loads every namespace).

## Decisions (reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| F1 | Times are typed in `datetime-local` fields and sent as `YYYY-MM-DDTHH:mm:00+06:00`; answers (any offset) are shown as the Dhaka wall clock. | The business time is Asia/Dhaka (P8) whatever the device clock says; the form says so. | `dhakaLocalToIso`, `isoToDhakaLocal` in `logic.ts`. |
| F2 | Report periods are Dhaka days: `from` = 00:00:00+06:00, `to` = 23:59:59+06:00. | Same reason. | `dhakaDayBound`. |
| F3 | The summary strip on the list adds up the report of every promotion (all pages, 100 per page, at most 20 pages), and says an order with two promotions counts under each. | The API has no total row; no invented numbers. | `useCampaignTotals`, `summarizeReport`. |
| F4 | Activate is disabled while the form has unsaved changes. | The API switches on the saved record, not the form. | `dirty` in `promotion-editor.tsx`. |
| F5 | The price preview takes sample items picked by the person (inventory `VariantPicker`, so only active stock-tracking variants are offered). | The preview needs a cart and none may be made up. | `preview-panel.tsx`. |
| F6 | Free delivery zones are picked from `GET /delivery/zones`; without the zone permission the field falls back to typed codes (the API checks them). | Same approach as the courier form. | `ZonePicker` in `promotion-fields.tsx`. |
| F7 | A festival campaign may be saved without products (the API allows a draft) but needs slug and English title; the page says products are needed before it can be switched on. | Mirrors `PromotionsService` validation (title required at save, targets at activation). | `TYPE_RULES.FestivalCampaign.targetsRequired`. |
| F8 | API validation field errors are shown as our own translated one-line text per field (`editor.serverField.*`), never the API's English sentence; error codes use `errors.PROMO_*`. | English and Bengali for every text. | `SERVER_FIELDS`, `showProblem`. |
| F9 | Pictures are checked in the browser for type, 10 MB and a 1000 px long side before upload; the API checks again. | Quick feedback; the 400 answer shows a generic rules text. | `ImageUploads`. |
| F10 | Stack mode radios are disabled for free delivery (the engine ignores them there) and the explanation says so. | `docs/pricing-engine.md` section 3, rule 6. | `stackingText`. |

## Contract gaps (the backend was not changed)

1. **No channel filter on the list**: `GET /promotions` filters by type, status, `liveNow` and search only, and the list item carries no channels. The list says so; channels are edited on each promotion. (`liveNow` is not offered as a control; the Active status filter covers it.)
2. **No sort on the list** (always newest first), so the columns are not sortable.
3. **Validation messages from the API are English only**; the screen shows its own translated per-field text instead of the API wording.
4. **The report cannot be filtered by status or searched by name** and there is no totals row; the strip is summed on the client (F3).
5. **Returns do not reduce the report** (documented backend limitation), shown in the UI next to every report.
6. **The preview needs variants**: it accepts `variantId` or `sku` lines only, so category- or brand-based promotions are previewed through the items the person picks.

## Partial and not built

- Storefront campaign pages, banners, badges, cart and checkout lines (builder B), coupon admin (Step 12), `PREORDER.*` error texts.
- Customer segments or per-customer promotions (the backend has none).
- Bengali texts are a first pass and need native review (Step 12).

## Tests

Gate on the final tree (Windows, Node 26): `npx tsc --noEmit` clean; `npx eslint src e2e tests` clean; `npm test` 20 files, 301 tests passed (52 new in `tests/promotions.test.ts`: Dhaka time, validation per type, form to request, detail to form, status to actions, stacking text, report totals); `npm run build` passed (it sometimes stops at the very end with a Next.js "SecurityFatalError" advisory-lookup message and exit 1, and passes when run again; seen on this machine). Browser specs on desktop with API 5111, web 3111, database `JuvoDb_E2E_promo` (dropped afterwards): `promotions.spec.ts` 11 flows and `promotions-ui-checks.spec.ts` 21 checks, 33 of 33 passed including setup and cleanup. UI checks cover list, report, new, flash sale, bundle, campaign, picker and preview at 360, 768 and 1280 px (no sideways scroll, no tap target under 44 px, axe WCAG 2.1 AA clean, no console errors), the dark theme and Bengali at 360 px. Screenshots in `juvo-front/docs/ui-checks/promotions/` (five kept in git). The Pixel 7 and iPhone 14 projects were not run: the config maps only `shell` and `devices` specs to them, and the UI-check spec sets the viewport itself (360, 768, 1280).

Not covered by a spec: the "saved" toast texts and the unsaved-changes guard on Activate beyond the flows; two promotions stacking in a real order (the engine belongs to the backend and has its own tests).

Found while testing: the shared inventory `VariantPicker` shows "Added" on a disabled button whose accessible name is "Add <sku>" (axe `label-content-name-mismatch`). The promotions screens avoid it by not passing the chosen set (duplicates are ignored); the shared component was left unchanged.

## Traceability suggestions

| ID | Status | Notes |
| --- | --- | --- |
| FR-Z01 | Done (admin) | Flash sale editor with required end time, discount kinds, targets, schedule in Dhaka time, usage limit, preview. Countdown on the shop is builder B. |
| FR-Z02 | Done (admin) | Bundle, buy X get Y and free delivery editors with targets, quantities, zones. |
| FR-Z03 | Done (admin) | Festival campaign editor: slug, titles, texts, banner place, hero and banner pictures. Public page and banners are builder B. |
| FR-Z04 | Done (admin) | Stack mode per promotion with the plain-language policy; preview shows the API explanation. |
| FR-Z05 | Done (admin) | Campaign report tab and per-promotion results with the returns limitation. |
