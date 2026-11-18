import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { apiCheckout } from "./support/orders";
import { addToCart, createCoupon, newMobile } from "./support/store";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { call } from "./support/cod";
import { adminAccess } from "./support/returns";
import { allowPreorder, flashSale, makePromotion, makeShopProduct, stamp, target, uploadPromoImage, type ShopProduct } from "./support/promo-store";

/**
 * Every new promotion and pre-order screen on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and
 * no console errors; the key pages also in the dark theme. Screenshots go to docs/ui-checks/promo-store/.
 */
const WIDTHS = [360, 768, 1280] as const;

let flash: ShopProduct;
let pre: ShopProduct;
let couponCode: string;
let campaignSlug: string;
let preorderOrder: { orderId: string; number: string };

test.beforeAll(async ({ browser }) => {
  test.setTimeout(180_000);
  mkdirSync(resolve(SHOTS, "promo-store"), { recursive: true });
  flash = await makeShopProduct({ label: "UiFlash", price: 1500, stock: 20 });
  await flashSale(flash, 25, 3 * 3_600_000, "Weekend flash UI");
  pre = await makeShopProduct({ label: "UiPre", price: 900, stock: 0 });
  await allowPreorder(pre.id, { date: "2027-02-10", advancePercent: 30 });
  couponCode = await createCoupon("Fixed", 20);
  campaignSlug = `ui-${stamp()}`;
  const campaign = await makePromotion(
    { nameEn: "Campaign UI", type: "FestivalCampaign", slug: campaignSlug, titleEn: "Winter Collection UI", titleBn: "শীতের কালেকশন", bodyEn: "Warm layers for the cold months.", bodyBn: "শীতের জন্য গরম পোশাক।", bannerSlot: "HomeTop", targets: [target(flash.id)] },
    { activate: false, endsInMs: 3 * 3_600_000 },
  );
  await uploadPromoImage(campaign.id, "hero");
  await uploadPromoImage(campaign.id, "banner");
  await call("POST", `/api/v1/promotions/${campaign.id}/activate`, { token: await adminAccess(), expect: 200 });
  const context = await browser.newContext({ baseURL: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` });
  preorderOrder = await apiCheckout(context.request, { lines: [{ variantId: pre.variantId, quantity: 1 }], mobile: newMobile() });
  await context.close();
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await page.waitForLoadState("networkidle").catch(() => undefined);
  // A toast carries a small close button of its own (a shared component); the page is checked once it is gone.
  await expect(page.locator("[data-sonner-toast]")).toHaveCount(0, { timeout: 20_000 });
  if (name) await shot(page, `promo-store/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

for (const width of WIDTHS) {
  test(`shop pages with a flash sale and a pre-order at ${width}px`, async ({ page }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });

    await page.goto(`/shop?q=${encodeURIComponent(flash.name)}`);
    await expect(page.getByTestId("product-card").filter({ hasText: flash.name }).getByTestId("countdown")).toHaveAttribute("data-state", "running");
    await checks(page, width, errors, `shop-flash-${width}`);

    await page.goto(`/shop?q=${encodeURIComponent(pre.name)}`);
    await expect(page.getByTestId("product-card").filter({ hasText: pre.name }).getByTestId("preorder-badge")).toBeVisible();
    await checks(page, width, errors, `shop-preorder-${width}`);

    await page.goto(`/product/${flash.slug}`);
    await expect(page.getByTestId("countdown").first()).toHaveAttribute("data-state", "running");
    await checks(page, width, errors, `product-flash-${width}`);

    await page.goto(`/product/${pre.slug}`);
    await expect(page.getByTestId("preorder-notice")).toBeVisible();
    await checks(page, width, errors, `product-preorder-${width}`);

    await page.goto(`/campaign/${campaignSlug}`);
    await expect(page.getByTestId("campaign-title")).toBeVisible();
    await checks(page, width, errors, `campaign-${width}`);

    await page.goto("/");
    await expect(page.locator(`a[href="/campaign/${campaignSlug}"]`)).toBeVisible();
    await checks(page, width, errors, `home-banner-${width}`);
  });

  test(`cart and checkout with offers and a pre-order at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    await addToCart(context, flash.variantId, 1);
    await addToCart(context, pre.variantId, 1);

    await page.goto("/cart");
    await page.getByLabel("Coupon code").fill(couponCode);
    await page.getByRole("button", { name: "Apply" }).click();
    await expect(page.getByTestId("coupon-kept")).toBeVisible();
    await expect(page.getByTestId("preorder-cart-notice")).toBeVisible();
    await checks(page, width, errors, `cart-${width}`);

    await page.goto("/checkout");
    await expect(page.getByTestId("amount-list")).toBeVisible();
    await expect(page.getByTestId("preorder-cart-notice")).toBeVisible();
    await checks(page, width, errors, `checkout-${width}`);
  });

  test(`staff pre-order screens at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);

    await page.goto("/admin/orders/preorders");
    await page.getByLabel("Search pre-orders").fill(preorderOrder.number);
    await expect(page.getByTestId("queue-order").filter({ hasText: preorderOrder.number }).filter({ visible: true }).first()).toBeVisible();
    await checks(page, width, errors, `queue-${width}`);

    await page.goto(`/admin/products/${pre.id}?tab=preorder`);
    await expect(page.getByTestId("preorder-product")).toBeVisible();
    await checks(page, width, errors, `settings-${width}`);
  });
}

test("promotion and pre-order screens in the dark theme", async ({ page, context }) => {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  await addToCart(context, flash.variantId, 1);
  await addToCart(context, pre.variantId, 1);

  await page.goto(`/shop?q=${encodeURIComponent(flash.name)}`);
  await expect(page.getByTestId("product-card").filter({ hasText: flash.name })).toHaveCount(1);
  await checks(page, 1280, errors, "shop-flash-dark");
  await page.goto("/cart");
  await expect(page.getByTestId("preorder-cart-notice")).toBeVisible();
  await checks(page, 1280, errors, "cart-dark");
  await page.goto(`/campaign/${campaignSlug}`);
  await expect(page.getByTestId("campaign-title")).toBeVisible();
  await checks(page, 1280, errors, "campaign-dark");

  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto("/admin/orders/preorders");
  await expect(page.getByTestId("allocate")).toBeVisible();
  await checks(page, 1280, errors, "queue-dark");
  await page.goto(`/admin/products/${pre.id}?tab=preorder`);
  await expect(page.getByTestId("preorder-product")).toBeVisible();
  await checks(page, 1280, errors, "settings-dark");
});
