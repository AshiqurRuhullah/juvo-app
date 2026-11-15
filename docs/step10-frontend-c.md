# Step 10 front end, builder C: booking, customer tracking, live updates, desk totals

Notes for the lead to merge into `PROGRESS.md`, `docs/understanding.md` and `docs/assumptions.md`. Contract: `docs/couriers-api.md`.

## What was built

| Area | Where | Notes |
| --- | --- | --- |
| Courier panel on the staff order page | `juvo-front/src/modules/orders/booking/courier-panel.tsx` (lazy, `next/dynamic`), `book-dialog.tsx` (lazy) | Parcels (forward and return, newest first) with status, courier, consignment, https-only tracking link, COD, link to `/admin/shipments/[id]` and its label. Book dialog: suggestions with the recommended courier preselected, ineligible ones muted with the API's reason, optional weight/size/note, suggestions refetched 400 ms after the weight changes, booking errors as texts, then consignment and Print label. |
| Bulk booking | `booking/bulk-book-dialog.tsx` (lazy), `orders-list.tsx` | Selected Packed orders (up to 50), optional courier (default automatic), per-order result list, failures first. Existing bulk confirm/cancel/print unchanged. |
| Returns | `returns/pickup-courier.tsx`, `returns/return-parcel.tsx` (lazy), `return-actions.tsx`, `returns/api.ts` | "Book a courier for the pickup" switch (needs `couriers.shipment.book` in the return's branch); the manual courier-name path is unchanged. Reverse parcel card on the return detail. |
| Customer card | `booking/courier-card.tsx`, used in `track-view.tsx` and `my-order-detail.tsx` | Courier, consignment, https-only link, step timeline, attempts, failed delivery with icon and text. Only when `shipment` is not null. |
| Guest cancel | `booking/guest-cancel.tsx`, `track-view.tsx` | `POST /store/orders/track/cancel` with the lookup's proof; confirmation dialog; `ORDER.CANNOT_CANCEL` shown and the page refreshed. |
| Live | `booking/use-staff-live.tsx`, `track-view.tsx`, `my-order-detail.tsx`, `orders-list.tsx`, `order-detail.tsx`, `return-detail.tsx` | Guest (proof), customer (own order), staff list (active branch, or `all` when the session sees every branch), staff order page (joins the order), return page (joins the return's branch). New-order toast with a link. LiveBadge on each. No polling. |
| Desk totals | `booking/use-desk-quote.ts`, `booking/desk-summary.tsx`, `order-desk.tsx` | Real preview from `POST /orders/manual/quote`, 400 ms debounce, aborted when superseded, the request is the query key so an old answer can never replace a newer one. Coupon field added (the desk could not send a coupon before). The old local estimate is gone. |
| Shared pure logic | `booking/logic.ts` | Unit tested in `tests/booking.test.ts`. |
| Texts | `src/messages/booking.{en,bn}.json` | Namespace `booking`. Bengali is a natural first pass and needs the Step 12 native review. |

Small surgical edits to existing files: `src/lib/realtime/use-live.ts` (guest join, see decisions), `e2e/orders.spec.ts` (desk note assertion now checks the server preview), `returns/api.ts` (schedule-pickup body, shipments invalidation).

## Decisions (for docs/assumptions.md)

1. **A guest must join the order group itself.** The hub does not add a connection to any group. The guest hub token answer carries `orderId`, but the tracking answer does not, so `useLive` now joins `first.orderId` for mode `guest`. Without this a guest never receives an event.
2. **Staff order page joins its order, not the branch.** Every event it gets is then about that order and the refresh touches only that order's queries.
3. **Return page joins the return's branch.** Reverse-parcel scans are published to branch groups only (not to the order group, contract section 10), so following the order would miss them.
4. **Order list live scope:** the active branch; `all` only when `isAllBranches`; with neither, no live connection.
5. **Bulk booking uses the statuses seen when the order was ticked** to decide which selected orders count as bookable; the API still decides per order.
6. **Desk preview waits until the form can be priced** (valid mobile, complete address, at least one line). Until then a hint is shown; no request is sent.
7. **Confirmation page:** it has no tracking data call and no parcel exists at that moment, so no courier card was added there. It links to the tracking page, which has the card.
8. **Courier ineligibility reasons** are shown as the API sends them (English staff text), because they are free text and not a code.

## Traceability rows believed changed

| FR | Status | Note |
| --- | --- | --- |
| FR-H02 (suggest and book a courier) | Done (front end) | Order screen dialog, recommended preselected, reasons for ineligible couriers. |
| FR-H03 (bulk booking) | Done (front end) | Up to 50, per-order result. |
| FR-H04 / FR-H05 (parcel on the order, label link) | Done (front end) | Panel on the order page; label opens Builder A's route. |
| FR-J03 (customer sees courier and tracking) | Done | Card on tracking and account order pages. |
| FR-J06 (live updates for staff) | Done | List, order page, return page. |
| FR-J07 (live updates for customers) | Done | Guest and signed-in customer. |
| FR-F03 (customer cancels while allowed) | Done | Guest cancel on `/track`; signed-in cancel already existed. |
| FR-L01 / FR-L04 (return pickup by courier, reverse parcel visible) | Done | Switch in schedule-pickup, reverse parcel card. |
| FR-S01 (social order desk totals) | Done | Real preview, closes the Step 8-b "estimate" gap. |

## Partial, deferred or not built

- The confirmation page has no courier card (see decision 7).
- The return parcel card cannot tell which return a reverse parcel belongs to when an order has several returns (the list item has no `returnRequestId`); it shows every reverse parcel of the order.
- No end-to-end test for the Bengali texts; key parity is enforced by `tests/messages.test.ts`.

## Contract gaps found in the API

1. **`ShipmentListItemDto` has no `returnRequestId`** (only the detail has it). The return screen cannot pick its own reverse parcel from the list when an order has more than one return.
2. **`TrackingDto` has no order id.** A guest can only learn the order id from the hub token answer; the live hook relies on it.
3. **`playwright.config.ts` does not give the API an allowed origin for the e2e web URL.** The SignalR negotiation is a cross-origin request, so the live specs need `Cors__AllowedOrigins__0` set to the web URL (`http://localhost:${E2E_WEB_PORT}`) in the API's `webServer.env`. Locally it was passed through the shell environment. The lead should add `Cors__AllowedOrigins__0: E2E.webUrl` to the config.
4. The suggestion `reason` is free English text, not a code, so it cannot be translated.
