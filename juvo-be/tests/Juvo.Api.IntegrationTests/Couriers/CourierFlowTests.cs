using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Returns;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Couriers;
using Juvo.Domain.Notifications;
using Juvo.Domain.Payments;
using Juvo.Infrastructure.Couriers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using static Juvo.Api.IntegrationTests.Couriers.CourierHelper;

namespace Juvo.Api.IntegrationTests.Couriers;

public partial class CourierFlowTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly CourierHelper _c = new(new OrderHelper(new StoreHelper(factory)), factory);

    private StoreHelper H => _c.H;

    private OrderHelper O => _c.O;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex(@"^CS-HO-\d{4}-\d{6}$")]
    private static partial Regex SettlementNumber();

    private async Task<int> NotificationsAsync(string template, string mobile)
    {
        var tail = mobile[^8..];
        return await _c.DbAsync(db => db.NotificationLogs.CountAsync(n => n.Template == template && n.Recipient.EndsWith(tail), Ct));
    }

    // ---- booking, events, mapping onto the order tracks ----------------------------------------------------

    [Fact]
    public async Task Booking_a_packed_order_makes_a_parcel_with_the_branch_pickup_address_and_the_cash_to_collect()
    {
        var (admin, product) = await _c.ProductAsync(500m);
        var order = await _c.PackedAsync(admin, product.Variants[0].Id, 2);

        var shipment = await _c.BookAsync(admin, order.OrderId);

        Assert.Equal("Booked", Str(shipment, "status"));
        Assert.Equal("Forward", Str(shipment, "kind"));
        Assert.Matches(@"^SBX-[0-9A-F]{8}01$", Str(shipment, "consignmentId"));
        Assert.Equal("SANDBOX", Str(shipment, "courierCode"));
        Assert.Equal(order.Total, shipment.GetProperty("codAmount").GetDecimal());
        Assert.Equal("Head Office", Str(shipment, "pickupName"));
        Assert.Equal("Head Office", Str(shipment, "pickupAddress")); // the branch has no address yet: its name stands in
        Assert.Equal("Nadia Rahman", Str(shipment, "deliveryName"));
        Assert.Contains("Dhanmondi", Str(shipment, "deliveryAddress"), StringComparison.Ordinal);
        Assert.Equal(600, shipment.GetProperty("weightGrams").GetInt32());
        Assert.NotNull(shipment.GetProperty("trackingUrl").GetString());
        Assert.True(shipment.GetProperty("canCancel").GetBoolean());
        Assert.Single(shipment.GetProperty("events").EnumerateArray());

        var detail = await O.OrderAsync(admin, order.OrderId);
        Assert.Equal("Packed", Str(detail, "status")); // booking does not move the order: only the courier's pickup does
        Assert.Equal("ReadyToShip", Str(detail, "deliveryStatus"));

        Assert.Equal(HttpStatusCode.Conflict, (await _c.TryBookAsync(admin, order.OrderId)).StatusCode);
        await StoreHelper.ExpectCodeAsync(await _c.TryBookAsync(admin, order.OrderId), "SHIPMENT.ALREADY_BOOKED");
    }

    [Fact]
    public async Task An_order_that_is_not_packed_cannot_be_booked()
    {
        var (admin, product) = await _c.ProductAsync();
        var placed = await O.PlaceAsync(product.Variants[0].Id, 1);
        var id = placed.GetProperty("orderId").GetGuid();
        await StoreHelper.ExpectCodeAsync(await _c.TryBookAsync(admin, id), "SHIPMENT.ORDER_NOT_READY");
        await O.ActAsync(admin, id, "confirm");
        await StoreHelper.ExpectCodeAsync(await _c.TryBookAsync(admin, id), "SHIPMENT.ORDER_NOT_READY");
    }

    [Fact]
    public async Task Sandbox_events_walk_the_order_through_every_delivery_step_and_collect_the_cash()
    {
        var (admin, product) = await _c.ProductAsync(500m);
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var shipment = await _c.BookAsync(admin, order.OrderId);
        var id = shipment.GetProperty("id").GetGuid();

        var expected = new[] { ("PickedUp", "HandedToCourier", "HandedToCourier"), ("InTransit", "InTransit", "InTransit"), ("OutForDelivery", "OutForDelivery", "OutForDelivery"), ("Delivered", "Delivered", "Delivered") };
        foreach (var (shipmentStatus, orderStatus, deliveryStatus) in expected)
        {
            var moved = await _c.AdvanceAsync(admin, id);
            Assert.Equal(shipmentStatus, Str(moved, "status"));
            var detail = await O.OrderAsync(admin, order.OrderId);
            Assert.Equal(orderStatus, Str(detail, "status"));
            Assert.Equal(deliveryStatus, Str(detail, "deliveryStatus"));
        }

        var done = await _c.ShipmentAsync(admin, id);
        Assert.Equal(order.Total, done.GetProperty("codCollectedAmount").GetDecimal());
        Assert.Equal(5, done.GetProperty("events").GetArrayLength());
        Assert.False(done.GetProperty("canSimulate").GetBoolean());
        var final = await O.OrderAsync(admin, order.OrderId);
        Assert.Equal("CollectedByCourier", Str(final, "paymentStatus"));
        Assert.Equal(1, await _c.CountOrderEventsAsync(order.OrderId, "OutForDelivery"));

        // The immutable order history records the whole journey with the courier as the actor.
        var timeline = final.GetProperty("timeline").EnumerateArray().Where(t => t.GetProperty("track").GetString() == "Order").Select(t => t.GetProperty("to").GetString()).ToArray();
        Assert.Equal(["Placed", "Confirmed", "Packed", "HandedToCourier", "InTransit", "OutForDelivery", "Delivered"], timeline);
        Assert.Equal("Sandbox courier", final.GetProperty("timeline").EnumerateArray().Last(t => t.GetProperty("track").GetString() == "Order").GetProperty("actorName").GetString());

        // Nothing is left to simulate.
        await StoreHelper.ExpectCodeAsync(await _c.TryAdvanceAsync(admin, id), "SHIPMENT.INVALID_TRANSITION");

        // Customers hear about the steps through the notification templates.
        await _c.DrainOutboxAsync();
        Assert.True(await NotificationsAsync("order.handed-to-courier", order.Mobile) >= 1);
        Assert.True(await NotificationsAsync("order.out-for-delivery", order.Mobile) >= 1);
        Assert.True(await NotificationsAsync("order.delivered", order.Mobile) >= 1);
    }

    // ---- failed delivery: retry or return to sender ---------------------------------------------------------

    [Fact]
    public async Task A_failed_delivery_is_recorded_with_the_reason_and_can_be_retried()
    {
        var (admin, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var id = (await _c.BookAsync(admin, order.OrderId)).GetProperty("id").GetGuid();
        for (var i = 0; i < 3; i++)
        {
            await _c.AdvanceAsync(admin, id);
        }

        var failed = await _c.AdvanceAsync(admin, id, "delivery_failed", "Customer not reachable");
        Assert.Equal("DeliveryFailed", Str(failed, "status"));
        Assert.Equal(1, failed.GetProperty("attempts").GetInt32());
        Assert.Equal("Customer not reachable", Str(failed, "failureReason"));
        var detail = await O.OrderAsync(admin, order.OrderId);
        Assert.Equal("DeliveryFailed", Str(detail, "status"));
        Assert.Equal("Failed", Str(detail, "deliveryStatus"));
        Assert.Equal("Pending", Str(detail, "paymentStatus"));
        await _c.DrainOutboxAsync();
        Assert.True(await NotificationsAsync("order.delivery-failed", order.Mobile) >= 1);

        // The next step of the journey is the retry; the order follows.
        var retry = await _c.AdvanceAsync(admin, id);
        Assert.Equal("OutForDelivery", Str(retry, "status"));
        Assert.Equal(2, retry.GetProperty("attempts").GetInt32());
        Assert.Equal("OutForDelivery", Str(await O.OrderAsync(admin, order.OrderId), "status"));

        var delivered = await _c.AdvanceAsync(admin, id);
        Assert.Equal("Delivered", Str(delivered, "status"));
        Assert.Equal("CollectedByCourier", Str(await O.OrderAsync(admin, order.OrderId), "paymentStatus"));
        Assert.Equal(2, await _c.CountOrderEventsAsync(order.OrderId, "OutForDelivery"));
    }

    [Fact]
    public async Task A_parcel_returned_to_the_sender_puts_the_stock_back_on_the_shelf()
    {
        var (admin, product) = await _c.ProductAsync(500m, 10);
        var variant = product.Variants[0].Id;
        var order = await _c.PackedAsync(admin, variant, 2);
        Assert.Equal((8, 0), await O.StockAsync(variant)); // packing took the two units off the shelf
        var id = (await _c.BookAsync(admin, order.OrderId)).GetProperty("id").GetGuid();
        for (var i = 0; i < 3; i++)
        {
            await _c.AdvanceAsync(admin, id);
        }

        await _c.AdvanceAsync(admin, id, "delivery_failed", "Address not found");
        var returned = await _c.AdvanceAsync(admin, id, "returned", "Returned to the shop");

        Assert.Equal("Returned", Str(returned, "status"));
        var detail = await O.OrderAsync(admin, order.OrderId);
        Assert.Equal("Returned", Str(detail, "status"));
        Assert.Equal("Returned", Str(detail, "deliveryStatus"));
        Assert.Equal((10, 0), await O.StockAsync(variant));
        var movements = await _c.DbAsync(db => db.StockMovements.CountAsync(m => m.VariantId == variant && m.Reason == Domain.Inventory.StockReason.OrderRestocked, Ct));
        Assert.Equal(1, movements);
        await StoreHelper.ExpectCodeAsync(await _c.TryAdvanceAsync(admin, id), "SHIPMENT.INVALID_TRANSITION");
        await _c.DrainOutboxAsync();
        Assert.True(await NotificationsAsync("order.returned-to-sender", order.Mobile) >= 1);
    }

    // ---- cancel and re-book ---------------------------------------------------------------------------------

    [Fact]
    public async Task A_booking_can_be_cancelled_and_booked_again_until_the_courier_picks_it_up()
    {
        var (admin, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var first = await _c.BookAsync(admin, order.OrderId);
        var id = first.GetProperty("id").GetGuid();

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/shipments/{id}/cancel", new { reason = " " }, admin), "GENERAL.VALIDATION");
        var cancelled = await H.JsonAsync(HttpMethod.Post, $"/api/v1/shipments/{id}/cancel", HttpStatusCode.OK, new { reason = "Wrong address" }, admin);
        Assert.Equal("Cancelled", Str(cancelled, "status"));
        Assert.Equal("Packed", Str(await O.OrderAsync(admin, order.OrderId), "status"));
        var consignment = Str(first, "consignmentId");
        var remote = await _c.DbAsync(db => db.SandboxConsignments.Where(s => s.ConsignmentId == consignment).Select(s => s.RawStatus).SingleAsync(Ct));
        Assert.Equal("cancelled", remote);

        // Booking again gives the next deterministic id (the same order number, the next running count).
        var second = await _c.BookAsync(admin, order.OrderId);
        Assert.EndsWith("02", Str(second, "consignmentId"), StringComparison.Ordinal);
        Assert.Equal(Str(first, "consignmentId")[..^2], Str(second, "consignmentId")[..^2]);

        // Re-book cancels a still-booked parcel and books again.
        var rebooked = await H.JsonAsync(HttpMethod.Post, $"/api/v1/shipments/{second.GetProperty("id").GetGuid()}/rebook", HttpStatusCode.OK, new { reason = "Bigger parcel", weightGrams = 1500 }, admin);
        Assert.EndsWith("03", Str(rebooked, "consignmentId"), StringComparison.Ordinal);
        Assert.Equal(1500, rebooked.GetProperty("weightGrams").GetInt32());
        Assert.Equal("Cancelled", Str(await _c.ShipmentAsync(admin, second.GetProperty("id").GetGuid()), "status"));

        // Once picked up the courier has it: no cancel, no re-book.
        var open = rebooked.GetProperty("id").GetGuid();
        await _c.AdvanceAsync(admin, open);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/shipments/{open}/cancel", new { reason = "Too late" }, admin), "SHIPMENT.CANNOT_CANCEL");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/shipments/{open}/rebook", new { }, admin), "SHIPMENT.CANNOT_CANCEL");
        var all = await _c.ListAsync(admin, $"?orderId={order.OrderId}");
        Assert.Equal(3, all.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Cancelling_the_order_cancels_a_booked_parcel_and_releases_the_stock()
    {
        var (admin, product) = await _c.ProductAsync(500m, 10);
        var variant = product.Variants[0].Id;
        var order = await _c.PackedAsync(admin, variant, 2);
        var shipment = await _c.BookAsync(admin, order.OrderId);

        await O.ActAsync(admin, order.OrderId, "cancel", new { reason = "Customer asked" });

        var after = await _c.ShipmentAsync(admin, shipment.GetProperty("id").GetGuid());
        Assert.Equal("Cancelled", Str(after, "status"));
        Assert.Contains("order was cancelled", after.GetProperty("events").EnumerateArray().Last().GetProperty("description").GetString(), StringComparison.Ordinal);
        Assert.Equal((10, 0), await O.StockAsync(variant));
        Assert.Equal("Cancelled", Str(await O.OrderAsync(admin, order.OrderId), "status"));
    }

    // ---- bulk booking, concurrency --------------------------------------------------------------------------

    [Fact]
    public async Task Bulk_booking_handles_each_order_on_its_own()
    {
        var (admin, product) = await _c.ProductAsync();
        var a = await _c.PackedAsync(admin, product.Variants[0].Id);
        var b = await _c.PackedAsync(admin, product.Variants[0].Id);
        var c = await _c.PackedAsync(admin, product.Variants[0].Id);
        var notPacked = await O.PlaceAsync(product.Variants[0].Id, 1);
        var notPackedId = notPacked.GetProperty("orderId").GetGuid();
        await O.ActAsync(admin, notPackedId, "confirm");

        var result = await H.JsonAsync(HttpMethod.Post, "/api/v1/shipments/bulk-book", HttpStatusCode.OK, new { orderIds = new[] { a.OrderId, b.OrderId, c.OrderId, notPackedId, Guid.NewGuid() } }, admin);

        Assert.Equal(3, result.GetProperty("succeeded").GetInt32());
        Assert.Equal(2, result.GetProperty("failed").GetInt32());
        var rows = result.GetProperty("results").EnumerateArray().ToList();
        Assert.All(rows.Take(3), r => Assert.True(r.GetProperty("ok").GetBoolean()));
        Assert.Equal("SHIPMENT.ORDER_NOT_READY", rows[3].GetProperty("errorCode").GetString());
        Assert.Equal("ORDER.NOT_FOUND", rows[4].GetProperty("errorCode").GetString());
        Assert.Equal(3, rows.Take(3).Select(r => r.GetProperty("consignmentId").GetString()).Distinct().Count());

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/shipments/bulk-book", new { orderIds = Enumerable.Range(0, 51).Select(_ => Guid.NewGuid()).ToArray() }, admin), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/shipments/bulk-book", new { orderIds = Array.Empty<Guid>() }, admin), "GENERAL.VALIDATION");
    }

    [Fact]
    public async Task Two_simultaneous_bookings_of_one_order_make_exactly_one_parcel()
    {
        var (admin, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _c.TryBookAsync(admin, order.OrderId)));

        var statuses = string.Join(",", responses.Select(r => (int)r.StatusCode));
        Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.Created) == 1, statuses);
        Assert.True(responses.Count(r => r.StatusCode == HttpStatusCode.Conflict) == 3, statuses);
        var open = await _c.DbAsync(db => db.Shipments.CountAsync(s => s.OrderId == order.OrderId, Ct));
        Assert.Equal(1, open);
    }

    // ---- COD settlement: expected versus received -----------------------------------------------------------

    [Fact]
    public async Task A_settlement_batch_records_short_and_over_amounts_and_settles_the_cash()
    {
        var (admin, product) = await _c.ProductAsync(500m);
        var variant = product.Variants[0].Id;
        var orders = new[] { await _c.PackedAsync(admin, variant), await _c.PackedAsync(admin, variant, 2), await _c.PackedAsync(admin, variant, 3) };
        var shipments = new List<Guid>();
        foreach (var o in orders)
        {
            var s = await _c.BookAsync(admin, o.OrderId);
            shipments.Add(s.GetProperty("id").GetGuid());
            await _c.DeliverAsync(admin, shipments[^1]);
        }

        var accountant = await _c.StaffAsync(admin, null, "couriers.cod.view", "couriers.cod.settle");
        var courierId = await _c.CourierIdAsync(admin, "SANDBOX");
        var pending = await H.JsonAsync(HttpMethod.Get, $"/api/v1/couriers/cod-settlements/pending?courierId={courierId}", HttpStatusCode.OK, bearer: accountant);
        Assert.True(pending.GetArrayLength() >= 3);

        var batch = await H.JsonAsync(HttpMethod.Post, "/api/v1/couriers/cod-settlements", HttpStatusCode.Created, new { courierId, shipmentIds = shipments, note = "Weekly" }, accountant);
        var batchId = batch.GetProperty("id").GetGuid();
        Assert.Matches(SettlementNumber(), Str(batch, "number"));
        Assert.Equal("Open", Str(batch, "status"));
        var expected = orders.Sum(o => o.Total);
        Assert.Equal(expected, batch.GetProperty("expectedTotal").GetDecimal());
        Assert.Equal(180m, batch.GetProperty("feesTotal").GetDecimal()); // three parcels at the sandbox rate of 60
        Assert.Equal(3, batch.GetProperty("items").GetArrayLength());

        // The parcels are in a batch now: another batch of the same ones is refused.
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/couriers/cod-settlements", new { courierId, shipmentIds = shipments }, accountant), "SHIPMENT.NOTHING_TO_SETTLE");
        // A batch must be reconciled first.
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/couriers/cod-settlements/{batchId}/settle", new { reference = "BANK-1" }, accountant), "SHIPMENT.SETTLEMENT_STATE");

        // Short and over need a note.
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/couriers/cod-settlements/{batchId}/reconcile",
            new { lines = new[] { new { shipmentId = shipments[1], received = orders[1].Total - 50m } } }, accountant), "SHIPMENT.NOTE_REQUIRED");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/couriers/cod-settlements/{batchId}/reconcile",
            new { lines = new[] { new { shipmentId = shipments[1], received = -1m, note = "x" } } }, accountant), "SHIPMENT.AMOUNT_INVALID");

        var reconciled = await H.JsonAsync(HttpMethod.Post, $"/api/v1/couriers/cod-settlements/{batchId}/reconcile", HttpStatusCode.OK, new
        {
            lines = new object[]
            {
                new { shipmentId = shipments[1], received = orders[1].Total - 50m, note = "Courier kept 50 for change" },
                new { shipmentId = shipments[2], received = orders[2].Total + 20m, note = "Customer tipped 20" },
            },
        }, accountant);
        Assert.Equal("Reconciled", Str(reconciled, "status"));
        Assert.Equal(expected - 30m, reconciled.GetProperty("receivedTotal").GetDecimal());
        Assert.Equal(-30m, reconciled.GetProperty("difference").GetDecimal());
        Assert.Equal(1, reconciled.GetProperty("shortParcels").GetInt32());
        Assert.Equal(1, reconciled.GetProperty("overParcels").GetInt32());
        var diffs = reconciled.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("orderNumber").GetString()!, i => i.GetProperty("difference").GetDecimal());
        Assert.Equal(0m, diffs[orders[0].Number]);
        Assert.Equal(-50m, diffs[orders[1].Number]);
        Assert.Equal(20m, diffs[orders[2].Number]);

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/couriers/cod-settlements/{batchId}/settle", new { reference = " " }, accountant), "GENERAL.VALIDATION");
        var settled = await H.JsonAsync(HttpMethod.Post, $"/api/v1/couriers/cod-settlements/{batchId}/settle", HttpStatusCode.OK, new { reference = "BANK-DEP-77" }, accountant);
        Assert.Equal("Settled", Str(settled, "status"));
        Assert.Equal("BANK-DEP-77", Str(settled, "reference"));
        Assert.Equal(-30m, settled.GetProperty("difference").GetDecimal());

        foreach (var o in orders)
        {
            Assert.Equal("Settled", Str(await O.OrderAsync(admin, o.OrderId), "paymentStatus"));
            var cash = await _c.DbAsync(db => db.Payments.Where(p => p.OrderId == o.OrderId && p.Kind == PaymentKind.Cod).Select(p => new { p.Status, p.ReceivedReference }).SingleAsync(Ct));
            Assert.Equal(Domain.Orders.PaymentStatus.Settled, cash.Status);
            Assert.Contains("CS-HO-", cash.ReceivedReference, StringComparison.Ordinal);
        }

        // Settled is final.
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/couriers/cod-settlements/{batchId}/settle", new { reference = "AGAIN" }, accountant), "SHIPMENT.SETTLEMENT_STATE");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/couriers/cod-settlements/{batchId}/cancel", new { }, accountant), "SHIPMENT.SETTLEMENT_STATE");
        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/couriers/cod-settlements?status=Settled", HttpStatusCode.OK, bearer: accountant);
        Assert.Contains(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == batchId);
        Assert.Equal(3, (await _c.SettlementAsync(accountant, batchId)).GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task A_cancelled_batch_frees_its_parcels_and_only_collected_cash_can_be_batched()
    {
        var (admin, product) = await _c.ProductAsync(500m);
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var id = (await _c.BookAsync(admin, order.OrderId)).GetProperty("id").GetGuid();
        var courierId = await _c.CourierIdAsync(admin, "SANDBOX");

        // Not delivered yet: nothing to settle.
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/couriers/cod-settlements", new { courierId, shipmentIds = new[] { id } }, admin), "SHIPMENT.NOTHING_TO_SETTLE");
        await _c.DeliverAsync(admin, id);

        var batch = await H.JsonAsync(HttpMethod.Post, "/api/v1/couriers/cod-settlements", HttpStatusCode.Created, new { courierId, shipmentIds = new[] { id } }, admin);
        var batchId = batch.GetProperty("id").GetGuid();
        var cancelled = await H.JsonAsync(HttpMethod.Post, $"/api/v1/couriers/cod-settlements/{batchId}/cancel", HttpStatusCode.OK, new { }, admin);
        Assert.Equal("Cancelled", Str(cancelled, "status"));
        Assert.Equal(JsonValueKind.Null, (await _c.ShipmentAsync(admin, id)).GetProperty("codSettlementId").ValueKind);

        // The parcel goes into a new batch.
        var again = await H.JsonAsync(HttpMethod.Post, "/api/v1/couriers/cod-settlements", HttpStatusCode.Created, new { courierId, shipmentIds = new[] { id } }, admin);
        Assert.NotEqual(batchId, again.GetProperty("id").GetGuid());
    }

    // ---- webhook --------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_signed_webhook_moves_the_parcel_once_and_a_repeat_is_a_duplicate()
    {
        var (admin, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var shipment = await _c.BookAsync(admin, order.OrderId);
        var id = shipment.GetProperty("id").GetGuid();
        var consignment = Str(shipment, "consignmentId");

        var first = await TestApi.ExpectAsync(await _c.WebhookAsync(consignment, "picked_up", "evt-1"), HttpStatusCode.OK);
        Assert.Equal("processed", Str(first, "status"));
        Assert.Equal("PickedUp", Str(first, "shipmentStatus"));
        var repeat = await TestApi.ExpectAsync(await _c.WebhookAsync(consignment, "picked_up", "evt-1"), HttpStatusCode.OK);
        Assert.Equal("duplicate", Str(repeat, "status"));

        var detail = await _c.ShipmentAsync(admin, id);
        Assert.Equal(2, detail.GetProperty("events").GetArrayLength());
        Assert.Equal("Webhook", Str(detail.GetProperty("events").EnumerateArray().Last(), "source"));
        Assert.Equal("HandedToCourier", Str(await O.OrderAsync(admin, order.OrderId), "status"));
        Assert.Equal(1, await _c.CountOrderEventsAsync(order.OrderId, "HandedToCourier"));
    }

    [Fact]
    public async Task Concurrent_replays_of_one_event_apply_it_exactly_once()
    {
        var (admin, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var shipment = await _c.BookAsync(admin, order.OrderId);
        var consignment = Str(shipment, "consignmentId");

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _c.WebhookAsync(consignment, "picked_up", "evt-race")));

        var bodies = new List<string>();
        foreach (var r in results)
        {
            var text = await r.Content.ReadAsStringAsync(Ct);
            Assert.True(r.StatusCode is HttpStatusCode.OK or HttpStatusCode.Conflict, text); // a collision is told to retry; the retry is a duplicate
            bodies.Add(text);
        }

        Assert.Equal(1, bodies.Count(b => b.Contains("\"processed\"", StringComparison.Ordinal)));
        var detail = await _c.ShipmentAsync(admin, shipment.GetProperty("id").GetGuid());
        Assert.Equal(1, detail.GetProperty("events").EnumerateArray().Count(e => e.GetProperty("providerEventId").GetString() == "evt-race"));
        Assert.Equal(1, await _c.CountOrderEventsAsync(order.OrderId, "HandedToCourier"));
    }

    [Fact]
    public async Task A_forged_stale_or_malformed_webhook_changes_nothing()
    {
        var (admin, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var shipment = await _c.BookAsync(admin, order.OrderId);
        var consignment = Str(shipment, "consignmentId");

        var forged = await _c.WebhookAsync(consignment, "delivered", "evt-f", secret: "not-the-secret-not-the-secret-not-the-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, forged.StatusCode);
        await StoreHelper.ExpectCodeAsync(forged, "COURIER.INVALID_SIGNATURE");

        var stale = await _c.WebhookAsync(consignment, "delivered", "evt-s", signedAt: DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert.Equal(HttpStatusCode.BadRequest, stale.StatusCode);
        await StoreHelper.ExpectCodeAsync(stale, "COURIER.WEBHOOK_EXPIRED");

        await StoreHelper.ExpectCodeAsync(await _c.WebhookAsync(consignment, "delivered", "evt-m", rawBody: "this is not json"), "COURIER.WEBHOOK_MALFORMED");
        await StoreHelper.ExpectCodeAsync(await _c.WebhookAsync(consignment, "exploded", "evt-u"), "COURIER.WEBHOOK_MALFORMED");

        // No signature header at all.
        var bare = new HttpRequestMessage(HttpMethod.Post, "/api/v1/webhooks/couriers/SANDBOX") { Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json") };
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.Api.Client.SendAsync(bare, Ct)).StatusCode);

        var detail = await _c.ShipmentAsync(admin, shipment.GetProperty("id").GetGuid());
        Assert.Equal("Booked", Str(detail, "status"));
        Assert.Equal(1, detail.GetProperty("events").GetArrayLength());
        Assert.Equal("Packed", Str(await O.OrderAsync(admin, order.OrderId), "status"));
    }

    [Fact]
    public async Task A_webhook_that_skips_scans_fills_in_the_steps_between_and_late_reports_are_only_noted()
    {
        var (admin, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var shipment = await _c.BookAsync(admin, order.OrderId);
        var id = shipment.GetProperty("id").GetGuid();
        var consignment = Str(shipment, "consignmentId");

        // Delivered straight after booking: the steps between are recorded as inferred, and the cash is what the courier says it collected.
        var delivered = await TestApi.ExpectAsync(await _c.WebhookAsync(consignment, "delivered", "evt-d", codCollected: order.Total), HttpStatusCode.OK);
        Assert.Equal("processed", Str(delivered, "status"));
        var detail = await _c.ShipmentAsync(admin, id);
        Assert.Equal("Delivered", Str(detail, "status"));
        var statuses = detail.GetProperty("events").EnumerateArray().Select(e => Str(e, "status")).ToArray();
        Assert.Equal(["Booked", "PickedUp", "InTransit", "OutForDelivery", "Delivered"], statuses);
        Assert.Equal(3, detail.GetProperty("events").EnumerateArray().Count(e => Str(e, "rawStatus") == "inferred"));
        Assert.Equal(order.Total, detail.GetProperty("codCollectedAmount").GetDecimal());
        var final = await O.OrderAsync(admin, order.OrderId);
        Assert.Equal("Delivered", Str(final, "status"));
        Assert.Equal("CollectedByCourier", Str(final, "paymentStatus"));

        // A report that comes too late (the courier says in transit after delivery) is written on the timeline and nothing else.
        var late = await TestApi.ExpectAsync(await _c.WebhookAsync(consignment, "in_transit", "evt-late"), HttpStatusCode.OK);
        Assert.Equal("recorded", Str(late, "status"));
        var after = await _c.ShipmentAsync(admin, id);
        Assert.Equal("Delivered", Str(after, "status"));
        Assert.Contains("Ignored", Str(after.GetProperty("events").EnumerateArray().Last(), "description"), StringComparison.Ordinal);

        // A scan that is not a status ("at hub") is a timeline entry too.
        var scanOrder = await _c.PackedAsync(admin, product.Variants[0].Id);
        var scanned = await _c.BookAsync(admin, scanOrder.OrderId);
        var scan = await TestApi.ExpectAsync(await _c.WebhookAsync(Str(scanned, "consignmentId"), "at_hub", "evt-hub", note: "Arrived at Dhaka hub"), HttpStatusCode.OK);
        Assert.Equal("recorded", Str(scan, "status"));
        var scanDetail = await _c.ShipmentAsync(admin, scanned.GetProperty("id").GetGuid());
        Assert.Equal("Booked", Str(scanDetail, "status"));
        Assert.False(scanDetail.GetProperty("events").EnumerateArray().Last().GetProperty("isStatusChange").GetBoolean());
    }

    [Fact]
    public async Task A_courier_that_cancels_the_booking_leaves_the_order_packed()
    {
        var (admin, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var shipment = await _c.BookAsync(admin, order.OrderId);
        var cancelled = await TestApi.ExpectAsync(await _c.WebhookAsync(Str(shipment, "consignmentId"), "cancelled", "evt-c"), HttpStatusCode.OK);
        Assert.Equal("Cancelled", Str(cancelled, "shipmentStatus"));
        Assert.Equal("Packed", Str(await O.OrderAsync(admin, order.OrderId), "status"));
        // and the order can be booked again
        Assert.EndsWith("02", Str(await _c.BookAsync(admin, order.OrderId), "consignmentId"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Webhooks_for_unknown_parcels_providers_and_unconfigured_couriers_are_answered_without_effect()
    {
        var ignored = await TestApi.ExpectAsync(await _c.WebhookAsync("SBX-DOESNOTEXIST", "picked_up", "evt-x"), HttpStatusCode.OK);
        Assert.Equal("ignored", Str(ignored, "status"));
        Assert.Equal(HttpStatusCode.NotFound, (await _c.WebhookAsync("X", "picked_up", "evt-y", provider: "NOSUCH")).StatusCode);
        var slot = await _c.WebhookAsync("X", "picked_up", "evt-z", provider: "PATHAO");
        Assert.Equal(HttpStatusCode.Conflict, slot.StatusCode);
        await StoreHelper.ExpectCodeAsync(slot, "COURIER.NOT_CONFIGURED");
    }

    // ---- polling job ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_status_poll_applies_what_the_courier_reports_and_a_second_sweep_changes_nothing()
    {
        var (admin, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var shipment = await _c.BookAsync(admin, order.OrderId);
        var id = shipment.GetProperty("id").GetGuid();
        var consignment = Str(shipment, "consignmentId");

        // The courier moves the parcel on its side; nobody tells the shop.
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var simulator = (ICourierSimulator)scope.ServiceProvider.GetRequiredService<ICourierProviderRegistry>().Find("SANDBOX")!;
            Assert.True((await simulator.SimulateAsync(consignment, "out_for_delivery", "On the bike", Ct)).IsSuccess);
        }

        var worker = factory.Services.GetServices<IHostedService>().OfType<CourierStatusSyncWorker>().Single();
        var (_, changed, failed) = await worker.SweepAsync(Ct);
        Assert.Equal(1, changed);
        Assert.Equal(0, failed);

        var detail = await _c.ShipmentAsync(admin, id);
        Assert.Equal("OutForDelivery", Str(detail, "status"));
        Assert.Equal("Polling", Str(detail.GetProperty("events").EnumerateArray().Last(), "source"));
        Assert.Equal("OutForDelivery", Str(await O.OrderAsync(admin, order.OrderId), "status"));
        var skipped = detail.GetProperty("events").EnumerateArray().Count(e => Str(e, "rawStatus") == "inferred");
        Assert.Equal(2, skipped); // picked up and in transit were never reported

        var (_, changedAgain, _) = await worker.SweepAsync(Ct);
        Assert.Equal(0, changedAgain);
        Assert.Equal(detail.GetProperty("events").GetArrayLength(), (await _c.ShipmentAsync(admin, id)).GetProperty("events").GetArrayLength());
        Assert.True(await _c.DbAsync(db => db.Shipments.Where(s => s.Id == id).Select(s => s.LastPolledAt).SingleAsync(Ct)) is not null);
    }

    // ---- tracking page, label, list -------------------------------------------------------------------------

    [Fact]
    public async Task The_public_tracking_answer_carries_the_courier_events_without_staff_details()
    {
        var (admin, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var track = new { number = order.Number, mobile = order.Mobile };
        var before = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/orders/track", HttpStatusCode.OK, track);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("shipment").ValueKind);

        var shipment = await _c.BookAsync(admin, order.OrderId);
        var id = shipment.GetProperty("id").GetGuid();
        await _c.AdvanceAsync(admin, id, null, "Staff note: parcel is fragile");
        await _c.WebhookAsync(Str(shipment, "consignmentId"), "at_hub", "evt-hub", note: "Arrived at Dhaka hub");

        var after = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/orders/track", HttpStatusCode.OK, track);
        var s = after.GetProperty("shipment");
        Assert.Equal("Sandbox courier", Str(s, "courierName"));
        Assert.Equal(Str(shipment, "consignmentId"), Str(s, "consignmentId"));
        Assert.Contains(Str(shipment, "consignmentId"), Str(s, "trackingUrl"), StringComparison.Ordinal);
        Assert.Equal("PickedUp", Str(s, "status"));
        var events = s.GetProperty("events").EnumerateArray().ToList();
        Assert.Equal(["Booked", "PickedUp", "PickedUp"], events.Select(e => Str(e, "status")).ToArray());
        Assert.Equal("Picked up by the courier", Str(events[1], "description"));
        Assert.Equal("Arrived at Dhaka hub", Str(events[2], "description"));
        var raw = s.GetRawText();
        Assert.DoesNotContain("fragile", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("actorName", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("rawStatus", raw, StringComparison.Ordinal);

        // The same parcel is on the signed-in customer's order.
        var customer = await H.CustomerTokenAsync(order.Mobile);
        var mine = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/orders/{order.OrderId}", HttpStatusCode.OK, bearer: customer);
        Assert.Equal(Str(shipment, "consignmentId"), Str(mine.GetProperty("shipment"), "consignmentId"));
    }

    [Fact]
    public async Task The_label_data_and_the_list_filters_work()
    {
        var (admin, product) = await _c.ProductAsync(500m);
        var order = await _c.PackedAsync(admin, product.Variants[0].Id, 2);
        var shipment = await _c.BookAsync(admin, order.OrderId);
        var id = shipment.GetProperty("id").GetGuid();

        var label = await H.JsonAsync(HttpMethod.Get, $"/api/v1/shipments/{id}/label", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(Str(shipment, "consignmentId"), Str(label, "barcode"));
        Assert.Equal(order.Number, Str(label, "orderNumber"));
        Assert.Equal(2, label.GetProperty("items").GetInt32());
        Assert.Equal(order.Total, label.GetProperty("codAmount").GetDecimal());
        Assert.False(label.GetProperty("isReturn").GetBoolean());

        var list = await _c.ListAsync(admin, $"?search={order.Number}&status=Booked&codOnly=true&open=true&kind=Forward");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        Assert.Equal(0, (await _c.ListAsync(admin, $"?search={order.Number}&status=Delivered")).GetProperty("total").GetInt32());
        Assert.Equal(0, (await _c.ListAsync(admin, $"?search={order.Number}&kind=Return")).GetProperty("total").GetInt32());
        var events = await H.JsonAsync(HttpMethod.Get, $"/api/v1/shipments/{id}/events", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(1, events.GetArrayLength());
        var suggest = await H.JsonAsync(HttpMethod.Get, $"/api/v1/shipments/orders/{order.OrderId}/suggest", HttpStatusCode.OK, bearer: admin);
        Assert.True(suggest.EnumerateArray().First().GetProperty("recommended").GetBoolean());
        Assert.Equal("SANDBOX", Str(suggest.EnumerateArray().First(), "code"));
    }

    // ---- returns: the reverse parcel ------------------------------------------------------------------------

    [Fact]
    public async Task A_return_pickup_booked_with_the_courier_is_moved_by_the_courier_events()
    {
        var returns = new ReturnHelper(O);
        var (admin, product) = await returns.ProductAsync(500m, ("Red", "M", 10));
        var delivered = await returns.DeliverAsync(admin, product.Variants[0].Id, 2);
        var request = await returns.RequestAsync(delivered, 1);
        var returnId = request.GetProperty("id").GetGuid();
        await returns.ActAsync(admin, returnId, "approve");

        var scheduled = await returns.ActAsync(admin, returnId, "schedule-pickup", new { date = ReturnPickupDate(), useCourier = true, note = "Ring the bell" });
        Assert.Equal("PickupScheduled", Str(scheduled, "status"));
        Assert.Equal("Sandbox courier", scheduled.GetProperty("pickup").GetProperty("courier").GetString());

        var list = await _c.ListAsync(admin, $"?orderId={delivered.OrderId}&kind=Return");
        Assert.Equal(1, list.GetProperty("total").GetInt32());
        var item = list.GetProperty("items")[0];
        var shipmentId = item.GetProperty("id").GetGuid();
        Assert.Equal("Return", Str(item, "kind"));
        Assert.Equal(0m, item.GetProperty("codAmount").GetDecimal());
        var parcel = await _c.ShipmentAsync(admin, shipmentId);
        Assert.Equal(returnId, parcel.GetProperty("returnRequestId").GetGuid());
        Assert.Contains("Dhanmondi", Str(parcel, "pickupAddress"), StringComparison.Ordinal); // collected at the customer
        Assert.Equal("Head Office", Str(parcel, "deliveryName")); // brought to the shop

        // Pickup, then the trip, then arrival at the shop.
        await _c.AdvanceAsync(admin, shipmentId);
        Assert.Equal("PickedUp", Str(await returns.DetailAsync(admin, returnId), "status"));
        await _c.AdvanceAsync(admin, shipmentId);
        await _c.AdvanceAsync(admin, shipmentId);
        Assert.Equal("PickedUp", Str(await returns.DetailAsync(admin, returnId), "status")); // still travelling
        await _c.AdvanceAsync(admin, shipmentId);
        var received = await returns.DetailAsync(admin, returnId);
        Assert.Equal("Received", Str(received, "status"));

        // The order's own track was not touched by the reverse parcel, and the return continues as before.
        Assert.Equal("ReturnInProgress", Str(await O.OrderAsync(admin, delivered.OrderId), "status"));
        var stages = received.GetProperty("timeline").EnumerateArray().Select(t => Str(t, "to")).ToArray();
        Assert.Equal(["Requested", "Approved", "PickupScheduled", "PickedUp", "Received"], stages);
    }

    [Fact]
    public async Task The_manual_return_pickup_without_a_courier_still_works()
    {
        var returns = new ReturnHelper(O);
        var (admin, product) = await returns.ProductAsync(500m, ("Red", "M", 10));
        var delivered = await returns.DeliverAsync(admin, product.Variants[0].Id, 1);
        var returnId = (await returns.RequestAsync(delivered, 1)).GetProperty("id").GetGuid();
        await returns.ActAsync(admin, returnId, "approve");
        var scheduled = await returns.ActAsync(admin, returnId, "schedule-pickup", ReturnHelper.Pickup());
        Assert.Equal("Pathao", scheduled.GetProperty("pickup").GetProperty("courier").GetString());
        Assert.Equal(0, (await _c.ListAsync(admin, $"?orderId={delivered.OrderId}&kind=Return")).GetProperty("total").GetInt32());
        await returns.ActAsync(admin, returnId, "picked-up");
        Assert.Equal("Received", Str(await returns.ActAsync(admin, returnId, "received"), "status"));
    }

    private static string ReturnPickupDate() => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6)).AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);
}
