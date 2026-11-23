# Step 12f: admin dashboard (module M, FR-M01..M08)

Front end only (`juvo-front`), existing reports endpoints only, no backend change, no `gen:api`. Branch `feature/12f-dashboard`.

## Built
- `src/modules/dashboard/period.ts`: pure rules (range from the address, defaults, previous period, deltas, series alignment, top-N, payment split, stock health, order status groups, orders link). Unit tests: `tests/dashboard.test.ts` (15).
- `src/modules/dashboard/range-sections.tsx`: the date range part (filter bar reused from reports `FilterBar` without the branch field; sales trend with day/week/month and compare; best sellers; top categories; top customers; orders by zone; cash view; stock health). Each section is a `ReportCard` with its own query, skeleton, error and empty state; a 403 hides the card silently, other failures show an error inside the card only.
- `dashboard-view.tsx`: status strip (to pack, in delivery, delivered, cancelled, returned), "failed deliveries" row in the action queue, "screens to check" list, range part appended. Existing test ids are unchanged.
- `/admin/orders` now accepts any valid `status` and `from`/`to` (day range) in the address (it accepted only Placed and Confirmed). It only sets the initial state of the list's existing filters.
- Range in the address: `?from=&to=` (Dhaka days), `groupBy=day|week|month` (only when not the default), `compare=0` (compare is on by default). Default range: last 30 days.
- Messages: `dashboard2.{strip,period}` and `reports.dashboard.actions.*` in en and bn.

## Audit (before the work, then result)
| FR | Before | Now |
| --- | --- | --- |
| M01 | Today cards: sales, orders, average, to confirm. Missing: to pack, in delivery, delivered, cancelled/returned | Status strip adds all of them. The API has no "today" counts for these: to pack = all open orders, the others = orders placed in the last 30 days (labelled on each card) |
| M02 | Queue: to confirm, to pack, held by fraud, returns waiting, low stock | Adds failed deliveries (30-day count from orders-by-status). Payments to verify and failed courier bookings have no number in the API: said on screen, with links to Payments and Shipments |
| M03 | 14-day fixed chart | Range chart with day/week/month, previous period of equal length (line, change tiles, hidden data table); compare can be switched off |
| M04 | Top 5 products (30 days) | Adds range best sellers (sales groupBy=product), top categories (groupBy=category), top customers (customers report; name or mobile exactly as the API sends, masked when it masks). The fixed 30-day card stays |
| M05 | Status donut (30 days) | Adds orders by delivery zone (groupBy=zone). District is not available (gap) |
| M06 | COD in transit, COD pending (live) | Range cash view: COD pending, in transit, collected, settled, refunds completed, online-payment orders. Cost fields not used, so nothing to hide |
| M07 | Low-stock count only | Stock health: low, out of stock, slow movers (count and value, 60 days), worst low-stock rows, link to inventory |
| M08 | none | Presets + custom range in the URL; cards and rows link to the lists that exist (below) |

## Links (only what the target accepts)
Orders: `?status=` (any status) and `?from=&to=`; strip "delivered/cancelled", queue rows; "orders placed in this period". Others: `/admin/inventory`, `/admin/cod`, `/admin/payments`, `/admin/shipments`, `/admin/returns`, `/admin/fraud`, `/admin/customers/{id}`, `/admin/products?search=` (best sellers, by product name). Links render only when the person holds the screen's permission; otherwise the card is plain.

## Decisions
1. Range sections sit below the live cards, with their own heading, so live cards are not mistaken for range figures. Change: move `RangeSections` in `dashboard-view.tsx`.
2. Section visibility = `reports.<x>.view` (sales: trend, products, categories, zones; finance: cash; inventory: stock; customers: top customers). No report permission: a short note.
3. "Online-payment orders" = sales with payment method other than COD (orders placed, not confirmed payments). Labelled so; hint says why.
4. Stock request does not change with the range (snapshot); out of stock counted among low-stock rows (limit 100; shows "at least" when cut).
5. Previous series is matched by position (n-th day/week/month); week/month buckets may differ by one at the end.
6. Day grouping beyond 366 days is explained and no request is sent (same rule as the API).

## Contract gaps (for the backend)
- Dashboard feed: no counts for payments to verify, failed courier bookings, delivered/in delivery/cancelled for today, and `ordersByStatus` is fixed to 30 days (no range).
- No "refunds due" (only completed refunds); no split of confirmed online payments from unpaid online orders.
- Sales has zone but no district grouping; orders list has no zone filter.
- Out of stock: variants with reorder level 0 are not in the report; no out-of-stock count.
- Lists without query params: payments, returns, shipments, COD, inventory (no low-stock filter), customers.
- Sales product rows have no product id (best-seller link searches by name).

## Tests (run on the final tree)
- `npx tsc --noEmit` clean; eslint clean on changed files; `npm test` 547 passed (31 files), of which 15 new.
- Playwright desktop: `e2e/dashboard.spec.ts` 12 tests passed (range/preset/custom/reload/URL, too-large range, grouping, compare and off, cash/stock/top customers vs API, status strip vs API, links, three permission cases); `e2e/dashboard-ui-checks.spec.ts` 5 tests passed (360/768/1280 default and custom range, dark, Bengali 360 and 1280: axe, overflow, 44 px, console); existing `reports.spec.ts` dashboard tests (2) and `reports-ui-checks` dashboard at 3 widths passed.
- Not run: Pixel 7 and iPhone 14 projects, other suites, `ui-checks.spec` admin-dashboard.
- Environment note: the e2e API sometimes needs more than 120 s to be ready on this machine (one run timed out; a leftover `JuvoDb_E2E_*` database must be dropped before a rerun). Expect timeouts on the first dashboard load were raised to 30 s in the specs.

## Traceability suggestions
FR-M01 Partial (to pack/in delivery/delivered/cancelled/returned from 30-day feed). FR-M02 Partial (payments to verify and failed bookings have no counts). FR-M03 Done. FR-M04 Done. FR-M05 Partial (zone, not district). FR-M06 Partial (no refunds due, no confirmed-online split). FR-M07 Done (out of stock limited to items with a reorder level). FR-M08 Done for the range; links only to lists that accept filters.
