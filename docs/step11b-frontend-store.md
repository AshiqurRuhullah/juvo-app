# Step 11B front end, builder "store": promotions and pre-orders for customers, pre-order admin

Branch `feature/11b-front-store` (worktree `.claude/worktrees/b-store`), base `feature/11b-front` (`c20234a`). Screens only; the backend is untouched. Contracts: `docs/promotions-api.md` (shop part), `docs/pricing-engine.md`, `docs/step11b-backend-promotions.md` (P1..P17), `docs/orders-api.md`.

## What was built

### Customer side (FR-Z01..Z03, FR-AA02)

| Piece | Where | Notes |
| --- | --- | --- |
| Card: price now, struck list price, "You save", promotion label chip, countdown | `modules/store/store-product-card.tsx` (+ `promotions-store/promo-parts.tsx`) | Only what the API returns (`promotion`, `price`, `regularPrice`). |
| Product page: same, per chosen variant (variant `price`, `listPrice`, `promotion`), large countdown | `modules/store/product-view.tsx` | A pre-order variant counts as orderable (button "Pre-order now"). |
| Countdown | `promotions-store/countdown.tsx`, `logic.ts` (`countdownParts`, `tickStepMs`, `countdownSegments`) | `role="timer"`, numbers hidden from screen readers and a plain "ends on ..." sentence read instead; ticks once a minute above one hour and once a second inside the last hour, aimed at the moment a number flips, stops after the end; reserved width (no layout shift); at zero prints "Offer ended" once and asks the API again (queries `["store"]`, `["orders"]` and `router.refresh()`, at most once every 3 s). |
| Festival campaign page `/campaign/[slug]` | `app/(storefront)/campaign/[slug]/page.tsx` | Hero (hero picture, else banner picture), title, body paragraphs (plain text), countdown, product grid = the shop's `ProductGrid`. Unknown or not running campaign: the shop's 404. |
| Banners (`GET /store/banners`) | `promotions-store/promo-banners.tsx`; slots HomeTop (home), ShopTop (shop), Category (category page with `category=<slug>`) | Nothing is drawn when the API returns none. Banner links to `/campaign/<slug>` only when it has a slug. |
| Cart and checkout summary | `modules/cart/cart-view.tsx`, `modules/orders/checkout-view.tsx` (summary aside only), `promotions-store/price-explainer.tsx` | List price row, one row per offer (from `explanation.reasons`, fallback one "Offers" row), coupon row unchanged, "Why this price" sentence + the answer's notes, free-delivery hint (won / how much more, zone-limited offers say so), delivery waiver with the promotion label. Line level: struck list price + label. |
| Coupon outcome | `modules/cart/coupon-box.tsx`, `price-explainer.tsx` (`CouponLostNotice`) | `applied = false` (decision P4): the coupon stays on the cart as "kept, not used" with the API's reason in the page language. |
| Order desk quote | `modules/orders/booking/desk-summary.tsx` | Same rows, line offers, pre-order chip, why-this-price. |
| Order lines (my order, tracking) | `modules/orders/common.tsx` `OrderLines` | List price, offer label, pre-order tag, "waiting for stock" per line. |
| Pre-order label + ship date | `preorders/preorder-parts.tsx` (`PreorderBadge`, `PreorderTag`, `PreorderProductNotice`, `PreorderCartNotice`, `OrderPreorderNotice`, `PreorderConfirmed`) | Card, product page, cart line + notice (advance amount from `preorderAdvance`), checkout (lines + notice), confirmation, tracking page, my order detail. Shown dates are formatted in the page language; the whole order waits (decision P14) and can be cancelled while waiting. |
| Free-delivery in the cart estimate | `modules/cart/delivery-estimate.tsx` | Waiver line names the promotion; the free-delivery threshold now uses the amount after promotions (`subtotal - promotionDiscount - discount`). |

### Staff side (FR-AA01, FR-AA03)

- Product editor tab "Pre-order" (`?tab=preorder`): `preorders/preorder-settings.tsx`. Product switch, expected date, advance percent; each variant: inherit / allowed / not allowed with its own date and advance and the effective values from the API. Date not in the past (Dhaka today), advance 0..100, checked before sending; server field errors shown. Read-only without `catalog.product.edit`.
- Queue `/admin/orders/preorders` (menu: Sales > Pre-orders, permission `orders.order.view`): `preorders/preorder-queue.tsx`. Filters state, branch, expected before, search; per-line state (waiting units or served date), advance paid/required, link to the order; "Give stock to waiting orders" (`POST /orders/preorders/allocate`, branch = the filter, else the working branch, else all the person may serve) only with `orders.order.edit`.
- Error texts added to `errors.*`: `PREORDER_AWAITING_STOCK`, `PROMO_CAMPAIGN_NOT_FOUND`, `PROMO_USED_UP`, `PROMO_MULTIPLE_COUPONS` (en and bn).

## Decisions (reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| S1 | Server reads of the home page, product pages, product lists, banners and campaigns use `storeGet(..., { fresh: true })` (`cache: "no-store"`); other reads keep the one-minute cache. | P12: the API cuts its own Cache-Control at the next promotion start or end; the BFF/Next layer must not keep an answer longer. | `lib/server/store.ts` option `fresh`. |
| S2 | At zero the countdown asks the API again instead of computing a price itself. | The base price after a sale ends (sale price, regular price) is only known to the API. | `useOnPromotionEnded` in `promotions-store/promo-parts.tsx`. |
| S3 | Pre-order shown from the API facts only (`isPreorder`, expected date); the text is our own in en and bn (the API labels are used only as data, not displayed). | One wording for both languages and a localised date. | `preorders.*.json`. |
| S4 | The cart summary keeps the old "Subtotal" row when nothing was saved, and replaces it by list price + offer rows when something was. | Existing tests and the plain case stay as they were. | `hasSavings`, `savingRows` in `promotions-store/logic.ts`. |
| S5 | A pre-order variant has quantity up to the per-order maximum (10) on the product page; the cart answer is the authority. | `available` is 0 for such a variant; the API's cart limit for pre-order lines is the per-line maximum. | `product-view.tsx` `stock`. |
| S6 | Banner and campaign hero use a fixed dark base with white text. | Contrast must hold in both themes whatever the picture. | `promo-banners.tsx`, campaign page. |

## Contract gaps

1. `MyOrderListItemDto` (My orders list) has no pre-order state, so the list cannot show a "Waiting for stock" chip without one request per order; the state is shown on the order page and the tracking page. Adding `preorderState` to the list item would close it.
2. The shop cart answer gives no per-line pre-order advance, only the cart total `preorderAdvance`; the notice shows the total.
3. `GET /store/banners` returns a banner only with a picture (`imageUrl` is not nullable), so a campaign without a banner picture has a page but no banner; the admin editor (builder A) should ask for the picture.
4. The campaign page does not show a "starts on" date because the API serves the page only while it runs.
5. The public delivery quote by items judges the free-delivery threshold on catalog prices (documented in `step11b-backend-promotions.md`); the cart quote is exact.

## Partial, not built

- Notify-me, unsubscribe, rewards tender, promotions admin editor: other builders.
- No live (SignalR) refresh of the pre-order queue; the list refetches after the allocate action.
- The staff order detail does not show the pre-order state (only the friendly `PREORDER_AWAITING_STOCK` message when confirming).

## Tests

Final tree: `tsc --noEmit` clean, `eslint src e2e tests` clean, vitest 298 passed (21 files; new: `tests/promotions-store.test.ts`, `tests/preorders.test.ts`), `npm run build` (Turbopack) exit 0.

Browser (own ports 5112/3112, database `JuvoDb_E2E_store`, dropped by the suite's cleanup):
- `e2e/promo-store.spec.ts` (flow, real API): 8 passed on desktop. Flash sale on card, product page, cart, checkout; countdown reaching zero (price goes back, countdown gone); coupon that loses (kept with reason); free delivery; campaign page, banner, Bengali title, archive makes page and banner go; full pre-order path (labels, cart notice and advance, order placed, customer and guest see "waiting", staff queue, stock added, allocate, Ready everywhere); product editor tab (rules, save, variant override, reload); permissions (Order Manager read-only tab, Support has no allocate).
- `e2e/promo-store-ui-checks.spec.ts`: 11 tests (shop, product, campaign, home banner, cart, checkout, queue, settings at 360/768/1280, plus dark theme) passed on desktop, Pixel 7 and iPhone 14 (22 passed there; the device projects were run with a temporary testMatch edit that was reverted). No overflow, no tap target under 44 px, no axe violations, no console errors.
- Regression run of cart, store, orders, catalog, returns specs on desktop: 63 passed, 1 failed (`catalog.spec.ts` "a staff member who may only view products": it clicks "Classic Cotton T-Shirt" on page 1 of the product list and the link was not on the page; none of this builder's specs had run in that database, the list is paged by ten, so it looks like an existing list-order dependency, not checked against the base commit).
- Build note: `next build` failed with a Tailwind loader crash until `.next` was deleted (a leftover from a `--webpack` build); a clean Turbopack build passes. A webpack build has no Tailwind and must not be used for UI checks.

## Traceability suggestions

| ID | Status | Implemented in | Notes |
| --- | --- | --- | --- |
| FR-Z01 | Done (screens) | `countdown.tsx`, card and product page, cart/checkout rows | Countdown from `promotion.endsAt`, stops at zero and refreshes. |
| FR-Z02 | Done (screens, customer side) | `price-explainer.tsx`, cart/checkout/desk | Bundle, buy-X-get-Y and free delivery shown as price rows and hints. |
| FR-Z03 | Done (customer side) | `/campaign/[slug]`, `promo-banners.tsx` | Admin editor is builder A. |
| FR-Z04 | Done (customer side) | `WhyThisPrice`, `CouponLostNotice` | The API's own explanation and coupon reason. |
| FR-AA01 | Done | product editor tab | Product and variant. |
| FR-AA02 | Done | `preorder-parts.tsx` and the pages above | Label, date, advance notice, why "Waiting". |
| FR-AA03 | Done | `/admin/orders/preorders` | Filters, per-line state, allocate. |
