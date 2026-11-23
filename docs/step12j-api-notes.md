# Step 12j slice 1: public REST API, API keys and webhooks (notes)

Branch `feature/12j-public-api`. Contract and curl examples: `docs/public-api.md`. Scope: FR-AF05. Back end only (no screens). One migration `PublicApi` (three tables and one column). No new NuGet package.

## 1. What was built

* **Domain** `Juvo.Domain/Integrations`: `ApiKey` (name, prefix, SHA-256 hash with `[AuditIgnore]`, scopes, optional branch, enabled, expiry, optional rate limit, last used, last address hash, rotated at), `WebhookEndpoint` (name, https address, protected secret with `[AuditIgnore]`, events, enabled), `WebhookDelivery` (event id, type, endpoint, payload, payload hash, attempt, status Pending/Succeeded/Failed, http status, scrubbed error, next attempt, redeliveries; unique (event, endpoint)). `StockLevel.LowAlertedAt` (new nullable column).
* **Application** `Juvo.Application/Integrations`: `ApiKeyRules` (generate, hash, prefix, constant-time match, dummy hash), `ApiKeyScopes`, `ApiKeyRateLimiter`, `ApiKeyService` (staff CRUD), `ApiKeyAuthenticator`, `PublicApiService` (the one place that shapes orders, products, stock, customers), `WebhookService` (staff), `WebhookFanout`, `WebhookDispatcher`, five outbox handlers, `SsrfAddressClassifier`, `WebhookDelivering` (headers, backoff, secrets), interfaces `ISecretProtector`, `IWebhookTargetGuard`, `IWebhookSender`.
* **Infrastructure** `Juvo.Infrastructure/Integrations`: `DataProtectionSecretProtector`, `WebhookTargetGuard`, `WebhookHttpSender`, `WebhookDeliveryWorker`, `WebhookEventsInterceptor`; EF configurations; DI.
* **Api**: `ApiKeyAuthenticationHandler` (scheme `ApiKey`), policies `PublicApi` and `apikey:<scope>`, `PublicApiRateLimitFilter`, `IntegrationsEndpoints`, `PublicApiEndpoints`, a second OpenAPI document `public-v1`.
* Permissions `integrations.apikey.view|manage`, `integrations.webhook.view|manage` (Super Admin only; Manager has an explicit deny `integrations.*`); setting `PublicApi.RateLimitPerMinute` (Security group, default 60); error codes `PUBLICAPI.*` and `INTEGRATIONS.*`.

## 2. Decisions (reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| J1 | Key = `jv_live_` + 8-char public id + `_` + 43-char secret; row found by the 16-char prefix, then `CryptographicOperations.FixedTimeEquals` on the SHA-256; a dummy hash is compared when the prefix is unknown. | Fast indexed lookup, no secret in the database, no timing difference between "unknown" and "wrong". SHA-256 is right for a 256-bit random secret (no password stretching needed). | `ApiKeyRules`. |
| J2 | All bad-key cases answer 401 `PUBLICAPI.INVALID_KEY` except disabled and expired, which are only reported after the secret was proven right. | Integrators get a useful answer; strangers learn nothing. | `ApiKeyAuthenticator`. |
| J3 | Separate authentication scheme and route group (`/api/public/v1`); every endpoint needs the `PublicApi` policy plus one `apikey:<scope>` policy; a test fails for any endpoint without them, with a write method, or with an unknown scope. | Never mixes staff JWT and keys; secure by default for later additions. | `PublicApiEndpoints`, `EndpointPolicyTests`. |
| J4 | Read-only: no write scope in this step (`stock:write` excluded as asked). | Smallest safe surface. | Add scopes and endpoints later, each with its own policy test. |
| J5 | `pii:read` is a scope in the same list; without it mobile and e-mail are masked with the staff screens' masking, names and street addresses are `null`. Webhooks never carry personal data at all. | Same masking rules everywhere; events may be logged by receivers. | `PublicApiService.ToOrder`, `ToCustomer`. |
| J6 | `customers:read` got two endpoints (`/customers`, `/customers/{id}`) although the brief listed only orders, products and stock. | A scope without an endpoint would be a dead permission. | Remove the endpoints and the scope. |
| J7 | Per-key fixed-window limiter in memory, key override or setting default, `X-RateLimit-*` and `Retry-After`. The global per-IP limiter still applies in front. | Settings-driven, no new package, same one-server assumption as the other limiters (CLAUDE.md known issue 2). | `ApiKeyRateLimiter`; a shared store in Step 14. |
| J8 | "Last used" and the address hash are written with a direct update at most once a minute (no change tracking, no audit row). The address is stored only as 16 hex characters of a salted SHA-256. | A busy key must not flood the audit trail or the database; no addresses stored. | `ApiKeyAuthenticator`. |
| J9 | Webhook secret: `whsec_` + 256 bits, stored with ASP.NET Data Protection (purpose `Juvo.Integrations.WebhookSecret.v1`), nothing existed to reuse (payment and courier slots have no credentials yet). Unreadable secret (keys lost) fails the delivery with a clear reason and needs a rotation. | A signing secret must be readable, so hashing is not possible. | `DataProtectionSecretProtector`. |
| J10 | Outbox handlers only write delivery rows (fast, idempotent: unique (event, endpoint) plus an existence check). A separate worker sends them. The event id is the outbox message id. | Retries with their own schedule and a visible Failed state do not fit the outbox's generic dead-letter; fan-out failures and HTTP failures stay apart. | `WebhookFanout`, `WebhookDeliveryWorker`. |
| J11 | Backoff 30 s, 2 min, 8 min, 32 min, then Failed after the 5th attempt; a delivery is claimed with one conditional update and a 2 minute lease, so two workers never send the same attempt and a crash is retried after the lease. | At-least-once, safe with several workers. | `WebhookDelivering.DelayAfter`, `WebhookDispatcher.Lease`. |
| J12 | Order events reuse the existing domain-event outbox messages (`domain.OrderPlaced`, `domain.OrderStatusChanged`: all three tracks, with a `track` field). Product and stock events are written by a save interceptor as `webhook.*` messages. | No change to the order code; one place for catalogue and stock triggers. | `WebhookEventsInterceptor`. |
| J13 | `product.updated` also fires for a changed variant or picture of a published product; the handler reads the current state and sends nothing if the product is no longer published. One message per product per save. | The public product object contains variants and pictures. | Interceptor. |
| J14 | `stock.low` is edge-triggered: every stock-ledger line (and every changed or new stock row) queues a check; the handler sends only when the variant is low now and `StockLevel.LowAlertedAt` is empty, and clears the mark once stock is above its level again. The mark and the delivery rows are saved together. | Order reservations are direct SQL updates the change tracker never sees, but each writes a ledger line; and one alert per fall is what an integrator wants. | `WebhookStockLowHandler`. |
| J15 | SSRF: `SsrfAddressClassifier` (pure) plus `WebhookTargetGuard`; checked at save, before delivery, and in the socket `ConnectCallback` on the resolved address; no redirects, no proxy, 5 s limit, body never read. The production guard has no switch; tests replace the service (`LoopbackWebhookTargetGuard`, test project only). A refused address is not retried. | DNS rebinding and redirect tricks are covered; nothing in configuration can weaken it. | `WebhookTransport.cs`. |
| J16 | Errors stored for deliveries are fixed sentences (`HTTP 503`, `No answer within 5 seconds.`, `The connection failed.`, `Refused: ...`), never exception text, address or body. | Exception text can hold the address (and a private one is exactly what an attacker probes). | `WebhookHttpSender`. |
| J17 | The test ping is one try (no retry), works for a disabled endpoint, and returns the delivery. | A person is waiting for the answer. | `WebhookService.PingAsync`. |
| J18 | Deleting an endpoint deletes its deliveries; deleting a key is a hard delete. Both are in the audit trail. | These rows carry no personal data and a deleted key must stop working at once. | Add soft delete if history is wanted. |
| J19 | The Manager role is explicitly denied `integrations.*` (it never matched an allow pattern; the deny keeps it so if patterns change). | Same treatment as `customers.privacy.*`. | `DefaultRoles`. |
| J20 | OpenAPI: a second unversioned document `public-v1` (`ShouldInclude` by group name), analyzer AV0029 silenced in `Juvo.Api.csproj` for that reason. | Keeps the staff documents untouched. | `ServiceCollectionExtensions`. |

## 3. Tests (all run and seen passing)

* Domain 420 and application 359 (unit; includes this step's: key hashing and prefix lookup, scope catalogue, rate limiter, signature creation and verification with the payment checker, backoff schedule, 36 blocked and 16 allowed addresses incl. IPv6, IPv4-mapped, NAT64, 6to4, delivery state machine, Manager denied).
* Integration classes added: `ApiKeysAdminTests`, `PublicApiTests`, `WebhooksTests`, `WebhookSsrfTests` (one class per feature, each with its own host so rate-limit windows do not collide); `EndpointPolicyTests` gained the public-group test and now excludes `/api/public/` from the staff-route filter.
* Also run for regression (touched code paths: all saves, settings, permissions): EndpointPolicy, Seeder, SeederChange, UmsAdmin, SettingsCentre, Interceptor, Outbox, OrderFlow, OrderStock, PaymentFlow, Catalog, Inventory.
* The in-memory test host has no remote address, so the "last address hash" is covered by the unit test only.

## 4. Gaps and follow-ups

* No front-end screens (API keys, webhooks, delivery log): next slice.
* Rate-limit counters, and the key "last used" throttle, are per process.
* A product, variant or picture edit in a draft writes an outbox row that does nothing; every stock-ledger line writes a stock check row. With no webhook configured these are cheap but not free; if volume matters, add a cached "any enabled endpoint" flag.
* Webhook events for returns, payments, customers and tickets do not exist yet (only the five requested).
* Key and webhook list screens will want `lastUsedAt` and `failedDeliveries` (already in the DTOs).
* Scalar shows only the staff document; the public one is at `/openapi/public-v1.json`.
* Real-time delivery metrics in the health page (`/system/integrations/health`) do not include webhooks (SY3 stays true for couriers; webhook failures are now recorded in `WebhookDeliveries` and could replace the `webhooks` row of `/system/jobs/kinds`).

## 5. Traceability suggestions

| ID | Suggested status | Implemented in | Notes |
| --- | --- | --- | --- |
| FR-AF05 | Done (back end); screens Not started | `ApiKeyService`, `PublicApiService`, `WebhookService`, `WebhookDispatcher`, `/api/public/v1/*`, `/api/v1/integrations/*`, `docs/public-api.md` | Read-only API with keys and scopes; five webhook events; documented. |
| NFR-009 | Partial to stronger | `WebhookSignature` (shared with payment webhooks), `WebhookDelivering.Sign`, `WebhookTargetGuard` | Outgoing signatures, replay window for receivers, SSRF protection. |
| NFR-012 | Done (extend note) | `ApiKey` and `WebhookEndpoint` audited automatically without secrets; explicit events for rotate, ping, redeliver | |
| FR-I09 | no change | | Payment webhook scheme reused, not changed. |
| FR-AF02 | note | `WebhookDeliveries` is a recorded failure source (retry and redeliver there) | `/system/jobs/kinds` still lists `webhooks` as not recorded. |
