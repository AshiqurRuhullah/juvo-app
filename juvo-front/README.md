# juvo-front

Web front end of Juvo BD: the public storefront, the customer account and the staff admin. Next.js 16 (App Router, TypeScript strict, Tailwind 4). It talks to `juvo-be` only from the server (a backend-for-frontend), so access and refresh tokens never reach JavaScript in the browser.

Read `AGENTS.md` first (Next.js 16 differs from older versions: `proxy.ts` replaces `middleware.ts`, and request APIs are async).

## Run

```powershell
# 1. database and API (from the repository root)
.\scripts\setup-db.ps1
dotnet run --project juvo-be/src/Juvo.Api            # http://localhost:5080

# 2. this app
cd juvo-front
copy .env.example .env.local
npm install
npm run dev                                          # http://localhost:3000
```

Sign in at `/login` with the development account `admin@juvobd.local` / `Juvo@Dev#2026` (you must change the password at first sign-in). Customers sign in at `/login?as=customer` with a mobile number and OTP; in development the OTP is readable in the API's `NotificationLogs` table.

## Scripts

| Command | What it does |
| --- | --- |
| `npm run dev` / `build` / `start` | Next.js development server, production build, production server |
| `npm run lint` / `typecheck` | ESLint (Next + TypeScript rules), `tsc --noEmit` |
| `npm run gen:api` | Downloads `/openapi/v1.json` from the running API and regenerates `src/lib/api/schema.d.ts` (`OPENAPI_FILE=openapi/v1.json` uses the saved snapshot) |
| `npm test` | Unit tests (vitest) in `tests/` |
| `npm run test:e2e` | Playwright against a real API and a throw-away database (see below) |
| `npm run test:e2e:ui-checks` | Screenshots at 360/768/1280 px, no sideways scroll, 44 px tap targets, axe accessibility, no console errors |

`npm run test:e2e` starts its own API (port 5081, database `JuvoDb_E2E_<timestamp>`, dropped afterwards) and its own production build of this app (port 3100), so it never touches development data. Needs SQL Server or LocalDB, the same as the API tests. Projects: desktop 1280, tablet 820, Android (Pixel 7), iPhone 14.

## Layout

```
src/app/            routes: (auth) login pages, (admin)/admin, (account)/account, (storefront), api/bff, design (dev gallery)
src/components/ui/  design system (Radix primitives + Tailwind tokens)
src/components/     auth forms, shell (sidebar, top bar, branch switcher, command palette), providers
src/lib/auth/       permission checks that mirror the API, cookie names and JWT timing helpers
src/lib/server/     server-only: backend fetch, single-flight refresh, session loading, cookie writing
src/lib/api/        generated types (schema.d.ts), openapi-fetch client, problem+json handling
src/modules/        one folder per business module: ums, catalog (products, categories, import/export), inventory
src/messages/       en.json and bn.json (shell, auth, UMS) plus one file per module (catalog.*, lookups.*, inventory.*), merged by load.ts (next-intl)
src/proxy.ts        keeps the session fresh before protected pages render
e2e/ tests/         Playwright and vitest
docs/               frontend.md (design and decisions), brand-tokens.md, ui-checks/ (screenshots)
```

See `docs/frontend.md` for the session design, the route map and which permission shows which menu item.
