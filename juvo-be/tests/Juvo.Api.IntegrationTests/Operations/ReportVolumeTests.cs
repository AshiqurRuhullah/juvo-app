using System.Diagnostics;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Domain.Customers;
using Juvo.Domain.Orders;

namespace Juvo.Api.IntegrationTests.Operations;

/// <summary>A few thousand orders: every report is still one set-based answer, with exact totals and a generous time bound (the bound only catches a query that went row by row).</summary>
public class ReportVolumeTests(OpsFactory factory) : IClassFixture<OpsFactory>
{
    private readonly OpsHelper _o = new(new OrderHelper(new StoreHelper(factory)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Orders = 3000;

    [Fact]
    public async Task Every_report_answers_over_three_thousand_orders_with_exact_totals()
    {
        var admin = await _o.H.AdminAsync();
        var ho = await _o.HoAsync();
        var category = await _o.H.CategoryAsync(admin);
        var product = await _o.H.ProductAsync(admin, category.Id, "Volume Tee", 100m, [("Blue", "M", 0)]);
        var variant = product.Variants[0];
        var today = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6));
        var shipping = new ShippingSnapshot("Rina", "01711111111", "BD", null, null, null, null, null, null, null, "House 1", null, null, null, null, null, null);

        decimal gross = 0m;
        var units = 0;
        await _o.WithDbAsync(async db =>
        {
            var customer = new Customer("01722000001", ProfileLevel.Full);
            db.Customers.Add(customer);
            await db.SaveChangesAsync(Ct);
            for (var batch = 0; batch < Orders / 250; batch++)
            {
                for (var k = 0; k < 250; k++)
                {
                    var i = batch * 250 + k;
                    var price = 100m + i % 5;
                    var qty = 1 + i % 3;
                    var day = today.AddDays(-(i % 60));
                    var at = new DateTimeOffset(day.Year, day.Month, day.Day, 12, 0, 0, TimeSpan.FromHours(6));
                    gross += price * qty;
                    units += qty;
                    var data = new NewOrder($"JV-VOL-{i:D6}", ho, (OrderSource)(1 + i % 5), customer.Id, customer.Mobile, null, null, i % 2 == 0 ? "COD" : "BKASH", "DefaultBranch", "Z" + i % 4, "BDT", price * qty, 0m, null, null, 0m, 60m, 60m, 0m, 0, shipping,
                        null, null, "fp-v" + i, "hash-v" + i, null, null, false, null, null, null);
                    db.Orders.Add(Order.Place(data, [new NewOrderItem(variant.Id, product.Id, product.Name, null, variant.Sku, variant.Colour, variant.Size, null, price, qty, 0, true, 60m)], at));
                }

                await db.SaveChangesAsync(Ct);
                db.ChangeTracker.Clear();
            }

            return 0;
        });

        var range = $"from={today.AddDays(-59):yyyy-MM-dd}&to={today:yyyy-MM-dd}";
        var timings = new List<string>();

        async Task<System.Text.Json.JsonElement> Timed(string url)
        {
            var watch = Stopwatch.StartNew();
            var json = await _o.GetAsync(url, admin);
            watch.Stop();
            timings.Add($"{url.Split('?')[0]} {watch.ElapsedMilliseconds} ms");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(20), $"{url} took {watch.Elapsed}");
            return json;
        }

        foreach (var by in new[] { "day", "week", "month", "product", "category", "size", "color", "channel", "zone", "payment" })
        {
            var sales = await Timed($"/api/v1/reports/sales?{range}&groupBy={by}");
            var totals = sales.GetProperty("totals");
            Assert.Equal((Orders, units, Orders * 60m), (totals.GetProperty("orders").GetInt32(), totals.GetProperty("units").GetInt32(), totals.GetProperty("delivery").GetDecimal()));
            Assert.Equal(gross, sales.GetProperty("totals").GetProperty("gross").GetDecimal());
        }

        var pnl = await Timed($"/api/v1/accounting/pnl?{range}&groupBy=day");
        Assert.Equal(gross, pnl.GetProperty("total").GetProperty("grossSales").GetDecimal());
        Assert.Equal(units * 60m, pnl.GetProperty("total").GetProperty("costOfGoods").GetDecimal());

        Assert.Equal(Orders, (await Timed($"/api/v1/reports/orders?{range}")).GetProperty("placed").GetInt32());
        Assert.Equal(1, (await Timed($"/api/v1/reports/customers?{range}")).GetProperty("customers").GetInt32());
        await Timed($"/api/v1/reports/inventory?{range}");
        await Timed($"/api/v1/reports/finance?{range}");
        await Timed($"/api/v1/reports/courier?{range}");
        Assert.Equal(Enumerable.Range(0, Orders).Count(i => i % 60 < 14), (await Timed("/api/v1/reports/dashboard")).GetProperty("series").EnumerateArray().Sum(x => x.GetProperty("orders").GetInt32()));
        TestContext.Current.SendDiagnosticMessage(string.Join("; ", timings));
    }

}
