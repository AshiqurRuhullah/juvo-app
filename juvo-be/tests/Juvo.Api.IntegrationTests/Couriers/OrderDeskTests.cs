using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using static Juvo.Api.IntegrationTests.Couriers.CourierHelper;

namespace Juvo.Api.IntegrationTests.Couriers;

/// <summary>The two contract gaps of Step 8-b: the order desk preview and the guest cancel from the tracking page.</summary>
public class OrderDeskTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly CourierHelper _c = new(new OrderHelper(new StoreHelper(factory)), factory);

    private StoreHelper H => _c.H;

    private OrderHelper O => _c.O;

    private async Task<object> BodyAsync(IReadOnlyList<(string Sku, int Quantity)> lines, string? coupon = null, decimal? discount = null, decimal? deliveryOverride = null, string method = "COD", string mobile = "", string source = "Facebook") =>
        new
        {
            customerMobile = string.IsNullOrEmpty(mobile) ? StoreHelper.NewMobile() : mobile,
            customerName = "Nadia Rahman",
            source,
            address = new { recipientName = "Nadia Rahman", countryCode = "BD", districtId = await O.DistrictAsync(), addressLine = "House 3, Road 9, Gulshan" },
            lines = lines.Select(l => new { sku = l.Sku, quantity = l.Quantity }).ToArray(),
            paymentMethod = method,
            couponCode = coupon,
            discount,
            deliveryChargeOverride = deliveryOverride,
            confirmDuplicate = true,
        };

    // ---- manual order preview -------------------------------------------------------------------------------

    [Fact]
    public async Task The_desk_preview_shows_the_totals_of_the_order_that_create_makes_and_saves_nothing()
    {
        var (admin, product) = await _c.ProductAsync(700m, 10);
        var variant = product.Variants[0];
        var code = StoreHelper.Unique("DESK").ToUpperInvariant();
        await H.CouponAsync(admin, code, "Percent", 10m);
        var staff = await O.StaffTokenAsync(admin, null, "orders.order.create", "orders.order.view", "orders.order.apply-discount");
        var mobile = StoreHelper.NewMobile();
        var body = await BodyAsync([(variant.Sku, 3)], coupon: code, discount: 50m, mobile: mobile);

        var before = await O.StockAsync(variant.Id);
        var quote = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", HttpStatusCode.OK, body, staff);

        Assert.Equal(2100m, quote.GetProperty("subtotal").GetDecimal());
        Assert.Equal(210m, quote.GetProperty("couponDiscount").GetDecimal());
        Assert.Equal(50m, quote.GetProperty("manualDiscount").GetDecimal());
        Assert.Equal(code, Str(quote, "couponCode"));
        Assert.Equal("COD", Str(quote, "paymentMethod"));
        Assert.Equal(900, quote.GetProperty("weightGrams").GetInt32()); // three units of 300 g
        Assert.Equal(1, quote.GetProperty("lines").GetArrayLength());
        Assert.Equal(variant.Sku, Str(quote.GetProperty("lines")[0], "sku"));
        Assert.Equal(2100m, quote.GetProperty("lines")[0].GetProperty("lineTotal").GetDecimal());
        Assert.Equal(10, quote.GetProperty("lines")[0].GetProperty("availableInBranch").GetInt32());

        // Nothing was reserved, saved or counted: stock, orders and the coupon are as before.
        Assert.Equal(before, await O.StockAsync(variant.Id));
        Assert.Equal(0, (await H.JsonAsync(HttpMethod.Get, $"/api/v1/orders?search={Uri.EscapeDataString(mobile)}", HttpStatusCode.OK, bearer: admin)).GetProperty("total").GetInt32());
        var coupons = await H.JsonAsync(HttpMethod.Get, $"/api/v1/promotions/coupons?search={code}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(0, coupons.GetProperty("items")[0].GetProperty("usedCount").GetInt32());

        // Create it for real: every amount equals the preview.
        var created = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/manual", HttpStatusCode.Created, body, staff);
        var money = created.GetProperty("money");
        Assert.Equal(quote.GetProperty("subtotal").GetDecimal(), money.GetProperty("subtotal").GetDecimal());
        Assert.Equal(quote.GetProperty("couponDiscount").GetDecimal(), money.GetProperty("couponDiscount").GetDecimal());
        Assert.Equal(quote.GetProperty("manualDiscount").GetDecimal(), money.GetProperty("manualDiscount").GetDecimal());
        Assert.Equal(quote.GetProperty("deliveryCharge").GetDecimal(), money.GetProperty("deliveryCharge").GetDecimal());
        Assert.Equal(quote.GetProperty("grandTotal").GetDecimal(), money.GetProperty("grandTotal").GetDecimal());
        Assert.Equal(quote.GetProperty("advanceRequired").GetDecimal(), money.GetProperty("advanceRequired").GetDecimal());
        Assert.Equal(quote.GetProperty("weightGrams").GetInt32(), created.GetProperty("weightGrams").GetInt32());
        Assert.Equal(quote.GetProperty("zoneCode").GetString(), created.GetProperty("zoneCode").GetString());
        Assert.Equal(quote.GetProperty("branchId").GetGuid(), created.GetProperty("branchId").GetGuid());
        Assert.Equal(quote.GetProperty("grandTotal").GetDecimal(), 2100m - 210m - 50m + quote.GetProperty("deliveryCharge").GetDecimal());
    }

    [Fact]
    public async Task The_desk_preview_has_the_same_rules_and_permissions_as_create()
    {
        var (admin, product) = await _c.ProductAsync(700m, 4);
        var variant = product.Variants[0];
        var plain = await O.StaffTokenAsync(admin, null, "orders.order.create", "orders.order.view");
        var viewer = await O.StaffTokenAsync(admin, null, "orders.order.view");
        var overrider = await O.StaffTokenAsync(admin, null, "orders.order.create", "orders.order.override-delivery");

        // Same permission as create.
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", await BodyAsync([(variant.Sku, 1)]), viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", await BodyAsync([(variant.Sku, 1)]))).StatusCode);
        // A discount and a delivery override need their own permissions, just as when creating.
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", await BodyAsync([(variant.Sku, 1)], discount: 50m), plain)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", await BodyAsync([(variant.Sku, 1)], deliveryOverride: 10m), plain)).StatusCode);
        var overridden = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", HttpStatusCode.OK, await BodyAsync([(variant.Sku, 1)], deliveryOverride: 10m), overrider);
        Assert.Equal(10m, overridden.GetProperty("deliveryCharge").GetDecimal());
        Assert.Equal(710m, overridden.GetProperty("grandTotal").GetDecimal());

        // Same validation and refusals.
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", await BodyAsync([("NOPE-SKU", 1)]), plain), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", await BodyAsync([(variant.Sku, 5)]), plain), "ORDER.STOCK_INSUFFICIENT");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", await BodyAsync([(variant.Sku, 1)], coupon: "NO-SUCH-COUPON"), plain), "COUPON.NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", await BodyAsync([(variant.Sku, 0)]), plain), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual/quote", await BodyAsync([(variant.Sku, 1)], source: "Web"), plain), "GENERAL.VALIDATION");
    }

    // ---- guest cancel ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_guest_cancels_from_the_tracking_page_while_the_order_has_not_left_the_shop()
    {
        var (admin, product) = await _c.ProductAsync(500m, 10);
        var variant = product.Variants[0].Id;
        var placed = await O.PlaceAsync(variant, 2);
        var number = Str(placed, "number");
        var id = placed.GetProperty("orderId").GetGuid();
        var mobile = (await O.OrderAsync(admin, id)).GetProperty("customerMobile").GetString()!;
        var token = Str(placed, "trackingToken");
        Assert.Equal((10, 2), await O.StockAsync(variant));

        // Wrong proofs are the same neutral 404 and cancel nothing.
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track/cancel", new { number, mobile = StoreHelper.NewMobile() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track/cancel", new { number = "JV-HO-0000-000000", mobile })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track/cancel", new { token = "wrong" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track/cancel", new { })).StatusCode);
        Assert.Equal("Placed", Str(await O.OrderAsync(admin, id), "status"));

        var cancelled = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/orders/track/cancel", HttpStatusCode.OK, new { number, mobile, reason = "Changed my mind" });
        Assert.Equal("Cancelled", Str(cancelled, "status"));
        Assert.False(cancelled.GetProperty("canCancel").GetBoolean());
        Assert.Equal((10, 0), await O.StockAsync(variant)); // the reservation is back
        var detail = await O.OrderAsync(admin, id);
        Assert.Equal("Cancelled", Str(detail, "status"));
        Assert.Equal("Cancelled", Str(detail, "paymentStatus"));
        Assert.Equal("Changed my mind", Str(detail, "cancelReason"));
        Assert.Contains(detail.GetProperty("timeline").EnumerateArray(), t => Str(t, "to") == "Cancelled");

        // A second cancel is refused by the cancel rules.
        var again = await H.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track/cancel", new { token });
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        await StoreHelper.ExpectCodeAsync(again, "ORDER.CANNOT_CANCEL");
    }

    [Fact]
    public async Task A_guest_can_cancel_a_packed_order_but_not_one_the_courier_has_picked_up()
    {
        var (admin, product) = await _c.ProductAsync(500m, 10);
        var variant = product.Variants[0].Id;

        // Packed and booked, not picked up: allowed, the booking is cancelled with it and the shelf is restocked.
        var early = await _c.PackedAsync(admin, variant, 2);
        var booked = await _c.BookAsync(admin, early.OrderId);
        Assert.Equal((8, 0), await O.StockAsync(variant));
        var cancelled = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/orders/track/cancel", HttpStatusCode.OK, new { token = early.Token });
        Assert.Equal("Cancelled", Str(cancelled, "status"));
        Assert.Equal("Cancelled", Str(await _c.ShipmentAsync(admin, booked.GetProperty("id").GetGuid()), "status"));
        Assert.Equal((10, 0), await O.StockAsync(variant));
        Assert.Equal(JsonValueKind.Null, cancelled.GetProperty("shipment").ValueKind); // a cancelled booking is not shown to the customer

        // Handed to the courier: too late.
        var late = await _c.PackedAsync(admin, variant, 1);
        var parcel = await _c.BookAsync(admin, late.OrderId);
        await _c.AdvanceAsync(admin, parcel.GetProperty("id").GetGuid());
        var refused = await H.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track/cancel", new { number = late.Number, mobile = late.Mobile });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        await StoreHelper.ExpectCodeAsync(refused, "ORDER.CANNOT_CANCEL");
        Assert.Equal("HandedToCourier", Str(await O.OrderAsync(admin, late.OrderId), "status"));
        Assert.Equal("PickedUp", Str(await _c.ShipmentAsync(admin, parcel.GetProperty("id").GetGuid()), "status"));
    }

    [Fact]
    public async Task A_guest_reason_cannot_be_longer_than_the_staff_reason()
    {
        var (_, product) = await _c.ProductAsync(500m, 5);
        var placed = await O.PlaceAsync(product.Variants[0].Id, 1);
        var response = await H.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track/cancel", new { token = Str(placed, "trackingToken"), reason = new string('x', 301) });
        await StoreHelper.ExpectCodeAsync(response, "GENERAL.VALIDATION");
    }
}

/// <summary>A shop whose public checkout limit is low, to prove the guest cancel is rate limited.</summary>
public sealed class LimitedStoreFactory : StoreFactory
{
    protected override void ExtraSettings()
    {
        Setting("RateLimiting:Enabled", "true");
        Setting("RateLimiting:CheckoutPerMinute", "5");
        Setting("RateLimiting:GlobalPerMinute", "10000");
        Setting("RateLimiting:StorefrontPerMinute", "10000");
    }
}

public class PublicCancelRateLimitTests(LimitedStoreFactory factory) : IClassFixture<LimitedStoreFactory>
{
    [Fact]
    public async Task The_guest_cancel_and_the_hub_token_share_the_checkout_limit_and_answer_429_when_it_is_used_up()
    {
        var h = new StoreHelper(factory);
        var o = new OrderHelper(h);
        var admin = await h.AdminAsync();
        var category = await h.CategoryAsync(admin);
        var product = await h.ProductAsync(admin, category.Id, StoreHelper.Unique("Limit Tee"), 400m, [("Red", "M", 5)], weightGrams: 200);
        var placed = await o.PlaceAsync(product.Variants[0].Id, 1); // one checkout call

        // Guessing proofs uses the same allowance as checking out: four more calls, then the limit.
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            statuses.Add((await h.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track/cancel", new { number = placed.GetProperty("number").GetString(), mobile = StoreHelper.NewMobile() })).StatusCode);
        }

        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests], statuses);
        var limited = await h.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track/realtime-token", new { token = "x" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        await StoreHelper.ExpectCodeAsync(limited, "GENERAL.TOO_MANY_REQUESTS");
    }
}
