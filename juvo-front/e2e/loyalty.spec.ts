import { test, expect, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { api, loadState } from "./support/env";
import { adjust, amountOf, balances, createCard, customerIdOf, deactivateCard, setLoyalty } from "./support/loyalty";
import { disableAllRules, staffToken, topUpHeadOfficeStock } from "./support/orders";
import { deliveredOrder, advanceTo, freshMobile, requestReturn, stockedVariants } from "./support/returns";
import { addToCart, newMobile, pick, sampleVariants, signInCustomer } from "./support/store";
import { signInViaBff } from "./support/ui";

/**
 * Module AB (rewards) against the real stack: the feature is off by default and hidden from customers; staff set the rules up and make
 * gift cards; customers see balances, redeem a card and use a referral code; checkout takes points, store credit and a gift card and
 * the order, its payments and the cancellation follow; a return is refunded to store credit.
 */
let admin = "";

test.beforeAll(async () => {
  admin = await staffToken("admin");
  await topUpHeadOfficeStock(admin, loadState().branches.ho.id);
  await disableAllRules(admin);
  // The feature starts off (the default); the rules are made small so the sample basket can use them.
  await setLoyalty(admin, { enabled: false, minPointsToRedeem: 50, maxTenderPercent: 40 });
});

async function fillAddress(page: Page, name: string, phone: string) {
  await page.getByLabel("Recipient name").fill(name);
  await page.getByLabel("Recipient phone").fill(phone);
  await pick(page, page.getByLabel("Division"), "Dhaka");
  await pick(page, page.getByLabel("District"), "Dhaka");
  await page.getByLabel("Street address").fill("House 12, Road 5, Dhanmondi");
  await expect(page.getByTestId("delivery-charge")).toBeVisible();
}

test.describe.configure({ mode: "serial" });

test("with the feature off customers see no rewards and staff can still open the settings", async ({ page, context, browser }) => {
  const [dear] = [...(await sampleVariants(context.request, 6))].sort((a, b) => b.price - a.price);
  await signInCustomer(context);
  await addToCart(context, dear!.id, 2);

  await page.goto("/account");
  await expect(page.getByTestId("tile-orders")).toBeVisible();
  await expect(page.getByRole("link", { name: "My rewards" })).toHaveCount(0);

  await page.goto("/account/rewards");
  await expect(page.getByText("Rewards are not open yet")).toBeVisible();
  await expect(page.getByTestId("rewards-page")).toHaveCount(0);

  await page.goto("/checkout");
  await expect(page.getByTestId("place-order")).toBeVisible();
  await expect(page.getByTestId("tender-section")).toHaveCount(0);

  const staff = await browser.newContext();
  const staffPage = await staff.newPage();
  await signInViaBff(staff, loadState().admin.email, loadState().admin.password);
  await staffPage.goto("/admin/loyalty");
  await expect(staffPage.getByTestId("loyalty-settings")).toBeVisible();
  await expect(staffPage.getByTestId("enabled-consequence")).toContainText("Off: nothing is earned");
  await staff.close();
});

test("staff change the rules, see them refused when out of range, and switch the feature on", async ({ page, context }) => {
  await signInViaBff(context, loadState().admin.email, loadState().admin.password);
  await page.goto("/admin/loyalty");
  await expect(page.getByTestId("setting-maxTenderPercent")).toHaveValue("40");

  await page.getByTestId("setting-maxTenderPercent").fill("101");
  await page.getByTestId("settings-save").click();
  await expect(page.getByText("This number is outside the allowed range.")).toBeVisible();

  await page.getByTestId("setting-maxTenderPercent").fill("40");
  await page.getByTestId("setting-referralRewardReferrer").fill("120");
  await page.getByTestId("setting-referralRewardReferee").fill("80");
  await page.getByTestId("setting-enabled").click();
  await expect(page.getByTestId("enabled-consequence")).toContainText("On: customers earn points");
  await page.getByTestId("settings-save").click();
  await expect(page.getByText("Settings saved.")).toBeVisible();

  await page.reload();
  await expect(page.getByTestId("setting-referralRewardReferrer")).toHaveValue("120");
  await expect(page.getByTestId("setting-enabled")).toHaveAttribute("aria-checked", "true");
  const saved = await api<{ enabled: boolean; referralRewardReferee: number | string }>("GET", "/api/v1/loyalty/settings", undefined, admin);
  expect(saved.json.enabled).toBe(true);
  expect(Number(saved.json.referralRewardReferee)).toBe(80);
});

test("staff create a gift card, see the code once, and deactivate a card", async ({ page, context }) => {
  await signInViaBff(context, loadState().admin.email, loadState().admin.password);
  await page.goto("/admin/loyalty");
  await page.getByRole("tab", { name: "Gift cards" }).click();

  await page.getByTestId("card-create").click();
  await page.getByTestId("card-amount").fill("50");
  await page.getByTestId("dialog-confirm").click();
  await expect(page.getByText("The amount must be between")).toBeVisible();
  await page.getByTestId("card-amount").fill("300");
  await page.getByTestId("card-note").fill("E2E shown once");
  await page.getByTestId("card-send").check();
  await page.getByTestId("dialog-confirm").click();
  await expect(page.getByText("Enter a mobile number or an email")).toBeVisible();
  await page.getByTestId("card-send").uncheck();
  await page.getByTestId("dialog-confirm").click();

  const code = (await page.getByTestId("card-code").innerText()).trim();
  expect(code).toMatch(/^[A-HJ-NP-Z2-9]{4}(-[A-HJ-NP-Z2-9]{4}){3}$/);
  await page.getByTestId("card-code-done").click();
  await expect(page.getByTestId("card-code")).toHaveCount(0);

  // The code is never on the page again: only the last four characters.
  await expect(page.getByTestId("card-create")).toBeVisible();
  expect(await page.content()).not.toContain(code.replaceAll("-", ""));
  expect(await page.content()).not.toContain(code);
  const hint = code.slice(-4);
  await expect(page.getByText(`••••-${hint}`).first()).toBeVisible();

  await page.getByTestId("card-details").first().click();
  await expect(page.getByTestId("card-detail")).toContainText("E2E shown once");
  await expect(page.getByTestId("card-entries")).toContainText("Issued");
  await expect(page.getByTestId("card-detail")).toContainText("never shown again");
  await page.keyboard.press("Escape");

  // Deactivate the newest card (the one just made) and see the status change.
  await page.getByTestId("card-deactivate").first().click();
  await page.getByRole("dialog").getByRole("button", { name: "Deactivate" }).click();
  await expect(page.getByText("The gift card is switched off.")).toBeVisible();
  await expect(page.getByText("Deactivated").first()).toBeVisible();
});

test("a customer sees balances and history, redeems a gift card and meets the lockout and refusal texts", async ({ page, context }) => {
  const mobile = await signInCustomer(context);
  const customerId = await customerIdOf(admin, mobile);
  await adjust(admin, customerId, { points: 500, credit: 50 });
  const live = await createCard(admin, 300);
  const dead = await createCard(admin, 200);
  await deactivateCard(admin, dead.id);

  await page.goto("/account");
  await expect(page.getByRole("link", { name: "My rewards" }).first()).toBeVisible();
  await page.goto("/account/rewards");
  await expect(page.getByTestId("points-balance")).toHaveText("500");
  await expect(page.getByTestId("credit-balance")).toHaveText("৳50");
  await expect(page.getByTestId("rewards-rules")).toContainText("Each point is worth");

  // A wrong but well-formed code, a card staff switched off, then the good card.
  await page.getByLabel("Gift card code").fill("ABCD-EFGH-JKLM-NPQR");
  await page.getByTestId("gift-redeem").click();
  await expect(page.getByText("That gift card code is not right.")).toBeVisible();
  await page.getByLabel("Gift card code").fill("short");
  await page.getByTestId("gift-redeem").click();
  await expect(page.getByText("16 letters and digits.", { exact: false }).first()).toBeVisible();
  await page.getByLabel("Gift card code").fill(dead.code);
  await page.getByTestId("gift-redeem").click();
  await expect(page.getByText("This gift card has been switched off.")).toBeVisible();
  await page.getByLabel("Gift card code").fill(live.code.toLowerCase());
  await page.getByTestId("gift-redeem").click();
  await expect(page.getByTestId("gift-redeemed")).toContainText("৳300 added to your store credit");
  await expect(page.getByTestId("credit-balance")).toHaveText("৳350");

  await page.getByRole("tab", { name: "Store credit" }).click();
  await expect(page.getByText("Gift card added").first()).toBeVisible();
  await page.getByRole("tab", { name: "Points" }).click();
  await expect(page.getByText("Adjusted").first()).toBeVisible();
  expect((await balances(admin, customerId)).credit).toBe(350);
});

test("referral codes: copy, wrong code, own code, and a friend applies it", async ({ page, context, browser }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"], { origin: E2E.webUrl });
  await signInCustomer(context);
  await page.goto("/account/rewards");
  const code = (await page.getByTestId("referral-code").innerText()).trim();
  expect(code).toMatch(/^[A-HJ-NP-Z2-9]{8}$/);
  await page.getByTestId("referral-copy").click();
  await expect(page.getByText("Code copied")).toBeVisible();

  await page.locator("#rw-ref-code").fill(code);
  await page.getByTestId("referral-apply").click();
  await expect(page.getByText("You cannot use your own referral code.")).toBeVisible();

  const friendContext = await browser.newContext();
  const friend = await friendContext.newPage();
  await signInCustomer(friendContext);
  await friend.goto("/account/rewards");
  await friend.locator("#rw-ref-code").fill("ABC");
  await friend.getByTestId("referral-apply").click();
  await expect(friend.getByText("A referral code has 8 letters and digits.")).toBeVisible();
  await friend.locator("#rw-ref-code").fill("ABCDEFGH");
  await friend.getByTestId("referral-apply").click();
  await expect(friend.getByText("That referral code is not right.")).toBeVisible();
  await friend.locator("#rw-ref-code").fill(code.toLowerCase());
  await friend.getByTestId("referral-apply").click();
  await expect(friend.getByTestId("referral-applied")).toBeVisible();
  await expect(friend.getByTestId("referral-applied")).toContainText("Waiting for your first delivery");
  await friendContext.close();

  // Staff see the referral as pending, with the code that was used.
  const staffContext = await browser.newContext();
  const staff = await staffContext.newPage();
  await signInViaBff(staffContext, loadState().admin.email, loadState().admin.password);
  await staff.goto("/admin/loyalty");
  await staff.getByRole("tab", { name: "Referrals" }).click();
  await expect(staff.getByText(code).first()).toBeVisible();
  await expect(staff.getByTestId("referral-status").first()).toBeVisible();
  await staffContext.close();
});

test("staff look a customer up, adjust the balance with a reason and see the ledger check", async ({ page, context }) => {
  const mobile = newMobile();
  await signInCustomer(context, mobile);
  const staffContext = await page.context().browser()!.newContext();
  const staff = await staffContext.newPage();
  await signInViaBff(staffContext, loadState().admin.email, loadState().admin.password);
  await staff.goto("/admin/loyalty");
  await staff.getByRole("tab", { name: "Customer rewards" }).click();
  await staff.getByTestId("lookup-search").fill(mobile);
  await staff.getByTestId("lookup-result").first().click();
  await expect(staff.getByTestId("lookup-points")).toHaveText("0");
  await expect(staff.getByTestId("ledger-consistent")).toBeVisible();

  await staff.getByTestId("adjust-open").click();
  await staff.getByTestId("dialog-confirm").click();
  await expect(staff.getByText("Enter points, store credit or both.")).toBeVisible();
  await expect(staff.getByText("Please write the reason.")).toBeVisible();
  await staff.getByTestId("adjust-points").fill("120");
  await staff.getByTestId("adjust-credit").fill("45.50");
  await staff.getByTestId("adjust-reason").fill("E2E goodwill after call 4412");
  await staff.getByTestId("dialog-confirm").click();
  await expect(staff.getByTestId("lookup-points")).toHaveText("120");
  await expect(staff.getByTestId("lookup-credit")).toHaveText("৳45.50");
  await expect(staff.getByTestId("ledger-consistent")).toBeVisible();
  await expect(staff.getByText("E2E goodwill after call 4412").first()).toBeVisible();

  // A deduction below zero is refused by the API with a clear text? Taking more than held leaves the balance at zero or is refused.
  await staffContext.close();
});

test("checkout: points and store credit pay part of the order, the order shows them, cancelling gives them back", async ({ page, context, browser }) => {
  const dear = [...(await sampleVariants(context.request, 8))].sort((a, b) => b.price - a.price)[0]!;
  const mobile = await signInCustomer(context);
  const customerId = await customerIdOf(admin, mobile);
  await adjust(admin, customerId, { points: 500, credit: 100 });
  await addToCart(context, dear.id, 3);

  await page.goto("/checkout");
  await fillAddress(page, "Tania Islam", mobile);
  const section = page.getByTestId("tender-section");
  await expect(section).toBeVisible();
  await expect(page.getByTestId("tender-balances")).toContainText("500 points");
  const total = amountOf(await page.getByTestId("amount-total").innerText());
  expect(total).toBeGreaterThan(400);

  // Far too many points: the screen lowers it to what the cap allows and says so.
  await page.getByTestId("tender-points").fill("100000");
  await page.getByTestId("tender-apply").click();
  await expect(section).toContainText("We lowered the amount");
  await expect(page.getByTestId("tender-plan")).toBeVisible();
  await page.getByTestId("tender-remove").click();
  await expect(page.getByTestId("tender-plan")).toHaveCount(0);

  await page.getByTestId("tender-points").fill("100");
  await page.getByTestId("tender-credit").fill("50");
  await page.getByTestId("tender-apply").click();
  await expect(page.getByTestId("tender-row-points")).toHaveText("-৳100");
  await expect(page.getByTestId("tender-row-credit")).toHaveText("-৳50");
  await expect(page.getByTestId("tender-left")).toHaveText(new RegExp(String(total - 150).replace(/(\d)(?=(\d{3})+$)/g, "$1,")));
  await expect(page.getByTestId("amount-total")).toContainText(String(total).replace(/(\d)(?=(\d{3})+$)/g, "$1,")); // the total itself does not change

  await page.getByLabel(/I agree to the terms/).click();
  await page.getByTestId("place-order").click();
  await expect(page).toHaveURL(/\/order\/confirmed\/JV-/);
  const number = decodeURIComponent(page.url().split("/").pop()!);
  await expect(page.getByTestId("order-summary")).toContainText("Paid with rewards");
  await expect(page.getByTestId("amount-tender")).toHaveText("-৳150");

  const spent = await balances(admin, customerId);
  expect(spent).toEqual({ points: 400, credit: 50 });

  // Staff: the order page lists the two reward payments next to the order.
  const found = await api<{ items: { id: string; number: string }[] }>("GET", `/api/v1/orders?Search=${encodeURIComponent(number)}`, undefined, admin);
  const orderId = found.json.items.find((o) => o.number === number)!.id;
  const staffContext = await browser.newContext();
  const staff = await staffContext.newPage();
  await signInViaBff(staffContext, loadState().admin.email, loadState().admin.password);
  await staff.goto(`/admin/orders/${orderId}`);
  await expect(staff.getByTestId("order-payments")).toContainText("Reward points");
  await expect(staff.getByTestId("order-payments")).toContainText("Store credit");
  await expect(staff.getByTestId("order-payments")).toContainText("Rewards");

  // A reward payment can be refunded to store credit from its payment page (the dialog offers the new choice).
  await staff.getByTestId("open-payment").first().click();
  await staff.getByTestId("action-refund").click();
  await expect(staff.getByRole("dialog").getByText("To store credit")).toBeVisible();
  await staff.keyboard.press("Escape");

  // Cancelling gives points and credit back to the customer's ledgers.
  const cancelled = await api("POST", `/api/v1/orders/${orderId}/cancel`, { reason: "e2e cancel" }, admin);
  expect(cancelled.status, JSON.stringify(cancelled.json)).toBe(200);
  await expect.poll(async () => balances(admin, customerId)).toEqual({ points: 500, credit: 100 });
  await page.goto("/account/rewards");
  await expect(page.getByTestId("points-balance")).toHaveText("500");
  await expect(page.getByTestId("credit-balance")).toHaveText("৳100");
  await staffContext.close();
});

test("checkout: a guest can use a gift card, a wrong code is explained", async ({ page, context }) => {
  const dear = [...(await sampleVariants(context.request, 6))].sort((a, b) => b.price - a.price)[0]!;
  const card = await createCard(admin, 100);
  await addToCart(context, dear.id, 1);
  await page.goto("/checkout");
  await fillAddress(page, "Guest Buyer", newMobile());
  const section = page.getByTestId("tender-section");
  await expect(section).toBeVisible();
  await expect(page.getByTestId("tender-points")).toHaveCount(0);
  await expect(section).toContainText("Sign in to use points and store credit.");

  await page.getByTestId("tender-gift").fill("ABCD-EFGH-JKLM-NPQR");
  await page.getByTestId("tender-apply").click();
  await expect(page.getByTestId("tender-problem")).toContainText("That gift card code is not right.");
  await page.getByTestId("tender-gift").fill(card.code);
  await page.getByTestId("tender-apply").click();
  await expect(page.getByTestId("tender-row-giftCard")).toHaveText("-৳100");
  await expect(page.getByTestId("tender-problem")).toHaveCount(0);
});

test("a return is refunded to store credit without a reference", async ({ page, context }) => {
  const [variant] = await stockedVariants(1, 1, 5);
  const mobile = freshMobile();
  const order = await deliveredOrder([{ variantId: variant!.id, quantity: 1 }], mobile);
  const ret = await requestReturn(order);
  await advanceTo(ret.id, "Inspected");
  const customerId = await customerIdOf(admin, mobile);
  const before = await balances(admin, customerId);

  await signInViaBff(context, loadState().admin.email, loadState().admin.password);
  await page.goto(`/admin/returns/${ret.id}`);
  await page.getByTestId("action-refund").click();
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel("Paid by").click();
  await page.getByRole("option", { name: "Store credit" }).click();
  await expect(dialog.getByText("Write the reference of the payment.")).toHaveCount(0);
  await dialog.getByRole("button", { name: "Record refund" }).click();
  await expect(page.getByTestId("refund-paid")).toBeVisible();

  const after = await balances(admin, customerId);
  expect(after.credit).toBeGreaterThan(before.credit);
});
