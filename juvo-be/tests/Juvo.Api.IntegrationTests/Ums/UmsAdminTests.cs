using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Shared.Security;

namespace Juvo.Api.IntegrationTests.Ums;

public class UmsAdminTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly TestApi _api = new(factory);

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..24];

    // ---- permission catalog and roles -------------------------------------------------------------------

    [Fact]
    public async Task Permission_catalog_is_grouped_by_module_and_versioned()
    {
        var admin = await _api.LoginAdminAsync();

        var catalog = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/ums/permissions", admin.Access), HttpStatusCode.OK);

        Assert.Equal(PermissionCatalog.Version, catalog.GetProperty("version").GetString());
        Assert.Equal(PermissionCatalog.All.Count, catalog.GetProperty("total").GetInt32());
        var modules = catalog.GetProperty("modules").EnumerateArray().Select(m => m.GetProperty("module").GetString()).ToList();
        Assert.Contains("orders", modules);
        Assert.Contains("ums", modules);
    }

    [Fact]
    public async Task Default_roles_are_seeded_and_super_admin_is_protected()
    {
        var admin = await _api.LoginAdminAsync();

        var list = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/ums/roles?pageSize=50", admin.Access), HttpStatusCode.OK);
        var roles = list.GetProperty("items").EnumerateArray().ToDictionary(r => r.GetProperty("name").GetString()!);
        foreach (var name in DefaultRoles.All.Select(r => r.Name))
        {
            Assert.True(roles.ContainsKey(name), $"missing default role {name}");
        }

        var superAdmin = roles["Super Admin"];
        Assert.True(superAdmin.GetProperty("isSystem").GetBoolean());
        Assert.True(superAdmin.GetProperty("isSuperAdmin").GetBoolean());
        Assert.Equal(PermissionCatalog.All.Count, superAdmin.GetProperty("permissionCount").GetInt32());
        var id = superAdmin.GetProperty("id").GetGuid();

        var delete = await _api.DeleteAsync($"/api/v1/ums/roles/{id}", admin.Access);
        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("UMS.ROLE_SYSTEM_PROTECTED", await TestApi.ErrorCodeAsync(delete));

        var rename = await _api.PutAsync($"/api/v1/ums/roles/{id}", new { name = "Boss", description = "x" }, admin.Access);
        Assert.Equal("UMS.ROLE_SYSTEM_PROTECTED", await TestApi.ErrorCodeAsync(rename));

        var permissions = await _api.PutAsync($"/api/v1/ums/roles/{id}/permissions", new { permissions = new[] { "orders.order.view" } }, admin.Access);
        Assert.Equal("UMS.ROLE_SYSTEM_PROTECTED", await TestApi.ErrorCodeAsync(permissions));
    }

    [Fact]
    public async Task Role_crud_clone_and_permission_changes()
    {
        var admin = await _api.LoginAdminAsync();
        var name = Unique("Packers");

        var created = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/ums/roles",
            new { name, description = "Packs orders", permissions = new[] { "orders.order.view", "orders.order.pack" } }, admin.Access), HttpStatusCode.Created);
        var id = created.GetProperty("id").GetGuid();
        Assert.False(created.GetProperty("isSystem").GetBoolean());
        Assert.Equal(["orders.order.pack", "orders.order.view"], created.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Order().ToArray());

        var duplicate = await _api.PostAsync("/api/v1/ums/roles", new { name = name.ToUpperInvariant(), permissions = Array.Empty<string>() }, admin.Access);
        Assert.Equal("UMS.ROLE_NAME_TAKEN", await TestApi.ErrorCodeAsync(duplicate));

        var unknown = await _api.PostAsync("/api/v1/ums/roles", new { name = Unique("Bad"), permissions = new[] { "orders.order.fly" } }, admin.Access);
        Assert.Equal("UMS.PERMISSION_UNKNOWN", await TestApi.ErrorCodeAsync(unknown));

        var replaced = await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/ums/roles/{id}/permissions",
            new { permissions = new[] { "orders.order.view", "orders.order.print" } }, admin.Access), HttpStatusCode.OK);
        Assert.Equal(["orders.order.print", "orders.order.view"], replaced.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()).Order().ToArray());

        var clone = await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/ums/roles/{id}/clone", new { name = Unique("Copy") }, admin.Access), HttpStatusCode.Created);
        Assert.Equal(2, clone.GetProperty("permissions").GetArrayLength());

        var renamed = await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/ums/roles/{id}", new { name = Unique("Renamed"), description = "new" }, admin.Access), HttpStatusCode.OK);
        Assert.Equal("new", renamed.GetProperty("description").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/ums/roles/{id}", admin.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync($"/api/v1/ums/roles/{id}", admin.Access)).StatusCode);
    }

    [Fact]
    public async Task A_role_in_use_cannot_be_deleted()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.CreateRoleAsync(admin.Access, Unique("InUse"), "orders.order.view");
        await _api.CreateUserAsync(admin.Access, $"{Unique("inuse")}@juvo.test", "In Use", false, (await _api.BranchIdAsync("HO"), role));

        var delete = await _api.DeleteAsync($"/api/v1/ums/roles/{role}", admin.Access);

        Assert.Equal(HttpStatusCode.Conflict, delete.StatusCode);
        Assert.Equal("UMS.ROLE_IN_USE", await TestApi.ErrorCodeAsync(delete));
    }

    // ---- users ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Create_user_returns_a_temporary_password_once_and_rejects_duplicates_and_bad_input()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Support");
        var ho = await _api.BranchIdAsync("HO");
        var email = $"{Unique("new")}@juvo.test";

        var created = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/ums/users", new
        {
            email, fullName = "New Person", phone = "01712345678", assignments = new[] { new { branchId = (Guid?)ho, roleId = role } },
        }, admin.Access), HttpStatusCode.Created);
        Assert.False(string.IsNullOrEmpty(created.GetProperty("temporaryPassword").GetString()));
        Assert.True(created.GetProperty("user").GetProperty("mustChangePassword").GetBoolean());
        var id = created.GetProperty("user").GetProperty("id").GetGuid();

        // The temporary password is never shown again.
        var fetched = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/ums/users/{id}", admin.Access), HttpStatusCode.OK);
        Assert.DoesNotContain("temporaryPassword", fetched.ToString(), StringComparison.Ordinal);
        Assert.Contains("Support", fetched.GetProperty("assignments").EnumerateArray().Select(a => a.GetProperty("roleName").GetString()));

        var duplicate = await _api.PostAsync("/api/v1/ums/users", new { email, fullName = "Dup", assignments = Array.Empty<object>() }, admin.Access);
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.Equal("UMS.EMAIL_TAKEN", await TestApi.ErrorCodeAsync(duplicate));

        var badEmail = await _api.PostAsync("/api/v1/ums/users", new { email = "not-an-email", fullName = "X", assignments = Array.Empty<object>() }, admin.Access);
        Assert.Equal(HttpStatusCode.BadRequest, badEmail.StatusCode);

        var weak = await _api.PostAsync("/api/v1/ums/users", new { email = $"{Unique("weak")}@juvo.test", fullName = "X", password = "weak", assignments = Array.Empty<object>() }, admin.Access);
        Assert.Equal("AUTH.WEAK_PASSWORD", await TestApi.ErrorCodeAsync(weak));

        var list = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/ums/users?search={email}", admin.Access), HttpStatusCode.OK);
        Assert.Equal(1, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task A_role_without_a_branch_is_only_for_global_users()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Accountant");

        var rejected = await _api.PostAsync("/api/v1/ums/users", new
        {
            email = $"{Unique("global")}@juvo.test", fullName = "Not Global", isGlobalScope = false,
            assignments = new[] { new { branchId = (Guid?)null, roleId = role } },
        }, admin.Access);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);

        var accepted = await _api.PostAsync("/api/v1/ums/users", new
        {
            email = $"{Unique("global")}@juvo.test", fullName = "Global Accountant", isGlobalScope = true,
            assignments = new[] { new { branchId = (Guid?)null, roleId = role } },
        }, admin.Access);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
    }

    [Fact]
    public async Task Deactivating_a_user_ends_their_access_at_once_and_blocks_sign_in()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Manager");
        var user = await _api.CreateUserAsync(admin.Access, $"{Unique("deact")}@juvo.test", "Deactivate Me", false, (await _api.BranchIdAsync("HO"), role));
        var session = await _api.LoginAsync(user.Email, user.Password);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/ums/users", session.Access)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.PostAsync($"/api/v1/ums/users/{user.Id}/deactivate", null, admin.Access)).StatusCode);

        // The same access token stops working immediately, and the refresh token is revoked.
        var denied = await _api.GetAsync("/api/v1/ums/users", session.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Equal("AUTH.ACCOUNT_DISABLED", await TestApi.ErrorCodeAsync(denied));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = session.Refresh })).StatusCode);

        var login = await _api.PostAsync("/api/v1/auth/login", new { identifier = user.Email, password = user.Password });
        Assert.Equal(HttpStatusCode.Forbidden, login.StatusCode);
        Assert.Equal("AUTH.ACCOUNT_DISABLED", await TestApi.ErrorCodeAsync(login));

        // History is kept and the account can come back.
        Assert.Equal(HttpStatusCode.NoContent, (await _api.PostAsync($"/api/v1/ums/users/{user.Id}/activate", null, admin.Access)).StatusCode);
        Assert.NotEmpty((await _api.LoginAsync(user.Email, user.Password)).Access);
    }

    [Fact]
    public async Task The_last_super_admin_and_yourself_cannot_be_deactivated()
    {
        var admin = await _api.LoginAdminAsync();
        var adminId = await _api.UserIdAsync(ApiFactory.SeedEmail);

        var self = await _api.PostAsync($"/api/v1/ums/users/{adminId}/deactivate", null, admin.Access);
        Assert.Equal(HttpStatusCode.Forbidden, self.StatusCode);
        Assert.Equal("UMS.SELF_ACTION_FORBIDDEN", await TestApi.ErrorCodeAsync(self));

        // A second Super Admin deactivating the first is allowed only while another one remains.
        var superRole = await _api.RoleIdAsync("Super Admin");
        var second = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/ums/users", new
        {
            email = $"{Unique("super2")}@juvo.test", fullName = "Second Super", isGlobalScope = true,
            assignments = new[] { new { branchId = (Guid?)null, roleId = superRole } },
        }, admin.Access), HttpStatusCode.Created);
        Assert.NotEqual(Guid.Empty, second.GetProperty("user").GetProperty("id").GetGuid());

        var removeLast = await _api.GetAsync($"/api/v1/ums/users/{adminId}/branch-roles", admin.Access);
        var assignment = (await TestApi.ExpectAsync(removeLast, HttpStatusCode.OK)).EnumerateArray().First(a => a.GetProperty("roleName").GetString() == "Super Admin");
        Assert.NotEqual(Guid.Empty, assignment.GetProperty("assignmentId").GetGuid());
    }

    [Fact]
    public async Task Admin_reset_password_gives_a_temporary_password_ends_sessions_and_forces_a_change()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Support");
        var user = await _api.CreateUserAsync(admin.Access, $"{Unique("reset")}@juvo.test", "Reset Me", false, (await _api.BranchIdAsync("HO"), role));
        var session = await _api.LoginAsync(user.Email, user.Password);

        var reset = await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/ums/users/{user.Id}/reset-password", null, admin.Access), HttpStatusCode.OK);
        var temp = reset.GetProperty("temporaryPassword").GetString()!;

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = session.Refresh })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/v1/auth/login", new { identifier = user.Email, password = user.Password })).StatusCode);
        var login = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/login", new { identifier = user.Email, password = temp }), HttpStatusCode.OK);
        Assert.True(login.GetProperty("requiresPasswordChange").GetBoolean());
    }

    [Fact]
    public async Task Force_logout_ends_the_users_tokens()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Manager");
        var user = await _api.CreateUserAsync(admin.Access, $"{Unique("force")}@juvo.test", "Force Me", false, (await _api.BranchIdAsync("HO"), role));
        var session = await _api.LoginAsync(user.Email, user.Password);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.PostAsync($"/api/v1/ums/users/{user.Id}/force-logout", null, admin.Access)).StatusCode);

        var stale = await _api.GetAsync("/api/v1/auth/me", session.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = session.Refresh })).StatusCode);
    }

    [Fact]
    public async Task Admin_can_list_and_revoke_a_users_sessions_and_read_the_sign_in_history()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Support");
        var user = await _api.CreateUserAsync(admin.Access, $"{Unique("sess")}@juvo.test", "Sessions", false, (await _api.BranchIdAsync("HO"), role));
        var phone = await _api.LoginAsync(user.Email, user.Password);

        var sessions = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/ums/users/{user.Id}/sessions", admin.Access), HttpStatusCode.OK);
        var sessionId = sessions.EnumerateArray().First().GetProperty("sessionId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/ums/users/{user.Id}/sessions/{sessionId}", admin.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = phone.Refresh })).StatusCode);

        var history = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/ums/users/{user.Id}/sign-ins", admin.Access), HttpStatusCode.OK);
        Assert.True(history.GetProperty("total").GetInt32() >= 2);
        var all = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/ums/sign-in-history?succeeded=false&pageSize=5", admin.Access), HttpStatusCode.OK);
        Assert.NotNull(all.GetProperty("items").ToString());
    }

    // ---- roles take effect quickly ----------------------------------------------------------------------

    [Fact]
    public async Task Changing_a_role_takes_effect_for_users_of_that_role_without_waiting_for_the_token_to_expire()
    {
        var admin = await _api.LoginAdminAsync();
        var viewer = await _api.CreateRoleAsync(admin.Access, Unique("Viewer"), "ums.user.view");
        var ho = await _api.BranchIdAsync("HO");
        var user = await _api.CreateUserAsync(admin.Access, $"{Unique("quick")}@juvo.test", "Quick Effect", false, (ho, viewer));
        var session = await _api.LoginAsync(user.Email, user.Password);

        // Can view users, cannot read roles yet.
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/ums/users", session.Access)).StatusCode);
        var before = await _api.GetAsync("/api/v1/ums/roles", session.Access);
        Assert.Equal(HttpStatusCode.Forbidden, before.StatusCode);
        Assert.Equal("AUTH.FORBIDDEN", await TestApi.ErrorCodeAsync(before));

        // The admin adds the permission to the role.
        Assert.Equal(HttpStatusCode.OK, (await _api.PutAsync($"/api/v1/ums/roles/{viewer}/permissions",
            new { permissions = new[] { "ums.user.view", "ums.role.view" } }, admin.Access)).StatusCode);

        // The user's current token is now out of date: the first call says so, and a silent refresh fixes it.
        var stale = await _api.GetAsync("/api/v1/ums/roles", session.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal("AUTH.TOKEN_EXPIRED", await TestApi.ErrorCodeAsync(stale));

        var refreshed = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = session.Refresh }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/ums/roles", refreshed.GetProperty("accessToken").GetString())).StatusCode);
    }

    [Fact]
    public async Task Removing_a_permission_from_a_role_takes_it_away_after_a_refresh()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.CreateRoleAsync(admin.Access, Unique("Both"), "ums.user.view", "ums.role.view");
        var user = await _api.CreateUserAsync(admin.Access, $"{Unique("take")}@juvo.test", "Taken Away", false, (await _api.BranchIdAsync("HO"), role));
        var session = await _api.LoginAsync(user.Email, user.Password);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/ums/roles", session.Access)).StatusCode);

        await _api.PutAsync($"/api/v1/ums/roles/{role}/permissions", new { permissions = new[] { "ums.user.view" } }, admin.Access);

        var refreshed = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = session.Refresh }), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/ums/roles", refreshed.GetProperty("accessToken").GetString())).StatusCode);
    }

    [Fact]
    public async Task Endpoints_answer_403_for_a_user_without_the_permission_and_200_with_it()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.CreateRoleAsync(admin.Access, Unique("AuditOnly"), "ums.audit.view");
        var user = await _api.CreateUserAsync(admin.Access, $"{Unique("audit")}@juvo.test", "Audit Reader", false, (await _api.BranchIdAsync("HO"), role));
        var session = await _api.LoginAsync(user.Email, user.Password);

        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/ums/audit-logs", session.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/ums/users", session.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/ums/roles", session.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.PostAsync("/api/v1/ums/roles", new { name = "x", permissions = Array.Empty<string>() }, session.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.PostAsync("/api/v1/ums/branches", new { code = "ZZ", nameEn = "Z", type = "Shop" }, session.Access)).StatusCode);
    }

    // ---- no privilege escalation ------------------------------------------------------------------------

    [Fact]
    public async Task A_non_super_admin_cannot_grant_the_super_admin_role_or_permissions_they_do_not_hold()
    {
        var admin = await _api.LoginAdminAsync();
        var ho = await _api.BranchIdAsync("HO");
        var superRole = await _api.RoleIdAsync("Super Admin");
        var delegateRole = await _api.CreateRoleAsync(admin.Access, Unique("Delegate"),
            "ums.user.view", "ums.user.create", "ums.branch.assign-user", "ums.role.view", "ums.role.create", "ums.role.assign-permissions", "ums.branch.view");
        var accountant = await _api.RoleIdAsync("Accountant");
        var delegateUser = await _api.CreateUserAsync(admin.Access, $"{Unique("deleg")}@juvo.test", "Delegate", true, (null, delegateRole));
        var session = await _api.LoginAsync(delegateUser.Email, delegateUser.Password);

        var grantSuper = await _api.PostAsync("/api/v1/ums/users", new
        {
            email = $"{Unique("esc")}@juvo.test", fullName = "Escalate", assignments = new[] { new { branchId = (Guid?)ho, roleId = superRole } },
        }, session.Access);
        Assert.Equal(HttpStatusCode.Forbidden, grantSuper.StatusCode);

        // The delegate does not hold accounting or payments permissions, so cannot hand them out through a role.
        var grantMore = await _api.PostAsync("/api/v1/ums/users", new
        {
            email = $"{Unique("esc2")}@juvo.test", fullName = "Escalate", assignments = new[] { new { branchId = (Guid?)ho, roleId = accountant } },
        }, session.Access);
        Assert.Equal(HttpStatusCode.Forbidden, grantMore.StatusCode);

        var bigRole = await _api.PostAsync("/api/v1/ums/roles", new { name = Unique("Big"), permissions = new[] { "payments.refund.approve" } }, session.Access);
        Assert.Equal(HttpStatusCode.Forbidden, bigRole.StatusCode);

        var smallRole = await _api.PostAsync("/api/v1/ums/roles", new { name = Unique("Small"), permissions = new[] { "ums.user.view" } }, session.Access);
        Assert.Equal(HttpStatusCode.Created, smallRole.StatusCode);
    }

    // ---- audit log --------------------------------------------------------------------------------------

    [Fact]
    public async Task Admin_changes_are_written_to_the_audit_log_without_secrets()
    {
        var admin = await _api.LoginAdminAsync();
        var email = $"{Unique("aud")}@juvo.test";
        var role = await _api.RoleIdAsync("Support");
        var user = await _api.CreateUserAsync(admin.Access, email, "Audited", false, (await _api.BranchIdAsync("HO"), role));

        var logs = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/ums/audit-logs?entityId={user.Id}&pageSize=50", admin.Access), HttpStatusCode.OK);

        var items = logs.GetProperty("items").EnumerateArray().ToList();
        Assert.NotEmpty(items);
        Assert.Contains(items, i => i.GetProperty("entity").GetString() == "StaffUser" && i.GetProperty("action").GetString() == "Created");
        var text = logs.ToString();
        Assert.DoesNotContain("PasswordHash", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SecurityStamp", text, StringComparison.Ordinal);
        Assert.DoesNotContain(user.Password, text, StringComparison.Ordinal);
        Assert.Contains(items, i => i.GetProperty("userName").ValueKind == JsonValueKind.String || i.GetProperty("userName").ValueKind == JsonValueKind.Null);
    }
}
