using Juvo.Application.Abstractions;
using Juvo.Application.Ums;
using Juvo.Domain.Inventory;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Inventory;

/// <summary>
/// Stocktake basics (module V): start a count (the system quantity is snapshotted per line), enter what was counted, submit for review,
/// and a manager approves, which books the differences as stock movements. Uncounted lines change nothing. A difference is applied to the
/// current on-hand quantity (counted minus the snapshot), so sales during the count are not overwritten.
/// </summary>
public sealed class CountsService(IApplicationDbContext db, InventoryAccess access, StockLedger ledger, TimeProvider clock)
{
    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error NotFound() => Error.NotFound(ErrorCodes.Inventory.CountNotFound, "Stock count not found.");

    private static Error WrongState(CountStatus status, string action) =>
        Error.Conflict(ErrorCodes.Inventory.CountState, $"A stock count that is {status} cannot be {action}.");

    // ---- Reading ----------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<CountListItemDto>>> ListAsync(PageRequest request, CountListQuery filter, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var query = db.StockCounts.AsNoTracking().Include(c => c.Lines).AsQueryable();
        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Inventory.CountView);
        if (visible is not null)
        {
            query = query.Where(c => visible.Contains(c.BranchId));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(c => c.Status == status);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(c => c.BranchId == branchId);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(c => c.Number.Contains(term));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(c => c.CreatedAt).ThenByDescending(c => c.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var codes = await BranchCodesAsync(rows.Select(r => r.BranchId), cancellationToken);
        return new PagedResult<CountListItemDto>([.. rows.Select(c => Summary(c, codes))], total, page.Page, page.PageSize);
    }

    public async Task<Result<CountDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, tracking: false, cancellationToken);
        return loaded.IsFailure ? loaded.Error! : await ToDtoAsync(loaded.Value.Count, loaded.Value.Actor, cancellationToken);
    }

    // ---- Workflow ---------------------------------------------------------------------------------------

    public async Task<Result<CountDto>> StartAsync(StartCountRequest request, CancellationToken cancellationToken)
    {
        var target = await access.TargetAsync(request.BranchId, Permissions.Inventory.CountCreate, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        if (request.Note is { Length: > 300 })
        {
            return Invalid("note", "The note can have at most 300 characters.");
        }

        var branch = target.Value.Branch;
        if (await db.StockCounts.AnyAsync(c => c.BranchId == branch.Id && (c.Status == CountStatus.InProgress || c.Status == CountStatus.Submitted), cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Inventory.CountState, "Finish or cancel the open stock count of this branch first.");
        }

        var levels = db.StockLevels.AsNoTracking().Where(l => l.BranchId == branch.Id);
        List<(Guid VariantId, int OnHand)> expected;
        if (request.VariantIds is { Count: > 0 } wanted)
        {
            var ids = wanted.Distinct().ToList();
            var variants = await ledger.VariantsAsync(ids, cancellationToken);
            if (ids.Any(i => !variants.TryGetValue(i, out var v) || !v.TracksStock))
            {
                return Error.NotFound(ErrorCodes.Inventory.VariantNotFound, "A variant in the list was not found or does not track stock.");
            }

            var existing = await levels.Where(l => ids.Contains(l.VariantId)).ToDictionaryAsync(l => l.VariantId, l => l.OnHand, cancellationToken);
            expected = [.. ids.Select(i => (i, existing.GetValueOrDefault(i)))];
        }
        else
        {
            var all = await (from l in levels
                             join v in db.ProductVariants.AsNoTracking() on l.VariantId equals v.Id
                             select new { l.VariantId, l.OnHand }).ToListAsync(cancellationToken);
            expected = [.. all.Select(x => (x.VariantId, x.OnHand))];
        }

        if (expected.Count == 0)
        {
            return Invalid("variantIds", "This branch has no stock yet. Choose the variants to count.");
        }

        var now = clock.GetUtcNow();
        var count = new StockCount(branch.Id, request.Note, id => DocumentNumber.For("SC", now, id));
        foreach (var (variantId, onHand) in expected)
        {
            count.AddLine(variantId, onHand);
        }

        db.StockCounts.Add(count);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(count, target.Value.Actor, cancellationToken);
    }

    public async Task<Result<CountDto>> SaveLinesAsync(Guid id, SaveCountLinesRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadForActionAsync(id, Permissions.Inventory.CountCreate, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (count, actor) = loaded.Value;
        if (count.Status != CountStatus.InProgress)
        {
            return WrongState(count.Status, "edited");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            return Invalid("lines", "Send at least one line.");
        }

        foreach (var entry in request.Lines)
        {
            if (entry.CountedQuantity is < 0 or > 1_000_000)
            {
                return Invalid("lines", "A counted quantity must be 0 or more.");
            }

            var line = count.Lines.FirstOrDefault(l => l.VariantId == entry.VariantId);
            if (line is null)
            {
                return Invalid("lines", "A variant in the list is not part of this count.");
            }

            line.SetCounted(entry.CountedQuantity);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(count, actor, cancellationToken);
    }

    public async Task<Result<CountDto>> SubmitAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadForActionAsync(id, Permissions.Inventory.CountCreate, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (count, actor) = loaded.Value;
        if (count.Status != CountStatus.InProgress)
        {
            return WrongState(count.Status, "submitted");
        }

        if (!count.Lines.Any(l => l.CountedQuantity is not null))
        {
            return Invalid("lines", "Enter at least one counted quantity before submitting.");
        }

        count.Submit(clock.GetUtcNow(), actor.Id);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(count, actor, cancellationToken);
    }

    /// <summary>A reviewer sends a submitted count back for more counting.</summary>
    public async Task<Result<CountDto>> ReopenAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadForActionAsync(id, Permissions.Inventory.CountApprove, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (count, actor) = loaded.Value;
        if (count.Status != CountStatus.Submitted)
        {
            return WrongState(count.Status, "reopened");
        }

        count.Reopen();
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(count, actor, cancellationToken);
    }

    /// <summary>Books the differences of counted lines as movements. Refused if a correction would drop below what orders have reserved.</summary>
    public async Task<Result<CountDto>> ApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadForActionAsync(id, Permissions.Inventory.CountApprove, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (count, actor) = loaded.Value;
        if (count.Status != CountStatus.Submitted)
        {
            return WrongState(count.Status, "approved");
        }

        foreach (var line in count.Lines.Where(l => l.Difference is { } d && d != 0))
        {
            var level = await ledger.LevelAsync(count.BranchId, line.VariantId, cancellationToken);
            if (!ledger.TryChange(level, line.Difference!.Value, StockReason.StockCount, $"Count {count.Number}", "Count", count.Id))
            {
                var info = await ledger.VariantsAsync([line.VariantId], cancellationToken);
                return Error.Conflict(ErrorCodes.Inventory.InsufficientStock,
                    $"{(info.TryGetValue(line.VariantId, out var v) ? v.Sku : "A variant")}: the correction would drop below the {level.Reserved} units reserved for orders.");
            }
        }

        count.Approve(clock.GetUtcNow(), actor.Id);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(count, actor, cancellationToken);
    }

    public async Task<Result<CountDto>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadForActionAsync(id, Permissions.Inventory.CountCreate, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (count, actor) = loaded.Value;
        if (count.Status is not (CountStatus.InProgress or CountStatus.Submitted))
        {
            return WrongState(count.Status, "cancelled");
        }

        count.Cancel(clock.GetUtcNow(), actor.Id);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(count, actor, cancellationToken);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private async Task<Result<(StockCount Count, Actor Actor)>> LoadAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var actorResult = await access.ActorAsync(cancellationToken);
        if (actorResult.IsFailure)
        {
            return actorResult.Error!;
        }

        var query = db.StockCounts.Include(c => c.Lines).AsQueryable();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        // The branch filter already hides other branches' counts from scoped staff; the view permission is checked per branch too.
        var count = await query.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return count is null || !actorResult.Value.Can(Permissions.Inventory.CountView, count.BranchId) ? NotFound() : (count, actorResult.Value);
    }

    private async Task<Result<(StockCount Count, Actor Actor)>> LoadForActionAsync(Guid id, string permission, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, tracking: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var refusal = await access.CheckAsync(loaded.Value.Actor, loaded.Value.Count.BranchId, permission, cancellationToken);
        return refusal is not null ? refusal : loaded.Value;
    }

    private async Task<Dictionary<Guid, string>> BranchCodesAsync(IEnumerable<Guid> ids, CancellationToken cancellationToken)
    {
        var list = ids.Distinct().ToList();
        return await db.Branches.AsNoTracking().Where(b => list.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, cancellationToken);
    }

    private static CountListItemDto Summary(StockCount c, IReadOnlyDictionary<Guid, string> codes) =>
        new(c.Id, c.Number, c.BranchId, codes.GetValueOrDefault(c.BranchId, string.Empty), c.Status, c.Note, c.CreatedAt, c.SubmittedAt, c.ClosedAt,
            c.Lines.Count, c.Lines.Count(l => l.CountedQuantity is not null), c.Lines.Count(l => l.Difference is { } d && d != 0),
            c.Lines.Sum(l => l.Difference ?? 0));

    private async Task<CountDto> ToDtoAsync(StockCount count, Actor actor, CancellationToken cancellationToken)
    {
        var codes = await BranchCodesAsync([count.BranchId], cancellationToken);
        var variants = await ledger.VariantsAsync([.. count.Lines.Select(l => l.VariantId)], cancellationToken);
        var lines = count.Lines.Select(l => variants.TryGetValue(l.VariantId, out var v)
                ? new CountLineDto(l.VariantId, v.ProductId, v.ProductName, v.Sku, v.ColorName, v.Size, l.ExpectedQuantity, l.CountedQuantity, l.Difference)
                : new CountLineDto(l.VariantId, Guid.Empty, string.Empty, string.Empty, null, null, l.ExpectedQuantity, l.CountedQuantity, l.Difference))
            .OrderBy(l => l.ProductName).ThenBy(l => l.Sku).ToList();

        var actions = new List<string>();
        switch (count.Status)
        {
            case CountStatus.InProgress:
                if (actor.Can(Permissions.Inventory.CountCreate, count.BranchId)) { actions.AddRange(["save", "submit", "cancel"]); }
                break;
            case CountStatus.Submitted:
                if (actor.Can(Permissions.Inventory.CountApprove, count.BranchId)) { actions.AddRange(["approve", "reopen"]); }
                if (actor.Can(Permissions.Inventory.CountCreate, count.BranchId)) { actions.Add("cancel"); }
                break;
        }

        return new CountDto(Summary(count, codes), lines, actions);
    }
}
