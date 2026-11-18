using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using static Juvo.Api.IntegrationTests.Promotions.PromoHelper;

namespace Juvo.Api.IntegrationTests.Promotions;

/// <summary>
/// Flash sale before, during and after its window, with the test clock, and the stacking matrix through the API. Staff calls come first and anonymous shop calls after the clock moved, so the
/// staff access token stays valid.
/// </summary>
public class FlashSaleTimingTests(PromoFactory factory) : IClassFixture<PromoFactory>
{
    private readonly PromoHelper _p = new(new OrderHelper(new StoreHelper(factory)), factory);

    private StoreHelper H => _p.H;

    private OrderHelper O => _p.O;

    private static int MaxAge(HttpResponseMessage response)
    {
        var header = response.Headers.CacheControl;
        return header?.MaxAge is { } age ? (int)age.TotalSeconds : 0;
    }

    [Fact]
    public async Task A_flash_sale_prices_only_inside_its_window_and_both_ends_count()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var variant = product.Variants[0];
        var start = _p.Now.AddMinutes(2);
        var end = _p.Now.AddMinutes(10);
        await _p.LiveAsync(admin, FlashSale("Window flash", "FixedPrice", 600m, [ProductTarget(product)], start, end));
        var token = await O.CartAsync(variant.Id, 1);

        // Before the window: normal price, no promotion on the card, and the answer is not cached beyond the start.
        var before = await H.SendAsync(HttpMethod.Get, $"/api/v1/store/products/{product.Slug}");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.InRange(MaxAge(before), 0, 120);
        var beforeCard = JsonDocument.Parse(await before.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("card");
        Assert.Equal(1000m, Num(beforeCard, "price"));
        Assert.Equal(JsonValueKind.Null, beforeCard.GetProperty("promotion").ValueKind);
        Assert.Equal(1000m, Num((await _p.CartAsync(token)).GetProperty("lines")[0], "unitPrice"));

        factory.Time.Advance(TimeSpan.FromMinutes(2) - TimeSpan.FromSeconds(1)); // one second before the start
        Assert.Equal(1000m, Num((await _p.CartAsync(token)).GetProperty("lines")[0], "unitPrice"));
        Assert.InRange(MaxAge(await H.SendAsync(HttpMethod.Get, $"/api/v1/store/products/{product.Slug}")), 0, 1); // never cached past the start

        factory.Time.Advance(TimeSpan.FromSeconds(1)); // exactly the start: it runs
        var during = await _p.CartAsync(token);
        Assert.Equal(600m, Num(during.GetProperty("lines")[0], "unitPrice"));
        Assert.True(during.GetProperty("priceChanged").GetBoolean()); // the line was added at 1000
        Assert.Equal(1000m, Num(during.GetProperty("lines")[0], "previousUnitPrice"));
        var detail = await H.SendAsync(HttpMethod.Get, $"/api/v1/store/products/{product.Slug}");
        Assert.InRange(MaxAge(detail), 0, 8 * 60 + 1); // a cached answer cannot outlive the sale
        var card = JsonDocument.Parse(await detail.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("card");
        Assert.Equal(600m, Num(card, "price"));
        Assert.Equal(end.ToUnixTimeSeconds(), card.GetProperty("promotion").GetProperty("endsAt").GetDateTimeOffset().ToUnixTimeSeconds());

        var quote = await _p.QuoteAsync(token);
        Assert.Equal(600m, Num(quote, "subtotal"));
        var heldTotal = Num(quote, "grandTotal");

        factory.Time.Advance(end - _p.Now); // exactly the end: still running
        Assert.Equal(600m, Num((await _p.CartAsync(token)).GetProperty("lines")[0], "unitPrice"));
        Assert.InRange(MaxAge(await H.SendAsync(HttpMethod.Get, $"/api/v1/store/products/{product.Slug}")), 0, 1); // never cached past the end

        factory.Time.Advance(TimeSpan.FromSeconds(1)); // one second later: over
        var after = await _p.CartAsync(token);
        Assert.Equal(1000m, Num(after.GetProperty("lines")[0], "unitPrice"));
        var afterDetail = await H.SendAsync(HttpMethod.Get, $"/api/v1/store/products/{product.Slug}");
        var afterCard = JsonDocument.Parse(await afterDetail.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement.GetProperty("card");
        Assert.Equal(1000m, Num(afterCard, "price"));
        Assert.Equal(JsonValueKind.Null, afterCard.GetProperty("promotion").ValueKind);

        // A customer who held the old total is told the total changed (never billed more than shown).
        var late = await O.TryCheckoutAsync(token, StoreHelper.NewMobile(), new CheckoutOptions(Expected: heldTotal));
        await StoreHelper.ExpectCodeAsync(late, "ORDER.TOTAL_CHANGED");
        var current = Num(await _p.QuoteAsync(token), "grandTotal");
        Assert.Equal(heldTotal + 400m, current);
        var placed = await TestApi.ExpectAsync(await O.TryCheckoutAsync(token, StoreHelper.NewMobile(), new CheckoutOptions(Expected: current)), HttpStatusCode.Created);
        Assert.Equal(current, Num(placed, "grandTotal"));
    }

    [Fact]
    public async Task The_lowest_price_wins_a_tie_goes_to_the_higher_priority_and_a_sale_price_is_one_of_the_competitors()
    {
        // product sale price 900; a flash price 850; 25 percent off 900 = 675; another 25 percent with a higher priority gives the same 675.
        var (admin, product) = await _p.ProductAsync(1000m, salePrice: 900m);
        var token = await O.CartAsync(product.Variants[0].Id, 1);
        Assert.Equal(900m, Num((await _p.CartAsync(token)).GetProperty("lines")[0], "unitPrice"));

        await _p.LiveAsync(admin, FlashSale("Flash 850", "FixedPrice", 850m, [ProductTarget(product)], _p.Now.AddMinutes(-1), _p.Now.AddHours(1)));
        var withFlash = await _p.CartAsync(token);
        Assert.Equal(850m, Num(withFlash.GetProperty("lines")[0], "unitPrice"));

        var low = await _p.LiveAsync(admin, ProductDiscount("Quarter low", "Percent", 25m, [ProductTarget(product)], priority: 1));
        var high = await _p.LiveAsync(admin, ProductDiscount("Quarter high", "Percent", 25m, [ProductTarget(product)], priority: 9));
        var lines = (await _p.CartAsync(token)).GetProperty("lines")[0];
        Assert.Equal(675m, Num(lines, "unitPrice"));
        var promoAdjustments = lines.GetProperty("adjustments").EnumerateArray().Where(a => Str(a, "sourceType") == "Promotion").ToList();
        Assert.Single(promoAdjustments); // promotions never stack
        Assert.Equal(high.GetProperty("id").GetGuid(), promoAdjustments[0].GetProperty("sourceId").GetGuid());
        Assert.NotEqual(low.GetProperty("id").GetGuid(), promoAdjustments[0].GetProperty("sourceId").GetGuid());

        // The savings are counted from the list price: 1000 -> 675.
        var cart = await _p.CartAsync(token);
        Assert.Equal(325m, Num(cart.GetProperty("explanation"), "savings"));
    }

    [Fact]
    public async Task Scheduled_promotions_start_by_themselves_and_ended_ones_stop()
    {
        var (admin, product) = await _p.ProductAsync(1000m);
        var created = await _p.LiveAsync(admin, ProductDiscount("Scheduled", "Percent", 10m, [ProductTarget(product)], starts: _p.Now.AddMinutes(3), ends: _p.Now.AddMinutes(6)));
        Assert.Equal("Scheduled", Str(created, "status"));
        var token = await O.CartAsync(product.Variants[0].Id, 1);
        Assert.Equal(1000m, Num((await _p.CartAsync(token)).GetProperty("lines")[0], "unitPrice"));

        factory.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(900m, Num((await _p.CartAsync(token)).GetProperty("lines")[0], "unitPrice"));

        factory.Time.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(1000m, Num((await _p.CartAsync(token)).GetProperty("lines")[0], "unitPrice"));
    }
}
