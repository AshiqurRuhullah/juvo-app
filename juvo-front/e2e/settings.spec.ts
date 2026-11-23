import { test, expect, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { signInViaBff } from "./support/ui";
import { readSetting, resetSetting, settingRow, writeSetting } from "./support/settings";

/**
 * The settings centre in the browser against the real API: the list and its groups, search, a number and a switch that persist after a
 * reload, validation (here and from the server), reset to default with a confirm that names the default, read-only entries, a staff member
 * without the edit permission, a stale version, a dangerous switch asking first, the history and the menu item. The settings these
 * tests change are put back to their defaults afterwards (the database is shared with other specs).
 */
const NUMBER = "Marketing.AttributionWindowDays"; // whole 1..90, default 7
const SWITCH = "Notifications.WhatsAppEnabled"; // bool, feature switch, default off
const TIME = "Marketing.QuietHours.Start"; // text HH:mm, checked by the server
const OTHER = "Orders.MaxManualQuantity"; // whole, system setting

test.afterAll(async () => {
  for (const key of [NUMBER, SWITCH, TIME, OTHER]) await resetSetting(key).catch(() => undefined);
});

async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

/** Opens the centre and searches by key, so the row shows whatever group it belongs to. */
async function openSetting(page: Page, key: string) {
  await page.goto("/admin/settings");
  await expect(page.getByRole("heading", { level: 1, name: "Settings" })).toBeVisible();
  await page.getByLabel("Search settings").fill(key);
  const row = settingRow(page, key);
  await expect(row).toHaveCount(1);
  return row;
}

test.describe("Settings centre: reading", () => {
  test("lists the API's groups and settings with source and default", async ({ page }) => {
    await resetSetting(NUMBER);
    await adminSignIn(page);
    await page.goto("/admin/settings");
    await expect(page.getByRole("heading", { level: 1, name: "Settings" })).toBeVisible();
    const groups = page.getByTestId("settings-group");
    await expect(groups.first()).toBeVisible();
    expect(await groups.count()).toBeGreaterThanOrEqual(10);
    await expect(groups.first()).toHaveAttribute("aria-current", "true");
    await expect(page.getByTestId("settings-section")).toHaveCount(1); // one group at a time without a search
    await expect(page.getByTestId("setting-row").first()).toBeVisible();

    await page.getByTestId("settings-group").filter({ hasText: "Marketing" }).first().click();
    const row = settingRow(page, NUMBER);
    await expect(row).toBeVisible();
    await expect(row.getByTestId("setting-key")).toHaveText(NUMBER);
    await expect(row.getByTestId("setting-source")).toHaveText("Default");
    await expect(row.getByTestId("setting-meta")).toContainText("Default: 7");
    await expect(row.getByTestId("setting-meta")).toContainText("Allowed: 1 to 90");
    await expect(row.getByTestId("setting-input")).toHaveValue("7");
    await expect(row.getByTestId("setting-save")).toBeDisabled(); // nothing changed yet
    await expect(row.getByTestId("setting-reset")).toHaveCount(0); // nothing stored to reset
  });

  test("search covers every group; no match says so and can be cleared", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/settings");
    await page.getByLabel("Search settings").fill("return");
    await expect(page.getByTestId("settings-search-count")).toContainText("settings match");
    expect(await page.getByTestId("settings-section").count()).toBeGreaterThanOrEqual(1);
    await expect(page.getByTestId("settings-group")).toHaveCount(0); // the group chips give way to the results
    await page.getByLabel("Search settings").fill("zzzz-nothing-here");
    await expect(page.getByText("No setting matches")).toBeVisible();
    await page.getByRole("button", { name: "Clear the search" }).click();
    await expect(page.getByTestId("settings-group").first()).toBeVisible();
  });

  test("read-only entries from the server configuration and the fixed currency cannot be edited", async ({ page }) => {
    await adminSignIn(page);
    const lockout = await openSetting(page, "Auth.LockoutMinutes");
    await expect(lockout.getByTestId("setting-source")).toHaveText("Server configuration");
    await expect(lockout.getByTestId("setting-input")).toBeDisabled();
    await expect(lockout.getByTestId("setting-blocked")).toHaveAttribute("data-reason", "readOnlyConfig");
    await expect(lockout.getByTestId("setting-blocked")).toContainText("Set in the server configuration");
    await expect(lockout.getByTestId("setting-save")).toHaveCount(0);

    const currency = await openSetting(page, "Currency.Store");
    await expect(currency.getByTestId("setting-input")).toBeDisabled();
    await expect(currency.getByTestId("setting-input")).toHaveValue("BDT");
    await expect(currency.getByTestId("setting-blocked")).toHaveAttribute("data-reason", "readOnlyFixed");
    await expect(currency.getByTestId("setting-save")).toHaveCount(0);
    await expect(currency.getByTestId("setting-reset")).toHaveCount(0);
  });
});

test.describe("Settings centre: changing", () => {
  test("a number is saved, survives a reload, appears in the history and can be reset to the default", async ({ page }) => {
    await resetSetting(NUMBER);
    await adminSignIn(page);
    let row = await openSetting(page, NUMBER);
    await row.getByTestId("setting-input").fill("12");
    await expect(row.getByTestId("setting-save")).toBeEnabled();
    await row.getByTestId("setting-save").click();
    await expect(page.getByText("Saved: ").first()).toBeVisible();
    await expect(settingRow(page, NUMBER).getByTestId("setting-source")).toHaveText("Changed");

    expect((await readSetting(NUMBER)).value).toBe(12);
    row = await openSetting(page, NUMBER); // a reload, the value comes from the API
    await expect(row.getByTestId("setting-input")).toHaveValue("12");
    await expect(row.getByTestId("setting-source")).toHaveText("Changed");

    // history: who, what changed, from and to
    const history = page.getByTestId("history-row").filter({ hasText: NUMBER }).first();
    await expect(history).toBeVisible();
    await expect(history.getByTestId("history-change")).toContainText("12");
    await expect(history.getByTestId("history-who")).not.toHaveText("");

    // reset: the confirm names the default (7) and the stored value (12)
    await row.getByTestId("setting-reset").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("go back to its default, 7");
    await expect(dialog).toContainText("saved value, 12");
    await dialog.getByRole("button", { name: "Reset to default" }).click();
    await expect(page.getByText("Back to the default: ").first()).toBeVisible();
    await expect(settingRow(page, NUMBER).getByTestId("setting-input")).toHaveValue("7");
    await expect(settingRow(page, NUMBER).getByTestId("setting-source")).toHaveText("Default");
    const after = await readSetting(NUMBER);
    expect(after.value).toBe(7);
    expect(after.isSet).toBe(false);
    await expect(page.getByTestId("history-row").filter({ hasText: NUMBER }).first().getByTestId("history-change")).toContainText("Reset to the default");
  });

  test("a switch is saved and survives a reload", async ({ page }) => {
    await resetSetting(SWITCH);
    await adminSignIn(page);
    let row = await openSetting(page, SWITCH);
    const toggle = row.getByRole("switch");
    await expect(toggle).not.toBeChecked();
    await toggle.click();
    await expect(row.getByTestId("setting-save")).toBeEnabled();
    await row.getByTestId("setting-save").click();
    await expect(page.getByText("Saved: ").first()).toBeVisible();
    expect((await readSetting(SWITCH)).value).toBe(true);
    row = await openSetting(page, SWITCH);
    await expect(row.getByRole("switch")).toBeChecked();
    await row.getByRole("switch").click();
    await expect(row.getByTestId("setting-undo")).toBeVisible();
    await row.getByTestId("setting-undo").click(); // undo goes back to the stored value without saving
    await expect(row.getByRole("switch")).toBeChecked();
    expect((await readSetting(SWITCH)).value).toBe(true);
  });

  test("a value out of range is refused here, and the server's own message is shown for a bad format", async ({ page }) => {
    await adminSignIn(page);
    const row = await openSetting(page, NUMBER);
    await row.getByTestId("setting-input").fill("100");
    await row.getByTestId("setting-save").click();
    await expect(row.getByTestId("setting-error")).toContainText("at most 90");
    await row.getByTestId("setting-input").fill("2.5");
    await row.getByTestId("setting-save").click();
    await expect(row.getByTestId("setting-error")).toContainText("whole number");
    await row.getByTestId("setting-input").fill("");
    await row.getByTestId("setting-save").click();
    await expect(row.getByTestId("setting-error")).toContainText("Enter a value");

    const before = (await readSetting(TIME)).value;
    const time = await openSetting(page, TIME);
    await time.getByTestId("setting-input").fill("25:99");
    await time.getByTestId("setting-save").click();
    await expect(time.getByTestId("setting-error")).toBeVisible(); // the API refuses; its sentence is shown
    await expect(time.getByTestId("setting-error")).not.toHaveText("");
    expect((await readSetting(TIME)).value).toBe(before);
  });

  test("a stale version is reloaded and said so", async ({ page }) => {
    const first = await writeSetting(OTHER, 40);
    await adminSignIn(page);
    const row = await openSetting(page, OTHER);
    await expect(row.getByTestId("setting-input")).toHaveValue("40");
    await writeSetting(OTHER, 41, first.version); // somebody else saves meanwhile
    await row.getByTestId("setting-input").fill("55");
    await row.getByTestId("setting-save").click();
    const reloaded = settingRow(page, OTHER);
    await expect(reloaded.getByTestId("setting-stale")).toContainText("Someone else changed this setting");
    await expect(reloaded.getByTestId("setting-input")).toHaveValue("41"); // the newer value, not the edit
    expect((await readSetting(OTHER)).value).toBe(41);
  });

  test("a dangerous switch asks first and says what happens; cancel changes nothing", async ({ page }) => {
    const now = (await readSetting("Loyalty.Enabled")).value === true;
    await adminSignIn(page);
    const row = await openSetting(page, "Loyalty.Enabled");
    await row.getByRole("switch").click();
    await row.getByTestId("setting-save").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText(now ? "Turn rewards off?" : "Turn rewards on?");
    await expect(dialog).toContainText("reaches customers at once");
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(dialog).toHaveCount(0);
    expect((await readSetting("Loyalty.Enabled")).value).toBe(now);
  });
});

test.describe("Settings centre: permissions and menu", () => {
  test("view-only staff see the values with disabled controls and a plain reason", async ({ page }) => {
    const staff = await staffWithPermissions(["settings.system.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    const row = await openSetting(page, NUMBER);
    await expect(row.getByTestId("setting-input")).toBeDisabled();
    await expect(row.getByTestId("setting-blocked")).toHaveAttribute("data-reason", "noPermission");
    await expect(row.getByTestId("setting-blocked")).toContainText("settings.system.edit");
    await expect(row.getByTestId("setting-save")).toHaveCount(0);
    const flag = await openSetting(page, SWITCH);
    await expect(flag.getByRole("switch")).toBeDisabled();
  });

  test("a feature editor can change a feature switch but not a system setting", async ({ page }) => {
    const staff = await staffWithPermissions(["settings.system.view", "settings.feature.edit"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    const flag = await openSetting(page, SWITCH);
    await expect(flag.getByRole("switch")).toBeEnabled();
    const system = await openSetting(page, NUMBER);
    await expect(system.getByTestId("setting-input")).toBeDisabled();
  });

  test("without the view permission the menu item and the page are closed", async ({ page }) => {
    const staff = await staffWithPermissions(["catalog.product.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin");
    await expect(page.getByRole("link", { name: "Settings", exact: true })).toHaveCount(0);
    await page.goto("/admin/settings");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("the menu opens the settings centre", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin");
    await page.getByRole("button", { name: "System", exact: true }).click(); // Settings is a child of the System parent
    const link = page.getByRole("link", { name: "Settings", exact: true }).first();
    await expect(link).toBeVisible();
    await link.click();
    await expect(page).toHaveURL(/\/admin\/settings$/);
    await expect(page.getByRole("heading", { level: 1, name: "Settings" })).toBeVisible();
  });
});
