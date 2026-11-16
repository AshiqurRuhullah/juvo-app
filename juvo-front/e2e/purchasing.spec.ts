import { test, expect, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { call } from "./support/cod";
import { adminAccess, staffAccess, staffWithPermissions } from "./support/returns";
import { costOf, digits, getPo, hoId, makePo, makeSupplier, onHand, receive, sampleProduct, unique, type Product } from "./support/ops";
import { signInViaBff } from "./support/ui";

/**
 * Step 11A (purchasing front end) against the real stack: suppliers, a purchase order from draft to paid with the stock and cost
 * effects checked through the inventory and catalog APIs, over-receipt refused, close short, cancel, the print view, the cost
 * button that only people with the cost permission get, and roles with fewer permissions.
 */
test.describe.configure({ mode: "serial" });

let admin = "";
test.beforeAll(async () => {
  admin = await adminAccess();
});

async function signInAdmin(page: Page, next: string) {
  const who = loadState().admin;
  await signInViaBff(page.context(), who.email, who.password);
  await page.goto(next);
}

async function signInAs(page: Page, who: { email: string; password: string }, next: string) {
  await signInViaBff(page.context(), who.email, who.password);
  await page.goto(next);
}

const status = (page: Page) => page.getByTestId("po-status").first();

test("a supplier is created and then changed", async ({ page }) => {
  const name = unique("Dhaka Fabrics");
  await signInAdmin(page, "/admin/purchasing?tab=suppliers");
  await page.getByTestId("new-supplier").click();
  await page.locator("#sp-name").fill(name);
  await page.locator("#sp-contact").fill("Rahim Mia");
  await page.locator("#sp-phone").fill("01712345678");
  await page.locator("#sp-terms").fill("45");
  await page.getByTestId("supplier-save").click();
  const row = page.getByRole("row").filter({ hasText: name });
  await expect(row).toBeVisible();
  await expect(row).toContainText("Rahim Mia");
  await expect(row).toContainText("45 days");

  await row.getByRole("button", { name: `Edit ${name}` }).click();
  await page.locator("#sp-contact").fill("Salma Begum");
  await page.locator("#sp-terms").fill("10");
  await page.getByTestId("supplier-save").click();
  await expect(row).toContainText("Salma Begum");
  await expect(row).toContainText("10 days");

  const list = await call<{ items: { name: string; paymentTermsDays: number }[] }>("GET", `/api/v1/suppliers?search=${encodeURIComponent(name)}`, { token: admin, expect: 200 });
  expect(list.json.items.find((s) => s.name === name)?.paymentTermsDays).toBe(10);

  // A name that is already used is refused with the API's own text.
  await page.getByTestId("new-supplier").click();
  await page.locator("#sp-name").fill(name);
  await page.getByTestId("supplier-save").click();
  await expect(page.getByText("A supplier with this name already exists.").first()).toBeVisible();
});

test.describe("purchase order from draft to paid", () => {
  let product: Product;
  let supplier: { id: string; name: string };
  let poId = "";
  const lineIds: Record<string, string> = {};

  test("create the draft in the browser with totals worked out as you type", async ({ page }) => {
    product = await sampleProduct();
    supplier = await makeSupplier(unique("Order Supplier"));
    await signInAdmin(page, "/admin/purchasing/orders/new");
    await page.locator("#po-supplier").click();
    await page.getByRole("option", { name: supplier.name }).click();
    await page.locator("#po-branch").click();
    await page.getByRole("option", { name: /^HO/ }).click();

    await page.locator("#po-search").fill(product.nameEn);
    await page.getByRole("button", { name: new RegExp(`^${product.nameEn.replace(/[.*+?^${}()|[\]\\]/g, "\\$&")}`) }).first().click();
    for (const v of product.variants.slice(0, 2)) await page.getByRole("button", { name: `Add ${v.sku}` }).click();
    const [v1, v2] = product.variants;
    await expect(page.getByTestId("po-line")).toHaveCount(2);
    await page.locator(`#po-qty-${v1!.id}`).fill("5");
    await page.locator(`#po-cost-${v1!.id}`).fill("100");
    await page.locator(`#po-qty-${v2!.id}`).fill("3");
    await page.locator(`#po-cost-${v2!.id}`).fill("50.50");
    // 5 x 100 + 3 x 50.50 = 651.50
    expect(digits(await page.getByTestId("po-draft-total").textContent())).toBe(651.5);
    await page.getByTestId("po-save").click();

    await expect(page).toHaveURL(/\/admin\/purchasing\/orders\/[0-9a-f-]{36}$/);
    poId = page.url().split("/").pop()!;
    await expect(status(page)).toHaveText("Draft");
    expect(digits(await page.getByTestId("sum-total").textContent())).toBe(651.5);
    const po = await getPo(poId);
    for (const l of po.lines) lineIds[l.variantId] = l.id;
    await expect(page.getByTestId("po-receive")).toHaveCount(0); // a draft is not received
  });

  test("edit the draft, then send it", async ({ page }) => {
    await signInAdmin(page, `/admin/purchasing/orders/${poId}`);
    await page.getByTestId("po-edit").click();
    await expect(page).toHaveURL(/\/edit$/);
    await page.locator(`#po-cost-${product.variants[1]!.id}`).fill("50");
    await page.getByTestId("po-save").click();
    await expect(status(page)).toHaveText("Draft");
    expect(digits(await page.getByTestId("sum-total").textContent())).toBe(650); // 5 x 100 + 3 x 50

    await page.getByTestId("po-send").click();
    await page.getByRole("button", { name: "Mark as sent" }).click();
    await expect(status(page)).toHaveText("Sent");
    await expect(page.getByTestId("po-edit")).toHaveCount(0); // only a draft is edited
  });

  test("receive part of it: stock goes up, over-receipt is refused", async ({ page }) => {
    const [v1] = product.variants;
    const before = await onHand(product.id, v1!.id);
    await signInAdmin(page, `/admin/purchasing/orders/${poId}`);
    await page.getByTestId("po-receive").click();

    // More than is still expected on the line: the screen says so and nothing is sent.
    await page.locator(`#rc-a-${lineIds[v1!.id]}`).fill("99");
    await page.getByTestId("receive-confirm").click();
    await expect(page.getByText("Only 5 are still expected on this line.")).toBeVisible();
    expect((await getPo(poId)).status).toBe("Sent");
    // The API refuses it too (same rule, its own text).
    const refused = await call<{ code?: string }>("POST", `/api/v1/purchasing/orders/${poId}/receive`, { token: admin, body: { lines: [{ lineId: lineIds[v1!.id], acceptedQuantity: 99, rejectedQuantity: 0 }] } });
    expect(refused.status).toBe(409);
    expect(refused.json.code).toBe("PURCHASE.OVER_RECEIPT");

    await page.locator(`#rc-a-${lineIds[v1!.id]}`).fill("2");
    await page.locator(`#rc-r-${lineIds[v1!.id]}`).fill("1");
    await page.locator(`#rc-a-${lineIds[product.variants[1]!.id]}`).fill("0");
    await page.getByTestId("receive-confirm").click();
    await expect(status(page)).toHaveText("Partly received");
    expect(await onHand(product.id, v1!.id)).toBe(before + 2); // the damaged unit is not added
    await expect(page.getByTestId("po-receipts").getByRole("listitem")).toHaveCount(1);
    await expect(page.getByTestId("po-close-short")).toBeVisible();
  });

  test("receive the rest: received, stock and cost updated", async ({ page }) => {
    const [v1, v2] = product.variants;
    const b1 = await onHand(product.id, v1!.id);
    const b2 = await onHand(product.id, v2!.id);
    await signInAdmin(page, `/admin/purchasing/orders/${poId}`);
    await page.getByTestId("po-receive").click();
    // The dialog starts with what is still expected: 3 and 3.
    await expect(page.locator(`#rc-a-${lineIds[v1!.id]}`)).toHaveValue("3");
    await page.getByTestId("receive-confirm").click();
    await expect(status(page)).toHaveText("Received");
    expect(await onHand(product.id, v1!.id)).toBe(b1 + 3);
    expect(await onHand(product.id, v2!.id)).toBe(b2 + 3);
    expect(await costOf(product.id)).not.toBeNull(); // Super Admin may change costs, so the receipt set the product cost
    await expect(page.getByTestId("po-receipts").getByRole("listitem")).toHaveCount(2);
  });

  test("record the supplier invoice and pay it in parts", async ({ page }) => {
    await signInAdmin(page, `/admin/purchasing/orders/${poId}`);
    await page.getByTestId("po-invoice").click();
    await page.locator("#iv-number").fill("INV-7781");
    await page.locator("#iv-amount").fill("600");
    await page.getByTestId("invoice-confirm").click();
    await expect(page.getByTestId("invoice-line")).toContainText("INV-7781");
    expect(digits(await page.getByTestId("sum-payable").textContent())).toBe(600);
    expect(digits(await page.getByTestId("sum-outstanding").textContent())).toBe(600);

    await page.getByTestId("po-pay").click();
    // Not more than what is due.
    await page.locator("#pay-amount").fill("600.01");
    await page.getByTestId("pay-confirm").click();
    await expect(page.getByText(/cannot be more than the .*600\.00 still due/)).toBeVisible();
    await page.locator("#pay-amount").fill("200");
    await page.locator("#pay-ref").fill("BANK-1");
    await page.getByTestId("pay-confirm").click();
    await expect(page.getByTestId("pay-status").first()).toHaveText("Partly paid");
    expect(digits(await page.getByTestId("sum-paid").textContent())).toBe(200);
    expect(digits(await page.getByTestId("sum-outstanding").textContent())).toBe(400);
    await expect(page.getByTestId("balance-note")).toContainText("400.00");

    await page.getByTestId("po-pay").click();
    await expect(page.locator("#pay-amount")).toHaveValue("400");
    await page.getByTestId("pay-confirm").click();
    await expect(page.getByTestId("pay-status").first()).toHaveText("Paid");
    expect(digits(await page.getByTestId("sum-outstanding").textContent())).toBe(0);
    await expect(page.getByTestId("balance-note")).toContainText("Nothing is due");
    await expect(page.getByTestId("po-pay")).toHaveCount(0);
    await expect(page.getByTestId("po-payments").getByRole("listitem")).toHaveCount(2);
    const po = await getPo(poId);
    expect(Number(po.amountPaid)).toBe(600);
    expect(po.paymentStatus).toBe("Paid");
  });

  test("the print view shows the order from the API's print data", async ({ page }) => {
    await signInAdmin(page, `/admin/purchasing/orders/${poId}`);
    await page.getByTestId("po-print").click();
    await expect(page).toHaveURL(/\/print$/);
    const sheet = page.getByTestId("po-sheet");
    await expect(sheet).toBeVisible();
    await expect(sheet.getByTestId("sheet-row")).toHaveCount(2);
    expect(digits(await sheet.getByTestId("sheet-total").textContent())).toBe(650);
    await expect(sheet).toContainText(supplier.name);
    await page.emulateMedia({ media: "print" });
    await expect(page.getByTestId("print-now")).toBeHidden(); // the admin frame and buttons are not printed
    await expect(sheet).toBeVisible();
  });
});

test("a partly received order is closed short with a reason", async ({ page }) => {
  const product = await sampleProduct();
  const supplier = await makeSupplier();
  const po = await makePo(supplier.id, [{ variantId: product.variants[0]!.id, quantity: 10, unitCost: 20 }], "Sent");
  await receive(po, { [product.variants[0]!.id]: 4 });
  await signInAdmin(page, `/admin/purchasing/orders/${po.id}`);
  await expect(status(page)).toHaveText("Partly received");
  await page.getByTestId("po-close-short").click();
  await page.getByRole("dialog").getByLabel("Reason").fill("Supplier cannot deliver the rest");
  await page.getByTestId("dialog-confirm").click();
  await expect(status(page)).toHaveText("Received");
  await expect(page.getByTestId("po-history")).toContainText("Supplier cannot deliver the rest");
  expect((await getPo(po.id)).status).toBe("Received");
  expect(Number((await getPo(po.id)).amountPayable)).toBe(80); // what arrived: 4 x 20
});

test("a sent order is cancelled with a reason", async ({ page }) => {
  const product = await sampleProduct();
  const supplier = await makeSupplier();
  const po = await makePo(supplier.id, [{ variantId: product.variants[0]!.id, quantity: 2, unitCost: 10 }], "Sent");
  await signInAdmin(page, `/admin/purchasing/orders/${po.id}`);
  await page.getByTestId("po-cancel").click();
  // A reason is needed.
  await page.getByTestId("dialog-confirm").click();
  await expect(page.getByText("This is required.")).toBeVisible();
  await page.getByRole("dialog").getByLabel("Reason").fill("Ordered by mistake");
  await page.getByTestId("dialog-confirm").click();
  await expect(status(page)).toHaveText("Cancelled");
  await expect(page.getByText("This order was cancelled: Ordered by mistake")).toBeVisible();
  for (const id of ["po-receive", "po-send", "po-pay", "po-cancel", "po-edit"]) await expect(page.getByTestId(id)).toHaveCount(0);
});

test("the list filters by status and supplier and finds an order by number", async ({ page }) => {
  const product = await sampleProduct();
  const supplier = await makeSupplier(unique("List Supplier"));
  const draft = await makePo(supplier.id, [{ variantId: product.variants[0]!.id, quantity: 1, unitCost: 5 }]);
  const sent = await makePo(supplier.id, [{ variantId: product.variants[0]!.id, quantity: 1, unitCost: 5 }], "Sent");
  await signInAdmin(page, `/admin/purchasing?tab=orders&supplierId=${supplier.id}`);
  await expect(page.getByRole("link", { name: draft.number, exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: sent.number, exact: true })).toBeVisible();
  await expect(page.getByTestId("po-total")).toContainText("2 purchase orders");
  await page.getByRole("combobox", { name: /^Status/ }).click();
  await page.getByRole("option", { name: "Sent" }).click();
  await expect(page.getByRole("link", { name: draft.number, exact: true })).toHaveCount(0);
  await expect(page.getByRole("link", { name: sent.number, exact: true })).toBeVisible();
  await page.getByRole("button", { name: "Clear filters" }).click();
  await page.getByLabel("Search purchase orders").fill(draft.number);
  await expect(page.getByRole("link", { name: draft.number, exact: true })).toBeVisible();
  await expect(page.getByRole("link", { name: sent.number, exact: true })).toHaveCount(0);
});

test("suggestions start a draft order with the suggested lines", async ({ page }) => {
  const product = await sampleProduct();
  const variant = product.variants[0]!;
  await call("PUT", "/api/v1/inventory/reorder-levels", { token: admin, body: { branchId: hoId(), variantId: variant.id, reorderLevel: 100000 }, expect: 200 });
  await signInAdmin(page, "/admin/purchasing?tab=suggestions");
  if (await page.locator("#sg-branch").count()) {
    await page.locator("#sg-branch").click();
    await page.getByRole("option", { name: /^HO/ }).click();
  }
  const group = page.getByTestId("suggestion-group").filter({ hasText: variant.sku });
  await expect(group).toBeVisible();
  await group.getByTestId("start-draft").click();
  await expect(page).toHaveURL(/\/admin\/purchasing\/orders\/new/);
  await expect(page.getByTestId("po-line").filter({ hasText: variant.sku })).toBeVisible();
  await call("PUT", "/api/v1/inventory/reorder-levels", { token: admin, body: { branchId: hoId(), variantId: variant.id, reorderLevel: 0 }, expect: 200 });
});

test.describe("roles with fewer permissions", () => {
  test("a role without po.receive, po.send, cancel and pay has no such buttons", async ({ page }) => {
    const product = await sampleProduct();
    const supplier = await makeSupplier();
    const po = await makePo(supplier.id, [{ variantId: product.variants[0]!.id, quantity: 2, unitCost: 10 }], "Sent");
    const viewer = await staffWithPermissions(["purchasing.po.view", "purchasing.supplier.view", "purchasing.receipt.view"]);
    await signInAs(page, viewer, `/admin/purchasing/orders/${po.id}`);
    await expect(status(page)).toHaveText("Sent");
    for (const id of ["po-receive", "po-send", "po-pay", "po-cancel", "po-edit", "po-invoice", "po-close-short"]) await expect(page.getByTestId(id)).toHaveCount(0);
    await expect(page.getByTestId("po-print")).toBeVisible();
    await page.goto("/admin/purchasing?tab=suppliers");
    await expect(page.getByTestId("new-supplier")).toHaveCount(0);
    await page.goto("/admin/purchasing?tab=orders");
    await expect(page.getByTestId("new-po")).toHaveCount(0);
    // The API refuses what the screen hides.
    const token = await staffAccess(viewer.email, viewer.password);
    expect((await call("POST", `/api/v1/purchasing/orders/${po.id}/receive`, { token, body: { lines: [] } })).status).toBe(403);
  });

  test("a role without the cost permission gets no apply-cost button; the cost person gets one", async ({ page }) => {
    const product = await sampleProduct();
    const supplier = await makeSupplier();
    const variant = product.variants[0]!;
    const po = await makePo(supplier.id, [{ variantId: variant.id, quantity: 3, unitCost: 77 }], "Sent");
    const clerk = await staffWithPermissions(["purchasing.po.view", "purchasing.po.receive", "purchasing.receipt.view"]);
    const clerkToken = await staffAccess(clerk.email, clerk.password);
    const result = await receive(po, { [variant.id]: 3 }, clerkToken);
    const receiptId = (result.json as { receipt: { id: string; lines: { costApplied: boolean }[] } }).receipt;
    expect(receiptId.lines.every((l) => l.costApplied === false)).toBe(true); // no cost permission: the product cost was left alone

    await signInAs(page, clerk, "/admin/purchasing?tab=receipts");
    await page.getByRole("button", { name: `Open receipt ${(await getPo(po.id)).receipts[0]!.number}` }).click();
    await expect(page.getByTestId("cost-state").first()).toContainText("Cost not applied yet");
    await expect(page.getByTestId("apply-cost")).toHaveCount(0);
    await expect(page.getByRole("dialog").getByText(/Someone who may change cost prices/)).toBeVisible();

    await page.context().clearCookies();
    await signInAdmin(page, "/admin/purchasing?tab=receipts");
    await page.getByRole("button", { name: `Open receipt ${(await getPo(po.id)).receipts[0]!.number}` }).click();
    await page.getByTestId("apply-cost").click();
    await expect(page.getByTestId("cost-state").first()).toContainText("Cost applied");
    expect(await costOf(product.id)).toBe(77);
  });

  test("a person without purchasing permissions is sent away", async ({ page }) => {
    const other = await staffWithPermissions(["accounting.expense.view"]);
    await signInAs(page, other, "/admin/purchasing");
    await expect(page).toHaveURL(/forbidden/);
    await page.goto("/admin/purchasing/orders/new");
    await expect(page).toHaveURL(/forbidden/);
  });
});
