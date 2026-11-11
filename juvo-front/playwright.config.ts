import { defineConfig, devices } from "@playwright/test";

/**
 * End-to-end and UI checks against the REAL stack: the compiled Next.js app (npm run build first) talking to the real ASP.NET
 * API and a throw-away SQL Server database (created from migrations, dropped afterwards). No mocks.
 *
 *   npm run build && npm run test:e2e
 *
 * Needs: .NET 10 SDK with the API built (dotnet build juvo-be), SQL Server LocalDB or Express (see scripts/setup-db.ps1), sqlcmd.
 * Ports: API 5081, front end 3100 (separate from the development ports 5080 and 3000).
 */
process.env.E2E_DB ??= `JuvoDb_E2E_${Date.now()}`;
const API_PORT = 5081;
const WEB_PORT = 3100;
const apiUrl = `http://localhost:${API_PORT}`;
const sqlServer = process.env.JUVO_TEST_SQL_SERVER ?? "(localdb)\\MSSQLLocalDB";

export const E2E = {
  db: process.env.E2E_DB,
  apiUrl,
  webUrl: `http://localhost:${WEB_PORT}`,
  sqlServer,
  adminEmail: "admin@e2e.test",
  seedPassword: "E2e-Passw0rd!x1",
  adminPassword: "E2e-Admin#Pass1",
};

const mobile = (name: keyof typeof devices) => ({ ...devices[name], defaultBrowserType: "chromium" as const });

export default defineConfig({
  testDir: "./e2e",
  timeout: 45_000,
  expect: { timeout: 10_000 },
  fullyParallel: false,
  workers: 1, // one database, and the tests share seeded users
  reporter: [["list"], ["html", { open: "never" }]],
  use: {
    baseURL: E2E.webUrl,
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
  },
  projects: [
    { name: "setup", testMatch: /setup\.ts/, teardown: "cleanup" },
    { name: "cleanup", testMatch: /cleanup\.ts/ },
    {
      name: "desktop",
      use: { ...devices["Desktop Chrome"], viewport: { width: 1280, height: 800 } },
      dependencies: ["setup"],
      testMatch: /(auth|permissions|shell|ui-checks|ums|catalog|lookups|inventory|design)\.spec\.ts/,
    },
    { name: "tablet", use: { ...devices["Desktop Chrome"], viewport: { width: 820, height: 1180 }, hasTouch: true }, dependencies: ["setup"], testMatch: /(shell|devices)\.spec\.ts/ },
    { name: "android", use: mobile("Pixel 7"), dependencies: ["setup"], testMatch: /(shell|devices)\.spec\.ts/ },
    { name: "iphone", use: mobile("iPhone 14"), dependencies: ["setup"], testMatch: /(shell|devices)\.spec\.ts/ },
  ],
  webServer: [
    {
      command: "dotnet run --no-build --no-launch-profile --project ../juvo-be/src/Juvo.Api",
      url: `${apiUrl}/health/ready`,
      timeout: 120_000,
      reuseExistingServer: false,
      stdout: "ignore",
      env: {
        ASPNETCORE_ENVIRONMENT: "Development",
        ASPNETCORE_URLS: apiUrl,
        ConnectionStrings__Default: `Server=${sqlServer};Database=${E2E.db};Trusted_Connection=True;TrustServerCertificate=True`,
        Auth__SigningKey: "e2e-signing-key-0123456789-abcdefghijklmnop",
        Auth__RequireTwoFactorForSuperAdmin: "false",
        Auth__Seed__Email: E2E.adminEmail,
        Auth__Seed__Password: E2E.seedPassword,
        Auth__Otp__ResendCooldownSeconds: "0",
        Notifications__StoreOtpPlaintext: "true",
        RateLimiting__Enabled: "false", // every browser reaches the API through the BFF's single address in tests
        DataProtection__KeysPath: `${process.env.TEMP ?? "/tmp"}/juvo-e2e-keys-${E2E.db}`,
        Storage__RootPath: `${process.env.TEMP ?? "/tmp"}/juvo-e2e-files-${E2E.db}`,
      },
    },
    {
      command: `npm run start -- -p ${WEB_PORT}`,
      url: E2E.webUrl + "/login",
      timeout: 120_000,
      reuseExistingServer: false,
      stdout: "ignore",
      env: { API_BASE_URL: apiUrl, ENABLE_DESIGN_PAGE: "true", COOKIE_SECURE: "false", NEXT_TELEMETRY_DISABLED: "1" },
    },
  ],
});
