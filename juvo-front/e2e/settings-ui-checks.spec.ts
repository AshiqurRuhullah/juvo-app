import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";
import { resetSetting, settingRow, writeSetting } from "./support/settings";

/**
 * The settings centre on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console errors,
 * for the groups view, the search results, the dangerous-switch and reset dialogs, the read-only entries and a view-only staff member,
 * plus the dark theme and Bengali. Screenshots go to docs/ui-checks/settings/. Settings changed here are put back afterwards.
 */
const WIDTHS = [360, 768, 1280] as const;
const NUMBER = "Marketing.AttributionWindowDays";

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "settings"), { recursive: true });
  await writeSetting(NUMBER, 15); // a stored row, so the screen has a "Changed" entry with Reset and a line in the history
});
test.afterAll(async () => {
  await resetSetting(NUMBER).catch(() => undefined);
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `settings/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

const open = async (page: Page, search = "") => {
  await page.goto("/admin/settings");
  await expect(page.getByTestId("setting-row").first()).toBeVisible();
  if (search) {
    await page.getByLabel(/Search settings|সেটিংস খুঁজুন/).fill(search);
    await expect(page.getByTestId("setting-row").first()).toBeVisible();
  }
};

for (const width of WIDTHS) {
  for (const [label, run] of [
    ["groups view", async (page: Page, errors: string[]) => {
      await open(page);
      await checks(page, width, errors, `list-${width}`);
    }],
    ["marketing group with a changed setting", async (page: Page, errors: string[]) => {
      await open(page);
      await page.getByTestId("settings-group").filter({ hasText: "Marketing" }).first().click();
      await expect(settingRow(page, NUMBER)).toBeVisible();
      await expect(settingRow(page, NUMBER).getByTestId("setting-reset")).toBeVisible();
      await checks(page, width, errors, `marketing-${width}`);
    }],
    ["search results", async (page: Page, errors: string[]) => {
      await open(page, "return");
      await expect(page.getByTestId("settings-search-count")).toBeVisible();
      await checks(page, width, errors, `search-${width}`);
    }],
    ["edit with an error", async (page: Page, errors: string[]) => {
      await open(page, NUMBER);
      const row = settingRow(page, NUMBER);
      await row.getByTestId("setting-input").fill("500");
      await row.getByTestId("setting-save").click();
      await expect(row.getByTestId("setting-error")).toBeVisible();
      await checks(page, width, errors, `error-${width}`);
    }],
    ["dangerous switch dialog", async (page: Page, errors: string[]) => {
      await open(page, "Loyalty.Enabled");
      const row = settingRow(page, "Loyalty.Enabled");
      await row.getByRole("switch").click();
      await row.getByTestId("setting-save").click();
      await expect(page.getByRole("dialog")).toContainText("reaches customers at once");
      await checks(page, width, errors, `danger-${width}`);
    }],
    ["reset dialog", async (page: Page, errors: string[]) => {
      await open(page, NUMBER);
      await settingRow(page, NUMBER).getByTestId("setting-reset").click();
      await expect(page.getByRole("dialog")).toContainText("go back to its default");
      await checks(page, width, errors, `reset-${width}`);
    }],
    ["security group with read-only entries", async (page: Page, errors: string[]) => {
      await open(page);
      await page.getByTestId("settings-group").filter({ hasText: "Sign-in and security" }).first().click();
      await expect(settingRow(page, "Auth.LockoutMinutes")).toBeVisible();
      await checks(page, width, errors, `security-${width}`);
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

  test(`view-only staff at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const staff = await staffWithPermissions(["settings.system.view"]);
    await signInViaBff(context, staff.email, staff.password);
    await open(page, NUMBER);
    await expect(settingRow(page, NUMBER).getByTestId("setting-blocked")).toBeVisible();
    await checks(page, width, errors, `viewonly-${width}`);
  });
}

test("settings centre in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await open(page);
  await page.getByTestId("settings-group").filter({ hasText: "Marketing" }).first().click();
  await expect(settingRow(page, NUMBER)).toBeVisible();
  await checks(page, 1280, errors, "marketing-dark");
  await settingRow(page, NUMBER).getByTestId("setting-reset").click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await checks(page, 1280, errors, "reset-dark");
});

test("settings centre in Bengali", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 360, height: 900 });
  await context.addCookies([{ name: "juvo_locale", value: "bn", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await open(page, NUMBER);
  await expect(settingRow(page, NUMBER).getByTestId("setting-title")).toContainText("বার্তার পর");
  await expect(settingRow(page, NUMBER).getByTestId("setting-source")).toHaveText("পরিবর্তিত");
  await checks(page, 360, errors, "list-bn-360");
  await settingRow(page, NUMBER).getByTestId("setting-reset").click();
  await expect(page.getByRole("dialog")).toContainText("ডিফল্ট");
  await checks(page, 360, errors, "reset-bn-360");
});
