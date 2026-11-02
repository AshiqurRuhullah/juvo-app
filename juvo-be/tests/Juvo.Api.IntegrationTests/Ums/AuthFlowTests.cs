using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Identity;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Juvo.Api.IntegrationTests.Ums;

public class AuthFlowTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestApi _api = new(factory);

    private async Task<T> InDbAsync<T>(Func<JuvoDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<JuvoDbContext>());
    }

    // ---- sign-in ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Login_returns_tokens_and_me_shows_profile_branches_and_all_permissions_for_super_admin()
    {
        var tokens = await _api.LoginAdminAsync();

        var me = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/auth/me", tokens.Access), HttpStatusCode.OK);

        Assert.Equal("staff", me.GetProperty("subjectType").GetString());
        var staff = me.GetProperty("staff");
        Assert.Equal(ApiFactory.SeedEmail, staff.GetProperty("email").GetString());
        Assert.True(staff.GetProperty("isGlobalScope").GetBoolean());
        Assert.True(staff.GetProperty("isSuperAdmin").GetBoolean());
        Assert.True(me.GetProperty("hasAllPermissions").GetBoolean());
        var branchCodes = me.GetProperty("branches").EnumerateArray().Select(b => b.GetProperty("code").GetString()).ToList();
        Assert.Contains("HO", branchCodes);
        Assert.Contains("ONL", branchCodes);
        Assert.False(string.IsNullOrEmpty(me.GetProperty("permissionCatalogVersion").GetString()));
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_give_the_same_error_and_are_recorded()
    {
        var wrong = await _api.PostAsync("/api/v1/auth/login", new { identifier = ApiFactory.SeedEmail, password = "Wrong-Pass-1!" });
        var unknown = await _api.PostAsync("/api/v1/auth/login", new { identifier = "nobody@juvo.test", password = "Wrong-Pass-1!" });

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, unknown.StatusCode);
        Assert.Equal("AUTH.INVALID_CREDENTIALS", await TestApi.ErrorCodeAsync(wrong));
        Assert.Equal("AUTH.INVALID_CREDENTIALS", await TestApi.ErrorCodeAsync(unknown));

        var reasons = await InDbAsync(db => db.SignInHistory.Where(s => !s.Succeeded).Select(s => s.FailureReason).ToListAsync(Ct));
        Assert.Contains("bad-password", reasons);
        Assert.Contains("unknown-user", reasons);
    }

    [Fact]
    public async Task Login_by_user_name_works_and_success_is_recorded()
    {
        var tokens = await _api.LoginAsync("admin", ApiFactory.SeedPassword);

        Assert.False(string.IsNullOrEmpty(tokens.Access));
        Assert.True(await InDbAsync(db => db.SignInHistory.AnyAsync(s => s.Identifier == "admin" && s.Succeeded, Ct)));
    }

    [Fact]
    public async Task Repeated_wrong_passwords_lock_the_account_even_for_the_right_password()
    {
        var email = $"lock-{Guid.NewGuid():N}@juvo.test";
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Support");
        var user = await _api.CreateUserAsync(admin.Access, email, "Lock Test", false, (await _api.BranchIdAsync("HO"), role));

        for (var i = 0; i < 4; i++)
        {
            var bad = await _api.PostAsync("/api/v1/auth/login", new { identifier = email, password = "Bad-Pass-1234!" });
            Assert.Equal("AUTH.INVALID_CREDENTIALS", await TestApi.ErrorCodeAsync(bad));
        }

        var fifth = await _api.PostAsync("/api/v1/auth/login", new { identifier = email, password = "Bad-Pass-1234!" });
        Assert.Equal((HttpStatusCode)423, fifth.StatusCode);
        Assert.Equal("AUTH.LOCKED", await TestApi.ErrorCodeAsync(fifth));

        var correct = await _api.PostAsync("/api/v1/auth/login", new { identifier = email, password = user.Password });
        Assert.Equal((HttpStatusCode)423, correct.StatusCode);
    }

    // ---- tokens -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Protected_endpoints_reject_missing_garbage_and_expired_tokens_with_codes()
    {
        var none = await _api.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal("AUTH.UNAUTHORIZED", await TestApi.ErrorCodeAsync(none));

        var garbage = await _api.GetAsync("/api/v1/auth/me", "not-a-token");
        Assert.Equal(HttpStatusCode.Unauthorized, garbage.StatusCode);

        // A correctly signed token that is already expired.
        var key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes("integration-tests-signing-key-0123456789-abcdef")) { KeyId = "juvo-hs256" };
        var expired = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "juvo-api",
            Audience = "juvo-clients",
            NotBefore = DateTime.UtcNow.AddHours(-2),
            Expires = DateTime.UtcNow.AddHours(-1),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["stype"] = "staff", ["pv"] = 1 },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });
        var response = await _api.GetAsync("/api/v1/auth/me", expired);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("AUTH.TOKEN_EXPIRED", await TestApi.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_rejected()
    {
        var key = new SymmetricSecurityKey(System.Text.Encoding.UTF8.GetBytes("a-completely-different-signing-key-1234567890"));
        var forged = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "juvo-api",
            Audience = "juvo-clients",
            Expires = DateTime.UtcNow.AddHours(1),
            Claims = new Dictionary<string, object> { ["sub"] = Guid.NewGuid().ToString(), ["stype"] = "staff", ["pv"] = 1 },
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256),
        });

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync("/api/v1/auth/me", forged)).StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_the_old_one_cannot_be_used_again()
    {
        var first = await _api.LoginAdminAsync();

        var refreshed = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = first.Refresh }), HttpStatusCode.OK);
        var second = new Tokens(refreshed.GetProperty("accessToken").GetString()!, refreshed.GetProperty("refreshToken").GetString()!);
        Assert.NotEqual(first.Refresh, second.Refresh);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/auth/me", second.Access)).StatusCode);

        // Presenting the old token again is reuse: refused, and the whole family is cut, including the new token.
        var reuse = await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = first.Refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, reuse.StatusCode);
        Assert.Equal("AUTH.REFRESH_REUSED", await TestApi.ErrorCodeAsync(reuse));

        var afterCut = await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = second.Refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, afterCut.StatusCode);
        Assert.Equal("AUTH.REFRESH_REUSED", await TestApi.ErrorCodeAsync(afterCut));
    }

    [Fact]
    public async Task Two_simultaneous_refreshes_with_one_token_let_only_one_win_and_cut_the_session()
    {
        var tokens = await _api.LoginAdminAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = tokens.Refresh })));

        var codes = results.Select(r => r.StatusCode).ToList();
        Assert.True(codes.Count(c => c == HttpStatusCode.OK) <= 1, "at most one refresh may succeed");
        Assert.All(codes.Where(c => c != HttpStatusCode.OK), c => Assert.Equal(HttpStatusCode.Unauthorized, c));
    }

    [Fact]
    public async Task Refresh_tokens_are_stored_hashed_never_in_clear()
    {
        var tokens = await _api.LoginAdminAsync();

        var stored = await InDbAsync(db => db.RefreshTokens.Select(t => t.TokenHash).ToListAsync(Ct));

        Assert.DoesNotContain(tokens.Refresh, stored);
        Assert.Contains(Convert.ToBase64String(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(tokens.Refresh))), stored);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token()
    {
        var tokens = await _api.LoginAdminAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _api.PostAsync("/api/v1/auth/logout", new { refreshToken = tokens.Refresh })).StatusCode);

        var after = await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = tokens.Refresh });
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
    }

    [Fact]
    public async Task Logout_all_ends_every_session_and_the_access_tokens_at_once()
    {
        var a = await _api.LoginAdminAsync();
        var b = await _api.LoginAdminAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _api.PostAsync("/api/v1/auth/logout-all", null, a.Access)).StatusCode);

        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = b.Refresh })).StatusCode);
        var stale = await _api.GetAsync("/api/v1/auth/me", b.Access);
        Assert.Equal(HttpStatusCode.Unauthorized, stale.StatusCode);
        Assert.Equal("AUTH.TOKEN_EXPIRED", await TestApi.ErrorCodeAsync(stale));
    }

    [Fact]
    public async Task Sessions_list_my_devices_and_one_can_be_signed_out()
    {
        var phone = await _api.LoginAdminAsync();
        var laptop = await _api.LoginAdminAsync();

        var list = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/auth/sessions", phone.Access), HttpStatusCode.OK);
        var sessions = list.EnumerateArray().ToList();
        Assert.True(sessions.Count >= 2);
        Assert.Single(sessions, s => s.GetProperty("isCurrent").GetBoolean());

        var other = sessions.First(s => !s.GetProperty("isCurrent").GetBoolean()).GetProperty("sessionId").GetGuid();
        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/auth/sessions/{other}", phone.Access)).StatusCode);
        Assert.NotEmpty(laptop.Refresh);
    }

    // ---- passwords --------------------------------------------------------------------------------------

    [Fact]
    public async Task Change_password_ends_other_sessions_and_old_password_stops_working()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Support");
        var user = await _api.CreateUserAsync(admin.Access, $"pw-{Guid.NewGuid():N}@juvo.test", "Pw User", false, (await _api.BranchIdAsync("HO"), role));
        var other = await _api.LoginAsync(user.Email, user.Password);
        var current = await _api.LoginAsync(user.Email, user.Password);

        var changed = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/change-password",
            new { currentPassword = user.Password, newPassword = "Brand-New-Pass#77" }, current.Access), HttpStatusCode.OK);

        Assert.False(string.IsNullOrEmpty(changed.GetProperty("accessToken").GetString()));
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = other.Refresh })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/v1/auth/login", new { identifier = user.Email, password = user.Password })).StatusCode);
        Assert.NotEmpty((await _api.LoginAsync(user.Email, "Brand-New-Pass#77")).Access);
    }

    [Theory]
    [InlineData("short1A!")]
    [InlineData("alllowercase123!")]
    [InlineData("ALLUPPERCASE123!")]
    [InlineData("NoDigitsHere!!")]
    [InlineData("NoSymbols12345A")]
    public async Task Weak_passwords_are_rejected_with_the_rules(string weak)
    {
        var admin = await _api.LoginAdminAsync();

        var response = await _api.PostAsync("/api/v1/auth/change-password", new { currentPassword = ApiFactory.SeedPassword, newPassword = weak }, admin.Access);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("AUTH.WEAK_PASSWORD", await TestApi.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task New_admin_created_user_must_change_the_temporary_password_before_anything_else()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Manager");
        var email = $"first-{Guid.NewGuid():N}@juvo.test";
        var created = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/ums/users", new
        {
            email, fullName = "First Login", assignments = new[] { new { branchId = (Guid?)await _api.BranchIdAsync("HO"), roleId = role } },
        }, admin.Access), HttpStatusCode.Created);
        var temp = created.GetProperty("temporaryPassword").GetString()!;

        var login = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/login", new { identifier = email, password = temp }), HttpStatusCode.OK);
        Assert.True(login.GetProperty("requiresPasswordChange").GetBoolean());
        var limited = login.GetProperty("accessToken").GetString()!;

        // The limited token can read who it is and change the password, but not use ordinary endpoints.
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/auth/me", limited)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/ums/users", limited)).StatusCode);

        var changed = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/change-password",
            new { currentPassword = temp, newPassword = "Chosen-By-User#5" }, limited), HttpStatusCode.OK);
        Assert.False(changed.GetProperty("requiresPasswordChange").GetBoolean());
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/ums/users", changed.GetProperty("accessToken").GetString())).StatusCode);
    }

    [Fact]
    public async Task Forgot_password_is_silent_and_reset_with_the_emailed_token_works_once()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Support");
        var user = await _api.CreateUserAsync(admin.Access, $"forgot-{Guid.NewGuid():N}@juvo.test", "Forgot Pw", false, (await _api.BranchIdAsync("HO"), role));
        var session = await _api.LoginAsync(user.Email, user.Password);

        // Unknown email: same answer, nothing sent.
        Assert.Equal(HttpStatusCode.Accepted, (await _api.PostAsync("/api/v1/auth/forgot-password", new { email = "ghost@juvo.test" })).StatusCode);
        Assert.False(await InDbAsync(db => db.NotificationLogs.AnyAsync(n => n.Recipient == "ghost@juvo.test", Ct)));

        Assert.Equal(HttpStatusCode.Accepted, (await _api.PostAsync("/api/v1/auth/forgot-password", new { email = user.Email })).StatusCode);
        var mail = await _api.LastMessageToAsync(user.Email);
        Assert.Equal("auth.password-reset", mail.Template);
        var token = System.Net.WebUtility.UrlDecode(mail.Body.Split("token=")[1].Split(['\n', ' ', '\r'])[0]);

        var reset = await _api.PostAsync("/api/v1/auth/reset-password", new { email = user.Email, token, newPassword = "After-Reset#2026" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);

        Assert.NotEmpty((await _api.LoginAsync(user.Email, "After-Reset#2026")).Access);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/v1/auth/login", new { identifier = user.Email, password = user.Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = session.Refresh })).StatusCode);

        var again = await _api.PostAsync("/api/v1/auth/reset-password", new { email = user.Email, token, newPassword = "Another-One#2026" });
        Assert.Equal("AUTH.RESET_TOKEN_INVALID", await TestApi.ErrorCodeAsync(again));
    }

    // ---- two-step verification --------------------------------------------------------------------------

    [Fact]
    public async Task Two_step_verification_setup_enable_login_and_recovery_code()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Manager");
        var user = await _api.CreateUserAsync(admin.Access, $"2fa-{Guid.NewGuid():N}@juvo.test", "Two Factor", false, (await _api.BranchIdAsync("HO"), role));
        var session = await _api.LoginAsync(user.Email, user.Password);

        var setup = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/2fa/setup", null, session.Access), HttpStatusCode.OK);
        var key = setup.GetProperty("sharedKey").GetString()!;
        Assert.StartsWith("otpauth://totp/", setup.GetProperty("authenticatorUri").GetString(), StringComparison.Ordinal);

        var wrong = await _api.PostAsync("/api/v1/auth/2fa/enable", new { code = "000000" }, session.Access);
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        var enabled = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/2fa/enable", new { code = Totp.Generate(key) }, session.Access), HttpStatusCode.OK);
        var recovery = enabled.GetProperty("recoveryCodes").EnumerateArray().Select(c => c.GetString()!).ToList();
        Assert.Equal(10, recovery.Count);

        // Password alone is no longer enough.
        var noCode = await _api.PostAsync("/api/v1/auth/login", new { identifier = user.Email, password = user.Password });
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
        Assert.Equal("AUTH.TWO_FACTOR_REQUIRED", await TestApi.ErrorCodeAsync(noCode));

        var badCode = await _api.PostAsync("/api/v1/auth/login", new { identifier = user.Email, password = user.Password, totpCode = "123456" });
        Assert.Equal("AUTH.INVALID_CREDENTIALS", await TestApi.ErrorCodeAsync(badCode));

        Assert.NotEmpty((await _api.LoginAsync(user.Email, user.Password, Totp.Generate(key))).Access);

        // A recovery code works once.
        var viaRecovery = await _api.PostAsync("/api/v1/auth/login", new { identifier = user.Email, password = user.Password, recoveryCode = recovery[0] });
        Assert.Equal(HttpStatusCode.OK, viaRecovery.StatusCode);
        var reused = await _api.PostAsync("/api/v1/auth/login", new { identifier = user.Email, password = user.Password, recoveryCode = recovery[0] });
        Assert.Equal(HttpStatusCode.Unauthorized, reused.StatusCode);
    }

    [Fact]
    public async Task Admin_can_reset_a_users_two_step_verification_when_the_phone_is_lost()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.RoleIdAsync("Manager");
        var user = await _api.CreateUserAsync(admin.Access, $"lostphone-{Guid.NewGuid():N}@juvo.test", "Lost Phone", false, (await _api.BranchIdAsync("HO"), role));
        var session = await _api.LoginAsync(user.Email, user.Password);
        var setup = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/2fa/setup", null, session.Access), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/2fa/enable", new { code = Totp.Generate(setup.GetProperty("sharedKey").GetString()!) }, session.Access), HttpStatusCode.OK);

        Assert.Equal(HttpStatusCode.NoContent, (await _api.PostAsync($"/api/v1/ums/users/{user.Id}/reset-two-factor", null, admin.Access)).StatusCode);

        Assert.NotEmpty((await _api.LoginAsync(user.Email, user.Password)).Access);
    }
}

/// <summary>Two-step verification is mandatory for Super Admin (SRS FR-O01).</summary>
public class SuperAdminTwoFactorTests : IClassFixture<SuperAdminTwoFactorTests.StrictFactory>
{
    private readonly TestApi _api;

    public SuperAdminTwoFactorTests(StrictFactory factory) => _api = new TestApi(factory);

    public sealed class StrictFactory : ApiFactory
    {
        protected override void ConfigureSettings() => Setting("Auth:RequireTwoFactorForSuperAdmin", "true");
    }

    [Fact]
    public async Task Super_admin_without_two_step_gets_a_limited_token_until_it_is_set_up_and_cannot_turn_it_off()
    {
        var login = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/login",
            new { identifier = ApiFactory.SeedEmail, password = ApiFactory.SeedPassword }), HttpStatusCode.OK);
        Assert.True(login.GetProperty("requiresTwoFactorSetup").GetBoolean());
        var limited = login.GetProperty("accessToken").GetString()!;

        // Nothing ordinary works yet.
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/ums/roles", limited)).StatusCode);

        var setup = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/2fa/setup", null, limited), HttpStatusCode.OK);
        var key = setup.GetProperty("sharedKey").GetString()!;
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/2fa/enable", new { code = Totp.Generate(key) }, limited), HttpStatusCode.OK);

        // A new sign-in now asks for the code and gives a full token.
        var full = await _api.LoginAsync(ApiFactory.SeedEmail, ApiFactory.SeedPassword, Totp.Generate(key));
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/ums/roles", full.Access)).StatusCode);

        var disable = await _api.PostAsync("/api/v1/auth/2fa/disable", new { password = ApiFactory.SeedPassword, code = Totp.Generate(key) }, full.Access);
        Assert.Equal(HttpStatusCode.Forbidden, disable.StatusCode);
    }
}

/// <summary>The seeded Super Admin must change the password at first sign-in.</summary>
public class SeedAdminForcedChangeTests : IClassFixture<SeedAdminForcedChangeTests.ForcedFactory>
{
    private readonly TestApi _api;

    public SeedAdminForcedChangeTests(ForcedFactory factory) => _api = new TestApi(factory);

    public sealed class ForcedFactory : ApiFactory
    {
        protected override bool KeepSeedAdminPasswordChange => true;
    }

    [Fact]
    public async Task Seeded_admin_gets_a_limited_token_and_must_change_the_password()
    {
        var login = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/login",
            new { identifier = ApiFactory.SeedEmail, password = ApiFactory.SeedPassword }), HttpStatusCode.OK);

        Assert.True(login.GetProperty("requiresPasswordChange").GetBoolean());
        var limited = login.GetProperty("accessToken").GetString()!;
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/ums/users", limited)).StatusCode);

        var changed = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/change-password",
            new { currentPassword = ApiFactory.SeedPassword, newPassword = "Admin-Changed#2026" }, limited), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/ums/users", changed.GetProperty("accessToken").GetString())).StatusCode);
    }
}
