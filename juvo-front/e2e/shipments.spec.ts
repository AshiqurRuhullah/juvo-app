import { test, expect, type Page } from "@playwright/test";
import { advanceParcel, bookedParcel, cancelParcel, type Parcel } from "./support/couriers";
import { loadState } from "./support/env";
import { topUpHeadOfficeStock } from "./support/orders";
import { adminAccess, staffWithPermissions, stockedVariants, type SampleVariant } from "./support/returns";
import { pick } from "./support/store";
import { signInViaBff } from "./support/ui";

/**
 * Parcels (modules H and J) in the browser against the real API: the list with search and filters, the detail with its journey and
 * event log, the sandbox simulator, cancel and book again, the labels, what a limited staff member can do, and the live update.
 * Orders are packed and booked through the API first (the e2e database starts empty).
 */
test.describe.configure({ timeout: 90_000 });

let variants: SampleVariant[];
test.beforeAll(async () => {
  // Late in a long run head office stock is used up and orders go to the online branch, where these head office users cannot see them.
  await topUpHeadOfficeStock(await adminAccess(), loadState().branches.ho.id);
  variants = await stockedVariants(5, 4);
});

async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

const status = (page: Page) => page.getByTestId("shipment-status").first();
const money = (n: number | string) => new Intl.NumberFormat("en-US").format(Number(n));

/** A booked parcel for a fresh packed order (recipient name given so searches are unambiguous). */
async function newParcel(index: number, recipient?: string) {
  return bookedParcel([variants[index % variants.length]!.id], recipient);
}

test.describe("Parcels: list", () => {
  test("a booked parcel is found by consignment, order number and recipient and filtered by status, kind and cash", async ({ page }) => {
    const unique = `Zubair${Date.now() % 100000}`;
    const { order, parcel } = await newParcel(0, unique);
    await adminSignIn(page);
    await page.goto("/admin/shipments");
    await expect(page.getByRole("heading", { level: 1, name: "Parcels" })).toBeVisible();

    const search = page.getByRole("searchbox", { name: "Search parcels" });
    await search.fill(parcel.consignmentId);
    await expect(page.getByRole("link", { name: parcel.consignmentId }).first()).toBeVisible();
    await expect(page.getByTestId("shipment-total")).toHaveText("1 parcel");

    await search.fill(order.number);
    await expect(page.getByRole("link", { name: parcel.consignmentId }).first()).toBeVisible();
    await expect(page.getByTestId("shipment-total")).toHaveText("1 parcel");

    await search.fill(unique);
    await expect(page.getByRole("link", { name: parcel.consignmentId }).first()).toBeVisible();
    await expect(page.getByTestId("shipment-total")).toHaveText("1 parcel");

    // Filters narrow the same result.
    await pick(page, page.getByRole("combobox", { name: /^Status/ }), "Delivered");
    await expect(page.getByTestId("shipment-total")).toHaveText("0 parcels");
    await pick(page, page.getByRole("combobox", { name: /^Status/ }), "Booked");
    await expect(page.getByTestId("shipment-total")).toHaveText("1 parcel");
    await pick(page, page.getByRole("combobox", { name: /^Kind/ }), "Return pickup");
    await expect(page.getByTestId("shipment-total")).toHaveText("0 parcels");
    await pick(page, page.getByRole("combobox", { name: /^Kind/ }), "To customer");
    await expect(page.getByTestId("shipment-total")).toHaveText("1 parcel");
    await page.getByLabel("Cash on delivery only").click();
    await page.getByLabel("Still open only").click();
    await expect(page.getByTestId("shipment-total")).toHaveText("1 parcel");
    await expect(page.getByRole("link", { name: parcel.consignmentId }).first()).toBeVisible();

    // The row opens the parcel; the order number goes to the order.
    await page.getByRole("link", { name: parcel.consignmentId }).first().click();
    await expect(page).toHaveURL(new RegExp(`/admin/shipments/${parcel.id}$`));
  });

  test("a cancelled parcel is out of the open filter and the list pages and sorts on the server", async ({ page }) => {
    const { parcel } = await newParcel(1);
    await cancelParcel(parcel.id);
    await adminSignIn(page);
    await page.goto("/admin/shipments");
    await page.getByRole("searchbox", { name: "Search parcels" }).fill(parcel.consignmentId);
    await expect(page.getByTestId("shipment-total")).toHaveText("1 parcel");
    await page.getByLabel("Still open only").click();
    await expect(page.getByTestId("shipment-total")).toHaveText("0 parcels");
    await page.getByLabel("Still open only").click();
    await page.getByRole("searchbox", { name: "Search parcels" }).fill("");
    await expect(page.getByTestId("live-badge")).toHaveAttribute("data-live", /live|connecting|reconnecting|offline/);
    // Sorting by status asks the API (the request carries the sort).
    const request = page.waitForRequest((r) => r.url().includes("/api/v1/shipments") && r.url().includes("sort=status"));
    await page.getByRole("button", { name: /Sort by Status/ }).first().click();
    await request;
  });

  test("selected parcels open on one multi-label page", async ({ page }) => {
    const first = await newParcel(2);
    const second = await newParcel(3);
    await adminSignIn(page);
    await page.goto("/admin/shipments");
    await page.getByRole("checkbox", { name: `Select parcel ${first.parcel.consignmentId}` }).click();
    await page.getByRole("checkbox", { name: `Select parcel ${second.parcel.consignmentId}` }).click();
    await expect(page.getByTestId("bulk-bar")).toContainText("2 parcels selected");
    await page.getByTestId("print-labels").click();
    await expect(page).toHaveURL(/\/admin\/shipments\/labels\?ids=/);
    await expect(page.getByTestId("label")).toHaveCount(2);
    await expect(page.getByTestId("barcode")).toHaveCount(2);
  });
});

test.describe("Parcels: detail", () => {
  test("the detail shows the journey, the facts, the cash, the addresses and the first event", async ({ page }) => {
    const { order, parcel } = await newParcel(0, "Farhana Detail");
    await adminSignIn(page);
    await page.goto(`/admin/shipments/${parcel.id}`);
    await expect(page.getByRole("heading", { level: 1, name: parcel.consignmentId })).toBeVisible();
    await expect(status(page)).toHaveText("Booked");
    await expect(page.getByTestId("journey")).toHaveAttribute("data-status", "Booked");
    await expect(page.getByTestId("shipment-facts")).toContainText("Sandbox courier");
    await expect(page.getByTestId("cod-amount")).toContainText(money(parcel.codAmount));
    await expect(page.getByText("Farhana Detail").first()).toBeVisible();
    await expect(page.getByText("House 12, Road 5, Dhanmondi").first()).toBeVisible();
    await expect(page.getByTestId("timeline-event")).toHaveCount(1);
    await expect(page.getByTestId("timeline-event").first()).toContainText("Booked");
    await expect(page.getByTestId("tracking-link")).toHaveAttribute("href", /^https:\/\//);
    await expect(page.getByTestId("order-link")).toHaveAttribute("href", `/admin/orders/${order.orderId}`);
    await expect(page.getByTestId("simulator-box")).toContainText("Sandbox simulator");
    await expect(page.getByTestId("action-label")).toBeVisible();
  });

  test("the sandbox simulator moves a parcel to Delivered and the cash is shown as collected", async ({ page }) => {
    const { parcel } = await newParcel(1, "Rafiq Advance");
    await adminSignIn(page);
    await page.goto(`/admin/shipments/${parcel.id}`);
    for (const [label, shown] of [
      ["Simulate: Picked up", "Picked up"],
      ["Simulate: In transit", "In transit"],
      ["Simulate: Out for delivery", "Out for delivery"],
      ["Simulate: Delivered", "Delivered"],
    ] as const) {
      await page.getByRole("button", { name: label }).click();
      const dialog = page.getByRole("dialog");
      await expect(dialog.getByText("Move the parcel along (sandbox)")).toBeVisible();
      await dialog.getByTestId("advance-confirm").click();
      await expect(page.getByRole("dialog")).toHaveCount(0);
      await expect(status(page)).toHaveText(shown);
    }
    await expect(page.getByTestId("journey")).toHaveAttribute("data-status", "Delivered");
    await expect(page.getByTestId("simulator-box")).toHaveCount(0);
    await expect(page.getByTestId("action-cancel")).toHaveCount(0);
    await expect(page.getByTestId("shipment-cod")).toContainText("Collected");
    await expect(page.getByTestId("shipment-cod")).not.toContainText("Not yet");
    expect(await page.getByTestId("timeline-event").count()).toBeGreaterThanOrEqual(5);
  });

  test("a specific status can be chosen: a failed delivery shows its reason with an icon and text", async ({ page }) => {
    const { parcel } = await newParcel(2, "Tanvir Failed");
    await advanceParcel(parcel.id, "picked_up");
    await advanceParcel(parcel.id, "in_transit");
    await advanceParcel(parcel.id, "out_for_delivery");
    await adminSignIn(page);
    await page.goto(`/admin/shipments/${parcel.id}`);
    await page.getByRole("button", { name: /^Simulate:/ }).click();
    const dialog = page.getByRole("dialog");
    await dialog.getByRole("radio", { name: "Delivery failed" }).click();
    await dialog.getByLabel("Note").fill("Customer phone was off");
    await dialog.getByTestId("advance-confirm").click();
    await expect(status(page)).toHaveText("Delivery failed");
    await expect(page.getByTestId("failure-reason")).toBeVisible();
    await expect(page.locator('[data-testid="journey"] [data-state="failed"]')).toBeVisible();
    await expect(page.getByTestId("timeline-event").first()).toContainText("Customer phone was off");
  });

  test("cancelling needs a reason, then the parcel is cancelled and can be booked again", async ({ page }) => {
    const { parcel } = await newParcel(3, "Mita Cancel");
    await adminSignIn(page);
    await page.goto(`/admin/shipments/${parcel.id}`);
    await page.getByTestId("action-cancel").click();
    const dialog = page.getByRole("dialog");
    await dialog.getByTestId("cancel-confirm").click();
    await expect(dialog.getByText("Write a reason.")).toBeVisible();
    await dialog.getByLabel("Reason").fill("Customer changed the address");
    await dialog.getByTestId("cancel-confirm").click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(status(page)).toHaveText("Cancelled");
    await expect(page.getByTestId("journey-off")).toBeVisible();
    await expect(page.getByTestId("cancel-reason")).toContainText("Customer changed the address");
    await expect(page.getByTestId("action-cancel")).toHaveCount(0);

    // Book again: the courier is chosen automatically, a new parcel is booked.
    await page.getByTestId("action-rebook").click();
    const rebook = page.getByRole("dialog");
    await expect(rebook.getByRole("radio", { name: /Choose automatically/ })).toBeChecked();
    await rebook.getByLabel("Reason for booking again").fill("Address fixed");
    await rebook.getByTestId("rebook-confirm").click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(status(page)).toHaveText("Booked");
    await expect(page.getByTestId("action-cancel")).toBeVisible();
  });

  test("a parcel that does not exist is a clear not-found", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/shipments/00000000-0000-4000-8000-000000000000");
    await expect(page.getByText("We could not find that parcel.").first()).toBeVisible();
  });
});

test.describe("Parcels: labels", () => {
  let parcel: Parcel;
  test.beforeAll(async () => {
    parcel = (await newParcel(0, "Label Reader")).parcel;
  });

  test("the label shows a barcode, the consignment, the recipient and the cash to collect", async ({ page }) => {
    await adminSignIn(page);
    await page.goto(`/admin/shipments/${parcel.id}/label`);
    const label = page.getByTestId("label");
    await expect(label).toBeVisible();
    const barcode = label.getByTestId("barcode");
    await expect(barcode).toBeVisible();
    await expect(barcode).toHaveAttribute("data-value", /.+/);
    expect(Number(await barcode.getAttribute("data-bars"))).toBeGreaterThan(20);
    await expect(label.getByTestId("label-consignment")).toHaveText(parcel.consignmentId);
    await expect(label.getByTestId("label-order")).toHaveText(parcel.orderNumber);
    await expect(label).toContainText("Label Reader");
    await expect(label.getByTestId("label-cod-amount")).toContainText(money(parcel.codAmount));
    await expect(page.getByTestId("print-now")).toBeVisible();

    // Printing: the app chrome and the buttons disappear, the label stays on a page of its own.
    await page.emulateMedia({ media: "print" });
    await expect(page.getByTestId("print-now")).toBeHidden();
    await expect(label).toBeVisible();
    const box = await label.boundingBox();
    expect(box!.width).toBeLessThan(420);
  });

  test("a staff member who may view parcels but not print labels cannot open the label page", async ({ page, context }) => {
    const viewer = await staffWithPermissions(["couriers.shipment.view"]);
    await signInViaBff(context, viewer.email, viewer.password);
    await page.goto(`/admin/shipments/${parcel.id}`);
    await expect(status(page)).toHaveText("Booked");
    await expect(page.getByTestId("action-label")).toHaveCount(0);
    await expect(page.getByTestId("shipment-actions")).toHaveCount(0);
    await page.goto(`/admin/shipments/${parcel.id}/label`);
    await expect(page).toHaveURL(/forbidden/);
    // The list has no selection column without the label permission.
    await page.goto("/admin/shipments");
    await expect(page.getByRole("checkbox", { name: /^Select parcel/ })).toHaveCount(0);
  });
});

test.describe("Parcels: permissions and live updates", () => {
  test("a staff member without parcel permissions is sent to the forbidden page", async ({ page, context }) => {
    const other = await staffWithPermissions(["couriers.courier.view"]);
    await signInViaBff(context, other.email, other.password);
    await page.goto("/admin/shipments");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("a view-only staff member cannot cancel or advance a parcel, and the API agrees", async ({ page, context }) => {
    const { parcel } = await newParcel(1);
    const viewer = await staffWithPermissions(["couriers.shipment.view", "couriers.shipment.print-label"]);
    await signInViaBff(context, viewer.email, viewer.password);
    await page.goto(`/admin/shipments/${parcel.id}`);
    await expect(status(page)).toHaveText("Booked");
    await expect(page.getByTestId("action-cancel")).toHaveCount(0);
    await expect(page.getByTestId("action-rebook")).toHaveCount(0);
    await expect(page.getByTestId("action-advance")).toHaveCount(0);
    await expect(page.getByTestId("action-label")).toBeVisible();
    const attempt = await context.request.post(`/api/bff/api/v1/shipments/${parcel.id}/advance`, { data: {} });
    expect(attempt.status()).toBe(403);
  });

  test("an open parcel page follows the courier live: advancing it through the API changes the page without a reload", async ({ page }) => {
    const { parcel } = await newParcel(2, "Live Follower");
    await adminSignIn(page);
    await page.goto(`/admin/shipments/${parcel.id}`);
    await expect(status(page)).toHaveText("Booked");
    await expect(page.getByTestId("live-badge")).toHaveAttribute("data-live", "live", { timeout: 20_000 });
    await expect(page.getByTestId("timeline-event")).toHaveCount(1);

    await advanceParcel(parcel.id, "picked_up");
    await expect(status(page)).toHaveText("Picked up", { timeout: 25_000 });
    await expect(page.getByTestId("timeline-event")).toHaveCount(2);

    await advanceParcel(parcel.id, "in_transit");
    await expect(status(page)).toHaveText("In transit", { timeout: 25_000 });
    await expect(page.getByTestId("timeline-event")).toHaveCount(3);
  });
});
