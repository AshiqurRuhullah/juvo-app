# Step 11B backend: marketing (module AC)

Builder BE-M. Contract: `docs/marketing-api.md`. Branch of the work: the builder worktree branch (see the hand-over report for its name and last commit).

## 1. What was built

* **Segments** (FR-AC01): typed rule model (9 rule types, AND/OR), validator with per-field errors, translation to one set-based EF query (sub-queries, no in-memory filtering), live count with per-channel reachable counts, paged member list, four built-in segments, copy, CRUD, PII masking.
* **Campaigns** (FR-AC02, FR-AC04): CRUD, preview with real counts and sample messages in both languages, schedule (offset date-time, shown in Asia/Dhaka), send now, cancel, send test to the staff member's own contact, recipient list, results, campaign report, throttle, quiet hours, frequency cap, dedupe, crash-safe resumable send, `CampaignSendWorker`.
* **Consent and unsubscribe**: the consent check stays in the shared send path (`NotificationService.QueueCustomAsync`, a new public method next to `NotifyAsync`); signed unsubscribe tokens; public `GET/POST /store/unsubscribe`.
* **Automations** (FR-AC03): abandoned cart, back in stock (public `POST /store/products/{slug}/notify-me`), review request, win-back; switch (off by default), delay, window, cap, texts in two languages, dry run, results, `AutomationWorker`.
* **Results and attribution** (FR-AC04): sent, delivered, failed, skipped by reason, unsubscribed, orders attributed (last touch inside the window) with revenue; campaign list report.
* **Endpoints**: `/api/v1/marketing/{segments,campaigns,automations,settings}` plus the two public ones.

Main files: `juvo-be/src/Juvo.Domain/Marketing/Marketing.cs`; `juvo-be/src/Juvo.Application/Marketing/*` (`SegmentEngine`, `SegmentsService`, `CampaignsService`, `CampaignDispatchService`, `AutomationEngine`, `AutomationsService`, `PublicMarketingService`, `MarketingRules`, `MarketingSupport`, `MarketingContracts`); `juvo-be/src/Juvo.Application/Notifications/NotificationService.Custom.cs`; `juvo-be/src/Juvo.Infrastructure/Outbox/MarketingWorkers.cs`; `juvo-be/src/Juvo.Infrastructure/Persistence/Configurations/MarketingConfigurations.cs`; `juvo-be/src/Juvo.Api/Endpoints/MarketingEndpoints.cs`; `juvo-be/src/Juvo.Shared/Security/MarketingPermissions.cs`.

## 2. Edits to shared files (all additive)

| File | Change |
| --- | --- |
| `Juvo.Application/Notifications/NotificationService.cs` | class made `partial` (one word) so the custom send path sits in its own file |
| `Juvo.Application/Abstractions/IApplicationDbContext.cs`, `Juvo.Infrastructure/Persistence/JuvoDbContext.cs` | five `DbSet`s and one `using` |
| `Juvo.Application/DependencyInjection.cs`, `Juvo.Infrastructure/DependencyInjection.cs` | service, option, worker and token-service registrations |
| `Juvo.Api/Configuration/WebApplicationExtensions.cs` | `app.MapMarketingEndpoints();` |
| `Juvo.Shared/Results/ErrorCodes.cs` | nested class `Marketing` (inserted before `Report`) |
| `tests/.../Support/ApiFactory.cs` | two settings that switch the marketing workers off in tests |
| `tests/.../Ums/EndpointPolicyTests.cs` | the two new public routes in the expected anonymous list |

Permission codes live in `MarketingPermissions` (a new file) instead of `Permissions.cs`, so no other builder's lines are touched. The migration `MarketingModule` is in its own commit and is to be deleted by the lead; all schema is in `MarketingConfigurations.cs`.

## 3. Decisions

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| 1 | Segments exclude deactivated and archived customers. | A deactivated customer must not be messaged. | `SegmentQueryBuilder.Members`. |
| 2 | "Counted orders" for repeat buyers, spend and the member list are all orders except `Cancelled` and `Returned`; the "inactive" rule treats any order as activity. | Money that came back or never arrived is not loyalty. | The `Predicate` method in `SegmentEngine.cs`. |
| 3 | The `location` rule uses the **default** address only. | One answer per customer, no double counting. | `Predicate`, case `Location`. |
| 4 | Rule fields that the type does not use are refused. | A rule always means exactly what it says. | `SegmentValidator.Only`. |
| 5 | Recipients are written once per campaign and customer (unique index), including skipped ones with their reason. | Crash resume, dedupe, "skipped by reason" in the results. | `CampaignDispatchService.MaterialiseAsync`. |
| 6 | Each send batch is one transaction under an application lock; the dedupe key `campaign:<id>:<customer>` plus the unique index (`DedupeKey`, channel) is the second guard. | No double send after a crash or with two workers. | `SendBatchAsync`. |
| 7 | Frequency cap counts campaign messages only (template `marketing.campaign`); automations have their own `capDays`. | An abandoned-cart reminder must not block a campaign, and the other way round. | `CampaignDispatchService`, `CampaignsService.PreviewAsync`. |
| 8 | Quiet hours apply to SMS of campaigns and of all automations (also back in stock), not to email. Default on, 21:00 to 09:00 Dhaka. | The brief says marketing SMS; a night SMS about stock is also unwelcome. | `MarketingLogic.IsQuiet` callers; setting `Marketing.QuietHours.*`. |
| 9 | Throttle counts recipients queued in the last minute per campaign. Stateless, so a restart cannot exceed it. | Simple and crash safe. | `Marketing.ThrottlePerMinute`. |
| 10 | `delivered` equals `sent` while only sandbox senders exist. | No provider receipts to read. | A real adapter would add a delivered state to the log. |
| 11 | Attribution: last touch over all `Sent` marketing messages (campaigns and automations), orders not `Cancelled` or `Returned`, window default 7 days, computed from the log joined to orders, no Order change. | Brief; honest numbers. | `AttributionService`. |
| 12 | Review request is treated as marketing (consent required), win-back and abandoned cart too. Back in stock is the only transactional-style one (opt-out honoured). | Consent is the law of the module; only a requested message is exempt. | `AutomationCatalog` (category and consent mode per kind). |
| 13 | Back in stock is handled by the `AutomationWorker` (checks open requests against available stock) instead of a hook in the stock path. | No edit to the inventory code; a missed event cannot lose a request. Delay is the stock-check interval. | `AutomationEngine.BackInStockCandidatesAsync`. |
| 14 | Automation `windowDays` ignores events older than delay plus window. | Switching an automation on must not message the whole history. | Default values in `AutomationCatalog`. |
| 15 | Unsubscribe token: 33-byte payload (customer, channel, campaign) plus 12-byte HMAC, no expiry; link points at the API by default (`Marketing:UnsubscribeUrl`). | Short enough for SMS, works without the web shop; an opt-out link must not expire. | `UnsubscribeTokenService`, option. |
| 16 | A guest may subscribe a number to back-in-stock without proof of ownership. Limits: rate limit per address, 10 open requests per number, one message per request, opt-out honoured. | Matches "customers hesitate to register" (P7). The abuse is one text per request. | `Marketing.NotifyMe.MaxPerMobile`; adding an OTP is possible later. |
| 17 | Member mobile and email are masked without `customers.customer.view-pii`; recipient contacts are always masked. | NFR-012. | `SegmentsService.MembersAsync`, `CampaignsService.RecipientsAsync`. |
| 18 | The test message uses template `marketing.campaign-test`, category Transactional, consent mode none (it goes to the staff member's own contact) and is never counted. | It is not a customer message. | `CampaignsService.SendTestAsync`. |
| 19 | Marketing endpoints are global (not branch scoped); orders are read with the `BranchScope` filter ignored. | Segments and results span all branches. | `IgnoreQueryFilters(["BranchScope"])` in the engine and services. |

## 4. Traceability (for `docs/understanding.md`, written by the lead)

| ID | Status | Implemented in | Notes |
| --- | --- | --- | --- |
| FR-AC01 Customer segments | Done (backend) | `SegmentEngine`, `SegmentsService`, `MarketingEndpoints` | 9 rule types, built-ins, live counts, members. No UI (Step 12). |
| FR-AC02 Campaigns SMS and email with consent | Done (backend) | `CampaignsService`, `CampaignDispatchService`, `NotificationService.QueueCustomAsync`, `UnsubscribeService`, `CampaignSendWorker` | Sandbox senders. Real SMS and email providers are Step 14. WhatsApp campaigns not built. |
| FR-AC03 Automations | Done (backend) | `AutomationEngine`, `AutomationsService`, `BackInStockService`, `AutomationWorker` | All four, off by default. Texts need native Bengali review (Step 12). |
| FR-AC04 Campaign results | Done (backend), Partial for delivery | `CampaignsService.ResultsAsync`, `AttributionService` | Delivered equals sent until a provider reports receipts. Attribution is last touch only. |

## 5. Partial, deferred, not built

* No UI (admin or storefront "notify me" button, unsubscribe page): the front end is a later step.
* WhatsApp campaigns: not built (campaign and automation channel is Sms or Email).
* Delivery receipts: `delivered` equals `sent` (sandbox).
* Cancelling a sending campaign does not recall messages already in the outbox.
* A/B tests, click tracking, per-link statistics: not built (not in the brief).
* Large segments: recipient rows stay tracked in the context during one materialise call (batches of 500); fine for tens of thousands, a clear-tracker call would be needed for millions.
* The send worker serialises per campaign (lock), campaigns are processed one after another per sweep.
* `NotificationLogs` has no index on `CustomerId`; the cap and attribution queries use the `Template`, `Status`/`QueuedAt` and `DedupeKey` indexes. Add one if the log grows into the millions.
* `MARKETING.SETTINGS_INVALID` is defined but unused (settings errors use `GENERAL.VALIDATION` field errors).
* The Bengali sample texts of the automations are a first pass.
* The unsubscribe link in email is a plain GET that unsubscribes at once; a mail scanner that opens links can unsubscribe a customer. A confirmation page in the web shop that POSTs is the safer pattern (the POST endpoint exists).

## 6. Contract gaps and notes for the lead

* The lead deletes the builder migrations and generates one; this step's tables are in `MarketingConfigurations.cs` (five entities, filtered unique indexes on `CustomerSegments.BuiltInCode` and `BackInStockSubscriptions (VariantId, Mobile) where NotifiedAt is null`).
* `ErrorCodes.cs`: the `Marketing` class was inserted before `Report`; if other builders also edited near the end, merge by hand.
* `EndpointPolicyTests` lists the public routes exactly: this step added `unsubscribe` and `products/{slug}/notify-me`.
* `ApiFactory` disables `MarketingJobs:*`; any other factory that builds its own settings needs the same two lines.
* The web shop should show a "notify me" form on a sold-out variant (calls the public endpoint) and an unsubscribe page that reads `token` and POSTs it (or set `Marketing:UnsubscribeUrl`).
* OpenAPI: regenerate the front-end client after merge (`npm run gen:api`).

## 7. Gate numbers

Run in the builder worktree, from inside `juvo-be`:

* `dotnet build --no-incremental`: 0 warnings, 0 errors.
* Domain tests 339 passed; application tests 119 passed (24 of them new, in `Marketing/`); integration tests 556 passed, 0 failed (the whole project, including the existing Payments, Orders, Storefront, Returns, Operations and Support classes and `EndpointPolicyTests`). New integration classes: `SegmentTests` (8), `CampaignTests` (19), `AutomationTests` (9), `MarketingRateLimitTests` (2).
* `dotnet ef migrations has-pending-model-changes`: no changes.
* `dotnet list package --vulnerable --include-transitive`: no vulnerable packages in any project.
* Leftover `JuvoDb_Test_*` databases: see the hand-over report.
