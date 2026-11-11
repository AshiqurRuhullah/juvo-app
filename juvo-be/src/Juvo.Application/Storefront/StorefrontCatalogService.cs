using System.Linq.Expressions;
using Juvo.Application.Abstractions;
using Juvo.Application.Catalog;
using Juvo.Domain.Catalog;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Storefront;

/// <summary>
/// The public side of the catalog (module A): only published products with active variants in active categories, never the cost price,
/// and only the combined stock number, never which branch holds it. Nothing here needs a sign-in.
/// </summary>
public sealed class StorefrontCatalogService(IApplicationDbContext db, StockAvailability stock, IFileStorage storage, TimeProvider clock)
{
    private const int CardsPerSection = 8;
    private const int FuzzyPoolLimit = 3000;

    // ---- Categories -------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<StoreCategory>>> CategoriesAsync(CancellationToken cancellationToken)
    {
        var categories = await db.Categories.AsNoTracking().Where(c => c.IsActive).OrderBy(c => c.SortOrder).ThenBy(c => c.NameEn).ToListAsync(cancellationToken);
        var counts = await PublishedCountsAsync(cancellationToken);
        var byParent = categories.ToLookup(c => c.ParentId);

        // A child whose parent is inactive is not reachable from the tree.
        int Total(Guid id) => counts.GetValueOrDefault(id) + byParent[id].Sum(c => Total(c.Id));
        IReadOnlyList<StoreCategory> Build(Guid? parent) =>
        [
            .. byParent[parent].Select(c => new StoreCategory(c.Id, c.Slug, c.NameEn, c.NameBn, c.ImagePath is null ? null : storage.GetPublicUrl(c.ImagePath),
                Total(c.Id), Build(c.Id))),
        ];

        return Result.Success(Build(null));
    }

    private async Task<Dictionary<Guid, int>> PublishedCountsAsync(CancellationToken cancellationToken) =>
        await db.Products.AsNoTracking().Where(p => p.Status == ProductStatus.Published).GroupBy(p => p.CategoryId)
            .Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

    // ---- Home -------------------------------------------------------------------------------------------

    public async Task<Result<StoreHome>> HomeAsync(CancellationToken cancellationToken)
    {
        var context = await ContextAsync(cancellationToken);
        var published = Published(context);

        var newest = await CardsAsync(published.OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.CreatedAt).Take(CardsPerSection), context, cancellationToken);
        var best = await CardsAsync(published.Where(p => p.IsBestSeller).OrderByDescending(p => p.PublishedAt).Take(CardsPerSection), context, cancellationToken);
        var featured = await CardsAsync(published.Where(p => p.IsFeatured).OrderByDescending(p => p.PublishedAt).Take(CardsPerSection), context, cancellationToken);
        var sale = await CardsAsync(OnSale(published, context.Now).OrderByDescending(p => p.PublishedAt).Take(CardsPerSection), context, cancellationToken);
        var categories = (await CategoriesAsync(cancellationToken)).Value;
        return new StoreHome(newest, best, featured, sale, categories);
    }

    // ---- List -------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<StoreProductCard>>> ListAsync(PageRequest request, StoreProductQuery filter, CancellationToken cancellationToken)
    {
        var context = await ContextAsync(cancellationToken);
        var page = request.Normalized;

        if (!string.IsNullOrWhiteSpace(filter.Category) && context.CategoryScope(filter.Category) is null)
        {
            return new PagedResult<StoreProductCard>([], 0, page.Page, page.PageSize);
        }

        var query = Filtered(Published(context), filter, context, searchIds: null, applySearch: true);
        var total = await query.CountAsync(cancellationToken);

        if (total == 0 && !string.IsNullOrWhiteSpace(filter.Q))
        {
            // Nothing matched exactly: try again allowing small spelling mistakes.
            var ids = await FuzzyIdsAsync(filter.Q, context, cancellationToken);
            if (ids.Count > 0)
            {
                query = Filtered(Published(context), filter, context, ids, applySearch: false);
                total = await query.CountAsync(cancellationToken);
            }
        }

        var ordered = Sorted(query, filter.Sort, context.Now);
        var cards = await CardsAsync(ordered.Skip(page.Skip).Take(page.PageSize), context, cancellationToken);
        return new PagedResult<StoreProductCard>(cards, total, page.Page, page.PageSize);
    }

    /// <summary>The choices a shopper can narrow by inside a category (or the whole shop): sizes, colours, brands and the price range.</summary>
    public async Task<Result<StoreFacets>> FacetsAsync(string? category, CancellationToken cancellationToken)
    {
        var context = await ContextAsync(cancellationToken);
        var query = Filtered(Published(context), new StoreProductQuery(category, null, null, null, null, null, null, null, null, null), context, null, applySearch: false);
        var rows = await query.Select(p => new
        {
            p.RegularPrice,
            p.SalePrice,
            p.SaleStart,
            p.SaleEnd,
            BrandId = p.BrandId,
            Variants = p.Variants.Where(v => v.IsActive).Select(v => new { v.Size, v.ColorName, v.ColorHex, v.SortOrder }).ToList(),
        }).ToListAsync(cancellationToken);

        var sizes = rows.SelectMany(r => r.Variants).Where(v => !string.IsNullOrWhiteSpace(v.Size)).OrderBy(v => v.SortOrder).Select(v => v.Size!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var colours = rows.SelectMany(r => r.Variants).Where(v => !string.IsNullOrWhiteSpace(v.ColorName)).GroupBy(v => v.ColorName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new StoreSwatch(g.Key, g.Select(x => x.ColorHex).FirstOrDefault(h => h is not null))).ToList();
        var brandIds = rows.Where(r => r.BrandId is not null).Select(r => r.BrandId!.Value).Distinct().ToList();
        var brands = await db.Brands.AsNoTracking().Where(b => b.IsActive && brandIds.Contains(b.Id)).OrderBy(b => b.NameEn).Select(b => new StoreBrandFacet(b.Slug, b.NameEn)).ToListAsync(cancellationToken);
        decimal PriceNow(decimal regular, decimal? sale, DateTimeOffset? start, DateTimeOffset? end) =>
            sale is { } s && s < regular && (start is null || start <= context.Now) && (end is null || end >= context.Now) ? s : regular;
        var prices = rows.Select(r => PriceNow(r.RegularPrice, r.SalePrice, r.SaleStart, r.SaleEnd)).ToList();
        return new StoreFacets(sizes, colours, brands, prices.Count == 0 ? 0 : prices.Min(), prices.Count == 0 ? 0 : prices.Max());
    }

    // ---- Search suggestions -------------------------------------------------------------------------------

    public async Task<Result<StoreSuggestions>> SuggestAsync(string? q, CancellationToken cancellationToken)
    {
        var term = q?.Trim();
        if (string.IsNullOrEmpty(term) || term.Length < 2)
        {
            return new StoreSuggestions([], []);
        }

        var context = await ContextAsync(cancellationToken);
        var query = Filtered(Published(context), new StoreProductQuery(null, null, null, null, null, null, null, null, null, term), context, null, applySearch: true);
        var products = await CardsAsync(query.OrderByDescending(p => p.IsBestSeller).ThenByDescending(p => p.PublishedAt).Take(6), context, cancellationToken);
        if (products.Count == 0)
        {
            var ids = await FuzzyIdsAsync(term, context, cancellationToken);
            if (ids.Count > 0)
            {
                products = await CardsAsync(Filtered(Published(context), new StoreProductQuery(null, null, null, null, null, null, null, null, null, null), context, ids, applySearch: false).Take(6), context, cancellationToken);
            }
        }

        var tokens = TextMatch.Tokens(term);
        var categories = context.Categories.Where(c => tokens.All(t => c.NameEn.Contains(t, StringComparison.OrdinalIgnoreCase) || (c.NameBn?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false)))
            .Take(4).Select(c => new StoreSuggestCategory(c.Slug, c.NameEn)).ToList();
        return new StoreSuggestions([.. products.Select(p => new StoreSuggestProduct(p.Slug, p.NameEn, p.ImageUrl, p.Price))], categories);
    }

    // ---- Detail -----------------------------------------------------------------------------------------

    public async Task<Result<StoreProductDetail>> DetailAsync(string slug, CancellationToken cancellationToken)
    {
        var context = await ContextAsync(cancellationToken);
        var normalized = slug.Trim().ToLowerInvariant();
        var product = await Published(context).Include(p => p.Variants.Where(v => v.IsActive)).Include(p => p.Images).FirstOrDefaultAsync(p => p.Slug == normalized, cancellationToken);
        if (product is null)
        {
            return Error.NotFound(ErrorCodes.Catalog.ProductNotFound, "Product not found.");
        }

        var variants = product.Variants.OrderBy(v => v.SortOrder).ThenBy(v => v.Sku).ToList();
        var available = await stock.AvailableAsync([.. variants.Select(v => v.Id)], cancellationToken);
        var card = ToCard(product, variants, available, context);

        var related = await CardsAsync(Published(context).Where(p => p.CategoryId == product.CategoryId && p.Id != product.Id)
            .OrderByDescending(p => p.IsBestSeller).ThenByDescending(p => p.PublishedAt).Take(CardsPerSection), context, cancellationToken);

        var chart = product.SizeChartId is { } chartId ? await db.SizeCharts.AsNoTracking().Where(s => s.Id == chartId).Select(s => s.TableJson).FirstOrDefaultAsync(cancellationToken) : null;
        var crumbs = new List<StoreBreadcrumb>();
        var current = context.Categories.FirstOrDefault(c => c.Id == product.CategoryId);
        for (var guard = 0; current is not null && guard < 10; guard++)
        {
            crumbs.Insert(0, new StoreBreadcrumb(current.Slug, current.NameEn));
            current = current.ParentId is { } parent ? context.Categories.FirstOrDefault(c => c.Id == parent) : null;
        }

        var images = product.Images.OrderBy(i => i.SortOrder).Select(i => ProductMediaService.ToDto(i, storage))
            .Select(i => new StoreImage(i.Id, i.Url, i.CardUrl, i.ThumbUrl, i.ColorName, i.AltText, i.Width, i.Height, i.BlurDataUrl)).ToList();
        var storeVariants = variants.Select(v =>
        {
            var shown = stock.Shown(v, available);
            return new StoreVariant(v.Id, v.Sku, v.ColorName, v.ColorHex, v.Size, VariantPrice(product, v, context.Now), shown, shown > 0, stock.IsLow(v, available));
        }).ToList();

        return new StoreProductDetail(card, product.Description, product.Material, product.Care, product.Tags, product.TargetGroup.ToString(), product.WeightGrams, product.VideoUrl,
            product.MetaTitle, product.MetaDescription, chart, crumbs, storeVariants, images, related);
    }

    // ---- Query building ---------------------------------------------------------------------------------

    private sealed record Context(DateTimeOffset Now, List<Category> Categories, Dictionary<Guid, string> BrandNames, IQueryable<Guid> InStockVariantIds, List<Guid> ActiveCategoryIds)
    {
        /// <summary>The category with this slug and all its active descendants, or null when there is no such active category.</summary>
        public List<Guid>? CategoryScope(string slug)
        {
            var root = Categories.FirstOrDefault(c => string.Equals(c.Slug, slug.Trim(), StringComparison.OrdinalIgnoreCase));
            if (root is null)
            {
                return null;
            }

            var ids = new List<Guid> { root.Id };
            for (var i = 0; i < ids.Count; i++)
            {
                ids.AddRange(Categories.Where(c => c.ParentId == ids[i]).Select(c => c.Id));
            }

            return ids;
        }
    }

    private async Task<Context> ContextAsync(CancellationToken cancellationToken)
    {
        var all = await db.Categories.AsNoTracking().ToListAsync(cancellationToken);
        // Only categories whose whole chain of parents is active are visible.
        var byId = all.ToDictionary(c => c.Id);
        bool Visible(Category c)
        {
            for (var guard = 0; guard < 10; guard++)
            {
                if (!c.IsActive)
                {
                    return false;
                }

                if (c.ParentId is not { } parent || !byId.TryGetValue(parent, out var next))
                {
                    return c.ParentId is null;
                }

                c = next;
            }

            return false;
        }

        var visible = all.Where(Visible).ToList();
        var brands = await db.Brands.AsNoTracking().Where(b => b.IsActive).ToDictionaryAsync(b => b.Id, b => b.NameEn, cancellationToken);
        var branches = await stock.BranchIdsAsync(cancellationToken);
        var inStock = db.StockLevels.AsNoTracking().Where(l => branches.Contains(l.BranchId) && l.OnHand - l.Reserved > 0).Select(l => l.VariantId);
        return new Context(clock.GetUtcNow(), visible, brands, inStock, [.. visible.Select(c => c.Id)]);
    }

    private IQueryable<Product> Published(Context context)
    {
        var categoryIds = context.ActiveCategoryIds;
        return db.Products.AsNoTracking().Where(p => p.Status == ProductStatus.Published && categoryIds.Contains(p.CategoryId));
    }

    /// <summary>The price a customer pays for the product now: the sale price inside its dates, otherwise the regular price (same rule as the admin).</summary>
    private static Expression<Func<Product, decimal>> EffectivePrice(DateTimeOffset now) =>
        p => p.SalePrice != null && p.SalePrice < p.RegularPrice && (p.SaleStart == null || p.SaleStart <= now) && (p.SaleEnd == null || p.SaleEnd >= now)
            ? p.SalePrice.Value
            : p.RegularPrice;

    private static IQueryable<Product> OnSale(IQueryable<Product> query, DateTimeOffset now) =>
        query.Where(p => p.SalePrice != null && p.SalePrice < p.RegularPrice && (p.SaleStart == null || p.SaleStart <= now) && (p.SaleEnd == null || p.SaleEnd >= now));

    private static decimal VariantPrice(Product product, ProductVariant variant, DateTimeOffset now) => variant.PriceOverride ?? product.EffectivePrice(now);

    private IQueryable<Product> Filtered(IQueryable<Product> query, StoreProductQuery filter, Context context, List<Guid>? searchIds, bool applySearch)
    {
        if (!string.IsNullOrWhiteSpace(filter.Category) && context.CategoryScope(filter.Category) is { } scope)
        {
            query = query.Where(p => scope.Contains(p.CategoryId));
        }

        if (!string.IsNullOrWhiteSpace(filter.Brand))
        {
            var brandSlug = filter.Brand.Trim().ToLowerInvariant();
            var brandIds = db.Brands.Where(b => b.Slug == brandSlug).Select(b => b.Id);
            query = query.Where(p => p.BrandId != null && brandIds.Contains(p.BrandId.Value));
        }

        var now = context.Now;
        if (filter.MinPrice is { } min)
        {
            var price = EffectivePrice(now);
            query = query.Where(price.AndThenGreaterOrEqual(min));
        }

        if (filter.MaxPrice is { } max)
        {
            var price = EffectivePrice(now);
            query = query.Where(price.AndThenLessOrEqual(max));
        }

        if (!string.IsNullOrWhiteSpace(filter.Size))
        {
            var size = filter.Size.Trim();
            query = query.Where(p => p.Variants.Any(v => v.IsActive && v.Size == size));
        }

        if (!string.IsNullOrWhiteSpace(filter.Colour))
        {
            var colour = filter.Colour.Trim();
            query = query.Where(p => p.Variants.Any(v => v.IsActive && v.ColorName == colour));
        }

        if (filter.InStock == true)
        {
            var inStock = context.InStockVariantIds;
            query = query.Where(p => p.Variants.Any(v => v.IsActive && (!v.TracksStock || inStock.Contains(v.Id))));
        }

        if (filter.OnSale == true)
        {
            query = OnSale(query, now);
        }

        if (searchIds is not null)
        {
            query = query.Where(p => searchIds.Contains(p.Id));
        }

        if (applySearch && !string.IsNullOrWhiteSpace(filter.Q))
        {
            foreach (var token in TextMatch.Tokens(filter.Q))
            {
                var t = token;
                var upper = t.ToUpperInvariant();
                var categoryHits = context.Categories.Where(c => c.NameEn.Contains(t, StringComparison.OrdinalIgnoreCase) || (c.NameBn != null && c.NameBn.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    .Select(c => c.Id).ToList();
                var brandHits = context.BrandNames.Where(b => b.Value.Contains(t, StringComparison.OrdinalIgnoreCase)).Select(b => b.Key).ToList();
                query = query.Where(p => p.NameEn.Contains(t) || (p.NameBn != null && p.NameBn.Contains(t)) || p.Slug.Contains(t) || (p.Tags != null && p.Tags.Contains(t)) ||
                                         p.Variants.Any(v => v.IsActive && v.Sku.Contains(upper)) || categoryHits.Contains(p.CategoryId) ||
                                         (p.BrandId != null && brandHits.Contains(p.BrandId.Value)));
            }
        }

        return query;
    }

    private static IQueryable<Product> Sorted(IQueryable<Product> query, string? sort, DateTimeOffset now)
    {
        var price = EffectivePrice(now);
        return sort?.Trim().ToLowerInvariant() switch
        {
            "price-asc" => query.OrderBy(price).ThenBy(p => p.NameEn),
            "price-desc" => query.OrderByDescending(price).ThenBy(p => p.NameEn),
            "popular" => query.OrderByDescending(p => p.IsBestSeller).ThenByDescending(p => p.IsFeatured).ThenByDescending(p => p.PublishedAt),
            _ => query.OrderByDescending(p => p.PublishedAt).ThenByDescending(p => p.CreatedAt),
        };
    }

    /// <summary>Products whose words are close to every word of the search (spelling mistakes allowed). Bounded: only a few thousand products are considered.</summary>
    private async Task<List<Guid>> FuzzyIdsAsync(string q, Context context, CancellationToken cancellationToken)
    {
        var tokens = TextMatch.Tokens(q).Where(t => t.Length >= 3).ToList();
        if (tokens.Count == 0)
        {
            return [];
        }

        var pool = await Published(context).OrderByDescending(p => p.PublishedAt).Take(FuzzyPoolLimit)
            .Select(p => new { p.Id, p.NameEn, p.NameBn, p.Slug, p.Tags, p.CategoryId }).ToListAsync(cancellationToken);
        var categoryNames = context.Categories.ToDictionary(c => c.Id, c => c.NameEn);
        return [.. pool.Where(p =>
        {
            var words = TextMatch.Tokens(p.NameEn + " " + p.NameBn + " " + p.Slug + " " + p.Tags + " " + categoryNames.GetValueOrDefault(p.CategoryId));
            return tokens.All(t => TextMatch.Matches(t, words));
        }).Select(p => p.Id)];
    }

    // ---- Cards ------------------------------------------------------------------------------------------

    private async Task<IReadOnlyList<StoreProductCard>> CardsAsync(IQueryable<Product> query, Context context, CancellationToken cancellationToken)
    {
        var products = await query.Include(p => p.Variants.Where(v => v.IsActive)).Include(p => p.Images).ToListAsync(cancellationToken);
        var variantIds = products.SelectMany(p => p.Variants).Select(v => v.Id).ToList();
        var available = await stock.AvailableAsync(variantIds, cancellationToken);
        return [.. products.Select(p => ToCard(p, [.. p.Variants.OrderBy(v => v.SortOrder).ThenBy(v => v.Sku)], available, context))];
    }

    private StoreProductCard ToCard(Product product, List<ProductVariant> variants, IReadOnlyDictionary<Guid, int> available, Context context)
    {
        var price = product.EffectivePrice(context.Now);
        var onSale = price < product.RegularPrice;
        var images = product.Images.OrderBy(i => i.SortOrder).ToList();
        var main = images.FirstOrDefault() is { } first ? ProductMediaService.ToDto(first, storage) : null;
        var hover = images.Skip(1).FirstOrDefault() is { } second ? ProductMediaService.ToDto(second, storage) : null;
        var category = context.Categories.FirstOrDefault(c => c.Id == product.CategoryId);

        var swatches = variants.Where(v => !string.IsNullOrWhiteSpace(v.ColorName)).GroupBy(v => v.ColorName!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new StoreSwatch(g.Key, g.Select(v => v.ColorHex).FirstOrDefault(h => h is not null))).ToList();
        var sizes = variants.Where(v => !string.IsNullOrWhiteSpace(v.Size)).GroupBy(v => v.Size!, StringComparer.OrdinalIgnoreCase)
            .Select(g => new StoreSize(g.Key, g.All(v => stock.Shown(v, available) <= 0))).ToList();

        var anySellable = variants.Any(v => stock.Shown(v, available) > 0);
        return new StoreProductCard(product.Id, product.Slug, product.NameEn, product.NameBn, product.ShortDescription, category?.Slug ?? string.Empty, category?.NameEn ?? string.Empty,
            product.BrandId is { } brand ? context.BrandNames.GetValueOrDefault(brand) : null, product.RegularPrice, price, onSale,
            onSale && product.RegularPrice > 0 ? (int)Math.Round((product.RegularPrice - price) / product.RegularPrice * 100m, MidpointRounding.AwayFromZero) : 0,
            main?.CardUrl, hover?.CardUrl, main?.BlurDataUrl, product.IsNew, product.IsFeatured, product.IsBestSeller, variants.Count > 0 && !anySellable,
            variants.Any(v => stock.IsLow(v, available)), swatches, sizes);
    }
}

internal static class PriceExpressions
{
    public static Expression<Func<Product, bool>> AndThenGreaterOrEqual(this Expression<Func<Product, decimal>> price, decimal value) =>
        Expression.Lambda<Func<Product, bool>>(Expression.GreaterThanOrEqual(price.Body, Expression.Constant(value)), price.Parameters);

    public static Expression<Func<Product, bool>> AndThenLessOrEqual(this Expression<Func<Product, decimal>> price, decimal value) =>
        Expression.Lambda<Func<Product, bool>>(Expression.LessThanOrEqual(price.Body, Expression.Constant(value)), price.Parameters);
}
