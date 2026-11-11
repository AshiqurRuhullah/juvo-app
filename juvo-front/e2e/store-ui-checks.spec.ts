import { test, expect, type APIRequestContext, type Page } from "@playwright/test";
import { axeViolations, horizontalOverflow, settle, shot, smallTapTargets } from "./support/ui";

/**
 * The storefront browse pages on a phone, a tablet and a desktop: no sideways scroll, 44 px tap targets, axe (WCAG 2.1 AA) and no
 * console errors. Key screens are kept as screenshots (store-home and store-product at 360 and 1280 px).
 */
const WIDTHS = [360, 768, 1280] as const;

async function firstSlug(request: APIRequestContext): Promise<string> {
  const response = await request.get("/api/bff/api/v1/store/products?pageSize=1");
  const { items } = (await response.json()) as { items: { slug: string }[] };
  return items[0]!.slug;
}

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settle(page);
  if (name) await shot(page, name);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

for (const width of WIDTHS) {
  test.describe(`storefront at ${width}px`, () => {
    let errors: string[];
    test.beforeEach(async ({ page }) => {
      errors = [];
      page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
      await page.setViewportSize({ width, height: 900 });
    });

    test("home", async ({ page }) => {
      await page.goto("/");
      await expect(page.getByTestId("product-card").first()).toBeVisible();
      await checks(page, width, errors, width === 360 || width === 1280 ? `store-home-${width}` : undefined);
    });

    test("shop with filters", async ({ page }) => {
      await page.goto("/shop");
      await expect(page.getByTestId("product-card").first()).toBeVisible();
      await checks(page, width, errors);
    });

    test("product page", async ({ page, request }) => {
      await page.goto(`/product/${await firstSlug(request)}`);
      await expect(page.getByTestId("product-name")).toBeVisible();
      await checks(page, width, errors, width === 360 || width === 1280 ? `store-product-${width}` : undefined);
    });

    test("search results and the empty state", async ({ page }) => {
      await page.goto("/search?q=zzzzqqqq");
      await expect(page.getByRole("heading", { name: /Nothing found for/ }).first()).toBeVisible();
      await checks(page, width, errors);
    });

    test("not found", async ({ page }) => {
      await page.goto("/product/no-such-product");
      await expect(page.getByRole("heading", { name: "We could not find that" })).toBeVisible();
      await checks(page, width, errors);
    });
  });
}

test("phone: the menu drawer lists the categories and the filter sheet opens", async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 800 });
  await page.goto("/shop");
  await page.getByRole("button", { name: "Menu" }).click();
  const drawer = page.getByRole("dialog", { name: "Menu" });
  await expect(drawer.getByRole("link", { name: "All products" })).toBeVisible();
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: /^Filters/ }).click();
  const sheet = page.getByRole("dialog", { name: "Filters" });
  await expect(sheet.getByRole("group", { name: "Price" })).toBeVisible();
  await expect(sheet.getByRole("button", { name: /^Show \d+ products?$/ })).toBeVisible();
});

test("desktop: the category menu opens with the sub-categories", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.goto("/");
  const trigger = page.getByRole("navigation", { name: "Shop by category" }).getByRole("button").first();
  test.skip((await trigger.count()) === 0, "no category with sub-categories in the sample data");
  await trigger.click();
  await expect(page.getByRole("menuitem").first()).toBeVisible();
});
