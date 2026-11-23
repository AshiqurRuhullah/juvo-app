import { test, expect, type Page } from "@playwright/test";
import { E2E } from "../playwright.config";
import { loadState } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { resetSetting, writeSetting } from "./support/settings";
import { signInViaBff } from "./support/ui";
import { backupFiles, backupStatus, cleanBackupFolder, dataInventory, startBackupViaApi, waitUntilIdle } from "./support/backup";

/**
 * Backups and the data inventory in the browser against the real API: the status on a fresh database (Never, automatic backups off),
 * the honest notes, "Back up now" taking a REAL backup into the e2e folder (file written, run Succeeded and verified, status OK once
 * automatic backups are switched on), the calm "already running" message on 409, the data inventory against a direct API call, and the
 * permission cases. The e2e API writes into E2E.backupFolder (see playwright.config.ts); the folder is removed afterwards.
 */
test.describe.configure({ mode: "serial" });

test.beforeAll(() => {
  cleanBackupFolder();
});
test.afterAll(async () => {
  await resetSetting("Backup.Enabled").catch(() => undefined);
  cleanBackupFolder();
});

async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

async function openBackups(page: Page) {
  await page.goto("/admin/system/backups");
  await expect(page.getByRole("heading", { level: 1, name: "Backups" })).toBeVisible();
}

test.describe("Status on a fresh database", () => {
  test("shows Never, that automatic backups are off, the folder and the honest notes", async ({ page }) => {
    const before = await backupStatus();
    expect(before.lastVerifiedAt, "this spec needs a database without a verified backup").toBeNull();
    expect(before.enabled).toBe(false);

    await adminSignIn(page);
    await openBackups(page);
    const state = page.getByTestId("backup-state");
    await expect(state).toHaveAttribute("data-state", "never");
    await expect(state).toContainText("Never");
    await expect(page.getByTestId("backup-last-verified")).toHaveText("None yet");
    await expect(page.getByTestId("backup-off")).toContainText("Automatic backups are off");
    await expect(page.getByTestId("backup-settings-link")).toHaveAttribute("href", "/admin/settings");
    await expect(page.getByTestId("backup-folder")).toHaveText(before.folder);
    await expect(page.getByTestId("backup-retention")).toContainText("14 days");
    await expect(page.getByTestId("backup-next")).toContainText("Not scheduled");
    await expect(page.getByTestId("backup-free")).toContainText(/\d/);

    const notes = page.getByTestId("backup-notes");
    await expect(page.getByTestId("note-disk")).toContainText("not disaster recovery");
    await expect(page.getByTestId("note-encryption")).toContainText("not encrypted");
    await expect(page.getByTestId("note-uploads")).toContainText("needs its own copy");
    await expect(page.getByTestId("note-restore")).toContainText("scripts/restore-db.ps1");
    await expect(page.getByTestId("note-restore")).toContainText("scripts/restore-test.ps1");
    await expect(page.getByTestId("note-restore")).toContainText("no restore button on purpose");
    await expect(notes).toContainText("docs/backup-api.md");

    await expect(page.getByText("No backups yet")).toBeVisible();
    // The API serves no backup file, so the page has no download link and no restore button.
    await expect(page.locator('a[download], a[href$=".bak"], a[href*="/files/"]')).toHaveCount(0);
    await expect(page.getByRole("button", { name: /restore|download/i })).toHaveCount(0);
  });

  test("the menu opens the page from the System group", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin");
    await page.getByRole("button", { name: "System", exact: true }).click();
    await page.getByRole("link", { name: "Backups", exact: true }).first().click();
    await expect(page).toHaveURL(/\/admin\/system\/backups$/);
    await expect(page.getByRole("heading", { level: 1, name: "Backups" })).toBeVisible();
  });
});

test.describe("Back up now", () => {
  test("cancel changes nothing, confirm takes a real verified backup and the status turns OK", async ({ page }) => {
    test.setTimeout(180_000);
    await writeSetting("Backup.Enabled", true);
    await adminSignIn(page);
    await openBackups(page);
    await expect(page.getByTestId("backup-off")).toHaveCount(0);
    await expect(page.getByTestId("backup-next")).toContainText("Dhaka time");

    // Cancel: nothing is started.
    await page.getByTestId("backup-now").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("A verified backup is written to the configured folder");
    await expect(dialog).toContainText("It can take a while");
    await dialog.getByRole("button", { name: "Cancel" }).click();
    await expect(dialog).toBeHidden();
    expect((await backupStatus()).lastRun).toBeNull();
    expect(backupFiles()).toEqual([]);

    // Confirm: progress, then a Succeeded and verified run.
    await page.getByTestId("backup-now").click();
    await page.getByRole("dialog").getByRole("button", { name: "Start backup" }).click();
    await expect(page.getByTestId("backup-progress")).toBeVisible({ timeout: 10_000 });
    await expect(page.getByTestId("backup-now")).toBeDisabled();

    // The page refreshes itself every 15 s while the run is in progress; allow for two rounds.
    const row = page.getByTestId("run-status").filter({ visible: true }).first();
    await expect(row).toHaveAttribute("data-status", "Succeeded", { timeout: 90_000 });
    await expect(page.getByTestId("backup-progress")).toBeHidden({ timeout: 30_000 });
    await expect(page.getByTestId("run-verified").filter({ visible: true }).first()).toHaveAttribute("data-verified", "true");
    await expect(page.getByTestId("run-trigger").filter({ visible: true }).first()).toHaveText("Staff member");
    await expect(page.getByTestId("run-size").filter({ visible: true }).first()).toContainText(/\d [KMG]B/);
    await expect(page.getByTestId("run-error").filter({ visible: true }).first()).toHaveText("-");

    const state = page.getByTestId("backup-state");
    await expect(state).toHaveAttribute("data-state", "ok", { timeout: 30_000 });
    await expect(state).toContainText("OK");
    await expect(page.getByTestId("backup-age")).toContainText("ago");
    await expect(page.getByTestId("backup-now")).toBeEnabled();

    // A real, verified file is in the folder and is the one the status names.
    const status = await backupStatus();
    expect(status.lastVerifiedFileName).toMatch(/^juvo-backup_.*\.bak$/);
    expect(backupFiles()).toContain(status.lastVerifiedFileName);
    await expect(page.getByTestId("backup-file")).toHaveText(status.lastVerifiedFileName!);
    expect(E2E.backupFolder).toContain("e2e-backup-");
  });

  test("a second start while one runs shows the calm 'already running' message (409)", async ({ page }) => {
    test.setTimeout(180_000);
    await adminSignIn(page);
    await openBackups(page);
    await expect(page.getByTestId("backup-now")).toBeEnabled();

    // Somebody else (or the night) starts a backup after this page loaded: this page does not know yet.
    expect(await startBackupViaApi()).toBe(202);
    await page.getByTestId("backup-now").click();
    await page.getByRole("dialog").getByRole("button", { name: "Start backup" }).click();
    await expect(page.getByTestId("backup-already")).toContainText("A backup is already running");
    await expect(page.getByTestId("backup-start-error")).toHaveCount(0);
    await waitUntilIdle();
  });
});

test.describe("Data inventory", () => {
  test("the health page lists the counts of the API and says they carry no personal data", async ({ page }) => {
    await adminSignIn(page);
    const before = await dataInventory();
    await page.goto("/admin/system/health");
    await expect(page.getByTestId("data-inventory")).toBeVisible();
    await expect(page.getByTestId("inventory-row")).toHaveCount(before.tables.length);
    const after = await dataInventory();
    await expect(page.getByTestId("inventory-note")).toContainText("no personal data");
    const labels: Record<string, string> = {
      customers: "Customers", orders: "Orders", products: "Products", variants: "Product variants", stockLevels: "Stock levels", invoices: "Invoices", payments: "Payments", auditLogs: "Audit log entries",
    };
    for (const table of before.tables) {
      const shown = Number((await page.locator(`[data-testid="inventory-row"][data-key="${table.key}"]`).getByTestId("inventory-count").innerText()).replace(/\D/g, ""));
      const low = Number(table.rows);
      const high = Number(after.tables.find((t) => t.key === table.key)!.rows);
      expect(shown, table.key).toBeGreaterThanOrEqual(low);
      expect(shown, table.key).toBeLessThanOrEqual(high);
      await expect(page.locator(`[data-testid="inventory-row"][data-key="${table.key}"]`)).toContainText(labels[table.key]!);
    }
    const mb = ((Number(after.uploadsBytes) || 0) / 1_048_576).toFixed(1);
    await expect(page.getByTestId("inventory-uploads")).toContainText(`${mb} MB`);
  });
});

test.describe("Permissions", () => {
  test("a Manager has no Backups item, is refused the page, but sees the data inventory", async ({ page }) => {
    const manager = loadState().manager;
    await signInViaBff(page.context(), manager.email, manager.password);
    await page.goto("/admin");
    await expect(page.locator('a[href="/admin/system/backups"]')).toHaveCount(0);
    await expect(page.locator('a[href="/admin/system/health"]').first()).toBeAttached();
    await page.goto("/admin/system/backups");
    await expect(page).toHaveURL(/forbidden/);
    await page.goto("/admin/system/health");
    await expect(page.getByTestId("data-inventory")).toBeVisible();
    await expect(page.getByTestId("inventory-row").first()).toBeVisible();
  });

  test("a staff member with view only sees the status and the runs but no button", async ({ page }) => {
    const staff = await staffWithPermissions(["system.backup.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin");
    await expect(page.locator('a[href="/admin/system/backups"]').first()).toBeAttached();
    await openBackups(page);
    await expect(page.getByTestId("backup-state")).toBeVisible();
    await expect(page.getByTestId("run-status").filter({ visible: true }).first()).toBeVisible();
    await expect(page.getByTestId("backup-now")).toHaveCount(0);
    // No settings permission: no link to the settings page either (the setting is on in this run, so the notice is absent anyway).
    await expect(page.getByTestId("backup-settings-link")).toHaveCount(0);
    await page.goto("/admin/system/health");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("a staff member with only the run permission cannot open the page", async ({ page }) => {
    const staff = await staffWithPermissions(["system.backup.run"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin");
    await expect(page.locator('a[href="/admin/system/backups"]')).toHaveCount(0);
    await page.goto("/admin/system/backups");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("view plus run shows the button; Super Admin sees page, button and inventory", async ({ page }) => {
    const staff = await staffWithPermissions(["system.backup.view", "system.backup.run"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await openBackups(page);
    await expect(page.getByTestId("backup-now")).toBeEnabled();
    await page.context().clearCookies();
    await adminSignIn(page);
    await openBackups(page);
    await expect(page.getByTestId("backup-now")).toBeEnabled();
    await page.goto("/admin/system/health");
    await expect(page.getByTestId("data-inventory")).toBeVisible();
  });
});
