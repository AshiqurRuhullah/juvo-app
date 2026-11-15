import { test, expect, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { disableAllRules, staffToken } from "./support/orders";
import { staffWithPermissions } from "./support/returns";
import { call, deliveredParcel, digits, fmt2, makeBatch, reconcileBatch, type Parcel } from "./support/cod";
import { signInViaBff } from "./support/ui";

/**
 * Step 10 (COD settlement front end) against the real stack. Parcels are made through the API (order, pack, sandbox courier booking,
 * advanced to Delivered); batches are made through the API with explicit parcels where a test needs exact figures, so parcels left over
 * from other specs never change an expectation. The screens themselves are driven in the browser.
 */
test.describe.configure({ mode: "serial" });

let admin = "";
test.beforeAll(async () => {
  admin = await staffToken("admin");
  await disableAllRules(admin);
});

async function signInAdmin(page: Page, next: string) {
  const who = loadState().admin;
  await signInViaBff(page.context(), who.email, who.password);
  await page.goto(next);
}

const status = (page: Page) => page.getByTestId("settlement-status").first();
const row = (page: Page, parcel: Parcel) => page.locator(`[data-testid=reconcile-row][data-consignment="${parcel.consignmentId}"]`);

async function pendingIds(courierId: string, branchId: string): Promise<string[]> {
  const list = await call<{ id: string }[]>("GET", `/api/v1/couriers/cod-settlements/pending?courierId=${courierId}&branchId=${branchId}`, { token: admin, expect: 200 });
  return list.json.map((p) => p.id);
}

test.describe("List, summary and filters", () => {
  test("shows the cash waiting at the courier, the chips with counts, and filters batches", async ({ page }) => {
    const [a, b] = [await deliveredParcel(), await deliveredParcel()];
    await signInAdmin(page, "/admin/cod");
    await expect(page.getByTestId("cod-summary")).toBeVisible();
    // The two new parcels wait for a batch: at least their cash is shown for the sandbox courier.
    await expect(page.getByTestId("cash-courier").first()).toBeVisible();
    const waiting = digits(await page.getByTestId("cash-total").textContent());
    expect(waiting).toBeGreaterThanOrEqual(a.expected + b.expected - 0.001);

    const batch = await makeBatch([a, b]);
    await page.reload();
    await expect(page.getByRole("link", { name: batch.number, exact: true }).first()).toBeVisible();
    const open = page.getByTestId("chip-Open");
    expect(digits(await open.textContent())).toBeGreaterThanOrEqual(1);

    // A chip filters the list; searching by the number finds exactly the batch.
    await page.getByTestId("chip-Reconciled").click();
    await expect(page.getByTestId("chip-Reconciled")).toHaveAttribute("aria-pressed", "true");
    await expect(page.getByRole("link", { name: batch.number, exact: true })).toHaveCount(0);
    await page.getByTestId("chip-Reconciled").click();
    await page.getByLabel("Search settlements").fill(batch.number);
    await expect(page.getByRole("link", { name: batch.number, exact: true })).toHaveCount(1);
    const listRow = page.getByRole("row").filter({ hasText: batch.number });
    await expect(listRow.getByTestId("settlement-status")).toHaveText("Open");
    // Not reconciled yet: nothing received and no difference to show.
    await expect(listRow).toContainText(fmt2(a.expected + b.expected).replace(/\B(?=(\d{3})+(?!\d))/g, ","));
    await call("POST", `/api/v1/couriers/cod-settlements/${batch.id}/cancel`, { token: admin, expect: 200 });
  });

  test("a search with no match shows an empty list that says what to try", async ({ page }) => {
    await signInAdmin(page, "/admin/cod");
    await page.getByLabel("Search settlements").fill("CS-NOPE-0000");
    await expect(page.getByText("Nothing to show yet").first()).toBeVisible();
    await expect(page.getByRole("link", { name: /^CS-/ })).toHaveCount(0);
  });
});

test.describe("Create a batch", () => {
  test("a subset of the waiting parcels makes a batch and leaves the others waiting", async ({ page }) => {
    const parcels = [await deliveredParcel(), await deliveredParcel(), await deliveredParcel()];
    await signInAdmin(page, "/admin/cod/new");
    await page.getByLabel("Courier").click();
    await page.getByRole("option", { name: /Sandbox/ }).click();
    const rows = page.getByTestId("pending-row");
    await expect(rows.first()).toBeVisible();

    // Leave only our first two parcels ticked.
    await page.getByTestId("select-all").click();
    for (const p of parcels.slice(0, 2)) await page.locator(`[data-testid=pending-row][data-consignment="${p.consignmentId}"] [role=checkbox]`).click();
    await expect(page.getByTestId("selection-summary")).toContainText("2 of");
    expect(digits(await page.getByTestId("selection-total").textContent())).toBeCloseTo(parcels[0]!.expected + parcels[1]!.expected, 2);
    await page.getByTestId("create-batch").click();

    await expect(page).toHaveURL(/\/admin\/cod\/[0-9a-f-]{36}$/);
    await expect(status(page)).toHaveText("Open");
    await expect(page.getByText("2 parcels").first()).toBeVisible();
    expect(digits(await page.getByTestId("total-expected").textContent())).toBeCloseTo(parcels[0]!.expected + parcels[1]!.expected, 2);
    const pending = await pendingIds(parcels[2]!.courierId, parcels[2]!.branchId);
    expect(pending).toContain(parcels[2]!.shipmentId);
    expect(pending).not.toContain(parcels[0]!.shipmentId);
  });

  test("with nothing changed, every waiting parcel is ticked and the batch takes them all", async ({ page }) => {
    const parcel = await deliveredParcel();
    await signInAdmin(page, "/admin/cod/new");
    await page.getByLabel("Courier").click();
    await page.getByRole("option", { name: /Sandbox/ }).click();
    await expect(page.getByTestId("pending-row").first()).toBeVisible();
    await expect(page.getByTestId("select-all")).toHaveAttribute("aria-checked", "true");
    const count = await page.getByTestId("pending-row").count();
    // The running count and total match the rows on screen.
    await expect(page.getByTestId("selection-summary")).toContainText(`${count} of ${count}`);
    let sum = 0;
    for (const text of await page.getByTestId("pending-row").locator(".tabular-nums").allTextContents()) sum += digits(text);
    expect(digits(await page.getByTestId("selection-total").textContent())).toBeCloseTo(sum, 2);
    await page.getByLabel("Note (optional)").fill("Weekly cash");
    await page.getByTestId("create-batch").click();
    await expect(page).toHaveURL(/\/admin\/cod\/[0-9a-f-]{36}$/);
    await expect(page.getByTestId("history")).toContainText("Created");
    await expect(page.getByText("Weekly cash")).toBeVisible();
    expect(await pendingIds(parcel.courierId, parcel.branchId)).toEqual([]);
  });

  test("a parcel taken by another batch meanwhile is refused with a clear message", async ({ page }) => {
    const taken = await deliveredParcel();
    await signInAdmin(page, "/admin/cod/new");
    await page.getByLabel("Courier").click();
    await page.getByRole("option", { name: /Sandbox/ }).click();
    await expect(page.getByTestId("pending-row").first()).toBeVisible();
    await page.getByTestId("select-all").click();
    // Only the parcel that another person batches meanwhile is ticked.
    await page.locator(`[data-testid=pending-row][data-consignment="${taken.consignmentId}"] [role=checkbox]`).click();
    await expect(page.getByTestId("selection-summary")).toContainText("1 of");
    const stolen = await makeBatch([taken]);
    await page.getByTestId("create-batch").click();
    await expect(page.getByTestId("create-error")).toContainText("No delivered cash on delivery parcel is waiting");
    await expect(page).toHaveURL(/\/admin\/cod\/new$/);
    await call("POST", `/api/v1/couriers/cod-settlements/${stolen.id}/cancel`, { token: admin, expect: 200 });
  });

});

test.describe("Reconcile", () => {
  test("paid in full: one click, totals match, nothing short or over", async ({ page }) => {
    const parcels = [await deliveredParcel(), await deliveredParcel()];
    const batch = await makeBatch(parcels);
    await signInAdmin(page, `/admin/cod/${batch.id}`);
    await expect(page.getByTestId("reconcile-form")).toBeVisible();
    const total = parcels[0]!.expected + parcels[1]!.expected;
    // Prefilled with the expected cash, so the live totals already say "exact".
    await expect(page.locator(`#recv-${parcels[0]!.shipmentId}`)).toHaveValue(fmt2(parcels[0]!.expected));
    expect(digits(await page.getByTestId("sum-received").textContent())).toBeCloseTo(total, 2);
    await expect(page.getByTestId("sum-difference")).toContainText("Exact");
    await page.getByTestId("reconcile-save").click();
    await expect(status(page)).toHaveText("Reconciled");
    expect(digits(await page.getByTestId("total-received").textContent())).toBeCloseTo(total, 2);
    await expect(page.getByTestId("total-difference")).toContainText("Exact");
    await expect(page.getByTestId("total-short")).toContainText("0");
    await expect(page.getByTestId("total-over")).toContainText("0");
    await expect(page.getByTestId("history")).toContainText("Reconciled");
    await call("POST", `/api/v1/couriers/cod-settlements/${batch.id}/cancel`, { token: admin, expect: 200 });
  });

  test("short and over parcels need a note, then the totals match the worked example", async ({ page }) => {
    const parcels = [await deliveredParcel(), await deliveredParcel(), await deliveredParcel()];
    const [a, b, c] = parcels as [Parcel, Parcel, Parcel];
    const batch = await makeBatch(parcels);
    await signInAdmin(page, `/admin/cod/${batch.id}`);
    await expect(page.getByTestId("reconcile-form")).toBeVisible();

    // b is paid 50 short, c is paid 20 over (the example of the API contract): the live difference follows every keystroke.
    await page.locator(`#recv-${b.shipmentId}`).fill(fmt2(b.expected - 50));
    await page.locator(`#recv-${c.shipmentId}`).fill(fmt2(c.expected + 20));
    await expect(row(page, b).getByTestId("row-difference")).toContainText("Short");
    expect(digits(await row(page, b).getByTestId("row-difference").textContent())).toBe(50);
    await expect(row(page, c).getByTestId("row-difference")).toContainText("Over");
    expect(digits(await row(page, c).getByTestId("row-difference").textContent())).toBe(20);
    await expect(row(page, a).getByTestId("row-difference")).toContainText("Exact");
    await expect(page.getByTestId("sum-difference")).toContainText("Short");
    expect(digits(await page.getByTestId("sum-difference").textContent())).toBe(30);
    await expect(page.getByTestId("sum-counts")).toContainText("1 short, 1 over");

    // Without notes nothing is sent: the form names the problem, the batch stays open.
    await page.getByTestId("reconcile-save").click();
    await expect(page.getByTestId("reconcile-problems")).toContainText("2 parcels paid short or over need a note");
    await expect(row(page, b).getByText("Write why it is short or over")).toBeVisible();
    await expect(status(page)).toHaveText("Open");

    // An invalid amount is caught before sending too.
    await page.locator(`#recv-${a.shipmentId}`).fill("-5");
    await page.getByTestId("reconcile-save").click();
    await expect(row(page, a).getByText("Zero or more, at most two decimals")).toBeVisible();
    await page.locator(`#recv-${a.shipmentId}`).fill(fmt2(a.expected));

    await page.locator(`#note-${b.shipmentId}`).fill("kept 50 for change");
    await page.locator(`#note-${c.shipmentId}`).fill("customer tipped 20");
    await page.getByTestId("reconcile-save").click();
    await expect(status(page)).toHaveText("Reconciled");
    const expectedTotal = a.expected + b.expected + c.expected;
    expect(digits(await page.getByTestId("total-expected").textContent())).toBeCloseTo(expectedTotal, 2);
    expect(digits(await page.getByTestId("total-received").textContent())).toBeCloseTo(expectedTotal - 30, 2);
    await expect(page.getByTestId("total-difference")).toContainText("Short");
    expect(digits(await page.getByTestId("total-difference").textContent())).toBe(30);
    await expect(page.getByTestId("total-short")).toContainText("1");
    await expect(page.getByTestId("total-over")).toContainText("1");

    // The saved detail (API) has the per-parcel differences 0, -50, +20 and the notes.
    const saved = await call<{ difference: number; shortParcels: number; overParcels: number; items: { shipmentId: string; difference: number; note: string | null }[] }>("GET", `/api/v1/couriers/cod-settlements/${batch.id}`, { token: admin, expect: 200 });
    const diff = (p: Parcel) => Number(saved.json.items.find((i) => i.shipmentId === p.shipmentId)!.difference);
    expect([diff(a), diff(b), diff(c)]).toEqual([0, -50, 20]);
    expect(Number(saved.json.difference)).toBe(-30);
    expect(saved.json.items.find((i) => i.shipmentId === b.shipmentId)!.note).toBe("kept 50 for change");

    // Repeatable until settled: the form starts from what was saved and can be saved again.
    await expect(page.locator(`#recv-${b.shipmentId}`)).toHaveValue(fmt2(b.expected - 50));
    await page.locator(`#recv-${b.shipmentId}`).fill(fmt2(b.expected));
    await page.getByTestId("reconcile-save").click();
    await expect(page.getByTestId("total-short")).toContainText("0");
    await call("POST", `/api/v1/couriers/cod-settlements/${batch.id}/cancel`, { token: admin, expect: 200 });
  });
});

test.describe("Settle and cancel", () => {
  test("settling needs a reference, then every order shows Settled and nothing more can be done", async ({ page }) => {
    const parcels = [await deliveredParcel(), await deliveredParcel()];
    const batch = await makeBatch(parcels);
    await reconcileBatch(batch, parcels, [null, parcels[1]!.expected - 10], "kept for change");
    // Before settling the order's cash is only collected by the courier.
    const before = await call<{ paymentStatus: string }>("GET", `/api/v1/orders/${parcels[0]!.orderId}`, { token: admin, expect: 200 });
    expect(before.json.paymentStatus).toBe("CollectedByCourier");

    await signInAdmin(page, `/admin/cod/${batch.id}`);
    await expect(status(page)).toHaveText("Reconciled");
    await page.getByTestId("settle").click();
    await expect(page.getByRole("dialog")).toContainText("2 orders");
    await page.getByTestId("dialog-confirm").click();
    await expect(page.getByText("Enter the deposit or transfer reference")).toBeVisible();
    await page.getByLabel("Deposit or transfer reference").fill("DEP-2611-001");
    await page.getByTestId("dialog-confirm").click();

    await expect(status(page)).toHaveText("Settled");
    await expect(page.getByTestId("settled-note")).toContainText("2 orders");
    await expect(page.getByTestId("settled-note").getByRole("link", { name: "Open payments" })).toBeVisible();
    await expect(page.getByTestId("reference")).toHaveText("DEP-2611-001");
    await expect(page.getByTestId("settle")).toHaveCount(0);
    await expect(page.getByTestId("cancel-batch")).toHaveCount(0);
    await expect(page.getByTestId("reconcile-form")).toHaveCount(0);

    for (const p of parcels) {
      const order = await call<{ paymentStatus: string }>("GET", `/api/v1/orders/${p.orderId}`, { token: admin, expect: 200 });
      expect(order.json.paymentStatus).toBe("Settled");
    }
    // Settling again is refused by the API with the state code.
    const again = await call<{ code?: string }>("POST", `/api/v1/couriers/cod-settlements/${batch.id}/settle`, { token: admin, body: { reference: "X" } });
    expect(again.status).toBe(409);
    expect(again.json.code).toBe("SHIPMENT.SETTLEMENT_STATE");
  });

  test("cancelling a batch gives its parcels back to the waiting list", async ({ page }) => {
    const parcel = await deliveredParcel();
    const batch = await makeBatch([parcel]);
    expect(await pendingIds(parcel.courierId, parcel.branchId)).not.toContain(parcel.shipmentId);
    await signInAdmin(page, `/admin/cod/${batch.id}`);
    await expect(status(page)).toHaveText("Open");
    // Open: reconcile and cancel are offered, settle is not (it needs a reconciled batch).
    await expect(page.getByTestId("settle")).toHaveCount(0);
    await page.getByTestId("cancel-batch").click();
    await expect(page.getByRole("dialog")).toContainText("available for a new batch");
    await page.getByRole("dialog").getByRole("button", { name: "Cancel batch" }).click();
    await expect(status(page)).toHaveText("Cancelled");
    await expect(page.getByTestId("cancelled-note")).toBeVisible();
    await expect(page.getByTestId("cancel-batch")).toHaveCount(0);
    await expect(page.getByTestId("reconcile-form")).toHaveCount(0);
    expect(await pendingIds(parcel.courierId, parcel.branchId)).toContain(parcel.shipmentId);
  });

  test("a reconciled batch offers settle and cancel", async ({ page }) => {
    const parcel = await deliveredParcel();
    const batch = await makeBatch([parcel]);
    await reconcileBatch(batch, [parcel], [null]);
    await signInAdmin(page, `/admin/cod/${batch.id}`);
    await expect(status(page)).toHaveText("Reconciled");
    await expect(page.getByTestId("settle")).toBeVisible();
    await expect(page.getByTestId("cancel-batch")).toBeVisible();
    await expect(page.getByTestId("reconcile-form")).toBeVisible();
    await call("POST", `/api/v1/couriers/cod-settlements/${batch.id}/cancel`, { token: admin, expect: 200 });
  });
});

test.describe("Print", () => {
  test("the print sheet lists the parcels, totals and signature lines, and hides the admin frame when printing", async ({ page }) => {
    const parcels = [await deliveredParcel(), await deliveredParcel()];
    const batch = await makeBatch(parcels);
    await signInAdmin(page, `/admin/cod/${batch.id}`);
    await page.getByTestId("print-link").click();
    await expect(page).toHaveURL(new RegExp(`/admin/cod/${batch.id}/print$`));
    await expect(page.getByTestId("settlement-sheet")).toHaveAttribute("data-number", batch.number);
    await expect(page.getByTestId("sheet-row")).toHaveCount(2);
    expect(digits(await page.getByTestId("sheet-expected").textContent())).toBeCloseTo(parcels[0]!.expected + parcels[1]!.expected, 2);
    await expect(page.getByTestId("signatures")).toContainText("Courier representative");
    await expect(page.getByRole("complementary").first()).toBeVisible();
    await page.emulateMedia({ media: "print" });
    await expect(page.getByRole("complementary")).toHaveCount(0);
    await expect(page.getByTestId("print-now")).toBeHidden();
    await expect(page.getByTestId("settlement-sheet")).toBeVisible();
    await page.emulateMedia({ media: "screen" });
    await call("POST", `/api/v1/couriers/cod-settlements/${batch.id}/cancel`, { token: admin, expect: 200 });
  });
});

test.describe("Permissions", () => {
  test("someone who may only view sees the batch but no buttons, and cannot start a batch", async ({ page }) => {
    const parcel = await deliveredParcel();
    const batch = await makeBatch([parcel]);
    const viewer = await staffWithPermissions(["couriers.cod.view", "couriers.courier.view"]);
    await signInViaBff(page.context(), viewer.email, viewer.password);
    await page.goto(`/admin/cod/${batch.id}`);
    await expect(status(page)).toHaveText("Open");
    await expect(page.getByTestId("print-link")).toBeVisible();
    await expect(page.getByTestId("settle")).toHaveCount(0);
    await expect(page.getByTestId("cancel-batch")).toHaveCount(0);
    await expect(page.getByTestId("reconcile-form")).toHaveCount(0);
    await page.goto("/admin/cod");
    await expect(page.getByRole("link", { name: batch.number, exact: true }).first()).toBeVisible();
    await expect(page.getByTestId("new-batch")).toHaveCount(0);
    await page.goto("/admin/cod/new");
    await expect(page).toHaveURL(/\/forbidden/);
    await call("POST", `/api/v1/couriers/cod-settlements/${batch.id}/cancel`, { token: admin, expect: 200 });
  });

  test("someone without couriers.cod.view is sent to the forbidden page", async ({ page }) => {
    const nobody = await staffWithPermissions(["orders.order.view"]);
    await signInViaBff(page.context(), nobody.email, nobody.password);
    await page.goto("/admin/cod");
    await expect(page).toHaveURL(/\/forbidden/);
    await page.goto("/admin/cod/00000000-0000-4000-8000-000000000000");
    await expect(page).toHaveURL(/\/forbidden/);
  });

  test("a batch that does not exist says so", async ({ page }) => {
    await signInAdmin(page, "/admin/cod/00000000-0000-4000-8000-000000000000");
    await expect(page.getByRole("heading", { name: "We could not find this settlement" }).first()).toBeVisible();
  });
});
