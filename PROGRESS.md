# PROGRESS

Resume from this file alone. Plan: `Documents/juvo-step-prompts.md` (14 steps). Master prompt: `juvo-claude-code-prompt.md`.
Last updated: 2026-11-02.

## Status of the 14 steps

| Step | Title | Status |
| --- | --- | --- |
| 1 | Understand the business, Git and project docs | **Done** (pushed to `main`) |
| 2 | Backend foundation | **Done** (branch `feature/02-backend-foundation`, merged to `main`) |
| 3 | UMS backend: auth, roles, permissions, branches | Not started |
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

## Step 1 (done)

Git validated and connected, `.gitignore`/`.gitattributes`, SRS converted to Markdown (counts verified), `docs/understanding.md` (module matrix, state machines, ERD, traceability of 257 FR/NFR rows + 15 business rules), `docs/assumptions.md`, brand tokens from the logo (WCAG AA verified), `scripts/setup-db.ps1`.

## Step 2 (done): backend foundation in `juvo-be`

- Solution `Juvo.sln` (.NET 10.0.301): Api, Application, Domain, Infrastructure, Shared + 3 test projects. `Directory.Build.props` (nullable, analyzers, warnings as errors), `Directory.Packages.props` (central versions, licences checked, vulnerable transitive pinned), `.editorconfig`, `global.json` (testing platform runner), local tool `dotnet-ef 10.0.14`.
- Shared: `Result`/`Result<T>`, `Error`, stable error codes, `PageRequest`/`PagedResult` (page, pageSize max 100, sort, filter, search).
- Domain: `Entity` (v7 GUID, stamps, rowversion), soft delete, `IAppendOnly`, `[AuditIgnore]`, `AggregateRoot` + domain events, `AuditLog`, `OutboxMessage`, `JobLog`.
- Application: own dispatcher + pipeline behaviours (logging, permission [deny-all until Step 3], FluentValidation, transaction), abstractions (`ICurrentUser`, `IUnitOfWork`, `IFileStorage`, `IOutboxWriter`, `IOutboxMessageHandler`), sample `Ping` query.
- Infrastructure: `JuvoDbContext` (SQL Server, retry, compat level 150, `decimal(18,2)`, soft-delete filters), audit + domain-event interceptors, unit of work, transactional outbox + background worker (UPDLOCK/READPAST claim, exponential backoff, job log, handler isolation), local-disk `IFileStorage` (traversal-safe), validated options, design-time factory, migration `InitialPlatform` (AuditLogs, OutboxMessages, JobLogs).
- Api: correlation id, RFC 9457 ProblemDetails with codes, global exception handler (409 for concurrency), security headers, HSTS/HTTPS, CORS for `http://localhost:3000`, rate limiting (global + auth/otp/checkout policies), compression, output cache, `/health/live`, `/health/ready`, API versioning `/api/v1`, OpenAPI + Scalar, uploads at `/files`, `--migrate` command, auto-migrate in Development.
- Docs: `juvo-be/README.md`, `juvo-be/.env.example`, assumptions section E, traceability updates (10 NFR/FR rows now Partial).

**Gate result (all run and seen passing):**
- `dotnet build`: 0 errors, 0 warnings (warnings are errors).
- `dotnet test`: **69 passed, 0 failed** (22 domain, 10 application, 37 integration on real SQL Server).
- API ran: `/health/ready` Healthy (database check), OpenAPI document and `/scalar/v1` load, 400/404 return problem details with `code` and `correlationId`, CORS allows only the front-end origin.
- Migration `InitialPlatform` applied on `JuvoDb` (tables AuditLogs, JobLogs, OutboxMessages); `--migrate` verified in Production mode on a scratch database; `has-pending-model-changes`: none.
- `dotnet list package --vulnerable --include-transitive`: none. No leftover `JuvoDb_Test_*` databases.
- Start-up fails fast with a clear message when the connection string is missing or a CORS origin is a wildcard (verified).

## How to run now

```powershell
.\scripts\setup-db.ps1
dotnet run --project juvo-be/src/Juvo.Api         # https://localhost:7080/scalar/v1
dotnet test juvo-be                               # needs SQL Server / LocalDB
```

## Next step

**Step 3: UMS backend** (ASP.NET Core Identity staff users, JWT + rotating refresh tokens with reuse detection, permission catalog, roles, branches, branch-scoped authorization, seed Super Admin/HO). Plug into: `IPermissionChecker` (replace the deny-all default), `HttpCurrentUser.BranchItemKey` (set `X-Branch-Id` after validation), `JuvoDbContext` (add Identity tables + migration), `RateLimitSettings.AuthPolicy` on login/refresh.

## Known issues / notes

1. SQL Server is LocalDB 2019 (compatibility level 150); verified working with EF Core 10. Install SQL Server 2022 only if a later feature needs level 160.
2. Behind a reverse proxy, per-IP rate limiting needs forwarded-headers configuration (Step 14).
3. The OpenAPI document title shows "Juvo.Api | v1" (cosmetic; set a document transformer later).
4. Provider onboarding (courier, payment gateway, SMS sender ID, map key) is not started by the owner; not needed until Step 14.
5. Bangladesh VAT rules are unverified (sample 15% rule will be seeded inactive); confirm with an accountant before go-live.
6. Brand story, photos, policy text and pickup address must be supplied by the owner.
7. The Visual Studio GitHub credential for `ashiqurgit` is still stored in Windows Credential Manager (left alone; git uses the `AshiqurRuhullah` login now).
