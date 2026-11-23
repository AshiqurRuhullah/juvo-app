# Settings centre API (Step 12g slice 1)

One API over the runtime settings that the modules already keep in the `PlatformSettings` table (FR-AF01, FR-AF08 in part). It adds no table: a change made here is the same row a module's own endpoint writes (for example `PUT /loyalty/settings` or `PUT /payments/settings`), so the two can never disagree. Notes, audit of existing settings and gaps: `docs/step12g-settings-notes.md`.

Base path `/api/v1/settings`. Staff token only. Settings are global (not branch-scoped), so permissions are checked "anywhere".

## Permissions

| Call | Permission |
| --- | --- |
| `GET /settings`, `GET /settings/{key}`, `GET /settings/history` | `settings.system.view` |
| `PUT /settings/{key}`, `DELETE /settings/{key}` | `settings.system.view` to reach the endpoint, **and** the permission named by the setting's `editPermission` (checked inside, 403 otherwise) |

`editPermission` per group: feature switches (`Loyalty.Enabled`, notification channels) `settings.feature.edit`; payment settings (`Payments.*`) `payments.gateway.edit` (the same code the payment settings endpoint uses); everything else `settings.system.edit`. No new permission code was added. Super Admin holds all of them.

## Types

`bool` (JSON true/false), `whole` (integer), `number` (decimal, up to 4 decimals), `text`, `choice` (one of `allowedValues`, matched without case, stored in canonical spelling). Text settings may have a format: `HH:mm` time, email (or empty), absolute http(s) address (or empty).

`GET` returns typed JSON values. `PUT` accepts a JSON boolean, number or string for any type (`"12"` works for a whole number); arrays, objects and null are refused.

## Endpoints

### `GET /settings`

```json
{ "groups": [ { "group": "features", "settings": [ {
  "key": "Loyalty.Enabled", "group": "features", "type": "bool",
  "value": false, "default": false, "source": "default", "isSet": false,
  "descriptionEn": "...", "descriptionBn": "...",
  "allowedValues": null, "min": null, "max": null, "maxLength": null,
  "requiresRestart": false, "sensitive": false, "readOnly": false,
  "canEdit": true, "editPermission": "settings.feature.edit",
  "version": null, "updatedAt": null } ] } ] }
```

* `source`: `db` (a valid stored row), `default` (no row, or the stored text no longer passes the definition), `appsettings` (read-only server configuration).
* `version`: base64 of the row version when a row exists; send it back in `PUT` for optimistic concurrency.
* `sensitive` settings never return `value` or `default` (null). No registered setting is sensitive today.
* `readOnly` entries (source `appsettings`: `Auth.Otp.Length`, `Auth.Otp.ExpiryMinutes`, `Auth.Otp.ResendCooldownSeconds`, `Auth.Otp.MaxRequestsPerHourPerMobile`, `Auth.MaxFailedAccessAttempts`, `Auth.LockoutMinutes`, `Auth.RequireTwoFactorForSuperAdmin`; and `Currency.Store` = `BDT`, fixed by BR-005) are shown so the screen is complete but cannot be changed.
* Groups in order: features, loyalty, payments, orders, inventory, returns, business, notifications, marketing, operations, reports, locale, security.

### `GET /settings/{key}`
One setting (same shape). 404 `SETTINGS.UNKNOWN_KEY` for a key that is not registered.

### `PUT /settings/{key}`
Body `{ "value": <json>, "version": "<optional>" }`. Returns the setting.
* 400 `SETTINGS.INVALID_VALUE` (ProblemDetails with `errors.value[]`): wrong type, out of range, not an allowed choice, bad format, too long, or not a plain JSON value.
* 404 `SETTINGS.UNKNOWN_KEY`; 409 `SETTINGS.READ_ONLY`; 403 (no permission for this setting).
* 409 `GENERAL.CONCURRENCY_CONFLICT` when `version` is sent and the row changed or was reset meanwhile.
* A value equal to the default is still stored (an explicit choice that survives a later change of the default); `DELETE` removes the row.
* Takes effect on the next read (cache is emptied by the save; see below). Settings with `requiresRestart: true` would need a restart; none of the writable ones do.

### `DELETE /settings/{key}`
Resets to the default by removing the row. Returns the setting (now `source: default`). Resetting a setting with no row changes nothing (200). Same errors as `PUT` except validation.

### `GET /settings/history?key=&limit=50`
Newest first, from the audit log (`AuditLogs` entity `PlatformSetting`), only for registered settings, limit 1..200:
`[{ "id", "key", "action": "set"|"reset", "oldValue", "newValue", "changedBy", "changedByName", "at" }]`. `oldValue` is null for the first value, `newValue` is null for a reset. Sensitive settings would show `***`. Values are text as stored.

## How reads work

`ISettingsReader` (Application) keeps a snapshot of the table in memory (one app server). A save that adds, changes or removes a setting row (from this API or any module endpoint) empties the snapshot right after the save (`SettingsCacheInterceptor`); an entry never lives longer than 10 seconds, which also covers writes by raw SQL or another process. Consumers wired to the cache: `SettingsService.OrdersAsync` (OTP mode, COD advance, duplicate window, payment hold, `Inventory.DeductAt`, routing, manual quantity) and `LoyaltySettingsService.ReadAsync` (`Loyalty.Enabled` and the other rewards rules). Other modules still read the table directly and see changes at once.

## Registered settings

About 80 keys: loyalty (14), payment methods and limits (`Payments.<CODE>.Enabled` for COD, SANDBOX, MANUAL, BKASH, NAGAD, SSLCOMMERZ, AAMARPAY, STRIPE, PAYPAL; `Payments.Cod.MaxAmount`, `Payments.Manual.Instructions`, `Payments.AutoConfirmPaidOrders`), checkout and order routing (`Checkout.OtpMode`, `Checkout.CodAdvanceThreshold`, `Checkout.DuplicateWindowMinutes`, `Orders.PaymentHoldMinutes`, `Orders.RoutingMode`, `Orders.MaxManualQuantity`), `Inventory.DeductAt`, returns (3), invoice seller details (`Business.*`, 5), notification channels and `Notifications.DefaultLanguage`, marketing (11), purchasing/expenses/support (cost method, approval threshold, reopen, auto-close, tickets per hour, SLA hours per priority), daily summary email and hour. The list endpoint is the authoritative catalogue.

## Error codes added
`SETTINGS.UNKNOWN_KEY` (404), `SETTINGS.INVALID_VALUE` (400), `SETTINGS.READ_ONLY` (409).
