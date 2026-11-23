import { test, expect, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { topUpHeadOfficeStock } from "./support/orders";
import { deliveredParcel } from "./support/cod";
import { adminAccess, deliveredOrder, stockedVariants } from "./support/returns";
import { today } from "./support/ops";
import { signInViaBff } from "./support/ui";
import { digits, n, readReport, staffInBranch } from "./support/reports";

/**
 * Step 12f (dashboard, module M) against the real stack. Every number on screen is compared with the same report read straight from
 * the API; the e2e database is shared with other specs, so nothing is compared with a fixed value.
 */
test.describe.configure({ mode: "serial" });

type Row = { key: string; label: string; orders: number | string; units: number | string; net: number | string; total: number | string };
type Sales = { totals: Row; rows: Row[] };
type Finance = { cod: { collected: number | string; settled: number | string; pendingWithCouriers: number | string; inTransit: number | string }; totals: { refunds: number | string } };
type Inventory = { lowStockCount: number | string; deadStockCount: number | string };
type Customers = { topSpenders: { customerId: string; name: string | null; mobile: string; spent: number | string }[] };

const day = (iso: string, delta: number) => new Date(Date.parse(iso + "T00:00:00Z") + delta * 86_400_000).toISOString().slice(0, 10);
const TODAY = today();
const FROM = day(TODAY, -6);
const q = (from: string, to: string, extra = "") => `from=${from}&to=${to}${extra ? "&" + extra : ""}`;
const cents = (v: unknown) => Math.round(Number(v) * 100);

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

const tileCents = async (page: Page, key: string) => Math.round(digits(await page.getByTestId(`tile-${key}`).first().textContent()) * 100);

/** The branch the dashboard is showing (empty = consolidated), so the API reads use the same scope. */
async function branchParam(page: Page): Promise<string> {
  const branch = (await page.getByTestId("dashboard").getAttribute("data-branch")) ?? "";
  return branch ? `branchId=${branch}` : "";
}

let customerId = "";

test.beforeAll(async () => {
  test.setTimeout(240_000);
  await topUpHeadOfficeStock(await adminAccess(), loadState().branches.ho.id);
  const variants = await stockedVariants(6, 2, 2);
  await deliveredOrder([{ variantId: variants[0]!.id, quantity: 2 }]);
  await deliveredParcel();
  const top = await readReport<Customers>(`reports/customers?${q(FROM, TODAY)}`);
  customerId = top.topSpenders[0]?.customerId ?? "";
});

test.describe("range filter", () => {
  test("opens on the last 30 days, every section shows the API's numbers", async ({ page }) => {
    await signInAdmin(page, "/admin");
    await expect(page.getByTestId("range-part")).toBeVisible();
    await expect(page.getByTestId("preset-last30")).toHaveAttribute("aria-pressed", "true");
    const from = (await page.getByTestId("range-part").getAttribute("data-from"))!;
    const to = (await page.getByTestId("range-part").getAttribute("data-to"))!;
    expect(to).toBe(TODAY);
    const b = await branchParam(page);
    const sales = await readReport<Sales>(`reports/sales?${q(from, to, b)}`);
    await expect(page.getByTestId("range-trend-tiles")).toBeVisible({ timeout: 30_000 });
    await expect.poll(() => tileCents(page, "range-net")).toBe(cents(sales.totals.net));
    expect(digits(await page.getByTestId("tile-range-orders").textContent())).toBe(n(sales.totals.orders));
    // The top lists hold real rows from the orders created above.
    await expect(page.getByTestId("range-top-products-list").locator("li").first()).toBeVisible();
    await expect(page.getByTestId("range-top-categories-list").locator("li").first()).toBeVisible();
    await expect(page.getByTestId("range-zones-list").locator("li").first()).toBeVisible();
    const zones = await readReport<Sales>(`reports/sales?${q(from, to, "groupBy=zone" + (b ? "&" + b : ""))}`);
    const zoneRows = zones.rows.filter((r) => n(r.orders) > 0).length;
    expect(await page.getByTestId("range-zones-list").locator("li").count()).toBe(Math.min(8, zoneRows));
    await expect(page.getByTestId("range-zones-note")).toContainText("zone");
    await expect(page.getByText("Sample data")).toHaveCount(0);
  });

  test("a preset and a custom range live in the address, change the numbers and survive a reload", async ({ page }) => {
    await signInAdmin(page, "/admin");
    await expect(page.getByTestId("range-trend-tiles")).toBeVisible({ timeout: 30_000 });
    await expect(async () => {
      await page.getByTestId("preset-last7").click();
      await expect(page.getByTestId("preset-last7")).toHaveAttribute("aria-pressed", "true", { timeout: 3000 });
    }).toPass({ timeout: 20_000 });
    await expect(page).toHaveURL(new RegExp(`from=${FROM}&to=${TODAY}`));
    const b = await branchParam(page);
    const week = await readReport<Sales>(`reports/sales?${q(FROM, TODAY, b)}`);
    await expect.poll(() => tileCents(page, "range-net")).toBe(cents(week.totals.net));
    await page.reload();
    await expect(page).toHaveURL(new RegExp(`from=${FROM}&to=${TODAY}`));
    await expect(page.getByTestId("preset-last7")).toHaveAttribute("aria-pressed", "true");
    await expect.poll(() => tileCents(page, "range-net")).toBe(cents(week.totals.net));
    // A custom range from the date fields: a quiet past week with no orders.
    const from = day(TODAY, -400);
    const to = day(TODAY, -390);
    await page.locator("#rf-from").fill(from);
    await page.locator("#rf-to").fill(to);
    await expect(page).toHaveURL(new RegExp(`from=${from}&to=${to}`));
    await expect(page.getByTestId("preset-custom")).toHaveAttribute("aria-pressed", "true");
    const old = await readReport<Sales>(`reports/sales?${q(from, to, b)}`);
    await expect.poll(() => tileCents(page, "range-net")).toBe(cents(old.totals.net));
    await expect(page.getByTestId("range-top-products-list").or(page.getByTestId("range-top-products").getByRole("heading", { name: "No sales in this period" }))).toBeVisible();
  });

  test("a link with a range too large for daily grouping is explained and sends no request", async ({ page }) => {
    await signInAdmin(page, "/admin");
    const requests: string[] = [];
    page.on("request", (r) => /reports\/sales/.test(r.url()) && requests.push(r.url()));
    await page.goto(`/admin?from=2025-01-01&to=${TODAY}&groupBy=day`);
    await expect(page.getByTestId("range-problem")).toContainText("366 days");
    expect(requests).toEqual([]);
    await page.goto(`/admin?from=2025-01-01&to=${TODAY}`);
    await expect(page.getByTestId("range-trend-tiles")).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId("range-problem")).toHaveCount(0);
  });
});

test.describe("sales trend", () => {
  test("grouping day, week, month changes the chart rows and the address", async ({ page }) => {
    await signInAdmin(page, `/admin?${q(day(TODAY, -29), TODAY)}`);
    await expect(page.getByTestId("range-trend-tiles")).toBeVisible({ timeout: 30_000 });
    const b = await branchParam(page);
    const rowsOf = () => page.getByTestId("range-trend").locator(".sr-only table tbody tr");
    const week = await readReport<Sales>(`reports/sales?${q(day(TODAY, -29), TODAY, "groupBy=week" + (b ? "&" + b : ""))}`);
    await expect(async () => {
      await page.getByTestId("group-week").click();
      await expect(page.getByTestId("group-week")).toHaveAttribute("aria-pressed", "true", { timeout: 3000 });
    }).toPass({ timeout: 20_000 });
    await expect(page).toHaveURL(/groupBy=week/);
    await expect.poll(() => rowsOf().count()).toBe(week.rows.length);
    const month = await readReport<Sales>(`reports/sales?${q(day(TODAY, -29), TODAY, "groupBy=month" + (b ? "&" + b : ""))}`);
    await page.getByTestId("group-month").click();
    await expect(page).toHaveURL(/groupBy=month/);
    await expect.poll(() => rowsOf().count()).toBe(month.rows.length);
    await page.reload();
    await expect(page.getByTestId("group-month")).toHaveAttribute("aria-pressed", "true");
  });

  test("compare shows the previous period of the same length, its numbers and the change; it can be switched off", async ({ page }) => {
    await signInAdmin(page, `/admin?${q(FROM, TODAY)}`);
    await expect(page.getByTestId("range-trend-tiles")).toBeVisible({ timeout: 30_000 });
    const b = await branchParam(page);
    const prevFrom = day(FROM, -7);
    const prevTo = day(FROM, -1);
    const cur = await readReport<Sales>(`reports/sales?${q(FROM, TODAY, "groupBy=day" + (b ? "&" + b : ""))}`);
    const prev = await readReport<Sales>(`reports/sales?${q(prevFrom, prevTo, "groupBy=day" + (b ? "&" + b : ""))}`);
    await expect(page.getByTestId("range-previous")).toBeVisible();
    await expect(page.getByTestId("tile-range-net-change")).toBeVisible();
    // The hint carries the previous period's own figure; the delta is the API's two totals compared.
    const wasText = (await page.getByTestId("range-trend-tiles").getByText(/^Previous period: /).first().textContent()) ?? "";
    expect(Math.round(digits(wasText.replace("Previous period:", "")) * 100)).toBe(cents(prev.totals.net));
    const was = n(prev.totals.net);
    const shown = (await page.getByTestId("tile-range-net-change").textContent()) ?? "";
    if (was === 0) expect(shown).toContain("Nothing to compare with");
    else expect(Math.abs(digits(shown.replace(/[^0-9.-]/g, "")) - Math.round(((n(cur.totals.net) - was) / was) * 1000) / 10)).toBeLessThan(0.11);
    // Eight points per series, same count for both periods (matched by position).
    expect(await page.getByTestId("range-trend").locator(".sr-only table tbody tr").count()).toBe(cur.rows.length);
    await expect(page.getByTestId("range-trend").locator(".sr-only table thead th")).toHaveCount(3);
    await page.getByTestId("compare-toggle").click();
    await expect(page).toHaveURL(/compare=0/);
    await expect(page.getByTestId("range-previous")).toHaveCount(0);
    await expect(page.getByTestId("tile-range-net-change")).toHaveCount(0);
    await expect(page.getByTestId("range-trend").locator(".sr-only table thead th")).toHaveCount(2);
    await page.reload();
    await expect(page.getByTestId("compare-toggle")).toHaveAttribute("aria-pressed", "false");
  });
});

test.describe("sections with real data", () => {
  test("top customers, cash view and stock health match their reports", async ({ page }) => {
    await signInAdmin(page, `/admin?${q(FROM, TODAY)}`);
    await expect(page.getByTestId("range-cash-tiles")).toBeVisible({ timeout: 30_000 });
    const b = await branchParam(page);
    const extra = b ? "&" + b : "";
    const fin = await readReport<Finance>(`reports/finance?${q(FROM, TODAY, extra.slice(1))}`);
    await expect.poll(() => tileCents(page, "cash-cod-pending")).toBe(cents(fin.cod.pendingWithCouriers));
    expect(await tileCents(page, "cash-cod-collected")).toBe(cents(fin.cod.collected));
    expect(await tileCents(page, "cash-cod-settled")).toBe(cents(fin.cod.settled));
    expect(await tileCents(page, "cash-cod-transit")).toBe(cents(fin.cod.inTransit));
    expect(await tileCents(page, "cash-refunds")).toBe(cents(fin.totals.refunds));
    await expect(page.getByTestId("tile-cash-online")).toBeVisible();
    await expect(page.getByTestId("range-cash-gap")).toContainText("refunds due");
    // Stock health.
    const inv = await readReport<Inventory>(`reports/inventory?${q(day(TODAY, -29), TODAY, "limit=100" + extra)}`);
    await expect(page.getByTestId("range-stock-tiles")).toBeVisible({ timeout: 30_000 });
    expect(digits(await page.getByTestId("tile-stock-low").textContent())).toBe(n(inv.lowStockCount));
    expect(digits(await page.getByTestId("tile-stock-slow").textContent())).toBe(n(inv.deadStockCount));
    await expect(page.getByTestId("tile-stock-out")).toBeVisible();
    await expect(page.getByTestId("range-stock-gap")).toContainText("reorder level");
    // Top customers: the first row is the API's top spender (masked name or mobile as sent).
    const top = await readReport<Customers>(`reports/customers?${q(FROM, TODAY, "limit=20" + extra)}`);
    if (top.topSpenders.length > 0) {
      const first = top.topSpenders[0]!;
      await expect(page.getByTestId("range-top-customers-list").locator("li").first()).toContainText(first.name || first.mobile);
    } else await expect(page.getByTestId("range-top-customers")).toContainText("No customer orders");
  });

  test("the status strip and the queue show the dashboard feed, including failed deliveries", async ({ page }) => {
    await signInAdmin(page, "/admin");
    await expect(page.getByTestId("status-strip")).toBeVisible({ timeout: 30_000 });
    const b = await branchParam(page);
    const api = await readReport<{ ordersByStatus: { status: string; count: number | string }[]; pending: { toPack: number | string } }>(`reports/dashboard${b ? "?" + b : ""}`);
    const count = (s: string) => n(api.ordersByStatus.find((x) => x.status === s)?.count);
    const strip = (k: string) => page.getByTestId(`strip-${k}`).getByTestId("strip-value");
    await expect(strip("toPack")).toHaveText(String(n(api.pending.toPack)));
    await expect(strip("delivered")).toHaveText(String(count("Delivered")));
    await expect(strip("cancelled")).toHaveText(String(count("Cancelled")));
    await expect(strip("inDelivery")).toHaveText(String(count("HandedToCourier") + count("InTransit") + count("OutForDelivery")));
    await expect(strip("returned")).toHaveText(String(count("Returned") + count("ReturnInProgress")));
    await expect(page.getByTestId("action-count-deliveryFailed")).toHaveText(String(count("DeliveryFailed")));
    // Counts the feed does not carry are said so and point to the screens.
    await expect(page.getByTestId("action-screens")).toContainText("no counts");
  });
});

test.describe("links to the lists behind the numbers", () => {
  test("cards go to the orders list with the status and the day range the list accepts", async ({ page }) => {
    await signInAdmin(page, `/admin?${q(FROM, TODAY)}`);
    await expect(page.getByTestId("status-strip")).toBeVisible({ timeout: 30_000 });
    await page.getByTestId("strip-delivered").getByRole("link").click();
    await expect(page).toHaveURL(/\/admin\/orders\?status=Delivered$/);
    await expect(page.locator("#orders-from")).toHaveValue("");
    await page.goBack();
    await page.getByTestId("action-deliveryFailed").click();
    await expect(page).toHaveURL(/\/admin\/orders\?status=DeliveryFailed/);
    await page.goBack();
    await page.getByTestId("range-orders-link").click();
    await expect(page).toHaveURL(new RegExp(`/admin/orders\\?from=${FROM}&to=${TODAY}`));
    await expect(page.locator("#orders-from")).toHaveValue(FROM);
    await expect(page.locator("#orders-to")).toHaveValue(TODAY);
  });

  test("cash, stock and customer links go to their screens", async ({ page }) => {
    await signInAdmin(page, `/admin?${q(FROM, TODAY)}`);
    await expect(page.getByTestId("range-stock-tiles")).toBeVisible({ timeout: 30_000 });
    await page.getByTestId("range-stock-link").click();
    await expect(page).toHaveURL(/\/admin\/inventory$/);
    await page.goBack();
    await expect(page.getByTestId("range-cash-tiles")).toBeVisible({ timeout: 30_000 });
    await page.getByTestId("range-cash").getByRole("link", { name: "COD settlements" }).click();
    await expect(page).toHaveURL(/\/admin\/cod$/);
    await page.goBack();
    await page.getByTestId("action-screens").getByRole("link").first().click();
    await expect(page).toHaveURL(/\/admin\/(payments|shipments)$/);
    if (customerId) {
      await page.goto(`/admin?${q(FROM, TODAY)}`);
      await page.getByTestId("range-top-customers-list").getByRole("link").first().click();
      await expect(page).toHaveURL(/\/admin\/customers\/[0-9a-f-]{36}$/);
    }
  });
});

test.describe("permissions", () => {
  test("a role with the dashboard and sales reports only sees those sections, with no error for the rest", async ({ page }) => {
    const limited = await staffInBranch(["reports.dashboard.view", "reports.sales.view"]);
    await signInAs(page, limited, "/admin");
    await expect(page.getByTestId("dashboard")).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId("range-trend-tiles")).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId("range-top-products-list")).toBeVisible();
    await expect(page.getByTestId("range-zones")).toBeVisible();
    for (const hidden of ["range-cash", "range-stock", "range-top-customers"]) await expect(page.getByTestId(hidden)).toHaveCount(0);
    await expect(page.locator("[data-testid$='-error']")).toHaveCount(0);
    // No orders permission: cards are shown but are not links.
    await expect(page.getByTestId("strip-delivered").getByRole("link")).toHaveCount(0);
    await expect(page.getByTestId("range-orders-link")).toHaveCount(0);
  });

  test("a role with the dashboard only gets the live cards and a short note instead of period figures", async ({ page }) => {
    const limited = await staffInBranch(["reports.dashboard.view"]);
    await signInAs(page, limited, "/admin");
    await expect(page.getByTestId("dashboard")).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId("range-none")).toBeVisible();
    await expect(page.getByTestId("range-trend")).toHaveCount(0);
    await expect(page.getByTestId("kpi-orders")).toBeVisible();
  });

  test("a role with finance but not sales sees the cash view without the online line, and the inventory view with stock", async ({ page }) => {
    const limited = await staffInBranch(["reports.dashboard.view", "reports.finance.view", "reports.inventory.view"]);
    await signInAs(page, limited, "/admin");
    await expect(page.getByTestId("range-cash-tiles")).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId("tile-cash-online")).toHaveCount(0);
    await expect(page.getByTestId("tile-cash-cod-pending")).toBeVisible();
    await expect(page.getByTestId("range-stock-tiles")).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId("range-trend")).toHaveCount(0);
    await expect(page.locator("[data-testid$='-error']")).toHaveCount(0);
  });
});
