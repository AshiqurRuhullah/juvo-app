using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Branches;
using Juvo.Infrastructure.Identity;
using Juvo.Infrastructure.Persistence;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Ums;

public class SeederTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T> InDbAsync<T>(Func<JuvoDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<JuvoDbContext>());
    }

    private async Task<SeedResult> ReseedAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentitySeeder>().SeedAsync(Ct);
    }

    [Fact]
    public async Task Seed_creates_the_whole_catalog_the_seven_default_roles_and_the_first_branches()
    {
        Assert.Equal(PermissionCatalog.All.Count, await InDbAsync(db => db.Permissions.CountAsync(p => p.IsActive, Ct)));

        var roles = await InDbAsync(db => db.Roles.Select(r => new { r.Name, r.IsSystem, r.IsSuperAdmin }).ToListAsync(Ct));
        Assert.Equal(DefaultRoles.All.Count, roles.Count(r => r.IsSystem));
        Assert.Single(roles, r => r.IsSuperAdmin);

        var branches = await InDbAsync(db => db.Branches.OrderBy(b => b.Code).ToListAsync(Ct));
        Assert.Equal(["HO", "ONL"], branches.Select(b => b.Code));
        Assert.Single(branches, b => b.IsDefault);
        Assert.Equal("HO", branches.Single(b => b.IsDefault).Code);
        Assert.Equal(BranchType.HeadOffice, branches[0].Type);
        Assert.Equal(branches[0].Id, branches[1].ParentBranchId);
    }

    [Fact]
    public async Task Default_roles_get_the_permissions_from_the_roles_matrix()
    {
        foreach (var definition in DefaultRoles.All.Where(r => !r.IsSuperAdmin))
        {
            var codes = await InDbAsync(async db => await (
                from rp in db.RolePermissions
                join role in db.Roles on rp.RoleId equals role.Id
                join p in db.Permissions on rp.PermissionId equals p.Id
                where role.Name == definition.Name
                select p.Code).ToListAsync(Ct));

            Assert.Equal(PermissionPattern.Expand(definition).Order().ToList(), codes.Order().ToList());
        }
    }

    [Fact]
    public async Task Super_admin_user_is_created_with_global_scope_and_must_change_the_password()
    {
        var user = await InDbAsync(db => db.Users.SingleAsync(u => u.Email == ApiFactory.SeedEmail, Ct));

        Assert.True(user.IsGlobalScope);
        Assert.True(user.IsActive);
        Assert.NotNull(user.PasswordHash);
        Assert.NotEqual(ApiFactory.SeedPassword, user.PasswordHash); // hashed

        var assignment = await InDbAsync(async db => await (
            from ubr in db.UserBranchRoles
            join role in db.Roles on ubr.RoleId equals role.Id
            where ubr.UserId == user.Id
            select new { ubr.BranchId, role.IsSuperAdmin }).SingleAsync(Ct));
        Assert.Null(assignment.BranchId); // everywhere
        Assert.True(assignment.IsSuperAdmin);
    }

    [Fact]
    public async Task Running_the_seed_again_changes_nothing()
    {
        var before = await InDbAsync(async db => (
            await db.Permissions.CountAsync(Ct), await db.Roles.CountAsync(Ct), await db.RolePermissions.CountAsync(Ct),
            await db.Branches.CountAsync(Ct), await db.Users.CountAsync(Ct), await db.UserBranchRoles.CountAsync(Ct)));

        var result = await ReseedAsync();

        Assert.Equal(new SeedResult(0, 0, 0, false), result);
        var after = await InDbAsync(async db => (
            await db.Permissions.CountAsync(Ct), await db.Roles.CountAsync(Ct), await db.RolePermissions.CountAsync(Ct),
            await db.Branches.CountAsync(Ct), await db.Users.CountAsync(Ct), await db.UserBranchRoles.CountAsync(Ct)));
        Assert.Equal(before, after);
    }
}

/// <summary>Seed behaviour when data was changed since the first run. Own database, because these tests change reference data.</summary>
public class SeederChangeTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<T> InDbAsync<T>(Func<JuvoDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<JuvoDbContext>());
    }

    private async Task<SeedResult> ReseedAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IdentitySeeder>().SeedAsync(Ct);
    }

    [Fact]
    public async Task An_administrators_change_to_a_system_role_is_never_undone_by_the_seed()
    {
        // The owner removes a permission from the Support role...
        await InDbAsync(async db =>
        {
            var role = await db.Roles.SingleAsync(r => r.Name == "Support", Ct);
            var permission = await db.Permissions.SingleAsync(p => p.Code == "support.ticket.close", Ct);
            var row = await db.RolePermissions.SingleAsync(rp => rp.RoleId == role.Id && rp.PermissionId == permission.Id, Ct);
            db.RolePermissions.Remove(row);
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        await ReseedAsync();

        // ...and it stays removed.
        var has = await InDbAsync(async db => await (
            from rp in db.RolePermissions
            join role in db.Roles on rp.RoleId equals role.Id
            join p in db.Permissions on rp.PermissionId equals p.Id
            where role.Name == "Support" && p.Code == "support.ticket.close"
            select rp.Id).AnyAsync(Ct));
        Assert.False(has);
    }

    [Fact]
    public async Task A_permission_added_to_the_catalog_later_is_granted_to_matching_default_roles_only()
    {
        // Simulate a catalog that gained "orders.order.print": delete the permission and its role rows, then reseed.
        await InDbAsync(async db =>
        {
            var permission = await db.Permissions.SingleAsync(p => p.Code == "orders.order.print", Ct);
            await db.RolePermissions.Where(rp => rp.PermissionId == permission.Id).ExecuteDeleteAsync(Ct);
            await db.Permissions.Where(p => p.Id == permission.Id).ExecuteDeleteAsync(Ct);
            return 0;
        });

        var result = await ReseedAsync();

        Assert.Equal(1, result.PermissionsAdded);
        var roles = await InDbAsync(async db => await (
            from rp in db.RolePermissions
            join role in db.Roles on rp.RoleId equals role.Id
            join p in db.Permissions on rp.PermissionId equals p.Id
            where p.Code == "orders.order.print"
            select role.Name).ToListAsync(Ct));
        Assert.Contains("Manager", roles);
        Assert.Contains("Order Manager", roles);
        Assert.DoesNotContain("Content Editor", roles);
        Assert.DoesNotContain("Accountant", roles);
    }

    [Fact]
    public async Task Permissions_removed_from_the_catalog_are_deactivated_not_deleted()
    {
        await InDbAsync(async db =>
        {
            db.Permissions.Add(new Juvo.Domain.Identity.Permission("legacy.thing.do", "legacy", "thing", "do", "Old permission"));
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        await ReseedAsync();

        var legacy = await InDbAsync(db => db.Permissions.SingleAsync(p => p.Code == "legacy.thing.do", Ct));
        Assert.False(legacy.IsActive);
    }
}
