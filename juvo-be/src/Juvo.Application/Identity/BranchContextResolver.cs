using Juvo.Application.Abstractions;
using Juvo.Domain.Branches;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Identity;

/// <summary>The resolved branch scope of a request. Stored per request and read through <see cref="IBranchContext"/>.</summary>
public sealed record BranchContextData(
    Guid? ActiveBranchId,
    bool IsAllBranches,
    bool EnforceScope,
    IReadOnlyCollection<Guid> AllowedBranchIds) : IBranchContext
{
    public static BranchContextData Unscoped { get; } = new(null, true, false, []);
}

/// <summary>
/// Turns the <c>X-Branch-Id</c> header into a branch scope and validates it against the user's assignments on every request;
/// the header is never trusted by itself. Header values: a branch id, or <c>all</c> (global-scope users, or users holding
/// <c>ums.branch.switch-all</c>). With no header, global users work in "All branches" and a user with one branch uses it.
/// </summary>
public sealed class BranchContextResolver(IApplicationDbContext db, IUserStateCache states, AccessService access)
{
    public const string AllBranches = "all";

    public async Task<Result<BranchContextData>> ResolveAsync(Guid userId, string? header, CancellationToken cancellationToken = default)
    {
        var state = await states.GetStaffAsync(userId, cancellationToken);
        if (state is not { Exists: true, IsActive: true })
        {
            return Error.Unauthorized();
        }

        var user = await access.GetAsync(userId, state.PermissionVersion, cancellationToken);
        var assigned = user.AssignedBranchIds;
        var isGlobal = user.IsGlobalScope;

        if (string.IsNullOrWhiteSpace(header))
        {
            if (isGlobal)
            {
                return new BranchContextData(null, true, false, []);
            }

            return assigned.Count == 1
                ? new BranchContextData(assigned.First(), false, true, assigned)
                : new BranchContextData(null, false, true, assigned);
        }

        var value = header.Trim();
        if (string.Equals(value, AllBranches, StringComparison.OrdinalIgnoreCase))
        {
            if (isGlobal)
            {
                return new BranchContextData(null, true, false, []);
            }

            return user.HasAnywhere(Permissions.Ums.BranchSwitchAll)
                ? new BranchContextData(null, true, true, assigned)
                : Denied("'All branches' is not available for this account.");
        }

        if (!Guid.TryParse(value, out var branchId))
        {
            return new Error(ErrorCodes.General.BadRequest, "X-Branch-Id must be a branch id or 'all'.", ErrorType.Validation);
        }

        var usable = isGlobal
            ? await db.Branches.AsNoTracking().AnyAsync(b => b.Id == branchId && b.IsActive, cancellationToken)
            : assigned.Contains(branchId);

        return usable
            ? new BranchContextData(branchId, false, true, [branchId])
            : Denied("You do not have access to this branch.");
    }

    private static Result<BranchContextData> Denied(string message) =>
        Error.Forbidden(ErrorCodes.Auth.BranchForbidden, message);
}
