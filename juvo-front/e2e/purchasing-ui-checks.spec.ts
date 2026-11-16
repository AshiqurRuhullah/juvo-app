import { test, expect, type BrowserContext, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { call } from "./support/cod";
import { adminAccess } from "./support/returns";
import { hoId, makePo, makeSupplier, receive, sampleProduct, today, unique, type PoRef, type Product } from "./support/ops";
import { settleBounded } from "./support/couriers-ui";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The purchasing screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console
 * errors. Screenshots go to docs/ui-checks/purchasing/.
 */
const WIDTHS = [360, 768, 1280] as const;

let draft: PoRef;
let partly: PoRef;
let cancelled: PoRef;
let product: Product;

test.beforeAll(async () => {
  test.setTimeout(240_000);
  mkdirSync(resolve(SHOTS, "purchasing"), { recursive: true });
  const admin = await adminAccess();
  product = await sampleProduct();
  const [v1, v2] = product.variants;
  const supplier = await makeSupplier(unique("UI Supplier"));
  draft = await makePo(supplier.id, [
    { variantId: v1!.id, quantity: 12, unitCost: 450 },
    { variantId: v2!.id, quantity: 8, unitCost: 515.5 },
  ]);
  partly = await makePo(supplier.id, [
    { variantId: v1!.id, quantity: 10, unitCost: 300 },
    { variantId: v2!.id, quantity: 6, unitCost: 120.25 },
  ], "Sent");
  await receive(partly, { [v1!.id]: 4 });
  await call("POST", `/api/v1/purchasing/orders/${partly.id}/invoice`, { token: admin, body: { number: "INV-UI-1", invoiceDate: today(), amount: 1900 }, expect: 200 });
  await call("POST", `/api/v1/purchasing/orders/${partly.id}/payments`, { token: admin, body: { amount: 500, method: "Bank", reference: "TRX-UI-1" }, expect: 200 });
  cancelled = await makePo(supplier.id, [{ variantId: v1!.id, quantity: 1, unitCost: 10 }], "Sent");
  await call("POST", `/api/v1/purchasing/orders/${cancelled.id}/cancel`, { token: admin, body: { reason: "Ordered twice" }, expect: 200 });
  await call("PUT", "/api/v1/inventory/reorder-levels", { token: admin, body: { branchId: hoId(), variantId: v2!.id, reorderLevel: 100000 }, expect: 200 });
});

test.afterAll(async () => {
  const admin = await adminAccess();
  await call("PUT", "/api/v1/inventory/reorder-levels", { token: admin, body: { branchId: hoId(), variantId: product.variants[1]!.id, reorderLevel: 0 } });
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  const pending = await settleBounded(page);
  if (pending.length) console.log(`fonts still loading at ${width}px: ${pending.join(", ")}`);
  if (name) await shot(page, `purchasing/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

async function open(page: Page, context: BrowserContext, width: number) {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width, height: 900 });
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  return errors;
}

for (const width of WIDTHS) {
  test.describe(`${width}px`, () => {
    test("purchase order list", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto("/admin/purchasing");
      await expect(page.getByRole("link", { name: partly.number, exact: true }).first()).toBeVisible();
      await checks(page, width, errors, `list-${width}`);
    });

    test("suppliers, receipts and suggestions", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto("/admin/purchasing?tab=suppliers");
      await expect(page.getByTestId("new-supplier")).toBeVisible();
      await expect(page.getByText(/UI Supplier/).filter({ visible: true }).first()).toBeVisible(); // the table or the phone cards, whichever shows
      await checks(page, width, errors, `suppliers-${width}`);
      await page.goto("/admin/purchasing?tab=receipts");
      await expect(page.getByText(/GRN-/).filter({ visible: true }).first()).toBeVisible();
      await checks(page, width, errors, `receipts-${width}`);
      await page.goto("/admin/purchasing?tab=suggestions");
      await expect(page.getByTestId("suggestion-group").first()).toBeVisible();
      await checks(page, width, errors, `suggestions-${width}`);
    });

    test("new purchase order and the draft detail", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto(`/admin/purchasing/orders/${draft.id}/edit`);
      await expect(page.getByTestId("po-line")).toHaveCount(2);
      await checks(page, width, errors, `editor-${width}`);
      await page.goto(`/admin/purchasing/orders/${draft.id}`);
      await expect(page.getByTestId("po-summary")).toBeVisible();
      await expect(page.getByTestId("po-send")).toBeVisible();
      await checks(page, width, errors, `detail-draft-${width}`);
    });

    test("partly received detail with invoice, payment and history", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto(`/admin/purchasing/orders/${partly.id}`);
      await expect(page.getByTestId("invoice-line")).toBeVisible();
      await expect(page.getByTestId("po-payments").getByRole("listitem")).toHaveCount(1);
      await checks(page, width, errors, `detail-partly-${width}`);
    });

    test("receive dialog with an over-receipt error", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto(`/admin/purchasing/orders/${partly.id}`);
      await page.getByTestId("po-receive").click();
      await page.locator(`#rc-a-${partly.lines[0]!.id}`).fill("99");
      await page.getByTestId("receive-confirm").click();
      await expect(page.getByText(/are still expected on this line/)).toBeVisible();
      await page.waitForTimeout(500); // the dialog's opening animation
      await checks(page, width, errors, `receive-dialog-${width}`);
    });

    test("cancelled detail", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto(`/admin/purchasing/orders/${cancelled.id}`);
      await expect(page.getByText(/This order was cancelled/)).toBeVisible();
      await checks(page, width, errors, `detail-cancelled-${width}`);
    });

    test("print view", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto(`/admin/purchasing/orders/${partly.id}/print`);
      await expect(page.getByTestId("po-sheet")).toBeVisible();
      await checks(page, width, errors, `print-${width}`);
    });
  });
}

test("dark theme", async ({ page, context }) => {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto(`/admin/purchasing/orders/${partly.id}`);
  await expect(page.getByTestId("po-summary")).toBeVisible();
  await checks(page, 1280, errors, "detail-dark");
  await page.goto("/admin/purchasing");
  await expect(page.getByRole("link", { name: partly.number, exact: true }).first()).toBeVisible();
  await checks(page, 1280, errors, "list-dark");
});
