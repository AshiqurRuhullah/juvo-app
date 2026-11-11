# Couriers, shipments, real-time and COD settlement API (Step 10-a backend)

Contract for module H (couriers, shipments, cash on delivery settlement), module J (tracking and real-time) and the courier part of module L (return pickup). Base path `/api/v1`. Errors are RFC 9457 ProblemDetails with `code` and `correlationId`. Lists use `page, pageSize (max 100), sort, search` and answer `{items,total,page,pageSize}`. Enums travel as text. Money is BDT, `decimal(18,2)`. Decisions and reasons: `docs/assumptions.md` section S.

## 1. The shape of the flow

1. Staff **pack** the order (`POST /orders/{id}/pack`, unchanged). The order is `Packed`, delivery track `ReadyToShip`.
2. **Book**: `GET /shipments/orders/{orderId}/suggest` shows the couriers checked against the order (eligible first, the first is `recommended`); `POST /shipments/orders/{orderId}/book` books one (the recommended unless `courierId` is given). The parcel stores the pickup address of the **fulfilling branch** (the branch pickup address, else its address line, else its name), the customer's delivery address and the cash to collect (`grandTotal - advancePaid` for an unpaid cash on delivery order, otherwise 0). The order stays `Packed` until the courier reports pickup. `POST /shipments/bulk-book` books up to 50 orders.
3. The courier reports progress by **webhook** (`POST /webhooks/couriers/{provider}`), by the **polling job** (a safety net) or, for the sandbox courier, when staff press **advance** (`POST /shipments/{id}/advance`). All three go through one path (section 6), so every report is idempotent by its event id and drives the order the same way.
4. Order and delivery tracks follow (section 5): `PickedUp` becomes order `HandedToCourier`, and so on to `Delivered`. Every move writes the immutable order history (actor = the courier's name), raises the domain events and sends the customer messages through the Step 9 templates.
5. **Delivered** cash on delivery orders become payment `CollectedByCourier`; the parcel records `codCollectedAmount`. **Settlement batches** later reconcile what each courier paid over (section 8).
6. **Failed delivery**: `DeliveryFailed` (reason and attempt count), then a retry (`OutForDelivery` again) or **return to sender** (`Returned`, stock goes back on the shelf).
7. **Returns**: `POST /returns/{id}/schedule-pickup` with `"useCourier": true` books a reverse parcel; the courier's events move the return to `PickedUp` and `Received` (section 9).
8. The browser follows live through the SignalR hub (section 10).

## 2. Endpoints

All staff endpoints need a staff token and the permission shown; services prove the permission again in the parcel's own branch (a parcel of another branch is `404 SHIPMENT.NOT_FOUND`, audit logged).

| Method and path | Permission | Purpose |
| --- | --- | --- |
| `GET /couriers` | `couriers.courier.view` | Couriers with rates, switches, open parcel count. |
| `GET /couriers/providers` | `couriers.courier.view` | Adapters (`SANDBOX` and the six slots) and whether each is set up. |
| `GET /couriers/{id}` | `couriers.courier.view` | One courier. |
| `POST /couriers` | `couriers.courier.edit` | Add a courier (201). |
| `PUT /couriers/{id}` | `couriers.courier.edit` | Change it. |
| `PUT /couriers/{id}/enabled` | `couriers.courier.edit` | Switch on or off body `{isEnabled}`. A slot without keys: `409 COURIER.NOT_CONFIGURED`. |
| `DELETE /couriers/{id}` | `couriers.courier.edit` | Archive (204). Refused while parcels are with it. |
| `GET /shipments` | `couriers.shipment.view` | List. Filters `status, courierId, branchId, kind (Forward\|Return), orderId, codOnly, open, from, to`; `search` (consignment, order number, recipient name or phone); `sort` `bookedAt` (default newest), `status`, `order`. |
| `GET /shipments/{id}` | `couriers.shipment.view` | Detail with all events. |
| `GET /shipments/{id}/events` | `couriers.shipment.view` | The immutable event log, oldest first. |
| `GET /shipments/{id}/label` | `couriers.shipment.print-label` | Label data (JSON, the screen prints it). |
| `GET /shipments/orders/{orderId}/suggest` | `couriers.shipment.book` | Courier suggestions, optional `?weightGrams=`. |
| `POST /shipments/orders/{orderId}/book` | `couriers.shipment.book` | Book one order (201). Body `BookShipmentRequest`. |
| `POST /shipments/bulk-book` | `couriers.shipment.bulk-book` | Body `{orderIds (1-50), courierId?}`. |
| `POST /shipments/{id}/cancel` | `couriers.shipment.cancel` | Cancel a booking not yet picked up. Body `{reason}` (required, 300). |
| `POST /shipments/{id}/rebook` | `couriers.shipment.book` | Cancel a booked parcel (if still booked) and book again. |
| `POST /shipments/{id}/advance` | `couriers.shipment.simulate` | Sandbox courier only: move the parcel along. |
| `GET /couriers/cod-settlements` | `couriers.cod.view` | Batches. Filters `status, courierId, branchId`; `search` number or reference. |
| `GET /couriers/cod-settlements/pending` | `couriers.cod.view` | `?courierId=&branchId=`: delivered cash parcels in no batch yet. |
| `GET /couriers/cod-settlements/{id}` | `couriers.cod.view` | One batch with its parcels. |
| `POST /couriers/cod-settlements` | `couriers.cod.settle` | Make a batch (201). |
| `POST /couriers/cod-settlements/{id}/reconcile` | `couriers.cod.settle` | Enter what the courier paid. |
| `POST /couriers/cod-settlements/{id}/settle` | `couriers.cod.settle` | Close it. |
| `POST /couriers/cod-settlements/{id}/cancel` | `couriers.cod.settle` | Drop an open or reconciled batch. |
| `POST /webhooks/couriers/{provider}` | public, signature | Courier webhook (section 6). Rate limited (storefront policy). |
| `POST /realtime/token` | any signed-in staff or customer | Hub token (section 10). |
| `POST /store/orders/track/realtime-token` | public, proof of the order | Hub token for one order (guest). Rate limited (checkout policy). |
| `POST /store/orders/track/cancel` | public, proof of the order | Guest cancel (section 11). Rate limited (checkout policy). |
| `POST /orders/manual/quote` | `orders.order.create` | Desk preview (section 11). |

Existing endpoints that changed: `POST /returns/{id}/schedule-pickup` (new optional fields), `GET`/`POST /store/orders/track` and `GET /me/orders/{id}` (new `shipment` field), the delivery quote (per-courier override, section 3), and the notification templates (two new codes).

## 3. Couriers

`CourierDto`:

```json
{ "id": "…", "code": "SANDBOX", "name": "Sandbox courier", "provider": "SANDBOX", "providerName": "Sandbox courier", "providerConfigured": true,
  "isEnabled": true, "priority": 100, "codSupported": true, "supportsInternational": true, "minWeightGrams": null, "maxWeightGrams": null,
  "supportedZoneCodes": [], "chargeOverride": null, "baseRate": 60.00, "perExtraKgRate": 20.00, "baseRateUpToGrams": 1000,
  "trackingUrlTemplate": null, "notes": "…", "settings": {}, "isSimulator": true, "openShipments": 3, "createdAt": "…" }
```

`SaveCourierRequest` (create and update; `code` only on create, 2-30 of `A-Z 0-9 _ -`): `name, provider, isEnabled, priority (1-1000, lower first), codSupported, supportsInternational, minWeightGrams, maxWeightGrams, supportedZoneCodes[] (delivery zone codes; empty = every zone), chargeOverride, baseRate, perExtraKgRate, baseRateUpToGrams (default 1000), trackingUrlTemplate (https, `{consignment}` placeholder), notes, settings {key: value}`.

- `baseRate`, `perExtraKgRate`, `baseRateUpToGrams`: what the courier charges the **shop** (`baseRate` up to `baseRateUpToGrams`, plus `perExtraKgRate` per started kilogram above). Used to rank suggestions and stored as the parcel `cost`.
- `chargeOverride`: the one number that changes what the **customer** pays: the first switched-on courier (lowest `priority`, then name) with an override replaces the zone's base and weight fees of every domestic zone with this flat fee; the remote surcharge and the free-delivery threshold still apply. The quote, cart and order use the same number (BR-G2). Null (default): the zone rates apply.
- `settings`: non-secret values only (store id, area code). A key that looks like a secret (`secret, password, token, apikey, private, credential`) is refused with `COURIER.SETTING_INVALID`. Credentials are never stored or shown: the real adapters read them from configuration (Step 14).
- Seeded (only while no courier exists): `SANDBOX` on; `PATHAO, STEADFAST, REDX, PAPERFLY, ECOURIER, DHL` off with rates 0 (slots; they answer `COURIER.NOT_CONFIGURED` to every call and cannot be switched on).

## 4. Shipments

`BookShipmentRequest`: `{ courierId?, weightGrams? (default the order's weight, else 500), lengthCm?, widthCm?, heightCm? (1-300), note? (500) }`.

`ShipmentDto` (detail; the list item has the same fields minus addresses, events and flags):

```json
{ "id": "…", "orderId": "…", "orderNumber": "JV-HO-2611-000123", "branchId": "…", "courierId": "…", "courierCode": "SANDBOX", "courierName": "Sandbox courier",
  "kind": "Forward", "returnRequestId": null, "status": "Booked", "consignmentId": "SBX-1A2B3C4D01", "providerReference": "…", "trackingUrl": "https://…",
  "weightGrams": 600, "lengthCm": null, "widthCm": null, "heightCm": null, "codAmount": 1060.00, "codCollectedAmount": null, "codCollectedAt": null,
  "codSettlementId": null, "cost": 60.00, "pickupName": "Head Office", "pickupPhone": null, "pickupAddress": "Head Office",
  "deliveryName": "Nadia Rahman", "deliveryPhone": "+8801712345678", "deliveryAddress": "House 12, Road 5, Dhanmondi, Dhaka, Dhaka", "deliveryDistrict": "Dhaka",
  "note": null, "attempts": 0, "failureReason": null, "lastRawStatus": "booked", "bookedAt": "…", "pickedUpAt": null, "deliveredAt": null, "returnedAt": null,
  "cancelledAt": null, "cancelReason": null, "canCancel": true, "canSimulate": true, "nextStatuses": ["PickedUp", "Cancelled"],
  "events": [ { "id": "…", "providerEventId": null, "rawStatus": "booked", "status": "Booked", "isStatusChange": true, "description": "Booked with Sandbox courier. Consignment SBX-1A2B3C4D01.",
                "location": null, "occurredAt": "…", "source": "Staff", "actorName": "Juvo Super Admin" } ] }
```

`canCancel` is true only while `Booked`. `canSimulate` is true for an open parcel of the sandbox courier. `source` is `Staff | Webhook | Polling | Sandbox | System`.

`CourierSuggestionDto` (suggest): `{courierId, code, name, provider, eligible, reason, cost, daysMin, daysMax, recommended}`. A courier is eligible when it is switched on, its adapter is set up, it takes cash on delivery when cash is due, the weight is within its limits, a domestic parcel's zone is in its zone list (international parcels need `supportsInternational`) and the adapter's own serviceability check passes. Order of eligible ones: `priority`, then `cost`, then name.

Booking errors: `409 SHIPMENT.ORDER_NOT_READY` (order not Packed), `409 SHIPMENT.ALREADY_BOOKED` (an open parcel exists; also when two bookings race: exactly one wins), `409 COURIER.NO_COURIER_AVAILABLE`, and for a courier chosen by hand the reason code (`COURIER.DISABLED`, `COURIER.NOT_CONFIGURED`, `COURIER.COD_NOT_SUPPORTED`, `COURIER.WEIGHT_NOT_ALLOWED`, `COURIER.NOT_SERVICEABLE`).

`BulkBookResult`: `{succeeded, failed, results: [{orderId, orderNumber, ok, shipmentId, consignmentId, errorCode, message}]}`. Each order is handled on its own.

**Cancel** (`{reason}`): only `Booked`; the courier is told; the order stays `Packed` and can be booked again. Cancelling the **order** while its parcel is only booked cancels the booking too (the order cancel never waits for the courier). **Re-book** (`{courierId?, weightGrams?, lengthCm?, widthCm?, heightCm?, note?, reason?}`): cancels a booked parcel (or starts from an already cancelled one) and books again; once the courier has picked the parcel up: `409 SHIPMENT.CANNOT_CANCEL`.

**Label** (`ShipmentLabelDto`): `{shipmentId, consignmentId, barcode, courierName, orderNumber, pickupName, pickupPhone, pickupAddress, deliveryName, deliveryPhone, deliveryAddress, deliveryDistrict, weightGrams, codAmount, items, note, trackingUrl, providerUrl, bookedAt, isReturn}`. `barcode` is the text to encode (Code 128 is fine). The screen prints it; no PDF.

**Advance (sandbox)**: body `{status?, note?}`. Without `status` the next step of a normal journey is taken (`picked_up`, `in_transit`, `out_for_delivery`, `delivered`; after `delivery_failed` the next step is the retry `out_for_delivery`). Or give one of `picked_up, in_transit, at_hub, out_for_delivery, delivered, delivery_failed, returned`. Answers the new `ShipmentDto`. Other couriers: `409 COURIER.NOT_SIMULATOR`; a finished parcel: `409 SHIPMENT.INVALID_TRANSITION`.

## 5. Status mapping

Shipment moves (anything else is refused or recorded as a note): `Booked` to `PickedUp` or `Cancelled`; `PickedUp` to `InTransit`; `InTransit` to `OutForDelivery`; `OutForDelivery` to `Delivered` or `DeliveryFailed`; `DeliveryFailed` to `OutForDelivery` (retry, attempts +1) or `Returned`. `Delivered`, `Returned`, `Cancelled` are final. A courier that skips scans is handled: a report of a later status is expanded into the steps in between (events with `rawStatus: "inferred"`, timestamps a millisecond apart so the timeline keeps its order).

| Shipment status | Order track (forward parcel) | Delivery track | Also |
| --- | --- | --- | --- |
| `Booked` | unchanged (`Packed`) | `ReadyToShip` | |
| `PickedUp` | `HandedToCourier` | `HandedToCourier` | customer SMS `order.handed-to-courier` |
| `InTransit` | `InTransit` | `InTransit` | |
| `OutForDelivery` | `OutForDelivery` | `OutForDelivery` | SMS `order.out-for-delivery` |
| `Delivered` | `Delivered` | `Delivered` | cash on delivery: payment `CollectedByCourier`, parcel `codCollectedAmount`; stock deducted now when `Inventory.DeductAt = Delivery`; SMS `order.delivered` |
| `DeliveryFailed` | `DeliveryFailed` | `Failed` | `failureReason`, `attempts`; SMS `order.delivery-failed` |
| `Returned` (to sender) | `Returned` | `Returned` | stock back on the shelf (restock when it had left it, release when only reserved); internal note to refund prepaid money; SMS `order.returned-to-sender` |
| `Cancelled` | unchanged (`Packed`) | `ReadyToShip` | |

The order moves through the normal `OrderWorkflow` transitions only. If staff already recorded a step by hand, the courier's report for it changes nothing; a step the order cannot take is written on the parcel timeline as a system note (the parcel still moves). The manual endpoint `POST /orders/{id}/transition` keeps working for orders without a parcel.

Sandbox raw status to shipment status: `booked` Booked; `picked_up` PickedUp; `in_transit` InTransit; `at_hub` (a scan, no status change); `out_for_delivery` OutForDelivery; `delivered` Delivered; `delivery_failed` DeliveryFailed; `returned` Returned; `cancelled` Cancelled.

## 6. Webhook, polling and idempotency

`POST /webhooks/couriers/{provider}` (`SANDBOX` is the only working provider; the slots answer `409 COURIER.NOT_CONFIGURED`, an unknown provider `404`).

Signature header (same scheme as payment webhooks): `Juvo-Signature: t=<unix seconds>,v1=<lowercase hex HMAC-SHA256 of "<t>.<raw body>" with the shared secret>`. The secret is `Couriers:Sandbox:WebhookSecret` (empty: a random one for the life of the process). The signature is compared in constant time; a timestamp more than 5 minutes from now (past or future) is refused. Answers: `401 COURIER.INVALID_SIGNATURE`, `400 COURIER.WEBHOOK_EXPIRED`, `400 COURIER.WEBHOOK_MALFORMED`.

Sandbox body:

```json
{ "eventId": "evt-1", "consignmentId": "SBX-1A2B3C4D01", "status": "picked_up", "occurredAt": "2026-11-11T09:30:00Z", "location": "Dhaka hub", "note": "…", "codCollected": 1060.00 }
```

`eventId` (up to 120) is unique per report at the courier. Answer `200 {status, shipmentStatus}` with `status` one of `processed` (applied), `duplicate` (the event id was seen: nothing changed), `recorded` (written on the timeline without a status change: a scan, or a report that does not fit, for example "in transit" after "delivered"), `ignored` (unknown consignment: acknowledged so the courier stops retrying). Two identical webhooks at the same moment apply the event once (unique index on shipment and event id); the loser may get `409` and its retry is a `duplicate`.

**Polling job** (`CourierStatusSyncWorker`, `Couriers:Sync:Enabled` default true, `IntervalSeconds` 60, `BatchSize` 200): asks every adapter about the open parcels (the one checked longest ago first, `LastPolledAt`) and applies the answer through the same path, with source `Polling`. The sandbox answers with its current status, event id `consignment:version`, so a report already applied by a webhook or by staff is a duplicate. A sweep that changed something or failed is written to the job log (`couriers.status-sync`). Turn it off in tests with `Couriers:Sync:Enabled=false`.

## 7. Settings and operations

| Setting | Default | Meaning |
| --- | --- | --- |
| `Couriers:Sandbox:Enabled` | true | The sandbox courier adapter. |
| `Couriers:Sandbox:WebhookSecret` | random per process | Signs sandbox webhooks. |
| `Couriers:Sandbox:TrackingUrlTemplate` | `https://sandbox.courier.invalid/track/{consignment}` | A reserved domain that never resolves: the sandbox has no public tracking page. |
| `Couriers:Sync:Enabled / IntervalSeconds / BatchSize` | true / 60 / 200 | The polling job. |
| `Auth:RealtimeTokenMinutes` | 5 | Hub token lifetime. |

## 8. COD settlement

A batch gathers the delivered cash on delivery parcels of **one courier in one branch** whose cash is still `CollectedByCourier` and that are in no open batch. Create: `POST /couriers/cod-settlements` body `{courierId, branchId? (default: the active branch, else the default branch), shipmentIds? (default: every waiting parcel), note?}`. Number `CS-<branch>-<yyMM>-<6 digits>` (gap-free per branch and month).

`SettlementDto`: `{id, number, branchId, courierId, courierName, status (Open|Reconciled|Settled|Cancelled), expectedTotal, receivedTotal, difference, feesTotal, reference, note, createdByName, createdAt, reconciledAt, reconciledByName, settledAt, settledByName, cancelledAt, shortParcels, overParcels, items: [{shipmentId, orderId, orderNumber, consignmentId, expected, received, difference, courierFee, note}]}`.

Maths: `expected` of a parcel = what the courier reported collecting (`codCollectedAmount`, else the cash to collect). `difference` = `received - expected` (negative is short, positive is over), per parcel and for the batch (`difference` = `receivedTotal - expectedTotal`). `feesTotal` = the sum of the parcels' `cost`; it is recorded for the books and **not** deducted (the courier's invoice is settled separately). Example: three parcels expecting 1,060.00, 1,560.00 and 2,060.00 (total 4,680.00). The courier pays 1,060.00, 1,510.00 ("kept 50 for change") and 2,080.00 ("customer tipped 20"): `receivedTotal` 4,650.00, `difference` -30.00, `shortParcels` 1, `overParcels` 1, parcel differences 0, -50.00, +20.00.

- `POST …/{id}/reconcile` body `{lines: [{shipmentId, received, note}]}`: a parcel left out counts as paid in full; `received` is 0 or more with at most 2 decimals (`409 SHIPMENT.AMOUNT_INVALID`); a short or over parcel needs a `note` (up to 300; `409 SHIPMENT.NOTE_REQUIRED`). Repeatable until settled.
- `POST …/{id}/settle` body `{reference}` (the deposit or transfer reference, required): only a reconciled batch (`409 SHIPMENT.SETTLEMENT_STATE` otherwise). Every order whose cash is still `CollectedByCourier` becomes `Settled` on the payment record (reference `<number>/<reference>`) and on the order's payment track (history reason `Settlement CS-… (reference)`), exactly like the single `POST /payments/orders/{orderId}/cod-settle` of Step 9. The differences stay on the batch and on the payment record's event text.
- `POST …/{id}/cancel`: an open or reconciled batch; its parcels become available again.
- Errors: `409 SHIPMENT.NOTHING_TO_SETTLE`, `404 SHIPMENT.SETTLEMENT_NOT_FOUND`.

## 9. Returns pickup through a courier

`POST /returns/{id}/schedule-pickup` body `{date, courier?, note?, useCourier?, courierId?}`. With `useCourier: true` (or a `courierId`) the system books a **reverse parcel** (`kind: Return`, `returnRequestId` set): pickup at the customer's delivery address, delivery to the order's branch, cash 0, weight from the returned lines. The return's `pickup.courier` is the courier's name. Errors are the booking errors of section 4 (for example `COURIER.NO_COURIER_AVAILABLE`); nothing is saved then. Without the flag the manual path works exactly as before.

Courier events on the reverse parcel: `PickedUp` moves a `PickupScheduled` return to `PickedUp`; `Delivered` (arrival at the branch) moves a `PickedUp` return to `Received` (the usual `ReturnOrderSync` then runs). `InTransit`, `OutForDelivery`, failed attempts and `Returned` only appear on the parcel timeline; a report that does not fit the return's state is noted on the parcel and changes nothing. Find the parcel with `GET /shipments?orderId=…&kind=Return`. The order's own tracks are not driven by the reverse parcel. Return notifications are unchanged (`return.pickup-scheduled`, `return.received`).

## 10. Real-time hub

### Why a token endpoint

The front end keeps its tokens in httpOnly cookies behind the BFF; a browser WebSocket cannot send them and must not hold the access token. So: the BFF asks the API for a **hub token** with the user's access token (or, for a guest, with the tracking proof), returns it to the page, and the page connects to the API's hub directly with that token. The hub token is a JWT with audience `<Auth:Audience>:realtime` (same issuer and key), `scope=realtime`, valid 5 minutes (`Auth:RealtimeTokenMinutes`): it opens only the hub, and an access token opens nothing there.

| Call | Who | Answer |
| --- | --- | --- |
| `POST /api/v1/realtime/token` | signed-in staff or customer (not a limited token) | `{token, expiresAt, hubPath: "/hubs/orders", role: "staff"\|"customer", orderId: null}` |
| `POST /api/v1/store/orders/track/realtime-token` | guest; body `{number, mobile}` or `{token}` (the tracking token) | same, `role: "track"`, `orderId` = the one order; `404 ORDER.NOT_FOUND` for any wrong proof |

The token endpoint for a person is called by the BFF with `Authorization: Bearer <access token>`; the BFF route (for example `POST /api/bff/realtime/token`) returns `{token, expiresAt, hubUrl}` where `hubUrl` = API origin + `hubPath`. Call it again whenever the connection has to be made again (the client's `accessTokenFactory` should call the BFF each time), and when a staff member's roles change (a hub token carries the permission version: joins are refused with `REALTIME.FORBIDDEN` after a role, branch or active change, and the new token has the new version).

### Connecting (browser, `@microsoft/signalr` client, MIT)

```ts
const conn = new HubConnectionBuilder()
  .withUrl(hubUrl, { accessTokenFactory: async () => (await fetch('/api/bff/realtime/token', { method: 'POST' }).then(r => r.json())).token })
  .withAutomaticReconnect().build();
conn.on('ShipmentEventAdded', e => { /* update the timeline; ignore an event you already have */ });
await conn.start();
await conn.invoke('JoinOrder', orderId);        // customer, guest or staff
await conn.invoke('JoinBranch', branchId);      // staff: a branch id, or 'all' (global staff only)
conn.onreconnected(async () => { await conn.invoke('JoinOrder', orderId); /* groups are lost on reconnect */ });
```

The CORS policy of the API already allows the front-end origin, `Authorization` and the SignalR headers. WebSockets are used when available (long polling otherwise); for a WebSocket the token travels as `?access_token=` on the hub path only. A connection that is already open keeps working after its token ended (the token is checked when connecting); the permission checks happen at every `Join*` call.

### Hub methods

| Method | Who | Returns | Refused with |
| --- | --- | --- | --- |
| `JoinOrder(orderId)` | a customer: their own orders; a guest: the one order of the token; staff: orders of branches where they hold `orders.order.view` | the group name | `HubException` message `REALTIME.FORBIDDEN` (the same for "not yours" and "does not exist") |
| `LeaveOrder(orderId)` | all | | |
| `JoinBranch(branch)` | staff only; `branch` = a branch id where they hold `orders.order.view`, or `all` (global scope only) | the group name | `REALTIME.FORBIDDEN`; a text that is neither a branch id nor `all`: `GENERAL.VALIDATION` |
| `LeaveBranch(branch)` | staff | | |

### Events (server to client, camelCase JSON)

Every event is published from the outbox handlers, so delivery is **at-least-once** (a client may get the same event twice and after a reconnect should reload the order) and arrives within the outbox poll interval after the change. The order group gets the **customer-safe** payload (no staff names, no internal reasons, no raw courier text); the branch groups get the **staff** payload (extra fields marked below).

| Event | Group | Payload |
| --- | --- | --- |
| `OrderStatusChanged` | order, branch | `{orderId, orderNumber, track: "Order"\|"Delivery", from, to, at}` (+ staff: `reason`) |
| `PaymentStatusChanged` | order, branch | `{orderId, orderNumber, from, to, at}` (+ staff: `reason`) |
| `ShipmentEventAdded` | order (forward parcels only), branch | customer: `{shipmentId, orderId, orderNumber, status, isStatusChange, location, trackingUrl, courierName, consignmentId, at}`; staff also `kind, returnRequestId, rawStatus, description` |
| `ReturnStatusChanged` | order, branch | `{returnId, returnNumber, orderId, orderNumber, kind, from, to, at}` (+ staff: `reason`) |
| `NewOrder` | branch only | `{orderId, orderNumber, branchId, total, source, at}` |

`status`, `from`, `to` are the enum names of the order, payment, delivery or shipment status. A staff member in a branch group also sees the events of orders they did not join; a global member of `all` sees every branch. Stock and alert events are not part of this slice.

## 11. Contract gaps closed in this step

**Desk preview** `POST /orders/manual/quote` (permission `orders.order.create`; the discount and delivery override need their own permissions, exactly as when creating). Body = `ManualOrderRequest` (the same as `POST /orders/manual`). Nothing is saved, reserved or counted (stock, coupon use, order numbers, customers). Same checks and calculation as create (it is the same pricing code), so the totals equal the created order's. Answer `ManualQuoteDto`:

```json
{ "currency": "BDT", "subtotal": 2100.00, "couponDiscount": 210.00, "couponCode": "DESK10", "manualDiscount": 50.00, "deliveryCharge": 70.00, "standardDeliveryCharge": 70.00,
  "grandTotal": 1910.00, "advanceRequired": 0.00, "weightGrams": 900, "zoneCode": "DHAKA", "paymentMethod": "COD", "branchId": "…", "routingRule": "Staff",
  "lines": [ { "variantId": "…", "sku": "…", "name": "…", "colorName": "Red", "size": "M", "unitPrice": 700.00, "quantity": 3, "lineTotal": 2100.00, "availableInBranch": 10 } ],
  "earliestDelivery": "2026-11-12", "latestDelivery": "2026-11-14" }
```

Errors as create: `GENERAL.VALIDATION`, `ORDER.STOCK_INSUFFICIENT` (the chosen branch does not hold every line), `COUPON.*`, `ORDER.DISCOUNT_TOO_LARGE`, `ORDER.COD_NOT_AVAILABLE`, `403`. The duplicate guard, idempotency and fraud rules are decided only when the order is created.

**Guest cancel** `POST /store/orders/track/cancel` body `{number, mobile}` or `{token}`, optional `reason` (up to 300, default "Cancelled by the customer"). No OTP; the proof is the order number plus the mobile used at checkout, or the tracking token; any wrong proof is the same `404 ORDER.NOT_FOUND`. Allowed only while the order is `Placed`, `Confirmed` or `Packed` (it has not left the shop): `409 ORDER.CANNOT_CANCEL` afterwards (`HandedToCourier` and later). Reserved stock goes back at once; a parcel that is booked but not picked up is cancelled with the courier. Answers the new `TrackingDto`. It shares the checkout rate limit (`RateLimiting:CheckoutPerMinute`, 20 per minute per client); beyond it `429 GENERAL.TOO_MANY_REQUESTS` with `Retry-After`.

**Public tracking** (`POST`/`GET /store/orders/track`, and `GET /me/orders/{id}`) gain `shipment`: null until a parcel is booked, then `{courierName, consignmentId, trackingUrl, status, attempts, events: [{status, description, location, occurredAt}]}` for the newest parcel that was not cancelled. `description` is a plain line per step ("Picked up by the courier", "Out for delivery") or the courier's own scan text; staff notes, actor names and raw statuses are never in it.

## 12. Error codes

| Code | Status | Meaning |
| --- | --- | --- |
| `COURIER.NOT_FOUND` | 404 | No such courier. |
| `COURIER.CODE_TAKEN` | 409 | A courier with this code exists. |
| `COURIER.UNKNOWN_PROVIDER` | 409 | The courier's adapter does not exist. |
| `COURIER.NOT_CONFIGURED` | 409 | The adapter has no account keys (a slot): it cannot be switched on, booked or called. |
| `COURIER.DISABLED` | 409 | The courier is switched off (named by hand). |
| `COURIER.NO_COURIER_AVAILABLE` | 409 | No courier can take the parcel. |
| `COURIER.NOT_SERVICEABLE` | 409 | The courier does not serve the address or zone (or abroad). |
| `COURIER.COD_NOT_SUPPORTED` | 409 | Cash is due and the courier does not collect it. |
| `COURIER.WEIGHT_NOT_ALLOWED` | 409 | The weight is outside the courier's limits. |
| `COURIER.PROVIDER_ERROR` | 4xx | The adapter refused the call (for example the sandbox consignment is unknown or already picked up). |
| `COURIER.SETTING_INVALID` | 409 | A setting looks like a secret, is too long, too many, or the courier cannot be archived. |
| `COURIER.INVALID_SIGNATURE` | 401 | Webhook signature missing or wrong. |
| `COURIER.WEBHOOK_EXPIRED` | 400 | Webhook timestamp outside the 5 minute window. |
| `COURIER.WEBHOOK_MALFORMED` | 400 | Webhook body is not a valid report. |
| `COURIER.NOT_SIMULATOR` | 409 | Only the sandbox courier can be advanced by hand. |
| `SHIPMENT.NOT_FOUND` | 404 | No such parcel (also another branch's). |
| `SHIPMENT.ORDER_NOT_READY` | 409 | The order is not Packed. |
| `SHIPMENT.ALREADY_BOOKED` | 409 | The order already has an open parcel. |
| `SHIPMENT.INVALID_TRANSITION` | 409 | The parcel cannot take that step (finished, or changed at the same moment). |
| `SHIPMENT.CANNOT_CANCEL` | 409 | The courier has the parcel; only a booking can be cancelled or booked again. |
| `SHIPMENT.NOTHING_TO_SETTLE` | 409 | No delivered cash on delivery parcel is waiting (or one chosen is not eligible). |
| `SHIPMENT.SETTLEMENT_NOT_FOUND` | 404 | No such batch (also another branch's). |
| `SHIPMENT.SETTLEMENT_STATE` | 409 | The batch is in the wrong state (settle before reconcile, change after settled). |
| `SHIPMENT.AMOUNT_INVALID` | 409 | A received amount is negative or has more than 2 decimals. |
| `SHIPMENT.NOTE_REQUIRED` | 409 | A short or over parcel needs a note. |
| `REALTIME.FORBIDDEN` | hub | The join is refused (not yours, not in your branches, stale token, deactivated). |
| `REALTIME.TOKEN_INVALID` | hub | The connection has no usable token. |

Also used: `ORDER.CANNOT_CANCEL`, `ORDER.NOT_FOUND`, `ORDER.STOCK_INSUFFICIENT`, `GENERAL.TOO_MANY_REQUESTS`, `GENERAL.VALIDATION`, `AUTH.FORBIDDEN`, `AUTH.BRANCH_FORBIDDEN`.

## 13. Permissions

Existing codes, now used: `couriers.courier.view`, `couriers.courier.edit`, `couriers.shipment.view`, `couriers.shipment.book`, `couriers.shipment.bulk-book`, `couriers.shipment.cancel`, `couriers.shipment.print-label`, `couriers.cod.view`, `couriers.cod.settle`. New: `couriers.shipment.simulate` (sandbox advance). `couriers.courier.manage-credentials` and `couriers.webhook.view` exist and are not used yet (credentials live in configuration; no webhook log screen). Default roles (by the existing patterns): Super Admin all; Manager `couriers.*`; Order Manager `couriers.shipment.*` (including simulate), `couriers.courier.view`, `couriers.cod.view`; Accountant `couriers.cod.*`; Support `couriers.shipment.view`.
