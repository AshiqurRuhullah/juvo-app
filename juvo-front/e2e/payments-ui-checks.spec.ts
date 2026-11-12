import { test, expect, type APIRequestContext, type Page, type PlaywrightWorkerArgs } from "@playwright/test";
import { E2E } from "../playwright.config";
import { api, loadState } from "./support/env";
import { apiCheckout, disableAllRules, staffToken, type Placed } from "./support/orders";
import { sampleVariants, signInCustomer } from "./support/store";
import { axeViolations, horizontalOverflow, settle, shot, smallTapTargets, SHOTS, signInViaBff } from "./support/ui";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";

/**
 * The payment screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console
 * errors. Key screens are kept as screenshots in docs/ui-checks/payments.
 */
type Playwright = PlaywrightWorkerArgs["playwright"];
const WIDTHS = [360, 768, 1280] as const;
let admin = "";
let paid: Placed;
let cancelled: Placed;
let waiting: Placed;
let manual: Placed;
let sandboxSession = "";
let waitingPaymentId = "";
let paidPaymentId = "";

async function guest(playwright: Playwright): Promise<APIRequestContext> {
  return playwright.request.newContext({ baseURL: E2E.webUrl });
}

async function start(request: APIRequestContext, placed: Placed) {
  const started = await request.post("/api/bff/api/v1/store/payments/initiate", { data: { orderNumber: placed.number, trackingToken: placed.trackingToken, method: null, returnUrl: "/pay/return" } });
  expect(started.status(), await started.text()).toBe(200);
  return (await started.json()) as { paymentId: string; sandboxSessionId: string };
}

async function decide(request: APIRequestContext, sessionId: string, outcome: "success" | "fail" | "cancel") {
  const done = await request.post(`/api/bff/api/v1/store/payments/sandbox/sessions/${sessionId}/complete`, { data: { outcome, sendWebhook: true } });
  expect(done.status(), await done.text()).toBe(200);
}

test.beforeAll(async ({ playwright }) => {
  admin = await staffToken("admin");
  await disableAllRules(admin);
  const current = await api<{ methods: { code: string; enabled: boolean }[]; codMaxAmount: number; manualInstructions: string | null; autoConfirmPaid: boolean }>("GET", "/api/v1/payments/settings", undefined, admin);
  const body = { methods: current.json.methods.map((m) => ({ code: m.code, enabled: m.code === "MANUAL" ? true : m.enabled })), codMaxAmount: current.json.codMaxAmount, manualInstructions: "bKash 01711-000000 (Juvo BD). Write the order number as the reference.", autoConfirmPaid: current.json.autoConfirmPaid };
  expect((await api("PUT", "/api/v1/payments/settings", body, admin)).status).toBe(200);

  const items = (await sampleVariants(await guest(playwright), 8)).slice(-1);
  const place = async (method: "SANDBOX" | "MANUAL") => {
    const own = await guest(playwright);
    const placed = await apiCheckout(own, { lines: [{ variantId: items[0]!.id, quantity: 1 }], method });
    await own.dispose();
    return placed;
  };
  const request = await guest(playwright);
  paid = await place("SANDBOX");
  const first = await start(request, paid);
  paidPaymentId = first.paymentId;
  await decide(request, first.sandboxSessionId, "success");
  cancelled = await place("SANDBOX");
  await decide(request, (await start(request, cancelled)).sandboxSessionId, "fail");
  waiting = await place("SANDBOX");
  const second = await start(request, waiting);
  sandboxSession = second.sandboxSessionId;
  waitingPaymentId = second.paymentId;
  manual = await place("MANUAL");
  await request.dispose();
});

test.afterAll(async () => {
  const current = await api<{ methods: { code: string; enabled: boolean }[]; codMaxAmount: number; manualInstructions: string | null; autoConfirmPaid: boolean }>("GET", "/api/v1/payments/settings", undefined, admin);
  await api("PUT", "/api/v1/payments/settings", { methods: current.json.methods.map((m) => ({ code: m.code, enabled: m.code === "MANUAL" ? false : m.enabled })), codMaxAmount: current.json.codMaxAmount, manualInstructions: current.json.manualInstructions, autoConfirmPaid: current.json.autoConfirmPaid }, admin);
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settle(page);
  if (name) {
    mkdirSync(resolve(SHOTS, "payments"), { recursive: true });
    await shot(page, `payments/${name}`);
  }
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page, [".leaflet-container"]);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/tile\.openstreetmap|Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

const watch = (page: Page) => {
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  return errors;
};

for (const width of WIDTHS) {
  const keep = (name: string) => (width === 360 || width === 1280 ? `${name}-${width}` : undefined);

  test(`customer pay pages at ${width}px`, async ({ page }) => {
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });

    await page.goto(`/pay/sandbox/${sandboxSession}?payment=${waitingPaymentId}`);
    await expect(page.getByTestId("sandbox-success")).toBeVisible();
    await checks(page, width, errors, keep("sandbox"));

    await page.goto(`/pay/${cancelled.number}?token=${cancelled.trackingToken}`);
    await expect(page.getByTestId("pay-again")).toBeVisible();
    await checks(page, width, errors, keep("pay-again"));

    await page.goto(`/pay/${manual.number}?token=${manual.trackingToken}`);
    await expect(page.getByTestId("manual-instructions")).toBeVisible();
    await checks(page, width, errors, keep("manual-form"));

    await page.goto("/track");
    await page.evaluate(([n, t]) => window.sessionStorage.setItem(`juvo_order_${n}`, JSON.stringify({ trackingToken: t })), [paid.number, paid.trackingToken] as const);
    await page.goto(`/pay/return?order=${paid.number}&payment=${paidPaymentId}`);
    await expect(page.getByTestId("return-title")).toHaveAttribute("data-outcome", "paid");
    await checks(page, width, errors, keep("return-paid"));

    await page.goto(`/track?token=${paid.trackingToken}`);
    await expect(async () => {
      await page.reload();
      await expect(page.getByTestId("invoice-box")).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 30_000 });
    await checks(page, width, errors, keep("track-invoice"));

    await page.goto(`/track?token=${cancelled.trackingToken}`);
    await expect(page.getByTestId("payment-due")).toBeVisible();
    await checks(page, width, errors, keep("track-pay-now"));
  });

  test(`customer order page with invoice at ${width}px`, async ({ page, context }) => {
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });
    await signInCustomer(context);
    const item = (await sampleVariants(context.request, 8)).at(-1);
    const placed = await apiCheckout(context.request, { lines: [{ variantId: item!.id, quantity: 1 }], method: "SANDBOX", signedIn: true });
    const started = await start(context.request, { ...placed, trackingToken: null });
    await decide(context.request, started.sandboxSessionId, "success");
    await page.goto(`/account/orders/${placed.orderId}`);
    await expect(page.getByTestId("status-payment")).toHaveText("Paid");
    await expect(async () => {
      await page.reload();
      await expect(page.getByTestId("invoice-box")).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 30_000 });
    await checks(page, width, errors, keep("my-order"));
  });

  test(`admin payments at ${width}px`, async ({ page }) => {
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });
    await signInViaBff(page.context(), loadState().admin.email, loadState().admin.password);

    await page.goto("/admin/payments");
    await expect(page.locator('[data-testid="payment-link"]:visible').first()).toBeVisible();
    await checks(page, width, errors, keep("admin-list"));

    await page.goto(`/admin/payments/${paidPaymentId}`);
    await expect(page.getByTestId("payment-facts")).toBeVisible();
    await checks(page, width, errors, keep("admin-detail"));

    await page.goto("/admin/payments?tab=ledger");
    await expect(page.getByTestId("ledger-tiles")).toBeVisible();
    await checks(page, width, errors, keep("admin-ledger"));

    await page.goto("/admin/payments?tab=settings");
    await expect(page.getByTestId("method-settings")).toBeVisible();
    await checks(page, width, errors, keep("admin-settings"));

    await page.goto("/admin/payments?tab=refunds");
    await expect(page.getByRole("tab", { name: "Refunds", selected: true })).toBeVisible();
    await checks(page, width, errors);

    await page.goto(`/admin/orders/${paid.orderId}`);
    await expect(page.getByTestId("order-payments")).toBeVisible();
    await checks(page, width, errors, keep("admin-order-payments"));
  });
}
