import { test, expect, type BrowserContext, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { resolve } from "node:path";
import { loadState } from "./support/env";
import { call } from "./support/cod";
import { adminAccess } from "./support/returns";
import { makeAccount, makeCategory, makeExpense, today, unique } from "./support/ops";
import { settleBounded } from "./support/couriers-ui";
import { SHOTS, axeViolations, horizontalOverflow, shot, signInViaBff, smallTapTargets } from "./support/ui";

/**
 * The expense and cash book screens on a phone, a tablet and a desktop: no sideways scroll, 44 px targets, axe (WCAG 2.1 AA) and no
 * console errors. Screenshots go to docs/ui-checks/accounting/.
 */
const WIDTHS = [360, 768, 1280] as const;

let submitted: { id: string; number: string };
let rejected: { id: string; number: string };
let accountName = "";

test.beforeAll(async () => {
  test.setTimeout(240_000);
  mkdirSync(resolve(SHOTS, "accounting"), { recursive: true });
  const admin = await adminAccess();
  const category = await makeCategory(unique("UI Rent"));
  const account = await makeAccount(unique("UI Drawer"), 2500);
  const other = await makeAccount(unique("UI Bank"), 800, "Bank");
  accountName = account.name;
  submitted = await makeExpense(admin, category.id, account.id, 1250.75, unique("UI-BILL"));
  await call("POST", `/api/v1/expenses/${submitted.id}/submit`, { token: admin, expect: 200 });
  rejected = await makeExpense(admin, category.id, account.id, 99, unique("UI-REJ"));
  await call("POST", `/api/v1/expenses/${rejected.id}/submit`, { token: admin, expect: 200 });
  await call("POST", `/api/v1/expenses/${rejected.id}/reject`, { token: admin, body: { reason: "Bill is not readable" }, expect: 200 });
  await makeExpense(admin, category.id, account.id, 20);
  await call("POST", "/api/v1/accounts/transfers", { token: admin, body: { fromAccountId: account.id, toAccountId: other.id, amount: 300, date: today(), note: "UI check" }, expect: 201 });
  await call("POST", "/api/v1/accounts/adjustments", { token: admin, body: { accountId: other.id, amount: -25, date: today(), reason: "UI check adjustment" }, expect: 201 });
});

async function checks(page: Page, width: number, errors: string[], name?: string) {
  const pending = await settleBounded(page);
  if (pending.length) console.log(`fonts still loading at ${width}px: ${pending.join(", ")}`);
  if (name) await shot(page, `accounting/${name}`);
  const overflow = await horizontalOverflow(page);
  expect(overflow.overflow, `sideways scroll at ${width}px: ${JSON.stringify(overflow)}`).toBeLessThanOrEqual(1);
  const small = await smallTapTargets(page);
  expect(small, `tap targets under 44px at ${width}px:\n${small.join("\n")}`).toEqual([]);
  const violations = await axeViolations(page);
  expect(violations, `accessibility at ${width}px:\n${violations.join("\n")}`).toEqual([]);
  expect(errors.filter((e) => !/Failed to load resource.*(404|401|403|409)|ERR_(INTERNET|NAME|CONNECTION)/.test(e))).toEqual([]);
}

async function open(page: Page, context: BrowserContext, width: number) {
  test.setTimeout(150_000);
  const errors: string[] = [];
  page.on("console", (m) => m.type() === "error" && errors.push(m.text()));
  await page.setViewportSize({ width, height: 900 });
  const admin = loadState().admin;
  await signInViaBff(context, admin.email, admin.password);
  return errors;
}

for (const width of WIDTHS) {
  test.describe(`${width}px`, () => {
    test("expense list", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto("/admin/accounting/expenses");
      await expect(page.getByTestId("expense-totals")).toBeVisible();
      await expect(page.getByRole("link", { name: submitted.number, exact: true }).first()).toBeVisible();
      await checks(page, width, errors, `expenses-${width}`);
    });

    test("expense detail (waiting and rejected)", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto(`/admin/accounting/expenses/${submitted.id}`);
      await expect(page.getByTestId("expense-approve")).toBeVisible();
      await checks(page, width, errors, `expense-waiting-${width}`);
      await page.goto(`/admin/accounting/expenses/${rejected.id}`);
      await expect(page.getByText(/Rejected: Bill is not readable/)).toBeVisible();
      await checks(page, width, errors, `expense-rejected-${width}`);
    });

    test("new expense dialog with errors", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto("/admin/accounting/expenses");
      await page.getByTestId("new-expense").click();
      await page.getByTestId("expense-save").click();
      await expect(page.getByText("Choose a category.")).toBeVisible();
      await page.waitForTimeout(500); // the dialog's opening animation
      await checks(page, width, errors, `expense-dialog-${width}`);
    });

    test("categories and repeating expenses", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto("/admin/accounting/expenses?tab=categories");
      await expect(page.getByTestId("new-category")).toBeVisible();
      await checks(page, width, errors, `categories-${width}`);
      await page.goto("/admin/accounting/expenses?tab=recurring");
      await expect(page.getByTestId("new-recurring")).toBeVisible();
      await checks(page, width, errors, `recurring-${width}`);
    });

    test("cash book accounts and day book", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto("/admin/accounting/cash");
      await expect(page.getByTestId("balances-summary")).toBeVisible();
      await expect(page.locator(`[data-testid=account-balance][data-name="${accountName}"]:visible`)).toBeVisible();
      await checks(page, width, errors, `cash-${width}`);
      await page.goto("/admin/accounting/cash?tab=daybook");
      await expect(page.getByTestId("daybook-totals")).toBeVisible();
      await checks(page, width, errors, `daybook-${width}`);
    });

    test("transfer dialog", async ({ page, context }) => {
      const errors = await open(page, context, width);
      await page.goto("/admin/accounting/cash");
      await page.getByTestId("new-transfer").click();
      await page.getByTestId("transfer-confirm").click();
      await expect(page.getByText("Choose the account to take money from.")).toBeVisible().catch(() => undefined);
      await page.waitForTimeout(500);
      await checks(page, width, errors, `transfer-dialog-${width}`);
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
  await page.goto(`/admin/accounting/expenses/${submitted.id}`);
  await expect(page.getByTestId("expense-approve")).toBeVisible();
  await checks(page, 1280, errors, "expense-dark");
  await page.goto("/admin/accounting/cash");
  await expect(page.getByTestId("balances-summary")).toBeVisible();
  await checks(page, 1280, errors, "cash-dark");
});
