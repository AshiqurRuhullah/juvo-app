using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Domain.Integrations;
using Juvo.Domain.Platform;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Integrations;

public sealed record ApiKeyDto(
    Guid Id, string Name, string KeyPrefix, IReadOnlyList<string> Scopes, Guid? BranchId, string? BranchCode, bool IsEnabled, DateTimeOffset? ExpiresAt, bool IsExpired,
    int? RateLimitPerMinute, DateTimeOffset? LastUsedAt, string? LastIpHash, DateTimeOffset? RotatedAt, DateTimeOffset CreatedAt, Guid? CreatedBy, string? CreatedByName);

/// <summary>The secret is in this response only. It cannot be shown again.</summary>
public sealed record ApiKeyCreatedDto(ApiKeyDto Key, string Secret);

public sealed record ApiKeyRequest(string? Name, IReadOnlyList<string>? Scopes, Guid? BranchId, DateTimeOffset? ExpiresAt, int? RateLimitPerMinute);

/// <summary>
/// Staff management of public API keys (FR-AF05): create (secret shown once), edit, rotate, disable, enable, delete. Every change is audited by the audit trail
/// (the entity's secret hash is excluded from it) and rotation writes an explicit entry; the secret itself is never written anywhere but the response.
/// </summary>
public sealed class ApiKeyService(IApplicationDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    public const string AuditEntity = "ApiKey";
    public const int MaxRateLimitPerMinute = 10_000;

    private static Error NotFound() => Error.NotFound(ErrorCodes.PublicApi.KeyNotFound, "API key not found.");

    private static Error Invalid(string code, string field, string message) =>
        new(code, message, ErrorType.Validation, new Dictionary<string, string[]> { [field] = [message] });

    public async Task<Result<PagedResult<ApiKeyDto>>> ListAsync(PageRequest request, bool? enabled, CancellationToken ct)
    {
        var page = request.Normalized;
        var keys = db.ApiKeys.AsNoTracking().AsQueryable();
        if (enabled is { } e)
        {
            keys = keys.Where(k => k.IsEnabled == e);
        }

        if (!string.IsNullOrWhiteSpace(page.Search))
        {
            var term = page.Search.Trim();
            keys = keys.Where(k => k.Name.Contains(term) || k.KeyPrefix.Contains(term));
        }

        var total = await keys.CountAsync(ct);
        var rows = await keys.OrderByDescending(k => k.CreatedAt).ThenBy(k => k.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(ct);
        return PagedResult<ApiKeyDto>.Create(await ToDtosAsync(rows, ct), total, page);
    }

    public async Task<Result<ApiKeyDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.Id == id, ct);
        return key is null ? NotFound() : (await ToDtosAsync([key], ct))[0];
    }

    public static IReadOnlyList<ScopeInfo> Scopes() => ApiKeyScopes.All;

    public async Task<Result<ApiKeyCreatedDto>> CreateAsync(ApiKeyRequest request, CancellationToken ct)
    {
        if (await ValidateAsync(request, ct) is { } error)
        {
            return error;
        }

        var generated = ApiKeyRules.Generate();
        var key = new ApiKey(request.Name!, generated.Prefix, generated.Hash, request.Scopes!, request.BranchId, request.ExpiresAt, request.RateLimitPerMinute);
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync(ct);
        return new ApiKeyCreatedDto((await ToDtosAsync([key], ct))[0], generated.Key);
    }

    public async Task<Result<ApiKeyDto>> UpdateAsync(Guid id, ApiKeyRequest request, CancellationToken ct)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
        {
            return NotFound();
        }

        if (await ValidateAsync(request, ct) is { } error)
        {
            return error;
        }

        key.Update(request.Name!, request.Scopes!, request.BranchId, request.ExpiresAt, request.RateLimitPerMinute);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([key], ct))[0];
    }

    public async Task<Result<ApiKeyCreatedDto>> RotateAsync(Guid id, CancellationToken ct)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
        {
            return NotFound();
        }

        var generated = ApiKeyRules.Generate();
        var now = clock.GetUtcNow();
        key.Rotate(generated.Prefix, generated.Hash, now);
        Audit(key, AuditAction.Updated, "rotated", now);
        await db.SaveChangesAsync(ct);
        return new ApiKeyCreatedDto((await ToDtosAsync([key], ct))[0], generated.Key);
    }

    public async Task<Result<ApiKeyDto>> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
        {
            return NotFound();
        }

        key.SetEnabled(enabled);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([key], ct))[0];
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var key = await db.ApiKeys.FirstOrDefaultAsync(k => k.Id == id, ct);
        if (key is null)
        {
            return NotFound();
        }

        db.ApiKeys.Remove(key);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    private async Task<Error?> ValidateAsync(ApiKeyRequest r, CancellationToken ct)
    {
        var name = r.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 80)
        {
            return Invalid(ErrorCodes.General.Validation, "name", "Give the key a name of 2 to 80 characters.");
        }

        if (r.Scopes is not { Count: > 0 })
        {
            return Invalid(ErrorCodes.PublicApi.InvalidScope, "scopes", "Choose at least one scope.");
        }

        if (r.Scopes.FirstOrDefault(s => !ApiKeyScopes.IsKnown(s)) is { } unknown)
        {
            return Invalid(ErrorCodes.PublicApi.InvalidScope, "scopes", $"Unknown scope '{unknown}'.");
        }

        if (r.ExpiresAt is { } expires && expires <= clock.GetUtcNow())
        {
            return Invalid(ErrorCodes.General.Validation, "expiresAt", "The expiry must be in the future.");
        }

        if (r.RateLimitPerMinute is < 1 or > MaxRateLimitPerMinute)
        {
            return Invalid(ErrorCodes.General.Validation, "rateLimitPerMinute", $"Use 1 to {MaxRateLimitPerMinute} requests per minute, or leave it empty for the default.");
        }

        if (r.BranchId is { } branch && !await db.Branches.AnyAsync(b => b.Id == branch, ct))
        {
            return Invalid(ErrorCodes.General.Validation, "branchId", "Unknown branch.");
        }

        return null;
    }

    private void Audit(ApiKey key, AuditAction action, string @event, DateTimeOffset now) =>
        db.AuditLogs.Add(new AuditLog(currentUser.UserId, currentUser.BranchId, nameof(ApiKey), key.Id.ToString(), action, null,
            JsonSerializer.Serialize(new { @event, prefix = key.KeyPrefix }), currentUser.CorrelationId, currentUser.IpAddress, now));

    private async Task<IReadOnlyList<ApiKeyDto>> ToDtosAsync(IReadOnlyList<ApiKey> keys, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var branchIds = keys.Where(k => k.BranchId is not null).Select(k => k.BranchId!.Value).Distinct().ToList();
        var branches = await db.Branches.IgnoreQueryFilters().AsNoTracking().Where(b => branchIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var creatorIds = keys.Where(k => k.CreatedBy is not null).Select(k => k.CreatedBy!.Value).Distinct().ToList();
        var creators = await db.Users.AsNoTracking().Where(u => creatorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return [.. keys.Select(k => new ApiKeyDto(
            k.Id, k.Name, k.KeyPrefix, k.ScopeList, k.BranchId, k.BranchId is { } b && branches.TryGetValue(b, out var code) ? code : null, k.IsEnabled, k.ExpiresAt, k.IsExpired(now),
            k.RateLimitPerMinute, k.LastUsedAt, k.LastIpHash, k.RotatedAt, k.CreatedAt, k.CreatedBy, k.CreatedBy is { } c && creators.TryGetValue(c, out var name) ? name : null))];
    }
}

/// <summary>Why a presented key was not accepted, or the key that was.</summary>
public sealed record ApiKeyCheck(Guid? KeyId, Guid? BranchId, IReadOnlyList<string> Scopes, int? RateLimitPerMinute, string? FailureCode)
{
    public bool Ok => FailureCode is null;
}

/// <summary>Checks a presented key: find the row by its public prefix, compare the hash in constant time, then enabled and expiry.</summary>
public sealed class ApiKeyAuthenticator(IApplicationDbContext db, TimeProvider clock)
{
    private static readonly ApiKeyCheck Invalid = new(null, null, [], null, ErrorCodes.PublicApi.InvalidKey);

    public async Task<ApiKeyCheck> CheckAsync(string? presented, string? ip, CancellationToken ct)
    {
        var prefix = ApiKeyRules.PrefixOf(presented);
        if (prefix is null || presented!.Length > 200)
        {
            _ = ApiKeyRules.Matches("x", ApiKeyRules.DummyHash);
            return Invalid;
        }

        var key = await db.ApiKeys.AsNoTracking().FirstOrDefaultAsync(k => k.KeyPrefix == prefix, ct);
        var matches = ApiKeyRules.Matches(presented, key?.KeyHash ?? ApiKeyRules.DummyHash);
        if (key is null || !matches)
        {
            return Invalid;
        }

        var now = clock.GetUtcNow();
        if (!key.IsEnabled)
        {
            return new ApiKeyCheck(null, null, [], null, ErrorCodes.PublicApi.KeyDisabled);
        }

        if (key.IsExpired(now))
        {
            return new ApiKeyCheck(null, null, [], null, ErrorCodes.PublicApi.KeyExpired);
        }

        // "Last used" is written at most once a minute, without change tracking or audit rows (a busy key must not flood the audit trail).
        if (key.LastUsedAt is null || now - key.LastUsedAt >= TimeSpan.FromMinutes(1))
        {
            var ipHash = ApiKeyRules.HashIp(ip);
            await db.ApiKeys.Where(k => k.Id == key.Id).ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now).SetProperty(k => k.LastIpHash, ipHash), ct);
        }

        return new ApiKeyCheck(key.Id, key.BranchId, key.ScopeList, key.RateLimitPerMinute, null);
    }
}
