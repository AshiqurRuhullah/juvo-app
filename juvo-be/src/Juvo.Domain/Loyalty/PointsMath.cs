namespace Juvo.Domain.Loyalty;

public enum PointsRounding
{
    Down = 1,
    Nearest = 2,
    Up = 3,
}

/// <summary>The arithmetic of points and credit, free of the database so every rule can be tested alone. Money is BDT with two decimals.</summary>
public static class PointsMath
{
    /// <summary>Points earned for an amount spent: <c>amount * rate</c>, rounded as configured. Never negative.</summary>
    public static int Earn(decimal amount, decimal ratePerBdt, PointsRounding rounding)
    {
        if (amount <= 0 || ratePerBdt <= 0)
        {
            return 0;
        }

        var raw = amount * ratePerBdt;
        var points = rounding switch
        {
            PointsRounding.Up => decimal.Ceiling(raw),
            PointsRounding.Nearest => decimal.Round(raw, 0, MidpointRounding.AwayFromZero),
            _ => decimal.Floor(raw),
        };
        return points > int.MaxValue ? int.MaxValue : (int)points;
    }

    /// <summary>What points are worth when used to pay: <c>points * value</c>, two decimals.</summary>
    public static decimal ValueOf(int points, decimal pointValue) => points <= 0 || pointValue <= 0 ? 0m : decimal.Round(points * pointValue, 2, MidpointRounding.AwayFromZero);

    /// <summary>The most of an order that points and store credit together may pay: a percent of the order total, rounded down to two decimals.</summary>
    public static decimal MaxTender(decimal orderTotal, int percent)
    {
        if (orderTotal <= 0 || percent <= 0)
        {
            return 0m;
        }

        var share = orderTotal * Math.Min(percent, 100) / 100m;
        return decimal.Floor(share * 100m) / 100m;
    }

    /// <summary>The most points that fit in <paramref name="maxAmount"/> BDT (whole points, rounded down).</summary>
    public static int MaxPointsFor(decimal maxAmount, decimal pointValue) => maxAmount <= 0 || pointValue <= 0 ? 0 : (int)Math.Min(int.MaxValue, decimal.Floor(maxAmount / pointValue));

    /// <summary>Part of the earned points of an order to take back when part of its value is refunded: proportional, rounded to the nearest point, never more than what is left to reverse.</summary>
    public static int ReverseShare(int earned, int alreadyReversed, decimal refunded, decimal orderTotal)
    {
        if (earned <= 0 || refunded <= 0 || orderTotal <= 0)
        {
            return 0;
        }

        var left = Math.Max(0, earned - alreadyReversed);
        var share = (int)decimal.Round(earned * Math.Min(1m, refunded / orderTotal), 0, MidpointRounding.AwayFromZero);
        return Math.Min(left, share);
    }

    /// <summary>
    /// How many points expire now. Points are used oldest-expiry first, so the points of lots whose date has passed are gone first:
    /// <c>expiredLots - everythingTakenOut - alreadyExpired</c>, never below zero and never more than the balance.
    /// </summary>
    public static int Expirable(long expiredLotPoints, long takenOutPoints, long alreadyExpiredPoints, int balance)
    {
        var due = expiredLotPoints - takenOutPoints - alreadyExpiredPoints;
        return (int)Math.Max(0, Math.Min(due, balance));
    }

    /// <summary>Splits a refund between the tender part and the cash part in proportion to what each can still give back.</summary>
    public static decimal TenderShareOfRefund(decimal refund, decimal tenderRefundable, decimal cashRefundable)
    {
        if (refund <= 0 || tenderRefundable <= 0)
        {
            return 0m;
        }

        var total = tenderRefundable + cashRefundable;
        var share = decimal.Round(refund * tenderRefundable / total, 2, MidpointRounding.AwayFromZero);
        return Math.Min(Math.Min(share, tenderRefundable), refund);
    }
}
