using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Orders;

/// <summary>Module T basics: blacklist, risk flags, holds, the review queue and the decisions.</summary>
public class FraudTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly OrderHelper _o = new(new StoreHelper(factory));

    private StoreHelper H => _o.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<(string Admin, SeededProduct Product)> ProductAsync(decimal price = 500m, int stock = 30)
    {
        var admin = await H.AdminAsync();
        var category = await H.CategoryAsync(admin);
        return (admin, await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Risk Tee"), price, [("Green", "L", stock)]));
    }

    private Task<JsonElement> SetRuleAsync(string admin, string code, bool enabled, decimal threshold, string action) =>
        H.JsonAsync(HttpMethod.Put, $"/api/v1/fraud/rules/{code}", HttpStatusCode.OK, new { enabled, threshold, action }, admin);

    private static JsonElement[] Flags(JsonElement order) => [.. order.GetProperty("flags").EnumerateArray()];

    [Fact]
    public async Task A_blacklisted_mobile_gets_a_neutral_refusal_and_no_stock_is_reserved()
    {
        var (admin, product) = await ProductAsync();
        var mobile = StoreHelper.NewMobile();
        var entry = await H.JsonAsync(HttpMethod.Post, "/api/v1/fraud/blacklist", HttpStatusCode.Created, new { kind = "Mobile", value = mobile, note = "Refused three parcels" }, admin);
        Assert.Equal("+88" + mobile, entry.GetProperty("value").GetString());
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/fraud/blacklist", new { kind = "Mobile", value = mobile }, admin), "FRAUD.BLACKLIST_EXISTS");

        var response = await _o.TryCheckoutAsync(await _o.CartAsync(product.Variants[0].Id, 1), mobile);

        await StoreHelper.ExpectCodeAsync(response, "ORDER.CHECKOUT_BLOCKED");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain("blacklist", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal((30, 0), await _o.StockAsync(product.Variants[0].Id));

        // Removing the entry lets the customer order again; the entry stays in the audit trail.
        await H.SendAsync(HttpMethod.Delete, $"/api/v1/fraud/blacklist/{entry.GetProperty("id").GetGuid()}", bearer: admin);
        await TestApi.ExpectAsync(await _o.TryCheckoutAsync(await _o.CartAsync(product.Variants[0].Id, 1), mobile), HttpStatusCode.Created);
        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/fraud/blacklist?kind=Mobile", HttpStatusCode.OK, bearer: admin);
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("value").GetString() == "+88" + mobile);
    }

    [Fact]
    public async Task A_blacklisted_address_text_or_device_is_blocked_and_staff_get_the_reason_on_the_order_desk()
    {
        var (admin, product) = await ProductAsync();
        await H.JsonAsync(HttpMethod.Post, "/api/v1/fraud/blacklist", HttpStatusCode.Created, new { kind = "Address", value = "Road 99, Fake Para" }, admin);
        await H.JsonAsync(HttpMethod.Post, "/api/v1/fraud/blacklist", HttpStatusCode.Created, new { kind = "Device", value = "device-banned-0001" }, admin);

        await StoreHelper.ExpectCodeAsync(
            await _o.TryCheckoutAsync(await _o.CartAsync(product.Variants[0].Id, 1), StoreHelper.NewMobile(), new CheckoutOptions(AddressLine: "House 4, ROAD 99, fake para, Dhaka")), "ORDER.CHECKOUT_BLOCKED");
        await StoreHelper.ExpectCodeAsync(
            await _o.TryCheckoutAsync(await _o.CartAsync(product.Variants[0].Id, 1), StoreHelper.NewMobile(), new CheckoutOptions(Device: "device-banned-0001")), "ORDER.CHECKOUT_BLOCKED");

        var staff = await H.SendAsync(HttpMethod.Post, "/api/v1/orders/manual", new
        {
            customerMobile = StoreHelper.NewMobile(),
            source = "Phone",
            address = new { recipientName = "Karim", countryCode = "BD", districtId = await _o.DistrictAsync(), addressLine = "House 4, Road 99, Fake Para" },
            lines = new[] { new { variantId = product.Variants[0].Id, quantity = 1 } },
            paymentMethod = "COD",
        }, admin);
        await StoreHelper.ExpectCodeAsync(staff, "ORDER.CHECKOUT_BLOCKED");
        Assert.Contains("Blacklisted address", await staff.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_large_cash_on_delivery_order_from_a_new_customer_is_held_and_confirmation_waits_for_the_review()
    {
        var (admin, product) = await ProductAsync(price: 6000m);
        await SetRuleAsync(admin, "HIGH_COD_NEW_CUSTOMER", true, 5000, "Hold");
        var mobile = StoreHelper.NewMobile();
        var id = (await _o.PlaceAsync(product.Variants[0].Id, 1, mobile)).GetProperty("orderId").GetGuid();

        var order = await _o.OrderAsync(admin, id);
        Assert.Equal("Flagged", order.GetProperty("fraudState").GetString());
        Assert.True(order.GetProperty("isHeld").GetBoolean());
        Assert.Contains(Flags(order), f => f.GetProperty("ruleCode").GetString() == "HIGH_COD_NEW_CUSTOMER");
        Assert.Contains(Flags(order), f => f.GetProperty("ruleCode").GetString() == "FIRST_TIME_COD");
        Assert.True(order.GetProperty("riskScore").GetInt32() >= 40);
        Assert.Equal(1, order.GetProperty("customerStats").GetProperty("totalOrders").GetInt32()); // this order is the only one so far

        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "confirm", new { advanceReceived = true }), "ORDER.HELD_FOR_REVIEW");

        var queue = await H.JsonAsync(HttpMethod.Get, "/api/v1/fraud/reviews", HttpStatusCode.OK, bearer: admin);
        var item = queue.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("orderId").GetGuid() == id);
        Assert.Equal("Flagged", item.GetProperty("state").GetString());
        Assert.Equal(1, item.GetProperty("customerStats").GetProperty("totalOrders").GetInt32());
        var held = await H.JsonAsync(HttpMethod.Get, "/api/v1/orders?held=true&search=" + Uri.EscapeDataString(mobile), HttpStatusCode.OK, bearer: admin);
        Assert.Equal(1, held.GetProperty("total").GetInt32());

        // Call, then approve: the decisions are logged with the staff name and note (FR-T06).
        var afterCall = await H.JsonAsync(HttpMethod.Post, $"/api/v1/fraud/reviews/{id}/decide", HttpStatusCode.OK, new { action = "Call", note = "Customer confirmed by phone" }, admin);
        Assert.Equal("InReview", (await _o.OrderAsync(admin, id)).GetProperty("fraudState").GetString());
        Assert.Single(afterCall.EnumerateArray());
        var afterApprove = await H.JsonAsync(HttpMethod.Post, $"/api/v1/fraud/reviews/{id}/decide", HttpStatusCode.OK, new { action = "Approve" }, admin);
        Assert.Equal(2, afterApprove.GetArrayLength());
        var first = afterApprove[0];
        Assert.Equal("Call", first.GetProperty("action").GetString());
        Assert.Equal("Customer confirmed by phone", first.GetProperty("note").GetString());
        Assert.False(string.IsNullOrWhiteSpace(first.GetProperty("actorName").GetString()));
        Assert.Equal("Flagged", first.GetProperty("fromState").GetString());

        var approved = await _o.OrderAsync(admin, id);
        Assert.Equal("Approved", approved.GetProperty("fraudState").GetString());
        Assert.False(approved.GetProperty("isHeld").GetBoolean());
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/fraud/reviews/{id}/decide", new { action = "Approve" }, admin), "FRAUD.NOT_WAITING");
        await _o.ActAsync(admin, id, "confirm", new { advanceReceived = true });
        var queueAfter = await H.JsonAsync(HttpMethod.Get, "/api/v1/fraud/reviews", HttpStatusCode.OK, bearer: admin);
        Assert.DoesNotContain(queueAfter.GetProperty("items").EnumerateArray(), i => i.GetProperty("orderId").GetGuid() == id);
    }

    [Fact]
    public async Task Rejecting_a_held_order_cancels_it_and_returns_the_stock_and_it_needs_a_reason()
    {
        var (admin, product) = await ProductAsync(price: 7000m, stock: 5);
        await SetRuleAsync(admin, "HIGH_COD_NEW_CUSTOMER", true, 5000, "Hold");
        var variant = product.Variants[0].Id;
        var id = (await _o.PlaceAsync(variant, 2)).GetProperty("orderId").GetGuid();
        Assert.Equal((5, 2), await _o.StockAsync(variant));

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/fraud/reviews/{id}/decide", new { action = "Reject" }, admin), "GENERAL.VALIDATION");
        await H.JsonAsync(HttpMethod.Post, $"/api/v1/fraud/reviews/{id}/decide", HttpStatusCode.OK, new { action = "Reject", note = "Fake number" }, admin);

        var order = await _o.OrderAsync(admin, id);
        Assert.Equal("Cancelled", order.GetProperty("status").GetString());
        Assert.Equal("Rejected", order.GetProperty("fraudState").GetString());
        Assert.Contains("Fake number", order.GetProperty("cancelReason").GetString(), StringComparison.Ordinal);
        Assert.Equal((5, 0), await _o.StockAsync(variant));
    }

    [Fact]
    public async Task Requesting_an_advance_asks_for_the_delivery_charge_and_keeps_the_order_held_until_approved()
    {
        var (admin, product) = await ProductAsync(price: 6500m);
        await SetRuleAsync(admin, "HIGH_COD_NEW_CUSTOMER", true, 5000, "Hold");
        var id = (await _o.PlaceAsync(product.Variants[0].Id, 1)).GetProperty("orderId").GetGuid();

        await H.JsonAsync(HttpMethod.Post, $"/api/v1/fraud/reviews/{id}/decide", HttpStatusCode.OK, new { action = "RequestAdvance", note = "Ask for the delivery charge" }, admin);

        var order = await _o.OrderAsync(admin, id);
        Assert.Equal("AdvanceRequested", order.GetProperty("fraudState").GetString());
        Assert.True(order.GetProperty("isHeld").GetBoolean());
        Assert.True(order.GetProperty("money").GetProperty("advanceRequired").GetDecimal() > 0);
        Assert.Equal(0m, order.GetProperty("money").GetProperty("deliveryCharge").GetDecimal()); // free delivery at this value; the advance is still the courier cost
        await StoreHelper.ExpectCodeAsync(await _o.TryActAsync(admin, id, "confirm", new { advanceReceived = true }), "ORDER.HELD_FOR_REVIEW");
    }

    [Fact]
    public async Task Many_mobile_numbers_ordering_from_one_device_are_flagged_and_held()
    {
        var (admin, product) = await ProductAsync(price: 400m);
        await SetRuleAsync(admin, "DEVICE_VELOCITY", true, 3, "Hold");
        var device = "dev-" + Guid.NewGuid().ToString("N");
        var variant = product.Variants[0].Id;

        var first = await _o.PlaceAsync(variant, 1, options: new CheckoutOptions(Device: device));
        var second = await _o.PlaceAsync(variant, 1, options: new CheckoutOptions(Device: device));
        var third = await _o.PlaceAsync(variant, 1, options: new CheckoutOptions(Device: device));

        Assert.False((await _o.OrderAsync(admin, first.GetProperty("orderId").GetGuid())).GetProperty("isHeld").GetBoolean());
        Assert.False((await _o.OrderAsync(admin, second.GetProperty("orderId").GetGuid())).GetProperty("isHeld").GetBoolean());
        var held = await _o.OrderAsync(admin, third.GetProperty("orderId").GetGuid());
        Assert.True(held.GetProperty("isHeld").GetBoolean());
        Assert.Contains(Flags(held), f => f.GetProperty("ruleCode").GetString() == "DEVICE_VELOCITY");

        // The rule can be switched off in the settings: the same pattern then raises no flag.
        await SetRuleAsync(admin, "DEVICE_VELOCITY", false, 3, "Hold");
        var fourth = await _o.OrderAsync(admin, (await _o.PlaceAsync(variant, 1, options: new CheckoutOptions(Device: device))).GetProperty("orderId").GetGuid());
        Assert.DoesNotContain(Flags(fourth), f => f.GetProperty("ruleCode").GetString() == "DEVICE_VELOCITY");
    }

    [Fact]
    public async Task A_customer_with_returned_cash_on_delivery_orders_is_held_on_the_next_one_and_the_history_shows_it()
    {
        var (admin, product) = await ProductAsync(price: 350m);
        var variant = product.Variants[0].Id;
        var mobile = StoreHelper.NewMobile();
        var ids = new List<Guid>
        {
            (await _o.PlaceAsync(variant, 1, mobile)).GetProperty("orderId").GetGuid(),
            (await _o.PlaceAsync(variant, 2, mobile)).GetProperty("orderId").GetGuid(),
        };
        await _o.WithDbAsync(db => db.Orders.Where(o => ids.Contains(o.Id)).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, OrderStatus.Returned), Ct));

        var next = await _o.OrderAsync(admin, (await _o.PlaceAsync(variant, 3, mobile)).GetProperty("orderId").GetGuid());

        Assert.True(next.GetProperty("isHeld").GetBoolean());
        Assert.Contains(Flags(next), f => f.GetProperty("ruleCode").GetString() == "PAST_FAILED_COD");
        var stats = next.GetProperty("customerStats");
        Assert.Equal(3, stats.GetProperty("totalOrders").GetInt32());
        Assert.Equal(2, stats.GetProperty("returnedOrFailed").GetInt32());
        Assert.True(stats.GetProperty("score").GetInt32() < 50);
        var byEndpoint = await H.JsonAsync(HttpMethod.Get, $"/api/v1/fraud/customers/{next.GetProperty("customerId").GetGuid()}/stats", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(2, byEndpoint.GetProperty("returnedOrFailed").GetInt32());
    }

    [Fact]
    public async Task A_very_short_address_is_flagged_but_does_not_hold_the_order()
    {
        var (admin, product) = await ProductAsync(price: 300m);

        var placed = await _o.PlaceAsync(product.Variants[0].Id, 1, options: new CheckoutOptions(AddressLine: "Dhaka"));

        var order = await _o.OrderAsync(admin, placed.GetProperty("orderId").GetGuid());
        Assert.Contains(Flags(order), f => f.GetProperty("ruleCode").GetString() == "SHORT_ADDRESS");
        Assert.False(order.GetProperty("isHeld").GetBoolean());
        Assert.Equal("None", order.GetProperty("fraudState").GetString());
    }

    [Fact]
    public async Task A_rule_set_to_reject_refuses_the_checkout_with_the_neutral_message()
    {
        var (admin, product) = await ProductAsync(price: 300m);
        await SetRuleAsync(admin, "SHORT_ADDRESS", true, 10, "Reject");

        var response = await _o.TryCheckoutAsync(await _o.CartAsync(product.Variants[0].Id, 1), StoreHelper.NewMobile(), new CheckoutOptions(AddressLine: "Dhaka"));

        await StoreHelper.ExpectCodeAsync(response, "ORDER.CHECKOUT_BLOCKED");
        await SetRuleAsync(admin, "SHORT_ADDRESS", true, 10, "Flag");
        var rules = await H.JsonAsync(HttpMethod.Get, "/api/v1/fraud/rules", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(7, rules.GetArrayLength());
        Assert.Equal("Flag", rules.EnumerateArray().Single(r => r.GetProperty("code").GetString() == "SHORT_ADDRESS").GetProperty("action").GetString());
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, "/api/v1/fraud/rules/NOPE", new { enabled = true, threshold = 1, action = "Flag" }, admin), "FRAUD.RULE_NOT_FOUND");
    }

    [Fact]
    public async Task The_fraud_screens_need_their_own_permissions()
    {
        var (admin, product) = await ProductAsync(price: 6000m);
        await SetRuleAsync(admin, "HIGH_COD_NEW_CUSTOMER", true, 5000, "Hold");
        var id = (await _o.PlaceAsync(product.Variants[0].Id, 1)).GetProperty("orderId").GetGuid();
        var viewer = await _o.StaffTokenAsync(admin, null, "fraud.flag.view", "orders.order.view");
        var nobody = await _o.StaffTokenAsync(admin, null, "orders.order.view");

        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, "/api/v1/fraud/reviews", bearer: viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/fraud/reviews", bearer: nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, $"/api/v1/fraud/reviews/{id}/decide", new { action = "Approve" }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/fraud/blacklist", bearer: viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/fraud/blacklist", new { kind = "Mobile", value = "01711111111" }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Put, "/api/v1/fraud/rules/HIGH_VALUE", new { enabled = true, threshold = 1, action = "Flag" }, viewer)).StatusCode);
        Assert.Equal("Flagged", (await _o.OrderAsync(admin, id)).GetProperty("fraudState").GetString());
    }
}
