using Juvo.Domain.Carts;
using Juvo.Domain.Promotions;

namespace Juvo.Application.Pricing;

public sealed record CalcLine(
    Guid VariantId, Guid ProductId, IReadOnlySet<Guid> CategoryPath, Guid? BrandId, int Quantity, decimal ListPrice, decimal BaseUnitPrice, string BaseSourceType, Guid? BaseSourceId,
    string BaseLabelEn, string BaseLabelBn);

public sealed record CalcTarget(PromotionTargetKind Kind, Guid? TargetId, TargetRole Role, int Quantity);

public sealed record CalcPromotion(
    Guid Id, string NameEn, string NameBn, PromotionType Type, int Priority, DateTimeOffset CreatedAt, StackMode StackMode, decimal MinCartValue, DiscountKind? Kind, decimal Value,
    decimal? MaxDiscount, int BuyQuantity, int GetQuantity, decimal GetPercent, decimal BundlePrice, IReadOnlyList<CalcTarget> Targets, string LabelEn, string LabelBn, IReadOnlyList<string> ZoneCodes);

/// <summary>A coupon that is valid at the pricing time (active, in its dates, not used up). Its minimum order is checked here against the value it would apply to.</summary>
public sealed record CalcCoupon(Guid Id, string Code, CouponKind Kind, decimal Value, decimal? MaxDiscount, decimal MinOrderValue, bool StacksWithPromotions);

public sealed record CalcInput(IReadOnlyList<CalcLine> Lines, IReadOnlyList<CalcPromotion> Promotions, CalcCoupon? Coupon);

public sealed record CalcOutput(
    IReadOnlyList<PricedLine> Lines, PricingTotals Totals, IReadOnlyList<AppliedPromotion> Promotions, CouponOutcome? Coupon, IReadOnlyList<FreeDeliveryOffer> FreeDelivery, PriceExplanation Explanation);

/// <summary>
/// The pure core of the pricing engine: no database, no clock. The stacking policy (docs/pricing-engine.md): (1) base price; (2) per line the LOWEST price among the base price and the
/// price-type promotions wins; (3) order-level promotions (bundle, buy X get Y) run on those prices in priority order, each consuming the units it uses; (4) a coupon applies after
/// promotions and stacks only if both sides allow it, otherwise the customer gets the better of the two; (5) never below zero; (6) every amount is rounded to two decimals once per line
/// (unit prices are rounded, line totals are unit price times quantity, discounts are rounded per unit), so cart, quote, order and invoice cannot drift.
/// </summary>
public static class PricingCalculator
{
    public static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private sealed class LineState(CalcLine line)
    {
        public CalcLine Line { get; } = line;

        public decimal Unit { get; set; } = line.BaseUnitPrice;

        public List<PriceAdjustment> Adjustments { get; } = [];

        public decimal OrderDiscount { get; set; }

        public decimal Total => Unit * Line.Quantity;
    }

    private sealed record Outcome(List<LineState> Lines, List<AppliedPromotion> Applied, bool AnyPromotion, bool AllAllowCoupon)
    {
        public decimal Subtotal => Lines.Sum(l => l.Total);

        public decimal OrderDiscount => Lines.Sum(l => l.OrderDiscount);

        public decimal Goods => Subtotal - OrderDiscount;
    }

    private sealed record Unit(int Line, int Ordinal, decimal Price)
    {
        public (int, int) Id => (Line, Ordinal);
    }

    private static (string En, string Bn) Label(CalcPromotion p) => (p.LabelEn, p.LabelBn);

    private static bool Matches(CalcLine line, IEnumerable<CalcTarget> targets) => Matches(line.ProductId, line.VariantId, line.CategoryPath, line.BrandId, targets);

    /// <summary>Whether a product (and variant) is in the target set: a product, variant, category (the category or one of its ancestors) or brand target, or Everything.</summary>
    public static bool Matches(Guid productId, Guid? variantId, IReadOnlySet<Guid> categoryPath, Guid? brandId, IEnumerable<CalcTarget> targets) =>
        targets.Any(t => t.Kind switch
        {
            PromotionTargetKind.Everything => true,
            PromotionTargetKind.Product => t.TargetId == productId,
            PromotionTargetKind.Variant => variantId is not null && t.TargetId == variantId,
            PromotionTargetKind.Category => t.TargetId is { } c && categoryPath.Contains(c),
            PromotionTargetKind.Brand => t.TargetId is not null && t.TargetId == brandId,
            _ => false,
        });

    private static IEnumerable<CalcPromotion> InOrder(IEnumerable<CalcPromotion> promotions) =>
        promotions.OrderByDescending(p => p.Priority).ThenBy(p => p.CreatedAt).ThenBy(p => p.Id);

    private static bool IsPriceType(CalcPromotion p) => p.Type is PromotionType.FlashSale or PromotionType.ProductDiscount || (p.Type == PromotionType.FestivalCampaign && p.Kind is not null);

    private static bool IsOrderType(CalcPromotion p) => p.Type is PromotionType.Bundle or PromotionType.BuyXGetY;

    private static bool Compatible(CalcPromotion a, CalcPromotion b) => a.StackMode != StackMode.Exclusive && b.StackMode != StackMode.Exclusive;

    /// <summary>The unit price a price-type promotion gives for a base price (rounded, never negative).</summary>
    public static decimal PriceFor(DiscountKind? kind, decimal value, decimal? maxDiscount, decimal basePrice)
    {
        decimal price;
        switch (kind)
        {
            case DiscountKind.Percent:
                var cut = Round(basePrice * value / 100m);
                if (maxDiscount is { } cap)
                {
                    cut = Math.Min(cut, cap);
                }

                price = basePrice - cut;
                break;
            case DiscountKind.Fixed:
                price = basePrice - value;
                break;
            case DiscountKind.FixedPrice:
                price = value;
                break;
            default:
                return basePrice;
        }

        return Math.Max(0m, Round(price));
    }

    // ---- Entry point --------------------------------------------------------------------------------------

    public static CalcOutput Calculate(CalcInput input)
    {
        var withPromotions = Run(input.Lines, input.Promotions, true);
        var chosen = withPromotions;
        CouponOutcome? coupon = null;
        decimal couponDiscount = 0m;
        var notesEn = new List<string>();
        var notesBn = new List<string>();

        if (input.Coupon is { } c)
        {
            var baseOnly = Run(input.Lines, input.Promotions, false);
            var okP = withPromotions.Goods >= c.MinOrderValue;
            var okB = baseOnly.Goods >= c.MinOrderValue;
            var discP = okP ? CouponDiscount(c, withPromotions.Goods) : 0m;
            var discB = okB ? CouponDiscount(c, baseOnly.Goods) : 0m;

            if (!okP && !okB)
            {
                coupon = new CouponOutcome(c.Code, c.Id, false, 0m, Shared.Results.ErrorCodes.Coupon.BelowMinimum, null, null);
            }
            else if (!withPromotions.AnyPromotion)
            {
                // Nothing from promotions to combine with: the coupon applies on the normal prices.
                chosen = withPromotions;
                couponDiscount = discP;
                coupon = new CouponOutcome(c.Code, c.Id, discP > 0m, discP, okP ? null : Shared.Results.ErrorCodes.Coupon.BelowMinimum, null, null);
            }
            else
            {
                var stackAllowed = c.StacksWithPromotions && withPromotions.AllAllowCoupon;
                var best = 1;
                var bestGoods = withPromotions.Goods;
                if (stackAllowed && okP && discP > 0m && withPromotions.Goods - discP < bestGoods)
                {
                    best = 2;
                    bestGoods = withPromotions.Goods - discP;
                }

                if (okB && discB > 0m && baseOnly.Goods - discB < bestGoods)
                {
                    best = 3;
                    bestGoods = baseOnly.Goods - discB;
                }

                switch (best)
                {
                    case 2:
                        couponDiscount = discP;
                        coupon = new CouponOutcome(c.Code, c.Id, true, discP, null, null, null);
                        notesEn.Add($"Coupon {c.Code} was applied on top of the promotions.");
                        notesBn.Add($"কুপন {c.Code} প্রমোশনের সঙ্গে যোগ হয়েছে।");
                        break;
                    case 3:
                        chosen = baseOnly;
                        couponDiscount = discB;
                        coupon = new CouponOutcome(c.Code, c.Id, true, discB, null, null, null);
                        notesEn.Add($"Coupon {c.Code} gives a better price than the promotions, so it was used instead of them (they cannot be combined).");
                        notesBn.Add($"কুপন {c.Code} প্রমোশনের চেয়ে ভালো দাম দিয়েছে, তাই প্রমোশনের বদলে কুপন প্রয়োগ হয়েছে (দুটি একসঙ্গে হয় না)।");
                        break;
                    default:
                        var why = c.StacksWithPromotions
                            ? "the promotions do not allow a coupon on top"
                            : "this coupon cannot be combined with promotions";
                        var whyBn = c.StacksWithPromotions
                            ? "প্রমোশনের সঙ্গে কুপন যোগ করা যায় না"
                            : "এই কুপন প্রমোশনের সঙ্গে যোগ হয় না";
                        coupon = new CouponOutcome(c.Code, c.Id, false, 0m, null,
                            $"The promotions already give a better price than coupon {c.Code}, and {why}, so the coupon was not applied.",
                            $"প্রমোশনেই কুপন {c.Code}-এর চেয়ে ভালো দাম পাওয়া গেছে এবং {whyBn}, তাই কুপন প্রয়োগ হয়নি।");
                        notesEn.Add(coupon.NotAppliedReasonEn!);
                        notesBn.Add(coupon.NotAppliedReasonBn!);
                        break;
                }
            }
        }

        return Assemble(chosen, coupon, couponDiscount, input.Promotions, notesEn, notesBn);
    }

    private static decimal CouponDiscount(CalcCoupon c, decimal goods)
    {
        var raw = c.Kind == CouponKind.Percent ? goods * c.Value / 100m : c.Value;
        if (c.Kind == CouponKind.Percent && c.MaxDiscount is { } cap)
        {
            raw = Math.Min(raw, cap);
        }

        return Round(Math.Min(Math.Max(raw, 0m), goods));
    }

    // ---- Promotions -------------------------------------------------------------------------------------

    private static Outcome Run(IReadOnlyList<CalcLine> lines, IReadOnlyList<CalcPromotion> promotions, bool include)
    {
        var states = lines.Select(l => new LineState(l)).ToList();
        foreach (var s in states)
        {
            if (s.Line.BaseUnitPrice < s.Line.ListPrice)
            {
                s.Adjustments.Add(new PriceAdjustment(s.Line.BaseSourceType, s.Line.BaseSourceId, s.Line.BaseLabelEn, s.Line.BaseLabelBn, (s.Line.ListPrice - s.Line.BaseUnitPrice) * s.Line.Quantity, "Line"));
            }
        }

        var applied = new List<AppliedPromotion>();
        if (!include)
        {
            return new Outcome(states, applied, false, true);
        }

        // Step 2: price-type promotions compete per line, the lowest unit price wins (ties: priority, then age).
        var cartAtBase = states.Sum(s => s.Line.BaseUnitPrice * s.Line.Quantity);
        var priceTypes = InOrder(promotions.Where(IsPriceType)).Where(p => cartAtBase >= p.MinCartValue).ToList();
        var priceWins = new Dictionary<Guid, decimal>();
        var allAllow = true;
        foreach (var s in states)
        {
            CalcPromotion? win = null;
            var best = s.Unit;
            foreach (var p in priceTypes)
            {
                if (!Matches(s.Line, p.Targets.Where(t => t.Role == TargetRole.Main)))
                {
                    continue;
                }

                var price = PriceFor(p.Kind, p.Value, p.MaxDiscount, s.Line.BaseUnitPrice);
                if (price < best)
                {
                    best = price;
                    win = p;
                }
            }

            if (win is not null)
            {
                var saving = (s.Line.BaseUnitPrice - best) * s.Line.Quantity;
                s.Unit = best;
                var (en, bn) = Label(win);
                s.Adjustments.Add(new PriceAdjustment("Promotion", win.Id, en, bn, saving, "Line"));
                if (s.Line.Quantity > 0)
                {
                    priceWins[win.Id] = priceWins.GetValueOrDefault(win.Id) + saving;
                    allAllow &= win.StackMode == StackMode.StacksWithCoupon;
                }
            }
        }

        foreach (var (id, saving) in priceWins)
        {
            var p = priceTypes.First(x => x.Id == id);
            applied.Add(new AppliedPromotion(p.Id, p.Type, p.NameEn, p.NameBn, p.LabelEn, p.LabelBn, saving, p.StackMode));
        }

        // Step 3: order-level promotions, in priority order; a conflict is settled in favour of the larger saving.
        var cartAfterLines = states.Sum(s => s.Total);
        var candidates = InOrder(promotions.Where(IsOrderType)).Where(p => cartAfterLines >= p.MinCartValue).ToList();
        var chosen = new List<CalcPromotion>();
        foreach (var p in candidates)
        {
            if (chosen.All(a => Compatible(a, p)))
            {
                var trial = InOrder(chosen.Append(p)).ToList();
                if (Evaluate(states, trial).DiscountOf(p.Id) > 0m)
                {
                    chosen = trial;
                }
            }
            else
            {
                var keep = chosen.Where(a => Compatible(a, p)).Append(p);
                var alternative = InOrder(keep).ToList();
                if (Evaluate(states, alternative).Total > Evaluate(states, chosen).Total)
                {
                    chosen = alternative;
                }
            }
        }

        var final = Evaluate(states, chosen);
        for (var i = 0; i < states.Count; i++)
        {
            states[i].OrderDiscount = final.PerLine[i];
        }

        foreach (var ((promoId, lineIndex), amount) in final.ByPromoLine.OrderBy(k => k.Key.Item2))
        {
            if (amount <= 0m)
            {
                continue;
            }

            var p = chosen.First(x => x.Id == promoId);
            var (en, bn) = Label(p);
            states[lineIndex].Adjustments.Add(new PriceAdjustment(p.Type == PromotionType.Bundle ? "Bundle" : "BuyXGetY", p.Id, en, bn, amount, "Order"));
        }

        foreach (var p in chosen)
        {
            var d = final.DiscountOf(p.Id);
            if (d > 0m)
            {
                applied.Add(new AppliedPromotion(p.Id, p.Type, p.NameEn, p.NameBn, p.LabelEn, p.LabelBn, d, p.StackMode));
                allAllow &= p.StackMode == StackMode.StacksWithCoupon;
            }
        }

        return new Outcome(states, applied, applied.Count > 0, allAllow);
    }

    private sealed class EvalResult
    {
        public required decimal[] PerLine { get; init; }

        public required Dictionary<(Guid, int), decimal> ByPromoLine { get; init; }

        public decimal Total => PerLine.Sum();

        public decimal DiscountOf(Guid promotionId) => ByPromoLine.Where(k => k.Key.Item1 == promotionId).Sum(k => k.Value);
    }

    /// <summary>Applies the promotions in the given order on the lines' current unit prices. Each promotion uses up units of its own kind; no line is ever discounted below zero.</summary>
    private static EvalResult Evaluate(List<LineState> states, IReadOnlyList<CalcPromotion> ordered)
    {
        var remaining = states.Select(s => s.Total).ToArray();
        var consumedBxgy = new int[states.Count];
        var consumedBundle = new int[states.Count];
        var perLine = new decimal[states.Count];
        var byPromoLine = new Dictionary<(Guid, int), decimal>();

        decimal Give(Guid promotionId, int line, decimal amount)
        {
            var given = Math.Min(Math.Max(amount, 0m), remaining[line]);
            remaining[line] -= given;
            perLine[line] += given;
            byPromoLine[(promotionId, line)] = byPromoLine.GetValueOrDefault((promotionId, line)) + given;
            return given;
        }

        List<Unit> Units(IEnumerable<CalcTarget> targets, int[] consumed)
        {
            var t = targets.ToList();
            var units = new List<Unit>();
            for (var i = 0; i < states.Count; i++)
            {
                if (!Matches(states[i].Line, t))
                {
                    continue;
                }

                for (var k = consumed[i]; k < states[i].Line.Quantity; k++)
                {
                    units.Add(new Unit(i, k, states[i].Unit));
                }
            }

            return units;
        }

        foreach (var p in ordered)
        {
            if (p.Type == PromotionType.BuyXGetY)
            {
                BuyXGetY(p);
            }
            else if (p.Type == PromotionType.Bundle)
            {
                Bundle(p);
            }
        }

        return new EvalResult { PerLine = perLine, ByPromoLine = byPromoLine };

        void BuyXGetY(CalcPromotion p)
        {
            var buyTargets = p.Targets.Where(t => t.Role == TargetRole.Buy).ToList();
            var getTargets = p.Targets.Where(t => t.Role == TargetRole.Get).ToList();
            var x = p.BuyQuantity;
            var y = p.GetQuantity;
            if (buyTargets.Count == 0 || x < 1 || y < 1)
            {
                return;
            }

            List<Unit> free;
            List<Unit> used;
            if (getTargets.Count == 0)
            {
                var units = Units(buyTargets, consumedBxgy).OrderBy(u => u.Price).ThenBy(u => u.Line).ThenBy(u => u.Ordinal).ToList();
                var groups = units.Count / (x + y);
                if (groups == 0)
                {
                    return;
                }

                used = [.. units.Take(groups * (x + y))];
                free = [.. used.Take(groups * y)];
            }
            else
            {
                var buy = Units(buyTargets, consumedBxgy);
                var get = Units(getTargets, consumedBxgy).OrderBy(u => u.Price).ThenBy(u => u.Line).ThenBy(u => u.Ordinal).ToList();
                free = [];
                used = [];
                for (var g = Math.Min(buy.Count / x, get.Count / y); g >= 1; g--)
                {
                    var candidateFree = get.Take(g * y).ToList();
                    var freeIds = candidateFree.Select(u => u.Id).ToHashSet();
                    var buyAvailable = buy.Where(u => !freeIds.Contains(u.Id)).OrderBy(u => u.Price).ThenBy(u => u.Line).ThenBy(u => u.Ordinal).ToList();
                    if (buyAvailable.Count >= g * x)
                    {
                        free = candidateFree;
                        used = [.. buyAvailable.Take(g * x), .. candidateFree];
                        break;
                    }
                }

                if (free.Count == 0)
                {
                    return;
                }
            }

            foreach (var u in used)
            {
                consumedBxgy[u.Line]++;
            }

            foreach (var u in free)
            {
                Give(p.Id, u.Line, Round(u.Price * p.GetPercent / 100m));
            }
        }

        void Bundle(CalcPromotion p)
        {
            var items = p.Targets.Where(t => t.Role == TargetRole.BundleItem).ToList();
            if (items.Count == 0 || p.BundlePrice < 0m)
            {
                return;
            }

            for (var guard = 0; guard < 1000; guard++)
            {
                var picked = new List<Unit>();
                var pickedIds = new HashSet<(int, int)>();
                var complete = true;
                foreach (var item in items)
                {
                    var take = Units([item], consumedBundle).Where(u => !pickedIds.Contains(u.Id)).OrderBy(u => u.Price).ThenBy(u => u.Line).ThenBy(u => u.Ordinal).Take(item.Quantity).ToList();
                    if (take.Count < item.Quantity)
                    {
                        complete = false;
                        break;
                    }

                    picked.AddRange(take);
                    foreach (var u in take)
                    {
                        pickedIds.Add(u.Id);
                    }
                }

                if (!complete)
                {
                    return;
                }

                var sum = picked.Sum(u => u.Price);
                var discount = sum - p.BundlePrice;
                if (discount <= 0m || sum <= 0m)
                {
                    return;
                }

                var allocated = 0m;
                for (var n = 0; n < picked.Count; n++)
                {
                    var share = n == picked.Count - 1 ? discount - allocated : Round(discount * picked[n].Price / sum);
                    allocated += share;
                    Give(p.Id, picked[n].Line, share);
                    consumedBundle[picked[n].Line]++;
                }
            }
        }
    }

    // ---- Result -----------------------------------------------------------------------------------------

    private static CalcOutput Assemble(Outcome o, CouponOutcome? coupon, decimal couponDiscount, IReadOnlyList<CalcPromotion> all, List<string> notesEn, List<string> notesBn)
    {
        var lines = o.Lines.Select(s => new PricedLine(s.Line.VariantId, s.Line.ProductId, s.Line.Quantity, s.Line.ListPrice, s.Line.BaseUnitPrice, s.Unit, s.Total, s.OrderDiscount, s.Total - s.OrderDiscount, s.Adjustments)).ToList();
        var listSubtotal = o.Lines.Sum(s => s.Line.ListPrice * s.Line.Quantity);
        var subtotal = o.Subtotal;
        var promo = o.OrderDiscount;
        var goods = subtotal - promo - couponDiscount;
        var totals = new PricingTotals(listSubtotal, subtotal, promo, couponDiscount, goods);

        var offers = InOrder(all.Where(p => p.Type == PromotionType.FreeDelivery)).Select(p =>
            new FreeDeliveryOffer(p.Id, p.LabelEn, p.LabelBn, p.MinCartValue, p.ZoneCodes, goods >= p.MinCartValue, Math.Max(0m, p.MinCartValue - goods))).ToList();
        foreach (var offer in offers.Where(x => x.Qualifies))
        {
            notesEn.Add($"Free delivery: {offer.LabelEn}.");
            notesBn.Add($"ফ্রি ডেলিভারি: {offer.LabelBn}।");
        }

        var reasons = new List<ExplainedReason>();
        var sale = o.Lines.Sum(s => s.Adjustments.Where(a => a.SourceType != "Promotion" && a.Level == "Line").Sum(a => a.Amount));
        if (sale > 0m)
        {
            var first = o.Lines.SelectMany(s => s.Adjustments).First(a => a.SourceType != "Promotion" && a.Level == "Line");
            reasons.Add(new ExplainedReason(first.SourceType, first.SourceId, first.LabelEn, first.LabelBn, sale));
        }

        foreach (var g in o.Lines.SelectMany(s => s.Adjustments).Where(a => a.SourceId is not null && (a.SourceType is "Promotion" or "Bundle" or "BuyXGetY")).GroupBy(a => a.SourceId!.Value))
        {
            var a = g.First();
            reasons.Add(new ExplainedReason(a.SourceType, a.SourceId, a.LabelEn, a.LabelBn, g.Sum(x => x.Amount)));
        }

        if (coupon is { Applied: true })
        {
            reasons.Add(new ExplainedReason("Coupon", coupon.CouponId, $"Coupon {coupon.Code}", $"কুপন {coupon.Code}", coupon.Discount));
        }

        var explained = o.Lines.Select(s =>
        {
            var adj = s.Adjustments.OrderBy(a => a.Level).ToList();
            var saving = s.Line.ListPrice * s.Line.Quantity - (s.Total - s.OrderDiscount);
            return new ExplainedLine(s.Line.VariantId, s.Line.ListPrice, s.Unit, s.Line.Quantity, s.Line.ListPrice * s.Line.Quantity, s.Total - s.OrderDiscount, saving,
                [.. adj.Select(a => a.LabelEn).Distinct()], [.. adj.Select(a => a.LabelBn).Distinct()]);
        }).ToList();

        var explanation = new PriceExplanation("BDT", listSubtotal, goods, listSubtotal - goods, reasons, explained, notesEn, notesBn);
        return new CalcOutput(lines, totals, o.Applied, coupon, offers, explanation);
    }
}
