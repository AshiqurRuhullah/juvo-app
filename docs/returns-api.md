# Returns API (module L, Step 10 backend slice)

Base path `/api/v1`. Money is BDT, tax-inclusive, decimals with two places. Enums travel as text. Errors are RFC 9457 problem documents with a stable `code` (list in section 6). The live typed contract is the OpenAPI document (`/scalar/v1` in development); this page explains how the parts fit together for the front end. Decisions and defaults: `docs/assumptions.md` section N. Orders: `docs/orders-api.md`.

What exists: the customer return and exchange request (module L, FR-F04, FR-L03..L08), the admin returns queue and workflow, restocking into the fulfilling branch, refund records, linked exchange orders, the SMS events. What does not exist yet: moving the money through a payment gateway (Step 9 adds an `IRefundGateway`-style adapter; until then staff do the transfer and write down the reference), booking the return pickup through a courier API (Step 10 courier slice; today staff arrange it and write the date, courier name and note), the policy page text (CMS, Step 12), a settings screen for the three return settings (Step 12).

## 1. Customer (`/me`, customer token)

### `GET /me/orders/{id}/return-eligibility`

`ReturnEligibilityDto`: `orderId`, `orderNumber`, `eligible`, `reasonCode` and `message` when not eligible, `deliveredAt`, `deadline` (`deliveredAt` + window), `windowDays`, `items[]`, `reasons[]` (`code, labelEn, labelBn`), `kinds` (`["Return","Exchange"]`).

`items[]` (`ReturnableLineDto`): `orderItemId, variantId, productId, name, nameBn, sku, colorName, size, imageUrl, unitPrice, quantity` (bought), `alreadyReturned` (in returns that still count), `returnableQuantity`, `refundPerUnit` (price paid per unit after the order discount; the delivery charge is not in it), `returnable`, `blockedReason` (`NON_RETURNABLE`, `ALL_RETURNED`, `ORDER_NOT_ELIGIBLE`).

`eligible` is false with `reasonCode` `RETURN.ORDER_NOT_ELIGIBLE` (not delivered), `RETURN.WINDOW_EXPIRED`, or `RETURN.ITEM_NOT_RETURNABLE` (every line blocked). An order of another customer is 404 `ORDER.NOT_FOUND`.

### `POST /me/returns/photos` (201, multipart form, field `file`)

One picture per call (JPEG, PNG or WebP, at least 1000 px on the long side, at most 10 MB; stored as WebP with a thumbnail through `IFileStorage`). Answer `{ path, url, thumbUrl }`. Send the `path` values in the request below. Paths are tied to the customer (`returns/<customerId>/...`); someone else's path is refused.

### `POST /me/returns` (201)

Header `Idempotency-Key` (optional, also `idempotencyKey` in the body, 80 characters, per customer): a repeated call answers with the same request.

```json
{
  "orderId": "...",
  "kind": "Return",
  "reasonCode": "DAMAGED_ITEM",
  "details": "The zip is broken",
  "items": [ { "orderItemId": "...", "quantity": 1, "exchangeVariantId": null } ],
  "photoPaths": [ "returns/.../2611/....webp" ],
  "idempotencyKey": "client-key"
}
```

Reasons and the party they point to by default: `DAMAGED_ITEM`, `WRONG_ITEM`, `NOT_AS_DESCRIBED`, `QUALITY_ISSUE` (seller); `SIZE_FIT`, `CHANGED_MIND`, `OTHER` (customer). For `kind: "Exchange"` every line names `exchangeVariantId` (a variant of the same product; defaults to the same variant). Checks, in order: order delivered (or already partly returned) and inside the window, product not non-returnable, quantity at most bought minus what is already in returns that still count (anything except Rejected, Cancelled, RefundRejected), up to 5 pictures, 1 to 50 lines. Concurrent requests for one order are serialised, so the last unit can be claimed once.

Response `MyReturnDto`: `id, number, orderId, orderNumber, kind, status, reasonCode, reasonText, rejectReason, requestedAt, items[], photos[], timeline[], pickup, refund, refundPaid, replacementOrderNumber, replacementOrderId, canCancel`. `refund` is `{ items, deliveryCharge, returnCharge, total, final }`: an estimate (`final: false`, items only) until the parcel is inspected, final afterwards. `timeline[]` is `{ from, to, reason, actorName: null, at }`: no staff names; a reason only when the customer wrote it or the shop explained a refusal. No internal notes, no fault.

### Other customer endpoints

- `GET /me/returns?filter=active|closed&page&pageSize` newest first: `MyReturnListItemDto` (`id, number, orderId, orderNumber, kind, status, quantity, firstItemName, firstItemImageUrl, refund, requestedAt, canCancel`).
- `GET /me/returns/{id}` as above; someone else's return is 404 `RETURN.NOT_FOUND`.
- `POST /me/returns/{id}/cancel { reason? }` while `Requested` or `Approved` (409 `RETURN.INVALID_TRANSITION` later). If the cancelled request was holding the order in ReturnInProgress, the order goes back to Delivered.

## 2. Staff (`/returns`)

Every endpoint names a permission; the service proves it again in the return's own branch (a return of another branch is 404 `RETURN.NOT_FOUND`, audit-logged; a missing permission there is 403 `AUTH.BRANCH_FORBIDDEN`). Lists show the branches where the permission is held.

| Endpoint | Permission | Notes |
| --- | --- | --- |
| `GET /returns` | `returns.return.view` | Filters `status, kind, fault, branchId, from, to`; `search` (return number, order number, mobile, name); `sort` `requestedAt` (default newest), `number`, `status`; `page, pageSize` |
| `GET /returns/summary` | `returns.return.view` | `ReturnSummaryDto`: `open, awaitingApproval, awaitingPickup, inTransit, awaitingInspection, awaitingRefund, awaitingExchange, refundedThisMonth, byStatus[]` |
| `GET /returns/{id}` | `returns.return.view` | `ReturnDetailDto` (below) |
| `POST /returns/{id}/approve { note? }` | `returns.return.approve` | Requested to Approved; the order goes Delivered to ReturnInProgress |
| `POST /returns/{id}/reject { reason }` | `returns.return.reject` | Requested to Rejected; the reason is shown to the customer |
| `POST /returns/{id}/schedule-pickup { date, courier?, note? }` | `returns.return.manage` | Approved to PickupScheduled; `date` from today to 60 days ahead |
| `POST /returns/{id}/picked-up` | `returns.return.manage` | PickupScheduled to PickedUp |
| `POST /returns/{id}/received` | `returns.return.receive` | PickedUp to Received |
| `POST /returns/{id}/inspect { items[], fault?, note? }` | `returns.return.inspect` | Received to Inspected; restocks; fixes the refund (section 4) |
| `POST /returns/{id}/refund { method, reference, amount? }` | `returns.return.refund` | Inspected to Refunded (kind Return) |
| `POST /returns/{id}/exchange { lines?, note? }` | `returns.return.exchange` | Inspected to Exchanged (kind Exchange) |
| `POST /returns/{id}/reject-refund { reason }` | `returns.return.reject` | Inspected to RefundRejected: nothing is refunded or exchanged |
| `POST /returns/{id}/notes { text }` | `returns.return.manage` | Internal note |
| `POST /returns/bulk { action: "approve"\|"reject", ids[], reason? }` | `returns.return.approve` | Up to 100; per-return result `{ id, number, ok, code, message }`; reject also needs `returns.return.reject`, checked per return |

All action endpoints answer the updated `ReturnDetailDto`.

`ReturnDetailDto`: identity (`id, number, branchId, branchCode, orderId, orderNumber, customerId, customerMobile, customerName`), `kind, status, fault, reasonCode, reasonText, rejectReason`, the timestamps (`requestedAt, approvedAt, rejectedAt, pickupScheduledAt, pickedUpAt, receivedAt, inspectedAt, refundedAt, exchangedAt, cancelledAt`), `pickup { date, courier, note }`, `refund { items, deliveryCharge, returnCharge, total, final }`, `refundPaid { id, amount, method, status, reference, actorName, at }`, `replacementOrderId`, `replacementOrderNumber`, `items[]`, `photos[]`, `timeline[]` (with staff names), `notes[]`, `order { status, paymentStatus, deliveryStatus, paymentMethod, grandTotal, deliveryCharge, deliveredAt }`, `nextStatuses[]`.

`items[]` (`ReturnItemDto`): `id, orderItemId, variantId, productId, name, sku, colorName, size, imageUrl, quantity, unitPrice, lineRefund` (worked out at request), `refundAmount` (what this line really refunds: `lineRefund`, or 0 when not accepted at inspection), `exchangeVariantId, exchangeSku, condition` (`Unopened, Good, Damaged`), `restock`, `restocked`, `refundAccepted`.

`inspect` body: `{ items: [ { returnItemId, condition, restock, acceptRefund? } ], fault?: "Customer"|"Seller", note? }`. One result per line is required (`GENERAL.VALIDATION` otherwise); a `Damaged` line cannot be `restock: true`; `acceptRefund` defaults to true.

`exchange` body: `{ lines?: [ { returnItemId, variantId } ], note? }` overrides the variants the customer asked for; lines not accepted at inspection get no replacement.

## 3. State machine

| From | To | Who | Order effect |
| --- | --- | --- | --- |
| Requested | Approved | staff `approve` | order Delivered to ReturnInProgress (delivery track stays Delivered) |
| Requested | Rejected | staff `reject` | none |
| Requested, Approved | Cancelled | customer | if no other return is open and something stays with the customer: order ReturnInProgress back to Delivered |
| Approved | PickupScheduled | staff `schedule-pickup` | none |
| PickupScheduled | PickedUp | staff `picked-up` | none |
| PickedUp | Received | staff `received` | none |
| Received | Inspected | staff `inspect` | restock of good lines; refund fixed |
| Inspected | Refunded | staff `refund` (kind Return) | payment PartiallyRefunded or Refunded; order Returned when every unit is covered |
| Inspected | Exchanged | staff `exchange` (kind Exchange) | replacement order placed; order Returned when every unit is covered |
| Inspected | RefundRejected | staff `reject-refund` | order back to Delivered when no return is open and units remain with the customer |

Rejected, Refunded, Exchanged, RefundRejected and Cancelled are final. Every other pair is refused with 409 `RETURN.INVALID_TRANSITION`. Every move writes an append-only history row (who, when, from, to, reason) and raises `ReturnStatusChanged` (outbox type `domain.ReturnStatusChanged`).

The order is "Returned" when every unit of every line is covered by a Refunded or Exchanged return (delivery track Returned too). A partly returned order stays Delivered (or ReturnInProgress while a return is open); the customer can file further requests for the rest inside the window.

## 4. Refund maths

Per line, at request time (stored as `lineRefund`):

```
discount     = min(subtotal, order.couponDiscount + order.manualDiscount)
netLine      = round(line.price x line.qty x (subtotal - discount) / subtotal, 2)
lineRefund   = round(netLine x returnedQty / boughtQty, 2)      // partial
             = netLine - already refunded for the line           // the last units take the remainder
```

At inspection (final fault decided by the inspector, default from the reason):

```
itemsRefund          = sum of lineRefund of accepted lines
deliveryChargeRefund = fault Seller AND Returns.RefundDeliveryOnSellerFault AND itemsRefund > 0
                       AND the delivery charge was not refunded by another return of this order
                       ? order.deliveryCharge : 0
returnCharge         = fault Customer ? Returns.CustomerFaultReturnCharge : 0     (never more than the money to give back)
refundTotal          = max(0, itemsRefund + deliveryChargeRefund - returnCharge)
```

An exchange refunds nothing. `POST /returns/{id}/refund` takes `amount` optionally; when sent it must equal `refundTotal` (409 `RETURN.REFUND_AMOUNT_MISMATCH`).

Example: order of 3 tees at BDT 300 and 1 cap at BDT 100 (subtotal 1,000), coupon discount 100, delivery charge 60, grand total 960. Tee line net = 900 x 0.9 = 810 (270 per unit), cap net = 90.

- Customer returns 1 tee, reason "changed my mind" (customer): items 270, delivery 0, return charge 0 (default), refund 270.
- Customer returns 1 tee, reason "damaged" (seller): items 270, delivery 60, refund 330. A second seller-fault return of the same order gets no delivery refund again.
- With `Returns.CustomerFaultReturnCharge = 50`, the first case refunds 220.
- Three single-tee returns refund 270 + 270 + 270 = 810 exactly.

Settings (table `PlatformSettings`, defaults when a row is missing): `Returns.WindowDays` = 7; `Returns.CustomerFaultReturnCharge` = 0; `Returns.RefundDeliveryOnSellerFault` = true.

## 5. Order, payment and stock effects

- Approve moves the order Delivered to ReturnInProgress (history recorded, `OrderStatusChanged` raised). When every unit is covered by a completed return the order moves to Returned and the delivery track to Returned. A cancelled, rejected or partial return that leaves units with the customer puts the order back to Delivered (a compensating step, written to the history; it is not in the normal order state machine).
- Payment track: a refund moves the payment to PartiallyRefunded, and to Refunded once every unit was refunded (a delivery charge the shop kept does not block it). Cash on delivery payments (CollectedByCourier, Settled) can be refunded the same way. A refund needs a received payment (`RETURN.PAYMENT_NOT_RECEIVED`) and the refunds of an order may never exceed its total. This is record only: the `ReturnRefund` row (amount, method `OriginalPayment|Cash|BankTransfer|Wallet`, status, reference, who, when; append-only, one per return enforced by a unique index) is what Step 9's gateway adapter will act on.
- Stock: at inspection, lines with `restock: true` that track stock go back on the shelf of the return's branch (the order's fulfilling branch) with one append-only `StockMovement` per variant (reason `CustomerReturn`, reference type `Return`). Damaged lines are recorded with condition `Damaged` and are not restocked. Never twice (`restocked` flag).
- Exchange: completing it places a new order for the customer with the same delivery address, source Other, zero amounts, payment method COD (nothing to collect), routing rule `Exchange`, in the same branch, and reserves its stock there. It is a normal order from then on (confirm, pack, courier). If a replacement is not in stock the call is refused (`RETURN.REPLACEMENT_UNAVAILABLE`) and nothing changes: restock, or reject the exchange and refund instead.

## 6. Permissions and error codes

Permissions (catalog `returns.return.*`): `view`, `approve`, `reject`, `manage`, `receive`, `inspect`, `refund`, `exchange`. Default roles: Super Admin all; Manager `returns.*`; Order Manager `returns.*` except `refund`; Support `view`, `approve`; Accountant `view`, `refund`; Inventory Manager `view`, `receive`, `inspect`. (Roles created before this slice keep their old grants; add the new codes in the role screen.)

| Code | HTTP | Meaning |
| --- | --- | --- |
| `RETURN.NOT_FOUND` | 404 | No such return, or not yours / not in your branch |
| `RETURN.INVALID_TRANSITION` | 409 | The state machine does not allow this move |
| `RETURN.ORDER_NOT_ELIGIBLE` | 409 | The order was not delivered |
| `RETURN.WINDOW_EXPIRED` | 409 | More than `Returns.WindowDays` after delivery |
| `RETURN.QUANTITY_EXCEEDED` | 409 | More than bought minus already in returns |
| `RETURN.ITEM_NOT_RETURNABLE` | 409 | A product is flagged non-returnable |
| `RETURN.ALREADY_REFUNDED` | 409 | The return already has a refund |
| `RETURN.REFUND_AMOUNT_MISMATCH` | 409 | `amount` differs from the server's figure, or refunds would exceed the order total |
| `RETURN.NOTHING_TO_REFUND` | 409 | Inspection accepted no line (reject the refund instead) |
| `RETURN.WRONG_KIND` | 409 | Refund of an exchange, or exchange of a return |
| `RETURN.PAYMENT_NOT_RECEIVED` | 409 | The order has no received payment to refund |
| `RETURN.REPLACEMENT_UNAVAILABLE` | 409 | The replacement variant is not active, not of the same product, or not in stock |
| `GENERAL.VALIDATION` | 400 | Bad body: unknown reason, quantity below 1, missing reference, bad pickup date, bad picture, incomplete inspection... |
| `GENERAL.IDEMPOTENCY_KEY_REUSED` | 409 | The key belongs to a request for another order |
| `ORDER.NOT_FOUND` | 404 | Eligibility or request for an order that is not the customer's |

Catalog: `SaveProductRequest.nonReturnable` (optional; null keeps the stored value) and `ProductDetailDto.nonReturnable` mark items that cannot be returned (FR-L08).
