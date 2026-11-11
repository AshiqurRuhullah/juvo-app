import { test, expect, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { api, loadState } from "./support/env";
import { apiCheckout, createStaff, disableAllRules, staffToken } from "./support/orders";
import { sampleVariants } from "./support/store";
import { signInViaBff } from "./support/ui";

/**
 * Step 9-b billing screens against the real stack: a tax rule is created, activated (an unverified rule needs the tick) and switched
 * off; an invoice made from a confirmed order shows in the list and its detail; the PDF comes in English and Bengali; people without
 * the permissions see less (or are sent away). Every rule this spec switches on is switched off again, so other specs' totals stay as they were.
 */
let admin = "";
let invoice: { id: string; number: string; orderNumber: string };
let orderId = "";
let support: { email: string; password: string };
let editor: { email: string; password: string };
const code = `E2E${Date.now().toString().slice(-6)}`;
const ruleName = `E2E Test Tax ${code}`;

type Rule = { id: string; code: string; isActive: boolean; isVerified: boolean };
const rules = async () => (await api<Rule[]>("GET", "/api/v1/billing/tax-rules", undefined, admin)).json;

async function signInAdmin(page: Page, next: string) {
  const user = loadState().admin;
  await signInViaBff(page.context(), user.email, user.password);
  await page.goto(next);
}

test.beforeAll(async ({ playwright }) => {
  admin = await staffToken("admin");
  await disableAllRules(admin);
  support = await createStaff(admin, "support@e2e.test", "Sam Support", "Support", loadState().branches.ho.id);
  editor = await createStaff(admin, "editor@e2e.test", "Eve Editor", "Content Editor", loadState().branches.ho.id);

  const own = await playwright.request.newContext({ baseURL: E2E.webUrl });
  const [item] = await sampleVariants(own, 1);
  const placed = await apiCheckout(own, { lines: [{ variantId: item!.id, quantity: 1 }] });
  await own.dispose();
  const confirmed = await api("POST", `/api/v1/orders/${placed.orderId}/confirm`, { advanceReceived: false }, admin);
  expect(confirmed.status, JSON.stringify(confirmed.json)).toBe(200);
  const issued = await api<{ id: string; number: string; orderNumber: string }>("POST", `/api/v1/billing/orders/${placed.orderId}/invoice`, undefined, admin);
  expect(issued.status, JSON.stringify(issued.json)).toBe(200);
  invoice = issued.json;
  orderId = placed.orderId;
});

test.afterAll(async () => {
  // Never leave a tax rule switched on.
  for (const rule of await rules()) if (rule.isActive) await api("POST", `/api/v1/billing/tax-rules/${rule.id}/deactivate`, undefined, admin);
});

test.describe("Tax rules", () => {
  test.describe.configure({ mode: "serial" });

  test("create a rule, activate it only after acknowledging it is unverified, then switch it off", async ({ page }) => {
    await signInAdmin(page, "/admin/billing?tab=rules");
    await expect(page.getByRole("heading", { name: "Tax and invoices", level: 1 })).toBeVisible();

    await page.getByTestId("new-rule").click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel(/^Code/).fill(code);
    await dialog.getByLabel(/^Name/).fill(ruleName);
    await dialog.getByLabel(/^Rate/).fill("9");
    await dialog.getByRole("button", { name: "Save" }).click();
    await expect(page.getByText(ruleName).first()).toBeVisible();
    await expect(page.getByText("Not verified").first()).toBeVisible();
    const created = (await rules()).find((r) => r.code === code)!;
    expect(created.isActive).toBe(false);
    expect(created.isVerified).toBe(false);

    // Activation: the warning shows and the tick is required.
    await page.getByRole("button", { name: `Activate ${ruleName}` }).click();
    const confirm = page.getByRole("dialog");
    await expect(confirm.getByTestId("unverified-warning")).toBeVisible();
    await confirm.getByRole("button", { name: "Activate rule" }).click();
    await expect(confirm.getByText("Tick the box to continue.")).toBeVisible();
    expect((await rules()).find((r) => r.code === code)!.isActive).toBe(false);
    await confirm.getByLabel(/I know this rule is not verified/).click();
    await confirm.getByRole("button", { name: "Activate rule" }).click();
    await expect(page.getByRole("button", { name: `Deactivate ${ruleName}` })).toBeVisible();
    expect((await rules()).find((r) => r.code === code)!.isActive).toBe(true);

    // Switch off.
    await page.getByRole("button", { name: `Deactivate ${ruleName}` }).click();
    await page.getByRole("dialog").getByRole("button", { name: "Switch off" }).click();
    await expect(page.getByRole("button", { name: `Activate ${ruleName}` })).toBeVisible();
    expect((await rules()).find((r) => r.code === code)!.isActive).toBe(false);
  });

  test("a rule can be edited, but its code stays", async ({ page }) => {
    await signInAdmin(page, "/admin/billing?tab=rules");
    await page.getByRole("button", { name: `Edit ${ruleName}` }).click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByLabel(/^Code/)).toBeDisabled();
    await dialog.getByLabel(/^Rate/).fill("7.5");
    await dialog.getByRole("button", { name: "Save" }).click();
    await expect(page.getByText(/7\.5% \(inclusive\)/).first()).toBeVisible();
  });

  test("a bad rate is refused in the form", async ({ page }) => {
    await signInAdmin(page, "/admin/billing?tab=rules");
    await page.getByTestId("new-rule").click();
    const dialog = page.getByRole("dialog");
    await dialog.getByLabel(/^Code/).fill("BAD");
    await dialog.getByLabel(/^Name/).fill("Bad");
    await dialog.getByLabel(/^Rate/).fill("120");
    await dialog.getByRole("button", { name: "Save" }).click();
    await expect(dialog.getByText("Enter a rate between 0 and 100.")).toBeVisible();
  });
});

test.describe("Invoices", () => {
  test("the invoice of a confirmed order is listed, opens, and downloads as a PDF in English and Bengali", async ({ page }) => {
    await signInAdmin(page, "/admin/billing");
    await page.getByRole("searchbox").fill(invoice.orderNumber);
    const link = page.locator('[data-testid="invoice-link"]:visible', { hasText: invoice.number });
    await expect(link).toBeVisible();
    await link.click();

    await expect(page).toHaveURL(new RegExp(`/admin/billing/invoices/${invoice.id}$`));
    await expect(page.getByRole("heading", { name: invoice.number, level: 1 })).toBeVisible();
    await expect(page.getByText(invoice.orderNumber).first()).toBeVisible();
    await expect(page.getByTestId("invoice-lines").locator("li").first()).toBeVisible();
    await expect(page.getByTestId("invoice-tax-note")).toBeVisible();

    for (const lang of ["en", "bn"] as const) {
      const href = await page.getByTestId(`invoice-pdf-${lang}`).getAttribute("href");
      expect(href).toContain(`/pdf?lang=${lang}`);
      const response = await page.request.get(href!);
      expect(response.status()).toBe(200);
      expect(response.headers()["content-type"]).toContain("application/pdf");
      const bytes = await response.body();
      expect(bytes.subarray(0, 4).toString("latin1")).toBe("%PDF");
      expect(bytes.length).toBeGreaterThan(1000);
    }

    const download = page.waitForEvent("download");
    await page.getByRole("link", { name: "Download PDF (English)" }).click();
    expect((await download).suggestedFilename().toLowerCase()).toContain("pdf");
  });

  test("an unknown invoice shows a not-found page", async ({ page }) => {
    await signInAdmin(page, "/admin/billing/invoices/00000000-0000-4000-8000-000000000000");
    await expect(page.getByRole("heading", { name: "Invoice not found" }).first()).toBeVisible();
  });

  test("the credit notes tab opens", async ({ page }) => {
    await signInAdmin(page, "/admin/billing");
    await page.getByRole("tab", { name: "Credit notes" }).click();
    await expect(page.getByRole("tab", { name: "Credit notes" })).toHaveAttribute("aria-selected", "true");
    await expect(page.getByRole("searchbox")).toBeVisible();
  });

  test("issuing the invoice of the same order again returns the same invoice (one per order, no gap in numbers)", async () => {
    const again = await api<{ id: string; number: string }>("POST", `/api/v1/billing/orders/${orderId}/invoice`, undefined, admin);
    expect(again.status).toBe(200);
    expect(again.json.number).toBe(invoice.number);
    const list = await api<{ items: { id: string; number: string }[] }>("GET", `/api/v1/billing/invoices?Search=${invoice.orderNumber}`, undefined, admin);
    expect(list.json.items.filter((i) => i.number === invoice.number)).toHaveLength(1);
  });
});

test.describe("Permissions", () => {
  test("Support sees the invoices (and can download) but no tax rules and no way to create one", async ({ page }) => {
    await signInViaBff(page.context(), support.email, support.password);
    await page.goto("/admin/billing?tab=rules");
    await expect(page.getByRole("tab", { name: "Invoices" })).toBeVisible();
    await expect(page.getByRole("tab", { name: "Tax rules" })).toHaveCount(0);
    await expect(page.getByTestId("new-rule")).toHaveCount(0);
    await page.goto(`/admin/billing/invoices/${invoice.id}`);
    await expect(page.getByTestId("invoice-pdf-en")).toBeVisible();
  });

  test("someone without tax permissions has no menu entry and is sent to the forbidden page", async ({ page }) => {
    await signInViaBff(page.context(), editor.email, editor.password);
    await page.goto("/admin");
    await expect(page.getByRole("link", { name: "Tax and invoices" })).toHaveCount(0);
    await page.goto("/admin/billing");
    await expect(page).toHaveURL(/\/forbidden/);
    await page.goto(`/admin/billing/invoices/${invoice.id}`);
    await expect(page).toHaveURL(/\/forbidden/);
  });
});
