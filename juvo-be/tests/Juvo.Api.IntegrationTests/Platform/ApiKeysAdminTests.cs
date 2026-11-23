using System.Net;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Platform;

/// <summary>Staff management of public API keys (FR-AF05): the secret is shown once, nothing leaks into lists or the audit trail, and only the right people may manage keys.</summary>
public class ApiKeysAdminTests(PublicApiFactory factory) : IClassFixture<PublicApiFactory>
{
    private readonly IntegrationsHelper _i = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private Task<string> AdminAsync() => _i.Store.AdminAsync();

    [Fact]
    public async Task Creating_a_key_shows_the_secret_once_and_no_list_or_detail_ever_shows_it_again()
    {
        var admin = await AdminAsync();
        var (id, secret, prefix) = await _i.CreateKeyAsync(admin, ["orders:read", "stock:read"], name: "ERP sync");

        Assert.StartsWith("jv_live_", secret, StringComparison.Ordinal);
        Assert.StartsWith(prefix, secret, StringComparison.Ordinal);
        Assert.Equal(16, prefix.Length);

        var list = await _i.Api.GetAsync("/api/v1/integrations/api-keys?search=ERP%20sync", admin);
        var listText = await list.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        Assert.DoesNotContain(secret, listText, StringComparison.Ordinal);
        Assert.DoesNotContain("keyHash", listText, StringComparison.OrdinalIgnoreCase);
        var row = System.Text.Json.JsonDocument.Parse(listText).RootElement.GetProperty("items").EnumerateArray().Single(k => k.GetProperty("id").GetGuid() == id);
        Assert.Equal(prefix, row.GetProperty("keyPrefix").GetString());
        Assert.Equal(["orders:read", "stock:read"], row.GetProperty("scopes").EnumerateArray().Select(s => s.GetString()!));
        Assert.True(row.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(JsonValueKindNull, row.GetProperty("lastUsedAt").ValueKind);

        var detail = await _i.Api.GetAsync($"/api/v1/integrations/api-keys/{id}", admin);
        Assert.DoesNotContain(secret, await detail.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        // Only the SHA-256 hash is stored.
        var stored = await _i.WithDbAsync(db => db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == id, Ct));
        Assert.Equal(Juvo.Application.Integrations.ApiKeyRules.Hash(secret), stored.KeyHash);
        Assert.Equal(prefix, stored.KeyPrefix);
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Every_change_is_audited_and_the_audit_trail_never_holds_a_secret_or_a_hash()
    {
        var admin = await AdminAsync();
        var (id, secret, _) = await _i.CreateKeyAsync(admin, ["products:read"]);
        var rotated = await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/api-keys/{id}/rotate", null, admin), HttpStatusCode.OK);
        var newSecret = rotated.GetProperty("secret").GetString()!;
        await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/api-keys/{id}/disable", null, admin), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await _i.Api.DeleteAsync($"/api/v1/integrations/api-keys/{id}", admin), HttpStatusCode.NoContent);

        var rows = await _i.WithDbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Entity == "ApiKey" && a.EntityId == id.ToString()).OrderBy(a => a.OccurredAt).ToListAsync(Ct));
        Assert.Contains(rows, r => r.Action == Juvo.Domain.Platform.AuditAction.Created);
        Assert.Contains(rows, r => r.Action == Juvo.Domain.Platform.AuditAction.Updated && (r.NewValue ?? string.Empty).Contains("rotated", StringComparison.Ordinal));
        Assert.Contains(rows, r => r.Action == Juvo.Domain.Platform.AuditAction.Deleted);
        Assert.All(rows, r => Assert.NotNull(r.UserId));
        var everything = string.Concat(rows.Select(r => r.OldValue + r.NewValue));
        Assert.DoesNotContain(secret, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(newSecret, everything, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Juvo.Application.Integrations.ApiKeyRules.Hash(secret)), everything, StringComparison.Ordinal);
        Assert.DoesNotContain("KeyHash", everything, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rotating_replaces_the_secret_at_once_disabling_stops_it_and_deleting_removes_it()
    {
        var admin = await AdminAsync();
        var (id, oldSecret, _) = await _i.CreateKeyAsync(admin, ["products:read"]);
        Assert.Equal(HttpStatusCode.OK, (await _i.GetWithKeyAsync(oldSecret, "/api/public/v1/products")).StatusCode);

        var rotated = await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/api-keys/{id}/rotate", null, admin), HttpStatusCode.OK);
        var newSecret = rotated.GetProperty("secret").GetString()!;
        Assert.NotEqual(oldSecret, newSecret);
        Assert.Equal("PUBLICAPI.INVALID_KEY", await Code(await _i.GetWithKeyAsync(oldSecret, "/api/public/v1/products")));
        Assert.Equal(HttpStatusCode.OK, (await _i.GetWithKeyAsync(newSecret, "/api/public/v1/products")).StatusCode);

        await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/api-keys/{id}/disable", null, admin), HttpStatusCode.OK);
        Assert.Equal("PUBLICAPI.KEY_DISABLED", await Code(await _i.GetWithKeyAsync(newSecret, "/api/public/v1/products")));
        await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/api-keys/{id}/enable", null, admin), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await _i.GetWithKeyAsync(newSecret, "/api/public/v1/products")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _i.Api.DeleteAsync($"/api/v1/integrations/api-keys/{id}", admin)).StatusCode);
        Assert.Equal("PUBLICAPI.INVALID_KEY", await Code(await _i.GetWithKeyAsync(newSecret, "/api/public/v1/products")));
        Assert.Equal(HttpStatusCode.NotFound, (await _i.Api.GetAsync($"/api/v1/integrations/api-keys/{id}", admin)).StatusCode);
    }

    private static async Task<string> Code(HttpResponseMessage response) => await TestApi.ErrorCodeAsync(response);

    [Fact]
    public async Task A_key_can_be_edited_without_changing_its_secret()
    {
        var admin = await AdminAsync();
        var (id, secret, _) = await _i.CreateKeyAsync(admin, ["products:read"]);

        var updated = await TestApi.ExpectAsync(await _i.Api.PutAsync($"/api/v1/integrations/api-keys/{id}",
            new { name = "Renamed", scopes = new[] { "products:read", "stock:read" }, rateLimitPerMinute = 5 }, admin), HttpStatusCode.OK);

        Assert.Equal("Renamed", updated.GetProperty("name").GetString());
        Assert.Equal(5, updated.GetProperty("rateLimitPerMinute").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await _i.GetWithKeyAsync(secret, "/api/public/v1/stock")).StatusCode);
    }

    [Theory]
    [InlineData("noscopes")]
    [InlineData("unknownscope")]
    [InlineData("writescope")]
    [InlineData("pastexpiry")]
    [InlineData("shortname")]
    [InlineData("badlimit")]
    [InlineData("badbranch")]
    public async Task Invalid_key_requests_are_refused_with_a_field_error(string problem)
    {
        var admin = await AdminAsync();
        var body = problem switch
        {
            "noscopes" => new { name = "Valid name", scopes = Array.Empty<string>(), branchId = (Guid?)null, expiresAt = (DateTimeOffset?)null, rateLimitPerMinute = (int?)null },
            "unknownscope" => new { name = "Valid name", scopes = new[] { "orders:everything" }, branchId = (Guid?)null, expiresAt = (DateTimeOffset?)null, rateLimitPerMinute = (int?)null },
            "writescope" => new { name = "Valid name", scopes = new[] { "stock:write" }, branchId = (Guid?)null, expiresAt = (DateTimeOffset?)null, rateLimitPerMinute = (int?)null },
            "pastexpiry" => new { name = "Valid name", scopes = new[] { "orders:read" }, branchId = (Guid?)null, expiresAt = (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-1), rateLimitPerMinute = (int?)null },
            "shortname" => new { name = "x", scopes = new[] { "orders:read" }, branchId = (Guid?)null, expiresAt = (DateTimeOffset?)null, rateLimitPerMinute = (int?)null },
            "badlimit" => new { name = "Valid name", scopes = new[] { "orders:read" }, branchId = (Guid?)null, expiresAt = (DateTimeOffset?)null, rateLimitPerMinute = (int?)0 },
            _ => new { name = "Valid name", scopes = new[] { "orders:read" }, branchId = (Guid?)Guid.NewGuid(), expiresAt = (DateTimeOffset?)null, rateLimitPerMinute = (int?)null },
        };

        var response = await _i.Api.PostAsync("/api/v1/integrations/api-keys", body, admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await TestApi.JsonAsync(response);
        Assert.True(json.TryGetProperty("errors", out _), "a field error is expected");
    }

    [Fact]
    public async Task The_scope_catalogue_lists_the_fixed_scopes()
    {
        var json = await TestApi.ExpectAsync(await _i.Api.GetAsync("/api/v1/integrations/api-keys/scopes", await AdminAsync()), HttpStatusCode.OK);

        Assert.Equal(["orders:read", "products:read", "stock:read", "customers:read", "pii:read"], json.EnumerateArray().Select(s => s.GetProperty("scope").GetString()!));
    }

    [Fact]
    public async Task Only_staff_with_the_integration_permissions_can_see_or_manage_keys()
    {
        var admin = await AdminAsync();
        var o = new Orders.OrderHelper(_i.Store);
        var nobody = await o.StaffTokenAsync(admin, null, "orders.order.view");
        var viewer = await o.StaffTokenAsync(admin, null, "integrations.apikey.view");
        var managerRole = await _i.Api.RoleIdAsync("Manager");
        var ho = await _i.Api.BranchIdAsync("HO");
        var manager = await _i.Api.CreateUserAsync(admin, $"{StoreHelper.Unique("mg")}@example.com", "Manager Person", false, (ho, managerRole));
        var managerToken = (await _i.Api.LoginAsync(manager.Email, manager.Password)).Access;
        var (id, _, _) = await _i.CreateKeyAsync(admin, ["orders:read"]);

        Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.GetAsync("/api/v1/integrations/api-keys", nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.GetAsync("/api/v1/integrations/api-keys", managerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.PostAsync("/api/v1/integrations/api-keys", new { name = "Nope key", scopes = new[] { "orders:read" } }, managerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _i.Api.GetAsync("/api/v1/integrations/api-keys")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await _i.Api.GetAsync("/api/v1/integrations/api-keys", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.PostAsync("/api/v1/integrations/api-keys", new { name = "Nope key", scopes = new[] { "orders:read" } }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.PostAsync($"/api/v1/integrations/api-keys/{id}/rotate", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _i.Api.DeleteAsync($"/api/v1/integrations/api-keys/{id}", viewer)).StatusCode);
    }

    [Fact]
    public async Task The_super_admin_role_holds_the_integration_permissions_and_no_default_role_does()
    {
        var holders = await _i.WithDbAsync(async db =>
        {
            var codes = new[] { "integrations.apikey.view", "integrations.apikey.manage", "integrations.webhook.view", "integrations.webhook.manage" };
            var ids = await db.Permissions.Where(p => codes.Contains(p.Code)).Select(p => p.Id).ToListAsync(Ct);
            Assert.Equal(4, ids.Count);
            return await db.RolePermissions.Where(rp => ids.Contains(rp.PermissionId)).Select(rp => rp.RoleId).Distinct().CountAsync(Ct);
        });

        // Super Admin holds permissions by definition (no stored rows); no other default role was given any of them.
        Assert.Equal(0, holders);
    }
}
