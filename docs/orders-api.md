# Orders API (Step 8-a)

Base path `/api/v1`. Money is BDT, tax-inclusive, decimals with two places. Enums travel as text. Errors are RFC 9457 problem documents with a stable `code` (list at the end). The live typed contract is the OpenAPI document (`/scalar/v1` in development); this page explains how the parts fit together for the front end. Decisions and defaults: `docs/assumptions.md` section M.

## 1. Shop: quote, checkout, tracking (anonymous, rate limited)

A customer token is understood but not needed. A guest identifies the cart with `X-Cart-Token` (Step 7); a signed-in customer orders from their own cart (no header). `POST /store/checkout`, `/store/orders/track` and `/store/coupons/validate` use the stricter checkout rate limit.

### `POST /store/checkout/quote`

Request: `{ addressId?, address?: CheckoutAddress, paymentMethod?: "COD" | "SANDBOX", mobile? }` (`addressId` only for a signed-in customer; `address` may hold just `countryCode`, `districtId`, `upazilaId` for the delivery estimate).

Response `CheckoutQuoteDto`: `currency`, `taxIncluded`, `lines[]` (`variantId, productId, name, nameBn, sku, colorName, size, imageUrl, unitPrice, quantity, lineTotal, problem`), `itemCount`, `subtotal`, `couponCode`, `couponDiscount`, `delivery` (the Step 7 delivery quote or null), `deliveryError` (code, for example `DELIVERY.NOT_DELIVERABLE`), `deliveryCharge`, `grandTotal`, `paymentMethod`, `advanceRequired`, `payNow`, `payOnDelivery`, `otpRequired`, `paymentMethods[]` (`code, name, available, reason`), `problems[]` (`OUT_OF_STOCK`, `REDUCED`, `COUPON.EXPIRED`...), `canPlace`.

`payNow` is the whole total for an online method, the advance for a large COD order (above BDT 10,000: the courier cost, BR-D1), otherwise 0.

### `POST /store/checkout` (201)

Header `Idempotency-Key` (optional, also accepted as `idempotencyKey` in the body) makes retries safe.

```json
{
  "contact": { "mobile": "01712345678", "fullName": "Nadia Rahman", "email": null },
  "addressId": null,
  "address": { "recipientName": "Nadia Rahman", "phone": null, "countryCode": "BD", "districtId": "...", "upazilaId": null,
               "area": null, "addressLine": "House 12, Road 5, Dhanmondi", "landmark": null, "stateProvince": null,
               "city": null, "postalCode": null, "latitude": null, "longitude": null },
  "saveAddress": false,
  "paymentMethod": "COD",
  "note": "Gift wrap please",
  "acceptTerms": true,
  "otpCode": "123456",
  "confirmDuplicate": false,
  "expectedTotal": 1670.00,
  "idempotencyKey": "client-generated-unique-key",
  "deviceId": "stable-random-id-kept-in-the-browser"
}
```

Flow for a guest on COD (default `Checkout.OtpMode = CodOnly`): `POST /auth/otp/request { mobile, purpose: "ConfirmOrder" }`, then send the code as `otpCode`. A signed-in customer with a verified mobile sends no code (and `contact` is ignored in favour of their profile). `expectedTotal` is the grand total shown; a different total today answers 409 `ORDER.TOTAL_CHANGED` (quote again, show, resubmit). `confirmDuplicate: true` answers the duplicate warning.

Response `CheckoutResultDto`: `orderId, number` (`JV-HO-2611-000123`), `trackingToken` (shown once; null on a replay), `status, paymentStatus, paymentMethod, grandTotal, advanceRequired, payNow, payOnDelivery, earliestDelivery, latestDelivery, offerProfile` (true for guests: show "Create my profile"), `replayed, paymentDeadline` (online methods: when the unpaid order is cancelled). Fraud holds are never revealed to the customer.

### `POST /store/orders/track`, `GET /store/orders/track`

Body or query: `{ number, mobile }` or `{ token }`. Response `TrackingDto`: `number, status, paymentStatus, deliveryStatus, paymentMethod, placedAt, district, grandTotal, items[], timeline[], canCancel`. No street address, contact details, staff names or internal reasons. Any mismatch is 404 `ORDER.NOT_FOUND`.

## 2. Customer panel `/me/orders` (customer token)

| Method and path | Notes |
| --- | --- |
| `GET /me/orders?filter=active|delivered|cancelled|returned&page=&pageSize=` | `MyOrderListItemDto`: `id, number, status, paymentStatus, deliveryStatus, placedAt, grandTotal, itemCount, firstItemName, firstItemImageUrl, canCancel`. Newest first. |
| `GET /me/orders/{id}` | `MyOrderDto`: `money, shipping, items[], timeline[] (statuses, times, no staff names), notes[] (customer notes), canCancel, paymentDeadline`. Another customer's id is 404. |
| `POST /me/orders/{id}/cancel` body `{ reason? }` | Allowed while Placed, Confirmed or Packed; stock goes back at once. Otherwise 409 `ORDER.CANNOT_CANCEL`. |

`GET /me/summary` now fills `activeOrderCount` and `orderCount`.

## 3. Staff orders `/orders`

All endpoints need a staff token and a permission; the service proves it again in the order's own branch (an order of another branch is 404 and audit-logged, a missing permission there is 403 `AUTH.BRANCH_FORBIDDEN`). Lists follow the standard convention (`page, pageSize (max 100), sort, search`).

| Method and path | Permission | Notes |
| --- | --- | --- |
| `GET /orders` | `orders.order.view` | Filters `status, paymentStatus, deliveryStatus, branchId, source, fraudState, from, to, held`; `search` by number, mobile or name; `sort` `placedAt` (default newest), `total`, `number`, `status`. Items: `OrderListItemDto`. |
| `GET /orders/{id}` | `orders.order.view` | `OrderDetailDto`: three tracks, `money, shipping, items, timeline[] (track, from, to, reason, actorName, at), notes[], flags[], fraudLog[], customerStats, nextOrderStatuses, nextPaymentStatuses, routingRule, routingPending, stockDeducted, isHeld, fraudState, riskScore`. |
| `POST /orders/{id}/confirm` `{ advanceReceived? }` | `orders.order.confirm` | Refused: `ORDER.HELD_FOR_REVIEW`, `ORDER.PAYMENT_PENDING` (online, unpaid), `ORDER.ADVANCE_REQUIRED` (until `advanceReceived: true`). |
| `POST /orders/{id}/pack` | `orders.order.pack` | Confirmed to Packed; reserved stock leaves the shelf (`Inventory.DeductAt`). |
| `POST /orders/{id}/cancel` `{ reason }` | `orders.order.cancel` | Reason required. Placed, Confirmed or Packed only (`ORDER.CANNOT_CANCEL`). |
| `POST /orders/{id}/transition` `{ to, reason? }` | `orders.order.edit` | `to`: `HandedToCourier, InTransit, OutForDelivery, Delivered, DeliveryFailed, ReturnInProgress, Returned`, only along the state machine (`ORDER.INVALID_TRANSITION`). Couriers drive this in Step 10. |
| `POST /orders/{id}/notes` `{ kind: "Internal"|"Customer", text }` | `orders.order.note` | Customer notes are visible to the customer. |
| `POST /orders/{id}/reassign` `{ branchId, reason? }` | `orders.order.reassign-branch` | Before packing; the other branch must hold every item (`ORDER.STOCK_INSUFFICIENT`). |
| `GET /orders/{id}/packing-slip` | `orders.order.print` | `PackingSlipDto` (the UI prints it): branch, recipient and full address, customer note, lines, totals, `collectOnDelivery`, weight. |
| `POST /orders/bulk` `{ action: "confirm"|"cancel"|"print", ids[], reason?, advanceReceived? }` | `orders.order.bulk-action` (+ the single-action permission per order) | Up to 100 orders, each on its own; answer `succeeded, failed, results[] (orderId, number, ok, code, message), slips[]`. |
| `POST /orders/manual` | `orders.order.create` | Social order desk (below). |
| `GET /orders/sources/summary?from=&to=` | `orders.source.view` | Orders and value per source. |

### Social order desk `POST /orders/manual` (201, returns `OrderDetailDto`)

```json
{ "customerMobile": "01712345678", "customerName": "Nadia Rahman", "customerEmail": null,
  "source": "Facebook", "branchId": null,
  "address": { "recipientName": "...", "phone": "...", "countryCode": "BD", "districtId": "...", "addressLine": "..." },
  "lines": [ { "sku": "JV-TSH-BLA-M", "quantity": 2 }, { "variantId": "...", "quantity": 1 } ],
  "paymentMethod": "COD", "couponCode": null, "discount": 100.00, "deliveryChargeOverride": 0,
  "customerNote": null, "internalNote": null, "idempotencyKey": null, "confirmDuplicate": false }
```

`source`: `Facebook, WhatsApp, Phone, WalkIn, Other`. The customer is found or created by mobile. `discount` needs `orders.order.apply-discount`, `deliveryChargeOverride` needs `orders.order.override-delivery`. The branch is `branchId`, else the active branch, else the default branch. The order starts Placed; confirm it afterwards.

## 4. Fraud `/fraud`

| Method and path | Permission | Notes |
| --- | --- | --- |
| `GET /fraud/reviews?state=` | `fraud.flag.view` | Held orders (Flagged, InReview, AdvanceRequested), riskiest first: `ReviewItemDto` with `flags[]` and `customerStats`. |
| `POST /fraud/reviews/{orderId}/decide` `{ action: "Approve"|"RequestAdvance"|"Call"|"Reject", note? }` | `fraud.review.decide` | Reject needs a note and cancels the order. Returns the decision log (`FraudLogDto[]`). `FRAUD.NOT_WAITING` when the order is not held. |
| `GET /fraud/customers/{customerId}/stats` | `fraud.flag.view` | `totalOrders, delivered, cancelled, returnedOrFailed, open, totalSpent, firstOrderAt, score`. |
| `GET /fraud/blacklist?kind=` / `POST` `{ kind: "Mobile"|"Email"|"Address"|"Device", value, note? }` / `DELETE /{id}` | `fraud.blacklist.view / create / delete` | Mobile values are normalised to `+880...`. |
| `GET /fraud/rules` / `PUT /fraud/rules/{code}` `{ enabled, threshold, action: "Flag"|"Hold"|"RequireAdvance"|"Reject" }` | `fraud.rule.view / edit` | Seven rules; thresholds and actions live in the settings table. |

## 5. State machines

Order track (each change writes who, when, from, to, reason to the append-only history and raises `OrderStatusChanged`):

| From | To |
| --- | --- |
| Placed | Confirmed, Cancelled |
| Confirmed | Packed, Cancelled |
| Packed | HandedToCourier, Cancelled |
| HandedToCourier | InTransit |
| InTransit | OutForDelivery |
| OutForDelivery | Delivered, DeliveryFailed |
| DeliveryFailed | OutForDelivery, Returned |
| Delivered | ReturnInProgress |
| ReturnInProgress | Returned |
| Cancelled, Returned | (final) |

Payment track: Pending to Paid, Failed, Cancelled, UnderReview, CollectedByCourier; UnderReview to Paid or Rejected; Paid to PartiallyRefunded or Refunded; PartiallyRefunded to Refunded; CollectedByCourier to Settled; Failed to Pending or Cancelled. `Paid` is only set by the payment module after server-side verification (Step 9).

Delivery track (follows the order track): NotShipped (Placed, Confirmed), ReadyToShip (Packed), HandedToCourier, InTransit, OutForDelivery, Delivered (also ReturnInProgress), Failed, Returned, Cancelled.

Fraud state: None, Flagged, InReview, AdvanceRequested (all three hold the order), Approved, Rejected.

## 6. Stock effects

| Event | Stock |
| --- | --- |
| Order placed | `Reserved += qty` in the fulfilling branch (atomic conditional update), ledger `OrderReserved` (change 0) |
| Cancelled, payment timeout, reassign (old branch) | `Reserved -= qty`, ledger `OrderReleased` (change 0) |
| Packed (or Delivered with `Inventory.DeductAt = Delivery`) | `Reserved -= qty` and `OnHand -= qty`, ledger `OrderPacked` (negative) |
| Cancelled or Returned after deduction | `OnHand += qty`, ledger `OrderRestocked` (positive) |

## 7. Permissions

New: `orders.order.apply-discount`, `orders.order.override-delivery`. Used: `orders.order.view/create/edit/confirm/pack/cancel/reassign-branch/note/bulk-action/print`, `orders.source.view`, `fraud.flag.view`, `fraud.review.decide`, `fraud.blacklist.view/create/delete`, `fraud.rule.view/edit`. Defaults: Super Admin all; Manager `orders.*` and `fraud.*`; Order Manager `orders.*` except reassign, plus `fraud.flag.view`; Support `orders.order.view` and `orders.order.note`. (Roles created in an existing database keep their stored permissions; add the new codes in the role screen.)

## 8. Error codes

`ORDER.NOT_FOUND, ORDER.CART_EMPTY, ORDER.TOTAL_CHANGED, ORDER.DUPLICATE_SUSPECTED, ORDER.CHECKOUT_BLOCKED (403, neutral), ORDER.OTP_REQUIRED, ORDER.PAYMENT_METHOD_UNAVAILABLE, ORDER.COD_NOT_AVAILABLE, ORDER.HELD_FOR_REVIEW, ORDER.ADVANCE_REQUIRED, ORDER.PAYMENT_PENDING, ORDER.CANNOT_CANCEL, ORDER.BRANCH_NOT_ALLOWED, ORDER.DISCOUNT_TOO_LARGE, ORDER.NOTHING_TO_DO, ORDER.STOCK_INSUFFICIENT, ORDER.INVALID_TRANSITION, FRAUD.BLACKLIST_NOT_FOUND, FRAUD.BLACKLIST_EXISTS, FRAUD.NOT_WAITING, FRAUD.RULE_NOT_FOUND`, plus the existing `CART.*`, `COUPON.*`, `DELIVERY.*`, `AUTH.OTP_*`, `GENERAL.VALIDATION` (field errors such as `address.districtId`, `contact.mobile`, `lines[0].sku`) and `GENERAL.IDEMPOTENCY_KEY_REUSED`.

## 9. Background work

`OrderTimeoutWorker` (option `Orders:HoldJobEnabled`, default true; `Orders:HoldJobIntervalSeconds`, default 30) cancels unpaid online orders whose hold ended and releases their stock; a sweep that cancelled something writes a `JobLog` row (`orders.payment-timeout`). Order events go through the outbox to handlers that send the customer an SMS (sandbox: the notification log).
