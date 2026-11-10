import { test, expect, type Page as PwPage } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { api, loadState } from "./support/env";
import { axeViolations, horizontalOverflow, settle, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The same checks as ums-ui-checks.spec.ts (screenshot, no sideways scroll, 44 px tap targets, axe, no console errors) for every
 * inventory screen at 360, 768 and 1280 px. A transfer and a stock count are created once through the API so the detail pages have data.
 */
const WIDTHS = [360, 768, 1280] as const;
const HEIGHT = { 360: 740, 768: 1024, 1280: 800 } as const;
const SHOTS = resolve(__dirname, "../docs/ui-checks/inventory");

type Ids = { transfer: string; count: string };
type Target = { name: string; path: (ids: Ids) => string; prepare?: (page: PwPage) => Promise<void> };

const pages: Target[] = [
  { name: "inventory-stock", path: () => "/admin/inventory" },
  {
    name: "inventory-stock-adjust",
    path: () => "/admin/inventory",
    prepare: async (page) => {
      await page.getByRole("button", { name: /^Adjust stock of/ }).first().click();
      await expect(page.getByRole("heading", { name: "Adjust stock" })).toBeVisible();
    },
  },
  { name: "inventory-movements", path: () => "/admin/inventory/movements" },
  { name: "inventory-transfers", path: () => "/admin/inventory/transfers" },
  { name: "inventory-transfer-new", path: () => "/admin/inventory/transfers/new" },
  { name: "inventory-transfer-detail", path: (ids) => `/admin/inventory/transfers/${ids.transfer}` },
  { name: "inventory-counts", path: () => "/admin/inventory/counts" },
  {
    name: "inventory-count-start",
    path: () => "/admin/inventory/counts",
    prepare: async (page) => {
      await page.getByRole("button", { name: "Start a count" }).click();
      await expect(page.getByRole("heading", { name: "Start a stock count" })).toBeVisible();
    },
  },
  { name: "inventory-count-detail", path: (ids) => `/admin/inventory/counts/${ids.count}` },
];

let ids: Ids;

test.beforeAll(async () => {
  const s = loadState();
  const login = await api<{ accessToken: string }>("POST", "/api/v1/auth/login", { identifier: s.admin.email, password: s.admin.password });
  expect(login.status).toBe(200);
  const token = login.json.accessToken;

  const stock = await api<{ items: { variantId: string }[] }>("GET", `/api/v1/inventory/stock?pageSize=1&branchId=${s.branches.ho.id}`, undefined, token);
  const variantId = stock.json.items[0]!.variantId;

  const transfer = await api<{ id: string }>("POST", "/api/v1/inventory/transfers", { fromBranchId: s.branches.ho.id, toBranchId: s.branches.a.id, note: "UI check", lines: [{ variantId, quantity: 1 }] }, token);
  expect(transfer.status).toBe(201);

  // The count goes in a shop of its own so it never blocks the count the inventory flow starts in Head Office.
  const count = await api<{ summary: { id: string } }>("POST", "/api/v1/inventory/counts", { branchId: s.branches.b.id, note: "UI check", variantIds: [variantId] }, token);
  let countId = count.status === 201 ? count.json.summary.id : undefined;
  if (!countId) {
    // Playwright restarts the worker after a failed test, which runs this set-up again: reuse the count that is still open.
    expect(count.status).toBe(409);
    const open = await api<{ items: { id: string }[] }>("GET", `/api/v1/inventory/counts?branchId=${s.branches.b.id}&status=InProgress`, undefined, token);
    countId = open.json.items[0]!.id;
  }
  ids = { transfer: transfer.json.id, count: countId };
});

for (const width of WIDTHS) {
  test.describe(`Inventory at ${width}px`, () => {
    for (const target of pages) {
      test(target.name, async ({ page, context }) => {
        const errors: string[] = [];
        page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
        page.on("pageerror", (e) => errors.push(String(e)));

        await page.setViewportSize({ width, height: HEIGHT[width] });
        await signInViaBff(context, loadState().admin.email, loadState().admin.password);

        await page.goto(target.path(ids));
        await expect(page.getByRole("heading", { level: 1 }).first()).toBeVisible();
        await expect(page.getByText("Loading").first()).toHaveCount(0);
        await settle(page);
        // Lists load their data after the page: wait until no skeleton is left.
        await expect(page.locator('[aria-busy="true"]')).toHaveCount(0, { timeout: 15_000 });
        await target.prepare?.(page);
        await settle(page);

        mkdirSync(SHOTS, { recursive: true });
        await page.screenshot({ path: resolve(SHOTS, `${target.name}-${width}.png`), fullPage: true, animations: "disabled" });

        const overflow = await horizontalOverflow(page);
        expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);

        const small = await smallTapTargets(page);
        expect(small, `tap targets under 44px:\n${small.join("\n")}`).toEqual([]);

        const violations = await axeViolations(page);
        expect(violations, `accessibility:\n${violations.join("\n")}`).toEqual([]);

        const unexpected = errors.filter((e) => !/Failed to load resource.*(404|401|403)/.test(e) && !/tile\.openstreetmap\.org/.test(e));
        expect(unexpected, `console errors:\n${unexpected.join("\n")}`).toEqual([]);
      });
    }
  });
}
