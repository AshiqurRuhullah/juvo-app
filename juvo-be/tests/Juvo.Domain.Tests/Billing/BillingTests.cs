using Juvo.Domain.Billing;
using Juvo.Domain.Common;
using Juvo.Domain.Notifications;

namespace Juvo.Domain.Tests.Billing;

public class BillingTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);

    // ---- tax maths ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(115, 15, 15)]
    [InlineData(1000, 15, 130.43)] // 1000 * 15 / 115 = 130.4347...
    [InlineData(100, 0, 0)]
    [InlineData(0, 15, 0)]
    [InlineData(50, 5, 2.38)] // 50 * 5 / 105 = 2.3809...
    public void The_tax_inside_a_gross_amount_is_rate_over_one_hundred_plus_rate(double gross, double rate, double expected) =>
        Assert.Equal((decimal)expected, TaxMath.TaxIn((decimal)gross, (decimal)rate));

    [Fact]
    public void An_order_with_no_active_rule_reports_no_tax_and_the_total_is_untouched()
    {
        var result = TaxMath.ForOrder([500m, 300m], 100m, 60m, 0m, true);

        Assert.Equal(0m, result.TaxTotal);
        Assert.Equal(760m, result.NetTotal); // items 800 - discount 100 + delivery 60: nothing taken out
        Assert.Equal(new[] { 0m, 0m }, result.LineTax);
    }

    [Fact]
    public void The_tax_is_taken_out_of_each_line_after_its_share_of_the_discount()
    {
        // Lines 600 and 400, discount 100 -> shares 60 and 40 -> 540 and 360. Tax at 15% inclusive: 70.43 and 46.96. Delivery 115 -> 15.
        var result = TaxMath.ForOrder([600m, 400m], 100m, 115m, 15m, true);

        Assert.Equal(new[] { 70.43m, 46.96m }, result.LineTax);
        Assert.Equal(15m, result.DeliveryTax);
        Assert.Equal(132.39m, result.TaxTotal);
        Assert.Equal(900m + 115m - 132.39m, result.NetTotal);
    }

    [Fact]
    public void Delivery_is_left_out_when_the_rule_does_not_apply_to_it()
    {
        var withDelivery = TaxMath.ForOrder([1150m], 0m, 115m, 15m, true);
        var without = TaxMath.ForOrder([1150m], 0m, 115m, 15m, false);

        Assert.Equal(15m, withDelivery.DeliveryTax);
        Assert.Equal(0m, without.DeliveryTax);
        Assert.Equal(150m, without.TaxTotal);
    }

    [Fact]
    public void The_discount_shares_add_up_exactly_and_never_make_a_line_negative()
    {
        // Three equal lines and a discount that does not divide by three: the last line takes the rounding rest.
        var result = TaxMath.ForOrder([100m, 100m, 100m], 100m, 0m, 15m, true);

        Assert.Equal(3, result.LineTax.Count);
        Assert.All(result.LineTax, t => Assert.True(t >= 0));
        Assert.Equal(result.LineTax.Sum(), result.TaxTotal);
        // The three discounted lines together are worth exactly 200: their tax is about 200 * 15 / 115 = 26.09 (rounding per line may differ by a cent).
        Assert.InRange(result.TaxTotal, 26.05m, 26.13m);
    }

    [Fact]
    public void A_discount_larger_than_a_line_does_not_make_negative_tax()
    {
        var result = TaxMath.ForOrder([100m, 900m], 500m, 0m, 15m, true);
        Assert.All(result.LineTax, t => Assert.True(t >= 0));
    }

    // ---- the sample rule ---------------------------------------------------------------------------------

    [Fact]
    public void A_new_tax_rule_is_inactive_and_inclusive()
    {
        var rule = new TaxRule("vat15", " Value added tax ", 15m, true, false, " sample ");

        Assert.Equal("VAT15", rule.Code);
        Assert.Equal("Value added tax", rule.Name);
        Assert.False(rule.IsActive);
        Assert.False(rule.IsVerified);
        Assert.True(rule.IsInclusive);
        Assert.Equal("sample", rule.Note);
    }

    // ---- invoices and credit notes -----------------------------------------------------------------------

    private static NewInvoice Data() =>
        new("INV-HO-2611-000001", Guid.NewGuid(), Guid.NewGuid(), "JV-HO-2611-000001", Guid.NewGuid(), "Nadia", "+8801712345678", null, "House 1, Dhaka", new InvoiceSeller("Juvo BD", "Dhaka", "01700000000", null, null),
            "BDT", 800m, 100m, 60m, 760m, "VAT15", "VAT", 15m, 7.83m, 99.13m,
            [new NewInvoiceLine("T-shirt", "টি-শার্ট", "JV-TSH-M", "Black / M", 2, 400m, 800m, 91.3m)], null, "System");

    [Fact]
    public void An_invoice_is_a_snapshot_with_its_lines_and_is_append_only()
    {
        var invoice = Invoice.Issue(Data(), Now);

        Assert.Equal("INV-HO-2611-000001", invoice.Number);
        Assert.Equal(760m, invoice.GrandTotal);
        Assert.Equal(99.13m, invoice.TaxTotal);
        Assert.Single(invoice.Lines);
        Assert.Equal("টি-শার্ট", invoice.Lines[0].NameBn);
        Assert.IsAssignableFrom<IAppendOnly>(invoice);
        Assert.IsAssignableFrom<IAppendOnly>(invoice.Lines[0]);
    }

    [Fact]
    public void A_credit_note_copies_the_invoice_reference_and_is_append_only()
    {
        var invoice = Invoice.Issue(Data(), Now);
        var refund = Guid.NewGuid();
        var note = new CreditNote("CN-HO-2611-000001", invoice, refund, 200m, 26.09m, " Damaged ", null, "Staff", Now);

        Assert.Equal(invoice.Id, note.InvoiceId);
        Assert.Equal(invoice.Number, note.InvoiceNumber);
        Assert.Equal(refund, note.RefundId);
        Assert.Equal("Damaged", note.Reason);
        Assert.IsAssignableFrom<IAppendOnly>(note);
    }

    // ---- notification log --------------------------------------------------------------------------------

    private static NotificationLog Queued() =>
        NotificationLog.Queue(NotificationChannel.Sms, "+8801712345678", "order.confirmed", null, "Hello", "en", 0, NotificationCategory.Transactional, null, "key", null, Now);

    [Fact]
    public void A_queued_message_is_pending_and_fails_then_dead_letters_on_the_last_attempt()
    {
        var log = Queued();
        Assert.Equal(NotificationStatus.Pending, log.Status);

        log.MarkAttemptFailed("provider down", false, Now);
        Assert.Equal(NotificationStatus.Failed, log.Status);
        Assert.Equal(1, log.Attempts);

        log.MarkAttemptFailed("provider down", true, Now);
        Assert.Equal(NotificationStatus.DeadLetter, log.Status);
        Assert.Equal(2, log.Attempts);
        Assert.Equal("provider down", log.LastError);
    }

    [Fact]
    public void A_dead_letter_can_be_requeued_and_a_success_clears_the_error()
    {
        var log = Queued();
        log.MarkAttemptFailed("boom", true, Now);
        log.Requeue(Now.AddHours(1));

        Assert.Equal(NotificationStatus.Pending, log.Status);
        Assert.Equal(0, log.Attempts);
        Assert.Null(log.LastError);

        log.MarkSent("sandbox", Now.AddHours(2));
        Assert.Equal(NotificationStatus.Sent, log.Status);
        Assert.Equal(1, log.Attempts);
    }

    [Fact]
    public void A_skipped_message_keeps_its_reason()
    {
        var log = NotificationLog.Skip(NotificationChannel.Sms, "+8801712345678", "marketing.promotion", "en", NotificationCategory.Marketing, null, "No consent", "k", Now);
        Assert.Equal(NotificationStatus.Skipped, log.Status);
        Assert.Equal("No consent", log.LastError);
    }

    [Fact]
    public void Editing_a_template_creates_a_new_version_and_returns_the_old_text()
    {
        var template = new NotificationTemplate("order.confirmed", NotificationChannel.Sms, "en", null, "Old text", NotificationCategory.Transactional, true, null, "A", Now);
        var previous = template.Edit(null, "New text", true, null, "B", Now.AddMinutes(1));

        Assert.Equal(2, template.Version);
        Assert.Equal("New text", template.Body);
        Assert.Equal(1, previous.Version);
        Assert.Equal("Old text", previous.Body);
        Assert.IsAssignableFrom<IAppendOnly>(previous);
    }
}
