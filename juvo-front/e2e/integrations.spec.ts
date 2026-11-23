import { test, expect, type APIRequestContext, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { API, loadState } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { signInViaBff } from "./support/ui";
import { cleanByTag, cleanIntegrations, looksLikeApiKey, newTag, seedIntegrations, type IntegrationSeed } from "./support/integrations";

/**
 * API keys and webhooks in the browser against the real API (FR-AF05). The key flow is proven end to end: a new key is shown once in
 * the one-time dialog, then used against the public API (Playwright's request context talks to the e2e API directly), rotated (the old
 * key is refused), switched off (KEY_DISABLED) and deleted. Webhook addresses: the e2e API runs the production address guard, so the
 * specs only test the refusals; endpoints and delivery rows for the log, the toggles and the redelivery are inserted into the throw-away
 * database (support/integrations.ts). Permission cases close the file.
 */
let seed: IntegrationSeed;
const runTag = newTag();

test.beforeAll(() => {
  seed = seedIntegrations();
});
test.afterAll(() => {
  cleanIntegrations(seed);
  cleanByTag(runTag);
});

async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

const toast = (page: Page, text: string | RegExp) => expect(page.getByText(text).first()).toBeVisible();
const keyRow = (page: Page, name: string) => page.locator("tr", { has: page.getByTestId("key-name").filter({ hasText: name }) });
const hookRow = (page: Page, name: string) => page.locator("tr", { has: page.getByTestId("hook-name").filter({ hasText: name }) });

async function openKeys(page: Page) {
  await page.goto("/admin/integrations/api-keys");
  await expect(page.getByRole("heading", { level: 1, name: "API keys" })).toBeVisible();
}
async function openHooks(page: Page) {
  await page.goto("/admin/integrations/webhooks");
  await expect(page.getByRole("heading", { level: 1, name: "Webhooks" })).toBeVisible();
}
async function searchKeys(page: Page, text: string) {
  await page.getByLabel("Search API keys").fill(text);
}

async function publicGet(request: APIRequestContext, path: string, key: string) {
  const response = await request.get(`${API}/api/public/v1${path}`, { headers: { "X-Api-Key": key } });
  const text = await response.text();
  let code: string | undefined;
  try {
    code = (JSON.parse(text) as { code?: string }).code;
  } catch {
    code = undefined;
  }
  return { status: response.status(), code };
}

test.describe("API keys", () => {
  test("a key is created, shown once, used, rotated, turned off and deleted", async ({ page, context, request }) => {
    test.setTimeout(180_000);
    await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: E2E.webUrl }).catch(() => undefined);
    const name = `E2E sync ${runTag}`;
    await adminSignIn(page);
    await openKeys(page);

    await test.step("create with only products:read", async () => {
      await page.getByTestId("key-new").click();
      await page.locator("#key-name").fill(name);
      await page.getByTestId("scope-products:read").click();
      await page.getByTestId("key-save").click();
    });

    let secret = "";
    await test.step("the secret is shown once and the dialog resists accidental dismissal", async () => {
      const dialog = page.getByTestId("secret-dialog");
      await expect(dialog).toBeVisible();
      await expect(dialog.getByTestId("secret-notice")).toContainText("You will not see this again");
      secret = await dialog.getByTestId("secret-value").inputValue();
      expect(looksLikeApiKey(secret), "a full key was shown").toBe(true);
      await page.mouse.click(5, 5); // outside
      await expect(dialog).toBeVisible();
      await page.keyboard.press("Escape");
      await expect(dialog).toBeVisible();
      await dialog.getByTestId("secret-copy").click();
      await expect(dialog.getByTestId("secret-copy")).toHaveAttribute("data-state", /copied|failed/);
    });

    await test.step("the key works, only for its scope", async () => {
      expect((await publicGet(request, "/products?pageSize=1", secret)).status).toBe(200);
      const orders = await publicGet(request, "/orders?pageSize=1", secret);
      expect(orders.status).toBe(403);
      expect(orders.code).toBe("PUBLICAPI.SCOPE_MISSING");
      expect((await publicGet(request, "/products?pageSize=1", secret.slice(0, -1) + (secret.endsWith("a") ? "b" : "a"))).status).toBe(401);
    });

    await test.step("closing returns focus and the secret is gone from the page, the address and the browser storage", async () => {
      await page.getByTestId("secret-done").click();
      await expect(page.getByTestId("secret-dialog")).toHaveCount(0);
      await expect(page.getByTestId("key-new")).toBeFocused();
      await searchKeys(page, runTag);
      await expect(keyRow(page, name)).toBeVisible();
      await expect(keyRow(page, name).getByTestId("key-prefix")).toContainText(secret.slice(0, 16));
      const gone = async () => {
        await expect(page.locator("body")).not.toContainText(secret);
        expect(page.url()).not.toContain(secret);
        const stored = await page.evaluate(() => JSON.stringify([Object.entries(localStorage), Object.entries(sessionStorage)]));
        expect(stored).not.toContain(secret);
      };
      await gone();
      await page.reload();
      await searchKeys(page, runTag);
      await expect(keyRow(page, name)).toBeVisible();
      await gone();
      expect(await page.content()).not.toContain(secret);
    });

    await test.step("rotate: confirm, new secret, old key refused at once", async () => {
      await keyRow(page, name).getByTestId("key-rotate").click();
      const confirm = page.getByRole("dialog");
      await expect(confirm).toContainText("stops working at once");
      await confirm.getByRole("button", { name: "Rotate key" }).click();
      const dialog = page.getByTestId("secret-dialog");
      await expect(dialog).toBeVisible();
      const next = await dialog.getByTestId("secret-value").inputValue();
      expect(looksLikeApiKey(next)).toBe(true);
      expect(next).not.toBe(secret);
      await dialog.getByTestId("secret-done").click();
      expect((await publicGet(request, "/products?pageSize=1", secret)).status).toBe(401);
      expect((await publicGet(request, "/products?pageSize=1", next)).status).toBe(200);
      secret = next;
    });

    await test.step("turn off: the proven key answers KEY_DISABLED; turn on again", async () => {
      await searchKeys(page, runTag);
      await keyRow(page, name).getByTestId("key-toggle").click();
      await toast(page, /is off/);
      await expect(keyRow(page, name).getByTestId("key-state")).toHaveAttribute("data-state", "Disabled");
      const off = await publicGet(request, "/products?pageSize=1", secret);
      expect(off.status).toBe(401);
      expect(off.code).toBe("PUBLICAPI.KEY_DISABLED");
      await keyRow(page, name).getByTestId("key-toggle").click();
      await expect(keyRow(page, name).getByTestId("key-state")).toHaveAttribute("data-state", "Enabled");
      expect((await publicGet(request, "/products?pageSize=1", secret)).status).toBe(200);
    });

    await test.step("delete: confirm, row gone, key refused", async () => {
      await keyRow(page, name).getByTestId("key-delete").click();
      await expect(page.getByRole("dialog")).toContainText("cannot be brought back");
      await page.getByRole("dialog").getByRole("button", { name: "Delete key" }).click();
      await toast(page, /was deleted/);
      await expect(keyRow(page, name)).toHaveCount(0);
      expect((await publicGet(request, "/products?pageSize=1", secret)).status).toBe(401);
    });
  });

  test("the form checks its fields, warns about personal data and keeps every option", async ({ page }) => {
    test.setTimeout(120_000);
    const name = `E2E full ${runTag}`;
    const ho = loadState().branches.ho;
    await adminSignIn(page);
    await openKeys(page);
    await page.getByTestId("key-new").click();
    await page.getByTestId("key-save").click();
    await expect(page.getByText("Give the key a name of 2 to 80 characters.")).toBeVisible();
    await expect(page.getByText("Choose at least one access right.")).toBeVisible();

    await expect(page.getByTestId("pii-warning")).toHaveCount(0);
    await page.getByTestId("scope-pii:read").click();
    await expect(page.getByTestId("pii-warning")).toContainText("customer names, mobile numbers");
    await page.getByTestId("scope-orders:read").click();

    await page.locator("#key-name").fill(name);
    await page.locator("#key-expires").fill("2020-01-01");
    await page.locator("#key-rate").fill("0");
    await page.getByTestId("key-save").click();
    await expect(page.getByText("The expiry must be in the future.")).toBeVisible();
    await expect(page.getByText(/Use a whole number from 1 to 10,000/)).toBeVisible();

    await page.locator("#key-expires").fill("2099-12-31");
    await page.locator("#key-rate").fill("120");
    await page.locator("#key-branch").click();
    await page.getByRole("option", { name: new RegExp(ho.code) }).click();
    await page.getByTestId("key-save").click();
    const dialog = page.getByTestId("secret-dialog");
    await expect(dialog).toBeVisible();
    await dialog.getByTestId("secret-done").click();

    await searchKeys(page, runTag);
    const row = keyRow(page, name);
    await expect(row).toBeVisible();
    await expect(row.getByTestId("key-scopes")).toContainText("pii:read");
    await expect(row.getByTestId("key-scopes")).toContainText("orders:read");
    await expect(row.getByTestId("key-branch")).toHaveText(ho.code);
    await expect(row).toContainText("Until 2099-12-31");
    await expect(row).toContainText("120 per minute");
    await expect(row.getByTestId("key-last-used")).toHaveText("Never used");

    await row.getByTestId("key-edit").click();
    await expect(page.getByTestId("key-form")).toBeVisible();
    await expect(page.locator("#key-rate")).toHaveValue("120");
    await expect(page.locator("#key-expires")).toHaveValue("2099-12-31");
    await page.getByTestId("scope-pii:read").click();
    await page.getByTestId("key-save").click();
    await toast(page, /was saved/);
    await expect(page.getByTestId("secret-dialog")).toHaveCount(0); // editing never shows a secret
    await expect(keyRow(page, name).getByTestId("key-scopes")).not.toContainText("pii:read");

    await keyRow(page, name).getByTestId("key-delete").click();
    await page.getByRole("dialog").getByRole("button", { name: "Delete key" }).click();
    await expect(keyRow(page, name)).toHaveCount(0);
  });

  test("a search with no match says so; the how-to-use block shows placeholders only", async ({ page }) => {
    await adminSignIn(page);
    await openKeys(page);
    await searchKeys(page, `nothing-${runTag}-here`);
    await expect(page.getByText("Try a different search or remove a filter.").first()).toBeVisible();
    await expect(page.getByTestId("usage-curl")).toContainText("YOUR_API_KEY");
    await expect(page.getByTestId("usage-base")).toContainText("/api/public/v1");
    await expect(page.getByTestId("usage-spec")).toContainText("/openapi/public-v1.json");
    await page.getByTestId("usage-curl-copy").click();
    await expect(page.getByTestId("usage-curl-copy")).toHaveAttribute("data-state", /copied|failed/);
  });
});

test.describe("Webhooks", () => {
  test("a new endpoint is checked in the browser and then by the API (private and http addresses are refused)", async ({ page }) => {
    test.setTimeout(120_000);
    const name = `E2E refused ${runTag}`;
    await adminSignIn(page);
    await openHooks(page);
    await page.getByTestId("hook-new").click();
    await page.getByTestId("hook-save").click();
    await expect(page.getByText("Give the webhook a name of 2 to 80 characters.")).toBeVisible();
    await expect(page.getByText("Enter the https address that should receive the events.")).toBeVisible();
    await expect(page.getByText("Choose at least one event.")).toBeVisible();

    await page.locator("#hook-name").fill(name);
    await page.getByTestId("event-order.placed").click();
    await page.locator("#hook-url").fill("http://example.com/hook");
    await page.getByTestId("hook-save").click();
    await expect(page.getByText("Only https addresses are allowed.")).toBeVisible();
    await page.locator("#hook-url").fill("https://user:secret@example.com/hook");
    await page.getByTestId("hook-save").click();
    await expect(page.getByText("Remove the user name and password from the address.")).toBeVisible();

    // Loopback and private addresses pass the browser check; only the API can refuse them, and its words are shown beside the field.
    for (const url of ["https://localhost/hook", "https://127.0.0.1/hook", "https://169.254.169.254/latest"]) {
      await page.locator("#hook-url").fill(url);
      await page.getByTestId("hook-save").click();
      const refusal = page.locator("#hook-url-error");
      await expect(refusal, url).toBeVisible();
      expect((await refusal.innerText()).trim().length).toBeGreaterThan(10);
      await expect(page.getByTestId("secret-dialog")).toHaveCount(0);
    }
    await page.getByRole("dialog").getByRole("button", { name: "Cancel" }).click();
    await page.getByLabel("Search webhooks").fill(name);
    await expect(page.getByText("Try a different search or remove a filter.").first()).toBeVisible();
  });

  test("an endpoint is turned off and on, and a test event shows the answer gracefully", async ({ page }) => {
    test.setTimeout(120_000);
    await adminSignIn(page);
    await openHooks(page);
    await page.getByLabel("Search webhooks").fill(seed.hookToggle.name);
    const row = hookRow(page, seed.hookToggle.name);
    await expect(row).toBeVisible();
    await expect(row.getByTestId("hook-events")).toContainText("stock.low");
    await expect(row.getByTestId("hook-state")).toHaveAttribute("data-enabled", "true");
    await row.getByTestId("hook-toggle").click();
    await toast(page, /is off/);
    await expect(row.getByTestId("hook-state")).toHaveAttribute("data-enabled", "false");
    await row.getByTestId("hook-toggle").click();
    await expect(row.getByTestId("hook-state")).toHaveAttribute("data-enabled", "true");

    // The test ping goes through the real sender and the production address guard: an address that cannot be reached is a normal answer.
    await expect(row.getByTestId("hook-no-delivery")).toBeVisible();
    await row.getByTestId("hook-test").click();
    const result = page.getByTestId("test-result");
    await expect(result).toBeVisible();
    await expect(result).toHaveAttribute("data-status", "Failed");
    expect((await result.getByTestId("test-error").innerText()).trim().length).toBeGreaterThan(5);
    await expect(row.getByTestId("hook-last-delivery")).toContainText("Failed");
  });

  test("a new signing secret is confirmed first and an endpoint is deleted after a confirm", async ({ page }) => {
    await adminSignIn(page);
    await openHooks(page);
    await page.getByLabel("Search webhooks").fill(seed.hookDelete.name);
    const row = hookRow(page, seed.hookDelete.name);
    await expect(row).toBeVisible();
    await row.getByTestId("hook-rotate").click();
    await expect(page.getByRole("dialog")).toContainText("must be updated with the new secret");
    await page.getByRole("dialog").getByRole("button", { name: "Cancel" }).click();
    await expect(page.getByTestId("secret-dialog")).toHaveCount(0);
    await row.getByTestId("hook-delete").click();
    await expect(page.getByRole("dialog")).toContainText("delivery log is deleted");
    await page.getByRole("dialog").getByRole("button", { name: "Delete webhook" }).click();
    await toast(page, /were deleted/);
    await expect(hookRow(page, seed.hookDelete.name)).toHaveCount(0);
  });

  test("the verification help shows the header scheme with a placeholder secret", async ({ page }) => {
    await adminSignIn(page);
    await openHooks(page);
    const help = page.getByTestId("verify-help");
    await expect(help).toContainText("Juvo-Signature");
    await expect(page.getByTestId("verify-sample")).toContainText("YOUR_WEBHOOK_SECRET");
    await expect(page.getByTestId("verify-sample")).toContainText("timingSafeEqual");
    await expect(page.getByTestId("verify-sample")).toContainText("300");
  });

  test("the delivery log filters by endpoint, status and event and redelivers failed deliveries", async ({ page }) => {
    test.setTimeout(150_000);
    await adminSignIn(page);
    await openHooks(page);
    await page.getByRole("combobox", { name: /^Webhook:/ }).click();
    await page.getByRole("option", { name: seed.hookLog.name }).click();
    const events = page.getByTestId("delivery-event").filter({ visible: true });
    await expect(events).toHaveCount(5);

    await page.getByRole("combobox", { name: /^Status:/ }).click();
    await page.getByRole("option", { name: "Failed" }).click();
    await expect(events).toHaveCount(3);
    await expect(page.getByTestId("delivery-http").filter({ visible: true }).filter({ hasText: "503" })).toHaveCount(1);
    await expect(page.getByTestId("delivery-error").filter({ visible: true }).filter({ hasText: "HTTP 503" })).toHaveCount(1);

    await page.getByRole("combobox", { name: /^Status:/ }).click();
    await page.getByRole("option", { name: "Any status" }).click();
    await page.getByRole("combobox", { name: /^Event:/ }).click();
    await page.getByRole("option", { name: "product.updated" }).click();
    await expect(events).toHaveCount(2);
    await page.getByRole("combobox", { name: /^Event:/ }).click();
    await page.getByRole("option", { name: "Any event" }).click();
    await page.getByRole("combobox", { name: /^Status:/ }).click();
    await page.getByRole("option", { name: "Failed" }).click();
    await expect(events).toHaveCount(3);

    // One delivery again: the confirm says what happens, then the API's answer is shown.
    await page.getByTestId("delivery-redeliver").filter({ visible: true }).first().click();
    await expect(page.getByRole("dialog")).toContainText("same event id and body");
    await page.getByRole("dialog").getByRole("button", { name: "Send again" }).click();
    const result = page.getByTestId("redeliver-result");
    await expect(result).toBeVisible();
    await expect(result).toContainText("queued again");

    // Several failed deliveries again.
    await page.reload();
    await page.getByRole("combobox", { name: /^Webhook:/ }).click();
    await page.getByRole("option", { name: seed.hookLog.name }).click();
    await page.getByRole("combobox", { name: /^Status:/ }).click();
    await page.getByRole("option", { name: "Failed" }).click();
    await page.getByTestId("delivery-select-failed").click();
    await expect(page.getByTestId("delivery-selected")).toContainText("selected");
    await page.getByTestId("delivery-redeliver-selected").click();
    await expect(page.getByRole("dialog")).toContainText("queued again");
    await page.getByRole("dialog").getByRole("button", { name: "Send again" }).click();
    await expect(page.getByTestId("redeliver-result")).toContainText("queued again");
  });
});

test.describe("Permissions and menu", () => {
  test("Super Admin sees both menu items", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin");
    await expect(page.locator('a[href="/admin/integrations/api-keys"]').first()).toBeAttached();
    await expect(page.locator('a[href="/admin/integrations/webhooks"]').first()).toBeAttached();
  });

  test("a Manager sees neither menu item and the pages are closed", async ({ page }) => {
    const manager = loadState().manager;
    await signInViaBff(page.context(), manager.email, manager.password);
    await page.goto("/admin");
    await expect(page.locator('a[href="/admin/integrations/api-keys"]')).toHaveCount(0);
    await expect(page.locator('a[href="/admin/integrations/webhooks"]')).toHaveCount(0);
    await page.goto("/admin/integrations/api-keys");
    await expect(page).toHaveURL(/forbidden/);
    await page.goto("/admin/integrations/webhooks");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("view-only staff see the lists and the log but no buttons to change anything", async ({ page }) => {
    test.setTimeout(120_000);
    const staff = await staffWithPermissions(["integrations.apikey.view", "integrations.webhook.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await openKeys(page);
    await expect(page.getByTestId("keys-read-only")).toBeVisible();
    await expect(page.getByTestId("key-new")).toHaveCount(0);
    await expect(page.getByTestId("key-edit")).toHaveCount(0);
    await expect(page.getByTestId("key-rotate")).toHaveCount(0);
    await expect(page.getByTestId("key-delete")).toHaveCount(0);

    await openHooks(page);
    await expect(page.getByTestId("hooks-read-only")).toBeVisible();
    await page.getByLabel("Search webhooks").fill(seed.hookLog.name);
    await expect(hookRow(page, seed.hookLog.name)).toBeVisible();
    await expect(page.getByTestId("hook-new")).toHaveCount(0);
    await expect(page.getByTestId("hook-test")).toHaveCount(0);
    await expect(page.getByTestId("hook-toggle")).toHaveCount(0);
    await expect(page.getByTestId("hook-delete")).toHaveCount(0);
    await hookRow(page, seed.hookLog.name).getByTestId("hook-log").click();
    await expect(page.getByTestId("delivery-event").filter({ visible: true }).first()).toBeVisible();
    await expect(page.getByTestId("delivery-redeliver")).toHaveCount(0);
    await expect(page.getByTestId("delivery-select")).toHaveCount(0);
    await expect(page.getByTestId("delivery-bulk")).toHaveCount(0);
  });

  test("a role with only the key permission sees only the keys item", async ({ page }) => {
    const staff = await staffWithPermissions(["integrations.apikey.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin");
    await expect(page.locator('a[href="/admin/integrations/api-keys"]').first()).toBeAttached();
    await expect(page.locator('a[href="/admin/integrations/webhooks"]')).toHaveCount(0);
    await page.goto("/admin/integrations/webhooks");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("a role with the manage permission can create but the API still decides: a key without scopes is refused in the form", async ({ page }) => {
    const staff = await staffWithPermissions(["integrations.apikey.view", "integrations.apikey.manage"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await openKeys(page);
    await expect(page.getByTestId("key-new")).toBeVisible();
    await expect(page.getByTestId("keys-read-only")).toHaveCount(0);
    await page.getByTestId("key-new").click();
    await page.locator("#key-name").fill(`E2E nope ${runTag}`);
    await page.getByTestId("key-save").click();
    await expect(page.getByText("Choose at least one access right.")).toBeVisible();
  });
});
