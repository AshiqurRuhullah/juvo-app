import { test, expect, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { loadState } from "./support/env";
import { apiCheckout, disableAllRules, postCheckout, setRule, staffToken, type Placed } from "./support/orders";
import { addToCart, newMobile, pick, sampleVariants, signInCustomer } from "./support/store";
import { axeViolations, horizontalOverflow, settle, shot, smallTapTargets, SHOTS, signInViaBff } from "./support/ui";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";

/**
 * The order screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console errors.
 * Key screens are kept as screenshots in docs/ui-checks/orders.
 */
const WIDTHS = [360, 768, 1280] as const;
const orderLinks = (page: Page) => page.locator('[data-testid="order-link"]:visible');
let placed: Placed;

test.beforeAll(async ({ playwright }) => {
  const admin = await staffToken("admin");
  await disableAllRules(admin);
  const request = await playwright.request.newContext({ baseURL: E2E.webUrl });
  const items = await sampleVariants(request, 3);
  const own = await playwright.request.newContext({ baseURL: E2E.webUrl });
  placed = await apiCheckout(own, { lines: [{ variantId: items[0]!.id, quantity: 2 }] });
  await own.dispose();
  await setRule(admin, "HIGH_COD_NEW_CUSTOMER", { enabled: true, threshold: 1, action: "Hold" });
  const second = await playwright.request.newContext({ baseURL: E2E.webUrl });
  await apiCheckout(second, { lines: [{ variantId: items[1]!.id, quantity: 1 }] });
  await second.dispose();
  await setRule(admin, "HIGH_COD_NEW_CUSTOMER", { enabled: false });
  await request.dispose();
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settle(page);
  if (name) {
    mkdirSync(resolve(SHOTS, "orders"), { recursive: true });
    await shot(page, `orders/${name}`);
  }
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page, [".leaflet-container"]);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/tile\.openstreetmap|Failed to load resource.*(404|401|403)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

const watch = (page: Page) => {
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  return errors;
};

for (const width of WIDTHS) {
  const keep = (name: string) => (width === 360 || width === 1280 ? `${name}-${width}` : undefined);

  test(`checkout at ${width}px`, async ({ page, context }) => {
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });
    const items = await sampleVariants(context.request, 2);
    await addToCart(context, items[0]!.id, 1);
    await page.goto("/checkout");
    await page.getByLabel(/^Mobile number/).fill(newMobile());
    await pick(page, page.getByLabel("Division"), "Dhaka");
    await pick(page, page.getByLabel("District"), "Dhaka");
    await expect(page.getByTestId("delivery-charge")).toBeVisible();
    await checks(page, width, errors, keep("checkout"));
  });

  test(`order confirmation at ${width}px`, async ({ page, context }) => {
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });
    const items = await sampleVariants(context.request, 3);
    const response = await postCheckout(context.request, { lines: [{ variantId: items[2]!.id, quantity: 1 }], mobile: newMobile() });
    expect(response.status()).toBe(201);
    const result = (await response.json()) as { number: string };
    await page.addInitScript(([key, value]) => window.sessionStorage.setItem(key!, value!), [`juvo_order_${result.number}`, JSON.stringify(result)]);
    await page.goto(`/order/confirmed/${result.number}`);
    await expect(page.getByTestId("tracking-box")).toBeVisible();
    await checks(page, width, errors, keep("confirmed"));
  });

  test(`tracking at ${width}px`, async ({ page }) => {
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });
    await page.goto(`/track?token=${encodeURIComponent(placed.trackingToken!)}`);
    await expect(page.getByTestId("track-result")).toBeVisible();
    await checks(page, width, errors, keep("track"));
  });

  test(`customer orders at ${width}px`, async ({ page, context }) => {
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });
    const items = await sampleVariants(context.request, 1);
    const mobile = await signInCustomer(context);
    const mine = await apiCheckout(context.request, { lines: [{ variantId: items[0]!.id, quantity: 1 }], mobile, signedIn: true });
    await page.goto("/account/orders");
    await expect(page.getByTestId("my-order")).toHaveCount(1);
    await checks(page, width, errors, keep("my-orders"));
    await page.goto(`/account/orders/${mine.orderId}`);
    await expect(page.getByTestId("progress-line")).toBeVisible();
    await checks(page, width, errors, keep("my-order"));
  });

  test(`admin orders at ${width}px`, async ({ page, context }) => {
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });
    await signInViaBff(context, loadState().admin.email, loadState().admin.password);
    await page.goto("/admin/orders");
    await expect(orderLinks(page).first()).toBeVisible();
    await page.getByRole("checkbox", { name: `Select order ${placed.number}` }).click();
    await expect(page.getByTestId("bulk-bar")).toBeVisible();
    await checks(page, width, errors, keep("admin-orders"));

    await page.goto(`/admin/orders/${placed.orderId}`);
    await expect(page.getByTestId("order-lines")).toBeVisible();
    await checks(page, width, errors, keep("admin-order"));

    await page.goto(`/admin/orders/${placed.orderId}/packing-slip`);
    await expect(page.getByTestId("packing-slip")).toBeVisible();
    await checks(page, width, errors, keep("packing-slip"));
  });

  test(`social order desk at ${width}px`, async ({ page, context }) => {
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });
    await signInViaBff(context, loadState().admin.email, loadState().admin.password);
    await page.goto("/admin/orders/new");
    await page.getByLabel(/^Mobile number/).fill(newMobile());
    await page.getByLabel("Find a product").fill("JV-TSH-BLA-M");
    await page.getByTestId("desk-product").first().click();
    await page.getByRole("button", { name: "Add JV-TSH-BLA-M" }).click();
    await expect(page.getByTestId("desk-lines")).toBeVisible();
    await checks(page, width, errors, keep("desk"));
  });

  test(`fraud review at ${width}px`, async ({ page, context }) => {
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });
    await signInViaBff(context, loadState().admin.email, loadState().admin.password);
    await page.goto("/admin/fraud");
    await expect(page.getByTestId("review-item").first()).toBeVisible();
    await checks(page, width, errors, keep("fraud-queue"));
    await page.getByRole("tab", { name: "Blacklist" }).click();
    await expect(page.getByLabel("Value")).toBeVisible();
    await checks(page, width, errors, keep("fraud-blacklist"));
    await page.getByRole("tab", { name: "Rules" }).click();
    await expect(page.getByTestId("rule").first()).toBeVisible();
    await checks(page, width, errors, keep("fraud-rules"));
  });
}
