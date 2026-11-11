import { test, expect, type APIRequestContext, type Locator, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { signInViaBff } from "./support/ui";

/**
 * Step 6-e gate for inventory, driven through the real screens on the real API: stock numbers, an adjustment with its rules, the low-stock
 * level, the ledger, a transfer from Head Office to a shop (request, approve, ship, receive) with stock checked on both sides, a stock
 * count (start, enter, submit, approve), and what a role with view-only stock rights sees.
 *
 * Test data is created through the BFF (the same API the screens use), so each test starts from a product of its own.
 */
const state = () => loadState();
const stamp = () => `${Date.now().toString(36)}${Math.floor(Math.random() * 1296).toString(36)}`;

type Seeded = { name: string; sku: string; productId: string; variantId: string };

async function post<T>(request: APIRequestContext, path: string, data: unknown, status = [200, 201]): Promise<T> {
  const response = await request.post(`/api/bff/api/v1/${path}`, { data });
  expect(status, `${path}: ${response.status()} ${await response.text()}`).toContain(response.status());
  return (await response.json()) as T;
}

/** A new product with one variant and, optionally, opening stock in some branches. */
async function seedVariant(request: APIRequestContext, label: string, stock: { branchId: string; quantity: number }[]): Promise<Seeded> {
  const tree = await (await request.get("/api/bff/api/v1/catalog/categories")).json();
  const categoryId = (tree as { id: string }[])[0]!.id;
  const tag = stamp();
  const name = `E2E ${label} ${tag}`;
  const product = await post<{ id: string }>(request, "catalog/products", { nameEn: name, categoryId, regularPrice: 500, targetGroup: "Unisex", isNew: false, isFeatured: false, isBestSeller: false });
  const sku = `E2E-${label}-${tag}`.toUpperCase();
  const variant = await post<{ id: string }>(request, `catalog/products/${product.id}/variants`, { sku, colorName: "Red", size: "M" });
  for (const s of stock) {
    await post(request, "inventory/adjustments", { branchId: s.branchId, reason: "OpeningStock", lines: [{ variantId: variant.id, quantityChange: s.quantity }] });
  }
  return { name, sku, productId: product.id, variantId: variant.id };
}

async function pick(page: Page, trigger: Locator, option: string | RegExp) {
  await trigger.click();
  await page.getByRole("option", { name: option }).first().click();
  // Wait until the list has closed: a second dropdown opened while this one still fades out would offer the same options twice.
  await expect(page.getByRole("option")).toHaveCount(0);
}

/** The row of one SKU in one branch of the stock table. */
function stockRow(page: Page, sku: string, branchCode: string): Locator {
  return page
    .getByRole("row")
    .filter({ has: page.getByRole("cell", { name: branchCode, exact: true }) })
    .filter({ hasText: sku });
}

async function searchStock(page: Page, sku: string) {
  await page.goto("/admin/inventory");
  await expect(page.getByRole("heading", { level: 1, name: "Stock by branch" })).toBeVisible();
  await page.getByLabel("Search stock").fill(sku);
  await expect(page.getByRole("row").filter({ hasText: sku }).first()).toBeVisible();
}

const ON_HAND = 2;

test.describe("Inventory screens", () => {
  test.describe.configure({ mode: "serial" });

  test.beforeEach(async ({ context }) => {
    await signInViaBff(context, state().admin.email, state().admin.password);
  });

  test("the stock list shows the sample catalog per branch and filters by branch and low stock", async ({ page }) => {
    await page.goto("/admin/inventory");
    await expect(page.getByRole("heading", { level: 1, name: "Stock by branch" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Classic Cotton T-Shirt" }).first()).toBeVisible();
    await expect(page.getByRole("cell", { name: "HO", exact: true }).first()).toBeVisible();

    await page.getByLabel("Search stock").fill("JV-TSH-BLA-M");
    await expect(page.getByRole("row").filter({ hasText: "JV-TSH-BLA-M" })).toHaveCount(2); // Head Office and Online Fulfilment

    await page.getByLabel("Search stock").fill("");
    await page.getByRole("switch", { name: "Only low stock" }).click();
    await expect(page.getByText("Low", { exact: true }).first()).toBeVisible(); // the sample catalog has low-stock rows in Head Office
  });

  test("an adjustment follows the reason, refuses removing reserved stock, and the ledger and the low-stock level show it", async ({ page, context }) => {
    const item = await seedVariant(context.request, "ADJ", [{ branchId: state().branches.ho.id, quantity: 10 }]);
    await searchStock(page, item.sku);
    const row = stockRow(page, item.sku, "HO");
    await expect(row.getByRole("cell").nth(ON_HAND)).toHaveText("10");

    // Damaged units leave the shelf.
    await row.getByRole("button", { name: `Adjust stock of ${item.sku} in HO` }).click();
    await expect(page.getByRole("heading", { name: "Adjust stock" })).toBeVisible();
    await pick(page, page.locator("#adj-reason"), /Damaged/);
    await page.locator("#adj-qty").fill("3");
    await page.getByLabel("Note").fill("Water damage");
    await expect(page.getByText("On hand will become 7.")).toBeVisible();
    await page.getByRole("button", { name: "Save adjustment" }).click();
    await expect(page.getByText("Stock adjusted.").first()).toBeVisible();
    await expect(row.getByRole("cell").nth(ON_HAND)).toHaveText("7");

    // More than is there cannot be removed.
    await row.getByRole("button", { name: `Adjust stock of ${item.sku} in HO` }).click();
    await pick(page, page.locator("#adj-reason"), /Lost/);
    await page.locator("#adj-qty").fill("99");
    await page.getByRole("button", { name: "Save adjustment" }).click();
    await expect(page.getByText("Only 7 can be removed", { exact: false })).toBeVisible();
    await page.getByRole("button", { name: "Cancel" }).click();

    // The low-stock level turns on the warning.
    await row.getByRole("button", { name: `Set the low-stock level of ${item.sku} in HO` }).click();
    await page.locator("#rl-level").fill("8");
    await page.getByRole("button", { name: "Save level" }).click();
    await expect(page.getByText("Low-stock level saved.").first()).toBeVisible();
    await expect(row.getByText("Low", { exact: true })).toBeVisible();

    // The ledger has the entry, with the note and who did it.
    await page.goto("/admin/inventory/movements");
    await expect(page.getByRole("heading", { level: 1, name: "Stock movements" })).toBeVisible();
    await pick(page, page.getByRole("combobox", { name: /^Reason/ }), "Damaged");
    const entry = page.getByRole("row").filter({ hasText: item.sku });
    await expect(entry).toHaveCount(1); // only the damaged entry is left once the filter has applied
    await expect(entry).toBeVisible();
    await expect(entry).toContainText("-3");
    await expect(entry).toContainText("Water damage");
    await expect(entry).toContainText(state().admin.name);
  });

  test("a transfer from Head Office to a shop moves the stock on both sides", async ({ page, context }) => {
    const item = await seedVariant(context.request, "TRF", [{ branchId: state().branches.ho.id, quantity: 10 }]);
    const shop = state().branches.a;

    await page.goto("/admin/inventory/transfers/new");
    await expect(page.getByRole("heading", { level: 1, name: "New transfer" })).toBeVisible();
    await pick(page, page.locator("#tr-from"), /^HO ·/);
    await pick(page, page.locator("#tr-to"), new RegExp(`^${shop.code} ·`));
    await page.locator("#tr-search").fill(item.name);
    await page.getByRole("button", { name: new RegExp(item.name) }).click();
    await page.getByRole("button", { name: `Add ${item.sku}` }).click();
    await page.getByRole("textbox", { name: "Quantity" }).fill("4");
    await page.getByLabel("Note").fill("Shop restock");
    await page.getByRole("button", { name: "Request transfer" }).click();

    await expect(page).toHaveURL(/\/admin\/inventory\/transfers\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { level: 1, name: /^TR-/ })).toBeVisible();
    await expect(page.getByText("Shop restock")).toBeVisible();

    await page.getByRole("button", { name: "Approve" }).click();
    await expect(page.getByText("Transfer approved.").first()).toBeVisible();
    await page.getByRole("button", { name: "Ship", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Ship this transfer?" })).toBeVisible();
    await page.getByRole("button", { name: "Ship now" }).click();
    await expect(page.getByText("Transfer shipped", { exact: false }).first()).toBeVisible();
    await expect(page.getByRole("button", { name: "Mark as received" })).toBeVisible();
    await page.getByRole("button", { name: "Mark as received" }).click();
    await expect(page.getByText("Transfer received", { exact: false }).first()).toBeVisible();
    await expect(page.getByText("Received: stock entered the receiving branch")).toBeVisible();
    await expect(page.getByRole("button", { name: "Mark as received" })).toHaveCount(0);

    // 10 - 4 in Head Office, 4 in the shop.
    await searchStock(page, item.sku);
    await expect(stockRow(page, item.sku, "HO").getByRole("cell").nth(ON_HAND)).toHaveText("6");
    await expect(stockRow(page, item.sku, shop.code).getByRole("cell").nth(ON_HAND)).toHaveText("4");

    // The transfer is in the list.
    await page.goto("/admin/inventory/transfers");
    await expect(page.getByRole("heading", { level: 1, name: "Stock transfers" })).toBeVisible();
    await expect(page.getByRole("link", { name: /^TR-/ }).first()).toBeVisible();
  });

  test("a rejected request moves nothing and a transfer in transit can be cancelled, putting the goods back", async ({ page, context }) => {
    const item = await seedVariant(context.request, "CAN", [{ branchId: state().branches.ho.id, quantity: 5 }]);
    const shop = state().branches.b;
    const request = async (quantity: number) =>
      (await post<{ id: string }>(context.request, "inventory/transfers", { fromBranchId: state().branches.ho.id, toBranchId: shop.id, note: null, lines: [{ variantId: item.variantId, quantity }] })).id;

    const rejectedId = await request(2);
    await page.goto(`/admin/inventory/transfers/${rejectedId}`);
    await page.getByRole("button", { name: "Reject", exact: true }).click();
    await page.getByLabel("Reason").fill("Keeping it");
    await page.getByRole("button", { name: "Reject request" }).click();
    await expect(page.getByText("Transfer rejected.").first()).toBeVisible();
    await expect(page.getByText("Keeping it")).toBeVisible();

    const transitId = await request(3);
    await post(context.request, `inventory/transfers/${transitId}/approve`, {});
    await post(context.request, `inventory/transfers/${transitId}/ship`, {});
    await searchStock(page, item.sku);
    await expect(stockRow(page, item.sku, "HO").getByRole("cell").nth(ON_HAND)).toHaveText("2");

    await page.goto(`/admin/inventory/transfers/${transitId}`);
    await page.getByRole("button", { name: "Cancel transfer" }).click();
    await expect(page.getByText("Cancelling puts them back", { exact: false })).toBeVisible();
    await page.getByRole("button", { name: "Cancel transfer" }).last().click();
    await expect(page.getByText("Transfer cancelled.").first()).toBeVisible();

    await searchStock(page, item.sku);
    await expect(stockRow(page, item.sku, "HO").getByRole("cell").nth(ON_HAND)).toHaveText("5");
  });

  test("a stock count books only what was counted: start, enter, submit, approve", async ({ page, context }) => {
    const item = await seedVariant(context.request, "CNT", [{ branchId: state().branches.ho.id, quantity: 10 }]);

    await page.goto("/admin/inventory/counts");
    await expect(page.getByRole("heading", { level: 1, name: "Stock counts" })).toBeVisible();
    await page.getByRole("button", { name: "Start a count" }).click();
    await expect(page.getByRole("heading", { name: "Start a stock count" })).toBeVisible();
    await pick(page, page.locator("#sc-branch"), /^HO ·/);
    await page.locator("#sc-search").fill(item.name);
    await page.getByRole("button", { name: new RegExp(item.name) }).click();
    await page.getByRole("button", { name: `Add ${item.sku}` }).click();
    await expect(page.getByText("Only 1 chosen variant.")).toBeVisible();
    await page.getByRole("button", { name: "Start count" }).click();

    await expect(page).toHaveURL(/\/admin\/inventory\/counts\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { level: 1, name: /^SC-/ })).toBeVisible();
    await expect(page.getByRole("button", { name: "Submit for review" })).toBeDisabled(); // nothing counted yet

    await page.getByLabel(`Counted quantity for ${item.sku}`).fill("7");
    await expect(page.getByText("-3", { exact: true }).first()).toBeVisible(); // the difference shows at once
    await page.getByRole("button", { name: "Save counts" }).click();
    await expect(page.getByText("Counted quantities saved.").first()).toBeVisible();
    await page.getByRole("button", { name: "Submit for review" }).click();
    await expect(page.getByText("Count submitted for review.").first()).toBeVisible();
    await expect(page.getByText("waiting for review", { exact: false }).first()).toBeVisible();

    await page.getByRole("button", { name: "Approve", exact: true }).click();
    await expect(page.getByRole("heading", { name: "Approve this count?" })).toBeVisible();
    await page.getByRole("button", { name: "Approve and correct stock" }).click();
    await expect(page.getByText("Count approved.", { exact: false }).first()).toBeVisible();
    await expect(page.getByText("Approved on", { exact: false })).toBeVisible();

    await searchStock(page, item.sku);
    await expect(stockRow(page, item.sku, "HO").getByRole("cell").nth(ON_HAND)).toHaveText("7");
  });

  test("a role that can only view stock sees the numbers but no actions, and the transfer and count pages answer 403", async ({ page, context }) => {
    await context.clearCookies();
    await signInViaBff(context, state().orderManager.email, state().orderManager.password);

    await page.goto("/admin/inventory");
    await expect(page.getByRole("heading", { level: 1, name: "Stock by branch" })).toBeVisible();
    await expect(page.getByRole("row").nth(1)).toBeVisible();
    await expect(page.getByRole("button", { name: /^Adjust stock of/ })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /^Set the low-stock level/ })).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Transfers" })).toHaveCount(0);
    await expect(page.getByRole("link", { name: "Stock counts" })).toHaveCount(0);

    await page.goto("/admin/inventory/transfers");
    await expect(page).toHaveURL(/\/forbidden/);
    await page.goto("/admin/inventory/counts");
    await expect(page).toHaveURL(/\/forbidden/);
  });
});
