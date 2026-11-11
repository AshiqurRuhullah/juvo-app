import { test, expect, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { loadState } from "./support/env";
import { makePng } from "./support/png";
import {
  advanceTo,
  deliveredOrder,
  expireReturnWindow,
  productWithTwoVariants,
  requestReturn,
  staffWithPermissions,
  stockedVariants,
} from "./support/returns";
import { pick, signInCustomer } from "./support/store";
import { signInViaBff } from "./support/ui";

/**
 * Returns (module L) in the browser against the real API: the customer's request with a picture, the list, detail and cancel, the
 * staff queue and every stage up to the refund (and the exchange), the refusals, and what a limited staff member can and cannot do.
 * Orders are placed and delivered through the API first (the e2e database starts empty).
 */
const money = (n: number) => new Intl.NumberFormat("en-US").format(n);

async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

/** The status badge of a return page. */
const status = (page: Page) => page.getByTestId("return-status").first();

test.describe("Returns: customer", () => {
  test("a customer asks for a return with a picture, sees it in the list and detail, and cancels it", async ({ page, context }) => {
    const [variant] = await stockedVariants(2, 1);
    const order = await deliveredOrder([{ variantId: variant!.id, quantity: 2 }]);
    await signInCustomer(context, order.mobile);

    await page.goto(`/account/returns/new?order=${order.orderId}`);
    await expect(page.getByRole("heading", { level: 1, name: "Return or exchange" })).toBeVisible();
    await expect(page.getByTestId("return-deadline")).toContainText("You can ask for a return until");
    await expect(page.getByTestId("return-line")).toHaveCount(1);

    // Nothing is chosen yet: the form explains what is missing instead of sending.
    await page.getByRole("button", { name: "Send request" }).click();
    await expect(page.getByText("Choose at least one item to send back.")).toBeVisible();
    await expect(page.getByText("Choose a reason.")).toBeVisible();

    // The stepper never goes above what can still be returned (2).
    const more = page.getByRole("button", { name: "One more" });
    await more.click();
    await more.click();
    await expect(more).toBeDisabled();
    await page.getByRole("button", { name: "One less" }).click();
    await expect(page.getByTestId("refund-estimate")).toContainText(money(variant!.price));

    await page.getByRole("radio", { name: "The item arrived damaged" }).click();
    await expect(page.getByText("The shop covers the return in this case.")).toBeVisible();
    await page.getByLabel("Tell us more (optional)").fill("The zip is broken");

    await page.getByTestId("return-photo-input").setInputFiles({ name: "broken-zip.png", mimeType: "image/png", buffer: makePng(1200, 900) });
    await expect(page.getByTestId("return-photo")).toContainText("Ready");

    await page.getByRole("button", { name: "Send request" }).click();
    await expect(page).toHaveURL(/\/account\/returns\/[0-9a-f-]{36}$/);
    await expect(page.getByRole("heading", { level: 1, name: /^Return RT-/ })).toBeVisible();
    await expect(status(page)).toHaveText("Requested");
    await expect(page.getByTestId("return-next-step")).toContainText("We have your request");
    await expect(page.getByTestId("refund-box")).toContainText("Estimated refund");
    await expect(page.getByTestId("refund-box")).toContainText("This is an estimate");
    await expect(page.getByRole("link", { name: "Open photo 1" })).toBeVisible();
    const detailUrl = page.url();

    await page.goto("/account/returns");
    await expect(page.getByTestId("my-return")).toHaveCount(1);
    await expect(page.getByTestId("my-return").first()).toContainText("Requested");

    // Cancel with the confirmation, then it moves to the closed tab.
    await page.goto(detailUrl);
    await expect(page.getByRole("button", { name: "Cancel request" })).toBeVisible();
    await page.getByRole("button", { name: "Cancel request" }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByText("Cancel this request?")).toBeVisible();
    await dialog.getByRole("button", { name: "Cancel request" }).click();
    await expect(page.getByText("Your request was cancelled.").first()).toBeVisible();
    await expect(status(page)).toHaveText("Cancelled");
    await expect(page.getByRole("button", { name: "Cancel request" })).toHaveCount(0);

    await page.goto("/account/returns");
    await expect(page.getByText("No returns in progress")).toBeVisible();
    await page.getByRole("button", { name: "Closed" }).click();
    await expect(page.getByTestId("my-return")).toHaveCount(1);
    await expect(page.getByTestId("my-return").first()).toContainText("Cancelled");
  });

  test("the BFF passes the Idempotency-Key, so sending the same request twice files it once", async ({ context }) => {
    const [variant] = await stockedVariants(1, 1, 1);
    const order = await deliveredOrder([{ variantId: variant!.id, quantity: 1 }]);
    await signInCustomer(context, order.mobile);
    const eligibility = await context.request.get(`/api/bff/api/v1/me/orders/${order.orderId}/return-eligibility`);
    expect(eligibility.status()).toBe(200);
    const item = ((await eligibility.json()) as { items: { orderItemId: string }[] }).items[0]!;
    const send = () =>
      context.request.post("/api/bff/api/v1/me/returns", {
        headers: { "Idempotency-Key": "e2e-key-" + order.orderId },
        data: { orderId: order.orderId, kind: "Return", reasonCode: "CHANGED_MIND", details: null, items: [{ orderItemId: item.orderItemId, quantity: 1, exchangeVariantId: null }], photoPaths: [] },
      });
    const first = await send();
    expect(first.status()).toBe(201);
    const second = await send();
    expect([200, 201]).toContain(second.status());
    expect(((await second.json()) as { id: string }).id).toBe(((await first.json()) as { id: string }).id);
  });

  test("pictures of the wrong type or size are refused before upload, and can be removed", async ({ page, context }) => {
    const [variant] = await stockedVariants(1, 1, 2);
    const order = await deliveredOrder([{ variantId: variant!.id, quantity: 1 }]);
    await signInCustomer(context, order.mobile);
    await page.goto(`/account/returns/new?order=${order.orderId}`);
    await expect(page.getByTestId("return-line")).toHaveCount(1);

    const input = page.getByTestId("return-photo-input");
    await input.setInputFiles({ name: "note.gif", mimeType: "image/gif", buffer: Buffer.from("GIF89a") });
    await expect(page.getByText("Use a JPEG, PNG or WebP picture.")).toBeVisible();
    await input.setInputFiles({ name: "tiny.png", mimeType: "image/png", buffer: makePng(300, 300) });
    await expect(page.getByText("This picture is too small. It needs at least 1000 pixels on the long side.")).toBeVisible();
    await expect(page.getByTestId("return-photo")).toHaveCount(2);
    await page.getByRole("button", { name: "Remove photo 1" }).click();
    await page.getByRole("button", { name: "Remove photo 1" }).click();
    await expect(page.getByTestId("return-photo")).toHaveCount(0);
  });

  test("a request for more than is left is refused with a clear text, and an expired window is explained", async ({ page, context }) => {
    const [variant] = await stockedVariants(2, 1, 3);
    const order = await deliveredOrder([{ variantId: variant!.id, quantity: 2 }]);
    await signInCustomer(context, order.mobile);
    await page.goto(`/account/returns/new?order=${order.orderId}`);
    await expect(page.getByTestId("return-line")).toHaveCount(1);

    // While this page is open, both units are claimed by another request (the page still shows 2 as returnable).
    await requestReturn(order, { quantity: 2 });
    const more = page.getByRole("button", { name: "One more" });
    await more.click();
    await more.click();
    await page.getByRole("radio", { name: "I changed my mind" }).click();
    await page.getByRole("button", { name: "Send request" }).click();
    await expect(page.getByTestId("request-problem")).toContainText("You asked to return more than is left to return.");
    // The page reloads the figures: nothing is left to return now, so the form gives way to the explanation.
    await expect(page.getByTestId("not-eligible")).toContainText("cannot be returned");
    await expect(page.getByRole("button", { name: "Send request" })).toHaveCount(0);

    const [other] = await stockedVariants(1, 1, 4);
    const old = await deliveredOrder([{ variantId: other!.id, quantity: 1 }], order.mobile);
    expireReturnWindow(old.orderId);
    await page.goto(`/account/returns/new?order=${old.orderId}`);
    await expect(page.getByTestId("not-eligible")).toContainText("The return period for this order has ended");
    await expect(page.getByRole("button", { name: "Send request" })).toHaveCount(0);
  });

  test("an exchange picks another variant, and staff complete it with a replacement order the customer sees", async ({ page, context }) => {
    const { bought, other } = await productWithTwoVariants();
    const order = await deliveredOrder([{ variantId: bought.id, quantity: 1 }]);
    await signInCustomer(context, order.mobile);
    await page.goto(`/account/returns/new?order=${order.orderId}`);
    await expect(page.getByTestId("return-line")).toHaveCount(1);
    await page.getByRole("button", { name: "One more" }).click();
    await page.getByRole("radio", { name: /Exchange for another size or colour/ }).click();
    const replacement = page.getByLabel("Replacement", { exact: true });
    await expect(replacement).toBeVisible();
    await pick(page, replacement, other.sku);
    await page.getByRole("radio", { name: "Size or fit is not right" }).click();
    await page.getByRole("button", { name: "Send request" }).click();
    await expect(page).toHaveURL(/\/account\/returns\/[0-9a-f-]{36}$/);
    await expect(page.getByTestId("my-return-item")).toContainText(`Replacement: ${other.sku}`);
    const customerUrl = page.url();
    const returnId = customerUrl.split("/").pop()!;

    await advanceTo(returnId, "Inspected");

    // Staff complete the exchange in the admin screen.
    const staff = await context.browser()!.newContext({ baseURL: E2E.webUrl, viewport: { width: 1280, height: 900 } });
    const staffPage = await staff.newPage();
    const admin = loadState().admin;
    await signInViaBff(staff, admin.email, admin.password);
    await staffPage.goto(`/admin/returns/${returnId}`);
    await expect(staffPage.getByTestId("return-actions")).toBeVisible();
    await expect(staffPage.getByTestId("action-refund")).toHaveCount(0); // an exchange is never refunded
    await staffPage.getByTestId("action-exchange").click();
    const dialog = staffPage.getByRole("dialog");
    await expect(dialog.getByTestId("exchange-line")).toHaveCount(1);
    await dialog.getByRole("button", { name: "Complete exchange" }).click();
    await expect(status(staffPage)).toHaveText("Exchanged");
    await expect(staffPage.getByTestId("replacement-order")).toContainText(/^JV-/);
    await staff.close();

    await page.goto(customerUrl);
    await expect(status(page)).toHaveText("Exchanged");
    await expect(page.getByTestId("replacement-order")).toContainText("Your replacement is order JV-");
  });
});

test.describe("Returns: staff", () => {
  test("staff take a return from request to refund, and the customer sees it refunded", async ({ page, browser }) => {
    const [variant] = await stockedVariants(2, 1, 5);
    const order = await deliveredOrder([{ variantId: variant!.id, quantity: 2 }]);
    const ret = await requestReturn(order, { quantity: 2, reasonCode: "WRONG_ITEM" });
    await adminSignIn(page);

    // The queue: counts and a search that finds this return by its number.
    await page.goto("/admin/returns");
    await expect(page.getByTestId("return-summary")).toBeVisible();
    await expect(page.getByTestId("chip-awaitingApproval")).toBeVisible();
    await page.getByLabel("Search returns").fill(ret.number);
    await expect(page.getByRole("link", { name: ret.number }).first()).toBeVisible();
    await page.getByRole("link", { name: ret.number }).first().click();
    await expect(page.getByRole("heading", { level: 1, name: ret.number })).toBeVisible();
    await expect(status(page)).toHaveText("Requested");
    await expect(page.getByTestId("order-link")).toContainText(order.number);

    // Only the legal steps are offered at each stage.
    const actions = page.getByTestId("return-actions");
    await expect(actions.getByRole("button")).toHaveText(["Approve", "Reject"]);
    await page.getByTestId("action-approve").click();
    await page.getByRole("dialog").getByRole("button", { name: "Approve", exact: true }).click();
    await expect(status(page)).toHaveText("Approved");
    await expect(actions.getByRole("button")).toHaveText(["Schedule pickup"]);

    await page.getByTestId("action-schedulePickup").click();
    const pickup = page.getByRole("dialog");
    await pickup.getByRole("button", { name: "Schedule pickup" }).click();
    await expect(pickup.getByText("Choose a day from today to 60 days ahead.")).toBeVisible();
    const tomorrow = new Date(Date.now() + 86_400_000);
    await pickup.getByLabel("Pickup day").fill(`${tomorrow.getFullYear()}-${String(tomorrow.getMonth() + 1).padStart(2, "0")}-${String(tomorrow.getDate()).padStart(2, "0")}`);
    await pickup.getByLabel("Courier name").fill("Pathao");
    await pickup.getByRole("button", { name: "Schedule pickup" }).click();
    await expect(status(page)).toHaveText("Pickup arranged");
    await expect(page.getByTestId("pickup-info")).toContainText("Pathao");

    await expect(actions.getByRole("button")).toHaveText(["Mark picked up"]);
    await page.getByTestId("action-pickedUp").click();
    await expect(status(page)).toHaveText("Picked up");
    await page.getByTestId("action-received").click();
    await expect(status(page)).toHaveText("Received");

    // Inspect: a damaged unit cannot be restocked.
    await page.getByTestId("action-inspect").click();
    const inspect = page.getByRole("dialog");
    await expect(inspect.getByTestId("inspect-line")).toHaveCount(1);
    await inspect.getByRole("radio", { name: "Damaged" }).click();
    await expect(inspect.getByRole("switch", { name: /Put back on the shelf/ })).toBeDisabled();
    await expect(inspect.getByText("A damaged item cannot be restocked.")).toBeVisible();
    await inspect.getByRole("radio", { name: "Good" }).click();
    await expect(inspect.getByRole("switch", { name: /Put back on the shelf/ })).toBeEnabled();
    await inspect.getByRole("button", { name: "Save inspection" }).click();
    await expect(status(page)).toHaveText("Inspected");
    await expect(page.getByTestId("inspection-result").first()).toContainText("Back on the shelf");
    // Wrong item sent = the shop's fault, so the delivery charge comes back too: the figure is final and shown by the server.
    await expect(page.getByTestId("refund-box").first()).toContainText("Refund");
    const total = (await page.getByTestId("refund-total").first().innerText()).trim();

    await expect(actions.getByRole("button")).toHaveText(["Record refund", "Decline refund"]);
    await page.getByTestId("action-refund").click();
    const refund = page.getByRole("dialog");
    await expect(refund.getByTestId("refund-amount")).toHaveValue(total);
    await expect(refund.getByTestId("refund-amount")).toHaveAttribute("readonly", "");
    await refund.getByRole("button", { name: "Record refund" }).click();
    await expect(refund.getByText("Write the reference of the payment.")).toBeVisible();
    await refund.getByLabel("Reference").fill("RCPT-E2E-1");
    await refund.getByRole("button", { name: "Record refund" }).click();
    await expect(status(page)).toHaveText("Refunded");
    await expect(page.getByTestId("refund-paid")).toContainText("RCPT-E2E-1");
    await expect(page.getByTestId("return-actions")).toHaveCount(0);
    await expect(page.getByTestId("return-timeline").first().getByText("Refunded", { exact: true })).toBeVisible();

    // Internal note.
    await page.getByLabel("Add a note").fill("Refund paid in cash at the counter");
    await page.getByRole("button", { name: "Add note" }).click();
    await expect(page.getByTestId("return-note")).toContainText("Refund paid in cash at the counter");

    // The customer sees the same return as refunded, with the final amount and no staff names.
    const customer = await browser.newContext({ baseURL: E2E.webUrl, viewport: { width: 1280, height: 900 } });
    const customerPage = await customer.newPage();
    await signInCustomer(customer, order.mobile);
    await customerPage.goto(`/account/returns/${ret.id}`);
    await expect(status(customerPage)).toHaveText("Refunded");
    await expect(customerPage.getByTestId("refund-total")).toHaveText(total);
    await expect(customerPage.getByTestId("refund-paid")).toContainText("RCPT-E2E-1");
    await expect(customerPage.getByTestId("return-next-step")).toContainText("Your refund has been paid.");
    await expect(customerPage.getByTestId("return-timeline")).not.toContainText("Juvo Super Admin");
    await customerPage.goto("/account/returns");
    await customerPage.getByRole("button", { name: "Closed" }).click();
    await expect(customerPage.getByTestId("my-return").first()).toContainText("Refunded");
    await customer.close();
  });

  test("a rejection needs a reason and the customer sees it", async ({ page, browser }) => {
    const [variant] = await stockedVariants(1, 1, 6);
    const order = await deliveredOrder([{ variantId: variant!.id, quantity: 1 }]);
    const ret = await requestReturn(order, { reasonCode: "CHANGED_MIND" });
    await adminSignIn(page);
    await page.goto(`/admin/returns/${ret.id}`);
    await expect(status(page)).toHaveText("Requested");
    await page.getByTestId("action-reject").click();
    const dialog = page.getByRole("dialog");
    await dialog.getByRole("button", { name: "Reject request" }).click();
    await expect(dialog.getByText("Write a reason.")).toBeVisible();
    await dialog.getByLabel("Reason").fill("The item was worn and washed");
    await dialog.getByRole("button", { name: "Reject request" }).click();
    await expect(status(page)).toHaveText("Not accepted");
    await expect(page.getByTestId("reject-reason")).toContainText("The item was worn and washed");
    await expect(page.getByTestId("return-actions")).toHaveCount(0);

    const customer = await browser.newContext({ baseURL: E2E.webUrl });
    const customerPage = await customer.newPage();
    await signInCustomer(customer, order.mobile);
    await customerPage.goto(`/account/returns/${ret.id}`);
    await expect(status(customerPage)).toHaveText("Not accepted");
    await expect(customerPage.getByTestId("reject-reason")).toContainText("The item was worn and washed");
    await customer.close();
  });

  test("bulk approve and reject work on the ticked returns and list what each did", async ({ page }) => {
    const variants = await stockedVariants(1, 3, 7);
    const refs = [];
    for (const v of variants) refs.push(await requestReturn(await deliveredOrder([{ variantId: v.id, quantity: 1 }]), { reasonCode: "SIZE_FIT" }));
    await adminSignIn(page);
    await page.goto("/admin/returns");
    const search = page.getByLabel("Search returns");

    for (const ref of refs.slice(0, 2)) {
      await search.fill(ref.number);
      await expect(page.getByRole("link", { name: ref.number }).first()).toBeVisible();
      await page.getByRole("checkbox", { name: `Select return ${ref.number}` }).first().click();
    }
    await expect(page.getByTestId("bulk-bar")).toContainText("2 returns selected");
    await page.getByRole("button", { name: "Approve selected" }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Approve selected" }).click();
    const result = page.getByRole("dialog");
    await expect(result.getByTestId("bulk-result")).toHaveCount(2);
    await expect(result).toContainText("2 done, 0 not done.");
    await result.getByRole("button", { name: "Close" }).last().click();

    // The third one is rejected; an already approved one in the same batch is refused and listed as not done.
    await search.fill(refs[2]!.number);
    await page.getByRole("checkbox", { name: `Select return ${refs[2]!.number}` }).first().click();
    await search.fill(refs[0]!.number);
    await page.getByRole("checkbox", { name: `Select return ${refs[0]!.number}` }).first().click();
    await page.getByRole("button", { name: "Reject selected" }).click();
    const reject = page.getByRole("dialog");
    await reject.getByLabel("Reason for the customers").fill("Out of the return period for this item");
    await reject.getByRole("button", { name: "Reject selected" }).click();
    const outcome = page.getByRole("dialog");
    await expect(outcome.getByTestId("bulk-result")).toHaveCount(2);
    await expect(outcome).toContainText("1 done, 1 not done.");
    await expect(outcome).toContainText("That step is not possible now.");

    // A chip filters the table by stage.
    await outcome.getByRole("button", { name: "Close" }).last().click();
    await search.fill("");
    await page.getByTestId("chip-awaitingPickup").click();
    await expect(page.getByTestId("chip-awaitingPickup")).toHaveAttribute("aria-pressed", "true");
    await expect(page.getByRole("link", { name: refs[1]!.number }).first()).toBeVisible();
    await expect(page.getByRole("link", { name: refs[2]!.number })).toHaveCount(0);
  });

  test("a staff member who may only view returns sees no actions, and one who may only approve sees only Approve", async ({ page, context }) => {
    const [variant] = await stockedVariants(1, 1, 10);
    const order = await deliveredOrder([{ variantId: variant!.id, quantity: 1 }]);
    const ret = await requestReturn(order);

    const viewer = await staffWithPermissions(["returns.return.view"]);
    await signInViaBff(context, viewer.email, viewer.password);
    await page.goto("/admin/returns");
    await expect(page.getByRole("heading", { level: 1, name: "Returns" })).toBeVisible();
    await expect(page.getByRole("checkbox")).toHaveCount(0); // no bulk selection without approve
    await page.goto(`/admin/returns/${ret.id}`);
    await expect(status(page)).toHaveText("Requested");
    await expect(page.getByTestId("return-actions")).toHaveCount(0);
    await expect(page.getByText("This return is waiting for a step you do not have permission for.")).toBeVisible();
    await expect(page.getByRole("button", { name: "Add note" })).toHaveCount(0);
    // The API refuses the same step if it is called anyway.
    const attempt = await context.request.post(`/api/bff/api/v1/returns/${ret.id}/approve`, { data: {} });
    expect(attempt.status()).toBe(403);

    await context.clearCookies();
    const approver = await staffWithPermissions(["returns.return.view", "returns.return.approve"]);
    await signInViaBff(context, approver.email, approver.password);
    await page.goto(`/admin/returns/${ret.id}`);
    await expect(page.getByTestId("return-actions").getByRole("button")).toHaveText(["Approve"]);
    await expect(page.getByTestId("action-reject")).toHaveCount(0);
  });

  test("a return that is not in my branch or does not exist is a clear not-found, not a crash", async ({ page, context }) => {
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);
    await page.goto("/admin/returns/00000000-0000-4000-8000-000000000000");
    await expect(page.getByText("We could not find that return.").first()).toBeVisible();
  });
});

test.describe("Returns: customer pages need a customer", () => {
  test("a visitor is sent to sign in, and a missing order explains itself", async ({ page, context }) => {
    await page.goto("/account/returns");
    await expect(page).toHaveURL(/\/login/);
    await signInCustomer(context);
    await page.goto("/account/returns/new");
    await expect(page.getByText("No order chosen")).toBeVisible();
    await page.goto("/account/returns");
    await expect(page.getByText("No returns in progress")).toBeVisible();
    await page.goto("/account");
    await expect(page.getByTestId("tile-returns")).toContainText("0 in progress");
    await page.getByTestId("tile-returns").click();
    await expect(page).toHaveURL(/\/account\/returns$/);
  });
});
