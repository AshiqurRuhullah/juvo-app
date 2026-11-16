using Juvo.Application.Abstractions;
using Juvo.Application.Carts;
using Juvo.Domain.Carts;
using Juvo.Domain.Catalog;
using Juvo.Domain.Promotions;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Pricing;

/// <summary>The catalog price: the variant's price override, else the product's sale price inside its dates, else the regular price. The struck-through list price is the override or the regular price.</summary>
public sealed class CatalogBasePriceSource : IBasePriceSource
{
    public int Order => 0;

    public Task<IReadOnlyDictionary<Guid, BasePrice>> PricesAsync(BasePriceRequest request, CancellationToken cancellationToken)
    {
        var now = request.Now;
        var result = new Dictionary<Guid, BasePrice>();
        foreach (var v in request.Variants)
        {
            var sale = v.SalePrice is { } s && (v.SaleStart is null || v.SaleStart <= now) && (v.SaleEnd is null || v.SaleEnd >= now) ? s : v.RegularPrice;
            var unit = v.PriceOverride ?? sale;
            var list = Math.Max(v.PriceOverride ?? v.RegularPrice, unit);
            result[v.VariantId] = new BasePrice(list, unit, "Sale", null, "Sale price", "সেল মূল্য");
        }

        return Task.FromResult<IReadOnlyDictionary<Guid, BasePrice>>(result);
    }
}

/// <inheritdoc cref="IPricingEngine"/>
public sealed class PricingEngine(IApplicationDbContext db, IEnumerable<IBasePriceSource> sources, TimeProvider clock) : IPricingEngine
{
    private sealed record Row(ProductVariant Variant, Product Product, Guid CategoryId);

    public async Task<Result<PricingResult>> PriceAsync(PricingRequest request, CancellationToken cancellationToken)
    {
        var now = request.At ?? clock.GetUtcNow();
        if (request.Lines.Any(l => l.Quantity < 0))
        {
            return Error.Validation(new Dictionary<string, string[]> { ["lines"] = ["A quantity cannot be negative."] });
        }

        var codes = (request.CouponCodes ?? []).Where(c => !string.IsNullOrWhiteSpace(c)).Select(Coupon.Normalize).Distinct().ToList();
        if (codes.Count > 1)
        {
            return Error.Conflict(ErrorCodes.Promo.MultipleCoupons, "Only one coupon can be used on an order.");
        }

        var wanted = request.Lines.GroupBy(l => l.VariantId).Select(g => new PricingLineRequest(g.Key, g.Sum(x => x.Quantity))).ToList();
        var ids = wanted.Select(w => w.VariantId).ToList();
        var query = from v in db.ProductVariants.AsNoTracking()
                    join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                    join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                    where ids.Contains(v.Id)
                    select new { Variant = v, Product = p, Category = c };
        if (request.RequireSellable)
        {
            query = query.Where(x => x.Variant.IsActive && x.Product.Status == ProductStatus.Published && x.Category.IsActive);
        }

        var rows = (await query.ToListAsync(cancellationToken)).Select(x => new Row(x.Variant, x.Product, x.Category.Id)).ToList();
        if (rows.Count != ids.Count)
        {
            return Error.NotFound(ErrorCodes.Cart.VariantUnavailable, "An item in your order is no longer available.");
        }

        // Base prices: every source in order; a later answer replaces an earlier one.
        var facts = rows.Select(r => new VariantPriceFacts(r.Variant.Id, r.Product.Id, r.CategoryId, r.Product.BrandId, r.Product.RegularPrice, r.Product.SalePrice, r.Product.SaleStart, r.Product.SaleEnd, r.Variant.PriceOverride)).ToList();
        var priceRequest = new BasePriceRequest(facts, request.Customer, now, request.Channel, request.PriceSourceHints ?? []);
        var basePrices = new Dictionary<Guid, BasePrice>();
        foreach (var source in sources.OrderBy(s => s.Order))
        {
            foreach (var (variantId, price) in await source.PricesAsync(priceRequest, cancellationToken))
            {
                basePrices[variantId] = price;
            }
        }

        var parents = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToDictionaryAsync(c => c.Id, c => c.ParentId, cancellationToken);
        IReadOnlySet<Guid> PathOf(Guid categoryId)
        {
            var path = new HashSet<Guid>();
            Guid? current = categoryId;
            for (var guard = 0; current is { } id && guard < 12 && path.Add(id); guard++)
            {
                current = parents.GetValueOrDefault(id);
            }

            return path;
        }

        var lines = new List<CalcLine>();
        foreach (var w in wanted)
        {
            var row = rows.First(r => r.Variant.Id == w.VariantId);
            if (!basePrices.TryGetValue(w.VariantId, out var price))
            {
                return Error.NotFound(ErrorCodes.Cart.VariantUnavailable, "An item in your order has no price.");
            }

            lines.Add(new CalcLine(row.Variant.Id, row.Product.Id, PathOf(row.CategoryId), row.Product.BrandId, w.Quantity, Math.Max(price.ListPrice, price.UnitPrice), price.UnitPrice,
                price.SourceType, price.SourceId, price.LabelEn, price.LabelBn));
        }

        var promotions = await LoadPromotionsAsync(request, now, cancellationToken);
        var (coupon, couponOutcome, couponError) = await LoadCouponAsync(codes.FirstOrDefault(), now, cancellationToken);

        var calc = PricingCalculator.Calculate(new CalcInput(lines, promotions, coupon));
        var outcome = couponOutcome ?? calc.Coupon;
        if (couponOutcome is null && calc.Coupon is { Problem: not null } problem && coupon is not null)
        {
            couponError = Error.Conflict(problem.Problem!, $"Spend at least {coupon.MinOrderValue:0.##} to use this coupon.");
        }

        return new PricingResult(calc.Lines, calc.Totals, calc.Promotions, outcome, calc.FreeDelivery, calc.Explanation, couponError);
    }

    private async Task<List<CalcPromotion>> LoadPromotionsAsync(PricingRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var channel = (PromotionChannels)(int)request.Channel;
        var query = db.Promotions.AsNoTracking().Include(p => p.Targets).Where(p => !p.IsArchived && (p.Channels & channel) != 0);
        if (request.PromotionSet is { } set)
        {
            var setIds = set.ToList();
            query = query.Where(p => setIds.Contains(p.Id));
        }
        else
        {
            query = query.Where(p => p.IsEnabled && (p.StartsAt == null || p.StartsAt <= now) && (p.EndsAt == null || p.EndsAt >= now) && (p.UsageLimit == null || p.UsedCount < p.UsageLimit));
        }

        var rows = await query.ToListAsync(cancellationToken);
        return [.. rows.Select(ToCalc)];
    }

    internal static CalcPromotion ToCalc(Promotion p)
    {
        var labelEn = p.LabelEn ?? p.NameEn;
        var labelBn = p.LabelBn ?? p.NameBn ?? labelEn;
        var zones = string.IsNullOrWhiteSpace(p.ZoneCodes) ? [] : p.ZoneCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return new CalcPromotion(p.Id, p.NameEn, p.NameBn ?? p.NameEn, p.Type, p.Priority, p.CreatedAt, p.StackMode, p.MinCartValue ?? 0m, p.DiscountKind, p.DiscountValue ?? 0m, p.MaxDiscount,
            p.BuyQuantity, p.GetQuantity, p.GetDiscountPercent, p.BundlePrice ?? 0m, [.. p.Targets.Select(t => new CalcTarget(t.Kind, t.TargetId, t.Role, t.Quantity))], labelEn, labelBn, zones);
    }

    private async Task<(CalcCoupon? Coupon, CouponOutcome? Outcome, Error? Error)> LoadCouponAsync(string? code, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (code is null)
        {
            return (null, null, null);
        }

        var coupon = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code, cancellationToken);
        if (coupon is null)
        {
            return (null, new CouponOutcome(code, null, false, 0m, ErrorCodes.Coupon.NotFound, null, null), Error.NotFound(ErrorCodes.Coupon.NotFound, "This coupon code does not exist."));
        }

        var problem = coupon.Check(now, decimal.MaxValue); // dates, switch and usage; the minimum order is checked against the right value by the calculator
        if (problem != CouponProblem.None)
        {
            return (null, new CouponOutcome(coupon.Code, coupon.Id, false, 0m, CouponCodes.For(problem), null, null), CouponCodes.ToError(problem, coupon));
        }

        return (new CalcCoupon(coupon.Id, coupon.Code, coupon.Kind, coupon.Value, coupon.MaxDiscount, coupon.MinOrderValue, coupon.StacksWithPromotions), null, null);
    }
}
