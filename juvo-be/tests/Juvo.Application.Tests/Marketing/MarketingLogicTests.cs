using Juvo.Application.Marketing;
using Juvo.Domain.Orders;

namespace Juvo.Application.Tests.Marketing;

public class QuietHoursTests
{
    private static DateTimeOffset Dhaka(int hour, int minute = 0) => new(2026, 11, 20, hour, minute, 0, TimeSpan.FromHours(6));

    private static readonly TimeOnly Start = new(21, 0);
    private static readonly TimeOnly End = new(9, 0);

    [Theory]
    [InlineData(21, 0, true)]
    [InlineData(23, 59, true)]
    [InlineData(0, 0, true)]
    [InlineData(8, 59, true)]
    [InlineData(9, 0, false)]
    [InlineData(12, 0, false)]
    [InlineData(20, 59, false)]
    public void The_window_that_crosses_midnight_holds_from_21_to_09_in_dhaka_time(int hour, int minute, bool quiet) =>
        Assert.Equal(quiet, MarketingLogic.IsQuiet(Dhaka(hour, minute), true, Start, End));

    [Fact]
    public void The_clock_is_read_in_dhaka_whatever_offset_the_instant_has()
    {
        var utc = new DateTimeOffset(2026, 11, 20, 16, 0, 0, TimeSpan.Zero); // 22:00 in Dhaka

        Assert.True(MarketingLogic.IsQuiet(utc, true, Start, End));
        Assert.False(MarketingLogic.IsQuiet(new DateTimeOffset(2026, 11, 20, 4, 0, 0, TimeSpan.Zero), true, Start, End)); // 10:00 in Dhaka
    }

    [Fact]
    public void A_window_inside_one_day_and_a_switched_off_window_work()
    {
        Assert.True(MarketingLogic.IsQuiet(Dhaka(13), true, new TimeOnly(12, 0), new TimeOnly(14, 0)));
        Assert.False(MarketingLogic.IsQuiet(Dhaka(14), true, new TimeOnly(12, 0), new TimeOnly(14, 0)));
        Assert.False(MarketingLogic.IsQuiet(Dhaka(23), false, Start, End));
        Assert.False(MarketingLogic.IsQuiet(Dhaka(23), true, Start, Start)); // start equal to end means no window
    }
}

public class FrequencyCapTests
{
    [Theory]
    [InlineData(0, 3, true)]
    [InlineData(2, 3, true)]
    [InlineData(3, 3, false)]
    [InlineData(9, 3, false)]
    [InlineData(50, 0, true)]
    public void A_customer_gets_another_message_only_below_the_cap_and_zero_means_no_cap(int sent, int max, bool allowed) =>
        Assert.Equal(allowed, MarketingLogic.CapAllows(sent, max));
}

public class AttributionTests
{
    private static readonly DateTimeOffset Sent = new(2026, 11, 10, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void An_order_counts_from_the_send_until_the_end_of_the_window()
    {
        Assert.True(MarketingLogic.InAttributionWindow(Sent, Sent, 7));
        Assert.True(MarketingLogic.InAttributionWindow(Sent, Sent.AddDays(7), 7));
        Assert.False(MarketingLogic.InAttributionWindow(Sent, Sent.AddDays(7).AddSeconds(1), 7));
        Assert.False(MarketingLogic.InAttributionWindow(Sent, Sent.AddSeconds(-1), 7)); // placed before the message
    }

    [Fact]
    public void The_newest_touch_before_the_order_wins()
    {
        var order = Sent.AddDays(3);

        Assert.True(MarketingLogic.IsLastTouch(Sent, order, []));
        Assert.False(MarketingLogic.IsLastTouch(Sent, order, [Sent.AddDays(1)])); // a later message reached the customer first
        Assert.True(MarketingLogic.IsLastTouch(Sent, order, [Sent.AddDays(5)])); // a message after the order does not matter
        Assert.True(MarketingLogic.IsLastTouch(Sent, order, [Sent.AddDays(-1)])); // an earlier one does not either
    }
}

public class MarketingTemplateTests
{
    private static readonly Dictionary<string, string> Values = new() { ["customerName"] = "Nadia", ["shopName"] = "Juvo BD", ["link"] = "https://juvobd.com" };

    [Fact]
    public void The_placeholders_are_replaced_in_english_and_in_bengali()
    {
        Assert.Equal("Hi Nadia, new at Juvo BD: https://juvobd.com", MarketingTemplate.Render("Hi {customerName}, new at {shopName}: {link}", Values));
        Assert.Equal("হ্যালো Nadia, Juvo BD-তে নতুন: https://juvobd.com", MarketingTemplate.Render("হ্যালো {customerName}, {shopName}-তে নতুন: {link}", Values));
    }

    [Fact]
    public void A_placeholder_without_a_value_stays_as_written_and_unknown_names_are_found()
    {
        Assert.Equal("Link: {unsubscribeLink}", MarketingTemplate.Render("Link: {unsubscribeLink}", Values));
        Assert.Equal(["customerName", "oops"], MarketingTemplate.UnknownIn("{customerName} {oops} {customerName}", ["shopName", "link"]));
        Assert.Equal(["oops"], MarketingTemplate.UnknownIn("{customerName} {oops}", MarketingTemplate.CampaignPlaceholders));
        Assert.Empty(MarketingTemplate.UnknownIn("{customerName}, {shopName}, {link}, {unsubscribeLink}", MarketingTemplate.CampaignPlaceholders));
    }

    [Fact]
    public void Text_with_other_curly_braces_is_left_alone()
    {
        Assert.Equal("Price { 100 } and {1}", MarketingTemplate.Render("Price { 100 } and {1}", Values));
    }
}

public class SegmentValidatorTests
{
    private static SegmentDefinition One(SegmentRule rule, string combinator = "and") => new() { Combinator = combinator, Rules = [rule] };

    [Fact]
    public void Every_rule_type_with_its_fields_is_accepted()
    {
        SegmentRule[] rules =
        [
            new() { Type = "newCustomers", Days = 30 },
            new() { Type = "repeatBuyers", MinOrders = 2, PeriodDays = 90 },
            new() { Type = "highSpenders", MinSpend = 5000m },
            new() { Type = "inactive", Days = 60 },
            new() { Type = "location", Level = "district", Ids = [Guid.NewGuid()] },
            new() { Type = "consent", Channel = "sms", Granted = true },
            new() { Type = "lastOrderStatus", Status = OrderStatus.Delivered },
            new() { Type = "wishlist" },
            new() { Type = "cart", IdleHours = 24 },
        ];

        foreach (var rule in rules)
        {
            Assert.Null(SegmentValidator.Validate(One(rule)));
        }
    }

    [Fact]
    public void Missing_or_out_of_range_values_name_the_rule_and_field()
    {
        var errors = SegmentValidator.Validate(new SegmentDefinition
        {
            Combinator = "xor",
            Rules = [new SegmentRule { Type = "newCustomers" }, new SegmentRule { Type = "highSpenders", MinSpend = -1m }, new SegmentRule { Type = "location", Level = "city", Ids = [] }, new SegmentRule { Type = "dance" }],
        })!;

        Assert.Contains("combinator", errors.Keys);
        Assert.Contains("rules[0].days", errors.Keys);
        Assert.Contains("rules[1].minSpend", errors.Keys);
        Assert.Contains("rules[2].level", errors.Keys);
        Assert.Contains("rules[2].ids", errors.Keys);
        Assert.Contains("rules[3].type", errors.Keys);
    }

    [Fact]
    public void A_field_that_the_rule_type_does_not_use_is_refused()
    {
        var errors = SegmentValidator.Validate(One(new SegmentRule { Type = "newCustomers", Days = 7, MinSpend = 100m }))!;

        Assert.Contains("rules[0].minSpend", errors.Keys);
    }

    [Fact]
    public void An_empty_or_huge_rule_list_and_a_missing_definition_are_refused()
    {
        Assert.Contains("rules", SegmentValidator.Validate(new SegmentDefinition { Rules = [] })!.Keys);
        Assert.Contains("rules", SegmentValidator.Validate(new SegmentDefinition { Rules = [.. Enumerable.Range(0, 21).Select(_ => new SegmentRule { Type = "wishlist" })] })!.Keys);
        Assert.Contains("definition", SegmentValidator.Validate(null)!.Keys);
    }

    [Fact]
    public void Repeat_buyers_need_at_least_two_orders_and_days_are_capped()
    {
        Assert.Contains("rules[0].minOrders", SegmentValidator.Validate(One(new SegmentRule { Type = "repeatBuyers", MinOrders = 1 }))!.Keys);
        Assert.Contains("rules[0].days", SegmentValidator.Validate(One(new SegmentRule { Type = "inactive", Days = 4000 }))!.Keys);
    }
}
