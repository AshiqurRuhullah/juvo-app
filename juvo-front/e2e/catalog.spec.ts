import { test, expect, type Locator, type Page } from "@playwright/test";
import { api, loadState } from "./support/env";
import { makePng } from "./support/png";
import { signInViaBff } from "./support/ui";

/**
 * The Step 6-e gate for products, driven through the real screens against the real API: an admin adds a product, its live card
 * preview follows the form, colour and size variants are generated, stock is set per branch, pictures are uploaded, reordered and
 * given a focal point, and the product is published. A staff member with view-only rights cannot change anything.
 */
const state = () => loadState();
const stamp = Date.now().toString(36);

async function pick(page: Page, trigger: Locator, option: string | RegExp) {
  await trigger.click();
  await page.getByRole("option", { name: option }).first().click();
}

async function adminToken() {
  const login = await api<{ accessToken: string }>("POST", "/api/v1/auth/login", { identifier: state().admin.email, password: state().admin.password });
  return login.json.accessToken;
}

test.describe("Products", () => {
  test.describe.configure({ mode: "serial" });

  const name = `E2E Tee ${stamp}`;
  let productId = "";

  test("the list shows the sample catalog and finds a product by name", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "the long forms are driven on desktop; phones are covered by the UI checks");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin/products");
    await expect(page.getByRole("heading", { level: 1, name: "Products" })).toBeVisible();
    await expect(page.getByRole("link", { name: /Classic Cotton T-Shirt/ }).first()).toBeVisible();

    await page.getByLabel("Search products").fill("Denim Jacket");
    await expect(page.getByRole("link", { name: /Denim Jacket/ }).first()).toBeVisible();
    await expect(page.getByRole("link", { name: /Classic Cotton T-Shirt/ })).toHaveCount(0);

    await page.getByLabel("Search products").fill("sample"); // the sample products carry the tag "sample"
    await expect(page.getByRole("link", { name: /Classic Cotton T-Shirt/ }).first()).toBeVisible();
  });

  test("a new product shows mistakes next to the fields and a live card preview", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin/products/new");
    await expect(page.getByRole("heading", { level: 1, name: "New product" })).toBeVisible();

    await page.getByRole("button", { name: "Create product" }).click();
    await expect(page.getByText("Enter a name.")).toBeVisible();
    await expect(page.getByText("Choose a category.")).toBeVisible();
    await expect(page.getByText("Enter the regular price.")).toBeVisible();

    const preview = page.getByRole("complementary", { name: "Storefront preview" });
    await page.locator("#p-name").fill(name);
    await expect(preview.getByRole("heading", { name })).toBeVisible();
    await page.locator("#p-price").fill("1200");
    await page.locator("#p-sale").fill("1300");
    await page.getByRole("button", { name: "Create product" }).click();
    await expect(page.getByText("The sale price must be lower than the regular price.")).toBeVisible();

    await page.locator("#p-sale").fill("999");
    await expect(preview.getByText("-17%")).toBeVisible();
    await expect(preview.getByText("৳999")).toBeVisible();
    await page.locator("#p-new").click();
    await expect(preview.getByText("New", { exact: true })).toBeVisible();
  });

  test("creating the product leads to its variants", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin/products/new");
    await page.locator("#p-name").fill(name);
    await page.locator("#p-name-bn").fill("ই২ই টি-শার্ট");
    await pick(page, page.locator("#p-category"), /Clothing > T-shirts/);
    await page.locator("#p-price").fill("1200");
    await page.locator("#p-sale").fill("999");
    await page.locator("#p-tags").fill("e2e, tee");
    await page.getByRole("button", { name: "Create product" }).click();

    await expect(page).toHaveURL(/\/admin\/products\/[0-9a-f-]{36}\?tab=variants$/);
    productId = page.url().match(/products\/([0-9a-f-]{36})/)![1]!;
    await expect(page.getByRole("heading", { level: 1, name })).toBeVisible();
    await expect(page.getByText("No variants yet.")).toBeVisible();
    // Not publishable yet: the checklist says what is missing.
    await expect(page.getByText("Before this product can go live:")).toBeVisible();
    await expect(page.getByRole("button", { name: "Publish" })).toBeDisabled();
  });

  test("colour and size combinations are made in one go and stock is set per branch", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto(`/admin/products/${productId}?tab=variants`);

    await page.getByRole("button", { name: "Generate variants" }).click();
    await page.getByLabel("SKU start").fill(`E2E-${stamp.toUpperCase()}`);
    await page.getByLabel("Colours", { exact: true }).fill("Black, Red");
    await page.getByLabel("Sizes", { exact: true }).fill("M, L");
    await expect(page.getByText("4 variants will be made")).toBeVisible();
    await page.getByRole("dialog").getByRole("button", { name: "Generate variants" }).click();
    await expect(page.getByText("4 variants made.")).toBeVisible();

    const sku = `E2E-${stamp.toUpperCase()}-BLA-M`;
    await expect(page.getByText(sku).first()).toBeVisible();
    await expect(page.getByRole("table").getByText("Black").first()).toBeVisible();

    // Stock grid: set 8 in Head Office for the first variant.
    const group = page.getByRole("group", { name: new RegExp(sku) });
    await group.getByLabel(/^HO/).fill("8");
    await expect(page.getByText("1 change to save")).toBeVisible();
    await page.getByRole("button", { name: "Save stock" }).click();
    await expect(page.getByText("Stock saved.")).toBeVisible();

    const token = await adminToken();
    const stock = await api<{ items: { sku: string; branchCode: string; onHand: number }[] }>("GET", `/api/v1/inventory/stock?search=${sku}`, undefined, token);
    expect(stock.json.items.find((i) => i.branchCode === "HO")?.onHand).toBe(8);
    const movements = await api<{ items: { reason: string; quantityChange: number }[] }>("GET", `/api/v1/inventory/movements?productId=${productId}`, undefined, token);
    expect(movements.json.items[0]).toMatchObject({ reason: "OpeningStock", quantityChange: 8 });

    // One variant can be edited and removed again.
    const removable = `E2E-${stamp.toUpperCase()}-RED-L`;
    await page.getByRole("button", { name: `Edit ${removable}` }).click();
    await page.getByLabel("Barcode").fill("8901234567890");
    await page.getByRole("dialog").getByRole("button", { name: "Save" }).click();
    await expect(page.getByText("Variant saved.")).toBeVisible();
    await page.getByRole("button", { name: `Remove ${removable}` }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Remove" }).click();
    await expect(page.getByText("Variant removed.")).toBeVisible();
    await expect(page.getByText(removable)).toHaveCount(0);

    // A variant that still has stock cannot be removed: the API's refusal is shown in plain words.
    await page.getByRole("button", { name: `Remove ${sku}` }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Remove" }).click();
    await expect(page.getByText("This variant still has stock. Move or write it off first.").first()).toBeVisible();
  });

  test("pictures are uploaded, checked, reordered and given a focal point", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto(`/admin/products/${productId}?tab=pictures`);
    await expect(page.getByText("No pictures yet.")).toBeVisible();

    // A picture that is too small is refused in the browser, before it is sent.
    await page.locator("#picture-files").setInputFiles({ name: "tiny.png", mimeType: "image/png", buffer: makePng(300, 300) });
    await expect(page.getByText(/must be at least 1000 pixels/)).toBeVisible();

    await page.locator("#picture-files").setInputFiles([
      { name: "front.png", mimeType: "image/png", buffer: makePng(1000, 1250, [200, 30, 30], [30, 60, 200]) },
      { name: "back.png", mimeType: "image/png", buffer: makePng(1000, 1250, [30, 160, 60], [240, 200, 40]) },
    ]);
    await expect(page.getByText("2 pictures uploaded.")).toBeVisible();
    const pictures = page.getByTestId("picture");
    await expect(pictures).toHaveCount(2);
    await expect(pictures.first().getByText("Card picture")).toBeVisible();

    // Move the second picture first: it becomes the card picture.
    const token = await adminToken();
    const before = await api<{ images: { id: string; sortOrder: number }[] }>("GET", `/api/v1/catalog/products/${productId}`, undefined, token);
    const secondId = [...before.json.images].sort((a, b) => a.sortOrder - b.sortOrder)[1]!.id;
    await page.getByRole("button", { name: "Move picture 2 earlier" }).click();
    await expect.poll(async () => {
      const after = await api<{ images: { id: string; sortOrder: number }[] }>("GET", `/api/v1/catalog/products/${productId}`, undefined, token);
      return [...after.json.images].sort((a, b) => a.sortOrder - b.sortOrder)[0]!.id;
    }).toBe(secondId);

    // Focal point: click the left edge of the first picture's frame and save.
    await page.getByRole("button", { name: "Edit picture 1" }).click();
    const frame = page.getByTestId("focal-frame");
    await expect(frame).toBeVisible();
    await frame.click({ position: { x: 14, y: 140 } }); // near the left edge; waits until the dialog has stopped animating
    await page.getByLabel("Description for screen readers").fill("Front view");
    await page.getByRole("dialog").getByRole("button", { name: "Save" }).click();
    await expect(page.getByText("Picture saved.")).toBeVisible();
    await expect.poll(async () => {
      const after = await api<{ images: { sortOrder: number; focalX: number; altText: string | null }[] }>("GET", `/api/v1/catalog/products/${productId}`, undefined, token);
      const first = [...after.json.images].sort((a, b) => a.sortOrder - b.sortOrder)[0]!;
      return first.focalX < 0.2 && first.altText === "Front view";
    }).toBe(true);

    // Delete one picture (with confirmation).
    await page.getByRole("button", { name: "Delete picture 2" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Delete picture" }).click();
    await expect(page.getByText("Picture deleted.")).toBeVisible();
    await expect(pictures).toHaveCount(1);
  });

  test("the product is published, shown live in the preview, unpublished and archived", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto(`/admin/products/${productId}`);

    const preview = page.getByRole("complementary", { name: "Storefront preview" });
    await expect(preview.locator("img").first()).toBeVisible(); // the card picture from the picture tab
    await expect(preview.getByText("Black").first()).toBeAttached(); // colour swatch
    await expect(page.getByText("Everything needed is in place. You can publish it.")).toBeVisible();

    await page.getByRole("button", { name: "Publish" }).click();
    await expect(page.getByText("The product is now live.")).toBeVisible();
    await expect(page.getByText("This product is live on the storefront.")).toBeVisible();

    await page.getByRole("button", { name: "Unpublish" }).click();
    await expect(page.getByText("The product is back to draft.")).toBeVisible();

    await page.getByRole("button", { name: "Archive" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Archive" }).click();
    await expect(page.getByText("The product is archived.")).toBeVisible();
    await expect(page.getByText("This product is archived and cannot be changed.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Save" })).toHaveCount(0);
  });

  test("a product is duplicated as a draft with its variants", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin/products?search=Denim");
    await page.getByRole("link", { name: /Denim Jacket/ }).first().click();
    await expect(page.getByRole("heading", { level: 1, name: "Denim Jacket" })).toBeVisible();

    await page.getByRole("button", { name: "Duplicate" }).first().click();
    await page.getByLabel("Name of the copy").fill(`Denim Jacket copy ${stamp}`);
    await page.getByRole("dialog").getByRole("button", { name: "Duplicate" }).click();
    await expect(page.getByText("Copy created as a draft.")).toBeVisible();
    await expect(page.getByRole("heading", { level: 1, name: `Denim Jacket copy ${stamp}` })).toBeVisible();
    await expect(page.getByText("Draft", { exact: true }).first()).toBeVisible();
  });

  test("a staff member who may only view products cannot change anything", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    const token = await adminToken();
    const roleName = `Catalog Viewer ${stamp}`;
    const role = await api<{ id: string }>("POST", "/api/v1/ums/roles", { name: roleName, description: "view only", permissions: ["catalog.product.view", "catalog.category.view"] }, token);
    expect(role.status).toBe(201);
    const email = `viewer-${stamp}@e2e.test`;
    const created = await api<{ user: { id: string }; temporaryPassword: string }>("POST", "/api/v1/ums/users", { email, fullName: "Vera Viewer", assignments: [{ branchId: state().branches.ho.id, roleId: role.json.id }] }, token);
    expect(created.status).toBe(201);
    const limited = await api<{ accessToken: string }>("POST", "/api/v1/auth/login", { identifier: email, password: created.json.temporaryPassword });
    const password = "E2e-Viewer#Pass1";
    expect((await api("POST", "/api/v1/auth/change-password", { currentPassword: created.json.temporaryPassword, newPassword: password }, limited.json.accessToken)).status).toBe(200);

    await signInViaBff(context, email, password);
    await page.goto("/admin/products");
    await expect(page.getByRole("heading", { level: 1, name: "Products" })).toBeVisible();
    await expect(page.getByRole("link", { name: "New product" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: /Import|Export|Template/ })).toHaveCount(0);

    await page.getByRole("link", { name: /Classic Cotton T-Shirt/ }).first().click();
    await expect(page.getByText("You can look at this product but not change it.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Save" })).toHaveCount(0);
    await expect(page.getByRole("button", { name: "Publish" })).toHaveCount(0);

    await page.goto("/admin/products/new");
    await expect(page).toHaveURL(/\/forbidden/);
  });
});
