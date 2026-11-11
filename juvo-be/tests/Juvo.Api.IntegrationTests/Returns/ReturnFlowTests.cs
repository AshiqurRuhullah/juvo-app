using System.Net;
using System.Text.RegularExpressions;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Inventory;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Juvo.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juvo.Api.IntegrationTests.Returns;

public partial class ReturnFlowTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly ReturnHelper _r = new(new OrderHelper(new StoreHelper(factory)));

    private StoreHelper H => _r.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^RT-HO-\d{4}-\d{6}$")]
    private static partial Regex NumberFormat();

    private static string Status(System.Text.Json.JsonElement json) => json.GetProperty("status").GetString()!;

    // ---- the whole journey -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_customer_returns_delivered_goods_and_the_shop_inspects_restocks_and_refunds_them()
    {
        var (admin, product) = await _r.ProductAsync(500m, ("Red", "M", 10));
        var variant = product.Variants[0].Id;
        var d = await _r.DeliverAsync(admin, variant, 2);
        Assert.Equal((8, 0), await _r.O.StockAsync(variant)); // packing took both units off the shelf

        var eligibility = await _r.EligibilityAsync(d);
        Assert.True(eligibility.GetProperty("eligible").GetBoolean());
        Assert.Equal(7, eligibility.GetProperty("windowDays").GetInt32());
        Assert.Equal(7, eligibility.GetProperty("reasons").GetArrayLength());
        var deliveredAt = eligibility.GetProperty("deliveredAt").GetDateTimeOffset();
        Assert.Equal(deliveredAt.AddDays(7), eligibility.GetProperty("deadline").GetDateTimeOffset());
        var line = Assert.Single(eligibility.GetProperty("items").EnumerateArray());
        Assert.Equal(2, line.GetProperty("returnableQuantity").GetInt32());
        Assert.Equal(500m, line.GetProperty("refundPerUnit").GetDecimal());

        var requested = await _r.RequestAsync(d, 2);
        var id = requested.GetProperty("id").GetGuid();
        Assert.Matches(NumberFormat(), requested.GetProperty("number").GetString()!);
        Assert.Equal("Requested", Status(requested));
        Assert.Equal(1000m, requested.GetProperty("refund").GetProperty("total").GetDecimal());
        Assert.False(requested.GetProperty("refund").GetProperty("final").GetBoolean());
        Assert.True(requested.GetProperty("canCancel").GetBoolean());
        Assert.Equal("Delivered", (await _r.O.OrderAsync(admin, d.OrderId)).GetProperty("status").GetString()); // a request alone does not move the order

        var approved = await _r.ActAsync(admin, id, "approve");
        Assert.Equal("Approved", Status(approved));
        Assert.Equal("ReturnInProgress", approved.GetProperty("order").GetProperty("status").GetString());

        var scheduled = await _r.ActAsync(admin, id, "schedule-pickup", ReturnHelper.Pickup());
        Assert.Equal("PickupScheduled", Status(scheduled));
        Assert.Equal("Pathao", scheduled.GetProperty("pickup").GetProperty("courier").GetString());
        await _r.ActAsync(admin, id, "picked-up");
        var received = await _r.ActAsync(admin, id, "received");
        Assert.Equal("Received", Status(received));

        var inspected = await _r.InspectAsync(admin, id);
        Assert.Equal("Inspected", Status(inspected));
        Assert.Equal(1000m, inspected.GetProperty("refund").GetProperty("total").GetDecimal());
        Assert.True(inspected.GetProperty("refund").GetProperty("final").GetBoolean());
        Assert.Equal((10, 0), await _r.O.StockAsync(variant)); // the good units are back on the shelf of the fulfilling branch
        var movements = await _r.O.WithDbAsync(db => db.StockMovements.AsNoTracking().Where(m => m.ReferenceId == id).ToListAsync(Ct));
        var movement = Assert.Single(movements);
        Assert.Equal(StockReason.CustomerReturn, movement.Reason);
        Assert.Equal(2, movement.QuantityChange);
        Assert.Equal(10, movement.OnHandAfter);
        Assert.True(inspected.GetProperty("items")[0].GetProperty("restocked").GetBoolean());

        var refunded = await _r.RefundAsync(admin, id, 1000m, "Cash", "RCPT-2611-1");
        Assert.Equal("Refunded", Status(refunded));
        Assert.Equal("RCPT-2611-1", refunded.GetProperty("refundPaid").GetProperty("reference").GetString());
        Assert.Equal("Completed", refunded.GetProperty("refundPaid").GetProperty("status").GetString());
        var order = await _r.O.OrderAsync(admin, d.OrderId);
        Assert.Equal("Returned", order.GetProperty("status").GetString());
        Assert.Equal("Returned", order.GetProperty("deliveryStatus").GetString());
        Assert.Equal("Refunded", order.GetProperty("paymentStatus").GetString());
        var orderHistory = order.GetProperty("timeline").EnumerateArray().Select(t => $"{t.GetProperty("track").GetString()}:{t.GetProperty("to").GetString()}").ToList();
        Assert.Contains("Order:ReturnInProgress", orderHistory);
        Assert.Contains("Order:Returned", orderHistory);
        Assert.Contains("Payment:Refunded", orderHistory);
        Assert.Contains("Delivery:Returned", orderHistory);
        Assert.Equal((10, 0), await _r.O.StockAsync(variant)); // the order's own return did not restock a second time

        // Staff see who did every step; the customer sees the steps without names, with the refund they got.
        var staffView = await _r.DetailAsync(admin, id);
        Assert.Equal(["Requested", "Approved", "PickupScheduled", "PickedUp", "Received", "Inspected", "Refunded"], staffView.GetProperty("timeline").EnumerateArray().Select(t => t.GetProperty("to").GetString()).ToArray());
        Assert.All(staffView.GetProperty("timeline").EnumerateArray().Skip(1), t => Assert.False(string.IsNullOrWhiteSpace(t.GetProperty("actorName").GetString())));
        var mine = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/returns/{id}", HttpStatusCode.OK, bearer: d.Customer);
        Assert.Equal("Refunded", Status(mine));
        Assert.All(mine.GetProperty("timeline").EnumerateArray(), t => Assert.Equal(System.Text.Json.JsonValueKind.Null, t.GetProperty("actorName").ValueKind));
        Assert.Equal(1000m, mine.GetProperty("refund").GetProperty("total").GetDecimal());
        Assert.False(mine.GetProperty("canCancel").GetBoolean());
        var closed = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/returns?filter=closed", HttpStatusCode.OK, bearer: d.Customer);
        Assert.Single(closed.GetProperty("items").EnumerateArray());
        Assert.Equal(0, (await H.JsonAsync(HttpMethod.Get, "/api/v1/me/returns?filter=active", HttpStatusCode.OK, bearer: d.Customer)).GetProperty("total").GetInt32());

        // The customer is told by SMS at the steps they care about, once each (the outbox may deliver twice).
        var processor = factory.Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }

        var messages = await _r.O.WithDbAsync(db => db.NotificationLogs.AsNoTracking().Where(n => n.Recipient == "+88" + d.Mobile && n.Template.StartsWith("return.")).ToListAsync(Ct));
        Assert.Single(messages, m => m.Body.Contains("we received your return request", StringComparison.Ordinal));
        Assert.Single(messages, m => m.Body.Contains("is approved", StringComparison.Ordinal));
        Assert.Single(messages, m => m.Body.Contains("refund", StringComparison.Ordinal) && m.Body.Contains(requested.GetProperty("number").GetString()!, StringComparison.Ordinal));
        Assert.All(messages, m => Assert.Equal(NotificationStatus.Sent, m.Status));
        var deliveredSms = await _r.O.WithDbAsync(db => db.NotificationLogs.AsNoTracking().CountAsync(n => n.Recipient == "+88" + d.Mobile && n.Body.Contains("was delivered"), Ct));
        Assert.Equal(1, deliveredSms);
    }

    // ---- eligibility rules -----------------------------------------------------------------------------------

    [Fact]
    public async Task Only_delivered_orders_inside_the_window_can_be_returned_and_the_window_is_a_setting()
    {
        var (admin, product) = await _r.ProductAsync();
        var variant = product.Variants[0].Id;

        // An order that was only placed.
        var mobile = StoreHelper.NewMobile();
        var other = await _r.O.PlaceAsync(variant, 1, mobile);
        var notDelivered = new DeliveredOrder(other.GetProperty("orderId").GetGuid(), other.GetProperty("number").GetString()!, mobile, await H.CustomerTokenAsync(mobile), variant, 0m);
        var notYet = await _r.EligibilityAsync(notDelivered);
        Assert.False(notYet.GetProperty("eligible").GetBoolean());
        Assert.Equal("RETURN.ORDER_NOT_ELIGIBLE", notYet.GetProperty("reasonCode").GetString());
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(notDelivered, [new { orderItemId = Guid.NewGuid(), quantity = 1 }]), "RETURN.ORDER_NOT_ELIGIBLE");

        // Delivered, but eight days ago: the seven-day window is over.
        var d = await _r.DeliverAsync(admin, variant, 1);
        var item = (await _r.ItemIdsAsync(d))[0];
        await _r.BackdateDeliveryAsync(d.OrderId, 8);
        var expired = await _r.EligibilityAsync(d);
        Assert.False(expired.GetProperty("eligible").GetBoolean());
        Assert.Equal("RETURN.WINDOW_EXPIRED", expired.GetProperty("reasonCode").GetString());
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }]), "RETURN.WINDOW_EXPIRED");

        // The owner widens the window to 30 days (Returns.WindowDays): the same order is returnable again.
        await _r.SetSettingAsync("Returns.WindowDays", "30");
        try
        {
            Assert.True((await _r.EligibilityAsync(d)).GetProperty("eligible").GetBoolean());
            Assert.Equal(30, (await _r.EligibilityAsync(d)).GetProperty("windowDays").GetInt32());
            await TestApi.ExpectAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }]), HttpStatusCode.Created);
        }
        finally
        {
            await _r.ClearSettingAsync("Returns.WindowDays");
        }
    }

    [Fact]
    public async Task A_non_returnable_product_cannot_be_returned()
    {
        var (admin, product) = await _r.ProductAsync(900m);
        var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 1);
        var item = (await _r.ItemIdsAsync(d))[0];

        await TestApi.ExpectAsync(await H.Api.PutAsync($"/api/v1/catalog/products/{product.Id}",
            new { nameEn = product.Name, slug = product.Slug, categoryId = product.CategoryId, targetGroup = "Unisex", regularPrice = 900m, nonReturnable = true }, admin), HttpStatusCode.OK);
        var detail = await TestApi.ExpectAsync(await H.Api.GetAsync($"/api/v1/catalog/products/{product.Id}", admin), HttpStatusCode.OK);
        Assert.True(detail.GetProperty("nonReturnable").GetBoolean());

        var eligibility = await _r.EligibilityAsync(d);
        Assert.False(eligibility.GetProperty("eligible").GetBoolean());
        Assert.Equal("RETURN.ITEM_NOT_RETURNABLE", eligibility.GetProperty("reasonCode").GetString());
        Assert.Equal("NON_RETURNABLE", eligibility.GetProperty("items")[0].GetProperty("blockedReason").GetString());
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }]), "RETURN.ITEM_NOT_RETURNABLE");

        // Updating without the flag keeps it; switching it off allows the return again.
        await TestApi.ExpectAsync(await H.Api.PutAsync($"/api/v1/catalog/products/{product.Id}",
            new { nameEn = product.Name, slug = product.Slug, categoryId = product.CategoryId, targetGroup = "Unisex", regularPrice = 950m }, admin), HttpStatusCode.OK);
        Assert.False((await _r.EligibilityAsync(d)).GetProperty("eligible").GetBoolean());
        await TestApi.ExpectAsync(await H.Api.PutAsync($"/api/v1/catalog/products/{product.Id}",
            new { nameEn = product.Name, slug = product.Slug, categoryId = product.CategoryId, targetGroup = "Unisex", regularPrice = 950m, nonReturnable = false }, admin), HttpStatusCode.OK);
        Assert.True((await _r.EligibilityAsync(d)).GetProperty("eligible").GetBoolean());
    }

    [Fact]
    public async Task More_than_the_delivered_quantity_is_refused_also_across_several_requests()
    {
        var (admin, product) = await _r.ProductAsync();
        var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 2);
        var item = (await _r.ItemIdsAsync(d))[0];

        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 3 }]), "RETURN.QUANTITY_EXCEEDED");
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 0 }]), "GENERAL.VALIDATION");
        var first = await TestApi.ExpectAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }]), HttpStatusCode.Created);
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 2 }]), "RETURN.QUANTITY_EXCEEDED");
        var second = await TestApi.ExpectAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }]), HttpStatusCode.Created);
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }]), "RETURN.QUANTITY_EXCEEDED");

        var eligibility = await _r.EligibilityAsync(d);
        Assert.False(eligibility.GetProperty("eligible").GetBoolean());
        Assert.Equal(2, eligibility.GetProperty("items")[0].GetProperty("alreadyReturned").GetInt32());
        Assert.Equal("ALL_RETURNED", eligibility.GetProperty("items")[0].GetProperty("blockedReason").GetString());

        // A cancelled request gives its unit back.
        await H.JsonAsync(HttpMethod.Post, $"/api/v1/me/returns/{first.GetProperty("id").GetGuid()}/cancel", HttpStatusCode.OK, new { }, d.Customer);
        Assert.Equal(1, (await _r.EligibilityAsync(d)).GetProperty("items")[0].GetProperty("returnableQuantity").GetInt32());
        Assert.NotEqual(first.GetProperty("number").GetString(), second.GetProperty("number").GetString());

        // A line that is not on the order, and a request for another customer's order.
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d, [new { orderItemId = Guid.NewGuid(), quantity = 1 }]), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }], reason: "NOPE"), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }], bearer: await H.CustomerTokenAsync()), "ORDER.NOT_FOUND");
    }

    // ---- money -----------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_delivery_charge_is_refunded_only_when_the_seller_is_at_fault_and_only_once()
    {
        var (admin, product) = await _r.ProductAsync(800m, ("Red", "M", 20));
        var variant = product.Variants[0].Id;
        var seller = await _r.DeliverAsync(admin, variant, 2);
        Assert.True(seller.DeliveryCharge > 0, "the test needs a delivery charge");

        // First unit, damaged item (seller): the item and the delivery charge go back.
        var first = (await _r.RequestAsync(seller, 1, "DAMAGED_ITEM")).GetProperty("id").GetGuid();
        var inspectedFirst = await _r.ToInspectedAsync(admin, first);
        Assert.Equal("Seller", inspectedFirst.GetProperty("fault").GetString());
        Assert.Equal(800m, inspectedFirst.GetProperty("refund").GetProperty("items").GetDecimal());
        Assert.Equal(seller.DeliveryCharge, inspectedFirst.GetProperty("refund").GetProperty("deliveryCharge").GetDecimal());
        Assert.Equal(800m + seller.DeliveryCharge, inspectedFirst.GetProperty("refund").GetProperty("total").GetDecimal());
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, first, "refund", new { method = "Cash", reference = "R1", amount = 800m }), "RETURN.REFUND_AMOUNT_MISMATCH");
        await _r.RefundAsync(admin, first, 800m + seller.DeliveryCharge, "BankTransfer", "TX-1");
        var afterFirst = await _r.O.OrderAsync(admin, seller.OrderId);
        Assert.Equal("PartiallyRefunded", afterFirst.GetProperty("paymentStatus").GetString());
        Assert.Equal("Delivered", afterFirst.GetProperty("status").GetString()); // one unit is still with the customer: the order is not Returned

        // Second unit, also the seller's fault: the delivery charge is not given back a second time.
        var second = (await _r.RequestAsync(seller, 1, "WRONG_ITEM")).GetProperty("id").GetGuid();
        var inspectedSecond = await _r.ToInspectedAsync(admin, second);
        Assert.Equal(0m, inspectedSecond.GetProperty("refund").GetProperty("deliveryCharge").GetDecimal());
        Assert.Equal(800m, inspectedSecond.GetProperty("refund").GetProperty("total").GetDecimal());
        await _r.RefundAsync(admin, second, null, "Cash", "R2");
        var done = await _r.O.OrderAsync(admin, seller.OrderId);
        Assert.Equal("Refunded", done.GetProperty("paymentStatus").GetString());
        Assert.Equal("Returned", done.GetProperty("status").GetString());

        // Customer's fault (changed mind): items only, the delivery charge stays with the shop.
        var customerFault = await _r.DeliverAsync(admin, variant, 1);
        var own = (await _r.RequestAsync(customerFault, 1, "CHANGED_MIND")).GetProperty("id").GetGuid();
        var inspectedOwn = await _r.ToInspectedAsync(admin, own);
        Assert.Equal("Customer", inspectedOwn.GetProperty("fault").GetString());
        Assert.Equal(0m, inspectedOwn.GetProperty("refund").GetProperty("deliveryCharge").GetDecimal());
        Assert.Equal(800m, inspectedOwn.GetProperty("refund").GetProperty("total").GetDecimal());
        await _r.RefundAsync(admin, own);
        Assert.Equal("Refunded", (await _r.O.OrderAsync(admin, customerFault.OrderId)).GetProperty("paymentStatus").GetString());

        // The inspector can overrule the reason the customer picked: a size problem that was really the shop's mistake.
        var overruled = await _r.DeliverAsync(admin, variant, 1);
        var disputed = (await _r.RequestAsync(overruled, 1, "SIZE_FIT")).GetProperty("id").GetGuid();
        await _r.ToReceivedAsync(admin, disputed);
        var inspectedDisputed = await _r.InspectAsync(admin, disputed, fault: "Seller");
        Assert.Equal(800m + overruled.DeliveryCharge, inspectedDisputed.GetProperty("refund").GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task A_customer_return_charge_from_the_settings_is_kept_from_the_refund()
    {
        var (admin, product) = await _r.ProductAsync(800m, ("Red", "M", 20));
        await _r.SetSettingAsync("Returns.CustomerFaultReturnCharge", "50");
        try
        {
            var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 1);
            var id = (await _r.RequestAsync(d, 1, "CHANGED_MIND")).GetProperty("id").GetGuid();
            var inspected = await _r.ToInspectedAsync(admin, id);
            Assert.Equal(50m, inspected.GetProperty("refund").GetProperty("returnCharge").GetDecimal());
            Assert.Equal(750m, inspected.GetProperty("refund").GetProperty("total").GetDecimal());

            var mine = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/returns/{id}", HttpStatusCode.OK, bearer: d.Customer);
            Assert.Equal(750m, mine.GetProperty("refund").GetProperty("total").GetDecimal()); // shown to the customer (FR-L07)

            // The seller's fault never carries the return charge.
            var seller = await _r.DeliverAsync(admin, product.Variants[0].Id, 1);
            var damaged = (await _r.RequestAsync(seller, 1, "DAMAGED_ITEM")).GetProperty("id").GetGuid();
            var inspectedSeller = await _r.ToInspectedAsync(admin, damaged);
            Assert.Equal(0m, inspectedSeller.GetProperty("refund").GetProperty("returnCharge").GetDecimal());
        }
        finally
        {
            await _r.ClearSettingAsync("Returns.CustomerFaultReturnCharge");
        }
    }

    [Fact]
    public async Task A_return_cannot_be_refunded_twice_or_before_inspection_and_damaged_goods_stay_off_the_shelf()
    {
        var (admin, product) = await _r.ProductAsync(600m, ("Red", "M", 10));
        var variant = product.Variants[0].Id;
        var d = await _r.DeliverAsync(admin, variant, 1);
        var id = (await _r.RequestAsync(d)).GetProperty("id").GetGuid();

        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "refund", new { method = "Cash", reference = "R" }), "RETURN.INVALID_TRANSITION");
        await _r.ToReceivedAsync(admin, id);
        var damaged = await _r.H.SendAsync(HttpMethod.Post, $"/api/v1/returns/{id}/inspect", new
        {
            items = (await _r.DetailAsync(admin, id)).GetProperty("items").EnumerateArray().Select(i => new { returnItemId = i.GetProperty("id").GetGuid(), condition = "Damaged", restock = true }).ToArray(),
        }, admin);
        await StoreHelper.ExpectCodeAsync(damaged, "GENERAL.VALIDATION"); // damaged goods cannot be restocked
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "inspect", new { items = Array.Empty<object>() }), "GENERAL.VALIDATION");

        await _r.InspectAsync(admin, id, "Damaged", restock: false);
        Assert.Equal((9, 0), await _r.O.StockAsync(variant)); // nothing went back on the shelf
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "refund", new { method = "Cash" }), "GENERAL.VALIDATION"); // a reference is needed
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "refund", new { method = "Cash", reference = "R", amount = 1m }), "RETURN.REFUND_AMOUNT_MISMATCH");

        await _r.RefundAsync(admin, id, 600m);
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "refund", new { method = "Cash", reference = "AGAIN" }), "RETURN.ALREADY_REFUNDED");
        Assert.Equal(1, await _r.O.WithDbAsync(db => db.ReturnRefunds.CountAsync(x => x.ReturnRequestId == id, Ct)));
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "approve"), "RETURN.INVALID_TRANSITION");
    }

    [Fact]
    public async Task Two_refunds_sent_at_the_same_moment_pay_only_once()
    {
        var (admin, product) = await _r.ProductAsync(600m);
        var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 1);
        var id = (await _r.RequestAsync(d)).GetProperty("id").GetGuid();
        await _r.ToInspectedAsync(admin, id);

        var attempts = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => _r.TryActAsync(admin, id, "refund", new { method = "Cash", reference = $"RACE-{i}" })));

        Assert.Equal(1, attempts.Count(a => a.StatusCode == HttpStatusCode.OK));
        Assert.Equal(1, attempts.Count(a => a.StatusCode == HttpStatusCode.Conflict));
        Assert.Equal(1, await _r.O.WithDbAsync(db => db.ReturnRefunds.CountAsync(x => x.ReturnRequestId == id, Ct)));
    }

    [Fact]
    public async Task A_refund_after_the_inspector_accepted_no_line_is_refused_and_the_refund_can_be_rejected()
    {
        var (admin, product) = await _r.ProductAsync(600m);
        var variant = product.Variants[0].Id;
        var d = await _r.DeliverAsync(admin, variant, 1);
        var id = (await _r.RequestAsync(d)).GetProperty("id").GetGuid();
        await _r.ToReceivedAsync(admin, id);
        var inspected = await _r.InspectAsync(admin, id, "Damaged", restock: false, acceptRefund: false);
        Assert.Equal(0m, inspected.GetProperty("refund").GetProperty("total").GetDecimal());
        Assert.Equal(0m, inspected.GetProperty("items")[0].GetProperty("refundAmount").GetDecimal());

        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "refund", new { method = "Cash", reference = "R" }), "RETURN.NOTHING_TO_REFUND");
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "reject-refund", new { reason = "" }), "GENERAL.VALIDATION");
        var rejected = await _r.ActAsync(admin, id, "reject-refund", new { reason = "Worn and damaged by use" });
        Assert.Equal("RefundRejected", Status(rejected));

        // The customer reads the reason; the order goes back to Delivered because nothing was taken back; the unit can be claimed again.
        var mine = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/returns/{id}", HttpStatusCode.OK, bearer: d.Customer);
        Assert.Contains(mine.GetProperty("timeline").EnumerateArray(), t => t.GetProperty("to").GetString() == "RefundRejected" && t.GetProperty("reason").GetString() == "Worn and damaged by use");
        var order = await _r.O.OrderAsync(admin, d.OrderId);
        Assert.Equal("Delivered", order.GetProperty("status").GetString());
        var statuses = order.GetProperty("timeline").EnumerateArray().Where(t => t.GetProperty("track").GetString() == "Order").Select(t => t.GetProperty("to").GetString()).ToList();
        Assert.Equal(["Placed", "Confirmed", "Packed", "HandedToCourier", "InTransit", "OutForDelivery", "Delivered", "ReturnInProgress", "Delivered"], statuses);
        Assert.Equal("CollectedByCourier", order.GetProperty("paymentStatus").GetString());
        Assert.Equal(1, (await _r.EligibilityAsync(d)).GetProperty("items")[0].GetProperty("returnableQuantity").GetInt32());
    }

    [Fact]
    public void The_refund_maths_shares_the_order_discount_across_lines_and_loses_no_cent()
    {
        var shipping = new ShippingSnapshot("Nadia", "+8801712345678", "BD", null, null, null, null, null, null, null, "House 1", null, null, null, null, null, null);
        var order = Order.Place(
            new NewOrder("JV-HO-2611-000001", Guid.NewGuid(), OrderSource.Web, Guid.NewGuid(), "+8801712345678", "Nadia", null, "COD", "DefaultBranch", null, "BDT", 1000m, 100m, "SAVE", null, 0m, 60m, 60m, 0m, 500,
                shipping, null, null, "fp", "hash", null, null, true, null, null, null),
            [
                new NewOrderItem(Guid.NewGuid(), Guid.NewGuid(), "Tee", null, "TEE", null, null, null, 300m, 3, 100, true),
                new NewOrderItem(Guid.NewGuid(), Guid.NewGuid(), "Cap", null, "CAP", null, null, null, 100m, 1, 100, true),
            ], DateTimeOffset.UtcNow);
        var tee = order.Items.Single(i => i.Sku == "TEE");
        var cap = order.Items.Single(i => i.Sku == "CAP");

        // Subtotal 1000, discount 100 (10 percent): the tee line paid 810, the cap 90. The delivery charge is not part of any line.
        Assert.Equal(810m, Juvo.Application.Returns.ReturnMath.NetLine(order, tee));
        Assert.Equal(90m, Juvo.Application.Returns.ReturnMath.NetLine(order, cap));
        Assert.Equal(270m, Juvo.Application.Returns.ReturnMath.LineRefund(order, tee, 1, 0, 0m));

        // Three single-unit returns of the same line add up to exactly the line value.
        var a = Juvo.Application.Returns.ReturnMath.LineRefund(order, tee, 1, 0, 0m);
        var b = Juvo.Application.Returns.ReturnMath.LineRefund(order, tee, 1, 1, a);
        var c = Juvo.Application.Returns.ReturnMath.LineRefund(order, tee, 1, 2, a + b);
        Assert.Equal(810m, a + b + c);
        Assert.Equal(810m, Juvo.Application.Returns.ReturnMath.LineRefund(order, tee, 3, 0, 0m));
    }

    // ---- exchange --------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_exchange_places_a_linked_order_with_no_charge_and_reserves_the_new_size()
    {
        var (admin, product) = await _r.ProductAsync(700m, ("Red", "M", 10), ("Red", "L", 10));
        var medium = product.Variants[0].Id;
        var large = product.Variants[1].Id;
        var d = await _r.DeliverAsync(admin, medium, 1);
        var item = (await _r.ItemIdsAsync(d))[0];

        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1, exchangeVariantId = Guid.NewGuid() }], kind: "Exchange"), "GENERAL.VALIDATION");
        var requested = await TestApi.ExpectAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1, exchangeVariantId = large }], kind: "Exchange"), HttpStatusCode.Created);
        var id = requested.GetProperty("id").GetGuid();
        Assert.Equal("Exchange", requested.GetProperty("kind").GetString());
        Assert.Equal(product.Variants[1].Sku, requested.GetProperty("items")[0].GetProperty("exchangeSku").GetString());
        Assert.Equal(0m, requested.GetProperty("refund").GetProperty("total").GetDecimal()); // an exchange refunds nothing

        await _r.ToInspectedAsync(admin, id);
        Assert.Equal((10, 0), await _r.O.StockAsync(medium)); // the returned medium is back on the shelf
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "refund", new { method = "Cash", reference = "R" }), "RETURN.WRONG_KIND");

        var exchanged = await _r.ActAsync(admin, id, "exchange", new { note = "Size L, same colour" });
        Assert.Equal("Exchanged", Status(exchanged));
        var replacementId = exchanged.GetProperty("replacementOrderId").GetGuid();
        var replacement = await _r.O.OrderAsync(admin, replacementId);
        Assert.Equal(exchanged.GetProperty("replacementOrderNumber").GetString(), replacement.GetProperty("number").GetString());
        Assert.Equal("Placed", replacement.GetProperty("status").GetString());
        Assert.Equal(0m, replacement.GetProperty("money").GetProperty("grandTotal").GetDecimal());
        var replacementLine = Assert.Single(replacement.GetProperty("items").EnumerateArray());
        Assert.Equal(product.Variants[1].Sku, replacementLine.GetProperty("sku").GetString());
        Assert.Equal((10, 1), await _r.O.StockAsync(large)); // reserved for the replacement
        Assert.Contains(replacement.GetProperty("notes").EnumerateArray(), n => n.GetProperty("text").GetString()!.Contains(d.Number, StringComparison.Ordinal));

        // The replacement goes through the normal order flow; the original order is Returned and its payment is untouched (no new payment, no refund).
        var original = await _r.O.OrderAsync(admin, d.OrderId);
        Assert.Equal("Returned", original.GetProperty("status").GetString());
        Assert.Equal("CollectedByCourier", original.GetProperty("paymentStatus").GetString());
        await _r.O.ActAsync(admin, replacementId, "confirm");
        await _r.O.ActAsync(admin, replacementId, "pack");
        Assert.Equal((9, 0), await _r.O.StockAsync(large));

        var mine = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/returns/{id}", HttpStatusCode.OK, bearer: d.Customer);
        Assert.Equal("Exchanged", Status(mine));
        Assert.Equal(replacementId, mine.GetProperty("replacementOrderId").GetGuid());
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "exchange"), "RETURN.INVALID_TRANSITION");
    }

    [Fact]
    public async Task An_exchange_is_refused_without_changes_when_the_replacement_is_out_of_stock()
    {
        var (admin, product) = await _r.ProductAsync(700m, ("Red", "M", 10), ("Red", "L", 0));
        var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 1);
        var item = (await _r.ItemIdsAsync(d))[0];
        var id = (await TestApi.ExpectAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1, exchangeVariantId = product.Variants[1].Id }], kind: "Exchange"), HttpStatusCode.Created))
            .GetProperty("id").GetGuid();
        await _r.ToInspectedAsync(admin, id);

        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "exchange"), "RETURN.REPLACEMENT_UNAVAILABLE");
        Assert.Equal("Inspected", Status(await _r.DetailAsync(admin, id)));
        Assert.Equal((0, 0), await _r.O.StockAsync(product.Variants[1].Id));
        var orders = await _r.O.WithDbAsync(db => db.Orders.IgnoreQueryFilters(["BranchScope"]).CountAsync(o => o.CustomerMobile == "+88" + d.Mobile, Ct));
        Assert.Equal(1, orders); // no replacement order was left behind
    }

    // ---- access ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Staff_of_another_branch_cannot_see_or_change_a_return_and_missing_permissions_are_refused()
    {
        var (admin, product) = await _r.ProductAsync();
        var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 1);
        var id = (await _r.RequestAsync(d)).GetProperty("id").GetGuid();
        var other = await H.Api.CreateBranchAsync(admin, "RB" + Random.Shared.Next(100, 999), "Other shop");
        var elsewhere = await _r.O.StaffTokenAsync(admin, other, "returns.return.view", "returns.return.approve", "returns.return.reject", "returns.return.refund");
        var viewerOnly = await _r.O.StaffTokenAsync(admin, null, "returns.return.view");
        var approver = await _r.O.StaffTokenAsync(admin, null, "returns.return.view", "returns.return.approve");

        // Another branch: the return does not exist for them, in lists, detail and actions.
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/returns/{id}", bearer: elsewhere)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _r.TryActAsync(elsewhere, id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _r.TryActAsync(elsewhere, id, "reject", new { reason = "no" })).StatusCode);
        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/returns?pageSize=100", HttpStatusCode.OK, bearer: elsewhere);
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        var summary = await H.JsonAsync(HttpMethod.Get, "/api/v1/returns/summary", HttpStatusCode.OK, bearer: elsewhere);
        Assert.Equal(0, summary.GetProperty("open").GetInt32());
        var bulk = await H.JsonAsync(HttpMethod.Post, "/api/v1/returns/bulk", HttpStatusCode.OK, new { action = "approve", ids = new[] { id } }, elsewhere);
        Assert.Equal("RETURN.NOT_FOUND", bulk.GetProperty("results")[0].GetProperty("code").GetString());

        // Same branch, but not the permission.
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, $"/api/v1/returns/{id}", bearer: viewerOnly)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _r.TryActAsync(viewerOnly, id, "approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _r.TryActAsync(viewerOnly, id, "reject", new { reason = "no" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _r.TryActAsync(approver, id, "reject", new { reason = "no" })).StatusCode);
        await _r.ActAsync(approver, id, "approve");
        Assert.Equal(HttpStatusCode.Forbidden, (await _r.TryActAsync(approver, id, "schedule-pickup", ReturnHelper.Pickup())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _r.TryActAsync(approver, id, "received")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _r.TryActAsync(approver, id, "inspect", new { items = Array.Empty<object>() })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _r.TryActAsync(approver, id, "refund", new { method = "Cash", reference = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _r.TryActAsync(approver, id, "exchange")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _r.TryActAsync(approver, id, "notes", new { text = "x" })).StatusCode);
        var bulkReject = await H.JsonAsync(HttpMethod.Post, "/api/v1/returns/bulk", HttpStatusCode.OK, new { action = "reject", ids = new[] { id }, reason = "no" }, approver);
        Assert.Equal("AUTH.BRANCH_FORBIDDEN", bulkReject.GetProperty("results")[0].GetProperty("code").GetString());

        // Nobody signed in, a customer on a staff route and staff on a customer route.
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, "/api/v1/returns")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/returns", bearer: d.Customer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/me/returns", bearer: admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, "/api/v1/me/returns")).StatusCode);
    }

    [Fact]
    public async Task A_customer_only_sees_and_changes_their_own_returns()
    {
        var (admin, product) = await _r.ProductAsync();
        var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 1);
        var id = (await _r.RequestAsync(d)).GetProperty("id").GetGuid();
        var stranger = await H.CustomerTokenAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/me/returns/{id}", bearer: stranger)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, $"/api/v1/me/returns/{id}/cancel", new { }, stranger)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/me/orders/{d.OrderId}/return-eligibility", bearer: stranger)).StatusCode);
        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/returns", HttpStatusCode.OK, bearer: stranger);
        Assert.Equal(0, list.GetProperty("total").GetInt32());
        Assert.Equal("Requested", Status(await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/returns/{id}", HttpStatusCode.OK, bearer: d.Customer)));
    }

    // ---- request details -------------------------------------------------------------------------------------

    [Fact]
    public async Task Creating_a_request_twice_with_the_same_key_answers_with_the_same_request()
    {
        var (admin, product) = await _r.ProductAsync();
        var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 2);
        var key = StoreHelper.Unique("key");

        var first = await _r.RequestAsync(d, 1, key: key);
        var again = await _r.RequestAsync(d, 1, key: key);
        Assert.Equal(first.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
        Assert.Equal(1, await _r.O.WithDbAsync(db => db.ReturnRequests.IgnoreQueryFilters(["BranchScope"]).CountAsync(x => x.OrderId == d.OrderId, Ct)));

        // The header works too.
        var item = (await _r.ItemIdsAsync(d))[0];
        var headerKey = StoreHelper.Unique("hdr");
        var viaHeader = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/returns")
        {
            Content = System.Net.Http.Json.JsonContent.Create(new { orderId = d.OrderId, kind = "Return", reasonCode = "SIZE_FIT", items = new[] { new { orderItemId = item, quantity = 1 } } }),
        };
        viaHeader.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", d.Customer);
        viaHeader.Headers.Add("Idempotency-Key", headerKey);
        var created = await TestApi.ExpectAsync(await H.Api.Client.SendAsync(viaHeader, Ct), HttpStatusCode.Created);
        Assert.Equal("Requested", Status(created));
        var other = await _r.DeliverAsync(admin, product.Variants[0].Id, 1);
        var theirs = await TestApi.ExpectAsync(await _r.TryRequestAsync(other, [new { orderItemId = (await _r.ItemIdsAsync(other))[0], quantity = 1 }], key: key), HttpStatusCode.Created);
        Assert.NotEqual(first.GetProperty("id").GetGuid(), theirs.GetProperty("id").GetGuid()); // keys belong to one customer: another customer's identical key is a different request
    }

    [Fact]
    public async Task Pictures_are_uploaded_first_and_attached_to_the_request()
    {
        var (admin, product) = await _r.ProductAsync();
        var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 1);
        var item = (await _r.ItemIdsAsync(d))[0];

        var upload = await TestApi.ExpectAsync(await _r.UploadPhotoAsync(d.Customer, ReturnHelper.Picture()), HttpStatusCode.Created);
        var path = upload.GetProperty("path").GetString()!;
        Assert.StartsWith("returns/", path, StringComparison.Ordinal);
        Assert.True((await H.Api.Client.GetAsync(new Uri(upload.GetProperty("thumbUrl").GetString()!, UriKind.RelativeOrAbsolute), Ct)).IsSuccessStatusCode);

        var created = await TestApi.ExpectAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }], "DAMAGED_ITEM", photos: [path]), HttpStatusCode.Created);
        Assert.Single(created.GetProperty("photos").EnumerateArray());
        var staff = await _r.DetailAsync(admin, created.GetProperty("id").GetGuid());
        Assert.Equal(path, staff.GetProperty("photos")[0].GetProperty("path").GetString());

        // Too small, not a picture, somebody else's picture, a made-up path, too many.
        Assert.Equal(HttpStatusCode.BadRequest, (await _r.UploadPhotoAsync(d.Customer, ReturnHelper.Picture(300, 300))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _r.UploadPhotoAsync(d.Customer, [1, 2, 3, 4])).StatusCode);
        var stranger = await H.CustomerTokenAsync();
        var d2 = await _r.DeliverAsync(admin, product.Variants[0].Id, 1);
        var item2 = (await _r.ItemIdsAsync(d2))[0];
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d2, [new { orderItemId = item2, quantity = 1 }], photos: [path]), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d2, [new { orderItemId = item2, quantity = 1 }], photos: [$"returns/{Guid.NewGuid():N}/x.webp"]), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await _r.TryRequestAsync(d2, [new { orderItemId = item2, quantity = 1 }], photos: [.. Enumerable.Range(0, 6).Select(i => $"{path}{i}")]), "GENERAL.VALIDATION");
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Post, "/api/v1/me/returns/photos")).StatusCode);
        Assert.NotEmpty(stranger);
    }

    [Fact]
    public async Task A_customer_can_cancel_while_requested_or_approved_and_the_order_goes_back_to_delivered()
    {
        var (admin, product) = await _r.ProductAsync();
        var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 3);
        var item = (await _r.ItemIdsAsync(d))[0];
        var first = (await TestApi.ExpectAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }]), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        var second = (await TestApi.ExpectAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }]), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        var cancelled = await H.JsonAsync(HttpMethod.Post, $"/api/v1/me/returns/{first}/cancel", HttpStatusCode.OK, new { reason = "Found it fits" }, d.Customer);
        Assert.Equal("Cancelled", Status(cancelled));
        Assert.False(cancelled.GetProperty("canCancel").GetBoolean());
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/me/returns/{first}/cancel", new { }, d.Customer), "RETURN.INVALID_TRANSITION");
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, first, "approve"), "RETURN.INVALID_TRANSITION");
        Assert.Equal("Delivered", (await _r.O.OrderAsync(admin, d.OrderId)).GetProperty("status").GetString());

        // An approved request holds the order in ReturnInProgress; cancelling it releases the order again.
        await _r.ActAsync(admin, second, "approve");
        Assert.Equal("ReturnInProgress", (await _r.O.OrderAsync(admin, d.OrderId)).GetProperty("status").GetString());
        await H.JsonAsync(HttpMethod.Post, $"/api/v1/me/returns/{second}/cancel", HttpStatusCode.OK, new { }, d.Customer);
        Assert.Equal("Delivered", (await _r.O.OrderAsync(admin, d.OrderId)).GetProperty("status").GetString());

        // Once the pickup is arranged it is too late for the customer.
        var third = (await TestApi.ExpectAsync(await _r.TryRequestAsync(d, [new { orderItemId = item, quantity = 1 }]), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await _r.ActAsync(admin, third, "approve");
        await _r.ActAsync(admin, third, "schedule-pickup", ReturnHelper.Pickup());
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/me/returns/{third}/cancel", new { }, d.Customer), "RETURN.INVALID_TRANSITION");
        Assert.Equal("ReturnInProgress", (await _r.O.OrderAsync(admin, d.OrderId)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Rejecting_a_request_needs_a_reason_the_customer_sees_and_pickup_dates_in_the_past_are_refused()
    {
        var (admin, product) = await _r.ProductAsync();
        var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 1);
        var id = (await _r.RequestAsync(d)).GetProperty("id").GetGuid();

        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "reject", new { reason = " " }), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, id, "schedule-pickup", ReturnHelper.Pickup()), "RETURN.INVALID_TRANSITION"); // not approved yet
        var rejected = await _r.ActAsync(admin, id, "reject", new { reason = "Worn item, outside our policy" });
        Assert.Equal("Rejected", Status(rejected));
        Assert.Equal("Worn item, outside our policy", rejected.GetProperty("rejectReason").GetString());
        var mine = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/returns/{id}", HttpStatusCode.OK, bearer: d.Customer);
        Assert.Equal("Worn item, outside our policy", mine.GetProperty("rejectReason").GetString());
        Assert.Equal("Delivered", (await _r.O.OrderAsync(admin, d.OrderId)).GetProperty("status").GetString());

        var again = (await _r.RequestAsync(d)).GetProperty("id").GetGuid();
        await _r.ActAsync(admin, again, "approve");
        await StoreHelper.ExpectCodeAsync(await _r.TryActAsync(admin, again, "schedule-pickup", new { date = "2020-01-01" }), "GENERAL.VALIDATION");
        var note = await _r.ActAsync(admin, again, "notes", new { text = "Customer asked for an evening pickup" });
        Assert.Equal("Customer asked for an evening pickup", note.GetProperty("notes")[0].GetProperty("text").GetString());
        var mineAgain = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/returns/{again}", HttpStatusCode.OK, bearer: d.Customer);
        Assert.DoesNotContain("evening pickup", mineAgain.ToString(), StringComparison.Ordinal); // staff notes are internal
    }

    // ---- queue, bulk and dashboard ---------------------------------------------------------------------------

    [Fact]
    public async Task The_queue_filters_searches_and_counts_and_bulk_actions_report_each_return()
    {
        var (admin, product) = await _r.ProductAsync();
        var variant = product.Variants[0].Id;
        var a = await _r.DeliverAsync(admin, variant, 1);
        var b = await _r.DeliverAsync(admin, variant, 1);
        var c = await _r.DeliverAsync(admin, variant, 1);
        var ra = await _r.RequestAsync(a, 1, "DAMAGED_ITEM");
        var rb = await _r.RequestAsync(b);
        var rc = await _r.RequestAsync(c);

        var bulk = await H.JsonAsync(HttpMethod.Post, "/api/v1/returns/bulk", HttpStatusCode.OK,
            new { action = "approve", ids = new[] { ra.GetProperty("id").GetGuid(), rb.GetProperty("id").GetGuid(), Guid.NewGuid(), ra.GetProperty("id").GetGuid() } }, admin);
        Assert.Equal(2, bulk.GetProperty("succeeded").GetInt32());
        Assert.Equal(1, bulk.GetProperty("failed").GetInt32());
        Assert.Contains(bulk.GetProperty("results").EnumerateArray(), r => !r.GetProperty("ok").GetBoolean() && r.GetProperty("code").GetString() == "RETURN.NOT_FOUND");
        var rejected = await H.JsonAsync(HttpMethod.Post, "/api/v1/returns/bulk", HttpStatusCode.OK, new { action = "reject", ids = new[] { rc.GetProperty("id").GetGuid() }, reason = "Past the policy" }, admin);
        Assert.Equal(1, rejected.GetProperty("succeeded").GetInt32());
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/returns/bulk", new { action = "reject", ids = new[] { Guid.NewGuid() } }, admin), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/returns/bulk", new { action = "delete", ids = new[] { Guid.NewGuid() } }, admin), "GENERAL.VALIDATION");

        var approved = await H.JsonAsync(HttpMethod.Get, $"/api/v1/returns?status=Approved&search={Uri.EscapeDataString(a.Number)}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(1, approved.GetProperty("total").GetInt32());
        Assert.Equal(ra.GetProperty("number").GetString(), approved.GetProperty("items")[0].GetProperty("number").GetString());
        var byReturnNumber = await H.JsonAsync(HttpMethod.Get, $"/api/v1/returns?search={ra.GetProperty("number").GetString()}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(1, byReturnNumber.GetProperty("total").GetInt32());
        var byMobile = await H.JsonAsync(HttpMethod.Get, $"/api/v1/returns?search={Uri.EscapeDataString(b.Mobile)}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(rb.GetProperty("number").GetString(), Assert.Single(byMobile.GetProperty("items").EnumerateArray()).GetProperty("number").GetString());
        var seller = await H.JsonAsync(HttpMethod.Get, $"/api/v1/returns?fault=Seller&search={Uri.EscapeDataString(a.Mobile)}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(1, seller.GetProperty("total").GetInt32());
        var wrongFault = await H.JsonAsync(HttpMethod.Get, $"/api/v1/returns?fault=Seller&search={Uri.EscapeDataString(b.Mobile)}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(0, wrongFault.GetProperty("total").GetInt32());
        var exchanges = await H.JsonAsync(HttpMethod.Get, $"/api/v1/returns?kind=Exchange&search={Uri.EscapeDataString(a.Mobile)}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(0, exchanges.GetProperty("total").GetInt32());
        var future = await H.JsonAsync(HttpMethod.Get, $"/api/v1/returns?from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("o"))}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(0, future.GetProperty("total").GetInt32());
        var paged = await H.JsonAsync(HttpMethod.Get, "/api/v1/returns?page=1&pageSize=1&sort=number", HttpStatusCode.OK, bearer: admin);
        Assert.Single(paged.GetProperty("items").EnumerateArray());
        Assert.True(paged.GetProperty("total").GetInt32() >= 3);
    }

    [Fact]
    public async Task The_dashboard_summary_counts_returns_per_step_and_the_money_refunded_this_month()
    {
        var (admin, product) = await _r.ProductAsync(400m);
        var variant = product.Variants[0].Id;
        var before = await H.JsonAsync(HttpMethod.Get, "/api/v1/returns/summary", HttpStatusCode.OK, bearer: admin);

        var waiting = await _r.DeliverAsync(admin, variant, 1);
        await _r.RequestAsync(waiting);
        var refunded = await _r.DeliverAsync(admin, variant, 1);
        var id = (await _r.RequestAsync(refunded)).GetProperty("id").GetGuid();
        await _r.ToInspectedAsync(admin, id);
        var middle = await H.JsonAsync(HttpMethod.Get, "/api/v1/returns/summary", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(before.GetProperty("awaitingApproval").GetInt32() + 1, middle.GetProperty("awaitingApproval").GetInt32());
        Assert.Equal(before.GetProperty("awaitingRefund").GetInt32() + 1, middle.GetProperty("awaitingRefund").GetInt32());
        Assert.Equal(before.GetProperty("open").GetInt32() + 2, middle.GetProperty("open").GetInt32());

        await _r.RefundAsync(admin, id, 400m);
        var after = await H.JsonAsync(HttpMethod.Get, "/api/v1/returns/summary", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(before.GetProperty("awaitingRefund").GetInt32(), after.GetProperty("awaitingRefund").GetInt32());
        Assert.Equal(before.GetProperty("refundedThisMonth").GetDecimal() + 400m, after.GetProperty("refundedThisMonth").GetDecimal());
        Assert.Equal(11, after.GetProperty("byStatus").GetArrayLength());
    }

    [Fact]
    public async Task Return_numbers_are_consecutive_per_branch_and_month()
    {
        var (admin, product) = await _r.ProductAsync();
        var d = await _r.DeliverAsync(admin, product.Variants[0].Id, 3);
        var first = await _r.RequestAsync(d);
        var second = await _r.RequestAsync(d);

        var a = int.Parse(first.GetProperty("number").GetString()![^6..], System.Globalization.CultureInfo.InvariantCulture);
        var b = int.Parse(second.GetProperty("number").GetString()![^6..], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(first.GetProperty("number").GetString()![..^6], second.GetProperty("number").GetString()![..^6]);
        Assert.True(b > a);
        var numbers = await _r.O.WithDbAsync(db => db.ReturnRequests.IgnoreQueryFilters(["BranchScope"]).Where(x => x.Number.StartsWith("RT-HO-")).Select(x => x.Number).ToListAsync(Ct));
        var sequence = numbers.Select(n => int.Parse(n[^6..], System.Globalization.CultureInfo.InvariantCulture)).Order().ToList();
        Assert.Equal(Enumerable.Range(1, sequence.Count).ToList(), sequence); // gap free
    }
}
