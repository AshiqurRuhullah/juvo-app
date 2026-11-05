using Juvo.Domain.Common;

namespace Juvo.Domain.Catalog;

public enum ProductStatus
{
    Draft = 1,
    Published = 2,
    Archived = 3,
}

public enum TargetGroup
{
    Unisex = 0,
    Women = 1,
    Men = 2,
    Kids = 3,
}

/// <summary>Everything a staff member edits about a product except its variants and images.</summary>
public sealed record ProductDetails(
    string NameEn,
    string? NameBn,
    string Slug,
    Guid CategoryId,
    Guid? BrandId,
    string? ShortDescription,
    string? Description,
    string? Material,
    string? Care,
    string? Tags,
    TargetGroup TargetGroup,
    int? WeightGrams,
    decimal? LengthCm,
    decimal? WidthCm,
    decimal? HeightCm,
    decimal RegularPrice,
    decimal? SalePrice,
    DateTimeOffset? SaleStart,
    DateTimeOffset? SaleEnd,
    decimal? CostPrice,
    bool IsNew,
    bool IsFeatured,
    bool IsBestSeller,
    Guid? SizeChartId,
    string? MetaTitle,
    string? MetaDescription,
    string? SocialImagePath,
    string? VideoUrl);

/// <summary>A sellable product. Prices are tax-inclusive (assumptions, section C9). Publishing rules BR-B1/B2 live in the catalog service.</summary>
public sealed class Product : SoftDeletableEntity
{
    private readonly List<ProductVariant> _variants = [];
    private readonly List<ProductImage> _images = [];

    private Product()
    {
    }

    public Product(ProductDetails details) => Apply(details);

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public string Slug { get; private set; } = string.Empty;

    public Guid CategoryId { get; private set; }

    public Guid? BrandId { get; private set; }

    public string? ShortDescription { get; private set; }

    public string? Description { get; private set; }

    public string? Material { get; private set; }

    public string? Care { get; private set; }

    /// <summary>Comma separated tags.</summary>
    public string? Tags { get; private set; }

    public TargetGroup TargetGroup { get; private set; }

    public int? WeightGrams { get; private set; }

    public decimal? LengthCm { get; private set; }

    public decimal? WidthCm { get; private set; }

    public decimal? HeightCm { get; private set; }

    public ProductStatus Status { get; private set; } = ProductStatus.Draft;

    public decimal RegularPrice { get; private set; }

    public decimal? SalePrice { get; private set; }

    public DateTimeOffset? SaleStart { get; private set; }

    public DateTimeOffset? SaleEnd { get; private set; }

    /// <summary>Admin only (permission <c>catalog.product.view-cost</c>): used by the profit report.</summary>
    public decimal? CostPrice { get; private set; }

    public bool IsNew { get; private set; }

    public bool IsFeatured { get; private set; }

    public bool IsBestSeller { get; private set; }

    public Guid? SizeChartId { get; private set; }

    public string? MetaTitle { get; private set; }

    public string? MetaDescription { get; private set; }

    public string? SocialImagePath { get; private set; }

    public string? VideoUrl { get; private set; }

    public DateTimeOffset? PublishedAt { get; private set; }

    public IReadOnlyList<ProductVariant> Variants => _variants;

    public IReadOnlyList<ProductImage> Images => _images;

    /// <summary>The price a customer pays at <paramref name="now"/>: the sale price inside its dates, otherwise the regular price.</summary>
    public decimal EffectivePrice(DateTimeOffset now) =>
        SalePrice is { } sale && (SaleStart is null || SaleStart <= now) && (SaleEnd is null || SaleEnd >= now) ? sale : RegularPrice;

    public void Update(ProductDetails details) => Apply(details);

    public void Publish(DateTimeOffset now)
    {
        Status = ProductStatus.Published;
        PublishedAt ??= now;
    }

    public void Unpublish() => Status = ProductStatus.Draft;

    public void Archive() => Status = ProductStatus.Archived;

    public void AddVariant(ProductVariant variant) => _variants.Add(variant);

    private void Apply(ProductDetails d)
    {
        NameEn = d.NameEn.Trim();
        NameBn = Clean(d.NameBn);
        Slug = d.Slug;
        CategoryId = d.CategoryId;
        BrandId = d.BrandId;
        ShortDescription = Clean(d.ShortDescription);
        Description = Clean(d.Description);
        Material = Clean(d.Material);
        Care = Clean(d.Care);
        Tags = Clean(d.Tags);
        TargetGroup = d.TargetGroup;
        WeightGrams = d.WeightGrams;
        LengthCm = d.LengthCm;
        WidthCm = d.WidthCm;
        HeightCm = d.HeightCm;
        RegularPrice = d.RegularPrice;
        SalePrice = d.SalePrice;
        SaleStart = d.SaleStart;
        SaleEnd = d.SaleEnd;
        CostPrice = d.CostPrice;
        IsNew = d.IsNew;
        IsFeatured = d.IsFeatured;
        IsBestSeller = d.IsBestSeller;
        SizeChartId = d.SizeChartId;
        MetaTitle = Clean(d.MetaTitle);
        MetaDescription = Clean(d.MetaDescription);
        SocialImagePath = Clean(d.SocialImagePath);
        VideoUrl = Clean(d.VideoUrl);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A colour and size combination with its own SKU (unique across the system, BR-B3).</summary>
public sealed class ProductVariant : SoftDeletableEntity
{
    private ProductVariant()
    {
    }

    public ProductVariant(Guid productId, string sku, string? colorName, string? colorHex, string? size)
    {
        ProductId = productId;
        Sku = NormalizeSku(sku);
        ColorName = Clean(colorName);
        ColorHex = Clean(colorHex);
        Size = Clean(size);
    }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string? ColorName { get; private set; }

    /// <summary>Swatch such as <c>#1C0807</c>.</summary>
    public string? ColorHex { get; private set; }

    public string? Size { get; private set; }

    public decimal? PriceOverride { get; private set; }

    public string? Barcode { get; private set; }

    public int? WeightGramsOverride { get; private set; }

    /// <summary>Stock is tracked per branch (Step 6-c); the flag exists for non-stock items later.</summary>
    public bool TracksStock { get; private set; } = true;

    public bool IsActive { get; private set; } = true;

    public int SortOrder { get; private set; }

    public static string NormalizeSku(string sku) => sku.Trim().ToUpperInvariant();

    public void Update(string sku, string? colorName, string? colorHex, string? size, decimal? priceOverride, string? barcode,
        int? weightGramsOverride, bool isActive, int sortOrder)
    {
        Sku = NormalizeSku(sku);
        ColorName = Clean(colorName);
        ColorHex = Clean(colorHex);
        Size = Clean(size);
        PriceOverride = priceOverride;
        Barcode = Clean(barcode);
        WeightGramsOverride = weightGramsOverride;
        IsActive = isActive;
        SortOrder = sortOrder;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>One processed image of a product (the pipeline in Step 6-b fills the renditions). The first by <see cref="SortOrder"/> is primary.</summary>
public sealed class ProductImage : Entity
{
    private ProductImage()
    {
    }

    public ProductImage(Guid productId, string path, string? colorName, int sortOrder)
    {
        ProductId = productId;
        Path = path;
        ColorName = string.IsNullOrWhiteSpace(colorName) ? null : colorName.Trim();
        SortOrder = sortOrder;
    }

    public Guid ProductId { get; private set; }

    /// <summary>Logical storage path of the main rendition.</summary>
    public string Path { get; private set; } = string.Empty;

    /// <summary>Per-colour set: the colour label this image belongs to, or null for all colours.</summary>
    public string? ColorName { get; private set; }

    public int SortOrder { get; private set; }

    public string? AltText { get; private set; }

    /// <summary>Focal point for the 4:5 crop, 0 to 1 from the top-left.</summary>
    public double FocalX { get; private set; } = 0.5;

    public double FocalY { get; private set; } = 0.5;

    public int Width { get; private set; }

    public int Height { get; private set; }

    /// <summary>Tiny base64 WebP shown while the real image loads.</summary>
    public string? BlurDataUrl { get; private set; }

    public void SetOrder(int sortOrder) => SortOrder = sortOrder;
}
