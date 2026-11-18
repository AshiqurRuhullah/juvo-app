using System.Globalization;
using Juvo.Application.Pricing;
using Juvo.Domain.Catalog;

namespace Juvo.Application.Preorders;

/// <summary>What a variant allows when it is out of stock (module AA): the variant's own setting wins over the product's.</summary>
public sealed record PreorderInfo(bool Allowed, DateOnly? ExpectedShipDate, decimal AdvancePercent);

/// <summary>
/// The pre-order rules in one place (FR-AA01, FR-AA02). A line is a pre-order line when its variant allows pre-orders, tracks stock and has no available stock at all (every selling branch
/// together); a variant that still has some stock is sold normally and limited to what is there. A pre-order line holds no reservation until stock is allocated to it.
/// </summary>
public static class PreorderRules
{
    public static PreorderInfo For(Product product, ProductVariant variant) =>
        new(variant.AllowPreorder ?? product.AllowPreorder, variant.PreorderExpectedShipDate ?? product.PreorderExpectedShipDate, variant.PreorderAdvancePercent ?? product.PreorderAdvancePercent ?? 0m);

    public static bool IsPreorderLine(PreorderInfo info, ProductVariant variant, int available) => info.Allowed && variant.TracksStock && available <= 0;

    /// <summary>The advance of a pre-order line: its share of the value the customer pays for the line (after promotions, before a coupon), rounded like every amount.</summary>
    public static decimal Advance(decimal netLine, decimal percent) => percent <= 0m ? 0m : PricingCalculator.Round(netLine * percent / 100m);

    public static string LabelEn(DateOnly? ship) =>
        ship is { } d ? $"Pre-order: ships around {d.ToString("d MMM yyyy", CultureInfo.InvariantCulture)}" : "Pre-order: ships when it is back in stock";

    public static string LabelBn(DateOnly? ship) =>
        ship is { } d ? $"প্রি-অর্ডার: আনুমানিক {d.ToString("d MMM yyyy", CultureInfo.InvariantCulture)} নাগাদ পাঠানো হবে" : "প্রি-অর্ডার: স্টকে এলে পাঠানো হবে";
}
