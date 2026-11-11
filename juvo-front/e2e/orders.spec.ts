import { test, expect, type APIRequestContext, type Page, type PlaywrightWorkerArgs } from "@playwright/test";
import { E2E } from "../playwright.config";
import { api, loadState } from "./support/env";
import { apiCheckout, bumpProductPrice, createStaff, disableAllRules, emptyVariantStock, postCheckout, setRule, staffToken, type Placed } from "./support/orders";
import { addToCart, newMobile, pick, sampleVariants, signInCustomer } from "./support/store";
import { signInViaBff } from "./support/ui";

/**
 * Step 8 (checkout, orders, order processing, social order desk, fraud basics) against the real stack: a guest and a signed-in
 * customer place orders in the browser, customers and staff work on them, staff create orders by hand, and the fraud queue,
 * blacklist and rules are used. Fraud rules are switched off for ordinary orders (every e2e order comes from one address, which the
 * velocity rule would hold) and switched on again only by the tests that need a held order.
 */
type Playwright = PlaywrightWorkerArgs["playwright"];
const state = () => loadState();
let admin = "";
let support: { email: string; password: string };

test.beforeAll(async () => {
  admin = await staffToken("admin");
  await disableAllRules(admin);
  support = await createStaff(admin, "support@e2e.test", "Sam Support", "Support", state().branches.ho.id);
});

/** A request context with its own cookie jar: one guest with one cart. */
async function guest(playwright: Playwright): Promise<APIRequestContext> {
  return playwright.request.newContext({ baseURL: E2E.webUrl });
}

async function fillAddress(page: Page, name: string, phone: string) {
  await page.getByLabel("Recipient name").fill(name);
  await page.getByLabel("Recipient phone").fill(phone);
  await pick(page, page.getByLabel("Division"), "Dhaka");
  await pick(page, page.getByLabel("District"), "Dhaka");
  await page.getByLabel("Street address").fill("House 12, Road 5, Dhanmondi");
  await expect(page.getByTestId("delivery-charge")).toBeVisible();
}

/** The order links that are on screen (the table and the phone cards both render one per order; only one layout shows). */
const orderLinks = (page: Page) => page.locator('[data-testid="order-link"]:visible');
const acceptTerms = (page: Page) => page.getByLabel(/I agree to the terms/).click();

async function signInStaff(page: Page, who: { email: string; password: string }, next: string) {
  await signInViaBff(page.context(), who.email, who.password);
  await page.goto(next);
}

test.describe("Checkout and tracking", () => {
  test.describe.configure({ mode: "serial" });

  test("a guest places a cash on delivery order with a code, sees the one-time link and tracks it", async ({ page, context }) => {
    const [item] = await sampleVariants(context.request, 1);
    await addToCart(context, item!.id, 1);
    const mobile = newMobile();
    await page.goto("/cart");
    await page.getByTestId("checkout-button").click();
    await expect(page).toHaveURL(/\/checkout$/);

    await page.getByLabel(/^Mobile number/).fill(mobile);
    await fillAddress(page, "Nadia Rahman", mobile);
    await expect(page.getByTestId("checkout-lines")).toContainText("× 1");
    // The server's quote says a guest on cash on delivery must confirm the number with a code.
    await page.getByRole("button", { name: "Send code" }).click();
    const code = (await page.getByTestId("sandbox-code").getByText(/^\d{6}$/).textContent())!.trim();
    await page.getByLabel("6-digit code").fill(code);
    await acceptTerms(page);
    await page.getByTestId("place-order").click();

    await expect(page).toHaveURL(/\/order\/confirmed\/JV-/);
    const number = (await page.getByTestId("order-number").textContent())!.trim();
    await expect(page.getByTestId("order-summary")).toContainText("Cash on delivery");
    await expect(page.getByTestId("delivery-window")).toBeVisible();
    await expect(page.getByTestId("tracking-link")).toContainText("/track?token=");
    await expect(page.getByTestId("profile-offer")).toBeVisible();
    await expect(page.getByTestId("create-profile")).toHaveAttribute("href", /login\?as=customer/);

    // The private link opens the tracking page.
    await page.getByTestId("track-now").click();
    await expect(page.getByTestId("track-number")).toHaveText(number);
    await expect(page.getByTestId("status-order")).toHaveText("Order placed");
    await expect(page.getByTestId("status-payment")).toHaveText("Payment pending");
    await expect(page.getByTestId("progress-line")).toBeVisible();

    // The order number and mobile also find it, and a wrong mobile finds nothing.
    await page.goto("/track");
    await page.getByLabel("Order number").fill(number);
    await page.getByLabel(/^Mobile number/).fill("01999999999");
    await page.getByRole("button", { name: "Track order" }).click();
    await expect(page.getByTestId("track-error")).toContainText("We could not find an order");
    await page.getByLabel(/^Mobile number/).fill(mobile);
    await page.getByRole("button", { name: "Track order" }).click();
    await expect(page.getByTestId("track-result")).toContainText(number);

    // The cart was spent by the order.
    await page.goto("/cart");
    await expect(page.getByRole("heading", { name: "Your cart is empty" })).toBeVisible();
  });

  test("a line that sold out while the customer was checking out is reported and nothing is ordered", async ({ page, context }) => {
    const items = await sampleVariants(context.request, 2);
    const item = items[1]!;
    await signInCustomer(context);
    await addToCart(context, item.id, 1);
    await page.goto("/checkout");
    await fillAddress(page, "Sara Khan", newMobile());
    await acceptTerms(page);
    const stock = await emptyVariantStock(admin, item.productId, item.id);
    try {
      await page.getByTestId("place-order").click();
      await expect(page.getByTestId("checkout-error")).toBeVisible();
      await expect(page.getByTestId("line-problem")).toContainText("Out of stock");
      await expect(page).toHaveURL(/\/checkout$/);
    } finally {
      await stock.restore();
    }
  });

  test("a total that changed while looking is shown and the order is placed on the second press", async ({ page, context }) => {
    const items = await sampleVariants(context.request, 3);
    const item = items[2]!;
    await signInCustomer(context);
    await addToCart(context, item.id, 1);
    await page.goto("/checkout");
    await fillAddress(page, "Tania Islam", newMobile());
    await acceptTerms(page);
    bumpProductPrice(item.productId, 10);
    try {
      await page.getByTestId("place-order").click();
      await expect(page.getByTestId("total-changed")).toContainText("The total was");
      await page.getByTestId("place-order").click();
      await expect(page).toHaveURL(/\/order\/confirmed\/JV-/);
    } finally {
      bumpProductPrice(item.productId, -10);
    }
  });

  test("the same order twice in a row asks first and is placed when the customer insists", async ({ page, context }) => {
    const items = await sampleVariants(context.request, 4);
    const item = items[3]!;
    const mobile = await signInCustomer(context);
    await apiCheckout(context.request, { lines: [{ variantId: item.id, quantity: 1 }], mobile, signedIn: true });
    await addToCart(context, item.id, 1);
    await page.goto("/checkout");
    await fillAddress(page, "Tania Islam", mobile);
    await acceptTerms(page);
    await page.getByTestId("place-order").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("You ordered this a moment ago");
    await dialog.getByRole("button", { name: "Yes, place it again" }).click();
    await expect(page).toHaveURL(/\/order\/confirmed\/JV-/);
  });

  test("a large cash order shows the advance notice before and after it is placed", async ({ page, context }) => {
    const all = await sampleVariants(context.request, 12);
    const dear = [...all].sort((a, b) => b.price - a.price).slice(0, 4);
    await signInCustomer(context);
    let total = 0;
    for (const item of dear) {
      await addToCart(context, item.id, 3);
      total += item.price * 3;
    }
    expect(total, "the sample basket must pass the BDT 10,000 advance threshold").toBeGreaterThan(10_000);
    await page.goto("/checkout");
    await fillAddress(page, "Big Buyer", newMobile());
    await expect(page.getByTestId("advance-notice")).toContainText("in advance");
    await acceptTerms(page);
    await page.getByTestId("place-order").click();
    await expect(page).toHaveURL(/\/order\/confirmed\/JV-/);
    await expect(page.getByTestId("order-summary")).toContainText("Pay now (advance)");
    await expect(page.getByText(/Please pay the advance of/)).toBeVisible();
  });
});

test.describe("Customer orders", () => {
  test("a customer lists, opens and cancels an order", async ({ page, context }) => {
    const [item] = await sampleVariants(context.request, 1);
    const mobile = await signInCustomer(context);
    const placed = await apiCheckout(context.request, { lines: [{ variantId: item!.id, quantity: 1 }], mobile, signedIn: true });

    await page.goto("/account/orders");
    const card = page.getByTestId("my-order").filter({ hasText: placed.number });
    await expect(card).toBeVisible();
    await expect(card).toContainText("Order placed");

    await page.getByTestId("filter-delivered").click();
    await expect(page.getByText("No delivered orders yet")).toBeVisible();
    await page.getByTestId("filter-active").click();
    await card.click();
    await expect(page).toHaveURL(/\/account\/orders\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { name: placed.number })).toBeVisible();
    await expect(page.getByTestId("order-lines")).toBeVisible();
    await expect(page.getByTestId("order-address")).toContainText("E2E Buyer");

    await page.getByTestId("cancel-order").click();
    await page.getByRole("dialog").getByRole("button", { name: "Yes, cancel the order" }).click();
    await expect(page.getByTestId("status-order")).toHaveText("Cancelled");
    await expect(page.getByTestId("cancel-order")).toHaveCount(0);
    await expect(page.getByTestId("history-order")).toContainText("Cancelled");

    await page.goto("/account/orders");
    await page.getByTestId("filter-cancelled").click();
    await expect(page.getByTestId("my-order").filter({ hasText: placed.number })).toBeVisible();
  });
});

test.describe("Staff order processing", () => {
  test.describe.configure({ mode: "serial" });
  const orders: Placed[] = [];

  test.beforeAll(async ({ playwright }) => {
    const request = await guest(playwright);
    const items = await sampleVariants(request, 5);
    for (let i = 0; i < 4; i++) {
      const own = await guest(playwright);
      orders.push(await apiCheckout(own, { lines: [{ variantId: items[i]!.id, quantity: 1 }] }));
      await own.dispose();
    }
    await request.dispose();
  });

  test("the list searches and filters by status, source and dates and shows the source summary", async ({ page }) => {
    const [a] = orders;
    await signInStaff(page, state().manager, "/admin/orders");
    await expect(page.getByTestId("source-chips")).toContainText("Website");

    await page.getByLabel("Search orders").fill(a!.number);
    await expect(orderLinks(page)).toHaveCount(1);
    await expect(orderLinks(page)).toHaveText(a!.number);
    await page.getByLabel("Search orders").fill("");

    await pick(page, page.getByRole("combobox", { name: /^Status/ }), "Order placed");
    await expect(orderLinks(page).filter({ hasText: a!.number })).toHaveCount(1);
    await pick(page, page.getByRole("combobox", { name: /^Source/ }), "Phone call");
    await expect(orderLinks(page).filter({ hasText: a!.number })).toHaveCount(0);
    await page.getByRole("button", { name: "Clear filters" }).click();

    const today = new Date().toLocaleDateString("en-CA");
    await page.getByLabel("From date").fill(today);
    await expect(orderLinks(page).filter({ hasText: a!.number })).toHaveCount(1);
    const yesterday = new Date(Date.now() - 2 * 86_400_000).toLocaleDateString("en-CA");
    await page.getByLabel("To date").fill(yesterday);
    await expect(orderLinks(page)).toHaveCount(0);
  });

  test("bulk confirm works order by order and reports the result", async ({ page }) => {
    const [a, b] = orders;
    await signInStaff(page, state().manager, "/admin/orders");
    await page.getByRole("checkbox", { name: `Select order ${a!.number}` }).click();
    await page.getByRole("checkbox", { name: `Select order ${b!.number}` }).click();
    await expect(page.getByTestId("bulk-count")).toHaveText("2 selected");
    await page.getByTestId("bulk-confirm").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("Confirm 2 orders?");
    await dialog.getByRole("button", { name: "Confirm", exact: true }).click();
    await expect(page.getByTestId("bulk-result")).toHaveText("2 done, 0 not done.");
    await page.getByRole("dialog").getByRole("button", { name: "Close" }).last().click();

    for (const o of [a!, b!]) {
      const detail = await api<{ status: string }>("GET", `/api/v1/orders/${o.orderId}`, undefined, admin);
      expect(detail.json.status).toBe("Confirmed");
    }
  });

  test("bulk print shows one packing slip per order", async ({ page }) => {
    const [a, b] = orders;
    await signInStaff(page, state().manager, "/admin/orders");
    await page.getByRole("checkbox", { name: `Select order ${a!.number}` }).click();
    await page.getByRole("checkbox", { name: `Select order ${b!.number}` }).click();
    await page.getByTestId("bulk-print").click();
    await expect(page).toHaveURL(/\/admin\/orders\/print\?ids=/);
    await expect(page.getByTestId("packing-slip")).toHaveCount(2);
    await expect(page.getByTestId("print-now")).toBeVisible();
  });

  test("an order is confirmed, packed, printed and handed to the courier; notes are added", async ({ page }) => {
    const c = orders[2]!;
    await signInStaff(page, state().orderManager, `/admin/orders/${c.orderId}`);
    await expect(page.getByTestId("status-order")).toHaveText("Order placed");
    await expect(page.getByTestId("order-lines")).toBeVisible();
    await expect(page.getByTestId("customer-mobile")).toContainText(c.mobile.slice(-8));

    await page.getByTestId("action-confirm").click();
    await expect(page.getByTestId("status-order")).toHaveText("Confirmed");
    await page.getByTestId("action-pack").click();
    await expect(page.getByTestId("status-order")).toHaveText("Packed");
    await expect(page.getByTestId("status-delivery")).toHaveText("Ready to ship");

    await page.getByRole("textbox", { name: "Note" }).fill("Fragile, wrap twice");
    await page.getByTestId("add-note").click();
    await expect(page.getByTestId("notes")).toContainText("Fragile, wrap twice");

    await page.getByTestId("action-print").click();
    await expect(page).toHaveURL(/\/packing-slip$/);
    await expect(page.getByTestId("slip-number")).toHaveText(c.number);
    await expect(page.getByTestId("collect")).toContainText("Collect on delivery");
    await expect(page.getByTestId("print-now")).toBeVisible();
    await page.goBack();

    await page.getByTestId("action-transition").click();
    await page.getByRole("dialog").getByRole("button", { name: "Save" }).click();
    await expect(page.getByTestId("status-order")).toHaveText("Handed to courier");
    await expect(page.getByTestId("action-cancel")).toHaveCount(0); // it has left: the state machine allows no cancel
    await expect(page.getByTestId("history-order")).toContainText("Handed to courier");
  });

  test("cancelling needs a reason, which stays in the history", async ({ page }) => {
    const d = orders[3]!;
    await signInStaff(page, state().orderManager, `/admin/orders/${d.orderId}`);
    await page.getByTestId("action-cancel").click();
    const dialog = page.getByRole("dialog");
    await dialog.getByRole("button", { name: "Cancel order" }).click();
    await expect(dialog.getByText("Enter a reason.")).toBeVisible();
    await dialog.getByLabel("Reason").fill("Customer called to cancel");
    await dialog.getByRole("button", { name: "Cancel order" }).click();
    await expect(page.getByTestId("status-order")).toHaveText("Cancelled");
    await expect(page.getByText("Customer called to cancel").first()).toBeVisible();
  });

  test("a large cash order cannot be confirmed until the advance is ticked", async ({ page, playwright }) => {
    const request = await guest(playwright);
    const all = await sampleVariants(request, 12);
    const dear = [...all].sort((a, b) => b.price - a.price).slice(0, 4);
    const own = await guest(playwright);
    const big = await apiCheckout(own, { lines: dear.map((i) => ({ variantId: i.id, quantity: 3 })) });
    await own.dispose();
    await request.dispose();

    await signInStaff(page, state().orderManager, `/admin/orders/${big.orderId}`);
    await page.getByTestId("action-confirm").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("needs an advance");
    await dialog.getByRole("button", { name: "Confirm order" }).click();
    await expect(dialog.getByText("Tick the box once the advance has been received.")).toBeVisible();
    await dialog.getByLabel(/I received the advance/).click();
    await dialog.getByRole("button", { name: "Confirm order" }).click();
    await expect(page.getByTestId("status-order")).toHaveText("Confirmed");
  });

  test("a person without the permissions sees the order but none of the actions", async ({ page }) => {
    const [a] = orders;
    await signInStaff(page, support, "/admin/orders");
    await expect(page.getByRole("heading", { name: "Orders", level: 1 })).toBeVisible();
    await expect(page.getByRole("link", { name: "New order" })).toHaveCount(0);
    await expect(page.getByRole("checkbox", { name: /^Select order/ })).toHaveCount(0);
    await expect(page.getByRole("complementary", { name: "Navigation" })).not.toContainText("Fraud review");

    await page.goto(`/admin/orders/${a!.orderId}`);
    await expect(page.getByTestId("status-order")).toBeVisible();
    for (const id of ["action-confirm", "action-pack", "action-cancel", "action-transition", "action-print", "action-reassign"]) await expect(page.getByTestId(id)).toHaveCount(0);
    await expect(page.getByTestId("add-note")).toBeVisible(); // Support may write notes

    await page.goto("/admin/orders/new");
    await expect(page).toHaveURL(/\/forbidden/);
    await page.goto("/admin/fraud");
    await expect(page).toHaveURL(/\/forbidden/);
  });
});

test.describe("Social order desk", () => {
  test("staff create a WhatsApp order from a SKU search and land on its page", async ({ page }) => {
    await signInStaff(page, state().orderManager, "/admin/orders/new");
    await page.getByLabel("WhatsApp").click();
    const mobile = newMobile();
    await page.getByLabel(/^Mobile number/).fill(mobile);
    await expect(page.getByTestId("customer-lookup")).toBeVisible();
    await page.getByLabel("Name", { exact: true }).fill("Rafi Ahmed");
    await fillDeskAddress(page);

    await page.getByLabel("Find a product").fill("JV-TSH-BLA-M");
    await page.getByTestId("desk-product").first().click();
    await page.getByRole("button", { name: "Add JV-TSH-BLA-M" }).click();
    await expect(page.getByTestId("desk-lines")).toContainText("JV-TSH-BLA-M");
    await page.getByLabel("Quantity of JV-TSH-BLA-M").fill("2");
    await page.getByLabel("Discount (BDT)").fill("50");
    await expect(page.getByTestId("amount-discount")).toContainText("50");
    await expect(page.getByTestId("amount-delivery")).not.toContainText("Add items");
    await expect(page.getByTestId("desk-estimate-note")).toContainText("final amounts");

    await page.getByTestId("desk-save").click();
    await expect(page).toHaveURL(/\/admin\/orders\/[0-9a-f-]{36}$/);
    await expect(page.getByText(/WhatsApp/).first()).toBeVisible();
    await expect(page.getByTestId("status-order")).toHaveText("Order placed");
    await expect(page.getByTestId("customer-mobile")).toContainText(mobile.slice(-8));
    await expect(page.getByTestId("amount-discount")).toContainText("50");
    await expect(page.getByTestId("order-lines")).toContainText("JV-TSH-BLA-M");
  });

  test("the desk refuses an empty order with clear messages", async ({ page }) => {
    await signInStaff(page, state().orderManager, "/admin/orders/new");
    await page.getByTestId("desk-save").click();
    await expect(page.getByTestId("desk-error")).toContainText("check the highlighted fields");
    await expect(page.getByText("Add at least one item.")).toBeVisible();
    await expect(page.getByText("Enter a Bangladesh mobile number.")).toBeVisible();
  });
});

async function fillDeskAddress(page: Page) {
  await page.getByLabel("Recipient name").fill("Rafi Ahmed");
  await pick(page, page.getByLabel("Division"), "Dhaka");
  await pick(page, page.getByLabel("District"), "Dhaka");
  await page.getByLabel("Street address").fill("Flat 4B, Road 9, Banani");
}

test.describe("Fraud review", () => {
  test.describe.configure({ mode: "serial" });

  /** An order held for review: a new cash customer with the "new customer cash" rule set to hold from BDT 1. */
  async function heldOrder(playwright: Playwright, variantIndex: number): Promise<Placed> {
    const request = await guest(playwright);
    const items = await sampleVariants(request, variantIndex + 1);
    await setRule(admin, "HIGH_COD_NEW_CUSTOMER", { enabled: true, threshold: 1, action: "Hold" });
    const own = await guest(playwright);
    try {
      return await apiCheckout(own, { lines: [{ variantId: items[variantIndex]!.id, quantity: 1 }] });
    } finally {
      await setRule(admin, "HIGH_COD_NEW_CUSTOMER", { enabled: false });
      await own.dispose();
      await request.dispose();
    }
  }

  test("a held order shows its flags, is called, asked for an advance and approved", async ({ page, playwright }) => {
    const held = await heldOrder(playwright, 0);
    await signInStaff(page, state().manager, "/admin/orders");
    await page.getByLabel("Held for review only").click();
    const row = orderLinks(page).filter({ hasText: held.number });
    await expect(row).toHaveCount(1);
    await expect(page.getByText("Flagged: needs review").first()).toBeVisible();

    await page.goto(`/admin/orders/${held.orderId}`);
    await expect(page.getByTestId("held-banner")).toContainText("Held for fraud review");
    await expect(page.getByTestId("action-confirm")).toBeDisabled();

    await page.goto("/admin/fraud");
    const item = page.getByTestId("review-item").filter({ hasText: held.number });
    await expect(item).toBeVisible();
    await expect(item.getByRole("list", { name: "Flags" })).toBeVisible();
    await expect(item.getByTestId("stats-panel")).toBeVisible();
    await expect(item.getByTestId("risk-score")).toBeVisible();

    await item.getByTestId("decide-Call").click();
    await page.getByRole("dialog").getByLabel("Note").fill("Called, will ring back");
    await page.getByRole("dialog").getByRole("button", { name: "Call the customer" }).click();
    await expect(item).toContainText("In review");

    await expect(page.getByRole("dialog")).toHaveCount(0);
    await item.getByTestId("decide-RequestAdvance").click();
    await page.getByRole("dialog").getByRole("button", { name: "Request advance" }).click();
    await expect(item).toContainText("Advance requested");

    await expect(page.getByRole("dialog")).toHaveCount(0);
    await item.getByTestId("decide-Approve").click();
    await page.getByRole("dialog").getByRole("button", { name: "Approve" }).click();
    await expect(page.getByTestId("review-item").filter({ hasText: held.number })).toHaveCount(0);

    await page.goto(`/admin/orders/${held.orderId}`);
    await expect(page.getByTestId("held-banner")).toHaveCount(0);
    await expect(page.getByText("Approved").first()).toBeVisible();
  });

  test("rejecting needs a note and cancels the order", async ({ page, playwright }) => {
    const held = await heldOrder(playwright, 1);
    await signInStaff(page, state().manager, "/admin/fraud");
    const item = page.getByTestId("review-item").filter({ hasText: held.number });
    await item.getByTestId("decide-Reject").click();
    const dialog = page.getByRole("dialog");
    await dialog.getByRole("button", { name: "Reject", exact: true }).click();
    await expect(dialog.getByText("A note is required to reject an order.")).toBeVisible();
    await dialog.getByLabel("Note").fill("Fake number");
    await dialog.getByRole("button", { name: "Reject", exact: true }).click();
    await expect(page.getByTestId("review-item").filter({ hasText: held.number })).toHaveCount(0);
    const detail = await api<{ status: string; fraudState: string }>("GET", `/api/v1/orders/${held.orderId}`, undefined, admin);
    expect(detail.json.status).toBe("Cancelled");
    expect(detail.json.fraudState).toBe("Rejected");
  });

  test("a blacklisted mobile cannot order until the entry is removed", async ({ page, playwright }) => {
    const mobile = newMobile();
    await signInStaff(page, state().manager, "/admin/fraud");
    await page.getByRole("tab", { name: "Blacklist" }).click();
    await page.getByLabel("Value").fill(mobile);
    await page.getByLabel("Note").fill("e2e test entry");
    await page.getByTestId("bl-add").click();
    const normalized = `+880${mobile.slice(1)}`;
    await expect(page.getByRole("cell", { name: normalized, exact: true })).toBeVisible();

    const request = await guest(playwright);
    const [item] = await sampleVariants(request, 1);
    const blocked = await postCheckout(request, { lines: [{ variantId: item!.id, quantity: 1 }], mobile });
    expect(blocked.status()).toBe(403);
    expect(((await blocked.json()) as { code: string }).code).toBe("ORDER.CHECKOUT_BLOCKED");

    await page.getByRole("button", { name: `Remove ${normalized}` }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Remove" }).click();
    await expect(page.getByRole("cell", { name: normalized, exact: true })).toHaveCount(0);

    const own = await guest(playwright);
    const placed = await apiCheckout(own, { lines: [{ variantId: item!.id, quantity: 1 }], mobile });
    expect(placed.number).toMatch(/^JV-/);
    await own.dispose();
    await request.dispose();
  });

  test("a rule's threshold is edited and remembered", async ({ page }) => {
    await signInStaff(page, state().manager, "/admin/fraud");
    await page.getByRole("tab", { name: "Rules" }).click();
    const rule = page.locator('[data-testid="rule"][data-code="HIGH_VALUE"]');
    await expect(rule).toHaveCount(1);
    await rule.getByLabel("Threshold").fill("25000");
    await rule.getByTestId("rule-save").click();
    await expect(page.getByText("Rule HIGH_VALUE saved.")).toBeVisible();
    await page.reload();
    await page.getByRole("tab", { name: "Rules" }).click();
    await expect(page.locator('[data-testid="rule"][data-code="HIGH_VALUE"]').getByLabel("Threshold")).toHaveValue("25000");
    await setRule(admin, "HIGH_VALUE", { enabled: false });
  });
});
