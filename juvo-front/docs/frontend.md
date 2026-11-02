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
| `/admin/...` (users, roles, branches, audit) | staff | Step 5 |
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
| Catalog | Products, Categories | `catalog.product.view`, `catalog.category.view` | anywhere | 6 |
| | Inventory | `inventory.stock.view` | active | 6 |
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

## Design system

Colours are CSS variables from the logo (`docs/brand-tokens.md`) exposed to Tailwind through `@theme inline` (`primary`, `surface`, `card`, `line`, `strong`, `muted`, `success`, `warning`, `danger`, `info`); the dark theme redefines the same variables. Every button, link and field is at least 44 px (`size-11`, `min-h-11`) and has a visible focus ring. Radix gives keyboard and screen reader behaviour for dialogs, menus, tabs, select, checkbox, radio and switch. Forms use react-hook-form with zod and show server problem details through `use-problem-message`. Tables use TanStack Table v8 (sorting, filters, pagination, column visibility, sticky header, card layout on phones). Icons: lucide. Toasts: sonner.

Accessibility notes found by the automated checks and fixed: icon buttons must not shrink in flex rows (`shrink-0`); a control with visible text must carry that text in its accessible name (command palette button, account menu); the icon rail keeps labels as visually hidden text so links keep a name; nested dropdowns do not work on phones, so the account menu lists language and theme choices inline.

## Test evidence

- `npm test`: 39 unit tests (permission rules, menu visibility, `safeNext`, JWT timing, single-flight refresh, en/bn message parity).
- `npm run test:e2e`: 104 passed on desktop, tablet, Android and iPhone projects against the real API (login, wrong password, httpOnly tokens, token endpoints blocked, protected redirects, open-redirect ignored, silent refresh, 6 parallel requests share one refresh, role change effective without re-login, forced first-login, customer OTP, menu per role and per branch, forged branch cookie refused, responsive shell, command palette, language and theme switch, empty notifications).
- UI checks at 360/768/1280 px on every page: no sideways scroll, no tap target under 44 px, no axe violations (WCAG 2.1 A and AA), no console errors, plus 200% and 400% text zoom. Key screenshots are in `docs/ui-checks/`.
