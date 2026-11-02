# Authentication, permissions and branches (backend contract)

For the front end (Step 4), the UMS screens (Step 5) and anyone calling the API. Implemented in Step 3; verified by the integration tests in `juvo-be/tests/Juvo.Api.IntegrationTests/Ums`.
All paths are under `/api/v1`. Errors are RFC 9457 problem details with a stable `code` and a `correlationId` (see `juvo-be/README.md`). Open `https://localhost:7080/scalar/v1` for every endpoint.

## 1. Tokens

| Token | Life | Where it lives | Purpose |
| --- | --- | --- | --- |
| Access token (JWT, HS256) | 15 min | memory / httpOnly cookie set by the Next.js BFF | `Authorization: Bearer <token>` on every call |
| Refresh token (random 32 bytes) | staff 14 days, customers 30 days | httpOnly cookie set by the BFF | exchange for a new pair at `POST /auth/refresh` |

- Only the SHA-256 hash of a refresh token is stored. Every refresh **rotates**: the old token is marked used and a new one in the same *family* (one sign-in on one device) is issued.
- **Reuse detection:** presenting an already-used or revoked refresh token cuts the whole family (`401 AUTH.REFRESH_REUSED`). The front end must keep **one in-flight refresh** at a time, otherwise two tabs can cut each other's session.
- The access token holds `sub`, `name`, `stype` (`staff` or `customer`), `sid` (session = family id), `pv` (permission version) and, for restricted sessions, `scope=limited`. **Permissions are not in the token.**
- **Stale tokens:** if roles, assignments, branch state, password or active state change, the user's `pv` changes. The next call with an older token returns `401 AUTH.TOKEN_EXPIRED`; the client refreshes silently and carries on with the new permissions. Deactivation answers `401 AUTH.ACCOUNT_DISABLED` and revokes the refresh tokens.
- Revoked on: logout (that session), logout-all, password change or reset, admin force-logout, deactivation, two-step reset, reuse detection.

### Error codes the client must handle

| Code | HTTP | Meaning / client action |
| --- | --- | --- |
| `AUTH.INVALID_CREDENTIALS` | 401 | Wrong identifier, password or code (same answer for unknown users) |
| `AUTH.LOCKED` | 423 | Too many failures (5), locked 15 minutes; message says how long |
| `AUTH.TWO_FACTOR_REQUIRED` | 401 | Show the code field, resend login with `totpCode` or `recoveryCode` |
| `AUTH.ACCOUNT_DISABLED` | 403 / 401 | Account deactivated |
| `AUTH.UNAUTHORIZED` | 401 | No or invalid token: go to login |
| `AUTH.TOKEN_EXPIRED` | 401 | Access token expired or stale: refresh once, retry |
| `AUTH.REFRESH_REUSED` | 401 | Session cut: clear cookies, go to login |
| `AUTH.FORBIDDEN` | 403 | Missing permission (or limited token on a normal endpoint) |
| `AUTH.BRANCH_FORBIDDEN` | 403 | `X-Branch-Id` not allowed for this user |
| `AUTH.WEAK_PASSWORD` | 400 | `errors.password` lists the broken rules |
| `AUTH.RESET_TOKEN_INVALID` | 400 | Reset link used or expired |
| `AUTH.OTP_*`, `AUTH.INVALID_MOBILE` | 400 / 429 | Customer sign-in (see section 5) |

## 2. Staff sign-in flow

1. `POST /auth/login` `{ identifier, password, totpCode?, recoveryCode?, deviceName? }` (identifier = email or user name). Rate limited (10/min per IP).
2. Response `{ accessToken, refreshToken, ..., requiresPasswordChange, requiresTwoFactorSetup }`.
3. **Limited token:** when `requiresPasswordChange` or `requiresTwoFactorSetup` is true the token carries `scope=limited`. It works only for `GET /auth/me`, `POST /auth/change-password`, `POST /auth/2fa/*` and logout; everything else answers 403. After `change-password` (new session returned) or `2fa/enable` the user signs in again for a full token.
4. New accounts and admin resets get a temporary password and `MustChangePassword`. **Super Admin must have two-step verification** (`Auth:RequireTwoFactorForSuperAdmin`, on by default; off only in `appsettings.Development.json`).
5. `GET /auth/me` returns the profile, the branches the user may use, role assignments, `hasAllPermissions`, `globalPermissions` and `branchPermissions` (permission codes per branch id), `activeBranchId`, and `permissionCatalogVersion`.
6. `GET /auth/can?permission=orders.order.confirm` answers whether the permission holds **in the active branch**, for hiding buttons.
7. Sessions: `GET /auth/sessions`, `DELETE /auth/sessions/{id}`, `POST /auth/logout`, `POST /auth/logout-all`.
8. Password: `POST /auth/change-password`, `POST /auth/forgot-password` (always 202), `POST /auth/reset-password`. Two-step: `POST /auth/2fa/setup` (key + `otpauth://` URI for a QR code), `/enable` (returns 10 recovery codes once), `/disable`, `/recovery-codes`.

Password rules: at least 10 characters with upper case, lower case, digit and symbol, at least 4 different characters.

## 3. Permissions

- A permission is `module.resource.action`, for example `orders.order.confirm`. The full list (about 230 codes for every SRS module) is code-defined in `Juvo.Shared/Security/PermissionCatalog.cs`, seeded to the `Permissions` table on start-up, and returned by `GET /ums/permissions` with a `version` hash.
- **Role** = named set of permissions (`RolePermission`). Default roles (SRS roles matrix): Super Admin, Manager, Order Manager, Inventory Manager, Content Editor, Support, Accountant. System roles cannot be deleted or renamed. **Super Admin always holds every permission, including ones added later**, without stored rows. The seed never undoes an administrator's edits to a system role; it only adds permissions that are new to the catalog and match the role's defaults.
- **User gets roles per branch** (`UserBranchRole`: user, branch, role). A role with no branch means "everywhere" and counts only for users with **global scope**.
- No privilege escalation: you can only grant roles whose permissions you hold yourself (in that branch), and only a Super Admin can grant Super Admin or manage a Super Admin.
- Endpoints declare their permission with `RequirePermission("ums.user.view")` (held in any of the user's assignments, for global features such as users and roles) or `RequireBranchPermission(...)` (held in the **active branch**). Endpoints that need a branch-specific check inside the handler (branches) do it per target branch. `EndpointPolicyTests` lists every endpoint and fails if one has no policy.

## 4. Branches and the active branch

- Send `X-Branch-Id: <branch id>` or `X-Branch-Id: all` on admin calls. The server **validates it on every request** against the user's assignments (`403 AUTH.BRANCH_FORBIDDEN` otherwise, and the attempt is written to the audit log with action `AccessDenied`).
- No header: a global-scope user works in "All branches"; a user with exactly one branch uses it; a user with several branches and no header has no active branch (branch-scoped permission checks answer false).
- `all` is allowed for global-scope users and for users holding `ums.branch.switch-all` (still limited to their own branches).
- Effective permission = permission **and** branch scope: a user's role in branch A is not effective in branch B. A global-scope user acts in every branch.
- **Data isolation is central:** every entity implementing `IBranchScoped` (orders, stock, expenses, ... as they are built) gets an EF global query filter and a save guard from `IBranchContext`. A scoped user cannot read another branch's rows (a direct lookup finds nothing, an API call gets 404) and cannot write into another branch (`BranchAccessDeniedException` -> 403 + audit). Global-scope users, customers, anonymous requests and background jobs are not scoped. Bypassing the filter must be explicit: `IgnoreQueryFilters([ModelDefaults.BranchScopeFilter])`.
- Branch rules: one default branch (database-enforced), the default cannot be deactivated, an inactive branch cannot be the default and gives its users no access (their tokens go stale at once), codes are unique 2-20 characters (`HO`, `DHK-01`).

## 5. Customers (mobile + OTP)

- `POST /auth/otp/request` `{ mobile }` sends a 6-digit code by SMS (sandbox: written to `NotificationLogs`, readable in Development). Valid 5 minutes, 5 wrong attempts then the code is dead, 60-second resend cooldown, 5 requests per hour per number, 5 requests per minute per IP. The response never contains the code.
- `POST /auth/otp/verify` `{ mobile, code, deviceName? }` creates the quick profile on first use and signs the customer in (`subjectType: "customer"`). Customer tokens cannot call staff endpoints.
- Numbers are stored in international form: `01712345678`, `8801712345678` and `+88 01712-345678` all become `+8801712345678`.

## 6. UMS endpoints (`/ums`)

| Area | Endpoints |
| --- | --- |
| Users | `GET/POST /users`, `GET/PUT /users/{id}`, `POST /users/{id}/activate`, `/deactivate`, `/reset-password`, `/force-logout`, `/reset-two-factor`, `GET /users/{id}/sessions`, `DELETE /users/{id}/sessions/{sessionId}`, `GET /users/{id}/sign-ins`, `GET /users/{id}/effective-permissions`, `GET/POST /users/{id}/branch-roles`, `DELETE /users/{id}/branch-roles/{assignmentId}` |
| Roles | `GET/POST /roles`, `GET/PUT/DELETE /roles/{id}`, `POST /roles/{id}/clone`, `PUT /roles/{id}/permissions`, `GET /permissions` |
| Branches | `GET/POST /branches`, `GET/PUT /branches/{id}`, `POST /branches/{id}/activate`, `/deactivate`, `/set-default`, `GET /branches/{id}/users` |
| Logs | `GET /audit-logs`, `GET /sign-in-history` |

Lists use `?page&pageSize&sort&search` plus endpoint-specific filters and return `{ items, total, page, pageSize }`. Branch-scoped admins only see users and audit rows of their own branches. A temporary password (new user, admin reset) is returned **once** and the user must change it at first sign-in.

## 7. Development seed

`Development` creates `admin@juvobd.local` (user name `admin`, password `Juvo@Dev#2026`, global scope, Super Admin) and must change the password at first sign-in. Production creates no account unless `Auth__Seed__Password` is set. The signing key (`Auth:SigningKey`) is never in the repository: in Development a random key is generated per run (tokens stop working after a restart); elsewhere it is required (32+ characters).
