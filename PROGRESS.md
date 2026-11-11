# PROGRESS

Resume from this file alone. Plan: `Documents/juvo-step-prompts.md` (14 steps). Master prompt: `juvo-claude-code-prompt.md`.
Last updated: 2026-11-10.

## Status of the 14 steps

| Step | Title | Status |
| --- | --- | --- |
| 1 | Understand the business, Git and project docs | **Done** |
| 2 | Backend foundation | **Done** |
| 3 | UMS backend: auth, roles, permissions, branches | **Done** (branch `feature/03-ums-backend`, merged to `main`) |
| 4 | Front-end foundation, brand, login, token handling | **Done** (branch `feature/04-front-foundation`, merged to `main`) |
| 5 | UMS user interface | **Done** (branch `feature/05-ums-ui`) |
| 6 | Catalog, media, branch-wise inventory | **Done** (6-a to 6-e; branch `feature/06-catalog-inventory`, not yet merged to `main`) |
| 7 | Storefront, cart, customer identity, address, delivery zones | **Done** (7-a backend, 7-b front end; gated 2026-11-11) |
| 8 | Checkout, orders, order processing | In progress |
| 9 | Payments, tax/invoices, notifications | Not started |
| 10 | Couriers, real-time status, returns | Not started |
| 11A | Operations part 1: purchasing, expenses, support, reports | Not started |
| 11B | Operations part 2: promotions, pre-order, loyalty, marketing, reseller, POS | Not started |
| 12 | Remaining admin UI, CMS, dashboard, settings, platform services | Not started |
| 13 | Hardening, QA, documentation | Not started |
| 14 | Real providers and go-live preparation | Not started |

## Steps 1 and 2 (done)

See git history and `docs/`. Step 1: Git, docs, brand tokens, `setup-db.ps1`. Step 2: .NET 10 solution, Result/pagination, base entities, audit and outbox interceptors, worker, API host (ProblemDetails, health, versioning, OpenAPI), 69 tests.

## Step 3 (done): UMS backend

Contract for the front end: **`docs/auth-and-access.md`**. Decisions: `docs/assumptions.md` section F. Endpoint list: `/scalar/v1` (53 operations).

- **Auth (staff):** ASP.NET Core Identity users, password policy (10+ chars), lockout (5 failures, 15 min), optional TOTP 2FA with recovery codes (mandatory for Super Admin via a *limited* token), sign-in history, forgot/reset/change password, forced change for new accounts.
- **Tokens:** JWT access (15 min) + rotating refresh tokens stored hashed with device/IP/user-agent, reuse detection cuts the family, concurrent refresh safe; revoked on logout, logout-all, password change/reset, force-logout, deactivation. Permission-version stamp (`pv`): role/branch/password/active changes make old access tokens answer `AUTH.TOKEN_EXPIRED` so clients refresh silently.
- **Permissions:** code-defined catalog (234 `module.resource.action` codes covering every SRS module, hash-versioned), seeded idempotently; 7 default roles per the roles matrix; Super Admin holds everything including future permissions; system roles protected; endpoint policies (`RequirePermission`/`RequireBranchPermission`).
- **Branches:** CRUD with rules (one DB-enforced default, default/inactive protections), `UserBranchRole`, global scope, `X-Branch-Id` validated on every request (refusals audit-logged), `/auth/can`, `/auth/me` with roles and permissions per branch. Central data isolation: EF named query filter + save guard for `IBranchScoped` entities. Seed: Head Office (default) + Online Fulfilment.
- **UMS admin endpoints:** users (CRUD, activate/deactivate, reset password, force logout, reset 2FA, sessions, sign-ins, effective permissions, branch roles), roles (CRUD, clone, permissions), permission catalog, branches, audit-log search, sign-in history. No privilege escalation; branch-scoped admins see only their branches' users and logs.
- **Customers (thin):** mobile + OTP sign-in (6 digits, 5 min, 5 attempts, cooldown, hourly cap), quick profile created on first verify, sandbox SMS to `NotificationLogs`.
- **Data:** migration `IdentityBranchesCustomers` (14 tables), connection strings without MARS.

**Gate result (all run and seen passing):**
- `dotnet build --no-incremental`: 0 errors, 0 warnings (warnings are errors).
- `dotnet test`: **210 passed, 0 failed**, run three times in a row after fixing a race in token revocation: domain 73, application 10, integration 127 (real SQL Server, one throw-away database per class). Covered: login, lockout, forged/expired tokens, refresh rotation, reuse detection, concurrent refresh, hashed storage, logout-all, password and forced change, forgot/reset, 2FA incl. recovery codes and mandatory Super Admin 2FA, customer OTP, user/role/branch management, no escalation, role change effective via token refresh, branch isolation (404 plus audit), `X-Branch-Id` validation, per-branch roles, global scope, data-layer filter and write guard, endpoint policy inventory, seeder.
- Real run on `JuvoDb`: migration applied, seed created 234 permissions, 7 roles, 2 branches and the Super Admin; dev login returns a limited token (forced password change); `/auth/me` shows HO and ONL; `/ums/users` with the limited token answers 403; OpenAPI lists 45 paths and Scalar loads.
- `has-pending-model-changes`: none. `dotnet list package --vulnerable --include-transitive`: none. No leftover `JuvoDb_Test_*` databases.

## Step 4 (done): front-end foundation

- **App:** Next.js 16.4 (App Router, strict TypeScript, Tailwind 4) in `juvo-front`; details and route/menu maps in `juvo-front/docs/frontend.md`, run instructions in `juvo-front/README.md`.
- **Session:** backend-for-frontend; tokens only in httpOnly cookies; one in-flight refresh; `proxy.ts` keeps the session fresh; token endpoints blocked in the generic proxy; open redirects refused.
- **Typed API client** generated from the API's OpenAPI file (`npm run gen:api`); the API now declares response types on all 53 operations.
- **Design system** from the logo (light and dark), forms, dialogs, menus, tabs, select, data table, toasts; dev-only `/design` gallery.
- **Screens:** staff login with 2FA step, customer OTP login, forgot/reset password, forced first-login, admin shell (sidebar / rail / drawer + bottom bar, branch switcher, command palette, notifications bell, language and theme), profile with security panel, 401/403/404/error pages, account and storefront placeholders. English and Bengali.
- **Small backend fix:** `PhoneNumber.Normalize` accepts Bengali and other Unicode digits.

**Gate result (all run and seen passing):**
- `npm run lint`: clean. `npx tsc --noEmit`: clean. `npm run build`: succeeds.
- `npm test`: 39 unit tests passed. `npm run test:e2e`: **104 passed** (desktop, tablet, Android, iPhone) against the real API and a throw-away database; includes login, silent refresh, logout, role change without re-login, per-role and per-branch menus, forged branch cookie.
- UI checks at 360 / 768 / 1280 px on all 12 pages: no horizontal overflow, no tap target under 44 px, no axe violations, no console errors; 200% and 400% zoom pass. Key screenshots in `juvo-front/docs/ui-checks/`.
- Backend: all test projects run **213 passed** (domain 76, application 10, integration 127).
- Unbuilt modules are shown as "Soon" by design.

## Step 5 (done): UMS user interface

- **Screens** under `/admin/ums/` (code in `juvo-front/src/modules/ums`): users (list, create with one-time temporary password, edit, activate/deactivate, reset password, force logout, reset 2FA, sessions, sign-ins, branch-role assignment, effective permissions per branch, global scope), roles (list, create/edit/clone/delete, module-grouped permission matrix with select-all), branches (list, create/edit with Leaflet map pin, activate/deactivate, set default), audit log, sign-in history. Menu items flipped to `ready`; new `ums.sign-in.view` item.
- **Small backend fix:** `Branch` default order prefix is cut to 10 characters (a 20-character code overflowed the column); domain test added.
- English and Bengali strings added for all screens.

**Gate result (all run and seen passing):**
- `npm run lint` and `npx tsc --noEmit`: clean. `npm run build`: succeeds. `npm test`: 39 passed.
- `npm run test:e2e`: **150 passed**, 3 skipped (desktop-only sidebar test on other projects), against the real API and a throw-away database. UMS flows: create a role with a few permissions, matrix select-all, create a user and assign branch + role, log in as that user and see only the allowed menus in their own branch, deactivate/reactivate, create a branch with a map pin and deactivate it, audit and sign-in history show the actions.
- UMS UI checks at 360 / 768 / 1280 px on every UMS page: no horizontal overflow, no tap target under 44 px, no axe violations, no console errors.
- Backend: build 0 warnings 0 errors; **214 passed** (domain 77, application 10, integration 127).

## Step 6 (in progress): catalog, media, inventory

Split (the step is too large for one session); each sub-step has its own gate and commit.

| Sub-step | Scope | Status |
| --- | --- | --- |
| 6-a | Catalog backend: categories (tree), brands, size charts, products, variants, pricing/sale dates, publish rules BR-B1..B3, duplicate, cost-price visibility, audit, seed | **Done** |
| 6-b | Image pipeline: upload, validate, resize, WebP, focal point, blur placeholder, per-colour sets, reorder (`IFileStorage`) | **Done** |
| 6-c | Inventory: branch stock locations, on-hand/reserved/available, append-only movements, adjustments, thresholds, transfers, stocktake | **Done** |
| 6-d | Bulk CSV/Excel import and export with validation report; sample products with images | **Done** |
| 6-e | Admin UI: product list, add/edit with live card preview, variants/stock grid, categories, inventory by branch, transfers, adjustments; Playwright and screenshots | **Done** |

**6-a result:** endpoints under `/api/v1/catalog` (categories, brands, size-charts, products, variants, publish/unpublish/archive/duplicate), 21 new `Permissions.Catalog` constants (codes already in the catalog). Migration `CatalogProducts`. `CatalogSeeder` (4 top-level categories with sub-categories, 2 size charts) runs with the migration. `ProductImage` entity exists (publish needs one image); upload comes in 6-b. SKU unique among live variants (case-insensitive, freed on removal); slugs unique among live rows; staff without `catalog.product.view-cost` neither see nor change cost. Tests: 143 integration (20 new), 23 application (13 new), build 0 warnings, no pending model changes.

**6-b result:** pictures under `/api/v1/catalog/products/{id}/images` (multipart upload, update colour/alt/focal point, reorder, delete). Accepts JPEG/PNG/WebP, max 10 MB (measured from the stream, not the header), min 1000 px long side, pixel cap against decompression bombs, EXIF rotation applied, max 12 images per product. SkiaSharp (MIT) writes WebP renditions: main (long side up to 1600), 4:5 card (800x1000) and thumbnail (160x200) cropped around the focal point, plus a 16 px blur placeholder data URL. Changing the focal point re-crops; a duplicated product shares files (copy on write); delete removes files only when no product still uses them. Storage via `IFileStorage` (`/files`); DTOs return `url`, `cardUrl`, `thumbUrl`. Permissions `catalog.media.upload` / `catalog.media.delete`. No migration needed.
**6-b gate (run and seen):** `dotnet build --no-incremental` 0 warnings, 0 errors; test executables: domain 77, application 23, integration 151 (8 new media tests), 0 failed; `has-pending-model-changes`: none; no vulnerable packages; no leftover `JuvoDb_Test_*` databases.


**6-c result:** `/api/v1/inventory` (permissions `inventory.*`, constants in `Permissions.Inventory`). **Stock:** `GET /stock` (per variant and branch: on hand, reserved, available, threshold, filters branch/product/low-only/search), `GET /stock/products/{id}` (variants x branches grid), `POST /adjustments` (all-or-nothing lines with a reason: opening stock, correction, damage, loss, found, customer return), `PUT /reorder-levels`, `GET /movements` (append-only ledger with who/when/reason/reference). **Transfers:** request (either branch) -> approve -> ship (stock leaves the source, movement there) -> receive (stock enters the destination, movement there); reject, cancel (goods in transit go back to the source), per-user `allowedActions`. **Stock counts:** start (snapshot), enter counted quantities, submit, approve (books differences as movements), reopen, cancel. Domain `StockLevel` already has the reserve / release / deduct operations Step 8 will use. Database refuses negative stock and reserved above on-hand; `StockLevel` is `IBranchScoped` (central isolation) and a variant that still has stock cannot be removed. Migration `InventoryStock` (6 tables). Decisions: `docs/assumptions.md` section H.
**6-c gate (run and seen):** `dotnet build --no-incremental` 0 warnings, 0 errors; test executables: domain 94, application 23, integration 171 (20 new inventory tests incl. branch isolation with audit, per-branch permission, transfer flow with movements on both sides, invalid transitions, cancel in transit, count approval, reserved-units guard, 12 parallel adjustments with a ledger that adds up, two simultaneous last-unit shipments, DB check constraints), 0 failed; `has-pending-model-changes`: none; no vulnerable packages; no leftover test databases; migration applied on `JuvoDb`, `/health/ready` healthy, OpenAPI lists all 19 inventory paths.

**6-d result:** **Import/export** (`/api/v1/catalog/products/import`, `/export`, `/import-template`; permissions `catalog.product.import` and `.export`): CSV or Excel, one row per variant, mode `create` or `upsert` (blank cell keeps the value), `dryRun`, one transaction so a file with any error saves nothing, a report with row, column, severity and message, brands created on the fly, opening stock by `branch_code` (skipped when the variant already has stock history), cost price only for those who may see it, CSV-injection protection, 5 MB / 5,000 rows. Excel via Microsoft's MIT Open XML SDK (ClosedXML rejected: SixLabors licence). **Sample catalog** (`Seed:SampleCatalog`, on in Development): 16 published demo products, 90 variants, 31 generated placeholder pictures (labelled "SAMPLE PICTURE"), stock in HO and ONL, tag `sample`; already seeded in your `JuvoDb`; it never runs once a non-sample product exists. Product search now also matches tags. Decisions: `docs/assumptions.md` section I.
**6-d gate (run and seen):** `dotnet build --no-incremental` 0 warnings, 0 errors; test executables: domain 94, application 23, integration 190 (19 new: create/dry-run/all-or-nothing/row errors/upsert/stock history/permissions/export round trip/Excel validated with the Open XML validator/shared-string and sparse-cell reading/CSV quoting and injection/unusable files/sample catalog and its guard), 0 failed; real start on `JuvoDb` seeded 16 products and the generated pictures were fetched and looked at.

**6-e result (admin UI):** menu items Products, Categories and Inventory are live (`juvo-front/src/modules/catalog`, `modules/inventory`; routes in `juvo-front/docs/frontend.md`).
- **Products** `/admin/products`: list with search (name, slug, SKU, tag), status/category/brand filters, thumbnails, price with sale; editor with tabs *Details*, *Variants and stock*, *Pictures*, *Search and sharing*; **live storefront card preview** (the same premium `ProductCard` Step 7 will use) fed by the unsaved form; colour x size variant generator; stock-per-branch grid that books the difference in the ledger; picture upload (several at once, client checks), drag or button reorder, per-colour sets, focal-point editor with live 4:5 crop frame, delete; publish checklist from the API; publish, unpublish, archive, duplicate, delete draft; import/export buttons (check first, row-by-row report).
- **Categories** `/admin/categories`: category tree (add, edit, move up/down, delete empty), brands, size charts (grid editor).
- **Inventory** `/admin/inventory`: stock by branch (adjust with reason, low-stock level), movements ledger, transfers (request, approve, ship, receive, reject, cancel, driven by the API's allowed actions), stock counts (start, enter, submit, approve, reopen, cancel).
- English and Bengali texts per module (`src/messages/catalog.*`, `lookups.*`, `inventory.*`); Bengali is a first pass. Pictures are served at `/media`. Decisions: `docs/assumptions.md` section J.
- Work was split between three parallel builders (products, categories/import, inventory) over a shared foundation; the lead merged and verified everything.

**6-e gate (run and seen):** `npx tsc --noEmit` clean; `npm run lint` clean; `npm test` 39 passed; `npm run build` succeeds; `npm run test:e2e` (desktop, tablet, Android, iPhone): **237 passed, 3 skipped (desktop-only tests on other profiles), 1 failed** = a timing flake in the inventory spec (a strict row locator saw two ledger rows before the filter applied); fixed in the spec and the inventory spec re-run: 6 of 6 passed (the "4 did not run" of that run were its later tests). New real-browser flows: product create to publish (preview, validation, variants, stock, pictures, focal point, reorder, duplicate, view-only role), categories/brands/size charts, import check and import with a bad file, inventory adjust, transfer, reject/cancel, stock count, view-only role. UI checks at 360 / 768 / 1280 px on every new page: no sideways scroll, no tap target under 44 px, no axe violations, no console errors (all passed after fixes found by these checks: switch hit areas, a crowded filter row, a long button at 360 px, a stacked action column, a focusable scroll region in the shared table). Backend unchanged in this step (build 0 warnings; domain 94, application 23, integration 190 as in 6-d). Key screenshots: `juvo-front/docs/ui-checks/`.

## Step 7 (done): storefront, cart, customer account, delivery

| Sub-step | Scope | Status |
| --- | --- | --- |
| 7-a | Backend: public catalog and search, cart and coupons, customer account (profile, consent, addresses, wishlist), staff customer view, delivery zones and geography | **Done** (branch `feature/07-storefront-backend`) |
| 7-b | Front end: storefront, cart, customer account (profile, consent, addresses with map pin, wishlist), customer sign-in by SMS or email | **Done** (admin screens for coupons, zones and customers move to Step 12) |

**7-a result:** public `/store/*` (categories, home, products with filters/sort/fuzzy search, facets, suggestions, product detail, geography, delivery quote, coupon check, cart with guest token and merge), customer `/me/*`, staff `/customers`, `/promotions/coupons`, `/delivery/*`. Migration `StorefrontCartCustomersDelivery`. Geography from `nuhil/bangladesh-geocode` (MIT): 8 divisions, 64 districts, 494 upazilas. The seeded delivery zones and rates are **placeholders** the owner must replace. Contract: `docs/storefront-api.md`; decisions: `docs/assumptions.md` section K.

**7-b result:** pages `/`, `/shop`, `/search`, `/category/...`, `/product/[slug]`, `/cart`, `/account/{profile,addresses,wishlist}`; header with search suggestions and category menu; filters with chips; infinite grid; gallery; delivery estimate; coupon; guest cart in the httpOnly cookie `juvo_cart`, merged at sign-in; English and Bengali (Bengali first pass). Gate: tsc, eslint, vitest 44, build clean; full Playwright run 299 passed, 8 skipped, 4 failed (SQL and 45 s timeouts under load in a 1.5 h run; all 4 passed on re-run); overflow, 44 px tap targets and axe checks at 360/768/1280. Known gaps: size-guide popup, notify-me, share buttons, address search, BDT conversion; delivery rates are placeholders.

## How to run now

One command (checks tools, creates the database, installs packages, starts API and web app; details in the root `README.md`):

```powershell
.\scripts\start-all.ps1          # -Open opens the login page, -Status shows what runs, -Stop stops both
# web http://localhost:3000   API docs http://localhost:5080/scalar/v1
# sign in at /login: admin@juvobd.local / Juvo@Dev#2026 (development only; change it at first sign-in)
dotnet test juvo-be                               # needs SQL Server / LocalDB
cd juvo-front; npm test; npm run build; npm run test:e2e
```

### Step 8-a backend result (orders, checkout, order processing, fraud basics)

Branch: worktree `worktree-agent-a658e3fc833b3ffc7` (not merged, not pushed). Contract: `docs/orders-api.md`; decisions: `docs/assumptions.md` section M; traceability rows updated (Done or Partial, honest notes).

- **Built:** order aggregate with three parallel tracks and the exact state machine (domain tests cover every status pair), append-only status history, domain events through the outbox (with handlers that send the customer an SMS once), idempotent and duplicate-guarded creation, gap-free order numbers `JV-<prefix>-<yyMM>-<6 digits>` per branch and month, snapshots; stock reserved with atomic conditional updates in one transaction (release on cancel, payment timeout and reassign; deduct at packing or delivery by setting) with ledger lines; routing (default, all stock, nearest, manual) and reassign before packing; `POST /store/checkout/quote`, `POST /store/checkout`, public tracking (POST and GET), `/me/orders` (list with filters, detail with timeline, cancel); staff `/orders` (list, detail, confirm, pack, cancel, delivery steps, notes, bulk, reassign, packing slip data, manual desk orders, source summary); fraud `/fraud` (rules, blacklist, history stats, review queue and decisions); payment-hold timeout worker (BR-D2); `PlatformSettings` table with safe defaults; new permissions `orders.order.apply-discount` and `orders.order.override-delivery`.
- **Migrations:** `OrdersCheckoutFraud`, `OrderStandardDeliveryCharge`.
- **Not in 8-a (Partial/Deferred):** the whole front end of Step 8 (checkout page, confirmation, tracking page, admin order screens, desk form, fraud screens, my orders: 8-b); payment capture and refunds (Step 9; online orders wait in Pending and time out); couriers, returns (Step 10); order splitting across branches; settings screen; policy version with the terms acceptance; FR-S03, FR-S04 payment link, FR-S05; BR-005 display conversion.
- **Gate (2026-11-11, run and seen):** `dotnet build juvo-be --no-incremental` 0 warnings, 0 errors; `dotnet test juvo-be` 468 passed, 0 failed (domain 134, application 39, integration 295; 37 of the integration tests are the new order, stock-race, fraud and timeout tests, 20 domain tests are new); `dotnet ef migrations has-pending-model-changes` clean; `dotnet list package --vulnerable --include-transitive` clean for all 8 projects; no `JuvoDb_Test_*` database left on LocalDB. Race proof: 8 concurrent checkouts for the last unit give exactly 1 order, 7 `ORDER.STOCK_INSUFFICIENT`, reserved 1 of on-hand 1; 4 buyers of 2 units on 5 in stock give 2 orders and reserved 4.

### Returns backend result (module L: returns, exchanges; Step 10 slice)

Branch: worktree `worktree-agent-a2d78cbed558486ba` (built on the merged 8-a orders; not merged, not pushed). Contract: `docs/returns-api.md`; decisions: `docs/assumptions.md` section N; traceability rows FR-F04, FR-L03 to FR-L08 updated (Done or Partial with honest notes).

- **Built:** `ReturnRequest` aggregate (items, append-only history and refund, notes, photos) with the exact state machine (Requested to Approved or Rejected; Approved to PickupScheduled, PickedUp, Received, Inspected, then Refunded, Exchanged or RefundRejected; customer cancel while Requested or Approved), numbers `RT-<prefix>-<yyMM>-<6 digits>` gap-free per branch and month, events through the outbox with an SMS handler. Customer API `/me/returns` (eligibility per order, pictures, idempotent request, list, detail, cancel). Staff API `/returns` (queue with filters and search, detail, approve, reject, schedule pickup, picked up, received, inspect with restock, refund, exchange, reject refund, notes, bulk, dashboard summary). Rules: window setting `Returns.WindowDays` (7), non-returnable product flag, server-side refund maths (proportional discount share, delivery charge only for seller fault and only once, return charge for customer fault), no double refund (state machine, unique index, row version), restock into the fulfilling branch with a `CustomerReturn` stock movement, exchange as a linked zero-value order with stock reserved, order tracks follow (ReturnInProgress, Returned, payment PartiallyRefunded or Refunded; COD payments can be refunded).
- **Migration:** `ReturnsModule` (return tables and `Products.NonReturnable`).
- **Not in this slice (Partial/Deferred):** moving refund money through a gateway (Step 9: `IRefundGateway` does not exist yet, refunds are records completed by staff with a reference), booking the return pickup through a courier API (couriers slice), all screens (staff queue and detail, customer request and tracking pages, dashboard card), return policy text and per-category window (Step 12), settings screen for the three `Returns.*` settings, clean-up of unattached return pictures.
- **Gate (2026-11-11, run and seen):** `dotnet build juvo-be --no-incremental` 0 warnings, 0 errors; tests run per project executable: domain 149 passed (15 new), application 39 passed, integration 316 passed (21 new returns tests, including two simultaneous refunds paying once); `dotnet ef migrations has-pending-model-changes` clean; `dotnet list package --vulnerable --include-transitive` clean; no `JuvoDb_Test_*` database left.

### Step 8-b front-end result (checkout, orders, order desk, fraud screens)

Branch: worktree `worktree-agent-a5ff1ad67525599ff` (not merged, not pushed). Decisions: `docs/assumptions.md` section O; traceability rows updated; routes in `juvo-front/docs/frontend.md`.

- **Built (all in `juvo-front/src/modules/orders`):** `/checkout` (one page, server quote on every change, `expectedTotal`, duplicate prompt, per-attempt idempotency key, COD advance notice, OTP before confirm for guests), `/order/confirmed/[number]` (summary, one-time tracking link, create-my-profile prompt), `/track` (number + mobile or `?token=`), `/account/orders` and `/account/orders/[id]` (filters, cancel with confirm), `/admin/orders` (filters for every track, branch, source, fraud state, dates, held; source chips; bulk confirm/cancel/print), `/admin/orders/[id]` (three tracks, immutable history, snapshots, customer, risk, notes, confirm with advance, pack, cancel with reason, reassign, delivery steps), `/admin/orders/[id]/packing-slip` and `/admin/orders/print?ids=`, `/admin/orders/new` (social order desk), `/admin/fraud` (review queue, blacklist, rules). BFF forwards the cart token and `Idempotency-Key` on checkout. English and Bengali texts in `orders.*.json` plus `ORDER_*`/`FRAUD_*` error texts.
- **Not built / Partial:** reorder (FR-F07), reviews (FR-F09), phone/WhatsApp help buttons (FR-F11 partial), chat helper and WhatsApp/Messenger buttons (FR-S03, FR-S05), live push (Step 10), payment page for the sandbox online method (Step 9), desk totals are an estimate (no dry-run endpoint), e2e of branch reassignment (needs a second branch holding stock).
- **Gate (2026-11-11, run and seen):** `npx tsc --noEmit` clean; `npx eslint src e2e tests` clean; `npm test` 58 passed (14 new in `tests/orders.test.ts`); `npm run build` ok; `orders.spec.ts` 20/20 passed on desktop; `orders-ui-checks.spec.ts` 23/23 (21 checks at 360, 768 and 1280 px: overflow, 44 px targets, axe, no console errors; screenshots in `juvo-front/docs/ui-checks/orders/`); full `npm run test:e2e` (351 tests, 15.3 min): 333 passed, 9 skipped (as before), 6 failed: one timing flake in the fraud spec (dialog click), `permissions.spec` and `shell.spec` x4 (the menu now has ready order items; the specs were updated and the menu entries keep the active-branch scope); those three specs plus `orders.spec` were re-run after the fixes: 54 passed, 9 skipped, 0 failed. The whole suite was not run a second time end to end.

## Next step

**Step 9: payments, tax/invoices, notifications** (the sandbox online method gets its payment page; Step 8-b is done).

## Known issues / notes

1. SQL Server is LocalDB 2019 (compatibility level 150); verified with EF Core 10.
2. Behind a reverse proxy, per-IP rate limiting needs forwarded-headers configuration (Step 14). Data-protection keys in `App_Data/keys` must be persisted in production (Step 14).
3. No branch-scoped entity exists yet, so isolation is proven on a test entity (data layer) and on branches/users/audit (API). Orders, stock etc. adopt `IBranchScoped` from Step 6.
4. A used or revoked refresh token cuts the whole session by design; the front end must use one in-flight refresh.
5. `Auth:RequireTwoFactorForSuperAdmin` is off in `appsettings.Development.json` only.
6. Provider onboarding (courier, payment gateway, SMS sender ID, map key) is not started by the owner; not needed until Step 14. Bangladesh VAT rules are unverified. Brand content, policies and the pickup address must be supplied by the owner.
7. The Visual Studio GitHub credential for `ashiqurgit` is still in Windows Credential Manager (left alone).
8. `dotnet test` from `juvo-be` sometimes reports "Zero tests ran" in this environment; running each test project's `.exe` under `tests/*/bin/Debug/net10.0/` works and shows the real totals.
9. The refresh single-flight store is process-local (one app server); several servers need a shared store (Step 14). Bengali translations need native review (Step 12).
10. Node.js 26.13.0 LTS and Playwright's Chromium were installed on this machine in Step 6-e (they were missing); new shells see Node on the PATH, older shells may need `C:\Program Files\nodejs` added.
11. Product pictures: the 16 sample products use generated placeholders; replace them (and the products) with real ones when ready (see `juvo-be/README.md`).
