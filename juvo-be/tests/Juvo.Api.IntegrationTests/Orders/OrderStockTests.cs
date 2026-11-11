using System.Net;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Orders;

/// <summary>NFR-019: two buyers must never get the last unit. These tests fire real concurrent checkouts at a real SQL Server.</summary>
public class OrderStockTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly OrderHelper _o = new(new StoreHelper(factory));

    private StoreHelper H => _o.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Admin, SeededProduct Product)> ProductAsync(int stock)
    {
        var admin = await H.AdminAsync();
        var category = await H.CategoryAsync(admin);
        return (admin, await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Race Tee"), 450m, [("Black", "M", stock)]));
    }

    private sealed record Buyer(string Cart, string Mobile, string Otp);

    private async Task<List<Buyer>> BuyersAsync(Guid variant, int count, int quantity)
    {
        var list = new List<Buyer>();
        for (var i = 0; i < count; i++)
        {
            var mobile = StoreHelper.NewMobile();
            list.Add(new Buyer(await _o.CartAsync(variant, quantity), mobile, await _o.OtpAsync(mobile)));
        }

        return list;
    }

    private async Task<List<HttpResponseMessage>> RaceAsync(IReadOnlyList<Buyer> buyers)
    {
        // All requests wait on one gate, then go at the same moment.
        var gate = new TaskCompletionSource();
        var tasks = buyers.Select(async b =>
        {
            await gate.Task;
            return await _o.TryCheckoutAsync(b.Cart, b.Mobile, new CheckoutOptions(OtpCode: b.Otp));
        }).ToList();
        gate.SetResult();
        return [.. await Task.WhenAll(tasks)];
    }

    [Fact]
    public async Task Eight_buyers_race_for_the_last_unit_and_exactly_one_gets_it()
    {
        var (_, product) = await ProductAsync(stock: 1);
        var variant = product.Variants[0].Id;
        var buyers = await BuyersAsync(variant, 8, 1);

        var responses = await RaceAsync(buyers);

        var created = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
        var refused = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        Assert.Single(created);
        Assert.Equal(7, refused.Count);
        foreach (var response in refused)
        {
            await StoreHelper.ExpectCodeAsync(response, "ORDER.STOCK_INSUFFICIENT");
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        Assert.Equal((1, 1), await _o.StockAsync(variant));
        Assert.Equal(1, await _o.WithDbAsync(db => db.OrderItems.CountAsync(i => i.VariantId == variant, Ct)));
    }

    [Fact]
    public async Task Many_buyers_never_reserve_more_than_what_is_on_hand()
    {
        var (_, product) = await ProductAsync(stock: 5);
        var variant = product.Variants[0].Id;
        var buyers = await BuyersAsync(variant, 4, 2); // 4 x 2 units wanted, 5 available: two orders fit

        var responses = await RaceAsync(buyers);

        Assert.Equal(2, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        Assert.Equal((5, 4), await _o.StockAsync(variant));
        Assert.Equal(4, await _o.WithDbAsync(db => db.OrderItems.Where(i => i.VariantId == variant).SumAsync(i => i.Quantity, Ct)));

        // The two refused buyers took no order number: the numbers of the branch are still 1..N without a gap.
        var numbers = await _o.WithDbAsync(db => db.Orders.IgnoreQueryFilters(["BranchScope"]).Select(o => o.Number).ToListAsync(Ct));
        var sequence = numbers.Select(n => int.Parse(n[^6..], System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
        Assert.Equal(Enumerable.Range(1, sequence.Count).ToList(), sequence);
    }

    [Fact]
    public async Task A_refused_order_leaves_no_trace_no_reservation_no_order_and_no_gap_in_the_numbers()
    {
        var (_, product) = await ProductAsync(stock: 1);
        var variant = product.Variants[0].Id;
        var winner = await _o.PlaceAsync(variant, 1);
        var numberBefore = int.Parse(winner.GetProperty("number").GetString()![^6..], System.Globalization.CultureInfo.InvariantCulture);
        var ordersBefore = await _o.WithDbAsync(db => db.Orders.IgnoreQueryFilters(["BranchScope"]).CountAsync(Ct));

        // The cart accepted this quantity while stock was there; now it is gone.
        var loser = await _o.TryCheckoutAsync(await StaleCartAsync(variant), StoreHelper.NewMobile());

        await StoreHelper.ExpectCodeAsync(loser, "ORDER.STOCK_INSUFFICIENT");
        Assert.Equal((1, 1), await _o.StockAsync(variant));
        Assert.Equal(ordersBefore, await _o.WithDbAsync(db => db.Orders.IgnoreQueryFilters(["BranchScope"]).CountAsync(Ct)));

        // The next successful order of the same branch continues the sequence without a gap.
        var admin = await H.AdminAsync();
        await H.SetStockChangeAsync(admin, variant, 5);
        var next = await _o.PlaceAsync(variant, 1);
        Assert.Equal(numberBefore + 1, int.Parse(next.GetProperty("number").GetString()![^6..], System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>A cart that was filled while the unit was still free.</summary>
    private async Task<string> StaleCartAsync(Guid variant)
    {
        var admin = await H.AdminAsync();
        await H.SetStockChangeAsync(admin, variant, 1); // one more unit appears...
        var cart = await _o.CartAsync(variant, 1);
        await H.SetStockChangeAsync(admin, variant, -1, reason: "Damage"); // ...and is written off before checkout
        return cart;
    }

    [Fact]
    public async Task The_database_itself_refuses_to_reserve_more_than_on_hand()
    {
        var (_, product) = await ProductAsync(stock: 2);
        var variant = product.Variants[0].Id;

        // Even a buggy writer that bypasses the application cannot oversell: the check constraint on StockLevels is the last wall.
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => _o.WithDbAsync(db =>
            db.StockLevels.Where(s => s.VariantId == variant).ExecuteUpdateAsync(s => s.SetProperty(x => x.Reserved, 3), Ct)));

        Assert.Contains("CK_StockLevels_Reserved", ex.ToString(), StringComparison.Ordinal);
        Assert.Equal((2, 0), await _o.StockAsync(variant));
    }
}
