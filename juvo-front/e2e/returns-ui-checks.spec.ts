import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { advanceTo, deliveredOrder, requestReturn, stockedVariants, type Delivered, type ReturnRef } from "./support/returns";
import { signInCustomer } from "./support/store";
import { SHOTS, axeViolations, horizontalOverflow, settle, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The returns screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console errors.
 * Screenshots go to docs/ui-checks/returns/.
 */
const WIDTHS = [360, 768, 1280] as const;

let fresh: Delivered;
let requested: ReturnRef;
let inspected: ReturnRef;
let inspectedOrder: Delivered;

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "returns"), { recursive: true });
  const variants = await stockedVariants(2, 3, 12);
  fresh = await deliveredOrder([{ variantId: variants[0]!.id, quantity: 2 }]);
  const second = await deliveredOrder([{ variantId: variants[1]!.id, quantity: 1 }]);
  requested = await requestReturn(second, { reasonCode: "DAMAGED_ITEM" });
  inspectedOrder = await deliveredOrder([{ variantId: variants[2]!.id, quantity: 1 }]);
  inspected = await requestReturn(inspectedOrder, { reasonCode: "WRONG_ITEM" });
  await advanceTo(inspected.id, "Inspected");
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settle(page);
  if (name) await shot(page, `returns/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

for (const width of WIDTHS) {
  test(`customer returns screens at ${width}px`, async ({ page, context }) => {
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    await signInCustomer(context, fresh.mobile);

    await page.goto(`/account/returns/new?order=${fresh.orderId}`);
    await expect(page.getByTestId("return-line")).toHaveCount(1);
    await page.getByRole("button", { name: "One more" }).click();
    await page.getByRole("radio", { name: "The item arrived damaged" }).click();
    await checks(page, width, errors, `request-${width}`);

    await signInCustomer(context, inspectedOrder.mobile);
    await page.goto("/account/returns");
    await expect(page.getByTestId("my-return")).toHaveCount(1);
    await checks(page, width, errors, `list-${width}`);

    await page.goto(`/account/returns/${inspected.id}`);
    await expect(page.getByTestId("refund-box").first()).toBeVisible();
    await checks(page, width, errors, `detail-${width}`);
  });

  test(`admin returns screens at ${width}px`, async ({ page, context }) => {
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);

    await page.goto("/admin/returns");
    await expect(page.getByTestId("return-summary")).toBeVisible();
    await expect(page.getByRole("link", { name: requested.number }).first()).toBeVisible();
    await checks(page, width, errors, `queue-${width}`);

    await page.goto(`/admin/returns/${inspected.id}`);
    await expect(page.getByTestId("return-actions")).toBeVisible();
    await checks(page, width, errors, `admin-detail-${width}`);

    await page.getByTestId("action-refund").click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await page.waitForTimeout(400);
    await checks(page, width, errors, width === 360 ? "refund-dialog-360" : undefined);
  });
}

test("returns screens in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: "http://localhost:3100" }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto(`/admin/returns/${inspected.id}`);
  await expect(page.getByTestId("return-actions")).toBeVisible();
  await checks(page, 1280, errors, "admin-detail-dark");
});
