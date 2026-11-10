using System.Globalization;
using System.Text;
using Juvo.Application.Abstractions;
using Juvo.Application.Inventory;
using Juvo.Application.Ums;
using Juvo.Domain.Catalog;
using Juvo.Domain.Inventory;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Catalog;

/// <summary>
/// Bulk import and export of products, variants and opening stock as CSV or Excel (FR-B09). One row is one variant; rows with the same
/// slug (or name) form one product. The import goes through the same services as the admin screens, so it applies exactly the same
/// rules (prices, SKUs, slugs, stock), inside one database transaction that is rolled back when the file has any error or when it is
/// a dry run. Images are not part of the file: they are uploaded per product afterwards.
/// </summary>
public sealed class ProductImportService(
    IApplicationDbContext db, ProductsService products, CatalogLookupsService lookups, StockService stock, InventoryAccess inventoryAccess,
    ActorContext actors, ITabularFile files, IUnitOfWork unitOfWork, IBranchContext branchContext, TimeProvider clock)
{
    public const long MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 5000;
    public const int MaxExportRows = 20_000;
    private const int MaxIssues = 500;
    private static readonly TimeSpan Dhaka = TimeSpan.FromHours(6);

    private static readonly string[] ProductColumns =
    [
        "slug", "name_en", "name_bn", "category", "brand", "short_description", "description", "material", "care", "tags", "target_group",
        "weight_g", "length_cm", "width_cm", "height_cm", "regular_price", "sale_price", "sale_start", "sale_end", "cost_price", "is_new",
        "is_featured", "is_best_seller", "size_chart", "meta_title", "meta_description",
    ];

    private static readonly string[] VariantColumns =
        ["sku", "color_name", "color_hex", "size", "price_override", "barcode", "variant_weight_g", "opening_stock", "branch_code"];

    /// <summary>Written by the export for information; ignored (without a warning) on import.</summary>
    private static readonly string[] InfoColumns = ["status", "on_hand", "reserved"];

    private static readonly HashSet<string> NumericColumns = new(StringComparer.Ordinal)
    {
        "weight_g", "length_cm", "width_cm", "height_cm", "regular_price", "sale_price", "cost_price", "price_override", "variant_weight_g",
        "opening_stock", "on_hand", "reserved",
    };

    // ---- Import -----------------------------------------------------------------------------------------

    public async Task<Result<ImportReportDto>> ImportAsync(Stream upload, ImportMode mode, bool dryRun, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var bytes = await ReadBoundedAsync(upload, cancellationToken);
        if (bytes is null)
        {
            return FileError($"The file must be {MaxBytes / (1024 * 1024)} MB or smaller.");
        }

        var format = files.Detect(bytes);
        TableData table;
        try
        {
            using var stream = new MemoryStream(bytes);
            table = files.Read(stream, format, MaxRows);
        }
        catch (TabularFileException ex)
        {
            return FileError(ex.Message);
        }

        var log = new ImportLog();
        var headers = table.Headers.Select(NormaliseHeader).ToList();
        var known = ProductColumns.Concat(VariantColumns).Concat(InfoColumns).ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[] { "name_en", "category", "regular_price" })
        {
            if (!headers.Contains(required))
            {
                return FileError($"The column \"{required}\" is missing. Download the template to see the expected columns.");
            }
        }

        for (var i = 0; i < headers.Count; i++)
        {
            if (!known.Contains(headers[i]))
            {
                log.Add(1, table.Headers[i], false, "Unknown column, ignored.");
            }
            else if (headers.IndexOf(headers[i]) != i)
            {
                return FileError($"The column \"{headers[i]}\" appears twice.");
            }
        }

        var rows = table.Rows.Select((cells, i) => new RawRow(i + 2, headers, cells)).Where(r => !r.IsEmpty).ToList();
        var canSeeCost = actor.Access.HasAnywhere(Permissions.Catalog.ProductViewCost);
        if (!canSeeCost && headers.Contains("cost_price") && rows.Any(r => r.Get("cost_price") is not null))
        {
            log.Add(0, "cost_price", false, "You do not have permission to see or change cost prices; the cost column is ignored.");
        }

        var groups = GroupRows(rows, log);
        var refs = await LoadReferencesAsync(cancellationToken);
        var counts = new ImportCounts();
        var pendingStock = new List<PendingStock>();

        try
        {
            await unitOfWork.ExecuteInTransactionAsync<bool>(async ct =>
            {
                counts.Reset();
                pendingStock.Clear();
                var createdBrands = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in groups)
                {
                    await ApplyGroupAsync(group, mode, canSeeCost, refs, createdBrands, log, counts, pendingStock, ct);
                }

                await ApplyStockAsync(pendingStock, refs, log, counts, ct);
                if (log.ErrorCount > 0 || dryRun)
                {
                    throw new ImportRollback();
                }

                return true;
            }, cancellationToken);
        }
        catch (ImportRollback)
        {
            // Nothing was saved: the transaction is rolled back. The report says what happened (or would have).
        }

        var committed = !dryRun && log.ErrorCount == 0;
        return log.ToReport(dryRun, committed, mode, format.ToString(), rows.Count, counts);
    }

    private static Error FileError(string message) =>
        Error.Validation(new Dictionary<string, string[]> { ["file"] = [message] }, message);

    private static async Task<byte[]?> ReadBoundedAsync(Stream content, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBytes)
            {
                return null;
            }
        }

        return buffer.ToArray();
    }

    private static string NormaliseHeader(string header) =>
        header.Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');

    // ---- Grouping and parsing ---------------------------------------------------------------------------

    private static List<Group> GroupRows(List<RawRow> rows, ImportLog log)
    {
        var groups = new List<Group>();
        var byKey = new Dictionary<string, Group>(StringComparer.Ordinal);
        var skus = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var row in rows)
        {
            var slugText = row.Get("slug");
            var nameText = row.Get("name_en");
            var key = SlugRules.Slugify(slugText ?? nameText);
            if (key.Length == 0)
            {
                log.Add(row.Number, slugText is null && nameText is null ? "name_en" : "slug", true,
                    slugText is null && nameText is null ? "name_en is required." : "A slug or an English name with letters or digits is required.");
                continue;
            }

            if (!byKey.TryGetValue(key, out var group))
            {
                group = new Group(key, row, ParseProduct(row, key, log));
                byKey[key] = group;
                groups.Add(group);
            }
            else
            {
                foreach (var column in ProductColumns.Where(c => row.Get(c) is { } v && group.First.Get(c) is { } first && !string.Equals(v, first, StringComparison.Ordinal)))
                {
                    log.Add(row.Number, column, false, $"Differs from row {group.First.Number}; the value from row {group.First.Number} is used.");
                }
            }

            var sku = row.Get("sku");
            if (sku is null)
            {
                if (HasVariantData(row))
                {
                    log.Add(row.Number, "sku", true, "A SKU is required when the row has variant data.");
                }

                continue;
            }

            var normalised = ProductVariant.NormalizeSku(sku);
            if (skus.TryGetValue(normalised, out var firstRow))
            {
                log.Add(row.Number, "sku", true, $"The SKU {normalised} is already used in row {firstRow}.");
                continue;
            }

            skus[normalised] = row.Number;
            group.Variants.Add(ParseVariant(row, normalised, log));
        }

        return groups;
    }

    private static bool HasVariantData(RawRow row) =>
        VariantColumns.Where(c => c != "sku").Any(c => row.Get(c) is not null);

    private static ParsedProduct ParseProduct(RawRow row, string key, ImportLog log)
    {
        var p = new ParsedProduct
        {
            Key = key,
            Row = row.Number,
            Slug = row.Get("slug") is { } s ? SlugRules.Slugify(s) : null,
            NameEn = row.Get("name_en"),
            NameBn = row.Get("name_bn"),
            CategoryText = row.Get("category"),
            BrandName = row.Get("brand"),
            ShortDescription = row.Get("short_description"),
            Description = row.Get("description"),
            Material = row.Get("material"),
            Care = row.Get("care"),
            Tags = row.Get("tags"),
            SizeChartName = row.Get("size_chart"),
            MetaTitle = row.Get("meta_title"),
            MetaDescription = row.Get("meta_description"),
            WeightG = row.Int("weight_g", log),
            LengthCm = row.Decimal("length_cm", log),
            WidthCm = row.Decimal("width_cm", log),
            HeightCm = row.Decimal("height_cm", log),
            RegularPrice = row.Decimal("regular_price", log),
            SalePrice = row.Decimal("sale_price", log),
            SaleStart = row.Date("sale_start", endOfDay: false, log),
            SaleEnd = row.Date("sale_end", endOfDay: true, log),
            CostPrice = row.Decimal("cost_price", log),
            IsNew = row.Bool("is_new", log),
            IsFeatured = row.Bool("is_featured", log),
            IsBestSeller = row.Bool("is_best_seller", log),
        };

        if (row.Get("target_group") is { } target)
        {
            if (Enum.TryParse<TargetGroup>(target, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
            {
                p.Target = parsed;
            }
            else
            {
                log.Add(row.Number, "target_group", true, "Use Unisex, Women, Men or Kids.");
            }
        }

        return p;
    }

    private static ParsedVariant ParseVariant(RawRow row, string sku, ImportLog log)
    {
        var stock = row.Int("opening_stock", log);
        if (stock is < 0)
        {
            log.Add(row.Number, "opening_stock", true, "Opening stock cannot be negative.");
            stock = null;
        }

        return new ParsedVariant
        {
            Row = row.Number,
            Sku = sku,
            Color = row.Get("color_name"),
            Hex = row.Get("color_hex"),
            Size = row.Get("size"),
            PriceOverride = row.Decimal("price_override", log),
            Barcode = row.Get("barcode"),
            WeightG = row.Int("variant_weight_g", log),
            OpeningStock = stock,
            BranchCode = row.Get("branch_code")?.ToUpperInvariant(),
        };
    }

    // ---- Applying ---------------------------------------------------------------------------------------

    private async Task<References> LoadReferencesAsync(CancellationToken cancellationToken)
    {
        var categories = await db.Categories.AsNoTracking().Select(c => new CategoryRef(c.Id, c.NameEn, c.Slug, c.ParentId)).ToListAsync(cancellationToken);
        var brands = await db.Brands.AsNoTracking().Select(b => new NameRef(b.Id, b.NameEn, b.Slug)).ToListAsync(cancellationToken);
        var charts = await db.SizeCharts.AsNoTracking().Select(s => new NameRef(s.Id, s.NameEn, string.Empty)).ToListAsync(cancellationToken);
        var branches = await db.Branches.AsNoTracking().Where(b => b.IsActive).Select(b => new BranchRef(b.Id, b.Code)).ToListAsync(cancellationToken);
        return new References(categories, brands, charts, branches);
    }

    private async Task ApplyGroupAsync(
        Group group, ImportMode mode, bool canSeeCost, References refs, Dictionary<string, Guid> createdBrands, ImportLog log, ImportCounts counts,
        List<PendingStock> pendingStock, CancellationToken ct)
    {
        var p = group.Product;
        var errorsBefore = log.ErrorCount;

        var existing = await db.Products.AsNoTracking().Where(pr => pr.Slug == group.Key).Select(pr => new { pr.Id, pr.Status }).FirstOrDefaultAsync(ct);
        if (existing is not null && mode == ImportMode.Create)
        {
            log.Add(p.Row, "slug", true, $"A product with the slug \"{group.Key}\" already exists. Use the update mode to change it.");
            return;
        }

        if (existing is { Status: ProductStatus.Archived })
        {
            log.Add(p.Row, "slug", true, "This product is archived and cannot be changed.");
            return;
        }

        // References (category, brand, size chart) are resolved first so every problem of the row is reported together.
        Guid? categoryId = null;
        if (p.CategoryText is { } categoryText)
        {
            categoryId = ResolveCategory(categoryText, refs.Categories, p.Row, log);
        }
        else if (existing is null)
        {
            log.Add(p.Row, "category", true, "category is required.");
        }

        Guid? brandId = null;
        if (p.BrandName is { } brandName)
        {
            brandId = refs.Brands.FirstOrDefault(b => string.Equals(b.Name, brandName, StringComparison.OrdinalIgnoreCase) || b.Slug == SlugRules.Slugify(brandName))?.Id;
            if (brandId is null && createdBrands.TryGetValue(brandName, out var known))
            {
                brandId = known;
            }

            if (brandId is null)
            {
                var created = await lookups.CreateBrandAsync(new SaveBrandRequest(brandName, null, null), ct);
                if (created.IsFailure)
                {
                    AddError(log, p.Row, created.Error!, "brand");
                }
                else
                {
                    brandId = created.Value.Id;
                    createdBrands[brandName] = brandId.Value;
                    log.Add(p.Row, "brand", false, $"The brand \"{brandName}\" did not exist and was created.");
                }
            }
        }
        Guid? chartId = null;
        if (p.SizeChartName is { } chartName)
        {
            chartId = refs.SizeCharts.FirstOrDefault(s => string.Equals(s.Name, chartName, StringComparison.OrdinalIgnoreCase))?.Id;
            if (chartId is null)
            {
                log.Add(p.Row, "size_chart", true, $"Size chart \"{chartName}\" was not found.");
            }
        }

        if (existing is null && p.RegularPrice is null)
        {
            log.Add(p.Row, "regular_price", true, "regular_price is required for a new product.");
        }

        if (log.ErrorCount > errorsBefore)
        {
            return;
        }

        Guid productId;
        IReadOnlyList<VariantDto> currentVariants = [];
        if (existing is null)
        {
            var request = new SaveProductRequest(p.NameEn!, p.NameBn, group.Key, categoryId!.Value, brandId, p.ShortDescription, p.Description, p.Material, p.Care,
                p.Tags, p.Target ?? TargetGroup.Unisex, p.WeightG, p.LengthCm, p.WidthCm, p.HeightCm, p.RegularPrice!.Value, p.SalePrice, p.SaleStart, p.SaleEnd,
                canSeeCost ? p.CostPrice : null, p.IsNew ?? false, p.IsFeatured ?? false, p.IsBestSeller ?? false, chartId, p.MetaTitle, p.MetaDescription, null, null);
            var created = await products.CreateAsync(request, ct);
            if (created.IsFailure)
            {
                AddError(log, p.Row, created.Error!, null);
                return;
            }

            productId = created.Value.Id;
            counts.ProductsCreated++;
        }
        else
        {
            productId = existing.Id;
            var current = await products.GetAsync(productId, ct);
            if (current.IsFailure)
            {
                AddError(log, p.Row, current.Error!, null);
                return;
            }

            var d = current.Value;
            currentVariants = d.Variants;
            var before = new SaveProductRequest(d.NameEn, d.NameBn, d.Slug, d.CategoryId, d.BrandId, d.ShortDescription, d.Description, d.Material, d.Care, d.Tags,
                d.TargetGroup, d.WeightGrams, d.LengthCm, d.WidthCm, d.HeightCm, d.RegularPrice, d.SalePrice, d.SaleStart, d.SaleEnd, d.CostPrice, d.IsNew, d.IsFeatured,
                d.IsBestSeller, d.SizeChartId, d.MetaTitle, d.MetaDescription, d.SocialImagePath, d.VideoUrl);
            var after = before with
            {
                NameEn = p.NameEn ?? before.NameEn,
                NameBn = p.NameBn ?? before.NameBn,
                CategoryId = categoryId ?? before.CategoryId,
                BrandId = brandId ?? before.BrandId,
                ShortDescription = p.ShortDescription ?? before.ShortDescription,
                Description = p.Description ?? before.Description,
                Material = p.Material ?? before.Material,
                Care = p.Care ?? before.Care,
                Tags = p.Tags ?? before.Tags,
                TargetGroup = p.Target ?? before.TargetGroup,
                WeightGrams = p.WeightG ?? before.WeightGrams,
                LengthCm = p.LengthCm ?? before.LengthCm,
                WidthCm = p.WidthCm ?? before.WidthCm,
                HeightCm = p.HeightCm ?? before.HeightCm,
                RegularPrice = p.RegularPrice ?? before.RegularPrice,
                SalePrice = p.SalePrice ?? before.SalePrice,
                SaleStart = p.SaleStart ?? before.SaleStart,
                SaleEnd = p.SaleEnd ?? before.SaleEnd,
                CostPrice = canSeeCost ? p.CostPrice ?? before.CostPrice : before.CostPrice,
                IsNew = p.IsNew ?? before.IsNew,
                IsFeatured = p.IsFeatured ?? before.IsFeatured,
                IsBestSeller = p.IsBestSeller ?? before.IsBestSeller,
                SizeChartId = chartId ?? before.SizeChartId,
                MetaTitle = p.MetaTitle ?? before.MetaTitle,
                MetaDescription = p.MetaDescription ?? before.MetaDescription,
            };

            if (after != before)
            {
                var updated = await products.UpdateAsync(productId, after, ct);
                if (updated.IsFailure)
                {
                    AddError(log, p.Row, updated.Error!, null);
                    return;
                }

                counts.ProductsUpdated++;
            }
        }

        var order = 0;
        foreach (var v in group.Variants)
        {
            var found = currentVariants.FirstOrDefault(x => string.Equals(x.Sku, v.Sku, StringComparison.OrdinalIgnoreCase));
            Guid variantId;
            if (found is null)
            {
                var added = await products.AddVariantAsync(productId,
                    new SaveVariantRequest(v.Sku, v.Color, v.Hex, v.Size, v.PriceOverride, v.Barcode, v.WeightG, true, order), ct);
                if (added.IsFailure)
                {
                    AddError(log, v.Row, added.Error!, null);
                    order++;
                    continue;
                }

                variantId = added.Value.Id;
                counts.VariantsCreated++;
            }
            else
            {
                variantId = found.Id;
                var before = new SaveVariantRequest(found.Sku, found.ColorName, found.ColorHex, found.Size, found.PriceOverride, found.Barcode, found.WeightGramsOverride,
                    found.IsActive, found.SortOrder);
                var after = before with
                {
                    ColorName = v.Color ?? before.ColorName,
                    ColorHex = v.Hex ?? before.ColorHex,
                    Size = v.Size ?? before.Size,
                    PriceOverride = v.PriceOverride ?? before.PriceOverride,
                    Barcode = v.Barcode ?? before.Barcode,
                    WeightGramsOverride = v.WeightG ?? before.WeightGramsOverride,
                };
                if (after != before)
                {
                    var updated = await products.UpdateVariantAsync(productId, variantId, after, ct);
                    if (updated.IsFailure)
                    {
                        AddError(log, v.Row, updated.Error!, null);
                        order++;
                        continue;
                    }

                    counts.VariantsUpdated++;
                }
            }

            if (v.OpeningStock is > 0)
            {
                pendingStock.Add(new PendingStock(v.Row, variantId, v.Sku, v.OpeningStock.Value, v.BranchCode));
            }

            order++;
        }
    }

    private async Task ApplyStockAsync(List<PendingStock> pending, References refs, ImportLog log, ImportCounts counts, CancellationToken ct)
    {
        foreach (var byBranch in pending.GroupBy(s => s.BranchCode ?? string.Empty))
        {
            var code = byBranch.Key;
            Guid branchId;
            if (code.Length == 0)
            {
                if (branchContext.ActiveBranchId is not { } active)
                {
                    foreach (var row in byBranch)
                    {
                        log.Add(row.Row, "branch_code", true, "branch_code is required for opening stock (no active branch is selected).");
                    }

                    continue;
                }

                branchId = active;
            }
            else if (refs.Branches.FirstOrDefault(b => b.Code == code) is { } branch)
            {
                branchId = branch.Id;
            }
            else
            {
                foreach (var row in byBranch)
                {
                    log.Add(row.Row, "branch_code", true, $"Branch \"{code}\" was not found or is not active.");
                }

                continue;
            }

            // Opening stock only fills an empty history: a variant that already has stock movements keeps its numbers.
            var variantIds = byBranch.Select(s => s.VariantId).ToList();
            var withHistory = (await db.StockMovements.AsNoTracking().Where(m => m.BranchId == branchId && variantIds.Contains(m.VariantId)).Select(m => m.VariantId).Distinct().ToListAsync(ct)).ToHashSet();
            var lines = new List<AdjustLine>();
            var rowsOfLines = new List<PendingStock>();
            foreach (var s in byBranch)
            {
                if (withHistory.Contains(s.VariantId))
                {
                    log.Add(s.Row, "opening_stock", false, $"{s.Sku} already has stock history in this branch; the opening stock was skipped. Use an adjustment or a stock count.");
                    continue;
                }

                lines.Add(new AdjustLine(s.VariantId, s.Quantity));
                rowsOfLines.Add(s);
            }

            if (lines.Count == 0)
            {
                continue;
            }

            var result = await stock.AdjustAsync(new AdjustStockRequest(branchId, StockReason.OpeningStock, "Imported opening stock", lines), ct);
            if (result.IsFailure)
            {
                foreach (var s in rowsOfLines)
                {
                    log.Add(s.Row, "opening_stock", true, result.Error!.Type == ErrorType.Forbidden || result.Error.Type == ErrorType.NotFound
                        ? "You do not have permission to set stock in this branch."
                        : result.Error.Message);
                }
            }
            else
            {
                counts.StockLinesApplied += lines.Count;
            }
        }
    }

    private static Guid? ResolveCategory(string text, List<CategoryRef> categories, int row, ImportLog log)
    {
        var path = text.Split('>', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (path.Length == 0)
        {
            log.Add(row, "category", true, "category is required.");
            return null;
        }

        var last = path[^1];
        var slug = SlugRules.Slugify(last);
        var matches = categories.Where(c => c.Slug == slug || string.Equals(c.Name, last, StringComparison.OrdinalIgnoreCase)).ToList();
        if (path.Length > 1 && matches.Count > 1)
        {
            var parentText = path[^2];
            var parentSlug = SlugRules.Slugify(parentText);
            matches = [.. matches.Where(m => m.ParentId is { } pid && categories.FirstOrDefault(c => c.Id == pid) is { } parent &&
                                              (parent.Slug == parentSlug || string.Equals(parent.Name, parentText, StringComparison.OrdinalIgnoreCase)))];
        }

        if (matches.Count == 1)
        {
            return matches[0].Id;
        }

        log.Add(row, "category", true, matches.Count == 0
            ? $"Category \"{text}\" was not found. Use a category name or slug, or a path such as \"Clothing > T-shirts\"."
            : $"Category \"{text}\" matches more than one category. Use the slug or a path such as \"Clothing > T-shirts\".");
        return null;
    }

    private static void AddError(ImportLog log, int row, Error error, string? defaultColumn)
    {
        if (error.ValidationErrors.Count > 0)
        {
            foreach (var (field, messages) in error.ValidationErrors)
            {
                foreach (var message in messages)
                {
                    log.Add(row, ToColumn(field) ?? defaultColumn, true, message);
                }
            }
        }
        else
        {
            log.Add(row, defaultColumn, true, error.Message);
        }
    }

    private static string? ToColumn(string field)
    {
        switch (field)
        {
            case "weightGrams": return "weight_g";
            case "weightGramsOverride": return "variant_weight_g";
            case "publish": case "": return null;
        }

        var builder = new StringBuilder();
        foreach (var ch in field)
        {
            if (char.IsUpper(ch))
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        return builder.ToString();
    }

    // ---- Export and template ----------------------------------------------------------------------------

    public async Task<Result<ExportFileDto>> ExportAsync(TabularFormat format, ExportQuery filter, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        string? branchCode = null;
        if (filter.BranchId is { } branchId)
        {
            var refusal = await inventoryAccess.CheckAsync(actor, branchId, Permissions.Inventory.StockView, cancellationToken);
            if (refusal is not null)
            {
                return refusal;
            }

            branchCode = await db.Branches.AsNoTracking().Where(b => b.Id == branchId).Select(b => b.Code).FirstAsync(cancellationToken);
        }

        var query = db.Products.AsNoTracking().Include(p => p.Variants).AsQueryable();
        if (filter.Status is { } status)
        {
            query = query.Where(p => p.Status == status);
        }

        if (filter.CategoryId is { } categoryId)
        {
            var ids = await CategoryAndDescendantsAsync(categoryId, cancellationToken);
            query = query.Where(p => ids.Contains(p.CategoryId));
        }

        var list = await query.OrderBy(p => p.NameEn).ToListAsync(cancellationToken);
        if (list.Sum(p => Math.Max(1, p.Variants.Count(v => !v.IsDeleted))) > MaxExportRows)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["filter"] = [$"More than {MaxExportRows} rows. Narrow the export with a category or status."] });
        }

        var categories = await db.Categories.AsNoTracking().Select(c => new CategoryRef(c.Id, c.NameEn, c.Slug, c.ParentId)).ToListAsync(cancellationToken);
        var brands = await db.Brands.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.NameEn, cancellationToken);
        var charts = await db.SizeCharts.AsNoTracking().ToDictionaryAsync(s => s.Id, s => s.NameEn, cancellationToken);
        var levels = new Dictionary<Guid, StockLevel>();
        if (filter.BranchId is { } stockBranch)
        {
            levels = await db.StockLevels.AsNoTracking().Where(l => l.BranchId == stockBranch).ToDictionaryAsync(l => l.VariantId, cancellationToken);
        }

        var canSeeCost = actor.Access.HasAnywhere(Permissions.Catalog.ProductViewCost);
        var headers = HeaderRow(canSeeCost);
        var rows = new List<IReadOnlyList<string?>>();
        foreach (var p in list)
        {
            var common = new Dictionary<string, string?>
            {
                ["slug"] = p.Slug,
                ["name_en"] = p.NameEn,
                ["name_bn"] = p.NameBn,
                ["category"] = CategoryPath(p.CategoryId, categories),
                ["brand"] = p.BrandId is { } b && brands.TryGetValue(b, out var brandName) ? brandName : null,
                ["short_description"] = p.ShortDescription,
                ["description"] = p.Description,
                ["material"] = p.Material,
                ["care"] = p.Care,
                ["tags"] = p.Tags,
                ["target_group"] = p.TargetGroup.ToString(),
                ["weight_g"] = Num(p.WeightGrams),
                ["length_cm"] = Num(p.LengthCm),
                ["width_cm"] = Num(p.WidthCm),
                ["height_cm"] = Num(p.HeightCm),
                ["regular_price"] = Num(p.RegularPrice),
                ["sale_price"] = Num(p.SalePrice),
                ["sale_start"] = p.SaleStart?.ToOffset(Dhaka).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["sale_end"] = p.SaleEnd?.ToOffset(Dhaka).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                ["cost_price"] = canSeeCost ? Num(p.CostPrice) : null,
                ["is_new"] = YesNo(p.IsNew),
                ["is_featured"] = YesNo(p.IsFeatured),
                ["is_best_seller"] = YesNo(p.IsBestSeller),
                ["size_chart"] = p.SizeChartId is { } c && charts.TryGetValue(c, out var chartName) ? chartName : null,
                ["meta_title"] = p.MetaTitle,
                ["meta_description"] = p.MetaDescription,
                ["status"] = p.Status.ToString(),
            };

            var variants = p.Variants.Where(v => !v.IsDeleted).OrderBy(v => v.SortOrder).ThenBy(v => v.Sku).ToList();
            if (variants.Count == 0)
            {
                rows.Add(Row(headers, common));
                continue;
            }

            foreach (var v in variants)
            {
                var row = new Dictionary<string, string?>(common)
                {
                    ["sku"] = v.Sku,
                    ["color_name"] = v.ColorName,
                    ["color_hex"] = v.ColorHex,
                    ["size"] = v.Size,
                    ["price_override"] = Num(v.PriceOverride),
                    ["barcode"] = v.Barcode,
                    ["variant_weight_g"] = Num(v.WeightGramsOverride),
                };
                if (branchCode is not null)
                {
                    levels.TryGetValue(v.Id, out var level);
                    row["branch_code"] = branchCode;
                    row["on_hand"] = Num(level?.OnHand ?? 0);
                    row["reserved"] = Num(level?.Reserved ?? 0);
                }

                rows.Add(Row(headers, row));
            }
        }

        var bytes = files.Write(new TableData(headers, rows, NumericColumns), format);
        return new ExportFileDto(bytes, files.ContentType(format), $"juvo-products-{clock.GetUtcNow().ToOffset(Dhaka):yyyyMMdd-HHmm}.{files.Extension(format)}");
    }

    /// <summary>The expected columns plus a few example rows (two variants of one product, and a second product).</summary>
    public ExportFileDto Template(TabularFormat format)
    {
        var headers = HeaderRow(includeCost: true);
        var examples = new List<Dictionary<string, string?>>
        {
            new()
            {
                ["name_en"] = "Example Cotton T-Shirt", ["name_bn"] = "উদাহরণ কটন টি-শার্ট", ["category"] = "Clothing > T-shirts", ["brand"] = "Juvo",
                ["short_description"] = "Soft everyday tee.", ["target_group"] = "Unisex", ["regular_price"] = "750", ["sale_price"] = "650",
                ["sale_start"] = "2026-12-01", ["sale_end"] = "2026-12-31", ["cost_price"] = "420", ["is_new"] = "yes", ["is_featured"] = "no",
                ["sku"] = "EX-TEE-BLK-M", ["color_name"] = "Black", ["color_hex"] = "#1C0807", ["size"] = "M", ["opening_stock"] = "10", ["branch_code"] = "HO",
            },
            new() { ["name_en"] = "Example Cotton T-Shirt", ["sku"] = "EX-TEE-BLK-L", ["color_name"] = "Black", ["color_hex"] = "#1C0807", ["size"] = "L", ["opening_stock"] = "8", ["branch_code"] = "HO" },
            new()
            {
                ["name_en"] = "Example Leather Wallet", ["category"] = "Purses > Wallets", ["regular_price"] = "1250", ["target_group"] = "Men",
                ["sku"] = "EX-WAL-BRN", ["color_name"] = "Brown", ["size"] = "Free size",
            },
        };

        var rows = examples.Select(e => Row(headers, e)).ToList();
        return new ExportFileDto(files.Write(new TableData(headers, rows, NumericColumns), format), files.ContentType(format), $"juvo-product-import-template.{files.Extension(format)}");
    }

    private static List<string> HeaderRow(bool includeCost) =>
    [
        .. ProductColumns.Where(c => includeCost || c != "cost_price"), .. VariantColumns, .. InfoColumns,
    ];

    private static IReadOnlyList<string?> Row(List<string> headers, Dictionary<string, string?> values) =>
        [.. headers.Select(h => values.GetValueOrDefault(h))];

    private static string? Num(decimal? value) => value?.ToString(value % 1 == 0 ? "0" : "0.##", CultureInfo.InvariantCulture);

    private static string? Num(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static string CategoryPath(Guid id, List<CategoryRef> categories)
    {
        var parts = new List<string>();
        var current = categories.FirstOrDefault(c => c.Id == id);
        var guard = 0;
        while (current is not null && guard++ < 10)
        {
            parts.Insert(0, current.Name);
            current = current.ParentId is { } parent ? categories.FirstOrDefault(c => c.Id == parent) : null;
        }

        return string.Join(" > ", parts);
    }

    private async Task<List<Guid>> CategoryAndDescendantsAsync(Guid id, CancellationToken cancellationToken)
    {
        var all = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToListAsync(cancellationToken);
        var result = new List<Guid> { id };
        for (var i = 0; i < result.Count; i++)
        {
            result.AddRange(all.Where(c => c.ParentId == result[i]).Select(c => c.Id));
        }

        return result;
    }

    // ---- Support types ----------------------------------------------------------------------------------

    private sealed class ImportRollback : Exception;

    private sealed record CategoryRef(Guid Id, string Name, string Slug, Guid? ParentId);

    private sealed record NameRef(Guid Id, string Name, string Slug);

    private sealed record BranchRef(Guid Id, string Code);

    private sealed record References(List<CategoryRef> Categories, List<NameRef> Brands, List<NameRef> SizeCharts, List<BranchRef> Branches);

    private sealed record PendingStock(int Row, Guid VariantId, string Sku, int Quantity, string? BranchCode);

    private sealed class Group(string key, RawRow first, ParsedProduct product)
    {
        public string Key { get; } = key;

        public RawRow First { get; } = first;

        public ParsedProduct Product { get; } = product;

        public List<ParsedVariant> Variants { get; } = [];
    }

    private sealed class ParsedProduct
    {
        public string Key { get; init; } = string.Empty;
        public int Row { get; init; }
        public string? Slug { get; init; }
        public string? NameEn { get; init; }
        public string? NameBn { get; init; }
        public string? CategoryText { get; init; }
        public string? BrandName { get; init; }
        public string? ShortDescription { get; init; }
        public string? Description { get; init; }
        public string? Material { get; init; }
        public string? Care { get; init; }
        public string? Tags { get; init; }
        public TargetGroup? Target { get; set; }
        public int? WeightG { get; init; }
        public decimal? LengthCm { get; init; }
        public decimal? WidthCm { get; init; }
        public decimal? HeightCm { get; init; }
        public decimal? RegularPrice { get; init; }
        public decimal? SalePrice { get; init; }
        public DateTimeOffset? SaleStart { get; init; }
        public DateTimeOffset? SaleEnd { get; init; }
        public decimal? CostPrice { get; init; }
        public bool? IsNew { get; init; }
        public bool? IsFeatured { get; init; }
        public bool? IsBestSeller { get; init; }
        public string? SizeChartName { get; init; }
        public string? MetaTitle { get; init; }
        public string? MetaDescription { get; init; }
    }

    private sealed class ParsedVariant
    {
        public int Row { get; init; }
        public string Sku { get; init; } = string.Empty;
        public string? Color { get; init; }
        public string? Hex { get; init; }
        public string? Size { get; init; }
        public decimal? PriceOverride { get; init; }
        public string? Barcode { get; init; }
        public int? WeightG { get; init; }
        public int? OpeningStock { get; init; }
        public string? BranchCode { get; init; }
    }

    private sealed class ImportCounts
    {
        public int ProductsCreated { get; set; }
        public int ProductsUpdated { get; set; }
        public int VariantsCreated { get; set; }
        public int VariantsUpdated { get; set; }
        public int StockLinesApplied { get; set; }

        public void Reset()
        {
            ProductsCreated = ProductsUpdated = VariantsCreated = VariantsUpdated = StockLinesApplied = 0;
        }
    }

    private sealed class ImportLog
    {
        private readonly List<ImportIssueDto> _issues = [];

        public int ErrorCount { get; private set; }

        public int WarningCount { get; private set; }

        public void Add(int row, string? column, bool isError, string message)
        {
            if (isError)
            {
                ErrorCount++;
            }
            else
            {
                WarningCount++;
            }

            if (_issues.Count < MaxIssues)
            {
                _issues.Add(new ImportIssueDto(row, column, isError ? "error" : "warning", message));
            }
        }

        public ImportReportDto ToReport(bool dryRun, bool committed, ImportMode mode, string format, int rowsRead, ImportCounts c) =>
            new(dryRun, committed, mode, format, rowsRead, c.ProductsCreated, c.ProductsUpdated, c.VariantsCreated, c.VariantsUpdated, c.StockLinesApplied,
                ErrorCount, WarningCount, ErrorCount + WarningCount > _issues.Count,
                [.. _issues.OrderBy(i => i.Row).ThenBy(i => i.Severity == "error" ? 0 : 1)]);
    }

    /// <summary>One data row with typed readers that report their own problems against the row and column.</summary>
    private sealed class RawRow(int number, List<string> headers, IReadOnlyList<string?> cells)
    {
        public int Number { get; } = number;

        public bool IsEmpty => cells.All(string.IsNullOrWhiteSpace);

        public string? Get(string column)
        {
            var index = headers.IndexOf(column);
            if (index < 0 || index >= cells.Count || string.IsNullOrWhiteSpace(cells[index]))
            {
                return null;
            }

            var value = cells[index]!.Trim();
            // Exported CSV protects text that looks like a formula with a leading apostrophe; take it off again.
            return value.Length > 1 && value[0] == '\'' && value[1] is '=' or '+' or '-' or '@' ? value[1..] : value;
        }

        public decimal? Decimal(string column, ImportLog log)
        {
            if (Get(column) is not { } text)
            {
                return null;
            }

            if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value) && value is >= -1_000_000_000m and <= 1_000_000_000m)
            {
                return value;
            }

            log.Add(Number, column, true, $"\"{text}\" is not a number. Use digits and a dot, for example 1250.50.");
            return null;
        }

        public int? Int(string column, ImportLog log)
        {
            if (Get(column) is not { } text)
            {
                return null;
            }

            if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }

            // Excel writes whole numbers it computed as 10.0 sometimes.
            if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d) && Math.Abs(d) <= int.MaxValue)
            {
                return (int)d;
            }

            log.Add(Number, column, true, $"\"{text}\" is not a whole number.");
            return null;
        }

        public bool? Bool(string column, ImportLog log)
        {
            if (Get(column) is not { } text)
            {
                return null;
            }

            switch (text.ToLowerInvariant())
            {
                case "yes" or "y" or "true" or "1": return true;
                case "no" or "n" or "false" or "0": return false;
                default:
                    log.Add(Number, column, true, $"\"{text}\" is not yes or no.");
                    return null;
            }
        }

        public DateTimeOffset? Date(string column, bool endOfDay, ImportLog log)
        {
            if (Get(column) is not { } text)
            {
                return null;
            }

            DateTime date;
            if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var full) && text.Contains(':', StringComparison.Ordinal))
            {
                return full;
            }

            if (DateTime.TryParseExact(text, ["yyyy-MM-dd", "dd/MM/yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                // A date without a time is a day in Bangladesh: the sale starts at its beginning and ends at its end.
                return new DateTimeOffset(date.Date.AddHours(endOfDay ? 23 : 0).AddMinutes(endOfDay ? 59 : 0).AddSeconds(endOfDay ? 59 : 0), Dhaka);
            }

            // A date cell in Excel arrives as a serial number.
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 20000 and < 80000)
            {
                var day = DateTime.FromOADate(Math.Floor(serial));
                return new DateTimeOffset(day.AddHours(endOfDay ? 23 : 0).AddMinutes(endOfDay ? 59 : 0).AddSeconds(endOfDay ? 59 : 0), Dhaka);
            }

            log.Add(Number, column, true, $"\"{text}\" is not a date. Use yyyy-mm-dd, for example 2026-12-31.");
            return null;
        }
    }
}
