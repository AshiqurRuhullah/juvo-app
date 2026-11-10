# CLAUDE.md — Juvo BD e-commerce platform (project memory)

Read this first in every session, then `PROGRESS.md` (live status). Everything here was recapped on 2026-11-10 from `Documents/`, `docs/`, `PROGRESS.md`, the master prompt and the code. If this file and the code disagree, trust the code and fix this file.

## 1. What we are building

**Juvo BD**: fashion retail (clothing, shoes, ladies' dresses, purses, more), Bangladesh + international. Today it sells through a Facebook page with manual orders, stock, payment and delivery. The platform gives customers a premium shop (guest checkout with only a mobile number, live tracking) and gives the owner one admin panel for catalog, stock, orders, couriers, payments, money and staff. Currency BDT. English + Bengali. Multi-branch (head office, shops, warehouses, online fulfilment) from one installation.

Problems to solve (BRD 2.2): P1 no catalog, P2 lost orders, P3 no status visibility, P4 manual courier/payment, P5 hard to serve outside Dhaka/abroad, P6 unknown stock/overselling, P7 customers hesitate to register, P8 inconsistent brand.

Core business rules (BRD 2.6): BR-001 one order, many variant lines; BR-002 stock reserved on order, released on cancel/fail; BR-003 immutable status history; BR-004 delivery charge by zone + weight + courier, customer sees full total first; BR-005 prices stored in BDT, international display is a conversion; BR-006 mobile number is the customer identity (guest, quick, full profile all can order).

## 2. Sources of truth (read in this order when in doubt)

| File | What it is |
| --- | --- |
| `Documents/Juvo BD — E-Commerce Platform BRD & SRS.docx` | Business truth (never modify). Markdown copy: `docs/source/brd-srs.md` (16 sections, modules A–AF, FR/NFR/BR IDs, ERD, acceptance list §14.2). **Business rules win over everything.** |
| `juvo-claude-code-prompt.md` | Master prompt: ground rules, Git workflow, architecture, UI rules. **Wins on technology and structure.** |
| `Documents/juvo-step-prompts.md` | The 14-step plan (common header + one prompt per step + resume/split prompts). |
| `PROGRESS.md` | Live status, gate results per step, how to run, known issues. Update after every step. |
| `docs/understanding.md` | Module matrix, state machines, ERD, FR/NFR/BR traceability table (257 FR/NFR rows + 15 BR; keep Status/Implemented-in/Notes current). |
| `docs/assumptions.md` | Every decision on open questions (sections A–G). Add a row for each new decision (decision, reason, how to change). |
| `docs/auth-and-access.md` | Auth, tokens, permissions, branches API contract. |
| `juvo-front/docs/frontend.md`, `docs/brand-tokens.md` | Front-end design/session/route/menu map; brand palette. |
| `juvo-be/README.md`, `juvo-front/README.md` | Run/test/conventions per project. |

## 3. How I must work (rules from the owner)

1. **Ask the owner when something is unclear or needs a decision.** (Owner said so on 2026-11-10: "ask me if you want anything, I will suggest". The master prompt's older "do not ask questions" rule is superseded for real doubts; still record every decision in `docs/assumptions.md`.)
2. **Never invent facts.** Verify any NuGet/npm package, API, CLI flag and its **licence** before use (avoid MediatR, AutoMapper, FluentAssertions-style licence changes; `react-leaflet` rejected, plain Leaflet used).
3. **No placeholders, TODO stubs or fake-data screens.** Real backend, real DB. External providers go behind interfaces with a working **Sandbox adapter on by default**.
4. **One step per session, in order; each step ends with a verification gate** (build 0 warnings/errors, tests pass, app runs, screenshots for UI). Be honest: claim only what was run and seen.
5. **No Docker/containers anywhere.** Local SQL Server (LocalDB). No Redis.
6. No secrets in code or git; `.env.example` only. Never put credentials in the remote URL.
7. Backend and front end stay independent (no shared code folders, no root package.json/sln); they talk only over HTTP/SignalR and the generated OpenAPI client.
8. At step end: update `PROGRESS.md`, the traceability table, `docs/assumptions.md`; commit; push; report what was built, how to run it, Partial/Deferred items, `git log --oneline -n 15`, `git status -sb`.
9. Default environment is Windows (PowerShell, quote paths: `F:\JUVO App\JuvoApp` has a space).

## 4. Git workflow

- Repo root `F:\JUVO App\JuvoApp`; remote `https://github.com/AshiqurRuhullah/juvo-app.git` (origin). Branches `feature/<NN>-<topic>`, merged into `main` when a step's gate passes. Never force-push, never rewrite pushed history.
- Conventional Commits: `type(scope): imperative summary` ≤ 72 chars, lower-case, no full stop. Types: feat fix refactor perf test docs build ci chore style revert. Reference FR/BR IDs in the body. One logical change per commit; migrations in their own commit; generated code with its generator.
- Stage explicit paths (never blind `git add -A`), read `git status` first, build/lint/test before commit, no secrets staged.
- Commit trailer (from the harness): `Co-Authored-By: Claude Sonnet 5.5 <noreply@anthropic.com>`. Commit/push only when the owner asks or the step workflow requires it.
- Known: GitHub credential in Windows is user `ashiqurgit`; earlier push got 403 and was later resolved (origin has all feature branches).

## 5. Architecture

### Backend `juvo-be` (ASP.NET Core 10, .NET SDK 10.0.301, EF Core 10.0.14, SQL Server)
Modular monolith, Clean Architecture, DDD-lite, own tiny CQRS dispatcher (no MediatR) with pipeline behaviours (logging, permission, validation, transaction).

| Project | Role |
| --- | --- |
| `Juvo.Api` | Minimal-API endpoints (`AuthEndpoints`, `UmsEndpoints`, `CatalogEndpoints`, `SystemEndpoints`), ProblemDetails, health, versioning `/api/v1`, OpenAPI + Scalar (`/scalar/v1`), CORS (localhost:3000), rate limits |
| `Juvo.Application` | Use cases/services, abstractions (`IFileStorage`, `IImageProcessor`, `IBranchContext`, `IPermissionChecker`, `IOutbox`, ...), folders `Identity`, `Ums`, `Catalog`, `Messaging` |
| `Juvo.Domain` | Entities, base types, events, rules (`Branches`, `Catalog`, `Customers`, `Identity`, `Notifications`, `Platform`) |
| `Juvo.Infrastructure` | EF Core, interceptors (audit, outbox), outbox worker, file storage, `SkiaImageProcessor` (SkiaSharp, MIT), migrations |
| `Juvo.Shared` | `Result`, `ErrorCodes`, pagination, `PermissionCatalog` (234+ codes) |
| `tests/` | `Juvo.Domain.Tests`, `Juvo.Application.Tests`, `Juvo.Api.IntegrationTests` (real SQL Server, one throw-away `JuvoDb_Test_<guid>` per class) |

Conventions: `Result<T>` with stable codes (`AREA.REASON`) for expected failures; RFC 9457 ProblemDetails with `code`, `correlationId`; lists use `page,pageSize(max 100),sort,filter,search` → `{items,total,page,pageSize}`; entities derive `Entity` (v7 GUID, stamps, `rowversion`), archivable ones `SoftDeletableEntity`, append-only `IAppendOnly`, `[AuditIgnore]` on sensitive props; money `decimal(18,2)` + currency; `datetimeoffset` UTC; `nvarchar` everywhere (Bengali); `TimeProvider` not `DateTime.Now`; warnings are errors; central packages in `Directory.Packages.props`; xunit v3 with plain asserts; Development auto-migrates, Production uses `dotnet run --project src/Juvo.Api -- --migrate`; domain events → outbox (at-least-once, idempotent handlers); uploads via `IFileStorage` served at `/files`.

### Front end `juvo-front` (Next.js 16.4 App Router, strict TS, Tailwind 4, Node 26)
**Read `juvo-front/AGENTS.md` and `node_modules/next/dist/docs/` before writing Next code**: this Next.js differs from older versions (`proxy.ts` replaces `middleware.ts`, request APIs async, `cacheComponents` off).

- **BFF**: browser only calls `/api/bff/...`; tokens only in httpOnly cookies (`juvo_at` access, `juvo_rt` refresh at path `/api/bff`, `juvo_rf`, `juvo_branch`; prefs `juvo_locale|theme|sidebar`). Single in-flight refresh (API cuts the session if a used refresh token returns). `src/proxy.ts` keeps session fresh; `requireSession()` server-side.
- Typed API client generated from OpenAPI: `npm run gen:api` (rerun after backend contract changes; snapshot in `openapi/`).
- Libraries: TanStack Query + Table v8.21.3, React Hook Form + Zod, Radix, next-intl 4 (cookie locale, no URL prefix, en + bn), sonner, lucide, plain Leaflet + OSM (Google via env flag), qrcode.
- Layout: `src/app/{(auth),(admin)/admin,(account),(storefront),api/bff,design}`, `src/modules/<module>` (so far `ums`), `src/components/{ui,auth,layout,providers}`, `src/lib/{auth,server,api}`, `src/lib/nav.ts` (permission → menu; unbuilt items show "Soon" until `ready: true`), messages `src/messages/{en,bn}.json` (keep key parity; Bengali needs native review in Step 12).
- UI rules: all colours from tokens (`src/styles/tokens.css`, generated by `scripts/generate_tokens.py` from the logo; red `#DB0100`, ink `#1C0807`, cream `#EBE5DB`; danger is crimson and always with icon+text); tap targets ≥ 44 px; sidebar nav (rail on tablet, drawer + bottom bar on phone); no horizontal scroll at any width; tables become cards on phones; forms single-column on mobile with 16 px inputs; light + dark theme; WCAG 2.1 AA.
- UI gate for every UI step: lint + `tsc --noEmit` + build + vitest + Playwright (desktop, tablet, Pixel 7, iPhone 14) with axe, overflow and tap-target checks and screenshots at 360/768/1280 in `juvo-front/docs/ui-checks/`.

## 6. Security and access model (implemented in Step 3)

- Staff = ASP.NET Core Identity users (no Identity roles); roles/permissions/branch assignments are our tables. JWT access 15 min + rotating refresh (hashed, family reuse detection, 14 d staff / 30 d customers). Access token has no permissions, only `pv` permission-version; any role/branch/password/active change makes old tokens answer `AUTH.TOKEN_EXPIRED` → silent refresh.
- Permission code `module.resource.action` (e.g. `catalog.product.view-cost`); Super Admin holds all incl. future ones; 7 default roles (Super Admin, Manager, Order Manager, Inventory Manager, Content Editor, Support, Accountant); system roles protected; no privilege escalation.
- Endpoints must declare `RequirePermission` (global features) or `RequireBranchPermission` (active branch); `EndpointPolicyTests` fails on an endpoint without policy.
- Effective permission = permission AND branch scope (`UserBranchRole`; global scope flag). `X-Branch-Id` (id or `all`) validated every request; out-of-scope → 404 (or 403 `AUTH.BRANCH_FORBIDDEN`) and audit `AccessDenied`. Central isolation: EF named filter "BranchScope" + save guard for `IBranchScoped`. **Entities that are branch-scoped (orders, stock, purchasing, expenses, shipments, returns, tickets, POS) must implement `IBranchScoped`.**
- Limited tokens (`scope=limited`) for forced password change / 2FA setup. Lockout 5 failures/15 min; password ≥10 chars with upper/lower/digit/symbol; 2FA mandatory for Super Admin (off only in Development appsettings).
- Customers: mobile + OTP (6 digits, 5 min, 5 attempts, 60 s cooldown, 5/h/number); numbers normalised to `+880…` (Bengali digits accepted); sandbox SMS writes to `NotificationLogs`.
- Dev login: `admin@juvobd.local` / `Juvo@Dev#2026` (dev only; forced change at first sign-in). Never reuse in production.

## 7. Domain rules to implement exactly (from SRS; used in upcoming steps)

- **Order status**: Placed → Confirmed → Packed → HandedToCourier → InTransit → OutForDelivery → Delivered; Placed/Confirmed/Packed → Cancelled; OutForDelivery → DeliveryFailed → (OutForDelivery | Returned); Delivered → ReturnInProgress → Returned. Three parallel tracks: order, payment, delivery. Every transition writes history (who, when, from, to, reason) and raises a domain event.
- **Payment status**: Pending → Paid (only after server-side gateway verification, never from browser return) / Failed / Cancelled / UnderReview → Paid|Rejected; Paid → PartiallyRefunded → Refunded; COD: Pending → CollectedByCourier → Settled. Webhooks idempotent and signature-checked. COD above BDT 10,000 needs delivery-charge advance (BR-D1); unpaid online orders time out after 30 min (BR-D2). OTP before confirm: COD only.
- **Stock**: `available = on_hand − reserved` per variant per branch. **Reserve at placement, deduct at packing**, release on cancel/fail/timeout. Single transaction with row locking (`UPDLOCK`/atomic conditional update) + rowversion so two buyers never get the last unit (NFR-019). Cart does not reserve. Stock movements append-only.
- **Immutable/append-only**: order status history, stock movements, payments, refunds, shipment events, audit log. Order items snapshot name, price, address.
- **Numbering**: order `JV-<BRANCHCODE>-<YYMM>-<6-digit seq>` (e.g. `JV-DHK01-2611-000123`); invoice and credit-note numbers gap-free per branch.
- **Delivery charge**: zone, weight slabs, order value, free-delivery threshold, per-courier override, remote surcharge; shown charge = billed charge (BR-G2). International: sample India enabled in sandbox, duties paid by receiver (DAP), max 20 kg.
- **Pricing/tax**: prices stored tax-inclusive; 15% VAT sample rule seeded **inactive** and marked unverified (confirm with accountant).
- **Returns**: 7 days from delivery; return shipping not refunded for customer-reason, refunded for seller fault. Return flow: Requested → Approved/Rejected → Pickup → Received → Inspected → Refunded/Exchanged.
- **Other workflows**: PO Draft → Sent → Partly received → Received; Ticket Open → In progress → Waiting → Resolved → Closed; Transfer Requested → Approved → In transit → Received (movements both sides); Product Draft → Published → Archived (publish needs name, category, price, ≥1 image, ≥1 variant: BR-B1..B3); Stocktake count → review → approve adjustment; Fraud Flagged → Review → Approve / Request advance / Call / Reject.
- **Online order routing** to a fulfilling branch (default branch / branch with all stock / nearest / manual); admin can reassign before packing; courier booking uses the fulfilling branch's pickup address.
- **Soft delete** (archive/deactivate, never erase) for products, customers, staff, branches. Cost price/profit/settings hidden from roles lacking the permission (FR-O04).
- **Adapters**: `IPaymentGateway`, `ICourierProvider`, SMS/email/WhatsApp/maps behind interfaces; Sandbox on by default; real ones (Pathao, Steadfast, RedX, Paperfly, eCourier, DHL, bKash, Nagad, SSLCommerz, aamarPay, Stripe, PayPal) are disabled slots built in Step 14 only with owner credentials.
- Defaults decided: COD + sandbox gateway enabled; reviews verified-buyers-only and moderated; loyalty (AB) behind a feature flag default off; Leaflet/OSM maps; stock-deduct setting `Inventory.DeductAt`; Bangladesh geo dataset chosen and recorded in Step 7; invoice PDF library chosen in Step 9 after licence check (QuestPDF Community licence limits).

## 8. The 14-step plan and status (details in `PROGRESS.md`)

| Step | Scope | Status (2026-11-10) |
| --- | --- | --- |
| 1 | Understand business, Git, docs, brand tokens | Done |
| 2 | Backend foundation | Done |
| 3 | UMS backend (auth, roles, permissions, branches) | Done |
| 4 | Front-end foundation, brand, login, tokens | Done |
| 5 | UMS UI | Done |
| 6 | Catalog, media, branch inventory (6-a…6-e) | **In progress** (6-a, 6-b, 6-c done; next 6-d) on `feature/06-catalog-inventory` |
| 7 | Storefront, cart, customer identity, address, delivery zones (modules A, C, E, F, G) | Not started |
| 8 | Checkout, orders, order processing, social desk, fraud basics (D, S, T, J) | Not started |
| 9 | Payments, tax/invoices, notifications (I, U, K) | Not started |
| 10 | Couriers, real-time (SignalR), returns (H, J, L) | Not started |
| 11A | Purchasing, expenses, support, reports (W, X, Y, P) | Not started |
| 11B | Promotions, pre-order, loyalty, marketing, reseller, POS (Z, AA–AE) | Not started |
| 12 | Remaining admin UI, CMS, dashboard, settings, platform services (M, N, Q, R, AF) | Not started |
| 13 | Hardening, QA, acceptance checklist, docs | Not started |
| 14 | Real providers, go-live (tag `v1.0.0` only if owner confirms) | Not started |

### Step 6 detail
- 6-a Catalog backend: **Done** (committed `f5aaf89`): categories tree, brands, size charts, products, variants, pricing/sale dates, publish rules, duplicate, cost-price visibility, audit, `CatalogSeeder`; endpoints `/api/v1/catalog/*`; migration `CatalogProducts`; SKU unique among live variants (case-insensitive); slugs unique among live rows.
- 6-b Image pipeline: **Done** (gated). Endpoints `/api/v1/catalog/products/{id}/images` (upload, update focal/colour/alt, reorder, delete); SkiaSharp WebP main/card (4:5)/thumb + blur placeholder; min 1000 px, max 10 MB, max 12 per product (FR-B04..B06). **Next: 6-c inventory.**
- 6-c Inventory: **Done** (gated 2026-11-10: domain 94, application 23, integration 171 pass, migration `InventoryStock`). `/api/v1/inventory`: stock list and product grid, adjustments with reasons, reorder levels, append-only movements, transfers (request, approve, ship, receive, reject, cancel), stock counts (start, enter, submit, approve, reopen, cancel). One stock location per branch; `StockLevel` is `IBranchScoped`; reserve/release/deduct exist on `StockLevel` for Step 8. Decisions: `docs/assumptions.md` section H. **Next: 6-d bulk import/export, then 6-e admin UI.**
- 6-d Bulk CSV/Excel import/export with validation report; sample products with images: not started.
- 6-e Admin UI (product list, add/edit with live premium card preview, variants/stock grid, categories, inventory by branch, transfers, adjustments; Playwright + screenshots): not started. Menu items Products/Categories/Inventory flip to `ready`.

## 9. Commands

```powershell
.\scripts\setup-db.ps1                               # creates JuvoDb on (localdb)\MSSQLLocalDB
dotnet run --project juvo-be/src/Juvo.Api            # http://localhost:5080 , https://localhost:7080/scalar/v1
cd juvo-front; copy .env.example .env.local; npm install; npm run dev    # http://localhost:3000
dotnet build juvo-be --no-incremental                # must be 0 warnings / 0 errors
dotnet test juvo-be                                  # real SQL Server needed
cd juvo-be; dotnet ef migrations add <Name> -p src/Juvo.Infrastructure -s src/Juvo.Api -o Persistence/Migrations
dotnet ef migrations has-pending-model-changes -p src/Juvo.Infrastructure -s src/Juvo.Api
dotnet list package --vulnerable --include-transitive
cd juvo-front; npm run lint; npx tsc --noEmit; npm run build; npm test; npm run test:e2e; npm run gen:api
```
Ports: API 5080/7080 (e2e API 5081), front end 3000 (e2e 3100). `scripts/`: `setup-db.ps1`, `docx_to_md.py`, `extract_palette.py`, `generate_tokens.py`, `generate_brand_assets.py`, `gen_traceability.py`.

## 10. Test status (last recorded)
Backend (6-c): domain 94 + application 23 + integration 171, build 0 warnings. Front end (Step 5): vitest 39, Playwright 150 passed / 3 skipped. If `dotnet test` reports "Zero tests ran" (known environment quirk), run each `tests/*/bin/Debug/net10.0/*.exe` directly.

## 11. Known issues / things the owner must supply
1. LocalDB is SQL Server 2019 (compat level 150): avoid features needing 160+.
2. Behind a reverse proxy, forwarded headers + known proxy needed for per-IP rate limits; data-protection keys in `App_Data/keys` must be persisted; refresh single-flight store is process-local (one app server) — all Step 14.
3. Bengali translations are first-pass and need native review (Step 12).
4. Owner must provide before go-live: courier/payment/SMS/map provider accounts and keys, domain/hosting, brand content (About story, policies, tagline, photos), pickup address, tax confirmation (VAT), restricted/non-returnable item lists.
5. Bangladesh VAT rules unverified; international shipping restricted-items list starts empty.
6. Open decisions for the owner can be raised any time; ask rather than guess when a business rule is unclear.
