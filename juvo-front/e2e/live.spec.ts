import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { api, loadState } from "./support/env";
import { advanceParcel, bookOrder, confirmedOrder, guestContext, packedOrder, trackUrl } from "./support/booking";
import { apiCheckout, createStaff, disableAllRules, staffToken, type Placed } from "./support/orders";
import { advanceTo, deliveredOrder, requestReturn, stockedVariants } from "./support/returns";
import { newMobile, pick, sampleVariants, signInCustomer } from "./support/store";
import { axeViolations, horizontalOverflow, SHOTS, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * Step 10 front end (courier booking, customer tracking, live updates, desk totals) against the real stack: staff book couriers from
 * the order screen and in bulk, customers see the parcel and follow it live, guests cancel before pickup, the admin list shows new
 * orders as they arrive and the order desk shows the server's own totals. The last block checks the changed screens on a phone, a
 * tablet and a desktop (no sideways scroll, 44 px targets, axe WCAG 2.1 AA, no console errors) and keeps screenshots in
 * docs/ui-checks/live.
 *
 * The live checks need the API to accept this site's origin for the SignalR negotiation (Cors__AllowedOrigins__0 = the web URL).
 */
const state = () => loadState();
let admin = "";
let support: { email: string; password: string };
let variants: { id: string; price: number; slug: string; productId: string }[] = [];

test.beforeAll(async ({ playwright }) => {
  admin = await staffToken("admin");
  await disableAllRules(admin);
  support = await createStaff(admin, "support-live@e2e.test", "Sam Live", "Support", state().branches.ho.id);
  const request = await guestContext(playwright);
  variants = await sampleVariants(request, 6);
  await request.dispose();
});

async function signInAdmin(page: Page, next: string) {
  const who = state().admin;
  await signInViaBff(page.context(), who.email, who.password);
  await page.goto(next);
}

const live = (page: Page) => page.getByTestId("live-badge");
const waitLive = (page: Page) => expect(live(page).first()).toHaveAttribute("data-live", "live", { timeout: 20_000 });

test.describe("Booking a courier", () => {
  test.describe.configure({ mode: "serial" });

  test("staff book the recommended courier from the order screen and get the label link", async ({ page, playwright }) => {
    const order = await packedOrder(playwright, admin, variants[0]!.id);
    await signInAdmin(page, `/admin/orders/${order.orderId}`);
    await expect(page.getByTestId("status-order")).toHaveText("Packed");
    await expect(page.getByText("This order is packed and ready for a courier.")).toBeVisible();

    await page.getByTestId("book-courier").click();
    const dialog = page.getByRole("dialog");
    const options = dialog.getByTestId("courier-option");
    await expect(options.first()).toBeVisible();
    // The recommended courier is preselected and says so.
    await expect(options.first()).toContainText("Recommended");
    await expect(options.first().getByRole("radio")).toBeChecked();
    // Couriers that cannot take the parcel are listed apart, with the API's reason.
    await expect(dialog.getByTestId("ineligible")).toBeVisible();
    await expect(dialog.getByTestId("ineligible-reason").first()).not.toBeEmpty();

    // A weight the form cannot read is explained instead of sent.
    await dialog.getByLabel("Weight (kg)").fill("abc");
    await expect(dialog.getByText("Enter a weight like 0.6 or 1.25.")).toBeVisible();
    await expect(dialog.getByTestId("book-confirm")).toBeDisabled();
    await dialog.getByLabel("Weight (kg)").fill("0.6");
    await expect(dialog.getByTestId("book-confirm")).toBeEnabled();

    await dialog.getByTestId("book-confirm").click();
    await expect(dialog.getByTestId("booked-result")).toBeVisible();
    await expect(dialog.getByTestId("booked-consignment")).toHaveText(/^SBX-/);
    await expect(dialog.getByTestId("booked-label")).toHaveAttribute("href", /\/admin\/shipments\/[0-9a-f-]{36}\/label$/);
    await dialog.getByTestId("booked-close").click();

    // The panel now lists the parcel and no longer offers a second booking.
    const parcel = page.getByTestId("parcel").first();
    await expect(parcel).toHaveAttribute("data-status", "Booked");
    await expect(parcel.getByTestId("parcel-consignment")).toHaveText(/^SBX-/);
    await expect(parcel.getByTestId("parcel-open")).toHaveAttribute("href", /\/admin\/shipments\/[0-9a-f-]{36}$/);
    await expect(page.getByTestId("book-courier")).toHaveCount(0);
  });

  test("an order that is not packed explains that it must be packed first", async ({ page, playwright }) => {
    const order = await confirmedOrder(playwright, admin, variants[1]!.id);
    await signInAdmin(page, `/admin/orders/${order.orderId}`);
    await expect(page.getByTestId("status-order")).toHaveText("Confirmed");
    await expect(page.getByTestId("book-not-ready")).toBeVisible();
    await expect(page.getByTestId("book-courier")).toHaveCount(0);
  });

  test("a staff member without the book permission sees the parcels but no book button", async ({ page, playwright }) => {
    const order = await packedOrder(playwright, admin, variants[2]!.id);
    await signInViaBff(page.context(), support.email, support.password);
    await page.goto(`/admin/orders/${order.orderId}`);
    await expect(page.getByTestId("status-order")).toHaveText("Packed");
    await expect(page.getByText("This order is packed and ready for a courier.")).toBeVisible();
    await expect(page.getByTestId("book-courier")).toHaveCount(0);
  });

  test("two packed orders are booked in one go and every order shows its result", async ({ page, playwright }) => {
    const a = await packedOrder(playwright, admin, variants[3]!.id);
    const b = await packedOrder(playwright, admin, variants[4]!.id);
    await signInAdmin(page, "/admin/orders");
    const search = page.getByLabel("Search orders");
    for (const order of [a, b]) {
      await search.fill(order.number);
      await page.getByRole("checkbox", { name: `Select order ${order.number}` }).first().check();
    }
    await expect(page.getByTestId("bulk-count")).toContainText("2");
    await page.getByTestId("bulk-book").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("Book a courier for 2 packed orders")).toBeVisible();
    await dialog.getByTestId("bulk-book-confirm").click();
    await expect(dialog.getByTestId("bulk-book-result")).toBeVisible();
    await expect(dialog.getByTestId("bulk-book-summary")).toHaveText("2 booked, 0 failed.");
    await expect(dialog.getByTestId("bulk-book-row")).toHaveCount(2);
    await expect(dialog.getByTestId("bulk-book-row").first()).toContainText("SBX-");
    await dialog.getByTestId("bulk-book-close").click();
    await expect(page.getByTestId("bulk-bar")).toHaveCount(0);

    // Booking the same orders again is refused per order, with the API's text (they already have an open parcel).
    const again = await api<{ succeeded: number | string; failed: number | string; results: { ok: boolean; errorCode: string | null }[] }>("POST", "/api/v1/shipments/bulk-book", { orderIds: [a.orderId], courierId: null }, admin);
    expect(Number(again.json.failed)).toBe(1);
    expect(again.json.results[0]!.errorCode).toBe("SHIPMENT.ALREADY_BOOKED");
  });

  test("a return pickup is booked with a courier and the return follows the reverse parcel live", async ({ page }) => {
    const [variant] = await stockedVariants(2, 1);
    const order = await deliveredOrder([{ variantId: variant!.id, quantity: 1 }]);
    const ret = await requestReturn(order);
    await advanceTo(ret.id, "Approved");
    await signInAdmin(page, `/admin/returns/${ret.id}`);
    await waitLive(page);

    await page.getByTestId("action-schedulePickup").click();
    const dialog = page.getByRole("dialog");
    const tomorrow = new Date(Date.now() + 86_400_000);
    await dialog.getByLabel("Pickup day").fill(`${tomorrow.getFullYear()}-${String(tomorrow.getMonth() + 1).padStart(2, "0")}-${String(tomorrow.getDate()).padStart(2, "0")}`);
    // The manual courier name field stays until the courier option is switched on.
    await expect(dialog.getByLabel("Courier name")).toBeVisible();
    await dialog.getByLabel("Book a courier for the pickup").click();
    await expect(dialog.getByLabel("Courier name")).toHaveCount(0);
    await dialog.getByRole("button", { name: "Schedule pickup" }).click();
    await expect(page.getByTestId("return-status").first()).toHaveText("Pickup arranged");
    const parcel = page.getByTestId("return-parcel").first();
    await expect(parcel).toBeVisible();
    await expect(parcel.getByTestId("return-consignment")).toHaveText(/^SBX-/);
    await expect(parcel.getByTestId("return-parcel-open")).toHaveAttribute("href", /\/admin\/shipments\//);

    // The courier picks the parcel up: the return moves on without a reload.
    await page.evaluate(() => ((window as unknown as { __kept: number }).__kept = 1));
    const list = await api<{ items: { id: string }[] }>("GET", `/api/v1/shipments?orderId=${order.orderId}&kind=Return`, undefined, admin);
    await advanceParcel(admin, list.json.items[0]!.id);
    await expect(page.getByTestId("return-status").first()).toHaveText("Picked up", { timeout: 20_000 });
    expect(await page.evaluate(() => (window as unknown as { __kept?: number }).__kept)).toBe(1);
  });

  test("the manual pickup path still works without a courier booking", async ({ page }) => {
    const [variant] = await stockedVariants(2, 1, 1);
    const order = await deliveredOrder([{ variantId: variant!.id, quantity: 1 }]);
    const ret = await requestReturn(order);
    await advanceTo(ret.id, "Approved");
    await signInAdmin(page, `/admin/returns/${ret.id}`);
    await page.getByTestId("action-schedulePickup").click();
    const dialog = page.getByRole("dialog");
    const tomorrow = new Date(Date.now() + 86_400_000);
    await dialog.getByLabel("Pickup day").fill(`${tomorrow.getFullYear()}-${String(tomorrow.getMonth() + 1).padStart(2, "0")}-${String(tomorrow.getDate()).padStart(2, "0")}`);
    await dialog.getByLabel("Courier name").fill("Pathao");
    await dialog.getByRole("button", { name: "Schedule pickup" }).click();
    await expect(page.getByTestId("return-status").first()).toHaveText("Pickup arranged");
    await expect(page.getByTestId("pickup-info")).toContainText("Pathao");
    await expect(page.getByTestId("return-parcel")).toHaveCount(0);
  });
});

test.describe("Customer tracking and live updates", () => {
  test.describe.configure({ mode: "serial" });

  test("the tracking page shows the courier card only once a parcel is booked, then follows the parcel live", async ({ page, playwright }) => {
    const order = await packedOrder(playwright, admin, variants[0]!.id);
    await page.goto(trackUrl(order));
    await expect(page.getByTestId("track-result")).toBeVisible();
    await expect(page.getByTestId("courier-card")).toHaveCount(0);
    await waitLive(page);

    const parcel = await bookOrder(admin, order.orderId);
    // Booking is live too: the card appears without a reload.
    await page.evaluate(() => ((window as unknown as { __kept: number }).__kept = 1));
    await expect(page.getByTestId("courier-card")).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId("courier-consignment")).toHaveText(parcel.consignmentId);
    await expect(page.getByTestId("courier-status")).toHaveText("Booked");
    await expect(page.getByTestId("courier-event").first()).toBeVisible();
    // The link to the courier's own page is drawn for https addresses only, and opens safely in a new tab.
    await expect(page.getByTestId("courier-link")).toHaveAttribute("href", /^https:\/\//);
    await expect(page.getByTestId("courier-link")).toHaveAttribute("rel", /noopener/);

    await advanceParcel(admin, parcel.id);
    await expect(page.getByTestId("courier-status")).toHaveText("Picked up", { timeout: 20_000 });
    await expect(page.getByTestId("status-order")).toHaveText("Handed to courier", { timeout: 20_000 });
    await advanceParcel(admin, parcel.id);
    await advanceParcel(admin, parcel.id);
    await expect(page.getByTestId("courier-status")).toHaveText("Out for delivery", { timeout: 20_000 });
    expect(await page.evaluate(() => (window as unknown as { __kept?: number }).__kept)).toBe(1);
    expect(await page.getByTestId("courier-event").count()).toBeGreaterThanOrEqual(4);

    // A failed attempt is shown with an icon and words, with the number of attempts.
    await advanceParcel(admin, parcel.id, "delivery_failed");
    await expect(page.getByTestId("courier-failed")).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId("courier-failed")).toContainText("could not deliver");
    await expect(page.getByTestId("courier-status")).toHaveText("Delivery failed");
  });

  test("a signed-in customer's order page follows the parcel live", async ({ page, context, playwright }) => {
    const order = await packedOrder(playwright, admin, variants[1]!.id);
    const parcel = await bookOrder(admin, order.orderId);
    await signInCustomer(context, order.mobile);
    await page.goto(`/account/orders/${order.orderId}`);
    await expect(page.getByTestId("courier-card")).toBeVisible();
    await expect(page.getByTestId("courier-status")).toHaveText("Booked");
    await waitLive(page);
    await advanceParcel(admin, parcel.id);
    await expect(page.getByTestId("courier-status")).toHaveText("Picked up", { timeout: 20_000 });
  });

  test("a guest cancels before pickup and the order is cancelled", async ({ page, playwright }) => {
    const order = await packedOrder(playwright, admin, variants[2]!.id);
    await bookOrder(admin, order.orderId);
    await page.goto(trackUrl(order));
    await expect(page.getByTestId("guest-cancel")).toBeVisible();
    await page.getByTestId("guest-cancel").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText(`Cancel order ${order.number}?`)).toBeVisible();
    await dialog.getByLabel("Why are you cancelling?").fill("Ordered by mistake");
    await dialog.getByRole("button", { name: "Yes, cancel the order" }).click();
    await expect(page.getByTestId("status-order")).toHaveText("Cancelled");
    await expect(page.getByTestId("guest-cancel")).toHaveCount(0);
    // The booking with the courier went with it.
    await expect(page.getByTestId("courier-card")).toHaveCount(0);
  });

  test("once the courier has the parcel the cancel button disappears and the API refuses", async ({ page, playwright }) => {
    const order = await packedOrder(playwright, admin, variants[3]!.id);
    const parcel = await bookOrder(admin, order.orderId);
    await page.goto(trackUrl(order));
    await expect(page.getByTestId("guest-cancel")).toBeVisible();
    await waitLive(page);
    await advanceParcel(admin, parcel.id);
    await expect(page.getByTestId("status-order")).toHaveText("Handed to courier", { timeout: 20_000 });
    await expect(page.getByTestId("guest-cancel")).toHaveCount(0);

    const refused = await page.request.post("/api/bff/api/v1/store/orders/track/cancel", { data: { token: order.trackingToken, reason: "too late" } });
    expect(refused.status()).toBe(409);
    expect(((await refused.json()) as { code: string }).code).toBe("ORDER.CANNOT_CANCEL");
  });

  test("a wrong proof cannot cancel anything", async ({ page }) => {
    const refused = await page.request.post("/api/bff/api/v1/store/orders/track/cancel", { data: { number: "JV-XX-0000-000000", mobile: newMobile(), reason: null } });
    expect(refused.status()).toBe(404);
  });
});

test.describe("Live admin and desk totals", () => {
  test("the order list shows a toast with a link when another session places an order, and refreshes", async ({ page, playwright }) => {
    await signInAdmin(page, "/admin/orders");
    await waitLive(page);
    const request = await guestContext(playwright);
    let placed: Placed;
    try {
      placed = await apiCheckout(request, { lines: [{ variantId: variants[4]!.id, quantity: 1 }] });
    } finally {
      await request.dispose();
    }
    const toast = page.getByText(new RegExp(`New order ${placed.number}`));
    await expect(toast).toBeVisible({ timeout: 20_000 });
    await expect(page.getByTestId("new-order-link").first()).toHaveAttribute("href", `/admin/orders/${placed.orderId}`);
    // The list refreshed itself: the order is in it without a reload.
    await page.getByLabel("Search orders").fill(placed.number);
    await expect(page.locator('[data-testid="order-link"]:visible', { hasText: placed.number })).toHaveCount(1);
  });

  test("the order page follows its order live", async ({ page, playwright }) => {
    const order = await confirmedOrder(playwright, admin, variants[5]!.id);
    await signInAdmin(page, `/admin/orders/${order.orderId}`);
    await expect(page.getByTestId("status-order")).toHaveText("Confirmed");
    await waitLive(page);
    const pack = await api("POST", `/api/v1/orders/${order.orderId}/pack`, undefined, admin);
    expect(pack.status).toBe(200);
    await expect(page.getByTestId("status-order")).toHaveText("Packed", { timeout: 20_000 });
    await expect(page.getByTestId("book-courier")).toBeVisible();
  });

  test("the desk preview shows the server's totals and the saved order shows the same ones", async ({ page }) => {
    await signInViaBff(page.context(), state().orderManager.email, state().orderManager.password);
    await page.goto("/admin/orders/new");
    await expect(page.getByTestId("desk-quote-hint")).toBeVisible();
    await page.getByLabel("WhatsApp").click();
    const mobile = newMobile();
    await page.getByLabel(/^Mobile number/).fill(mobile);
    await page.getByLabel("Recipient name").fill("Rafi Ahmed");
    await pick(page, page.getByLabel("Division"), "Dhaka");
    await pick(page, page.getByLabel("District"), "Dhaka");
    await page.getByLabel("Street address").fill("Flat 4B, Road 9, Banani");
    await page.getByLabel("Find a product").fill("JV-TSH-BLA-M");
    await page.getByTestId("desk-product").first().click();
    await page.getByRole("button", { name: "Add JV-TSH-BLA-M" }).click();
    await page.getByLabel("Quantity of JV-TSH-BLA-M").fill("2");
    await page.getByLabel("Discount (BDT)").fill("50");

    const quote = page.getByTestId("desk-quote");
    await expect(quote).toBeVisible();
    await expect(quote).toHaveAttribute("data-settling", "0", { timeout: 10_000 });
    await expect(quote.getByTestId("quote-lines")).toContainText("JV-TSH-BLA-M");
    await expect(quote.getByTestId("quote-lines")).toContainText("in stock here");
    await expect(quote.getByTestId("amount-discount")).toContainText("50");
    await expect(quote.getByTestId("quote-window")).toBeVisible();
    const total = (await quote.getByTestId("amount-total").textContent())!.trim();
    const delivery = (await quote.getByTestId("amount-delivery").textContent())!.trim();

    // A typed discount larger than the goods is refused by the server's own rule, shown inline.
    await page.getByLabel("Discount (BDT)").fill("99999");
    await expect(page.getByTestId("desk-quote-error")).toContainText("discount cannot be more", { timeout: 10_000 });
    await page.getByLabel("Discount (BDT)").fill("50");
    await expect(page.getByTestId("desk-quote")).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId("amount-total")).toHaveText(total);

    await page.getByTestId("desk-save").click();
    await expect(page).toHaveURL(/\/admin\/orders\/[0-9a-f-]{36}$/);
    await expect(page.getByTestId("amount-total")).toHaveText(total);
    await expect(page.getByTestId("amount-delivery")).toHaveText(delivery);
    await expect(page.getByTestId("amount-discount")).toContainText("50");
  });
});

/* ---- UI checks: phone, tablet, desktop ---------------------------------------------------------------------------------------- */

const WIDTHS = [360, 768, 1280] as const;

async function checks(page: Page, width: number, errors: string[], name?: string) {
  // The page keeps a live connection open, so wait for the document and fonts with a ceiling instead of for a quiet network.
  await page.waitForLoadState("domcontentloaded");
  await page.waitForTimeout(600);
  await page.evaluate(() => Promise.race([document.fonts.ready, new Promise((resolve) => setTimeout(resolve, 3000))]));
  if (name) {
    mkdirSync(resolve(SHOTS, "live"), { recursive: true });
    await shot(page, `live/${name}-${width}`);
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

test.describe("UI checks", () => {
  test.describe.configure({ mode: "serial", timeout: 120_000 });
  let booked: Placed;
  let packed: Placed;

  test.beforeAll(async ({ playwright }) => {
    booked = await packedOrder(playwright, admin, variants[0]!.id);
    const parcel = await bookOrder(admin, booked.orderId);
    await advanceParcel(admin, parcel.id);
    await advanceParcel(admin, parcel.id);
    packed = await packedOrder(playwright, admin, variants[1]!.id);
  });

  for (const width of WIDTHS) {
    test(`order page, book dialog and list at ${width}px`, async ({ page }) => {
      const errors = watch(page);
      await page.setViewportSize({ width, height: 900 });
      await signInAdmin(page, `/admin/orders/${booked.orderId}`);
      await expect(page.getByTestId("parcel").first()).toBeVisible();
      await checks(page, width, errors, "order-with-parcel");

      await page.goto(`/admin/orders/${packed.orderId}`);
      await expect(page.getByTestId("book-courier")).toBeVisible();
      await page.getByTestId("book-courier").click();
      await expect(page.getByTestId("courier-option").first()).toBeVisible();
      await checks(page, width, errors, "book-dialog");
      await page.keyboard.press("Escape");

      await page.goto("/admin/orders");
      await expect(page.locator('[data-testid="order-link"]:visible').first()).toBeVisible();
      await waitLive(page);
      await page.getByLabel("Search orders").fill(packed.number);
      await page.getByRole("checkbox", { name: `Select order ${packed.number}` }).first().check();
      await expect(page.getByTestId("bulk-book")).toBeVisible();
      await checks(page, width, errors, "list-bulk");
    });

    test(`tracking, account order and desk at ${width}px`, async ({ page, context }) => {
      const errors = watch(page);
      await page.setViewportSize({ width, height: 900 });
      await page.goto(trackUrl(booked));
      await expect(page.getByTestId("courier-card")).toBeVisible();
      await expect(page.getByTestId("guest-cancel")).toHaveCount(0); // already handed to the courier
      await checks(page, width, errors, "track-courier");

      await signInCustomer(context, booked.mobile);
      await page.goto(`/account/orders/${booked.orderId}`);
      await expect(page.getByTestId("courier-card")).toBeVisible();
      await checks(page, width, errors, "account-order");

      await page.goto(trackUrl(packed));
      await expect(page.getByTestId("guest-cancel")).toBeVisible();
      await page.getByTestId("guest-cancel").click();
      await expect(page.getByRole("dialog")).toBeVisible();
      await checks(page, width, errors, "guest-cancel-dialog");
      await page.keyboard.press("Escape");

      await signInViaBff(context, state().orderManager.email, state().orderManager.password);
      await page.goto("/admin/orders/new");
      await expect(page.getByTestId("desk-quote-hint")).toBeVisible();
      await checks(page, width, errors, "desk-hint");
    });
  }

  test("the desk preview with a priced order at 360px", async ({ page }) => {
    const errors = watch(page);
    await page.setViewportSize({ width: 360, height: 900 });
    await signInViaBff(page.context(), state().orderManager.email, state().orderManager.password);
    await page.goto("/admin/orders/new");
    await page.getByLabel(/^Mobile number/).fill(newMobile());
    await page.getByLabel("Recipient name").fill("Rafi Ahmed");
    await pick(page, page.getByLabel("Division"), "Dhaka");
    await pick(page, page.getByLabel("District"), "Dhaka");
    await page.getByLabel("Street address").fill("Flat 4B, Road 9, Banani");
    await page.getByLabel("Find a product").fill("JV-TSH-BLA-M");
    await page.getByTestId("desk-product").first().click();
    await page.getByRole("button", { name: "Add JV-TSH-BLA-M" }).click();
    await expect(page.getByTestId("desk-quote")).toHaveAttribute("data-settling", "0", { timeout: 10_000 });
    await checks(page, 360, errors, "desk-quote");
  });
});
