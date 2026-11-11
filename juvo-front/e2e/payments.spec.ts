import { test, expect, type APIRequestContext, type Page, type PlaywrightWorkerArgs } from "@playwright/test";
import { E2E } from "../playwright.config";
import { api, loadState } from "./support/env";
import { apiCheckout, createStaff, disableAllRules, staffToken, type Placed } from "./support/orders";
import { makePng } from "./support/png";
import { addToCart, newMobile, pick, sampleVariants } from "./support/store";
import { signInViaBff } from "./support/ui";

/**
 * Step 9-b (payments front end) against the real stack: the customer pays with the sandbox (success, failure then retry, cancel),
 * the return page never trusts the address, a manual transfer is reviewed by staff, cash on delivery is collected and settled, an
 * advance is recorded, refunds are created and completed, invoices download as PDF, the settings change what the checkout offers,
 * and a limited staff member sees no payment actions.
 */
type Playwright = PlaywrightWorkerArgs["playwright"];
const state = () => loadState();
let admin = "";
let support: { email: string; password: string };

test.describe.configure({ mode: "serial" });

test.beforeAll(async () => {
  admin = await staffToken("admin");
  await disableAllRules(admin);
  support = await createStaff(admin, "support@e2e.test", "Sam Support", "Support", state().branches.ho.id);
});

async function guest(playwright: Playwright): Promise<APIRequestContext> {
  return playwright.request.newContext({ baseURL: E2E.webUrl });
}

/** Places an order the way the shop does (guest, one cheap line) and returns it. */
async function place(playwright: Playwright, method: "COD" | "SANDBOX" | "MANUAL", quantity = 1): Promise<Placed> {
  const request = await guest(playwright);
  const [item] = await sampleVariants(request, 1);
  const own = await guest(playwright);
  const placed = await apiCheckout(own, { lines: [{ variantId: item!.id, quantity }], method });
  await own.dispose();
  await request.dispose();
  return placed;
}

/** The sandbox pay page the pay address leads to; returns once it is on screen. */
async function openSandbox(page: Page, placed: Placed, extra = "") {
  await page.goto(`/pay/${placed.number}?token=${placed.trackingToken}${extra}`);
  await expect(page).toHaveURL(/\/pay\/sandbox\//);
  await expect(page.getByTestId("sandbox-order")).toHaveText(placed.number);
}

const outcome = (page: Page) => page.getByTestId("return-title");

async function trackingOf(playwright: Playwright, placed: Placed) {
  const request = await guest(playwright);
  const response = await request.get(`/api/bff/api/v1/store/orders/track?token=${placed.trackingToken}`);
  const body = (await response.json()) as { status: string; paymentStatus: string };
  await request.dispose();
  return body;
}

async function signInStaff(page: Page, who: { email: string; password: string }, next: string) {
  await signInViaBff(page.context(), who.email, who.password);
  await page.goto(next);
}

async function setPaymentSettings(patch: Record<string, unknown>) {
  const current = await api<{ methods: { code: string; enabled: boolean }[]; codMaxAmount: number; manualInstructions: string | null; autoConfirmPaid: boolean }>("GET", "/api/v1/payments/settings", undefined, admin);
  expect(current.status).toBe(200);
  const body = { methods: current.json.methods.map((m) => ({ code: m.code, enabled: m.enabled })), codMaxAmount: current.json.codMaxAmount, manualInstructions: current.json.manualInstructions, autoConfirmPaid: current.json.autoConfirmPaid, ...patch };
  const saved = await api("PUT", "/api/v1/payments/settings", body, admin);
  expect(saved.status, JSON.stringify(saved.json)).toBe(200);
}

const digits = (text: string | null) => Number((text ?? "").replace(/[^0-9.]/g, ""));

test.describe("Customer pays with the sandbox", () => {
  test("a guest orders with the online method, pays on the branded sandbox page and the order confirms itself", async ({ page, context }) => {
    const [item] = await sampleVariants(context.request, 1);
    await addToCart(context, item!.id, 1);
    const mobile = newMobile();
    await page.goto("/checkout");
    await page.getByLabel(/^Mobile number/).fill(mobile);
    await page.getByLabel("Recipient name").fill("Nadia Rahman");
    await page.getByLabel("Recipient phone").fill(mobile);
    await pick(page, page.getByLabel("Division"), "Dhaka");
    await pick(page, page.getByLabel("District"), "Dhaka");
    await page.getByLabel("Street address").fill("House 12, Road 5, Dhanmondi");
    await expect(page.getByTestId("delivery-charge")).toBeVisible();
    // The checkout shows the server's methods: cash on delivery and the sandbox (the manual transfer is off until it has instructions).
    await expect(page.locator("#co-pay-COD")).toBeVisible();
    await page.locator("#co-pay-SANDBOX").click();
    await page.getByLabel(/I agree to the terms/).click();
    await page.getByTestId("place-order").click();

    // An online order goes straight to its payment: the pay address starts the payment and the sandbox page opens.
    await expect(page).toHaveURL(/\/pay\/sandbox\//);
    await expect(page.getByTestId("sandbox-amount")).toContainText(/\d/);
    await page.getByTestId("sandbox-success").click();

    await expect(page).toHaveURL(/\/pay\/return/);
    await expect(outcome(page)).toHaveAttribute("data-outcome", "paid");
    await expect(outcome(page)).toHaveText("Payment received");
    await expect(page.getByTestId("return-confirmed")).toBeVisible();

    // The order page shows the paid track, no "Pay now", and the invoice with real PDF downloads.
    await page.getByRole("link", { name: "View my order" }).click();
    await expect(page.getByTestId("status-payment")).toHaveText("Paid");
    await expect(page.getByTestId("status-order")).toHaveText("Confirmed");
    await expect(page.getByTestId("payment-due")).toHaveCount(0);
    await expect(async () => {
      await page.reload();
      await expect(page.getByTestId("invoice-box")).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 30_000 });
    await expect(page.getByTestId("invoice-number")).toContainText("INV-");
    await page.getByTestId("invoice-toggle").click();
    await expect(page.getByTestId("invoice-data")).toContainText("Total");
    for (const lang of ["en", "bn"] as const) {
      const href = await page.getByTestId(`invoice-pdf-${lang}`).getAttribute("href");
      const pdf = await page.request.get(href!);
      expect(pdf.status()).toBe(200);
      expect((await pdf.body()).subarray(0, 4).toString()).toBe("%PDF");
    }
  });

  test("a failed payment can be tried again and then succeeds; the failed one stays in the history", async ({ page, playwright }) => {
    const placed = await place(playwright, "SANDBOX");
    await openSandbox(page, placed);
    await page.getByTestId("sandbox-fail").click();
    await expect(page).toHaveURL(/\/pay\/return/);
    await expect(outcome(page)).toHaveAttribute("data-outcome", "failed");
    await expect(page.getByTestId("return-retry")).toBeVisible();

    await page.getByTestId("return-retry").click();
    await expect(page).toHaveURL(/\/pay\/sandbox\//);
    await page.getByTestId("sandbox-fail").click();
    await expect(outcome(page)).toHaveAttribute("data-outcome", "failed");

    // The order's payment track is Failed: the order page offers to try again, and the pay page shows the "try again" choice.
    await page.goto(`/track?token=${placed.trackingToken}`);
    await expect(page.getByTestId("payment-due")).toContainText("did not go through");
    await page.goto(`/pay/${placed.number}?token=${placed.trackingToken}`);
    await expect(page.getByTestId("pay-again")).toBeVisible();
    await page.getByTestId("pay-retry").click();
    await expect(page).toHaveURL(/\/pay\/sandbox\//);
    await page.getByTestId("sandbox-success").click();
    await expect(outcome(page)).toHaveAttribute("data-outcome", "paid");

    // Both payments are in the staff list: the failed one and the paid one.
    const list = await api<{ items: { status: string; orderNumber: string }[] }>("GET", `/api/v1/payments?search=${placed.number}`, undefined, admin);
    expect(list.json.items.map((p) => p.status).sort()).toEqual(["Failed", "Failed", "Paid"]);
  });

  test("a cancelled payment keeps the order open and the order page offers to pay again", async ({ page, playwright }) => {
    const placed = await place(playwright, "SANDBOX");
    await openSandbox(page, placed);
    await page.getByTestId("sandbox-cancel").click();
    await expect(outcome(page)).toHaveAttribute("data-outcome", "cancelled");
    await expect(outcome(page)).toHaveText("Payment cancelled");

    await page.goto(`/track?token=${placed.trackingToken}`);
    await expect(page.getByTestId("status-order")).toHaveText("Order placed");
    await expect(page.getByTestId("payment-due")).toBeVisible();
    // A cancelled attempt leaves the order's payment pending: "Pay now" opens a new sandbox page at once.
    await page.getByTestId("pay-now").click();
    await expect(page).toHaveURL(/\/pay\/sandbox\//);
  });

  test("a sandbox page can be used once", async ({ page, playwright }) => {
    const placed = await place(playwright, "SANDBOX");
    await openSandbox(page, placed);
    const url = page.url();
    await page.getByTestId("sandbox-success").click();
    await expect(outcome(page)).toHaveAttribute("data-outcome", "paid");
    await page.goto(url);
    await expect(page.getByTestId("sandbox-used")).toBeVisible();
    await expect(page.getByTestId("sandbox-success")).toHaveCount(0);
  });

  test("the return page never trusts the address: success words in it change nothing", async ({ page, playwright }) => {
    const placed = await place(playwright, "SANDBOX");
    // The payment is started (so it exists and is pending at the gateway) but the customer never pays.
    const request = await guest(playwright);
    const started = await request.post("/api/bff/api/v1/store/payments/initiate", { data: { orderNumber: placed.number, trackingToken: placed.trackingToken, method: null, returnUrl: "/pay/return" } });
    expect(started.status(), await started.text()).toBe(200);
    const session = (await started.json()) as { paymentId: string };
    await request.dispose();

    await page.goto("/track");
    await page.evaluate(([n, t]) => window.sessionStorage.setItem(`juvo_order_${n}`, JSON.stringify({ trackingToken: t })), [placed.number, placed.trackingToken] as const);
    await page.goto(`/pay/return?order=${placed.number}&payment=${session.paymentId}&status=success&paid=true&outcome=paid&redirect_status=succeeded`);
    await expect(page.getByTestId("return-order")).toHaveText(placed.number);
    await expect(outcome(page)).toHaveAttribute("data-outcome", "pending");
    await expect(outcome(page)).toHaveText("Checking your payment");
    // The shop's own records say the order is still unpaid.
    await expect.poll(async () => (await trackingOf(playwright, placed)).paymentStatus).toBe("Pending");
    // A made-up payment id proves nothing either.
    await page.goto(`/pay/return?order=${placed.number}&payment=00000000-0000-0000-0000-000000000000&status=success`);
    await expect(outcome(page)).not.toHaveAttribute("data-outcome", "paid");
  });

  test("without a way to prove the order the pay page asks to open the private link", async ({ page, playwright }) => {
    const placed = await place(playwright, "SANDBOX");
    await page.goto(`/pay/${placed.number}`);
    await expect(page.getByTestId("pay-no-access")).toBeVisible();
    await page.goto(`/pay/${placed.number}?token=not-the-token`);
    await expect(page.getByTestId("pay-no-access")).toBeVisible();
  });
});

test.describe("Manual transfer, reviewed by staff", () => {
  test.beforeAll(async () => {
    await setPaymentSettings({ methods: [{ code: "MANUAL", enabled: true }], manualInstructions: "bKash 01711-000000 (Juvo BD). Write the order number as the reference." });
  });
  test.afterAll(async () => {
    await setPaymentSettings({ methods: [{ code: "MANUAL", enabled: false }] });
  });

  test("the customer sends the reference and a receipt; staff approve and the order confirms", async ({ page, playwright }) => {
    const placed = await place(playwright, "MANUAL");
    await page.goto(`/pay/${placed.number}?token=${placed.trackingToken}`);
    await expect(page.getByTestId("manual-instructions")).toContainText("bKash 01711-000000");

    // A missing reference is asked for, a wrong file type is refused.
    await page.getByTestId("manual-submit").click();
    await expect(page.getByText("Please enter the transaction reference.")).toBeVisible();
    await page.getByLabel(/^Transaction reference/).fill(`TXN${Date.now()}`);
    await page.getByLabel(/^Name or number you sent from/).fill("Nadia Rahman");
    await page.getByLabel(/^Screenshot or receipt/).setInputFiles({ name: "note.txt", mimeType: "text/plain", buffer: Buffer.from("hello") });
    await page.getByTestId("manual-submit").click();
    await expect(page.getByText("Choose a JPEG, PNG, WebP or PDF file.")).toBeVisible();
    await page.getByLabel(/^Screenshot or receipt/).setInputFiles({ name: "receipt.png", mimeType: "image/png", buffer: makePng(40, 40) });
    await page.getByTestId("manual-submit").click();
    await expect(page.getByTestId("manual-review")).toBeVisible();
    await expect(page.getByText("Your receipt was saved.").first()).toBeVisible();

    await page.goto(`/track?token=${placed.trackingToken}`);
    await expect(page.getByTestId("status-payment")).toHaveText("Payment under review");
    await expect(page.getByTestId("payment-review")).toBeVisible();

    // Staff: the review queue, the proof and the approval.
    await signInStaff(page, state().admin, "/admin/payments");
    await page.getByTestId("review-queue").click();
    await page.getByRole("searchbox").fill(placed.number);
    await page.locator('[data-testid="payment-link"]:visible', { hasText: placed.number }).first().click();
    await expect(page.getByTestId("review-banner")).toContainText("Nadia Rahman");
    await expect(page.getByTestId("proof-link")).toBeVisible();
    await page.getByTestId("action-approve").click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel("Note (optional)").fill("Checked in the bKash app");
    await dialog.getByTestId("dialog-confirm").click();
    await expect(page.getByText("The payment is approved.").first()).toBeVisible();
    await expect(page.getByTestId("action-approve")).toHaveCount(0);
    await expect(page.getByTestId("payment-events")).toContainText("Checked in the bKash app");

    await expect.poll(async () => trackingOf(playwright, placed)).toMatchObject({ paymentStatus: "Paid", status: "Confirmed" });
  });

  test("a rejected transfer cancels the order, and the reason is required", async ({ page, playwright }) => {
    const placed = await place(playwright, "MANUAL");
    const request = await guest(playwright);
    const sent = await request.post("/api/bff/api/v1/store/payments/manual", { data: { orderNumber: placed.number, trackingToken: placed.trackingToken, reference: `TXN${Date.now()}`, payerName: "Bad Payer" } });
    expect(sent.status(), await sent.text()).toBe(200);
    await request.dispose();

    await signInStaff(page, state().admin, "/admin/payments");
    await page.getByTestId("review-queue").click();
    await page.getByRole("searchbox").fill(placed.number);
    await page.locator('[data-testid="payment-link"]:visible', { hasText: placed.number }).first().click();
    await page.getByTestId("action-reject").click();
    const dialog = page.getByRole("dialog");
    await dialog.getByTestId("dialog-confirm").click();
    await expect(dialog.getByText("This is required.")).toBeVisible();
    await dialog.getByLabel("Reason").fill("The money never arrived");
    await dialog.getByTestId("dialog-confirm").click();
    await expect(page.getByText("The payment is rejected and the order is cancelled.").first()).toBeVisible();
    await expect(page.getByTestId("payment-events")).toContainText("The money never arrived");
    await expect.poll(async () => trackingOf(playwright, placed)).toMatchObject({ status: "Cancelled" });
  });
});

test.describe("Cash on delivery and advance", () => {
  test("collect and settle the cash of a delivered order", async ({ page, playwright }) => {
    const placed = await place(playwright, "COD");
    for (const step of ["confirm", "pack"]) {
      const done = await api("POST", `/api/v1/orders/${placed.orderId}/${step}`, step === "confirm" ? { advanceReceived: false } : undefined, admin);
      expect(done.status, JSON.stringify(done.json)).toBe(200);
    }
    for (const to of ["HandedToCourier", "InTransit", "OutForDelivery"]) {
      const moved = await api("POST", `/api/v1/orders/${placed.orderId}/transition`, { to, reason: null }, admin);
      expect(moved.status, JSON.stringify(moved.json)).toBe(200);
    }

    await signInStaff(page, state().admin, `/admin/orders/${placed.orderId}`);
    await expect(page.getByTestId("cod-settle")).toHaveCount(0);
    await page.getByTestId("cod-collect").click();
    await page.getByRole("dialog").getByLabel("Courier receipt (optional)").fill("RCPT-1");
    await page.getByRole("dialog").getByTestId("dialog-confirm").click();
    await expect(page.getByText("The cash is recorded as collected.").first()).toBeVisible();
    await expect(page.getByTestId("status-payment")).toHaveText("Collected by courier");
    await expect(page.getByTestId("order-payments")).toContainText("Cash on delivery");

    await page.getByTestId("cod-settle").click();
    const dialog = page.getByRole("dialog");
    await dialog.getByTestId("dialog-confirm").click();
    await expect(dialog.getByText("This is required.")).toBeVisible();
    await dialog.getByLabel("Settlement reference").fill("DEP-77");
    await dialog.getByTestId("dialog-confirm").click();
    await expect(page.getByText("The cash is settled.").first()).toBeVisible();
    await expect(page.getByTestId("status-payment")).toHaveText("Settled");
    await expect(page.getByTestId("cod-settle")).toHaveCount(0);
    await expect(page.getByTestId("cod-collect")).toHaveCount(0);
  });

  test("an advance is recorded for a large cash order and counts toward what is required", async ({ page, playwright }) => {
    const request = await guest(playwright);
    const all = await sampleVariants(request, 12);
    await request.dispose();
    const dear = [...all].sort((a, b) => b.price - a.price).slice(0, 4);
    const own = await guest(playwright);
    const big = await apiCheckout(own, { lines: dear.map((i) => ({ variantId: i.id, quantity: 3 })) });
    await own.dispose();
    expect(big.payNow, "the basket must need an advance").toBeGreaterThan(0);

    await signInStaff(page, state().admin, `/admin/orders/${big.orderId}`);
    await expect(page.getByTestId("advance-state")).toContainText("An advance is needed");
    await page.getByTestId("record-advance").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByLabel(/^Amount/)).toHaveValue(String(big.payNow));
    await dialog.getByLabel("Reference (optional)").fill("BKASH-ADV-1");
    await dialog.getByTestId("dialog-confirm").click();
    await expect(page.getByText("The advance is recorded.").first()).toBeVisible();
    await expect(page.getByTestId("advance-state")).toContainText("is received");
    await expect(page.getByTestId("record-advance")).toHaveCount(0);
    await expect(page.getByTestId("order-payments")).toContainText("Advance");

    // With the advance in, the order confirms without the old "I received the advance" tick.
    await page.getByTestId("action-confirm").click();
    await expect(page.getByTestId("status-order")).toHaveText("Confirmed");
  });
});

test.describe("Refunds, ledger and credit notes", () => {
  test("a manual refund is created, completed with a credit note, and the rest goes back through the gateway", async ({ page, playwright }) => {
    const placed = await place(playwright, "SANDBOX", 1);
    const request = await guest(playwright);
    const started = await request.post("/api/bff/api/v1/store/payments/initiate", { data: { orderNumber: placed.number, trackingToken: placed.trackingToken, method: null, returnUrl: "/pay/return" } });
    const session = (await started.json()) as { sandboxSessionId: string };
    const decided = await request.post(`/api/bff/api/v1/store/payments/sandbox/sessions/${session.sandboxSessionId}/complete`, { data: { outcome: "success", sendWebhook: true } });
    expect(decided.status(), await decided.text()).toBe(200);
    await request.dispose();
    await expect.poll(async () => (await trackingOf(playwright, placed)).paymentStatus).toBe("Paid");

    const list = await api<{ items: { id: string }[] }>("GET", `/api/v1/payments?search=${placed.number}`, undefined, admin);
    const paymentId = list.json.items[0]!.id;
    await signInStaff(page, state().admin, `/admin/payments/${paymentId}`);
    const refundable = digits(await page.getByTestId("fact-refundable").textContent());
    expect(refundable).toBeGreaterThan(100);

    // Manual refund of 100: pending until completed.
    await page.getByTestId("action-refund").click();
    let dialog = page.getByRole("dialog");
    await dialog.getByLabel(/^Amount/).fill("100");
    await dialog.getByRole("radio", { name: /Paid back by hand/ }).click();
    await dialog.getByLabel("Reason").fill("Customer returned one item");
    await dialog.getByTestId("dialog-confirm").click();
    await expect(page.getByText("The refund was created.").first()).toBeVisible();
    await expect(page.getByTestId("payment-refunds")).toContainText("Pending");

    await page.getByTestId("refund-complete").click();
    dialog = page.getByRole("dialog");
    await dialog.getByLabel("Reference").fill("BANK-REF-9");
    await dialog.getByTestId("dialog-confirm").click();
    await expect(page.getByText("The refund is completed and the credit note is issued.").first()).toBeVisible();
    await expect(page.getByTestId("payment-refunds")).toContainText("CN-");
    await expect(page.getByTestId("payment-refunds")).toContainText("Completed");

    // The rest through the gateway (the sandbox completes it at once).
    await page.getByTestId("action-refund").click();
    dialog = page.getByRole("dialog");
    await expect(dialog.getByLabel(/^Amount/)).toHaveValue(String(refundable - 100));
    await dialog.getByRole("radio", { name: /Through the gateway/ }).click();
    await dialog.getByLabel("Reason").fill("Order cancelled");
    await dialog.getByTestId("dialog-confirm").click();
    await expect(page.getByText("The refund was created.").first()).toBeVisible();
    await expect(page.getByTestId("action-refund")).toHaveCount(0);
    await expect(page.getByTestId("payment-facts")).toBeVisible();

    // Both refunds are completed and each has a credit note, which downloads as a PDF.
    const refunds = await api<{ items: { status: string; creditNoteNumber: string | null }[] }>("GET", `/api/v1/payments/refunds?orderId=${placed.orderId}`, undefined, admin);
    expect(refunds.json.items.map((r) => r.status)).toEqual(["Completed", "Completed"]);
    const number = refunds.json.items[0]!.creditNoteNumber!;
    expect(number).toMatch(/^CN-/);
    const credit = await api<{ items: { id: string }[] }>("GET", `/api/v1/billing/credit-notes?search=${number}`, undefined, admin);
    const pdf = await page.request.get(`/api/bff/api/v1/billing/credit-notes/${credit.json.items[0]!.id}/pdf?lang=en`);
    expect(pdf.status()).toBe(200);
    expect((await pdf.body()).subarray(0, 4).toString()).toBe("%PDF");
  });

  test("the ledger shows money in and out and matches the server", async ({ page }) => {
    const server = await api<{ onlineReceived: number | string; onlineRefunded: number | string; codCollected: number | string; codSettled: number | string }>("GET", "/api/v1/payments/ledger", undefined, admin);
    expect(server.status).toBe(200);
    await signInStaff(page, state().admin, "/admin/payments?tab=ledger");
    await expect(page.getByTestId("ledger-tiles")).toBeVisible();
    await expect.poll(async () => digits(await page.getByTestId("ledger-online-received").textContent())).toBe(Number(server.json.onlineReceived));
    expect(digits(await page.getByTestId("ledger-online-refunded").textContent())).toBe(Number(server.json.onlineRefunded));
    expect(digits(await page.getByTestId("ledger-cod-settled").textContent())).toBe(Number(server.json.codSettled));
    expect(Number(server.json.onlineReceived)).toBeGreaterThan(0);
    await expect(page.getByTestId("ledger-methods")).toContainText("Online payment (sandbox)");
    // A past range with no payments shows nothing received.
    await page.locator("#ledger-to").fill("2020-01-02");
    await page.locator("#ledger-from").fill("2020-01-01");
    await expect.poll(async () => digits(await page.getByTestId("ledger-online-received").textContent())).toBe(0);
  });
});

test.describe("Settings and what the checkout offers", () => {
  test("switching a method and the cash limit changes the customer's choices, with the reason when one is not available", async ({ page, context }) => {
    await signInStaff(page, state().admin, "/admin/payments?tab=settings");
    const form = page.getByRole("form", { name: "Payment settings" });
    await expect(form).toBeVisible();
    await expect(page.getByTestId("method-settings")).toContainText("Online payment (sandbox)");

    // The limit: with 1 taka no cash order is possible, so cash on delivery shows as not available with the reason.
    await page.getByLabel("Cash on delivery limit (BDT)").fill("1");
    await page.getByTestId("settings-save").click();
    await expect(page.getByText("Payment settings saved.").first()).toBeVisible();

    const [item] = await sampleVariants(context.request, 1);
    await addToCart(context, item!.id, 1);
    await page.goto("/checkout");
    await expect(page.locator("#co-pay-SANDBOX")).toBeEnabled();
    await expect(page.locator("#co-pay-COD")).toBeDisabled();

    // Back to no limit; the customer-facing list reads the same settings.
    await page.goto("/admin/payments?tab=settings");
    await page.getByLabel("Cash on delivery limit (BDT)").fill("0");
    await page.getByTestId("settings-save").click();
    await expect(page.getByText("Payment settings saved.").first()).toBeVisible();
    const methods = await context.request.get("/api/bff/api/v1/store/payment-methods?country=BD&amount=500");
    const list = (await methods.json()) as { code: string; available: boolean }[];
    expect(list.find((m) => m.code === "COD")?.available).toBe(true);
    expect(list.map((m) => m.code)).not.toContain("MANUAL");
  });
});

test.describe("Permissions", () => {
  test("support can look at payments but sees no actions and no settings", async ({ page, playwright }) => {
    const placed = await place(playwright, "SANDBOX");
    const request = await guest(playwright);
    const started = await request.post("/api/bff/api/v1/store/payments/initiate", { data: { orderNumber: placed.number, trackingToken: placed.trackingToken, method: null, returnUrl: "/pay/return" } });
    expect(started.status()).toBe(200);
    await request.dispose();

    await signInStaff(page, support, "/admin/payments");
    await expect(page.getByRole("tab", { name: "Payments" })).toBeVisible();
    await expect(page.getByRole("tab", { name: "Settings" })).toHaveCount(0);
    await expect(page.getByRole("tab", { name: "Ledger" })).toHaveCount(0);
    await expect(page.getByRole("tab", { name: "Refunds" })).toHaveCount(0);
    await page.getByRole("searchbox").fill(placed.number);
    await page.locator('[data-testid="payment-link"]:visible', { hasText: placed.number }).first().click();
    await expect(page.getByTestId("payment-facts")).toBeVisible();
    for (const id of ["action-approve", "action-reject", "action-refund", "action-settle"]) await expect(page.getByTestId(id)).toHaveCount(0);

    // The API refuses what the screen hides.
    const login = await api<{ accessToken: string }>("POST", "/api/v1/auth/login", { identifier: support.email, password: support.password });
    const settings = await api("GET", "/api/v1/payments/settings", undefined, login.json.accessToken);
    expect(settings.status).toBe(403);
  });

  test("a person without payment permissions is sent away", async ({ page }) => {
    const token = admin;
    const editor = await createStaff(token, "editor-pay@e2e.test", "Eddie Editor", "Content Editor", state().branches.ho.id);
    await signInViaBff(page.context(), editor.email, editor.password);
    await page.goto("/admin/payments");
    await expect(page).toHaveURL(/forbidden/);
  });
});
