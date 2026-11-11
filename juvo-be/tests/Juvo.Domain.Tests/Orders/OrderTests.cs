using Juvo.Domain.Orders;

namespace Juvo.Domain.Tests.Orders;

public class OrderStateMachineTests
{
    private static readonly (OrderStatus From, OrderStatus To)[] AllowedOrder =
    [
        (OrderStatus.Placed, OrderStatus.Confirmed), (OrderStatus.Placed, OrderStatus.Cancelled),
        (OrderStatus.Confirmed, OrderStatus.Packed), (OrderStatus.Confirmed, OrderStatus.Cancelled),
        (OrderStatus.Packed, OrderStatus.HandedToCourier), (OrderStatus.Packed, OrderStatus.Cancelled),
        (OrderStatus.HandedToCourier, OrderStatus.InTransit), (OrderStatus.InTransit, OrderStatus.OutForDelivery),
        (OrderStatus.OutForDelivery, OrderStatus.Delivered), (OrderStatus.OutForDelivery, OrderStatus.DeliveryFailed),
        (OrderStatus.DeliveryFailed, OrderStatus.OutForDelivery), (OrderStatus.DeliveryFailed, OrderStatus.Returned),
        (OrderStatus.Delivered, OrderStatus.ReturnInProgress), (OrderStatus.ReturnInProgress, OrderStatus.Returned),
    ];

    [Fact]
    public void Exactly_the_transitions_of_the_specification_are_allowed_on_the_order_track()
    {
        foreach (var from in Enum.GetValues<OrderStatus>())
        {
            foreach (var to in Enum.GetValues<OrderStatus>())
            {
                var expected = AllowedOrder.Contains((from, to));
                Assert.True(expected == OrderStateMachine.CanMove(from, to), $"{from} -> {to} should be {(expected ? "allowed" : "refused")}");
            }
        }
    }

    [Fact]
    public void Cancelled_and_Returned_are_final_and_only_early_statuses_can_be_cancelled()
    {
        Assert.Empty(OrderStateMachine.NextFor(OrderStatus.Cancelled));
        Assert.Empty(OrderStateMachine.NextFor(OrderStatus.Returned));
        Assert.Equal(new[] { OrderStatus.Placed, OrderStatus.Confirmed, OrderStatus.Packed },
            Enum.GetValues<OrderStatus>().Where(OrderStateMachine.CanCancel).Order().ToArray());
    }

    [Fact]
    public void Payment_track_follows_the_specification()
    {
        Assert.True(OrderStateMachine.CanMove(PaymentStatus.Pending, PaymentStatus.Paid));
        Assert.True(OrderStateMachine.CanMove(PaymentStatus.Pending, PaymentStatus.Failed));
        Assert.True(OrderStateMachine.CanMove(PaymentStatus.Pending, PaymentStatus.UnderReview));
        Assert.True(OrderStateMachine.CanMove(PaymentStatus.UnderReview, PaymentStatus.Rejected));
        Assert.True(OrderStateMachine.CanMove(PaymentStatus.Paid, PaymentStatus.PartiallyRefunded));
        Assert.True(OrderStateMachine.CanMove(PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded));
        Assert.True(OrderStateMachine.CanMove(PaymentStatus.Pending, PaymentStatus.CollectedByCourier));
        Assert.True(OrderStateMachine.CanMove(PaymentStatus.CollectedByCourier, PaymentStatus.Settled));

        // A payment is only Paid after verification: it cannot jump from Failed or Cancelled to Paid, and a refund needs a payment.
        Assert.False(OrderStateMachine.CanMove(PaymentStatus.Failed, PaymentStatus.Paid));
        Assert.False(OrderStateMachine.CanMove(PaymentStatus.Cancelled, PaymentStatus.Paid));
        Assert.False(OrderStateMachine.CanMove(PaymentStatus.Pending, PaymentStatus.Refunded));
        Assert.False(OrderStateMachine.CanMove(PaymentStatus.Settled, PaymentStatus.Pending));
    }

    [Fact]
    public void The_delivery_track_follows_the_order_track()
    {
        Assert.Equal(DeliveryStatus.NotShipped, OrderStateMachine.DeliveryFor(OrderStatus.Confirmed));
        Assert.Equal(DeliveryStatus.ReadyToShip, OrderStateMachine.DeliveryFor(OrderStatus.Packed));
        Assert.Equal(DeliveryStatus.Failed, OrderStateMachine.DeliveryFor(OrderStatus.DeliveryFailed));
        Assert.Equal(DeliveryStatus.Delivered, OrderStateMachine.DeliveryFor(OrderStatus.Delivered));
        Assert.Equal(DeliveryStatus.Cancelled, OrderStateMachine.DeliveryFor(OrderStatus.Cancelled));
        foreach (var status in Enum.GetValues<OrderStatus>())
        {
            Assert.True(Enum.IsDefined(OrderStateMachine.DeliveryFor(status)));
        }
    }
}

public class OrderAggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.Zero);

    private static Order NewOrder(string method = "COD", decimal subtotal = 1000m, decimal coupon = 100m, decimal manual = 50m, decimal delivery = 70m)
    {
        var shipping = new ShippingSnapshot("Nadia", "+8801712345678", "BD", null, "Dhaka", null, "Dhaka", null, null, null, "House 1, Road 2", null, null, null, null, null, null);
        var data = new NewOrder("JV-HO-2611-000001", Guid.NewGuid(), OrderSource.Web, Guid.NewGuid(), "+8801712345678", "Nadia", null, method, "AllStock", "DHAKA_CITY", "BDT", subtotal, coupon, "EID",
            null, manual, delivery, delivery, 0m, 500, shipping, method == "COD" ? null : Now.AddMinutes(30), null, "fp", "th", null, null, false, Now, null, null);
        return Order.Place(data, [new NewOrderItem(Guid.NewGuid(), Guid.NewGuid(), "Tee", null, "SKU-1", "Red", "M", null, 500m, 2, 250, true)], Now);
    }

    [Fact]
    public void A_new_order_starts_Placed_with_one_history_row_per_track_and_one_event()
    {
        var order = NewOrder();

        Assert.Equal(OrderStatus.Placed, order.Status);
        Assert.Equal(PaymentStatus.Pending, order.PaymentStatus);
        Assert.Equal(DeliveryStatus.NotShipped, order.DeliveryStatus);
        Assert.Equal(3, order.History.Count);
        Assert.Equal(new[] { OrderTrack.Order, OrderTrack.Payment, OrderTrack.Delivery }, order.History.Select(h => h.Track).Order().ToArray());
        Assert.Equal(1000m - 100m - 50m + 70m, order.GrandTotal);
        Assert.IsType<OrderPlaced>(Assert.Single(order.DomainEvents));
        Assert.Equal(1000m, Assert.Single(order.Items).LineTotal);
    }

    [Fact]
    public void An_invalid_transition_throws_and_changes_nothing()
    {
        var order = NewOrder();

        var ex = Assert.Throws<InvalidOperationException>(() => order.TransitionTo(OrderStatus.Packed, Now, null, null, null));

        Assert.Contains("Placed", ex.Message, StringComparison.Ordinal);
        Assert.Equal(OrderStatus.Placed, order.Status);
        Assert.Equal(3, order.History.Count);
    }

    [Fact]
    public void Every_status_change_writes_who_when_from_to_and_reason_and_raises_an_event()
    {
        var order = NewOrder();
        order.ClearDomainEvents();
        var staff = Guid.NewGuid();

        order.TransitionTo(OrderStatus.Confirmed, Now.AddMinutes(5), staff, "Rina", "Called the customer");

        var row = order.History.Single(h => h.Track == OrderTrack.Order && h.ToStatus == "Confirmed");
        Assert.Equal("Placed", row.FromStatus);
        Assert.Equal("Called the customer", row.Reason);
        Assert.Equal(staff, row.ActorId);
        Assert.Equal("Rina", row.ActorName);
        Assert.Equal(Now.AddMinutes(5), row.At);
        Assert.Equal(Now.AddMinutes(5), order.ConfirmedAt);
        var changed = Assert.IsType<OrderStatusChanged>(Assert.Single(order.DomainEvents));
        Assert.Equal(OrderTrack.Order, changed.Track);
    }

    [Fact]
    public void Cancelling_an_unpaid_order_also_cancels_the_pending_payment_and_the_delivery_track()
    {
        var order = NewOrder("SANDBOX");

        order.TransitionTo(OrderStatus.Cancelled, Now, null, "System", "Payment not received");

        Assert.Equal(OrderStatus.Cancelled, order.Status);
        Assert.Equal(PaymentStatus.Cancelled, order.PaymentStatus);
        Assert.Equal(DeliveryStatus.Cancelled, order.DeliveryStatus);
        Assert.Equal("Payment not received", order.CancelReason);
        Assert.Null(order.PaymentHoldExpiresAt);
        Assert.Throws<InvalidOperationException>(() => order.TransitionTo(OrderStatus.Confirmed, Now, null, null, null));
    }

    [Fact]
    public void Delivery_of_a_cash_on_delivery_order_marks_the_cash_as_collected()
    {
        var order = NewOrder();
        foreach (var step in new[] { OrderStatus.Confirmed, OrderStatus.Packed, OrderStatus.HandedToCourier, OrderStatus.InTransit, OrderStatus.OutForDelivery, OrderStatus.Delivered })
        {
            order.TransitionTo(step, Now, null, null, null);
        }

        Assert.Equal(PaymentStatus.CollectedByCourier, order.PaymentStatus);
        Assert.Equal(DeliveryStatus.Delivered, order.DeliveryStatus);
        Assert.NotNull(order.DeliveredAt);
        Assert.Throws<InvalidOperationException>(() => order.TransitionTo(OrderStatus.Cancelled, Now, null, null, null));
    }

    [Fact]
    public void A_held_order_waits_for_a_decision_and_a_decision_is_recorded()
    {
        var order = NewOrder();
        order.RecordRisk([("DEVICE_VELOCITY", "Many orders", 40, RuleAction.Hold)], Now, 70m);
        Assert.True(order.IsHeld);
        Assert.Equal(FraudState.Flagged, order.FraudState);
        Assert.Equal(40, order.RiskScore);

        var call = order.Decide(FraudAction.Call, "Phone switched off", Now, Guid.NewGuid(), "Rina");
        Assert.Equal(FraudState.InReview, order.FraudState);
        Assert.Equal(FraudState.Flagged, call.FromState);
        Assert.True(order.IsHeld);

        order.Decide(FraudAction.Approve, null, Now, Guid.NewGuid(), "Rina");
        Assert.False(order.IsHeld);
        Assert.Throws<InvalidOperationException>(() => order.Decide(FraudAction.Approve, null, Now, null, null));
    }

    [Fact]
    public void A_flag_without_a_hold_does_not_hold_the_order()
    {
        var order = NewOrder();

        order.RecordRisk([("SHORT_ADDRESS", "Short", 15, RuleAction.Flag)], Now, null);

        Assert.False(order.IsHeld);
        Assert.Equal(FraudState.None, order.FraudState);
        Assert.Single(order.Flags);
    }

    [Fact]
    public void The_branch_can_only_change_before_packing()
    {
        var order = NewOrder();
        var other = Guid.NewGuid();
        order.ReassignBranch(other, Now, null, "Rina", "More stock there");
        Assert.Equal(other, order.BranchId);
        Assert.Equal("Staff", order.RoutingRule);

        order.TransitionTo(OrderStatus.Confirmed, Now, null, null, null);
        order.TransitionTo(OrderStatus.Packed, Now, null, null, null);
        Assert.Throws<InvalidOperationException>(() => order.ReassignBranch(Guid.NewGuid(), Now, null, null, null));
    }

    [Fact]
    public void An_order_needs_at_least_one_line()
    {
        var shipping = new ShippingSnapshot("N", "+8801712345678", "BD", null, null, null, null, null, null, null, "House 1", null, null, null, null, null, null);
        var data = new NewOrder("JV-HO-2611-000002", Guid.NewGuid(), OrderSource.Web, Guid.NewGuid(), "+8801712345678", null, null, "COD", "AllStock", null, "BDT", 0m, 0m, null, null, 0m, 0m, 0m, 0m, 0,
            shipping, null, null, "fp", "th", null, null, false, null, null, null);

        Assert.Throws<ArgumentException>(() => Order.Place(data, [], Now));
    }
}
