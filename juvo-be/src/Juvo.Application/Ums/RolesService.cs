using Juvo.Application.Identity;
using Juvo.Application.Abstractions;
using Juvo.Domain.Identity;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Ums;

/// <summary>Roles and the permission catalog. Roles are global; assigning them to users is per branch (see <see cref="UsersService"/>).</summary>
public sealed class RolesService(IApplicationDbContext db, ActorContext actors, AccessChanges changes, IUnitOfWork unitOfWork)
{
    private static Error NotFound() => Error.NotFound(ErrorCodes.Ums.RoleNotFound, "Role not found.");

    public static Result<PermissionCatalogDto> Catalog() =>
        new PermissionCatalogDto(PermissionCatalog.Version, PermissionCatalog.All.Count,
            [.. PermissionCatalog.All.GroupBy(p => p.Module).Select(g => new PermissionModuleDto(g.Key,
                [.. g.Select(p => new PermissionItemDto(p.Code, p.Resource, p.Action, p.Description))]))]);

    public async Task<Result<PagedResult<RoleListItemDto>>> ListAsync(PageRequest request, CancellationToken cancellationToken)
    {
        var query = db.Roles.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(r => r.Name.Contains(term) || (r.Description != null && r.Description.Contains(term)));
        }

        var sort = request.ParseSort(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name" }) is { Count: > 0 } fields ? fields[0] : null;
        query = sort is { Descending: true } ? query.OrderByDescending(r => r.Name) : query.OrderBy(r => r.Name);

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.Id).ToList();

        var userCounts = await db.UserBranchRoles.AsNoTracking().Where(x => ids.Contains(x.RoleId))
            .GroupBy(x => x.RoleId).Select(g => new { g.Key, Count = g.Select(x => x.UserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
        var permissionCounts = await db.RolePermissions.AsNoTracking().Where(x => ids.Contains(x.RoleId))
            .GroupBy(x => x.RoleId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var items = rows.Select(r => new RoleListItemDto(r.Id, r.Name, r.Description, r.IsSystem, r.IsSuperAdmin,
            userCounts.GetValueOrDefault(r.Id), r.IsSuperAdmin ? PermissionCatalog.All.Count : permissionCounts.GetValueOrDefault(r.Id))).ToList();
        return new PagedResult<RoleListItemDto>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<RoleDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        return role is null ? NotFound() : await DetailAsync(role, cancellationToken);
    }

    public async Task<Result<RoleDetailDto>> CreateAsync(CreateRoleRequest request, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var invalid = ValidateName(request.Name);
        if (invalid is not null)
        {
            return invalid;
        }

        if (await NameTakenAsync(request.Name, null, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Ums.RoleNameTaken, "A role with this name already exists.");
        }

        var codes = (request.Permissions ?? []).Distinct(StringComparer.Ordinal).ToList();
        var codeError = CheckCodes(actor, codes);
        if (codeError is not null)
        {
            return codeError;
        }

        var role = new Role(request.Name, request.Description);
        var result = await unitOfWork.ExecuteInTransactionAsync<Result<RoleDetailDto>>(async ct =>
        {
            db.Roles.Add(role);
            await AddPermissionsAsync(role.Id, codes, ct);
            await db.SaveChangesAsync(ct);
            return await DetailAsync(role, ct);
        }, cancellationToken);
        return result;
    }

    public async Task<Result<RoleDetailDto>> UpdateAsync(Guid id, UpdateRoleRequest request, CancellationToken cancellationToken)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (role is null)
        {
            return NotFound();
        }

        var invalid = ValidateName(request.Name);
        if (invalid is not null)
        {
            return invalid;
        }

        if (role.IsSystem && !string.Equals(role.Name, request.Name.Trim(), StringComparison.Ordinal))
        {
            return Error.Conflict(ErrorCodes.Ums.RoleSystemProtected, "System roles cannot be renamed.");
        }

        if (await NameTakenAsync(request.Name, id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Ums.RoleNameTaken, "A role with this name already exists.");
        }

        role.Update(request.Name, request.Description);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(role, cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (role is null)
        {
            return NotFound();
        }

        if (role.IsSystem)
        {
            return Error.Conflict(ErrorCodes.Ums.RoleSystemProtected, "System roles cannot be deleted.");
        }

        if (await db.UserBranchRoles.AnyAsync(x => x.RoleId == id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Ums.RoleInUse, "This role is assigned to users. Remove the assignments first.");
        }

        db.Roles.Remove(role); // soft delete
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<RoleDetailDto>> CloneAsync(Guid id, CloneRoleRequest request, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var source = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (source is null)
        {
            return NotFound();
        }

        var invalid = ValidateName(request.Name);
        if (invalid is not null)
        {
            return invalid;
        }

        if (await NameTakenAsync(request.Name, null, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Ums.RoleNameTaken, "A role with this name already exists.");
        }

        var codes = source.IsSuperAdmin
            ? PermissionCatalog.All.Select(p => p.Code).ToList()
            : await (from rp in db.RolePermissions.AsNoTracking()
                     where rp.RoleId == id
                     join p in db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                     where p.IsActive
                     select p.Code).ToListAsync(cancellationToken);

        var codeError = CheckCodes(actor, codes);
        if (codeError is not null)
        {
            return codeError;
        }

        var clone = new Role(request.Name, source.Description);
        return await unitOfWork.ExecuteInTransactionAsync<Result<RoleDetailDto>>(async ct =>
        {
            db.Roles.Add(clone);
            await AddPermissionsAsync(clone.Id, codes, ct);
            await db.SaveChangesAsync(ct);
            return await DetailAsync(clone, ct);
        }, cancellationToken);
    }

    public async Task<Result<RoleDetailDto>> SetPermissionsAsync(Guid id, SetRolePermissionsRequest request, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (role is null)
        {
            return NotFound();
        }

        if (role.IsSuperAdmin)
        {
            return Error.Conflict(ErrorCodes.Ums.RoleSystemProtected, "Super Admin always has every permission.");
        }

        var wanted = (request.Permissions ?? []).Distinct(StringComparer.Ordinal).ToList();
        var unknown = wanted.FirstOrDefault(c => !PermissionCatalog.Exists(c));
        if (unknown is not null)
        {
            return Error.Failure(ErrorCodes.Ums.PermissionUnknown, $"Unknown permission '{unknown}'.");
        }

        var current = await db.RolePermissions.Where(x => x.RoleId == id).ToListAsync(cancellationToken);
        var currentCodes = await db.Permissions.AsNoTracking().Where(p => current.Select(c => c.PermissionId).Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);

        var existingCodes = current.Select(c => currentCodes.GetValueOrDefault(c.PermissionId, string.Empty)).ToHashSet();
        var added = wanted.Where(c => !existingCodes.Contains(c)).ToList();
        var codeError = CheckCodes(actor, added); // you can only grant what you hold; removing is always allowed
        if (codeError is not null)
        {
            return codeError;
        }

        await unitOfWork.ExecuteInTransactionAsync<bool>(async ct =>
        {
            foreach (var row in current.Where(c => !wanted.Contains(currentCodes.GetValueOrDefault(c.PermissionId, string.Empty))))
            {
                db.RolePermissions.Remove(row);
            }

            await AddPermissionsAsync(id, added, ct);
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);

        await changes.BumpUsersOfRoleAsync(id, cancellationToken);
        return await DetailAsync(role, cancellationToken);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    private async Task AddPermissionsAsync(Guid roleId, List<string> codes, CancellationToken cancellationToken)
    {
        if (codes.Count == 0)
        {
            return;
        }

        var ids = await db.Permissions.AsNoTracking().Where(p => codes.Contains(p.Code) && p.IsActive).Select(p => p.Id)
            .ToListAsync(cancellationToken);
        foreach (var permissionId in ids)
        {
            db.RolePermissions.Add(new RolePermission(roleId, permissionId));
        }
    }

    private static Error? CheckCodes(Actor actor, IReadOnlyList<string> codes)
    {
        var unknown = codes.FirstOrDefault(c => !PermissionCatalog.Exists(c));
        if (unknown is not null)
        {
            return Error.Failure(ErrorCodes.Ums.PermissionUnknown, $"Unknown permission '{unknown}'.");
        }

        if (!actor.IsSuperAdmin)
        {
            var notHeld = codes.FirstOrDefault(c => !actor.Access.HasAnywhere(c));
            if (notHeld is not null)
            {
                return Error.Forbidden(ErrorCodes.Auth.Forbidden, $"You cannot grant '{notHeld}' because you do not hold it yourself.");
            }
        }

        return null;
    }

    private static Error? ValidateName(string? name) =>
        string.IsNullOrWhiteSpace(name) || name.Trim().Length > 100
            ? Error.Validation(new Dictionary<string, string[]> { ["name"] = ["Role name is required (up to 100 characters)."] })
            : null;

    private async Task<bool> NameTakenAsync(string name, Guid? exceptId, CancellationToken cancellationToken)
    {
        // The database collation is case-insensitive, so a plain comparison matches "ORDER MANAGER" with "Order Manager".
        var trimmed = name.Trim();
        return await db.Roles.AsNoTracking().AnyAsync(r => r.Name == trimmed && r.Id != exceptId, cancellationToken);
    }

    private async Task<RoleDetailDto> DetailAsync(Role role, CancellationToken cancellationToken)
    {
        var codes = role.IsSuperAdmin
            ? PermissionCatalog.All.Select(p => p.Code).Order(StringComparer.Ordinal).ToList()
            : await (from rp in db.RolePermissions.AsNoTracking()
                     where rp.RoleId == role.Id
                     join p in db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                     where p.IsActive
                     orderby p.Code
                     select p.Code).ToListAsync(cancellationToken);
        var users = await db.UserBranchRoles.AsNoTracking().Where(x => x.RoleId == role.Id).Select(x => x.UserId).Distinct().CountAsync(cancellationToken);
        return new RoleDetailDto(role.Id, role.Name, role.Description, role.IsSystem, role.IsSuperAdmin, users, codes);
    }
}
