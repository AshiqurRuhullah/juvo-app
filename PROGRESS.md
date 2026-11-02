# PROGRESS

Resume from this file alone. Plan: `Documents/juvo-step-prompts.md` (14 steps). Master prompt: `juvo-claude-code-prompt.md`.
Last updated: 2026-11-02.

## Status of the 14 steps

| Step | Title | Status |
| --- | --- | --- |
| 1 | Understand the business, Git and project docs | **Done** |
| 2 | Backend foundation | **Done** |
| 3 | UMS backend: auth, roles, permissions, branches | **Done** (branch `feature/03-ums-backend`, merged to `main`) |
| 4 | Front-end foundation, brand, login, token handling | Not started |
| 5 | UMS user interface | Not started |
| 6 | Catalog, media, branch-wise inventory | Not started |
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

## How to run now

```powershell
.\scripts\setup-db.ps1
dotnet run --project juvo-be/src/Juvo.Api         # https://localhost:7080/scalar/v1
# sign in: admin / Juvo@Dev#2026 (development only; change it at first sign-in)
dotnet test juvo-be                               # needs SQL Server / LocalDB
```

## Next step

**Step 4: Front-end foundation** (Next.js inside `juvo-front`, brand tokens, design system, typed API client from `/openapi/v1.json`, BFF route handlers that keep the tokens in httpOnly cookies and refresh with a single in-flight request, middleware, `usePermissions`/`<Can>`, login page with the 2FA step, admin shell with the permission-built sidebar and branch switcher). Everything it needs about tokens, error codes, `X-Branch-Id` and `/auth/me` is in `docs/auth-and-access.md`.

## Known issues / notes

1. SQL Server is LocalDB 2019 (compatibility level 150); verified with EF Core 10.
2. Behind a reverse proxy, per-IP rate limiting needs forwarded-headers configuration (Step 14). Data-protection keys in `App_Data/keys` must be persisted in production (Step 14).
3. No branch-scoped entity exists yet, so isolation is proven on a test entity (data layer) and on branches/users/audit (API). Orders, stock etc. adopt `IBranchScoped` from Step 6.
4. A used or revoked refresh token cuts the whole session by design; the front end must use one in-flight refresh.
5. `Auth:RequireTwoFactorForSuperAdmin` is off in `appsettings.Development.json` only.
6. Provider onboarding (courier, payment gateway, SMS sender ID, map key) is not started by the owner; not needed until Step 14. Bangladesh VAT rules are unverified. Brand content, policies and the pickup address must be supplied by the owner.
7. The Visual Studio GitHub credential for `ashiqurgit` is still in Windows Credential Manager (left alone).
