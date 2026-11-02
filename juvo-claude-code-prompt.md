# JUVO BD — FULL E-COMMERCE PLATFORM BUILD PROMPT (for Claude Code terminal)

Paste everything below into Claude Code, started from `F:\JUVO App\JuvoApp` (the folder that contains `Documents/`, `Logo/`, `juvo-be/`, `juvo-front/`).
Tip: first run it in plan mode (Shift+Tab) so Claude Code shows its plan, then approve and let it execute.

---

## 0. ROLE AND GROUND RULES

You are a senior full-stack architect and engineer. Build a complete, working, production-grade e-commerce platform for **Juvo BD** (fashion retail: clothing, shoes, ladies' dresses, purses, more; Bangladesh + international).

Rules you must follow for the whole job:

1. **Do not ask me questions.** Where something is undecided, choose the most sensible option, and record it in `docs/assumptions.md` (decision, reason, how to change it).
2. **Do not invent facts.** Before using any NuGet package, npm package, API, CLI flag or framework feature, verify it exists and check the current stable version with the tool itself (`dotnet package search`, `dotnet add package`, `npm view <pkg> version`, official docs via web fetch). Never guess method names. If you are unsure whether a function exists, look it up. Check licences too: avoid libraries that changed to commercial/restrictive licences (verify the current licence of anything like MediatR, AutoMapper, FluentAssertions, MassTransit before adopting; prefer permissive or built-in options).
3. **No placeholders, no TODO stubs, no fake data screens.** Every screen must call the real backend API; every endpoint must hit the real database. Provider integrations (courier, payment, SMS, email, maps) must be real adapters behind interfaces, with a working **Sandbox/Mock adapter** enabled by default so the system runs end to end without external keys, and configuration slots for the real providers.
4. **Work in checkpoints.** Keep `PROGRESS.md` at the repo root: phases, status, what is done, what is next, known issues. Update it after every phase. If your context runs low, you must be able to resume from `PROGRESS.md` alone. Follow the **Git workflow in section 0A** for every commit and push (small tasks, valid messages, push to the remote repository).
5. **Every phase ends with a verification gate:** the solution builds with zero errors, the tests for that phase pass, and the app runs. Fix failures before moving on. Do not skip ahead.
6. Never hard-code secrets. Use `appsettings` + environment variables + user-secrets for development, and a `.env.example`.
7. Install everything automatically (`dotnet restore`, `npm install`). **Do NOT use Docker or containers anywhere** (no Dockerfile, no docker-compose, no Testcontainers). Everything runs directly on the machine: Microsoft SQL Server is a locally installed service. Final result must start with documented one-line commands.

---

## 0A. GIT WORKFLOW (mandatory: small tasks, valid commits, push to GitHub)

**Remote repository:** `https://github.com/AshiqurRuhullah/juvo-app.git`. The whole project (`juvo-be`, `juvo-front`, `docs`, `scripts`, root files) lives in this **one repository**, with the two projects kept as separate folders. The git root is `F:\JUVO App\JuvoApp`.

### Validate the repository first (before any code)
1. Run `git --version`, then `git rev-parse --is-inside-work-tree` in the root; if it is not a repository, run `git init -b main`.
2. Validate the remote **without changing anything**: `git ls-remote https://github.com/AshiqurRuhullah/juvo-app.git`. Record the result in `docs/assumptions.md`: reachable or not, default branch, empty or not. If it is missing, private without access, or authentication fails, **stop and tell me exactly what is wrong** (wrong URL, no permission, not logged in). Do not guess, do not create another repository, and never put a username, password or token in the URL, in files or in commands. Authentication must use my existing Git credential manager / `gh auth` login on this machine; if none is set up, tell me what to run and wait.
3. Add the remote as `origin` (`git remote add origin ...`, or `git remote set-url` if it exists), check `git remote -v`.
4. If the remote already has commits, `git fetch` and **integrate them** (pull with rebase, or merge) before the first push. Never force-push (`--force`, `--force-with-lease` on `main`) and never rewrite history that is already pushed. If there is a conflict you cannot resolve safely, stop and report it.
5. Create a root `.gitignore` before the first commit (Visual Studio/.NET: `bin/`, `obj/`, `.vs/`, `*.user`, `TestResults/`; Node/Next.js: `node_modules/`, `.next/`, `out/`, `.env*.local`; secrets: `.env`, `appsettings.*.local.json`, `secrets.json`, `*.pfx`, `*.key`; OS/IDE junk), and a root `.gitattributes` (normalise line endings for Windows). **Never commit secrets, connection strings with passwords, tokens, database files, logs, or build output.** Before each push run a quick check that no secret-like files or values are staged (`git status`, `git diff --cached`), and unstage anything suspicious.
6. Also decide (record in assumptions) whether the original `Documents/*.docx` and `Logo/` files are committed; default: commit them, they are the source material, unless a file is larger than ~50 MB (then use Git LFS only if available, or ask me).

### Branching
- `main` is always buildable. Do the work on short-lived branches named `feature/<step>-<topic>` (for example `feature/03-ums-backend`, `feature/05-ums-ui`), merge into `main` when the step's gate passes, then push `main`. If I did not ask for branches, committing directly on `main` in small steps is acceptable, but still push after each step's commits. Push the working branch as well: `git push -u origin <branch>`.

### Small tasks and commits (this is the main rule)
- **One logical change per commit.** Break every phase into small tasks and commit after each one that builds and passes its tests. Examples of one-commit tasks: "add solution and project skeleton", "add global ProblemDetails error handling", "add Branch entity and EF configuration", "add refresh-token rotation with reuse detection", "add login page UI", "add product card component". Never make a giant "initial commit with everything", and never mix unrelated changes (for example a refactor and a new feature) in one commit.
- Before every commit: the solution builds (`dotnet build`), the front end type-checks/lints (`npm run lint`, `npm run build` when the front end changed), and tests relevant to the change pass. **Do not commit code that does not build.** (A deliberate work-in-progress commit must be on a feature branch and marked `wip:`, and must be fixed before merging to `main`.)
- Stage files explicitly (`git add <paths>`), not blindly `git add -A`, and read `git status` first.
- **Push after every small task or at least after every 2–3 commits** (`git push`), and always at the end of a phase and before you stop for any reason, so nothing is lost. If a push fails, read the error, fix the cause (for example `git pull --rebase`), and retry; report persistent failures to me.

### Commit message rules (valid, proper, consistent)
- Use **Conventional Commits**: `<type>(<scope>): <summary>`, where type is one of `feat`, `fix`, `refactor`, `perf`, `test`, `docs`, `build`, `ci`, `chore`, `style`, `revert`; scope is the module or project (for example `be`, `front`, `ums`, `orders`, `catalog`, `inventory`, `payments`, `couriers`, `branches`, `db`, `docs`).
- Summary: imperative mood, present tense ("add", not "added"), lower-case start, no trailing full stop, **maximum 72 characters**.
- Add a body when the "why" is not obvious (wrap at ~72 characters; explain what and why, not how), and reference the SRS requirement IDs the commit implements, for example `Implements FR-D01, FR-D02` or `Refs: BR-002`. Use `BREAKING CHANGE:` in the footer for breaking API/database changes.
- Good examples: `feat(ums): add role and permission assignment endpoints`, `feat(be): add branch entity and user-branch-role mapping`, `fix(orders): release reserved stock when payment times out`, `feat(front): add branded login page with token cookie handling`, `test(ums): cover refresh token reuse detection`, `docs: add module traceability table for FR IDs`, `build(be): add central package management`, `chore: add gitignore and gitattributes`.
- Bad examples (never write): `update`, `changes`, `fix bug`, `wip`, `final`, `asdf`, `done`, messages that list many unrelated things.
- Database migrations get their own commit: `feat(db): add initial schema migration for catalog and orders`. Generated code (OpenAPI client, tokens.css) is committed in its own commit together with the script/version that produced it.

### Cadence and reporting
- After each step/phase gate: update `PROGRESS.md`, commit it (`docs: update progress for step N`), push, and print `git log --oneline -n 15` and `git status` in your report so I can see what was pushed.
- At the end, the final report must include the repository URL, the branch(es) pushed, the last commit hash, and confirmation that the working tree is clean and `origin` is up to date (`git status -sb`, `git log origin/<branch>..HEAD` empty).

---

## 1. INPUTS — READ FIRST (before writing any code)

### 1.1 `./Documents/` folder (business truth)
- The single source of truth is the file **`F:\JUVO App\JuvoApp\Documents\Juvo BD — E-Commerce Platform BRD & SRS.docx`** (the name contains spaces, an em dash and `&`: quote the path, and list the folder first to get the exact file name). It holds the complete business logic and everything the front end and back end are built from: 16 sections, modules A to AF, FR/NFR/BR IDs, brand/design-system rules, workflows and status lifecycle, ERD (Mermaid), roles matrix, phases, acceptance checklist, open questions.
- It is a Word `.docx` file. Claude Code cannot read it as plain text, so convert it to Markdown yourself (verify a tool before using it: `pandoc` if installed, or a small script that reads the OOXML `word/document.xml` and keeps headings, tables, lists, code blocks), save the result as `docs/source/brd-srs.md`, and check that **all 16 sections, every table and the Mermaid ERD** came through (count headings and tables against the original). If conversion loses content, say so in `docs/assumptions.md` and re-read the original in parts until nothing is missing.
- Also read any other file in `Documents/`. Read the BRD + SRS **completely, top to bottom, before writing code**; do not skim or rely on the index only. If the document contains open questions (section 14.5), take the default stated in the document or the most sensible one and record it in `docs/assumptions.md`.
- Understand the full business logic: modules A to AF, functional requirements (FR-xxx), non-functional requirements (NFR-xxx), business rules (BR-xxx), order/payment/delivery status lifecycle, roles matrix, ERD, phases, acceptance checklist.
- Produce `docs/understanding.md` containing:
  - a module matrix (module → entities → API endpoints → UI screens → permissions),
  - the order status state machine and the payment/delivery tracks,
  - the list of all FR IDs with the file/class/endpoint/page where each is implemented (a traceability table you keep updated, mark each FR as Done / Partial / Deferred with reason),
  - the final ERD (Mermaid) for the database you will build.
- If the documents conflict with this prompt, the documents win for business rules; this prompt wins for technology and structure.

### 1.2 `./Logo/` folder (brand)
- Inspect all logo files. Extract the **real colour palette** from the logo(s) (write a small script, for example Python Pillow or Node sharp/node-vibrant, verified before use) and produce `docs/brand-tokens.md` + `juvo-front/src/styles/tokens.css` with CSS variables: primary, primary-dark, primary-soft, secondary, surface, surface-card, text-strong, text-muted, border, success, warning, danger, info. Ensure WCAG AA contrast (4.5:1) for text; adjust neutrals only, never the logo colours.
- Use the logo **everywhere in the front end**: header, footer, login page, admin sidebar, favicon/app icons, loading screen, email/SMS-less templates, invoice PDF, order tracking page, 404/500 pages, social share image. Copy logo files into `juvo-front/public/brand/` and generate optimised variants (SVG if available, PNG sizes, favicon, apple-touch icon, manifest icons).
- All colours in the UI must come from these tokens. No random hex values in components.

---

## 2. TARGET REPOSITORY LAYOUT

```
F:\JUVO App\JuvoApp\       (root = the folder Claude Code is started in; Windows machine)
  Documents/               (EXISTS, input, do not modify)
  Logo/                    (EXISTS, input, do not modify)
  juvo-be/                 (EXISTS, currently empty: create the ASP.NET Core 10 solution INSIDE it)
  juvo-front/              (EXISTS, currently empty: create the Next.js project INSIDE it)
  docs/                    (new: understanding.md, assumptions.md, brand-tokens.md, api.md, runbook.md)
  scripts/                 (new: setup-db and run scripts, PowerShell first, no Docker)
  PROGRESS.md              (new)
  README.md                (new)
```
Backend and front end are **two separate projects in two separate folders** and must stay independent: no shared code folders, no root-level package.json or solution file. They communicate only through the HTTP API (and SignalR) and a generated OpenAPI client. **Organising docs and logo (allowed):** the root `Documents/` and `Logo/` originals are never modified or moved, but you may place **copies** inside each project where that makes the project self-contained and easier to maintain:
- `juvo-be/docs/` (backend-relevant docs: SRS/BRD copy or extracts, ERD, API notes, permission catalog, order state machine, assumptions) and `juvo-be/assets/brand/` (logo copies used for invoice PDFs and email templates).
- `juvo-front/docs/` (front-end-relevant docs: design system and brand tokens, UI/module screen list, route map, permission-to-menu map) and `juvo-front/public/brand/` (logo copies used by the UI).
- Root `docs/` keeps the cross-cutting documents (understanding, assumptions, traceability, runbook). If a document exists in several places, the root `Documents/` original is the source of truth, and each copy must start with a line saying it is a copy and the date copied. Generated files (tokens.css, API client, permission catalog export) should be produced by a script so the copies can be refreshed, not edited by hand.
- This does not change the rule that the two projects stay independent: no shared code folders and no project referencing files outside its own folder at build or run time (only documentation and brand copies are duplicated).

Never create a second `juvo-be` or `juvo-front` folder and never nest one inside the other. Because the folders already exist, do not run generators that insist on a new folder name in a way that produces `juvo-front/juvo-front`; scaffold directly into the existing folder (for example `npx create-next-app@latest .` inside `juvo-front`, and `dotnet new sln` / `dotnet new` with explicit output paths inside `juvo-be`).

**Environment: Windows.** Use PowerShell-compatible commands, Windows paths (`\`) in scripts, `.ps1` scripts as the primary scripts (bash optional), and make sure long paths, spaces in `F:\JUVO App` and file locking do not break builds (quote every path). Local default ports: API `https://localhost:7xxx` / `http://localhost:5xxx` (read from `launchSettings.json`), front end `http://localhost:3000`; configure CORS and `NEXT_PUBLIC_API_BASE_URL` accordingly.

---

## 3. BACKEND — `juvo-be` (ASP.NET Core 10)

### 3.1 Solution and architecture
Create the solution and projects with the `dotnet` CLI on the latest stable .NET (verify `dotnet --list-sdks`; target .NET 10; if the SDK is missing, tell me exactly how to install it and use the newest installed SDK meanwhile, recorded in assumptions).

Architecture: **Modular Monolith with Clean Architecture + DDD-lite + CQRS-style use cases**, designed so any module can be split into a service later.

```
juvo-be/
  Juvo.sln
  src/
    Juvo.Api/                (ASP.NET Core host: endpoints, middleware, DI composition, OpenAPI, SignalR hubs)
    Juvo.Application/        (use cases: commands/queries, validators, DTOs, interfaces, behaviours)
    Juvo.Domain/             (entities, value objects, enums, domain events, state machines, business rules)
    Juvo.Infrastructure/     (EF Core DbContext, configurations, repositories, migrations, identity, adapters)
    Juvo.Shared/             (result types, error codes, pagination, constants, permission catalog)
  tests/
    Juvo.Domain.Tests/
    Juvo.Application.Tests/
    Juvo.Api.IntegrationTests/   (real local SQL Server using a separate test database that the tests create and drop; no Docker)
```
Inside Application/Domain/Infrastructure organise by **feature module folders** that match the document: Identity (UMS), Catalog, Cart, Checkout, Orders, Customers, Delivery, Couriers, Payments, Inventory, Purchasing, Accounting, Support, Promotions, Marketing, Notifications, Cms, Reports, Tax/Invoicing, Fraud, SocialOrders, Platform.

Mandatory design points:
- Use only the **built-in dependency injection**, `IOptions<T>` with validation on start, `ProblemDetails` (RFC 9457) for all errors, a global exception handler, correlation id on every request, structured logging (Serilog or built-in logging, verify), health checks (`/health/live`, `/health/ready`), built-in rate limiting, response compression, output/HTTP caching where safe, CORS for the front end only, API versioning (`/api/v1/...`), OpenAPI document generation plus an interactive UI (Scalar or Swagger UI; verify the package), `TimeProvider` instead of `DateTime.Now`, nullable reference types on, analyzers on, `TreatWarningsAsErrors` for new code, central package management (`Directory.Packages.props`), `Directory.Build.props`, `.editorconfig`.
- Validation with FluentValidation (verify licence/version) or built-in validation; a pipeline behaviour for validation, logging, transactions, and permission checks. If you use a mediator, verify licence; otherwise implement a tiny internal dispatcher.
- Mapping with a **source-generator mapper** (e.g. Mapperly, verify) or hand-written mappers. No reflection-heavy mapper unless its licence is verified.
- **Result pattern** for expected failures; exceptions only for unexpected ones. Stable machine-readable error codes (e.g. `ORDER.STOCK_INSUFFICIENT`) returned in ProblemDetails.
- Pagination, sorting, filtering, search on every list endpoint with a consistent contract (`page`, `pageSize`, `sort`, `filter`, response with `items`, `total`, `page`, `pageSize`).
- Idempotency keys for order creation and payment callbacks; optimistic concurrency (SQL Server `rowversion` concurrency token) on orders, stock, payments.
- **Transactional outbox** for events/notifications/webhooks and a background worker (hosted service; Hangfire only if its licence/version is verified) with retry + backoff + job log table (FR-AF02).
- Domain events for status changes, stock changes, payment confirmations; handlers write the audit log, push SignalR messages, enqueue notifications.

### 3.2 Database (Microsoft SQL Server) and EF Core
- **Microsoft SQL Server** via the official **`Microsoft.EntityFrameworkCore.SqlServer` provider** (verify the matching version for the EF Core version used). Connection string from config, pointing to a **locally installed SQL Server** (Developer/Express/LocalDB or a named instance; default `localhost`, database `JuvoDb`; Windows authentication or SQL login, credentials via user-secrets/env, documented in README; use `TrustServerCertificate=True` for local development only). Provide `scripts/setup-db.ps1` (PowerShell) that creates the database if SQL Server is reachable (using `sqlcmd` or EF migrations), and detect/print clear install instructions if it is missing. Use in-memory cache (`IMemoryCache`) and SignalR without a backplane (no Redis). Auto-apply migrations in Development; a separate migration command for Production.
- **Multi-branch (section 4A) is part of the data model from the first migration:** `Branch`, `UserBranchRole`, `StockLocation` per branch, `StockTransfer` and lines, and a `BranchId` foreign key on every branch-scoped entity listed in 4A.2 (orders, stock, purchasing, expenses, cash book, shipments, returns, tickets, POS), with indexes that start with `BranchId` where lists are filtered by branch.
- Implement **every entity and relationship** from the documents: the ERD in section 12 of the SRS plus the extra entities of section 15 (risk flags, blacklist, invoices, tax rules, stock locations/counts, suppliers/purchase orders/goods receipts, expenses/cash book, support tickets, promotions, loyalty ledger, campaigns/segments, API keys, webhook subscriptions, job log, feature flags), plus cart/cart items, reviews, CMS pages/sections, settings, OTP codes, webhook log, notification log, audit log, refresh tokens, permissions/roles/user-role/role-permission tables.
- Use **Fluent API configurations** (one `IEntityTypeConfiguration<T>` per entity), explicit foreign keys with sensible delete behaviour (no accidental cascades on financial data), unique and composite indexes, check constraints (e.g. quantities >= 0, sale price < regular price), `decimal(18,2)` for money with a currency code, `datetimeoffset` (or `datetime2` in UTC) times, `nvarchar` for all text so Bengali (Unicode) is stored correctly, sequences or identity columns for human-readable order/invoice numbers with no gaps where required, soft delete (global query filter) where the document says archive/deactivate, owned types or value objects for Address and Money where it helps, snapshot columns on order items (name, price, address text).
- Append-only tables: order status history, stock movements, payments, refunds, shipment events, audit log.
- Stock: `available = on_hand - reserved`; reservation on order placement inside a **single transaction with row locking (SQL Server `UPDLOCK`/`ROWLOCK` hints or an atomic conditional `UPDATE`) plus the rowversion concurrency check** so two buyers can never take the last unit (NFR-019). Release on cancel/failure/timeout. Follow the deduction point from `docs/assumptions.md` (default: reserve at placement, deduct at packing).
- Seed data (idempotent): Super Admin user, all permissions, default roles from the roles matrix, Bangladesh divisions/districts/upazilas (use a reliable open dataset, verify and record the source; if full upazila data is unavailable, seed divisions and districts completely and mark upazila as importable), delivery zones and rate slabs, sample categories/products/variants/images, couriers (Sandbox + named placeholders disabled), payment methods (COD, Sandbox gateway), notification templates (EN + BN), tax rule sample, CMS pages (About, policies, FAQ), settings.

### 3.3 Domain behaviour (implement exactly per the documents)
- **Order state machine** (section 11.1) with three parallel tracks: Order status, Payment status, Delivery status. Only allowed transitions; each transition writes history (who, when, from, to, reason) and raises a domain event.
- Checkout: guest / quick profile / full profile identity by mobile number (+880 normalisation, OTP rules, rate limits), zone and delivery charge calculation (weight slabs, order value, free-delivery threshold, per-courier override, remote surcharge), coupons/promotion stacking rules, tax/VAT calculation, duplicate-order guard, stock re-check, payment hold timeout (auto-cancel and release).
- Payments: `IPaymentGateway` adapter (initiate, verify, refund, query) + Sandbox adapter + COD flow + manual verification queue; server-side verification before Paid; idempotent webhook endpoint with signature verification; refunds; invoice and credit note numbering with no gaps.
- Couriers: `ICourierProvider` adapter (create parcel, cancel, status, label, charge estimate, COD reconcile) + Sandbox adapter that simulates webhook events; webhook endpoint with signature check, duplicate protection, raw status stored and mapped to platform status; COD settlement tracking.
- Returns/refunds/exchange, cancellations, fraud rules and blacklist, support tickets, purchasing and goods receipt (updates stock and cost), expenses and profit/loss, promotions (flash, bundle, buy X get Y, free delivery), loyalty/store credit, marketing segments/campaigns, reseller/affiliate, POS (basic), reports (sales, orders, inventory, finance, courier, customer) with Excel/CSV export.
- **Real-time:** SignalR hubs for admin (new order, status, stock, alerts) and customer (own order tracking) with authorisation; fallback polling endpoint.
- Notifications: SMS/email/in-app with template engine (EN/BN variables), channel provider adapters (Sandbox writes to a log table, and email sandbox saves messages as viewable records in the admin Notifications log), retries.
- Image pipeline: upload endpoint → validate → resize to several sizes → WebP (or AVIF if verified) → focal point → blur placeholder; store on local disk behind `IFileStorage` with an S3-compatible implementation switchable by config. Serve through the API/static files with caching headers.
- Invoice PDF generation (verify a licence-friendly library such as QuestPDF; check its current licence terms for this company's use) with the logo and brand colours.

### 3.4 Security (NFR-006 to NFR-012)
- HTTPS/HSTS, secure headers, input validation, output encoding, anti-forgery where cookies are used, rate limits on login/OTP/checkout, protection against OWASP Top 10, secrets never in code, encrypted courier/gateway credentials in DB (data protection API), PII access logged.
- All admin changes written to the audit log (who, entity, action, old/new JSON).

---

## 4. USER MANAGEMENT SYSTEM (UMS) — BACKEND + FRONT END, FULLY WORKING

Create a dedicated UMS module: backend folder `Identity` (Application/Domain/Infrastructure/Api) and front-end folder `juvo-front/src/modules/ums` with its own pages under `/admin/ums/*`.

### 4.1 Model: Feature → Permission → Role → User
- **Permission catalog** (code-defined, seeded to DB, versioned): every feature of every module is a permission with a stable code `module.resource.action`, for example `orders.order.view`, `orders.order.confirm`, `orders.order.cancel`, `catalog.product.create`, `catalog.product.publish`, `inventory.stock.adjust`, `payments.refund.approve`, `couriers.shipment.book`, `reports.finance.view`, `ums.user.create`, `ums.role.assign`, `settings.system.edit`, and so on. Generate the **complete list from the documents** (every FR and admin screen/action), grouped by module for display.
- **Roles** are named sets of permissions (many-to-many). **Users** get one or more roles (many-to-many) **per branch** (section 4A: User ↔ Branch ↔ Role, with an optional global scope). Optional per-user explicit deny/allow overrides (store but keep the UI simple).
- Default roles from the SRS matrix: Super Admin, Manager, Order Manager, Inventory Manager, Content Editor, Support, Accountant. System roles cannot be deleted; Super Admin always has all permissions (including permissions added later).
- Rule from the requirement: **every feature is assigned to a role; a role is assigned to a user; the user can then access and execute exactly that.** Enforce on the API with permission-based authorization policies (custom `IAuthorizationPolicyProvider` / requirement handler or endpoint filters) and mirror in the UI.

### 4.2 Authentication and token management (back end)
- Staff: email/username + password (ASP.NET Core Identity with strong password policy, lockout after repeated failures, optional TOTP two-step verification mandatory for Super Admin, sign-in history). Customers: mobile + OTP (guest / quick / full profile per module E), optional password.
- **Access token (JWT, short life, e.g. 15 min) + rotating refresh token** (long life, stored **hashed** in DB with device/IP/user agent, rotation on every refresh, **reuse detection** that revokes the whole token family, revoke on logout/password change/role change/deactivation).
- Permission and role claims embedded or looked up via a **permission-version** stamp so role changes take effect quickly without waiting for token expiry.
- Endpoints: `POST /api/v1/auth/login`, `/refresh`, `/logout`, `/logout-all`, `/me` (profile + roles + effective permissions), `/change-password`, `/forgot-password`, `/reset-password`, `/2fa/*`, customer `/otp/request`, `/otp/verify`. Consistent error codes (`AUTH.INVALID_CREDENTIALS`, `AUTH.LOCKED`, `AUTH.TOKEN_EXPIRED`, `AUTH.REFRESH_REUSED`, `AUTH.FORBIDDEN`).
- UMS admin endpoints: users CRUD + activate/deactivate + reset password + force logout + sessions list/revoke; roles CRUD + permission assignment (grouped matrix) + clone role; permission catalog list; user-role assignment; audit log search; sign-in history.

### 4.3 Front-end UMS screens (all functional, permission-guarded)
Users list/detail/create/edit, Roles list/detail with a **module-grouped permission matrix with checkboxes and select-all per module**, Assign roles to a user, Active sessions, Sign-in history, Audit log viewer, My profile & security (change password, 2FA, sessions).

---

## 4A. MULTI-BRANCH SUPPORT (new requirement; part of UMS and the whole data model)

The platform must run **many branches** (head office, shops, warehouses, online fulfilment centres) from one installation. This is an addition to the BRD/SRS: where the document is silent, follow this section; it connects the document's Module AE (POS and multi-branch), Module V (stock locations) and the roles matrix. Record any design choice in `docs/assumptions.md`.

### 4A.1 Branch model (UMS)
- **Branch** entity: id, unique code (e.g. `HO`, `DHK-01`), name (EN + BN), type (Head Office, Shop, Warehouse, Online Fulfilment), parent branch (optional hierarchy), address with map coordinates, phone, email, working hours, timezone, default currency, order/invoice number prefix, courier pickup address / store id per courier, default delivery zone, status (Active, Inactive), is-default flag. Soft delete; a branch with history is deactivated, never erased.
- **User ↔ Branch ↔ Role:** a user is assigned to one or more branches, and gets **one or more roles per branch** (table `UserBranchRole`: user, branch, role). The same person can be Order Manager in Branch A and Viewer in Branch B. A user may also hold an **"All branches" (global) scope** for head-office roles such as Super Admin and Accountant; Super Admin always has global scope.
- Permission check = **permission AND branch scope**. A permission such as `orders.order.confirm` is only effective in the branches where the user holds a role that includes it. Global-scope users can act on every branch.
- **Active branch context:** the admin user selects an active branch (or "All branches" if allowed). The front end sends it as a header (e.g. `X-Branch-Id`); the API validates it against the user's assignments on every request and never trusts it blindly. The `/auth/me` response returns the user's branches, roles per branch and effective permissions per branch. Role or branch assignment changes take effect through the permission-version mechanism (4.2).

### 4A.2 What is branch-scoped and what is global
| Branch-scoped (carries `BranchId`, filtered by the user's branch scope) | Global (shared by all branches) |
| --- | --- |
| Orders (fulfilling branch), shipments/pickup, returns, manual and social orders | Product catalog (products, variants, categories, brands, images) |
| Stock per branch (stock locations), stock movements, stock counts, inter-branch transfers | Customers (one profile for all branches; order history shows branch) |
| Purchase orders, goods receipts, suppliers (optionally shared) | Roles, permission catalog, tax rules, delivery zones definitions |
| Expenses, cash book, daily cash register, POS sessions and sales | CMS, About, policies, SEO, brand settings |
| Support tickets, staff assignments, notifications sent by the branch | System settings (with optional per-branch override, e.g. pickup address, prefixes, working hours) |
| Reports (filterable by branch; consolidated view for global users) | Audit log (global, but every entry records the branch) |

### 4A.3 Behaviour to implement
- **Online order routing:** every storefront order gets a fulfilling branch by a configurable rule (default branch, the branch that has stock for all items, nearest branch to the delivery address, or manual). Admin can **reassign/transfer** an order to another branch while it is still before packing (status history records it). Stock is reserved in the fulfilling branch's stock location.
- **Stock by branch:** available = on_hand − reserved per variant per branch; storefront shows combined availability across branches that can fulfil online; **inter-branch stock transfer** workflow (Requested → Approved → In transit → Received) with stock movements on both sides.
- **Numbering:** order, invoice and credit-note numbers carry the branch prefix and stay gap-free per branch (e.g. `JV-DHK01-2611-000123`).
- **Courier booking** uses the fulfilling branch's pickup address/store id.
- **Money:** expenses, cash book, COD settlement and P&L are per branch with a consolidated company view.
- **Enforcement at the data layer:** implement branch scoping centrally (EF Core global query filters driven by the current user's allowed branch ids, plus a guard in write commands) so a developer cannot forget it. A user from Branch A requesting an order of Branch B gets 404/403 and the attempt is audit-logged. Global-scope users bypass the filter explicitly and visibly.
- Seed: Head Office (`HO`, default, global scope for Super Admin) and one default "Online Fulfilment" branch so a single-branch business works out of the box with zero extra setup. The UI hides branch controls when only one branch exists.
- Tests: branch isolation (A cannot read/modify B), per-branch role (Order Manager in A only), global vs scoped users, order routing rules, order reassignment, inter-branch transfer, per-branch numbering, branch deactivation rules.

### 4A.4 UMS screens for branches (admin front end, under `/admin/ums/`)
- **Branches:** list, create/edit (all fields above incl. map pin), activate/deactivate, set default.
- **Branch users:** for a branch, list its users and roles; add a user with a role; remove; for a user, a **branch × role assignment grid** (rows = branches, columns/chips = roles) plus a global-scope toggle (permission-guarded).
- **Role matrix view per branch:** show what a selected user can do in a selected branch (effective permissions).
- **Header branch switcher** (with "All branches" where allowed) that updates the active branch for every list, dashboard widget, report and real-time feed (SignalR groups per branch plus a global group).
- New permissions in the catalog: `ums.branch.view/create/edit/deactivate`, `ums.branch.assign-user`, `ums.branch.switch-all`, `inventory.transfer.request/approve/receive`, `orders.order.reassign-branch`, and branch-filter variants where useful.

---

## 5. FRONT END — `juvo-front` (Next.js, latest stable)

### 5.1 Setup
- Create with the official Next.js CLI (verify the current command and latest version with `npm view next version`): **App Router, TypeScript strict, ESLint, Tailwind CSS, `src/` directory, import alias**. Run `npm install` automatically and make sure `npm run build` passes.
- Libraries (verify each exists and is current before installing): TanStack Query, TanStack Table, React Hook Form + Zod (+ resolver), a headless accessible UI base (shadcn/ui or Radix UI) styled **only** with the brand tokens, `next-intl` (English + Bengali, route-aware), `@microsoft/signalr`, Leaflet + OpenStreetMap by default (Google Maps switchable by env flag), Recharts (charts), `date-fns`, `sonner` or equivalent toasts, an OpenAPI→TypeScript client generator (e.g. `openapi-typescript` / `orval`; verify) so the front end uses a **typed client generated from the backend OpenAPI document** (`npm run gen:api`).
- Fonts: premium serif display + clean sans body + Bengali-capable font (verify licences; self-host via `next/font`).
- PWA manifest and icons from the logo; `next/image` for all product images with blur placeholders.

### 5.2 Structure
```
juvo-front/src/
  app/
    (storefront)/   home, category, search, product/[slug], cart, checkout, order-success, track
    (account)/      account dashboard, orders, order detail+live tracking, addresses, wishlist, profile, returns, support
    (auth)/         login (staff + customer), register/quick-profile, otp, forgot/reset password
    (admin)/admin/  dashboard, orders, products, inventory, customers, delivery, couriers, payments, returns,
                    promotions, cms, notifications, reports, purchasing, accounting, support, social-orders,
                    fraud, tax-invoices, marketing, settings, audit, ums/*
    api/            BFF route handlers (auth cookie handling, file upload proxy if needed)
  modules/          one folder per business module (components, hooks, schemas, api wrappers) — includes modules/ums
  components/       shared UI (design system built on tokens)
  lib/              api client, auth, permissions, i18n, realtime, utils
  styles/           tokens.css (generated from Logo), globals.css
```

### 5.3 Login, token handling and route protection (must work perfectly end to end)
- A single branded **login page** (logo + brand colours) that authenticates against `POST /api/v1/auth/login`, supports staff login and customer OTP login, validation errors, lockout message, 2FA step, "remember device" optional.
- **Token management:** store access/refresh tokens in **httpOnly, Secure, SameSite cookies** set by Next.js route handlers (BFF pattern) so JavaScript never reads tokens; automatic silent refresh with a single in-flight refresh promise; on refresh failure → clear session and redirect to login with return URL; logout calls backend and clears cookies. Server components and route handlers forward the token to the API.
- **Route protection:** Next.js `middleware` checks session for `(account)` and `(admin)` areas and redirects; admin routes also check permissions.
- **Role/permission handling in UI:** `/auth/me` returns effective permissions; provide `usePermissions()`, `<Can permission="orders.order.confirm">`, route guards, and hide/disable menus, buttons and actions the user may not use. The admin sidebar is **built from permissions** (a user sees only modules they can access). Always still enforced on the server.
- Show friendly 401/403/404/500 pages with the logo.

### 5.3A Navigation (sidebar), user-friendliness and every-device layout (mandatory)

**Menus are a sidebar.** The admin panel and the customer account area use a **left sidebar navigation** (right side in Bengali only if RTL were needed; Bengali is LTR, so keep left). The storefront keeps a top header with search, cart and account, plus a category menu, but the admin and account areas are sidebar-based.
- **Sidebar structure:** logo at top, branch switcher (admin), then **grouped menu sections** that match the modules (for example Dashboard; Sales: Orders, Social orders, Returns; Catalog: Products, Categories, Inventory, Purchasing; Customers; Delivery: Zones, Couriers; Payments and Accounting; Marketing: Promotions, Campaigns; Content: CMS; Reports; Support; Settings; User Management: Users, Roles, Branches, Audit). Groups are **collapsible**, sub-items are indented, the **active page and its parent group are highlighted** with the brand colour, icons come from one consistent icon set, and items show a small badge for counts that need action (new orders, low stock, pending reviews) updated live.
- **Permission-built:** the sidebar is generated from the user's permissions and active branch; a user never sees menu items they cannot use, and empty groups are hidden.
- **Desktop (>= 1024 px):** fixed sidebar, expanded by default, user can **collapse to an icon-only rail** (with tooltips) and the choice is remembered. Top bar holds breadcrumbs, global search (command palette with shortcut such as Ctrl+K to jump to any page, order or customer), notifications bell, language switch, theme toggle, and the user menu (profile, change password, sign out).
- **Tablet (768–1023 px):** icon rail that expands as an overlay on tap.
- **Mobile (< 768 px):** sidebar becomes an **off-canvas drawer** opened by a hamburger button, closes on item tap, on outside tap and on swipe, traps focus while open, and the page behind does not scroll. For the customer account and key admin tasks also provide a **bottom navigation bar** with the 4–5 most used items (for example Orders, Products, Dashboard, Notifications, More).
- **Page layout:** consistent page header (title, breadcrumb, primary action button), content area with a sensible max width, sticky table headers and sticky action bars on long forms, and a "back to top" on long lists.

**Works perfectly on any device** (phones, tablets, laptops, large monitors, touch and mouse, small and large text sizes):
- Mobile-first CSS with fluid layouts (CSS grid/flex, `clamp()` for type and spacing); test widths **320, 360, 390, 412, 768, 1024, 1280, 1440, 1920 px** and landscape orientation. **No horizontal page scroll at any width**, no clipped or overlapping elements, no text that breaks the layout (also with long Bengali text and long names).
- Use `dvh/svh` viewport units and `env(safe-area-inset-*)` so phones with notches and browser bars display correctly; set the viewport meta tag correctly; prevent iOS input zoom (inputs at least 16 px font).
- **Tables adapt:** on desktop full data tables (sort, filter, column chooser); on small screens each row becomes a **card/list item** showing the key fields with an expandable "details" and the row actions in a menu; wide tables that must stay tabular scroll horizontally inside their own container only, with the first column sticky.
- **Forms adapt:** single column on mobile, multi-column on desktop, labels above fields, correct input types and `inputmode` (numeric keypad for mobile numbers and amounts), autofill attributes, big touch targets (**at least 44 x 44 px**), spacing between tap targets, inline validation, sticky submit bar. Dropdowns with search become **bottom sheets** on mobile. Modals become full-screen sheets on mobile.
- **Touch and mouse:** hover effects only where hover exists (`@media (hover: hover)`), visible keyboard focus states, swipe/drag where natural (image gallery, drawers), no hover-only information.
- **Images and media:** responsive `next/image` with `sizes`, aspect-ratio boxes to avoid layout jump, lazy loading, blur placeholders; product gallery supports swipe and pinch/zoom on mobile.
- **Performance on phones and weak networks:** code-splitting per route, lazy-load heavy components (charts, maps, editors), skeleton loaders, optimistic UI, small bundles; target good Core Web Vitals on a mid-range Android on 4G. Offline-friendly shell via the PWA where safe.
- **Accessibility and comfort:** WCAG 2.1 AA contrast, keyboard navigation for the whole sidebar and tables, ARIA landmarks/labels, `prefers-reduced-motion`, text scales up to 200% without breaking, light and dark theme from the brand tokens.
- **User-friendly behaviour:** clear empty states with a next action, friendly error messages in plain language (English and Bengali), confirmation for destructive actions with an undo where possible, loading and success feedback (toasts), remember filters and page size per screen, keyboard shortcuts for power users in admin, short inline help/tooltips on complex settings, and a first-time guided tour for the admin dashboard (skippable).
- **Verification (required, part of every UI step's gate):** use **Playwright** (verify the current package and commands) to run the key flows and capture screenshots at the widths listed above on Chromium plus mobile device emulation (an Android and an iPhone profile) and a tablet profile; automated checks for horizontal overflow and for small tap targets; run an accessibility scan (for example axe via Playwright, verify the package). Store screenshots in `juvo-front/docs/ui-checks/`, fix every visible defect, and list in the report what was checked at which sizes. Do not claim "works on all devices"; report exactly what was tested.

### 5.4 UI coverage (every module has a working UI)
Implement UI for **all modules A–AF** exactly as the documents describe: storefront (premium product cards auto-built from data, filters, search, gallery/zoom, size guide, reviews), cart drawer, wishlist, **guest checkout with map-based address selection** (Bangladesh Division/District/Upazila fields + map pin + autocomplete + current location; international address form), delivery/charge calculation, payment selection, order confirmation with quick-profile creation, public tracking, customer panel, and the complete admin panel (dashboard with live widgets, orders list/detail with timeline and three status tracks, one-click and bulk courier booking, payments and refunds, COD reconciliation, inventory and stock counts, purchasing, expenses and profit/loss, fraud review queue, invoices/tax, support tickets, promotions, marketing, CMS, notifications templates, reports with export, settings and feature switches, audit log, UMS).
- Live updates through SignalR on admin dashboard/orders and customer tracking; reconnect handling.
- Forms: React Hook Form + Zod; server validation errors (ProblemDetails) mapped to fields.
- Tables: server-side pagination/sort/filter, column chooser, export, bulk actions.
- Responsive mobile-first (breakpoints 360/768/1024/1280), accessibility WCAG 2.1 AA, loading skeletons, empty states, error states, optimistic updates where safe.
- English + Bengali switch with translation files for all UI text.

---

## 6. EXECUTION PLAN (follow in order; verify at each gate)

1. **Phase 0 — Understand:** read `Documents/` and `Logo/`, write `docs/understanding.md`, `docs/assumptions.md`, `docs/brand-tokens.md`, FR traceability table. Validate and connect the GitHub remote and make the first commits per section 0A (`.gitignore`, `.gitattributes`, docs), `PROGRESS.md`, `scripts/`. *Gate: documents summarised, palette extracted, local SQL Server reachable and `JuvoDb` database created.*
2. **Phase 1 — Backend foundation:** solution, projects, central packages, host, middleware, ProblemDetails, logging, health checks, OpenAPI, DbContext, base entities, audit, outbox, job worker. *Gate: builds, `/health/ready` OK, OpenAPI served.*
3. **Phase 2 — Identity & UMS (backend):** Identity, tokens, refresh rotation, permission catalog, roles, users, policies, seeding, UMS endpoints, **branches (section 4A: branch CRUD, user-branch-role assignment, branch scope filter, active-branch header, seed HO + default branch)**, tests. *Gate: integration tests prove login, refresh rotation, reuse detection, role→permission enforcement, 403 without permission, and branch isolation (user of branch A cannot see or change branch B data).*
4. **Phase 3 — Domain modules (backend):** all entities + migrations for the full ERD; Catalog, Cart, Customers/identity levels, Delivery/Zones, Checkout/Orders (state machine, stock reservation, idempotency), Payments, Couriers (+ sandbox adapters), Notifications, Real-time, Invoices/Tax, Returns, Fraud, Inventory/Purchasing/Accounting, Support, Promotions, Marketing, CMS, Reports, Platform services. Unit tests for state machine, stock, pricing, tax, promotions; integration tests for checkout→payment→courier→delivered flow and concurrent last-unit purchase. *Gate: all tests pass; full order flow works through the API in sandbox.*
5. **Phase 4 — Frontend foundation:** Next.js project, tokens from logo, design system components, i18n, API client generation, BFF auth, middleware, login page, permission system, app shells (storefront, account, admin). *Gate: login works against the real backend; refresh works; role-based menu works; `npm run build` passes.*
6. **Phase 5 — UMS UI (including branches, branch-user-role grid, header branch switcher), then Admin modules (all branch-aware), then Storefront and Customer panel**, module by module, each with real API calls and live updates. After each module run lint, type-check, and Playwright (verify) smoke tests for key flows (guest checkout, login, role assignment, order processing).
7. **Phase 6 — Hardening:** security review, NFR checks (performance, accessibility), seed demo data, README and runbook, `docs/api.md`, final traceability table (every FR = Done/Partial/Deferred with reason), final `PROGRESS.md`.

---

## 7. RUN AND DELIVERY REQUIREMENTS

- One-line start documented in `README.md`: `scripts\setup-db.ps1`, `dotnet run --project juvo-be/src/Juvo.Api`, `npm run dev` in `juvo-front` (plus a root script that does all of it). Dockerisation is explicitly out of scope for now.
- Default seeded Super Admin credentials printed in README for development only, with a forced password change on first login.
- `.env.example` for both projects; no secrets committed.
- Final message to me must contain: what was built, how to run it, URLs/ports, seeded logins, the list of anything **Partial/Deferred** with reasons, and what I must provide for production (real courier/payment/SMS/map keys, domain, hosting).
- Be honest in the final report: do not claim a feature works unless you ran it and saw it work.

Begin with Phase 0 now.