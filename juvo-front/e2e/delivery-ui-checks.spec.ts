import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { staffWithPermissions } from "./support/returns";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";
import { deleteZone, freeDistricts, makeZone, type DistrictRef, type ZoneRef } from "./support/delivery";

/**
 * The delivery zone screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console
 * errors. Screenshots go to docs/ui-checks/delivery/.
 */
const WIDTHS = [360, 768, 1280] as const;

let zone: ZoneRef;
let districts: DistrictRef[];

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "delivery"), { recursive: true });
  districts = await freeDistricts(2);
  zone = await makeZone({
    districts: [{ id: districts[0]!.id }, { id: districts[1]!.id, remote: true }],
    rates: { baseFee: 80, freeDeliveryThreshold: 3000, remoteSurcharge: 40, perExtraKgFee: 15, maxWeightGrams: 10000, slabs: [{ upToGrams: 1000, extraFee: 0 }, { upToGrams: 2000, extraFee: 25 }] },
  });
});

test.afterAll(async () => {
  if (zone) await deleteZone(zone.id);
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `delivery/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

const tab = (page: Page, name: string) => page.getByRole("tab", { name, exact: true });

async function visitAll(page: Page, width: number, errors: string[], suffix: string) {
  await page.goto("/admin/delivery/zones");
  await page.getByLabel("Search zones").fill(zone.code);
  await expect(page.locator("[data-testid=zone-link]:visible")).toHaveCount(1);
  await checks(page, width, errors, `list-${suffix}`);

  await tab(page, "Countries").click();
  await expect(page.getByTestId("countries")).toBeVisible();
  await expect(page.locator("[data-testid=country-name]:visible").first()).toBeVisible();
  await checks(page, width, errors, `countries-${suffix}`);

  await page.goto("/admin/delivery/zones/new");
  await expect(page.getByTestId("details-form")).toBeVisible();
  await checks(page, width, errors, `new-${suffix}`);

  await page.goto(`/admin/delivery/zones/${zone.id}`);
  await expect(page.getByTestId("zone-editor")).toBeVisible();
  await checks(page, width, errors, `details-${suffix}`);

  await tab(page, "Areas").click();
  await expect(page.getByTestId("areas-editor")).toBeVisible();
  await page.getByLabel("Search districts").fill(districts[0]!.nameEn);
  await page.getByRole("button", { name: `Show upazilas of ${districts[0]!.nameEn}` }).first().click();
  await expect(page.getByTestId("upazila-list")).toBeVisible();
  await checks(page, width, errors, `areas-${suffix}`);

  await tab(page, "Rates").click();
  await expect(page.getByTestId("slab-ranges")).toBeVisible();
  await checks(page, width, errors, `rates-${suffix}`);

  await tab(page, "Try a quote").click();
  await page.getByRole("combobox", { name: /^District/ }).click();
  await page.getByRole("option", { name: districts[0]!.nameEn }).first().click();
  await page.getByTestId("quote-run").click();
  await expect(page.getByTestId("quote-result")).toBeVisible();
  await checks(page, width, errors, `quote-${suffix}`);
}

for (const width of WIDTHS) {
  test(`staff delivery screens at ${width}px`, async ({ page, context }) => {
    test.setTimeout(240_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);
    await visitAll(page, width, errors, String(width));

    await page.goto("/admin/delivery/zones");
    await page.getByTestId("open-import").click();
    await page.getByLabel("Upazilas", { exact: true }).fill("Dhaka, Sample one\nNowhere, Sample two");
    await expect(page.getByTestId("import-errors")).toBeVisible();
    await checks(page, width, errors, `import-${width}`);
    await page.keyboard.press("Escape");

    await page.goto(`/admin/delivery/zones/${zone.id}`);
    await page.getByTestId("zone-delete").click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await checks(page, width, errors, `delete-dialog-${width}`);
  });

  test(`read-only delivery screens at ${width}px`, async ({ page, context }) => {
    test.setTimeout(180_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const staff = await staffWithPermissions(["delivery.zone.view"]);
    await signInViaBff(context, staff.email, staff.password);
    await page.goto("/admin/delivery/zones");
    await page.getByLabel("Search zones").fill(zone.code);
    await expect(page.locator("[data-testid=zone-link]:visible")).toHaveCount(1);
    await checks(page, width, errors, `list-readonly-${width}`);
    await page.goto(`/admin/delivery/zones/${zone.id}`);
    await tab(page, "Rates").click();
    await expect(page.getByTestId("rates-read-only")).toBeVisible();
    await checks(page, width, errors, `rates-readonly-${width}`);
    await tab(page, "Areas").click();
    await expect(page.getByTestId("areas-read-only")).toBeVisible();
    await checks(page, width, errors, `areas-readonly-${width}`);
  });
}

test("delivery screens in the dark theme", async ({ page, context }) => {
  test.setTimeout(240_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await visitAll(page, 1280, errors, "dark");
});
