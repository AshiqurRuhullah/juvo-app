import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { deliveredOrder, stockedVariants } from "./support/returns";
import { settleBounded } from "./support/couriers-ui";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The dashboard with the date range, trend compare and report sections on a phone, a tablet and a desktop: no sideways scroll, 44 px
 * targets, axe (WCAG 2.1 AA, charts included), no console errors. Also the dark theme and Bengali. Screenshots go to docs/ui-checks/dashboard/.
 */
const WIDTHS = [360, 768, 1280] as const;

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "dashboard"), { recursive: true });
  const variants = await stockedVariants(2, 1, 8);
  await deliveredOrder([{ variantId: variants[0]!.id, quantity: 1 }]);
});

async function ready(page: Page) {
  await expect(page.getByTestId("dashboard")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId("range-trend-tiles")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId("range-cash-tiles")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId("range-stock-tiles")).toBeVisible({ timeout: 30_000 });
  await expect(page.getByTestId("range-top-customers")).toBeVisible();
  // Charts load lazily: wait until every chart container holds its drawing.
  for (const chart of await page.getByTestId("chart").all()) await expect(chart.locator("svg").first()).toBeVisible({ timeout: 15_000 });
  await settleBounded(page);
}

async function checks(page: Page, errors: string[], name: string, width: number) {
  await shot(page, `dashboard/${name}-${width}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll on ${name} at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px on ${name} at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility on ${name} at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e)), `console errors on ${name}`).toEqual([]);
}

for (const width of WIDTHS) {
  test(`dashboard with period sections at ${width}px`, async ({ page, context }) => {
    test.setTimeout(180_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);
    await page.goto("/admin");
    await ready(page);
    await checks(page, errors, "dashboard", width);
    // The same page with the comparison switched off and a custom range.
    await page.goto("/admin?from=2026-01-01&to=2026-03-31&groupBy=month&compare=0");
    await expect(page.getByTestId("range-trend-tiles")).toBeVisible({ timeout: 30_000 });
    for (const chart of await page.getByTestId("chart").all()) await expect(chart.locator("svg").first()).toBeVisible({ timeout: 15_000 });
    await settleBounded(page);
    await checks(page, errors, "dashboard-custom", width);
  });
}

test("the dashboard in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto("/admin");
  await ready(page);
  await checks(page, errors, "dashboard-dark", 1280);
});

test("the dashboard in Bengali on a phone and a desktop", async ({ page, context }) => {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await context.request.post("/api/bff/session/preferences", { data: { locale: "bn" } });
  for (const width of [360, 1280]) {
    await page.setViewportSize({ width, height: 900 });
    await page.goto("/admin");
    await ready(page);
    await expect(page.getByTestId("range-part").getByRole("heading", { level: 2 }).first()).toHaveText("নির্বাচিত সময়ে");
    await checks(page, errors, "dashboard-bn", width);
  }
  await context.request.post("/api/bff/session/preferences", { data: { locale: "en" } });
});
