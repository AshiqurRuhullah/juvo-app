using Juvo.Domain.Orders;
using Juvo.Domain.Payments;

namespace Juvo.Domain.Tests.Payments;

public class PaymentTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    private static Payment NewPayment(PaymentKind kind = PaymentKind.Online, decimal amount = 1000m) =>
        Payment.Start(new PaymentInit(Guid.NewGuid(), "JV-HO-2611-000001", Guid.NewGuid(), kind, "SANDBOX", "SANDBOX", amount, "BDT", "sbx_1", "/pay", null, null, null, null, null), Now, null, "Customer");

    /// <summary>The route from Pending to a status, using only moves the state machine allows.</summary>
    private static readonly Dictionary<PaymentStatus, PaymentStatus[]> Routes = new()
    {
        [PaymentStatus.Pending] = [],
        [PaymentStatus.Paid] = [PaymentStatus.Paid],
        [PaymentStatus.Failed] = [PaymentStatus.Failed],
        [PaymentStatus.Cancelled] = [PaymentStatus.Cancelled],
        [PaymentStatus.UnderReview] = [PaymentStatus.UnderReview],
        [PaymentStatus.Rejected] = [PaymentStatus.UnderReview, PaymentStatus.Rejected],
        [PaymentStatus.PartiallyRefunded] = [PaymentStatus.Paid, PaymentStatus.PartiallyRefunded],
        [PaymentStatus.Refunded] = [PaymentStatus.Paid, PaymentStatus.Refunded],
        [PaymentStatus.CollectedByCourier] = [PaymentStatus.CollectedByCourier],
        [PaymentStatus.Settled] = [PaymentStatus.CollectedByCourier, PaymentStatus.Settled],
    };

    /// <summary>CLAUDE.md section 7, written out independently of the state machine: Pending to Paid, Failed, Cancelled, UnderReview, CollectedByCourier; UnderReview to Paid or Rejected; ...</summary>
    private static readonly Dictionary<PaymentStatus, PaymentStatus[]> Allowed = new()
    {
        [PaymentStatus.Pending] = [PaymentStatus.Paid, PaymentStatus.Failed, PaymentStatus.Cancelled, PaymentStatus.UnderReview, PaymentStatus.CollectedByCourier],
        [PaymentStatus.UnderReview] = [PaymentStatus.Paid, PaymentStatus.Rejected],
        [PaymentStatus.Paid] = [PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded],
        [PaymentStatus.PartiallyRefunded] = [PaymentStatus.Refunded],
        [PaymentStatus.CollectedByCourier] = [PaymentStatus.Settled, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded],
        [PaymentStatus.Failed] = [PaymentStatus.Pending, PaymentStatus.Cancelled],
        [PaymentStatus.Settled] = [PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded],
        [PaymentStatus.Cancelled] = [],
        [PaymentStatus.Rejected] = [],
        [PaymentStatus.Refunded] = [],
    };

    private static Payment In(PaymentStatus status)
    {
        var p = NewPayment();
        foreach (var step in Routes[status])
        {
            p.MoveTo(step, "Test", null, Now, null, "Test");
        }

        Assert.Equal(status, p.Status);
        return p;
    }

    public static TheoryData<PaymentStatus, PaymentStatus> EveryPair()
    {
        var data = new TheoryData<PaymentStatus, PaymentStatus>();
        foreach (var from in Enum.GetValues<PaymentStatus>())
        {
            foreach (var to in Enum.GetValues<PaymentStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(EveryPair))]
    public void Every_pair_of_payment_statuses_follows_the_specification(PaymentStatus from, PaymentStatus to)
    {
        var payment = In(from);
        var events = payment.Events.Count;

        if (Allowed[from].Contains(to))
        {
            payment.MoveTo(to, "Test", "reason", Now, null, "Test");
            Assert.Equal(to, payment.Status);
            Assert.Equal(events + 1, payment.Events.Count);
            Assert.Equal(from, payment.Events[^1].FromStatus);
            Assert.Equal(to, payment.Events[^1].ToStatus);
        }
        else
        {
            Assert.Throws<InvalidOperationException>(() => payment.MoveTo(to, "Test", null, Now, null, "Test"));
            Assert.Equal(from, payment.Status); // a refused move changes nothing
            Assert.Equal(events, payment.Events.Count);
        }
    }

    [Fact]
    public void The_payment_state_machine_and_the_order_payment_track_use_the_same_table()
    {
        foreach (var from in Enum.GetValues<PaymentStatus>())
        {
            Assert.Equal(Allowed[from].Order(), OrderStateMachine.NextFor(from).Order());
        }
    }

    [Fact]
    public void A_new_payment_is_pending_with_a_creation_event()
    {
        var p = NewPayment();
        Assert.Equal(PaymentStatus.Pending, p.Status);
        Assert.Single(p.Events);
        Assert.Equal("Created", p.Events[0].Type);
        Assert.Throws<ArgumentOutOfRangeException>(() => NewPayment(amount: 0m));
    }

    [Fact]
    public void Confirming_a_payment_records_the_gateway_transaction_and_the_verification_time()
    {
        var p = NewPayment();
        p.ConfirmPaid("TXN-1", Now.AddMinutes(1), "SANDBOX:evt_1", "Confirmed", null, "Gateway");

        Assert.Equal(PaymentStatus.Paid, p.Status);
        Assert.Equal("TXN-1", p.ProviderTransactionId);
        Assert.Equal(Now.AddMinutes(1), p.VerifiedAt);
        Assert.Equal("SANDBOX:evt_1", p.Events[^1].ProviderEventId);
    }

    [Fact]
    public void A_manual_transfer_goes_under_review_with_its_reference()
    {
        var p = NewPayment(PaymentKind.Manual);
        p.SubmitForReview("  TX12345  ", "Nadia", Now, null, "Customer");

        Assert.Equal(PaymentStatus.UnderReview, p.Status);
        Assert.Equal("TX12345", p.SubmittedReference);
        Assert.Equal("Nadia", p.PayerName);
    }

    // ---- refunds -----------------------------------------------------------------------------------------

    [Fact]
    public void A_refund_is_reserved_first_and_counts_once_completed()
    {
        var p = In(PaymentStatus.Paid);
        p.ReserveRefund(300m, Now, null, "Staff", "Damaged");

        Assert.Equal(700m, p.Refundable);
        Assert.Equal(0m, p.RefundedAmount);
        Assert.Equal(300m, p.RefundPendingAmount);
        Assert.Equal(PaymentStatus.Paid, p.Status); // nothing was given back yet

        p.CompleteRefund(300m, Now, null, "Staff", "Refunded");
        Assert.Equal(300m, p.RefundedAmount);
        Assert.Equal(0m, p.RefundPendingAmount);
        Assert.Equal(PaymentStatus.PartiallyRefunded, p.Status);
        Assert.Equal(700m, p.Refundable);
    }

    [Fact]
    public void Refunding_everything_makes_the_payment_Refunded_and_nothing_more_can_be_refunded()
    {
        var p = In(PaymentStatus.Paid);
        p.ReserveRefund(400m, Now, null, "Staff", "First");
        p.CompleteRefund(400m, Now, null, "Staff", "First");
        p.ReserveRefund(600m, Now, null, "Staff", "Second");
        p.CompleteRefund(600m, Now, null, "Staff", "Second");

        Assert.Equal(PaymentStatus.Refunded, p.Status);
        Assert.Equal(0m, p.Refundable);
        Assert.Throws<InvalidOperationException>(() => p.ReserveRefund(1m, Now, null, "Staff", "Too much"));
    }

    [Fact]
    public void A_refund_cannot_be_more_than_what_is_left_even_while_another_is_pending()
    {
        var p = In(PaymentStatus.Paid);
        p.ReserveRefund(700m, Now, null, "Staff", "Pending one");

        Assert.Throws<InvalidOperationException>(() => p.ReserveRefund(400m, Now, null, "Staff", "Together more than paid"));
        p.ReserveRefund(300m, Now, null, "Staff", "Exactly the rest");
        Assert.Equal(0m, p.Refundable);
    }

    [Fact]
    public void A_failed_refund_gives_the_money_back_to_the_refundable_amount_and_keeps_the_status()
    {
        var p = In(PaymentStatus.Paid);
        p.ReserveRefund(500m, Now, null, "Staff", "Try");
        p.FailRefund(500m, Now, null, "Staff", "Gateway said no");

        Assert.Equal(1000m, p.Refundable);
        Assert.Equal(PaymentStatus.Paid, p.Status);
        Assert.Contains(p.Events, e => e.Type == "RefundFailed");
    }

    [Theory]
    [InlineData(PaymentStatus.Pending)]
    [InlineData(PaymentStatus.UnderReview)]
    [InlineData(PaymentStatus.Failed)]
    [InlineData(PaymentStatus.Cancelled)]
    [InlineData(PaymentStatus.Rejected)]
    public void A_payment_that_holds_no_money_cannot_be_refunded(PaymentStatus status)
    {
        var p = In(status);
        Assert.False(p.HoldsMoney);
        Assert.Throws<InvalidOperationException>(() => p.ReserveRefund(10m, Now, null, "Staff", "No money"));
    }

    [Theory]
    [InlineData(PaymentStatus.CollectedByCourier)]
    [InlineData(PaymentStatus.Settled)]
    public void Cash_collected_by_the_courier_can_be_refunded(PaymentStatus status)
    {
        var p = In(status);
        p.ReserveRefund(1000m, Now, null, "Staff", "Return");
        p.CompleteRefund(1000m, Now, null, "Staff", "Return");
        Assert.Equal(PaymentStatus.Refunded, p.Status);
    }

    [Fact]
    public void A_refund_record_finishes_once_and_never_changes_again()
    {
        var p = In(PaymentStatus.Paid);
        var refund = new Refund(p, 100m, RefundMode.Manual, " Damaged ", null, null, "Staff", Now);
        Assert.Equal(RefundState.Pending, refund.Status);
        Assert.Equal("Damaged", refund.Reason);

        refund.Complete("  BANK-77 ", Now);
        Assert.Equal(RefundState.Completed, refund.Status);
        Assert.Equal("BANK-77", refund.Reference);
        Assert.Throws<InvalidOperationException>(() => refund.Complete("again", Now));
        Assert.Throws<InvalidOperationException>(() => refund.Fail("late", Now));

        var failed = new Refund(p, 50m, RefundMode.Gateway, null, null, null, "Staff", Now);
        failed.Fail("Refused", Now);
        Assert.Equal(RefundState.Failed, failed.Status);
        Assert.Throws<InvalidOperationException>(() => failed.Complete("x", Now));
    }

    [Fact]
    public void Events_and_sandbox_sessions_are_append_only_or_one_shot()
    {
        Assert.True(typeof(Juvo.Domain.Common.IAppendOnly).IsAssignableFrom(typeof(PaymentEvent)));

        var session = new SandboxGatewaySession("sbx_1", Guid.NewGuid(), "JV-1", 100m, "BDT", Now.AddMinutes(30));
        Assert.True(session.Decide(1, Now));
        Assert.False(session.Decide(2, Now)); // the first decision stands
        Assert.Equal(1, session.Outcome);
    }
}
