using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Juvo.Domain.Notifications;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Support;

/// <summary>A signed-in session: the two tokens an API client holds.</summary>
public sealed record Tokens(string Access, string Refresh);

/// <summary>Small helper around the in-memory API: sign in, call endpoints with a token and branch, read JSON.</summary>
public sealed class TestApi(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public HttpClient Client { get; } = factory.CreateClient();

    public ApiFactory Factory => factory;

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? token = null,
        Guid? branch = null, string? branchHeader = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        if (branch is { } id)
        {
            request.Headers.Add("X-Branch-Id", id.ToString());
        }
        else if (branchHeader is not null)
        {
            request.Headers.Add("X-Branch-Id", branchHeader);
        }

        return await Client.SendAsync(request, Ct);
    }

    public Task<HttpResponseMessage> GetAsync(string url, string? token = null, Guid? branch = null, string? branchHeader = null) =>
        SendAsync(HttpMethod.Get, url, null, token, branch, branchHeader);

    public Task<HttpResponseMessage> PostAsync(string url, object? body = null, string? token = null, Guid? branch = null) =>
        SendAsync(HttpMethod.Post, url, body ?? new { }, token, branch);

    public Task<HttpResponseMessage> PutAsync(string url, object body, string? token = null, Guid? branch = null) =>
        SendAsync(HttpMethod.Put, url, body, token, branch);

    public Task<HttpResponseMessage> DeleteAsync(string url, string? token = null, Guid? branch = null) =>
        SendAsync(HttpMethod.Delete, url, null, token, branch);

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<JsonElement>(Ct);

    /// <summary>Asserts the status and returns the JSON body, showing the body when the status is wrong.</summary>
    public static async Task<JsonElement> ExpectAsync(HttpResponseMessage response, HttpStatusCode status)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True(response.StatusCode == status, $"Expected {(int)status} {status} but got {(int)response.StatusCode} {response.StatusCode}: {text}");
        return string.IsNullOrWhiteSpace(text) ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<string> ErrorCodeAsync(HttpResponseMessage response)
    {
        var json = await JsonAsync(response);
        return json.GetProperty("code").GetString()!;
    }

    public async Task<Tokens> LoginAsync(string identifier, string password, string? totp = null)
    {
        var response = await PostAsync("/api/v1/auth/login", new { identifier, password, totpCode = totp });
        var json = await ExpectAsync(response, HttpStatusCode.OK);
        return new Tokens(json.GetProperty("accessToken").GetString()!, json.GetProperty("refreshToken").GetString()!);
    }

    public Task<Tokens> LoginAdminAsync() => LoginAsync(ApiFactory.SeedEmail, ApiFactory.SeedPassword);

    // ---- data helpers (read the database directly, so tests do not depend on list endpoints) ----

    public async Task<Guid> BranchIdAsync(string code)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        return await db.Branches.Where(b => b.Code == code).Select(b => b.Id).SingleAsync(Ct);
    }

    public async Task<Guid> RoleIdAsync(string name)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        return await db.Roles.Where(r => r.Name == name).Select(r => r.Id).SingleAsync(Ct);
    }

    public async Task<Guid> UserIdAsync(string email)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        return await db.Users.Where(u => u.Email == email).Select(u => u.Id).SingleAsync(Ct);
    }

    /// <summary>The newest sandbox message sent to a recipient. Password-reset links and (in tests) OTP codes are readable here.</summary>
    public async Task<NotificationLog> LastMessageToAsync(string recipient)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        return await db.NotificationLogs.Where(n => n.Recipient == recipient).OrderByDescending(n => n.SentAt).FirstAsync(Ct);
    }

    /// <summary>Creates a role with the given permission codes through the API and returns its id.</summary>
    public async Task<Guid> CreateRoleAsync(string adminToken, string name, params string[] permissions)
    {
        var response = await PostAsync("/api/v1/ums/roles", new { name, description = name, permissions }, adminToken);
        var json = await ExpectAsync(response, HttpStatusCode.Created);
        return json.GetProperty("id").GetGuid();
    }

    public async Task<Guid> CreateBranchAsync(string adminToken, string code, string name)
    {
        var response = await PostAsync("/api/v1/ums/branches", new { code, nameEn = name, type = "Shop" }, adminToken);
        var json = await ExpectAsync(response, HttpStatusCode.Created);
        return json.GetProperty("id").GetGuid();
    }

    /// <summary>Creates a staff user with a known password already changed, so the account is usable at once.</summary>
    public async Task<(Guid Id, string Email, string Password)> CreateUserAsync(
        string adminToken, string email, string fullName, bool global = false, params (Guid? Branch, Guid Role)[] assignments)
    {
        var response = await PostAsync("/api/v1/ums/users", new
        {
            email,
            fullName,
            isGlobalScope = global,
            assignments = assignments.Select(a => new { branchId = a.Branch, roleId = a.Role }).ToArray(),
        }, adminToken);
        var json = await ExpectAsync(response, HttpStatusCode.Created);
        var id = json.GetProperty("user").GetProperty("id").GetGuid();
        var temp = json.GetProperty("temporaryPassword").GetString()!;

        // First sign-in is limited: change the temporary password to a permanent one.
        var limited = await LoginAsync(email, temp);
        const string permanent = "Perm4nent!Passw0rd";
        var change = await PostAsync("/api/v1/auth/change-password", new { currentPassword = temp, newPassword = permanent }, limited.Access);
        await ExpectAsync(change, HttpStatusCode.OK);
        return (id, email, permanent);
    }
}

/// <summary>RFC 6238 time-based one-time passwords (30 s, 6 digits, SHA-1) as used by authenticator apps.</summary>
public static class Totp
{
    public static string Generate(string base32Key, DateTimeOffset? at = null)
    {
        var key = Base32Decode(base32Key);
        var counter = (at ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        var message = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian)
        {
            Array.Reverse(message);
        }

        // RFC 6238 authenticator apps use HMAC-SHA1; this is the protocol, not a security choice of ours.
#pragma warning disable CA5350
        var hash = HMACSHA1.HashData(key, message);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    private static byte[] Base32Decode(string input)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var clean = input.Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal)
            .TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>();
        var buffer = 0;
        var bits = 0;
        foreach (var c in clean)
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                bytes.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. bytes];
    }
}
