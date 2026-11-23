import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { E2E } from "../playwright.config";
import { api, loadState } from "./support/env";
import { adminAccess, staffWithPermissions } from "./support/returns";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";
import { cleanByTag, cleanIntegrations, newTag, seedIntegrations, type IntegrationSeed } from "./support/integrations";

/**
 * API keys, webhooks, the one-time secret dialog and the delivery log on a phone, a tablet and a desktop: no sideways scroll, 44 px
 * targets, axe (WCAG 2.1 AA) and no console errors; plus a view-only staff member, the dark theme and Bengali. Screenshots go to
 * docs/ui-checks/integrations/. The secret dialog is checked but never photographed (it shows a throw-away key).
 */
const WIDTHS = [360, 768, 1280] as const;
const runTag = newTag();
let seed: IntegrationSeed;

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "integrations"), { recursive: true });
  seed = seedIntegrations();
  const token = await adminAccess();
  for (const [name, scopes] of [
    [`E2E ui keys ${runTag} A`, ["orders:read", "products:read"]],
    [`E2E ui keys ${runTag} B`, ["stock:read", "pii:read"]],
  ] as const) {
    const created = await api("POST", "/api/v1/integrations/api-keys", { name, scopes, branchId: null, expiresAt: null, rateLimitPerMinute: null }, token);
    expect(created.status).toBe(201);
  }
});
test.afterAll(() => {
  cleanIntegrations(seed);
  cleanByTag(runTag);
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `integrations/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409|400)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

const openKeys = async (page: Page) => {
  await page.goto("/admin/integrations/api-keys");
  await page.getByLabel(/^Search API keys|^এপিআই কী খুঁজুন/).fill(runTag);
  await expect(page.getByTestId("key-name").filter({ visible: true }).first()).toBeVisible();
};
const openHooks = async (page: Page) => {
  await page.goto("/admin/integrations/webhooks");
  await page.getByLabel(/^Search webhooks|^ওয়েবহুক খুঁজুন/).fill(seed.tag);
  await expect(page.getByTestId("hook-name").filter({ visible: true }).first()).toBeVisible();
};
const openLog = async (page: Page, status = "Failed") => {
  await openHooks(page);
  await page.getByRole("combobox", { name: /^(Webhook|ওয়েবহুক):/ }).click();
  await page.getByRole("option", { name: seed.hookLog.name }).click();
  await page.getByRole("combobox", { name: /^(Status|অবস্থা):/ }).click();
  await page.getByRole("option", { name: status }).click();
  await expect(page.getByTestId("delivery-event").filter({ visible: true }).first()).toBeVisible();
};

for (const width of WIDTHS) {
  for (const [label, run] of [
    ["key list", async (page: Page, errors: string[]) => {
      await openKeys(page);
      await checks(page, width, errors, `keys-${width}`);
    }],
    ["key form with the personal data warning", async (page: Page, errors: string[]) => {
      await page.goto("/admin/integrations/api-keys");
      await page.getByTestId("key-new").click();
      await page.getByTestId("scope-pii:read").click();
      await expect(page.getByTestId("pii-warning")).toBeVisible();
      await checks(page, width, errors, `key-form-${width}`);
    }],
    ["the one-time secret dialog", async (page: Page, errors: string[]) => {
      await page.goto("/admin/integrations/api-keys");
      await page.getByTestId("key-new").click();
      await page.locator("#key-name").fill(`E2E ui secret ${runTag}`);
      await page.getByTestId("scope-stock:read").click();
      await page.getByTestId("key-save").click();
      await expect(page.getByTestId("secret-dialog")).toBeVisible();
      await checks(page, width, errors); // no screenshot: the dialog shows a (throw-away) key
    }],
    ["key rotate confirm", async (page: Page, errors: string[]) => {
      await openKeys(page);
      await page.getByTestId("key-rotate").filter({ visible: true }).first().click();
      await expect(page.getByRole("dialog")).toContainText("stops working at once");
      await checks(page, width, errors, `key-rotate-${width}`);
    }],
    ["webhook list", async (page: Page, errors: string[]) => {
      await openHooks(page);
      await checks(page, width, errors, `hooks-${width}`);
    }],
    ["webhook form", async (page: Page, errors: string[]) => {
      await page.goto("/admin/integrations/webhooks");
      await page.getByTestId("hook-new").click();
      await page.locator("#hook-url").fill("https://localhost/hook");
      await page.locator("#hook-name").fill("Receiver");
      await page.getByTestId("event-order.placed").click();
      await page.getByTestId("hook-save").click();
      await expect(page.locator("#hook-url-error")).toBeVisible();
      await checks(page, width, errors, `hook-form-${width}`);
    }],
    ["delivery log with a selection and the confirm dialog", async (page: Page, errors: string[]) => {
      await openLog(page);
      await page.getByTestId("delivery-select-failed").click();
      await expect(page.getByTestId("delivery-selected")).toBeVisible();
      await checks(page, width, errors, `log-${width}`);
      await page.getByTestId("delivery-redeliver-selected").click();
      await expect(page.getByRole("dialog")).toContainText("queued");
      await checks(page, width, errors, `log-confirm-${width}`);
    }],
  ] as const) {
    test(`${label} at ${width}px`, async ({ page, context }) => {
      test.setTimeout(120_000);
      const errors: string[] = [];
      page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
      await page.setViewportSize({ width, height: 900 });
      const admin = loadState().admin;
      await signInViaBff(context, admin.email, admin.password);
      await run(page, errors);
    });
  }

  test(`view-only staff at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const staff = await staffWithPermissions(["integrations.apikey.view", "integrations.webhook.view"]);
    await signInViaBff(context, staff.email, staff.password);
    await openKeys(page);
    await expect(page.getByTestId("key-edit")).toHaveCount(0);
    await checks(page, width, errors, `keys-viewonly-${width}`);
    await openLog(page);
    await expect(page.getByTestId("delivery-redeliver")).toHaveCount(0);
    await checks(page, width, errors);
  });
}

test("keys, webhooks and the secret dialog in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: E2E.webUrl }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await openKeys(page);
  await checks(page, 1280, errors, "keys-dark");
  await openLog(page);
  await checks(page, 1280, errors, "hooks-dark");
  await page.goto("/admin/integrations/api-keys");
  await page.getByTestId("key-new").click();
  await page.locator("#key-name").fill(`E2E ui dark ${runTag}`);
  await page.getByTestId("scope-stock:read").click();
  await page.getByTestId("key-save").click();
  await expect(page.getByTestId("secret-dialog")).toBeVisible();
  await checks(page, 1280, errors);
});

test("keys and webhooks in Bengali on a phone", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 360, height: 900 });
  await context.addCookies([{ name: "juvo_locale", value: "bn", url: E2E.webUrl }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await openKeys(page);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("এপিআই কী");
  await checks(page, 360, errors, "keys-bn-360");
  await openLog(page, "ব্যর্থ");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("ওয়েবহুক");
  await checks(page, 360, errors, "hooks-bn-360");
  await page.goto("/admin/integrations/api-keys");
  await page.getByTestId("key-new").click();
  await page.locator("#key-name").fill(`E2E ui bn ${runTag}`);
  await page.getByTestId("scope-stock:read").click();
  await page.getByTestId("key-save").click();
  await expect(page.getByTestId("secret-dialog")).toBeVisible();
  await checks(page, 360, errors);
});
