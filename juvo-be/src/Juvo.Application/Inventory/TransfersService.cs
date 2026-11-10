using Juvo.Application.Abstractions;
using Juvo.Application.Ums;
using Juvo.Domain.Inventory;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Inventory;

/// <summary>
/// Inter-branch stock transfers (SRS 4A.3): Requested, Approved, In transit, Received. The source branch approves and ships (stock leaves
/// it and a movement is written there); the destination receives (stock enters it, a movement is written there). Either side may
/// request. A transfer belongs to two branches, so a user sees it when they hold <c>inventory.transfer.view</c> in either.
/// </summary>
public sealed class TransfersService(IApplicationDbContext db, InventoryAccess access, StockLedger ledger, TimeProvider clock)
{
    public const int MaxLines = 100;

    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error NotFound() => Error.NotFound(ErrorCodes.Inventory.TransferNotFound, "Transfer not found.");

    private static Error WrongState(TransferStatus status, string action) =>
        Error.Conflict(ErrorCodes.Inventory.TransferState, $"A transfer that is {status} cannot be {action}.");

    // ---- Reading ----------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<TransferDto>>> ListAsync(PageRequest request, TransferListQuery filter, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var query = db.StockTransfers.AsNoTracking().Include(t => t.Lines).AsQueryable();
        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Inventory.TransferView);
        if (visible is not null)
        {
            query = query.Where(t => visible.Contains(t.FromBranchId) || visible.Contains(t.ToBranchId));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(t => t.Status == status);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(t => t.FromBranchId == branchId || t.ToBranchId == branchId);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(t => t.Number.Contains(term));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(t => t.CreatedAt).ThenByDescending(t => t.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var dtos = await ToDtosAsync(rows, actor.Value, cancellationToken);
        return new PagedResult<TransferDto>(dtos, total, page.Page, page.PageSize);
    }

    public async Task<Result<TransferDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, tracking: false, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        return (await ToDtosAsync([loaded.Value.Transfer], loaded.Value.Actor, cancellationToken))[0];
    }

    // ---- Workflow ---------------------------------------------------------------------------------------

    public async Task<Result<TransferDto>> RequestAsync(RequestTransferRequest request, CancellationToken cancellationToken)
    {
        var actorResult = await access.ActorAsync(cancellationToken);
        if (actorResult.IsFailure)
        {
            return actorResult.Error!;
        }

        var actor = actorResult.Value;
        if (request.FromBranchId == request.ToBranchId)
        {
            return Invalid("toBranchId", "Choose two different branches.");
        }

        if (request.Lines is null || request.Lines.Count is 0 or > MaxLines)
        {
            return Invalid("lines", $"Send between 1 and {MaxLines} lines.");
        }

        if (request.Lines.Any(l => l.Quantity is <= 0 or > 1_000_000))
        {
            return Invalid("lines", "Each quantity must be a whole number from 1 to 1,000,000.");
        }

        if (request.Note is { Length: > 300 })
        {
            return Invalid("note", "The note can have at most 300 characters.");
        }

        // Either side may ask: the sender offering stock, or the receiver asking for it.
        if (!actor.Can(Permissions.Inventory.TransferRequest, request.FromBranchId) && !actor.Can(Permissions.Inventory.TransferRequest, request.ToBranchId))
        {
            return await access.CheckAsync(actor, request.ToBranchId, Permissions.Inventory.TransferRequest, cancellationToken)
                   ?? Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "You do not hold this permission in either branch.");
        }

        var branches = await db.Branches.AsNoTracking().Where(b => b.Id == request.FromBranchId || b.Id == request.ToBranchId).ToListAsync(cancellationToken);
        if (branches.Count != 2)
        {
            return Error.NotFound(ErrorCodes.Ums.BranchNotFound, "Branch not found.");
        }

        if (branches.Any(b => !b.IsActive))
        {
            return Error.Conflict(ErrorCodes.Inventory.BranchInactive, "Both branches must be active.");
        }

        var ids = request.Lines.Select(l => l.VariantId).Distinct().ToList();
        var variants = await ledger.VariantsAsync(ids, cancellationToken);
        if (ids.Any(i => !variants.ContainsKey(i)))
        {
            return Error.NotFound(ErrorCodes.Inventory.VariantNotFound, "A variant in the list was not found.");
        }

        if (variants.Values.FirstOrDefault(v => !v.TracksStock) is { } untracked)
        {
            return Invalid("lines", $"{untracked.Sku} does not track stock.");
        }

        var now = clock.GetUtcNow();
        var transfer = new StockTransfer(request.FromBranchId, request.ToBranchId, request.Note, id => DocumentNumber.For("TR", now, id));
        foreach (var line in request.Lines)
        {
            transfer.AddLine(line.VariantId, line.Quantity);
        }

        db.StockTransfers.Add(transfer);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([transfer], actor, cancellationToken))[0];
    }

    public async Task<Result<TransferDto>> ApproveAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadForActionAsync(id, Permissions.Inventory.TransferApprove, t => t.FromBranchId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (transfer, actor) = loaded.Value;
        if (transfer.Status != TransferStatus.Requested)
        {
            return WrongState(transfer.Status, "approved");
        }

        var shortage = await ShortageAsync(transfer, cancellationToken);
        if (shortage is not null)
        {
            return shortage;
        }

        transfer.Approve(clock.GetUtcNow(), actor.Id);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([transfer], actor, cancellationToken))[0];
    }

    public async Task<Result<TransferDto>> RejectAsync(Guid id, ReasonRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadForActionAsync(id, Permissions.Inventory.TransferApprove, t => t.FromBranchId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (transfer, actor) = loaded.Value;
        if (transfer.Status != TransferStatus.Requested)
        {
            return WrongState(transfer.Status, "rejected");
        }

        transfer.Reject(clock.GetUtcNow(), actor.Id, request.Reason);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([transfer], actor, cancellationToken))[0];
    }

    /// <summary>The goods leave the source branch: stock is taken from it now (Approved to In transit).</summary>
    public async Task<Result<TransferDto>> ShipAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadForActionAsync(id, Permissions.Inventory.TransferApprove, t => t.FromBranchId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (transfer, actor) = loaded.Value;
        if (transfer.Status != TransferStatus.Approved)
        {
            return WrongState(transfer.Status, "shipped");
        }

        foreach (var line in transfer.Lines)
        {
            var level = await ledger.LevelAsync(transfer.FromBranchId, line.VariantId, cancellationToken);
            if (level.Available < line.Quantity ||
                !ledger.TryChange(level, -line.Quantity, StockReason.TransferOut, $"Transfer {transfer.Number}", "Transfer", transfer.Id))
            {
                return Error.Conflict(ErrorCodes.Inventory.InsufficientStock, $"Not enough stock in the source branch to ship {transfer.Number}.");
            }
        }

        transfer.Ship(clock.GetUtcNow(), actor.Id);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([transfer], actor, cancellationToken))[0];
    }

    /// <summary>The goods arrive: stock enters the destination branch (In transit to Received).</summary>
    public async Task<Result<TransferDto>> ReceiveAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadForActionAsync(id, Permissions.Inventory.TransferReceive, t => t.ToBranchId, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (transfer, actor) = loaded.Value;
        if (transfer.Status != TransferStatus.InTransit)
        {
            return WrongState(transfer.Status, "received");
        }

        foreach (var line in transfer.Lines)
        {
            var level = await ledger.LevelAsync(transfer.ToBranchId, line.VariantId, cancellationToken);
            ledger.TryChange(level, line.Quantity, StockReason.TransferIn, $"Transfer {transfer.Number}", "Transfer", transfer.Id);
        }

        transfer.Receive(clock.GetUtcNow(), actor.Id);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([transfer], actor, cancellationToken))[0];
    }

    /// <summary>Cancels before receipt. Goods already in transit go back into the source branch's stock.</summary>
    public async Task<Result<TransferDto>> CancelAsync(Guid id, ReasonRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, tracking: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (transfer, actor) = loaded.Value;
        if (transfer.Status is not (TransferStatus.Requested or TransferStatus.Approved or TransferStatus.InTransit))
        {
            return WrongState(transfer.Status, "cancelled");
        }

        // Stock goes back to the source, so a transfer in transit is cancelled by someone who holds the permission there.
        var branchId = transfer.Status == TransferStatus.InTransit ? transfer.FromBranchId : (Guid?)null;
        var refusal = branchId is { } required
            ? await access.RequireAsync(actor, required, Permissions.Inventory.TransferCancel, cancellationToken)
            : await EitherSideAsync(actor, transfer, Permissions.Inventory.TransferCancel, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (transfer.Status == TransferStatus.InTransit)
        {
            foreach (var line in transfer.Lines)
            {
                var level = await ledger.LevelAsync(transfer.FromBranchId, line.VariantId, cancellationToken);
                ledger.TryChange(level, line.Quantity, StockReason.TransferCancelled, $"Transfer {transfer.Number} cancelled", "Transfer", transfer.Id);
            }
        }

        transfer.Cancel(clock.GetUtcNow(), actor.Id, request.Reason);
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync([transfer], actor, cancellationToken))[0];
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private async Task<Error?> ShortageAsync(StockTransfer transfer, CancellationToken cancellationToken)
    {
        var variantIds = transfer.Lines.Select(l => l.VariantId).ToList();
        var levels = await db.StockLevels.AsNoTracking().Where(l => l.BranchId == transfer.FromBranchId && variantIds.Contains(l.VariantId)).ToListAsync(cancellationToken);
        var short1 = transfer.Lines.FirstOrDefault(l => (levels.FirstOrDefault(x => x.VariantId == l.VariantId)?.Available ?? 0) < l.Quantity);
        if (short1 is null)
        {
            return null;
        }

        var info = await ledger.VariantsAsync([short1.VariantId], cancellationToken);
        var have = levels.FirstOrDefault(x => x.VariantId == short1.VariantId)?.Available ?? 0;
        return Error.Conflict(ErrorCodes.Inventory.InsufficientStock,
            $"{(info.TryGetValue(short1.VariantId, out var v) ? v.Sku : "A variant")}: {have} available in the source branch, {short1.Quantity} requested.");
    }

    private async Task<Result<(StockTransfer Transfer, Actor Actor)>> LoadForActionAsync(
        Guid id, string permission, Func<StockTransfer, Guid> branchOf, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, tracking: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var (transfer, actor) = loaded.Value;
        var refusal = await access.RequireAsync(actor, branchOf(transfer), permission, cancellationToken);
        return refusal is not null ? refusal : (transfer, actor);
    }

    private async Task<Error?> EitherSideAsync(Actor actor, StockTransfer transfer, string permission, CancellationToken cancellationToken)
    {
        if (actor.Can(permission, transfer.FromBranchId) || actor.Can(permission, transfer.ToBranchId))
        {
            return null;
        }

        return await access.RequireAsync(actor, transfer.FromBranchId, permission, cancellationToken);
    }

    /// <summary>Loads a transfer the actor may see (view permission in either branch); anything else is 404.</summary>
    private async Task<Result<(StockTransfer Transfer, Actor Actor)>> LoadAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var actorResult = await access.ActorAsync(cancellationToken);
        if (actorResult.IsFailure)
        {
            return actorResult.Error!;
        }

        var query = db.StockTransfers.Include(t => t.Lines).AsQueryable();
        if (!tracking)
        {
            query = query.AsNoTracking();
        }

        var transfer = await query.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        var actor = actorResult.Value;
        if (transfer is null || !(actor.Can(Permissions.Inventory.TransferView, transfer.FromBranchId) || actor.Can(Permissions.Inventory.TransferView, transfer.ToBranchId)))
        {
            return NotFound();
        }

        return (transfer, actor);
    }

    private async Task<IReadOnlyList<TransferDto>> ToDtosAsync(IReadOnlyList<StockTransfer> transfers, Actor actor, CancellationToken cancellationToken)
    {
        var branchIds = transfers.SelectMany(t => new[] { t.FromBranchId, t.ToBranchId }).Distinct().ToList();
        var codes = await db.Branches.AsNoTracking().Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, cancellationToken);
        var variantIds = transfers.SelectMany(t => t.Lines).Select(l => l.VariantId).Distinct().ToList();
        var variants = await ledger.VariantsAsync(variantIds, cancellationToken);

        return [.. transfers.Select(t => new TransferDto(
            t.Id, t.Number, t.FromBranchId, codes.GetValueOrDefault(t.FromBranchId, string.Empty), t.ToBranchId, codes.GetValueOrDefault(t.ToBranchId, string.Empty),
            t.Status, t.Note, t.CancelReason, t.CreatedAt, t.ApprovedAt, t.ShippedAt, t.ReceivedAt, t.ClosedAt,
            [.. t.Lines.Select(l => variants.TryGetValue(l.VariantId, out var v)
                ? new TransferLineDto(l.VariantId, v.ProductId, v.ProductName, v.Sku, v.ColorName, v.Size, l.Quantity)
                : new TransferLineDto(l.VariantId, Guid.Empty, string.Empty, string.Empty, null, null, l.Quantity))],
            Actions(t, actor)))];
    }

    private static List<string> Actions(StockTransfer t, Actor actor)
    {
        var actions = new List<string>();
        bool In(string permission, Guid branch) => actor.Can(permission, branch);
        switch (t.Status)
        {
            case TransferStatus.Requested:
                if (In(Permissions.Inventory.TransferApprove, t.FromBranchId)) { actions.Add("approve"); actions.Add("reject"); }
                if (In(Permissions.Inventory.TransferCancel, t.FromBranchId) || In(Permissions.Inventory.TransferCancel, t.ToBranchId)) { actions.Add("cancel"); }
                break;
            case TransferStatus.Approved:
                if (In(Permissions.Inventory.TransferApprove, t.FromBranchId)) { actions.Add("ship"); }
                if (In(Permissions.Inventory.TransferCancel, t.FromBranchId) || In(Permissions.Inventory.TransferCancel, t.ToBranchId)) { actions.Add("cancel"); }
                break;
            case TransferStatus.InTransit:
                if (In(Permissions.Inventory.TransferReceive, t.ToBranchId)) { actions.Add("receive"); }
                if (In(Permissions.Inventory.TransferCancel, t.FromBranchId)) { actions.Add("cancel"); }
                break;
        }

        return actions;
    }
}
