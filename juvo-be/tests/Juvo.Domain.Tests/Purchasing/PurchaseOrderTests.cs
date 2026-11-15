using Juvo.Domain.Purchasing;

namespace Juvo.Domain.Tests.Purchasing;

public class PurchaseStateMachineTests
{
    private static readonly (PurchaseStatus From, PurchaseStatus To)[] Allowed =
    [
        (PurchaseStatus.Draft, PurchaseStatus.Sent), (PurchaseStatus.Draft, PurchaseStatus.Cancelled),
        (PurchaseStatus.Sent, PurchaseStatus.PartlyReceived), (PurchaseStatus.Sent, PurchaseStatus.Received), (PurchaseStatus.Sent, PurchaseStatus.Cancelled),
        (PurchaseStatus.PartlyReceived, PurchaseStatus.Received),
    ];

    [Fact]
    public void Exactly_the_transitions_of_the_specification_are_allowed()
    {
        foreach (var from in Enum.GetValues<PurchaseStatus>())
        {
            foreach (var to in Enum.GetValues<PurchaseStatus>())
            {
                var expected = Allowed.Contains((from, to));
                Assert.True(expected == PurchaseStateMachine.CanMove(from, to), $"{from} -> {to} should be {(expected ? "allowed" : "refused")}");
            }
        }
    }

    [Fact]
    public void Received_and_cancelled_are_final()
    {
        Assert.True(PurchaseStateMachine.IsFinal(PurchaseStatus.Received));
        Assert.True(PurchaseStateMachine.IsFinal(PurchaseStatus.Cancelled));
        Assert.False(PurchaseStateMachine.IsFinal(PurchaseStatus.PartlyReceived));
    }
}

public class PurchaseOrderAggregateTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 9, 0, 0, TimeSpan.FromHours(6));
    private static readonly Guid V1 = Guid.NewGuid();
    private static readonly Guid V2 = Guid.NewGuid();

    private static NewPurchaseLine Line(Guid variant, int quantity, decimal cost) => new(variant, Guid.NewGuid(), "SKU-" + variant.ToString("N")[..4], "Tee", "Red", "M", quantity, cost);

    private static PurchaseOrder NewOrder(params NewPurchaseLine[] lines) =>
        new("PO-HO-2611-000001", Guid.NewGuid(), Guid.NewGuid(), null, " note ", lines.Length == 0 ? [Line(V1, 10, 100m), Line(V2, 5, 40m)] : lines, Now, Guid.NewGuid(), "Buyer");

    private static ReceivedLine Got(PurchaseOrder o, int index, int accepted, int rejected = 0) => new(o.Lines[index].Id, accepted, rejected, null);

    [Fact]
    public void A_new_order_is_a_draft_with_a_history_row_and_totals()
    {
        var o = NewOrder();
        Assert.Equal(PurchaseStatus.Draft, o.Status);
        Assert.Equal("note", o.Notes);
        Assert.Equal(1200m, o.Total); // 10 x 100 + 5 x 40
        Assert.Equal(SupplierPaymentStatus.Unpaid, o.PaymentStatus);
        var first = Assert.Single(o.History);
        Assert.Null(first.FromStatus);
        Assert.Equal(PurchaseStatus.Draft, first.ToStatus);
    }

    [Fact]
    public void A_variant_listed_twice_is_merged_and_empty_or_invalid_lines_are_refused()
    {
        var o = NewOrder(Line(V1, 2, 10m), Line(V1, 3, 12m));
        var line = Assert.Single(o.Lines);
        Assert.Equal((5, 12m), (line.OrderedQuantity, line.UnitCost));
        Assert.Throws<ArgumentException>(() => NewOrder([Line(V1, 0, 10m)]));
        Assert.Throws<ArgumentException>(() => NewOrder([Line(V1, 1, -1m)]));
        Assert.Throws<ArgumentException>(() => new PurchaseOrder("PO", Guid.NewGuid(), Guid.NewGuid(), null, null, [], Now, null, null));
    }

    [Fact]
    public void Receiving_in_parts_moves_through_partly_received_to_received_and_writes_history_only_on_status_changes()
    {
        var o = NewOrder();
        o.Send(Now, null, "Buyer");
        o.Receive([Got(o, 0, 4, 1)], Now.AddDays(1), null, "Clerk");
        Assert.Equal(PurchaseStatus.PartlyReceived, o.Status);
        Assert.Equal((4, 1, 6), (o.Lines[0].ReceivedQuantity, o.Lines[0].RejectedQuantity, o.Lines[0].OutstandingQuantity));
        o.Receive([Got(o, 0, 6)], Now.AddDays(2), null, "Clerk"); // one line is complete, the other not: still partly received
        Assert.Equal(PurchaseStatus.PartlyReceived, o.Status);
        o.Receive([Got(o, 1, 5)], Now.AddDays(3), null, "Clerk");
        Assert.Equal(PurchaseStatus.Received, o.Status);
        Assert.Equal(Now.AddDays(1), o.FirstReceivedAt);
        Assert.Equal(Now.AddDays(3), o.CompletedAt);
        Assert.Equal([PurchaseStatus.Draft, PurchaseStatus.Sent, PurchaseStatus.PartlyReceived, PurchaseStatus.Received], o.History.Select(h => h.ToStatus).ToArray());
        Assert.Equal(1200m, o.ReceivedValue);
    }

    [Fact]
    public void More_than_ordered_is_refused_and_rejected_units_never_complete_an_order()
    {
        var o = NewOrder(Line(V1, 3, 10m));
        o.Send(Now, null, null);
        Assert.Throws<InvalidOperationException>(() => o.Receive([Got(o, 0, 4)], Now, null, null));
        Assert.Equal(0, o.Lines[0].ReceivedQuantity); // nothing changed
        o.Receive([Got(o, 0, 0, 3)], Now, null, null);
        Assert.Equal(PurchaseStatus.Sent, o.Status); // only rejected units: nothing was received yet
        o.Receive([Got(o, 0, 3)], Now, null, null);
        Assert.Equal(PurchaseStatus.Received, o.Status);
        Assert.Equal(3, o.Lines[0].RejectedQuantity);
    }

    [Fact]
    public void Goods_can_only_be_received_on_a_sent_or_partly_received_order()
    {
        var o = NewOrder();
        Assert.Throws<InvalidOperationException>(() => o.Receive([Got(o, 0, 1)], Now, null, null)); // draft
        o.Send(Now, null, null);
        o.Cancel("changed our mind", Now, null, null);
        Assert.Throws<InvalidOperationException>(() => o.Receive([Got(o, 0, 1)], Now, null, null)); // cancelled
        Assert.Equal("changed our mind", o.CancelReason);
    }

    [Fact]
    public void Cancelling_is_only_possible_from_draft_and_sent()
    {
        var draft = NewOrder();
        draft.Cancel("x", Now, null, null);
        Assert.Equal(PurchaseStatus.Cancelled, draft.Status);
        Assert.Throws<InvalidOperationException>(() => draft.Cancel("again", Now, null, null));
        Assert.Throws<InvalidOperationException>(() => draft.Send(Now, null, null));

        var partly = NewOrder();
        partly.Send(Now, null, null);
        partly.Receive([Got(partly, 0, 1)], Now, null, null);
        Assert.Throws<InvalidOperationException>(() => partly.Cancel("late", Now, null, null));
    }

    [Fact]
    public void Only_a_draft_can_be_edited()
    {
        var o = NewOrder();
        o.UpdateDraft(Guid.NewGuid(), null, "new", [Line(V1, 1, 5m)]);
        Assert.Equal(5m, o.Total);
        o.Send(Now, null, null);
        Assert.Throws<InvalidOperationException>(() => o.UpdateDraft(Guid.NewGuid(), null, null, [Line(V1, 1, 5m)]));
    }

    [Fact]
    public void Payment_status_follows_what_is_paid_against_what_is_owed()
    {
        var o = NewOrder(Line(V1, 10, 100m)); // owes 1000
        o.Send(Now, null, null);
        o.ApplyPayment(250m);
        Assert.Equal((SupplierPaymentStatus.PartlyPaid, 750m), (o.PaymentStatus, o.Outstanding));
        o.ApplyPayment(750m);
        Assert.Equal(SupplierPaymentStatus.Paid, o.PaymentStatus);

        // The supplier invoices less than ordered: what is owed is the invoice amount.
        var invoiced = NewOrder(Line(V1, 10, 100m));
        invoiced.Send(Now, null, null);
        invoiced.RecordInvoice("INV-1", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 15), 900m);
        Assert.Equal(900m, invoiced.AmountPayable);
        invoiced.ApplyPayment(900m);
        Assert.Equal(SupplierPaymentStatus.Paid, invoiced.PaymentStatus);
        Assert.False(invoiced.IsOverdue(new DateOnly(2026, 12, 1))); // paid is never overdue

        // Without an invoice a received order owes what arrived, so a short delivery owes less.
        var shortOrder = NewOrder(Line(V1, 10, 100m));
        shortOrder.Send(Now, null, null);
        shortOrder.Receive([Got(shortOrder, 0, 6)], Now, null, null);
        shortOrder.CloseShort("supplier ran out", Now, null, null);
        Assert.Equal(PurchaseStatus.Received, shortOrder.Status);
        Assert.Equal(600m, shortOrder.AmountPayable);
    }

    [Fact]
    public void An_unpaid_order_past_its_due_date_is_overdue_and_a_draft_cannot_be_invoiced()
    {
        var o = NewOrder(Line(V1, 1, 100m));
        Assert.Throws<InvalidOperationException>(() => o.RecordInvoice("INV", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 10), null));
        o.Send(Now, null, null);
        o.RecordInvoice("INV", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 10), null);
        Assert.False(o.IsOverdue(new DateOnly(2026, 11, 10)));
        Assert.True(o.IsOverdue(new DateOnly(2026, 11, 11)));
    }

    [Fact]
    public void A_cost_less_receipt_line_records_the_numbers_the_average_needs()
    {
        var o = NewOrder(Line(V1, 4, 25m));
        var receipt = new GoodsReceipt("GRN-1", o.Id, o.Number, o.BranchId, Now, null, "Clerk", " note ");
        var line = receipt.AddLine(o.Lines[0], 3, 1, "one damaged", productOnHandBefore: 12, productCostBefore: 20m, costApplied: false);
        Assert.Equal((3, 1, 25m, 12, 20m, false), (line.AcceptedQuantity, line.RejectedQuantity, line.UnitCost, line.ProductOnHandBefore, line.ProductCostBefore, line.CostApplied));
        line.MarkCostApplied();
        Assert.True(line.CostApplied);
        Assert.Equal("note", receipt.Note);
    }
}
