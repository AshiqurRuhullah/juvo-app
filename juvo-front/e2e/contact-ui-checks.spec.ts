import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { call } from "./support/cod";
import { loadState } from "./support/env";
import { adminAccess, freshMobile, staffWithPermissions } from "./support/returns";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";
import { resetBrand } from "./support/content";
import { freeAddress, sendMessage, uniqueToken } from "./support/contact";

/**
 * The contact page and the staff inbox on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no
 * console errors, for the empty form, the form with errors, the confirmation, the inbox list (cards on a phone), a message with notes,
 * a masked read-only message, plus the dark theme and Bengali. Screenshots go to docs/ui-checks/contact/.
 */
const WIDTHS = [360, 768, 1280] as const;
const WEB = `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}`;
let latin: { id: string; reference: string; token: string };
let bengali: { id: string; reference: string };

test.beforeAll(async () => {
  test.setTimeout(120_000);
  mkdirSync(resolve(SHOTS, "contact"), { recursive: true });
  const token = await adminAccess();
  await call("PUT", "/api/v1/content/brand", {
    token,
    expect: 200,
    body: { businessName: "Juvo BD", email: "hello@juvo.test", phone: "02-5551234", whatsApp: "01712345678", addressEn: "House 12, Road 5, Dhanmondi, Dhaka", addressBn: "বাড়ি ১২, রোড ৫, ধানমন্ডি, ঢাকা", workingHoursEn: "Sat to Thu, 10 am to 8 pm", workingHoursBn: "শনি থেকে বৃহস্পতি, সকাল ১০টা থেকে রাত ৮টা", latitude: 23.7461, longitude: 90.3742 },
  });
  const layout = uniqueToken("Layout");
  const a = await sendMessage({ token: layout, name: "Nadia Rahman with a rather long name to test wrapping", subject: `${layout} A very long subject line that has to wrap on a phone without breaking the layout`, message: `Where is my parcel? It was due yesterday and nobody answered the phone. ${"Please help. ".repeat(40)}` });
  const b = await sendMessage({ name: "রহিম উদ্দিন", subject: "আমার অর্ডার কোথায়", message: "আমি গত সপ্তাহে একটি জামা অর্ডার করেছি, এখনও পাইনি।", email: "rahim@example.com" });
  const list = await call<{ items: { id: string; reference: string }[] }>("GET", "/api/v1/content/messages?pageSize=50", { token, expect: 200 });
  const find = (reference: string) => list.json.items.find((i) => i.reference === reference)!;
  latin = { ...find(a.reference), token: a.token };
  bengali = find(b.reference);
  await call("POST", `/api/v1/content/messages/${latin.id}/note`, { token, body: { note: "Called her, parcel is on the way" }, expect: 200 });
  await call("POST", `/api/v1/content/messages/${latin.id}/status`, { token, body: { status: "Read" }, expect: 200 });
});
test.afterAll(async () => {
  await resetBrand();
});
test.afterEach(() => freeAddress());

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `contact/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(400|404|401|403|409|429)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

function watch(page: Page) {
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  return errors;
}

for (const width of WIDTHS) {
  test(`contact page: empty, with errors and sent at ${width}px`, async ({ page }) => {
    test.setTimeout(120_000);
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });
    await page.goto("/contact");
    await expect(page.getByTestId("contact-message-form")).toBeVisible();
    await expect(page.getByTestId("contact-brand")).toBeVisible();
    await checks(page, width, errors, `form-${width}`);

    await page.getByTestId("contact-message-submit").click();
    await expect(page.getByText("Give a mobile number or an e-mail so we can answer.").first()).toBeVisible();
    await checks(page, width, errors, `form-errors-${width}`);

    await page.getByLabel("Your name").fill("Nadia Rahman");
    await page.getByLabel("Mobile number").fill(freshMobile());
    await page.getByLabel("Your message").fill("Where is my parcel? It was due yesterday.");
    await page.getByTestId("contact-message-submit").click();
    await expect(page.getByTestId("contact-sent")).toBeVisible();
    await checks(page, width, errors, `sent-${width}`);
  });

  for (const [label, run] of [
    ["inbox list", async (page: Page) => {
      await page.goto("/admin/content/messages");
      await expect(page.locator("[data-testid=message-link]:visible").first()).toBeVisible();
    }],
    ["inbox with a status filter and search", async (page: Page) => {
      await page.goto("/admin/content/messages");
      await page.getByTestId("filter-Read").click();
      await page.getByLabel("Search messages").fill(latin.token);
      await expect(page.locator("[data-testid=message-link]:visible")).toHaveCount(1);
    }],
    ["message with notes", async (page: Page) => {
      await page.goto(`/admin/content/messages/${latin.id}`);
      await expect(page.getByTestId("message-notes")).toContainText("Called her");
    }],
    ["message with an empty note refused", async (page: Page) => {
      await page.goto(`/admin/content/messages/${bengali.id}`);
      await page.getByTestId("note-add").click();
      await expect(page.getByText("Write something first.")).toBeVisible();
    }],
    ["message not found", async (page: Page) => {
      await page.goto("/admin/content/messages/00000000-0000-4000-8000-000000000000");
      await expect(page.getByText("This message was not found.")).toBeVisible();
    }],
  ] as const) {
    test(`${label} at ${width}px`, async ({ page, context }) => {
      test.setTimeout(120_000);
      const errors = watch(page);
      await page.setViewportSize({ width, height: 900 });
      const admin = loadState().admin;
      await signInViaBff(context, admin.email, admin.password);
      await run(page);
      await checks(page, width, errors, `${label.replaceAll(" ", "-")}-${width}`);
    });
  }

  test(`masked read-only inbox at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors = watch(page);
    await page.setViewportSize({ width, height: 900 });
    const staff = await staffWithPermissions(["content.message.view"]);
    await signInViaBff(context, staff.email, staff.password);
    await page.goto("/admin/content/messages");
    await expect(page.getByTestId("pii-note")).toBeVisible();
    await checks(page, width, errors, `inbox-masked-${width}`);
    await page.goto(`/admin/content/messages/${latin.id}`);
    await expect(page.getByTestId("message-read-only")).toBeVisible();
    await expect(page.getByTestId("pii-masked")).toBeVisible();
    await checks(page, width, errors, `message-read-only-${width}`);
  });
}

test("contact page and inbox in the dark theme", async ({ page, context }) => {
  test.setTimeout(180_000);
  const errors = watch(page);
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: WEB }]);
  await page.goto("/contact");
  await expect(page.getByTestId("contact-brand")).toBeVisible();
  await page.getByTestId("contact-message-submit").click();
  await expect(page.getByText("This is required.").first()).toBeVisible();
  await checks(page, 1280, errors, "form-dark");
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto("/admin/content/messages");
  await expect(page.locator("[data-testid=message-link]:visible").first()).toBeVisible();
  await checks(page, 1280, errors, "inbox-dark");
  await page.goto(`/admin/content/messages/${latin.id}`);
  await expect(page.getByTestId("message-notes")).toBeVisible();
  await checks(page, 1280, errors, "message-dark");
});

test("contact page and inbox in Bengali", async ({ page, context }) => {
  test.setTimeout(180_000);
  const errors = watch(page);
  await page.setViewportSize({ width: 360, height: 900 });
  await context.addCookies([{ name: "juvo_locale", value: "bn", url: WEB }]);
  await page.goto("/contact");
  await expect(page.getByRole("heading", { level: 1, name: "যোগাযোগ করুন" })).toBeVisible();
  await expect(page.getByTestId("contact-brand")).toContainText("ধানমন্ডি");
  await page.getByTestId("contact-message-submit").click();
  await expect(page.getByText("এটি আবশ্যক।").first()).toBeVisible();
  await checks(page, 360, errors, "form-bn-360");
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto("/admin/content/messages");
  await expect(page.getByRole("heading", { level: 1, name: "যোগাযোগের বার্তা" })).toBeVisible();
  await expect(page.locator("[data-testid=message-link]:visible").first()).toBeVisible();
  await checks(page, 360, errors, "inbox-bn-360");
  await page.goto(`/admin/content/messages/${bengali.id}`);
  await expect(page.getByTestId("message-text")).toContainText("জামা");
  await checks(page, 360, errors, "message-bn-360");
});
