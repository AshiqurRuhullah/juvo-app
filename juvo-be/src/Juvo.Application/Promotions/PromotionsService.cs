using System.Linq.Expressions;
using Juvo.Application.Abstractions;
using Juvo.Application.Pricing;
using Juvo.Domain.Promotions;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Promotions;

/// <summary>
/// Promotion management for staff (module Z). Promotions are global (not per branch), like coupons. A promotion is a Draft until it is activated; whether it is Scheduled, Active or Ended then follows
/// its schedule. Archiving keeps the row for the reports and for the order lines that name it. Every change is audited by the generic audit interceptor.
/// </summary>
public sealed class PromotionsService(IApplicationDbContext db, IFileStorage storage, IImageProcessor images, IPricingEngine pricing, TimeProvider clock)
{
    private static readonly TimeSpan Dhaka = TimeSpan.FromHours(6);

    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error NotFound() => Error.NotFound(ErrorCodes.Promo.NotFound, "Promotion not found.");

    private static DateTimeOffset? InDhaka(DateTimeOffset? value) => value?.ToOffset(Dhaka);

    // ---- Reading ----------------------------------------------------------------------------------------

    private static Expression<Func<Promotion, bool>> StatusFilter(PromotionStatus status, DateTimeOffset now) => status switch
    {
        PromotionStatus.Archived => p => p.IsArchived,
        PromotionStatus.Draft => p => !p.IsArchived && !p.IsEnabled,
        PromotionStatus.Scheduled => p => !p.IsArchived && p.IsEnabled && p.StartsAt != null && p.StartsAt > now,
        PromotionStatus.Ended => p => !p.IsArchived && p.IsEnabled && p.EndsAt != null && p.EndsAt < now,
        _ => p => !p.IsArchived && p.IsEnabled && (p.StartsAt == null || p.StartsAt <= now) && (p.EndsAt == null || p.EndsAt >= now),
    };

    public async Task<Result<PagedResult<PromotionListItemDto>>> ListAsync(PageRequest request, PromotionListQuery filter, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var query = db.Promotions.AsNoTracking().AsQueryable();
        if (filter.Type is { } type)
        {
            query = query.Where(p => p.Type == type);
        }

        if (filter.Status is { } status)
        {
            query = query.Where(StatusFilter(status, now));
        }

        if (filter.LiveNow == true)
        {
            query = query.Where(StatusFilter(PromotionStatus.Active, now));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(p => p.NameEn.Contains(term) || (p.NameBn != null && p.NameBn.Contains(term)) || (p.Slug != null && p.Slug.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(p => p.CreatedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<PromotionListItemDto>([.. rows.Select(p => ToListItem(p, now))], total, page.Page, page.PageSize);
    }

    private static PromotionListItemDto ToListItem(Promotion p, DateTimeOffset now) =>
        new(p.Id, p.NameEn, p.NameBn, p.Type, p.StatusAt(now), InDhaka(p.StartsAt), InDhaka(p.EndsAt), p.Priority, p.StackMode, p.UsedCount, p.UsageLimit, p.Slug, p.CreatedAt);

    public async Task<Result<PromotionDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var promotion = await db.Promotions.AsNoTracking().Include(p => p.Targets).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        return promotion is null ? NotFound() : await ToDetailAsync(promotion, cancellationToken);
    }

    private async Task<PromotionDetailDto> ToDetailAsync(Promotion p, CancellationToken cancellationToken)
    {
        var names = await TargetNamesAsync(p.Targets, cancellationToken);
        var now = clock.GetUtcNow();
        return new PromotionDetailDto(p.Id, p.NameEn, p.NameBn, p.Type, p.StatusAt(now), p.IsEnabled, p.IsArchived, InDhaka(p.StartsAt), InDhaka(p.EndsAt), p.Priority, p.StackMode, p.Channels, p.MinCartValue,
            p.UsageLimit, p.UsedCount, p.DiscountKind, p.DiscountValue, p.MaxDiscount, p.BuyQuantity, p.GetQuantity, p.GetDiscountPercent, p.BundlePrice,
            string.IsNullOrWhiteSpace(p.ZoneCodes) ? [] : p.ZoneCodes.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), p.LabelEn, p.LabelBn, p.Slug, p.TitleEn, p.TitleBn, p.BodyEn, p.BodyBn,
            p.HeroImagePath is null ? null : storage.GetPublicUrl(p.HeroImagePath), p.BannerImagePath is null ? null : storage.GetPublicUrl(p.BannerImagePath), p.BannerSlot, p.BannerCategoryId,
            [.. p.Targets.OrderBy(t => t.Role).ThenBy(t => t.CreatedAt).Select(t => new PromotionTargetDto(t.Id, t.Kind, t.TargetId, t.Role, t.Quantity, t.TargetId is { } tid ? names.GetValueOrDefault((t.Kind, tid)) : "Everything"))], p.CreatedAt);
    }

    private async Task<Dictionary<(PromotionTargetKind, Guid), string>> TargetNamesAsync(IEnumerable<PromotionTarget> targets, CancellationToken cancellationToken)
    {
        var list = targets.Where(t => t.TargetId is not null).ToList();
        var result = new Dictionary<(PromotionTargetKind, Guid), string>();
        var products = list.Where(t => t.Kind == PromotionTargetKind.Product).Select(t => t.TargetId!.Value).ToList();
        foreach (var x in await db.Products.IgnoreQueryFilters().AsNoTracking().Where(p => products.Contains(p.Id)).Select(p => new { p.Id, p.NameEn }).ToListAsync(cancellationToken))
        {
            result[(PromotionTargetKind.Product, x.Id)] = x.NameEn;
        }

        var variants = list.Where(t => t.Kind == PromotionTargetKind.Variant).Select(t => t.TargetId!.Value).ToList();
        foreach (var x in await db.ProductVariants.IgnoreQueryFilters().AsNoTracking().Where(v => variants.Contains(v.Id)).Select(v => new { v.Id, v.Sku }).ToListAsync(cancellationToken))
        {
            result[(PromotionTargetKind.Variant, x.Id)] = x.Sku;
        }

        var categories = list.Where(t => t.Kind == PromotionTargetKind.Category).Select(t => t.TargetId!.Value).ToList();
        foreach (var x in await db.Categories.AsNoTracking().Where(c => categories.Contains(c.Id)).Select(c => new { c.Id, c.NameEn }).ToListAsync(cancellationToken))
        {
            result[(PromotionTargetKind.Category, x.Id)] = x.NameEn;
        }

        var brands = list.Where(t => t.Kind == PromotionTargetKind.Brand).Select(t => t.TargetId!.Value).ToList();
        foreach (var x in await db.Brands.AsNoTracking().Where(b => brands.Contains(b.Id)).Select(b => new { b.Id, b.NameEn }).ToListAsync(cancellationToken))
        {
            result[(PromotionTargetKind.Brand, x.Id)] = x.NameEn;
        }

        return result;
    }

    // ---- Validation -------------------------------------------------------------------------------------

    private async Task<Error?> ValidateAsync(SavePromotionRequest r, Guid? existingId, bool forActivation, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(r.NameEn) || r.NameEn.Trim().Length > 150 || r.NameBn is { Length: > 150 })
        {
            return Invalid("nameEn", "Enter a name (up to 150 characters).");
        }

        if (!Enum.IsDefined(r.Type) || !Enum.IsDefined(r.StackMode) || !Enum.IsDefined(r.BannerSlot))
        {
            return Invalid("type", "Unknown promotion type, stack mode or banner slot.");
        }

        if (r.StartsAt is { } start && r.EndsAt is { } end && end <= start)
        {
            return Invalid("endsAt", "The promotion must end after it starts.");
        }

        if (r.Priority is < -1000 or > 1000 || r.UsageLimit is <= 0 || r.MinCartValue is < 0 or > 100_000_000)
        {
            return Invalid("priority", "Priority is between -1000 and 1000; the usage limit and the minimum cart value must be positive.");
        }

        if (r.Channels is { } channels && (channels == PromotionChannels.None || (channels & ~PromotionChannels.All) != 0))
        {
            return Invalid("channels", "Choose at least one of Web, SocialDesk, Pos.");
        }

        if (r.LabelEn is { Length: > 80 } || r.LabelBn is { Length: > 80 })
        {
            return Invalid("labelEn", "A label can have at most 80 characters.");
        }

        var targets = r.Targets ?? [];
        if (targets.Count > 200 || targets.Any(t => !Enum.IsDefined(t.Kind) || !Enum.IsDefined(t.Role) || t.Quantity is < 1 or > 1000 || (t.Kind != PromotionTargetKind.Everything && t.TargetId is null)))
        {
            return Invalid("targets", "Targets need a kind, an id (except Everything) and a quantity between 1 and 1000; at most 200.");
        }

        Error? Discount(bool required)
        {
            if (r.DiscountKind is null)
            {
                return required ? Invalid("discountKind", "Choose Percent, Fixed or FixedPrice.") : null;
            }

            if (!Enum.IsDefined(r.DiscountKind.Value) || r.DiscountValue is not { } v || v <= 0m || v > 10_000_000m || (r.DiscountKind == DiscountKind.Percent && v > 100m) || r.MaxDiscount is <= 0m)
            {
                return Invalid("discountValue", r.DiscountKind == DiscountKind.Percent ? "A percentage must be above 0 and at most 100." : "The amount must be above 0.");
            }

            return null;
        }

        Error? Roles(params TargetRole[] allowed) =>
            targets.Any(t => !allowed.Contains(t.Role)) ? Invalid("targets", $"For this type use only these roles: {string.Join(", ", allowed)}.") : null;

        Error? problem;
        switch (r.Type)
        {
            case PromotionType.FlashSale:
                if (r.EndsAt is null)
                {
                    return Invalid("endsAt", "A flash sale needs an end time (the shop shows a countdown).");
                }

                problem = Discount(true) ?? Roles(TargetRole.Main);
                if (problem is not null)
                {
                    return problem;
                }

                if (targets.Count == 0)
                {
                    return Invalid("targets", "Choose the products, categories or brands on sale.");
                }

                break;
            case PromotionType.ProductDiscount:
                problem = Discount(true) ?? Roles(TargetRole.Main);
                if (problem is not null)
                {
                    return problem;
                }

                if (targets.Count == 0)
                {
                    return Invalid("targets", "Choose what the discount applies to (or Everything).");
                }

                break;
            case PromotionType.Bundle:
                problem = Roles(TargetRole.BundleItem);
                if (problem is not null)
                {
                    return problem;
                }

                if (r.BundlePrice is not { } price || price < 0m || price > 10_000_000m)
                {
                    return Invalid("bundlePrice", "Enter the combo price (0 or more).");
                }

                if (targets.Count == 0 || targets.Sum(t => t.Quantity) < 2 || targets.Any(t => t.Kind == PromotionTargetKind.Everything))
                {
                    return Invalid("targets", "A bundle needs items (products, variants, categories or brands) that add up to at least two units.");
                }

                break;
            case PromotionType.BuyXGetY:
                problem = Roles(TargetRole.Buy, TargetRole.Get);
                if (problem is not null)
                {
                    return problem;
                }

                if (r.BuyQuantity < 1 || r.GetQuantity < 1 || r.BuyQuantity > 100 || r.GetQuantity > 100 || r.GetDiscountPercent is <= 0m or > 100m)
                {
                    return Invalid("buyQuantity", "Buy and get quantities are 1 to 100; the discount on the get units is above 0 and at most 100 percent.");
                }

                if (!targets.Any(t => t.Role == TargetRole.Buy))
                {
                    return Invalid("targets", "Choose what has to be bought (role Buy). The set that is discounted (role Get) is optional: without it the same set is used.");
                }

                break;
            case PromotionType.FreeDelivery:
                if (targets.Count > 0)
                {
                    return Invalid("targets", "Free delivery has no targets; use the minimum cart value and optional zones.");
                }

                if (r.MinCartValue is null)
                {
                    return Invalid("minCartValue", "Enter the cart value from which delivery is free (0 for always).");
                }

                if (r.ZoneCodes is { Count: > 0 } zones)
                {
                    var codes = zones.Select(z => z.Trim().ToUpperInvariant()).Distinct().ToList();
                    var known = await db.DeliveryZones.AsNoTracking().Where(z => codes.Contains(z.Code)).Select(z => z.Code).ToListAsync(cancellationToken);
                    if (known.Count != codes.Count)
                    {
                        return Invalid("zoneCodes", "A zone code does not exist.");
                    }
                }

                break;
            case PromotionType.FestivalCampaign:
                problem = Discount(false) ?? Roles(TargetRole.Main);
                if (problem is not null)
                {
                    return problem;
                }

                var slug = r.Slug?.Trim().ToLowerInvariant() ?? string.Empty;
                if (slug.Length is < 2 or > 100 || !slug.All(ch => char.IsAsciiLetterOrDigit(ch) || ch == '-') || slug.StartsWith('-') || slug.EndsWith('-'))
                {
                    return Invalid("slug", "Use 2 to 100 lower-case letters, digits and hyphens.");
                }

                if (string.IsNullOrWhiteSpace(r.TitleEn) || r.TitleEn.Length > 200 || r.TitleBn is { Length: > 200 } || r.BodyEn is { Length: > 8000 } || r.BodyBn is { Length: > 8000 })
                {
                    return Invalid("titleEn", "Enter an English title (up to 200 characters); texts are limited to 8000 characters.");
                }

                if (await db.Promotions.AnyAsync(p => p.Slug == slug && p.Id != existingId, cancellationToken))
                {
                    return Error.Conflict(ErrorCodes.Promo.SlugTaken, "Another campaign already uses this web address.");
                }

                if (r.BannerSlot == BannerSlot.Category && r.BannerCategoryId is null)
                {
                    return Invalid("bannerCategoryId", "A category banner needs the category.");
                }

                if (forActivation && targets.Count == 0)
                {
                    return Invalid("targets", "A campaign page needs a product set.");
                }

                break;
        }

        if (r.Type != PromotionType.FestivalCampaign && r.BannerSlot != BannerSlot.None)
        {
            return Invalid("bannerSlot", "Only a festival campaign has a banner.");
        }

        // Targets must exist.
        foreach (var group in targets.Where(t => t.TargetId is not null).GroupBy(t => t.Kind))
        {
            var ids = group.Select(t => t.TargetId!.Value).Distinct().ToList();
            var found = group.Key switch
            {
                PromotionTargetKind.Product => await db.Products.CountAsync(p => ids.Contains(p.Id), cancellationToken),
                PromotionTargetKind.Variant => await db.ProductVariants.CountAsync(v => ids.Contains(v.Id), cancellationToken),
                PromotionTargetKind.Category => await db.Categories.CountAsync(c => ids.Contains(c.Id), cancellationToken),
                _ => await db.Brands.CountAsync(b => ids.Contains(b.Id), cancellationToken),
            };
            if (found != ids.Count)
            {
                return Error.NotFound(ErrorCodes.Promo.TargetNotFound, $"A {group.Key.ToString().ToLowerInvariant()} in the targets does not exist.");
            }
        }

        if (r.BannerCategoryId is { } bannerCategory && !await db.Categories.AnyAsync(c => c.Id == bannerCategory, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Promo.TargetNotFound, "The banner category does not exist.");
        }

        return null;
    }

    private static PromotionDetails ToDetails(SavePromotionRequest r) =>
        new(r.NameEn, r.NameBn, r.Type, r.StartsAt, r.EndsAt, r.Priority, r.StackMode, r.Channels ?? PromotionChannels.All, r.MinCartValue, r.UsageLimit,
            r.Type is PromotionType.FlashSale or PromotionType.ProductDiscount or PromotionType.FestivalCampaign ? r.DiscountKind : null,
            r.Type is PromotionType.FlashSale or PromotionType.ProductDiscount or PromotionType.FestivalCampaign ? r.DiscountValue : null,
            r.Type is PromotionType.FlashSale or PromotionType.ProductDiscount or PromotionType.FestivalCampaign ? r.MaxDiscount : null,
            r.Type == PromotionType.BuyXGetY ? r.BuyQuantity : 0, r.Type == PromotionType.BuyXGetY ? r.GetQuantity : 0, r.Type == PromotionType.BuyXGetY ? r.GetDiscountPercent : 100m,
            r.Type == PromotionType.Bundle ? r.BundlePrice : null,
            r.Type == PromotionType.FreeDelivery && r.ZoneCodes is { Count: > 0 } ? string.Join(',', r.ZoneCodes.Select(z => z.Trim().ToUpperInvariant()).Distinct()) : null,
            r.LabelEn, r.LabelBn, r.Type == PromotionType.FestivalCampaign ? r.Slug : null, r.TitleEn, r.TitleBn, r.BodyEn, r.BodyBn, null, null,
            r.Type == PromotionType.FestivalCampaign ? r.BannerSlot : BannerSlot.None, r.Type == PromotionType.FestivalCampaign ? r.BannerCategoryId : null);

    private static IEnumerable<(PromotionTargetKind, Guid?, TargetRole, int)> ToTargets(SavePromotionRequest r) =>
        (r.Targets ?? []).Select(t => (t.Kind, t.Kind == PromotionTargetKind.Everything ? null : t.TargetId, t.Role, t.Quantity)).DistinctBy(t => (t.Kind, t.Item2, t.Role));

    // ---- Writing ----------------------------------------------------------------------------------------

    public async Task<Result<PromotionDetailDto>> CreateAsync(SavePromotionRequest request, CancellationToken cancellationToken)
    {
        if (await ValidateAsync(request, null, false, cancellationToken) is { } problem)
        {
            return problem;
        }

        var promotion = new Promotion(ToDetails(request));
        promotion.ReplaceTargets(ToTargets(request));
        db.Promotions.Add(promotion);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(promotion.Id, cancellationToken);
    }

    public async Task<Result<PromotionDetailDto>> UpdateAsync(Guid id, SavePromotionRequest request, CancellationToken cancellationToken)
    {
        var promotion = await db.Promotions.Include(p => p.Targets).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (promotion is null)
        {
            return NotFound();
        }

        if (promotion.IsArchived)
        {
            return Error.Conflict(ErrorCodes.Promo.Archived, "An archived promotion cannot be changed.");
        }

        if (request.Type != promotion.Type)
        {
            return Invalid("type", "The type of a promotion cannot change. Create a new promotion instead.");
        }

        if (await ValidateAsync(request, id, promotion.IsEnabled, cancellationToken) is { } problem)
        {
            return problem;
        }

        if (request.UsageLimit is { } limit && limit < promotion.UsedCount)
        {
            return Invalid("usageLimit", "The limit cannot be lower than the number of times the promotion was already used.");
        }

        promotion.Update(ToDetails(request));
        promotion.ReplaceTargets(ToTargets(request));
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(id, cancellationToken);
    }

    private async Task<Result<PromotionDetailDto>> SwitchAsync(Guid id, bool enable, CancellationToken cancellationToken)
    {
        var promotion = await db.Promotions.Include(p => p.Targets).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (promotion is null)
        {
            return NotFound();
        }

        if (promotion.IsArchived)
        {
            return Error.Conflict(ErrorCodes.Promo.Archived, "An archived promotion cannot be switched on or off.");
        }

        if (enable)
        {
            var missing = Incomplete(promotion);
            if (missing is not null)
            {
                return Error.Conflict(ErrorCodes.Promo.Incomplete, missing);
            }

            promotion.Enable();
        }
        else
        {
            promotion.Disable();
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(promotion, cancellationToken);
    }

    /// <summary>What is still missing before a promotion can run (null when complete).</summary>
    private static string? Incomplete(Promotion p) => p.Type switch
    {
        PromotionType.FlashSale or PromotionType.ProductDiscount when p.DiscountKind is null || p.Targets.Count == 0 => "Choose the discount and what it applies to first.",
        PromotionType.FlashSale when p.EndsAt is null => "A flash sale needs an end time.",
        PromotionType.Bundle when p.BundlePrice is null || p.Targets.Count == 0 => "A bundle needs its items and the combo price.",
        PromotionType.BuyXGetY when p.BuyQuantity < 1 || p.GetQuantity < 1 || p.Targets.All(t => t.Role != TargetRole.Buy) => "Buy X get Y needs the quantities and the set that has to be bought.",
        PromotionType.FreeDelivery when p.MinCartValue is null => "Free delivery needs the cart value it starts at.",
        PromotionType.FestivalCampaign when string.IsNullOrEmpty(p.Slug) || p.Targets.Count == 0 => "A campaign needs its web address and a product set.",
        _ => null,
    };

    public Task<Result<PromotionDetailDto>> ActivateAsync(Guid id, CancellationToken cancellationToken) => SwitchAsync(id, true, cancellationToken);

    public Task<Result<PromotionDetailDto>> DeactivateAsync(Guid id, CancellationToken cancellationToken) => SwitchAsync(id, false, cancellationToken);

    /// <summary>Archive: the promotion stops, disappears from the shop and stays in the reports. Nothing is erased (also what DELETE does).</summary>
    public async Task<Result<PromotionDetailDto>> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var promotion = await db.Promotions.Include(p => p.Targets).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (promotion is null)
        {
            return NotFound();
        }

        promotion.Archive();
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(promotion, cancellationToken);
    }

    public async Task<Result<PromotionDetailDto>> DuplicateAsync(Guid id, CancellationToken cancellationToken)
    {
        var source = await db.Promotions.AsNoTracking().Include(p => p.Targets).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        string? slug = null;
        if (source.Slug is { } baseSlug)
        {
            slug = baseSlug + "-copy";
            for (var n = 2; await db.Promotions.AnyAsync(p => p.Slug == slug, cancellationToken); n++)
            {
                slug = $"{baseSlug}-copy-{n}";
            }
        }

        var details = new PromotionDetails($"{source.NameEn} (copy)", source.NameBn is null ? null : $"{source.NameBn} (কপি)", source.Type, source.StartsAt, source.EndsAt, source.Priority, source.StackMode, source.Channels,
            source.MinCartValue, source.UsageLimit, source.DiscountKind, source.DiscountValue, source.MaxDiscount, source.BuyQuantity, source.GetQuantity, source.GetDiscountPercent, source.BundlePrice,
            source.ZoneCodes, source.LabelEn, source.LabelBn, slug, source.TitleEn, source.TitleBn, source.BodyEn, source.BodyBn, source.HeroImagePath, source.BannerImagePath, source.BannerSlot, source.BannerCategoryId);
        var copy = new Promotion(details);
        copy.ReplaceTargets(source.Targets.Select(t => (t.Kind, t.TargetId, t.Role, t.Quantity)));
        db.Promotions.Add(copy);
        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(copy.Id, cancellationToken);
    }

    // ---- Campaign pictures --------------------------------------------------------------------------------

    /// <summary>Stores the hero or banner picture of a campaign through <see cref="IFileStorage"/> (WebP, long side at most 1600 px, at least 1000 px).</summary>
    public async Task<Result<PromotionDetailDto>> SetImageAsync(Guid id, string kind, Stream content, CancellationToken cancellationToken)
    {
        var which = kind.Trim().ToLowerInvariant();
        if (which is not ("hero" or "banner"))
        {
            return Invalid("kind", "Use hero or banner.");
        }

        var promotion = await db.Promotions.Include(p => p.Targets).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (promotion is null)
        {
            return NotFound();
        }

        if (promotion.IsArchived)
        {
            return Error.Conflict(ErrorCodes.Promo.Archived, "An archived promotion cannot be changed.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > ImageRenditions.MaxBytes)
            {
                return Invalid("file", "The image must be 10 MB or smaller.");
            }
        }

        var (image, problem) = images.Process(buffer.ToArray(), 0.5, 0.5);
        if (image is null)
        {
            var message = problem switch
            {
                ImageProblem.TooSmall => $"The image must be at least {ImageRenditions.MinLongSide} px on its longer side.",
                ImageProblem.TooLarge => "The image has too many pixels. Resize it and try again.",
                _ => "The file is not a JPEG, PNG or WebP image.",
            };
            return Error.Validation(new Dictionary<string, string[]> { ["file"] = [message] }, message);
        }

        var path = $"promotions/{promotion.Id:N}/{which}-{Guid.NewGuid():N}.webp";
        await storage.SaveAsync(path, new MemoryStream(image.Main), "image/webp", cancellationToken);
        var old = which == "hero" ? promotion.HeroImagePath : promotion.BannerImagePath;
        promotion.SetImages(which == "hero" ? path : promotion.HeroImagePath, which == "banner" ? path : promotion.BannerImagePath);
        await db.SaveChangesAsync(cancellationToken);
        if (old is not null)
        {
            await storage.DeleteAsync(old, CancellationToken.None);
        }

        return await ToDetailAsync(promotion, cancellationToken);
    }

    // ---- Preview ----------------------------------------------------------------------------------------

    /// <summary>Prices a sample cart with a chosen promotion set so staff see the result before publishing. Nothing is saved or counted.</summary>
    public async Task<Result<PreviewResultDto>> PreviewAsync(PreviewRequest request, CancellationToken cancellationToken)
    {
        if (request.Lines is null or { Count: 0 } || request.Lines.Count > 50 || request.Lines.Any(l => l.Quantity is < 1 or > 1000))
        {
            return Invalid("lines", "Send 1 to 50 lines with a quantity between 1 and 1000.");
        }

        var lines = new List<PricingLineRequest>();
        foreach (var line in request.Lines)
        {
            var variantId = line.VariantId;
            if (variantId is null && !string.IsNullOrWhiteSpace(line.Sku))
            {
                var sku = line.Sku.Trim().ToUpperInvariant();
                variantId = await db.ProductVariants.AsNoTracking().Where(v => v.Sku == sku).Select(v => (Guid?)v.Id).FirstOrDefaultAsync(cancellationToken);
            }

            if (variantId is null)
            {
                return Invalid("lines", "A line has no known variant or SKU.");
            }

            lines.Add(new PricingLineRequest(variantId.Value, line.Quantity));
        }

        if (request.PromotionIds is { Count: > 0 } ids && await db.Promotions.CountAsync(p => ids.Contains(p.Id) && !p.IsArchived, cancellationToken) != ids.Distinct().Count())
        {
            return Error.NotFound(ErrorCodes.Promo.NotFound, "A chosen promotion does not exist or is archived.");
        }

        var priced = await pricing.PriceAsync(new PricingRequest(lines, null, string.IsNullOrWhiteSpace(request.CouponCode) ? null : [request.CouponCode], request.Channel ?? PricingChannel.Web, request.At,
            PromotionSet: request.PromotionIds is null ? null : [.. request.PromotionIds.Distinct()]), cancellationToken);
        if (priced.IsFailure)
        {
            return priced.Error!;
        }

        var r = priced.Value;
        return new PreviewResultDto(r.Lines, r.Totals, r.Promotions, r.Coupon, r.FreeDelivery, r.Explanation, r.CouponError?.Code, r.CouponError?.Message);
    }
}
