import { test, expect, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { signInViaBff } from "./support/ui";

/**
 * Couriers (module H) in the browser against the real API: the courier cards, adding, changing, switching on and off and archiving a
 * courier, the slots that cannot be switched on, the connections tab and what a view-only staff member can and cannot do.
 */
async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

const card = (page: Page, code: string) => page.locator(`[data-testid="courier-card"][data-code="${code}"]`);

test.describe("Couriers", () => {
  test("the seeded couriers show their rules, the sandbox is marked as a simulator and the slots are not set up", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/couriers");
    await expect(page.getByRole("heading", { level: 1, name: "Couriers" })).toBeVisible();

    const sandbox = card(page, "SANDBOX");
    await expect(sandbox).toBeVisible();
    await expect(sandbox.getByTestId("simulator-badge")).toBeVisible();
    await expect(sandbox.getByRole("switch")).toBeChecked();
    await expect(sandbox).toContainText("Cash on delivery");
    await expect(sandbox).toContainText("Every zone");

    for (const slot of ["PATHAO", "STEADFAST", "REDX", "PAPERFLY", "ECOURIER", "DHL"]) {
      await expect(card(page, slot).getByTestId("not-connected")).toBeVisible();
      await expect(card(page, slot).getByRole("switch")).not.toBeChecked();
    }
  });

  test("a courier slot without account keys cannot be switched on and the page says why", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/couriers");
    const pathao = card(page, "PATHAO");
    await pathao.getByRole("switch").click();
    await expect(pathao.getByTestId("courier-notice")).toContainText("no account keys yet");
    await expect(pathao.getByRole("switch")).not.toBeChecked();
    await expect(pathao.getByTestId("courier-notice")).not.toContainText("COURIER.");
  });

  test("the connections tab lists the sandbox and the six slots honestly", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/couriers");
    await page.getByRole("tab", { name: "Connections" }).click();
    await expect(page.getByText("Real couriers are connected later")).toBeVisible();
    await expect(page.getByTestId("provider-card")).toHaveCount(7);
    await expect(page.locator('[data-testid="provider-card"][data-code="SANDBOX"]')).toContainText("Set up");
    await expect(page.locator('[data-testid="provider-card"][data-code="DHL"]')).toContainText("Not set up");
  });

  test("a courier is added, changed, switched off and on, and archived", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/couriers");

    await page.getByTestId("courier-add").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog.getByRole("heading", { name: "Add courier" })).toBeVisible();

    // Empty and wrong input is explained next to the field.
    await dialog.getByTestId("courier-save").click();
    await expect(dialog.getByText("Use 2 to 30 capital letters, digits, - or _.")).toBeVisible();
    await expect(dialog.getByText("Write a name (up to 100 characters).")).toBeVisible();

    await dialog.locator("#cf-code").fill("e2e_fast");
    await dialog.locator("#cf-name").fill("E2E Fast Courier");
    await dialog.locator("#cf-priority").fill("250");
    await dialog.getByLabel("Base rate (BDT)").fill("75");
    await dialog.getByLabel("Per extra kg (BDT)").fill("25");
    await dialog.getByLabel("Heaviest parcel (g)").fill("5000");
    await dialog.getByLabel("Tracking page address").fill("http://not-secure.example/{consignment}");
    await dialog.getByTestId("courier-save").click();
    await expect(dialog.getByText("Use an https:// address that contains {consignment}.")).toBeVisible();
    await dialog.getByLabel("Tracking page address").fill("https://track.example/t/{consignment}");

    // A setting whose name looks like a key is refused before anything is sent.
    await dialog.getByRole("button", { name: "Add setting" }).click();
    await dialog.locator("#cf-sk-0").fill("api_key");
    await dialog.locator("#cf-sv-0").fill("abc");
    await dialog.getByTestId("courier-save").click();
    await expect(dialog.getByText("A setting name looks like a key or password. Those are never stored here.")).toBeVisible();
    await dialog.locator("#cf-sk-0").fill("store_id");

    // Restrict to the first zone (the zones come from the delivery zone list).
    await dialog.getByRole("group", { name: "Zones it serves" }).getByRole("checkbox").first().click();

    await dialog.getByTestId("courier-save").click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    const created = card(page, "E2E_FAST");
    await expect(created).toBeVisible();
    await expect(created).toContainText("250");
    await expect(created).toContainText("E2E Fast Courier");
    await expect(created.getByRole("switch")).not.toBeChecked();

    // The same code twice is refused with the API's explanation.
    await page.getByTestId("courier-add").click();
    const again = page.getByRole("dialog");
    await again.locator("#cf-code").fill("E2E_FAST");
    await again.locator("#cf-name").fill("Duplicate");
    await again.getByTestId("courier-save").click();
    await expect(again.getByTestId("courier-form-error")).toContainText("A courier with this code already exists");
    await again.getByRole("button", { name: "Cancel" }).click();

    // Change it: the code is fixed, the name and priority change.
    await created.getByRole("button", { name: /Edit/ }).click();
    const edit = page.getByRole("dialog");
    await expect(edit.locator("#cf-code")).toHaveCount(0);
    await expect(edit.locator("#cf-name")).toHaveValue("E2E Fast Courier");
    await edit.locator("#cf-name").fill("E2E Faster Courier");
    await edit.locator("#cf-priority").fill("240");
    await edit.getByTestId("courier-save").click();
    await expect(page.getByRole("dialog")).toHaveCount(0);
    await expect(card(page, "E2E_FAST")).toContainText("E2E Faster Courier");
    await expect(card(page, "E2E_FAST")).toContainText("240");

    // Switch on and off (a sandbox-backed courier can be switched on).
    const sw = card(page, "E2E_FAST").getByRole("switch");
    await sw.click();
    await expect(sw).toBeChecked();
    await sw.click();
    await expect(sw).not.toBeChecked();

    // Archive with the confirmation.
    await card(page, "E2E_FAST").getByRole("button", { name: /Archive/ }).click();
    const confirm = page.getByRole("dialog");
    await expect(confirm.getByText("Archive E2E Faster Courier?")).toBeVisible();
    await confirm.getByRole("button", { name: "Archive courier" }).click();
    await expect(card(page, "E2E_FAST")).toHaveCount(0);
  });

  test("a view-only staff member sees the couriers but has no way to change them", async ({ page, context }) => {
    const viewer = await staffWithPermissions(["couriers.courier.view"]);
    await signInViaBff(context, viewer.email, viewer.password);
    await page.goto("/admin/couriers");
    await expect(card(page, "SANDBOX")).toBeVisible();
    await expect(page.getByText("You can look at the couriers but not change them.")).toBeVisible();
    await expect(page.getByTestId("courier-add")).toHaveCount(0);
    await expect(card(page, "SANDBOX").getByRole("switch")).toBeDisabled();
    await expect(card(page, "SANDBOX").getByRole("button", { name: /Edit/ })).toHaveCount(0);
    await expect(card(page, "SANDBOX").getByRole("button", { name: /Archive/ })).toHaveCount(0);
    // The API refuses the same change if it is called anyway.
    const list = await context.request.get("/api/bff/api/v1/couriers");
    const sandbox = ((await list.json()) as { id: string; code: string }[]).find((c) => c.code === "SANDBOX")!;
    const attempt = await context.request.put(`/api/bff/api/v1/couriers/${sandbox.id}/enabled`, { data: { isEnabled: false } });
    expect(attempt.status()).toBe(403);
  });

  test("a staff member without the courier permission is sent to the forbidden page", async ({ page, context }) => {
    const other = await staffWithPermissions(["couriers.shipment.view"]);
    await signInViaBff(context, other.email, other.password);
    await page.goto("/admin/couriers");
    await expect(page).toHaveURL(/forbidden/);
  });
});
