import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { makeCustomer, type CustomerRef } from "./support/customers";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";

/**
 * The customer screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console errors.
 * Screenshots go to docs/ui-checks/customers/.
 */
const WIDTHS = [360, 768, 1280] as const;

let customer: CustomerRef;

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "customers"), { recursive: true });
  customer = await makeCustomer({ marketingSms: true });
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `customers/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

for (const width of WIDTHS) {
  test(`staff customer screens at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);

    await page.goto("/admin/customers");
    await page.getByLabel("Search customers").fill(customer.name);
    await expect(page.locator("[data-testid=customer-link]:visible")).toHaveCount(1);
    await checks(page, width, errors, `list-${width}`);

    await page.goto(`/admin/customers/${customer.id}`);
    await expect(page.getByTestId("order-stats")).toBeVisible();
    await checks(page, width, errors, `detail-${width}`);

    await page.getByTestId("customer-deactivate").click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await checks(page, width, errors, `deactivate-dialog-${width}`);
  });

  test(`masked, read-only customer screens at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const staff = await staffWithPermissions(["customers.customer.view"]);
    await signInViaBff(context, staff.email, staff.password);

    await page.goto("/admin/customers");
    await expect(page.getByTestId("pii-notice")).toBeVisible();
    await page.getByLabel("Search customers").fill(customer.name);
    await expect(page.locator("[data-testid=customer-link]:visible")).toHaveCount(1);
    await checks(page, width, errors, `list-masked-${width}`);

    await page.goto(`/admin/customers/${customer.id}`);
    await expect(page.getByTestId("read-only-note")).toBeVisible();
    await checks(page, width, errors, `detail-masked-${width}`);
  });
}

test("customer screens in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto("/admin/customers");
  await page.getByLabel("Search customers").fill(customer.name);
  await expect(page.locator("[data-testid=customer-link]:visible")).toHaveCount(1);
  await checks(page, 1280, errors, "list-dark");
  await page.goto(`/admin/customers/${customer.id}`);
  await expect(page.getByTestId("order-stats")).toBeVisible();
  await checks(page, 1280, errors, "detail-dark");
});
