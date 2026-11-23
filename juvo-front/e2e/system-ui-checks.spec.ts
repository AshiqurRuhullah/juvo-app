import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";
import { cleanSystem, seedSystem, type SystemSeed } from "./support/system";

/**
 * Background jobs and system health on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no
 * console errors, for the three job tabs (with a ticked selection, the confirm dialog and a bulk result), the health cards and the
 * status panel, a view-only staff member, the dark theme and Bengali. Screenshots go to docs/ui-checks/system/.
 */
const WIDTHS = [360, 768, 1280] as const;
let seed: SystemSeed;

test.beforeAll(() => {
  mkdirSync(resolve(SHOTS, "system"), { recursive: true });
  seed = seedSystem();
});
test.afterAll(() => {
  cleanSystem(seed);
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `system/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

const TABS = { en: { queue: "Queue", failed: "Failed sends" }, bn: { queue: "সারি", failed: "ব্যর্থ পাঠানো" } } as const;

const openJobs = async (page: Page) => {
  await page.goto("/admin/system/jobs");
  await expect(page.getByTestId("job-card").first()).toBeVisible();
};
const openQueue = async (page: Page, lang: "en" | "bn" = "en") => {
  await openJobs(page);
  await page.getByRole("tab", { name: TABS[lang].queue }).click();
  await page.locator("#outbox-type-filter").fill(`${seed.typePrefix}.dead`);
  await expect(page.getByTestId("outbox-type").filter({ visible: true }).first()).toBeVisible();
};
const openFailed = async (page: Page, lang: "en" | "bn" = "en") => {
  await openJobs(page);
  await page.getByRole("tab", { name: TABS[lang].failed }).click();
  await page.locator("#failed-template-filter").fill(seed.templatePrefix);
  await expect(page.getByTestId("failed-template").filter({ visible: true }).first()).toBeVisible();
};
const openHealth = async (page: Page) => {
  await page.goto("/admin/system/health");
  await expect(page.getByTestId("integration-card").first()).toBeVisible();
  await expect(page.getByTestId("status-panel").getByTestId("stat-database")).toBeVisible();
};

for (const width of WIDTHS) {
  for (const [label, run] of [
    ["job cards and runs", async (page: Page, errors: string[]) => {
      await openJobs(page);
      await page.getByLabel("Search job runs").fill(`e2esys${seed.tag}`);
      await expect(page.getByTestId("run-job").filter({ visible: true }).first()).toBeVisible();
      await checks(page, width, errors, `runs-${width}`);
    }],
    ["run detail dialog", async (page: Page, errors: string[]) => {
      await openJobs(page);
      await page.getByLabel("Search job runs").fill(`e2esys${seed.tag}`);
      await page.getByTestId("run-details").filter({ visible: true }).first().click();
      await expect(page.getByTestId("run-detail")).toBeVisible();
      await checks(page, width, errors, `run-detail-${width}`);
    }],
    ["queue with a selection", async (page: Page, errors: string[]) => {
      await openQueue(page);
      await page.getByTestId("bulk-select-page").click();
      await expect(page.getByTestId("bulk-count")).toContainText("selected");
      await checks(page, width, errors, `queue-${width}`);
    }],
    ["queue confirm dialog", async (page: Page, errors: string[]) => {
      await openQueue(page);
      await page.getByTestId("bulk-select-page").click();
      await page.getByTestId("bulk-action").click();
      await expect(page.getByRole("dialog")).toContainText("Requeue");
      await checks(page, width, errors, `queue-confirm-${width}`);
    }],
    ["failed sends", async (page: Page, errors: string[]) => {
      await openFailed(page);
      await checks(page, width, errors, `failed-${width}`);
    }],
    ["health cards and status", async (page: Page, errors: string[]) => {
      await openHealth(page);
      await checks(page, width, errors, `health-${width}`);
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
    const staff = await staffWithPermissions(["system.jobs.view", "system.health.view"]);
    await signInViaBff(context, staff.email, staff.password);
    await openQueue(page);
    await expect(page.getByTestId("outbox-requeue")).toHaveCount(0);
    await checks(page, width, errors, `queue-viewonly-${width}`);
  });
}

test("the queue and the health page in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await openQueue(page);
  await page.getByTestId("bulk-select-page").click();
  await checks(page, 1280, errors, "queue-dark");
  await openHealth(page);
  await checks(page, 1280, errors, "health-dark");
});

test("jobs and health in Bengali on a phone", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 360, height: 900 });
  await context.addCookies([{ name: "juvo_locale", value: "bn", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await openQueue(page, "bn");
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("ব্যাকগ্রাউন্ড জব");
  await checks(page, 360, errors, "queue-bn-360");
  await openFailed(page, "bn");
  await checks(page, 360, errors, "failed-bn-360");
  await openHealth(page);
  await expect(page.getByRole("heading", { level: 1 })).toHaveText("সিস্টেমের অবস্থা");
  await checks(page, 360, errors, "health-bn-360");
});
