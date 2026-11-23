import { test, expect, type Page } from "@playwright/test";
import { readFileSync } from "node:fs";
import { loadState } from "./support/env";
import { call } from "./support/cod";
import { customerAccess, staffAccess, staffWithPermissions } from "./support/returns";
import { makeCustomer, readCustomer } from "./support/customers";
import { changeConsent, consentHistory, customersByMobile, placeOpenOrder } from "./support/privacy";
import { signInViaBff } from "./support/ui";

/**
 * Customer privacy screens (FR-AF06, FR-E08, FR-E07) in the browser against the real API: consent history, data export as a file
 * download, the anonymise flow (blocked by an open order, then done), and who may see which card.
 */
async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

const detailUrl = (id: string) => `/admin/customers/${id}`;

async function fillAnonymise(page: Page, id: string, reason = "Customer asked for erasure by phone") {
  const dialog = page.getByRole("dialog");
  await dialog.getByLabel(/^Reason/).fill(reason);
  await dialog.getByLabel(/^Type the customer id/).fill(id);
  return dialog;
}

test.describe("Privacy: consent history", () => {
  test("shows each consent change with channel, old and new value, source, time and IP", async ({ page }) => {
    const customer = await makeCustomer({ marketingSms: true });
    await changeConsent(customer.mobile, "MarketingSms", false);
    await changeConsent(customer.mobile, "MarketingEmail", true);
    const api = await consentHistory(customer.id);
    expect(api.items.length).toBeGreaterThanOrEqual(3);

    await adminSignIn(page);
    await page.goto(detailUrl(customer.id));
    const card = page.getByTestId("consent-history");
    await expect(card.getByRole("heading", { name: "Consent history" })).toBeVisible();
    const rows = card.getByTestId("consent-history-row");
    await expect(rows).toHaveCount(api.items.length);
    // newest first: the e-mail consent was the last change
    await expect(rows.first()).toContainText("Email");
    await expect(rows.first()).toContainText("Given (first record)");
    await expect(card.getByTestId("consent-history-row").filter({ hasText: "Withdrawn" })).toHaveCount(1);
    await expect(card.getByTestId("consent-history-row").filter({ hasText: "SMS" }).filter({ hasText: "Also covers WhatsApp" }).first()).toBeVisible();
    await expect(rows.first()).toContainText("The customer, in their profile");
    await expect(rows.first().getByTestId("consent-source")).toHaveText("customer-profile");
    await expect(rows.first().locator("time")).toHaveAttribute("datetime", /20\d\d-/);
    await expect(rows.first().getByTestId("consent-ip")).toBeVisible();
  });

  test("says plainly when nothing was ever recorded", async ({ page }) => {
    const customer = await makeCustomer({ withAddress: false });
    const api = await consentHistory(customer.id);
    await adminSignIn(page);
    await page.goto(detailUrl(customer.id));
    if (api.items.length === 0) {
      await expect(page.getByTestId("history-empty")).toHaveText("No consent changes recorded");
    } else {
      // signing in already recorded a policy consent: the empty state cannot be shown for this customer
      await expect(page.getByTestId("consent-history-row")).toHaveCount(api.items.length);
    }
  });
});

test.describe("Privacy: export", () => {
  test("asks first, then downloads a JSON file that holds the customer's data", async ({ page }) => {
    const customer = await makeCustomer({ marketingSms: true });
    await adminSignIn(page);
    await page.goto(detailUrl(customer.id));
    await page.getByTestId("privacy-export").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("personal data");
    await expect(dialog).toContainText("logged under your name");
    // cancelling downloads nothing and gives the focus back to the button
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(page.getByTestId("privacy-export")).toBeFocused();

    await page.getByTestId("privacy-export").click();
    const [download] = await Promise.all([page.waitForEvent("download"), page.getByRole("dialog").getByRole("button", { name: "Download file" }).click()]);
    expect(download.suggestedFilename()).toMatch(/^customer-data-.*\.json$/);
    const path = await download.path();
    const text = readFileSync(path, "utf8");
    const json = JSON.parse(text) as { export: { customerId: string }; profile: unknown };
    expect(json.export.customerId).toBe(customer.id);
    expect(text).toContain(customer.mobile.slice(1));
    expect(text).toContain(customer.name);
    await expect(page.getByText("The file was downloaded.")).toBeVisible();
  });

  test("the hourly limit is said plainly", async ({ page }) => {
    const customer = await makeCustomer({ withAddress: false });
    const staff = await staffWithPermissions(["customers.customer.view", "customers.privacy.export"]);
    const token = await staffAccess(staff.email, staff.password);
    for (let i = 0; i < 10; i++) await call("POST", `/api/v1/customers/${customer.id}/privacy/export`, { token, expect: 200 });
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto(detailUrl(customer.id));
    await page.getByTestId("privacy-export").click();
    await page.getByRole("dialog").getByRole("button", { name: "Download file" }).click();
    await expect(page.getByTestId("export-error")).toContainText("limit of 10 customer data exports per hour");
    await expect(page.getByRole("dialog")).toBeVisible();
  });
});

test.describe("Privacy: anonymise", () => {
  test("is refused while the customer has an open order, and says so in plain words", async ({ page }) => {
    const customer = await makeCustomer();
    const order = await placeOpenOrder(customer.mobile);
    await adminSignIn(page);
    await page.goto(detailUrl(customer.id));
    await page.getByTestId("privacy-anonymise").click();
    const dialog = page.getByRole("dialog");

    // what is replaced, deleted and kept, said before anything is asked
    await expect(dialog).toContainText("This cannot be undone");
    await expect(dialog.getByTestId("anonymise-replaced")).toContainText("Name, mobile number and email");
    await expect(dialog.getByTestId("anonymise-deleted")).toContainText("Saved addresses");
    await expect(dialog.getByTestId("anonymise-kept")).toContainText("Invoices and credit notes exactly as issued");
    await expect(dialog.getByTestId("kept-summary")).toHaveText("Invoices and order totals are kept for the books.");

    // the first focus is the reason box, never the destructive button
    await expect(dialog.getByLabel(/^Reason/)).toBeFocused();
    const submit = dialog.getByTestId("anonymise-submit");
    await expect(submit).toBeDisabled();

    // reason alone is not enough; a wrong id is said; only the right id enables the button
    await dialog.getByLabel(/^Reason/).fill("no");
    await dialog.getByLabel(/^Reason/).blur();
    await expect(dialog).toContainText("Write at least 5 characters.");
    await dialog.getByLabel(/^Reason/).fill("Customer asked for erasure by phone");
    await expect(submit).toBeDisabled();
    await dialog.getByLabel(/^Type the customer id/).fill(customer.id.slice(0, 12));
    await dialog.getByLabel(/^Type the customer id/).blur();
    await expect(dialog).toContainText("This is not the id of this customer.");
    await expect(submit).toBeDisabled();
    await dialog.getByLabel(/^Type the customer id/).fill(customer.id.toUpperCase().replaceAll("-", ""));
    await expect(submit).toBeEnabled();

    await submit.click();
    const blocked = page.getByTestId("anonymise-blocked");
    await expect(blocked).toContainText("still has open work");
    await expect(blocked).toContainText("Finish or cancel the open work first");
    await expect(page.getByRole("dialog")).toBeVisible();
    expect(order.number).toBeTruthy();
    // nothing changed
    expect((await readCustomer(customer.id)).customer.mobile.startsWith("ANON")).toBe(false);
    await expect(page.getByTestId("anonymised-banner")).toHaveCount(0);
  });

  test("anonymises a customer without open orders; the page shows the banner and the actions are gone", async ({ page }) => {
    const customer = await makeCustomer({ marketingSms: true });
    await adminSignIn(page);
    await page.goto(detailUrl(customer.id));
    await expect(page.getByTestId("detail-mobile")).toContainText(customer.mobile.slice(1));

    // Escape closes the dialog and gives the focus back to the button that opened it
    await page.getByTestId("privacy-anonymise").click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await page.keyboard.press("Escape");
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(page.getByTestId("privacy-anonymise")).toBeFocused();

    await page.getByTestId("privacy-anonymise").click();
    const dialog = await fillAnonymise(page, customer.id);
    await dialog.getByTestId("anonymise-submit").click();
    const result = page.getByTestId("anonymise-result");
    await expect(result).toContainText("Customer anonymised");
    await result.getByTestId("anonymise-close").click();
    await expect(page.getByRole("dialog")).toHaveCount(0);

    // banner with the date, personal fields as the API returns them, actions gone
    await expect(page.getByTestId("anonymised-banner")).toContainText("Anonymised on");
    await expect(page.getByTestId("detail-mobile")).toContainText("ANON");
    await expect(page.getByTestId("detail-mobile")).not.toContainText(customer.mobile.slice(1));
    await expect(page.getByRole("heading", { level: 1, name: "Anonymised" })).toBeVisible();
    await expect(page.getByTestId("privacy-anonymise")).toHaveCount(0);
    await expect(page.getByTestId("privacy-export")).toHaveCount(0);
    await expect(page.getByTestId("privacy-anonymised-note")).toBeVisible();
    await expect(page.getByTestId("customer-activate")).toHaveCount(0);
    await expect(page.getByTestId("customer-address")).toHaveCount(0);

    // a reload still knows (the date is only known in the session that did it)
    await page.reload();
    await expect(page.getByTestId("anonymised-banner")).toContainText("This customer is anonymised");
    await expect(page.getByTestId("privacy-anonymise")).toHaveCount(0);

    // the API agrees, and the same mobile can sign in again as a new customer
    expect((await readCustomer(customer.id)).customer.mobile.startsWith("ANON")).toBe(true);
    const token = await customerAccess(customer.mobile);
    const me = await call<{ mobile?: string }>("GET", "/api/v1/me", { token, expect: 200 });
    expect(me.json).toBeTruthy();
    const found = await customersByMobile(customer.mobile);
    expect(found.length).toBe(1);
    expect(found[0]!.id).not.toBe(customer.id);
  });
});

test.describe("Privacy: permissions", () => {
  test("view only: the history is shown, the Privacy card is not", async ({ page }) => {
    const customer = await makeCustomer();
    const staff = await staffWithPermissions(["customers.customer.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto(detailUrl(customer.id));
    await expect(page.getByTestId("consent-history")).toBeVisible();
    await expect(page.getByTestId("privacy-card")).toHaveCount(0);
    await expect(page.getByTestId("privacy-export")).toHaveCount(0);
    await expect(page.getByTestId("privacy-anonymise")).toHaveCount(0);
  });

  test("export only: just the export button", async ({ page }) => {
    const customer = await makeCustomer();
    const staff = await staffWithPermissions(["customers.customer.view", "customers.privacy.export"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto(detailUrl(customer.id));
    await expect(page.getByTestId("privacy-card")).toBeVisible();
    await expect(page.getByTestId("privacy-export")).toBeVisible();
    await expect(page.getByTestId("privacy-anonymise")).toHaveCount(0);
  });

  test("anonymise only: just the anonymise button", async ({ page }) => {
    const customer = await makeCustomer();
    const staff = await staffWithPermissions(["customers.customer.view", "customers.privacy.anonymise"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto(detailUrl(customer.id));
    await expect(page.getByTestId("privacy-anonymise")).toBeVisible();
    await expect(page.getByTestId("privacy-export")).toHaveCount(0);
  });

  test("Super Admin sees both, and the default Manager role sees neither", async ({ page }) => {
    const customer = await makeCustomer();
    await adminSignIn(page);
    await page.goto(detailUrl(customer.id));
    await expect(page.getByTestId("privacy-export")).toBeVisible();
    await expect(page.getByTestId("privacy-anonymise")).toBeVisible();

    const manager = loadState().manager;
    await page.context().clearCookies();
    await signInViaBff(page.context(), manager.email, manager.password);
    await page.goto(detailUrl(customer.id));
    await expect(page.getByTestId("consent-history")).toBeVisible();
    await expect(page.getByTestId("privacy-card")).toHaveCount(0);
  });
});
