import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { call } from "./support/cod";
import { loadState } from "./support/env";
import { adminAccess, staffWithPermissions } from "./support/returns";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";
import { publishPage, resetContent } from "./support/content";

/**
 * The content screens and the public pages on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and
 * no console errors, for the four staff screens, the page editor, the dialogs, a published page and the home page with badges and
 * footer, plus the dark theme, Bengali and the read-only view. Screenshots go to docs/ui-checks/content/.
 */
const WIDTHS = [360, 768, 1280] as const;
const WEB = `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}`;

test.beforeAll(async () => {
  test.setTimeout(120_000);
  mkdirSync(resolve(SHOTS, "content"), { recursive: true });
  await resetContent();
  const token = await adminAccess();
  await publishPage("about", "About Juvo", "# Our story\n\nWe sell **fashion**, shoes, ladies' dresses and purses.\n\n- Delivered across Bangladesh\n- Pay on delivery\n\nRead the [delivery policy](/delivery-policy).", {
    titleBn: "জুভো সম্পর্কে", bodyBn: "# আমাদের গল্প\n\nআমরা ফ্যাশন, জুতা ও ব্যাগ বিক্রি করি।\n\n- সারা বাংলাদেশে ডেলিভারি\n- ডেলিভারির সময় টাকা দিন",
    metaTitleEn: "About Juvo BD", metaDescriptionEn: "Who we are.",
  });
  await publishPage("delivery-policy", "Delivery policy", "We deliver in 2 to 5 days.");
  await call("PUT", "/api/v1/content/brand", { token, expect: 200, body: { businessName: "Juvo BD", email: "juvobd@gmail.com", phone: "02-5551234", whatsApp: "01712345678", addressEn: "House 12, Road 5, Dhanmondi, Dhaka", addressBn: "বাড়ি ১২, রোড ৫, ধানমন্ডি, ঢাকা", workingHoursEn: "Sat to Thu, 10 am to 8 pm", workingHoursBn: "শনি থেকে বৃহস্পতি, সকাল ১০টা থেকে রাত ৮টা" } });
  await call("PUT", "/api/v1/content/social/instagram", { token, expect: 200, body: { url: "https://instagram.com/juvobd", isEnabled: true, sortOrder: 1 } });
  await call("PUT", "/api/v1/content/social/whatsapp", { token, expect: 200, body: { url: "01712345678", isEnabled: false, sortOrder: 2 } });
  for (const [index, [key, en, bn]] of ([
    ["secure-payment", "Pay safely, cash on delivery available", "নিরাপদে পরিশোধ করুন, ক্যাশ অন ডেলিভারি আছে"],
    ["delivery-coverage", "We deliver across Bangladesh", "সারা বাংলাদেশে ডেলিভারি"],
    ["return-policy", "Seven days to return", "ফেরত দেওয়ার জন্য সাত দিন"],
  ] as const).entries()) {
    await call("PUT", `/api/v1/content/badges/${key}`, { token, expect: 200, body: { textEn: en, textBn: bn, isEnabled: true, sortOrder: index } });
  }
});
test.afterAll(async () => {
  await resetContent();
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `content/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(400|404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

for (const width of WIDTHS) {
  for (const [label, run] of [
    ["pages list", async (page: Page, errors: string[]) => {
      await page.goto("/admin/content/pages");
      await expect(page.getByTestId("page-row-about")).toBeVisible();
      await checks(page, width, errors, `pages-${width}`);
    }],
    ["page editor", async (page: Page, errors: string[]) => {
      await page.goto("/admin/content/pages/about");
      await expect(page.locator("#pg-title-En")).toHaveValue("About Juvo");
      await expect(page.getByTestId("page-preview-body")).toBeVisible();
      await checks(page, width, errors, `editor-${width}`);
    }],
    ["page editor with a refused save", async (page: Page, errors: string[]) => {
      await page.goto("/admin/content/pages/terms");
      await page.locator("#pg-title-En").fill("Terms");
      await page.locator("#pg-body-En").fill("<b>bold</b>");
      await page.getByTestId("page-save").click();
      await expect(page.getByTestId("page-error")).toContainText("Nothing was saved");
      await checks(page, width, errors, `editor-error-${width}`);
    }],
    ["publish dialog", async (page: Page, errors: string[]) => {
      await page.goto("/admin/content/pages/delivery-policy");
      await page.getByTestId("page-unpublish").click();
      await expect(page.getByRole("dialog")).toContainText("stops working at once");
      await checks(page, width, errors, `unpublish-dialog-${width}`);
    }],
    ["brand details", async (page: Page, errors: string[]) => {
      await page.goto("/admin/content/brand");
      await expect(page.locator("#br-name")).toHaveValue("Juvo BD");
      await checks(page, width, errors, `brand-${width}`);
    }],
    ["brand details with errors", async (page: Page, errors: string[]) => {
      await page.goto("/admin/content/brand");
      await page.locator("#br-phone").fill("call me");
      await page.locator("#br-lat").fill("23");
      await page.getByTestId("brand-save").click();
      await expect(page.getByText("Enter both latitude and longitude, or leave both empty.")).toBeVisible();
      await checks(page, width, errors, `brand-errors-${width}`);
    }],
    ["social links", async (page: Page, errors: string[]) => {
      await page.goto("/admin/content/social");
      await expect(page.getByTestId("social-row-instagram")).toBeVisible();
      await checks(page, width, errors, `social-${width}`);
    }],
    ["social link refused", async (page: Page, errors: string[]) => {
      await page.goto("/admin/content/social");
      await page.locator("#so-tiktok-url").fill("http://tiktok.com/@juvo");
      await page.getByTestId("so-tiktok-save").click();
      await expect(page.getByText("The address must start with https://, not http://.")).toBeVisible();
      await checks(page, width, errors, `social-error-${width}`);
    }],
    ["trust badges", async (page: Page, errors: string[]) => {
      await page.goto("/admin/content/badges");
      await expect(page.getByTestId("badge-row-secure-payment")).toBeVisible();
      await checks(page, width, errors, `badges-${width}`);
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

  for (const [label, path, ready] of [
    ["published page", "/about", "content-page"],
    ["page not available", "/faq", "content-not-available"],
    ["home with badges and footer", "/", "trust-badges"],
  ] as const) {
    test(`shop: ${label} at ${width}px`, async ({ page }) => {
      test.setTimeout(120_000);
      const errors: string[] = [];
      page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
      await page.setViewportSize({ width, height: 900 });
      await page.goto(path);
      await expect(page.getByTestId(ready)).toBeVisible();
      await expect(page.getByTestId("footer-brand")).toBeVisible();
      await expect(page.getByTestId("footer-social")).toBeVisible();
      await checks(page, width, errors, `shop-${label.replaceAll(" ", "-")}-${width}`);
    });
  }

  test(`read-only screens at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const staff = await staffWithPermissions(["content.page.view", "content.brand.view"]);
    await signInViaBff(context, staff.email, staff.password);
    await page.goto("/admin/content/pages/about");
    await expect(page.getByTestId("page-read-only")).toBeVisible();
    await checks(page, width, errors, `readonly-editor-${width}`);
    await page.goto("/admin/content/brand");
    await expect(page.getByTestId("brand-read-only")).toBeVisible();
    await checks(page, width, errors);
  });
}

test("content screens and shop pages in the dark theme", async ({ page, context }) => {
  test.setTimeout(180_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: WEB }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto("/admin/content/pages/about");
  await expect(page.getByTestId("page-preview-body")).toBeVisible();
  await checks(page, 1280, errors, "editor-dark");
  await page.goto("/admin/content/social");
  await expect(page.getByTestId("social-row-instagram")).toBeVisible();
  await checks(page, 1280, errors, "social-dark");
  await page.goto("/about");
  await expect(page.getByTestId("content-page")).toBeVisible();
  await checks(page, 1280, errors, "shop-page-dark");
  await page.goto("/");
  await expect(page.getByTestId("trust-badges")).toBeVisible();
  await checks(page, 1280, errors, "shop-home-dark");
});

test("content screens and shop pages in Bengali", async ({ page, context }) => {
  test.setTimeout(180_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 360, height: 900 });
  await context.addCookies([{ name: "juvo_locale", value: "bn", url: WEB }]);
  await page.goto("/about");
  await expect(page.getByRole("heading", { level: 1, name: "জুভো সম্পর্কে" })).toBeVisible();
  await expect(page.getByTestId("footer-brand")).toContainText("ধানমন্ডি");
  await checks(page, 360, errors, "shop-page-bn-360");
  await page.goto("/");
  await expect(page.getByTestId("trust-badge-secure-payment")).toContainText("নিরাপদে পরিশোধ করুন");
  await checks(page, 360, errors, "shop-home-bn-360");
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto("/admin/content/pages/about");
  await expect(page.getByTestId("page-editor")).toBeVisible();
  await checks(page, 360, errors, "editor-bn-360");
  await page.goto("/admin/content/badges");
  await expect(page.getByTestId("badge-row-secure-payment")).toBeVisible();
  await checks(page, 360, errors, "badges-bn-360");
});
