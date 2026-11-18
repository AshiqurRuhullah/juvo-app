using Juvo.Domain.Promotions;
using Juvo.Shared.Results;

namespace Juvo.Application.Pricing;

/// <summary>Where the cart is priced. The numeric values are the <see cref="PromotionChannels"/> bits, so a promotion can be limited to channels.</summary>
public enum PricingChannel
{
    Web = 1,
    SocialDesk = 2,
    Pos = 4,
}

public sealed record PricingLineRequest(Guid VariantId, int Quantity);

public sealed record PricingCustomer(Guid? CustomerId, string? Mobile);

/// <summary>
/// Everything the engine needs to price a cart. <c>At</c> defaults to the clock. <c>PriceSourceHints</c> go to the <see cref="IBasePriceSource"/> implementations (a reseller price
/// list reads its own hint, for example <c>reseller:&lt;id&gt;</c>). <c>PromotionSet</c> is for previews: exactly these promotions are evaluated as if they were running now (schedule,
/// switch and archive flag are ignored); null means the promotions that are running at <c>At</c>. <c>RequireSellable</c> refuses variants that cannot be sold (placement); the cart
/// passes false so it can show them as unavailable.
/// </summary>
public sealed record PricingRequest(
    IReadOnlyList<PricingLineRequest> Lines,
    PricingCustomer? Customer = null,
    IReadOnlyList<string>? CouponCodes = null,
    PricingChannel Channel = PricingChannel.Web,
    DateTimeOffset? At = null,
    IReadOnlyCollection<string>? PriceSourceHints = null,
    string? ZoneCode = null,
    IReadOnlyCollection<Guid>? PromotionSet = null,
    bool RequireSellable = false);

/// <summary>One saving or price reason. <c>Amount</c> is positive and covers the whole line. <c>SourceType</c>: Sale, Promotion, Bundle, BuyXGetY (and later Reseller). <c>Level</c>: Line (lowered the unit price) or Order (shared out from an order-level promotion).</summary>
public sealed record PriceAdjustment(string SourceType, Guid? SourceId, string LabelEn, string LabelBn, decimal Amount, string Level);

/// <summary>
/// One priced line. <c>ListPrice</c> is the struck-through price, <c>BaseUnitPrice</c> the price before promotions (sale price or a price list), <c>UnitPrice</c> the price after the
/// line-level promotion (what the order line stores). <c>LineTotal</c> is <c>UnitPrice * Quantity</c>; <c>OrderDiscount</c> is the line's share of bundle and buy-X-get-Y savings;
/// <c>NetTotal</c> is <c>LineTotal - OrderDiscount</c> (before any coupon).
/// </summary>
public sealed record PricedLine(
    Guid VariantId, Guid ProductId, int Quantity, decimal ListPrice, decimal BaseUnitPrice, decimal UnitPrice, decimal LineTotal, decimal OrderDiscount, decimal NetTotal,
    IReadOnlyList<PriceAdjustment> Adjustments);

public sealed record AppliedPromotion(Guid Id, PromotionType Type, string NameEn, string NameBn, string LabelEn, string LabelBn, decimal Discount, StackMode StackMode);

/// <summary>What happened to the coupon. <c>Problem</c> is a coupon error code (for example COUPON.EXPIRED); <c>NotAppliedReasonEn/Bn</c> explain a valid coupon that was not used because promotions gave a better price.</summary>
public sealed record CouponOutcome(string Code, Guid? CouponId, bool Applied, decimal Discount, string? Problem, string? NotAppliedReasonEn, string? NotAppliedReasonBn);

/// <summary><c>GoodsTotal</c> = <c>Subtotal - PromotionDiscount - CouponDiscount</c>. Delivery and manual discounts are added by the caller.</summary>
public sealed record PricingTotals(decimal ListSubtotal, decimal Subtotal, decimal PromotionDiscount, decimal CouponDiscount, decimal GoodsTotal);

/// <summary>A free-delivery promotion. <c>Qualifies</c>: the goods value reaches its threshold. <c>ZoneCodes</c> empty means every zone.</summary>
public sealed record FreeDeliveryOffer(Guid PromotionId, string LabelEn, string LabelBn, decimal MinCartValue, IReadOnlyList<string> ZoneCodes, bool Qualifies, decimal AmountToQualify);

public sealed record ExplainedReason(string SourceType, Guid? SourceId, string LabelEn, string LabelBn, decimal Amount);

public sealed record ExplainedLine(Guid VariantId, decimal ListPrice, decimal UnitPrice, int Quantity, decimal ListTotal, decimal FinalTotal, decimal Saving, IReadOnlyList<string> ReasonsEn, IReadOnlyList<string> ReasonsBn);

/// <summary>The price story in one glance: struck-through list total, final price, savings and short reasons, in both languages.</summary>
public sealed record PriceExplanation(
    string Currency, decimal ListTotal, decimal FinalTotal, decimal Savings, IReadOnlyList<ExplainedReason> Reasons, IReadOnlyList<ExplainedLine> Lines, IReadOnlyList<string> NotesEn, IReadOnlyList<string> NotesBn);

public sealed record PricingResult(
    IReadOnlyList<PricedLine> Lines, PricingTotals Totals, IReadOnlyList<AppliedPromotion> Promotions, CouponOutcome? Coupon, IReadOnlyList<FreeDeliveryOffer> FreeDelivery,
    PriceExplanation Explanation, Error? CouponError)
{
    /// <summary>The free-delivery offer that applies to the zone (a zone-limited offer needs the zone code), or null.</summary>
    public FreeDeliveryOffer? FreeDeliveryFor(string? zoneCode) =>
        FreeDelivery.Where(o => o.Qualifies && (o.ZoneCodes.Count == 0 || (zoneCode is not null && o.ZoneCodes.Contains(zoneCode, StringComparer.OrdinalIgnoreCase)))).OrderBy(o => o.MinCartValue).FirstOrDefault();

    /// <summary>The line adjustments as the order stores them: the line-level ones, then the shared order-level ones.</summary>
    public IReadOnlyList<PriceAdjustment> AdjustmentsOf(Guid variantId) => Lines.FirstOrDefault(l => l.VariantId == variantId)?.Adjustments ?? [];
}

/// <summary>The one place that prices a cart (cart, checkout quote, placement, manual orders, invoices, previews, later POS and reseller).</summary>
public interface IPricingEngine
{
    Task<Result<PricingResult>> PriceAsync(PricingRequest request, CancellationToken cancellationToken);
}

/// <summary>Facts about a variant a price source needs.</summary>
public sealed record VariantPriceFacts(Guid VariantId, Guid ProductId, Guid CategoryId, Guid? BrandId, decimal RegularPrice, decimal? SalePrice, DateTimeOffset? SaleStart, DateTimeOffset? SaleEnd, decimal? PriceOverride);

/// <summary>A base price: <c>ListPrice</c> is what is struck through, <c>UnitPrice</c> what is charged before promotions.</summary>
public sealed record BasePrice(decimal ListPrice, decimal UnitPrice, string SourceType, Guid? SourceId, string LabelEn, string LabelBn);

public sealed record BasePriceRequest(IReadOnlyList<VariantPriceFacts> Variants, PricingCustomer? Customer, DateTimeOffset Now, PricingChannel Channel, IReadOnlyCollection<string> Hints);

/// <summary>
/// Where base prices come from. The catalog source (<c>Order = 0</c>) answers for every variant. A later source (reseller price list) may answer for some variants and, when it does, its
/// price replaces the one of the earlier sources: sources run in ascending <c>Order</c>, the last answer wins. Promotions then compete on top of the winning base price.
/// </summary>
public interface IBasePriceSource
{
    int Order { get; }

    Task<IReadOnlyDictionary<Guid, BasePrice>> PricesAsync(BasePriceRequest request, CancellationToken cancellationToken);
}
