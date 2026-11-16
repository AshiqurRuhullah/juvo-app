import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { deliveredOrder, stockedVariants } from "./support/returns";
import { settleBounded } from "./support/couriers-ui";
import { reportPath } from "./support/reports";
import { rangeQuery } from "./support/reports";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The report screens, the profit and loss and the dashboard on a phone, a tablet and a desktop: no sideways scroll, 44 px targets,
 * axe (WCAG 2.1 AA, charts included) and no console errors. Screenshots go to docs/ui-checks/reports/.
 */
const WIDTHS = [360, 768, 1280] as const;

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "reports"), { recursive: true });
  const variants = await stockedVariants(2, 1, 8);
  await deliveredOrder([{ variantId: variants[0]!.id, quantity: 1 }]);
});

async function checks(page: Page, width: number, errors: string[], name: string, ready: string) {
  await expect(page.getByTestId(ready).first()).toBeVisible();
  // Charts load lazily: wait until the chart container holds its drawing.
  const chart = page.getByTestId("chart").first();
  if (await chart.count()) await expect(chart.locator("svg").first()).toBeVisible({ timeout: 15_000 });
  await settleBounded(page);
  await shot(page, `reports/${name}-${width}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll on ${name} at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px on ${name} at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility on ${name} at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e)), `console errors on ${name}`).toEqual([]);
}

const PAGES: { name: string; path: string; ready: string }[] = [
  { name: "landing", path: "/admin/reports", ready: "report-cards" },
  { name: "sales", path: reportPath("sales"), ready: "tile-orders" },
  { name: "sales-product", path: reportPath("sales", "groupBy=product"), ready: "sales-table" },
  { name: "orders", path: reportPath("orders"), ready: "orders-funnel" },
  { name: "inventory", path: reportPath("inventory"), ready: "inventory-movements" },
  { name: "finance", path: reportPath("finance"), ready: "finance-cod" },
  { name: "courier", path: reportPath("courier"), ready: "courier-table" },
  { name: "customers", path: reportPath("customers"), ready: "customers-top" },
  { name: "pnl", path: `/admin/accounting/pnl?${rangeQuery("branch=")}`, ready: "pnl-export" },
  { name: "dashboard", path: "/admin", ready: "dashboard" },
];

for (const width of WIDTHS) {
  test(`reports, profit and loss and dashboard at ${width}px`, async ({ page, context }) => {
    test.setTimeout(240_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);
    for (const p of PAGES) {
      await page.goto(p.path);
      await checks(page, width, errors, p.name, p.ready);
    }
  });
}

test("the export menu and the filter bar are usable on a phone", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 360, height: 800 });
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto(reportPath("sales"));
  await expect(page.getByTestId("tile-orders")).toBeVisible();
  await page.getByTestId("export-open").click();
  await expect(page.getByTestId("export-panel")).toBeVisible();
  await page.getByTestId("export-format-pdf").click();
  await page.waitForTimeout(400);
  await checks(page, 360, errors, "export-menu", "export-panel");
});

test("reports in the dark theme", async ({ page, context }) => {
  test.setTimeout(180_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  for (const p of [PAGES[1]!, PAGES[8]!, PAGES[9]!]) {
    await page.goto(p.path);
    await checks(page, 1280, errors, `${p.name}-dark`, p.ready);
  }
});
