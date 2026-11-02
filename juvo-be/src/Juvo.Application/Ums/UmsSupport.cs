using System.Security.Cryptography;
using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Shared.Pagination;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Ums;

/// <summary>The signed-in staff member performing an admin action, with their effective access.</summary>
public sealed record Actor(Guid Id, UserAccess Access)
{
    /// <summary>May act on every branch (global scope or Super Admin).</summary>
    public bool IsGlobal => Access.IsGlobalScope || Access.HasAllPermissions;

    public bool IsSuperAdmin => Access.HasAllPermissions;

    public bool Can(string permission, Guid? branchId) => Access.Has(permission, branchId);
}

public sealed class ActorContext(ICurrentUser currentUser, IUserStateCache states, AccessService access)
{
    public async Task<Actor?> GetAsync(CancellationToken cancellationToken = default)
    {
        if (currentUser.UserId is not { } id || currentUser.SubjectType != "staff")
        {
            return null;
        }

        var state = await states.GetStaffAsync(id, cancellationToken);
        return state is { Exists: true, IsActive: true }
            ? new Actor(id, await access.GetAsync(id, state.PermissionVersion, cancellationToken))
            : null;
    }
}

/// <summary>Bumps the permission version of users whose access changed so their tokens refresh, and clears the caches.</summary>
public sealed class AccessChanges(IApplicationDbContext db, IUserStateCache states)
{
    public async Task BumpAsync(IEnumerable<Guid> userIds, CancellationToken cancellationToken = default)
    {
        var ids = userIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return;
        }

        var users = await db.Users.Where(u => ids.Contains(u.Id)).ToListAsync(cancellationToken);
        foreach (var user in users)
        {
            user.BumpPermissionVersion();
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var id in ids)
        {
            states.Invalidate(id);
        }
    }

    public async Task BumpUsersOfRoleAsync(Guid roleId, CancellationToken cancellationToken = default)
    {
        var ids = await db.UserBranchRoles.Where(x => x.RoleId == roleId).Select(x => x.UserId).Distinct().ToListAsync(cancellationToken);
        await BumpAsync(ids, cancellationToken);
    }

    public async Task BumpUsersOfBranchAsync(Guid branchId, CancellationToken cancellationToken = default)
    {
        var ids = await db.UserBranchRoles.Where(x => x.BranchId == branchId).Select(x => x.UserId).Distinct().ToListAsync(cancellationToken);
        await BumpAsync(ids, cancellationToken);
    }
}

internal static class TemporaryPassword
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%*?";

    /// <summary>16 characters with upper, lower, digit and symbol, so it satisfies any sensible password policy.</summary>
    public static string Generate()
    {
        var all = Upper + Lower + Digits + Symbols;
        var chars = new List<char>
        {
            Pick(Upper), Pick(Lower), Pick(Digits), Pick(Symbols),
        };
        while (chars.Count < 16)
        {
            chars.Add(Pick(all));
        }

        // Fisher-Yates shuffle with a cryptographic source.
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string([.. chars]);
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
}

internal static class PagingExtensions
{
    public static async Task<PagedResult<TOut>> ToPagedAsync<T, TOut>(
        this IQueryable<T> query, PageRequest request, Func<T, TOut> map, CancellationToken cancellationToken)
    {
        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var items = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<TOut>([.. items.Select(map)], total, page.Page, page.PageSize);
    }
}
