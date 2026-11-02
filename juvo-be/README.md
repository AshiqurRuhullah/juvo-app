# juvo-be: Juvo BD backend (ASP.NET Core, .NET 10)

Modular monolith, Clean Architecture. Microsoft SQL Server (local, no Docker). Business rules: `../docs/source/brd-srs.md`.

## Layout

| Project | Role |
| --- | --- |
| `src/Juvo.Api` | Host: endpoints, middleware, ProblemDetails, health, API versioning, OpenAPI, CORS, rate limiting |
| `src/Juvo.Application` | Use cases: dispatcher, pipeline behaviours (logging, permission, validation, transaction), abstractions |
| `src/Juvo.Domain` | Entities, base types, domain events, business rules (no framework dependencies) |
| `src/Juvo.Infrastructure` | EF Core (SQL Server), interceptors (audit, outbox), outbox worker, file storage, migrations |
| `src/Juvo.Shared` | `Result`, error codes, pagination contract |
| `tests/*` | Domain, Application and API integration tests (real SQL Server, no Docker) |

Rules: Domain depends only on Shared; Application on Domain + Shared; Infrastructure on Application; Api composes everything.

## Run it

Prerequisites: .NET 10 SDK, a local SQL Server (Developer, Express or LocalDB).

```powershell
.\scripts\setup-db.ps1                      # from the repo root: creates JuvoDb, prints the connection string
dotnet run --project juvo-be/src/Juvo.Api   # Development: applies migrations automatically
```

| URL (Development) | What |
| --- | --- |
| `https://localhost:7080/scalar/v1` | Interactive API documentation |
| `https://localhost:7080/openapi/v1.json` | OpenAPI document |
| `https://localhost:7080/health/live` | Liveness (process is up) |
| `https://localhost:7080/health/ready` | Readiness (database reachable) |
| `https://localhost:7080/api/v1/system/ping?name=Juvo` | Smoke test through the full pipeline |
| `http://localhost:5080` | Same API over HTTP |

Ports come from `src/Juvo.Api/Properties/launchSettings.json`. The ASP.NET dev certificate may show as untrusted: run `dotnet dev-certs https --trust` once.

## Sign in (Development)

On first start in Development the database is migrated and seeded (permission catalog, 7 default roles, branches HO and ONL, and a Super Admin):

| | |
| --- | --- |
| Email / user name | `admin@juvobd.local` / `admin` |
| Password | `Juvo@Dev#2026` (development only; you must change it at first sign-in) |
| Try it | open `/scalar/v1`, call `POST /api/v1/auth/login`, then use the token as a Bearer token |

How tokens, permissions and branches work: `docs/auth-and-access.md` in the repository root. Access tokens last 15 minutes; refresh tokens rotate. In Development the signing key is generated per run, so tokens stop working after a restart; set a fixed one with `dotnet user-secrets set "Auth:SigningKey" "<32+ characters>" --project juvo-be/src/Juvo.Api`.

## Configuration

Order of precedence: `appsettings.json` < `appsettings.{Environment}.json` < user-secrets (Development) < environment variables. Environment variables use `__` for nesting, for example `ConnectionStrings__Default`. See `.env.example` for every key.

Development default connection string (Windows authentication, no password, in `appsettings.Development.json`): `Server=(localdb)\MSSQLLocalDB;Database=JuvoDb;...`. Override it with user-secrets for another server:

```powershell
dotnet user-secrets set "ConnectionStrings:Default" "Server=.;Database=JuvoDb;Trusted_Connection=True;TrustServerCertificate=True" --project juvo-be/src/Juvo.Api
```

Options are validated at start-up (`ValidateOnStart`); a missing connection string or a wildcard CORS origin stops the app with a clear message.

## Database and migrations

```powershell
cd juvo-be
dotnet tool restore                                   # installs dotnet-ef 10.0.14 (local tool)
dotnet ef migrations add <Name> -p src/Juvo.Infrastructure -s src/Juvo.Api -o Persistence/Migrations
dotnet ef migrations has-pending-model-changes -p src/Juvo.Infrastructure -s src/Juvo.Api
dotnet run --project src/Juvo.Api -- --migrate        # Production: apply migrations and exit
```

Development applies pending migrations at start-up (`Database:AutoMigrate`). Production never does: run the `--migrate` command during deployment (with the production connection string in the environment). One migration per commit, committed with the code that needs it.

## Tests

```powershell
dotnet test                                     # all projects
dotnet test --project tests/Juvo.Api.IntegrationTests
```

Integration tests create a throw-away database `JuvoDb_Test_<guid>` from the migrations and drop it afterwards (only names with that prefix can be dropped). Server: environment variable `JUVO_TEST_SQL_SERVER`, default `(localdb)\MSSQLLocalDB`. Test framework is xunit v3 with plain asserts (FluentAssertions is avoided because of its licence change). `dotnet test` needs the `global.json` runner setting already present.

## Conventions

- Expected failures return `Result` / `Result<T>` with a stable code (`AREA.REASON`); exceptions are for unexpected faults. Errors reach the client as RFC 9457 problem details with `code`, `correlationId`, `traceId`.
- Every list endpoint uses `PageRequest` (`page`, `pageSize` max 100, `sort`, `filter`, `search`) and returns `PagedResult` (`items`, `total`, `page`, `pageSize`).
- Every request has a correlation id (`X-Correlation-Id`) in logs, audit rows and error responses.
- Entities derive from `Entity` (v7 GUID, created/updated stamps, `rowversion`). Archivable ones derive from `SoftDeletableEntity`; append-only tables implement `IAppendOnly`; sensitive properties get `[AuditIgnore]`.
- Domain events raised by an `AggregateRoot` are saved to the outbox in the same transaction and delivered by the worker (`IOutboxMessageHandler`, at-least-once, handlers must be idempotent). Failed deliveries retry with exponential backoff and are visible in `JobLogs`.
- Commands run in a transaction (`ICommand`); queries do not. A request implementing `IRequirePermission` is denied unless the permission checker allows it (default: deny all until the Identity module in Step 3).
- Uploads go through `IFileStorage` (local disk under `Storage:RootPath`, served at `/files` with long cache headers).
- Behind a reverse proxy, configure forwarded headers and the known proxy before relying on per-IP rate limits (Step 14).
