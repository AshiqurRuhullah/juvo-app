using Juvo.Domain.Loyalty;

namespace Juvo.Domain.Tests.Loyalty;

public class PointsMathTests
{
    [Theory]
    [InlineData(1000, 0.01, PointsRounding.Down, 10)]
    [InlineData(1099, 0.01, PointsRounding.Down, 10)]
    [InlineData(1099, 0.01, PointsRounding.Nearest, 11)]
    [InlineData(1049, 0.01, PointsRounding.Nearest, 10)]
    [InlineData(1001, 0.01, PointsRounding.Up, 11)]
    [InlineData(1000, 0.01, PointsRounding.Up, 10)]
    [InlineData(250.50, 1, PointsRounding.Down, 250)]
    [InlineData(99, 0.01, PointsRounding.Down, 0)]
    [InlineData(0, 1, PointsRounding.Down, 0)]
    [InlineData(-50, 1, PointsRounding.Up, 0)]
    [InlineData(500, 0, PointsRounding.Up, 0)]
    public void Points_earned_follow_the_rate_and_the_rounding_and_are_never_negative(double amount, double rate, PointsRounding rounding, int expected) =>
        Assert.Equal(expected, PointsMath.Earn((decimal)amount, (decimal)rate, rounding));

    [Fact]
    public void An_enormous_order_does_not_overflow_the_points() =>
        Assert.Equal(int.MaxValue, PointsMath.Earn(2_000_000_000m, 5m, PointsRounding.Down));

    [Theory]
    [InlineData(200, 1, 200)]
    [InlineData(333, 0.5, 166.5)]
    [InlineData(7, 0.33, 2.31)]
    [InlineData(0, 1, 0)]
    [InlineData(-5, 1, 0)]
    public void The_value_of_points_is_points_times_the_point_value_with_two_decimals(int points, double value, double expected) =>
        Assert.Equal((decimal)expected, PointsMath.ValueOf(points, (decimal)value));

    [Theory]
    [InlineData(1060, 50, 530)]
    [InlineData(1000.99, 50, 500.49)]
    [InlineData(1000, 100, 1000)]
    [InlineData(1000, 150, 1000)]
    [InlineData(1000, 0, 0)]
    [InlineData(0, 50, 0)]
    public void Points_and_credit_may_pay_at_most_a_percent_of_the_order_rounded_down(double total, int percent, double expected) =>
        Assert.Equal((decimal)expected, PointsMath.MaxTender((decimal)total, percent));

    [Theory]
    [InlineData(530, 1, 530)]
    [InlineData(530, 0.5, 1060)]
    [InlineData(530.40, 1, 530)]
    [InlineData(0, 1, 0)]
    [InlineData(100, 0, 0)]
    public void The_most_points_that_fit_under_a_cap_are_whole_points(double cap, double value, int expected) =>
        Assert.Equal(expected, PointsMath.MaxPointsFor((decimal)cap, (decimal)value));

    [Theory]
    [InlineData(2000, 0, 1000, 2000, 1000)]
    [InlineData(2000, 0, 2000, 2000, 2000)]
    [InlineData(2000, 0, 3000, 2000, 2000)] // never more than the order value
    [InlineData(2000, 1500, 2000, 2000, 500)] // only what is left to reverse
    [InlineData(2000, 2000, 500, 2000, 0)]
    [InlineData(0, 0, 500, 2000, 0)]
    [InlineData(2000, 0, 0, 2000, 0)]
    [InlineData(2000, 0, 500, 0, 0)]
    [InlineData(10, 0, 333, 1000, 3)] // 10 * 0.333 rounds to 3
    public void The_reversal_of_earned_points_is_proportional_and_capped(int earned, int already, double refunded, double orderValue, int expected) =>
        Assert.Equal(expected, PointsMath.ReverseShare(earned, already, (decimal)refunded, (decimal)orderValue));

    [Theory]
    [InlineData(300, 0, 0, 300, 300)] // a whole lot is due
    [InlineData(300, 100, 0, 200, 200)] // 100 were used: points leave oldest lot first
    [InlineData(300, 100, 0, 150, 150)] // never more than the balance
    [InlineData(300, 400, 0, 500, 0)] // everything of the old lot was used already
    [InlineData(300, 100, 200, 100, 0)] // an earlier run already expired the rest
    [InlineData(0, 0, 0, 500, 0)] // nothing is due yet
    public void The_points_that_expire_are_the_due_lots_minus_what_left_the_account_and_never_exceed_the_balance(long lots, long takenOut, long alreadyExpired, int balance, int expected) =>
        Assert.Equal(expected, PointsMath.Expirable(lots, takenOut, alreadyExpired, balance));

    [Theory]
    [InlineData(1000, 400, 660, 377.36)]
    [InlineData(1000, 400, 0, 400)] // nothing but tender: it gives back what it can
    [InlineData(300, 400, 0, 300)] // never more than the refund
    [InlineData(1000, 0, 660, 0)]
    [InlineData(0, 400, 660, 0)]
    public void A_refund_is_split_between_the_tender_and_the_cash_in_proportion(double refund, double tender, double cash, double expected) =>
        Assert.Equal((decimal)expected, PointsMath.TenderShareOfRefund((decimal)refund, (decimal)tender, (decimal)cash));
}

public class LoyaltyAccountTests
{
    [Fact]
    public void A_balance_changes_by_the_posted_amount_and_can_never_go_below_zero()
    {
        var account = new LoyaltyAccount(Guid.NewGuid());
        Assert.Equal(300, account.ApplyPoints(300));
        Assert.Equal(100, account.ApplyPoints(-200));
        Assert.Throws<InvalidOperationException>(() => account.ApplyPoints(-101));
        Assert.Equal(100, account.PointsBalance);
        Assert.Equal(40.5m, account.ApplyCredit(40.5m));
        Assert.Throws<InvalidOperationException>(() => account.ApplyCredit(-40.51m));
        Assert.Equal(0m, account.ApplyCredit(-40.5m));
    }

    [Fact]
    public void Five_wrong_codes_inside_the_window_lock_code_entry_and_an_old_failure_does_not_count()
    {
        var account = new LoyaltyAccount(Guid.NewGuid());
        var t0 = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        var window = TimeSpan.FromMinutes(15);
        var lockFor = TimeSpan.FromMinutes(15);

        for (var i = 0; i < 4; i++)
        {
            account.RecordCodeFailure(t0.AddMinutes(i), 5, window, lockFor);
            Assert.False(account.IsCodeLocked(t0.AddMinutes(i)));
        }

        account.RecordCodeFailure(t0.AddMinutes(4), 5, window, lockFor);
        Assert.True(account.IsCodeLocked(t0.AddMinutes(5)));
        Assert.True(account.IsCodeLocked(t0.AddMinutes(18)));
        Assert.False(account.IsCodeLocked(t0.AddMinutes(20)));

        var slow = new LoyaltyAccount(Guid.NewGuid());
        for (var i = 0; i < 6; i++)
        {
            slow.RecordCodeFailure(t0.AddMinutes(i * 20), 5, window, lockFor); // a failure every 20 minutes never adds up
        }

        Assert.False(slow.IsCodeLocked(t0.AddMinutes(200)));
    }
}

public class GiftCardTests
{
    private static GiftCard Card(decimal amount, DateTimeOffset? expires = null) => new("hash", "ABCD", amount, expires, null, null, null, "tester");

    [Fact]
    public void A_card_can_be_spent_until_it_is_empty_expired_or_switched_off()
    {
        var now = new DateTimeOffset(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);
        var card = Card(500m, now.AddDays(10));
        Assert.True(card.CanSpend(now));
        Assert.Equal(200m, card.ApplyChange(-300m));
        Assert.Throws<InvalidOperationException>(() => card.ApplyChange(-200.01m));
        Assert.False(card.CanSpend(now.AddDays(10))); // the expiry moment itself is expired
        Assert.True(card.IsExpired(now.AddDays(11)));

        var other = Card(100m);
        Assert.True(other.CanSpend(now.AddYears(50))); // no expiry date
        other.Deactivate();
        Assert.False(other.CanSpend(now));
        other.ApplyChange(-100m);
        Assert.Equal(0m, other.Balance);
    }
}

public class OrderTenderTests
{
    [Fact]
    public void Points_returned_never_exceed_the_points_used_and_the_total_adds_the_three_parts()
    {
        var tender = new OrderTender(Guid.NewGuid(), Guid.NewGuid(), 400, 1m, 400m, 50m, 120.5m, Guid.NewGuid(), "ABCD");
        Assert.Equal(570.5m, tender.Total);
        tender.ReturnPoints(300);
        tender.ReturnPoints(300);
        Assert.Equal(400, tender.PointsReturned);
    }
}

public class ReferralTests
{
    [Fact]
    public void A_referral_is_pending_until_decided_and_a_reward_can_be_reversed()
    {
        var now = DateTimeOffset.UtcNow;
        var referral = new Referral(Guid.NewGuid(), Guid.NewGuid(), "ABCDEFGH", now);
        Assert.Equal(ReferralStatus.Pending, referral.Status);
        referral.Reward(Guid.NewGuid(), 100m, 50m, "household", now);
        Assert.Equal((ReferralStatus.Rewarded, 100m, 50m), (referral.Status, referral.ReferrerReward, referral.RefereeReward));
        referral.MarkReversed("returned", now);
        Assert.Equal(ReferralStatus.Reversed, referral.Status);

        var rejected = new Referral(Guid.NewGuid(), Guid.NewGuid(), "ABCDEFGH", now);
        rejected.Reject(new string('x', 400), null, "household", now);
        Assert.Equal(ReferralStatus.Rejected, rejected.Status);
        Assert.Equal(300, rejected.Note!.Length);
    }
}
