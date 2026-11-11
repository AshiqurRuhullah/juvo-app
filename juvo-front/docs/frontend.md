# Front end: design and decisions (Step 4)

## Session and tokens (BFF)

The browser never calls the API. It calls this app, and this app calls the API with the token.

| Cookie | Content | Flags |
| --- | --- | --- |
| `juvo_at` | access token (15 min) | httpOnly, SameSite=Lax, path `/`, Secure in production |
| `juvo_rt` | refresh token | httpOnly, path `/api/bff` only, so it is not sent with page requests |
| `juvo_rf` | 15 s marker "just refreshed" | httpOnly; tells the next request not to refresh again |
| `juvo_branch` | active branch id or `all` | httpOnly; validated on every request (a forged value is refused by the API) |
| `juvo_locale`, `juvo_theme`, `juvo_sidebar` | preferences | readable; no secrets |

- **Generic proxy** `/api/bff/api/v1/...` adds `Authorization: Bearer` and `X-Branch-Id`, renews an expired access token and retries once. Endpoints that return tokens (login, refresh, logout, change-password, OTP verify) are **blocked** in the generic proxy; they have dedicated routes (`/api/bff/auth/*`) that set cookies and answer with no token in the body.
- **Single in-flight refresh** (`src/lib/server/refresh.ts`): the API cuts the whole session if a used refresh token comes back, so callers sharing one refresh token share one request, and for 10 s afterwards get the same result. Proven by a unit test and an e2e test with 6 parallel requests.
- **`src/proxy.ts`** (matcher `/admin`, `/account`, `/first-login`) sends the browser through `/api/bff/auth/refresh?next=...` when the access cookie is missing or about to expire, so server components always see a fresh token. `safeNext()` rejects anything that is not a same-site path (open redirect).
- **`requireSession()`** (server) loads `/auth/me`; limited sessions (forced password change, 2FA setup) go to `/first-login`; the wrong subject type (customer on admin pages and the reverse) goes to `/forbidden`.
- Session ended mid-use: the client middleware redirects to `/login?next=...&reason=expired`.

## Permissions in the UI

`src/lib/auth/permissions.ts` mirrors the server (`UserAccess`/`PermissionChecker`): `can(access, code, scope)` where scope `active` = the active branch (any assignment in "All branches" mode), `anywhere` = any assignment (global features). `useSession()`, `usePermissions()` and `<Can permission="...">` read the session loaded on the server. Hiding is for display only; the API enforces every request.

## Route map

| Route | Who | State |
| --- | --- | --- |
| `/` | everyone | storefront placeholder (Step 7) |
| `/login`, `/login?as=customer`, `/forgot-password`, `/reset-password`, `/first-login` | public / limited session | done |
| `/admin`, `/admin/profile` | staff | done (profile: password, 2FA, sessions, sign-in history) |
| `/admin/ums/users`, `/roles`, `/branches`, `/audit`, `/sign-ins` (+ `[id]`, `new`) | staff | done (Step 5) |
| `/admin/products`, `/new`, `/[id]` (tabs: details, variants and stock, pictures, search and sharing; `?tab=`) | staff | done (Step 6-e) |
| `/admin/categories` (tabs: categories, brands, size charts; `?tab=`) | staff | done (Step 6-e) |
| `/admin/inventory` (stock), `/movements`, `/transfers`, `/transfers/new`, `/transfers/[id]`, `/counts`, `/counts/[id]` | staff | done (Step 6-e) |
| `/media/...` (product pictures from the API, public, cached for a year) | everyone | done (Step 6-e) |
| `/account` | customer | placeholder (Step 7) |
| `/forbidden` (403), `/unauthorized` (401), not-found (404), error and global-error | all | done |
| `/design` | development only (404 in production unless `ENABLE_DESIGN_PAGE=true`) | every component in every state |

## Menu: permission to item

Items for screens that do not exist yet are shown muted with a "Soon" tag, never as dead links. A user sees an item only if they hold its permission in the active branch (`scope: anywhere` items: in any assignment). Empty groups disappear. Defined in `src/lib/nav.ts`.

| Group | Item (route) | Permission | Scope | Built in |
| --- | --- | --- | --- | --- |
| Overview | Dashboard (`/admin`) | none | | Step 4 |
| Sales | Orders (`/admin/orders`) | `orders.order.view` | active | 8 |
| | Social orders | `orders.order.create` | active | 8 |
| | Returns | `returns.return.view` | active | 10 |
| Catalog | Products, Categories | `catalog.product.view`, `catalog.category.view` | anywhere | 6 (done) |
| | Inventory | `inventory.stock.view` | active | 6 (done) |
| | Purchasing | `purchasing.po.view` | active | 11A |
| Customers | Customers | `customers.customer.view` | anywhere | 12 |
| Delivery | Zones; Couriers | `delivery.zone.view` (anywhere); `couriers.courier.view` | | 7; 10 |
| Money | Payments; Tax and invoices; Expenses | `payments.payment.view`; `tax.invoice.view`; `accounting.expense.view` | active | 9; 9; 11A |
| Marketing | Promotions; Campaigns | `promotions.promotion.view`; `marketing.campaign.view` | anywhere | 11B |
| Content | CMS | `cms.page.view` | anywhere | 12 |
| Insights | Reports; Support | `reports.sales.view`; `support.ticket.view` | active | 11A |
| System | Settings | `settings.system.view` | anywhere | 12 |
| Administration | Users, Roles, Branches, Audit | `ums.user.view`, `ums.role.view`, `ums.branch.view`, `ums.audit.view` | anywhere | 5 |
| Account (customer) | Account home; My orders; Addresses; Wishlist | none | | 7 |

## Layout by screen size

- **1024 px and up:** sidebar with labels (collapsible to an icon rail; the choice is the `juvo_sidebar` cookie, so the server renders it without flicker).
- **768 to 1023 px:** icon rail; labels become tooltips; the full menu opens as a drawer.
- **Below 768 px:** off-canvas drawer (Escape, outside tap) plus a bottom bar with the 4 most used pages and "More". Language and theme live in the account menu.
- Ctrl+K (Cmd+K) opens the command palette: jump to any page the user may open.

## Design refresh (after Step 6)

- **Type:** Roboto everywhere (self-hosted by `next/font`, weights 400, 500, 700), then Noto Sans Bengali for Bengali letters. Body and UI: `'Roboto', system-ui, -apple-system, 'Segoe UI', sans-serif`; buttons, inputs, selects, textareas and tables declare `'Roboto', system-ui, sans-serif` explicitly in `globals.css` (browsers do not inherit fonts into form controls). `font-display` is an alias of the same stack; headings are Roboto 500 (h1 700) with slightly tighter tracking. Inter and Playfair Display were removed.
- **Colour:** the logo red is for primary actions and the active state only. Everything else is calm warm neutrals (page `#F7F5F1`, white cards, soft borders and shadows) plus **soft tinted chips**: each of success, info, warning, danger and neutral has a `-soft` background and a readable `-ink` text colour (every pair is AA, see `docs/brand-tokens.md`). Badges and alerts use them (Published green, Draft grey, Low stock and Best seller amber, Featured and In transit blue, New brand tint, Sale solid brand red); danger chips always carry an icon and text.
- **Tables:** a quiet header (small, muted, no uppercase), generous cell padding, light row separators, a gentle hover tint, numeric columns right-aligned with tabular figures (`meta.numeric`), phone cards unchanged, a keyboard-focusable scroll region when a table is wider than its box.
- **Menu (parent and child):** each group with several items is a collapsible parent row (icon, label, chevron) with its children indented under a thin guide line; the parent of the current page is open and tinted, the current child has the soft brand tint and a small accent bar; a group with one item is a plain top-level link; items not built yet are muted with "Soon". Open or closed parents are remembered in the browser (`juvo_nav_open` in localStorage, optional). On tablets and in the collapsed desktop sidebar the menu is an icon rail: a parent opens a small list of its children next to the rail, a single link has a tooltip. The phone drawer shows the same tree. The customer account menu is one flat list. The code is `components/layout/nav-list.tsx` (`NavTree`, `NavRail`) and `lib/nav.ts` (a group icon and `isLeafGroup`).
- **Dashboard** (`modules/dashboard`): KPI cards with sparklines, a 14-day sales and orders chart, orders by status, an action queue, top products, cash and COD, stock health and recent orders. **Everything on it is sample data** (labelled with a "Sample data" chip) from one file, `modules/dashboard/sample-data.ts`; replace `buildSampleDashboard` with the real report endpoints later and nothing else changes. Charts (Recharts) load lazily in the browser, carry a text summary and a hidden data table, and never rely on colour alone (legends with numbers).

## Design system

Colours are CSS variables from the logo (`docs/brand-tokens.md`) exposed to Tailwind through `@theme inline` (`primary`, `surface`, `card`, `line`, `strong`, `muted`, `success`, `warning`, `danger`, `info`); the dark theme redefines the same variables. Every button, link and field is at least 44 px (`size-11`, `min-h-11`) and has a visible focus ring. Radix gives keyboard and screen reader behaviour for dialogs, menus, tabs, select, checkbox, radio and switch. Forms use react-hook-form with zod and show server problem details through `use-problem-message`. Tables use TanStack Table v8 (sorting, filters, pagination, column visibility, sticky header, card layout on phones). Icons: lucide. Toasts: sonner.

Accessibility notes found by the automated checks and fixed: icon buttons must not shrink in flex rows (`shrink-0`); a control with visible text must carry that text in its accessible name (command palette button, account menu); the icon rail keeps labels as visually hidden text so links keep a name; nested dropdowns do not work on phones, so the account menu lists language and theme choices inline.

## Test evidence

- `npm test`: 39 unit tests (permission rules, menu visibility, `safeNext`, JWT timing, single-flight refresh, en/bn message parity).
- `npm run test:e2e`: 104 passed on desktop, tablet, Android and iPhone projects against the real API (login, wrong password, httpOnly tokens, token endpoints blocked, protected redirects, open-redirect ignored, silent refresh, 6 parallel requests share one refresh, role change effective without re-login, forced first-login, customer OTP, menu per role and per branch, forged branch cookie refused, responsive shell, command palette, language and theme switch, empty notifications).
- UI checks at 360/768/1280 px on every page: no sideways scroll, no tap target under 44 px, no axe violations (WCAG 2.1 A and AA), no console errors, plus 200% and 400% text zoom. Key screenshots are in `docs/ui-checks/`.
