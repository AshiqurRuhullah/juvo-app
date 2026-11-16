using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Orders;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Preorders;

/// <summary>Pre-order switch of a product, or of one variant (null allow = inherit from the product).</summary>
public sealed record PreorderSettingsRequest(bool? AllowPreorder, DateOnly? ExpectedShipDate, decimal? AdvancePercent);

public sealed record VariantPreorderDto(Guid VariantId, string Sku, string? ColorName, string? Size, bool? AllowPreorder, DateOnly? ExpectedShipDate, decimal? AdvancePercent, bool EffectiveAllow, DateOnly? EffectiveShipDate, decimal EffectiveAdvancePercent);

public sealed record PreorderSettingsDto(Guid ProductId, string Name, bool AllowPreorder, DateOnly? ExpectedShipDate, decimal? AdvancePercent, IReadOnlyList<VariantPreorderDto> Variants);

/// <summary>Staff side of module AA: the pre-order queue, manual allocation and the product and variant switches.</summary>
public sealed class PreorderService(IApplicationDbContext db, OrderAccess access, PreorderAllocator allocator, TimeProvider clock)
{
    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    // ---- Queue ----------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<PreorderQueueItemDto>>> QueueAsync(PageRequest request, PreorderQuery filter, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Orders.OrderView);
        var query = from o in db.Orders.AsNoTracking()
                    join b in db.Branches.AsNoTracking() on o.BranchId equals b.Id
                    where o.PreorderState != PreorderState.None && o.Status != OrderStatus.Cancelled
                    select new { Order = o, BranchCode = b.Code };
        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.Order.BranchId));
        }

        if (filter.State is { } state && state != PreorderState.None)
        {
            query = query.Where(x => x.Order.PreorderState == state);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x => x.Order.BranchId == branchId);
        }

        if (filter.VariantId is { } variantId)
        {
            query = query.Where(x => x.Order.Items.Any(i => i.IsPreorder && i.VariantId == variantId));
        }

        if (filter.ExpectedBefore is { } before)
        {
            query = query.Where(x => x.Order.Items.Any(i => i.IsPreorder && i.PreorderExpectedShipDate != null && i.PreorderExpectedShipDate <= before));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(x => x.Order.Number.Contains(term) || x.Order.CustomerMobile.Contains(term) || (x.Order.CustomerName != null && x.Order.CustomerName.Contains(term)) || x.Order.Items.Any(i => i.Sku.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(x => x.Order.PreorderState).ThenBy(x => x.Order.PlacedAt).ThenBy(x => x.Order.Number).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var ids = rows.Select(x => x.Order.Id).ToList();
        var items = await db.OrderItems.AsNoTracking().Where(i => ids.Contains(i.OrderId) && i.IsPreorder).ToListAsync(cancellationToken);
        var result = rows.Select(x => new PreorderQueueItemDto(x.Order.Id, x.Order.Number, x.Order.BranchId, x.BranchCode, x.Order.Status, x.Order.PreorderState, x.Order.CustomerName ?? x.Order.ShipName, x.Order.CustomerMobile,
            x.Order.PlacedAt, x.Order.GrandTotal, x.Order.AdvanceRequired, x.Order.AdvancePaid,
            [.. items.Where(i => i.OrderId == x.Order.Id).OrderBy(i => i.CreatedAt).Select(i => new PreorderLineDto(i.Id, i.VariantId, i.Sku, i.Name, i.ColorName, i.Size, i.Quantity, i.BackorderedQuantity, i.PreorderExpectedShipDate, i.PreorderAllocatedAt))])).ToList();
        return new PagedResult<PreorderQueueItemDto>(result, total, page.Page, page.PageSize);
    }

    // ---- Allocation -----------------------------------------------------------------------------------------

    /// <summary>Staff run the allocation now (stock may have arrived by a transfer, a count or a late receipt). Limited to the branches the actor may edit orders in.</summary>
    public async Task<Result<AllocationResultDto>> AllocateAsync(AllocatePreordersRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        IReadOnlyCollection<Guid>? branches = OrderAccess.VisibleBranches(actor, Permissions.Orders.OrderEdit);
        if (request.BranchId is { } branchId)
        {
            if (await access.CheckAsync(actor, branchId, Permissions.Orders.OrderEdit, cancellationToken) is { } refusal)
            {
                return refusal;
            }

            branches = [branchId];
        }

        IReadOnlyCollection<Guid>? variants = request.VariantId is { } v ? [v] : null;
        return await allocator.AllocateAsync(branches, variants, cancellationToken);
    }

    // ---- Product and variant switches ---------------------------------------------------------------------

    private static Error? Check(PreorderSettingsRequest r, DateOnly today)
    {
        if (r.AdvancePercent is { } pct && (pct < 0m || pct > 100m))
        {
            return Invalid("advancePercent", "The advance must be between 0 and 100 percent.");
        }

        if (r.ExpectedShipDate is { } date && (date < today || date > today.AddYears(2)))
        {
            return Invalid("expectedShipDate", "The expected ship date must be today or later (within two years).");
        }

        return null;
    }

    private DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(6)).DateTime);

    public async Task<Result<PreorderSettingsDto>> GetSettingsAsync(Guid productId, CancellationToken cancellationToken)
    {
        var product = await db.Products.AsNoTracking().FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
        {
            return Error.NotFound(ErrorCodes.Catalog.ProductNotFound, "Product not found.");
        }

        var variants = await db.ProductVariants.AsNoTracking().Where(v => v.ProductId == productId).OrderBy(v => v.SortOrder).ThenBy(v => v.Sku).ToListAsync(cancellationToken);
        return ToDto(product, variants);
    }

    private static PreorderSettingsDto ToDto(Domain.Catalog.Product product, List<Domain.Catalog.ProductVariant> variants) =>
        new(product.Id, product.NameEn, product.AllowPreorder, product.PreorderExpectedShipDate, product.PreorderAdvancePercent,
            [.. variants.Select(v =>
            {
                var info = PreorderRules.For(product, v);
                return new VariantPreorderDto(v.Id, v.Sku, v.ColorName, v.Size, v.AllowPreorder, v.PreorderExpectedShipDate, v.PreorderAdvancePercent, info.Allowed, info.ExpectedShipDate, info.AdvancePercent);
            })]);

    public async Task<Result<PreorderSettingsDto>> SetProductAsync(Guid productId, PreorderSettingsRequest request, CancellationToken cancellationToken)
    {
        if (Check(request, Today) is { } problem)
        {
            return problem;
        }

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId, cancellationToken);
        if (product is null)
        {
            return Error.NotFound(ErrorCodes.Catalog.ProductNotFound, "Product not found.");
        }

        product.SetPreorder(request.AllowPreorder ?? false, request.ExpectedShipDate, request.AdvancePercent);
        await db.SaveChangesAsync(cancellationToken);
        return await GetSettingsAsync(productId, cancellationToken);
    }

    public async Task<Result<PreorderSettingsDto>> SetVariantAsync(Guid productId, Guid variantId, PreorderSettingsRequest request, CancellationToken cancellationToken)
    {
        if (Check(request, Today) is { } problem)
        {
            return problem;
        }

        var variant = await db.ProductVariants.FirstOrDefaultAsync(v => v.Id == variantId && v.ProductId == productId, cancellationToken);
        if (variant is null)
        {
            return Error.NotFound(ErrorCodes.Catalog.VariantNotFound, "Variant not found.");
        }

        variant.SetPreorder(request.AllowPreorder, request.ExpectedShipDate, request.AdvancePercent);
        await db.SaveChangesAsync(cancellationToken);
        return await GetSettingsAsync(productId, cancellationToken);
    }
}
