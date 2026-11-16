import { test, expect, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { call, deliveredParcel } from "./support/cod";
import { adminAccess, deliveredOrder, stockedVariants, type Delivered } from "./support/returns";
import { makeAccount, makeCategory, makeExpense, unique } from "./support/ops";
import { signInViaBff } from "./support/ui";
import { csvLines, digits, exportFrom, n, orderMoney, rangeQuery, readReport, reportPath, staffInBranch, startsWith, tile } from "./support/reports";

/**
 * Step 11A (reports front end) against the real stack. The e2e database is shared with other specs, so every number is checked as a
 * difference: the API is read before three orders, a delivered cash parcel and an expense are created, and the screens must show the
 * "before" numbers plus exactly what was created.
 */
test.describe.configure({ mode: "serial" });

type Sales = { totals: { orders: number | string; units: number | string; total: number | string; net: number | string }; rows: unknown[] };
type OrdersR = { placed: number | string; funnel: { stage: string; count: number | string }[] };
type Finance = { cod: { collected: number | string; settled: number | string; pendingWithCouriers: number | string }; totals: { grossSales: number | string } };
type Customers = { newCustomers: number | string; customers: number | string };
type Courier = { totals: { parcels: number | string; delivered: number | string } };
type Pnl = { total: { expenses: number | string; netRevenue: number | string; purchasesReceived: number | string }; costHidden: boolean };
type Dash = { todayTotals: { orders: number | string; sales: number | string }; pending: { toConfirm: number | string; toPack: number | string; heldByFraud: number | string; returnsWaiting: number | string; lowStock: number | string } };

const before = {} as { sales: Sales; orders: OrdersR; finance: Finance; customers: Customers; courier: Courier; pnl: Pnl; dash: Dash };
const created = { orders: 3, units: 0, total: 0, gross: 0, expense: 125, parcel: 0 };
let delivered: Delivered[] = [];

const q = () => rangeQuery();

test.beforeAll(async () => {
  test.setTimeout(240_000);
  before.sales = await readReport<Sales>(`reports/sales?${q()}`);
  before.orders = await readReport<OrdersR>(`reports/orders?${q()}`);
  before.finance = await readReport<Finance>(`reports/finance?${q()}`);
  before.customers = await readReport<Customers>(`reports/customers?${q()}`);
  before.courier = await readReport<Courier>(`reports/courier?${q()}`);
  before.pnl = await readReport<Pnl>(`accounting/pnl?${q()}`);
  before.dash = await readReport<Dash>("reports/dashboard");

  const variants = await stockedVariants(6, 3, 3);
  delivered = [
    await deliveredOrder([{ variantId: variants[0]!.id, quantity: 2 }]),
    await deliveredOrder([{ variantId: variants[1]!.id, quantity: 3 }]),
  ];
  const parcel = await deliveredParcel();
  created.parcel = parcel.expected;
  created.units = 2 + 3 + 1;
  const money = await Promise.all([...delivered.map((d) => orderMoney(d.orderId)), orderMoney(parcel.orderId)]);
  created.total = money.reduce((a, m) => a + m.grandTotal, 0);
  created.gross = money.reduce((a, m) => a + m.subtotal, 0);

  const admin = await adminAccess();
  const category = await makeCategory();
  const account = await makeAccount(unique("Reports drawer"), 1000);
  const expense = await makeExpense(admin, category.id, account.id, created.expense, unique("REP"));
  await call("POST", `/api/v1/expenses/${expense.id}/submit`, { token: admin, expect: 200 });
  await call("POST", `/api/v1/expenses/${expense.id}/approve`, { token: admin, body: {}, expect: 200 });
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

test.describe("landing and sales", () => {
  test("the landing page shows a card for every report the person may open", async ({ page }) => {
    await signInAdmin(page, "/admin/reports");
    for (const r of ["sales", "orders", "inventory", "finance", "courier", "customers", "pnl"]) await expect(page.getByTestId(`report-card-${r}`)).toBeVisible();
    await page.getByTestId("report-card-sales").click();
    await expect(page).toHaveURL(/\/admin\/reports\/sales/);
  });

  test("sales shows the numbers that were created", async ({ page }) => {
    await signInAdmin(page, reportPath("sales"));
    await expect(page.getByTestId("tile-orders")).toBeVisible();
    await expect.poll(() => tile(page, "orders")).toBe(n(before.sales.totals.orders) + created.orders);
    expect(await tile(page, "units")).toBe(n(before.sales.totals.units) + created.units);
    // Money is compared to the cent.
    const total = digits(await page.getByTestId("tile-total").textContent());
    expect(Math.round(total * 100)).toBe(Math.round((n(before.sales.totals.total) + created.total) * 100));
    // The chart has a text summary and a hidden data table.
    await expect(page.getByTestId("chart-summary")).not.toBeEmpty();
    expect(await page.locator(".sr-only table").count()).toBeGreaterThan(0);
    const api = await readReport<Sales>(`reports/sales?${q()}`);
    expect(n(api.totals.orders)).toBe(n(before.sales.totals.orders) + created.orders);
  });

  test("changing the grouping changes the table and the address, and survives a reload", async ({ page }) => {
    await signInAdmin(page, reportPath("sales"));
    await expect(page.getByTestId("sales-table")).toBeVisible();
    await page.locator("#rf-group").click();
    await page.getByRole("option", { name: "Product" }).click();
    await expect(page).toHaveURL(/groupBy=product/);
    await expect(page.getByTestId("sales-table").getByRole("columnheader", { name: "Product" })).toBeVisible();
    await page.reload();
    await expect(page).toHaveURL(/groupBy=product/);
    await expect(page.getByTestId("sales-table").getByRole("columnheader", { name: "Product" })).toBeVisible();
    // The totals do not depend on the grouping.
    expect(await tile(page, "orders")).toBe(n(before.sales.totals.orders) + created.orders);
  });

  test("a preset and the date filters live in the address and survive a reload", async ({ page }) => {
    await signInAdmin(page, reportPath("sales"));
    await page.getByTestId("preset-last7").click();
    await expect(page.getByTestId("preset-last7")).toHaveAttribute("aria-pressed", "true");
    await expect(page).toHaveURL(/from=\d{4}-\d{2}-\d{2}&to=\d{4}-\d{2}-\d{2}/);
    const url = page.url();
    await page.reload();
    expect(page.url()).toBe(url);
    await expect(page.getByTestId("preset-last7")).toHaveAttribute("aria-pressed", "true");
    // A custom range turns "Custom" on.
    await page.locator("#rf-from").fill("2026-01-05");
    await expect(page).toHaveURL(/from=2026-01-05/);
    await expect(page.getByTestId("preset-custom")).toHaveAttribute("aria-pressed", "true");
  });

  test("a wrong range is explained before any request is sent", async ({ page }) => {
    await signInAdmin(page, reportPath("sales"));
    const requests: string[] = [];
    page.on("request", (r) => r.url().includes("/api/bff/api/v1/reports/sales") && requests.push(r.url()));
    await expect(page.getByTestId("tile-orders")).toBeVisible();
    requests.length = 0;
    await page.locator("#rf-from").fill("2026-12-31");
    await page.locator("#rf-to").fill("2026-01-01");
    await expect(page.getByTestId("range-problem")).toContainText("The end date must not be before the start date.");
    // Too large for daily grouping: a link with 400 days is refused with the API's limit.
    await page.goto("/admin/reports/sales?from=2025-01-01&to=2026-02-15&branch=");
    await expect(page.getByTestId("range-problem")).toContainText("366 days");
    expect(requests).toEqual([]);
    // The same range with monthly grouping is fine.
    await page.goto("/admin/reports/sales?from=2025-01-01&to=2026-02-15&branch=&groupBy=month");
    await expect(page.getByTestId("tile-orders")).toBeVisible();
    await expect(page.getByTestId("range-problem")).toHaveCount(0);
  });
});

test.describe("other reports", () => {
  test("orders shows the funnel of the created orders", async ({ page }) => {
    await signInAdmin(page, reportPath("orders"));
    await expect(page.getByTestId("funnel-Placed")).toBeVisible();
    const count = (stage: string) => page.getByTestId(`funnel-${stage}`).getByTestId("funnel-count");
    const was = (stage: string) => n(before.orders.funnel.find((f) => f.stage === stage)?.count);
    await expect.poll(async () => digits(await count("Placed").textContent())).toBe(was("Placed") + created.orders);
    expect(digits(await count("Delivered").textContent())).toBe(was("Delivered") + created.orders);
    await expect(page.getByTestId("orders-cancellations")).toBeVisible();
    await expect(page.getByTestId("orders-returns")).toBeVisible();
  });

  test("finance shows the cash on delivery that was created and states that gateway fees are not recorded", async ({ page }) => {
    await signInAdmin(page, reportPath("finance"));
    await expect(page.getByTestId("cod-tiles")).toBeVisible();
    const cents = async (key: string) => Math.round((await tile(page, key)) * 100);
    await expect.poll(() => cents("cod-pending")).toBe(Math.round((n(before.finance.cod.pendingWithCouriers) + created.parcel) * 100));
    expect(await cents("cod-collected")).toBe(Math.round((n(before.finance.cod.collected) + created.parcel) * 100));
    expect(await cents("cod-settled")).toBe(Math.round(n(before.finance.cod.settled) * 100));
    await expect(page.getByTestId("notice-gateway-fees")).toBeVisible();
    await expect(page.getByTestId("tile-fees")).toContainText("Not recorded");
    await expect(page.getByTestId("notice-cost-hidden")).toHaveCount(0);
    expect(await cents("gross")).toBe(Math.round((n(before.finance.totals.grossSales) + created.gross) * 100));
  });

  test("courier lists the sandbox courier with the parcel", async ({ page }) => {
    await signInAdmin(page, reportPath("courier"));
    await expect(page.getByTestId("courier-table")).toBeVisible();
    await expect.poll(() => tile(page, "parcels")).toBe(n(before.courier.totals.parcels) + 1);
    await expect(page.getByTestId("chart-summary")).not.toBeEmpty();
  });

  test("customers counts the three new buyers", async ({ page }) => {
    await signInAdmin(page, reportPath("customers"));
    await expect.poll(() => tile(page, "new")).toBe(n(before.customers.newCustomers) + 3);
    await expect(page.getByTestId("customers-top")).toBeVisible();
  });

  test("inventory shows the stock summary the API reports", async ({ page }) => {
    await signInAdmin(page, reportPath("inventory"));
    const api = await readReport<{ summary: { onHand: number | string; available: number | string }; deadStockCount: number | string; lowStockCount: number | string }>(`reports/inventory?${q()}&limit=100`);
    await expect(page.getByTestId("tile-onHand")).toBeVisible();
    await expect.poll(() => tile(page, "onHand")).toBe(n(api.summary.onHand));
    expect(await tile(page, "available")).toBe(n(api.summary.available));
    expect(await tile(page, "low")).toBe(n(api.lowStockCount));
    // Dead stock follows the "days" filter in the address.
    await page.goto(reportPath("inventory", "days=7"));
    await expect(page.getByTestId("inventory-dead")).toContainText("7 days");
    await expect(page.getByTestId("inventory-movements")).toBeVisible();
  });
});

test.describe("branches", () => {
  test("a global admin can pick one branch or look at everything together", async ({ page }) => {
    const a = loadState().branches.a;
    await signInAdmin(page, reportPath("sales"));
    await expect(page.getByTestId("report-scope")).toContainText("everything you may see");
    await page.locator("#rf-branch").click();
    await page.getByRole("option", { name: new RegExp(a.code) }).click();
    await expect(page).toHaveURL(new RegExp(`branch=${a.id}`));
    await expect(page.getByTestId("report-scope")).toContainText(a.code);
    await expect.poll(() => tile(page, "orders")).toBe(0); // nothing was ordered in this shop
    await page.locator("#rf-branch").click();
    await page.getByRole("option", { name: "All branches (consolidated)" }).click();
    await expect.poll(() => tile(page, "orders")).toBe(n(before.sales.totals.orders) + created.orders);
  });

  test("a person limited to one branch only ever sees that branch", async ({ page }) => {
    const { a, ho } = loadState().branches;
    const shopA = await staffInBranch(["reports.sales.view", "reports.sales.export"], a.id);
    await signInAs(page, shopA, reportPath("sales"));
    await expect(page.getByTestId("tile-orders")).toBeVisible();
    expect(await tile(page, "orders")).toBe(0);
    await expect(page.getByTestId("report-scope")).toContainText(a.code);
    // Asking for the head office through the address gives an empty answer, never the head office numbers.
    await page.goto(`/admin/reports/sales?${q()}&branch=${ho.id}`);
    await expect(page.getByTestId("tile-orders")).toBeVisible();
    expect(await tile(page, "orders")).toBe(0);
    // The branch list holds only their own branch, and there is no consolidated wording.
    await page.locator("#rf-branch").click();
    await expect(page.getByRole("option", { name: "All branches (consolidated)" })).toHaveCount(0);
    await expect(page.getByRole("option", { name: new RegExp(`^${ho.code} ·`) })).toHaveCount(0);
    await page.keyboard.press("Escape");

    const shopHo = await staffInBranch(["reports.sales.view"], ho.id);
    await signInAs(page, shopHo, reportPath("sales"));
    await expect.poll(() => tile(page, "orders")).toBe(n(before.sales.totals.orders) + created.orders);
  });
});

test.describe("exports", () => {
  test("CSV, Excel and PDF are real files with the server's name, and the CSV total matches the screen", async ({ page }) => {
    await signInAdmin(page, reportPath("sales"));
    await expect.poll(() => tile(page, "orders")).toBe(n(before.sales.totals.orders) + created.orders);
    const screenOrders = await tile(page, "orders");
    const screenUnits = await tile(page, "units");
    const screenTotal = Math.round((await tile(page, "total")) * 100);

    const csv = await exportFrom(page, "csv");
    expect(csv.name).toMatch(/^juvo-sales-.*.csv$/);
    expect(csv.bytes.length).toBeGreaterThan(50);
    expect(csv.bytes[0]).toBe(0xef); // UTF-8 byte order mark for Excel
    const lines = csvLines(csv.bytes);
    const total = lines.filter((l) => l.startsWith("Total")).pop()!.split(",");
    expect(Number(total[1])).toBe(screenOrders);
    expect(Number(total[2])).toBe(screenUnits);
    expect(Math.round(Number(total[7]) * 100)).toBe(screenTotal);
    await expect(page.getByTestId("export-done")).toContainText(csv.name);

    await page.reload();
    const xlsx = await exportFrom(page, "xlsx");
    expect(xlsx.name).toMatch(/^juvo-sales-.*.xlsx$/);
    expect(startsWith(xlsx.bytes, "PK")).toBe(true);
    expect(xlsx.bytes.length).toBeGreaterThan(1000);

    await page.reload();
    await page.getByTestId("export-open").click();
    await page.getByTestId("export-format-pdf").click();
    await expect(page.locator("#ex-lang")).toBeVisible();
    await page.locator("#ex-lang").click();
    await page.getByRole("option", { name: "Bengali" }).click();
    const waiting = page.waitForEvent("download");
    await page.getByTestId("export-download").click();
    const pdf = await waiting;
    expect(pdf.suggestedFilename()).toMatch(/^juvo-sales-.*.pdf$/);
    const bytes = (await import("node:fs")).readFileSync(await pdf.path());
    expect(startsWith(bytes, "%PDF")).toBe(true);
    expect(bytes.length).toBeGreaterThan(5000);
  });

  test("the section picker exports another table of the same report", async ({ page }) => {
    await signInAdmin(page, reportPath("orders"));
    await expect(page.getByTestId("funnel-Placed")).toBeVisible();
    const funnel = await exportFrom(page, "csv", "Funnel");
    expect(csvLines(funnel.bytes)[0]).toContain("Stage");
    await page.reload();
    const reasons = await exportFrom(page, "csv", "Cancellation reasons");
    expect(csvLines(reasons.bytes)[0]).toContain("Reason");
  });

  test("a person without the export permission has no Export menu", async ({ page }) => {
    const viewer = await staffInBranch(["reports.sales.view"]);
    await signInAs(page, viewer, reportPath("sales"));
    await expect(page.getByTestId("tile-orders")).toBeVisible();
    await expect(page.getByTestId("export-open")).toHaveCount(0);
  });
});

test.describe("cost access", () => {
  test("without cost access no cost, profit or stock value at cost is shown, and the notice says why", async ({ page }) => {
    const nocost = await staffInBranch(["reports.finance.view", "reports.finance.export", "reports.inventory.view", "accounting.pnl.view", "accounting.export.run"]);
    await signInAs(page, nocost, reportPath("finance"));
    await expect(page.getByTestId("notice-cost-hidden")).toBeVisible();
    await expect(page.getByTestId("tile-cost")).toContainText("Hidden");
    await expect(page.getByTestId("tile-profit")).toContainText("Hidden");
    await expect(page.getByRole("columnheader", { name: "Gross profit" })).toHaveCount(0);
    await expect(page.getByRole("columnheader", { name: "Cost of goods" })).toHaveCount(0);

    await page.goto(reportPath("inventory"));
    await expect(page.getByTestId("notice-cost-hidden")).toBeVisible();
    await expect(page.getByTestId("tile-valueCost")).toContainText("Hidden");
    await expect(page.getByRole("columnheader", { name: "Value at cost" })).toHaveCount(0);

    await page.goto(`/admin/accounting/pnl?${q()}&branch=`);
    await expect(page.getByTestId("notice-cost-hidden")).toBeVisible();
    await expect(page.getByTestId("tile-netProfit")).toContainText("Hidden");
    await expect(page.getByTestId("tile-cost")).toContainText("Hidden");

    // The accountant export of profit and loss is refused by the API; its reason is shown, and the day book still works.
    await page.getByTestId("accountant-export").scrollIntoViewIfNeeded();
    await page.locator("#ax-kind").click();
    await page.getByRole("option", { name: "Profit and loss" }).click();
    await page.getByTestId("accountant-download").click();
    await expect(page.getByTestId("accountant-error")).toContainText("Cost and profit figures are hidden");
    await page.locator("#ax-kind").click();
    await page.getByRole("option", { name: "Day book" }).click();
    const waiting = page.waitForEvent("download");
    await page.getByTestId("accountant-download").click();
    expect((await waiting).suggestedFilename()).toMatch(/\.csv$/);
  });
});

test.describe("profit and loss and the accountant export", () => {
  test("shows the expense that was created and purchases as information only", async ({ page }) => {
    await signInAdmin(page, `/admin/accounting/pnl?${q()}&branch=`);
    const cents = async (key: string) => Math.round((await tile(page, key)) * 100);
    await expect.poll(() => cents("expenses")).toBe(Math.round((n(before.pnl.total.expenses) + created.expense) * 100));
    expect(await cents("net")).toBeGreaterThan(Math.round(n(before.pnl.total.netRevenue) * 100));
    await expect(page.getByTestId("tile-fees")).toContainText("Not recorded");
    await expect(page.getByTestId("notice-gateway-fees")).toBeVisible();
    await expect(page.getByTestId("notice-cost-hidden")).toHaveCount(0);
    await expect(page.getByTestId("purchases-received")).toBeVisible();
    await expect(page.getByTestId("pnl-purchases")).toContainText("not part of the profit");
    await expect(page.getByTestId("pnl-categories")).toBeVisible();
    const api = await readReport<Pnl>(`accounting/pnl?${q()}`);
    expect(n(api.total.expenses)).toBe(n(before.pnl.total.expenses) + created.expense);
  });

  test("the accountant export downloads every kind as CSV or Excel", async ({ page }) => {
    await signInAdmin(page, `/admin/accounting/pnl?${q()}&branch=`);
    await expect(page.getByTestId("accountant-export")).toBeVisible();
    const download = async (kind: string, format: "CSV (UTF-8, opens in Excel)" | "Excel") => {
      await page.locator("#ax-kind").click();
      await page.getByRole("option", { name: kind, exact: true }).click();
      await page.locator("#ax-format").click();
      await page.getByRole("option", { name: format, exact: true }).click();
      const waiting = page.waitForEvent("download");
      await page.getByTestId("accountant-download").click();
      return (await import("./support/reports")).readDownload(await waiting);
    };
    const daybook = await download("Day book", "CSV (UTF-8, opens in Excel)");
    expect(daybook.name).toMatch(/\.csv$/);
    expect(daybook.bytes.length).toBeGreaterThan(30);
    const expenses = await download("Expenses", "CSV (UTF-8, opens in Excel)");
    expect(csvLines(expenses.bytes).join("\n")).toContain("125.00");
    const pnl = await download("Profit and loss", "Excel");
    expect(startsWith(pnl.bytes, "PK")).toBe(true);
    const payables = await download("Payables", "CSV (UTF-8, opens in Excel)");
    expect(payables.bytes.length).toBeGreaterThan(0);
    const purchases = await download("Purchases", "Excel");
    expect(startsWith(purchases.bytes, "PK")).toBe(true);
    // More than 366 days is explained on the screen.
    await page.goto("/admin/accounting/pnl?from=2024-01-01&to=2026-06-30&branch=&groupBy=month");
    await page.locator("#ax-kind").click();
    await page.getByRole("option", { name: "Expenses", exact: true }).click();
    await expect(page.getByTestId("range-problem")).toBeVisible();
    await expect(page.getByTestId("accountant-download")).toBeDisabled();
  });
});

test.describe("dashboard", () => {
  test("shows real numbers, the seeded orders, and no sample data", async ({ page }) => {
    await signInAdmin(page, "/admin");
    await expect(page.getByTestId("dashboard")).toBeVisible();
    await expect(page.getByText("Sample data")).toHaveCount(0);
    const branch = (await page.getByTestId("dashboard").getAttribute("data-branch")) ?? "";
    const api = await readReport<Dash & { today: string }>(`reports/dashboard${branch ? "?branchId=" + branch : ""}`);
    await expect(page.getByTestId("kpi-orders").getByTestId("kpi-value")).toHaveText(String(n(api.todayTotals.orders)));
    // The three orders and the parcel placed in this run are all today's: the consolidated numbers grew by exactly that.
    const consolidated = await readReport<Dash>("reports/dashboard");
    expect(n(consolidated.todayTotals.orders)).toBe(n(before.dash.todayTotals.orders) + created.orders);
    expect(n(consolidated.todayTotals.sales)).toBeCloseTo(n(before.dash.todayTotals.sales) + created.total, 2);
    // The action queue shows the API's counts.
    for (const key of ["toConfirm", "toPack", "heldByFraud", "returnsWaiting", "lowStock"] as const) {
      await expect(page.getByTestId(`action-count-${key}`)).toHaveText(String(n(api.pending[key])));
    }
    // A chart with a hidden table of 14 days.
    expect(await page.getByTestId("dashboard-series").locator("tbody tr").count()).toBe(14);
    await expect(page.getByTestId("top-products").or(page.getByText("No sales yet"))).toBeVisible();
    // The queue links go to the matching screens.
    await page.getByTestId("action-toConfirm").click();
    await expect(page).toHaveURL(/\/admin\/orders\?status=Placed/);
  });

  test("a role without the dashboard permission gets quick links and no numbers", async ({ page }) => {
    const limited = await staffInBranch(["orders.order.view"]);
    await signInAs(page, limited, "/admin");
    await expect(page.getByTestId("quick-links")).toBeVisible();
    await expect(page.getByTestId("kpi-sales")).toHaveCount(0);
    await expect(page.getByTestId("dashboard")).toHaveCount(0);
    await expect(page.getByTestId("quick-links").getByRole("link", { name: "Orders" }).first()).toBeVisible();
  });
});

test.describe("permissions", () => {
  test("a role without any report permission is sent away and sees no card", async ({ page }) => {
    const none = await staffInBranch(["orders.order.view"]);
    await signInAs(page, none, "/admin/reports");
    await expect(page.getByTestId("report-cards")).toHaveCount(0);
    await expect(page.getByText("No reports for you yet")).toBeVisible();
    for (const path of ["/admin/reports/sales", "/admin/reports/finance", "/admin/accounting/pnl"]) {
      await page.goto(path);
      await expect(page).toHaveURL(/forbidden/);
    }
  });

  test("a role with one report sees only that card and cannot open the others", async ({ page }) => {
    const one = await staffInBranch(["reports.courier.view"]);
    await signInAs(page, one, "/admin/reports");
    await expect(page.getByTestId("report-card-courier")).toBeVisible();
    await expect(page.getByTestId("report-card-sales")).toHaveCount(0);
    await page.goto("/admin/reports/sales");
    await expect(page).toHaveURL(/forbidden/);
    await page.goto("/admin/reports/nonsense");
    await expect(page.getByText(/not found|404/i).first()).toBeVisible();
  });

  });

