using Juvo.Domain.Common;

namespace Juvo.Domain.Promotions;

public enum PromotionType
{
    FlashSale = 1,
    ProductDiscount = 2,
    Bundle = 3,
    BuyXGetY = 4,
    FreeDelivery = 5,
    FestivalCampaign = 6,
}

/// <summary>Derived from the manual switch, the archive flag and the schedule (see <see cref="Promotion.StatusAt"/>); never stored.</summary>
public enum PromotionStatus
{
    Draft = 1,
    Scheduled = 2,
    Active = 3,
    Ended = 4,
    Archived = 5,
}

/// <summary>
/// How far a promotion combines with others. <c>Exclusive</c>: alone (no other order-level promotion, no coupon). <c>StacksWithOrderPromotions</c>: may combine with other
/// order-level promotions that allow it, not with a coupon. <c>StacksWithCoupon</c>: may combine with order-level promotions and with a coupon that allows it.
/// </summary>
public enum StackMode
{
    Exclusive = 1,
    StacksWithOrderPromotions = 2,
    StacksWithCoupon = 3,
}

/// <summary><c>Percent</c> off the unit price, <c>Fixed</c> amount off each unit, <c>FixedPrice</c> the unit costs exactly this (flash price).</summary>
public enum DiscountKind
{
    Percent = 1,
    Fixed = 2,
    FixedPrice = 3,
}

public enum PromotionTargetKind
{
    Product = 1,
    Variant = 2,
    Category = 3,
    Brand = 4,
    Everything = 5,
}

/// <summary>What a target row means for the promotion type: the discounted set, the buy set or get set of a BuyXGetY, an item of a bundle, or the product set of a campaign page.</summary>
public enum TargetRole
{
    Main = 1,
    Buy = 2,
    Get = 3,
    BundleItem = 4,
}

public enum BannerSlot
{
    None = 0,
    HomeTop = 1,
    ShopTop = 2,
    Category = 3,
}

/// <summary>Where the promotion applies. Stored as a bit set.</summary>
[Flags]
public enum PromotionChannels
{
    None = 0,
    Web = 1,
    SocialDesk = 2,
    Pos = 4,
    All = Web | SocialDesk | Pos,
}

/// <summary>One product, variant, category or brand a promotion points at. <c>Quantity</c> is the units a bundle item needs.</summary>
public sealed class PromotionTarget : Entity, INotAudited
{
    private PromotionTarget()
    {
    }

    public PromotionTarget(Guid promotionId, PromotionTargetKind kind, Guid? targetId, TargetRole role, int quantity)
    {
        PromotionId = promotionId;
        Kind = kind;
        TargetId = targetId;
        Role = role;
        Quantity = Math.Max(1, quantity);
    }

    public Guid PromotionId { get; private set; }

    public PromotionTargetKind Kind { get; private set; }

    /// <summary>Null for <see cref="PromotionTargetKind.Everything"/>.</summary>
    public Guid? TargetId { get; private set; }

    public TargetRole Role { get; private set; }

    public int Quantity { get; private set; } = 1;
}

/// <summary>The values staff edit on a promotion. The service validates; the entity only stores.</summary>
public sealed record PromotionDetails(
    string NameEn, string? NameBn, PromotionType Type, DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int Priority, StackMode StackMode, PromotionChannels Channels, decimal? MinCartValue,
    int? UsageLimit, DiscountKind? DiscountKind, decimal? DiscountValue, decimal? MaxDiscount, int BuyQuantity, int GetQuantity, decimal GetDiscountPercent, decimal? BundlePrice,
    string? ZoneCodes, string? LabelEn, string? LabelBn, string? Slug, string? TitleEn, string? TitleBn, string? BodyEn, string? BodyBn, string? HeroImagePath, string? BannerImagePath,
    BannerSlot BannerSlot, Guid? BannerCategoryId);

/// <summary>
/// A promotion of module Z. Status is derived: the manual switch, the archive flag and the schedule (Asia/Dhaka local times are converted to UTC offsets by the caller) decide
/// whether it is Draft, Scheduled, Active, Ended or Archived. Archived promotions are kept for the reports and the order lines that name them; nothing is erased.
/// </summary>
public sealed class Promotion : Entity
{
    private readonly List<PromotionTarget> _targets = [];

    private Promotion()
    {
    }

    public Promotion(PromotionDetails details) => Apply(details);

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public PromotionType Type { get; private set; }

    /// <summary>The manual switch. A promotion that was never activated is a Draft.</summary>
    public bool IsEnabled { get; private set; }

    public bool IsArchived { get; private set; }

    public DateTimeOffset? StartsAt { get; private set; }

    public DateTimeOffset? EndsAt { get; private set; }

    /// <summary>Higher wins ties and is evaluated first.</summary>
    public int Priority { get; private set; }

    public StackMode StackMode { get; private set; } = StackMode.StacksWithOrderPromotions;

    public PromotionChannels Channels { get; private set; } = PromotionChannels.All;

    public decimal? MinCartValue { get; private set; }

    public int? UsageLimit { get; private set; }

    public int UsedCount { get; private set; }

    public DiscountKind? DiscountKind { get; private set; }

    public decimal? DiscountValue { get; private set; }

    /// <summary>Cap of a percentage discount per unit.</summary>
    public decimal? MaxDiscount { get; private set; }

    public int BuyQuantity { get; private set; }

    public int GetQuantity { get; private set; }

    /// <summary>Percent off the "get" units of a BuyXGetY (100 = free).</summary>
    public decimal GetDiscountPercent { get; private set; } = 100m;

    public decimal? BundlePrice { get; private set; }

    /// <summary>Comma separated delivery zone codes a free-delivery promotion is limited to; empty means every zone.</summary>
    public string? ZoneCodes { get; private set; }

    /// <summary>Short customer label ("Eid sale"), shown next to the price. Defaults to the name.</summary>
    public string? LabelEn { get; private set; }

    public string? LabelBn { get; private set; }

    // ---- Festival campaign page ----
    public string? Slug { get; private set; }

    public string? TitleEn { get; private set; }

    public string? TitleBn { get; private set; }

    public string? BodyEn { get; private set; }

    public string? BodyBn { get; private set; }

    public string? HeroImagePath { get; private set; }

    public string? BannerImagePath { get; private set; }

    public BannerSlot BannerSlot { get; private set; }

    public Guid? BannerCategoryId { get; private set; }

    public IReadOnlyList<PromotionTarget> Targets => _targets;

    /// <summary>True for the types that change a unit price (they compete: the lowest price wins).</summary>
    public bool IsPriceType => Type is PromotionType.FlashSale or PromotionType.ProductDiscount || (Type == PromotionType.FestivalCampaign && DiscountKind is not null);

    public bool IsOrderType => Type is PromotionType.Bundle or PromotionType.BuyXGetY;

    public PromotionStatus StatusAt(DateTimeOffset now)
    {
        if (IsArchived)
        {
            return PromotionStatus.Archived;
        }

        if (!IsEnabled)
        {
            return PromotionStatus.Draft;
        }

        if (StartsAt is { } start && now < start)
        {
            return PromotionStatus.Scheduled;
        }

        return EndsAt is { } end && now > end ? PromotionStatus.Ended : PromotionStatus.Active;
    }

    /// <summary>The window includes both ends: a flash sale that ends at 18:00:00 still prices at 18:00:00 and stops after.</summary>
    public bool IsRunning(DateTimeOffset now) => StatusAt(now) == PromotionStatus.Active;

    public bool AppliesTo(PromotionChannels channel) => (Channels & channel) != 0;

    public void Update(PromotionDetails details) => Apply(details);

    public void Enable() => IsEnabled = true;

    public void Disable() => IsEnabled = false;

    public void Archive()
    {
        IsArchived = true;
        IsEnabled = false;
    }

    public void Unarchive() => IsArchived = false;

    public void SetImages(string? heroPath, string? bannerPath)
    {
        HeroImagePath = heroPath;
        BannerImagePath = bannerPath;
    }

    /// <summary>Replaces the target rows. Returns the new rows so the caller can add them to the context.</summary>
    public IReadOnlyList<PromotionTarget> ReplaceTargets(IEnumerable<(PromotionTargetKind Kind, Guid? TargetId, TargetRole Role, int Quantity)> targets)
    {
        _targets.Clear();
        _targets.AddRange(targets.Select(t => new PromotionTarget(Id, t.Kind, t.TargetId, t.Role, t.Quantity)));
        return _targets;
    }

    private void Apply(PromotionDetails d)
    {
        NameEn = d.NameEn.Trim();
        NameBn = Clean(d.NameBn);
        Type = d.Type;
        StartsAt = d.StartsAt;
        EndsAt = d.EndsAt;
        Priority = d.Priority;
        StackMode = d.StackMode;
        Channels = d.Channels;
        MinCartValue = d.MinCartValue;
        UsageLimit = d.UsageLimit;
        DiscountKind = d.DiscountKind;
        DiscountValue = d.DiscountValue;
        MaxDiscount = d.MaxDiscount;
        BuyQuantity = d.BuyQuantity;
        GetQuantity = d.GetQuantity;
        GetDiscountPercent = d.GetDiscountPercent;
        BundlePrice = d.BundlePrice;
        ZoneCodes = Clean(d.ZoneCodes);
        LabelEn = Clean(d.LabelEn);
        LabelBn = Clean(d.LabelBn);
        Slug = Clean(d.Slug)?.ToLowerInvariant();
        TitleEn = Clean(d.TitleEn);
        TitleBn = Clean(d.TitleBn);
        BodyEn = Clean(d.BodyEn);
        BodyBn = Clean(d.BodyBn);
        HeroImagePath = Clean(d.HeroImagePath) ?? HeroImagePath;
        BannerImagePath = Clean(d.BannerImagePath) ?? BannerImagePath;
        BannerSlot = d.BannerSlot;
        BannerCategoryId = d.BannerCategoryId;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
