using Juvo.Application.Abstractions;
using Juvo.Domain.Inventory;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Inventory;

/// <summary>
/// Stock per branch (module V): on hand, reserved and available, adjustments with a reason, low-stock thresholds and the movement ledger.
/// Reads are limited to the branches where the actor holds <c>inventory.stock.view</c>; writes are checked in the branch they target.
/// </summary>
public sealed class StockService(IApplicationDbContext db, InventoryAccess access, StockLedger ledger)
{
    public const int MaxAdjustLines = 200;

    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    // ---- Reading ----------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<StockRowDto>>> ListAsync(PageRequest request, StockListQuery filter, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Inventory.StockView);
        var query = from l in db.StockLevels.AsNoTracking()
                    join v in db.ProductVariants.AsNoTracking() on l.VariantId equals v.Id
                    join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                    join b in db.Branches.AsNoTracking() on l.BranchId equals b.Id
                    select new { Level = l, Variant = v, Product = p, Branch = b };

        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.Level.BranchId));
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x => x.Level.BranchId == branchId);
        }

        if (filter.ProductId is { } productId)
        {
            query = query.Where(x => x.Product.Id == productId);
        }

        if (filter.LowOnly == true)
        {
            query = query.Where(x => x.Level.ReorderLevel > 0 && x.Level.OnHand - x.Level.Reserved <= x.Level.ReorderLevel);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(x => x.Product.NameEn.Contains(term) || (x.Product.NameBn != null && x.Product.NameBn.Contains(term)) ||
                                     x.Variant.Sku.Contains(term) || (x.Variant.Barcode != null && x.Variant.Barcode == term));
        }

        var sort = request.ParseSort(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "product", "sku", "onHand", "available" }) is { Count: > 0 } f ? f[0] : null;
        query = (sort?.Name.ToLowerInvariant(), sort?.Descending ?? false) switch
        {
            ("sku", true) => query.OrderByDescending(x => x.Variant.Sku),
            ("sku", false) => query.OrderBy(x => x.Variant.Sku),
            ("onhand", true) => query.OrderByDescending(x => x.Level.OnHand),
            ("onhand", false) => query.OrderBy(x => x.Level.OnHand),
            ("available", true) => query.OrderByDescending(x => x.Level.OnHand - x.Level.Reserved),
            ("available", false) => query.OrderBy(x => x.Level.OnHand - x.Level.Reserved),
            ("product", true) => query.OrderByDescending(x => x.Product.NameEn).ThenBy(x => x.Variant.Sku),
            _ => query.OrderBy(x => x.Product.NameEn).ThenBy(x => x.Variant.Sku).ThenBy(x => x.Branch.Code),
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<StockRowDto>(
            [.. rows.Select(x => Row(x.Level, x.Branch.Code, x.Variant.Id, x.Product.Id, x.Product.NameEn, x.Variant.Sku, x.Variant.ColorName, x.Variant.Size))],
            total, page.Page, page.PageSize);
    }

    /// <summary>Every variant of a product against every branch the actor may view (zero where nothing was ever stocked).</summary>
    public async Task<Result<ProductStockDto>> ForProductAsync(Guid productId, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var product = await db.Products.AsNoTracking().Where(p => p.Id == productId)
            .Select(p => new { p.Id, p.NameEn }).FirstOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return Error.NotFound(ErrorCodes.Catalog.ProductNotFound, "Product not found.");
        }

        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Inventory.StockView);
        var branches = await db.Branches.AsNoTracking().Where(b => b.IsActive && (visible == null || visible.Contains(b.Id)))
            .OrderBy(b => b.Code).ToListAsync(cancellationToken);
        var variants = await db.ProductVariants.AsNoTracking().Where(v => v.ProductId == productId)
            .OrderBy(v => v.SortOrder).ThenBy(v => v.Sku).ToListAsync(cancellationToken);
        var variantIds = variants.Select(v => v.Id).ToList();
        var levels = await db.StockLevels.AsNoTracking().Where(l => variantIds.Contains(l.VariantId)).ToListAsync(cancellationToken);

        var result = variants.Select(v =>
        {
            var perBranch = branches.Select(b =>
            {
                var level = levels.FirstOrDefault(l => l.VariantId == v.Id && l.BranchId == b.Id);
                return new BranchStockDto(b.Id, b.Code, b.NameEn, level?.OnHand ?? 0, level?.Reserved ?? 0, level?.Available ?? 0,
                    level?.ReorderLevel ?? 0, level?.IsLow ?? false);
            }).ToList();
            return new VariantStockDto(v.Id, v.Sku, v.ColorName, v.Size, v.TracksStock, perBranch.Sum(x => x.OnHand),
                perBranch.Sum(x => x.Reserved), perBranch.Sum(x => x.Available), perBranch);
        }).ToList();
        return new ProductStockDto(product.Id, product.NameEn, result);
    }

    public async Task<Result<PagedResult<StockMovementDto>>> MovementsAsync(PageRequest request, MovementListQuery filter, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Inventory.StockView);
        var query = from m in db.StockMovements.AsNoTracking()
                    join v in db.ProductVariants.AsNoTracking() on m.VariantId equals v.Id
                    join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                    join b in db.Branches.AsNoTracking() on m.BranchId equals b.Id
                    select new { Movement = m, Variant = v, Product = p, Branch = b };

        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.Movement.BranchId));
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x => x.Movement.BranchId == branchId);
        }

        if (filter.VariantId is { } variantId)
        {
            query = query.Where(x => x.Movement.VariantId == variantId);
        }

        if (filter.ProductId is { } productId)
        {
            query = query.Where(x => x.Product.Id == productId);
        }

        if (filter.Reason is { } reason)
        {
            query = query.Where(x => x.Movement.Reason == reason);
        }

        if (filter.From is { } from)
        {
            query = query.Where(x => x.Movement.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(x => x.Movement.CreatedAt <= to);
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.Movement.CreatedAt).ThenByDescending(x => x.Movement.Id)
            .Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);

        var userIds = rows.Where(r => r.Movement.CreatedBy is not null).Select(r => r.Movement.CreatedBy!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id)).Select(u => new { u.Id, u.FullName }).ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return new PagedResult<StockMovementDto>(
            [.. rows.Select(x => new StockMovementDto(x.Movement.Id, x.Movement.BranchId, x.Branch.Code, x.Variant.Id, x.Product.Id, x.Product.NameEn, x.Variant.Sku,
                x.Movement.QuantityChange, x.Movement.OnHandAfter, x.Movement.Reason, x.Movement.Note, x.Movement.ReferenceType, x.Movement.ReferenceId,
                x.Movement.CreatedAt, x.Movement.CreatedBy is { } id && names.TryGetValue(id, out var name) ? name : null))],
            total, page.Page, page.PageSize);
    }

    // ---- Changing ---------------------------------------------------------------------------------------

    /// <summary>
    /// Adds or removes stock with a reason. Every line is checked first and nothing is saved when one fails. Removing more than is
    /// available (stock that is reserved for orders cannot be taken away) is refused.
    /// </summary>
    public async Task<Result<IReadOnlyList<StockRowDto>>> AdjustAsync(AdjustStockRequest request, CancellationToken cancellationToken)
    {
        var target = await access.TargetAsync(request.BranchId, Permissions.Inventory.StockAdjust, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var problem = ValidateAdjust(request);
        if (problem is not null)
        {
            return problem;
        }

        var lines = request.Lines.GroupBy(l => l.VariantId).Select(g => new AdjustLine(g.Key, g.Sum(x => x.QuantityChange))).Where(l => l.QuantityChange != 0).ToList();
        if (lines.Count == 0)
        {
            return Invalid("lines", "Nothing to change.");
        }

        var variants = await ledger.VariantsAsync([.. lines.Select(l => l.VariantId)], cancellationToken);
        var missing = lines.FirstOrDefault(l => !variants.ContainsKey(l.VariantId));
        if (missing is not null)
        {
            return Error.NotFound(ErrorCodes.Inventory.VariantNotFound, "A variant in the list was not found.");
        }

        var notTracked = lines.Select(l => variants[l.VariantId]).FirstOrDefault(v => !v.TracksStock);
        if (notTracked is not null)
        {
            return Invalid("lines", $"{notTracked.Sku} does not track stock.");
        }

        var branch = target.Value.Branch;
        var changed = new List<(StockLevel Level, VariantInfo Variant)>();
        foreach (var line in lines)
        {
            var level = await ledger.LevelAsync(branch.Id, line.VariantId, cancellationToken);
            if (!ledger.TryChange(level, line.QuantityChange, request.Reason, request.Note, null, null))
            {
                return Error.Conflict(ErrorCodes.Inventory.InsufficientStock,
                    $"{variants[line.VariantId].Sku}: only {level.Available} available (on hand {level.OnHand}, reserved {level.Reserved}); cannot remove {-line.QuantityChange}.");
            }

            changed.Add((level, variants[line.VariantId]));
        }

        await db.SaveChangesAsync(cancellationToken);
        return changed.Select(c => Row(c.Level, branch.Code, c.Variant.VariantId, c.Variant.ProductId, c.Variant.ProductName, c.Variant.Sku, c.Variant.ColorName, c.Variant.Size)).ToList();
    }

    public async Task<Result<StockRowDto>> SetReorderLevelAsync(SetReorderLevelRequest request, CancellationToken cancellationToken)
    {
        var target = await access.TargetAsync(request.BranchId, Permissions.Inventory.ReorderEdit, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        if (request.ReorderLevel is < 0 or > 1_000_000)
        {
            return Invalid("reorderLevel", "The threshold must be 0 or more.");
        }

        var variants = await ledger.VariantsAsync([request.VariantId], cancellationToken);
        if (!variants.TryGetValue(request.VariantId, out var variant))
        {
            return Error.NotFound(ErrorCodes.Inventory.VariantNotFound, "Variant not found.");
        }

        var level = await ledger.LevelAsync(target.Value.Branch.Id, request.VariantId, cancellationToken);
        level.SetReorderLevel(request.ReorderLevel);
        await db.SaveChangesAsync(cancellationToken);
        return Row(level, target.Value.Branch.Code, variant.VariantId, variant.ProductId, variant.ProductName, variant.Sku, variant.ColorName, variant.Size);
    }

    private static Error? ValidateAdjust(AdjustStockRequest request)
    {
        if (request.Reason is StockReason.TransferOut or StockReason.TransferIn or StockReason.TransferCancelled or StockReason.StockCount ||
            !Enum.IsDefined(request.Reason))
        {
            return Invalid("reason", "Choose a reason: opening stock, correction, damage, loss, found or customer return.");
        }

        if (request.Lines is null || request.Lines.Count is 0 or > MaxAdjustLines)
        {
            return Invalid("lines", $"Send between 1 and {MaxAdjustLines} lines.");
        }

        if (request.Note is { Length: > 300 })
        {
            return Invalid("note", "The note can have at most 300 characters.");
        }

        foreach (var line in request.Lines)
        {
            if (line.QuantityChange == 0 || Math.Abs((long)line.QuantityChange) > 1_000_000)
            {
                return Invalid("lines", "Each quantity must be a non-zero whole number up to 1,000,000.");
            }

            var mustRemove = request.Reason is StockReason.Damage or StockReason.Loss;
            var mustAdd = request.Reason is StockReason.OpeningStock or StockReason.Found or StockReason.CustomerReturn;
            if ((mustRemove && line.QuantityChange > 0) || (mustAdd && line.QuantityChange < 0))
            {
                return Invalid("lines", mustRemove ? $"{request.Reason} removes stock: use a negative quantity." : $"{request.Reason} adds stock: use a positive quantity.");
            }
        }

        return null;
    }

    private static StockRowDto Row(StockLevel l, string branchCode, Guid variantId, Guid productId, string productName, string sku, string? color, string? size) =>
        new(l.BranchId, branchCode, variantId, productId, productName, sku, color, size, l.OnHand, l.Reserved, l.Available, l.ReorderLevel, l.IsLow);
}
