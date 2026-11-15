import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { settleBounded } from "./support/couriers-ui";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The courier screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console errors.
 * Screenshots go to docs/ui-checks/couriers/.
 */
const WIDTHS = [360, 768, 1280] as const;

test.beforeAll(() => {
  mkdirSync(resolve(SHOTS, "couriers"), { recursive: true });
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  const pendingFonts = await settleBounded(page);
  if (pendingFonts.length) console.log(`fonts still loading at ${width}px:`, pendingFonts.join(", "));
  if (name) await shot(page, `couriers/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

for (const width of WIDTHS) {
  test(`courier screens at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);

    await page.goto("/admin/couriers");
    await expect(page.locator('[data-testid="courier-card"][data-code="SANDBOX"]')).toBeVisible();
    await checks(page, width, errors, `list-${width}`);

    await page.getByRole("tab", { name: "Connections" }).click();
    await expect(page.getByTestId("provider-card")).toHaveCount(7);
    await checks(page, width, errors, `connections-${width}`);

    await page.getByRole("tab", { name: "Couriers" }).click();
    await page.getByTestId("courier-add").click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await expect(page.getByRole("group", { name: "Zones it serves" }).or(page.getByLabel("Zone codes"))).toBeVisible();
    await page.getByRole("button", { name: "Add setting" }).click();
    await page.waitForTimeout(400);
    await checks(page, width, errors, width === 360 ? "form-360" : undefined);
  });
}

test("courier screens in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto("/admin/couriers");
  await expect(page.locator('[data-testid="courier-card"][data-code="SANDBOX"]')).toBeVisible();
  await checks(page, 1280, errors, "list-dark");
});
