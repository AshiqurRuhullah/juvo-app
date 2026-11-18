using Juvo.Application.Abstractions;
using Juvo.Application.Pricing;
using Juvo.Application.Storefront;
using Juvo.Domain.Catalog;
using Juvo.Domain.Promotions;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Promotions;

/// <summary>
/// The running price promotions as the shop windows need them (cards and product pages). Only promotions without a minimum cart value are shown on a card: a condition on the whole cart cannot be
/// decided for one product. The cart and checkout use the pricing engine, which applies every promotion; for the ones shown here both give the same unit price.
/// </summary>
public sealed class PromotionSet(IReadOnlyList<Promotion> promotions)
{
    public static readonly PromotionSet Empty = new([]);

    public bool Any => promotions.Count > 0;

    /// <summary>The lowest unit price a running promotion gives for the product (or variant), or null when none lowers <paramref name="basePrice"/>. Ties: priority, then age.</summary>
    public StorePromotionInfo? Best(Product product, ProductVariant? variant, IReadOnlySet<Guid> categoryPath, decimal basePrice, decimal listPrice)
    {
        Promotion? winner = null;
        var best = basePrice;
        foreach (var p in promotions.OrderByDescending(x => x.Priority).ThenBy(x => x.CreatedAt).ThenBy(x => x.Id))
        {
            var targets = p.Targets.Where(t => t.Role == TargetRole.Main && (variant is not null || t.Kind != PromotionTargetKind.Variant)).Select(t => new CalcTarget(t.Kind, t.TargetId, t.Role, t.Quantity));
            if (!PricingCalculator.Matches(product.Id, variant?.Id, categoryPath, product.BrandId, targets))
            {
                continue;
            }

            var price = PricingCalculator.PriceFor(p.DiscountKind, p.DiscountValue ?? 0m, p.MaxDiscount, basePrice);
            if (price < best)
            {
                best = price;
                winner = p;
            }
        }

        return winner is null
            ? null
            : new StorePromotionInfo(winner.Id, winner.Type, best, listPrice, winner.LabelEn ?? winner.NameEn, winner.LabelBn ?? winner.NameBn ?? winner.LabelEn ?? winner.NameEn, winner.EndsAt?.ToOffset(TimeSpan.FromHours(6)));
    }
}

public sealed class PromotionCardPricing(IApplicationDbContext db)
{
    public async Task<PromotionSet> LoadAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var rows = await db.Promotions.AsNoTracking().Include(p => p.Targets)
            .Where(p => p.IsEnabled && !p.IsArchived && (p.StartsAt == null || p.StartsAt <= now) && (p.EndsAt == null || p.EndsAt >= now) && (p.UsageLimit == null || p.UsedCount < p.UsageLimit) &&
                        (p.Channels & PromotionChannels.Web) != 0 && (p.MinCartValue == null || p.MinCartValue == 0m) &&
                        (p.Type == PromotionType.FlashSale || p.Type == PromotionType.ProductDiscount || (p.Type == PromotionType.FestivalCampaign && p.DiscountKind != null)))
            .ToListAsync(cancellationToken);
        return rows.Count == 0 ? PromotionSet.Empty : new PromotionSet(rows);
    }
}

/// <summary>Campaign pages, banners, free-delivery offers and the cache boundary for the public shop (module Z).</summary>
public sealed class StorefrontPromotionsService(IApplicationDbContext db, IFileStorage storage, StorefrontCatalogService catalog, TimeProvider clock)
{
    private const int CampaignProducts = 60;

    private static readonly TimeSpan Dhaka = TimeSpan.FromHours(6);

    private IQueryable<Promotion> Running(DateTimeOffset now) =>
        db.Promotions.AsNoTracking().Where(p => p.IsEnabled && !p.IsArchived && (p.StartsAt == null || p.StartsAt <= now) && (p.EndsAt == null || p.EndsAt >= now) && (p.Channels & PromotionChannels.Web) != 0);

    public async Task<Result<StoreCampaignDto>> CampaignAsync(string slug, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var normalized = slug.Trim().ToLowerInvariant();
        var promotion = await Running(now).Include(p => p.Targets).FirstOrDefaultAsync(p => p.Type == PromotionType.FestivalCampaign && p.Slug == normalized, cancellationToken);
        if (promotion is null)
        {
            return Error.NotFound(ErrorCodes.Promo.CampaignNotFound, "This campaign is not running.");
        }

        var ids = await ProductIdsAsync(promotion.Targets.Where(t => t.Role == TargetRole.Main).ToList(), cancellationToken);
        var cards = await catalog.CardsByIdsAsync(ids, CampaignProducts, cancellationToken);
        var labelEn = promotion.LabelEn ?? promotion.NameEn;
        return new StoreCampaignDto(promotion.Slug!, promotion.TitleEn, promotion.TitleBn, promotion.BodyEn, promotion.BodyBn,
            promotion.HeroImagePath is null ? null : storage.GetPublicUrl(promotion.HeroImagePath), promotion.BannerImagePath is null ? null : storage.GetPublicUrl(promotion.BannerImagePath),
            labelEn, promotion.LabelBn ?? promotion.NameBn ?? labelEn, promotion.StartsAt?.ToOffset(Dhaka), promotion.EndsAt?.ToOffset(Dhaka), cards);
    }

    private async Task<List<Guid>> ProductIdsAsync(IReadOnlyList<PromotionTarget> targets, CancellationToken cancellationToken)
    {
        var ids = new HashSet<Guid>();
        ids.UnionWith(targets.Where(t => t is { Kind: PromotionTargetKind.Product, TargetId: not null }).Select(t => t.TargetId!.Value));
        var variantIds = targets.Where(t => t is { Kind: PromotionTargetKind.Variant, TargetId: not null }).Select(t => t.TargetId!.Value).ToList();
        if (variantIds.Count > 0)
        {
            ids.UnionWith(await db.ProductVariants.AsNoTracking().Where(v => variantIds.Contains(v.Id)).Select(v => v.ProductId).ToListAsync(cancellationToken));
        }

        var categoryIds = targets.Where(t => t is { Kind: PromotionTargetKind.Category, TargetId: not null }).Select(t => t.TargetId!.Value).ToList();
        if (categoryIds.Count > 0)
        {
            var parents = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync(cancellationToken);
            var scope = new HashSet<Guid>(categoryIds);
            for (var grew = true; grew;)
            {
                grew = false;
                foreach (var c in parents.Where(c => c.ParentId is { } p && scope.Contains(p)))
                {
                    grew |= scope.Add(c.Id);
                }
            }

            ids.UnionWith(await db.Products.AsNoTracking().Where(p => p.Status == ProductStatus.Published && scope.Contains(p.CategoryId)).Select(p => p.Id).Take(500).ToListAsync(cancellationToken));
        }

        var brandIds = targets.Where(t => t is { Kind: PromotionTargetKind.Brand, TargetId: not null }).Select(t => t.TargetId!.Value).ToList();
        if (brandIds.Count > 0)
        {
            ids.UnionWith(await db.Products.AsNoTracking().Where(p => p.Status == ProductStatus.Published && p.BrandId != null && brandIds.Contains(p.BrandId.Value)).Select(p => p.Id).Take(500).ToListAsync(cancellationToken));
        }

        if (targets.Any(t => t.Kind == PromotionTargetKind.Everything))
        {
            ids.UnionWith(await db.Products.AsNoTracking().Where(p => p.Status == ProductStatus.Published).OrderByDescending(p => p.PublishedAt).Select(p => p.Id).Take(CampaignProducts).ToListAsync(cancellationToken));
        }

        return [.. ids];
    }

    /// <summary>Active banners for a slot (HomeTop, ShopTop, Category with the category's web address), highest priority first.</summary>
    public async Task<Result<IReadOnlyList<StoreBannerDto>>> BannersAsync(BannerSlot? slot, string? categorySlug, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var query = Running(now).Where(p => p.BannerImagePath != null && p.BannerSlot != BannerSlot.None);
        if (slot is { } s && s != BannerSlot.None)
        {
            query = query.Where(p => p.BannerSlot == s);
        }

        var rows = await query.OrderByDescending(p => p.Priority).ThenByDescending(p => p.CreatedAt).Take(20).ToListAsync(cancellationToken);
        if (rows.Any(p => p.BannerSlot == BannerSlot.Category))
        {
            // A category banner shows on its category and below it.
            var path = new HashSet<Guid>();
            if (!string.IsNullOrWhiteSpace(categorySlug))
            {
                var slugValue = categorySlug.Trim().ToLowerInvariant();
                var all = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId, c.Slug }).ToListAsync(cancellationToken);
                var current = all.FirstOrDefault(c => c.Slug == slugValue);
                for (var guard = 0; current is not null && guard < 12; guard++)
                {
                    path.Add(current.Id);
                    var parent = current.ParentId;
                    current = parent is null ? null : all.FirstOrDefault(c => c.Id == parent);
                }
            }

            rows = [.. rows.Where(p => p.BannerSlot != BannerSlot.Category || (p.BannerCategoryId is { } cid && path.Contains(cid)))];
        }

        return Result.Success<IReadOnlyList<StoreBannerDto>>([.. rows.Select(p =>
            new StoreBannerDto(p.Id, p.BannerSlot, storage.GetPublicUrl(p.BannerImagePath!), p.TitleEn ?? p.NameEn, p.TitleBn ?? p.NameBn, p.Slug, p.LabelEn ?? p.NameEn, p.LabelBn ?? p.NameBn ?? p.LabelEn ?? p.NameEn,
                p.EndsAt?.ToOffset(Dhaka)))]);
    }

    /// <summary>The free-delivery promotions that are running for the web shop, judged on a goods value.</summary>
    public async Task<IReadOnlyList<FreeDeliveryOffer>> FreeDeliveryOffersAsync(decimal goods, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var rows = await Running(now).Where(p => p.Type == PromotionType.FreeDelivery && (p.UsageLimit == null || p.UsedCount < p.UsageLimit)).ToListAsync(cancellationToken);
        return [.. rows.Select(p =>
        {
            var min = p.MinCartValue ?? 0m;
            var zones = string.IsNullOrWhiteSpace(p.ZoneCodes) ? [] : p.ZoneCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return new FreeDeliveryOffer(p.Id, p.LabelEn ?? p.NameEn, p.LabelBn ?? p.NameBn ?? p.LabelEn ?? p.NameEn, min, zones, goods >= min, Math.Max(0m, min - goods));
        })];
    }

    /// <summary>
    /// Seconds until the next promotion starts or ends (an end counts from the second after it), so a cached answer is never served past a price change: a flash price never outlives its window in a cache.
    /// Null when no promotion is scheduled to change.
    /// </summary>
    public async Task<int?> SecondsToNextChangeAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var start = await db.Promotions.AsNoTracking().Where(p => p.IsEnabled && !p.IsArchived && p.StartsAt != null && p.StartsAt > now).OrderBy(p => p.StartsAt).Select(p => p.StartsAt).FirstOrDefaultAsync(cancellationToken);
        var end = await db.Promotions.AsNoTracking().Where(p => p.IsEnabled && !p.IsArchived && p.EndsAt != null && p.EndsAt >= now).OrderBy(p => p.EndsAt).Select(p => p.EndsAt).FirstOrDefaultAsync(cancellationToken);
        DateTimeOffset? next = start;
        if (end is { } e && (next is null || e.AddSeconds(1) < next))
        {
            next = e.AddSeconds(1);
        }

        return next is { } n ? (int)Math.Max(0, Math.Ceiling((n - now).TotalSeconds)) : null;
    }
}
