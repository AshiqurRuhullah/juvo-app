# PROGRESS

Resume from this file alone. Plan: `Documents/juvo-step-prompts.md` (14 steps). Master prompt: `juvo-claude-code-prompt.md`.
Last updated: 2026-11-05.

## Status of the 14 steps

| Step | Title | Status |
| --- | --- | --- |
| 1 | Understand the business, Git and project docs | **Done** |
| 2 | Backend foundation | **Done** |
| 3 | UMS backend: auth, roles, permissions, branches | **Done** (branch `feature/03-ums-backend`, merged to `main`) |
| 4 | Front-end foundation, brand, login, token handling | **Done** (branch `feature/04-front-foundation`, merged to `main`) |
| 5 | UMS user interface | **Done** (branch `feature/05-ums-ui`) |
| 6 | Catalog, media, branch-wise inventory | **In progress**, split into 6-a to 6-e below (branch `feature/06-catalog-inventory`) |
| 7 | Storefront, cart, customer identity, address, delivery zones | Not started |
| 8 | Checkout, orders, order processing | Not started |
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
| 6-c | Inventory: branch stock locations, on-hand/reserved/available, append-only movements, adjustments, thresholds, transfers, stocktake | Not started |
| 6-d | Bulk CSV/Excel import and export with validation report; sample products with images | Not started |
| 6-e | Admin UI: product list, add/edit with live card preview, variants/stock grid, categories, inventory by branch, transfers, adjustments; Playwright and screenshots | Not started |

**6-a result:** endpoints under `/api/v1/catalog` (categories, brands, size-charts, products, variants, publish/unpublish/archive/duplicate), 21 new `Permissions.Catalog` constants (codes already in the catalog). Migration `CatalogProducts`. `CatalogSeeder` (4 top-level categories with sub-categories, 2 size charts) runs with the migration. `ProductImage` entity exists (publish needs one image); upload comes in 6-b. SKU unique among live variants (case-insensitive, freed on removal); slugs unique among live rows; staff without `catalog.product.view-cost` neither see nor change cost. Tests: 143 integration (20 new), 23 application (13 new), build 0 warnings, no pending model changes.

**6-b result:** pictures under `/api/v1/catalog/products/{id}/images` (multipart upload, update colour/alt/focal point, reorder, delete). Accepts JPEG/PNG/WebP, max 10 MB (measured from the stream, not the header), min 1000 px long side, pixel cap against decompression bombs, EXIF rotation applied, max 12 images per product. SkiaSharp (MIT) writes WebP renditions: main (long side up to 1600), 4:5 card (800x1000) and thumbnail (160x200) cropped around the focal point, plus a 16 px blur placeholder data URL. Changing the focal point re-crops; a duplicated product shares files (copy on write); delete removes files only when no product still uses them. Storage via `IFileStorage` (`/files`); DTOs return `url`, `cardUrl`, `thumbUrl`. Permissions `catalog.media.upload` / `catalog.media.delete`. No migration needed.
**6-b gate (run and seen):** `dotnet build --no-incremental` 0 warnings, 0 errors; test executables: domain 77, application 23, integration 151 (8 new media tests), 0 failed; `has-pending-model-changes`: none; no vulnerable packages; no leftover `JuvoDb_Test_*` databases.

## How to run now

```powershell
.\scripts\setup-db.ps1
dotnet run --project juvo-be/src/Juvo.Api         # http://localhost:5080/scalar/v1
cd juvo-front; copy .env.example .env.local; npm install; npm run dev    # http://localhost:3000
# sign in at /login: admin@juvobd.local / Juvo@Dev#2026 (development only; change it at first sign-in)
dotnet test juvo-be                               # needs SQL Server / LocalDB
npm test; npm run test:e2e                        # in juvo-front
```

## Next step

**Step 6: Catalog, media and branch-wise inventory** (backend + UI). Orders, stock etc. adopt `IBranchScoped` from here.

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
