import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { adjust, createCard, customerIdOf, setLoyalty } from "./support/loyalty";
import { disableAllRules, staffToken, topUpHeadOfficeStock } from "./support/orders";
import { customerAccess, freshMobile } from "./support/returns";
import { addToCart, pick, sampleVariants, signInCustomer } from "./support/store";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";

/**
 * The rewards screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console errors.
 * Screenshots go to docs/ui-checks/loyalty/.
 */
const WIDTHS = [360, 768, 1280] as const;

let admin = "";

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "loyalty"), { recursive: true });
  admin = await staffToken("admin");
  await topUpHeadOfficeStock(admin, loadState().branches.ho.id);
  await disableAllRules(admin);
  await setLoyalty(admin, { enabled: true, minPointsToRedeem: 50, maxTenderPercent: 40 });
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `loyalty/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

async function fillAddress(page: Page, phone: string) {
  await page.getByLabel("Recipient name").fill("Tania Islam");
  await page.getByLabel("Recipient phone").fill(phone);
  await pick(page, page.getByLabel("Division"), "Dhaka");
  await pick(page, page.getByLabel("District"), "Dhaka");
  await page.getByLabel("Street address").fill("House 12, Road 5, Dhanmondi");
  await expect(page.getByTestId("delivery-charge")).toBeVisible();
}

for (const width of WIDTHS) {
  test(`customer rewards and checkout tender at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const customerMobile = await signInCustomer(context);
    const customerId = await customerIdOf(admin, customerMobile);
    await adjust(admin, customerId, { points: 300, credit: 80 });

    await page.goto("/account/rewards");
    await expect(page.getByTestId("rewards-page")).toBeVisible();
    await expect(page.getByTestId("points-balance")).toHaveText("300");
    await checks(page, width, errors, `rewards-${width}`);

    const dear = [...(await sampleVariants(context.request, 6))].sort((a, b) => b.price - a.price)[0]!;
    await addToCart(context, dear.id, 3);
    await page.goto("/checkout");
    await fillAddress(page, customerMobile);
    await expect(page.getByTestId("tender-section")).toBeVisible();
    await page.getByTestId("tender-points").fill("100");
    await page.getByTestId("tender-credit").fill("40");
    await page.getByTestId("tender-apply").click();
    await expect(page.getByTestId("tender-plan")).toBeVisible();
    await checks(page, width, errors, `checkout-tender-${width}`);

    // A refused tender: the explanation is crimson with an icon and a text.
    await page.getByTestId("tender-gift").fill("ABCD-EFGH-JKLM-NPQR");
    await page.getByTestId("tender-apply").click();
    await expect(page.getByTestId("tender-problem")).toBeVisible();
    await checks(page, width, errors, `checkout-tender-problem-${width}`);
  });

  test(`staff rewards screens at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const state = loadState().admin;
    await signInViaBff(context, state.email, state.password);
    await createCard(admin, 250);
    const lookupMobile = freshMobile();
    await customerAccess(lookupMobile); // a customer for the lookup tab

    await page.goto("/admin/loyalty");
    await expect(page.getByTestId("loyalty-settings")).toBeVisible();
    await checks(page, width, errors, `settings-${width}`);

    await page.getByRole("tab", { name: "Gift cards" }).click();
    await expect(page.locator('[data-testid="card-details"]:visible').first()).toBeVisible();
    await checks(page, width, errors, `cards-${width}`);

    await page.getByTestId("card-create").click();
    await expect(page.getByTestId("card-amount")).toBeVisible();
    await checks(page, width, errors, `card-create-${width}`);
    await page.getByTestId("card-amount").fill("120");
    await page.getByTestId("dialog-confirm").click();
    await expect(page.getByTestId("card-code")).toBeVisible();
    await checks(page, width, errors, `card-code-${width}`);
    await page.getByTestId("card-code-done").click();

    await page.getByRole("tab", { name: "Referrals" }).click();
    await expect(page.getByText("A referral is rewarded when")).toBeVisible();
    await checks(page, width, errors, `referrals-${width}`);

    await page.getByRole("tab", { name: "Customer rewards" }).click();
    await page.getByTestId("lookup-search").fill(lookupMobile);
    await page.getByTestId("lookup-result").first().click();
    await expect(page.getByTestId("customer-rewards")).toBeVisible();
    await checks(page, width, errors, `customer-${width}`);

    await page.getByTestId("adjust-open").click();
    await expect(page.getByTestId("adjust-reason")).toBeVisible();
    await checks(page, width, errors, `adjust-${width}`);
  });
}

test("rewards screens in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const mobile = await signInCustomer(context);
  await adjust(admin, await customerIdOf(admin, mobile), { points: 200, credit: 30 });
  await page.goto("/account/rewards");
  await expect(page.getByTestId("rewards-page")).toBeVisible();
  await checks(page, 1280, errors, "rewards-dark");

  const staffContext = await page.context().browser()!.newContext();
  await staffContext.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const staff = await staffContext.newPage();
  staff.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await staff.setViewportSize({ width: 1280, height: 900 });
  const state = loadState().admin;
  await signInViaBff(staffContext, state.email, state.password);
  await staff.goto("/admin/loyalty");
  await expect(staff.getByTestId("loyalty-settings")).toBeVisible();
  await checks(staff, 1280, errors, "settings-dark");
  await staffContext.close();
});
