using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Domain.Branches;
using Juvo.Domain.Identity;
using Juvo.Infrastructure.Persistence;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Identity;

public sealed record SeedResult(int PermissionsAdded, int RolesAdded, int BranchesAdded, bool SuperAdminCreated);

/// <summary>
/// Idempotent seed of reference data: the permission catalog, default roles with their permissions, the first branches
/// (Head Office + Online Fulfilment) and, when a password is configured, the first Super Admin. Safe to run on every start.
/// </summary>
public sealed class IdentitySeeder(
    JuvoDbContext db,
    UserManager<StaffUser> users,
    IUnitOfWork unitOfWork,
    IOptions<AuthOptions> options,
    ILogger<IdentitySeeder> logger)
{
    public async Task<SeedResult> SeedAsync(CancellationToken cancellationToken = default)
    {
        var result = await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            var newCodes = await SeedPermissionsAsync(ct);
            var rolesAdded = await SeedRolesAsync(newCodes, ct);
            var branchesAdded = await SeedBranchesAsync(ct);
            var created = await SeedSuperAdminAsync(ct);
            return new SeedResult(newCodes.Count, rolesAdded, branchesAdded, created);
        }, cancellationToken);

        logger.LogInformation(
            "Seed done: {Permissions} permission(s) added, {Roles} role(s) added, {Branches} branch(es) added, super admin created: {Admin}. Catalog version {Version}.",
            result.PermissionsAdded, result.RolesAdded, result.BranchesAdded, result.SuperAdminCreated, PermissionCatalog.Version);
        return result;
    }

    private async Task<HashSet<string>> SeedPermissionsAsync(CancellationToken cancellationToken)
    {
        var existing = await db.Permissions.ToDictionaryAsync(p => p.Code, StringComparer.Ordinal, cancellationToken);
        var added = new HashSet<string>(StringComparer.Ordinal);

        foreach (var definition in PermissionCatalog.All)
        {
            if (existing.TryGetValue(definition.Code, out var permission))
            {
                permission.Refresh(definition.Description, isActive: true);
            }
            else
            {
                db.Permissions.Add(new Permission(definition.Code, definition.Module, definition.Resource, definition.Action, definition.Description));
                added.Add(definition.Code);
            }
        }

        // Codes removed from the catalog stay in the table (old audit rows still make sense) but stop counting.
        foreach (var (code, permission) in existing.Where(kv => !PermissionCatalog.Exists(kv.Key)))
        {
            permission.Refresh(permission.Description, isActive: false);
            logger.LogWarning("Permission {Code} is no longer in the catalog and was deactivated.", code);
        }

        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    private async Task<int> SeedRolesAsync(HashSet<string> newCodes, CancellationToken cancellationToken)
    {
        var permissionIds = await db.Permissions.ToDictionaryAsync(p => p.Code, p => p.Id, StringComparer.Ordinal, cancellationToken);
        var created = 0;

        foreach (var definition in DefaultRoles.All)
        {
            var role = await db.Roles.IgnoreQueryFilters().FirstOrDefaultAsync(r => r.Name == definition.Name, cancellationToken);
            var isNew = role is null;
            if (role is null)
            {
                role = new Role(definition.Name, definition.Description, isSystem: true, isSuperAdmin: definition.IsSuperAdmin);
                db.Roles.Add(role);
                created++;
            }
            else if (role.IsDeleted)
            {
                role.Restore();
            }

            if (definition.IsSuperAdmin)
            {
                continue; // holds every permission by definition; no rows needed
            }

            // A new role gets all its default permissions. An existing one only gets permissions added to the catalog since, so
            // an administrator's own changes to a system role are never undone.
            var codes = PermissionPattern.Expand(definition).Where(c => isNew || newCodes.Contains(c)).ToList();
            if (codes.Count == 0)
            {
                continue;
            }

            var current = isNew
                ? []
                : await db.RolePermissions.Where(rp => rp.RoleId == role.Id).Select(rp => rp.PermissionId).ToListAsync(cancellationToken);
            foreach (var code in codes)
            {
                var permissionId = permissionIds[code];
                if (!current.Contains(permissionId))
                {
                    db.RolePermissions.Add(new RolePermission(role.Id, permissionId));
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        return created;
    }

    private async Task<int> SeedBranchesAsync(CancellationToken cancellationToken)
    {
        // Only on an empty system: later renames or removals by the owner are never undone.
        if (await db.Branches.IgnoreQueryFilters().AnyAsync(cancellationToken))
        {
            return 0;
        }

        var head = new Branch("HO", "Head Office", "প্রধান কার্যালয়", BranchType.HeadOffice);
        head.MarkDefault();
        db.Branches.Add(head);
        await db.SaveChangesAsync(cancellationToken);

        var online = new Branch("ONL", "Online Fulfilment", "অনলাইন ফুলফিলমেন্ট", BranchType.OnlineFulfilment);
        online.UpdateDetails(online.NameEn, online.NameBn, BranchType.OnlineFulfilment, head.Id, null, null, null, null, null, null,
            "Asia/Dhaka", "BDT", null, null, null);
        db.Branches.Add(online);
        await db.SaveChangesAsync(cancellationToken);
        return 2;
    }

    private async Task<bool> SeedSuperAdminAsync(CancellationToken cancellationToken)
    {
        var seed = options.Value.Seed;
        if (string.IsNullOrWhiteSpace(seed.Password))
        {
            return false;
        }

        var superAdminRole = await db.Roles.FirstAsync(r => r.IsSuperAdmin, cancellationToken);
        var hasOne = await db.UserBranchRoles.AnyAsync(x => x.RoleId == superAdminRole.Id, cancellationToken);
        if (hasOne)
        {
            return false;
        }

        var user = new StaffUser
        {
            UserName = seed.UserName,
            Email = seed.Email,
            EmailConfirmed = true,
            FullName = seed.FullName,
            IsGlobalScope = true,
            MustChangePassword = true, // the seeded password is a placeholder; it must be changed at first sign-in
        };

        var identity = await users.CreateAsync(user, seed.Password);
        if (!identity.Succeeded)
        {
            throw new InvalidOperationException(
                "The Super Admin account could not be created: " + string.Join("; ", identity.Errors.Select(e => e.Description)));
        }

        db.UserBranchRoles.Add(new UserBranchRole(user.Id, null, superAdminRole.Id));
        await db.SaveChangesAsync(cancellationToken);
        logger.LogWarning("Super Admin account {Email} created. The password must be changed at first sign-in.", seed.Email);
        return true;
    }
}
