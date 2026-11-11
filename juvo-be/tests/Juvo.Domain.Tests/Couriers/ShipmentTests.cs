using Juvo.Domain.Couriers;
using Juvo.Domain.Orders;

namespace Juvo.Domain.Tests.Couriers;

public class ShipmentStateMachineTests
{
    private static readonly (ShipmentStatus From, ShipmentStatus To)[] Allowed =
    [
        (ShipmentStatus.Booked, ShipmentStatus.PickedUp), (ShipmentStatus.Booked, ShipmentStatus.Cancelled),
        (ShipmentStatus.PickedUp, ShipmentStatus.InTransit),
        (ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery),
        (ShipmentStatus.OutForDelivery, ShipmentStatus.Delivered), (ShipmentStatus.OutForDelivery, ShipmentStatus.DeliveryFailed),
        (ShipmentStatus.DeliveryFailed, ShipmentStatus.OutForDelivery), (ShipmentStatus.DeliveryFailed, ShipmentStatus.Returned),
    ];

    [Fact]
    public void Exactly_the_listed_moves_are_allowed_for_every_pair_of_statuses()
    {
        foreach (var from in Enum.GetValues<ShipmentStatus>())
        {
            foreach (var to in Enum.GetValues<ShipmentStatus>())
            {
                var expected = Allowed.Contains((from, to));
                Assert.True(expected == ShipmentStateMachine.CanMove(from, to), $"{from} -> {to} should be {(expected ? "allowed" : "refused")}");
            }
        }
    }

    [Fact]
    public void Delivered_Returned_and_Cancelled_are_final_and_the_rest_are_open()
    {
        foreach (var final in new[] { ShipmentStatus.Delivered, ShipmentStatus.Returned, ShipmentStatus.Cancelled })
        {
            Assert.Empty(ShipmentStateMachine.NextFor(final));
            Assert.False(ShipmentStateMachine.IsOpen(final));
            Assert.True(ShipmentStateMachine.IsFinal(final));
        }

        foreach (var open in new[] { ShipmentStatus.Booked, ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, ShipmentStatus.DeliveryFailed })
        {
            Assert.True(ShipmentStateMachine.IsOpen(open));
        }
    }

    [Fact]
    public void Every_shipment_status_maps_onto_the_order_and_delivery_tracks_as_the_table_says()
    {
        var expected = new Dictionary<ShipmentStatus, (OrderStatus? Order, DeliveryStatus Delivery)>
        {
            [ShipmentStatus.Booked] = (null, DeliveryStatus.ReadyToShip),
            [ShipmentStatus.PickedUp] = (OrderStatus.HandedToCourier, DeliveryStatus.HandedToCourier),
            [ShipmentStatus.InTransit] = (OrderStatus.InTransit, DeliveryStatus.InTransit),
            [ShipmentStatus.OutForDelivery] = (OrderStatus.OutForDelivery, DeliveryStatus.OutForDelivery),
            [ShipmentStatus.Delivered] = (OrderStatus.Delivered, DeliveryStatus.Delivered),
            [ShipmentStatus.DeliveryFailed] = (OrderStatus.DeliveryFailed, DeliveryStatus.Failed),
            [ShipmentStatus.Returned] = (OrderStatus.Returned, DeliveryStatus.Returned),
            [ShipmentStatus.Cancelled] = (null, DeliveryStatus.ReadyToShip),
        };

        foreach (var status in Enum.GetValues<ShipmentStatus>())
        {
            Assert.Equal(expected[status].Order, ShipmentStateMachine.OrderStatusFor(status));
            Assert.Equal(expected[status].Delivery, ShipmentStateMachine.DeliveryTrackFor(status));
        }
    }

    [Fact]
    public void Every_shipment_move_between_two_mapped_statuses_is_also_a_legal_order_move()
    {
        // The courier can only ever drive the order along paths the order state machine allows (so the sync never has to force anything).
        foreach (var from in Enum.GetValues<ShipmentStatus>())
        {
            foreach (var to in ShipmentStateMachine.NextFor(from))
            {
                if (ShipmentStateMachine.OrderStatusFor(from) is { } orderFrom && ShipmentStateMachine.OrderStatusFor(to) is { } orderTo)
                {
                    Assert.True(OrderStateMachine.CanMove(orderFrom, orderTo), $"shipment {from} -> {to} would need the order to go {orderFrom} -> {orderTo}");
                }
            }
        }

        // And the first courier step starts from the order status a booked parcel leaves behind.
        Assert.True(OrderStateMachine.CanMove(OrderStatus.Packed, ShipmentStateMachine.OrderStatusFor(ShipmentStatus.PickedUp)!.Value));
    }

    [Fact]
    public void A_report_that_skips_scans_is_expanded_into_the_steps_between()
    {
        Assert.Equal([ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, ShipmentStatus.Delivered], ShipmentStateMachine.PathTo(ShipmentStatus.Booked, ShipmentStatus.Delivered));
        Assert.Equal([ShipmentStatus.OutForDelivery], ShipmentStateMachine.PathTo(ShipmentStatus.DeliveryFailed, ShipmentStatus.OutForDelivery));
        Assert.Equal([ShipmentStatus.Returned], ShipmentStateMachine.PathTo(ShipmentStatus.DeliveryFailed, ShipmentStatus.Returned));
        Assert.Equal([ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, ShipmentStatus.DeliveryFailed], ShipmentStateMachine.PathTo(ShipmentStatus.Booked, ShipmentStatus.DeliveryFailed));
        Assert.Equal([ShipmentStatus.Cancelled], ShipmentStateMachine.PathTo(ShipmentStatus.Booked, ShipmentStatus.Cancelled));
    }

    [Fact]
    public void There_is_no_path_back_or_out_of_a_final_status_and_never_through_Cancelled()
    {
        Assert.Empty(ShipmentStateMachine.PathTo(ShipmentStatus.Delivered, ShipmentStatus.InTransit));
        Assert.Empty(ShipmentStateMachine.PathTo(ShipmentStatus.InTransit, ShipmentStatus.PickedUp));
        Assert.Empty(ShipmentStateMachine.PathTo(ShipmentStatus.InTransit, ShipmentStatus.Cancelled));
        Assert.Empty(ShipmentStateMachine.PathTo(ShipmentStatus.Returned, ShipmentStatus.Delivered));
        Assert.Empty(ShipmentStateMachine.PathTo(ShipmentStatus.Cancelled, ShipmentStatus.Booked));
        Assert.Empty(ShipmentStateMachine.PathTo(ShipmentStatus.Booked, ShipmentStatus.Booked));
        foreach (var from in Enum.GetValues<ShipmentStatus>())
        {
            foreach (var to in Enum.GetValues<ShipmentStatus>().Where(t => t != ShipmentStatus.Cancelled))
            {
                Assert.DoesNotContain(ShipmentStatus.Cancelled, ShipmentStateMachine.PathTo(from, to));
            }
        }
    }
}

public class ShipmentAggregateTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private static Shipment Booked(decimal cod = 1000m) => Shipment.Book(
        new NewShipment(Guid.NewGuid(), "JV-HO-2611-000001", Guid.NewGuid(), Guid.NewGuid(), "SANDBOX", "Sandbox courier", ShipmentKind.Forward, null, "SBX-1", null, null, 500, null, null, null, cod, 60m,
            "Head Office", null, "Dhaka", "Nadia", "+8801712345678", "House 1, Dhaka", "Dhaka", null), T0, null, "Staff");

    [Fact]
    public void A_new_shipment_is_Booked_with_one_event_and_one_domain_event()
    {
        var s = Booked();
        Assert.Equal(ShipmentStatus.Booked, s.Status);
        Assert.Single(s.Events);
        Assert.Single(s.DomainEvents.OfType<ShipmentEventAdded>());
        Assert.True(s.IsOpen);
    }

    [Fact]
    public void Moving_records_an_event_each_time_and_counts_delivery_attempts()
    {
        var s = Booked();
        s.MoveTo(ShipmentStatus.PickedUp, "picked_up", "e1", null, null, T0.AddHours(1), ShipmentEventSource.Webhook, "C");
        s.MoveTo(ShipmentStatus.InTransit, "in_transit", "e2", null, null, T0.AddHours(2), ShipmentEventSource.Webhook, "C");
        s.MoveTo(ShipmentStatus.OutForDelivery, "out_for_delivery", "e3", null, null, T0.AddHours(3), ShipmentEventSource.Webhook, "C");
        Assert.Equal(1, s.Attempts);
        s.MoveTo(ShipmentStatus.DeliveryFailed, "delivery_failed", "e4", "Customer not reachable", null, T0.AddHours(4), ShipmentEventSource.Webhook, "C");
        Assert.Equal("Customer not reachable", s.FailureReason);
        s.MoveTo(ShipmentStatus.OutForDelivery, "out_for_delivery", "e5", null, null, T0.AddHours(5), ShipmentEventSource.Webhook, "C");
        Assert.Equal(2, s.Attempts);
        Assert.Null(s.FailureReason);
        s.MoveTo(ShipmentStatus.Delivered, "delivered", "e6", null, null, T0.AddHours(6), ShipmentEventSource.Webhook, "C");
        Assert.Equal(7, s.Events.Count);
        Assert.Equal(T0.AddHours(6), s.DeliveredAt);
        Assert.False(s.IsOpen);
        Assert.Equal(7, s.DomainEvents.OfType<ShipmentEventAdded>().Count());
    }

    [Fact]
    public void A_move_the_state_machine_does_not_allow_throws_and_changes_nothing()
    {
        var s = Booked();
        Assert.Throws<InvalidOperationException>(() => s.MoveTo(ShipmentStatus.Delivered, "delivered", "x", null, null, T0, ShipmentEventSource.Webhook, null));
        Assert.Equal(ShipmentStatus.Booked, s.Status);
        Assert.Single(s.Events);
    }

    [Fact]
    public void A_scan_adds_to_the_timeline_without_changing_the_status()
    {
        var s = Booked();
        var scan = s.RecordScan("at_hub", "h1", "Arrived at the hub", "Dhaka hub", T0.AddHours(1), ShipmentEventSource.Webhook, "C");
        Assert.False(scan.IsStatusChange);
        Assert.Equal(ShipmentStatus.Booked, s.Status);
        Assert.Equal(2, s.Events.Count);
        Assert.Equal("at_hub", s.LastRawStatus);
    }

    [Fact]
    public void Cash_collected_is_recorded_once()
    {
        var s = Booked(1500m);
        s.RecordCodCollected(1500m, T0);
        s.RecordCodCollected(999m, T0.AddHours(1));
        Assert.Equal(1500m, s.CodCollectedAmount);
        Assert.Equal(T0, s.CodCollectedAt);
    }
}

public class CourierTests
{
    private static Courier Make(decimal baseRate = 60m, decimal perKg = 20m, int upTo = 1000, string? template = null) =>
        new("pathao", new CourierDetails("Pathao", "pathao", false, 10, true, false, null, 20000, ["dhaka", " DHK "], null, baseRate, perKg, upTo, template, null, new Dictionary<string, string> { ["store"] = "12" }));

    [Fact]
    public void Codes_and_zone_codes_are_normalised_and_the_cost_follows_the_weight_slabs()
    {
        var c = Make();
        Assert.Equal("PATHAO", c.Code);
        Assert.Equal("PATHAO", c.Provider);
        Assert.Equal(["DHAKA", "DHK"], c.ZoneCodes);
        Assert.Equal("12", c.Settings["store"]);
        Assert.Equal(60m, c.CostFor(300));
        Assert.Equal(60m, c.CostFor(1000));
        Assert.Equal(80m, c.CostFor(1001));
        Assert.Equal(100m, c.CostFor(2500));
    }

    [Fact]
    public void The_tracking_url_fills_in_the_consignment()
    {
        Assert.Equal("https://t.example/track/AB%2012", Make(template: "https://t.example/track/{consignment}").TrackingUrlFor("AB 12"));
        Assert.Null(Make().TrackingUrlFor("X"));
    }
}

public class CodSettlementTests
{
    private static CodSettlement Batch(params decimal[] expected) =>
        new("CS-HO-2611-000001", Guid.NewGuid(), Guid.NewGuid(), "Sandbox courier",
            expected.Select((e, i) => new CodSettlementItem(Guid.NewGuid(), Guid.NewGuid(), $"JV-{i}", $"SBX-{i}", e, 60m)), null, "Accountant");

    [Fact]
    public void Expected_is_the_sum_and_the_difference_is_received_minus_expected()
    {
        var s = Batch(1000m, 2000m, 500m);
        Assert.Equal(3500m, s.ExpectedTotal);
        Assert.Equal(180m, s.FeesTotal);
        Assert.Null(s.Difference);
        var lines = new[] { new ReceivedLine(s.Items[0].ShipmentId, 1000m, null), new ReceivedLine(s.Items[1].ShipmentId, 1950m, "50 short"), new ReceivedLine(s.Items[2].ShipmentId, 520m, "20 over") };
        s.Reconcile(lines, DateTimeOffset.UtcNow, "A");
        Assert.Equal(SettlementStatus.Reconciled, s.Status);
        Assert.Equal(3470m, s.ReceivedTotal);
        Assert.Equal(-30m, s.Difference);
        Assert.Equal([0m, -50m, 20m], s.Items.Select(i => i.Difference!.Value).ToArray());
    }

    [Fact]
    public void A_batch_must_be_reconciled_before_it_is_settled_and_a_settled_batch_is_closed()
    {
        var s = Batch(100m);
        Assert.Throws<InvalidOperationException>(() => s.Settle("REF", DateTimeOffset.UtcNow, "A"));
        s.Reconcile([new ReceivedLine(s.Items[0].ShipmentId, 100m, null)], DateTimeOffset.UtcNow, "A");
        s.Settle(" REF-1 ", DateTimeOffset.UtcNow, "A");
        Assert.Equal(SettlementStatus.Settled, s.Status);
        Assert.Equal("REF-1", s.Reference);
        Assert.Throws<InvalidOperationException>(() => s.Cancel(DateTimeOffset.UtcNow));
        Assert.Throws<InvalidOperationException>(() => s.Reconcile([new ReceivedLine(s.Items[0].ShipmentId, 100m, null)], DateTimeOffset.UtcNow, "A"));
    }

    [Fact]
    public void Reconciling_needs_a_line_for_every_parcel()
    {
        var s = Batch(100m, 200m);
        Assert.Throws<ArgumentException>(() => s.Reconcile([new ReceivedLine(s.Items[0].ShipmentId, 100m, null)], DateTimeOffset.UtcNow, "A"));
    }
}
