import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { deliveredOrder, freshMobile, stockedVariants } from "./support/returns";
import { signInCustomer } from "./support/store";
import { canned, customerTicket, staffStep, staffTicket } from "./support/tickets";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";
import { settleBounded } from "./support/couriers-ui";

/**
 * The support screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no console errors.
 * Screenshots go to docs/ui-checks/support/.
 */
const WIDTHS = [360, 768, 1280] as const;

let customerMobile: string;
let customerTicketRef: { id: string; number: string };
let staffTicketRef: { id: string; number: string };

test.beforeAll(async () => {
  mkdirSync(resolve(SHOTS, "support"), { recursive: true });
  customerMobile = freshMobile();
  const [variant] = await stockedVariants(1, 1, 4);
  const order = await deliveredOrder([{ variantId: variant!.id, quantity: 1 }], customerMobile);
  customerTicketRef = await customerTicket(customerMobile, { subject: "Size question for the checks", message: "Does it run small?", orderNumber: order.number });
  staffTicketRef = await staffTicket({ mobile: customerMobile, subject: "Phone call for the checks", priority: "Urgent", orderNumber: order.number });
  await staffStep(customerTicketRef.id, "reply", { text: "It fits true to size.", internal: false, setStatus: null, attachmentPaths: [] });
  await staffStep(customerTicketRef.id, "reply", { text: "Internal: check the size chart.", internal: true, setStatus: null, attachmentPaths: [] });
  await canned(`Checks reply ${Date.now()}`, "Hello {customerName}, about {ticketNumber}.", "en");
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  await settleBounded(page);
  if (name) await shot(page, `support/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

for (const width of WIDTHS) {
  test(`public contact form at ${width}px`, async ({ page }) => {
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    await page.goto("/contact/order-help");
    await expect(page.getByTestId("contact-form")).toBeVisible();
    await checks(page, width, errors, `contact-${width}`);
  });

  test(`customer support screens at ${width}px`, async ({ page, context }) => {
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    await signInCustomer(context, customerMobile);

    await page.goto("/account/support");
    await expect(page.getByTestId("my-ticket").first()).toBeVisible();
    await checks(page, width, errors, `my-list-${width}`);

    await page.goto("/account/support/new");
    await expect(page.getByTestId("my-ticket-form")).toBeVisible();
    await checks(page, width, errors, `my-new-${width}`);

    await page.goto(`/account/support/${customerTicketRef.id}`);
    await expect(page.getByTestId("message-staff")).toBeVisible();
    await checks(page, width, errors, `my-detail-${width}`);
  });

  test(`staff support screens at ${width}px`, async ({ page, context }) => {
    test.setTimeout(120_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const admin = loadState().admin;
    await signInViaBff(context, admin.email, admin.password);

    await page.goto("/admin/support");
    await expect(page.getByTestId("ticket-summary")).toBeVisible();
    await expect(page.getByRole("link", { name: staffTicketRef.number }).first()).toBeVisible();
    await checks(page, width, errors, `list-${width}`);

    await page.goto(`/admin/support/${customerTicketRef.id}`);
    await expect(page.getByTestId("message-internal")).toBeVisible();
    await checks(page, width, errors, `detail-${width}`);

    await page.goto("/admin/support");
    await page.getByRole("tab", { name: "Canned replies" }).click();
    await expect(page.getByTestId("canned-item").first()).toBeVisible();
    await checks(page, width, errors, `canned-${width}`);

    await page.getByRole("tab", { name: "Report" }).click();
    await expect(page.getByTestId("support-report")).toBeVisible();
    await checks(page, width, errors, `report-${width}`);
  });
}

test("support screens in the dark theme", async ({ page, context }) => {
  test.setTimeout(120_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width: 1280, height: 900 });
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  await page.goto(`/admin/support/${customerTicketRef.id}`);
  await expect(page.getByTestId("message-internal")).toBeVisible();
  await checks(page, 1280, errors, "detail-dark");
});
