using Juvo.Application.Notifications;
using Juvo.Application.Support;
using Juvo.Domain.Notifications;

namespace Juvo.Application.Tests.Support;

public class CannedReplyTemplateTests
{
    private static readonly Dictionary<string, string?> Values = new() { ["customerName"] = "Nadia", ["orderNumber"] = "JV-HO-2611-000007", ["ticketNumber"] = "TK-2611-000003" };

    [Fact]
    public void The_known_placeholders_are_replaced_in_english_and_in_bengali()
    {
        var (en, enEmpty) = CannedReplyTemplate.Render("Hello {customerName}, about {orderNumber} ({ticketNumber}).", Values);
        var (bn, bnEmpty) = CannedReplyTemplate.Render("হ্যালো {customerName}, অর্ডার {orderNumber}, টিকিট {ticketNumber}।", Values);

        Assert.Equal("Hello Nadia, about JV-HO-2611-000007 (TK-2611-000003).", en);
        Assert.Equal("হ্যালো Nadia, অর্ডার JV-HO-2611-000007, টিকিট TK-2611-000003।", bn);
        Assert.Empty(enEmpty);
        Assert.Empty(bnEmpty);
    }

    [Fact]
    public void A_known_placeholder_without_a_value_becomes_empty_and_is_reported_once()
    {
        var (text, empty) = CannedReplyTemplate.Render("Order {orderNumber}. Again {orderNumber}.", new Dictionary<string, string?> { ["orderNumber"] = null });

        Assert.Equal("Order . Again .", text);
        Assert.Equal(["orderNumber"], empty);
    }

    [Fact]
    public void An_unknown_placeholder_stays_as_typed_and_is_listed()
    {
        var (text, _) = CannedReplyTemplate.Render("Hi {customerName} {typo}", Values);

        Assert.Equal("Hi Nadia {typo}", text);
        Assert.Equal(["typo"], CannedReplyTemplate.UnknownIn("Hi {customerName} {typo} {typo} {orderNumber}"));
    }

    [Fact]
    public void Placeholders_are_found_once_in_order_and_plain_braces_are_ignored()
    {
        Assert.Equal(["orderNumber", "customerName"], CannedReplyTemplate.PlaceholdersIn("{orderNumber} {customerName} {orderNumber} { } {1} {}"));
        Assert.Empty(CannedReplyTemplate.PlaceholdersIn(null));
        Assert.Empty(CannedReplyTemplate.PlaceholdersIn("no braces"));
    }

    [Fact]
    public void Placeholders_are_case_sensitive()
    {
        Assert.Equal(["CustomerName"], CannedReplyTemplate.UnknownIn("{CustomerName}"));
    }
}

public class TicketStatsTests
{
    [Fact]
    public void The_median_is_the_middle_value_or_the_mean_of_the_two_middle_values()
    {
        Assert.Null(TicketStats.Median([]));
        Assert.Equal(5d, TicketStats.Median([5d]));
        Assert.Equal(3d, TicketStats.Median([9d, 1d, 3d]));
        Assert.Equal(6d, TicketStats.Median([10d, 2d, 4d, 8d]));
    }

    [Fact]
    public void A_duration_has_a_count_an_average_and_a_median_rounded_to_two_decimals()
    {
        var d = TicketStats.Duration([10d, 20d, 40d]);
        Assert.Equal(3, d.Count);
        Assert.Equal(23.33, d.AverageMinutes);
        Assert.Equal(20d, d.MedianMinutes);

        var none = TicketStats.Duration([]);
        Assert.Equal(0, none.Count);
        Assert.Null(none.AverageMinutes);
        Assert.Null(none.MedianMinutes);
    }
}

public class TicketLimitsTests
{
    [Fact]
    public void A_good_text_is_trimmed_and_the_category_defaults_to_other()
    {
        var r = TicketLimits.Text("  Late parcel ", "  Please help  ", null);

        Assert.True(r.IsSuccess);
        Assert.Equal(("Late parcel", "Please help", "other"), r.Value);
    }

    [Theory]
    [InlineData("", "text", null)]
    [InlineData("subject", " ", null)]
    [InlineData("subject", "text", "complaint")]
    public void A_missing_subject_or_message_or_an_unknown_category_is_refused(string subject, string message, string? category) =>
        Assert.True(TicketLimits.Text(subject, message, category).IsFailure);

    [Fact]
    public void Over_long_texts_are_refused()
    {
        Assert.True(TicketLimits.Text(new string('a', TicketLimits.MaxSubject + 1), "text", null).IsFailure);
        Assert.True(TicketLimits.Text("subject", new string('a', TicketLimits.MaxText + 1), null).IsFailure);
        Assert.True(TicketLimits.Text(new string('a', TicketLimits.MaxSubject), new string('b', TicketLimits.MaxText), null).IsSuccess);
    }

    [Theory]
    [InlineData("a@b.co", true)]
    [InlineData("nadia.rahman@example.com", true)]
    [InlineData("no-at-sign", false)]
    [InlineData("a@b", false)]
    [InlineData("a b@c.de", false)]
    public void Email_addresses_are_checked_loosely(string email, bool ok) => Assert.Equal(ok, TicketLimits.LooksLikeEmail(email));
}

public class TicketTemplateTests
{
    [Theory]
    [InlineData("ticket.received")]
    [InlineData("ticket.replied")]
    [InlineData("ticket.resolved")]
    public void Each_ticket_message_exists_in_both_languages_for_sms_and_email_and_is_transactional(string code)
    {
        var definition = NotificationCatalog.Find(code);
        Assert.NotNull(definition);
        Assert.Equal(NotificationCategory.Transactional, definition.Category);
        foreach (var language in NotificationCatalog.Languages)
        {
            Assert.NotNull(NotificationCatalog.DefaultFor(code, NotificationChannel.Sms, language));
            Assert.NotNull(NotificationCatalog.DefaultFor(code, NotificationChannel.Email, language));
        }
    }

    [Fact]
    public void Only_the_email_of_a_reply_carries_the_text_of_the_reply()
    {
        Assert.DoesNotContain("{{Reply}}", NotificationCatalog.DefaultFor("ticket.replied", NotificationChannel.Sms, "en")!.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("{{Reply}}", NotificationCatalog.DefaultFor("ticket.replied", NotificationChannel.Sms, "bn")!.Body, StringComparison.Ordinal);
        Assert.Contains("{{Reply}}", NotificationCatalog.DefaultFor("ticket.replied", NotificationChannel.Email, "en")!.Body, StringComparison.Ordinal);
        Assert.Contains("{{Reply}}", NotificationCatalog.DefaultFor("ticket.replied", NotificationChannel.Email, "bn")!.Body, StringComparison.Ordinal);
    }
}
