using Juvo.Application.Catalog;

namespace Juvo.Application.Storefront;

public sealed record StoreSwatch(string Name, string? Hex);

public sealed record StoreSize(string Label, bool SoldOut);

public sealed record StoreCategory(Guid Id, string Slug, string NameEn, string? NameBn, string? ImageUrl, int ProductCount, IReadOnlyList<StoreCategory> Children);

/// <summary>What the product card shows (SRS 3.4). Prices are tax-inclusive BDT; <c>Price</c> is what is paid now.</summary>
public sealed record StoreProductCard(
    Guid Id, string Slug, string NameEn, string? NameBn, string? Tagline, string CategorySlug, string CategoryName, string? Brand,
    decimal RegularPrice, decimal Price, bool OnSale, int DiscountPercent, string? ImageUrl, string? HoverImageUrl, string? BlurDataUrl,
    bool IsNew, bool IsFeatured, bool IsBestSeller, bool SoldOut, bool LowStock, IReadOnlyList<StoreSwatch> Swatches, IReadOnlyList<StoreSize> Sizes);

/// <summary><c>Available</c> is the combined number across the branches that sell online, capped (10 means ten or more).</summary>
public sealed record StoreVariant(Guid Id, string Sku, string? ColorName, string? ColorHex, string? Size, decimal Price, int Available, bool InStock, bool LowStock);

public sealed record StoreImage(Guid Id, string Url, string CardUrl, string ThumbUrl, string? ColorName, string? AltText, int Width, int Height, string? BlurDataUrl);

public sealed record StoreBreadcrumb(string Slug, string Name);

public sealed record StoreProductDetail(
    StoreProductCard Card, string? Description, string? Material, string? Care, string? Tags, string TargetGroup, int? WeightGrams, string? VideoUrl,
    string? MetaTitle, string? MetaDescription, string? SizeChartJson, IReadOnlyList<StoreBreadcrumb> Breadcrumb, IReadOnlyList<StoreVariant> Variants,
    IReadOnlyList<StoreImage> Images, IReadOnlyList<StoreProductCard> Related);

/// <summary>Filters and sort of the product list (FR-A04). <c>Sort</c>: newest (default), price-asc, price-desc, popular.</summary>
public sealed record StoreProductQuery(
    string? Category, decimal? MinPrice, decimal? MaxPrice, string? Size, string? Colour, string? Brand, bool? InStock, bool? OnSale, string? Sort, string? Q);

public sealed record StoreHome(
    IReadOnlyList<StoreProductCard> NewArrivals, IReadOnlyList<StoreProductCard> BestSellers, IReadOnlyList<StoreProductCard> Featured,
    IReadOnlyList<StoreProductCard> OnSale, IReadOnlyList<StoreCategory> Categories);

public sealed record StoreBrandFacet(string Slug, string Name);

public sealed record StoreFacets(IReadOnlyList<string> Sizes, IReadOnlyList<StoreSwatch> Colours, IReadOnlyList<StoreBrandFacet> Brands, decimal MinPrice, decimal MaxPrice);

public sealed record StoreSuggestProduct(string Slug, string Name, string? ImageUrl, decimal Price);

public sealed record StoreSuggestCategory(string Slug, string Name);

public sealed record StoreSuggestions(IReadOnlyList<StoreSuggestProduct> Products, IReadOnlyList<StoreSuggestCategory> Categories);
