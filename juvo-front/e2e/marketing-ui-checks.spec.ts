import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { api, loadState } from "./support/env";
import { adminAccess } from "./support/returns";
import { emptyVariantStock, topUpHeadOfficeStock } from "./support/orders";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";
import { campaign, consentedSegment, customer, lastMessageTo, openSending, productWithOptions, tokenIn, type CampaignRef, type SegmentRef } from "./support/marketing";

/**
 * The marketing screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console errors.
 * Screenshots go to docs/ui-checks/marketing/.
 */
const WIDTHS = [360, 768, 1280] as const;

let segment: SegmentRef;
let draft: CampaignRef;
let sent: CampaignRef;
let soldOutSlug: string;
let unsubscribeToken: string;
let restores: { restore: () => Promise<void> }[] = [];

test.beforeAll(async () => {
  test.setTimeout(180_000);
  mkdirSync(resolve(SHOTS, "marketing"), { recursive: true });
  await openSending();
  const people = [await customer(), await customer()];
  segment = await consentedSegment();
  draft = await campaign(segment.id, { name: `UI draft ${Date.now()}` });
  sent = await campaign(segment.id, { name: `UI sent ${Date.now()}` });
  const token = await adminAccess();
  const response = await api("POST", `/api/v1/marketing/campaigns/${sent.id}/send`, undefined, token);
  expect(response.status).toBeLessThan(300);
  await expect
    .poll(async () => (await api<{ status: string }>("GET", `/api/v1/marketing/campaigns/${sent.id}`, undefined, token)).json.status, { timeout: 60_000 })
    .toBe("Sent");
  await expect.poll(() => lastMessageTo(people[0]!.mobile), { timeout: 60_000 }).toContain("token=");
  unsubscribeToken = tokenIn(lastMessageTo(people[0]!.mobile));

  // A sold-out product for the storefront form.
  await topUpHeadOfficeStock(token, loadState().branches.ho.id);
  const item = await productWithOptions();
  restores = [];
  for (const v of item.variants) restores.push(await emptyVariantStock(token, item.productId, v.id));
  soldOutSlug = item.slug;
});

test.afterAll(async () => {
  for (const r of restores) await r.restore();
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `marketing/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409|400)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

for (const width of WIDTHS) {
  test(`marketing screens at ${width}px`, async ({ page, context }) => {
    test.setTimeout(240_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);

    await page.goto("/admin/marketing/segments");
    await page.getByLabel("Search segments").fill(segment.name);
    await expect(page.locator("[data-testid=segment-name]:visible", { hasText: segment.name })).toHaveCount(1);
    await expect(page.locator("[data-testid=segment-count-total]:visible").first()).toBeVisible();
    await checks(page, width, errors, `segments-${width}`);

    await page.getByRole("button", { name: `Edit ${segment.name}` }).first().click();
    await expect(page.getByTestId("segment-form")).toBeVisible();
    await expect(page.getByTestId("live-total")).toBeVisible();
    await checks(page, width, errors, `segment-editor-${width}`);
    await page.keyboard.press("Escape");

    await page.getByRole("button", { name: `Members of ${segment.name}` }).first().click();
    await expect(page.getByTestId("members-dialog")).toBeVisible();
    await checks(page, width, errors, `segment-members-${width}`);
    await page.keyboard.press("Escape");

    await page.goto("/admin/marketing/campaigns");
    await expect(page.locator("[data-testid=campaign-link]:visible").first()).toBeVisible();
    await checks(page, width, errors, `campaigns-${width}`);

    await page.goto("/admin/marketing/campaigns/new");
    await expect(page.getByTestId("campaign-form")).toBeVisible();
    await checks(page, width, errors, `campaign-new-${width}`);

    await page.goto(`/admin/marketing/campaigns/${draft.id}`);
    await expect(page.getByTestId("campaign-detail")).toBeVisible();
    await page.getByTestId("campaign-preview").click();
    await expect(page.getByTestId("campaign-preview-panel")).toBeVisible();
    await checks(page, width, errors, `campaign-draft-${width}`);

    await page.getByTestId("campaign-send").click();
    await expect(page.getByTestId("send-confirm-count")).toBeVisible();
    await checks(page, width, errors, `campaign-send-confirm-${width}`);
    await page.keyboard.press("Escape");

    await page.getByTestId("campaign-schedule").click();
    await expect(page.getByTestId("schedule-dialog")).toBeVisible();
    await checks(page, width, errors, `campaign-schedule-${width}`);
    await page.keyboard.press("Escape");

    await page.goto(`/admin/marketing/campaigns/${sent.id}`);
    await expect(page.getByTestId("campaign-results")).toBeVisible();
    await expect(page.getByTestId("campaign-recipients")).toBeVisible();
    await checks(page, width, errors, `campaign-sent-${width}`);

    await page.goto("/admin/marketing/automations");
    await expect(page.getByTestId("automation-WinBack")).toBeVisible();
    await page.getByTestId("dry-run").click();
    await expect(page.getByTestId("dry-run-panel")).toBeVisible();
    await checks(page, width, errors, `automations-${width}`);

    await page.goto("/admin/marketing/settings");
    await expect(page.getByTestId("marketing-settings-form")).toBeVisible();
    await checks(page, width, errors, `settings-${width}`);
  });

  test(`public marketing pages at ${width}px`, async ({ page }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });

    await page.goto(`/product/${soldOutSlug}`);
    await expect(page.getByTestId("notify-me")).toBeVisible();
    await checks(page, width, errors, `notify-me-${width}`);

    await page.goto(`/unsubscribe?token=${encodeURIComponent(unsubscribeToken)}`);
    await expect(page.getByTestId("unsubscribe-confirm")).toBeVisible();
    await checks(page, width, errors, `unsubscribe-${width}`);

    await page.goto("/unsubscribe");
    await expect(page.getByTestId("unsubscribe-invalid")).toBeVisible();
    await checks(page, width, errors, `unsubscribe-missing-${width}`);
  });
}

test("marketing screens in the dark theme", async ({ page, context }) => {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);

  await page.goto(`/admin/marketing/campaigns/${sent.id}`);
  await expect(page.getByTestId("campaign-results")).toBeVisible();
  await checks(page, 1280, errors, "campaign-sent-dark");

  await page.goto("/admin/marketing/automations");
  await expect(page.getByTestId("automation-WinBack")).toBeVisible();
  await checks(page, 1280, errors, "automations-dark");

  await page.goto(`/product/${soldOutSlug}`);
  await expect(page.getByTestId("notify-me")).toBeVisible();
  await checks(page, 1280, errors, "notify-me-dark");
});
