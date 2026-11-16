import { test, expect, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { call } from "./support/cod";
import { adminAccess, staffAccess, staffWithPermissions } from "./support/returns";
import { balanceOf, digits, hoId, makeAccount, makeCategory, makeExpense, today, unique } from "./support/ops";
import { makePng } from "./support/png";
import { signInViaBff } from "./support/ui";

/**
 * Step 11A (expenses and cash book front end) against the real stack: an expense from draft to approved by another person, self
 * approval hidden and refused, rejection with a reason, categories, repeating expenses, accounts, transfers, adjustments, the balances
 * and the day book matching what was entered, and roles with fewer permissions.
 */
test.describe.configure({ mode: "serial" });

let admin = "";
test.beforeAll(async () => {
  admin = await adminAccess();
});

async function signInAdmin(page: Page, next: string) {
  const who = loadState().admin;
  await signInViaBff(page.context(), who.email, who.password);
  await page.goto(next);
}

async function signInAs(page: Page, who: { email: string; password: string }, next: string) {
  await page.context().clearCookies();
  await signInViaBff(page.context(), who.email, who.password);
  await page.goto(next);
}

const status = (page: Page) => page.getByTestId("expense-status").first();
const balanceCell = (page: Page, name: string) => page.locator(`[data-testid=account-balance][data-name="${name}"]:visible`);

test.describe("cash book", () => {
  const nameA = unique("Shop drawer");
  const nameB = unique("Bank account");
  let idA = "";
  let idB = "";

  test("a new account shows its opening balance", async ({ page }) => {
    const b = await makeAccount(nameB, 100, "Bank");
    idB = b.id;
    await signInAdmin(page, "/admin/accounting/cash");
    await page.getByTestId("new-account").click();
    await page.locator("#ac-name").fill(nameA);
    await page.locator("#ac-branch").click();
    await page.getByRole("option", { name: /^HO/ }).click();
    await page.locator("#ac-opening").fill("500");
    await page.getByTestId("account-save").click();
    await expect(balanceCell(page, nameA)).toBeVisible();
    expect(digits(await balanceCell(page, nameA).textContent())).toBe(500);
    const list = await call<{ id: string; name: string }[]>("GET", "/api/v1/accounts", { token: admin, expect: 200 });
    idA = list.json.find((a) => a.name === nameA)!.id;
    expect(await balanceOf(idA)).toBe(500);
  });

  test("a transfer moves money between two accounts", async ({ page }) => {
    await signInAdmin(page, "/admin/accounting/cash");
    await page.getByTestId("new-transfer").click();
    await page.locator("#tf-from").click();
    await page.getByRole("option", { name: new RegExp(nameA) }).click();
    await page.locator("#tf-to").click();
    await page.getByRole("option", { name: new RegExp(nameB) }).click();
    // The same account twice is refused on the screen.
    await page.locator("#tf-amount").fill("0");
    await page.getByTestId("transfer-confirm").click();
    await expect(page.getByText("Enter an amount above zero.")).toBeVisible();
    await page.locator("#tf-amount").fill("150");
    await page.getByTestId("transfer-confirm").click();
    await expect(balanceCell(page, nameA)).toBeVisible();
    await expect.poll(async () => digits(await balanceCell(page, nameA).textContent())).toBe(350);
    expect(digits(await balanceCell(page, nameB).textContent())).toBe(250);
    expect(await balanceOf(idA)).toBe(350);
    expect(await balanceOf(idB)).toBe(250);
  });

  test("an adjustment needs a reason and changes the balance", async ({ page }) => {
    await signInAdmin(page, "/admin/accounting/cash");
    await page.getByRole("button", { name: `Adjust ${nameA}` }).click();
    await page.locator("#adj-amount").fill("-50");
    await page.getByTestId("adjust-confirm").click();
    await expect(page.getByText("Give a reason.")).toBeVisible();
    await page.locator("#adj-reason").fill("Counted short at close");
    await page.getByTestId("adjust-confirm").click();
    await expect.poll(async () => digits(await balanceCell(page, nameA).textContent())).toBe(300);
    expect(await balanceOf(idA)).toBe(300);
    // The entries of the account list both movements, newest first.
    await page.getByRole("button", { name: `Entries of ${nameA}` }).click();
    const entries = page.getByTestId("entries");
    await expect(entries).toContainText("Adjustment");
    await expect(entries).toContainText("Transfer out");
  });

  test("the balances as of a date and the account edit", async ({ page }) => {
    await signInAdmin(page, "/admin/accounting/cash");
    await expect(page.getByTestId("balances-summary")).toBeVisible();
    // As of yesterday the account did not hold today's movements: opening only.
    const yesterday = new Date(Date.now() + 6 * 3600_000 - 86_400_000).toISOString().slice(0, 10);
    await page.locator("#ac-asof").fill(yesterday);
    await expect.poll(async () => digits(await balanceCell(page, nameA).textContent())).toBe(500);
    await page.locator("#ac-asof").fill("");
    await expect.poll(async () => digits(await balanceCell(page, nameA).textContent())).toBe(300);

    await page.getByRole("button", { name: `Edit ${nameA}` }).click();
    await page.locator("#ac-name").fill(`${nameA} main`);
    await page.getByTestId("account-save").click();
    await expect(balanceCell(page, `${nameA} main`)).toBeVisible();
  });

  test("the day book totals equal what was entered", async ({ page }) => {
    await signInAdmin(page, "/admin/accounting/cash?tab=daybook");
    await page.locator("#db-from").fill(today());
    await page.locator("#db-to").fill(today());
    await page.getByRole("combobox", { name: /^Account/ }).click();
    await page.getByRole("option", { name: new RegExp(nameA) }).click();
    // Account A: transfer out 150 and an adjustment of -50 today.
    await expect.poll(async () => digits(await page.getByTestId("daybook-out").textContent())).toBe(200);
    expect(digits(await page.getByTestId("daybook-in").textContent())).toBe(0);
    expect(digits(await page.getByTestId("daybook-net").textContent())).toBe(-200);
    expect(digits(await page.getByTestId("daybook-page-net").textContent())).toBe(-200);
    // The same numbers as the API gives.
    const api = await call<{ totalIn: number; totalOut: number; net: number }>("GET", `/api/v1/accounts/daybook?from=${today()}&to=${today()}&accountId=${idA}`, { token: admin, expect: 200 });
    expect(Number(api.json.totalOut)).toBe(200);
    expect(Number(api.json.net)).toBe(-200);
    await expect(page.getByRole("row").filter({ hasText: "Counted short at close" })).toBeVisible();
  });
});

test.describe("expenses", () => {
  let category: { id: string; name: string };
  let account: { id: string; name: string };
  let author: { email: string; password: string };

  test.beforeAll(async () => {
    category = await makeCategory(unique("Rent"));
    account = await makeAccount(unique("Expense drawer"), 1000);
    author = await staffWithPermissions(["accounting.expense.view", "accounting.expense.create", "accounting.expense.edit", "accounting.expense.approve", "accounting.cashbook.view"]);
  });

  test("an expense with a receipt is created, sent, and approved by another person", async ({ page }) => {
    const reference = unique("BILL");
    await signInAs(page, author, "/admin/accounting/expenses");
    await page.getByTestId("new-expense").click();
    await page.locator("#ex-category").click();
    await page.getByRole("option", { name: category.name }).click();
    await page.locator("#ex-account").click();
    await page.getByRole("option", { name: new RegExp(account.name) }).click();
    await page.locator("#ex-amount").fill("120.50");
    await page.locator("#ex-ref").fill(reference);
    await page.locator("#ex-file").setInputFiles({ name: "bill.png", mimeType: "image/png", buffer: makePng(64, 64) });
    await expect(page.getByTestId("ex-list")).toContainText("bill.png");
    await page.getByTestId("expense-save-send").click();

    await expect(page).toHaveURL(/\/admin\/accounting\/expenses\/[0-9a-f-]{36}$/);
    const id = page.url().split("/").pop()!;
    await expect(status(page)).toHaveText("Waiting for approval");
    expect(digits(await page.getByTestId("expense-amount").textContent())).toBe(120.5);
    await expect(page.getByTestId("expense-receipts")).toContainText("bill.png");

    // The author does not get an approve button, and the API refuses self approval with its own code.
    await expect(page.getByTestId("expense-approve")).toHaveCount(0);
    await expect(page.getByText("You sent this expense, so someone else has to approve it.")).toBeVisible();
    const token = await staffAccess(author.email, author.password);
    const refused = await call<{ code?: string }>("POST", `/api/v1/expenses/${id}/approve`, { token, body: {} });
    expect(refused.status).toBe(403);
    expect(refused.json.code).toBe("EXPENSE.SELF_APPROVAL");

    // Another person approves: the money leaves the account.
    const before = await balanceOf(account.id);
    await signInAdmin(page, `/admin/accounting/expenses/${id}`);
    await page.getByTestId("expense-approve").click();
    await page.getByTestId("dialog-confirm").click();
    await expect(status(page)).toHaveText("Approved");
    await expect(page.getByTestId("expense-approve")).toHaveCount(0);
    expect(await balanceOf(account.id)).toBe(before - 120.5);

    // The list shows it with the total of the filtered rows.
    await page.goto("/admin/accounting/expenses");
    await page.getByLabel("Search expenses").fill(reference);
    await expect(page.getByRole("row").filter({ hasText: reference }).or(page.getByRole("row").filter({ hasText: "Approved" })).first()).toBeVisible();
    await expect.poll(async () => digits(await page.getByTestId("expense-total-amount").textContent())).toBe(120.5);
  });

  test("a draft can be edited and deleted; a rejected one is reopened", async ({ page }) => {
    const draft = await makeExpense(admin, category.id, account.id, 40);
    await signInAdmin(page, `/admin/accounting/expenses/${draft.id}`);
    await expect(status(page)).toHaveText("Draft");
    await page.getByTestId("expense-edit").click();
    await page.locator("#ex-amount").fill("45");
    await page.getByTestId("expense-save").click();
    await expect.poll(async () => digits(await page.getByTestId("expense-amount").textContent())).toBe(45);
    await page.getByTestId("expense-delete").click();
    await page.getByRole("button", { name: "Delete draft" }).last().click();
    await expect(page).toHaveURL(/\/admin\/accounting\/expenses$/);

    // A submitted expense of someone else is rejected with a reason, then reopened by its author.
    const authorToken = await staffAccess(author.email, author.password);
    const other = await makeExpense(authorToken, category.id, account.id, 60);
    await call("POST", `/api/v1/expenses/${other.id}/submit`, { token: authorToken, expect: 200 });
    await page.goto(`/admin/accounting/expenses/${other.id}`);
    await expect(status(page)).toHaveText("Waiting for approval");
    await page.getByTestId("expense-reject").click();
    await page.getByTestId("dialog-confirm").click();
    await expect(page.getByText("This is required.")).toBeVisible();
    await page.getByRole("dialog").getByLabel("Reason").fill("No bill attached");
    await page.getByTestId("dialog-confirm").click();
    await expect(status(page)).toHaveText("Rejected");
    await expect(page.getByText("Rejected: No bill attached")).toBeVisible();

    await signInAs(page, author, `/admin/accounting/expenses/${other.id}`);
    await page.getByTestId("expense-return").click();
    await expect(status(page)).toHaveText("Draft");
    await expect(page.getByTestId("expense-submit")).toBeVisible();
  });

  test("a category is added, renamed and switched off", async ({ page }) => {
    const name = unique("Transport");
    await signInAdmin(page, "/admin/accounting/expenses?tab=categories");
    await page.getByTestId("new-category").click();
    await page.locator("#cat-en").fill(name);
    await page.locator("#cat-bn").fill("পরিবহন");
    await page.getByTestId("category-save").click();
    const row = page.getByRole("row").filter({ hasText: name });
    await expect(row).toBeVisible();
    await row.getByRole("button", { name: `Edit ${name}` }).click();
    await page.locator("#cat-en").fill(`${name} costs`);
    await page.getByTestId("category-save").click();
    const renamed = page.getByRole("row").filter({ hasText: `${name} costs` });
    await expect(renamed).toBeVisible();
    await renamed.getByRole("button", { name: `Edit ${name} costs` }).click();
    await page.locator("#cat-active").click();
    await page.getByTestId("category-save").click();
    await expect(page.getByRole("row").filter({ hasText: `${name} costs` })).toContainText("Switched off");
  });

  test("a repeating expense is created and run now", async ({ page }) => {
    const title = unique("Shop rent");
    await signInAdmin(page, "/admin/accounting/expenses?tab=recurring");
    await page.getByTestId("new-recurring").click();
    await page.locator("#rc-title").fill(title);
    await page.locator("#rc-category").click();
    await page.getByRole("option", { name: category.name }).click();
    // The dialog starts on the first branch of the list; the spec's cash account belongs to Head Office.
    await page.locator("#rc-branch").click();
    await page.getByRole("option", { name: /^HO/ }).click();
    await page.locator("#rc-account").click();
    await page.getByRole("option", { name: new RegExp(account.name) }).click();
    await page.locator("#rc-amount").fill("900");
    await page.locator("#rc-day").fill("1");
    await page.locator("#rc-start").fill(`${today().slice(0, 7)}-01`);
    await page.getByTestId("recurring-save").click();
    await expect(page.getByRole("row").filter({ hasText: title })).toBeVisible();

    await page.getByTestId("run-now").click();
    await expect(page.getByTestId("run-result")).toContainText(/created/);
    const list = await call<{ expenses: { items: { isRecurring: boolean; amount: number | string }[] } }>("GET", `/api/v1/expenses?Page=1&PageSize=50&search=`, { token: admin, expect: 200 });
    expect(list.json.expenses.items.some((e) => e.isRecurring && Number(e.amount) === 900)).toBe(true);
  });

  test("a view-only role has no action buttons", async ({ page }) => {
    const draft = await makeExpense(admin, category.id, account.id, 15);
    const viewer = await staffWithPermissions(["accounting.expense.view", "accounting.cashbook.view"]);
    await signInAs(page, viewer, "/admin/accounting/expenses");
    await expect(page.getByTestId("new-expense")).toHaveCount(0);
    await page.goto(`/admin/accounting/expenses/${draft.id}`);
    await expect(status(page)).toHaveText("Draft");
    for (const id of ["expense-edit", "expense-submit", "expense-delete", "expense-approve", "expense-reject", "expense-return"]) await expect(page.getByTestId(id)).toHaveCount(0);
    await page.goto("/admin/accounting/expenses?tab=categories");
    await expect(page.getByTestId("new-category")).toHaveCount(0);
    await page.goto("/admin/accounting/expenses?tab=recurring");
    await expect(page.getByTestId("run-now")).toHaveCount(0);
    await page.goto("/admin/accounting/cash");
    await expect(page.getByTestId("new-account")).toHaveCount(0);
    await expect(page.getByTestId("new-transfer")).toHaveCount(0);
    // The API refuses what the screen hides.
    const token = await staffAccess(viewer.email, viewer.password);
    expect((await call("POST", `/api/v1/expenses/${draft.id}/submit`, { token })).status).toBe(403);
    expect((await call("POST", "/api/v1/accounts/transfers", { token, body: { fromAccountId: account.id, toAccountId: account.id, amount: 1 } })).status).toBe(403);
  });

  test("a role without view permission is sent away", async ({ page }) => {
    const other = await staffWithPermissions(["purchasing.po.view"]);
    await signInAs(page, other, "/admin/accounting/expenses");
    await expect(page).toHaveURL(/forbidden/);
    await page.goto("/admin/accounting/cash");
    await expect(page).toHaveURL(/forbidden/);
  });
});

test("expense totals and the day book agree after an approval", async () => {
  const category = await makeCategory();
  const account = await makeAccount(unique("Check drawer"), 300);
  const e = await makeExpense(admin, category.id, account.id, 75, unique("CHK"));
  await call("POST", `/api/v1/expenses/${e.id}/submit`, { token: admin, expect: 200 });
  await call("POST", `/api/v1/expenses/${e.id}/approve`, { token: admin, body: {}, expect: 200 });
  expect(await balanceOf(account.id)).toBe(225);
  const book = await call<{ totalOut: number | string }>("GET", `/api/v1/accounts/daybook?from=${today()}&to=${today()}&accountId=${account.id}&branchId=${hoId()}`, { token: admin, expect: 200 });
  expect(Number(book.json.totalOut)).toBe(75);
});
