import { test, expect } from "@playwright/test";
import { addToCart, createCoupon, pick, sampleVariants, signInCustomer } from "./support/store";

/**
 * The cart page against the real API: a guest cart kept in an httpOnly cookie, quantity limits, remove with undo, coupons with friendly
 * errors, the delivery charge worked out by the API, and the merge into the customer's cart on sign-in. Checkout itself is not part
 * of this step, so its button stays disabled with a note.
 */
test.describe("Cart", () => {
  test("an empty cart invites the visitor back to the shop", async ({ page }) => {
    await page.goto("/cart");
    await expect(page.getByRole("heading", { name: "Your cart is empty" })).toBeVisible();
    await expect(page.getByRole("link", { name: "Continue shopping" })).toHaveAttribute("href", "/shop");
  });

  test("lines, quantity limits, remove and undo", async ({ page, context }) => {
    const [first, second] = await sampleVariants(context.request, 2);
    await addToCart(context, first!.id, 1);
    await addToCart(context, second!.id, 1);
    await page.goto("/cart");

    const lines = page.getByTestId("cart-line");
    await expect(lines).toHaveCount(2);
    const line = lines.first();
    await expect(line.getByTestId("line-quantity")).toHaveText("1");
    await expect(line.getByRole("button", { name: "Decrease quantity" })).toBeDisabled(); // never below 1

    await line.getByRole("button", { name: "Increase quantity" }).click();
    await expect(line.getByTestId("line-quantity")).toHaveText("2");

    await line.getByRole("button", { name: /^Remove / }).click();
    await expect(lines).toHaveCount(1);
    await page.getByRole("button", { name: "Undo" }).click();
    await expect(lines).toHaveCount(2);
  });

  test("a wrong coupon gives a friendly message, a valid one lowers the total and can be removed", async ({ page, context }) => {
    const [item] = await sampleVariants(context.request, 1);
    await addToCart(context, item!.id, 1);
    const code = await createCoupon("Percent", 10);
    await page.goto("/cart");

    await page.getByLabel("Coupon code").fill("NOSUCHCODE");
    await page.getByRole("button", { name: "Apply" }).click();
    await expect(page.getByText("We do not recognise that coupon code. Check the spelling and try again.")).toBeVisible();

    await page.getByLabel("Coupon code").fill(code.toLowerCase());
    await page.getByRole("button", { name: "Apply" }).click();
    await expect(page.getByTestId("coupon-applied")).toContainText(code);
    await expect(page.getByTestId("summary-discount")).toBeVisible();

    await page.getByRole("button", { name: `Remove coupon ${code}` }).click();
    await expect(page.getByTestId("coupon-applied")).toHaveCount(0);
    await expect(page.getByTestId("summary-discount")).toHaveCount(0);
  });

  test("the delivery charge for a district, the free-delivery progress, and the remembered choice", async ({ page, context }) => {
    const [item] = await sampleVariants(context.request, 1);
    await addToCart(context, item!.id, 1);
    await page.goto("/cart");

    await expect(page.getByTestId("summary-delivery")).toHaveText("Choose a place");
    await pick(page, page.getByLabel("Division"), "Dhaka");
    await pick(page, page.getByLabel("District"), "Dhaka");

    const quote = page.getByTestId("delivery-quote");
    await expect(quote).toBeVisible();
    await expect(quote.getByTestId("delivery-fee")).toContainText(/৳|Free/);
    await expect(page.getByTestId("summary-total")).toBeVisible();
    await expect(quote).toContainText(/Arrives in \d+–\d+ days/);

    // The place is remembered after a reload, so the charge comes back without choosing again.
    await page.reload();
    await expect(page.getByTestId("delivery-quote")).toBeVisible();
  });

  test("the checkout button opens the checkout page", async ({ page, context }) => {
    const [item] = await sampleVariants(context.request, 1);
    await addToCart(context, item!.id, 1);
    await page.goto("/cart");
    await expect(page.getByTestId("checkout-note")).toHaveText("You can review everything before you place the order.");
    await page.getByTestId("checkout-button").click();
    await expect(page).toHaveURL(/\/checkout$/);
    await expect(page.getByRole("heading", { name: "Checkout", level: 1 })).toBeVisible();
  });

  test("the cart survives a reload and is merged into the customer's cart on sign-in", async ({ page, context }) => {
    const [item] = await sampleVariants(context.request, 1);
    await addToCart(context, item!.id, 2);
    await page.goto("/cart");
    await expect(page.getByTestId("line-quantity")).toHaveText("2");
    await page.reload();
    await expect(page.getByTestId("line-quantity")).toHaveText("2");

    await signInCustomer(context);
    await page.goto("/cart");
    await expect(page.getByTestId("cart-line")).toHaveCount(1);
    await expect(page.getByTestId("line-quantity")).toHaveText("2");
  });
});
