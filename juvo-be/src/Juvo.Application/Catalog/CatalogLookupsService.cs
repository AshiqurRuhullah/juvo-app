using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Domain.Catalog;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Catalog;

/// <summary>Categories (tree), brands and size charts. The catalog is shared by every branch, so nothing here is branch scoped.</summary>
public sealed class CatalogLookupsService(IApplicationDbContext db)
{
    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    // ---- Categories -------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<CategoryNodeDto>>> CategoryTreeAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var rows = await db.Categories.AsNoTracking().Where(c => includeInactive || c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.NameEn).ToListAsync(cancellationToken);
        var counts = await ProductCountsByCategoryAsync(cancellationToken);

        var byParent = rows.ToLookup(c => c.ParentId);
        IReadOnlyList<CategoryNodeDto> Build(Guid? parent) =>
        [
            .. byParent[parent].Select(c => new CategoryNodeDto(c.Id, c.NameEn, c.NameBn, c.Slug, c.ImagePath, c.SortOrder, c.IsActive,
                counts.GetValueOrDefault(c.Id), Build(c.Id))),
        ];

        return Result.Success(Build(null));
    }

    public async Task<Result<CategoryDto>> GetCategoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var category = await db.Categories.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (category is null)
        {
            return CategoryNotFound();
        }

        var counts = await ProductCountsByCategoryAsync(cancellationToken);
        return ToDto(category, counts.GetValueOrDefault(id));
    }

    public async Task<Result<CategoryDto>> CreateCategoryAsync(SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var checkedRequest = await ValidateCategoryAsync(null, request, cancellationToken);
        if (checkedRequest.IsFailure)
        {
            return checkedRequest.Error!;
        }

        var category = new Category(request.NameEn, request.NameBn, checkedRequest.Value, request.ParentId);
        category.Update(request.NameEn, request.NameBn, checkedRequest.Value, request.ParentId, request.SortOrder, request.IsActive);
        db.Categories.Add(category);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(category, 0);
    }

    public async Task<Result<CategoryDto>> UpdateCategoryAsync(Guid id, SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (category is null)
        {
            return CategoryNotFound();
        }

        var slug = await ValidateCategoryAsync(id, request, cancellationToken);
        if (slug.IsFailure)
        {
            return slug.Error!;
        }

        category.Update(request.NameEn, request.NameBn, slug.Value, request.ParentId, request.SortOrder, request.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        var counts = await ProductCountsByCategoryAsync(cancellationToken);
        return ToDto(category, counts.GetValueOrDefault(id));
    }

    /// <summary>Sets the order of sibling categories: the position in <paramref name="request"/> becomes the sort order.</summary>
    public async Task<Result> ReorderCategoriesAsync(ReorderRequest request, CancellationToken cancellationToken)
    {
        var ids = request.Ids.Distinct().ToList();
        var rows = await db.Categories.Where(c => ids.Contains(c.Id)).ToListAsync(cancellationToken);
        if (rows.Count != ids.Count)
        {
            return CategoryNotFound();
        }

        for (var i = 0; i < ids.Count; i++)
        {
            rows.First(c => c.Id == ids[i]).SetSortOrder(i);
        }

        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> DeleteCategoryAsync(Guid id, CancellationToken cancellationToken)
    {
        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (category is null)
        {
            return CategoryNotFound();
        }

        if (await db.Categories.AnyAsync(c => c.ParentId == id, cancellationToken) ||
            await db.Products.AnyAsync(p => p.CategoryId == id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Catalog.CategoryInUse, "This category still has sub-categories or products. Move or remove them first, or deactivate the category.");
        }

        db.Categories.Remove(category); // soft delete
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result<string>> ValidateCategoryAsync(Guid? id, SaveCategoryRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NameEn) || request.NameEn.Trim().Length > 150)
        {
            return Invalid("nameEn", "Name is required (up to 150 characters).");
        }

        if (request.ParentId is { } parentId)
        {
            if (id == parentId)
            {
                return Error.Conflict(ErrorCodes.Catalog.CategoryCycle, "A category cannot be its own parent.");
            }

            var parents = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync(cancellationToken);
            if (!parents.Any(c => c.Id == parentId))
            {
                return Error.NotFound(ErrorCodes.Catalog.CategoryNotFound, "Parent category not found.");
            }

            // Walk up from the new parent: meeting the category itself means the move would create a loop.
            var cursor = (Guid?)parentId;
            var guard = 0;
            while (cursor is { } current && guard++ < 100)
            {
                if (current == id)
                {
                    return Error.Conflict(ErrorCodes.Catalog.CategoryCycle, "A category cannot be moved under its own sub-category.");
                }

                cursor = parents.First(c => c.Id == current).ParentId;
            }
        }

        return await SlugRules.ResolveAsync(request.Slug, request.NameEn, id,
            (slug, except, ct) => db.Categories.AnyAsync(c => c.Slug == slug && c.Id != except, ct), cancellationToken);
    }

    private async Task<Dictionary<Guid, int>> ProductCountsByCategoryAsync(CancellationToken cancellationToken) =>
        await db.Products.AsNoTracking().GroupBy(p => p.CategoryId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

    private static CategoryDto ToDto(Category c, int products) =>
        new(c.Id, c.ParentId, c.NameEn, c.NameBn, c.Slug, c.ImagePath, c.SortOrder, c.IsActive, products);

    private static Error CategoryNotFound() => Error.NotFound(ErrorCodes.Catalog.CategoryNotFound, "Category not found.");

    // ---- Brands -----------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<BrandDto>>> ListBrandsAsync(PageRequest request, CancellationToken cancellationToken)
    {
        var query = db.Brands.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(b => b.NameEn.Contains(term) || (b.NameBn != null && b.NameBn.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(b => b.NameEn).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var ids = rows.Select(b => b.Id).ToList();
        var counts = await db.Products.AsNoTracking().Where(p => p.BrandId != null && ids.Contains(p.BrandId.Value))
            .GroupBy(p => p.BrandId!.Value).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        return new PagedResult<BrandDto>([.. rows.Select(b => ToDto(b, counts.GetValueOrDefault(b.Id)))], total, page.Page, page.PageSize);
    }

    public async Task<Result<BrandDto>> CreateBrandAsync(SaveBrandRequest request, CancellationToken cancellationToken)
    {
        var slug = await ValidateBrandAsync(null, request, cancellationToken);
        if (slug.IsFailure)
        {
            return slug.Error!;
        }

        var brand = new Brand(request.NameEn, request.NameBn, slug.Value);
        brand.Update(request.NameEn, request.NameBn, slug.Value, request.IsActive);
        db.Brands.Add(brand);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(brand, 0);
    }

    public async Task<Result<BrandDto>> UpdateBrandAsync(Guid id, SaveBrandRequest request, CancellationToken cancellationToken)
    {
        var brand = await db.Brands.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (brand is null)
        {
            return BrandNotFound();
        }

        var slug = await ValidateBrandAsync(id, request, cancellationToken);
        if (slug.IsFailure)
        {
            return slug.Error!;
        }

        brand.Update(request.NameEn, request.NameBn, slug.Value, request.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        var count = await db.Products.CountAsync(p => p.BrandId == id, cancellationToken);
        return ToDto(brand, count);
    }

    public async Task<Result> DeleteBrandAsync(Guid id, CancellationToken cancellationToken)
    {
        var brand = await db.Brands.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (brand is null)
        {
            return BrandNotFound();
        }

        if (await db.Products.AnyAsync(p => p.BrandId == id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Catalog.BrandInUse, "Products still use this brand. Deactivate it instead.");
        }

        db.Brands.Remove(brand);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Result<string>> ValidateBrandAsync(Guid? id, SaveBrandRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NameEn) || request.NameEn.Trim().Length > 150)
        {
            return Invalid("nameEn", "Name is required (up to 150 characters).");
        }

        return await SlugRules.ResolveAsync(request.Slug, request.NameEn, id,
            (slug, except, ct) => db.Brands.AnyAsync(b => b.Slug == slug && b.Id != except, ct), cancellationToken);
    }

    private static BrandDto ToDto(Brand b, int products) => new(b.Id, b.NameEn, b.NameBn, b.Slug, b.IsActive, products);

    private static Error BrandNotFound() => Error.NotFound(ErrorCodes.Catalog.BrandNotFound, "Brand not found.");

    // ---- Size charts ------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<SizeChartDto>>> ListSizeChartsAsync(CancellationToken cancellationToken) =>
        Result.Success<IReadOnlyList<SizeChartDto>>([.. (await db.SizeCharts.AsNoTracking().OrderBy(s => s.NameEn).ToListAsync(cancellationToken)).Select(ToDto)]);

    public async Task<Result<SizeChartDto>> CreateSizeChartAsync(SaveSizeChartRequest request, CancellationToken cancellationToken)
    {
        var error = await ValidateSizeChartAsync(request, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        var chart = new SizeChart(request.NameEn, request.NameBn, request.TableJson, request.CategoryId);
        db.SizeCharts.Add(chart);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(chart);
    }

    public async Task<Result<SizeChartDto>> UpdateSizeChartAsync(Guid id, SaveSizeChartRequest request, CancellationToken cancellationToken)
    {
        var chart = await db.SizeCharts.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (chart is null)
        {
            return SizeChartNotFound();
        }

        var error = await ValidateSizeChartAsync(request, cancellationToken);
        if (error is not null)
        {
            return error;
        }

        chart.Update(request.NameEn, request.NameBn, request.TableJson, request.CategoryId);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(chart);
    }

    public async Task<Result> DeleteSizeChartAsync(Guid id, CancellationToken cancellationToken)
    {
        var chart = await db.SizeCharts.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (chart is null)
        {
            return SizeChartNotFound();
        }

        if (await db.Products.AnyAsync(p => p.SizeChartId == id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Catalog.CategoryInUse, "Products still use this size chart.");
        }

        db.SizeCharts.Remove(chart);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private async Task<Error?> ValidateSizeChartAsync(SaveSizeChartRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.NameEn) || request.NameEn.Trim().Length > 150)
        {
            return Invalid("nameEn", "Name is required (up to 150 characters).");
        }

        try
        {
            using var document = JsonDocument.Parse(request.TableJson);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("columns", out var columns) || columns.ValueKind != JsonValueKind.Array ||
                !root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array)
            {
                return Invalid("tableJson", "The table needs \"columns\" and \"rows\" lists.");
            }

            var width = columns.GetArrayLength();
            if (rows.EnumerateArray().Any(r => r.ValueKind != JsonValueKind.Array || r.GetArrayLength() != width))
            {
                return Invalid("tableJson", "Every row must have one cell per column.");
            }
        }
        catch (JsonException)
        {
            return Invalid("tableJson", "The table is not valid JSON.");
        }

        if (request.CategoryId is { } category && !await db.Categories.AnyAsync(c => c.Id == category, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Catalog.CategoryNotFound, "Category not found.");
        }

        return null;
    }

    private static SizeChartDto ToDto(SizeChart s) => new(s.Id, s.NameEn, s.NameBn, s.TableJson, s.CategoryId);

    private static Error SizeChartNotFound() => Error.NotFound(ErrorCodes.Catalog.SizeChartNotFound, "Size chart not found.");
}
