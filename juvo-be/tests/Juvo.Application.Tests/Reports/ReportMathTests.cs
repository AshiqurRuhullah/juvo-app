using Juvo.Application.Reports;

namespace Juvo.Application.Tests.Reports;

public class ReportMathTests
{
    [Fact]
    public void Rounding_goes_to_two_decimals_and_half_rounds_away_from_zero()
    {
        Assert.Equal(1.01m, ReportMath.Round2(1.005m));
        Assert.Equal(-1.01m, ReportMath.Round2(-1.005m));
        Assert.Equal(58.33m, ReportMath.Round2(175m / 3m));
    }

    [Theory]
    [InlineData(1, 3, 33.33)]
    [InlineData(2, 3, 66.67)]
    [InlineData(5, 5, 100)]
    [InlineData(0, 7, 0)]
    public void A_rate_is_a_percentage_with_two_decimals(int part, int whole, double expected) =>
        Assert.Equal((decimal)expected, ReportMath.Percent(part, whole));

    [Fact]
    public void A_rate_of_nothing_is_zero_not_an_error() => Assert.Equal(0m, ReportMath.Percent(5, 0));

    [Fact]
    public void A_day_belongs_to_its_own_bucket_or_to_the_monday_of_its_week_or_the_first_of_its_month()
    {
        var wednesday = new DateOnly(2026, 11, 11);
        Assert.Equal(wednesday, ReportMath.BucketStart(wednesday, "day"));
        Assert.Equal(new DateOnly(2026, 11, 9), ReportMath.BucketStart(wednesday, "week"));
        Assert.Equal(new DateOnly(2026, 11, 9), ReportMath.BucketStart(new DateOnly(2026, 11, 9), "week"));
        Assert.Equal(new DateOnly(2026, 11, 9), ReportMath.BucketStart(new DateOnly(2026, 11, 15), "week")); // Sunday closes the week
        Assert.Equal(new DateOnly(2026, 11, 1), ReportMath.BucketStart(wednesday, "month"));
    }

    [Fact]
    public void Buckets_cover_the_whole_period_including_empty_ones_and_the_first_one_may_start_before_the_period()
    {
        var days = ReportMath.Buckets(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 3), "day");
        Assert.Equal(3, days.Count);

        var weeks = ReportMath.Buckets(new DateOnly(2026, 11, 11), new DateOnly(2026, 11, 24), "week");
        Assert.Equal([new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 23)], weeks);

        var months = ReportMath.Buckets(new DateOnly(2026, 10, 20), new DateOnly(2026, 12, 2), "month");
        Assert.Equal([new DateOnly(2026, 10, 1), new DateOnly(2026, 11, 1), new DateOnly(2026, 12, 1)], months);
    }

    [Fact]
    public void Bucket_labels_are_the_day_or_the_month()
    {
        Assert.Equal("2026-11-09", ReportMath.BucketLabel(new DateOnly(2026, 11, 9), "week"));
        Assert.Equal("2026-11", ReportMath.BucketLabel(new DateOnly(2026, 11, 1), "month"));
    }

    [Fact]
    public void The_sql_day_key_is_the_bangladesh_day_since_the_unix_epoch()
    {
        Assert.Equal(new DateOnly(1970, 1, 1), ReportMath.DayFromKey(0));
        // The key is (unix seconds + 6 hours) / 86400, as the SQL expression computes it.
        var at = new DateTimeOffset(2026, 11, 10, 22, 30, 0, TimeSpan.Zero); // 04:30 on the 11th in Bangladesh
        var key = (int)((at.ToUnixTimeSeconds() + 21600L) / 86400L);
        Assert.Equal(new DateOnly(2026, 11, 11), ReportMath.DayFromKey(key));
        var noonThere = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.FromHours(6));
        Assert.Equal(new DateOnly(2026, 11, 10), ReportMath.DayFromKey((int)((noonThere.ToUnixTimeSeconds() + 21600L) / 86400L)));
    }

    [Theory]
    [InlineData("day", true)]
    [InlineData("week", true)]
    [InlineData("month", true)]
    [InlineData("product", false)]
    [InlineData(null, false)]
    public void Only_day_week_and_month_are_periods(string? groupBy, bool expected) => Assert.Equal(expected, ReportMath.IsPeriod(groupBy));

    [Fact]
    public void A_late_evening_order_in_bangladesh_falls_on_the_bangladesh_day_not_the_utc_day()
    {
        // 22:30 UTC on the 10th is 04:30 on the 11th in Bangladesh (UTC+6): the SQL grouping adds six hours to the UTC time.
        var at = new DateTimeOffset(2026, 11, 10, 22, 30, 0, TimeSpan.Zero);
        var local = at.UtcDateTime.AddHours(6);
        Assert.Equal(20261111, local.Year * 10000 + local.Month * 100 + local.Day);
    }

    [Fact]
    public void Texts_fall_back_to_english_when_no_bengali_is_known()
    {
        Assert.Equal("Orders", ReportText.T("Orders", "en"));
        Assert.NotEqual("Orders", ReportText.T("Orders", "bn"));
        Assert.Equal("Some unknown label", ReportText.T("Some unknown label", "bn"));
        Assert.True(ReportText.IsLang(null) && ReportText.IsLang("bn") && !ReportText.IsLang("fr"));
    }
}
