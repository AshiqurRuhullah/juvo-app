using Juvo.Application.Pricing;
using Juvo.Domain.Promotions;

namespace Juvo.Application.Promotions;

/// <summary>One target row. <c>Role</c>: Main (the discounted set, a campaign's product set), Buy or Get (BuyXGetY), BundleItem (with <c>Quantity</c> units). <c>TargetId</c> is empty for Everything.</summary>
public sealed record PromotionTargetInput(PromotionTargetKind Kind, Guid? TargetId, TargetRole Role = TargetRole.Main, int Quantity = 1);

/// <summary>
/// Create or change a promotion. Times carry their UTC offset (Asia/Dhaka is +06:00); answers always come back in Dhaka time. Which fields count depends on <c>Type</c>: FlashSale and
/// ProductDiscount use DiscountKind/Value/MaxDiscount and Main targets; Bundle uses BundlePrice and BundleItem targets; BuyXGetY uses BuyQuantity, GetQuantity, GetDiscountPercent and Buy (and
/// optionally Get) targets; FreeDelivery uses MinCartValue and ZoneCodes; FestivalCampaign uses Slug, titles, body, banner fields and Main targets (and may add a DiscountKind/Value).
/// </summary>
public sealed record SavePromotionRequest(
    string NameEn, string? NameBn, PromotionType Type, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int Priority = 0, StackMode StackMode = StackMode.StacksWithOrderPromotions,
    PromotionChannels? Channels = null, decimal? MinCartValue = null, int? UsageLimit = null, DiscountKind? DiscountKind = null, decimal? DiscountValue = null, decimal? MaxDiscount = null,
    int BuyQuantity = 0, int GetQuantity = 0, decimal GetDiscountPercent = 100m, decimal? BundlePrice = null, IReadOnlyList<string>? ZoneCodes = null, string? LabelEn = null, string? LabelBn = null,
    string? Slug = null, string? TitleEn = null, string? TitleBn = null, string? BodyEn = null, string? BodyBn = null, BannerSlot BannerSlot = BannerSlot.None, Guid? BannerCategoryId = null,
    IReadOnlyList<PromotionTargetInput>? Targets = null);

public sealed record PromotionTargetDto(Guid Id, PromotionTargetKind Kind, Guid? TargetId, TargetRole Role, int Quantity, string? Name);

public sealed record PromotionListItemDto(
    Guid Id, string NameEn, string? NameBn, PromotionType Type, PromotionStatus Status, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int Priority, StackMode StackMode, int UsedCount, int? UsageLimit,
    string? Slug, DateTimeOffset CreatedAt);

public sealed record PromotionDetailDto(
    Guid Id, string NameEn, string? NameBn, PromotionType Type, PromotionStatus Status, bool IsEnabled, bool IsArchived, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int Priority, StackMode StackMode,
    PromotionChannels Channels, decimal? MinCartValue, int? UsageLimit, int UsedCount, DiscountKind? DiscountKind, decimal? DiscountValue, decimal? MaxDiscount, int BuyQuantity, int GetQuantity,
    decimal GetDiscountPercent, decimal? BundlePrice, IReadOnlyList<string> ZoneCodes, string? LabelEn, string? LabelBn, string? Slug, string? TitleEn, string? TitleBn, string? BodyEn, string? BodyBn,
    string? HeroImageUrl, string? BannerImageUrl, BannerSlot BannerSlot, Guid? BannerCategoryId, IReadOnlyList<PromotionTargetDto> Targets, DateTimeOffset CreatedAt);

/// <summary><c>Status</c> filters by the derived status; <c>LiveNow</c> true keeps only promotions that are running at this moment.</summary>
public sealed record PromotionListQuery(PromotionType? Type, PromotionStatus? Status, bool? LiveNow);

public sealed record PreviewLine(Guid? VariantId, string? Sku, int Quantity);

/// <summary>Prices a sample cart: <c>PromotionIds</c> chosen set evaluated as if running (draft ones too); omitted means the promotions running at <c>At</c> (default now).</summary>
public sealed record PreviewRequest(IReadOnlyList<PreviewLine> Lines, string? CouponCode, IReadOnlyList<Guid>? PromotionIds, DateTimeOffset? At, PricingChannel? Channel);

public sealed record PreviewResultDto(
    IReadOnlyList<PricedLine> Lines, PricingTotals Totals, IReadOnlyList<AppliedPromotion> Promotions, CouponOutcome? Coupon, IReadOnlyList<FreeDeliveryOffer> FreeDelivery, PriceExplanation Explanation,
    string? CouponErrorCode, string? CouponErrorMessage);

public sealed record PromotionReportQuery(DateTimeOffset? From, DateTimeOffset? To, PromotionType? Type);

/// <summary>Numbers of one promotion from the order line breakdown (cancelled orders left out): orders and units that carried it, revenue of those lines after promotion discounts, and what the promotion cost.</summary>
public sealed record PromotionReportDto(Guid PromotionId, string NameEn, PromotionType Type, PromotionStatus Status, int Orders, int Units, decimal Revenue, decimal DiscountCost);

// ---- Storefront ------------------------------------------------------------------------------------------------

public sealed record StoreCampaignDto(
    string Slug, string? TitleEn, string? TitleBn, string? BodyEn, string? BodyBn, string? HeroImageUrl, string? BannerImageUrl, string LabelEn, string LabelBn, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt,
    IReadOnlyList<Storefront.StoreProductCard> Products);

public sealed record StoreBannerDto(Guid PromotionId, BannerSlot Slot, string ImageUrl, string? TitleEn, string? TitleBn, string? Slug, string LabelEn, string LabelBn, DateTimeOffset? EndsAt);

/// <summary>What a product card or detail shows for a promotion: the price now, the label in both languages and when it ends (for a countdown).</summary>
public sealed record StorePromotionInfo(Guid PromotionId, PromotionType Type, decimal Price, decimal ListPrice, string LabelEn, string LabelBn, DateTimeOffset? EndsAt);
