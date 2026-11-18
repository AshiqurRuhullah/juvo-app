import { test, expect, type Locator, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { makePng } from "./support/png";
import { freshMobile, deliveredOrder, staffWithPermissions, stockedVariants } from "./support/returns";
import { createPromotion, dhakaLocalIn, findPromotionByName, getPromotion, promotionAction, reportOf, uniqueName, uniqueProduct } from "./support/promotions";
import { signInViaBff } from "./support/ui";

/**
 * Promotions admin (module Z) in the browser against the real API: the editor for every type, drafts, preview, activate, switch off,
 * archive, duplicate, Dhaka time, stacking explanation, validation and API error texts, campaign pictures, permissions and reports.
 */
async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

async function pick(page: Page, trigger: Locator, option: string | RegExp) {
  await trigger.click();
  await page.getByRole("option", { name: option }).first().click();
}

const state = (page: Page) => page.getByTestId("promotion-state").getByTestId("promotion-status");

async function chooseProduct(page: Page, role: string, name: string) {
  await page.getByTestId(`targets-add-${role}`).click();
  await page.locator(`#promo-product-${role}`).fill(name);
  await page.getByTestId("product-result").filter({ hasText: name }).first().click();
}

test.describe("Promotions", () => {
  test("a flash sale is created, previewed, activated, switched off, archived and copied", async ({ page }) => {
    test.setTimeout(120_000);
    const product = await uniqueProduct(0);
    const name = uniqueName("Eid flash");
    await adminSignIn(page);
    await page.goto("/admin/promotions");
    await expect(page.getByRole("heading", { level: 1, name: "Promotions" })).toBeVisible();
    await page.getByTestId("new-promotion").click();
    await expect(page).toHaveURL(/\/admin\/promotions\/new$/);

    await page.locator("#nameEn").fill(name);
    await page.locator("#endsAt").fill(dhakaLocalIn(48));
    await page.locator("#discountValue").fill("20");
    await chooseProduct(page, "Main", product.nameEn);
    await expect(page.getByTestId("target-row")).toContainText(product.nameEn);
    await page.getByTestId("promotion-save").click();
    await expect(page).toHaveURL(/\/admin\/promotions\/[0-9a-f-]{36}$/);
    await expect(state(page)).toHaveText("Draft");

    const saved = await findPromotionByName(name);
    expect(saved?.type).toBe("FlashSale");
    expect(saved?.targets).toHaveLength(1);
    expect(saved?.discountValue).toBe(20);

    // The preview prices a sample cart with this draft alone.
    const panel = page.getByTestId("preview-panel");
    await panel.locator("#preview-search").fill(product.nameEn);
    await panel.getByRole("button", { name: new RegExp(product.nameEn) }).first().click();
    await panel.getByRole("button", { name: `Add ${product.variants[0]!.sku}` }).click();
    await expect(page.getByTestId("sample-line")).toHaveCount(1);
    await page.getByTestId("preview-run").click();
    await expect(page.getByTestId("preview-result")).toBeVisible();
    await expect(page.getByTestId("preview-result").locator("s")).toBeVisible();
    await expect(page.getByTestId("preview-applied")).toContainText(name);

    // Activate (asks first), then switch off, archive and copy.
    await page.getByTestId("promotion-activate").click();
    await page.getByRole("dialog").getByRole("button", { name: "Activate" }).click();
    await expect(state(page)).toHaveText("Active");
    expect((await getPromotion(saved!.id)).status).toBe("Active");
    await expect(page.getByTestId("promotion-activate")).toHaveCount(0);

    await page.getByTestId("promotion-deactivate").click();
    await page.getByRole("dialog").getByRole("button", { name: "Switch off" }).click();
    await expect(state(page)).toHaveText("Draft");

    await page.getByTestId("promotion-duplicate").click();
    await expect(page).not.toHaveURL(new RegExp(saved!.id));
    await expect(page.locator("#nameEn")).toHaveValue(`${name} (copy)`);
    await expect(state(page)).toHaveText("Draft");

    await page.goto(`/admin/promotions/${saved!.id}`);
    await page.getByTestId("promotion-archive").click();
    await page.getByRole("dialog").getByRole("button", { name: "Archive" }).click();
    await expect(state(page)).toHaveText("Archived");
    await expect(page.getByText("This promotion is archived and cannot be changed")).toBeVisible();
    await expect(page.locator("#nameEn")).toBeDisabled();
    await expect(page.getByTestId("promotion-save")).toHaveCount(0);
    await expect(page.getByTestId("promotion-archive")).toHaveCount(0);
    await expect(page.getByTestId("promotion-duplicate")).toBeVisible();
  });

  test("empty and wrong input is explained next to the field before anything is sent", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/promotions/new");
    await page.getByTestId("promotion-save").click();
    await expect(page.getByText("Enter the promotion name.")).toBeVisible();
    await expect(page.getByText("A flash sale needs an end time.")).toBeVisible();
    await expect(page.getByText("Enter a value above 0.")).toBeVisible();
    await expect(page.getByTestId("targets-error")).toContainText("Choose at least one product");
    await expect(page.getByTestId("promotion-failure")).toContainText("Please check the marked fields.");

    await page.locator("#nameEn").fill("Wrong ends");
    await page.locator("#startsAt").fill("2026-12-02T10:00");
    await page.locator("#endsAt").fill("2026-12-01T10:00");
    await page.locator("#discountValue").fill("150");
    await page.getByTestId("promotion-save").click();
    await expect(page.getByText("The end must not be before the start.")).toBeVisible();
    await expect(page.getByText("A percentage can be at most 100.")).toBeVisible();

    // Another type shows its own fields and its own checks.
    await pick(page, page.locator("#pf-type"), "Bundle or combo");
    await expect(page.locator("#bundlePrice")).toBeVisible();
    await expect(page.locator("#discountValue")).toHaveCount(0);
    await page.getByTestId("promotion-save").click();
    await expect(page.getByText("Enter the combo price (0 or more).")).toBeVisible();
    await expect(page.getByTestId("targets-error")).toContainText("at least two units");
  });

  test("the schedule is typed as Dhaka time and the API stores it with +06:00", async ({ page }) => {
    const product = await uniqueProduct(0);
    const name = uniqueName("Dhaka time");
    await adminSignIn(page);
    await page.goto("/admin/promotions/new");
    await expect(page.getByTestId("dhaka-note")).toContainText("Dhaka time");
    await page.locator("#nameEn").fill(name);
    await page.locator("#startsAt").fill("2026-12-01T10:00");
    await page.locator("#endsAt").fill("2026-12-01T18:00");
    await page.locator("#discountValue").fill("10");
    await chooseProduct(page, "Main", product.nameEn);
    await page.getByTestId("promotion-save").click();
    await expect(page).toHaveURL(/\/admin\/promotions\/[0-9a-f-]{36}$/);

    const saved = await findPromotionByName(name);
    expect(new Date(saved!.startsAt!).toISOString()).toBe("2026-12-01T04:00:00.000Z");
    expect(new Date(saved!.endsAt!).toISOString()).toBe("2026-12-01T12:00:00.000Z");
    // Reopened, the fields show the same Dhaka wall clock again.
    await page.reload();
    await expect(page.locator("#startsAt")).toHaveValue("2026-12-01T10:00");
    await expect(page.locator("#endsAt")).toHaveValue("2026-12-01T18:00");
  });

  test("bundle, buy X get Y and free delivery are created with their own fields", async ({ page }) => {
    test.setTimeout(120_000);
    const product = await uniqueProduct(0);
    await adminSignIn(page);

    // Bundle: one product, two units, a combo price.
    const bundle = uniqueName("Combo");
    await page.goto("/admin/promotions/new");
    await pick(page, page.locator("#pf-type"), "Bundle or combo");
    await page.locator("#nameEn").fill(bundle);
    await page.locator("#bundlePrice").fill("1000");
    await chooseProduct(page, "BundleItem", product.nameEn);
    await page.getByLabel(`Quantity of ${product.nameEn}`).fill("2");
    await page.getByTestId("promotion-save").click();
    await expect(page).toHaveURL(/\/admin\/promotions\/[0-9a-f-]{36}$/);
    const savedBundle = await findPromotionByName(bundle);
    expect(savedBundle?.type).toBe("Bundle");
    expect(savedBundle?.targets[0]).toMatchObject({ role: "BundleItem", quantity: 2, targetId: product.id });

    // Buy X get Y: everything in a category, buy 2 get 1 free.
    const buy = uniqueName("Buy two");
    await page.goto("/admin/promotions/new");
    await pick(page, page.locator("#pf-type"), "Buy X get Y");
    await page.locator("#nameEn").fill(buy);
    await page.getByTestId("targets-add-Buy").click();
    await page.getByTestId("kind-Buy-Category").click();
    await page.getByRole("combobox", { name: "Choose a category" }).click();
    await page.getByRole("option").first().click();
    await expect(page.getByTestId("targets-Buy").getByTestId("target-row")).toHaveCount(1);
    await page.getByTestId("promotion-save").click();
    await expect(page).toHaveURL(/\/admin\/promotions\/[0-9a-f-]{36}$/);
    const savedBuy = await findPromotionByName(buy);
    expect(savedBuy?.type).toBe("BuyXGetY");
    expect(savedBuy?.targets[0]).toMatchObject({ role: "Buy", kind: "Category" });

    // Free delivery: no targets, a cart value and one zone.
    const free = uniqueName("Free delivery");
    await page.goto("/admin/promotions/new");
    await pick(page, page.locator("#pf-type"), "Free delivery");
    await expect(page.getByTestId("targets-none")).toBeVisible();
    await page.locator("#nameEn").fill(free);
    await page.locator("#minCartValue").fill("2500");
    await page.getByTestId("zone-picker").getByRole("checkbox").first().click();
    await expect(page.getByTestId("stacking-explanation")).toContainText("judged on its own");
    await page.getByTestId("promotion-save").click();
    await expect(page).toHaveURL(/\/admin\/promotions\/[0-9a-f-]{36}$/);
    const savedFree = await findPromotionByName(free);
    expect(savedFree?.type).toBe("FreeDelivery");
    expect(savedFree?.targets).toHaveLength(0);
  });

  test("the stacking explanation follows the type and the choice", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/promotions/new");
    const box = page.getByTestId("stacking-explanation");
    await expect(box).toContainText("the lowest price wins");
    await expect(box).toContainText("A coupon is not added on top");
    await page.getByRole("radio", { name: /Also combine with a coupon/ }).click();
    await expect(box).toContainText("A coupon can be added on top");
    await expect(box).toContainText("Every promotion that applies must allow a coupon");
    await page.getByRole("radio", { name: /^Alone/ }).click();
    await expect(box).toContainText("A coupon is not combined with it either");
    await pick(page, page.locator("#pf-type"), "Bundle or combo");
    await expect(box).toContainText("priority order");
    await expect(box).toContainText("No other bundle or buy X get Y");
  });

  test("a festival campaign takes a clean address, a picture and answers a taken address in plain words", async ({ page }) => {
    test.setTimeout(120_000);
    const slug = `eid-${Math.random().toString(36).slice(2, 8)}`;
    await createPromotion({ nameEn: uniqueName("Existing campaign"), type: "FestivalCampaign", slug, titleEn: "Existing", bannerSlot: "HomeTop", targets: [] });
    await adminSignIn(page);
    await page.goto("/admin/promotions/new");
    await pick(page, page.locator("#pf-type"), "Festival campaign");
    const name = uniqueName("Eid page");
    await page.locator("#nameEn").fill(name);
    await page.locator("#slug").fill("Eid Page!");
    await expect(page.locator("#slug")).toHaveValue("eidpage");
    await page.locator("#slug").fill(slug);
    await page.locator("#titleEn").fill("Eid collection");
    await expect(page.getByText("Pictures can be added after the draft is saved.")).toBeVisible();

    // The address is taken: the API's code is explained in words and the field is marked.
    await page.getByTestId("promotion-save").click();
    await expect(page.getByTestId("promotion-failure")).toContainText("Another campaign already uses this web address");
    await expect(page.getByTestId("promotion-failure")).not.toContainText("PROMO.");

    const own = `${slug}-two`;
    await page.locator("#slug").fill(own);
    await page.getByTestId("promotion-save").click();
    await expect(page).toHaveURL(/\/admin\/promotions\/[0-9a-f-]{36}$/);
    await expect(page.getByTestId("promotion-images")).toBeVisible();

    // A small picture is refused before it is sent; a real one is stored and shown.
    await page.getByTestId("upload-hero").setInputFiles({ name: "small.png", mimeType: "image/png", buffer: makePng(600, 600) });
    await expect(page.getByTestId("image-error")).toContainText("at least 1000 pixels");
    await page.getByTestId("upload-hero").setInputFiles({ name: "hero.png", mimeType: "image/png", buffer: makePng(1200, 1200) });
    await expect(page.getByTestId("image-hero-preview")).toBeVisible();
    await expect(page.getByTestId("image-error")).toHaveCount(0);
    const saved = await findPromotionByName(name);
    expect(saved?.heroImageUrl).toBeTruthy();
    expect(saved?.slug).toBe(own);
  });

  test("the list searches, filters by status and type and links to the editor", async ({ page }) => {
    const name = uniqueName("Listed promo");
    const made = await createPromotion({ nameEn: name, type: "FreeDelivery", minCartValue: 1500, targets: [] });
    await adminSignIn(page);
    await page.goto("/admin/promotions");
    await page.getByPlaceholder("Search by name or campaign address").fill(name);
    const row = page.getByRole("link", { name, exact: true }).filter({ visible: true });
    await expect(row).toHaveCount(1);
    await expect(page.getByTestId("promotion-status").filter({ visible: true }).first()).toHaveText("Draft");
    await expect(page.getByTestId("promotion-summary")).toBeVisible();

    await page.getByRole("combobox", { name: /^Status/ }).click();
    await page.getByRole("option", { name: "Active" }).click();
    await expect(page.getByRole("link", { name, exact: true })).toHaveCount(0);
    await page.getByRole("button", { name: "Clear filters" }).click();
    await page.getByPlaceholder("Search by name or campaign address").fill(name);
    await page.getByRole("combobox", { name: /^Type/ }).click();
    await page.getByRole("option", { name: "Free delivery" }).click();
    await expect(row).toHaveCount(1);
    await row.click();
    await expect(page).toHaveURL(new RegExp(made.id));
    await expect(page.getByRole("heading", { level: 1, name })).toBeVisible();
  });

  test("an unknown promotion says so and a missing permission is refused", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/promotions/00000000-0000-7000-8000-000000000000");
    await expect(page.getByRole("heading", { level: 1, name: "Promotion not found" })).toBeVisible();

    const viewer = await staffWithPermissions(["promotions.promotion.view"]);
    const other = page.context();
    await other.clearCookies();
    await signInViaBff(other, viewer.email, viewer.password);
    await page.goto("/admin/promotions/new");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("a view-only person sees promotions without any control to change them", async ({ page }) => {
    const made = await createPromotion({ nameEn: uniqueName("Read only"), type: "FreeDelivery", minCartValue: 100, targets: [] });
    const viewer = await staffWithPermissions(["promotions.promotion.view"]);
    await signInViaBff(page.context(), viewer.email, viewer.password);
    await page.goto("/admin/promotions");
    await expect(page.getByRole("heading", { level: 1, name: "Promotions" })).toBeVisible();
    await expect(page.getByTestId("new-promotion")).toHaveCount(0);
    await page.goto(`/admin/promotions/${made.id}`);
    await expect(page.getByText("You can view this promotion but not change it.")).toBeVisible();
    await expect(page.locator("#nameEn")).toBeDisabled();
    for (const id of ["promotion-save", "promotion-activate", "promotion-deactivate", "promotion-archive", "promotion-duplicate"]) {
      await expect(page.getByTestId(id)).toHaveCount(0);
    }
  });

  test("edit without delete can activate and copy but not archive", async ({ page }) => {
    const made = await createPromotion({ nameEn: uniqueName("No archive"), type: "FreeDelivery", minCartValue: 100, targets: [] });
    const staff = await staffWithPermissions(["promotions.promotion.view", "promotions.promotion.edit", "promotions.promotion.create"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto(`/admin/promotions/${made.id}`);
    await expect(page.getByTestId("promotion-save")).toBeVisible();
    await expect(page.getByTestId("promotion-activate")).toBeVisible();
    await expect(page.getByTestId("promotion-duplicate")).toBeVisible();
    await expect(page.getByTestId("promotion-archive")).toHaveCount(0);
  });

  test("results show orders, units, revenue and discount cost, and say returns are not subtracted", async ({ page }) => {
    test.setTimeout(180_000);
    const [variant] = await stockedVariants(3, 1, 2);
    const name = uniqueName("Report promo");
    const made = await createPromotion({ nameEn: name, type: "ProductDiscount", discountKind: "Percent", discountValue: 10, targets: [{ kind: "Product", targetId: variant!.productId, role: "Main", quantity: 1 }] });
    await promotionAction(made.id, "activate");
    try {
      await deliveredOrder([{ variantId: variant!.id, quantity: 2 }], freshMobile());
      const report = await reportOf(made.id);
      expect(Number(report.orders)).toBe(1);
      expect(Number(report.units)).toBe(2);
      expect(Number(report.discountCost)).toBeGreaterThan(0);

      await adminSignIn(page);
      await page.goto(`/admin/promotions/${made.id}`);
      const results = page.getByTestId("promotion-results");
      await expect(results.getByTestId("result-orders")).toContainText("1");
      await expect(results.getByTestId("result-units")).toContainText("2");
      await expect(results.getByTestId("result-discountCost")).toContainText(/[1-9]/);
      await expect(results.getByTestId("report-returns-note")).toContainText("Returns do not reduce these numbers");

      await page.goto("/admin/promotions");
      await page.getByRole("tab", { name: "Campaign report" }).click();
      await expect(page.getByTestId("campaign-report")).toBeVisible();
      await expect(page.getByTestId("report-returns-note")).toBeVisible();
      await expect(page.getByTestId("report-link").filter({ visible: true }).first()).toBeVisible();
    } finally {
      await promotionAction(made.id, "archive");
    }
  });
});
