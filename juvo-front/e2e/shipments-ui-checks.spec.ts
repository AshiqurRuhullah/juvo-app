import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { advanceParcel, bookedParcel, type Parcel } from "./support/couriers";
import { loadState } from "./support/env";
import { stockedVariants } from "./support/returns";
import { settleBounded } from "./support/couriers-ui";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The parcel screens on a phone, a tablet and a desktop: list, detail, dialogs and labels. No sideways scroll, 44 px targets,
 * axe (WCAG 2.1 AA) and no console errors. Screenshots go to docs/ui-checks/couriers/.
 */
const WIDTHS = [360, 768, 1280] as const;

let booked: Parcel;
let failed: Parcel;

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "couriers"), { recursive: true });
  const variants = await stockedVariants(4, 3);
  booked = (await bookedParcel([variants[0]!.id], "Nadia Booked")).parcel;
  failed = (await bookedParcel([variants[1]!.id], "Tanvir Failed")).parcel;
  for (const step of ["picked_up", "in_transit", "out_for_delivery", "delivery_failed"]) await advanceParcel(failed.id, step, step === "delivery_failed" ? "Customer phone was off" : undefined);
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  const pendingFonts = await settleBounded(page);
  if (pendingFonts.length) console.log(`fonts still loading at ${width}px:`, pendingFonts.join(", "));
  if (name) await shot(page, `couriers/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)|WebSocket|negotiate|hubs\/orders/.test(e))).toEqual([]);
}

for (const width of WIDTHS) {
  test(`parcel screens at ${width}px`, async ({ page, context }) => {
    test.setTimeout(150_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);

    await page.goto("/admin/shipments");
    await expect(page.getByRole("link", { name: booked.consignmentId }).first()).toBeVisible();
    await checks(page, width, errors, `list-${width}`);

    await page.getByRole("checkbox", { name: `Select parcel ${booked.consignmentId}` }).click();
    await expect(page.getByTestId("bulk-bar")).toBeVisible();
    await checks(page, width, errors, undefined);

    await page.goto(`/admin/shipments/${booked.id}`);
    await expect(page.getByTestId("shipment-actions")).toBeVisible();
    await checks(page, width, errors, `detail-${width}`);

    await page.goto(`/admin/shipments/${failed.id}`);
    await expect(page.getByTestId("failure-reason")).toBeVisible();
    await checks(page, width, errors, `detail-failed-${width}`);

    await page.goto(`/admin/shipments/${booked.id}`);
    await page.getByTestId("action-cancel").click();
    await expect(page.getByRole("dialog")).toBeVisible();
    await page.waitForTimeout(400);
    await checks(page, width, errors, width === 360 ? "cancel-dialog-360" : undefined);
    await page.keyboard.press("Escape");

    await page.getByTestId("action-rebook").click();
    await expect(page.getByRole("radio", { name: /Choose automatically/ })).toBeVisible();
    await page.waitForTimeout(400);
    await checks(page, width, errors, width === 360 ? "rebook-dialog-360" : undefined);
  });

  test(`label pages at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);

    await page.goto(`/admin/shipments/${booked.id}/label`);
    await expect(page.getByTestId("barcode")).toBeVisible();
    await checks(page, width, errors, `label-${width}`);

    await page.goto(`/admin/shipments/labels?ids=${booked.id},${failed.id}`);
    await expect(page.getByTestId("label")).toHaveCount(2);
    await checks(page, width, errors, width === 1280 ? "labels-1280" : undefined);
  });
}

test("parcel screens in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto(`/admin/shipments/${failed.id}`);
  await expect(page.getByTestId("failure-reason")).toBeVisible();
  await checks(page, 1280, errors, "detail-dark");
  await page.goto(`/admin/shipments/${booked.id}/label`);
  await expect(page.getByTestId("barcode")).toBeVisible();
  await checks(page, 1280, errors, "label-dark");
});
