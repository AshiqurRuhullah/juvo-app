import { test, expect, type BrowserContext, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { disableAllRules, staffToken } from "./support/orders";
import { call, deliveredParcel, fmt2, makeBatch, reconcileBatch, type BatchRef, type Parcel } from "./support/cod";
import { SHOTS, axeViolations, horizontalOverflow, settle, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The COD settlement screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no
 * console errors. Screenshots go to docs/ui-checks/cod/.
 */
const WIDTHS = [360, 768, 1280] as const;

let openBatch: BatchRef;
let openParcels: Parcel[];
let settledBatch: BatchRef;

test.beforeAll(async () => {
  test.setTimeout(240_000);
  mkdirSync(resolve(SHOTS, "cod"), { recursive: true });
  const admin = await staffToken("admin");
  await disableAllRules(admin);
  openParcels = [await deliveredParcel(), await deliveredParcel(), await deliveredParcel()];
  openBatch = await makeBatch(openParcels, "Friday handover");
  const settledParcels = [await deliveredParcel(), await deliveredParcel()];
  settledBatch = await makeBatch(settledParcels);
  await reconcileBatch(settledBatch, settledParcels, [null, settledParcels[1]!.expected - 20], "kept 20 for change");
  await call("POST", `/api/v1/couriers/cod-settlements/${settledBatch.id}/settle`, { token: admin, body: { reference: "DEP-UI-001" }, expect: 200 });
  await deliveredParcel(); // two parcels stay waiting for a batch (the new-batch screen)
  await deliveredParcel();
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settle(page);
  if (name) await shot(page, `cod/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

/** A signed-in page of the given width that collects console errors. */
async function open(page: Page, context: BrowserContext, width: number) {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width, height: 900 });
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  return errors;
}

async function chooseSandbox(page: Page) {
  await page.getByLabel("Courier").click();
  await page.getByRole("option", { name: /Sandbox/ }).click();
  await expect(page.getByTestId("pending-row").first()).toBeVisible();
}

for (const width of WIDTHS) {
  test.describe(`${width}px`, () => {
    test(`list`, async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto("/admin/cod");
      await expect(page.getByTestId("cod-summary")).toBeVisible();
      await expect(page.getByRole("link", { name: openBatch.number, exact: true })).toBeVisible();
      await expect(page.getByTestId("cash-total")).toBeVisible();
      await checks(page, width, errors, `list-${width}`);
    });

    test(`new batch`, async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto("/admin/cod/new");
      await chooseSandbox(page);
      await checks(page, width, errors, `new-${width}`);
    });

    test(`detail with the reconcile form`, async ({ page, context }) => {
      const errors = await open(page, context, width);
      // One parcel short with its note still missing, one over: the danger and warning tones and the error show.
      await page.goto(`/admin/cod/${openBatch.id}`);
      await expect(page.getByTestId("reconcile-form")).toBeVisible();
      await page.locator(`#recv-${openParcels[1]!.shipmentId}`).fill(fmt2(openParcels[1]!.expected - 50));
      await page.locator(`#recv-${openParcels[2]!.shipmentId}`).fill(fmt2(openParcels[2]!.expected + 20));
      await page.getByTestId("reconcile-save").click();
      await expect(page.getByTestId("reconcile-problems")).toBeVisible();
      await checks(page, width, errors, `detail-reconcile-${width}`);
    });

    test(`settled detail`, async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto(`/admin/cod/${settledBatch.id}`);
      await expect(page.getByTestId("settled-note")).toBeVisible();
      await checks(page, width, errors, `detail-settled-${width}`);
    });

    test(`print sheet`, async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto(`/admin/cod/${settledBatch.id}/print`);
      await expect(page.getByTestId("settlement-sheet")).toBeVisible();
      await checks(page, width, errors, `print-${width}`);
    });
  });
}

test("dark theme", async ({ page, context }) => {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto(`/admin/cod/${openBatch.id}`);
  await expect(page.getByTestId("reconcile-form")).toBeVisible();
  await page.locator(`#recv-${openParcels[1]!.shipmentId}`).fill(fmt2(openParcels[1]!.expected - 50));
  await checks(page, 1280, errors, "detail-dark");
  await page.goto("/admin/cod");
  await expect(page.getByTestId("cod-summary")).toBeVisible();
  await checks(page, 1280, errors, "list-dark");
});
