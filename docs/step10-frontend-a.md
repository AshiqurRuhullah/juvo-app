# Step 10 front end, builder A: couriers, parcels, labels, live parcel updates

Written by builder A for the lead to merge into `PROGRESS.md`, `docs/understanding.md` and `docs/assumptions.md`. Backend contract: `docs/couriers-api.md`.

## What was built

| Route | Permission | Files |
| --- | --- | --- |
| `/admin/couriers` (tabs Couriers and Connections) | `couriers.courier.view` (edit controls need `couriers.courier.edit`) | `src/modules/couriers/{couriers-view,courier-form,zones}.ts(x)` |
| `/admin/shipments` | `couriers.shipment.view` | `shipments-list.tsx` |
| `/admin/shipments/[id]` | `couriers.shipment.view` | `shipment-detail.tsx`, `shipment-dialogs.tsx` (cancel, re-book, sandbox advance; lazy chunk) |
| `/admin/shipments/[id]/label` | `couriers.shipment.print-label` | `label-view.tsx`, `label.css`, `barcode.tsx`, `code128.ts` |
| `/admin/shipments/labels?ids=a,b,c` (max 50) | `couriers.shipment.print-label` | same |

Shared pieces: `common.tsx` (status badge, journey bar, tracking link), `admin-logic.ts` (pure form, validation and id logic).

## Decisions (for `docs/assumptions.md`)

1. Own Code 128 encoder (`code128.ts`, about 90 lines, no package): set B for text, set C for even digit strings of 4 or more, 10-module quiet zones, drawn as one inline SVG path. Reason: no dependency, no licence question, nothing to download. Change: swap `barcode.tsx` for a package if a 2D code is ever needed.
2. Labels are always black on white ("paper" scope in `label.css` re-declares the surface and text tokens inside the label only) so the barcode scans from the screen and the printout in the dark theme too. A6 page size (105 x 148 mm) in `@media print`; the admin shell already hides itself in print.
3. Courier list is cards, not a table: a handful of rows with many facts each, and cards already work on every width.
4. The "Serves these zones" choice reads `GET /delivery/zones` (permission `delivery.zone.view`). A courier editor without that permission gets a typed zone-code field instead of the checkbox list.
5. Switching on a slot answers `409 COURIER.NOT_CONFIGURED`; the card shows the API's translated explanation inline (not only a toast). The switch inside the edit form is disabled for a connection that is not set up.
6. The detail page follows the parcel live by joining the order group and the active branch group (`all` for global staff) and reloading only that parcel's query on `ShipmentEventAdded` (events are at-least-once, so it never appends). The list reloads its queries on the same event. No polling anywhere.
7. Sandbox "Advance" is one dialog: it preselects the API's default next step (`defaultNext`), lets staff pick another allowed status or a hub scan, and takes a note. The box around it says "Sandbox simulator" and says it does not touch a real courier.
8. Count chips on the parcel list were not built: there is no summary endpoint, and one request per status would not be cheap.
9. Performance: page shells are server components; the courier form and the parcel dialogs are `next/dynamic` chunks; the SignalR client loads only on the live screens; lists use server paging (20), `keepPreviousData`, memoised columns and cards; labels are fetched in parallel (`useQueries`).

## Traceability rows believed changed (ids from docs/understanding.md)

The backend rows stay as they are; these notes add the front-end half.

| FR | Status | Front-end note |
| --- | --- | --- |
| FR-H01 | Partial | Connections tab shows the sandbox and the six slots with set up or not set up; real adapters are Step 14. |
| FR-H02 | Partial | Courier form covers every field of SaveCourierRequest (code on create, provider, switch, priority, COD, international, weights, zones, charge override, rates, tracking template, notes, non-secret settings) plus archive. Credentials stay out of the UI by design (read from configuration in Step 14). |
| FR-H03 | Done | Booking one order and bulk booking are the order screens (builder C); this builder adds the parcel list and the re-book dialog. |
| FR-H04 | Done | Parcel page shows consignment, https-only tracking link and cost; single and multi label pages with a Code 128 barcode (A6 print layout). The packing slip is the existing order slip. |
| FR-H05 | Done | Event timeline with source (staff, courier message, status check, sandbox, system), actor, place and the raw courier status. |
| FR-H08 | Done | Failed delivery shows its reason with an icon and text, the attempts count, and the retry or return steps through the sandbox advance dialog. |
| FR-H12 | Done | Cancel (reason required) and book again (courier by suggestion or automatic) on the parcel page. |
| FR-J03 | Partial to Done for parcels | Parcel list and parcel detail refresh live on ShipmentEventAdded with a connection badge. Order and dashboard live updates are builder C and the dashboard step. |
| FR-J06 | Done | The parcel page shows the immutable parcel log with actor and time. |

## Partial and deferred

- Count chips (decision 8).
- If a staff member lacks `orders.order.view` in the branch, the hub refuses `JoinOrder` and the live badge shows "Offline" even though the page works; it still reloads on navigation. A contract gap, see below.
- Courier connections are display only: connecting a real account is Step 14.

## Contract gaps found

- `GET /delivery/zones` needs `delivery.zone.view`, which a courier editor may lack. A zones list with only code and names under `couriers.courier.view` would remove the typed fallback.
- The hub joins fail as a whole when the first `JoinBranch` is refused (the client stops before `JoinOrder`). The hub could answer per join; the client could join each group on its own.
- `POST /shipments/{id}/cancel` answers no documented body; the e2e helper only checks for a 2xx.

## Verification (builder A, run in the worktree)

- tsc --noEmit clean; eslint src e2e tests 0 problems; vitest 11 files, 110 tests passed (tests/couriers-admin.test.ts included); npm run build ok; backend dotnet build in juvo-be 0 warnings, 0 errors.
- Playwright (desktop project, API 5181, web 3200): couriers and shipments specs, 31 tests, all passed (functional specs plus UI checks at 360, 768 and 1280 px and dark theme: overflow, 44 px targets, axe WCAG 2.1 AA, console errors). Screenshots in juvo-front/docs/ui-checks/couriers/.

## Notes for the lead

- playwright.config.ts needs the CORS origin. The live hub is reached by the browser directly on the API, whose CORS policy allows only http://localhost:3000, so in e2e the socket is refused and the live badge says Offline. I ran with the environment variable Cors__AllowedOrigins__0=http://localhost:3200. Add Cors__AllowedOrigins__0: E2E.webUrl to the API webServer env, otherwise the live update test in e2e/shipments.spec.ts fails.
- e2e/support/couriers-ui.ts: settleBounded replaces the shared settle in my UI checks. In headless Chrome at 1280 px document.fonts.ready never resolved (fonts stay unloaded) and hung settle. The checks are unchanged; the font wait is capped at 8 s.
- npm ci hung in the worktree (three builders installing at once); node_modules was copied from the main checkout (same lockfile).
- The cleanup project once failed to drop the e2e database while the API still held a connection; it was dropped by hand.
