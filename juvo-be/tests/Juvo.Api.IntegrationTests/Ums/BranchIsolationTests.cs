using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Ums;

/// <summary>Branch isolation, per-branch roles and global scope (section 4A of the master prompt), through the real API.</summary>
public class BranchIsolationTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestApi _api = new(factory);

    private static string Code() => "T" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private static string Email(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)] + "@juvo.test";

    /// <summary>Two branches, a branch admin in each, signed in. Branch admins hold the UMS permissions only inside their own branch.</summary>
    private async Task<Scenario> StartAsync()
    {
        var admin = await _api.LoginAdminAsync();
        var a = await _api.CreateBranchAsync(admin.Access, Code(), "Branch A");
        var b = await _api.CreateBranchAsync(admin.Access, Code(), "Branch B");
        var branchAdmin = await _api.CreateRoleAsync(admin.Access, "BranchAdmin-" + Guid.NewGuid().ToString("N")[..8],
            "ums.branch.view", "ums.branch.edit", "ums.branch.deactivate", "ums.user.view", "ums.user.edit", "ums.user.create",
            "ums.user.reset-password", "ums.branch.assign-user", "orders.order.view", "orders.order.confirm");
        var alice = await _api.CreateUserAsync(admin.Access, Email("alice"), "Alice A", false, (a, branchAdmin));
        var bob = await _api.CreateUserAsync(admin.Access, Email("bob"), "Bob B", false, (b, branchAdmin));
        return new Scenario(admin, a, b, branchAdmin, alice, bob, await _api.LoginAsync(alice.Email, alice.Password), await _api.LoginAsync(bob.Email, bob.Password));
    }

    private sealed record Scenario(Tokens Admin, Guid A, Guid B, Guid BranchAdminRole,
        (Guid Id, string Email, string Password) Alice, (Guid Id, string Email, string Password) Bob, Tokens AliceSession, Tokens BobSession);

    private async Task<int> AccessDeniedCountAsync(Guid userId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        return await db.AuditLogs.CountAsync(a => a.UserId == userId && a.Action == AuditAction.AccessDenied, Ct);
    }

    // ---- reading ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_branch_admin_sees_only_their_own_branch()
    {
        var s = await StartAsync();

        var list = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/ums/branches?pageSize=100", s.AliceSession.Access), HttpStatusCode.OK);

        var ids = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([s.A], ids);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync($"/api/v1/ums/branches/{s.A}", s.AliceSession.Access)).StatusCode);
    }

    [Fact]
    public async Task Another_branch_answers_404_and_the_attempt_is_audit_logged()
    {
        var s = await StartAsync();
        var before = await AccessDeniedCountAsync(s.Alice.Id);

        var read = await _api.GetAsync($"/api/v1/ums/branches/{s.B}", s.AliceSession.Access);
        var write = await _api.PutAsync($"/api/v1/ums/branches/{s.B}", new { nameEn = "Hacked", type = "Shop" }, s.AliceSession.Access);
        var deactivate = await _api.PostAsync($"/api/v1/ums/branches/{s.B}/deactivate", null, s.AliceSession.Access);
        var users = await _api.GetAsync($"/api/v1/ums/branches/{s.B}/users", s.AliceSession.Access);

        Assert.All(new[] { read, write, deactivate, users }, r => Assert.Equal(HttpStatusCode.NotFound, r.StatusCode));
        Assert.Equal("UMS.BRANCH_NOT_FOUND", await TestApi.ErrorCodeAsync(read));
        Assert.True(await AccessDeniedCountAsync(s.Alice.Id) >= before + 4, "each attempt must be written to the audit log");

        // Branch B is untouched.
        var b = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/ums/branches/{s.B}", s.Admin.Access), HttpStatusCode.OK);
        Assert.Equal("Branch B", b.GetProperty("nameEn").GetString());
        Assert.True(b.GetProperty("isActive").GetBoolean());
    }

    [Fact]
    public async Task A_branch_admin_sees_only_users_of_their_branch_and_cannot_manage_others()
    {
        var s = await StartAsync();

        var list = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/ums/users?pageSize=100", s.AliceSession.Access), HttpStatusCode.OK);
        var ids = list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(s.Alice.Id, ids);
        Assert.DoesNotContain(s.Bob.Id, ids);
        Assert.DoesNotContain(await _api.UserIdAsync(ApiFactory.SeedEmail), ids); // the global Super Admin is not visible either

        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync($"/api/v1/ums/users/{s.Bob.Id}", s.AliceSession.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.PostAsync($"/api/v1/ums/users/{s.Bob.Id}/reset-password", null, s.AliceSession.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.PostAsync($"/api/v1/ums/users/{s.Bob.Id}/branch-roles", new { branchId = s.A, roleId = s.BranchAdminRole }, s.AliceSession.Access)).StatusCode);
    }

    // ---- X-Branch-Id is validated on every request -------------------------------------------------------

    [Fact]
    public async Task The_branch_header_is_checked_against_the_users_assignments()
    {
        var s = await StartAsync();
        var before = await AccessDeniedCountAsync(s.Alice.Id);

        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/auth/me", s.AliceSession.Access, s.A)).StatusCode);

        var other = await _api.GetAsync("/api/v1/auth/me", s.AliceSession.Access, s.B);
        Assert.Equal(HttpStatusCode.Forbidden, other.StatusCode);
        Assert.Equal("AUTH.BRANCH_FORBIDDEN", await TestApi.ErrorCodeAsync(other));
        Assert.True(await AccessDeniedCountAsync(s.Alice.Id) > before);

        var all = await _api.GetAsync("/api/v1/auth/me", s.AliceSession.Access, branchHeader: "all");
        Assert.Equal("AUTH.BRANCH_FORBIDDEN", await TestApi.ErrorCodeAsync(all));

        var garbage = await _api.GetAsync("/api/v1/auth/me", s.AliceSession.Access, branchHeader: "not-a-guid");
        Assert.Equal(HttpStatusCode.BadRequest, garbage.StatusCode);

        var unknown = await _api.GetAsync("/api/v1/auth/me", s.AliceSession.Access, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, unknown.StatusCode);

        // A user with a single branch needs no header.
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/auth/me", s.AliceSession.Access)).StatusCode);
    }

    // ---- per-branch roles -------------------------------------------------------------------------------

    [Fact]
    public async Task The_same_person_can_hold_different_roles_in_different_branches()
    {
        var admin = await _api.LoginAdminAsync();
        var a = await _api.CreateBranchAsync(admin.Access, Code(), "Branch A");
        var b = await _api.CreateBranchAsync(admin.Access, Code(), "Branch B");
        var editor = await _api.CreateRoleAsync(admin.Access, "Editor-" + Guid.NewGuid().ToString("N")[..8], "ums.branch.view", "ums.branch.edit", "orders.order.confirm");
        var viewer = await _api.CreateRoleAsync(admin.Access, "Viewer-" + Guid.NewGuid().ToString("N")[..8], "ums.branch.view");
        var carol = await _api.CreateUserAsync(admin.Access, Email("carol"), "Carol", false, (a, editor), (b, viewer));
        var session = await _api.LoginAsync(carol.Email, carol.Password);

        // The same permission is effective in A and not in B.
        var inA = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/auth/can?permission=orders.order.confirm", session.Access, a), HttpStatusCode.OK);
        var inB = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/auth/can?permission=orders.order.confirm", session.Access, b), HttpStatusCode.OK);
        Assert.True(inA.GetProperty("allowed").GetBoolean());
        Assert.False(inB.GetProperty("allowed").GetBoolean());

        // /auth/me reports the permissions per branch.
        var me = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/auth/me", session.Access), HttpStatusCode.OK);
        var perBranch = me.GetProperty("branchPermissions");
        Assert.Contains("orders.order.confirm", perBranch.GetProperty(a.ToString()).EnumerateArray().Select(p => p.GetString()));
        Assert.DoesNotContain("orders.order.confirm", perBranch.GetProperty(b.ToString()).EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(2, me.GetProperty("assignments").GetArrayLength());

        // Real endpoints follow: she can edit branch A, and can see but not edit branch B.
        Assert.Equal(HttpStatusCode.OK, (await _api.PutAsync($"/api/v1/ums/branches/{a}", new { nameEn = "Branch A edited", type = "Shop" }, session.Access)).StatusCode);
        var forbidden = await _api.PutAsync($"/api/v1/ums/branches/{b}", new { nameEn = "Branch B edited", type = "Shop" }, session.Access);
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync($"/api/v1/ums/branches/{b}", session.Access)).StatusCode);

        // With no active branch she holds two, so the header decides: unscoped permission checks fall back to "not allowed".
        var noHeader = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/auth/can?permission=orders.order.confirm", session.Access), HttpStatusCode.OK);
        Assert.False(noHeader.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task Effective_permissions_per_branch_can_be_inspected_by_an_admin()
    {
        var s = await StartAsync();

        var effective = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/ums/users/{s.Alice.Id}/effective-permissions", s.Admin.Access), HttpStatusCode.OK);

        var inBranchA = effective.EnumerateArray().Single(e => e.TryGetProperty("branchId", out var id) && id.ValueKind == JsonValueKind.String && id.GetGuid() == s.A);
        Assert.Contains("orders.order.confirm", inBranchA.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()));
        Assert.DoesNotContain(effective.EnumerateArray(), e => e.TryGetProperty("branchId", out var id) && id.ValueKind == JsonValueKind.String && id.GetGuid() == s.B);
    }

    // ---- global scope -----------------------------------------------------------------------------------

    [Fact]
    public async Task Global_scope_users_see_every_branch_and_may_pick_any_or_all()
    {
        var admin = await _api.LoginAdminAsync();
        var a = await _api.CreateBranchAsync(admin.Access, Code(), "Branch A");
        var role = await _api.CreateRoleAsync(admin.Access, "GlobalViewer-" + Guid.NewGuid().ToString("N")[..8], "ums.branch.view", "orders.order.view");
        var gina = await _api.CreateUserAsync(admin.Access, Email("gina"), "Gina Global", true, (null, role));
        var session = await _api.LoginAsync(gina.Email, gina.Password);

        var list = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/ums/branches?pageSize=100", session.Access), HttpStatusCode.OK);
        Assert.True(list.GetProperty("total").GetInt32() >= 3); // HO, ONL and A

        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/auth/me", session.Access, a)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/auth/me", session.Access, branchHeader: "all")).StatusCode);
        var can = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/auth/can?permission=orders.order.view", session.Access, a), HttpStatusCode.OK);
        Assert.True(can.GetProperty("allowed").GetBoolean()); // a role without a branch counts in every branch for global users
    }

    [Fact]
    public async Task A_role_without_a_branch_gives_nothing_to_a_user_who_is_not_global()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.CreateRoleAsync(admin.Access, "NotGlobal-" + Guid.NewGuid().ToString("N")[..8], "orders.order.view");
        var ho = await _api.BranchIdAsync("HO");
        var user = await _api.CreateUserAsync(admin.Access, Email("scoped"), "Scoped", false, (ho, await _api.RoleIdAsync("Support")));
        var session = await _api.LoginAsync(user.Email, user.Password);

        // Make the user non-global with a stray branch-less assignment is impossible through the API: the check is on creation.
        var bad = await _api.PostAsync($"/api/v1/ums/users/{user.Id}/branch-roles", new { branchId = (Guid?)null, roleId = role }, admin.Access);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        // What the user does hold comes only from the HO assignment (Support): view yes, confirm no.
        var view = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/auth/can?permission=orders.order.view", session.Access, ho), HttpStatusCode.OK);
        Assert.True(view.GetProperty("allowed").GetBoolean());
        var confirm = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/auth/can?permission=orders.order.confirm", session.Access, ho), HttpStatusCode.OK);
        Assert.False(confirm.GetProperty("allowed").GetBoolean());
    }

    [Fact]
    public async Task The_switch_all_permission_lets_a_scoped_user_pick_all_branches_but_stays_scoped()
    {
        var admin = await _api.LoginAdminAsync();
        var a = await _api.CreateBranchAsync(admin.Access, Code(), "Branch A");
        var b = await _api.CreateBranchAsync(admin.Access, Code(), "Branch B");
        var role = await _api.CreateRoleAsync(admin.Access, "Switcher-" + Guid.NewGuid().ToString("N")[..8], "ums.branch.view", "ums.branch.switch-all");
        var dave = await _api.CreateUserAsync(admin.Access, Email("dave"), "Dave", false, (a, role));
        var session = await _api.LoginAsync(dave.Email, dave.Password);

        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/auth/me", session.Access, branchHeader: "all")).StatusCode);
        var list = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/ums/branches?pageSize=100", session.Access, branchHeader: "all"), HttpStatusCode.OK);
        Assert.Equal([a], list.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/auth/me", session.Access, b)).StatusCode);
    }

    // ---- creating and assigning inside your own branch --------------------------------------------------

    [Fact]
    public async Task A_branch_admin_can_create_users_in_their_branch_but_not_in_another()
    {
        var s = await StartAsync();
        var support = await _api.RoleIdAsync("Support");

        // Support holds fewer permissions than a branch admin, so it may be granted here.
        var supportRole = await _api.CreateRoleAsync(s.Admin.Access, "Small-" + Guid.NewGuid().ToString("N")[..8], "orders.order.view");

        var inA = await _api.PostAsync("/api/v1/ums/users", new
        {
            email = Email("newa"), fullName = "New In A", assignments = new[] { new { branchId = (Guid?)s.A, roleId = supportRole } },
        }, s.AliceSession.Access);
        Assert.Equal(HttpStatusCode.Created, inA.StatusCode);

        var inB = await _api.PostAsync("/api/v1/ums/users", new
        {
            email = Email("newb"), fullName = "New In B", assignments = new[] { new { branchId = (Guid?)s.B, roleId = supportRole } },
        }, s.AliceSession.Access);
        Assert.Equal(HttpStatusCode.Forbidden, inB.StatusCode);

        var global = await _api.PostAsync("/api/v1/ums/users", new
        {
            email = Email("newg"), fullName = "Global", isGlobalScope = true, assignments = new[] { new { branchId = (Guid?)null, roleId = supportRole } },
        }, s.AliceSession.Access);
        Assert.Equal(HttpStatusCode.Forbidden, global.StatusCode);

        var noBranch = await _api.PostAsync("/api/v1/ums/users", new { email = Email("none"), fullName = "None", assignments = Array.Empty<object>() }, s.AliceSession.Access);
        Assert.Equal(HttpStatusCode.BadRequest, noBranch.StatusCode);
        Assert.NotEqual(Guid.Empty, support);
    }

    // ---- branch lifecycle rules -------------------------------------------------------------------------

    [Fact]
    public async Task The_default_branch_cannot_be_deactivated_and_there_is_only_ever_one_default()
    {
        var admin = await _api.LoginAdminAsync();
        var ho = await _api.BranchIdAsync("HO");
        var fresh = await _api.CreateBranchAsync(admin.Access, Code(), "New Default");

        var deactivate = await _api.PostAsync($"/api/v1/ums/branches/{ho}/deactivate", null, admin.Access);
        Assert.Equal(HttpStatusCode.Conflict, deactivate.StatusCode);
        Assert.Equal("UMS.BRANCH_DEFAULT_PROTECTED", await TestApi.ErrorCodeAsync(deactivate));

        Assert.Equal(HttpStatusCode.NoContent, (await _api.PostAsync($"/api/v1/ums/branches/{fresh}/set-default", null, admin.Access)).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        var defaults = await db.Branches.Where(b => b.IsDefault).Select(b => b.Id).ToListAsync(Ct);
        Assert.Equal([fresh], defaults);

        // Restore HO so other tests keep a predictable default.
        Assert.Equal(HttpStatusCode.NoContent, (await _api.PostAsync($"/api/v1/ums/branches/{ho}/set-default", null, admin.Access)).StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_branch_removes_access_at_once_and_reactivating_restores_it()
    {
        var s = await StartAsync();
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/auth/me", s.BobSession.Access, s.B)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.PostAsync($"/api/v1/ums/branches/{s.B}/deactivate", null, s.Admin.Access)).StatusCode);

        // Bob's token is stale; after a silent refresh, branch B is no longer his.
        Assert.Equal("AUTH.TOKEN_EXPIRED", await TestApi.ErrorCodeAsync(await _api.GetAsync("/api/v1/auth/me", s.BobSession.Access, s.B)));
        var refreshed = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = s.BobSession.Refresh }), HttpStatusCode.OK);
        var access = refreshed.GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/auth/me", access, s.B)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.PostAsync($"/api/v1/ums/branches/{s.B}/activate", null, s.Admin.Access)).StatusCode);
        var again = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = refreshed.GetProperty("refreshToken").GetString() }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/auth/me", again.GetProperty("accessToken").GetString(), s.B)).StatusCode);
    }

    [Fact]
    public async Task Branch_codes_are_unique_normalized_and_validated_and_only_global_users_create_branches()
    {
        var s = await StartAsync();
        var code = Code();

        var created = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/ums/branches",
            new { code = code.ToLowerInvariant(), nameEn = "Lower", nameBn = "শাখা", type = "Warehouse", timeZoneId = "Asia/Dhaka", defaultCurrency = "bdt" }, s.Admin.Access), HttpStatusCode.Created);
        Assert.Equal(code, created.GetProperty("code").GetString());
        Assert.Equal("BDT", created.GetProperty("defaultCurrency").GetString());
        Assert.Equal("Warehouse", created.GetProperty("type").GetString());

        var duplicate = await _api.PostAsync("/api/v1/ums/branches", new { code, nameEn = "Dup", type = "Shop" }, s.Admin.Access);
        Assert.Equal("UMS.BRANCH_CODE_TAKEN", await TestApi.ErrorCodeAsync(duplicate));

        Assert.Equal(HttpStatusCode.BadRequest, (await _api.PostAsync("/api/v1/ums/branches", new { code = "x", nameEn = "Bad", type = "Shop" }, s.Admin.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.PostAsync("/api/v1/ums/branches", new { code = Code(), nameEn = "Bad Zone", type = "Shop", timeZoneId = "Mars/Olympus" }, s.Admin.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.PostAsync("/api/v1/ums/branches", new { code = Code(), nameEn = "Bad Lat", type = "Shop", latitude = 123.0 }, s.Admin.Access)).StatusCode);

        // A branch admin holds ums.branch.edit but not creation rights over the whole company (no ums.branch.create).
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.PostAsync("/api/v1/ums/branches", new { code = Code(), nameEn = "Sneaky", type = "Shop" }, s.AliceSession.Access)).StatusCode);
    }

    [Fact]
    public async Task Branch_users_can_be_listed_for_a_branch_and_assignments_added_and_removed()
    {
        var s = await StartAsync();

        var users = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/ums/branches/{s.A}/users", s.AliceSession.Access), HttpStatusCode.OK);
        Assert.Contains(s.Alice.Id, users.EnumerateArray().Select(u => u.GetProperty("userId").GetGuid()));
        Assert.DoesNotContain(s.Bob.Id, users.EnumerateArray().Select(u => u.GetProperty("userId").GetGuid()));

        var smallRole = await _api.CreateRoleAsync(s.Admin.Access, "Tiny-" + Guid.NewGuid().ToString("N")[..8], "orders.order.view");
        var added = await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/ums/users/{s.Alice.Id}/branch-roles", new { branchId = s.A, roleId = smallRole }, s.Admin.Access), HttpStatusCode.Created);
        var assignmentId = added.GetProperty("assignmentId").GetGuid();

        var duplicate = await _api.PostAsync($"/api/v1/ums/users/{s.Alice.Id}/branch-roles", new { branchId = s.A, roleId = smallRole }, s.Admin.Access);
        Assert.Equal("UMS.ASSIGNMENT_EXISTS", await TestApi.ErrorCodeAsync(duplicate));

        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/ums/users/{s.Alice.Id}/branch-roles/{assignmentId}", s.Admin.Access)).StatusCode);
        Assert.Equal("UMS.ASSIGNMENT_NOT_FOUND", await TestApi.ErrorCodeAsync(await _api.DeleteAsync($"/api/v1/ums/users/{s.Alice.Id}/branch-roles/{assignmentId}", s.Admin.Access)));
    }
}
