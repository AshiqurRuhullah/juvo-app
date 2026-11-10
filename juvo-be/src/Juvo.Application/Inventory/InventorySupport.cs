using Juvo.Application.Abstractions;
using Juvo.Application.Ums;
using Juvo.Domain.Branches;
using Juvo.Domain.Inventory;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Inventory;

/// <summary>The branch an inventory write targets, with the actor who may act in it.</summary>
public sealed record BranchTarget(Actor Actor, Branch Branch);

/// <summary>
/// Branch and permission checks shared by the inventory services. Endpoints only prove the permission somewhere; here it is proven in
/// the branch the action targets (the explicit branch, otherwise the active one), because stock is per branch.
/// </summary>
public sealed class InventoryAccess(ActorContext actors, IApplicationDbContext db, IBranchContext branchContext, IAccessDeniedAuditor auditor)
{
    public async Task<Result<Actor>> ActorAsync(CancellationToken cancellationToken)
    {
        return await actors.GetAsync(cancellationToken) is { } actor ? actor : Error.Unauthorized();
    }

    /// <summary>Resolves and checks the branch of a write. A branch the actor cannot see is answered with 404 and audit-logged.</summary>
    public async Task<Result<BranchTarget>> TargetAsync(Guid? requested, string permission, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var branchId = requested ?? branchContext.ActiveBranchId;
        if (branchId is not { } id)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["branchId"] = ["Choose a branch."] });
        }

        var refusal = await CheckAsync(actor, id, permission, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var branch = await db.Branches.FirstAsync(b => b.Id == id, cancellationToken);
        return branch.IsActive
            ? new BranchTarget(actor, branch)
            : Error.Conflict(ErrorCodes.Inventory.BranchInactive, "This branch is not active.");
    }

    /// <summary>Null when the actor holds <paramref name="permission"/> in the (existing) branch; otherwise the refusal.</summary>
    public async Task<Error?> CheckAsync(Actor actor, Guid branchId, string permission, CancellationToken cancellationToken)
    {
        var exists = await db.Branches.AnyAsync(b => b.Id == branchId, cancellationToken);
        if (!exists || (!actor.IsGlobal && !actor.Access.BranchPermissions.ContainsKey(branchId)))
        {
            if (exists)
            {
                await auditor.RecordAsync("Branch", $"Inventory access to branch {branchId} refused (not assigned).", cancellationToken);
            }

            return Error.NotFound(ErrorCodes.Ums.BranchNotFound, "Branch not found.");
        }

        if (!actor.Can(permission, branchId))
        {
            await auditor.RecordAsync("Branch", $"Inventory action {permission} in branch {branchId} refused.", cancellationToken);
            return Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "You do not hold this permission in that branch.");
        }

        return null;
    }

    /// <summary>
    /// For a record the actor can already see (a transfer touching their branch): null when they hold the permission in the branch the
    /// step acts on, otherwise 403 (not 404, because the record is not a secret from them) and an audit entry.
    /// </summary>
    public async Task<Error?> RequireAsync(Actor actor, Guid branchId, string permission, CancellationToken cancellationToken)
    {
        if (actor.Can(permission, branchId))
        {
            return null;
        }

        await auditor.RecordAsync("Branch", $"Inventory action {permission} in branch {branchId} refused.", cancellationToken);
        return Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "You do not hold this permission in that branch.");
    }

    /// <summary>The branches where the actor holds <paramref name="permission"/>; null means every branch.</summary>
    public static List<Guid>? VisibleBranches(Actor actor, string permission)
    {
        if (actor.IsGlobal)
        {
            return null;
        }

        return [.. actor.Access.BranchPermissions.Where(kv => kv.Value.Contains(permission)).Select(kv => kv.Key)];
    }
}

/// <summary>
/// The only place stock quantities change: it moves a <see cref="StockLevel"/> and writes the matching append-only
/// <see cref="StockMovement"/> in the same unit of work, so the ledger can always explain the number on the shelf.
/// </summary>
public sealed class StockLedger(IApplicationDbContext db)
{
    public async Task<StockLevel> LevelAsync(Guid branchId, Guid variantId, CancellationToken cancellationToken)
    {
        var local = db.StockLevels.Local.FirstOrDefault(l => l.BranchId == branchId && l.VariantId == variantId);
        if (local is not null)
        {
            return local;
        }

        var level = await db.StockLevels.FirstOrDefaultAsync(l => l.BranchId == branchId && l.VariantId == variantId, cancellationToken);
        if (level is null)
        {
            level = new StockLevel(branchId, variantId);
            db.StockLevels.Add(level);
        }

        return level;
    }

    /// <summary>Changes on-hand and records the movement. False when the result would be below what is reserved.</summary>
    public bool TryChange(StockLevel level, int delta, StockReason reason, string? note, string? referenceType, Guid? referenceId)
    {
        if (!level.TryChangeOnHand(delta))
        {
            return false;
        }

        db.StockMovements.Add(new StockMovement(level.BranchId, level.VariantId, delta, level.OnHand, reason, note, referenceType, referenceId));
        return true;
    }

    /// <summary>Loads live variants with their product name by id.</summary>
    public async Task<Dictionary<Guid, VariantInfo>> VariantsAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        var rows = await (from v in db.ProductVariants.AsNoTracking()
                          join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                          where ids.Contains(v.Id)
                          select new VariantInfo(v.Id, p.Id, p.NameEn, v.Sku, v.ColorName, v.Size, v.TracksStock)).ToListAsync(cancellationToken);
        return rows.ToDictionary(r => r.VariantId);
    }
}

public sealed record VariantInfo(Guid VariantId, Guid ProductId, string ProductName, string Sku, string? ColorName, string? Size, bool TracksStock);

internal static class DocumentNumber
{
    /// <summary>Readable document number such as <c>TR-2611-A3F9C2</c>: prefix, year and month, and the tail of the id.</summary>
    public static string For(string prefix, DateTimeOffset now, Guid id) =>
        $"{prefix}-{now:yyMM}-{id.ToString("N")[^6..].ToUpperInvariant()}";
}
