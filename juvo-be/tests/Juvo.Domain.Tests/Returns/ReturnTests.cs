using Juvo.Domain.Orders;
using Juvo.Domain.Returns;

namespace Juvo.Domain.Tests.Returns;

public class ReturnStateMachineTests
{
    private static readonly (ReturnStatus From, ReturnStatus To)[] Allowed =
    [
        (ReturnStatus.Requested, ReturnStatus.Approved), (ReturnStatus.Requested, ReturnStatus.Rejected), (ReturnStatus.Requested, ReturnStatus.Cancelled),
        (ReturnStatus.Approved, ReturnStatus.PickupScheduled), (ReturnStatus.Approved, ReturnStatus.Cancelled),
        (ReturnStatus.PickupScheduled, ReturnStatus.PickedUp), (ReturnStatus.PickedUp, ReturnStatus.Received), (ReturnStatus.Received, ReturnStatus.Inspected),
        (ReturnStatus.Inspected, ReturnStatus.Refunded), (ReturnStatus.Inspected, ReturnStatus.Exchanged), (ReturnStatus.Inspected, ReturnStatus.RefundRejected),
    ];

    [Fact]
    public void Exactly_the_transitions_of_the_specification_are_allowed()
    {
        foreach (var from in Enum.GetValues<ReturnStatus>())
        {
            foreach (var to in Enum.GetValues<ReturnStatus>())
            {
                var expected = Allowed.Contains((from, to));
                Assert.True(expected == ReturnStateMachine.CanMove(from, to), $"{from} -> {to} should be {(expected ? "allowed" : "refused")}");
            }
        }
    }

    [Fact]
    public void Closed_statuses_are_final_and_only_open_ones_hold_the_order_in_return_in_progress()
    {
        foreach (var status in new[] { ReturnStatus.Rejected, ReturnStatus.Refunded, ReturnStatus.Exchanged, ReturnStatus.RefundRejected, ReturnStatus.Cancelled })
        {
            Assert.True(ReturnStateMachine.IsFinal(status), status.ToString());
            Assert.False(ReturnStateMachine.IsOpen(status), status.ToString());
        }

        Assert.Equal(
            [ReturnStatus.Approved, ReturnStatus.PickupScheduled, ReturnStatus.PickedUp, ReturnStatus.Received, ReturnStatus.Inspected],
            Enum.GetValues<ReturnStatus>().Where(ReturnStateMachine.IsOpen).Order().ToArray());
        Assert.False(ReturnStateMachine.HoldsItems(ReturnStatus.Rejected));
        Assert.False(ReturnStateMachine.HoldsItems(ReturnStatus.Cancelled));
        Assert.False(ReturnStateMachine.HoldsItems(ReturnStatus.RefundRejected));
        Assert.True(ReturnStateMachine.HoldsItems(ReturnStatus.Requested));
        Assert.True(ReturnStateMachine.HoldsItems(ReturnStatus.Refunded));
    }

    [Fact]
    public void The_cash_on_delivery_payment_track_can_be_refunded_too()
    {
        Assert.True(OrderStateMachine.CanMove(PaymentStatus.CollectedByCourier, PaymentStatus.PartiallyRefunded));
        Assert.True(OrderStateMachine.CanMove(PaymentStatus.Settled, PaymentStatus.Refunded));
        Assert.False(OrderStateMachine.CanMove(PaymentStatus.Pending, PaymentStatus.PartiallyRefunded));
        Assert.False(OrderStateMachine.CanMove(PaymentStatus.Refunded, PaymentStatus.PartiallyRefunded));
    }
}

public class ReturnAggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.FromHours(6));
    private static readonly Guid Staff = Guid.NewGuid();

    private static ReturnRequest New(ReturnKind kind = ReturnKind.Return, ReturnFault fault = ReturnFault.Customer) =>
        ReturnRequest.Request(
            new NewReturn("RT-HO-2611-000001", Guid.NewGuid(), Guid.NewGuid(), "JV-HO-2611-000001", Guid.NewGuid(), "+8801712345678", "Nadia", kind, "SIZE_FIT", "Too small", fault, null, ["returns/a.webp"]),
            [
                new NewReturnItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Tee", "TEE-1", "Red", "M", null, 2, 500m, 900m, null, null),
                new NewReturnItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Cap", "CAP-1", null, null, null, 1, 300m, 300m, null, null),
            ],
            null, "Nadia", Now);

    private static void ToInspected(ReturnRequest r, bool accept = true, ReturnFault fault = ReturnFault.Customer, decimal delivery = 0m, decimal charge = 0m)
    {
        r.Approve(Staff, "Ana", null, Now);
        r.SchedulePickup(new DateOnly(2026, 11, 12), "Pathao", null, Staff, "Ana", Now);
        r.MarkPickedUp(Staff, "Ana", Now);
        r.MarkReceived(Staff, "Ana", Now);
        r.Inspect([.. r.Items.Select(i => new ItemInspection(i.Id, ItemCondition.Good, true, accept))], fault, delivery, charge, null, Staff, "Ana", Now);
    }

    [Fact]
    public void A_new_request_has_lines_photos_history_and_an_event()
    {
        var r = New();

        Assert.Equal(ReturnStatus.Requested, r.Status);
        Assert.Equal(3, r.TotalQuantity);
        Assert.Equal(1200m, r.ItemsRefund);
        Assert.Single(r.Photos);
        var history = Assert.Single(r.History);
        Assert.Null(history.FromStatus);
        Assert.Equal("Requested", history.ToStatus);
        var raised = Assert.IsType<ReturnStatusChanged>(Assert.Single(r.DomainEvents));
        Assert.Equal("Requested", raised.To);
        Assert.Null(raised.From);
    }

    [Fact]
    public void A_request_needs_a_line_and_a_positive_quantity()
    {
        var data = new NewReturn("RT-HO-2611-000002", Guid.NewGuid(), Guid.NewGuid(), "JV-1", Guid.NewGuid(), "+8801712345678", null, ReturnKind.Return, "OTHER", null, ReturnFault.Customer, null, []);
        Assert.Throws<ArgumentException>(() => ReturnRequest.Request(data, [], null, null, Now));
        Assert.Throws<ArgumentException>(() => ReturnRequest.Request(data, [new NewReturnItem(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "x", "x", null, null, null, 0, 1m, 1m, null, null)], null, null, Now));
    }

    [Fact]
    public void The_happy_path_writes_one_history_row_and_one_event_per_step()
    {
        var r = New();
        ToInspected(r, delivery: 0m);
        r.Refund(RefundMethod.Cash, "REF-1", Staff, "Ana", Now);

        Assert.Equal(ReturnStatus.Refunded, r.Status);
        Assert.Equal(
            ["Requested", "Approved", "PickupScheduled", "PickedUp", "Received", "Inspected", "Refunded"],
            r.History.Select(h => h.ToStatus).ToArray());
        Assert.Equal(r.History.Count, r.DomainEvents.OfType<ReturnStatusChanged>().Count());
        Assert.NotNull(r.RefundedAt);
        var refund = Assert.Single(r.Refunds);
        Assert.Equal(1200m, refund.Amount);
        Assert.Equal(RefundStatus.Completed, refund.Status);
    }

    [Fact]
    public void Every_forbidden_move_throws_and_changes_nothing()
    {
        var r = New();
        Assert.Throws<InvalidOperationException>(() => r.MarkReceived(Staff, "Ana", Now));
        Assert.Throws<InvalidOperationException>(() => r.SchedulePickup(new DateOnly(2026, 11, 12), null, null, Staff, "Ana", Now));
        Assert.Throws<InvalidOperationException>(() => r.Inspect([], ReturnFault.Customer, 0, 0, null, Staff, "Ana", Now));
        Assert.Throws<InvalidOperationException>(() => r.Refund(RefundMethod.Cash, "x", Staff, "Ana", Now));
        Assert.Equal(ReturnStatus.Requested, r.Status);
        Assert.Single(r.History);

        r.Reject("Out of policy", Staff, "Ana", Now);
        Assert.Equal("Out of policy", r.RejectReason);
        Assert.Throws<InvalidOperationException>(() => r.Approve(Staff, "Ana", null, Now));
        Assert.Throws<InvalidOperationException>(() => r.Cancel(null, null, "Nadia", Now));
    }

    [Fact]
    public void A_customer_can_cancel_while_requested_or_approved_but_not_later()
    {
        var requested = New();
        requested.Cancel("Changed my mind", requested.CustomerId, "Nadia", Now);
        Assert.Equal(ReturnStatus.Cancelled, requested.Status);
        Assert.NotNull(requested.CancelledAt);

        var approved = New();
        approved.Approve(Staff, "Ana", null, Now);
        approved.Cancel(null, approved.CustomerId, "Nadia", Now);
        Assert.Equal(ReturnStatus.Cancelled, approved.Status);

        var scheduled = New();
        scheduled.Approve(Staff, "Ana", null, Now);
        scheduled.SchedulePickup(new DateOnly(2026, 11, 12), null, null, Staff, "Ana", Now);
        Assert.Throws<InvalidOperationException>(() => scheduled.Cancel(null, null, "Nadia", Now));
    }

    [Fact]
    public void Inspection_sums_the_accepted_lines_adds_the_delivery_charge_and_keeps_the_return_charge()
    {
        var seller = New(fault: ReturnFault.Seller);
        ToInspected(seller, fault: ReturnFault.Seller, delivery: 100m);
        Assert.Equal(1200m, seller.ItemsRefund);
        Assert.Equal(100m, seller.DeliveryChargeRefund);
        Assert.Equal(0m, seller.ReturnCharge);
        Assert.Equal(1300m, seller.RefundTotal);

        var customer = New();
        ToInspected(customer, fault: ReturnFault.Customer, delivery: 0m, charge: 60m);
        Assert.Equal(1140m, customer.RefundTotal);
        Assert.All(customer.Items, i => Assert.Equal(ItemCondition.Good, i.Condition));
    }

    [Fact]
    public void A_line_the_inspector_does_not_accept_refunds_nothing_and_a_return_with_nothing_cannot_be_refunded()
    {
        var r = New();
        r.Approve(Staff, "Ana", null, Now);
        r.SchedulePickup(new DateOnly(2026, 11, 12), null, null, Staff, "Ana", Now);
        r.MarkPickedUp(Staff, "Ana", Now);
        r.MarkReceived(Staff, "Ana", Now);
        var first = r.Items[0];
        r.Inspect([new ItemInspection(first.Id, ItemCondition.Damaged, false, false), new ItemInspection(r.Items[1].Id, ItemCondition.Unopened, true, true)], ReturnFault.Customer, 0m, 0m, null, Staff, "Ana", Now);
        Assert.Equal(0m, first.RefundAmount);
        Assert.Equal(300m, r.RefundTotal);

        var none = New();
        ToInspected(none, accept: false, fault: ReturnFault.Seller, delivery: 100m);
        Assert.Equal(0m, none.RefundTotal);
        Assert.Equal(0m, none.DeliveryChargeRefund); // the delivery charge is only given back together with an item refund
        Assert.Throws<InvalidOperationException>(() => none.Refund(RefundMethod.Cash, "x", Staff, "Ana", Now));
        none.RejectRefund("Item was worn", Staff, "Ana", Now);
        Assert.Equal(ReturnStatus.RefundRejected, none.Status);
    }

    [Fact]
    public void Inspection_needs_a_result_for_every_line()
    {
        var r = New();
        r.Approve(Staff, "Ana", null, Now);
        r.SchedulePickup(new DateOnly(2026, 11, 12), null, null, Staff, "Ana", Now);
        r.MarkPickedUp(Staff, "Ana", Now);
        r.MarkReceived(Staff, "Ana", Now);

        Assert.Throws<ArgumentException>(() => r.Inspect([new ItemInspection(r.Items[0].Id, ItemCondition.Good, true, true)], ReturnFault.Customer, 0, 0, null, Staff, "Ana", Now));
    }

    [Fact]
    public void The_return_charge_never_goes_below_zero_money()
    {
        var r = New();
        ToInspected(r, charge: 5000m);
        Assert.Equal(1200m, r.ReturnCharge);
        Assert.Equal(0m, r.RefundTotal);
    }

    [Fact]
    public void A_refund_can_be_written_only_once_and_never_for_an_exchange()
    {
        var r = New();
        ToInspected(r);
        r.Refund(RefundMethod.BankTransfer, " TX-77 ", Staff, "Ana", Now);
        Assert.Equal("TX-77", r.Refunds[0].Reference);
        Assert.Throws<InvalidOperationException>(() => r.Refund(RefundMethod.Cash, "again", Staff, "Ana", Now));

        var exchange = New(ReturnKind.Exchange);
        ToInspected(exchange, fault: ReturnFault.Seller, delivery: 100m);
        Assert.Equal(0m, exchange.RefundTotal);
        Assert.Throws<InvalidOperationException>(() => exchange.Refund(RefundMethod.Cash, "x", Staff, "Ana", Now));
        exchange.Exchange(Guid.NewGuid(), "JV-HO-2611-000009", Staff, "Ana", Now);
        Assert.Equal(ReturnStatus.Exchanged, exchange.Status);
        Assert.Equal("JV-HO-2611-000009", exchange.ReplacementOrderNumber);

        var plain = New();
        ToInspected(plain);
        Assert.Throws<InvalidOperationException>(() => plain.Exchange(Guid.NewGuid(), "JV-2", Staff, "Ana", Now));
    }

    [Fact]
    public void Reasons_point_to_the_party_at_fault_and_can_be_found_by_code()
    {
        Assert.Equal(ReturnFault.Seller, ReturnReason.Find("damaged_item")!.DefaultFault);
        Assert.Equal(ReturnFault.Seller, ReturnReason.Find("WRONG_ITEM")!.DefaultFault);
        Assert.Equal(ReturnFault.Customer, ReturnReason.Find("CHANGED_MIND")!.DefaultFault);
        Assert.Null(ReturnReason.Find("nope"));
        Assert.Null(ReturnReason.Find(null));
        Assert.All(ReturnReason.All, r => Assert.False(string.IsNullOrWhiteSpace(r.LabelBn)));
    }

    [Fact]
    public void An_order_with_a_return_in_progress_can_be_reopened_as_delivered_with_history()
    {
        var order = DeliveredOrder();
        order.TransitionTo(OrderStatus.ReturnInProgress, Now, Staff, "Ana", "Return approved");
        order.ReopenAsDelivered(Now, Staff, "Ana", "The return was cancelled");

        Assert.Equal(OrderStatus.Delivered, order.Status);
        var last = order.History.Where(h => h.Track == OrderTrack.Order).OrderBy(h => h.At).Last();
        Assert.Equal("ReturnInProgress", last.FromStatus);
        Assert.Equal("Delivered", last.ToStatus);
        Assert.Throws<InvalidOperationException>(() => order.ReopenAsDelivered(Now, Staff, "Ana", null));
    }

    private static Order DeliveredOrder()
    {
        var shipping = new ShippingSnapshot("Nadia", "+8801712345678", "BD", null, null, null, null, null, null, null, "House 1", null, null, null, null, null, null);
        var data = new NewOrder("JV-HO-2611-000001", Guid.NewGuid(), OrderSource.Web, Guid.NewGuid(), "+8801712345678", "Nadia", null, "SANDBOX", "DefaultBranch", null, "BDT", 500m, 0m, null, null, 0m, 60m, 60m, 0m, 300,
            shipping, null, null, "fp", "hash", null, null, true, null, null, null);
        var order = Order.Place(data, [new NewOrderItem(Guid.NewGuid(), Guid.NewGuid(), "Tee", null, "TEE-1", null, null, null, 500m, 1, 300, true)], Now);
        order.TransitionTo(OrderStatus.Confirmed, Now, null, null, null);
        order.TransitionPaymentTo(PaymentStatus.Paid, Now, null, null, null);
        order.TransitionTo(OrderStatus.Packed, Now, null, null, null);
        order.TransitionTo(OrderStatus.HandedToCourier, Now, null, null, null);
        order.TransitionTo(OrderStatus.InTransit, Now, null, null, null);
        order.TransitionTo(OrderStatus.OutForDelivery, Now, null, null, null);
        order.TransitionTo(OrderStatus.Delivered, Now, null, null, null);
        return order;
    }
}
