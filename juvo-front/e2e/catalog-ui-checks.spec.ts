import { test, expect } from "@playwright/test";
import { loadState } from "./support/env";
import { axeViolations, horizontalOverflow, settle, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The same checks as ums-ui-checks.spec.ts (screenshot, no sideways scroll, 44 px tap targets, axe, no console errors) for the
 * product screens at 360, 768 and 1280 px. The product used is one of the sample products the e2e API seeds in Development.
 */
const WIDTHS = [360, 768, 1280] as const;
const HEIGHT = { 360: 740, 768: 1024, 1280: 800 } as const;

type Target = { name: string; path: (id: string) => string; prepare?: (page: import("@playwright/test").Page) => Promise<void> };

const targets: Target[] = [
  { name: "catalog-products", path: () => "/admin/products" },
  { name: "catalog-product-new", path: () => "/admin/products/new" },
  { name: "catalog-product-details", path: (id) => `/admin/products/${id}` },
  {
    name: "catalog-product-variants",
    path: (id) => `/admin/products/${id}?tab=variants`,
    prepare: async (page) => void (await expect(page.getByRole("heading", { name: "Stock by branch" })).toBeVisible()),
  },
  {
    name: "catalog-product-pictures",
    path: (id) => `/admin/products/${id}?tab=pictures`,
    prepare: async (page) => void (await expect(page.getByTestId("picture").first()).toBeVisible()),
  },
  { name: "catalog-product-seo", path: (id) => `/admin/products/${id}?tab=seo` },
  {
    name: "catalog-picture-editor",
    path: (id) => `/admin/products/${id}?tab=pictures`,
    prepare: async (page) => {
      await page.getByRole("button", { name: "Edit picture 1" }).click();
      await expect(page.getByTestId("focal-frame")).toBeVisible();
    },
  },
];

for (const width of WIDTHS) {
  test.describe(`Products at ${width}px`, () => {
    for (const target of targets) {
      test(target.name, async ({ page, context }) => {
        const errors: string[] = [];
        page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
        page.on("pageerror", (e) => errors.push(String(e)));

        await page.setViewportSize({ width, height: HEIGHT[width] });
        await signInViaBff(context, loadState().admin.email, loadState().admin.password);
        const list = (await (await context.request.get("/api/bff/api/v1/catalog/products?search=Classic Cotton&PageSize=5")).json()) as { items: { id: string; nameEn: string }[] };
        const id = list.items.find((p) => p.nameEn === "Classic Cotton T-Shirt")!.id;

        await page.goto(target.path(id));
        await expect(page.getByRole("heading", { level: 1 }).first()).toBeVisible();
        await target.prepare?.(page);
        await settle(page);
        await expect(page.locator('[aria-busy="true"]')).toHaveCount(0, { timeout: 15_000 });

        await shot(page, `${target.name}-${width}`);

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
