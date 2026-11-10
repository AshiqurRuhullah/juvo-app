import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { axeViolations, horizontalOverflow, SHOTS, settle, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The standard UI checks (screenshot, no sideways scroll, 44 px tap targets, axe, no console errors) for the categories page (each tab,
 * and its dialogs) and the import dialog on the products list, at 360, 768 and 1280 px. Screenshots: docs/ui-checks/lookups/.
 */
const WIDTHS = [360, 768, 1280] as const;
const HEIGHT = { 360: 740, 768: 1024, 1280: 800 } as const;
const DIR = resolve(SHOTS, "lookups");

const BAD_CSV = ["name_en,category,regular_price,sale_price,sku", "UI Check Tee,Clothing > T-shirts,500,900,UI-CHECK-1", "UI Check Tee 2,No Such Category,500,,UI-CHECK-2"].join("\n");

type Target = { name: string; path: string; prepare?: (page: Page) => Promise<void> };

const targets: Target[] = [
  { name: "categories", path: "/admin/categories", prepare: async (page) => void (await expect(page.getByRole("list", { name: "Category tree" })).toBeVisible()) },
  {
    name: "categories-dialog",
    path: "/admin/categories",
    prepare: async (page) => {
      await page.getByRole("button", { name: "New category" }).click();
      await expect(page.getByRole("dialog").getByRole("heading", { name: "New category" })).toBeVisible();
    },
  },
  { name: "brands", path: "/admin/categories?tab=brands", prepare: async (page) => void (await expect(page.getByRole("button", { name: "New brand" })).toBeVisible()) },
  {
    name: "brands-dialog",
    path: "/admin/categories?tab=brands",
    prepare: async (page) => {
      await page.getByRole("button", { name: "New brand" }).click();
      await expect(page.getByRole("dialog").getByRole("heading", { name: "New brand" })).toBeVisible();
    },
  },
  { name: "size-charts", path: "/admin/categories?tab=sizeCharts", prepare: async (page) => void (await expect(page.getByRole("button", { name: "New size chart" })).toBeVisible()) },
  {
    name: "size-chart-dialog",
    path: "/admin/categories?tab=sizeCharts",
    prepare: async (page) => {
      await page.getByRole("button", { name: "New size chart" }).click();
      await expect(page.getByRole("dialog").getByRole("region", { name: "Size table" })).toBeVisible();
    },
  },
  {
    name: "import-dialog",
    path: "/admin/products",
    prepare: async (page) => {
      await page.getByRole("button", { name: "Import" }).click();
      await expect(page.getByRole("dialog").getByRole("heading", { name: "Import products" })).toBeVisible();
    },
  },
  {
    name: "import-report",
    path: "/admin/products",
    prepare: async (page) => {
      await page.getByRole("button", { name: "Import" }).click();
      const dialog = page.getByRole("dialog");
      await dialog.locator("#import-file").setInputFiles({ name: "bad.csv", mimeType: "text/csv", buffer: Buffer.from(BAD_CSV, "utf8") });
      await dialog.getByRole("button", { name: "Check the file (nothing is saved)" }).click();
      await expect(dialog.getByRole("heading", { name: "Problems found" })).toBeVisible();
    },
  },
];

for (const width of WIDTHS) {
  test.describe(`Catalog lists at ${width}px`, () => {
    for (const target of targets) {
      test(target.name, async ({ page, context }) => {
        const errors: string[] = [];
        page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
        page.on("pageerror", (e) => errors.push(String(e)));

        await page.setViewportSize({ width, height: HEIGHT[width] });
        await signInViaBff(context, loadState().admin.email, loadState().admin.password);
        await page.goto(target.path);
        await expect(page.getByRole("heading", { level: 1 }).first()).toBeVisible();
        await expect(page.locator('[aria-busy="true"]')).toHaveCount(0, { timeout: 15_000 });
        await target.prepare?.(page);
        await settle(page);
        await expect(page.locator('[aria-busy="true"]')).toHaveCount(0, { timeout: 15_000 });

        mkdirSync(DIR, { recursive: true });
        await page.screenshot({ path: resolve(DIR, `${target.name}-${width}.png`), fullPage: true, animations: "disabled" });

        const overflow = await horizontalOverflow(page);
        expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);

        const small = await smallTapTargets(page);
        expect(small, `tap targets under 44px:\n${small.join("\n")}`).toEqual([]);

        const violations = await axeViolations(page);
        expect(violations, `accessibility:\n${violations.join("\n")}`).toEqual([]);

        const unexpected = errors.filter((e) => !/Failed to load resource.*(404|401|403)/.test(e));
        expect(unexpected, `console errors:\n${unexpected.join("\n")}`).toEqual([]);
      });
    }
  });
}
