using Juvo.Application.Privacy;
using Juvo.Domain.Customers;

namespace Juvo.Application.Tests.Privacy;

public class PrivacyRulesTests
{
    private static readonly Guid Customer = Guid.CreateVersion7();
    private static readonly DateTimeOffset T0 = new(2026, 11, 1, 8, 0, 0, TimeSpan.Zero);

    private static ConsentLog Row(ConsentType type, bool granted, string source, int minutes, string? ip = "203.0.113.77") =>
        new(Customer, type, granted, source, ip, T0.AddMinutes(minutes));

    [Fact]
    public void The_history_shows_the_old_value_of_each_type_and_lists_newest_first()
    {
        var rows = new[]
        {
            Row(ConsentType.MarketingSms, true, "customer-profile", 0),
            Row(ConsentType.MarketingEmail, true, "customer-profile", 1),
            Row(ConsentType.MarketingSms, false, "unsubscribe-link", 2),
            Row(ConsentType.MarketingSms, true, "customer-profile", 3),
        };

        var history = PrivacyRules.BuildConsentHistory(rows, maskIp: false);

        Assert.Equal(4, history.Count);
        Assert.Equal(T0.AddMinutes(3), history[0].At);
        Assert.Equal(false, history[0].OldValue);
        Assert.True(history[0].NewValue);
        Assert.Equal(true, history[1].OldValue);
        Assert.False(history[1].NewValue);
        Assert.Equal("UnsubscribeLink", history[1].SourceKind);
        var first = history.Single(h => h.Type == "MarketingSms" && h.At == T0);
        Assert.Null(first.OldValue);
        var email = history.Single(h => h.Type == "MarketingEmail");
        Assert.Null(email.OldValue);
        Assert.Equal("Email", email.Channel);
    }

    [Fact]
    public void The_sms_consent_also_covers_whatsapp_and_the_policy_has_no_channel()
    {
        Assert.Equal(["Sms", "WhatsApp"], PrivacyRules.AppliesTo(ConsentType.MarketingSms));
        Assert.Equal(["Email"], PrivacyRules.AppliesTo(ConsentType.MarketingEmail));
        Assert.Empty(PrivacyRules.AppliesTo(ConsentType.PrivacyPolicy));
        Assert.Equal("Policy", PrivacyRules.ChannelOf(ConsentType.PrivacyPolicy));
    }

    [Theory]
    [InlineData("customer-profile", "Customer")]
    [InlineData("unsubscribe-link", "UnsubscribeLink")]
    [InlineData("unsubscribe:0123456789abcdef0123456789abcdef", "UnsubscribeLink")]
    [InlineData("staff:edit", "Staff")]
    [InlineData("import-2026", "Import")]
    [InlineData("something else", "Other")]
    [InlineData("", "Other")]
    [InlineData(null, "Other")]
    public void The_source_text_is_sorted_into_a_kind(string? source, string kind) => Assert.Equal(kind, PrivacyRules.SourceKindOf(source));

    [Fact]
    public void Addresses_are_partly_hidden_when_masked_and_untouched_otherwise()
    {
        var row = new[] { Row(ConsentType.MarketingSms, true, "customer-profile", 0) };

        Assert.Equal("203.0.*.*", PrivacyRules.BuildConsentHistory(row, maskIp: true)[0].IpAddress);
        Assert.Equal("203.0.113.77", PrivacyRules.BuildConsentHistory(row, maskIp: false)[0].IpAddress);
        Assert.Equal("2001:db8:*", PrivacyRules.MaskIp("2001:db8:85a3::8a2e:370:7334"));
        Assert.Null(PrivacyRules.MaskIp(null));
    }

    [Fact]
    public void A_mobile_number_is_matched_in_every_written_form()
    {
        Assert.Equal(["+8801712345678", "8801712345678", "01712345678"], PrivacyRules.MobileVariants("+8801712345678"));
        Assert.Equal(["+14155550100"], PrivacyRules.MobileVariants("+14155550100"));
        Assert.Empty(PrivacyRules.MobileVariants(""));
        Assert.Empty(PrivacyRules.MobileVariants(PrivacyPlaceholders.MobileFor(Customer)));
    }

    [Theory]
    [InlineData("Customer asked by phone", "Customer asked by phone")]
    [InlineData("  Customer asked  ", "Customer asked")]
    [InlineData("none", null)]
    [InlineData("abcd", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_reason_must_be_five_to_three_hundred_characters(string? input, string? expected) => Assert.Equal(expected, PrivacyRules.CleanReason(input));

    [Fact]
    public void The_reason_is_refused_above_three_hundred_characters() => Assert.Null(PrivacyRules.CleanReason(new string('x', 301)));

    [Fact]
    public void The_confirm_token_is_the_customer_id_in_any_case()
    {
        Assert.True(PrivacyRules.ConfirmMatches(Customer, Customer.ToString()));
        Assert.True(PrivacyRules.ConfirmMatches(Customer, " " + Customer.ToString().ToUpperInvariant() + " "));
        Assert.True(PrivacyRules.ConfirmMatches(Customer, Customer.ToString("N")));
        Assert.False(PrivacyRules.ConfirmMatches(Customer, Guid.CreateVersion7().ToString()));
        Assert.False(PrivacyRules.ConfirmMatches(Customer, "yes"));
        Assert.False(PrivacyRules.ConfirmMatches(Customer, null));
    }
}
