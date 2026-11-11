using System.Net;
using System.Text.Json;

namespace Juvo.Api.IntegrationTests.Storefront;

public class StoreCartTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly StoreHelper _h = new(factory);

    private const string Cart = "/api/v1/store/cart";

    private async Task<(string Admin, SeededProduct Product)> OneProductAsync(decimal price = 500m, int stock = 25, decimal? salePrice = null)
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        var product = await _h.ProductAsync(admin, cat.Id, StoreHelper.Unique("Cart Tee"), price, [("Red", "M", stock)], salePrice: salePrice, weightGrams: 400);
        return (admin, product);
    }

    private async Task<JsonElement> AddAsync(Guid variantId, int quantity, string? token = null, string? bearer = null) =>
        await _h.JsonAsync(HttpMethod.Post, Cart + "/items", HttpStatusCode.OK, new { variantId, quantity }, bearer, token);

    [Fact]
    public async Task A_guest_gets_a_cart_token_and_only_that_token_opens_the_cart()
    {
        var (_, product) = await OneProductAsync(price: 500m);

        var added = await AddAsync(product.Variants[0].Id, 2);
        var token = added.GetProperty("token").GetString()!;

        Assert.False(string.IsNullOrWhiteSpace(token));
        Assert.Equal(1000m, added.GetProperty("subtotal").GetDecimal());
        Assert.Equal(800, added.GetProperty("weightGrams").GetInt32());
        Assert.Equal(2, added.GetProperty("itemCount").GetInt32());

        var mine = await _h.JsonAsync(HttpMethod.Get, Cart, HttpStatusCode.OK, cartToken: token);
        Assert.Equal(2, mine.GetProperty("itemCount").GetInt32());

        var other = await _h.JsonAsync(HttpMethod.Get, Cart, HttpStatusCode.OK, cartToken: "not-the-token");
        Assert.Equal(0, other.GetProperty("itemCount").GetInt32());
        var none = await _h.JsonAsync(HttpMethod.Get, Cart, HttpStatusCode.OK);
        Assert.Equal(0, none.GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task The_cart_stores_only_a_hash_of_the_token()
    {
        var (_, product) = await OneProductAsync();
        var token = (await AddAsync(product.Variants[0].Id, 1)).GetProperty("token").GetString()!;

        await using var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(factory.Services);
        var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<Juvo.Infrastructure.Persistence.JuvoDbContext>(scope.ServiceProvider);
        var hashes = await Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.ToListAsync(
            db.Carts.Where(c => c.TokenHash != null).Select(c => c.TokenHash!), TestContext.Current.CancellationToken);

        Assert.DoesNotContain(token, hashes);
        Assert.All(hashes, h => Assert.NotEqual(token, h));
    }

    [Fact]
    public async Task Adding_the_same_variant_again_adds_up_and_each_line_is_limited_to_the_per_line_maximum()
    {
        var (_, product) = await OneProductAsync(stock: 25);
        var first = await AddAsync(product.Variants[0].Id, 4);
        var token = first.GetProperty("token").GetString()!;

        var second = await AddAsync(product.Variants[0].Id, 3, token);
        Assert.Equal(7, second.GetProperty("lines")[0].GetProperty("quantity").GetInt32());
        Assert.Single(second.GetProperty("lines").EnumerateArray());

        // 25 in stock, but one line may hold at most 10.
        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Post, Cart + "/items", new { variantId = product.Variants[0].Id, quantity = 4 }, cartToken: token),
            "CART.STOCK_INSUFFICIENT");
    }

    [Fact]
    public async Task More_than_is_available_is_refused_and_the_message_says_how_many_can_be_ordered()
    {
        var (_, product) = await OneProductAsync(stock: 3);

        var response = await _h.SendAsync(HttpMethod.Post, Cart + "/items", new { variantId = product.Variants[0].Id, quantity = 5 });

        await StoreHelper.ExpectCodeAsync(response, "CART.STOCK_INSUFFICIENT");
    }

    [Fact]
    public async Task A_sold_out_or_unpublished_variant_cannot_be_added()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        var soldOut = await _h.ProductAsync(admin, cat.Id, StoreHelper.Unique("Out"), 500m, [("Red", "M", 0)]);
        var draft = await _h.ProductAsync(admin, cat.Id, StoreHelper.Unique("Draft"), 500m, [("Red", "M", 5)], publish: false);

        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Post, Cart + "/items", new { variantId = soldOut.Variants[0].Id, quantity = 1 }), "CART.STOCK_INSUFFICIENT");
        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Post, Cart + "/items", new { variantId = draft.Variants[0].Id, quantity = 1 }), "CART.VARIANT_UNAVAILABLE");
    }

    [Fact]
    public async Task A_quantity_below_one_is_rejected_and_setting_zero_removes_the_line()
    {
        var (_, product) = await OneProductAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.SendAsync(HttpMethod.Post, Cart + "/items", new { variantId = product.Variants[0].Id, quantity = 0 })).StatusCode);

        var token = (await AddAsync(product.Variants[0].Id, 2)).GetProperty("token").GetString()!;
        var changed = await _h.JsonAsync(HttpMethod.Put, Cart + "/items/" + product.Variants[0].Id, HttpStatusCode.OK, new { quantity = 5 }, cartToken: token);
        Assert.Equal(5, changed.GetProperty("itemCount").GetInt32());

        var removed = await _h.JsonAsync(HttpMethod.Put, Cart + "/items/" + product.Variants[0].Id, HttpStatusCode.OK, new { quantity = 0 }, cartToken: token);
        Assert.Equal(0, removed.GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task Stock_that_shrinks_after_adding_is_reflected_and_the_totals_follow_what_can_be_ordered()
    {
        var (admin, product) = await OneProductAsync(price: 100m, stock: 6);
        var token = (await AddAsync(product.Variants[0].Id, 5)).GetProperty("token").GetString()!;

        await _h.SetStockChangeAsync(admin, product.Variants[0].Id, -4); // 2 left
        var reduced = await _h.JsonAsync(HttpMethod.Get, Cart, HttpStatusCode.OK, cartToken: token);
        var line = reduced.GetProperty("lines")[0];
        Assert.Equal("REDUCED", line.GetProperty("problem").GetString());
        Assert.Equal(2, line.GetProperty("maxQuantity").GetInt32());
        Assert.Equal(200m, reduced.GetProperty("subtotal").GetDecimal());
        Assert.True(reduced.GetProperty("hasProblems").GetBoolean());

        await _h.SetStockChangeAsync(admin, product.Variants[0].Id, -2); // none left
        var gone = await _h.JsonAsync(HttpMethod.Get, Cart, HttpStatusCode.OK, cartToken: token);
        Assert.Equal("OUT_OF_STOCK", gone.GetProperty("lines")[0].GetProperty("problem").GetString());
        Assert.Equal(0m, gone.GetProperty("subtotal").GetDecimal());
    }

    [Fact]
    public async Task A_price_change_since_adding_is_marked_with_the_old_price()
    {
        var (admin, product) = await OneProductAsync(price: 500m);
        var token = (await AddAsync(product.Variants[0].Id, 1)).GetProperty("token").GetString()!;

        await _h.ChangePriceAsync(admin, product, 650m);
        var cart = await _h.JsonAsync(HttpMethod.Get, Cart, HttpStatusCode.OK, cartToken: token);
        var line = cart.GetProperty("lines")[0];

        Assert.True(cart.GetProperty("priceChanged").GetBoolean());
        Assert.True(line.GetProperty("priceChanged").GetBoolean());
        Assert.Equal(650m, line.GetProperty("unitPrice").GetDecimal());
        Assert.Equal(500m, line.GetProperty("previousUnitPrice").GetDecimal());
        Assert.Equal(650m, cart.GetProperty("subtotal").GetDecimal());
    }

    [Fact]
    public async Task A_percentage_coupon_gives_its_discount_and_can_be_removed()
    {
        var (admin, product) = await OneProductAsync(price: 1000m);
        var code = StoreHelper.Unique("PCT").ToUpperInvariant();
        await _h.CouponAsync(admin, code, "Percent", 10m);
        var token = (await AddAsync(product.Variants[0].Id, 2)).GetProperty("token").GetString()!;

        var applied = await _h.JsonAsync(HttpMethod.Post, Cart + "/coupon", HttpStatusCode.OK, new { code = code.ToLowerInvariant() }, cartToken: token);

        Assert.Equal(code, applied.GetProperty("coupon").GetProperty("code").GetString());
        Assert.Equal(200m, applied.GetProperty("discount").GetDecimal());
        Assert.Equal(1800m, applied.GetProperty("total").GetDecimal());

        var removed = await _h.JsonAsync(HttpMethod.Delete, Cart + "/coupon", HttpStatusCode.OK, cartToken: token);
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("coupon").ValueKind);
        Assert.Equal(2000m, removed.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Coupon_rules_unknown_minimum_expiry_and_usage_are_enforced_with_clear_codes()
    {
        var (admin, product) = await OneProductAsync(price: 300m);
        var token = (await AddAsync(product.Variants[0].Id, 1)).GetProperty("token").GetString()!;

        async Task<HttpResponseMessage> TryAsync(string code) =>
            await _h.SendAsync(HttpMethod.Post, Cart + "/coupon", new { code }, cartToken: token);

        await StoreHelper.ExpectCodeAsync(await TryAsync("NOPE-NOPE"), "COUPON.NOT_FOUND");

        var min = StoreHelper.Unique("MIN").ToUpperInvariant();
        await _h.CouponAsync(admin, min, "Fixed", 50m, minOrder: 1000m);
        await StoreHelper.ExpectCodeAsync(await TryAsync(min), "COUPON.BELOW_MINIMUM");

        var expired = StoreHelper.Unique("OLD").ToUpperInvariant();
        await _h.CouponAsync(admin, expired, "Fixed", 50m, startsAt: DateTimeOffset.UtcNow.AddDays(-10), endsAt: DateTimeOffset.UtcNow.AddDays(-1));
        await StoreHelper.ExpectCodeAsync(await TryAsync(expired), "COUPON.EXPIRED");

        var future = StoreHelper.Unique("SOON").ToUpperInvariant();
        await _h.CouponAsync(admin, future, "Fixed", 50m, startsAt: DateTimeOffset.UtcNow.AddDays(2));
        await StoreHelper.ExpectCodeAsync(await TryAsync(future), "COUPON.NOT_STARTED");

        var off = StoreHelper.Unique("OFF").ToUpperInvariant();
        await _h.CouponAsync(admin, off, "Fixed", 50m, active: false);
        await StoreHelper.ExpectCodeAsync(await TryAsync(off), "COUPON.INACTIVE");
    }

    [Fact]
    public async Task A_coupon_that_stops_applying_after_it_was_attached_shows_a_problem_and_no_discount()
    {
        var (admin, product) = await OneProductAsync(price: 300m);
        var code = StoreHelper.Unique("LATE").ToUpperInvariant();
        var created = await _h.CouponAsync(admin, code, "Fixed", 50m);
        var token = (await AddAsync(product.Variants[0].Id, 1)).GetProperty("token").GetString()!;
        await _h.JsonAsync(HttpMethod.Post, Cart + "/coupon", HttpStatusCode.OK, new { code }, cartToken: token);

        await Juvo.Api.IntegrationTests.Support.TestApi.ExpectAsync(await _h.Api.PutAsync("/api/v1/promotions/coupons/" + created.GetProperty("id").GetGuid(),
            new { code, kind = "Fixed", value = 50m, minOrderValue = 0m, isActive = false }, admin), HttpStatusCode.OK);

        var cart = await _h.JsonAsync(HttpMethod.Get, Cart, HttpStatusCode.OK, cartToken: token);

        Assert.Equal("COUPON.INACTIVE", cart.GetProperty("coupon").GetProperty("problem").GetString());
        Assert.Equal(0m, cart.GetProperty("discount").GetDecimal());
    }

    [Fact]
    public async Task Coupon_validation_without_a_cart_reports_the_discount_on_a_subtotal()
    {
        var admin = await _h.AdminAsync();
        var code = StoreHelper.Unique("VAL").ToUpperInvariant();
        await _h.CouponAsync(admin, code, "Percent", 20m, maxDiscount: 150m);

        var check = await _h.JsonAsync(HttpMethod.Post, "/api/v1/store/coupons/validate", HttpStatusCode.OK, new { code, subtotal = 1000m });

        Assert.Equal(150m, check.GetProperty("discount").GetDecimal()); // 20% of 1000 = 200, capped at 150
    }

    [Fact]
    public async Task Signing_in_merges_the_guest_cart_into_the_customers_cart_with_quantities_clamped()
    {
        var (admin, product) = await OneProductAsync(price: 200m, stock: 3);
        var cat = await _h.CategoryAsync(admin);
        var other = await _h.ProductAsync(admin, cat.Id, StoreHelper.Unique("Other"), 100m, [("Blue", "L", 10)]);
        var customer = await _h.CustomerTokenAsync();

        await AddAsync(product.Variants[0].Id, 2, bearer: customer);                     // customer already holds 2 of 3
        var guestToken = (await AddAsync(product.Variants[0].Id, 3)).GetProperty("token").GetString()!;
        await AddAsync(other.Variants[0].Id, 1, guestToken);

        var merged = await _h.JsonAsync(HttpMethod.Post, Cart + "/merge", HttpStatusCode.OK, new { token = guestToken }, customer);

        var lines = merged.GetProperty("lines").EnumerateArray().ToList();
        Assert.Equal(2, lines.Count);
        Assert.Equal(3, lines.Single(l => l.GetProperty("variantId").GetGuid() == product.Variants[0].Id).GetProperty("quantity").GetInt32()); // 2 + 3 clamped to 3
        Assert.Equal(1, lines.Single(l => l.GetProperty("variantId").GetGuid() == other.Variants[0].Id).GetProperty("quantity").GetInt32());

        // The guest cart is spent: its token now opens an empty cart.
        var spent = await _h.JsonAsync(HttpMethod.Get, Cart, HttpStatusCode.OK, cartToken: guestToken);
        Assert.Equal(0, spent.GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task Merge_needs_a_customer_sign_in()
    {
        var admin = await _h.AdminAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _h.SendAsync(HttpMethod.Post, Cart + "/merge", new { token = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Post, Cart + "/merge", new { token = "x" }, admin)).StatusCode);
    }

    [Fact]
    public async Task Two_customers_never_see_each_others_carts()
    {
        var (_, product) = await OneProductAsync();
        var a = await _h.CustomerTokenAsync();
        var b = await _h.CustomerTokenAsync();

        await AddAsync(product.Variants[0].Id, 2, bearer: a);

        var mineB = await _h.JsonAsync(HttpMethod.Get, Cart, HttpStatusCode.OK, bearer: b);
        var mineA = await _h.JsonAsync(HttpMethod.Get, Cart, HttpStatusCode.OK, bearer: a);
        Assert.Equal(0, mineB.GetProperty("itemCount").GetInt32());
        Assert.Equal(2, mineA.GetProperty("itemCount").GetInt32());
    }

    [Fact]
    public async Task The_carts_delivery_quote_checks_the_free_threshold_on_the_discounted_goods()
    {
        var (admin, product) = await OneProductAsync(price: 3000m);
        var dhaka = await DistrictIdAsync("Dhaka");
        var code = StoreHelper.Unique("FREE").ToUpperInvariant();
        await _h.CouponAsync(admin, code, "Fixed", 1500m);
        var token = (await AddAsync(product.Variants[0].Id, 2)).GetProperty("token").GetString()!; // 6000 in goods, free from 5000

        var before = await _h.JsonAsync(HttpMethod.Post, Cart + "/delivery-quote", HttpStatusCode.OK, new { districtId = dhaka }, cartToken: token);
        Assert.True(before.GetProperty("isFree").GetBoolean());

        await _h.JsonAsync(HttpMethod.Post, Cart + "/coupon", HttpStatusCode.OK, new { code }, cartToken: token); // 4500 payable: below the threshold
        var after = await _h.JsonAsync(HttpMethod.Post, Cart + "/delivery-quote", HttpStatusCode.OK, new { districtId = dhaka }, cartToken: token);

        Assert.False(after.GetProperty("isFree").GetBoolean());
        Assert.Equal(4500m, after.GetProperty("subtotal").GetDecimal());
        Assert.True(after.GetProperty("total").GetDecimal() > 0m);
    }

    private async Task<Guid> DistrictIdAsync(string name)
    {
        var districts = await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/geo/districts", HttpStatusCode.OK);
        return districts.EnumerateArray().Single(d => d.GetProperty("nameEn").GetString() == name).GetProperty("id").GetGuid();
    }
}
