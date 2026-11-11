import { test, expect, type Page } from "@playwright/test";
import { addToCart, pick, sampleVariants, signInCustomer } from "./support/store";
import { axeViolations, horizontalOverflow, settle, shot, smallTapTargets } from "./support/ui";

/**
 * The cart and the account pages on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console
 * errors (the map's tile requests may fail offline and are ignored). Key screens are kept as screenshots.
 */
const WIDTHS = [360, 768, 1280] as const;

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settle(page);
  if (name) await shot(page, name);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page, [".leaflet-container"]);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/tile\.openstreetmap|Failed to load resource.*(404|401|403)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

for (const width of WIDTHS) {
  test(`cart at ${width}px`, async ({ page, context }) => {
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const items = await sampleVariants(context.request, 2);
    for (const item of items) await addToCart(context, item.id, 1);
    await page.goto("/cart");
    await expect(page.getByTestId("cart-line")).toHaveCount(2);
    await pick(page, page.getByLabel("Division"), "Dhaka");
    await pick(page, page.getByLabel("District"), "Dhaka");
    await expect(page.getByTestId("delivery-quote")).toBeVisible();
    await checks(page, width, errors, width === 360 || width === 1280 ? `cart-${width}` : undefined);
  });

  test(`account addresses at ${width}px`, async ({ page, context }) => {
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    await signInCustomer(context);
    await page.goto("/account/addresses");
    await page.getByTestId("add-address").first().click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await expect(page.getByTestId("pin-map")).toBeVisible();
    await checks(page, width, errors, width === 360 ? "account-addresses-360" : undefined);
  });
}
