import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";
import { cleanHistory, newSeedTag, seedHistory } from "./support/backup";

/**
 * Backups and the data inventory on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no
 * console errors, for the page (status, notes, runs as a table or as cards), the confirm dialog and the data inventory on the health
 * page, plus the dark theme and Bengali. History rows are written into the throw-away database for the screenshots and removed after.
 * No backup is taken here (the flow spec needs a database that has none). Screenshots go to docs/ui-checks/backup/.
 */
const WIDTHS = [360, 768, 1280] as const;
const tag = newSeedTag();

test.beforeAll(() => {
  mkdirSync(resolve(SHOTS, "backup"), { recursive: true });
  seedHistory(tag);
});
test.afterAll(() => {
  cleanHistory(tag);
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `backup/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

const openBackups = async (page: Page) => {
  await page.goto("/admin/system/backups");
  await expect(page.getByTestId("backup-state")).toBeVisible();
  await expect(page.getByTestId("run-status").filter({ visible: true }).first()).toBeVisible();
};
const openHealth = async (page: Page) => {
  await page.goto("/admin/system/health");
  await expect(page.getByTestId("inventory-row").first()).toBeVisible();
};

for (const width of WIDTHS) {
  for (const [label, run] of [
    ["status, notes and runs", async (page: Page, errors: string[]) => {
      await openBackups(page);
      await checks(page, width, errors, `page-${width}`);
    }],
    ["second page of runs", async (page: Page, errors: string[]) => {
      await openBackups(page);
      await page.getByRole("button", { name: /next/i }).filter({ visible: true }).first().click();
      await expect(page.getByTestId("run-status").filter({ visible: true }).first()).toBeVisible();
      await checks(page, width, errors, `page2-${width}`);
    }],
    ["confirm dialog", async (page: Page, errors: string[]) => {
      await openBackups(page);
      await page.getByTestId("backup-now").click();
      await expect(page.getByRole("dialog")).toContainText("Back up the database now?");
      await checks(page, width, errors, `confirm-${width}`);
    }],
    ["data inventory", async (page: Page, errors: string[]) => {
      await openHealth(page);
      await page.getByTestId("data-inventory").scrollIntoViewIfNeeded();
      await checks(page, width, errors, `inventory-${width}`);
    }],
  ] as const) {
    test(`${label} at ${width}px`, async ({ page, context }) => {
      test.setTimeout(120_000);
      const errors: string[] = [];
      page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
      await page.setViewportSize({ width, height: 900 });
      const admin = loadState().admin;
      await signInViaBff(context, admin.email, admin.password);
      await run(page, errors);
    });
  }
}

test("the runs show a failed run with its scrubbed error", async ({ page, context }) => {
  test.setTimeout(120_000);
  await page.setViewportSize({ width: 1280, height: 900 });
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await openBackups(page);
  const failed = page.locator('[data-testid="run-status"][data-status="Failed"]').filter({ visible: true });
  await expect(failed.first()).toBeVisible();
  await expect(page.getByTestId("run-error").filter({ visible: true }).filter({ hasText: `e2e seed ${tag}` }).first()).toBeVisible();
});

test("the page and the inventory in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await openBackups(page);
  await checks(page, 1280, errors, "page-dark");
  await openHealth(page);
  await checks(page, 1280, errors, "inventory-dark");
});

test("the page and the inventory in Bengali on a phone", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 360, height: 900 });
  await context.addCookies([{ name: "juvo_locale", value: "bn", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await openBackups(page);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("ব্যাকআপ");
  await checks(page, 360, errors, "page-bn-360");
  await openHealth(page);
  await expect(page.getByTestId("data-inventory")).toContainText("সিস্টেমে কী আছে");
  await checks(page, 360, errors, "inventory-bn-360");
});
