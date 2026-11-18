import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { createPromotion, dhakaLocalIn, uniqueName, uniqueProduct } from "./support/promotions";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";

/**
 * The promotions screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console
 * errors. Screenshots go to docs/ui-checks/promotions/.
 */
const WIDTHS = [360, 768, 1280] as const;

let flash: { id: string };
let campaign: { id: string };
let bundle: { id: string };
let productName: string;

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "promotions"), { recursive: true });
  const product = await uniqueProduct(0);
  productName = product.nameEn;
  const target = { kind: "Product", targetId: product.id, role: "Main", quantity: 1 };
  flash = await createPromotion({ nameEn: uniqueName("Checks flash"), nameBn: "চেক ফ্ল্যাশ", type: "FlashSale", endsAt: `${dhakaLocalIn(72)}:00+06:00`, discountKind: "Percent", discountValue: 15, targets: [target] });
  campaign = await createPromotion({ nameEn: uniqueName("Checks campaign"), type: "FestivalCampaign", slug: `checks-${Math.random().toString(36).slice(2, 8)}`, titleEn: "Checks", bannerSlot: "HomeTop", targets: [target] });
  bundle = await createPromotion({ nameEn: uniqueName("Checks bundle"), type: "Bundle", bundlePrice: 900, targets: [{ kind: "Product", targetId: product.id, role: "BundleItem", quantity: 2 }] });
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `promotions/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

for (const width of WIDTHS) {
  for (const [label, run] of [
    ["list and report", async (page: Page, errors: string[]) => {
      await page.goto("/admin/promotions");
      await expect(page.getByTestId("promotion-summary")).toBeVisible();
      await expect(page.getByTestId("promotion-link").filter({ visible: true }).first()).toBeVisible();
      await checks(page, width, errors, `list-${width}`);
      await page.getByRole("tab", { name: "Campaign report" }).click();
      await expect(page.getByTestId("campaign-report")).toBeVisible();
      await expect(page.getByTestId("report-link").filter({ visible: true }).first()).toBeVisible();
      await checks(page, width, errors, `report-${width}`);
    }],
    ["new promotion", async (page: Page, errors: string[]) => {
      await page.goto("/admin/promotions/new");
      await expect(page.getByTestId("promotion-form")).toBeVisible();
      await checks(page, width, errors, `new-${width}`);
    }],
    ["flash sale", async (page: Page, errors: string[]) => {
      await page.goto(`/admin/promotions/${flash.id}`);
      await expect(page.getByTestId("promotion-state")).toBeVisible();
      await expect(page.getByTestId("target-row")).toContainText(productName);
      await expect(page.getByTestId("promotion-results").getByTestId("result-orders")).toBeVisible();
      await checks(page, width, errors, `flash-${width}`);
    }],
    ["bundle", async (page: Page, errors: string[]) => {
      await page.goto(`/admin/promotions/${bundle.id}`);
      await expect(page.getByTestId("promotion-state")).toBeVisible();
      await checks(page, width, errors, `bundle-${width}`);
    }],
    ["festival campaign", async (page: Page, errors: string[]) => {
      await page.goto(`/admin/promotions/${campaign.id}`);
      await expect(page.getByTestId("promotion-images")).toBeVisible();
      await checks(page, width, errors, `campaign-${width}`);
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

  test(`target picker and preview at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);
    await page.goto(`/admin/promotions/${flash.id}`);
    await page.getByTestId("targets-add-Main").click();
    await page.locator("#promo-product-Main").fill(productName);
    await expect(page.getByTestId("product-result").first()).toBeVisible();
    await checks(page, width, errors, `picker-${width}`);

    await page.locator("#preview-search").fill(productName);
    await page.getByTestId("preview-panel").getByRole("button", { name: new RegExp(productName) }).first().click();
    await page.getByTestId("preview-panel").getByRole("button", { name: /^Add / }).first().click();
    await page.getByTestId("preview-run").click();
    await expect(page.getByTestId("preview-result")).toBeVisible();
    await checks(page, width, errors, `preview-${width}`);
  });
}

test("promotion screens in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto("/admin/promotions");
  await expect(page.getByTestId("promotion-summary")).toBeVisible();
  await checks(page, 1280, errors, "list-dark");
  await page.goto(`/admin/promotions/${flash.id}`);
  await expect(page.getByTestId("promotion-state")).toBeVisible();
  await checks(page, 1280, errors, "flash-dark");
});

test("promotion screens in Bengali", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 360, height: 900 });
  await context.addCookies([{ name: "juvo_locale", value: "bn", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto(`/admin/promotions/${flash.id}`);
  await expect(page.getByTestId("promotion-state")).toBeVisible();
  await expect(page.getByTestId("stacking-section")).toContainText("অন্য অফারের সাথে কীভাবে মেলে");
  await checks(page, 360, errors, "flash-bn-360");
});
