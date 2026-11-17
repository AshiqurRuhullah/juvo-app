import { test, expect, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { makeCategory, makeSupplier, unique } from "./support/ops";
import { settleBounded } from "./support/couriers-ui";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * Every table shows ten rows per page and "Next" shows the next ten. The suppliers table pages on the server and the expense
 * categories table pages the rows it was given, so both ways are tried, then the pager is checked on a phone, a tablet and a desktop.
 */
const SUPPLIERS = 23;
const CATEGORIES = 13;

test.beforeAll(async () => {
  test.setTimeout(180_000);
  mkdirSync(resolve(SHOTS, "pagination"), { recursive: true });
  const prefix = unique("Pager");
  for (let i = 1; i <= SUPPLIERS; i++) await makeSupplier(`${prefix} ${String(i).padStart(2, "0")}`);
  for (let i = 1; i <= CATEGORIES; i++) await makeCategory(`${prefix} cat ${String(i).padStart(2, "0")}`);
});

async function signInAdmin(page: Page, next: string) {
  const who = loadState().admin;
  await signInViaBff(page.context(), who.email, who.password);
  await page.goto(next);
}

const bodyRows = (page: Page) => page.locator("tbody tr:visible");
const summary = (page: Page) => page.getByTestId("pagination-summary");

/** "Showing 11 to 20 of 134" -> [11, 20, 134] */
async function range(page: Page): Promise<[number, number, number]> {
  const text = (await summary(page).textContent()) ?? "";
  const m = /(\d+)\D+(\d+)\D+(\d+)/.exec(text.replace(/,/g, ""));
  if (!m) throw new Error(`unexpected summary: ${text}`);
  return [Number(m[1]), Number(m[2]), Number(m[3])];
}

test.describe("Pagination", () => {
  test("a table that pages on the server shows ten rows and Next shows the next ten", async ({ page }) => {
    test.skip(page.viewportSize()!.width < 1024, "the numbered strip and the table are driven on desktop; phones are covered by the checks below");
    await signInAdmin(page, "/admin/purchasing?tab=suppliers");
    await expect(page.getByTestId("pagination")).toBeVisible();

    const [from1, to1, total] = await range(page);
    expect([from1, to1]).toEqual([1, 10]);
    expect(total).toBeGreaterThanOrEqual(SUPPLIERS);
    await expect(bodyRows(page)).toHaveCount(10);
    const firstPage = await bodyRows(page).allTextContents();

    // First page: Previous is off, page 1 is the current page.
    await expect(page.getByTestId("page-prev")).toBeDisabled();
    await expect(page.getByTestId("page-1")).toHaveAttribute("aria-current", "page");
    await expect(page.getByTestId("page-next")).toBeEnabled();

    await page.getByTestId("page-next").click();
    await expect.poll(() => range(page)).toEqual([11, 20, total]);
    await expect(bodyRows(page)).toHaveCount(10);
    const secondPage = await bodyRows(page).allTextContents();
    expect(secondPage).not.toEqual(firstPage);
    expect(secondPage.filter((row) => firstPage.includes(row))).toEqual([]); // the next ten, never the same rows again
    await expect(page.getByTestId("page-2")).toHaveAttribute("aria-current", "page");
    await expect(page.getByTestId("page-prev")).toBeEnabled();

    // A numbered page, then the last page holds the rest and has no Next.
    const pages = Math.ceil(total / 10);
    await page.getByTestId(`page-${pages}`).click();
    await expect.poll(() => range(page)).toEqual([(pages - 1) * 10 + 1, total, total]);
    await expect(bodyRows(page)).toHaveCount(total - (pages - 1) * 10);
    await expect(page.getByTestId("page-next")).toBeDisabled();
    await expect(page.getByTestId(`page-${pages}`)).toHaveAttribute("aria-current", "page");

    // Back one page.
    await page.getByTestId("page-prev").click();
    await expect.poll(() => range(page)).toEqual([(pages - 2) * 10 + 1, (pages - 1) * 10, total]);
  });

  test("a table without server paging pages its own rows the same way", async ({ page }) => {
    test.skip(page.viewportSize()!.width < 1024, "driven on desktop");
    await signInAdmin(page, "/admin/accounting/expenses?tab=categories");
    await expect(page.getByTestId("pagination")).toBeVisible();
    const [from, to, total] = await range(page);
    expect([from, to]).toEqual([1, 10]);
    expect(total).toBeGreaterThanOrEqual(CATEGORIES);
    await expect(bodyRows(page)).toHaveCount(10);
    const firstPage = await bodyRows(page).allTextContents();

    await page.getByTestId("page-next").click();
    await expect.poll(() => range(page).then((r) => r[0])).toBe(11);
    const secondPage = await bodyRows(page).allTextContents();
    expect(secondPage.length).toBe(Math.min(10, total - 10));
    expect(secondPage.filter((row) => firstPage.includes(row))).toEqual([]);
  });

  test("the pager is a landmark with named buttons that a screen reader can use", async ({ page }) => {
    test.skip(page.viewportSize()!.width < 1024, "driven on desktop");
    await signInAdmin(page, "/admin/purchasing?tab=suppliers");
    await expect(page.getByRole("navigation", { name: /./ }).filter({ has: page.getByTestId("page-next") })).toBeVisible();
    await expect(page.getByTestId("page-2")).toHaveAccessibleName("Go to page 2");
    await expect(page.getByTestId("page-1")).toHaveAccessibleName(/Page 1, current page/);
    await expect(summary(page)).toHaveAttribute("aria-live", "polite");
    // Keyboard: Tab reaches the buttons and Enter turns the page.
    await page.getByTestId("page-2").focus();
    await page.keyboard.press("Enter");
    await expect.poll(() => range(page).then((r) => r[0])).toBe(11);
  });
});

/* ---- The pager on a phone, a tablet and a desktop ------------------------------------------------------------------------------ */

async function checks(page: Page, width: number, errors: string[], name: string) {
  const pending = await settleBounded(page);
  if (pending.length) console.log(`fonts still loading at ${width}px: ${pending.join(", ")}`);
  await shot(page, `pagination/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

for (const width of [360, 768, 1280] as const) {
  test(`the pager at ${width}px: no sideways scroll, 44 px targets, accessible`, async ({ page, context }) => {
    test.setTimeout(150_000);
    const errors: string[] = [];
    page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
    await page.setViewportSize({ width, height: 900 });
    const who = loadState().admin;
    await signInViaBff(context, who.email, who.password);
    await page.goto("/admin/purchasing?tab=suppliers");
    await expect(page.getByTestId("pagination")).toBeVisible();
    await checks(page, width, errors, `first-page-${width}`);

    // Turn a page on this size too: phones have no numbers, so use Next.
    await page.getByTestId("page-next").click();
    await expect.poll(() => range(page).then((r) => r[0])).toBe(11);
    await checks(page, width, errors, `second-page-${width}`);
  });
}

test("the pager in the dark theme", async ({ page, context }) => {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await context.addCookies([{ name: "juvo_theme", value: "dark", url: `http://localhost:${process.env.E2E_WEB_PORT ?? 3100}` }]);
  await page.setViewportSize({ width: 1280, height: 900 });
  const who = loadState().admin;
  await signInViaBff(context, who.email, who.password);
  await page.goto("/admin/purchasing?tab=suppliers");
  await expect(page.getByTestId("pagination")).toBeVisible();
  await checks(page, 1280, errors, "dark-1280");
});
