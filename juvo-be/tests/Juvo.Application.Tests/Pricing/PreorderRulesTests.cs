using Juvo.Application.Preorders;
using Juvo.Domain.Catalog;

namespace Juvo.Application.Tests.Pricing;

public class PreorderRulesTests
{
    private static Product NewProduct(bool allow, DateOnly? ship = null, decimal? advance = null)
    {
        var product = new Product(new ProductDetails("Coat", null, "coat", Guid.NewGuid(), null, null, null, null, null, null, TargetGroup.Unisex, null, null, null, null, 1000m, null, null, null, null, false, false, false, null, null, null, null, null));
        product.SetPreorder(allow, ship, advance);
        return product;
    }

    private static ProductVariant NewVariant(Product product) => new(product.Id, "COAT-M", "Red", null, "M");

    [Fact]
    public void A_variant_inherits_the_product_setting_unless_it_has_its_own()
    {
        var ship = new DateOnly(2026, 12, 20);
        var product = NewProduct(true, ship, 40m);
        var variant = NewVariant(product);
        Assert.Equal(new PreorderInfo(true, ship, 40m), PreorderRules.For(product, variant));

        variant.SetPreorder(false, null, null);
        Assert.False(PreorderRules.For(product, variant).Allowed);

        variant.SetPreorder(true, new DateOnly(2027, 1, 5), 10m);
        Assert.Equal(new PreorderInfo(true, new DateOnly(2027, 1, 5), 10m), PreorderRules.For(product, variant));

        variant.SetPreorder(null, null, null);
        Assert.Equal(new PreorderInfo(true, ship, 40m), PreorderRules.For(product, variant));
    }

    [Fact]
    public void A_switched_off_product_forgets_its_date_and_advance()
    {
        var product = NewProduct(true, new DateOnly(2026, 12, 20), 40m);
        product.SetPreorder(false, new DateOnly(2026, 12, 20), 40m);
        Assert.Null(product.PreorderExpectedShipDate);
        Assert.Null(product.PreorderAdvancePercent);
    }

    [Theory]
    [InlineData(true, 0, true)]
    [InlineData(true, 1, false)]   // some stock left: sold normally
    [InlineData(false, 0, false)]  // not allowed
    public void A_line_is_a_pre_order_only_when_allowed_and_completely_out_of_stock(bool allowed, int available, bool expected)
    {
        var product = NewProduct(allowed);
        var variant = NewVariant(product);
        Assert.Equal(expected, PreorderRules.IsPreorderLine(PreorderRules.For(product, variant), variant, available));
    }

    [Theory]
    [InlineData(2000, 50, 1000)]
    [InlineData(1799.99, 25, 450)]   // 449.9975 rounds up
    [InlineData(1000, 0, 0)]
    public void The_advance_is_a_rounded_share_of_the_line_value(double net, double percent, double expected)
    {
        Assert.Equal((decimal)expected, PreorderRules.Advance((decimal)net, (decimal)percent));
    }

    [Fact]
    public void The_labels_name_the_expected_date_in_both_languages()
    {
        Assert.Equal("Pre-order: ships around 20 Dec 2026", PreorderRules.LabelEn(new DateOnly(2026, 12, 20)));
        Assert.Contains("20 Dec 2026", PreorderRules.LabelBn(new DateOnly(2026, 12, 20)), StringComparison.Ordinal);
        Assert.Contains("প্রি-অর্ডার", PreorderRules.LabelBn(null), StringComparison.Ordinal);
    }
}
