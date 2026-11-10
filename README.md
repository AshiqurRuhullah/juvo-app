# Juvo BD

E-commerce platform for Juvo BD (fashion retail, Bangladesh and international): an ASP.NET Core 10 API (`juvo-be`), a Next.js 16 web app with the storefront, customer account and admin panel (`juvo-front`), and the business documents (`Documents/`, `docs/`).

## Run everything with one command (Windows, no Docker)

You need once: the **.NET 10 SDK**, **Node.js LTS** and **SQL Server Express or LocalDB** with `sqlcmd`
(`winget install Microsoft.DotNet.SDK.10`, `winget install OpenJS.NodeJS.LTS`, `winget install Microsoft.SQLServer.2022.Express`, `winget install Microsoft.Sqlcmd`).

```powershell
.\scripts\start-all.ps1          # checks the tools, creates the database, installs packages, starts the API and the web app
.\scripts\start-all.ps1 -Open    # the same, and opens the login page
.\scripts\start-all.ps1 -Status  # what is running
.\scripts\start-all.ps1 -Stop    # stop both
```

| What | Where |
| --- | --- |
| Web app (shop, account, admin) | http://localhost:3000 (admin sign-in: http://localhost:3000/login) |
| API and interactive docs | http://localhost:5080/scalar/v1 |
| Admin account (development only) | `admin@juvobd.local` / `Juvo@Dev#2026`, the password must be changed at first sign-in |
| Demo data | 16 sample products with generated pictures and stock (tag `sample`) |
| Logs | `.run\api.log`, `.run\web.log` |

The script creates `juvo-front\.env.local` from `.env.example` (no secrets). The API uses the Development settings in `juvo-be\src\Juvo.Api\appsettings.Development.json` (LocalDB, sample catalog on). Production settings and secrets come from environment variables, never from files in the repository: see `juvo-be\.env.example`.

## Where to read next

- `PROGRESS.md`: status of the 14 build steps and what is next. `CLAUDE.md`: project memory for working on this code.
- `docs/understanding.md` (modules, workflows, requirement traceability), `docs/assumptions.md` (every decision), `docs/auth-and-access.md` (API access contract).
- `juvo-be/README.md` and `juvo-front/README.md` for each project (tests, migrations, scripts).
