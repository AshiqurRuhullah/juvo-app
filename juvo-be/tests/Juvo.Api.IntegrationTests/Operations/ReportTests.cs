using System.Net;
using System.Text.Json;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Application.Abstractions;
using Juvo.Application.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Operations;

/// <summary>
/// The Step 11A gate: every report total is compared with a calculation written out by hand from the data set in <see cref="ReportSeeder"/> (read its comment first). The data set is
/// built once per class; the tests only read it.
/// </summary>
public class ReportTests(OpsFactory factory) : IClassFixture<OpsFactory>
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static ReportSeed? _seed;

    private readonly OpsHelper _o = new(new OrderHelper(new StoreHelper(factory)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<ReportSeed> SeedAsync()
    {
        await Gate.WaitAsync(Ct);
        try
        {
            return _seed ??= await ReportSeeder.BuildAsync(_o);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static readonly string[] AllView =
    [
        "reports.dashboard.view", "reports.sales.view", "reports.orders.view", "reports.inventory.view", "reports.finance.view", "reports.courier.view", "reports.customers.view", "accounting.pnl.view",
    ];

    private static readonly string[] AllExport =
    [
        "reports.sales.export", "reports.orders.export", "reports.inventory.export", "reports.finance.export", "reports.courier.export", "reports.customers.export", "accounting.export.run",
    ];

    /// <summary>Staff with every report permission: in one branch, or (branch null) with global scope over all branches. Cost visibility is chosen.</summary>
    private async Task<string> ScopedAsync(ReportSeed s, Guid? branch, bool cost, params string[] extra)
    {
        string[] permissions = [.. AllView, .. AllExport, .. extra, .. cost ? new[] { "catalog.product.view-cost" } : []];
        if (branch is not null)
        {
            return await _o.StaffAsync(s.Admin, branch, permissions);
        }

        var role = await _o.Api.CreateRoleAsync(s.Admin, StoreHelper.Unique("Role"), permissions);
        var user = await _o.Api.CreateUserAsync(s.Admin, $"{StoreHelper.Unique("gl")}@example.com", "Global Staff", true, (await _o.HoAsync(), role));
        return (await _o.Api.LoginAsync(user.Email, user.Password)).Access;
    }

    private static decimal D(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    private static int I(JsonElement e, string name) => e.GetProperty(name).GetInt32();

    private static JsonElement Row(JsonElement report, string label) => report.GetProperty("rows").EnumerateArray().Single(r => r.GetProperty("label").GetString() == label);

    private static void Sales(JsonElement r, int orders, int units, decimal gross, decimal discount, decimal delivery)
    {
        Assert.Equal((orders, units, gross, discount, gross - discount, delivery, gross - discount + delivery), (I(r, "orders"), I(r, "units"), D(r, "gross"), D(r, "discount"), D(r, "net"), D(r, "delivery"), D(r, "total")));
    }

    // ---- sales (FR-P01) -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Sales_totals_and_every_grouping_match_the_hand_calculation()
    {
        var s = await SeedAsync();

        // Orders O1, O2, O4, O5, O6 count; O3 is cancelled and O0 is outside the range.
        // orders 5, units 2+3+1+4+1 = 11, gross 2000+1500+1000+2000+500 = 7000, discount 100+50 = 150, delivery 60+100+0+60+60 = 280.
        var byDay = await _o.GetAsync($"/api/v1/reports/sales?{s.Range}", s.Admin);
        Sales(byDay.GetProperty("totals"), 5, 11, 7000m, 150m, 280m);
        Assert.Equal(10, byDay.GetProperty("rows").GetArrayLength()); // ten days, empty ones included
        Assert.Equal("day", byDay.GetProperty("filters").GetProperty("groupBy").GetString());
        Assert.True(byDay.TryGetProperty("generatedAt", out _));
        Sales(Row(byDay, s.Iso(5)), 1, 2, 2000m, 100m, 60m);
        Sales(Row(byDay, s.Iso(4)), 1, 3, 1500m, 50m, 100m);
        Sales(Row(byDay, s.Iso(3)), 0, 0, 0m, 0m, 0m);
        Sales(Row(byDay, s.Iso(0)), 1, 4, 2000m, 0m, 60m);

        // Channel: Web = O1, O5, O6; Facebook = O2; WalkIn = O4.
        var channel = await _o.GetAsync($"/api/v1/reports/sales?{s.Range}&groupBy=channel", s.Admin);
        Sales(Row(channel, "Web"), 3, 7, 4500m, 100m, 180m);
        Sales(Row(channel, "Facebook"), 1, 3, 1500m, 50m, 100m);
        Sales(Row(channel, "WalkIn"), 1, 1, 1000m, 0m, 0m);
        Sales(channel.GetProperty("totals"), 5, 11, 7000m, 150m, 280m);

        // Payment method: COD = O1, O4, O5, O6; BKASH = O2. Zone: DHAKA the same four, OUTSIDE = O2.
        var payment = await _o.GetAsync($"/api/v1/reports/sales?{s.Range}&groupBy=payment", s.Admin);
        Sales(Row(payment, "COD"), 4, 8, 5500m, 100m, 180m);
        Sales(Row(payment, "BKASH"), 1, 3, 1500m, 50m, 100m);
        var zone = await _o.GetAsync($"/api/v1/reports/sales?{s.Range}&groupBy=zone", s.Admin);
        Sales(Row(zone, "DHAKA"), 4, 8, 5500m, 100m, 180m);
        Sales(Row(zone, "OUTSIDE"), 1, 3, 1500m, 50m, 100m);

        // Product and category: product one = O1 (2 x 1000, discount share 100) and O4 (1 x 1000); product two = O2 (3 x 500, share 50), O5 (4 x 500), O6 (1 x 500). Delivery is not shared (0).
        foreach (var by in new[] { "product", "category" })
        {
            var rows = await _o.GetAsync($"/api/v1/reports/sales?{s.Range}&groupBy={by}", s.Admin);
            var one = rows.GetProperty("rows").EnumerateArray().Single(r => D(r, "gross") == 3000m);
            Sales(one, 2, 3, 3000m, 100m, 0m);
            var two = rows.GetProperty("rows").EnumerateArray().Single(r => D(r, "gross") == 4000m);
            Sales(two, 3, 8, 4000m, 50m, 0m);
            Sales(rows.GetProperty("totals"), 5, 11, 7000m, 150m, 280m); // the totals row is exact
        }

        Assert.Equal("Report Tee Two", (await _o.GetAsync($"/api/v1/reports/sales?{s.Range}&groupBy=product", s.Admin)).GetProperty("rows")[0].GetProperty("label").GetString()); // sorted by total, the bigger first
        Assert.Equal("Report Cat Two", (await _o.GetAsync($"/api/v1/reports/sales?{s.Range}&groupBy=category", s.Admin)).GetProperty("rows")[0].GetProperty("label").GetString());

        // Size: M = O1, O4 (product one M) and O2, O6 (product two M); L = O5.
        var size = await _o.GetAsync($"/api/v1/reports/sales?{s.Range}&groupBy=size", s.Admin);
        Sales(Row(size, "M"), 4, 7, 5000m, 150m, 0m);
        Sales(Row(size, "L"), 1, 4, 2000m, 0m, 0m);
        var colour = await _o.GetAsync($"/api/v1/reports/sales?{s.Range}&groupBy=color", s.Admin);
        Sales(Row(colour, "Black"), 2, 3, 3000m, 100m, 0m);
        Sales(Row(colour, "Red"), 3, 8, 4000m, 50m, 0m);

        // Week and month: whatever the buckets, they add up to the totals.
        foreach (var by in new[] { "week", "month" })
        {
            var rows = await _o.GetAsync($"/api/v1/reports/sales?{s.Range}&groupBy={by}", s.Admin);
            var all = rows.GetProperty("rows").EnumerateArray().ToList();
            Assert.Equal((5, 11, 7000m, 150m, 280m), (all.Sum(r => I(r, "orders")), all.Sum(r => I(r, "units")), all.Sum(r => D(r, "gross")), all.Sum(r => D(r, "discount")), all.Sum(r => D(r, "delivery"))));
        }

        // The row cap shows when rows are cut.
        var capped = await _o.GetAsync($"/api/v1/reports/sales?{s.Range}&groupBy=channel&limit=1", s.Admin);
        Assert.Equal(1, capped.GetProperty("rows").GetArrayLength());
        Assert.True(capped.GetProperty("truncated").GetBoolean());
        Sales(capped.GetProperty("totals"), 5, 11, 7000m, 150m, 280m);
    }

    // ---- branches ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_branch_user_sees_only_their_branch_and_a_global_user_sees_the_sum()
    {
        var s = await SeedAsync();
        var a = await ScopedAsync(s, s.A, true);
        var b = await ScopedAsync(s, s.B, true);

        // Branch A: O1, O2, O5 = orders 3, units 9, gross 5500, discount 150, delivery 220. Branch B: O4, O6 = orders 2, units 2, gross 1500, delivery 60.
        Sales((await _o.GetAsync($"/api/v1/reports/sales?{s.Range}", a)).GetProperty("totals"), 3, 9, 5500m, 150m, 220m);
        Sales((await _o.GetAsync($"/api/v1/reports/sales?{s.Range}", b)).GetProperty("totals"), 2, 2, 1500m, 0m, 60m);
        Sales((await _o.GetAsync($"/api/v1/reports/sales?{s.Range}", s.Admin)).GetProperty("totals"), 5, 11, 7000m, 150m, 280m);

        // Asking for another branch gives nothing, never its numbers.
        var other = await _o.GetAsync($"/api/v1/reports/sales?{s.BranchQuery(s.B)}", a);
        Sales(other.GetProperty("totals"), 0, 0, 0m, 0m, 0m);

        // A global user can narrow to one branch; the filters say which.
        var narrowed = await _o.GetAsync($"/api/v1/reports/sales?{s.BranchQuery(s.A)}", s.Admin);
        Sales(narrowed.GetProperty("totals"), 3, 9, 5500m, 150m, 220m);
        Assert.False(narrowed.GetProperty("filters").GetProperty("consolidated").GetBoolean());
        Assert.Equal(["RPA"], narrowed.GetProperty("filters").GetProperty("branchCodes").EnumerateArray().Select(c => c.GetString()).ToArray());
        Assert.True((await _o.GetAsync($"/api/v1/reports/sales?{s.Range}", s.Admin)).GetProperty("filters").GetProperty("consolidated").GetBoolean());

        // The same holds for the other reports.
        Assert.Equal(3, I((await _o.GetAsync($"/api/v1/reports/orders?{s.Range}", a)).GetProperty("funnel")[0], "count")); // O1, O2, O5 placed in A (O3 is in B)
        Assert.Equal(3, (await _o.GetAsync($"/api/v1/reports/courier?{s.Range}", a)).GetProperty("totals").GetProperty("parcels").GetInt32()); // parcels of O1, O2, O5
        Assert.Equal(2, I((await _o.GetAsync($"/api/v1/reports/inventory?{s.Range}", b)).GetProperty("summary"), "variants")); // B holds two stock rows
    }

    // ---- order funnel (FR-P02) -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_order_funnel_counts_stages_rates_and_reasons()
    {
        var s = await SeedAsync();
        var r = await _o.GetAsync($"/api/v1/reports/orders?{s.Range}", s.Admin);
        var stages = r.GetProperty("funnel").EnumerateArray().ToDictionary(x => x.GetProperty("stage").GetString()!);

        // Placed O1..O6 = 6. Confirmed O1, O2, O5, O6 = 4. Packed O1, O2, O6 = 3. Shipped = delivered = O1, O2, O6 = 3. Cancelled O3 = 1. Returned 0.
        Assert.Equal(6, I(r, "placed"));
        Assert.Equal([6, 4, 3, 3, 3, 1, 0], new[] { "Placed", "Confirmed", "Packed", "Shipped", "Delivered", "Cancelled", "Returned" }.Select(k => I(stages[k], "count")).ToArray());
        Assert.Equal([100.00m, 66.67m, 50.00m, 50.00m, 50.00m, 16.67m, 0m], new[] { "Placed", "Confirmed", "Packed", "Shipped", "Delivered", "Cancelled", "Returned" }.Select(k => D(stages[k], "rateOfPlaced")).ToArray());
        Assert.Equal(66.67m, D(stages["Confirmed"], "rateOfPrevious"));
        Assert.Equal(75.00m, D(stages["Packed"], "rateOfPrevious")); // 3 of 4
        Assert.Equal(100.00m, D(stages["Delivered"], "rateOfPrevious"));
        Assert.Equal(16.67m, D(r, "cancelledRate"));

        var cancel = r.GetProperty("cancellationReasons");
        Assert.Equal(1, cancel.GetArrayLength());
        Assert.Equal(("Customer changed mind", 1, 100.00m), (cancel[0].GetProperty("label").GetString(), I(cancel[0], "count"), D(cancel[0], "share")));

        // Returns: DAMAGED_ITEM twice (seller fault), SIZE_FIT once (customer fault).
        var returns = r.GetProperty("returnReasons");
        Assert.Equal(("DAMAGED_ITEM", 2, 66.67m, "Seller"), (returns[0].GetProperty("code").GetString(), I(returns[0], "count"), D(returns[0], "share"), returns[0].GetProperty("fault").GetString()));
        Assert.Equal(("SIZE_FIT", 1, 33.33m, "Customer"), (returns[1].GetProperty("code").GetString(), I(returns[1], "count"), D(returns[1], "share"), returns[1].GetProperty("fault").GetString()));
    }

    // ---- inventory (FR-P03) ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Inventory_values_low_stock_movements_and_dead_stock_match_and_cost_follows_the_permission()
    {
        var s = await SeedAsync();
        var r = await _o.GetAsync($"/api/v1/reports/inventory?{s.Range}", s.Admin);
        var sum = r.GetProperty("summary");

        // Stock rows: A product one M 20 (reserved 2), A product one L 10 (from the purchase), A product two M 3 (reserved 1), B product one M 7, B product two M 4. Units 44, reserved 3.
        // Value at price: product one 37 x 1000 = 37000, product two 7 x 500 = 3500, together 40500. At cost: 37 x 600 + 7 x 300 = 22200 + 2100 = 24300.
        Assert.Equal((5, 44, 3, 41, 40500m, 24300m, 0), (I(sum, "variants"), I(sum, "onHand"), I(sum, "reserved"), I(sum, "available"), D(sum, "valueAtPrice"), D(sum, "valueAtCost"), I(sum, "unitsWithoutCost")));
        var branches = r.GetProperty("byBranch").EnumerateArray().ToDictionary(x => x.GetProperty("branchCode").GetString()!);
        Assert.Equal((33, 31500m, 18900m), (I(branches["RPA"], "onHand"), D(branches["RPA"], "valueAtPrice"), D(branches["RPA"], "valueAtCost")));
        Assert.Equal((11, 9000m, 5400m), (I(branches["RPB"], "onHand"), D(branches["RPB"], "valueAtPrice"), D(branches["RPB"], "valueAtCost")));

        // Low stock: A product two M (available 2 <= 5) and B product two M (available 4 <= 4).
        Assert.Equal(2, I(r, "lowStockCount"));
        var low = r.GetProperty("lowStock").EnumerateArray().ToList();
        Assert.Equal([2, 4], low.Select(x => I(x, "available")).ToArray());
        Assert.Equal(["RPA", "RPB"], low.Select(x => x.GetProperty("branchCode").GetString()).ToArray());

        // Movements created today: purchase receipt +10, opening stock +20 and +7, damage -2.
        var moves = r.GetProperty("movements").EnumerateArray().ToDictionary(x => x.GetProperty("reason").GetString()!);
        Assert.Equal((1, 10, 0), (I(moves["PurchaseReceipt"], "movements"), I(moves["PurchaseReceipt"], "in"), I(moves["PurchaseReceipt"], "out")));
        Assert.Equal((2, 27, 0, 27), (I(moves["OpeningStock"], "movements"), I(moves["OpeningStock"], "in"), I(moves["OpeningStock"], "out"), I(moves["OpeningStock"], "net")));
        Assert.Equal((1, 0, 2, -2), (I(moves["Damage"], "movements"), I(moves["Damage"], "in"), I(moves["Damage"], "out"), I(moves["Damage"], "net")));

        // Dead stock (60 days): everything on hand sold in the last 60 days except product one L (10 units, 10000 at price, 6000 at cost), which never sold.
        Assert.Equal((1, 10000m), (I(r, "deadStockCount"), D(r, "deadStockValueAtPrice")));
        Assert.Equal((10, 6000m), (I(r.GetProperty("deadStock")[0], "onHand"), D(r.GetProperty("deadStock")[0], "valueAtCost")));
        Assert.Equal(JsonValueKind.Null, r.GetProperty("deadStock")[0].GetProperty("lastSoldAt").ValueKind);

        // Without the cost permission every cost figure is hidden (null), value at price stays.
        var noCost = await ScopedAsync(s, null, false);
        var hidden = await _o.GetAsync($"/api/v1/reports/inventory?{s.Range}", noCost);
        Assert.True(hidden.GetProperty("costHidden").GetBoolean());
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("summary").GetProperty("valueAtCost").ValueKind);
        Assert.Equal(40500m, D(hidden.GetProperty("summary"), "valueAtPrice"));
        Assert.Equal(JsonValueKind.Null, hidden.GetProperty("deadStock")[0].GetProperty("valueAtCost").ValueKind);
        Assert.All(hidden.GetProperty("byBranch").EnumerateArray(), x => Assert.Equal(JsonValueKind.Null, x.GetProperty("valueAtCost").ValueKind));
    }

    // ---- finance (FR-P04) and profit and loss (FR-X04) -----------------------------------------------------------

    [Fact]
    public async Task Profit_and_loss_matches_the_hand_calculation_consolidated_and_per_branch()
    {
        var s = await SeedAsync();

        // Gross 7000, discounts 150, refunds 200 (O2), net revenue 6650, delivery collected 280.
        // Cost of goods: O1 2 x 600 + O2 3 x 300 + O5 4 x 300 + O6 1 x 300 = 1200 + 900 + 1200 + 300 = 3600 (O4 has no cost recorded: 1 line missing). Gross profit 6650 - 3600 = 3050.
        // Courier charges 50 + 70 + 60 + 55 + 45 = 280 (the cancelled parcel of 40 does not count). Expenses 400 + 150 = 550 (the draft and the submitted one do not count). Purchases received 10 x 550 = 5500.
        // Net profit = 6650 + 280 - 3600 - 280 - 0 - 550 = 2500.
        var all = await _o.GetAsync($"/api/v1/accounting/pnl?{s.Range}&groupBy=day", s.Admin);
        var t = all.GetProperty("total");
        Assert.Equal((5, 7000m, 150m, 200m, 6650m, 280m, 3600m, 1, 3050m, 280m, 0m, 550m, 5500m, 2500m),
            (I(t, "orders"), D(t, "grossSales"), D(t, "discounts"), D(t, "refunds"), D(t, "netRevenue"), D(t, "deliveryCollected"), D(t, "costOfGoods"), I(t, "costMissingLines"), D(t, "grossProfit"), D(t, "courierCharges"),
                D(t, "gatewayFees"), D(t, "expenses"), D(t, "purchasesReceived"), D(t, "netProfit")));
        Assert.False(t.GetProperty("gatewayFeesRecorded").GetBoolean()); // honest: the system does not record gateway fees
        Assert.False(all.GetProperty("costHidden").GetBoolean());
        Assert.Equal(["Rent", "Packaging"], all.GetProperty("expensesByCategory").EnumerateArray().Select(c => c.GetProperty("category").GetString()).ToArray());
        Assert.Equal([400m, 150m], all.GetProperty("expensesByCategory").EnumerateArray().Select(c => D(c, "amount")).ToArray());
        Assert.NotEmpty(all.GetProperty("notes").EnumerateArray());

        // Single days of the series: T-4 has O2 (gross 1500, discount 50), the refund is on T-2, the purchase on T.
        var days = all.GetProperty("series").EnumerateArray().ToDictionary(x => x.GetProperty("label").GetString()!);
        Assert.Equal((1500m, 50m, 0m, 900m, 70m), (D(days[s.Iso(4)], "grossSales"), D(days[s.Iso(4)], "discounts"), D(days[s.Iso(4)], "refunds"), D(days[s.Iso(4)], "costOfGoods"), D(days[s.Iso(4)], "courierCharges")));
        Assert.Equal(200m, D(days[s.Iso(2)], "refunds"));
        Assert.Equal(5500m, D(days[s.Iso(0)], "purchasesReceived"));

        // Every grouping adds up to the total.
        foreach (var by in new[] { "day", "week", "month" })
        {
            var series = (await _o.GetAsync($"/api/v1/accounting/pnl?{s.Range}&groupBy={by}", s.Admin)).GetProperty("series").EnumerateArray().ToList();
            Assert.Equal((7000m, 200m, 3600m, 280m, 550m, 5500m, 2500m), (series.Sum(x => D(x, "grossSales")), series.Sum(x => D(x, "refunds")), series.Sum(x => D(x, "costOfGoods")), series.Sum(x => D(x, "courierCharges")),
                series.Sum(x => D(x, "expenses")), series.Sum(x => D(x, "purchasesReceived")), series.Sum(x => D(x, "netProfit"))));
        }

        // Branch A: gross 5500, discounts 150, refunds 200, net 5150, delivery 220, cost 1200 + 900 + 1200 = 3300, courier 50 + 70 + 55 = 175, expenses 400, purchases 5500, net profit 5150 + 220 - 3300 - 175 - 400 = 1495.
        var a = (await _o.GetAsync($"/api/v1/accounting/pnl?{s.BranchQuery(s.A)}", s.Admin)).GetProperty("total");
        Assert.Equal((5500m, 5150m, 220m, 3300m, 175m, 400m, 5500m, 1495m), (D(a, "grossSales"), D(a, "netRevenue"), D(a, "deliveryCollected"), D(a, "costOfGoods"), D(a, "courierCharges"), D(a, "expenses"), D(a, "purchasesReceived"), D(a, "netProfit")));

        // Branch B: gross 1500, net 1500, delivery 60, cost 300 (O4 missing), courier 60 + 45 = 105, expenses 150, net profit 1500 + 60 - 300 - 105 - 150 = 1005. A and B add up to the whole.
        var b = (await _o.GetAsync($"/api/v1/accounting/pnl?{s.BranchQuery(s.B)}", s.Admin)).GetProperty("total");
        Assert.Equal((1500m, 1500m, 60m, 300m, 105m, 150m, 0m, 1005m, 1), (D(b, "grossSales"), D(b, "netRevenue"), D(b, "deliveryCollected"), D(b, "costOfGoods"), D(b, "courierCharges"), D(b, "expenses"), D(b, "purchasesReceived"), D(b, "netProfit"), I(b, "costMissingLines")));
        Assert.Equal(D(t, "netProfit"), D(a, "netProfit") + D(b, "netProfit"));

        // A branch-scoped accountant sees only their own branch even when asking for the other.
        var scopedB = await ScopedAsync(s, s.B, true);
        Assert.Equal(1005m, D((await _o.GetAsync($"/api/v1/accounting/pnl?{s.Range}", scopedB)).GetProperty("total"), "netProfit"));
        Assert.Equal(0m, D((await _o.GetAsync($"/api/v1/accounting/pnl?{s.BranchQuery(s.A)}", scopedB)).GetProperty("total"), "grossSales"));
    }

    [Fact]
    public async Task Cost_and_profit_are_hidden_from_a_role_without_the_cost_permission()
    {
        var s = await SeedAsync();
        var noCost = await ScopedAsync(s, null, false);
        var pnl = await _o.GetAsync($"/api/v1/accounting/pnl?{s.Range}", noCost);
        Assert.True(pnl.GetProperty("costHidden").GetBoolean());
        var t = pnl.GetProperty("total");
        Assert.Equal(JsonValueKind.Null, t.GetProperty("costOfGoods").ValueKind);
        Assert.Equal(JsonValueKind.Null, t.GetProperty("grossProfit").ValueKind);
        Assert.Equal(JsonValueKind.Null, t.GetProperty("netProfit").ValueKind);
        Assert.Equal(6650m, D(t, "netRevenue")); // revenue is not cost
        Assert.All(pnl.GetProperty("series").EnumerateArray(), x => Assert.Equal(JsonValueKind.Null, x.GetProperty("netProfit").ValueKind));

        var finance = await _o.GetAsync($"/api/v1/reports/finance?{s.Range}", noCost);
        Assert.True(finance.GetProperty("costHidden").GetBoolean());
        Assert.Equal(JsonValueKind.Null, finance.GetProperty("totals").GetProperty("costOfGoods").ValueKind);
        Assert.Equal(JsonValueKind.Null, finance.GetProperty("totals").GetProperty("grossProfit").ValueKind);
        Assert.Equal(6650m, D(finance.GetProperty("totals"), "netRevenue"));
    }

    [Fact]
    public async Task The_finance_report_shows_delivery_collected_against_paid_and_cod_pending_against_settled()
    {
        var s = await SeedAsync();
        var r = await _o.GetAsync($"/api/v1/reports/finance?{s.Range}", s.Admin);
        var t = r.GetProperty("totals");
        Assert.Equal((7000m, 150m, 200m, 6650m, 280m, 280m, 0m, 3600m, 3050m), (D(t, "grossSales"), D(t, "discounts"), D(t, "refunds"), D(t, "netRevenue"), D(t, "deliveryCollected"), D(t, "courierCharges"), D(t, "gatewayFees"), D(t, "costOfGoods"), D(t, "grossProfit")));
        Assert.Equal(0m, D(r, "deliveryBalance")); // 280 collected - 280 paid

        // COD: collected 1960 (O1) + 560 (O6) = 2520; settled 1960 (settlement of O1); pending with couriers 560 (O6); in transit 2060 (O5).
        var cod = r.GetProperty("cod");
        Assert.Equal((2520m, 1960m, 560m, 2060m), (D(cod, "collected"), D(cod, "settled"), D(cod, "pendingWithCouriers"), D(cod, "inTransit")));
        Assert.Equal(10, r.GetProperty("rows").GetArrayLength());
    }

    // ---- couriers (FR-P05) -------------------------------------------------------------------------------------

    [Fact]
    public async Task Courier_performance_matches_the_hand_calculation()
    {
        var s = await SeedAsync();
        var r = await _o.GetAsync($"/api/v1/reports/courier?{s.Range}", s.Admin);
        var rows = r.GetProperty("rows").EnumerateArray().ToDictionary(x => x.GetProperty("courier").GetString()!);

        // Courier X: parcels O1 (delivered after 48 h) and O5 (in transit): delivered 50.00 %, average 48.00 h, cost 50 + 55 = 105, 52.50 per parcel, collected 1960, settled 1960, pending 0.
        var x = rows["Courier X"];
        Assert.Equal((2, 1, 50.00m, 48.00m, 0, 0.00m, 0, 105m, 52.50m, 1960m, 1960m, 0m),
            (I(x, "parcels"), I(x, "delivered"), D(x, "deliveredRate"), D(x, "avgDeliveryHours"), I(x, "failed"), D(x, "failedRate"), I(x, "returned"), D(x, "totalCost"), D(x, "costPerParcel"), D(x, "codCollected"), D(x, "codSettled"), D(x, "codPending")));

        // Courier Y: parcels O2 (24 h), O4 (delivery failed), O6 (36 h): delivered 2 = 66.67 %, failed 1 = 33.33 %, average 30.00 h, cost 70 + 60 + 45 = 175, 58.33 per parcel, collected 560, settled 0, pending 560.
        var y = rows["Courier Y"];
        Assert.Equal((3, 2, 66.67m, 30.00m, 1, 33.33m, 0, 175m, 58.33m, 560m, 0m, 560m),
            (I(y, "parcels"), I(y, "delivered"), D(y, "deliveredRate"), D(y, "avgDeliveryHours"), I(y, "failed"), D(y, "failedRate"), I(y, "returned"), D(y, "totalCost"), D(y, "costPerParcel"), D(y, "codCollected"), D(y, "codSettled"), D(y, "codPending")));

        // Total: 5 parcels, 3 delivered (60.00 %), 1 failed (20.00 %), average (48 + 24 + 36) / 3 = 36.00 h, cost 280 = 56.00 per parcel, collected 2520, settled 1960, pending 560.
        var t = r.GetProperty("totals");
        Assert.Equal((5, 3, 60.00m, 36.00m, 1, 20.00m, 280m, 56.00m, 2520m, 1960m, 560m),
            (I(t, "parcels"), I(t, "delivered"), D(t, "deliveredRate"), D(t, "avgDeliveryHours"), I(t, "failed"), D(t, "failedRate"), D(t, "totalCost"), D(t, "costPerParcel"), D(t, "codCollected"), D(t, "codSettled"), D(t, "codPending")));
    }

    // ---- customers (FR-P06) --------------------------------------------------------------------------------------

    [Fact]
    public async Task The_customer_report_splits_new_from_returning_and_lists_top_spenders()
    {
        var s = await SeedAsync();
        var r = await _o.GetAsync($"/api/v1/reports/customers?{s.Range}", s.Admin);

        // Customers with a counted order: one (O1, O6), two (O2) and three (O4, O5). Customer two already ordered on T-30 (O0): returning. New: one and three.
        // Orders from new = O1, O6, O4, O5 = 4, grand totals 1960 + 560 + 1000 + 2060 = 5580. Returning: O2 = 1550.
        Assert.Equal((3, 2, 1, 4, 1, 5580m, 1550m), (I(r, "customers"), I(r, "newCustomers"), I(r, "returningCustomers"), I(r, "ordersFromNew"), I(r, "ordersFromReturning"), D(r, "revenueFromNew"), D(r, "revenueFromReturning")));

        // Top spenders: customer three 1000 + 2060 = 3060, customer one 1960 + 560 = 2520, customer two 1550.
        var top = r.GetProperty("topSpenders").EnumerateArray().ToList();
        Assert.Equal([3060m, 2520m, 1550m], top.Select(x => D(x, "spent")).ToArray());
        Assert.Equal([2, 2, 1], top.Select(x => I(x, "orders")).ToArray());
        Assert.Equal("Customer Three", top[0].GetProperty("name").GetString());

        // Conversion: three customers were created now: one Full, one Quick, one Guest: 2 of 3 became profiles = 66.67 %.
        Assert.Equal((3, 2, 66.67m), (I(r, "guestsCreated"), I(r, "convertedToProfile"), D(r, "conversionRate")));
    }

    // ---- dashboard -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_dashboard_numbers_are_cheap_and_correct()
    {
        var s = await SeedAsync();
        var r = await _o.GetAsync("/api/v1/reports/dashboard", s.Admin);
        Assert.Equal(14, r.GetProperty("series").GetArrayLength());
        Assert.Equal((1, 2060m), (I(r.GetProperty("todayTotals"), "orders"), D(r.GetProperty("todayTotals"), "sales"))); // O5 today
        var series = r.GetProperty("series").EnumerateArray().ToList();
        Assert.Equal(1960m, D(series[^6], "sales")); // O1 five days ago: 2000 - 100 + 60
        Assert.Equal(5, series.Sum(x => I(x, "orders")));

        // Orders placed in the last 30 days by status: Placed O4, Confirmed O5, Delivered O1, O2, O6, Cancelled O3.
        var statuses = r.GetProperty("ordersByStatus").EnumerateArray().ToDictionary(x => x.GetProperty("status").GetString()!, x => I(x, "count"));
        Assert.Equal((1, 1, 3, 1), (statuses["Placed"], statuses["Confirmed"], statuses["Delivered"], statuses["Cancelled"]));

        // To confirm: O4. To pack: O5. Held by fraud: none. Returns waiting: three requested. Low stock: two rows.
        var p = r.GetProperty("pending");
        Assert.Equal((1, 1, 0, 3, 2), (I(p, "toConfirm"), I(p, "toPack"), I(p, "heldByFraud"), I(p, "returnsWaiting"), I(p, "lowStock")));

        // Top products by units over 30 days: product two 3 + 4 + 1 = 8 (revenue 4000 - 50), product one 2 + 1 = 3 (3000 - 100).
        var top = r.GetProperty("topProducts").EnumerateArray().ToList();
        Assert.Equal(("Report Tee Two", 8, 3950m), (top[0].GetProperty("name").GetString(), I(top[0], "units"), D(top[0], "revenue")));
        Assert.Equal(("Report Tee One", 3, 2900m), (top[1].GetProperty("name").GetString(), I(top[1], "units"), D(top[1], "revenue")));
        Assert.Equal((560m, 2060m), (D(r, "codPending"), D(r, "codInTransit")));

        // A branch-scoped user only sees their own branch (B: O4 placed to confirm, nothing to pack).
        var b = await _o.GetAsync("/api/v1/reports/dashboard", await ScopedAsync(s, s.B, false));
        Assert.Equal((1, 0, 1, 1), (I(b.GetProperty("pending"), "toConfirm"), I(b.GetProperty("pending"), "toPack"), I(b.GetProperty("pending"), "returnsWaiting"), I(b.GetProperty("pending"), "lowStock")));
        Assert.Equal(0, I(b.GetProperty("todayTotals"), "orders"));
    }

    // ---- permissions and validation -----------------------------------------------------------------------------

    [Fact]
    public async Task Every_report_and_export_needs_its_own_permission()
    {
        var s = await SeedAsync();
        var viewOnly = await _o.StaffAsync(s.Admin, null, "reports.sales.view", "reports.orders.view", "reports.inventory.view", "reports.finance.view", "reports.courier.view", "reports.customers.view", "reports.dashboard.view", "accounting.pnl.view");
        var nothing = await _o.StaffAsync(s.Admin, null, "catalog.product.view");
        foreach (var name in new[] { "sales", "orders", "inventory", "finance", "courier", "customers" })
        {
            Assert.Equal(HttpStatusCode.OK, (await _o.TryGetAsync($"/api/v1/reports/{name}?{s.Range}", viewOnly)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryGetAsync($"/api/v1/reports/{name}?{s.Range}", nothing)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryGetAsync($"/api/v1/reports/{name}/export?{s.Range}", viewOnly)).StatusCode); // viewing is not exporting
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryGetAsync("/api/v1/reports/dashboard", nothing)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryGetAsync($"/api/v1/accounting/pnl?{s.Range}", nothing)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryGetAsync($"/api/v1/accounting/export?{s.Range}&kind=daybook", viewOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _o.TryGetAsync($"/api/v1/reports/sales?{s.Range}", "")).StatusCode);

        // The one who may export sales may not export finance.
        var salesExporter = await _o.StaffAsync(s.Admin, null, "reports.sales.export");
        Assert.Equal(HttpStatusCode.OK, (await _o.TryGetAsync($"/api/v1/reports/sales/export?{s.Range}", salesExporter)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryGetAsync($"/api/v1/reports/finance/export?{s.Range}", salesExporter)).StatusCode);
    }

    [Fact]
    public async Task Bad_ranges_groupings_and_formats_are_refused_with_stable_codes()
    {
        var s = await SeedAsync();
        await OpsHelper.ExpectCodeAsync(await _o.TryGetAsync($"/api/v1/reports/sales?from={s.Iso(0)}&to={s.Iso(5)}", s.Admin), "REPORT.RANGE_INVALID");
        await OpsHelper.ExpectCodeAsync(await _o.TryGetAsync($"/api/v1/reports/sales?from={s.Iso(400)}&to={s.Iso(0)}&groupBy=day", s.Admin), "REPORT.RANGE_TOO_LARGE");
        Assert.Equal(HttpStatusCode.OK, (await _o.TryGetAsync($"/api/v1/reports/sales?from={s.Iso(400)}&to={s.Iso(0)}&groupBy=month", s.Admin)).StatusCode); // a long range is fine by month
        await OpsHelper.ExpectCodeAsync(await _o.TryGetAsync($"/api/v1/reports/sales?from={s.Iso(2000)}&to={s.Iso(0)}&groupBy=month", s.Admin), "REPORT.RANGE_TOO_LARGE");
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.TryGetAsync($"/api/v1/reports/sales?{s.Range}&groupBy=banana", s.Admin)).StatusCode);
        await OpsHelper.ExpectCodeAsync(await _o.TryGetAsync($"/api/v1/reports/sales/export?{s.Range}&format=docx", s.Admin), "REPORT.FORMAT_INVALID");
        await OpsHelper.ExpectCodeAsync(await _o.TryGetAsync($"/api/v1/reports/orders/export?{s.Range}&section=nothing", s.Admin), "REPORT.UNKNOWN");
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.TryGetAsync($"/api/v1/reports/sales/export?{s.Range}&lang=fr", s.Admin)).StatusCode);
        await OpsHelper.ExpectCodeAsync(await _o.TryGetAsync($"/api/v1/accounting/export?{s.Range}&kind=daybook&format=pdf", s.Admin), "REPORT.FORMAT_INVALID");
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.TryGetAsync($"/api/v1/accounting/export?{s.Range}&kind=banana", s.Admin)).StatusCode);
    }

    // ---- exports --------------------------------------------------------------------------------------------------

    private TableData ReadFile(byte[] bytes, TabularFormat format)
    {
        var files = factory.Services.GetRequiredService<ITabularFile>();
        using var stream = new MemoryStream(bytes);
        return files.Read(stream, format, 100_000);
    }

    private static void ValidWorkbook(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        using var document = SpreadsheetDocument.Open(stream, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019).Validate(document).ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.Description)));
    }

    private async Task<byte[]> FileAsync(string url, string token, string expectedType)
    {
        var response = await _o.TryGetAsync(url, token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith(expectedType, response.Content.Headers.ContentType?.ToString(), StringComparison.Ordinal);
        Assert.NotNull(response.Content.Headers.ContentDisposition?.FileName);
        return await response.Content.ReadAsByteArrayAsync(Ct);
    }

    [Fact]
    public async Task Csv_and_excel_exports_hold_the_same_totals_as_the_screen_and_the_excel_file_is_valid()
    {
        var s = await SeedAsync();
        var csv = await FileAsync($"/api/v1/reports/sales/export?{s.Range}&groupBy=channel&format=csv", s.Admin, "text/csv");
        Assert.Equal([0xEF, 0xBB, 0xBF], csv[..3]); // the byte order mark Excel needs for Bengali text
        var table = ReadFile(csv, TabularFormat.Csv);
        Assert.Equal(["Label", "Orders", "Units", "Gross", "Discount", "Net", "Delivery", "Grand total"], table.Headers);
        var total = table.Rows[^1];
        Assert.Equal(("Total", "5", "11", 7000m, 150m, 6850m, 280m, 7130m), (total[0], total[1], total[2], decimal.Parse(total[3]!, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(total[4]!, System.Globalization.CultureInfo.InvariantCulture),
            decimal.Parse(total[5]!, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(total[6]!, System.Globalization.CultureInfo.InvariantCulture), decimal.Parse(total[7]!, System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(["Web", "Facebook", "WalkIn"], table.Rows.Take(3).Select(r => r[0]).ToArray()); // biggest first

        var xlsx = await FileAsync($"/api/v1/reports/sales/export?{s.Range}&groupBy=channel&format=xlsx", s.Admin, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        ValidWorkbook(xlsx);
        var sheet = ReadFile(xlsx, TabularFormat.Xlsx);
        Assert.Equal(table.Rows.Count, sheet.Rows.Count);
        Assert.Equal(7000m, decimal.Parse(sheet.Rows[^1][3]!, System.Globalization.CultureInfo.InvariantCulture));

        // Every report exports to CSV and Excel with its first table; the second table of the funnel is reached with section=.
        foreach (var name in new[] { "sales", "orders", "inventory", "finance", "courier", "customers" })
        {
            var rows = ReadFile(await FileAsync($"/api/v1/reports/{name}/export?{s.Range}&format=csv", s.Admin, "text/csv"), TabularFormat.Csv);
            Assert.NotEmpty(rows.Headers);
            ValidWorkbook(await FileAsync($"/api/v1/reports/{name}/export?{s.Range}&format=xlsx", s.Admin, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));
        }

        var reasons = ReadFile(await FileAsync($"/api/v1/reports/orders/export?{s.Range}&section=cancellations", s.Admin, "text/csv"), TabularFormat.Csv);
        Assert.Equal("Customer changed mind", reasons.Rows[0][0]);

        // The finance export carries the same net revenue as the screen; the cost columns are empty without the cost permission.
        var finance = ReadFile(await FileAsync($"/api/v1/reports/finance/export?{s.Range}&groupBy=month", s.Admin, "text/csv"), TabularFormat.Csv);
        Assert.Equal("6650.00", finance.Rows[^1][finance.Headers.ToList().IndexOf("Net revenue")]);
        Assert.Equal("3600.00", finance.Rows[^1][finance.Headers.ToList().IndexOf("Cost of goods")]);
        var noCost = await ScopedAsync(s, null, false);
        var hidden = ReadFile(await FileAsync($"/api/v1/reports/finance/export?{s.Range}&groupBy=month", noCost, "text/csv"), TabularFormat.Csv);
        Assert.True(string.IsNullOrEmpty(hidden.Rows[^1][hidden.Headers.ToList().IndexOf("Cost of goods")]));
        Assert.True(string.IsNullOrEmpty(hidden.Rows[^1][hidden.Headers.ToList().IndexOf("Gross profit")]));
    }

    [Fact]
    public async Task Pdf_exports_are_small_valid_files_in_english_and_bengali()
    {
        var s = await SeedAsync();
        foreach (var name in new[] { "sales", "orders", "inventory", "finance", "courier", "customers" })
        {
            foreach (var lang in new[] { "en", "bn" })
            {
                var pdf = await FileAsync($"/api/v1/reports/{name}/export?{s.Range}&format=pdf&lang={lang}", s.Admin, "application/pdf");
                Assert.Equal("%PDF-", System.Text.Encoding.ASCII.GetString(pdf, 0, 5));
                Assert.Contains("%%EOF", System.Text.Encoding.ASCII.GetString(pdf, pdf.Length - 16, 16), StringComparison.Ordinal);
                Assert.InRange(pdf.Length, 2_000, 1_200_000); // like the invoices, the fonts are embedded whole: about 0.6 to 0.9 MB
            }
        }
    }

    [Fact]
    public async Task The_accountant_export_covers_the_day_book_expenses_profit_payables_and_purchases()
    {
        var s = await SeedAsync();
        string Url(string kind, string format = "csv") => $"/api/v1/accounting/export?{s.Range}&kind={kind}&format={format}";
        static decimal Num(string? v) => decimal.Parse(v!, System.Globalization.CultureInfo.InvariantCulture);

        // Day book: money read from payments: customer payment 1550 (O2), settled COD cash 1960 (O1), refund 200. In 3510, out 200, net 3310 (the same numbers as the day book screen).
        var day = ReadFile(await FileAsync(Url("daybook"), s.Admin, "text/csv"), TabularFormat.Csv);
        var total = day.Rows[^1];
        Assert.Equal((3510m, 200m, 3310m), (Num(total[5]), Num(total[6]), Num(total[7])));
        Assert.Equal(3, day.Rows.Count - 1);
        var screen = await _o.GetAsync($"/api/v1/accounts/daybook?from={s.From}&to={s.To}", s.Admin);
        Assert.Equal((3510m, 200m, 3310m), (D(screen, "totalIn"), D(screen, "totalOut"), D(screen, "net")));
        ValidWorkbook(await FileAsync(Url("daybook", "xlsx"), s.Admin, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"));

        // Expenses: approved only, 400 + 150 = 550.
        var expenses = ReadFile(await FileAsync(Url("expenses"), s.Admin, "text/csv"), TabularFormat.Csv);
        Assert.Equal(2, expenses.Rows.Count - 1);
        Assert.Equal(550m, Num(expenses.Rows[^1][4]));

        // Profit and loss: the same 2500 as the endpoint.
        var pnl = ReadFile(await FileAsync(Url("pnl"), s.Admin, "text/csv"), TabularFormat.Csv);
        Assert.Equal(2500m, Num(pnl.Rows[^1][pnl.Headers.ToList().IndexOf("Net profit")]));

        // Payables: the purchase order is received and unpaid: 5500 payable and outstanding. Purchases: 10 accepted at 550.
        var payables = ReadFile(await FileAsync(Url("payables"), s.Admin, "text/csv"), TabularFormat.Csv);
        Assert.Equal((5500m, 0m, 5500m), (Num(payables.Rows[^1][6]), Num(payables.Rows[^1][7]), Num(payables.Rows[^1][8])));
        var purchases = ReadFile(await FileAsync(Url("purchases"), s.Admin, "text/csv"), TabularFormat.Csv);
        Assert.Equal((10m, 5500m), (Num(purchases.Rows[^1][6]), Num(purchases.Rows[^1][9])));

        // Cost carrying exports need the cost permission as well; the others do not.
        var noCost = await _o.StaffAsync(s.Admin, null, "accounting.export.run");
        await OpsHelper.ExpectCodeAsync(await _o.TryGetAsync(Url("pnl"), noCost), "REPORT.COST_HIDDEN");
        await OpsHelper.ExpectCodeAsync(await _o.TryGetAsync(Url("purchases"), noCost), "REPORT.COST_HIDDEN");
        Assert.Equal(HttpStatusCode.OK, (await _o.TryGetAsync(Url("daybook"), noCost)).StatusCode);

        // A branch-scoped export holds only that branch: branch B has one approved expense of 150.
        var scopedB = await _o.StaffAsync(s.Admin, s.B, "accounting.export.run");
        var b = ReadFile(await FileAsync(Url("expenses"), scopedB, "text/csv"), TabularFormat.Csv);
        Assert.Equal(150m, Num(b.Rows[^1][4]));
    }

    [Fact]
    public async Task Text_that_could_be_a_formula_is_neutralised_in_exports()
    {
        var s = await SeedAsync();
        // The product name of a sales row is free text; a spreadsheet would run a leading "=" as a formula, so the writer prefixes an apostrophe.
        var files = factory.Services.GetRequiredService<ITabularFile>();
        var bytes = files.Write(new TableData(["Label", "Amount"], [["=HYPERLINK(\"http://x\")", "-5.00"]], new HashSet<string> { "Amount" }), TabularFormat.Csv);
        var text = System.Text.Encoding.UTF8.GetString(bytes);
        Assert.Contains("'=HYPERLINK", text, StringComparison.Ordinal);
        Assert.Contains("-5.00", text, StringComparison.Ordinal); // numbers keep their sign
        Assert.NotNull(s);
    }

    // ---- daily summary (FR-P07) -----------------------------------------------------------------------------------

    [Fact]
    public async Task The_daily_summary_is_off_without_an_email_and_is_sent_once_per_day()
    {
        var s = await SeedAsync();
        await _o.ClearSettingAsync(DailySummarySettings.Email);
        await _o.ClearSettingAsync(DailySummarySettings.LastSent);
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(DailySummaryOutcome.Disabled, await scope.ServiceProvider.GetRequiredService<DailySummaryService>().SendIfDueAsync(Ct));
        }

        await _o.SetSettingAsync(DailySummarySettings.Email, "owner@juvo.test");
        await _o.SetSettingAsync(DailySummarySettings.Hour, "0");
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<DailySummaryService>();
            Assert.Equal(DailySummaryOutcome.Sent, await service.SendIfDueAsync(Ct));
            Assert.Equal(DailySummaryOutcome.AlreadySent, await service.SendIfDueAsync(Ct)); // a repeated run the same day sends nothing
        }

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            Assert.Equal(DailySummaryOutcome.AlreadySent, await scope.ServiceProvider.GetRequiredService<DailySummaryService>().SendIfDueAsync(Ct)); // nor does a new scope (a restart)
        }

        var logs = await _o.WithDbAsync(async db => await db.NotificationLogs.Where(n => n.Template == DailySummarySettings.Template && n.Recipient == "owner@juvo.test").ToListAsync(Ct));
        var log = Assert.Single(logs);

        // Yesterday (T-1): order O4 only: 1 order, sales 1000, no cash, 2 low stock rows.
        Assert.Contains($"daily summary for {s.Iso(1)}", log.Body, StringComparison.Ordinal);
        Assert.Contains("Orders: 1", log.Body, StringComparison.Ordinal);
        Assert.Contains("Sales (BDT): 1000.00", log.Body, StringComparison.Ordinal);
        Assert.Contains("Cash in (customer payments and settled COD, BDT): 0.00", log.Body, StringComparison.Ordinal);
        Assert.Contains("Low stock items: 2", log.Body, StringComparison.Ordinal);

        // Before the send hour nothing is sent.
        await _o.ClearSettingAsync(DailySummarySettings.LastSent);
        await _o.SetSettingAsync(DailySummarySettings.Hour, "24");
        await using var late = factory.Services.CreateAsyncScope();
        Assert.Equal(DailySummaryOutcome.NotYetDue, await late.ServiceProvider.GetRequiredService<DailySummaryService>().SendIfDueAsync(Ct));
        await _o.ClearSettingAsync(DailySummarySettings.Email);
    }
}
