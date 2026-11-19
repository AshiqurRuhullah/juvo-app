import { test, expect, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { call } from "./support/cod";
import { staffWithPermissions } from "./support/returns";
import { makeCustomer, readCustomer } from "./support/customers";
import { pick } from "./support/store";
import { signInViaBff } from "./support/ui";

/**
 * Staff customer screens (module N, customers) in the browser against the real API: list, search, filters, detail, deactivate and
 * activate, and who may see or do what (masked personal data without view-pii, read-only without block).
 */
async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

const rows = (page: Page) => page.locator("[data-testid=customer-link]:visible");

test.describe("Customers: list and detail", () => {
  test("search, filters and the detail page show what the API holds", async ({ page }) => {
    const customer = await makeCustomer({ marketingSms: true });
    await adminSignIn(page);
    await page.goto("/admin/customers");
    await expect(page.getByRole("heading", { level: 1, name: "Customers" })).toBeVisible();
    await expect(page.getByTestId("pii-notice")).toHaveCount(0);

    // search by name, and by the full mobile number (allowed with view-pii)
    await page.getByLabel("Search customers").fill(customer.name);
    await expect(rows(page)).toHaveCount(1);
    await expect(rows(page)).toContainText(customer.name);
    await expect(page.locator("[data-testid=customer-mobile]:visible")).toContainText(customer.mobile.slice(1));
    await page.getByLabel("Search customers").fill(customer.mobile);
    await expect(rows(page)).toHaveCount(1);

    // profile-level filter follows what the API says the level is
    const level = (await readCustomer(customer.id)).customer.profileLevel;
    const levelLabel = { Guest: "Guest", Quick: "Quick profile", Full: "Full profile" }[level]!;
    const otherLabel = level === "Guest" ? "Full profile" : "Guest";
    await pick(page, page.getByRole("combobox", { name: /^Profile/ }), levelLabel);
    await expect(rows(page)).toHaveCount(1);
    await pick(page, page.getByRole("combobox", { name: /^Profile/ }), otherLabel);
    await expect(rows(page)).toHaveCount(0);
    await page.getByRole("button", { name: "Clear filters" }).click();

    // status filter
    await page.getByLabel("Search customers").fill(customer.name);
    await pick(page, page.getByRole("combobox", { name: /^Status/ }), "Deactivated");
    await expect(rows(page)).toHaveCount(0);
    await pick(page, page.getByRole("combobox", { name: /^Status/ }), "Active");
    await expect(rows(page)).toHaveCount(1);

    // detail
    await rows(page).first().click();
    await expect(page.getByTestId("customer-detail")).toBeVisible();
    await expect(page.getByRole("heading", { level: 1, name: customer.name })).toBeVisible();
    await expect(page.getByTestId("detail-mobile")).toContainText(customer.mobile.slice(1));
    await expect(page.getByTestId("detail-email")).toHaveText(customer.email);
    await expect(page.getByTestId("customer-address")).toHaveCount(1);
    await expect(page.getByTestId("customer-address")).toContainText("House 12, Road 5");
    await expect(page.getByTestId("consent-MarketingSms")).toContainText("Given");
    await expect(page.getByTestId("consent-MarketingEmail")).toContainText("Not asked yet");
    await expect(page.getByTestId("order-stats")).toBeVisible();
    await expect(page.getByTestId("stat-total")).toHaveText("0");
    await expect(page.getByTestId("pii-notice")).toHaveCount(0);
  });

  test("deactivate asks first, ends the access, and activate brings it back", async ({ page }) => {
    const customer = await makeCustomer();
    await adminSignIn(page);
    await page.goto(`/admin/customers/${customer.id}`);
    await expect(page.getByTestId("access-state")).toContainText("can sign in");

    await page.getByTestId("customer-deactivate").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("cannot sign in");
    await expect(dialog).toContainText("sessions end");
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    expect((await readCustomer(customer.id)).customer.isActive).toBe(true);

    await page.getByTestId("customer-deactivate").click();
    await page.getByRole("dialog").getByRole("button", { name: "Deactivate" }).click();
    await expect(page.getByTestId("access-state")).toContainText("deactivated");
    await expect(page.getByTestId("customer-inactive-note")).toBeVisible();
    expect((await readCustomer(customer.id)).customer.isActive).toBe(false);

    // a deactivated customer cannot sign in any more
    const requested = await call<{ devCode: string | null }>("POST", "/api/v1/auth/otp/request", { body: { mobile: customer.mobile, purpose: null, channel: "sms", email: null } });
    if (requested.status === 202) {
      const verified = await call("POST", "/api/v1/auth/otp/verify", { body: { mobile: customer.mobile, code: requested.json.devCode, deviceName: "e2e" } });
      expect(verified.status).toBeGreaterThanOrEqual(400);
    } else {
      expect(requested.status).toBeGreaterThanOrEqual(400);
    }

    // the list shows it as deactivated
    await page.goto("/admin/customers");
    await page.getByLabel("Search customers").fill(customer.name);
    await pick(page, page.getByRole("combobox", { name: /^Status/ }), "Deactivated");
    await expect(rows(page)).toHaveCount(1);

    await page.goto(`/admin/customers/${customer.id}`);
    await page.getByTestId("customer-activate").click();
    await page.getByRole("dialog").getByRole("button", { name: "Activate" }).click();
    await expect(page.getByTestId("access-state")).toContainText("can sign in");
    await expect(page.getByTestId("customer-inactive-note")).toHaveCount(0);
    expect((await readCustomer(customer.id)).customer.isActive).toBe(true);
  });

  test("an unknown customer shows a clear not-found page", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/customers/00000000-0000-4000-8000-000000000000");
    await expect(page.getByText("Customer not found").first()).toBeVisible();
    await expect(page.getByRole("link", { name: /Back to customers/ })).toBeVisible();
  });
});

test.describe("Customers: permissions", () => {
  test("view only: personal data stays masked, access cannot be changed, no order history", async ({ page }) => {
    const customer = await makeCustomer();
    const staff = await staffWithPermissions(["customers.customer.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);

    await page.goto("/admin/customers");
    await expect(page.getByTestId("pii-notice")).toBeVisible();
    await expect(page.getByTestId("pii-notice")).toContainText("hidden");
    await page.getByLabel("Search customers").fill(customer.name);
    await expect(rows(page)).toHaveCount(1);
    const mobile = (await page.locator("[data-testid=customer-mobile]:visible").textContent())!;
    expect(mobile).toContain("*");
    expect(mobile).not.toContain(customer.mobile.slice(1));
    // searching by the number finds nothing: the API only searches names without view-pii
    await page.getByLabel("Search customers").fill(customer.mobile);
    await expect(rows(page)).toHaveCount(0);

    await page.goto(`/admin/customers/${customer.id}`);
    await expect(page.getByTestId("pii-notice")).toBeVisible();
    await expect(page.getByTestId("detail-mobile")).toContainText("*");
    await expect(page.getByTestId("detail-email")).toContainText("***");
    await expect(page.getByTestId("customer-deactivate")).toHaveCount(0);
    await expect(page.getByTestId("customer-activate")).toHaveCount(0);
    await expect(page.getByTestId("read-only-note")).toContainText("not change");
    await expect(page.getByTestId("orders-hidden")).toBeVisible();
  });

  test("view and block without personal data: can deactivate, still masked", async ({ page }) => {
    const customer = await makeCustomer();
    const staff = await staffWithPermissions(["customers.customer.view", "customers.customer.block"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto(`/admin/customers/${customer.id}`);
    await expect(page.getByTestId("detail-mobile")).toContainText("*");
    await page.getByTestId("customer-deactivate").click();
    await page.getByRole("dialog").getByRole("button", { name: "Deactivate" }).click();
    await expect(page.getByTestId("access-state")).toContainText("deactivated");
    expect((await readCustomer(customer.id)).customer.isActive).toBe(false);
    await expect(page.getByTestId("customer-activate")).toBeVisible();
  });

  test("without the view permission the menu item and the page are closed", async ({ page }) => {
    const staff = await staffWithPermissions(["catalog.product.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin");
    await expect(page.getByRole("link", { name: "Customers" })).toHaveCount(0);
    const response = await page.goto("/admin/customers");
    expect(response?.status()).toBeGreaterThanOrEqual(400);
  });
});
