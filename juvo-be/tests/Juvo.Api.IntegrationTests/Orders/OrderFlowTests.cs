using System.Net;
using System.Text.RegularExpressions;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Orders;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;
using Juvo.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juvo.Api.IntegrationTests.Orders;

public partial class OrderFlowTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly OrderHelper _o = new(new StoreHelper(factory));

    private StoreHelper H => _o.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^JV-HO-\d{4}-\d{6}$")]
    private static partial Regex NumberFormat();

    private async Task<(string Admin, SeededProduct Product)> ProductAsync(decimal price = 500m, int stock = 20, string colour = "Red", string size = "M")
    {
        var admin = await H.AdminAsync();
        var category = await H.CategoryAsync(admin);
        var product = await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Order Tee"), price, [(colour, size, stock)], weightGrams: 300);
        return (admin, product);
    }

    // ---- checkout --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_guest_with_only_a_mobile_number_places_a_cash_on_delivery_order_and_can_track_it()
    {
        var (admin, product) = await ProductAsync(price: 800m, stock: 10);
        var variant = product.Variants[0].Id;
        var mobile = StoreHelper.NewMobile();
        var cart = await _o.CartAsync(variant, 2);

        var quote = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/checkout/quote", HttpStatusCode.OK,
            new { address = new { countryCode = "BD", districtId = await _o.DistrictAsync() }, paymentMethod = "COD" }, cartToken: cart);
        Assert.Equal(1600m, quote.GetProperty("subtotal").GetDecimal());
        Assert.True(quote.GetProperty("otpRequired").GetBoolean());
        Assert.True(quote.GetProperty("canPlace").GetBoolean());
        var grand = quote.GetProperty("grandTotal").GetDecimal();
        Assert.Equal(1600m + quote.GetProperty("deliveryCharge").GetDecimal(), grand);

        var placed = await TestApi.ExpectAsync(await _o.TryCheckoutAsync(cart, mobile, new CheckoutOptions(Expected: grand, Note: "Gift wrap please")), HttpStatusCode.Created);

        var number = placed.GetProperty("number").GetString()!;
        Assert.Matches(NumberFormat(), number);
        Assert.Equal("Placed", placed.GetProperty("status").GetString());
        Assert.Equal("Pending", placed.GetProperty("paymentStatus").GetString());
        Assert.Equal(grand, placed.GetProperty("grandTotal").GetDecimal());
        Assert.Equal(0m, placed.GetProperty("payNow").GetDecimal());
        Assert.True(placed.GetProperty("offerProfile").GetBoolean());
        var token = placed.GetProperty("trackingToken").GetString()!;

        // Stock is reserved, not yet taken off the shelf, and the cart is empty.
        Assert.Equal((10, 2), await _o.StockAsync(variant));
        var cartAfter = await H.JsonAsync(HttpMethod.Get, "/api/v1/store/cart", HttpStatusCode.OK, cartToken: cart);
        Assert.Equal(0, cartAfter.GetProperty("itemCount").GetInt32());

        // Public tracking: number + mobile, or the token. Anything else is the same neutral 404.
        var byMobile = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/orders/track", HttpStatusCode.OK, new { number, mobile });
        Assert.Equal("Placed", byMobile.GetProperty("status").GetString());
        Assert.False(byMobile.TryGetProperty("shipping", out _));
        Assert.DoesNotContain("House 12", byMobile.ToString(), StringComparison.Ordinal);
        await H.JsonAsync(HttpMethod.Get, $"/api/v1/store/orders/track?token={token}", HttpStatusCode.OK);
        await H.JsonAsync(HttpMethod.Get, $"/api/v1/store/orders/track?number={number}&mobile={mobile}", HttpStatusCode.OK);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track", new { number, mobile = StoreHelper.NewMobile() }), "ORDER.NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track", new { token = "wrong" }), "ORDER.NOT_FOUND");

        // The staff see the snapshot, the three tracks, the timeline and the customer note; a customer profile was created from the mobile (BR-006).
        var detail = await _o.OrderAsync(admin, placed.GetProperty("orderId").GetGuid());
        Assert.Equal("Web", detail.GetProperty("source").GetString());
        Assert.Equal("Placed", detail.GetProperty("status").GetString());
        Assert.Equal("NotShipped", detail.GetProperty("deliveryStatus").GetString());
        Assert.Equal(3, detail.GetProperty("timeline").GetArrayLength());
        Assert.Contains(detail.GetProperty("notes").EnumerateArray(), n => n.GetProperty("text").GetString() == "Gift wrap please" && n.GetProperty("kind").GetString() == "Customer");
        var line = detail.GetProperty("items")[0];
        Assert.Equal(product.Variants[0].Sku, line.GetProperty("sku").GetString());
        Assert.Equal(800m, line.GetProperty("unitPrice").GetDecimal());
        Assert.True(detail.GetProperty("otpVerified").GetBoolean());
        Assert.Equal(1, await _o.WithDbAsync(db => db.Customers.CountAsync(c => c.Mobile == "+88" + mobile, Ct)));
    }

    [Fact]
    public async Task Order_numbers_are_consecutive_per_branch_and_month()
    {
        var (_, product) = await ProductAsync(stock: 20);
        var first = await _o.PlaceAsync(product.Variants[0].Id, 1);
        var second = await _o.PlaceAsync(product.Variants[0].Id, 1);

        var a = int.Parse(first.GetProperty("number").GetString()![^6..], System.Globalization.CultureInfo.InvariantCulture);
        var b = int.Parse(second.GetProperty("number").GetString()![^6..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(first.GetProperty("number").GetString()![..^6], second.GetProperty("number").GetString()![..^6]);
        Assert.True(b > a);
        var numbers = await _o.WithDbAsync(db => db.Orders.IgnoreQueryFilters(["BranchScope"]).Where(o => o.Number.StartsWith("JV-HO-")).Select(o => o.Number).ToListAsync(Ct));
        var sequence = numbers.Select(n => int.Parse(n[^6..], System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
        Assert.Equal(Enumerable.Range(1, sequence.Count).ToList(), sequence); // gap free
    }

    [Fact]
    public async Task OTP_is_needed_for_cash_on_delivery_and_a_wrong_or_missing_code_is_refused()
    {
        var (_, product) = await ProductAsync();
        var variant = product.Variants[0].Id;
        var mobile = StoreHelper.NewMobile();
        var cart = await _o.CartAsync(variant, 1);

        await StoreHelper.ExpectCodeAsync(await _o.TryCheckoutAsync(cart, mobile, new CheckoutOptions(SkipOtp: true)), "ORDER.OTP_REQUIRED");
        await _o.OtpAsync(mobile);
        await StoreHelper.ExpectCodeAsync(await _o.TryCheckoutAsync(cart, mobile, new CheckoutOptions(OtpCode: "000000")), "AUTH.OTP_INVALID");
        Assert.Equal((20, 0), await _o.StockAsync(variant));

        // Terms must be accepted.
        await StoreHelper.ExpectCodeAsync(await _o.TryCheckoutAsync(cart, mobile, new CheckoutOptions(AcceptTerms: false)), "GENERAL.VALIDATION");

        // A signed-in customer with a verified mobile does not need a code (and orders from their own cart).
        var bearer = await H.CustomerTokenAsync(mobile);
        await H.JsonAsync(HttpMethod.Post, "/api/v1/store/cart/items", HttpStatusCode.OK, new { variantId = variant, quantity = 1 }, bearer);
        var signedIn = await TestApi.ExpectAsync(await _o.TryCheckoutAsync(null, mobile, bearer: bearer), HttpStatusCode.Created);
        Assert.False(signedIn.GetProperty("offerProfile").GetBoolean());
    }

    [Fact]
    public async Task A_changed_total_is_reported_and_a_repeated_request_with_the_same_key_returns_the_same_order()
    {
        var (_, product) = await ProductAsync(price: 300m);
        var variant = product.Variants[0].Id;
        var mobile = StoreHelper.NewMobile();
        var cart = await _o.CartAsync(variant, 1);

        await StoreHelper.ExpectCodeAsync(await _o.TryCheckoutAsync(cart, mobile, new CheckoutOptions(Expected: 1m)), "ORDER.TOTAL_CHANGED");
        Assert.Equal((20, 0), await _o.StockAsync(variant));

        var key = "key-" + Guid.NewGuid().ToString("N");
        var first = await TestApi.ExpectAsync(await _o.TryCheckoutAsync(cart, mobile, new CheckoutOptions(Key: key)), HttpStatusCode.Created);
        var again = await TestApi.ExpectAsync(await _o.TryCheckoutAsync(cart, mobile, new CheckoutOptions(Key: key)), HttpStatusCode.Created);

        Assert.Equal(first.GetProperty("orderId").GetGuid(), again.GetProperty("orderId").GetGuid());
        Assert.True(again.GetProperty("replayed").GetBoolean());
        Assert.Equal((20, 1), await _o.StockAsync(variant)); // reserved once, not twice

        // The same key for a different basket is an error, not a second order.
        var other = await _o.CartAsync(variant, 2);
        await StoreHelper.ExpectCodeAsync(await _o.TryCheckoutAsync(other, mobile, new CheckoutOptions(Key: key)), "GENERAL.IDEMPOTENCY_KEY_REUSED");
    }

    [Fact]
    public async Task The_same_mobile_ordering_the_same_items_again_within_minutes_gets_a_warning_until_confirmed()
    {
        var (_, product) = await ProductAsync(stock: 30);
        var variant = product.Variants[0].Id;
        var mobile = StoreHelper.NewMobile();
        var first = await _o.PlaceAsync(variant, 1, mobile);

        var cart = await _o.CartAsync(variant, 1);
        var warning = await _o.TryCheckoutAsync(cart, mobile);
        await StoreHelper.ExpectCodeAsync(warning, "ORDER.DUPLICATE_SUSPECTED");

        var confirmed = await TestApi.ExpectAsync(await _o.TryCheckoutAsync(cart, mobile, new CheckoutOptions(ConfirmDuplicate: true)), HttpStatusCode.Created);
        Assert.NotEqual(first.GetProperty("number").GetString(), confirmed.GetProperty("number").GetString());
    }

    [Fact]
    public async Task Cash_on_delivery_above_the_threshold_needs_the_delivery_charge_in_advance()
    {
        var (admin, product) = await ProductAsync(price: 12000m, stock: 5);
        // A new customer's large COD order is also held for review by default (module T); this test is about the advance, so let the rule only flag.
        await H.JsonAsync(HttpMethod.Put, "/api/v1/fraud/rules/HIGH_COD_NEW_CUSTOMER", HttpStatusCode.OK, new { enabled = true, threshold = 5000, action = "Flag" }, admin);
        var cart = await _o.CartAsync(product.Variants[0].Id, 1);

        var quote = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/checkout/quote", HttpStatusCode.OK,
            new { address = new { countryCode = "BD", districtId = await _o.DistrictAsync() }, paymentMethod = "COD" }, cartToken: cart);
        // Delivery is free at this value, but the advance is what the courier costs (base + weight + remote surcharge), not the zero the customer pays.
        var delivery = quote.GetProperty("delivery");
        Assert.Equal(0m, quote.GetProperty("deliveryCharge").GetDecimal());
        var charge = delivery.GetProperty("total").GetDecimal() + delivery.GetProperty("freeDeliveryWaiver").GetDecimal();
        Assert.True(charge > 0);
        Assert.Equal(charge, quote.GetProperty("advanceRequired").GetDecimal());
        Assert.Equal(charge, quote.GetProperty("payNow").GetDecimal());
        Assert.Equal(12000m - charge, quote.GetProperty("payOnDelivery").GetDecimal());

        var placed = await TestApi.ExpectAsync(await _o.TryCheckoutAsync(cart, StoreHelper.NewMobile()), HttpStatusCode.Created);
        var id = placed.GetProperty("orderId").GetGuid();
        Assert.Equal(charge, placed.GetProperty("advanceRequired").GetDecimal());

        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "confirm", new { advanceReceived = false }), "ORDER.ADVANCE_REQUIRED");
        var confirmed = await _o.ActAsync(admin, id, "confirm", new { advanceReceived = true });
        Assert.Equal("Confirmed", confirmed.GetProperty("status").GetString());
        Assert.Contains(confirmed.GetProperty("notes").EnumerateArray(), n => n.GetProperty("text").GetString()!.Contains("Advance", StringComparison.Ordinal));
        var slip = await H.JsonAsync(HttpMethod.Get, $"/api/v1/orders/{id}/packing-slip", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(12000m - charge, slip.GetProperty("collectOnDelivery").GetDecimal());
    }

    [Fact]
    public async Task An_online_payment_order_waits_for_payment_and_cannot_be_confirmed_before_it()
    {
        var (admin, product) = await ProductAsync();
        var placed = await _o.PlaceAsync(product.Variants[0].Id, 1, options: new CheckoutOptions(Method: "SANDBOX"));
        var id = placed.GetProperty("orderId").GetGuid();

        Assert.True(placed.GetProperty("payNow").GetDecimal() > 0);
        Assert.Equal(0m, placed.GetProperty("payOnDelivery").GetDecimal());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, placed.GetProperty("paymentDeadline").ValueKind);
        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "confirm"), "ORDER.PAYMENT_PENDING");
    }


    [Fact]
    public async Task Cash_on_delivery_is_not_offered_abroad_and_unknown_methods_are_refused()
    {
        var (_, product) = await ProductAsync();
        var cart = await _o.CartAsync(product.Variants[0].Id, 1);
        var mobile = StoreHelper.NewMobile();

        await StoreHelper.ExpectCodeAsync(await _o.TryCheckoutAsync(cart, mobile, new CheckoutOptions(Method: "BITCOIN", SkipOtp: true)), "ORDER.PAYMENT_METHOD_UNAVAILABLE");
        var quote = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/checkout/quote", HttpStatusCode.OK,
            new { address = new { countryCode = "IN" }, paymentMethod = "COD" }, cartToken: cart);
        var methods = quote.GetProperty("paymentMethods").EnumerateArray().ToDictionary(m => m.GetProperty("code").GetString()!, m => m.GetProperty("available").GetBoolean());
        Assert.False(methods["COD"]);
        Assert.True(methods["SANDBOX"]);
        Assert.False(quote.GetProperty("canPlace").GetBoolean());
    }

    // ---- processing: confirm, pack, deliver ---------------------------------------------------------------

    [Fact]
    public async Task A_guest_order_is_confirmed_packed_and_delivered_and_the_customer_sees_every_step()
    {
        var (admin, product) = await ProductAsync(stock: 10);
        var variant = product.Variants[0].Id;
        var mobile = StoreHelper.NewMobile();
        var placed = await _o.PlaceAsync(variant, 3, mobile);
        var id = placed.GetProperty("orderId").GetGuid();
        Assert.Equal((10, 3), await _o.StockAsync(variant));

        var confirmed = await _o.ActAsync(admin, id, "confirm");
        Assert.Equal("Confirmed", confirmed.GetProperty("status").GetString());
        Assert.Equal((10, 3), await _o.StockAsync(variant)); // still only reserved

        var packed = await _o.ActAsync(admin, id, "pack");
        Assert.Equal("Packed", packed.GetProperty("status").GetString());
        Assert.Equal("ReadyToShip", packed.GetProperty("deliveryStatus").GetString());
        Assert.True(packed.GetProperty("stockDeducted").GetBoolean());
        Assert.Equal((7, 0), await _o.StockAsync(variant)); // packing takes it off the shelf

        foreach (var step in new[] { "HandedToCourier", "InTransit", "OutForDelivery", "Delivered" })
        {
            await _o.ActAsync(admin, id, "transition", new { to = step });
        }

        var done = await _o.OrderAsync(admin, id);
        Assert.Equal("Delivered", done.GetProperty("status").GetString());
        Assert.Equal("CollectedByCourier", done.GetProperty("paymentStatus").GetString());
        Assert.Equal("Delivered", done.GetProperty("deliveryStatus").GetString());
        var orderTrack = done.GetProperty("timeline").EnumerateArray().Where(t => t.GetProperty("track").GetString() == "Order").Select(t => t.GetProperty("to").GetString()).ToList();
        Assert.Equal(["Placed", "Confirmed", "Packed", "HandedToCourier", "InTransit", "OutForDelivery", "Delivered"], orderTrack);
        Assert.All(done.GetProperty("timeline").EnumerateArray().Where(t => t.GetProperty("to").GetString() != "Placed" && t.GetProperty("track").GetString() == "Order"),
            t => Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("actorName").GetString()))); // staff see who did each step

        // The customer signs in with the same mobile and finds the order with its timeline (no staff names).
        var customer = await H.CustomerTokenAsync(mobile);
        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/orders?filter=delivered", HttpStatusCode.OK, bearer: customer);
        var mine = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(placed.GetProperty("number").GetString(), mine.GetProperty("number").GetString());
        Assert.False(mine.GetProperty("canCancel").GetBoolean());
        var active = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/orders?filter=active", HttpStatusCode.OK, bearer: customer);
        Assert.Equal(0, active.GetProperty("total").GetInt32());
        var detail = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/orders/{id}", HttpStatusCode.OK, bearer: customer);
        Assert.Equal("Delivered", detail.GetProperty("status").GetString());
        Assert.All(detail.GetProperty("timeline").EnumerateArray(), t => Assert.Equal(System.Text.Json.JsonValueKind.Null, t.GetProperty("actorName").ValueKind));
        var summary = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/summary", HttpStatusCode.OK, bearer: customer);
        Assert.Equal(1, summary.GetProperty("orderCount").GetInt32());

        // Another customer cannot open it.
        var stranger = await H.CustomerTokenAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/me/orders/{id}", bearer: stranger)).StatusCode);
    }

    [Fact]
    public async Task Invalid_transitions_are_rejected_with_a_stable_code_and_change_nothing()
    {
        var (admin, product) = await ProductAsync();
        var variant = product.Variants[0].Id;
        var id = (await _o.PlaceAsync(variant, 1)).GetProperty("orderId").GetGuid();

        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "pack"), "ORDER.INVALID_TRANSITION");
        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "transition", new { to = "Delivered" }), "ORDER.INVALID_TRANSITION");
        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "transition", new { to = "Cancelled" }), "ORDER.INVALID_TRANSITION"); // cancel has its own endpoint
        await _o.ActAsync(admin, id, "confirm");
        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "confirm"), "ORDER.INVALID_TRANSITION");

        var order = await _o.OrderAsync(admin, id);
        Assert.Equal("Confirmed", order.GetProperty("status").GetString());
        Assert.Equal(2, await _o.WithDbAsync(db => db.OrderStatusHistory.CountAsync(h => h.OrderId == id && h.Track == OrderTrack.Order, Ct))); // Placed and Confirmed only
    }

    [Fact]
    public async Task Cancelling_releases_the_reservation_and_a_delivered_order_cannot_be_cancelled()
    {
        var (admin, product) = await ProductAsync(stock: 10);
        var variant = product.Variants[0].Id;
        var id = (await _o.PlaceAsync(variant, 4)).GetProperty("orderId").GetGuid();
        Assert.Equal((10, 4), await _o.StockAsync(variant));

        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "cancel", new { reason = " " }), "GENERAL.VALIDATION");
        var cancelled = await _o.ActAsync(admin, id, "cancel", new { reason = "Customer called" });

        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("Cancelled", cancelled.GetProperty("paymentStatus").GetString());
        Assert.Equal("Cancelled", cancelled.GetProperty("deliveryStatus").GetString());
        Assert.Equal((10, 0), await _o.StockAsync(variant));
        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "cancel", new { reason = "again" }), "ORDER.CANNOT_CANCEL");

        // Cancelling after packing puts the units back on the shelf.
        var packedId = (await _o.PlaceAsync(variant, 2)).GetProperty("orderId").GetGuid();
        await _o.ActAsync(admin, packedId, "confirm");
        await _o.ActAsync(admin, packedId, "pack");
        Assert.Equal((8, 0), await _o.StockAsync(variant));
        await _o.ActAsync(admin, packedId, "cancel", new { reason = "Changed mind" });
        Assert.Equal((10, 0), await _o.StockAsync(variant));

        // Once handed to the courier the order can no longer be cancelled.
        var shippedId = (await _o.PlaceAsync(variant, 1)).GetProperty("orderId").GetGuid();
        await _o.ActAsync(admin, shippedId, "confirm");
        await _o.ActAsync(admin, shippedId, "pack");
        await _o.ActAsync(admin, shippedId, "transition", new { to = "HandedToCourier" });
        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, shippedId, "cancel", new { reason = "too late" }), "ORDER.CANNOT_CANCEL");
    }

    [Fact]
    public async Task A_customer_cancels_their_own_order_while_it_is_in_the_shop_and_the_stock_returns()
    {
        var (_, product) = await ProductAsync(stock: 6);
        var variant = product.Variants[0].Id;
        var mobile = StoreHelper.NewMobile();
        var id = (await _o.PlaceAsync(variant, 2, mobile)).GetProperty("orderId").GetGuid();
        var customer = await H.CustomerTokenAsync(mobile);
        Assert.Equal((6, 2), await _o.StockAsync(variant));

        var cancelled = await H.JsonAsync(HttpMethod.Post, $"/api/v1/me/orders/{id}/cancel", HttpStatusCode.OK, new { reason = "Ordered by mistake" }, customer);

        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Contains(cancelled.GetProperty("timeline").EnumerateArray(), t => t.GetProperty("to").GetString() == "Cancelled" && t.GetProperty("reason").GetString() == "Ordered by mistake");
        Assert.Equal((6, 0), await _o.StockAsync(variant));
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/me/orders/{id}/cancel", new { }, customer), "ORDER.CANNOT_CANCEL");
        var stranger = await H.CustomerTokenAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, $"/api/v1/me/orders/{id}/cancel", new { }, stranger)).StatusCode);
    }

    [Fact]
    public async Task The_order_keeps_the_prices_and_names_of_the_day_it_was_placed()
    {
        var (admin, product) = await ProductAsync(price: 500m);
        var id = (await _o.PlaceAsync(product.Variants[0].Id, 2)).GetProperty("orderId").GetGuid();

        await H.ChangePriceAsync(admin, product, 999m);

        var detail = await _o.OrderAsync(admin, id);
        Assert.Equal(500m, detail.GetProperty("items")[0].GetProperty("unitPrice").GetDecimal());
        Assert.Equal(1000m, detail.GetProperty("money").GetProperty("subtotal").GetDecimal());
    }

    [Fact]
    public async Task Notes_bulk_actions_the_packing_slip_and_the_source_count_work()
    {
        var (admin, product) = await ProductAsync(stock: 20);
        var variant = product.Variants[0].Id;
        var ids = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            ids.Add((await _o.PlaceAsync(variant, 1)).GetProperty("orderId").GetGuid());
        }

        var noted = await _o.ActAsync(admin, ids[0], "notes", new { kind = "Internal", text = "Call before delivery" });
        Assert.Contains(noted.GetProperty("notes").EnumerateArray(), n => n.GetProperty("kind").GetString() == "Internal" && n.GetProperty("text").GetString() == "Call before delivery");

        await _o.ActAsync(admin, ids[2], "confirm");
        var confirm = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/bulk", HttpStatusCode.OK, new { action = "confirm", ids }, admin);
        Assert.Equal(2, confirm.GetProperty("succeeded").GetInt32()); // the third one was confirmed already
        Assert.Equal(1, confirm.GetProperty("failed").GetInt32());
        Assert.Contains(confirm.GetProperty("results").EnumerateArray(), r => !r.GetProperty("ok").GetBoolean() && r.GetProperty("code").GetString() == "ORDER.INVALID_TRANSITION");

        var print = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/bulk", HttpStatusCode.OK, new { action = "print", ids }, admin);
        Assert.Equal(3, print.GetProperty("slips").GetArrayLength());
        var slip = print.GetProperty("slips")[0];
        Assert.Equal("House 12, Road 5, Dhanmondi", slip.GetProperty("shipping").GetProperty("addressLine").GetString());
        Assert.Equal(1, slip.GetProperty("totalQuantity").GetInt32());

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/orders/bulk", new { action = "cancel", ids }, admin), "GENERAL.VALIDATION"); // needs a reason
        var cancel = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/bulk", HttpStatusCode.OK, new { action = "cancel", ids, reason = "Test clean-up" }, admin);
        Assert.Equal(3, cancel.GetProperty("succeeded").GetInt32());
        Assert.Equal((20, 0), await _o.StockAsync(variant));

        var sources = await H.JsonAsync(HttpMethod.Get, "/api/v1/orders/sources/summary", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(6, sources.GetArrayLength());
    }

    [Fact]
    public async Task The_staff_list_filters_by_status_source_branch_and_search_and_pages()
    {
        var (admin, product) = await ProductAsync(stock: 20);
        var mobile = StoreHelper.NewMobile();
        var first = await _o.PlaceAsync(product.Variants[0].Id, 1, mobile);
        await _o.PlaceAsync(product.Variants[0].Id, 1);
        await _o.ActAsync(admin, first.GetProperty("orderId").GetGuid(), "confirm");

        var confirmed = await H.JsonAsync(HttpMethod.Get, $"/api/v1/orders?status=Confirmed&search={Uri.EscapeDataString(mobile)}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(1, confirmed.GetProperty("total").GetInt32());
        Assert.Equal(first.GetProperty("number").GetString(), confirmed.GetProperty("items")[0].GetProperty("number").GetString());

        var byNumber = await H.JsonAsync(HttpMethod.Get, $"/api/v1/orders?search={first.GetProperty("number").GetString()}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(1, byNumber.GetProperty("total").GetInt32());
        var web = await H.JsonAsync(HttpMethod.Get, "/api/v1/orders?source=Web&pageSize=1&page=1&sort=-total", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(1, web.GetProperty("items").GetArrayLength());
        Assert.True(web.GetProperty("total").GetInt32() >= 2);
        var phone = await H.JsonAsync(HttpMethod.Get, "/api/v1/orders?source=Phone&search=" + Uri.EscapeDataString(mobile), HttpStatusCode.OK, bearer: admin);
        Assert.Equal(0, phone.GetProperty("total").GetInt32());
        var ho = await H.Api.BranchIdAsync("HO");
        var inBranch = await H.JsonAsync(HttpMethod.Get, $"/api/v1/orders?branchId={ho}&search={Uri.EscapeDataString(mobile)}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(1, inBranch.GetProperty("total").GetInt32());
    }

    // ---- branch isolation, permissions, routing -------------------------------------------------------------

    [Fact]
    public async Task Staff_of_another_branch_cannot_see_or_change_the_order_and_missing_permissions_are_refused()
    {
        var (admin, product) = await ProductAsync();
        var id = (await _o.PlaceAsync(product.Variants[0].Id, 1)).GetProperty("orderId").GetGuid();
        var other = await H.Api.CreateBranchAsync(admin, "OB" + Random.Shared.Next(100, 999), "Other shop");
        var elsewhere = await _o.StaffTokenAsync(admin, other, "orders.order.view", "orders.order.confirm", "orders.order.cancel");
        var viewerOnly = await _o.StaffTokenAsync(admin, null, "orders.order.view");

        // Another branch: the order does not exist for them, in lists, detail and actions.
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/orders/{id}", bearer: elsewhere)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _o.TryActAsync(elsewhere, id, "confirm")).StatusCode);
        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/orders?pageSize=100", HttpStatusCode.OK, bearer: elsewhere);
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), o => o.GetProperty("id").GetGuid() == id);

        // Same branch but only the view permission.
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, $"/api/v1/orders/{id}", bearer: viewerOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryActAsync(viewerOnly, id, "confirm")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryActAsync(viewerOnly, id, "cancel", new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, $"/api/v1/orders/{id}/packing-slip", bearer: viewerOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, "/api/v1/orders")).StatusCode);
        var customer = await H.CustomerTokenAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/orders", bearer: customer)).StatusCode);

        Assert.Equal("Placed", (await _o.OrderAsync(admin, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task An_admin_moves_the_reservation_to_another_branch_before_packing_and_not_when_it_lacks_stock()
    {
        var (admin, product) = await ProductAsync(stock: 10);
        var variant = product.Variants[0].Id;
        var id = (await _o.PlaceAsync(variant, 3)).GetProperty("orderId").GetGuid();
        var code = "RB" + Random.Shared.Next(100, 999);
        var other = await H.Api.CreateBranchAsync(admin, code, "Reassign shop");

        // The other branch has nothing yet.
        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "reassign", new { branchId = other, reason = "test" }), "ORDER.STOCK_INSUFFICIENT");
        Assert.Equal((10, 3), await _o.StockAsync(variant));

        await H.JsonAsync(HttpMethod.Post, "/api/v1/inventory/adjustments", HttpStatusCode.OK,
            new { branchId = other, reason = "OpeningStock", lines = new[] { new { variantId = variant, quantityChange = 5 } } }, admin);
        var moved = await _o.ActAsync(admin, id, "reassign", new { branchId = other, reason = "Closer to the customer" });

        Assert.Equal(other, moved.GetProperty("branchId").GetGuid());
        Assert.Equal("Staff", moved.GetProperty("routingRule").GetString());
        Assert.Equal((10, 0), await _o.StockAsync(variant));
        Assert.Equal((5, 3), await _o.StockAsync(variant, code));
        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "reassign", new { branchId = other, reason = "same" }), "ORDER.NOTHING_TO_DO");

        // Packing happens in the new branch; after packing the branch cannot change any more.
        await _o.ActAsync(admin, id, "confirm");
        await _o.ActAsync(admin, id, "pack");
        Assert.Equal((2, 0), await _o.StockAsync(variant, code));
        var ho = await H.Api.BranchIdAsync("HO");
        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "reassign", new { branchId = ho, reason = "late" }), "ORDER.INVALID_TRANSITION");
    }

    [Fact]
    public async Task Routing_chooses_a_branch_that_holds_every_item_and_prefers_the_default_branch()
    {
        var admin = await H.AdminAsync();
        var category = await H.CategoryAsync(admin);
        // Stock only in the second branch: the order must be placed there, not fail because the default branch is empty.
        var code = "RT" + Random.Shared.Next(100, 999);
        var other = await H.Api.CreateBranchAsync(admin, code, "Routing shop");
        var product = await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Route Tee"), 400m, [("Blue", "L", 0)]);
        var variant = product.Variants[0].Id;
        await H.JsonAsync(HttpMethod.Post, "/api/v1/inventory/adjustments", HttpStatusCode.OK,
            new { branchId = other, reason = "OpeningStock", lines = new[] { new { variantId = variant, quantityChange = 4 } } }, admin);

        var placed = await _o.PlaceAsync(variant, 2);

        var detail = await _o.OrderAsync(admin, placed.GetProperty("orderId").GetGuid());
        Assert.Equal(other, detail.GetProperty("branchId").GetGuid());
        Assert.Equal(code, detail.GetProperty("branchCode").GetString());
        Assert.Equal("AllStock", detail.GetProperty("routingRule").GetString());
        Assert.StartsWith($"JV-{code}-", placed.GetProperty("number").GetString(), StringComparison.Ordinal);
        Assert.Equal((4, 2), await _o.StockAsync(variant, code));

        // Orders are not split: one unit here and one there is two units in the shop (the cart accepts two) but no branch holds both.
        var split = await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Split Tee"), 400m, [("Red", "S", 1)]);
        var splitVariant = split.Variants[0].Id;
        await H.JsonAsync(HttpMethod.Post, "/api/v1/inventory/adjustments", HttpStatusCode.OK,
            new { branchId = other, reason = "OpeningStock", lines = new[] { new { variantId = splitVariant, quantityChange = 1 } } }, admin);
        await StoreHelper.ExpectCodeAsync(await _o.TryCheckoutAsync(await _o.CartAsync(splitVariant, 2), StoreHelper.NewMobile()), "ORDER.STOCK_INSUFFICIENT");
        Assert.Equal((1, 0), await _o.StockAsync(splitVariant));
        Assert.Equal((1, 0), await _o.StockAsync(splitVariant, code));
    }

    // ---- social order desk ---------------------------------------------------------------------------------

    [Fact]
    public async Task Staff_create_an_order_for_a_facebook_customer_by_sku_and_reuse_the_customer_found_by_mobile()
    {
        var (admin, product) = await ProductAsync(price: 700m, stock: 10);
        var variant = product.Variants[0];
        var mobile = StoreHelper.NewMobile();
        var web = await _o.PlaceAsync(variant.Id, 1, mobile);
        var webCustomer = (await _o.OrderAsync(admin, web.GetProperty("orderId").GetGuid())).GetProperty("customerId").GetGuid();

        var body = new
        {
            customerMobile = mobile,
            customerName = "Nadia Rahman",
            source = "Facebook",
            address = new { recipientName = "Nadia Rahman", countryCode = "BD", districtId = await _o.DistrictAsync(), addressLine = "House 3, Road 9, Gulshan", phone = mobile },
            lines = new[] { new { sku = variant.Sku.ToLowerInvariant(), quantity = 2 } },
            paymentMethod = "COD",
            customerNote = "Deliver after 5 pm",
            internalNote = "Messenger order",
            discount = 100m,
            deliveryChargeOverride = 0m,
        };
        var created = await H.JsonAsync(HttpMethod.Post, "/api/v1/orders/manual", HttpStatusCode.Created, body, admin);

        Assert.Equal("Facebook", created.GetProperty("source").GetString());
        Assert.Equal("Placed", created.GetProperty("status").GetString());
        Assert.Equal(webCustomer, created.GetProperty("customerId").GetGuid());
        Assert.Equal(1400m, created.GetProperty("money").GetProperty("subtotal").GetDecimal());
        Assert.Equal(100m, created.GetProperty("money").GetProperty("manualDiscount").GetDecimal());
        Assert.Equal(0m, created.GetProperty("money").GetProperty("deliveryCharge").GetDecimal());
        Assert.Equal(1300m, created.GetProperty("money").GetProperty("grandTotal").GetDecimal());
        Assert.NotEqual(System.Text.Json.JsonValueKind.Null, created.GetProperty("createdByStaffId").ValueKind);
        Assert.Equal((10, 3), await _o.StockAsync(variant.Id));
        Assert.Equal(2, created.GetProperty("notes").GetArrayLength());

        var facebook = await H.JsonAsync(HttpMethod.Get, "/api/v1/orders?source=Facebook&search=" + Uri.EscapeDataString(mobile), HttpStatusCode.OK, bearer: admin);
        Assert.Equal(1, facebook.GetProperty("total").GetInt32());

        // Unknown SKU, web source and missing items are refused with field errors.
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual", body with { lines = new[] { new { sku = "NOPE-SKU", quantity = 1 } } }, admin), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual", body with { source = "Web" }, admin), "GENERAL.VALIDATION");
    }

    [Fact]
    public async Task A_discount_or_a_delivery_override_on_a_manual_order_needs_its_own_permission()
    {
        var (admin, product) = await ProductAsync(stock: 10);
        var plain = await _o.StaffTokenAsync(admin, null, "orders.order.create", "orders.order.view");
        var discounter = await _o.StaffTokenAsync(admin, null, "orders.order.create", "orders.order.apply-discount", "orders.order.override-delivery");
        var district = await _o.DistrictAsync();
        object Body(decimal? discount, decimal? overrideCharge) => new
        {
            customerMobile = StoreHelper.NewMobile(),
            source = "Phone",
            address = new { recipientName = "Karim", countryCode = "BD", districtId = district, addressLine = "House 7, Road 1, Uttara" },
            lines = new[] { new { variantId = product.Variants[0].Id, quantity = 1 } },
            paymentMethod = "COD",
            discount,
            deliveryChargeOverride = overrideCharge,
        };

        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual", Body(50m, null), plain)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual", Body(null, 10m), plain)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual", Body(null, null), plain)).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual", Body(50m, 10m), discounter)).StatusCode);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual", Body(99999m, null), discounter), "ORDER.DISCOUNT_TOO_LARGE");
    }

    // ---- events ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Order_events_reach_the_outbox_and_their_handlers_send_the_customer_an_sms_once()
    {
        var (admin, product) = await ProductAsync();
        var mobile = StoreHelper.NewMobile();
        var placed = await _o.PlaceAsync(product.Variants[0].Id, 1, mobile);
        await _o.ActAsync(admin, placed.GetProperty("orderId").GetGuid(), "confirm");

        var types = await _o.WithDbAsync(db => db.OutboxMessages.Select(m => m.Type).Distinct().ToListAsync(Ct));
        Assert.Contains("domain.OrderPlaced", types);
        Assert.Contains("domain.OrderStatusChanged", types);

        var processor = factory.Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }

        var messages = await _o.WithDbAsync(db => db.NotificationLogs.AsNoTracking().Where(n => n.Recipient == "+88" + mobile && n.Template.StartsWith("order.")).ToListAsync(Ct));
        Assert.Single(messages, m => m.Template == "order.placed" && m.Body.Contains(placed.GetProperty("number").GetString()!, StringComparison.Ordinal));
        Assert.Single(messages, m => m.Template == "order.confirmed" && m.Body.Contains("is confirmed", StringComparison.Ordinal));
        Assert.All(messages, m => Assert.Equal(NotificationStatus.Sent, m.Status));
        var failed = await _o.WithDbAsync(db => db.OutboxMessages.CountAsync(m => m.Status == Juvo.Domain.Platform.OutboxStatus.Failed, Ct));
        Assert.Equal(0, failed);
    }

    // ---- the payment-hold timeout (BR-D2) --------------------------------------------------------------------

    [Fact]
    public async Task An_unpaid_online_order_is_cancelled_after_its_hold_and_the_stock_is_released()
    {
        var (admin, product) = await ProductAsync(stock: 5);
        var variant = product.Variants[0].Id;
        var placed = await _o.PlaceAsync(variant, 2, options: new CheckoutOptions(Method: "SANDBOX"));
        var id = placed.GetProperty("orderId").GetGuid();
        var cod = (await _o.PlaceAsync(variant, 1)).GetProperty("orderId").GetGuid();
        Assert.Equal((5, 3), await _o.StockAsync(variant));

        async Task<int> SweepAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<OrderTimeoutService>().ExpireAsync(Ct);
        }

        // Still inside the 30 minute hold: nothing happens.
        var deadline = placed.GetProperty("paymentDeadline").GetDateTimeOffset();
        Assert.InRange((deadline - DateTimeOffset.UtcNow).TotalMinutes, 29, 31);
        Assert.Equal(0, await SweepAsync());
        Assert.Equal((5, 3), await _o.StockAsync(variant));

        // The hold ends.
        await _o.WithDbAsync(db => db.Orders.Where(o => o.Id == id).ExecuteUpdateAsync(s => s.SetProperty(o => o.PaymentHoldExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));
        Assert.Equal(1, await SweepAsync());

        var detail = await _o.OrderAsync(admin, id);
        Assert.Equal("Cancelled", detail.GetProperty("status").GetString());
        Assert.Equal("Cancelled", detail.GetProperty("paymentStatus").GetString());
        Assert.Contains("Payment not received", detail.GetProperty("cancelReason").GetString(), StringComparison.Ordinal);
        Assert.Equal((5, 1), await _o.StockAsync(variant)); // only the cash on delivery order still holds stock
        Assert.Equal("Placed", (await _o.OrderAsync(admin, cod)).GetProperty("status").GetString());
        Assert.Equal(0, await SweepAsync()); // nothing left to do, and running again is harmless

        var movement = await _o.WithDbAsync(db => db.StockMovements.AnyAsync(m => m.ReferenceId == id && m.Reason == Juvo.Domain.Inventory.StockReason.OrderReleased, Ct));
        Assert.True(movement);
        var system = detail.GetProperty("timeline").EnumerateArray().Last(t => t.GetProperty("track").GetString() == "Order");
        Assert.Equal("System", system.GetProperty("actorName").GetString());
    }

    [Fact]
    public async Task An_order_paid_just_before_the_sweep_is_left_alone()
    {
        var (_, product) = await ProductAsync(stock: 5);
        var variant = product.Variants[0].Id;
        var placed = await _o.PlaceAsync(variant, 1, options: new CheckoutOptions(Method: "SANDBOX"));
        var id = placed.GetProperty("orderId").GetGuid();
        await _o.WithDbAsync(db => db.Orders.Where(o => o.Id == id).ExecuteUpdateAsync(
            s => s.SetProperty(o => o.PaymentHoldExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)).SetProperty(o => o.PaymentStatus, PaymentStatus.Paid), Ct));

        await using var scope = factory.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<OrderTimeoutService>().ExpireAsync(Ct));
        Assert.Equal((5, 1), await _o.StockAsync(variant));
    }
}
