import { test, expect, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { api, loadState } from "./support/env";
import { apiCheckout, disableAllRules, staffToken } from "./support/orders";
import { sampleVariants } from "./support/store";
import { axeViolations, horizontalOverflow, settle, shot, smallTapTargets, SHOTS, signInViaBff } from "./support/ui";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";

/**
 * The notification screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console
 * errors. Key screens are kept as screenshots in docs/ui-checks/payments.
 */
const WIDTHS = [360, 768, 1280] as const;

test.beforeAll(async ({ playwright }) => {
  const admin = await staffToken("admin");
  await disableAllRules(admin);
  const guest = await playwright.request.newContext({ baseURL: E2E.webUrl });
  const items = await sampleVariants(guest, 1);
  const placed = await apiCheckout(guest, { lines: [{ variantId: items[0]!.id, quantity: 1 }] });
  await guest.dispose();
  // Make sure the log has the order's message before the screens are drawn.
  await expect
    .poll(async () => (await api<{ total: number }>("GET", `/api/v1/notifications/logs?template=order.placed&Search=${encodeURIComponent(placed.number)}`, undefined, admin)).json.total, { timeout: 30_000 })
    .toBeGreaterThan(0);
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settle(page);
  if (name) {
    mkdirSync(resolve(SHOTS, "payments"), { recursive: true });
    await shot(page, `payments/${name}`);
  }
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

const watch = (page: Page) => {
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  return errors;
};

for (const width of WIDTHS) {
  const keep = (name: string) => (width === 360 || width === 1280 ? `${name}-${width}` : undefined);

  test.describe(`at ${width}px`, () => {
    test.beforeEach(async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      await signInViaBff(page.context(), loadState().admin.email, loadState().admin.password);
      await page.goto("/admin/notifications");
      await expect(page.getByRole("tab", { name: "Templates" })).toBeVisible();
    });

    test("templates", async ({ page }) => {
      const errors = watch(page);
      await expect(page.locator('[data-testid="template-group"]').first()).toBeVisible();
      await checks(page, width, errors, keep("notifications-templates"));
    });

    test("template editor", async ({ page }) => {
      const errors = watch(page);
      await page.getByRole("button", { name: "Edit order.placed, SMS, English" }).click();
      await expect(page.getByTestId("template-preview-body")).not.toHaveText("...");
      await expect(page.getByTestId("template-history").locator("li").first().or(page.getByTestId("template-history-empty"))).toBeVisible();
      await checks(page, width, errors, keep("notifications-editor"));
    });

    test("delivery log", async ({ page }) => {
      const errors = watch(page);
      await page.getByRole("tab", { name: "Delivery log" }).click();
      await expect(page.locator('[data-testid="log-template"]:visible').first()).toBeVisible();
      await checks(page, width, errors, keep("notifications-logs"));
    });

    test("channels", async ({ page }) => {
      const errors = watch(page);
      await page.getByRole("tab", { name: "Channels" }).click();
      await expect(page.getByRole("switch", { name: /SMS/ })).toBeVisible();
      await checks(page, width, errors, keep("notifications-channels"));
    });
  });
}
