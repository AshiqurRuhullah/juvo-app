using Juvo.Application.Abstractions;
using Juvo.Domain.Identity;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Juvo.Application.Identity;

/// <summary>
/// Short-lived per-user cache of "is this account usable, and what is its permission version". Requests check the token's
/// version against it, so a role change or deactivation takes effect within seconds (immediately on this server, because
/// services invalidate the entry when access changes).
/// </summary>
internal sealed class UserStateCache(IMemoryCache cache, IApplicationDbContext db) : IUserStateCache
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    public async Task<UserState> GetStaffAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        if (cache.TryGetValue(Key(userId), out UserState? cached) && cached is not null)
        {
            return cached;
        }

        var state = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new UserState(true, u.IsActive, u.PermissionVersion))
            .FirstOrDefaultAsync(cancellationToken) ?? new UserState(false, false, 0);

        cache.Set(Key(userId), state, Lifetime);
        return state;
    }

    public async Task<bool> CustomerExistsAsync(Guid customerId, CancellationToken cancellationToken = default)
    {
        var key = "customer:" + customerId;
        if (cache.TryGetValue(key, out bool exists))
        {
            return exists;
        }

        exists = await db.Customers.AsNoTracking().AnyAsync(c => c.Id == customerId, cancellationToken);
        cache.Set(key, exists, Lifetime);
        return exists;
    }

    public void Invalidate(Guid userId)
    {
        cache.Remove(Key(userId));
        cache.Remove("customer:" + userId);
    }

    private static string Key(Guid userId) => "userstate:" + userId;
}

/// <summary>Works out what a staff member may do, per branch. Cached by (user, permission version), so any change invalidates it.</summary>
public sealed class AccessService(IApplicationDbContext db, IMemoryCache cache)
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    public async Task<UserAccess> GetAsync(Guid userId, int permissionVersion, CancellationToken cancellationToken = default)
    {
        var key = $"access:{userId}:{permissionVersion}";
        if (cache.TryGetValue(key, out UserAccess? cached) && cached is not null)
        {
            return cached;
        }

        var access = await ComputeAsync(userId, cancellationToken);
        cache.Set(key, access, Lifetime);
        return access;
    }

    public async Task<UserAccess> ComputeAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        var isGlobal = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.IsGlobalScope)
            .FirstOrDefaultAsync(cancellationToken);

        var activeBranches = await db.Branches.AsNoTracking().Where(b => b.IsActive).Select(b => b.Id).ToListAsync(cancellationToken);
        var activeSet = activeBranches.ToHashSet();

        var assignments = await (
                from ubr in db.UserBranchRoles.AsNoTracking()
                where ubr.UserId == userId
                join role in db.Roles.AsNoTracking() on ubr.RoleId equals role.Id
                select new { ubr.BranchId, RoleId = role.Id, role.IsSuperAdmin })
            .ToListAsync(cancellationToken);

        // Roles assigned to an inactive branch give nothing. Roles with no branch only count for global-scope users.
        assignments = assignments
            .Where(a => a.BranchId is null ? isGlobal : activeSet.Contains(a.BranchId.Value))
            .ToList();

        var roleIds = assignments.Select(a => a.RoleId).Distinct().ToList();
        var rolePermissions = await (
                from rp in db.RolePermissions.AsNoTracking()
                where roleIds.Contains(rp.RoleId)
                join p in db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                where p.IsActive
                select new { rp.RoleId, p.Code })
            .ToListAsync(cancellationToken);
        var codesByRole = rolePermissions.GroupBy(x => x.RoleId).ToDictionary(g => g.Key, g => g.Select(x => x.Code).ToList());

        var allCodes = PermissionCatalog.All.Select(p => p.Code).ToList();
        var global = new HashSet<string>(StringComparer.Ordinal);
        var perBranch = new Dictionary<Guid, HashSet<string>>();
        var hasAll = false;

        foreach (var assignment in assignments)
        {
            var codes = assignment.IsSuperAdmin
                ? allCodes
                : codesByRole.GetValueOrDefault(assignment.RoleId) ?? [];

            if (assignment.BranchId is null)
            {
                hasAll |= assignment.IsSuperAdmin;
                global.UnionWith(codes);
            }
            else
            {
                if (!perBranch.TryGetValue(assignment.BranchId.Value, out var set))
                {
                    perBranch[assignment.BranchId.Value] = set = new HashSet<string>(StringComparer.Ordinal);
                }

                set.UnionWith(codes);
            }
        }

        return new UserAccess
        {
            IsGlobalScope = isGlobal,
            HasAllPermissions = hasAll,
            GlobalPermissions = global,
            BranchPermissions = perBranch.ToDictionary(kv => kv.Key, kv => (IReadOnlySet<string>)kv.Value),
        };
    }
}

/// <summary>Permission AND branch scope, for the current request.</summary>
internal sealed class PermissionChecker(
    ICurrentUser currentUser, IBranchContext branchContext, IUserStateCache states, AccessService access) : IPermissionChecker
{
    public Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default) =>
        HasPermissionAsync(permission, PermissionScope.ActiveBranch, cancellationToken);

    public async Task<bool> HasPermissionAsync(string permission, PermissionScope scope, CancellationToken cancellationToken = default)
    {
        var user = await LoadAsync(cancellationToken);
        if (user is null)
        {
            return false;
        }

        if (scope == PermissionScope.Anywhere || branchContext.IsAllBranches)
        {
            return user.HasAnywhere(permission);
        }

        return user.Has(permission, branchContext.ActiveBranchId);
    }

    public async Task<bool> HasPermissionInBranchAsync(string permission, Guid branchId, CancellationToken cancellationToken = default)
    {
        var user = await LoadAsync(cancellationToken);
        return user is not null && user.Has(permission, branchId);
    }

    private async Task<UserAccess?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId
                                          || !string.Equals(currentUser.SubjectType, "staff", StringComparison.Ordinal))
        {
            return null;
        }

        var state = await states.GetStaffAsync(userId, cancellationToken);
        return state is { Exists: true, IsActive: true }
            ? await access.GetAsync(userId, state.PermissionVersion, cancellationToken)
            : null;
    }
}
