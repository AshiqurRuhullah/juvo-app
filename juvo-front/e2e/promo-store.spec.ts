import { test, expect, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { loadState } from "./support/env";
import { apiCheckout } from "./support/orders";
import { addToCart, createCoupon, pick, signInCustomer } from "./support/store";
import { signInViaBff } from "./support/ui";
import { call } from "./support/cod";
import { adminAccess } from "./support/returns";
import { addStock, allowPreorder, archivePromotion, flashSale, makePromotion, makeShopProduct, makeStaff, stamp, target, uploadPromoImage } from "./support/promo-store";

/**
 * Promotions and pre-orders as a customer and a staff member see them, driven through the real screens on the real API: a flash sale
 * with its countdown on the card, the product page, the cart and the checkout; a coupon that loses to better promotions; free delivery;
 * a festival campaign page with its home banner; a pre-order from the card to the queue, the allocation and the tracking page; and the
 * product editor's pre-order tab with its rules. Every product is created by the spec, so no other spec's prices or stock change.
 */
const state = () => loadState();

async function searchShop(page: Page, name: string) {
  await page.goto(`/shop?q=${encodeURIComponent(name)}`);
  const card = page.getByTestId("product-card").filter({ hasText: name });
  await expect(card).toHaveCount(1);
  return card;
}

test.describe("Promotions in the shop", () => {
  test("a flash sale shows its price, saving, label and countdown on the card, product page, cart and checkout", async ({ page, context }) => {
    const product = await makeShopProduct({ label: "Flash", price: 1000, stock: 8 });
    const promo = await flashSale(product, 20, 15 * 60_000, "Eid flash E2E");
    try {
      const card = await searchShop(page, product.name);
      await expect(card).toContainText("৳800");
      await expect(card.getByTestId("you-save")).toHaveText("You save ৳200");
      await expect(card.getByTestId("promo-badge")).toContainText("Eid flash E2E");
      await expect(card.getByTestId("countdown")).toHaveAttribute("data-state", "running");
      await expect(card.getByTestId("countdown")).toContainText(/Ends in/);

      await page.goto(`/product/${product.slug}`);
      await expect(page.getByTestId("product-price")).toContainText("৳800");
      await expect(page.getByTestId("product-price")).toContainText("৳1,000");
      // The first of each is the product itself (related products come after it and may carry offers of their own).
      await expect(page.getByTestId("you-save").first()).toBeVisible();
      await expect(page.getByTestId("countdown").first()).toHaveAttribute("data-state", "running");
      await expect(page.getByTestId("promo-badge").first()).toContainText("Eid flash E2E");

      await addToCart(context, product.variantId, 1);
      await page.goto("/cart");
      const line = page.getByTestId("cart-line");
      await expect(line.getByTestId("line-offer")).toContainText("৳1,000");
      await expect(line.getByTestId("line-offer")).toContainText("Eid flash E2E");
      await expect(page.getByTestId("summary-list")).toContainText("৳1,000");
      await expect(page.getByTestId("summary-promo-0")).toContainText("-৳200");
      await expect(page.getByTestId("why-this-price")).toContainText("Eid flash E2E");
      await expect(page.getByTestId("summary-total")).toHaveText("৳800");

      await page.goto("/checkout");
      await expect(page.getByTestId("amount-list")).toContainText("৳1,000");
      await expect(page.getByTestId("amount-promo-0")).toContainText("-৳200");
      await expect(page.getByTestId("why-this-price")).toBeVisible();
      await expect(page.getByTestId("amount-total")).toContainText("৳800");
    } finally {
      await archivePromotion(promo.id);
    }
  });

  test("the countdown stops at zero, says the offer ended and the price goes back", async ({ page }) => {
    const product = await makeShopProduct({ label: "Short", price: 1000, stock: 8 });
    const promo = await flashSale(product, 50, 9_000, "Quick flash E2E");
    try {
      await page.goto(`/product/${product.slug}`);
      await expect(page.getByTestId("product-price")).toContainText("৳500");
      await expect(page.getByTestId("countdown").first()).toHaveAttribute("data-state", "running");
      // At zero the countdown asks the API again; the sale is gone, so the price goes back and the countdown disappears with the offer.
      await expect(page.getByTestId("product-price")).toContainText("৳1,000", { timeout: 25_000 });
      await expect(page.getByTestId("product-price")).not.toContainText("৳500");
      // The block that holds the product's name and price (related products are outside it) shows no offer any more.
      const head = page.getByTestId("product-price").locator("xpath=..");
      await expect(head.getByTestId("countdown")).toHaveCount(0);
      await expect(head.getByTestId("you-save")).toHaveCount(0);
      await expect(head.getByTestId("promo-badge")).toHaveCount(0);
    } finally {
      await archivePromotion(promo.id);
    }
  });

  test("a coupon that loses to a better promotion stays on the cart with the API's reason", async ({ page, context }) => {
    const product = await makeShopProduct({ label: "Coupon", price: 1000, stock: 8 });
    const promo = await makePromotion({ nameEn: "Ten off E2E", type: "ProductDiscount", discountKind: "Percent", discountValue: 10, targets: [target(product.id)] });
    try {
      const code = await createCoupon("Fixed", 50);
      await addToCart(context, product.variantId, 1);
      await page.goto("/cart");
      await expect(page.getByTestId("summary-total")).toHaveText("৳900");
      await page.getByLabel("Coupon code").fill(code);
      await page.getByRole("button", { name: "Apply" }).click();

      await expect(page.getByTestId("coupon-kept")).toBeVisible();
      await expect(page.getByTestId("coupon-lost")).toContainText(code);
      await expect(page.getByTestId("coupon-lost")).toContainText(/better/i);
      await expect(page.getByTestId("summary-discount")).toHaveCount(0);
      await expect(page.getByTestId("summary-total")).toHaveText("৳900");
      await expect(page.getByTestId("why-this-price")).toBeVisible();

      await page.getByRole("button", { name: `Remove coupon ${code}` }).click();
      await expect(page.getByTestId("coupon-kept")).toHaveCount(0);
    } finally {
      await archivePromotion(promo.id);
    }
  });

  test("a free-delivery promotion is announced and waives the delivery charge", async ({ page, context }) => {
    const product = await makeShopProduct({ label: "Freedel", price: 600, stock: 8 });
    const promo = await makePromotion({ nameEn: "Free delivery E2E", type: "FreeDelivery", minCartValue: 0, stackMode: "StacksWithCoupon" }, { endsInMs: 10 * 60_000 });
    try {
      await addToCart(context, product.variantId, 1);
      await page.goto("/cart");
      await expect(page.getByTestId("free-delivery-offer")).toContainText("Free delivery E2E");
      await pick(page, page.getByLabel("Division"), "Dhaka");
      await pick(page, page.getByLabel("District"), "Dhaka");
      const quote = page.getByTestId("delivery-quote");
      await expect(quote).toContainText("Free delivery E2E");
      await expect(quote.getByTestId("delivery-fee")).toHaveText("Free");
      await expect(page.getByTestId("summary-delivery")).toHaveText("Free");
    } finally {
      await archivePromotion(promo.id);
    }
  });

  test("a festival campaign has its page and home banner, and both go when it ends", async ({ page }) => {
    const product = await makeShopProduct({ label: "Fest", price: 1000, stock: 8 });
    const slug = `eid-${stamp()}`;
    const promo = await makePromotion(
      {
        nameEn: "Eid campaign E2E",
        type: "FestivalCampaign",
        slug,
        titleEn: "Eid Collection E2E",
        titleBn: "ঈদ কালেকশন",
        bodyEn: "Fresh looks for the festival.\n\nFree gift wrapping.",
        bodyBn: "উৎসবের নতুন সাজ।",
        bannerSlot: "HomeTop",
        discountKind: "Percent",
        discountValue: 10,
        targets: [target(product.id)],
      },
      { activate: false, endsInMs: 30 * 60_000 },
    );
    await uploadPromoImage(promo.id, "hero");
    await uploadPromoImage(promo.id, "banner");
    await call("POST", `/api/v1/promotions/${promo.id}/activate`, { token: await adminAccess(), expect: 200 });
    try {
      await page.goto("/");
      const banner = page.getByTestId("promo-banner").filter({ has: page.locator(`a[href="/campaign/${slug}"]`) });
      await expect(banner).toHaveCount(1);
      await expect(banner).toContainText("Eid Collection E2E");
      await banner.getByRole("link").click();

      await expect(page).toHaveURL(new RegExp(`/campaign/${slug}$`));
      await expect(page.getByTestId("campaign-title")).toHaveText("Eid Collection E2E");
      await expect(page.getByTestId("campaign-body")).toContainText("Free gift wrapping.");
      await expect(page.getByTestId("campaign-hero").getByTestId("countdown")).toHaveAttribute("data-state", "running");
      const card = page.getByTestId("product-card").filter({ hasText: product.name });
      await expect(card).toHaveCount(1);
      await expect(card).toContainText("৳900");

      // The page follows the language.
      await context_setLocale(page, "bn");
      await page.goto(`/campaign/${slug}`);
      await expect(page.getByTestId("campaign-title")).toHaveText("ঈদ কালেকশন");
    } finally {
      await context_setLocale(page, "en");
      await archivePromotion(promo.id);
    }
    await page.goto(`/campaign/${slug}`);
    await expect(page.getByText("We could not find that")).toBeVisible();
    await page.goto("/");
    await expect(page.locator(`a[href="/campaign/${slug}"]`)).toHaveCount(0);
  });
});

async function context_setLocale(page: Page, locale: "en" | "bn") {
  await page.context().addCookies([{ name: "juvo_locale", value: locale, url: page.url().startsWith("http") ? page.url() : "http://localhost" }]);
}

test.describe("Pre-orders", () => {
  test("a pre-order is labelled everywhere, waits as an order, is served from the queue and the customer sees why", async ({ page, context, browser }) => {
    const product = await makeShopProduct({ label: "Pre", price: 1200, stock: 0 });
    await allowPreorder(product.id, { date: "2027-01-15", advancePercent: 40 });

    // Card and product page.
    const card = await searchShop(page, product.name);
    await expect(card.getByTestId("preorder-badge")).toContainText("Pre-order");
    await expect(card.getByTestId("preorder-tag")).toContainText("Ships around");
    await expect(card.getByTestId("preorder-tag")).toContainText("2027");
    await expect(card).not.toContainText("Sold out");
    await page.goto(`/product/${product.slug}`);
    await expect(page.getByTestId("preorder-notice")).toContainText("Pre-order");
    await expect(page.getByTestId("preorder-available")).toBeVisible();
    await expect(page.getByTestId("add-to-cart")).toHaveText(/Pre-order now/);
    await expect(page.getByTestId("add-to-cart")).toBeEnabled();
    await page.getByTestId("add-to-cart").click();
    await expect(page.getByText(`${product.name} added to your cart`)).toBeVisible();

    // Cart and checkout.
    await page.goto("/cart");
    await expect(page.getByTestId("cart-line").getByTestId("preorder-tag")).toContainText("Pre-order");
    await expect(page.getByTestId("preorder-cart-notice")).toContainText("The whole order waits");
    await expect(page.getByTestId("preorder-advance")).toContainText("480");
    await page.goto("/checkout");
    await expect(page.getByTestId("preorder-cart-notice")).toBeVisible();
    await expect(page.getByTestId("checkout-lines").getByTestId("preorder-tag")).toBeVisible();

    // A signed-in customer places the order (the same call the checkout page makes) and sees why it waits.
    const customerContext = await browser.newContext({ baseURL: E2E.webUrl });
    const mobile = await signInCustomer(customerContext);
    const placed = await apiCheckout(customerContext.request, { lines: [{ variantId: product.variantId, quantity: 2 }], mobile, signedIn: true });
    const mine = await customerContext.newPage();
    await mine.goto(`/account/orders/${placed.orderId}`);
    await expect(mine.getByTestId("preorder-waiting")).toContainText("Waiting for stock");
    await expect(mine.getByTestId("order-lines").getByTestId("preorder-tag")).toContainText("Waiting for stock");

    // The guest tracking link shows the same.
    const guestContext = await browser.newContext({ baseURL: E2E.webUrl });
    const guest = await guestContext.newPage();
    await guest.goto(`/track?token=${encodeURIComponent(placed.trackingToken ?? "")}`);
    if (placed.trackingToken) await expect(guest.getByTestId("preorder-waiting")).toBeVisible();

    // Staff: the queue lists it as Waiting; the advance is shown.
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto("/admin/orders/preorders");
    await page.getByLabel("Search pre-orders").fill(placed.number);
    const row = page.getByTestId("queue-order").filter({ hasText: placed.number });
    await expect(row.first()).toBeVisible();
    const queueRow = page.getByRole("row").filter({ hasText: placed.number });
    await expect(queueRow.first()).toContainText("Waiting for stock");
    await expect(queueRow.first()).toContainText(product.sku);

    // Stock arrives in the order's branch; the allocate action serves the line and the order becomes ready.
    const token = await adminAccess();
    const queue = await call<{ items: { orderId: string; branchId: string }[] }>("GET", `/api/v1/orders/preorders?Search=${placed.number}`, { token, expect: 200 });
    await addStock(queue.json.items[0]!.branchId, product.variantId, 5);
    await page.getByTestId("allocate").click();
    await expect(page.getByText(/Served 1 line; 1 order is now ready\./).or(page.getByText(/Served \d+ lines?; \d+ orders? (is|are) now ready\./)).first()).toBeVisible();
    await page.getByLabel("Search pre-orders").fill(placed.number);
    await expect(page.getByRole("row").filter({ hasText: placed.number }).first()).toContainText("Ready");

    await mine.reload();
    await expect(mine.getByTestId("preorder-ready")).toContainText("have arrived");
    await customerContext.close();
    await guestContext.close();
  });

  test("the product editor has a pre-order tab with rules, saves, and a variant can override the product", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    const product = await makeShopProduct({ label: "Admin", price: 800, stock: 3, withSecondVariant: true });
    await signInViaBff(context, state().admin.email, state().admin.password);
    await page.goto(`/admin/products/${product.id}?tab=preorder`);
    await expect(page.getByTestId("preorder-product")).toBeVisible();

    // Rules: a date in the past and an advance over 100 are refused before anything is sent.
    const productBlock = page.getByTestId("preorder-product");
    await productBlock.getByRole("switch").click();
    await productBlock.getByLabel("Expected ship date").fill("2020-01-01");
    await productBlock.getByLabel("Advance (percent)").fill("150");
    await page.getByTestId("preorder-save").click();
    await expect(page.getByText("The expected date cannot be in the past.")).toBeVisible();
    await expect(page.getByText("The advance must be between 0 and 100.")).toBeVisible();

    const date = new Date(Date.now() + 6 * 3_600_000 + 400 * 86_400_000).toISOString().slice(0, 10);
    await productBlock.getByLabel("Expected ship date").fill(date);
    await productBlock.getByLabel("Advance (percent)").fill("25");
    await page.getByTestId("preorder-save").click();
    await expect(page.getByText("Pre-order settings saved.")).toBeVisible();

    const token = await adminAccess();
    const saved = await call<{ allowPreorder: boolean; expectedShipDate: string; advancePercent: number }>("GET", `/api/v1/catalog/products/${product.id}/preorder`, { token, expect: 200 });
    expect(saved.json).toMatchObject({ allowPreorder: true, expectedShipDate: date });
    expect(Number(saved.json.advancePercent)).toBe(25);

    // The variant follows the product until it has its own setting.
    const first = page.getByTestId("preorder-variant").first();
    await expect(first.getByTestId("preorder-effective")).toContainText("allowed");
    await first.getByRole("combobox").click();
    await page.getByRole("option", { name: "Not allowed" }).click();
    await first.getByRole("button", { name: /Save pre-order settings of/ }).click();
    await expect(first.getByTestId("preorder-effective")).toContainText("not allowed");

    await page.reload();
    await expect(page.getByTestId("preorder-variant").first().getByTestId("preorder-effective")).toContainText("not allowed");
    await expect(page.getByTestId("preorder-variant").nth(1).getByTestId("preorder-effective")).toContainText("ships");
  });

  test("a staff member without the right can look at pre-order settings and the queue but not change them", async ({ page, context }) => {
    test.skip(page.viewportSize()!.width < 1024, "desktop only");
    const product = await makeShopProduct({ label: "Readonly", price: 700, stock: 1 });
    // Order Manager: sees the products (view only) and has all order rights; Support: sees orders but cannot edit them.
    await signInViaBff(context, state().orderManager.email, state().orderManager.password);
    await page.goto(`/admin/products/${product.id}?tab=preorder`);
    await expect(page.getByTestId("preorder-product")).toBeVisible();
    await expect(page.getByTestId("preorder-product").getByLabel("Expected ship date")).toBeDisabled();
    await expect(page.getByTestId("preorder-save")).toHaveCount(0);
    await page.goto("/admin/orders/preorders");
    await expect(page.getByTestId("allocate")).toBeVisible();

    const support = await makeStaff("Support");
    await context.clearCookies();
    await signInViaBff(context, support.email, support.password);
    await page.goto("/admin/orders/preorders");
    await expect(page.getByRole("heading", { level: 1, name: "Pre-orders" })).toBeVisible();
    await expect(page.getByTestId("allocate")).toHaveCount(0);
    await expect(page.getByRole("navigation").getByRole("link", { name: "Pre-orders" }).first()).toBeAttached();
  });
});
