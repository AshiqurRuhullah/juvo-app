using Juvo.Application.Pricing;
using Juvo.Domain.Carts;
using Juvo.Domain.Promotions;

namespace Juvo.Application.Tests.Pricing;

/// <summary>The pure pricing core: stacking, lowest price wins, order-level promotions, coupons, rounding. No database.</summary>
public class PricingCalculatorTests
{
    private static readonly DateTimeOffset Epoch = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);

    private static Guid Id(string name) => new(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(name)).AsSpan(0, 16));

    private static CalcLine Line(string name, decimal list, int quantity, decimal? basePrice = null, Guid? category = null, Guid? brand = null) =>
        new(Id("variant-" + name), Id("product-" + name), new HashSet<Guid> { category ?? Id("cat") }, brand, quantity, list, basePrice ?? list, "Sale", null, "Sale price", "সেল মূল্য");

    private static CalcTarget Set(PromotionTargetKind kind, string name) => new(kind, kind == PromotionTargetKind.Everything ? null : Id((kind == PromotionTargetKind.Product ? "product-" : "") + name), TargetRole.Main, 1);

    private static CalcTarget Product(string name, TargetRole role = TargetRole.Main, int quantity = 1) => new(PromotionTargetKind.Product, Id("product-" + name), role, quantity);

    private static CalcPromotion Price(string name, DiscountKind kind, decimal value, IReadOnlyList<CalcTarget> targets, int priority = 0, StackMode mode = StackMode.StacksWithOrderPromotions, int ageDays = 0,
        PromotionType type = PromotionType.ProductDiscount, decimal min = 0m) =>
        new(Id("promo-" + name), name, name, type, priority, Epoch.AddDays(-ageDays), mode, min, kind, value, null, 0, 0, 100m, 0m, targets, name, name, []);

    private static CalcPromotion BuyXGetY(string name, int x, int y, decimal percent, IReadOnlyList<CalcTarget> targets, int priority = 0, StackMode mode = StackMode.StacksWithOrderPromotions) =>
        new(Id("promo-" + name), name, name, PromotionType.BuyXGetY, priority, Epoch, mode, 0m, null, 0m, null, x, y, percent, 0m, targets, name, name, []);

    private static CalcPromotion Bundle(string name, decimal price, IReadOnlyList<CalcTarget> items, int priority = 0, StackMode mode = StackMode.StacksWithOrderPromotions) =>
        new(Id("promo-" + name), name, name, PromotionType.Bundle, priority, Epoch, mode, 0m, null, 0m, null, 0, 0, 100m, price, items, name, name, []);

    private static CalcPromotion FreeDelivery(string name, decimal min) =>
        new(Id("promo-" + name), name, name, PromotionType.FreeDelivery, 0, Epoch, StackMode.StacksWithCoupon, min, null, 0m, null, 0, 0, 100m, 0m, [], name, name, []);

    private static CalcCoupon Coupon(string code, CouponKind kind, decimal value, bool stacks = false, decimal min = 0m, decimal? cap = null) => new(Id("coupon-" + code), code, kind, value, cap, min, stacks);

    private static CalcOutput Run(IReadOnlyList<CalcLine> lines, IReadOnlyList<CalcPromotion>? promotions = null, CalcCoupon? coupon = null) =>
        PricingCalculator.Calculate(new CalcInput(lines, promotions ?? [], coupon));

    // ---- Base price and product-level promotions -----------------------------------------------------------

    [Fact]
    public void Without_promotions_the_unit_price_is_the_base_price_and_a_sale_price_is_explained()
    {
        var result = Run([Line("tee", 1000m, 2, 900m)]);
        var line = Assert.Single(result.Lines);
        Assert.Equal(900m, line.UnitPrice);
        Assert.Equal(1800m, line.LineTotal);
        Assert.Equal(1000m, line.ListPrice);
        Assert.Equal(1800m, result.Totals.GoodsTotal);
        var sale = Assert.Single(line.Adjustments);
        Assert.Equal("Sale", sale.SourceType);
        Assert.Equal(200m, sale.Amount);
        Assert.Equal(2000m, result.Explanation.ListTotal);
        Assert.Equal(200m, result.Explanation.Savings);
    }

    [Theory]
    [InlineData(DiscountKind.Percent, 20, 800)]
    [InlineData(DiscountKind.Fixed, 150, 850)]
    [InlineData(DiscountKind.FixedPrice, 700, 700)]
    public void A_product_promotion_lowers_the_unit_price(DiscountKind kind, decimal value, decimal expectedUnit)
    {
        var result = Run([Line("tee", 1000m, 3)], [Price("p", kind, value, [Set(PromotionTargetKind.Product, "tee")])]);
        Assert.Equal(expectedUnit, result.Lines[0].UnitPrice);
        Assert.Equal(expectedUnit * 3, result.Totals.Subtotal);
        Assert.Equal((1000m - expectedUnit) * 3, result.Lines[0].Adjustments.Single(a => a.SourceType == "Promotion").Amount);
    }

    [Fact]
    public void The_lowest_price_wins_between_a_sale_price_a_flash_sale_and_a_discount_and_they_never_stack()
    {
        // base (sale) 900; flash price 850; 10% off 900 = 810: the discount is the lowest.
        var flash = Price("flash", DiscountKind.FixedPrice, 850m, [Set(PromotionTargetKind.Everything, "")], type: PromotionType.FlashSale);
        var discount = Price("ten", DiscountKind.Percent, 10m, [Set(PromotionTargetKind.Everything, "")]);
        var result = Run([Line("tee", 1000m, 1, 900m)], [flash, discount]);
        Assert.Equal(810m, result.Lines[0].UnitPrice);
        Assert.Equal(Id("promo-ten"), Assert.Single(result.Lines[0].Adjustments, a => a.SourceType == "Promotion").SourceId);
    }

    [Fact]
    public void A_promotion_that_does_not_beat_the_sale_price_is_not_applied()
    {
        var weak = Price("weak", DiscountKind.FixedPrice, 750m, [Set(PromotionTargetKind.Everything, "")]);
        var result = Run([Line("tee", 1000m, 1, 700m)], [weak]);
        Assert.Equal(700m, result.Lines[0].UnitPrice);
        Assert.Empty(result.Promotions);
    }

    [Fact]
    public void A_tie_goes_to_the_higher_priority_then_to_the_older_promotion()
    {
        var targets = new[] { Set(PromotionTargetKind.Everything, "") };
        var low = Price("low", DiscountKind.FixedPrice, 800m, targets, priority: 1);
        var high = Price("high", DiscountKind.FixedPrice, 800m, targets, priority: 5);
        Assert.Equal(Id("promo-high"), Run([Line("a", 1000m, 1)], [low, high]).Promotions.Single().Id);

        var older = Price("older", DiscountKind.FixedPrice, 800m, targets, priority: 1, ageDays: 10);
        var newer = Price("newer", DiscountKind.FixedPrice, 800m, targets, priority: 1, ageDays: 1);
        Assert.Equal(Id("promo-older"), Run([Line("a", 1000m, 1)], [newer, older]).Promotions.Single().Id);
    }

    [Fact]
    public void A_price_is_never_negative()
    {
        var result = Run([Line("tee", 100m, 2)], [Price("big", DiscountKind.Fixed, 5000m, [Set(PromotionTargetKind.Everything, "")])]);
        Assert.Equal(0m, result.Lines[0].UnitPrice);
        Assert.Equal(0m, result.Totals.GoodsTotal);
    }

    [Fact]
    public void Rounding_happens_once_per_unit_so_the_line_total_is_exact()
    {
        // 15% of 99.99 = 14.9985 -> 15.00; unit 84.99; three units 254.97.
        var result = Run([Line("tee", 99.99m, 3)], [Price("fifteen", DiscountKind.Percent, 15m, [Set(PromotionTargetKind.Everything, "")])]);
        Assert.Equal(84.99m, result.Lines[0].UnitPrice);
        Assert.Equal(254.97m, result.Lines[0].LineTotal);
        Assert.Equal(15.00m * 3, result.Lines[0].Adjustments.Single(a => a.SourceType == "Promotion").Amount);
    }

    [Fact]
    public void A_percentage_discount_respects_its_cap_per_unit()
    {
        var capped = Price("capped", DiscountKind.Percent, 50m, [Set(PromotionTargetKind.Everything, "")]) with { MaxDiscount = 100m };
        Assert.Equal(900m, Run([Line("tee", 1000m, 1)], [capped]).Lines[0].UnitPrice);
    }

    [Fact]
    public void A_minimum_cart_value_gates_a_product_promotion()
    {
        var gated = Price("gated", DiscountKind.Percent, 10m, [Set(PromotionTargetKind.Everything, "")], min: 2000m);
        Assert.Equal(1000m, Run([Line("tee", 1000m, 1)], [gated]).Lines[0].UnitPrice);
        Assert.Equal(900m, Run([Line("tee", 1000m, 2)], [gated]).Lines[0].UnitPrice);
    }

    [Fact]
    public void Category_and_brand_targets_match_the_category_path_and_the_brand()
    {
        var parent = Id("parent-cat");
        var brand = Id("brand");
        var line = new CalcLine(Id("variant-x"), Id("product-x"), new HashSet<Guid> { Id("child-cat"), parent }, brand, 1, 1000m, 1000m, "Sale", null, "Sale price", "সেল");
        var byCategory = Price("cat", DiscountKind.Percent, 10m, [new CalcTarget(PromotionTargetKind.Category, parent, TargetRole.Main, 1)]);
        var byBrand = Price("brand", DiscountKind.Percent, 20m, [new CalcTarget(PromotionTargetKind.Brand, brand, TargetRole.Main, 1)]);
        var other = Price("other", DiscountKind.Percent, 90m, [new CalcTarget(PromotionTargetKind.Brand, Id("another"), TargetRole.Main, 1)]);
        Assert.Equal(900m, PricingCalculator.Calculate(new CalcInput([line], [byCategory, other], null)).Lines[0].UnitPrice);
        Assert.Equal(800m, PricingCalculator.Calculate(new CalcInput([line], [byCategory, byBrand, other], null)).Lines[0].UnitPrice);
    }

    // ---- Buy X get Y ---------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    [InlineData(3, 100)]
    [InlineData(5, 100)] // leftovers: two units are not in a complete group
    [InlineData(6, 200)]
    public void Buy_two_get_one_free_on_the_same_product_counts_complete_groups_only(int quantity, decimal expectedDiscount)
    {
        var promo = BuyXGetY("b2g1", 2, 1, 100m, [Product("tee", TargetRole.Buy)]);
        var result = Run([Line("tee", 100m, quantity)], [promo]);
        Assert.Equal(expectedDiscount, result.Totals.PromotionDiscount);
        Assert.Equal(quantity * 100m - expectedDiscount, result.Totals.GoodsTotal);
    }

    [Fact]
    public void The_cheapest_units_of_a_set_are_the_free_ones()
    {
        var promo = BuyXGetY("b2g1", 2, 1, 100m, [new CalcTarget(PromotionTargetKind.Everything, null, TargetRole.Buy, 1)]);
        var result = Run([Line("a", 300m, 1), Line("b", 200m, 1), Line("c", 100m, 1)], [promo]);
        Assert.Equal(100m, result.Totals.PromotionDiscount);
        Assert.Equal(100m, result.Lines.Single(l => l.VariantId == Id("variant-c")).OrderDiscount);
    }

    [Fact]
    public void Buy_from_one_set_and_get_a_discount_on_another()
    {
        // Buy 2 of A, get 1 of B at 50%.
        var promo = BuyXGetY("cross", 2, 1, 50m, [Product("a", TargetRole.Buy), Product("b", TargetRole.Get)]);
        Assert.Equal(30m, Run([Line("a", 100m, 2), Line("b", 60m, 1)], [promo]).Totals.PromotionDiscount);
        Assert.Equal(0m, Run([Line("a", 100m, 1), Line("b", 60m, 1)], [promo]).Totals.PromotionDiscount);   // not enough bought
        Assert.Equal(60m, Run([Line("a", 100m, 4), Line("b", 60m, 2)], [promo]).Totals.PromotionDiscount);  // two groups
        Assert.Equal(30m, Run([Line("a", 100m, 4), Line("b", 60m, 1)], [promo]).Totals.PromotionDiscount);  // only one get unit exists
    }

    // ---- Bundles -------------------------------------------------------------------------------------------

    [Fact]
    public void A_bundle_sets_a_combo_price_and_each_complete_set_counts()
    {
        var promo = Bundle("combo", 1000m, [Product("a", TargetRole.BundleItem), Product("b", TargetRole.BundleItem)]);
        Assert.Equal(200m, Run([Line("a", 500m, 1), Line("b", 700m, 1)], [promo]).Totals.PromotionDiscount);
        Assert.Equal(400m, Run([Line("a", 500m, 2), Line("b", 700m, 2)], [promo]).Totals.PromotionDiscount);
        Assert.Equal(200m, Run([Line("a", 500m, 2), Line("b", 700m, 1)], [promo]).Totals.PromotionDiscount);   // the second A has no partner
        Assert.Equal(0m, Run([Line("a", 500m, 3)], [promo]).Totals.PromotionDiscount);
    }

    [Fact]
    public void A_bundle_that_would_cost_more_than_the_goods_gives_nothing()
    {
        var promo = Bundle("combo", 2000m, [Product("a", TargetRole.BundleItem), Product("b", TargetRole.BundleItem)]);
        Assert.Equal(0m, Run([Line("a", 500m, 1), Line("b", 700m, 1)], [promo]).Totals.PromotionDiscount);
    }

    [Fact]
    public void The_bundle_discount_is_shared_over_its_lines_without_losing_a_cent()
    {
        var promo = Bundle("combo", 999.99m, [Product("a", TargetRole.BundleItem), Product("b", TargetRole.BundleItem), Product("c", TargetRole.BundleItem)]);
        var result = Run([Line("a", 333.33m, 1), Line("b", 444.44m, 1), Line("c", 555.55m, 1)], [promo]);
        var expected = 333.33m + 444.44m + 555.55m - 999.99m;
        Assert.Equal(expected, result.Totals.PromotionDiscount);
        Assert.Equal(expected, result.Lines.Sum(l => l.OrderDiscount));
        Assert.Equal(999.99m, result.Totals.GoodsTotal);
    }

    [Fact]
    public void A_bundle_uses_up_its_units_so_another_bundle_cannot_discount_them_again()
    {
        var first = Bundle("first", 1000m, [Product("a", TargetRole.BundleItem), Product("b", TargetRole.BundleItem)], priority: 5);
        var second = Bundle("second", 900m, [Product("a", TargetRole.BundleItem), Product("b", TargetRole.BundleItem)], priority: 1);
        var result = Run([Line("a", 500m, 1), Line("b", 700m, 1)], [first, second]);
        Assert.Equal(200m, result.Totals.PromotionDiscount);
        Assert.Single(result.Promotions);
        Assert.Equal(Id("promo-first"), result.Promotions[0].Id);
    }

    [Fact]
    public void Order_level_discounts_never_exceed_the_value_of_a_line()
    {
        var bxgy = BuyXGetY("free", 1, 1, 100m, [Product("a", TargetRole.Buy)]);
        var bundle = Bundle("combo", 0m, [Product("a", TargetRole.BundleItem), Product("b", TargetRole.BundleItem)]);
        var result = Run([Line("a", 100m, 2), Line("b", 50m, 1)], [bxgy, bundle]);
        Assert.All(result.Lines, l => Assert.True(l.OrderDiscount <= l.LineTotal));
        Assert.True(result.Totals.GoodsTotal >= 0m);
    }

    // ---- Stacking of order-level promotions ----------------------------------------------------------------

    [Fact]
    public void Order_promotions_that_allow_it_combine()
    {
        var bxgy = BuyXGetY("b1g1", 1, 1, 100m, [Product("a", TargetRole.Buy)]);
        var bundle = Bundle("combo", 600m, [Product("b", TargetRole.BundleItem), Product("c", TargetRole.BundleItem)]);
        var result = Run([Line("a", 100m, 2), Line("b", 500m, 1), Line("c", 300m, 1)], [bxgy, bundle]);
        Assert.Equal(100m + 200m, result.Totals.PromotionDiscount);
        Assert.Equal(2, result.Promotions.Count);
    }

    [Fact]
    public void An_exclusive_promotion_is_alone_and_the_larger_saving_wins()
    {
        var small = BuyXGetY("small", 1, 1, 100m, [Product("a", TargetRole.Buy)], priority: 9, mode: StackMode.Exclusive); // saves 100
        var big = Bundle("big", 300m, [Product("b", TargetRole.BundleItem), Product("c", TargetRole.BundleItem)], priority: 1);   // saves 500
        var result = Run([Line("a", 100m, 2), Line("b", 500m, 1), Line("c", 300m, 1)], [small, big]);
        Assert.Equal(500m, result.Totals.PromotionDiscount);
        Assert.Equal(Id("promo-big"), Assert.Single(result.Promotions).Id);

        var tiny = BuyXGetY("tiny", 1, 1, 100m, [Product("a", TargetRole.Buy)], priority: 9, mode: StackMode.Exclusive); // saves 100
        var bigger = Bundle("bigger", 790m, [Product("b", TargetRole.BundleItem), Product("c", TargetRole.BundleItem)], priority: 1); // saves 10
        var second = Run([Line("a", 100m, 2), Line("b", 500m, 1), Line("c", 300m, 1)], [tiny, bigger]);
        Assert.Equal(100m, second.Totals.PromotionDiscount);
        Assert.Equal(Id("promo-tiny"), Assert.Single(second.Promotions).Id);
    }

    // ---- Coupons -------------------------------------------------------------------------------------------

    private static readonly CalcPromotion TenPercent = Price("ten", DiscountKind.Percent, 10m, [Set(PromotionTargetKind.Everything, "")]);

    [Fact]
    public void A_coupon_alone_applies_on_the_normal_prices()
    {
        var result = Run([Line("tee", 1000m, 1)], [], Coupon("SAVE", CouponKind.Percent, 10m));
        Assert.True(result.Coupon!.Applied);
        Assert.Equal(100m, result.Totals.CouponDiscount);
        Assert.Equal(900m, result.Totals.GoodsTotal);
    }

    [Fact]
    public void When_promotions_are_better_the_coupon_is_not_applied_and_the_reason_is_given()
    {
        var result = Run([Line("tee", 1000m, 1)], [TenPercent with { Value = 30m }], Coupon("SAVE", CouponKind.Fixed, 50m));
        Assert.False(result.Coupon!.Applied);
        Assert.Equal(0m, result.Totals.CouponDiscount);
        Assert.Equal(700m, result.Totals.GoodsTotal);
        Assert.False(string.IsNullOrWhiteSpace(result.Coupon.NotAppliedReasonEn));
        Assert.False(string.IsNullOrWhiteSpace(result.Coupon.NotAppliedReasonBn));
        Assert.Contains(result.Explanation.NotesEn, n => n.Contains("SAVE", StringComparison.Ordinal));
    }

    [Fact]
    public void When_the_coupon_is_better_it_replaces_the_promotions_they_do_not_combine()
    {
        var result = Run([Line("tee", 1000m, 1)], [TenPercent], Coupon("BIG", CouponKind.Fixed, 300m));
        Assert.True(result.Coupon!.Applied);
        Assert.Equal(300m, result.Totals.CouponDiscount);
        Assert.Equal(0m, result.Totals.PromotionDiscount);
        Assert.Equal(1000m, result.Lines[0].UnitPrice); // the promotion was dropped, not stacked
        Assert.Equal(700m, result.Totals.GoodsTotal);
        Assert.Empty(result.Promotions);
    }

    [Fact]
    public void A_coupon_stacks_only_when_the_coupon_and_every_winning_promotion_allow_it()
    {
        var allows = TenPercent with { StackMode = StackMode.StacksWithCoupon };
        var stacked = Run([Line("tee", 1000m, 1)], [allows], Coupon("BOTH", CouponKind.Fixed, 50m, stacks: true));
        Assert.Equal(900m - 50m, stacked.Totals.GoodsTotal);
        Assert.Equal(50m, stacked.Totals.CouponDiscount);

        // the coupon allows it but the promotion does not: the better of the two (promotion 100 off beats coupon 50)
        var promoRefuses = Run([Line("tee", 1000m, 1)], [TenPercent], Coupon("BOTH", CouponKind.Fixed, 50m, stacks: true));
        Assert.False(promoRefuses.Coupon!.Applied);
        Assert.Equal(900m, promoRefuses.Totals.GoodsTotal);

        // the promotion allows it but the coupon does not
        var couponRefuses = Run([Line("tee", 1000m, 1)], [allows], Coupon("ONE", CouponKind.Fixed, 50m, stacks: false));
        Assert.False(couponRefuses.Coupon!.Applied);
        Assert.Equal(900m, couponRefuses.Totals.GoodsTotal);
    }

    [Fact]
    public void The_minimum_order_of_a_coupon_is_checked_on_what_the_customer_pays_after_promotions()
    {
        // goods 1000, promotion 10% -> 900. The coupon needs 950: not valid with the promotion, valid alone (1000).
        var result = Run([Line("tee", 1000m, 1)], [TenPercent], Coupon("MIN", CouponKind.Fixed, 20m, min: 950m));
        // promotion alone gives 900; coupon alone gives 980: the promotion is better, coupon not applied (and not an error)
        Assert.False(result.Coupon!.Applied);
        Assert.Null(result.Coupon.Problem);
        Assert.Equal(900m, result.Totals.GoodsTotal);

        var neither = Run([Line("tee", 1000m, 1)], [], Coupon("MIN", CouponKind.Fixed, 20m, min: 5000m));
        Assert.False(neither.Coupon!.Applied);
        Assert.Equal("COUPON.BELOW_MINIMUM", neither.Coupon.Problem);
    }

    [Fact]
    public void A_coupon_never_takes_the_goods_below_zero_and_a_percentage_coupon_respects_its_cap()
    {
        Assert.Equal(0m, Run([Line("tee", 100m, 1)], [], Coupon("HUGE", CouponKind.Fixed, 5000m)).Totals.GoodsTotal);
        Assert.Equal(50m, Run([Line("tee", 1000m, 1)], [], Coupon("CAP", CouponKind.Percent, 50m, cap: 50m)).Totals.CouponDiscount);
    }

    // ---- Free delivery -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(1999, false, 1)]
    [InlineData(2000, true, 0)]   // the threshold itself qualifies
    [InlineData(2001, true, 0)]
    public void Free_delivery_qualifies_at_the_threshold_on_what_is_paid_for_the_goods(int goods, bool qualifies, int missing)
    {
        var result = Run([Line("tee", goods, 1)], [FreeDelivery("free", 2000m)]);
        var offer = Assert.Single(result.FreeDelivery);
        Assert.Equal(qualifies, offer.Qualifies);
        Assert.Equal((decimal)missing, offer.AmountToQualify);
    }

    [Fact]
    public void Free_delivery_is_judged_after_promotions_and_the_coupon()
    {
        var result = Run([Line("tee", 2200m, 1)], [FreeDelivery("free", 2000m)], Coupon("C", CouponKind.Fixed, 300m));
        Assert.False(result.FreeDelivery[0].Qualifies); // 2200 - 300 = 1900
    }

    // ---- Explanation ---------------------------------------------------------------------------------------

    [Fact]
    public void The_explanation_gives_list_price_final_price_savings_and_short_reasons_in_both_languages()
    {
        var promo = Price("Eid sale", DiscountKind.Percent, 10m, [Set(PromotionTargetKind.Everything, "")]) with { LabelEn = "Eid sale", LabelBn = "ঈদ সেল" };
        var result = Run([Line("tee", 1000m, 2, 900m)], [promo]);
        var e = result.Explanation;
        Assert.Equal(2000m, e.ListTotal);          // struck-through
        Assert.Equal(1620m, e.FinalTotal);         // 810 x 2
        Assert.Equal(380m, e.Savings);
        Assert.Contains(e.Reasons, r => r.LabelEn == "Sale price" && r.Amount == 200m);
        Assert.Contains(e.Reasons, r => r.LabelEn == "Eid sale" && r.LabelBn == "ঈদ সেল" && r.Amount == 180m);
        var line = Assert.Single(e.Lines);
        Assert.Equal(1000m, line.ListPrice);
        Assert.Equal(810m, line.UnitPrice);
        Assert.Contains("ঈদ সেল", line.ReasonsBn);
    }

    [Fact]
    public void Quantity_zero_lines_are_priced_but_cost_nothing()
    {
        var result = Run([Line("tee", 1000m, 0), Line("cap", 100m, 1)], [Price("p", DiscountKind.Percent, 10m, [Set(PromotionTargetKind.Everything, "")])]);
        Assert.Equal(900m, result.Lines[0].UnitPrice);
        Assert.Equal(0m, result.Lines[0].LineTotal);
        Assert.Equal(90m, result.Totals.Subtotal);
    }

    // ---- Schedule (status is derived, windows include both ends) ---------------------------------------------

    [Fact]
    public void A_promotion_window_includes_its_first_and_last_moment()
    {
        var start = new DateTimeOffset(2026, 12, 1, 10, 0, 0, TimeSpan.FromHours(6));
        var end = new DateTimeOffset(2026, 12, 1, 18, 0, 0, TimeSpan.FromHours(6));
        var details = new PromotionDetails("Flash", null, PromotionType.FlashSale, start, end, 0, StackMode.StacksWithOrderPromotions, PromotionChannels.All, null, null, DiscountKind.Percent, 10m, null, 0, 0, 100m, null,
            null, null, null, null, null, null, null, null, null, null, BannerSlot.None, null);
        var promotion = new Promotion(details);
        Assert.Equal(PromotionStatus.Draft, promotion.StatusAt(start));
        promotion.Enable();
        Assert.Equal(PromotionStatus.Scheduled, promotion.StatusAt(start.AddTicks(-1)));
        Assert.Equal(PromotionStatus.Active, promotion.StatusAt(start));
        Assert.Equal(PromotionStatus.Active, promotion.StatusAt(end));
        Assert.Equal(PromotionStatus.Ended, promotion.StatusAt(end.AddTicks(1)));
        promotion.Archive();
        Assert.Equal(PromotionStatus.Archived, promotion.StatusAt(start));
    }

    [Fact]
    public void A_price_for_the_helper_matches_the_calculator_rounding()
    {
        Assert.Equal(84.99m, PricingCalculator.PriceFor(DiscountKind.Percent, 15m, null, 99.99m));
        Assert.Equal(0m, PricingCalculator.PriceFor(DiscountKind.Fixed, 500m, null, 99.99m));
        Assert.Equal(49.5m, PricingCalculator.PriceFor(DiscountKind.FixedPrice, 49.5m, null, 99.99m));
    }
}
