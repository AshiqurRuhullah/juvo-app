using System.Text.RegularExpressions;
using Juvo.Application.Abstractions;
using Juvo.Application.Ums;
using Juvo.Domain.Catalog;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Catalog;

/// <summary>
/// Products, variants, pricing and the publish workflow (module B). BR-B1: publish needs a name, category, price, one image and one active
/// variant. BR-B2: the sale price is lower than the regular price. BR-B3: SKUs are unique. Every change is audited by the persistence layer (FR-B14).
/// </summary>
public sealed partial class ProductsService(IApplicationDbContext db, ActorContext actors, TimeProvider clock, IFileStorage storage)
{
    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColour();

    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error ProductNotFound() => Error.NotFound(ErrorCodes.Catalog.ProductNotFound, "Product not found.");

    // ---- Reading ----------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<ProductListItemDto>>> ListAsync(PageRequest request, ProductListQuery filter, CancellationToken cancellationToken)
    {
        var query = db.Products.AsNoTracking().AsQueryable();
        if (filter.Status is { } status)
        {
            query = query.Where(p => p.Status == status);
        }

        if (filter.CategoryId is { } categoryId)
        {
            var ids = await CategoryAndDescendantsAsync(categoryId, cancellationToken);
            query = query.Where(p => ids.Contains(p.CategoryId));
        }

        if (filter.BrandId is { } brandId)
        {
            query = query.Where(p => p.BrandId == brandId);
        }

        if (filter.Featured is { } featured)
        {
            query = query.Where(p => p.IsFeatured == featured);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(p => p.NameEn.Contains(term) || (p.NameBn != null && p.NameBn.Contains(term)) || p.Slug.Contains(term) || (p.Tags != null && p.Tags.Contains(term)) ||
                                     p.Variants.Any(v => v.Sku.Contains(term) || (v.Barcode != null && v.Barcode == term)));
        }

        var sort = request.ParseSort(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name", "price", "createdAt", "updatedAt" }) is { Count: > 0 } f ? f[0] : null;
        query = (sort?.Name.ToLowerInvariant(), sort?.Descending ?? true) switch
        {
            ("name", false) => query.OrderBy(p => p.NameEn),
            ("name", true) => query.OrderByDescending(p => p.NameEn),
            ("price", false) => query.OrderBy(p => p.RegularPrice),
            ("price", true) => query.OrderByDescending(p => p.RegularPrice),
            ("createdat", false) => query.OrderBy(p => p.CreatedAt),
            ("updatedat", false) => query.OrderBy(p => p.UpdatedAt ?? p.CreatedAt),
            ("updatedat", true) => query.OrderByDescending(p => p.UpdatedAt ?? p.CreatedAt),
            _ => query.OrderByDescending(p => p.CreatedAt),
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var rows = await query.Skip(page.Skip).Take(page.PageSize).Select(p => new
        {
            Product = p,
            Category = db.Categories.Where(c => c.Id == p.CategoryId).Select(c => c.NameEn).FirstOrDefault(),
            Brand = p.BrandId == null ? null : db.Brands.Where(b => b.Id == p.BrandId).Select(b => b.NameEn).FirstOrDefault(),
            Image = p.Images.OrderBy(i => i.SortOrder).Select(i => i.Path).FirstOrDefault(),
            Variants = p.Variants.Count,
            Images = p.Images.Count,
        }).ToListAsync(cancellationToken);

        return new PagedResult<ProductListItemDto>(
            [.. rows.Select(r => new ProductListItemDto(r.Product.Id, r.Product.NameEn, r.Product.NameBn, r.Product.Slug, r.Product.Status,
                r.Product.CategoryId, r.Category ?? string.Empty, r.Brand, r.Product.RegularPrice, r.Product.SalePrice,
                r.Product.EffectivePrice(now), r.Image, r.Variants, r.Images, r.Product.IsNew, r.Product.IsFeatured,
                r.Product.IsBestSeller, r.Product.UpdatedAt))],
            total, page.Page, page.PageSize);
    }

    public async Task<Result<ProductDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await LoadAsync(id, tracking: false, cancellationToken);
        return product is null ? ProductNotFound() : await ToDetailAsync(product, cancellationToken);
    }

    // ---- Product ----------------------------------------------------------------------------------------

    public async Task<Result<ProductDetailDto>> CreateAsync(SaveProductRequest request, CancellationToken cancellationToken)
    {
        var details = await ValidateAsync(null, request, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        var product = new Product(details.Value);
        if (request.NonReturnable is { } nonReturnable)
        {
            product.SetNonReturnable(nonReturnable);
        }

        db.Products.Add(product);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(product, cancellationToken);
    }

    public async Task<Result<ProductDetailDto>> UpdateAsync(Guid id, SaveProductRequest request, CancellationToken cancellationToken)
    {
        var product = await LoadAsync(id, tracking: true, cancellationToken);
        if (product is null)
        {
            return ProductNotFound();
        }

        var details = await ValidateAsync(id, request, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        var actor = await actors.GetAsync(cancellationToken);
        var canSeeCost = actor?.Access.HasAnywhere(Permissions.Catalog.ProductViewCost) == true;
        var next = canSeeCost ? details.Value : details.Value with { CostPrice = product.CostPrice }; // staff who cannot see the cost cannot change it either
        product.Update(next);
        if (request.NonReturnable is { } nonReturnable)
        {
            product.SetNonReturnable(nonReturnable);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(product, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        // Images are not loaded: they stay with the (soft-deleted) product and must not be touched by the delete cascade.
        var product = await db.Products.Include(p => p.Variants).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (product is null)
        {
            return ProductNotFound();
        }

        if (product.Status == ProductStatus.Published)
        {
            return Error.Conflict(ErrorCodes.Catalog.PublishRules, "A published product cannot be deleted. Archive it first.");
        }

        foreach (var variant in product.Variants)
        {
            db.ProductVariants.Remove(variant);
        }

        db.Products.Remove(product); // soft delete: orders and stock history keep their reference
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<ProductDetailDto>> PublishAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await LoadAsync(id, tracking: true, cancellationToken);
        if (product is null)
        {
            return ProductNotFound();
        }

        var problems = PublishProblems(product);
        if (problems.Count > 0)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["publish"] = [.. problems] },
                "The product cannot be published yet.");
        }

        product.Publish(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(product, cancellationToken);
    }

    public async Task<Result<ProductDetailDto>> UnpublishAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await LoadAsync(id, tracking: true, cancellationToken);
        if (product is null)
        {
            return ProductNotFound();
        }

        product.Unpublish();
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(product, cancellationToken);
    }

    public async Task<Result<ProductDetailDto>> ArchiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var product = await LoadAsync(id, tracking: true, cancellationToken);
        if (product is null)
        {
            return ProductNotFound();
        }

        product.Archive();
        await db.SaveChangesAsync(cancellationToken);
        return await ToDetailAsync(product, cancellationToken);
    }

    /// <summary>Copies a product (details, variants with new SKUs, image references) as a draft so similar items are quick to add (FR-B10).</summary>
    public async Task<Result<ProductDetailDto>> DuplicateAsync(Guid id, DuplicateProductRequest request, CancellationToken cancellationToken)
    {
        var source = await LoadAsync(id, tracking: false, cancellationToken);
        if (source is null)
        {
            return ProductNotFound();
        }

        var name = string.IsNullOrWhiteSpace(request.NameEn) ? source.NameEn + " (copy)" : request.NameEn.Trim();
        var slug = await SlugRules.ResolveAsync(null, name, null, (s, except, ct) => db.Products.AnyAsync(p => p.Slug == s, ct), cancellationToken);
        var details = new ProductDetails(name, source.NameBn, slug.Value, source.CategoryId, source.BrandId, source.ShortDescription, source.Description,
            source.Material, source.Care, source.Tags, source.TargetGroup, source.WeightGrams, source.LengthCm, source.WidthCm, source.HeightCm,
            source.RegularPrice, source.SalePrice, source.SaleStart, source.SaleEnd, source.CostPrice, false, false, false, source.SizeChartId,
            source.MetaTitle, source.MetaDescription, source.SocialImagePath, source.VideoUrl);
        var copy = new Product(details);
        copy.SetNonReturnable(source.NonReturnable);
        db.Products.Add(copy);

        foreach (var variant in source.Variants.OrderBy(v => v.SortOrder))
        {
            var sku = await FreeSkuAsync(variant.Sku, cancellationToken);
            var clone = new ProductVariant(copy.Id, sku, variant.ColorName, variant.ColorHex, variant.Size);
            clone.Update(sku, variant.ColorName, variant.ColorHex, variant.Size, variant.PriceOverride, null, variant.WeightGramsOverride, variant.IsActive, variant.SortOrder);
            db.ProductVariants.Add(clone);
        }

        foreach (var image in source.Images.OrderBy(i => i.SortOrder))
        {
            var imageCopy = new ProductImage(copy.Id, image.Path, image.ColorName, image.SortOrder);
            imageCopy.CopyMediaFrom(image);
            db.ProductImages.Add(imageCopy);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await GetAsync(copy.Id, cancellationToken);
    }

    // ---- Variants ---------------------------------------------------------------------------------------

    public async Task<Result<VariantDto>> AddVariantAsync(Guid productId, SaveVariantRequest request, CancellationToken cancellationToken)
    {
        var product = await LoadAsync(productId, tracking: false, cancellationToken);
        if (product is null)
        {
            return ProductNotFound();
        }

        if (product.Status == ProductStatus.Archived)
        {
            return Error.Conflict(ErrorCodes.Catalog.ProductArchived, "An archived product cannot be changed.");
        }

        var error = await ValidateVariantAsync(null, product, request, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var variant = new ProductVariant(productId, request.Sku, request.ColorName, request.ColorHex, request.Size);
        variant.Update(request.Sku, request.ColorName, request.ColorHex, request.Size, request.PriceOverride, request.Barcode,
            request.WeightGramsOverride, request.IsActive, request.SortOrder);
        db.ProductVariants.Add(variant);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(variant);
    }

    public async Task<Result<VariantDto>> UpdateVariantAsync(Guid productId, Guid variantId, SaveVariantRequest request, CancellationToken cancellationToken)
    {
        var variant = await db.ProductVariants.FirstOrDefaultAsync(v => v.Id == variantId && v.ProductId == productId, cancellationToken);
        if (variant is null)
        {
            return VariantNotFound();
        }

        var product = await LoadAsync(productId, tracking: false, cancellationToken);
        if (product!.Status == ProductStatus.Archived)
        {
            return Error.Conflict(ErrorCodes.Catalog.ProductArchived, "An archived product cannot be changed.");
        }

        var error = await ValidateVariantAsync(variantId, product, request, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        variant.Update(request.Sku, request.ColorName, request.ColorHex, request.Size, request.PriceOverride, request.Barcode,
            request.WeightGramsOverride, request.IsActive, request.SortOrder);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(variant);
    }

    public async Task<Result> RemoveVariantAsync(Guid productId, Guid variantId, CancellationToken cancellationToken)
    {
        var variant = await db.ProductVariants.FirstOrDefaultAsync(v => v.Id == variantId && v.ProductId == productId, cancellationToken);
        if (variant is null)
        {
            return VariantNotFound();
        }

        var product = await LoadAsync(productId, tracking: false, cancellationToken);
        if (product!.Status == ProductStatus.Published && product.Variants.Count(v => v.IsActive) <= 1 && variant.IsActive)
        {
            return Error.Conflict(ErrorCodes.Catalog.PublishRules, "A published product needs at least one variant. Unpublish it first.");
        }

        // Stock in any branch must be moved or written off first, so units never become invisible. The check looks across branches.
        if (await db.StockLevels.IgnoreQueryFilters().AnyAsync(l => l.VariantId == variantId && (l.OnHand > 0 || l.Reserved > 0), cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Inventory.VariantHasStock, "This variant still has stock. Move or write it off before removing the variant.");
        }

        db.ProductVariants.Remove(variant); // soft delete: frees the SKU, keeps stock and order history readable
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static Error VariantNotFound() => Error.NotFound(ErrorCodes.Catalog.VariantNotFound, "Variant not found.");

    private async Task<Error?> ValidateVariantAsync(Guid? variantId, Product product, SaveVariantRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        var sku = ProductVariant.NormalizeSku(request.Sku ?? string.Empty);
        if (sku.Length is 0 or > 60 || sku.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.')))
        {
            errors["sku"] = ["SKU is required: letters, digits, hyphen, underscore or dot, up to 60 characters."];
        }

        if (!string.IsNullOrWhiteSpace(request.ColorHex) && !HexColour().IsMatch(request.ColorHex.Trim()))
        {
            errors["colorHex"] = ["Use a colour such as #1C0807."];
        }

        if (request.PriceOverride is { } price && price < 0)
        {
            errors["priceOverride"] = ["Price cannot be negative."];
        }

        if (request.WeightGramsOverride is < 0)
        {
            errors["weightGramsOverride"] = ["Weight cannot be negative."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation(errors);
        }

        var except = variantId ?? Guid.Empty;
        if (await db.ProductVariants.AnyAsync(v => v.Sku == sku && v.Id != except, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Catalog.SkuTaken, "This SKU is already used by another variant.");
        }

        // The same colour and size twice on one product is almost always a mistake.
        var color = request.ColorName?.Trim();
        var size = request.Size?.Trim();
        if (await db.ProductVariants.AnyAsync(v => v.ProductId == product.Id && v.Id != except && v.ColorName == color && v.Size == size, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Catalog.SkuTaken, "This product already has a variant with the same colour and size.");
        }

        return null;
    }

    private async Task<string> FreeSkuAsync(string baseSku, CancellationToken cancellationToken)
    {
        var candidate = ProductVariant.NormalizeSku(baseSku) + "-COPY";
        for (var n = 2; await db.ProductVariants.AnyAsync(v => v.Sku == candidate, cancellationToken); n++)
        {
            candidate = ProductVariant.NormalizeSku(baseSku) + "-COPY" + n;
        }

        return candidate;
    }

    // ---- Shared -----------------------------------------------------------------------------------------

    private async Task<Result<ProductDetails>> ValidateAsync(Guid? id, SaveProductRequest request, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.NameEn) || request.NameEn.Trim().Length > 200)
        {
            errors["nameEn"] = ["Name is required (up to 200 characters)."];
        }

        if (request.RegularPrice < 0)
        {
            errors["regularPrice"] = ["Price cannot be negative."];
        }

        if (request.SalePrice is { } sale)
        {
            if (sale < 0 || sale >= request.RegularPrice)
            {
                errors["salePrice"] = ["The sale price must be lower than the regular price."];
            }
        }
        else if (request.SaleStart is not null || request.SaleEnd is not null)
        {
            errors["salePrice"] = ["Enter a sale price or clear the sale dates."];
        }

        if (request.SaleStart is { } start && request.SaleEnd is { } end && end <= start)
        {
            errors["saleEnd"] = ["The sale must end after it starts."];
        }

        if (request.CostPrice is < 0)
        {
            errors["costPrice"] = ["Cost cannot be negative."];
        }

        if (request.WeightGrams is < 0 || request.LengthCm is < 0 || request.WidthCm is < 0 || request.HeightCm is < 0)
        {
            errors["weightGrams"] = ["Weight and size cannot be negative."];
        }

        if (!string.IsNullOrWhiteSpace(request.MetaTitle) && request.MetaTitle.Length > 120)
        {
            errors["metaTitle"] = ["Up to 120 characters."];
        }

        if (!string.IsNullOrWhiteSpace(request.MetaDescription) && request.MetaDescription.Length > 320)
        {
            errors["metaDescription"] = ["Up to 320 characters."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation(errors);
        }

        if (!await db.Categories.AnyAsync(c => c.Id == request.CategoryId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Catalog.CategoryNotFound, "Category not found.");
        }

        if (request.BrandId is { } brand && !await db.Brands.AnyAsync(b => b.Id == brand, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Catalog.BrandNotFound, "Brand not found.");
        }

        if (request.SizeChartId is { } chart && !await db.SizeCharts.AnyAsync(s => s.Id == chart, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Catalog.SizeChartNotFound, "Size chart not found.");
        }

        var slug = await SlugRules.ResolveAsync(request.Slug, request.NameEn, id,
            (s, except, ct) => db.Products.AnyAsync(p => p.Slug == s && p.Id != except, ct), cancellationToken);
        if (slug.IsFailure)
        {
            return slug.Error!;
        }

        return new ProductDetails(request.NameEn, request.NameBn, slug.Value, request.CategoryId, request.BrandId, request.ShortDescription,
            request.Description, request.Material, request.Care, request.Tags, request.TargetGroup, request.WeightGrams, request.LengthCm,
            request.WidthCm, request.HeightCm, request.RegularPrice, request.SalePrice, request.SaleStart, request.SaleEnd, request.CostPrice,
            request.IsNew, request.IsFeatured, request.IsBestSeller, request.SizeChartId, request.MetaTitle, request.MetaDescription,
            request.SocialImagePath, request.VideoUrl);
    }

    /// <summary>BR-B1 (BR-B2 is enforced when saving): what is still missing before the product can go live.</summary>
    public static IReadOnlyList<string> PublishProblems(Product product)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(product.NameEn))
        {
            problems.Add("Add a name.");
        }

        if (product.CategoryId == Guid.Empty)
        {
            problems.Add("Choose a category.");
        }

        if (product.RegularPrice <= 0)
        {
            problems.Add("Set a price above zero.");
        }

        if (product.Images.Count == 0)
        {
            problems.Add("Add at least one image.");
        }

        if (!product.Variants.Any(v => v.IsActive))
        {
            problems.Add("Add at least one active variant (stock may be zero).");
        }

        return problems;
    }

    private async Task<Product?> LoadAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = db.Products.Include(p => p.Variants).Include(p => p.Images).AsQueryable();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        return await query.FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
    }

    private async Task<List<Guid>> CategoryAndDescendantsAsync(Guid categoryId, CancellationToken cancellationToken)
    {
        var all = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync(cancellationToken);
        var result = new List<Guid> { categoryId };
        for (var i = 0; i < result.Count; i++)
        {
            result.AddRange(all.Where(c => c.ParentId == result[i]).Select(c => c.Id));
        }

        return result;
    }

    private async Task<ProductDetailDto> ToDetailAsync(Product p, CancellationToken cancellationToken)
    {
        var actor = await actors.GetAsync(cancellationToken);
        var canSeeCost = actor?.Access.HasAnywhere(Permissions.Catalog.ProductViewCost) == true;
        return new ProductDetailDto(p.Id, p.NameEn, p.NameBn, p.Slug, p.CategoryId, p.BrandId, p.ShortDescription, p.Description, p.Material, p.Care,
            p.Tags, p.TargetGroup, p.WeightGrams, p.LengthCm, p.WidthCm, p.HeightCm, p.Status, p.RegularPrice, p.SalePrice, p.SaleStart, p.SaleEnd,
            canSeeCost ? p.CostPrice : null, !canSeeCost, p.IsNew, p.IsFeatured, p.IsBestSeller, p.SizeChartId, p.MetaTitle, p.MetaDescription,
            p.SocialImagePath, p.VideoUrl, p.PublishedAt, p.CreatedAt, p.UpdatedAt, p.RowVersion,
            [.. p.Variants.Where(v => !v.IsDeleted).OrderBy(v => v.SortOrder).ThenBy(v => v.Sku).Select(ToDto)],
            [.. p.Images.OrderBy(i => i.SortOrder).Select(i => ProductMediaService.ToDto(i, storage))],
            PublishProblems(p), p.NonReturnable);
    }

    private static VariantDto ToDto(ProductVariant v) =>
        new(v.Id, v.Sku, v.ColorName, v.ColorHex, v.Size, v.PriceOverride, v.Barcode, v.WeightGramsOverride, v.TracksStock, v.IsActive, v.SortOrder);
}
