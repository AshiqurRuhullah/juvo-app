# Step 12c: coupons admin screens (notes of the builder)

Branch `feature/12c-coupons-admin`. Front end only; `juvo-be/` untouched.

## Built
- Route `/admin/coupons` (menu: Marketing group, next to Promotions, `ready: true`, permission `promotions.coupon.view`, scope anywhere, label `nav.items.coupons` in en and bn).
- Module `juvo-front/src/modules/coupons`: `logic.ts` (pure rules), `api.ts`, `common.tsx` (rights, status badge, problem texts), `coupons-list.tsx`, `coupon-dialog.tsx` (create and edit), `delete-dialog.tsx`.
- List: search by code or note, status filter (Active, Switched off, Not started, Expired, Used up), shared pager (10 per page), cards on phones. Columns: code and note, discount and kind with cap, minimum order, validity window (Dhaka time), uses of limit, with-promotions flag, status, actions (Edit, Switch off/on, Delete).
- Create/edit dialog: every DTO field. Code upper-cased with spaces removed as you type, "Make a code" helper (pure client function, no ambiguous characters), kind radios with consequence text, percentage or BDT value, worked example ("on a 2,000 cart this takes ..."), cap (percentage only), minimum, start and end typed in Dhaka time and sent as `+06:00`, total usage limit, active switch with consequence, "Can be combined with promotions" with the stacking policy of `docs/pricing-engine.md` section 3 in plain words. Bengali digits and commas accepted in money.
- Delete dialog states the real consequence: unused coupon is deleted for good; used coupon is switched off and kept (see decisions); already off or no edit right: explanation only, no confirm.
- Read-only: no buttons and an info note without edit and delete permissions; create button only with `.create`; edit buttons only with `.edit`; delete only with `.delete`.

## Decisions (add to assumptions if accepted)
| Decision | Reason | How to change |
| --- | --- | --- |
| The screen loads every coupon (pages of 100, up to 50 pages) and filters, searches and pages in the browser. | The API filters only by on/off, but the owner wants expired, not started and used up; a shop has few coupons. A warning shows if more than 5,000 exist. | Add `status` and `search` filters to `GET /promotions/coupons`, then use the server pager (`useCoupons` in `api.ts`). |
| "Delete" on a used coupon is two steps in the UI: the API answers 409 `COUPON.INACTIVE` and never switches off, so the dialog explains and then saves the coupon with `isActive=false` through `PUT`. This needs the edit permission; with delete only the dialog just explains. | The API contract (`CouponsService.DeleteAsync`) refuses instead of switching off. Staff expectation (brief) is "switched off and kept". | If the API ever switches off itself, drop the PUT in `delete-dialog.tsx`. |
| A coupon with zero uses that sits in a cart is also refused by the API; the dialog then turns into the switch-off dialog with a warning. | Same API rule (`Carts.Any(CouponId)`). | Same as above. |
| Switch on/off is a direct row action (no confirm): it sends the whole coupon back with only `isActive` changed (`withActive`). | The API takes the whole coupon on every save; switching is reversible. | Wrap in a confirm dialog if the owner wants one. |
| Status is derived in the browser in the shop's own order: Off, Not started, Expired, Used up, Active (`couponStatus`, mirrors `Coupon.Check`). Clock refreshes every 30 s. | The DTO has no status field. | Replace by a server status field if added. |
| Coupon error texts live in `coupons.errors.*` (staff wording), not in the shared `errors.*` (those `COUPON_*` texts are customer wording under `cart.errors`). | Staff need "switch it off instead", customers need "not active". | n/a |
| End must be strictly after start (the API refuses equal times). | `CouponsService.Validate`. | n/a |
| Money parser and Dhaka time helpers are imported from `modules/delivery/logic` and `modules/promotions/logic` (both pure), not copied. | One rule for Bengali digits and 2 decimals. | n/a |

## Contract gaps (what the SRS or brief wants and the API cannot do)
1. **Per-customer usage limit** (for example one use per mobile number): DTO and request have no field. UI says so next to the limit. API change: `perCustomerLimit: int?` on `CouponDto`/`SaveCouponRequest`, a use log per customer (mobile) in order placement, `COUPON.CUSTOMER_LIMIT` code in `Coupon.Check`.
2. **Per-coupon revenue and discount-cost report** (orders, revenue, discount given, per day): none; only `usedCount`. API change: `GET /promotions/coupons/{id}/report` and a list report like `/promotions/report` (orders and `Orders.CouponDiscount` sums, filter by Dhaka date range).
3. **Bulk generation of unique codes** (for example 500 single-use codes for a campaign) and CSV export: none. API change: `POST /promotions/coupons/generate { prefix, count, template }` (unique codes with usage limit 1) and an export endpoint. The "Make a code" button only fills one random code in the form.
4. **Customer-specific and referral-linked coupons** (valid for one customer, segment or referrer): no `customerId`, `segmentId` or referral link on a coupon. API change: optional `customerId` or `segmentId` on coupons, checked in `Coupon.Check`/cart apply.
5. **Server-side status filter and search by status**: see decisions.
6. **Switch off on delete**: the API refuses instead; see decisions (optional `DELETE ...?switchOffIfUsed=true`).
7. **Which promotions a coupon lost to / won against** per order is only in the customer cart explanation; no staff view per coupon.
8. Category, product or brand restrictions for a coupon (the SRS does not ask; the API has none).

## Partial / not built
- No separate detail page: edit is a dialog (like couriers and delivery countries). No audit trail view of coupon changes (audit log exists in the API for staff elsewhere).
- Validation messages the API itself returns (400 field messages) are shown as sent (English); the browser checks the same rules first, in both languages.
- Bengali texts are a first pass (native review in Step 12).
- Pixel 7 and iPhone 14 projects of the UI-check spec were not run (machine limits); desktop project covers 360, 768 and 1280 with a resized viewport.

## Tests (last run on the final tree)
- tsc 0 errors; eslint on my files 0 errors, 0 warnings; vitest full run 27 files, 475 tests passed (28 new in `tests/coupons.test.ts`).
- `npm run build` ok (second run, first stops at the advisory notice).
- `e2e/coupons.spec.ts`: 17 tests passed on the desktop project (first run 14 passed, 3 spec mistakes fixed: duplicate table and card nodes, collapsed menu group; the 3 re-run and passed). Covers create percent and fixed, Bengali digits, generate code, validation, duplicate code, edit, switch off and on, delete unused, used coupon (real order through the shop endpoints: cart item, `POST /store/cart/coupon`, COD checkout) kept and switched off, status filter, pager, five permission cases.
- `e2e/coupons-ui-checks.spec.ts`: 23 passed (list, new, new with errors, edit, delete used, delete unused, read-only at 360, 768, 1280; dark; Bengali) with axe, overflow, 44 px and console checks. Screenshots in `juvo-front/docs/ui-checks/coupons/` (key ones committed).

## Traceability suggestions
| ID | Status | Notes |
| --- | --- | --- |
| FR-C07 | Done | Admin screen `/admin/coupons` (Step 12c): create, edit, switch, delete rules; per-customer limit not available (gap 1). |
| FR-Z04 | Done | Stacking flag explained in the coupon form with the policy of `docs/pricing-engine.md` section 3. |
| FR-C02 | no change | Customer side unchanged. |
