# Public REST API, API keys and webhooks (FR-AF05)

Two surfaces, one purpose: let other systems (accounting, ERP, marketplaces, dashboards) read the shop's data and react to events, without a staff login.

* **Public read API**: `/api/public/v1/...`, authenticated by an API key. Read only. Its own OpenAPI document: `GET /openapi/public-v1.json` (the staff API stays in `/openapi/v1.json`; neither lists the other).
* **Outgoing webhooks**: Juvo sends signed `POST` requests to https addresses you register.
* **Staff management** of keys and webhooks: `/api/v1/integrations/...` (JWT, permissions below).

All money is BDT (`currency` field), tax-inclusive. No cost price, internal note, fraud data, tracking secret or device data is ever returned. Errors are RFC 9457 problem details with a stable `code` and the `X-Correlation-Id` header, like the rest of the API.

## 1. API keys

Format `jv_live_` + 8 characters + `_` + 43 characters (256 random bits). The first 16 characters (`jv_live_ab12cd34`) are the visible **prefix** shown in lists; the rest is the secret. Only the SHA-256 hash of the whole key is stored. **The key is shown once**, when it is created or rotated. Lost keys cannot be recovered: rotate.

Send it in a header (never in the URL):

```bash
curl -H "X-Api-Key: $JUVO_API_KEY" "https://<your-host>/api/public/v1/products?pageSize=10"
```

(`$JUVO_API_KEY` is the value you copied at creation. The examples in this file use placeholders only.)

### Scopes (fixed list, all read only)

| Scope | Opens |
| --- | --- |
| `orders:read` | `GET /orders`, `GET /orders/{number}` |
| `products:read` | `GET /products`, `GET /products/{slug}` |
| `stock:read` | `GET /stock` |
| `customers:read` | `GET /customers`, `GET /customers/{id}` |
| `pii:read` | Not an endpoint: shows customer names, mobile numbers, e-mail and delivery addresses **unmasked**. Off by default. |

A key may also be limited to **one branch** (orders and stock of that branch only; a foreign order answers 404), have an **expiry**, and its own **rate limit**.

### Answers to a bad key

| Case | Status and `code` |
| --- | --- |
| header missing, key unknown, secret wrong, not a key at all | 401 `PUBLICAPI.INVALID_KEY` (same answer for all, so keys cannot be probed) |
| key correct but disabled | 401 `PUBLICAPI.KEY_DISABLED` |
| key correct but expired | 401 `PUBLICAPI.KEY_EXPIRED` |
| key valid, scope missing for this endpoint | 403 `PUBLICAPI.SCOPE_MISSING` |
| over the limit | 429 `PUBLICAPI.RATE_LIMITED` with `Retry-After` (seconds) |

Every answer of a valid key carries `X-RateLimit-Limit` and `X-RateLimit-Remaining`. The default limit is **60 requests per minute per key** (setting `PublicApi.RateLimitPerMinute`, 1 to 10000, Settings centre, group Security; a key can override it). The window is one clock minute; counters are in memory (one application server).

A staff or customer JWT does **not** open the public API and an API key does **not** open the staff API.

## 2. Endpoints

Lists answer `{items, total, page, pageSize}`; `pageSize` defaults to 20 and is capped at 100.

| Method and path | Scope | Query | Notes |
| --- | --- | --- | --- |
| `GET /orders` | orders:read | `status` (Placed, Confirmed, Packed, HandedToCourier, InTransit, OutForDelivery, Delivered, Cancelled, DeliveryFailed, ReturnInProgress, Returned), `placedFrom`, `placedTo`, `updatedSince` (ISO 8601), `page`, `pageSize` | Newest first. Bad `status` is 400. |
| `GET /orders/{number}` | orders:read | | E.g. `JV-DHK01-2611-000123`. 404 `ORDER.NOT_FOUND` (also for another branch). |
| `GET /products` | products:read | `updatedSince`, `page`, `pageSize` | Published products only, with variants, prices and pictures. |
| `GET /products/{slug}` | products:read | | 404 `CATALOG.PRODUCT_NOT_FOUND` for a draft, archived or unknown slug. |
| `GET /stock` | stock:read | `sku` (exact), `page`, `pageSize` | Per variant (SKU): `onHand`, `available` (totals over the branches the key may see) and `branches[]` with `branchCode, onHand, reserved, available, reorderLevel`. Published products only. |
| `GET /customers` | customers:read | `updatedSince`, `page`, `pageSize` | Mobile and e-mail masked, name omitted, unless `pii:read`. Includes `orderCount`. |
| `GET /customers/{id}` | customers:read | | 404 `CUSTOMER.NOT_FOUND`. |

Order object (shortened): `id, number, status, paymentStatus, deliveryStatus, paymentMethod, source, branchCode, currency, subtotal, discountTotal, deliveryCharge, taxAmount, grandTotal, advancePaid, placedAt, confirmedAt, packedAt, deliveredAt, cancelledAt, updatedAt, customer{id, mobile, name, email}, shipTo{country, division, district, upazila, city, recipient, phone, addressLine, landmark, postalCode}, items[{variantId, productId, sku, name, colour, size, quantity, unitPrice, lineTotal}]`. Without `pii:read`: `customer.mobile` is masked (`+8801712****78`), `customer.name`, `customer.email`, `shipTo.recipient|phone|addressLine|landmark|postalCode` are `null`; the area names stay.

Product object (shortened): `id, slug, nameEn, nameBn, shortDescription, description, material, targetGroup, categoryId, brandId, brand, currency, regularPrice, price (what a customer pays now), onSale, isNew, isFeatured, tags, weightGrams, publishedAt, updatedAt, images[{url, altText}], variants[{id, sku, colour, size, price, isActive}]`. Image `url` is relative to the API host (`/files/...`).

```bash
# orders changed since a time, only confirmed ones
curl -H "X-Api-Key: $JUVO_API_KEY" \
  "https://<your-host>/api/public/v1/orders?status=Confirmed&updatedSince=2026-11-01T00:00:00Z&pageSize=50"

# one order
curl -H "X-Api-Key: $JUVO_API_KEY" "https://<your-host>/api/public/v1/orders/JV-DHK01-2611-000123"

# stock of one SKU
curl -H "X-Api-Key: $JUVO_API_KEY" "https://<your-host>/api/public/v1/stock?sku=JV-TSH-BLA-M"
```

## 3. Webhooks

Register an endpoint (staff screen or API): a name, an **https** address and the events you want. The **signing secret** (`whsec_` + 64 hex characters) is shown once, at creation or when you rotate it; it is stored encrypted with the platform's data-protection keys.

| Event | When | `data` |
| --- | --- | --- |
| `order.placed` | an order was placed | the order object (no personal data) |
| `order.status_changed` | any of the three tracks (order, payment, delivery) changed | `{orderId, number, track (Order, Payment, Delivery), from, to, order}` |
| `product.published` | a product became published | the product object |
| `product.updated` | a published product, one of its variants or pictures changed | the product object |
| `stock.low` | a variant in a branch fell to or below its reorder level (once per fall; sent again after it was above the level) | `{variantId, sku, productSlug, branchCode, onHand, available, reorderLevel}` |
| `ping` | only the test button | `{message}` |

Webhook bodies never contain customer names, mobile numbers, e-mail or addresses, whatever the keys can see.

Body: `{ "id": "<event id>", "type": "order.placed", "createdAt": "...", "data": { ... } }`. The `id` is the same on every retry and redelivery: ignore an id you already processed.

Headers: `Juvo-Signature: t=<unix seconds>,v1=<hex HMAC-SHA256 of "t.body">` (the same scheme the payment webhooks use, in reverse), `Juvo-Event: <type>`, `Juvo-Delivery-Id: <delivery id>`, `Content-Type: application/json`.

Verify (any language): compute `HMAC_SHA256(secret, t + "." + rawBody)` as lower-case hex, compare in constant time with `v1`, and reject when `t` is more than 5 minutes from your clock.

```bash
# what a receiver computes (placeholders)
printf '%s' "$T.$RAW_BODY" | openssl dgst -sha256 -hmac "$WEBHOOK_SECRET"
```

Answer with any **2xx** within **5 seconds**. Redirects are not followed; the response body is never read or stored.

### Delivery, retry, failure

Events go through the outbox (at least once). Each endpoint gets its own delivery row. A failed attempt (non-2xx, timeout, connection error) is retried after **30 s, 2 min, 8 min, 32 min**; after the **5th** failed attempt the delivery is **Failed**. Failed deliveries stay in the log and can be **redelivered** by staff (same event id and body, attempts restart). The log shows status, attempts, HTTP status, a scrubbed one-line error, next attempt time and a hash of the body; errors never contain the address or the response.

### Address safety (SSRF)

Only **https** addresses without a user name or password. The host is resolved and **every** resulting address must be a public unicast address: loopback, private (10/8, 172.16/12, 192.168/16), link-local (including the cloud metadata address 169.254.169.254), shared (100.64/10), documentation, benchmarking, multicast and reserved ranges, and the IPv6 equivalents (also IPv4-mapped, NAT64 and 6to4 forms) are refused. The check runs when an endpoint is saved, before each delivery, and again on the exact address the connection is opened to (so a host name that is pointed at a private address later, DNS rebinding, still fails). A refused address is never retried. No system proxy is used.

## 4. Staff API (`/api/v1/integrations`)

Platform-wide (not branch scoped). **Super Admin only by default**; the Manager role is denied `integrations.*` like the privacy codes. Grant per role in the roles screen.

| Permission | Allows |
| --- | --- |
| `integrations.apikey.view` | list and read keys, list scopes |
| `integrations.apikey.manage` | create, edit, rotate, disable, enable, delete keys |
| `integrations.webhook.view` | list and read endpoints, delivery log |
| `integrations.webhook.manage` | create, edit, enable, disable, rotate secret, test ping, delete endpoints, redeliver |

| Method and path | Permission | Notes |
| --- | --- | --- |
| `GET /api-keys` | apikey.view | Query `enabled`, `search` (name or prefix), `page`, `pageSize`. Never the secret. |
| `GET /api-keys/scopes` | apikey.view | The fixed scope list. |
| `GET /api-keys/{id}` | apikey.view | 404 `INTEGRATIONS.KEY_NOT_FOUND`. |
| `POST /api-keys` | apikey.manage | Body `{name, scopes[], branchId?, expiresAt?, rateLimitPerMinute?}`. 201 `{key, secret}`; the secret is in this answer only. 400 for no or unknown scope (`INTEGRATIONS.INVALID_SCOPE`), expiry in the past, bad limit, unknown branch, bad name. |
| `PUT /api-keys/{id}` | apikey.manage | Same body; the secret is unchanged. |
| `POST /api-keys/{id}/rotate` | apikey.manage | New secret in the answer; the old one stops working at once. |
| `POST /api-keys/{id}/disable`, `/enable` | apikey.manage | |
| `DELETE /api-keys/{id}` | apikey.manage | 204. |
| `GET /webhooks`, `GET /webhooks/{id}`, `GET /webhooks/events` | webhook.view | Endpoint list (with `failedDeliveries`), one endpoint, the event types. 404 `INTEGRATIONS.WEBHOOK_NOT_FOUND`. |
| `POST /webhooks` | webhook.manage | Body `{name, url, events[], isEnabled?}`. 201 `{endpoint, secret}`. 400 `INTEGRATIONS.URL_REFUSED` (not https, private address, unresolvable) or `INTEGRATIONS.INVALID_EVENT`. At most 20 endpoints. |
| `PUT /webhooks/{id}` | webhook.manage | Same body; the address is checked again. |
| `POST /webhooks/{id}/enable`, `/disable`, `/rotate-secret` | webhook.manage | |
| `POST /webhooks/{id}/test` | webhook.manage | Sends a signed `ping` now (one try) and answers the delivery with its outcome; a failed ping is a normal 200 answer showing the reason. |
| `DELETE /webhooks/{id}` | webhook.manage | Removes the endpoint and its delivery log. |
| `GET /webhooks/deliveries` | webhook.view | Newest first. Query `endpointId`, `status` (Pending, Succeeded, Failed), `eventType`, `from`, `to`, `search` (event or delivery id), `page`, `pageSize`. |
| `GET /webhooks/deliveries/{id}` | webhook.view | With the exact body. 404 `INTEGRATIONS.DELIVERY_NOT_FOUND`. |
| `POST /webhooks/deliveries/{id}/redeliver` | webhook.manage | Failed or Succeeded deliveries; a Pending one answers 409 `INTEGRATIONS.NOT_REDELIVERABLE`. |

Every change is in the audit trail (who, when, what changed). The secret, its hash and the protected secret are excluded from it; rotation, test pings and redeliveries are recorded as events. Using a key updates "last used" and a hash of the caller address at most once a minute and writes no audit row.

## 5. Configuration

| Setting | Default | Notes |
| --- | --- | --- |
| `PublicApi.RateLimitPerMinute` (Settings centre) | 60 | Per key unless the key has its own limit. |
| `Webhooks:WorkerEnabled` (appsettings) | true | The delivery worker; polls every `Webhooks:PollIntervalSeconds` (5), batch `Webhooks:BatchSize` (20). |

There is no setting that relaxes the address rules. Data-protection keys (`App_Data/keys`, `DataProtection:KeysPath`) must be kept safe and persistent: if they are lost, stored webhook secrets cannot be read and each endpoint needs a new secret (rotate).
