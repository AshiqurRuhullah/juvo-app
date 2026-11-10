# Assumptions and decisions

Every decision the build needed that the SRS left open, or where the master prompt overrides the SRS.
Format: **decision**, reason, how to change it. Business rules in the SRS win over this file; the master prompt wins on technology and structure.

Last updated: 2026-11-02 (Step 1).

---

## A. Environment and Git validation (recorded in Step 1)

| Item | Result |
| --- | --- |
| Machine | Windows 10 Pro 10.0.19045, Git 2.51.0, .NET SDK 10.0.301, Node v26.3.0, npm 11.16.0, Python 3.14.7 |
| Remote | `https://github.com/AshiqurRuhullah/juvo-app.git` |
| `git ls-remote` | Reachable. Default branch `main`. Not empty: one commit `0ec0117 Initial commit` containing only `README.md` (`# juvo-app`). |
| Integration | Local repo created with `git init -b main`, `origin` added, `git fetch`, then `git checkout -B main origin/main` (no local commits existed, so nothing was rewritten). No force operations. |
| Push | **BLOCKED (HTTP 403).** `remote: Permission to AshiqurRuhullah/juvo-app.git denied to ashiqurgit.` The credential cached in Windows belongs to GitHub user `ashiqurgit`, which has no write access. Local commits are kept; push once access is fixed (add `ashiqurgit` as a collaborator with write access, or sign in as `AshiqurRuhullah` via Git Credential Manager / `gh auth login`). |
| Git identity | `H.N.ASHIQUR RUHULLAH <hnashiq@erainfotechbd.com>` (existing global config, unchanged). |
| Source files committed | **Yes.** `Documents/*` and `Logo/*` are small (80 KB and 60 KB), so they are committed as source material. No Git LFS needed. |
| Global git setting | `core.autocrlf=true` is set machine-wide; `.gitattributes` in the repo enforces LF for source files and CRLF for `.ps1`/`.sln`. |

## B. Technology decisions (master prompt overrides SRS section 4.3)

| Decision | Reason | How to change |
| --- | --- | --- |
| **Microsoft SQL Server** instead of PostgreSQL | Master prompt requires it. | Different EF provider + migrations rebuild. Not planned. |
| **No Redis, no Docker.** `IMemoryCache` for caching; SignalR without a backplane; background jobs through the transactional outbox + hosted service. | Master prompt rule 0.7. Fine for one server. | Add a Redis backplane/cache adapter behind the same interfaces if the site is scaled to several servers. |
| Local database: **SQL Server LocalDB `(localdb)\MSSQLLocalDB`, version 15.0.4382 (SQL Server 2019 Express)**, compatibility level 150. It was the only reachable instance (`localhost` and `.\SQLEXPRESS` are not installed). Database `JuvoDb` created by `scripts/setup-db.ps1` with the UTF-8 collation `Latin1_General_100_CI_AS_SC_UTF8`; all text columns use `nvarchar` so Bengali is safe regardless. | Express/LocalDB is enough for development and demos. | Pass `-Server` to the script for another instance. For production use SQL Server 2022+ (Express limits: 10 GB per database, 1 GB RAM). |
| **EF Core 10 vs SQL Server 2019 (verified in Step 2):** EF Core 10.0.14 runs against LocalDB 2019 (compatibility level 150) with `UseCompatibilityLevel(150)`; migrations applied, `/health/ready` healthy, 69 tests pass on it. The data model does not use JSON/vector features that need level 160+. | Avoid blocking on a SQL Server install. | Set `Database:CompatibilityLevel` (and install SQL Server 2022 Developer) if a feature needs 160; rerun `setup-db.ps1 -Server localhost`. |
| Integration tests use a **separate local database** (`JuvoDb_Test`, created and dropped by the test fixture). | Master prompt: real SQL Server, no containers. | `scripts/setup-db.ps1 -Database JuvoDb_Test`. |
| Front end: Next.js latest stable (App Router). `npm view next version` returned **16.4.3** on 2026-11-02; confirmed again at scaffold time in Step 4. Node v26.3.0 is installed. | Master prompt: latest stable. | Pin versions in `package.json`; upgrade deliberately. |
| Backend: .NET **10.0.301** SDK installed; target `net10.0`. | Installed SDK. | n/a |
| Python is used only for helper scripts (`docx_to_md.py`, `extract_palette.py`, `generate_tokens.py`). `pillow 13.0.0` was installed with `pip` for palette extraction. | No pandoc on the machine. | Scripts are stdlib-only except `extract_palette.py` (Pillow). |
| Maps: **Leaflet + OpenStreetMap by default**, Google Maps switchable by environment flag. | SRS 14.5 Q10: lower cost, no key needed for sandbox. OSM address quality in Bangladesh is weaker, so the pin always needs a text landmark (SRS 14.4). | `NEXT_PUBLIC_MAP_PROVIDER=google` plus a key. |
| Provider adapters (courier, payment, SMS, email, WhatsApp, maps): interface + **Sandbox adapter on by default**; named real adapters (Pathao, Steadfast, RedX, Paperfly, eCourier, DHL, bKash, Nagad, SSLCommerz, aamarPay, Stripe, PayPal) are configuration slots, **disabled**, built in Step 14 only against provider documentation and credentials supplied by the owner. | SRS section 4.6: provider details are unverified. | Step 14. |
| Provider onboarding (courier, gateway, SMS sender ID, map key) is **not needed until Step 14**, but takes time; the owner should start it now. | Step 0 note. | n/a |

## C. Answers to the SRS open questions (section 14.5)

| # | Question | Decision (default) | Change by |
| --- | --- | --- | --- |
| 1 | Logo and colours | Extracted from `Logo/Official Logo.jpg`: swoosh red `#DB0100` is the primary, near-black `#1C0807` the secondary, cream `#EBE5DB` the accent. The SRS plum/gold starter palette is **not used**. Full table with contrast checks in `docs/brand-tokens.md`. | Edit palette in `scripts/generate_tokens.py` and rerun. |
| 2 | Categories and sizes | Seed top-level categories **Clothing, Shoes, Ladies' Dresses, Purses** with sub-categories. Clothing sizes: XS, S, M, L, XL, XXL, Free size. Shoe sizes: EU 35-46. Colour as free label plus swatch hex. | Admin > Categories / size charts. |
| 3 | Couriers | Sandbox courier enabled. Pathao, Steadfast, RedX, Paperfly, eCourier (domestic) and DHL (international) exist as disabled placeholders. | Admin > Couriers (Step 10/14). |
| 4 | Payment methods at launch | **COD** and the **Sandbox online gateway** enabled. bKash, Nagad, Rocket, SSLCommerz, aamarPay, Stripe, PayPal and bank transfer (manual verification) are disabled slots. Trade licence / merchant account status unknown. | Admin > Payments. |
| 5 | COD advance rules | COD above **BDT 10,000** requires an advance of the delivery charge (BR-D1); first-time-customer COD is flagged for review (module T) but not forced. OTP before confirm: **COD only** (FR-D04 switch = "COD only"). | Settings centre. |
| 6 | International | Module is built; one sample destination (**India**) is enabled in Sandbox so the acceptance test can run; all other countries disabled. Duties and taxes are **paid by the receiver (DAP)**, stated clearly at checkout. International max parcel weight 20 kg, restricted-items list starts empty and is admin-editable (BR-G3). | Admin > Delivery > Countries. |
| 7 | Stock deduction point | **Reserve at order placement, deduct at packing**, release on cancel/failure/timeout. Applied everywhere (SRS 12.2 asks to decide once). | Setting `Inventory.DeductAt = Packing|Delivery`. |
| 8 | Returns | Return window **7 days from delivery**, per-category override. Non-returnable: none by default (owner to list). Delivery charge on a customer-reason return is not refunded; on a seller-fault return it is. | Admin > Settings > Returns, category policy. |
| 9 | VAT / tax | Prices stored **tax-inclusive**. A sample tax rule of **15%** is seeded **inactive** and marked "unverified: confirm with an accountant" (SRS 15, Module U note). Invoices and credit notes are issued regardless; the VAT lines appear once a rule is activated. | Admin > Tax. |
| 10 | Maps | See section B: Leaflet + OSM. | n/a |
| 11 | Language | **English default, Bengali switchable from launch** (full translation pass in Step 12). | Settings. |
| 12 | Staff and roles at launch | The seven default roles (Super Admin, Manager, Order Manager, Inventory Manager, Content Editor, Support, Accountant). | UMS. |
| 13 | Brand content | The About page, policies and FAQ are seeded as **editable drafts built only from facts in the SRS** (business name, Facebook `facebook.com/juvobd`, email `juvobd@gmail.com`, what is sold). The owner replaces story text, photos, tagline and the pickup address in the CMS. Nothing invented about the brand's history. | Admin > CMS. |
| 14 | Reviews | Enabled at launch, **verified buyers only, moderated** (SRS FR-A12). | Settings. |

## D. Other decisions and conflicts

| Topic | Decision | Reason |
| --- | --- | --- |
| Loyalty | SRS 2.5 lists loyalty points as out of scope for v1, but SRS section 15 defines module AB (Phase 4, priority Could). **Built in Step 11B** as an optional module behind a feature switch, default off. | Master prompt and step plan include it; the feature flag keeps v1 behaviour unchanged. |
| POS / multi-branch | SRS module AE is "Phase 5, only if needed". The master prompt makes **multi-branch part of the data model from the first migration** (section 4A); a basic POS is built in Step 11B. | Master prompt overrides. |
| Branch numbering | Order numbers: `JV-<BRANCHCODE>-<YYMM>-<6-digit sequence>`, e.g. `JV-DHK01-2611-000123` (SRS example `JV-2611-000123` plus branch prefix per master prompt 4A.3). Sequence is gap-free per branch (invoice and credit-note numbers) and unique per branch+month for orders. | BR-D3 + 4A. |
| Single-branch default | Seed **Head Office (`HO`)** (default, global scope for Super Admin) and **Online Fulfilment**. UI hides branch controls when only one active branch exists. | 4A.3. |
| Admin app | One Next.js app with `(admin)` route group, not a separate SPA repo. | Master prompt section 5.2; simpler, shared design system. |
| Repositories | One Git repository with `juvo-be` and `juvo-front` folders (SRS 16.2 suggests several repos). | Master prompt section 0A. |
| Time and money | Store `datetimeoffset` in UTC, display Asia/Dhaka; money `decimal(18,2)` plus currency code. Mobile numbers normalised to `+880...`. | SRS 12.2. |
| Order timeout | Unpaid online-payment orders are cancelled after **30 minutes**, stock released (BR-D2). | SRS example value. |
| Cart/reservation | Cart does not reserve stock; stock is reserved at order placement only. | Keeps abandoned carts from blocking stock. |
| Invoice PDF library | Chosen in Step 9 after checking its current licence (QuestPDF has a Community licence with revenue limits: verify). | Master prompt rule 0.2. |
| SMS / email | Sandbox senders write to the notification log (viewable in admin). Real SMS needs a provider with a registered sender ID (Step 14). | SRS 13.2. |
| Dataset of Bangladesh divisions/districts/upazilas | Chosen and sourced in Step 7 (verified against a maintained open dataset); the source is recorded in this file then. If upazila data is incomplete, divisions and districts are complete and upazilas importable. | SRS FR-G02. |
| Default admin credentials | Development-only Super Admin seeded in Step 3 with a forced password change at first login; credentials documented in the README, never reused in production. | Master prompt section 7. |
| Dark mode | Light is the default; a dark theme is included in the generated tokens (admin first). | SRS 3.3 "Could". |
| Danger colour | The brand colour is red, so the danger/error colour is a deeper crimson and is always paired with an icon and text. | `docs/brand-tokens.md`. |

## E. Step 2 (backend foundation) decisions

| Decision | Reason | How to change |
| --- | --- | --- |
| **No mediator library.** A small in-process dispatcher with pipeline behaviours (logging, permission, validation, transaction). | MediatR changed to a commercial licence; this avoids the risk. | Replace `Juvo.Application/Messaging` if ever needed. |
| **FluentValidation 12 (Apache-2.0)** for request validation; **no mapper library yet** (hand-written mappers; Mapperly may be added later). | Licences checked on nuget.org. | n/a |
| **Scalar (MIT) for the API UI** and **Asp.Versioning 10.x (MIT)** with the built-in OpenAPI generator (`Microsoft.AspNetCore.OpenApi`). One document per API version at `/openapi/v1.json`. | Built-in generator plus a maintained UI. | `OpenApi:Enabled` (on in Development, off by default elsewhere). |
| **Built-in logging with the JSON console formatter** (simple console in Development) instead of Serilog. Scope carries the correlation id. | Fewer dependencies. | Add Serilog sinks in Step 13/14 if central logging needs them. |
| **xunit v3 (Apache-2.0) with plain asserts**; no FluentAssertions. `global.json` selects the Microsoft Testing Platform runner for `dotnet test`. | FluentAssertions moved to a paid licence; .NET 10 requires the new runner. | n/a |
| **Tests use a real local SQL Server**, one throw-away `JuvoDb_Test_<guid>` database per test class, created from migrations and dropped afterwards; only names with that prefix may be dropped. | Master prompt: no Docker. | `JUVO_TEST_SQL_SERVER`. |
| **Transitive pin `System.Security.Cryptography.Pkcs 10.0.14`** in `Directory.Packages.props` because Microsoft.Data.SqlClient 6.1.7 pulls 9.0.11, which NuGet audit flags as vulnerable (moderate and high). | Warnings are errors; do not ship known vulnerable packages. | Remove the pin when SqlClient ships a fixed dependency. |
| **Audit log JSON keeps PascalCase property names** (the EF property names) and omits `RowVersion`, created/updated stamps and `[AuditIgnore]` properties. Soft deletes are recorded as action `Deleted`. | Matches the column names; no secrets in the log. | n/a |
| **Outbox delivery is at-least-once.** Each message runs in its own DI scope so a failing handler cannot leak changes. Retry backoff 5 s doubling up to 60 min, 8 attempts, then status `Failed` (manual requeue). Claiming uses `UPDLOCK, READPAST`. | Reliable notifications/webhooks (NFR-020). | `Outbox:*` settings. |
| **Rate limits are per client IP** (global 300/min; policies `auth` 10, `otp` 5, `checkout` 20 per minute). Behind a proxy, forwarded headers and the known proxy must be configured first (Step 14). | OWASP / NFR-007. | `RateLimiting:*`. |
| **Permission checker denies everything by default** until the Identity module (Step 3) registers the real one. A request implementing `IRequirePermission` is rejected with 401/403. | Secure by default. | Step 3. |
| **Migrations are applied automatically only in Development** (`Database:AutoMigrate`); Production uses `dotnet run --project src/Juvo.Api -- --migrate`. | Safer deployments. | n/a |
| **API ports:** HTTPS 7080, HTTP 5080. | `launchSettings.json`. | n/a |

## F. Step 3 (UMS backend) decisions

| Decision | Reason | How to change |
| --- | --- | --- |
| **Staff accounts use ASP.NET Core Identity** (`StaffUser`, UserManager) for password hashing, lockout, TOTP and reset tokens, but **no Identity roles**: roles, permissions and per-branch assignments are our own tables. | Roles must be per branch and permission-based. | n/a |
| **Access tokens carry no permissions**, only a permission version (`pv`). Permissions are looked up per request (cached by user and version). A role, assignment, branch or password change bumps `pv`; the old token answers `401 AUTH.TOKEN_EXPIRED` and the client refreshes silently. | Role changes take effect in seconds without long-lived stale claims. | `UserStateCache` lifetime is 30 s, invalidated on change. |
| **"Revoke on role change"** is implemented as invalidating access tokens (via `pv`), **not** revoking refresh tokens. Logout, logout-all, password change/reset, force-logout, deactivation, two-step reset and reuse detection do revoke refresh tokens. | Master prompt asks for quick effect; forcing a re-login on every role edit would be hostile. | Revoke in `RolesService.SetPermissionsAsync` if the owner prefers. |
| **Refresh-token reuse detection is strict**: a used or revoked token cuts the whole family. Two simultaneous refreshes with one token let at most one win. The front end must refresh with a single in-flight request. | Master prompt 4.2. | n/a |
| **JWT signing key** from `Auth:SigningKey` (user-secrets / environment, 32+ chars). Development with no key generates a random one per run (warning logged); Production without a key refuses to start. | Never commit secrets. | Persistent dev key via `dotnet user-secrets set "Auth:SigningKey" "..."`. |
| **Development seed account** `admin@juvobd.local` / `Juvo@Dev#2026` is committed in `appsettings.Development.json`: dev-only, forced password change at first sign-in. Production creates no account unless `Auth__Seed__Password` is set. | Master prompt section 7. | Change or remove the value for any shared environment. |
| **`Auth:RequireTwoFactorForSuperAdmin` is true by default and false in `appsettings.Development.json`** so developers are not forced to set up an authenticator app. With it on, a Super Admin without 2FA gets a *limited* token that only reaches setup endpoints. | SRS FR-O01. | Set it to true in Development to test the real flow (covered by tests). |
| **Limited tokens** (`scope=limited`) for forced password change and 2FA setup. | Safe first sign-in without a special flow. | n/a |
| **Lockout:** 5 wrong passwords or codes lock the account 15 minutes (`423 AUTH.LOCKED`). Unknown users and wrong passwords return the same error. | FR-O05, NFR-007. | `Auth:MaxFailedAccessAttempts`, `Auth:LockoutMinutes`. |
| **Password policy:** 10+ characters, upper, lower, digit, symbol, 4 distinct characters. | OWASP baseline. | `Auth:PasswordMinLength`. |
| **A role with no branch (`BranchId` null) only counts for users flagged global.** `X-Branch-Id: all` is allowed for global users and for users holding `ums.branch.switch-all` (still limited to their branches). Without the header, a global user works in "All branches", a one-branch user uses it, and a multi-branch user has no active branch. | Master prompt 4A.1. | n/a |
| **Branch scope is central:** EF global query filter (named filter "BranchScope") for `IBranchScoped` entities plus a save guard; branch/user/audit lookups in UMS enforce scope in the services. No `IBranchScoped` entity exists yet; orders, stock etc. adopt it from Step 6. | Master prompt 4A.3. | n/a |
| **Out-of-scope branch access answers 404** (nothing revealed) when the actor cannot see the branch at all, or 403 when they can see it but lack the permission, and **every refused attempt is written to the audit log** (`AccessDenied`). | Master prompt 4A.3. | n/a |
| **No privilege escalation:** granting a role requires holding all its permissions; only Super Admin grants Super Admin; branch admins manage only users who work entirely in branches where they hold the permission; the last active Super Admin cannot be deactivated or unassigned. | Security. | n/a |
| **Customers** have a thin identity now (mobile, quick profile via OTP). OTP: 6 digits, 5 minutes, 5 attempts, 60 s resend cooldown, 5 requests/hour/number, 5/min/IP. OTP SMS goes through the sandbox sender into `NotificationLogs`; the code is stored readable only when `Notifications:StoreOtpPlaintext` is true (Development and tests), otherwise masked. | SRS FR-E05, K. | `Auth:Otp:*`. |
| **Notification log table** (`NotificationLogs`) exists now (sandbox SMS/email); templates, retries and the admin viewer come in Step 9. Password-reset emails are written there as well (the link is readable in Development/tests). | Run end to end without providers. | Step 9. |
| **Data protection keys** (reset tokens, authenticator keys) persist to `App_Data/keys` (gitignored; `DataProtection:KeysPath` overrides). Production must back them up and keep them across deploys (Step 14). | Tokens survive restarts. | n/a |
| **Refresh tokens, sign-in history, OTP codes, notification logs, audit and outbox rows are not copied into the audit log** (`INotAudited`); password hashes and security stamps are never audited. | No secrets in the audit log. | n/a |
| **Admin list endpoints** return only what the actor's branches allow (users, branches, audit rows, sign-ins). | Branch isolation. | n/a |
| **Only a global-scope user can create branches, change global scope or set the default branch.** Branch activate/deactivate use the `ums.branch.deactivate` permission. | Company-wide actions. | n/a |
| **Connection strings no longer use MARS** (it disabled EF savepoints and logged a warning on every transaction); nothing in the code needs it. | Cleaner transactions. | n/a |

## G. Step 4 (front-end foundation) decisions

| Decision | Reason | How to change |
| --- | --- | --- |
| **Next.js 16.4 App Router with `proxy.ts` (Node runtime)**; `cacheComponents` and partial prefetching are off (classic dynamic rendering). | Every admin page depends on the session cookie, so nothing is shareable between users; the new caching model adds risk with no gain yet. | Turn on per route group when public catalogue pages arrive (Step 7). |
| **Backend-for-frontend:** tokens live only in httpOnly cookies; the browser calls `/api/bff/...`; token-returning endpoints are blocked in the generic proxy and have dedicated cookie-setting routes. | SRS NFR-007 and master prompt 4.2: no tokens in JavaScript or local storage. | n/a |
| **One in-flight refresh per refresh token** (shared promise plus a 10 s reuse window), because the API treats a reused refresh token as theft and cuts the session. | `docs/auth-and-access.md`. | `REUSE_WINDOW_MS` in `src/lib/server/refresh.ts`. |
| **Process-local refresh store** (a Map on `globalThis`). Correct for one Node process; a second server would need a shared store. | One app server for now. | Move the map to Redis/SQL before running several instances (Step 14). |
| **Language by cookie, no URL prefix** (`juvo_locale`, en/bn, next-intl 4). | One URL per page, no duplicate-content or redirect rules; SEO language URLs can be added with the storefront. | Switch to `/bn/...` routing in Step 7 if the owner wants indexable Bengali pages. |
| **Bengali strings are first-pass translations** and need review by a native speaker before launch. | I cannot verify tone and vocabulary. | Review `src/messages/bn.json` in Step 12. |
| **TanStack Table is pinned to v8.21.3.** v9 is a different, store-based API without a stable ecosystem yet. | Stability. | Upgrade deliberately later. |
| **`react-leaflet` is not used** (Hippocratic-2.1 licence, not OSI open source). Step 7 uses plain Leaflet (BSD-2). | Master prompt: verify licences. | n/a |
| **`@axe-core/playwright` (MPL-2.0) is a dev-only dependency**, never shipped. | Licence check passed for test tooling. | n/a |
| **`npm audit`:** production dependencies 0 vulnerabilities; 18 moderate findings (minimatch DoS) exist only in dev tooling and are not reachable at runtime. | Fixes require breaking downgrades of the tool chain. | Re-check each step. |
| **`/design` component gallery is development only:** 404 in production unless `ENABLE_DESIGN_PAGE=true` (the e2e run sets it). | No internal pages on the public site. | n/a |
| **Menu items for screens that are not built are shown muted with a "Soon" tag**, not hidden and not linked. Each later step flips `ready`. | Shows the final structure and avoids dead links. | `ready` in `src/lib/nav.ts`. |
| **Icon rail labels are visually hidden text, not `display:none`;** on phones language and theme are inline items of the account menu (nested dropdowns do not work on touch). | Found by the automated accessibility and device tests. | n/a |
| **QR codes for 2FA setup are drawn in the browser with `qrcode`** (MIT) from the otpauth URI; the secret is shown as text too. | No third-party QR service sees the secret. | n/a |
| **Per-IP rate limits behind the BFF:** the BFF forwards `X-Forwarded-For`, but the API does not trust forwarded headers yet, so in production it sees the app server's address. | Needs the proxy topology (Step 14). | Add `UseForwardedHeaders` with known proxies in Step 14. |
| **Phone numbers accept Bengali and other Unicode digits** (`PhoneNumber.Normalize` maps them to 0-9; the regex uses `[0-9]`, not `\d`, which also matches Bengali digits). | The Bengali form hint shows Bengali digits and customers type in their own script. | n/a |

## H. Step 6-c (inventory) decisions

| Decision | Reason | How to change |
| --- | --- | --- |
| **One stock location per branch** (no separate shop/warehouse/home locations inside a branch). The ERD's `STOCK_LOCATION` is folded into `StockLevel` keyed by (branch, variant). A warehouse is simply a branch of type Warehouse. | Simpler, and branches already model shops, warehouses and online fulfilment (4A). | Add a `StockLocation` table between branch and level if one branch ever needs several shelves. |
| **Stock is per variant and branch (`StockLevel`), changed only through `StockLedger`**, which writes an append-only `StockMovement` (with the on-hand after the change) in the same save. `StockLevel` and `StockMovement` are not copied into the audit log (the ledger is the record). | The ledger must always explain the number on the shelf. | n/a |
| **Reserved never exceeds on-hand** (database check). Manual removals and count corrections that would drop below reserved units are refused (`INVENTORY.INSUFFICIENT_STOCK`). | Reserved units are promised to orders (reserve at placement, deduct at packing, assumption Q7). The reserve/release/deduct operations exist on `StockLevel` now and are used by Step 8. | n/a |
| **Adjustment reasons** by hand: opening stock, correction, damage, loss, found, customer return (sign rules: damage and loss remove, opening stock, found and customer return add, correction either). Transfer and count reasons are system-only. Damaged units are a reason, **not** a separate "damaged" quantity (SRS FR-V05 partial). | Keeps one number per variant; damaged stock leaves the shelf count. | Add a damaged quantity column if the owner wants to keep damaged goods on the books. |
| **Transfers:** either branch may request (permission `inventory.transfer.request` in either); the **source** approves, rejects and ships (`inventory.transfer.approve`); the **destination** receives (`inventory.transfer.receive`). Stock leaves the source at **ship** and enters the destination at **receive**, each with a movement on its own side. Approval checks availability, ship checks again. Cancelling in transit returns the goods to the source and needs the cancel permission there. Receipts are **full only** (no partial or short receipts yet). | Matches SRS 4A.3; each action writes only the active branch's stock, so central branch isolation holds. | Partial receipts with a loss-in-transit movement can be added later. |
| A transfer touches two branches, so it is **not** `IBranchScoped`; the service shows it to users who hold `inventory.transfer.view` in either branch. A destination user acting on a source-only step gets 403 (not 404), because they can already see the transfer; unassigned branches answer 404 and are audit-logged. | A single branch filter would hide the transfer from one side. | n/a |
| **Stock counts:** one open count (in progress or submitted) per branch. Starting snapshots the system quantity of every stocked variant (or the variants named). On approval the difference **counted minus snapshot** is applied to the current on-hand (so sales during the count are not overwritten); uncounted lines change nothing; a count can be reopened by the approver or cancelled. Barcode scanning is not built (FR-V01 not started). | Practical stocktake that survives live trading. | n/a |
| **Document numbers** `TR-yyMM-xxxxxx` (transfers) and `SC-yyMM-xxxxxx` (counts): year-month plus the last 6 hex digits of the record's time-ordered GUID, unique index. These are working documents, not tax documents, so gaps are fine (invoices stay gap-free, Step 9). | No sequence table needed yet. | Move to a per-branch sequence if the owner wants them consecutive. |
| **Endpoints prove the permission somewhere; services prove it in the branch the action targets** (explicit `branchId`, else the active branch; a global user with no active branch must name one). Lists only include branches where the user holds the matching `view` permission. | Stock is per branch, but `RequireBranchPermission` only knows the active branch. | n/a |
| **A variant that still has stock (on hand or reserved, in any branch) cannot be removed** (`INVENTORY.VARIANT_HAS_STOCK`). | Units must not become invisible. | n/a |
| Concurrency: `StockLevel` uses the row version; a lost race answers `409 GENERAL.CONCURRENCY_CONFLICT` and the client retries. Verified with 12 parallel adjustments (the ledger always sums to on-hand) and two simultaneous shipments of the last units. | NFR-019. | n/a |
