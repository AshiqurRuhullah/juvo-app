# JUVO BD — STEP-BY-STEP PROMPTS FOR CLAUDE CODE

How to use:
1. Keep `juvo-claude-code-prompt.md` (the master prompt) in `F:\JUVO App\JuvoApp`.
2. Open Alacritty, `cd "F:\JUVO App\JuvoApp"`, run `claude`.
3. For each step: paste **the COMMON HEADER first, then the STEP PROMPT**, in one message. Do one step per session.
4. When the step finishes, run the app and check it yourself, then type `/clear` (or start a new session) and go to the next step.
5. If a session stops half way (context full, error): start a new session and paste the RESUME PROMPT at the bottom.

---

## STEP 0 — Preparation (you do this, not Claude)

- [ ] Installed and working: Git, .NET 10 SDK (`dotnet --list-sdks`), Node.js LTS (`node -v`), Microsoft SQL Server (Developer/Express/LocalDB) + SSMS or `sqlcmd`, a code editor.
- [ ] Logged in to GitHub on this machine (Git Credential Manager or `gh auth login`) with an account that can push to `https://github.com/AshiqurRuhullah/juvo-app.git`.
- [ ] `Documents\Juvo BD — E-Commerce Platform BRD & SRS.docx` and the logo files in `Logo\` are in place; `juvo-be` and `juvo-front` folders exist (empty).
- [ ] `juvo-claude-code-prompt.md` is in `F:\JUVO App\JuvoApp`.
- [ ] Start merchant/provider onboarding early (courier, payment gateway, SMS, map key). It takes time and is not needed until Step 14.

---

## COMMON HEADER (paste before every step prompt)

```
Read `juvo-claude-code-prompt.md` (the master prompt) completely, then read `PROGRESS.md` and `docs/understanding.md` if they exist. The BRD/SRS in Documents is the business source of truth.

Do ONLY the step described below. Do not start the next step.
Rules for this step:
- Follow the master prompt's ground rules (section 0), Git workflow (section 0A: small tasks, Conventional Commit messages, push to origin after every small task), and the section numbers referenced below.
- Verify every package/API/CLI command before using it; never invent names; check licences.
- No placeholders or fake data: real backend, real database, sandbox adapters for external providers.
- Build/test before every commit; fix failures before continuing.
- Do not ask me questions; decide, and record decisions in docs/assumptions.md.
When the step's gate passes: update PROGRESS.md (what is done, what is next, known issues), update the FR traceability table in docs/understanding.md, commit, push, and report: what was built, how to run/see it, anything Partial/Deferred, `git log --oneline -n 15` and `git status -sb`. Then stop.
```

---

## STEP 1 — Understand the business, set up Git and project docs

```
STEP 1 of 14: Understand and prepare (master prompt Phase 0; sections 0A, 1, 2).
Do NOT write application code in this step.
Tasks (one small commit each):
1. Validate Git and the GitHub remote exactly as section 0A says (git ls-remote, set origin, integrate existing remote commits safely, never force-push). If anything is wrong, stop and tell me exactly what.
2. Add root .gitignore and .gitattributes (Windows, .NET, Next.js, secrets).
3. Convert `Documents\Juvo BD — E-Commerce Platform BRD & SRS.docx` to Markdown at docs/source/brd-srs.md; verify all 16 sections, all tables and the Mermaid ERD came through; read the whole document.
4. Write docs/understanding.md: module matrix (A to AF), order/payment/delivery state machines, FR traceability table (every FR/NFR/BR ID with status Not started), final ERD including branches (section 4A).
5. Write docs/assumptions.md (decisions for every open question in SRS 14.5, with the default you choose) and record the Git remote validation result.
6. Extract the real colour palette from `Logo\` files; write docs/brand-tokens.md and juvo-front tokens source (not the app yet); copy logos to the places section 2 allows.
7. Create PROGRESS.md with all 14 steps and their status, plus scripts/ folder with setup-db.ps1 (create database JuvoDb on local SQL Server; clear message if SQL Server is missing).
Gate: all docs exist and are consistent with the SRS; palette extracted with contrast check; SQL Server reachable and database JuvoDb created; everything pushed to origin.
```

---

## STEP 2 — Backend foundation

```
STEP 2 of 14: Backend foundation (master prompt Phase 1; sections 3.1, 3.2, 3.4).
Create the ASP.NET Core 10 solution INSIDE the existing `juvo-be` folder: Juvo.Api, Juvo.Application, Juvo.Domain, Juvo.Infrastructure, Juvo.Shared + test projects, with Directory.Build.props, Directory.Packages.props, .editorconfig, nullable and analyzers on.
Implement (small commits): host setup and DI composition; configuration/options validation; ProblemDetails + global exception handler + correlation id; structured logging; health checks (/health/live, /health/ready); API versioning (/api/v1); OpenAPI + interactive UI; CORS for http://localhost:3000; rate limiting; result pattern + error codes; pagination/sort/filter contract; DbContext for SQL Server (Microsoft.EntityFrameworkCore.SqlServer) with base entity (id, created/updated, rowversion, soft delete), audit log table and interceptor, transactional outbox + background worker with retry and job log; TimeProvider; IFileStorage (local disk) abstraction; first migration; test projects wired with a sample passing test and an integration-test fixture using a separate local SQL Server test database.
Gate: `dotnet build` zero errors/warnings-as-errors, `dotnet test` passes, API runs, /health/ready OK (DB connected), OpenAPI page loads, migration applied on JuvoDb.
```

---

## STEP 3 — UMS backend: auth, roles, permissions, branches

```
STEP 3 of 14: User Management System backend including multi-branch (master prompt Phase 2; sections 4 and 4A).
Implement in the Identity module (small commits, with tests):
- ASP.NET Core Identity users (staff), password policy, lockout, optional TOTP 2FA (mandatory for Super Admin), sign-in history.
- JWT access token (short) + rotating refresh token stored hashed with device/IP/user-agent, reuse detection revoking the token family, revoke on logout/password change/role change/deactivation, permission-version stamp.
- Endpoints: login, refresh, logout, logout-all, me (profile, branches, roles per branch, effective permissions), change/forgot/reset password, 2FA, customer OTP request/verify (customer identity can be a thin version now; full flows come later).
- Permission catalog generated from the SRS (every module/feature as module.resource.action), seeded, versioned; Roles (many-to-many with permissions); default roles from the SRS matrix; system roles protected; Super Admin has all permissions including future ones.
- Branches: Branch entity and CRUD, UserBranchRole (user, branch, role), global scope flag, active-branch header (X-Branch-Id) validated on every request, seed Head Office + Online Fulfilment branch.
- Permission-based authorization (policy provider/requirement handler) combined with branch scope; EF Core global query filters for branch-scoped entities (use a sample branch-scoped entity or the BranchId base interface for now); audit log records branch.
- UMS admin endpoints: users CRUD/activate/deactivate/reset password/force logout/sessions, roles CRUD/clone/permission assignment, permission catalog, user-branch-role assignment, branches CRUD, audit log search, sign-in history.
- Seed: super admin (dev only credentials, forced password change), permissions, roles, branches.
Gate: integration tests prove login, refresh rotation, reuse detection, 403 without permission, role change effective quickly, branch isolation (user of branch A cannot read/modify branch B data), per-branch role, global scope; Swagger/OpenAPI shows all endpoints; all pushed.
```

---

## STEP 4 — Front-end foundation, brand, login and token handling

```
STEP 4 of 14: Front-end foundation (master prompt Phase 4; sections 1.2, 5.1, 5.2, 5.3, 5.3A).
Scaffold Next.js (latest stable, App Router, TypeScript strict, Tailwind) directly INSIDE the existing `juvo-front` folder (no nested folder). Install and verify all libraries from section 5.1.
Implement (small commits): brand tokens (tokens.css from the logo palette) and logo usage (header, login, favicon, manifest icons); design-system components (buttons, inputs, selects with search, checkbox/radio/switch, cards, image frames, modal/sheet, toasts, skeletons, tables, badges, empty states) using only tokens; typed API client generated from the backend OpenAPI (npm run gen:api); BFF route handlers that set httpOnly Secure SameSite cookies for access/refresh tokens, single in-flight refresh, logout; Next.js middleware for route protection; usePermissions(), <Can>, route guards; i18n (English + Bengali) skeleton; the branded login page (staff login, 2FA step, customer OTP login placeholder wired to the real endpoints); admin shell with the permission-built collapsible sidebar, header (branch switcher, search palette, notifications, language, theme, user menu), mobile drawer and bottom navigation, tablet rail (section 5.3A); account and storefront shells; 401/403/404/500 pages with the logo.
Gate: login works against the real backend; silent refresh works; logout clears session; sidebar shows only permitted items per role and per branch; `npm run lint` and `npm run build` pass; Playwright screenshots at 360/768/1280 saved in juvo-front/docs/ui-checks with no horizontal overflow.
```

---

## STEP 5 — UMS user interface (users, roles, branches)

```
STEP 5 of 14: UMS UI (section 4.3, 4A.4, 5.3A).
Build under /admin/ums/ in juvo-front/src/modules/ums, all using the real API, permission-guarded, fully responsive:
- Users: list (search/filter/pagination), create/edit, activate/deactivate, reset password, force logout, sessions, sign-in history.
- Roles: list, create/edit/clone, module-grouped permission matrix with checkboxes and select-all per module.
- Branches: list, create/edit with map pin, activate/deactivate, set default.
- User ↔ branch ↔ role assignment grid; global scope toggle; effective-permissions viewer per user per branch.
- Audit log viewer; My profile and security (password, 2FA, sessions).
- Active-branch switcher affecting data requests.
Gate: in the browser, create a role with a few permissions, create a user, assign branch + role, log in as that user and confirm only those menus/actions work and other branches' data is not visible; Playwright flow tests and mobile screenshots pass; lint/build pass; pushed.
```

---

## STEP 6 — Catalog, media and branch-wise inventory (backend + UI)

```
STEP 6 of 14: Catalog and inventory (modules B and V; sections 3.2, 3.3 image pipeline, 4A.2, 4A.3).
Backend + admin UI together, small commits:
- Categories (tree), brands, products, variants (colour/size/SKU), pricing and sale dates, size charts, SEO fields, status Draft/Published/Archived, publish rules (BR-B1..B3), product audit.
- Image pipeline: upload, validate, resize, WebP, focal point, blur placeholder, per-colour sets, reorder; IFileStorage local disk.
- Branch-wise stock locations, stock on hand/reserved/available per variant per branch, stock movements (append-only), adjustments with reason, low-stock thresholds, inter-branch transfer workflow (Requested → Approved → In transit → Received), stock count (stocktake) basics.
- Bulk import/export CSV/Excel with validation report.
- Admin UI: product list, add/edit product with live storefront card preview, variants/stock grid, categories, inventory by branch, transfers, adjustments.
- Seed sample categories (clothing, shoes, ladies' dresses, purses) and sample products with images.
Gate: adding a product in the admin produces a correct premium card preview; stock is per branch; transfer updates both sides; branch isolation holds for stock; unit/integration tests pass; UI screenshots at mobile/tablet/desktop; pushed.
```

---

## STEP 7 — Storefront, cart, customer identity, address and delivery zones

```
STEP 7 of 14: Storefront and customer side (modules A, C, E, F, G Bangladesh part; sections 5.2, 5.4).
Backend + front end together, small commits:
- Storefront: home (hero, categories, new arrivals, best sellers, sale), category pages, filters/sort/search, product detail (gallery with swipe/zoom, variants, live stock, size guide, delivery estimate), auto-designed premium product cards, wishlist, language switch.
- Cart (guest cart persisted, merge on sign-in, stock limits), coupons (basic).
- Customer identity: guest, quick profile (mobile + optional email, OTP), full profile; automatic attach of guest orders after OTP; consent; OTP rules and rate limits (sandbox SMS sender writes to the notification log).
- Customer panel: dashboard, address book, profile, wishlist (orders come next step).
- Delivery: Bangladesh Division/District/Upazila data (verify and record the dataset source), delivery zones, rate slabs, free-delivery threshold, charge calculation API, estimated delivery days.
- Map address picker: Leaflet + OpenStreetMap by default, search/autocomplete, drop pin, current location, reverse-geocode to fields, manual correction.
Gate: as a guest on a phone-size screen: browse, filter, open product, add to cart, enter mobile + address with map pin, see correct delivery charge; quick profile creation works; Playwright mobile/tablet/desktop flows and screenshots pass; pushed.
```

---

## STEP 8 — Checkout, orders and order processing

```
STEP 8 of 14: Checkout and orders (modules D, S, T basics, J basics; sections 3.3, 11.1 of the SRS, 4A.3).
Backend + UI, small commits:
- Order aggregate with the three tracks (order/payment/delivery status), allowed-transition state machine, status history, domain events, idempotent order creation, duplicate-order guard, order numbers with branch prefix, snapshots.
- Stock reservation in one transaction with locking + rowversion check, release on cancel/timeout, payment hold timeout job; order routing to a fulfilling branch (rules in 4A.3) and manual reassign before packing.
- Checkout page (single page on mobile): contact, address, delivery method, payment (COD now), review, confirm; totals recalculated server-side; confirmation page with "create my profile" prompt; public tracking page (order no. + mobile).
- Admin orders: list with filters, order detail with timeline and three tracks, confirm/pack/cancel, notes, bulk actions, print packing slip.
- Social order desk (module S): staff create manual orders with source tag.
- Fraud basics (module T): risk flags, customer history, blacklist, review queue.
- Customer panel: my orders, order detail with status.
Gate: tests prove concurrent buyers cannot take the last unit, invalid transitions are rejected, timeout releases stock; end to end: guest places COD order → admin confirms and packs → customer sees status; branch isolation holds; screenshots; pushed.
```

---

## STEP 9 — Payments, tax/invoices and notifications

```
STEP 9 of 14: Payments, invoicing and notifications (modules I, U, K).
Backend + UI, small commits:
- IPaymentGateway adapter + Sandbox gateway (initiate, verify, refund, query), checkout redirect/return flow, server-side verification before Paid, idempotent webhook endpoint with signature check, payment statuses, partial/advance payment rule, manual payment verification queue, refunds, payment ledger and reconciliation view.
- Multi-currency fields and exchange rate handling for international display.
- Tax/VAT rules (inclusive/exclusive), gap-free invoice and credit-note numbering per branch, invoice PDF with logo and brand colours (verify the PDF library's licence), downloadable from customer panel and admin.
- Notification engine: templates (EN/BN with variables), SMS/email/in-app channels with sandbox providers writing to the notification log, retries via outbox, admin template editor and log viewer.
Gate: online sandbox payment flow ends with order Confirmed; duplicate/forged webhooks change nothing; refund creates credit note; invoice PDF opens correctly; notifications are logged and retried; tests pass; pushed.
```

---

## STEP 10 — Couriers, real-time status and returns

```
STEP 10 of 14: Courier integration, real-time updates and returns (modules H, J, L).
Backend + UI, small commits:
- ICourierProvider adapter + Sandbox courier that simulates booking and webhook status events; courier settings (encrypted credentials), per-branch pickup address, one-click and bulk booking, label printing, webhook endpoint with signature check and duplicate protection, raw and mapped statuses, failed-delivery flow, COD collection and settlement tracking.
- SignalR hubs (admin, per-branch groups; customer own-order group) for orders, payments, delivery, stock and alerts; polling fallback; live updates in admin dashboard/orders and customer tracking.
- Cancellation, return request (photos), return workflow, exchange, restock/damage handling, refund rules.
- Admin UI: courier list/settings, booking from order screen, COD settlement list, returns queue.
Gate: end to end in the browser: order → pack → book sandbox courier → simulated courier events update admin and customer screens live without refresh → delivered → COD settled; return and refund path works; tests pass; pushed.
```

---

## STEP 11A — Operations modules, part 1

```
STEP 11A of 14: Purchasing, expenses, support, reports (modules W, X, Y, P).
Backend + UI, small commits, all branch-aware with a consolidated view for global users:
- Suppliers, purchase orders, goods receipt (updates stock and cost), supplier balance.
- Expenses, cash book, automatic entries (courier charges, gateway fees, refunds, purchases), profit and loss.
- Support tickets linked to orders, statuses, canned replies (EN/BN), customer updates.
- Reports: sales, orders funnel, inventory, finance, courier performance, customers; filters by branch and date; Excel/CSV/PDF export.
Gate: report totals match a manual calculation on seeded test data; branch filter and consolidated view correct; tests pass; screenshots; pushed.
```

---

## STEP 11B — Operations modules, part 2

```
STEP 11B of 14: Growth modules (Z, AA, AB, AC, AD, basic AE).
Backend + UI, small commits:
- Promotions engine (flash sale, bundle, buy X get Y, free delivery, festival campaigns, stacking rules).
- Pre-order/backorder.
- Loyalty points, referral codes, store credit/gift cards with ledger.
- Marketing: customer segments, SMS/email campaigns (consent-based), automations (abandoned cart, back-in-stock, review request, win-back).
- Reseller/affiliate accounts and commission statements.
- Basic POS screen and per-branch cash register report (module AE).
Gate: stacking rules produce one clear price at cart/checkout/invoice; campaigns respect consent; POS sale reduces the branch's stock; tests pass; pushed.
```

---

## STEP 12 — Remaining admin UI, CMS, dashboard, settings, platform services

```
STEP 12 of 14: Finish the admin experience (modules M, N leftovers, Q, R, AF).
Backend + UI, small commits:
- Admin dashboard with live widgets (today cards, action queue, sales chart, top products, cash view, stock health) per branch and consolidated.
- CMS: home sections, hero slider, About page, policy pages, FAQ, contact page and inbox, social links, trust badges; SEO fields, sitemap, share previews, structured data, analytics hooks with cookie consent.
- Settings centre and feature switches, delivery/tax/OTP/return settings, per-branch overrides, notification channel switches.
- Platform services: background job dashboard with retry, integration health page, backup/restore and import/export tools, data-privacy tools (consent log, export, anonymise), public API keys and webhooks, PWA install and push, full Bengali translation pass, staff help tips and dashboard tour.
- Walk through every sidebar item: each page must have real data, working actions, empty/error states and permissions.
Gate: every menu item works with real API data; Playwright smoke test visits every admin route at mobile/tablet/desktop with no overflow or console errors; accessibility scan passes; pushed.
```

---

## STEP 13 — Hardening, QA and documentation

```
STEP 13 of 14: Hardening (master prompt Phase 6; sections 3.4 and NFR/acceptance of the SRS).
Tasks (small commits):
- Security review against OWASP Top 10: authorization on every endpoint (list endpoints without a permission policy and fix), input validation, rate limits, secure headers, secrets, encrypted credentials, PII access logging, dependency vulnerability audit (dotnet list package --vulnerable, npm audit).
- Performance: indexes on slow queries, N+1 checks, caching where safe, front-end bundle size and Core Web Vitals on mobile profile.
- Full acceptance checklist from SRS 14.2 run and recorded in docs/acceptance-results.md (pass/fail with evidence).
- End-to-end Playwright suite for the key flows (guest checkout, login/refresh, role assignment, branch isolation, order processing, courier + payment sandbox, return); run on desktop, tablet and mobile profiles.
- Backup and restore of the SQL Server database tested; migration commands documented.
- Documentation: README.md, docs/runbook.md, docs/api.md, final FR/NFR traceability (every ID Done/Partial/Deferred with reason), seeded logins for development.
Gate: all tests pass, acceptance checklist results recorded honestly, no known critical/high security issues open, working tree clean and pushed.
```

---

## STEP 14 — Real providers and go-live preparation

```
STEP 14 of 14: Go-live preparation (master prompt section 7; SRS Phase 5).
Do NOT claim a real provider works unless you ran it against that provider's test/sandbox environment with credentials I supply.
Tasks (small commits):
- Implement the real adapters I name (courier(s), payment gateway(s), SMS provider, map provider) against each provider's current official documentation (fetch and cite it in docs/providers/*.md), keeping the sandbox adapters. Configuration only through environment/user-secrets; list exactly which keys I must provide in .env.example and docs/runbook.md.
- Production configuration: environments, HTTPS, CORS for the real domain, secure cookies, production migration procedure, logging/monitoring/alerting, backup schedule, data retention; production build of front end and back end; deployment instructions for the target I name (no Docker unless I ask).
- Seed/real data import: categories, products, branches, delivery zones, tax rules, policies and About page content from the files I provide.
- Final smoke test checklist and rollback plan in docs/go-live.md.
Gate: each real adapter passes its provider-sandbox test or is clearly marked "not verified: needs credentials"; go-live checklist exists; everything pushed and tagged `v1.0.0` only if I confirm.
```

---

## RESUME PROMPT (when a session stops half way)

```
Read `juvo-claude-code-prompt.md`, `PROGRESS.md`, `docs/understanding.md`, and run `git status -sb` and `git log --oneline -n 20`. Continue STEP N (name) exactly where it stopped: finish the unfinished task, do not redo completed commits, and follow the same rules and gate. If the working tree has uncommitted changes, review them, make them build, and commit them with a proper Conventional Commit message. Push, update PROGRESS.md, report, then stop.
```

## SPLIT PROMPT (if a step is too big for one session)

```
This step is too large for one session. Split STEP N into 2–3 sub-steps by module in PROGRESS.md (N-a, N-b, N-c), do only N-a now with its own gate, commit and push, then stop.
```