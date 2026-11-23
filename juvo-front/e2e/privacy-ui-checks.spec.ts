import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { makeCustomer, type CustomerRef } from "./support/customers";
import { changeConsent } from "./support/privacy";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";

/**
 * The customer detail page with the new privacy cards and dialogs on a phone, a tablet and a desktop, in the dark theme and in
 * Bengali: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA), no console errors. Screenshots go to docs/ui-checks/privacy/.
 */
const WIDTHS = [360, 768, 1280] as const;
const WEB = `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}`;

let customer: CustomerRef;

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "privacy"), { recursive: true });
  customer = await makeCustomer({ marketingSms: true });
  await changeConsent(customer.mobile, "MarketingSms", false);
  await changeConsent(customer.mobile, "MarketingEmail", true);
});

async function checks(page: Page, width: number, errors: string[], name: string) {
  await settleBounded(page);
  await shot(page, `privacy/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409|429)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

/** Opens the page and walks through the cards and both dialogs (the anonymise dialog also once with a filled, valid form). */
async function walk(page: Page, width: number, errors: string[], tag: string) {
  await page.goto(`/admin/customers/${customer.id}`);
  await expect(page.getByTestId("consent-history-row").first()).toBeVisible();
  await expect(page.getByTestId("privacy-card")).toBeVisible();
  await checks(page, width, errors, `detail-${tag}`);

  await page.getByTestId("privacy-export").click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await checks(page, width, errors, `export-dialog-${tag}`);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);

  await page.getByTestId("privacy-anonymise").click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await checks(page, width, errors, `anonymise-dialog-${tag}`);
  const dialog = page.getByRole("dialog");
  await dialog.locator("#anonymise-reason").fill("x");
  await dialog.locator("#anonymise-reason").blur();
  await dialog.locator("#anonymise-confirm").fill("wrong-id");
  await dialog.locator("#anonymise-confirm").blur();
  await expect(dialog.locator("#anonymise-reason-error")).toBeVisible();
  await checks(page, width, errors, `anonymise-dialog-errors-${tag}`);
  await page.keyboard.press("Escape");
  await expect(page.getByRole("dialog")).toHaveCount(0);
}

for (const width of WIDTHS) {
  test(`privacy cards and dialogs at ${width}px`, async ({ page, context }) => {
    test.setTimeout(150_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);
    await walk(page, width, errors, String(width));
  });
}

test("privacy cards in the dark theme", async ({ page, context }) => {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: WEB }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await walk(page, 1280, errors, "dark");
});

test("privacy cards in Bengali", async ({ page, context }) => {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 360, height: 900 });
  await context.addCookies([{ name: "juvo_locale", value: "bn", url: WEB }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto(`/admin/customers/${customer.id}`);
  await expect(page.getByText("সম্মতির ইতিহাস").first()).toBeVisible();
  await expect(page.getByTestId("consent-history-row").first()).toBeVisible();
  await checks(page, 360, errors, "detail-bn");
  await page.getByTestId("privacy-anonymise").click();
  await expect(page.getByRole("dialog")).toBeVisible();
  await expect(page.getByRole("dialog")).toContainText("এটি ফেরানো যাবে না");
  await checks(page, 360, errors, "anonymise-dialog-bn");
});

test("the anonymise result and the anonymised page", async ({ page, context }) => {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 360, height: 900 });
  const gone = await makeCustomer();
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto(`/admin/customers/${gone.id}`);
  await page.getByTestId("privacy-anonymise").click();
  const dialog = page.getByRole("dialog");
  await dialog.locator("#anonymise-reason").fill("Customer asked for erasure by phone");
  await dialog.locator("#anonymise-confirm").fill(gone.id);
  await dialog.getByTestId("anonymise-submit").click();
  await expect(page.getByTestId("anonymise-result")).toBeVisible();
  await checks(page, 360, errors, "anonymise-result-360");
  await page.getByTestId("anonymise-close").click();
  await expect(page.getByTestId("anonymised-banner")).toBeVisible();
  await checks(page, 360, errors, "anonymised-360");
});
