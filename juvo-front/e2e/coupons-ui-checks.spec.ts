import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";
import { dhakaLocalIn, makeCoupon, newCode, placeOrderWithCoupon } from "./support/coupons";

/**
 * The coupon screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console errors,
 * for the list, the create and edit dialogs and the delete dialog, plus the dark theme, Bengali and the read-only view.
 * Screenshots go to docs/ui-checks/coupons/.
 */
const WIDTHS = [360, 768, 1280] as const;
const prefix = newCode("UIC");
let used: { code: string };

test.beforeAll(async ({ playwright }) => {
  test.setTimeout(240_000);
  mkdirSync(resolve(SHOTS, "coupons"), { recursive: true });
  await makeCoupon({ code: `${prefix}LIVE`, description: "Eid offer for checks", value: 10, maxDiscount: 150, minOrderValue: 1000, startsAt: `${dhakaLocalIn(-24)}:00+06:00`, endsAt: `${dhakaLocalIn(240)}:00+06:00`, usageLimit: 50, stacksWithPromotions: true });
  await makeCoupon({ code: `${prefix}FIX`, kind: "Fixed", value: 200 });
  await makeCoupon({ code: `${prefix}OLD`, startsAt: `${dhakaLocalIn(-72)}:00+06:00`, endsAt: `${dhakaLocalIn(-24)}:00+06:00` });
  await makeCoupon({ code: `${prefix}OFF`, isActive: false });
  used = await makeCoupon({ code: `${prefix}USED`, usageLimit: 3, value: 5 });
  await placeOrderWithCoupon(playwright, used.code);
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `coupons/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

async function listPage(page: Page) {
  await page.goto("/admin/coupons");
  await page.getByLabel("Search coupons").fill(prefix);
  await expect(page.getByTestId("coupon-code").filter({ visible: true })).toHaveCount(5);
}

/** A card on a phone, a table row on a desktop: the visible element that holds the code. */
const rowOf = (page: Page, code: string) => page.getByTestId("coupon-code").filter({ hasText: code, visible: true }).locator("xpath=ancestor::*[self::tr or self::li or self::article][1]");

for (const width of WIDTHS) {
  for (const [label, run] of [
    ["list", async (page: Page, errors: string[]) => {
      await listPage(page);
      await checks(page, width, errors, `list-${width}`);
    }],
    ["new coupon dialog", async (page: Page, errors: string[]) => {
      await page.goto("/admin/coupons");
      await page.getByTestId("coupon-new").click();
      await expect(page.getByTestId("coupon-form")).toBeVisible();
      await page.locator("#cp-value").fill("10");
      await expect(page.getByTestId("coupon-example")).toBeVisible();
      await checks(page, width, errors, `new-${width}`);
    }],
    ["new coupon dialog with errors", async (page: Page, errors: string[]) => {
      await page.goto("/admin/coupons");
      await page.getByTestId("coupon-new").click();
      await page.getByTestId("coupon-save").click();
      await expect(page.getByText("Enter a code.")).toBeVisible();
      await checks(page, width, errors, `new-errors-${width}`);
    }],
    ["edit dialog", async (page: Page, errors: string[]) => {
      await listPage(page);
      await rowOf(page, `${prefix}LIVE`).getByTestId("coupon-edit").click();
      await expect(page.locator("#cp-code")).toHaveValue(`${prefix}LIVE`);
      await expect(page.getByTestId("coupon-stacks-explain")).toContainText("added on top of promotions that allow it");
      await checks(page, width, errors, `edit-${width}`);
    }],
    ["delete dialog of a used coupon", async (page: Page, errors: string[]) => {
      await listPage(page);
      await rowOf(page, used.code).getByTestId("coupon-delete").click();
      await expect(page.getByTestId("remove-consequence")).toHaveAttribute("data-plan", "switchOff");
      await checks(page, width, errors, `delete-used-${width}`);
    }],
    ["delete dialog of an unused coupon", async (page: Page, errors: string[]) => {
      await listPage(page);
      await rowOf(page, `${prefix}FIX`).getByTestId("coupon-delete").click();
      await expect(page.getByTestId("remove-consequence")).toHaveAttribute("data-plan", "delete");
      await checks(page, width, errors, `delete-unused-${width}`);
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

  test(`read-only list at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const staff = await staffWithPermissions(["promotions.coupon.view"]);
    await signInViaBff(context, staff.email, staff.password);
    await listPage(page);
    await expect(page.getByTestId("coupons-read-only")).toBeVisible();
    await checks(page, width, errors, `readonly-${width}`);
  });
}

test("coupon screens in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await listPage(page);
  await checks(page, 1280, errors, "list-dark");
  await rowOf(page, `${prefix}LIVE`).getByTestId("coupon-edit").click();
  await expect(page.getByTestId("coupon-form")).toBeVisible();
  await checks(page, 1280, errors, "edit-dark");
});

test("coupon screens in Bengali", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 360, height: 900 });
  await context.addCookies([{ name: "juvo_locale", value: "bn", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto("/admin/coupons");
  await page.getByLabel("কুপন খুঁজুন").fill(prefix);
  await expect(page.getByTestId("coupon-code").filter({ visible: true })).toHaveCount(5);
  await checks(page, 360, errors, "list-bn-360");
  await rowOf(page, `${prefix}LIVE`).getByTestId("coupon-edit").click();
  await expect(page.getByTestId("coupon-stacks-explain")).toContainText("অনুমতি দেয় এমন প্রচারের ওপর কুপন যোগ হয়");
  await checks(page, 360, errors, "edit-bn-360");
});
