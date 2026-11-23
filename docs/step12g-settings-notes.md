# Step 12g slice 1: platform settings registry (notes)

Branch `feature/12g-settings-backend`. Contract: `docs/settings-api.md`. Scope: FR-AF01 (settings centre, switches without code change) and FR-AF08 in part (language and currency settings). Back end only.

## 1. Audit: runtime-changeable settings that existed before this slice

Everything runtime-changeable already lived in one table, `PlatformSettings` (`Key` unique, `Value` nvarchar(2000), `Entity` stamps and rowversion; entity `PlatformSetting` in `Juvo.Domain/Orders/Order.cs`; generic audit logs every change with old and new value). There was no screen; each module had its own read code with its own defaults and some had their own PUT endpoint. Read path for almost all: `SettingsService.ReadAsync(keys)` (uncached, straight query); payments, purchasing and a few others read the table directly.

| Area (keys) | Stored in | Read by | Own endpoint |
| --- | --- | --- | --- |
| Rewards: `Loyalty.Enabled` (default off) and 13 rules (`Loyalty.*`) | `PlatformSettings` | `LoyaltySettingsService.ReadAsync` (rewards engine, tender, referral, gift cards) | `GET/PUT /loyalty/settings` (`loyalty.points.view/adjust`) |
| Checkout and orders: `Checkout.OtpMode`, `Checkout.CodAdvanceThreshold`, `Checkout.DuplicateWindowMinutes`, `Orders.PaymentHoldMinutes`, `Orders.RoutingMode`, `Orders.MaxManualQuantity` | `PlatformSettings` | `SettingsService.OrdersAsync` (checkout, order placement, workflow) | none (table only) |
| Inventory: `Inventory.DeductAt` (Packing or Delivery) | `PlatformSettings` | `SettingsService.OrdersAsync`, `OrderWorkflow` | none |
| Fraud rules: `Fraud.<CODE>.Enabled/Threshold/Action` | `PlatformSettings` | `FraudService` | `GET/PUT /fraud/rules` (`fraud.rule.view/edit`) |
| Payments: `Payments.<CODE>.Enabled` (9 methods), `Payments.Cod.MaxAmount`, `Payments.Manual.Instructions`, `Payments.AutoConfirmPaidOrders` | `PlatformSettings` | `PaymentSettingsService.ReadAsync` | `GET/PUT /payments/settings` (`payments.gateway.view/edit`) |
| Returns: `Returns.WindowDays`, `Returns.CustomerFaultReturnCharge`, `Returns.RefundDeliveryOnSellerFault` | `PlatformSettings` | return services | none |
| Invoice seller: `Business.Name/Address/Phone/Email/TaxId` | `PlatformSettings` | `InvoiceService.SellerAsync`, purchasing PDF | none |
| Notifications: `Notifications.SmsEnabled/EmailEnabled/WhatsAppEnabled/DefaultLanguage` | `PlatformSettings` | `NotificationService`, marketing | `GET/PUT` channel settings under `/notifications` |
| Marketing: 11 keys (`Marketing.*`) | `PlatformSettings` | `MarketingSettingsService` | `/marketing/settings` |
| Purchasing, expenses, support: `Purchasing.CostMethod`, `Expenses.ApprovalThreshold`, `Support.*` (reopen, auto-close, tickets per hour, SLA hours per priority) | `PlatformSettings` | `OpsSettingsService` | none |
| Daily summary: `Reports.DailySummary.Email/Hour` (and `LastSentDate`, internal bookkeeping) | `PlatformSettings` | `DailySummaryService` | none |
| Auth and OTP: `Auth:Otp:*`, lockout, password length, token lifetimes, 2FA for Super Admin | appsettings (`AuthOptions`) | `CustomerAuthService`, Identity setup | none (restart needed) |
| Providers, CORS, rate limits, outbox, couriers sync, files, storefront options, `Seed:*` | appsettings / environment | respective workers and options | none |
| Courier slots (enabled, non-secret settings) | `Couriers` table | courier services | `/couriers` |
| Delivery charge rules, zones, tax rules | own tables | pricing and delivery services | own endpoints |

## 2. What this slice built

* `Juvo.Application/Settings`: `SettingDefinition` (type, range, allowed values, format, default, en and bn text, edit permission, restart flag, sensitive flag), `SettingRegistry` (about 80 definitions), `ISettingsReader` plus `SettingsCache` (singleton snapshot) and `SettingsReader` (scoped), `SettingsCentreService`.
* `SettingsCacheInterceptor` (Infrastructure): empties the cache after any save touching a `PlatformSetting`.
* `AuditSaveChangesInterceptor`: masks the value of a setting the registry marks sensitive.
* `Juvo.Api/Endpoints/SettingsEndpoints.cs`: `/api/v1/settings` (list, get, put, delete, history).
* No migration and no new table (the existing `PlatformSettings` is the store), so there is no `PlatformSettings` migration commit.
* Error codes `SETTINGS.UNKNOWN_KEY|INVALID_VALUE|READ_ONLY`; `Permissions.Settings.*` constants (the codes already existed in `PermissionCatalog`).

## 3. Decisions (reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| S1 | Reuse `PlatformSettings`; no `SystemSettings` table. | One store means the centre and the module endpoints cannot diverge; the table already has key-unique, stamps, rowversion and audit. | n/a |
| S2 | Registry defaults repeat each module's fall-back; a unit test compares them with `LoyaltySettings.Defaults`, `SettingsService.Defaults`, `ReturnSettings.Defaults`, `MarketingSettings.Defaults`, the payment catalogue. | A second source of defaults can drift. The modules keep their own parsing (unchanged behaviour). | When a module default changes, change the definition (test fails otherwise for the covered ones). |
| S3 | A value equal to the default is stored as an explicit row; `DELETE` removes it. | Keeps the owner's explicit choice if a later release changes the default; reset is a separate, visible action in history. | Make `PUT` remove the row when equal to the default. |
| S4 | Only registered keys are accepted; unknown keys are 404. `Fraud.*` rules stay on their own endpoint; `Reports.DailySummary.LastSentDate` is internal and not registered. | Fraud has an action/threshold schema and screens of its own; bookkeeping is not a setting. | Add definitions. |
| S5 | Registry limits are stricter than some module validators (for example `Loyalty.MaxTenderPercent` 0..100). The modules still read whatever is stored and fall back on invalid text. | New API gets sane bounds; old paths are untouched. | Edit the definition. |
| S6 | Permission per setting: feature switches `settings.feature.edit`, payment settings `payments.gateway.edit`, the rest `settings.system.edit`; every endpoint needs `settings.system.view`; edit permission is checked inside, anywhere-scope. No new codes. | Matches the codes in the catalogue and the existing payment permission, so a payments-only role is not widened. | Change `EditPermission` in the definition. |
| S7 | Cache: singleton snapshot of the whole table, emptied after every save that touches a setting, hard lifetime 10 s. Bulk SQL and other processes are covered only by the lifetime. A read racing a commit can keep the old value for up to 10 s. | One app server (CLAUDE.md known issue 2); short lifetime bounds the staleness. | `SettingsCache.Lifetime`; for several servers use a shared cache or version check (Step 14). |
| S8 | Wired to the cache: `SettingsService.OrdersAsync` (`Inventory.DeductAt` and the order settings) and `LoyaltySettingsService.ReadAsync` (`Loyalty.Enabled`). Payments and the rest still read the table directly (always fresh). | Two real consumers as asked, without touching tests that clear rows with bulk SQL (`ReturnSupport`, `OpsSupport`). | Move other readers to `ReadCachedAsync` / `ISettingsReader`. |
| S9 | History is read from the generic audit log (entity `PlatformSetting`); the key of an update is found from the row or the create/delete audit entry. Only registered keys are listed; the newest 600 audit rows are scanned. | No second history table. | Add a dedicated log if more volume is expected. |
| S10 | Sensitive flag: value and default are never returned; the audit interceptor and history mask the value. No registered setting is sensitive (no secret belongs in `PlatformSettings`, see assumptions S5 on courier settings). The masking path has no integration test because no sensitive setting exists. | Honour the requirement without inventing a secret. | Set `Sensitive: true` on a definition; add a test then. |
| S11 | Optimistic concurrency is optional: `version` = base64 rowversion; without it the last write wins. | Simple screens can skip it. | Make it required. |
| S12 | Read-only entries (OTP rules, lockout, 2FA flag from appsettings; `Currency.Store` = BDT) are shown with source `appsettings`/`default` and answer 409 `SETTINGS.READ_ONLY` on change. | The SRS asks for OTP rules; they are configuration needing a restart, shown honestly instead of faked. | Move the options into `PlatformSettings` and read them through the reader. |

## 4. Gaps (cannot be changed at run time yet)

* OTP length, expiry, cooldown, requests per hour, attempts: appsettings only (`Auth:Otp`). OTP before confirm is switchable (`Checkout.OtpMode`).
* Modules on/off beyond rewards: pre-orders, reviews, social desk, marketing as a whole, POS, reseller have **no feature flag** in the code; only `Loyalty.Enabled` and the notification channels exist. Marketing sending can be limited by its own settings, not switched off as a module.
* FR-AF08: default storefront language is a browser cookie (`juvo_locale`) in the front end; the server only knows `Notifications.DefaultLanguage` (messages). No admin setting for the storefront default. Display currency list and conversion rates (BR-005 international display, assumptions row 17 of section M, FR-F/G) have **no store**; BDT is fixed and shown read-only. Needs a currency-rate table and a display feature first.
* Delivery charge rules, zones, tax rules and courier slots have their own tables and endpoints; they are not in this registry.
* Payment gateway credentials and provider keys: environment/appsettings only (Step 14).
* Fraud rule values: own endpoint.
* Several app servers: the cache is process-local.
* Audit entries of settings written before this slice are included in history as usual (they have the same entity name).

## 5. Tests

* `tests/Juvo.Application.Tests/Settings/SettingRegistryTests.cs`: definition completeness, defaults equal to module defaults, bool/whole/number/choice/text parsing and limits, JSON conversion, typed values, unknown keys.
* `tests/Juvo.Api.IntegrationTests/Settings/SettingsCentreTests.cs` (real SQL Server, throw-away DB): list shape, change and reset with immediate effect on `Loyalty.Enabled` (customer rewards endpoint), module endpoint change seen by the centre, payment store shared, unknown and invalid values and read-only keys, permissions (401, customer, nobody, viewer, system editor, feature editor), stale version, `Inventory.DeductAt` Delivery versus Packing through a full order, history (set, set, reset, names, limit), explicit default row, typed reader with an invalid stored value.
* `EndpointPolicyTests` needed no change (every settings endpoint declares a policy; it passes).

## 6. Traceability suggestions

| ID | Suggested status | Implemented in / note |
| --- | --- | --- |
| FR-AF01 | Partial | `/api/v1/settings` over `PlatformSettings` with registry, validation, audit, history, cached reads; switches for rewards, payment methods, OTP mode, deduct point, notification channels. Gaps: no flags for pre-order/reviews/social desk/POS/reseller; OTP lengths are config; no screen yet (front end next slice). |
| FR-AF08 | Partial | Default message language (`Notifications.DefaultLanguage`) and fixed store currency BDT shown. Gaps: storefront default language, display currencies and rates. |
| FR-D04 | note | `Checkout.OtpMode` is now editable from the centre. |
| FR-I02 | note | Payment method switches editable from the centre (same store). |
| FR-O04 / NFR-012 | note | Every change audited with old and new value; sensitive values masked. |
| FR-AB01 | note | `Loyalty.Enabled` and rules editable from the centre. |
| BR-005 | note | Store currency is read-only BDT; conversion store missing. |
