import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { E2E } from "../playwright.config";
import { api, loadState } from "./support/env";
import { apiCheckout, disableAllRules, staffToken } from "./support/orders";
import { sampleVariants } from "./support/store";
import { axeViolations, horizontalOverflow, settle, shot, smallTapTargets, SHOTS, signInViaBff } from "./support/ui";

/**
 * The billing screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console errors.
 * Key screens are kept as screenshots in docs/ui-checks/payments.
 */
const WIDTHS = [360, 768, 1280] as const;
let invoiceId = "";

test.beforeAll(async ({ playwright }) => {
  const admin = await staffToken("admin");
  await disableAllRules(admin);
  const own = await playwright.request.newContext({ baseURL: E2E.webUrl });
  const [item] = await sampleVariants(own, 1);
  const placed = await apiCheckout(own, { lines: [{ variantId: item!.id, quantity: 2 }] });
  await own.dispose();
  expect((await api("POST", `/api/v1/orders/${placed.orderId}/confirm`, { advanceReceived: false }, admin)).status).toBe(200);
  const issued = await api<{ id: string }>("POST", `/api/v1/billing/orders/${placed.orderId}/invoice`, undefined, admin);
  expect(issued.status).toBe(200);
  invoiceId = issued.json.id;
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

  test.describe(`billing at ${width}px`, () => {
    test.beforeEach(async ({ page }) => {
      await page.setViewportSize({ width, height: 900 });
      const admin = loadState().admin;
      await signInViaBff(page.context(), admin.email, admin.password);
    });

    test("invoices list", async ({ page }) => {
      const errors = watch(page);
      await page.goto("/admin/billing");
      await expect(page.locator('[data-testid="invoice-link"]:visible').first()).toBeVisible();
      await checks(page, width, errors, keep("billing-invoices"));
    });

    test("tax rules", async ({ page }) => {
      const errors = watch(page);
      await page.goto("/admin/billing?tab=rules");
      await expect(page.getByTestId("new-rule")).toBeVisible();
      await checks(page, width, errors, keep("billing-rules"));
    });

    test("tax rule dialog", async ({ page }) => {
      const errors = watch(page);
      await page.goto("/admin/billing?tab=rules");
      await page.getByTestId("new-rule").click();
      await expect(page.getByRole("dialog")).toBeVisible();
      await page.waitForTimeout(400);
      await checks(page, width, errors, keep("billing-rule-dialog"));
    });

    test("credit notes list", async ({ page }) => {
      const errors = watch(page);
      await page.goto("/admin/billing?tab=creditNotes");
      await expect(page.getByRole("searchbox")).toBeVisible();
      await checks(page, width, errors, keep("billing-credit-notes"));
    });

    test("invoice detail", async ({ page }) => {
      const errors = watch(page);
      await page.goto(`/admin/billing/invoices/${invoiceId}`);
      await expect(page.getByTestId("invoice-lines")).toBeVisible();
      await checks(page, width, errors, keep("billing-invoice"));
    });
  });
}
