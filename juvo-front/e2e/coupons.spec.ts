import { test, expect, type Locator, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { pick } from "./support/store";
import { signInViaBff } from "./support/ui";
import { call } from "./support/cod";
import { adminAccess } from "./support/returns";
import { dhakaLocalIn, findCoupon, makeCoupon, newCode, placeOrderWithCoupon, readCoupon, removeCoupon, type CouponApi } from "./support/coupons";

/**
 * Coupon screens in the browser against the real API: create percentage and fixed coupons, validation, a duplicate code, edit, switch
 * off and on, delete an unused coupon, a used coupon is kept (its use comes from a real order placed through the shop), the status filter
 * and who may see or change what. Every test makes its own coupons (random codes) and removes the unused ones again.
 */
async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

const made: string[] = [];
const track = async (patch: Parameters<typeof makeCoupon>[0] = {}): Promise<CouponApi> => {
  const coupon = await makeCoupon(patch);
  made.push(coupon.id);
  return coupon;
};
test.afterEach(async () => {
  while (made.length) await removeCoupon(made.pop()!);
});

/** Codes shown on screen (the table and the phone cards both exist in the page; only one is visible). */
const codes = (page: Page) => page.getByTestId("coupon-code").filter({ visible: true });
const toast = (page: Page, text: string) => expect(page.getByText(text).first()).toBeVisible();

/** Opens the list filtered to one code and returns that coupon's row. */
async function rowOf(page: Page, code: string): Promise<Locator> {
  await page.goto("/admin/coupons");
  await page.getByLabel("Search coupons").fill(code);
  const row = page.getByRole("row").filter({ hasText: code });
  await expect(row).toHaveCount(1);
  return row;
}

async function openNew(page: Page) {
  await page.goto("/admin/coupons");
  await expect(page.getByRole("heading", { level: 1, name: "Coupons" })).toBeVisible();
  await page.getByTestId("coupon-new").click();
  await expect(page.getByTestId("coupon-form")).toBeVisible();
}

test.describe("Coupons: create", () => {
  test("a percentage coupon with a cap, minimum, Dhaka dates, a limit and stacking", async ({ page }) => {
    const code = newCode("PCT");
    const start = dhakaLocalIn(24);
    const end = dhakaLocalIn(72);
    await adminSignIn(page);
    await openNew(page);
    await page.locator("#cp-code").fill(`${code.toLowerCase().slice(0, 4)} ${code.slice(4)}`); // typed lower case with a space
    await expect(page.locator("#cp-code")).toHaveValue(code);
    await page.locator("#cp-description").fill("E2E percentage coupon");
    await page.locator("#cp-value").fill("10");
    await page.locator("#cp-cap").fill("150");
    await page.locator("#cp-min").fill("1,000");
    await expect(page.getByTestId("coupon-example")).toContainText("takes ৳150 off"); // 10 percent of 2,000 is 200, capped at 150
    await page.locator("#cp-start").fill(start);
    await page.locator("#cp-end").fill(end);
    await page.locator("#cp-limit").fill("5");
    await page.getByTestId("coupon-stacks").click();
    await expect(page.getByTestId("coupon-stacks-explain")).toContainText("added on top of promotions that allow it");
    await page.getByTestId("coupon-save").click();
    await toast(page, `Coupon ${code} created.`);
    await expect(page.getByTestId("coupon-form")).toHaveCount(0);

    const saved = (await findCoupon(code))!;
    expect(saved).not.toBeNull();
    made.push(saved.id);
    expect(saved).toMatchObject({ kind: "Percent", value: 10, maxDiscount: 150, minOrderValue: 1000, usageLimit: 5, usedCount: 0, isActive: true, stacksWithPromotions: true, description: "E2E percentage coupon" });
    // typed as Dhaka time, stored as the same moment
    expect(Date.parse(saved.startsAt!)).toBe(Date.parse(`${start}:00+06:00`));
    expect(Date.parse(saved.endsAt!)).toBe(Date.parse(`${end}:00+06:00`));

    const row = await rowOf(page, code);
    await expect(row.getByTestId("coupon-discount")).toContainText("10% off");
    await expect(row.getByTestId("coupon-discount")).toContainText("Up to ৳150 off");
    await expect(row.getByTestId("coupon-uses")).toHaveText("0 of 5");
    await expect(row.getByTestId("coupon-status")).toHaveAttribute("data-status", "NotStarted");
    await expect(row).toContainText("Can be combined");
  });

  test("a fixed amount coupon typed with Bengali digits; the cap field does not exist for it", async ({ page }) => {
    const code = newCode("FIX");
    await adminSignIn(page);
    await openNew(page);
    await page.locator("#cp-code").fill(code);
    await page.getByTestId("kind-Fixed").click();
    await expect(page.locator("#cp-cap")).toHaveCount(0);
    await page.locator("#cp-value").fill("৫০০");
    await page.locator("#cp-min").fill("৳১,৫০০.৫০");
    await expect(page.getByTestId("coupon-example")).toContainText("takes ৳500 off");
    await page.getByTestId("coupon-save").click();
    await toast(page, `Coupon ${code} created.`);
    const saved = (await findCoupon(code))!;
    made.push(saved.id);
    expect(saved).toMatchObject({ kind: "Fixed", value: 500, minOrderValue: 1500.5, maxDiscount: null, usageLimit: null, startsAt: null, endsAt: null, isActive: true, stacksWithPromotions: false });
    const row = await rowOf(page, code);
    await expect(row.getByTestId("coupon-discount")).toContainText("৳500 off");
    await expect(row).toContainText("Better of the two");
    await expect(row.getByTestId("coupon-status")).toHaveAttribute("data-status", "Active");
  });

  test("make a code fills a valid readable code", async ({ page }) => {
    await adminSignIn(page);
    await openNew(page);
    await page.getByTestId("coupon-generate").click();
    await expect(page.locator("#cp-code")).toHaveValue(/^JV-[A-HJ-NP-Z2-9]{6}$/);
  });

  test("validation: every message shows before anything is sent", async ({ page }) => {
    await adminSignIn(page);
    await openNew(page);
    await page.getByTestId("coupon-save").click();
    await expect(page.getByText("Enter a code.")).toBeVisible();
    await expect(page.getByText("Enter the discount.")).toBeVisible();
    await expect(page.locator("#cp-code")).toHaveAttribute("aria-invalid", "true");

    await page.locator("#cp-code").fill("AB");
    await page.locator("#cp-value").fill("150");
    await page.locator("#cp-min").fill("-5");
    await page.locator("#cp-limit").fill("0");
    await page.locator("#cp-start").fill("2030-02-02T10:00");
    await page.locator("#cp-end").fill("2030-02-01T10:00");
    await page.getByTestId("coupon-save").click();
    await expect(page.getByText("A code has 3 to 40 characters.")).toBeVisible();
    await expect(page.getByText("A percentage cannot be more than 100.")).toBeVisible();
    await expect(page.getByText("The minimum order cannot be negative.")).toBeVisible();
    await expect(page.getByText("The limit must be at least 1, or empty for no limit.")).toBeVisible();
    await expect(page.getByText("The end must be after the start.")).toBeVisible();

    await page.locator("#cp-code").fill("A_B-1");
    await page.locator("#cp-value").fill("12.345");
    await page.locator("#cp-min").fill("");
    await page.locator("#cp-limit").fill("x");
    await page.locator("#cp-end").fill("");
    await page.getByTestId("coupon-save").click();
    await expect(page.getByText("Use at most 2 decimals.").first()).toBeVisible();
    await expect(page.getByText("Enter a whole number, or leave it empty for no limit.")).toBeVisible();
    await expect(page.getByTestId("coupon-form")).toBeVisible();
    expect(await findCoupon("A_B-1")).toBeNull();
  });

  test("a code that exists is refused with a clear message and the dialog stays open", async ({ page }) => {
    const existing = await track();
    await adminSignIn(page);
    await openNew(page);
    await page.locator("#cp-code").fill(existing.code.toLowerCase());
    await page.locator("#cp-value").fill("5");
    await page.getByTestId("coupon-save").click();
    await expect(page.getByText("A coupon with this code already exists. Choose another code.").first()).toBeVisible();
    await expect(page.locator("#cp-code")).toHaveAttribute("aria-invalid", "true");
    await expect(page.getByTestId("coupon-form")).toBeVisible();
    // changing the code clears the message and the next save works
    const code = newCode("DUP");
    await page.locator("#cp-code").fill(code);
    await page.getByTestId("coupon-save").click();
    await toast(page, `Coupon ${code} created.`);
    const saved = (await findCoupon(code))!;
    made.push(saved.id);
  });
});

test.describe("Coupons: change, switch and delete", () => {
  test("edit keeps the code fixed and saves the other fields", async ({ page }) => {
    const coupon = await track({ description: "before", value: 10, usageLimit: 10 });
    await adminSignIn(page);
    const row = await rowOf(page, coupon.code);
    await row.getByTestId("coupon-edit").click();
    await expect(page.locator("#cp-code")).toBeDisabled();
    await expect(page.locator("#cp-code")).toHaveValue(coupon.code);
    await expect(page.getByTestId("coupon-used-note")).toContainText("Already used 0 times");
    await page.locator("#cp-description").fill("after");
    await page.locator("#cp-value").fill("25");
    await page.locator("#cp-limit").fill("");
    await page.getByTestId("coupon-active").click();
    await expect(page.getByText("Nobody can use it, also not carts that already hold it.")).toBeVisible();
    await page.getByTestId("coupon-save").click();
    await toast(page, `Coupon ${coupon.code} saved.`);
    expect(await readCoupon(coupon.id)).toMatchObject({ code: coupon.code, description: "after", value: 25, usageLimit: null, isActive: false });
    const after = await rowOf(page, coupon.code);
    await expect(after.getByTestId("coupon-status")).toHaveAttribute("data-status", "Off");
    await expect(after.getByTestId("coupon-discount")).toContainText("25% off");
  });

  test("switch a coupon off and on from the list", async ({ page }) => {
    const coupon = await track({ startsAt: `${dhakaLocalIn(-48)}:00+06:00`, endsAt: `${dhakaLocalIn(48)}:00+06:00`, usageLimit: 3, stacksWithPromotions: true, description: "keeps everything" });
    await adminSignIn(page);
    const row = await rowOf(page, coupon.code);
    await expect(row.getByTestId("coupon-status")).toHaveAttribute("data-status", "Active");
    await row.getByTestId("coupon-toggle").click();
    await toast(page, `Coupon ${coupon.code} is switched off.`);
    await expect(row.getByTestId("coupon-status")).toHaveAttribute("data-status", "Off");
    const off = (await readCoupon(coupon.id))!;
    expect(off.isActive).toBe(false);
    // nothing else changed
    expect(Date.parse(off.startsAt!)).toBe(Date.parse(coupon.startsAt!));
    expect(Date.parse(off.endsAt!)).toBe(Date.parse(coupon.endsAt!));
    expect(off).toMatchObject({ usageLimit: 3, stacksWithPromotions: true, description: "keeps everything", value: 10 });
    await row.getByTestId("coupon-toggle").click();
    await toast(page, `Coupon ${coupon.code} is switched on.`);
    await expect(row.getByTestId("coupon-status")).toHaveAttribute("data-status", "Active");
    expect((await readCoupon(coupon.id))!.isActive).toBe(true);
  });

  test("an unused coupon is deleted after the dialog says so", async ({ page }) => {
    const coupon = await track();
    await adminSignIn(page);
    const row = await rowOf(page, coupon.code);
    await row.getByTestId("coupon-delete").click();
    await expect(page.getByTestId("remove-consequence")).toHaveAttribute("data-plan", "delete");
    await expect(page.getByTestId("remove-consequence")).toContainText("deleted for good");
    await page.getByRole("button", { name: "Cancel" }).click();
    expect(await readCoupon(coupon.id)).not.toBeNull();
    await row.getByTestId("coupon-delete").click();
    await page.getByTestId("remove-confirm").click();
    await toast(page, `Coupon ${coupon.code} deleted.`);
    await expect(page.getByRole("row").filter({ hasText: coupon.code })).toHaveCount(0);
    expect(await readCoupon(coupon.id)).toBeNull();
  });

  test("a used coupon cannot be deleted: the dialog switches it off and keeps it", async ({ page, playwright }) => {
    test.setTimeout(180_000);
    const coupon = await track({ usageLimit: 1, value: 5 });
    await placeOrderWithCoupon(playwright, coupon.code);
    expect((await readCoupon(coupon.id))!.usedCount).toBe(1);
    // the API itself refuses to delete it
    const refused = await call("DELETE", `/api/v1/promotions/coupons/${coupon.id}`, { token: await adminAccess() });
    expect(refused.status).toBe(409);

    await adminSignIn(page);
    const row = await rowOf(page, coupon.code);
    await expect(row.getByTestId("coupon-uses")).toHaveText("1 of 1");
    await expect(row.getByTestId("coupon-status")).toHaveAttribute("data-status", "UsedUp");
    await row.getByTestId("coupon-delete").click();
    await expect(page.getByTestId("remove-consequence")).toHaveAttribute("data-plan", "switchOff");
    await expect(page.getByTestId("remove-consequence")).toContainText("was used 1 time");
    await expect(page.getByTestId("remove-consequence")).toContainText("cannot be deleted");
    await page.getByTestId("remove-confirm").click();
    await toast(page, `Coupon ${coupon.code} is switched off and kept for history.`);
    expect(await readCoupon(coupon.id)).toMatchObject({ isActive: false, usedCount: 1 });
    await expect(row.getByTestId("coupon-status")).toHaveAttribute("data-status", "Off");

    // deleting it again only explains: it is already off, nothing to confirm
    await row.getByTestId("coupon-delete").click();
    await expect(page.getByTestId("remove-consequence")).toHaveAttribute("data-plan", "alreadyOff");
    await expect(page.getByTestId("remove-confirm")).toHaveCount(0);
    await page.getByRole("button", { name: "Close" }).first().click();

    // the limit cannot go below the uses already counted (the edit form says how many)
    await row.getByTestId("coupon-edit").click();
    await expect(page.getByTestId("coupon-used-note")).toContainText("Already used 1 time");
    await page.locator("#cp-limit").fill("1");
    await page.getByTestId("coupon-save").click();
    await toast(page, `Coupon ${coupon.code} saved.`);
  });
});

test.describe("Coupons: list, search and status filter", () => {
  test("search by code or note and the derived status filter", async ({ page }) => {
    const prefix = newCode("FLT");
    const expired = await track({ code: `${prefix}EXP`, startsAt: `${dhakaLocalIn(-72)}:00+06:00`, endsAt: `${dhakaLocalIn(-24)}:00+06:00` });
    const later = await track({ code: `${prefix}LAT`, startsAt: `${dhakaLocalIn(48)}:00+06:00`, description: `note ${prefix}` });
    const off = await track({ code: `${prefix}OFF`, isActive: false });
    const live = await track({ code: `${prefix}LIV` });
    await adminSignIn(page);
    await page.goto("/admin/coupons");
    await page.getByLabel("Search coupons").fill(prefix);
    await expect(codes(page)).toHaveCount(4);

    const status = page.getByRole("combobox", { name: /^Status/ });
    for (const [label, coupon] of [["Expired", expired], ["Not started", later], ["Switched off", off], ["Active", live]] as const) {
      await pick(page, status, label);
      await expect(codes(page)).toHaveCount(1);
      await expect(codes(page)).toHaveText(coupon.code);
    }
    await pick(page, status, "Used up");
    await expect(codes(page)).toHaveCount(0);
    await page.getByRole("button", { name: "Clear filters" }).click();
    await page.getByLabel("Search coupons").fill(prefix);
    await expect(codes(page)).toHaveCount(4);

    // the note is searched too
    await page.getByLabel("Search coupons").fill(`note ${prefix}`);
    await expect(codes(page)).toHaveCount(1);
    await expect(codes(page)).toHaveText(later.code);
  });

  test("the shared pager splits a long list into pages of 10", async ({ page }) => {
    const prefix = newCode("PGR");
    for (let i = 0; i < 12; i++) await track({ code: `${prefix}${String(i).padStart(2, "0")}` });
    await adminSignIn(page);
    await page.goto("/admin/coupons");
    await page.getByLabel("Search coupons").fill(prefix);
    await expect(codes(page)).toHaveCount(10);
    await expect(page.getByTestId("pagination")).toBeVisible();
    await expect(page.getByTestId("pagination-summary")).toContainText("12");
    await page.getByRole("button", { name: /Next/ }).first().click();
    await expect(codes(page)).toHaveCount(2);
  });
});

test.describe("Coupons: permissions", () => {
  test("view only: the list is read-only, no buttons at all", async ({ page }) => {
    const coupon = await track();
    const staff = await staffWithPermissions(["promotions.coupon.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    const row = await rowOf(page, coupon.code);
    await expect(page.getByTestId("coupons-read-only")).toBeVisible();
    await expect(page.getByTestId("coupon-new")).toHaveCount(0);
    await expect(row.getByTestId("coupon-edit")).toHaveCount(0);
    await expect(row.getByTestId("coupon-toggle")).toHaveCount(0);
    await expect(row.getByTestId("coupon-delete")).toHaveCount(0);
    await expect(row.getByTestId("coupon-status")).toBeVisible();
  });

  test("create without edit or delete: can add a coupon, cannot change or remove one", async ({ page }) => {
    const coupon = await track();
    const staff = await staffWithPermissions(["promotions.coupon.view", "promotions.coupon.create"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    const row = await rowOf(page, coupon.code);
    await expect(page.getByTestId("coupon-new")).toBeVisible();
    await expect(row.getByTestId("coupon-edit")).toHaveCount(0);
    await expect(row.getByTestId("coupon-delete")).toHaveCount(0);
    const code = newCode("CRT");
    await page.getByTestId("coupon-new").click();
    await page.locator("#cp-code").fill(code);
    await page.locator("#cp-value").fill("7");
    await page.getByTestId("coupon-save").click();
    await toast(page, `Coupon ${code} created.`);
    made.push((await findCoupon(code))!.id);
  });

  test("edit without delete: edit and switch work, delete is absent, the API agrees", async ({ page }) => {
    const coupon = await track();
    const staff = await staffWithPermissions(["promotions.coupon.view", "promotions.coupon.edit"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    const row = await rowOf(page, coupon.code);
    await expect(page.getByTestId("coupon-new")).toHaveCount(0);
    await expect(row.getByTestId("coupon-delete")).toHaveCount(0);
    await row.getByTestId("coupon-toggle").click();
    await toast(page, `Coupon ${coupon.code} is switched off.`);
    expect((await readCoupon(coupon.id))!.isActive).toBe(false);
    const login = await call<{ accessToken: string }>("POST", "/api/v1/auth/login", { body: { identifier: staff.email, password: staff.password }, expect: 200 });
    const denied = await call("DELETE", `/api/v1/promotions/coupons/${coupon.id}`, { token: login.json.accessToken });
    expect([401, 403]).toContain(denied.status);
    expect(await readCoupon(coupon.id)).not.toBeNull();
  });

  test("delete without edit: an unused coupon is deleted, a used one can only be explained", async ({ page, playwright }) => {
    test.setTimeout(180_000);
    const unused = await track();
    const used = await track({ usageLimit: 5 });
    await placeOrderWithCoupon(playwright, used.code);
    const staff = await staffWithPermissions(["promotions.coupon.view", "promotions.coupon.delete"]);
    await signInViaBff(page.context(), staff.email, staff.password);

    let row = await rowOf(page, used.code);
    await expect(row.getByTestId("coupon-edit")).toHaveCount(0);
    await expect(row.getByTestId("coupon-toggle")).toHaveCount(0);
    await row.getByTestId("coupon-delete").click();
    await expect(page.getByTestId("remove-consequence")).toHaveAttribute("data-plan", "noEditRight");
    await expect(page.getByTestId("remove-confirm")).toHaveCount(0);
    await page.getByRole("button", { name: "Close" }).first().click();
    expect(await readCoupon(used.id)).toMatchObject({ isActive: true, usedCount: 1 });

    row = await rowOf(page, unused.code);
    await row.getByTestId("coupon-delete").click();
    await page.getByTestId("remove-confirm").click();
    await toast(page, `Coupon ${unused.code} deleted.`);
    expect(await readCoupon(unused.id)).toBeNull();
  });

  test("without the view permission the menu item and the page are closed", async ({ page }) => {
    const staff = await staffWithPermissions(["catalog.product.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin");
    await expect(page.getByRole("link", { name: "Coupons", exact: true })).toHaveCount(0);
    await page.goto("/admin/coupons");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("the menu lists Coupons next to Promotions for staff who may view them", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin");
    await page.getByRole("button", { name: "Marketing" }).click();
    await page.getByRole("link", { name: "Coupons", exact: true }).first().click();
    await expect(page).toHaveURL(/\/admin\/coupons$/);
    await expect(page.getByRole("heading", { level: 1, name: "Coupons" })).toBeVisible();
  });
});
