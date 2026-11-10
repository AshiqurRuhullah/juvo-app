import { test, expect, type BrowserContext, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { signInViaBff } from "./support/ui";

/**
 * Categories, brands, size charts and the bulk import dialog, driven through the real screens. The e2e API runs in Development, so the
 * sample catalog (16 products, tagged "sample") exists: "T-shirts" is a category that products use and cannot be deleted.
 * The import dialog lives on the products list (it is mounted there by the products page).
 */
const stamp = Date.now().toString(36);
const admin = () => loadState().admin;

type Node = { id: string; nameEn: string; children: Node[] };

async function rootOrder(context: BrowserContext): Promise<string[]> {
  const response = await context.request.get("/api/bff/api/v1/catalog/categories?includeInactive=true");
  expect(response.status()).toBe(200);
  return ((await response.json()) as Node[]).map((n) => n.nameEn);
}

async function productCount(context: BrowserContext, search: string): Promise<number> {
  const response = await context.request.get(`/api/bff/api/v1/catalog/products?search=${encodeURIComponent(search)}&pageSize=50`);
  expect(response.status()).toBe(200);
  return Number(((await response.json()) as { total: number | string }).total);
}

async function saveDialog(page: Page) {
  await page.getByRole("dialog").getByRole("button", { name: "Save" }).click();
}

test.describe("Categories, brands and size charts", () => {
  test.describe.configure({ mode: "serial" });
  test.skip(({ viewport }) => (viewport?.width ?? 1280) < 1024, "long forms are driven on desktop; phones are covered by the UI checks");

  const parent = `E2E Cat A ${stamp}`;
  const sibling = `E2E Cat B ${stamp}`;
  const child = `E2E Child ${stamp}`;
  const renamed = `E2E Renamed ${stamp}`;

  test("add a category and a sub-category, rename it, and reorder siblings", async ({ page, context }) => {
    await signInViaBff(context, admin().email, admin().password);
    await page.goto("/admin/categories");
    await expect(page.getByRole("heading", { level: 1, name: "Categories, brands and size charts" })).toBeVisible();
    await expect(page.getByRole("tab", { name: "Categories" })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByRole("list", { name: "Category tree" })).toBeVisible();

    for (const name of [parent, sibling]) {
      await page.getByRole("button", { name: "New category" }).click();
      await page.locator("#cat-name-en").fill(name);
      await saveDialog(page);
      await expect(page.getByRole("button", { name: `Add sub-category to ${name}` })).toBeVisible();
    }

    await page.getByRole("button", { name: `Add sub-category to ${parent}` }).click();
    await expect(page.getByRole("heading", { name: `New sub-category of ${parent}` })).toBeVisible();
    await page.locator("#cat-name-en").fill(child);
    await saveDialog(page);
    await expect(page.getByRole("button", { name: `Edit ${child}` })).toBeVisible();

    await page.getByRole("button", { name: `Edit ${child}` }).click();
    await page.locator("#cat-name-en").fill(renamed);
    await saveDialog(page);
    await expect(page.getByRole("button", { name: `Edit ${renamed}` })).toBeVisible();
    await expect(page.getByRole("button", { name: `Edit ${child}` })).toHaveCount(0);

    // Both new categories have the same position, so they sit in name order: A then B. Moving B up puts it first, moving it down restores A.
    await expect.poll(async () => (await rootOrder(context)).indexOf(sibling) > (await rootOrder(context)).indexOf(parent)).toBe(true);
    await page.getByRole("button", { name: `Move ${sibling} up` }).click();
    await expect.poll(async () => (await rootOrder(context)).indexOf(sibling) < (await rootOrder(context)).indexOf(parent)).toBe(true);
    await page.getByRole("button", { name: `Move ${sibling} down` }).click();
    await expect.poll(async () => (await rootOrder(context)).indexOf(sibling) > (await rootOrder(context)).indexOf(parent)).toBe(true);
  });

  test("an empty category is deleted, one that products use is refused", async ({ page, context }) => {
    await signInViaBff(context, admin().email, admin().password);
    await page.goto("/admin/categories");

    await page.getByRole("button", { name: `Delete ${renamed}` }).click();
    await expect(page.getByRole("heading", { name: "Delete this category?" })).toBeVisible();
    await page.getByRole("button", { name: "Delete category" }).click();
    await expect(page.getByRole("button", { name: `Edit ${renamed}` })).toHaveCount(0);

    for (const name of [parent, sibling]) {
      await page.getByRole("button", { name: `Delete ${name}` }).click();
      await page.getByRole("button", { name: "Delete category" }).click();
      await expect(page.getByRole("button", { name: `Edit ${name}` })).toHaveCount(0);
    }

    // T-shirts holds sample products: the API refuses and the friendly message is shown; the category stays.
    await page.getByRole("button", { name: "Delete T-shirts" }).click();
    await page.getByRole("button", { name: "Delete category" }).click();
    await expect(page.getByText(/Move or remove them first/).first()).toBeVisible();
    await expect(page.getByRole("button", { name: "Edit T-shirts" })).toBeVisible();
  });

  test("create, edit and delete a brand", async ({ page, context }) => {
    const brand = `E2E Brand ${stamp}`;
    await signInViaBff(context, admin().email, admin().password);
    await page.goto("/admin/categories");
    await page.getByRole("tab", { name: "Brands" }).click();
    await expect(page).toHaveURL(/tab=brands/);

    await page.getByRole("button", { name: "New brand" }).click();
    await page.locator("#brand-name-en").fill(brand);
    await page.locator("#brand-name-bn").fill("ই২ই ব্র্যান্ড");
    await saveDialog(page);
    await expect(page.locator("table").getByText(brand, { exact: true })).toBeVisible();

    // The chosen tab survives a reload.
    await page.reload();
    await expect(page.getByRole("tab", { name: "Brands" })).toHaveAttribute("aria-selected", "true");
    await page.getByLabel("Search", { exact: true }).fill(brand);
    await expect(page.locator("table").getByText(brand, { exact: true })).toBeVisible();

    await page.getByRole("button", { name: `Edit ${brand}` }).click();
    await page.locator("#brand-name-en").fill(`${brand} 2`);
    await saveDialog(page);
    await expect(page.locator("table").getByText(`${brand} 2`, { exact: true })).toBeVisible();

    await page.getByRole("button", { name: `Delete ${brand} 2` }).click();
    await page.getByRole("button", { name: "Delete brand" }).click();
    await expect(page.locator("table").getByText(`${brand} 2`, { exact: true })).toHaveCount(0);
  });

  test("create a size chart with the grid editor", async ({ page, context }) => {
    const chart = `E2E Sizes ${stamp}`;
    await signInViaBff(context, admin().email, admin().password);
    await page.goto("/admin/categories?tab=sizeCharts");
    await expect(page.getByRole("tab", { name: "Size charts" })).toHaveAttribute("aria-selected", "true");

    await page.getByRole("button", { name: "New size chart" }).click();
    await page.locator("#sc-name-en").fill(chart);

    // An incomplete table is refused before anything is sent.
    await saveDialog(page);
    await expect(page.getByText("Fill every cell")).toBeVisible();

    await page.getByLabel("Column 1 title").fill("Size");
    await page.getByLabel("Column 2 title").fill("Chest");
    await page.getByRole("button", { name: "Add row" }).first().click();
    await page.getByLabel("Row 1, Size").fill("M");
    await page.getByLabel("Row 1, Chest").fill("38");
    await page.getByLabel("Row 2, Size").fill("L");
    await page.getByLabel("Row 2, Chest").fill("40");
    await saveDialog(page);

    await expect(page.getByRole("heading", { name: chart })).toBeVisible();
    await expect(page.getByText("2 columns x 2 rows").first()).toBeVisible();

    await page.getByRole("button", { name: `Edit ${chart}` }).click();
    await expect(page.getByLabel("Row 2, Chest")).toHaveValue("40");
  });
});

test.describe("Bulk import", () => {
  test.describe.configure({ mode: "serial" });
  test.skip(({ viewport }) => (viewport?.width ?? 1280) < 1024, "driven on desktop; phones are covered by the UI checks");

  const sku = `E2E-${stamp.toUpperCase()}`;
  const badSku = `E2EBAD-${stamp.toUpperCase()}`;

  test("a check saves nothing, the real import creates the product", async ({ page, context }) => {
    await signInViaBff(context, admin().email, admin().password);
    await page.goto("/admin/products");
    await page.getByRole("button", { name: "Import" }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("heading", { name: "Import products" })).toBeVisible();

    const csv = ["name_en,category,regular_price,sku,color_name,size", `E2E Import Tee ${stamp},Clothing > T-shirts,999,${sku}-M,Red,M`, `E2E Import Tee ${stamp},,,${sku}-L,Red,L`].join("\n");
    await dialog.locator("#import-file").setInputFiles({ name: "products.csv", mimeType: "text/csv", buffer: Buffer.from(csv, "utf8") });
    await expect(dialog.getByText("Chosen file: products.csv")).toBeVisible();

    await dialog.getByRole("button", { name: "Check the file (nothing is saved)" }).click();
    await expect(dialog.getByText("The file is fine.")).toBeVisible();
    await expect(dialog.getByText("Products added")).toBeVisible();
    expect(await productCount(context, sku)).toBe(0); // a check saves nothing

    await dialog.getByRole("button", { name: "Import now" }).click();
    await expect(dialog.getByRole("heading", { name: "Import finished" })).toBeVisible();
    expect(await productCount(context, sku)).toBe(1);

    await dialog.getByRole("button", { name: "Close" }).first().click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
  });

  test("a file with an error is rejected, shows the row and column and saves nothing", async ({ page, context }) => {
    await signInViaBff(context, admin().email, admin().password);
    await page.goto("/admin/products");
    await page.getByRole("button", { name: "Import" }).click();
    const dialog = page.getByRole("dialog");

    const csv = ["name_en,category,regular_price,sale_price,sku", `E2E Bad Tee ${stamp},Clothing > T-shirts,500,900,${badSku}`].join("\n");
    await dialog.locator("#import-file").setInputFiles({ name: "bad.csv", mimeType: "text/csv", buffer: Buffer.from(csv, "utf8") });
    await dialog.getByRole("button", { name: "Check the file (nothing is saved)" }).click();

    await expect(dialog.getByText("Nothing was saved").first()).toBeVisible();
    const table = dialog.locator("table");
    await expect(table.getByRole("cell", { name: "Row 2" })).toBeVisible();
    await expect(table.getByRole("cell", { name: "sale_price" })).toBeVisible();
    await expect(table.getByText("Error", { exact: true }).first()).toBeVisible();
    await expect(dialog.getByRole("button", { name: "Import now" })).toHaveCount(0);
    expect(await productCount(context, badSku)).toBe(0);
  });
});
