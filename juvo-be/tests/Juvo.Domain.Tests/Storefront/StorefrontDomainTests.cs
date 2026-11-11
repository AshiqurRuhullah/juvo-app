using Juvo.Domain.Carts;
using Juvo.Domain.Delivery;

namespace Juvo.Domain.Tests.Storefront;

public class DeliveryPricingTests
{
    private static readonly ZoneRules Domestic = new(100m, 5000m, 50m, 30m, null, [(1000, 0m), (2000, 25m), (5000, 70m)], 2, 4);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(1000, 0)]
    [InlineData(1001, 25)]
    [InlineData(2000, 25)]
    [InlineData(5000, 70)]
    public void The_weight_fee_is_the_first_slab_that_holds_the_weight(int grams, int expected)
    {
        Assert.Equal(expected, DeliveryPricing.WeightFee(Domestic, grams));
    }

    [Theory]
    [InlineData(5001, 100)] // one started kilogram beyond the last slab: 70 + 1 x 30
    [InlineData(6000, 100)]
    [InlineData(6001, 130)] // two started kilograms
    [InlineData(10000, 220)] // five kilograms beyond 5 kg: 70 + 5 x 30 = 220
    public void Beyond_the_last_slab_every_started_kilogram_adds_the_per_kg_fee(int grams, int expected)
    {
        Assert.Equal(expected, DeliveryPricing.WeightFee(Domestic, grams));
    }

    [Fact]
    public void A_zone_without_slabs_charges_no_weight_fee()
    {
        var flat = new ZoneRules(80m, null, 0m, 10m, null, [], 1, 2);

        Assert.Equal(0m, DeliveryPricing.WeightFee(flat, 9000));
        Assert.Equal(80m, DeliveryPricing.Calculate(flat, 100m, 9000, false)!.Total);
    }

    [Fact]
    public void The_charge_is_base_plus_weight_plus_remote()
    {
        var charge = DeliveryPricing.Calculate(Domestic, 1000m, 1500, isRemote: true)!;

        Assert.Equal((100m, 25m, 50m, 175m), (charge.BaseFee, charge.WeightFee, charge.RemoteSurcharge, charge.Total));
        Assert.False(charge.IsFree);
        Assert.Equal(4000m, charge.AmountToFree);
    }

    [Fact]
    public void Reaching_the_free_threshold_waives_base_and_weight_but_never_the_remote_surcharge()
    {
        var free = DeliveryPricing.Calculate(Domestic, 5000m, 1500, isRemote: false)!;
        var freeRemote = DeliveryPricing.Calculate(Domestic, 7000m, 1500, isRemote: true)!;

        Assert.True(free.IsFree);
        Assert.Equal((0m, 125m), (free.Total, free.FreeWaiver));
        Assert.Null(free.AmountToFree);
        Assert.Equal(50m, freeRemote.Total);
    }

    [Fact]
    public void The_charge_just_under_the_threshold_is_still_charged()
    {
        Assert.Equal(100m, DeliveryPricing.Calculate(Domestic, 4999.99m, 500, false)!.Total);
    }

    [Fact]
    public void A_parcel_heavier_than_the_zone_accepts_has_no_charge()
    {
        var international = Domestic with { MaxWeightGrams = 20000 };

        Assert.NotNull(DeliveryPricing.Calculate(international, 100m, 20000, false));
        Assert.Null(DeliveryPricing.Calculate(international, 100m, 20001, false));
    }

    [Fact]
    public void A_zone_keeps_its_slabs_sorted_and_rules_reflect_the_rates()
    {
        var zone = new DeliveryZone("TEST", "Test zone", null, ZoneKind.Domestic);
        zone.SetRates(90m, 3000m, 40m, 20m, null);
        zone.ReplaceSlabs([(2000, 30m), (1000, 0m)]);
        zone.SetDetails("Test zone", null, true, true, 1, 2, 1); // days max below min is lifted to min

        var rules = zone.Rules();

        Assert.Equal([(1000, 0m), (2000, 30m)], rules.Slabs);
        Assert.Equal((2, 2), (rules.DaysMin, rules.DaysMax));
        Assert.True(zone.IsFallback);
        Assert.Equal(90m, rules.BaseFee);
    }

    [Fact]
    public void Only_a_domestic_zone_can_be_the_fallback()
    {
        var zone = new DeliveryZone("INTL", "Abroad", null, ZoneKind.International);

        zone.SetDetails("Abroad", null, true, true, 1, 7, 14);

        Assert.False(zone.IsFallback);
    }
}

public class CouponTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    private static Coupon Percent(decimal value = 10m)
    {
        var coupon = new Coupon("eid10", CouponKind.Percent, value);
        coupon.Update(null, CouponKind.Percent, value, 0m, null, null, null, null, true);
        return coupon;
    }

    [Fact]
    public void The_code_is_stored_upper_case_without_spaces()
    {
        Assert.Equal("EID-10", Coupon.Normalize("  eid-10 "));
        Assert.Equal("EID10", new Coupon(" eid10 ", CouponKind.Fixed, 50m).Code);
    }

    [Theory]
    [InlineData(1000, 100)]
    [InlineData(999.99, 100)] // 99.999 rounds up to 100.00
    [InlineData(0, 0)]
    public void A_percentage_coupon_takes_that_share_rounded_to_two_decimals(double subtotal, double expected)
    {
        Assert.Equal((decimal)expected, Percent().DiscountFor((decimal)subtotal));
    }

    [Fact]
    public void A_percentage_coupon_is_capped_by_its_maximum_discount()
    {
        var coupon = Percent(50m);
        coupon.Update(null, CouponKind.Percent, 50m, 0m, 300m, null, null, null, true);

        Assert.Equal(300m, coupon.DiscountFor(2000m));
        Assert.Equal(250m, coupon.DiscountFor(500m));
    }

    [Fact]
    public void A_fixed_coupon_never_takes_more_than_the_subtotal()
    {
        var coupon = new Coupon("FLAT", CouponKind.Fixed, 200m);

        Assert.Equal(200m, coupon.DiscountFor(1000m));
        Assert.Equal(150m, coupon.DiscountFor(150m));
    }

    [Fact]
    public void Check_reports_why_a_coupon_cannot_be_used()
    {
        var coupon = new Coupon("TEST", CouponKind.Fixed, 50m);
        coupon.Update(null, CouponKind.Fixed, 50m, 500m, null, Now.AddDays(1), Now.AddDays(10), 2, true);

        Assert.Equal(CouponProblem.NotStarted, coupon.Check(Now, 1000m));
        Assert.Equal(CouponProblem.BelowMinimum, coupon.Check(Now.AddDays(2), 499m));
        Assert.Equal(CouponProblem.None, coupon.Check(Now.AddDays(2), 500m));
        Assert.Equal(CouponProblem.Expired, coupon.Check(Now.AddDays(11), 1000m));

        coupon.RegisterUse();
        coupon.RegisterUse();
        Assert.Equal(CouponProblem.UsedUp, coupon.Check(Now.AddDays(2), 1000m));

        coupon.Update(null, CouponKind.Fixed, 50m, 500m, null, null, null, 2, false);
        Assert.Equal(CouponProblem.Inactive, coupon.Check(Now, 1000m));
    }
}

public class CartTests
{
    private static Cart NewCart() => new(null, "hash", DateTimeOffset.UtcNow.AddDays(30));

    [Fact]
    public void Adding_the_same_variant_again_adds_up_and_refreshes_the_price()
    {
        var cart = NewCart();
        var variant = Guid.NewGuid();

        cart.Add(variant, 2, 500m);
        cart.Add(variant, 1, 450m);

        var line = Assert.Single(cart.Items);
        Assert.Equal((3, 450m), (line.Quantity, line.UnitPriceSnapshot));
    }

    [Fact]
    public void Removing_a_line_and_clearing_work_and_clearing_drops_the_coupon()
    {
        var cart = NewCart();
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        cart.Add(a, 1, 100m);
        cart.Add(b, 1, 100m);
        cart.SetCoupon(Guid.NewGuid());

        cart.Remove(cart.Find(a)!);
        Assert.Single(cart.Items);

        cart.Clear();
        Assert.Empty(cart.Items);
        Assert.Null(cart.CouponId);
    }

    [Fact]
    public void A_guest_cart_becomes_a_customer_cart_and_forgets_its_token()
    {
        var cart = NewCart();
        var customer = Guid.NewGuid();

        cart.AssignTo(customer);

        Assert.Equal(customer, cart.CustomerId);
        Assert.Null(cart.TokenHash);
    }
}
