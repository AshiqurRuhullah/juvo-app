using Juvo.Domain.Catalog;

namespace Juvo.Application.Catalog;

// ---- Categories -----------------------------------------------------------------------------------------

public sealed record CategoryDto(
    Guid Id, Guid? ParentId, string NameEn, string? NameBn, string Slug, string? ImagePath, int SortOrder, bool IsActive, int ProductCount);

public sealed record CategoryNodeDto(
    Guid Id, string NameEn, string? NameBn, string Slug, string? ImagePath, int SortOrder, bool IsActive, int ProductCount,
    IReadOnlyList<CategoryNodeDto> Children);

public sealed record SaveCategoryRequest(string NameEn, string? NameBn, string? Slug, Guid? ParentId, int SortOrder, bool IsActive = true);

public sealed record UpdateImageRequest(string? ColorName, string? AltText, double? FocalX, double? FocalY);

public sealed record ReorderRequest(IReadOnlyList<Guid> Ids);

// ---- Brands and size charts ------------------------------------------------------------------------------

public sealed record BrandDto(Guid Id, string NameEn, string? NameBn, string Slug, bool IsActive, int ProductCount);

public sealed record SaveBrandRequest(string NameEn, string? NameBn, string? Slug, bool IsActive = true);

public sealed record SizeChartDto(Guid Id, string NameEn, string? NameBn, string TableJson, Guid? CategoryId);

public sealed record SaveSizeChartRequest(string NameEn, string? NameBn, string TableJson, Guid? CategoryId);

// ---- Products -------------------------------------------------------------------------------------------

public sealed record ProductListQuery(ProductStatus? Status, Guid? CategoryId, Guid? BrandId, bool? Featured, bool? InStockOnly);

public sealed record ProductListItemDto(
    Guid Id, string NameEn, string? NameBn, string Slug, ProductStatus Status, Guid CategoryId, string CategoryName, string? BrandName,
    decimal RegularPrice, decimal? SalePrice, decimal EffectivePrice, string? PrimaryImagePath, int VariantCount, int ImageCount,
    bool IsNew, bool IsFeatured, bool IsBestSeller, DateTimeOffset? UpdatedAt);

public sealed record VariantDto(
    Guid Id, string Sku, string? ColorName, string? ColorHex, string? Size, decimal? PriceOverride, string? Barcode,
    int? WeightGramsOverride, bool TracksStock, bool IsActive, int SortOrder);

public sealed record ProductImageDto(
    Guid Id, string Path, string Url, string CardUrl, string ThumbUrl, string? ColorName, int SortOrder, string? AltText, double FocalX, double FocalY, int Width, int Height, string? BlurDataUrl);

public sealed record ProductDetailDto(
    Guid Id, string NameEn, string? NameBn, string Slug, Guid CategoryId, Guid? BrandId, string? ShortDescription, string? Description,
    string? Material, string? Care, string? Tags, TargetGroup TargetGroup, int? WeightGrams, decimal? LengthCm, decimal? WidthCm,
    decimal? HeightCm, ProductStatus Status, decimal RegularPrice, decimal? SalePrice, DateTimeOffset? SaleStart, DateTimeOffset? SaleEnd,
    decimal? CostPrice, bool CostHidden, bool IsNew, bool IsFeatured, bool IsBestSeller, Guid? SizeChartId, string? MetaTitle,
    string? MetaDescription, string? SocialImagePath, string? VideoUrl, DateTimeOffset? PublishedAt, DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt, byte[] RowVersion, IReadOnlyList<VariantDto> Variants, IReadOnlyList<ProductImageDto> Images,
    IReadOnlyList<string> PublishProblems);

public sealed record SaveProductRequest(
    string NameEn, string? NameBn, string? Slug, Guid CategoryId, Guid? BrandId, string? ShortDescription, string? Description,
    string? Material, string? Care, string? Tags, TargetGroup TargetGroup, int? WeightGrams, decimal? LengthCm, decimal? WidthCm,
    decimal? HeightCm, decimal RegularPrice, decimal? SalePrice, DateTimeOffset? SaleStart, DateTimeOffset? SaleEnd, decimal? CostPrice,
    bool IsNew, bool IsFeatured, bool IsBestSeller, Guid? SizeChartId, string? MetaTitle, string? MetaDescription, string? SocialImagePath,
    string? VideoUrl);

public sealed record SaveVariantRequest(
    string Sku, string? ColorName, string? ColorHex, string? Size, decimal? PriceOverride, string? Barcode, int? WeightGramsOverride,
    bool IsActive = true, int SortOrder = 0);

public sealed record DuplicateProductRequest(string? NameEn);
