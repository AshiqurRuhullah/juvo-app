using Juvo.Domain.Common;

namespace Juvo.Domain.Integrations;

/// <summary>
/// A key for the public read API (FR-AF05). Only the SHA-256 hash of the secret is stored; the secret is shown once when the key is created or rotated.
/// <see cref="KeyPrefix"/> (the first characters of the key) is public: it is shown in lists and used to find the row before the constant-time hash check.
/// </summary>
public sealed class ApiKey : Entity
{
    private ApiKey()
    {
    }

    public ApiKey(string name, string keyPrefix, byte[] keyHash, IEnumerable<string> scopes, Guid? branchId, DateTimeOffset? expiresAt, int? rateLimitPerMinute)
    {
        Name = name.Trim();
        KeyPrefix = keyPrefix;
        KeyHash = keyHash;
        Scopes = JoinScopes(scopes);
        BranchId = branchId;
        ExpiresAt = expiresAt;
        RateLimitPerMinute = rateLimitPerMinute;
    }

    public string Name { get; private set; } = string.Empty;

    /// <summary>The visible start of the key, for example <c>jv_live_ab12cd34</c>. Unique.</summary>
    public string KeyPrefix { get; private set; } = string.Empty;

    /// <summary>SHA-256 of the whole key. The key itself is never stored.</summary>
    [AuditIgnore]
    public byte[] KeyHash { get; private set; } = [];

    /// <summary>Comma separated scopes from the fixed catalogue.</summary>
    public string Scopes { get; private set; } = string.Empty;

    /// <summary>When set, orders and stock of this branch only.</summary>
    public Guid? BranchId { get; private set; }

    public bool IsEnabled { get; private set; } = true;

    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>Per-key override of the requests per minute (null = the setting <c>PublicApi.RateLimitPerMinute</c>).</summary>
    public int? RateLimitPerMinute { get; private set; }

    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>First 16 hex characters of SHA-256 of the caller address (never the address itself).</summary>
    public string? LastIpHash { get; private set; }

    public DateTimeOffset? RotatedAt { get; private set; }

    public IReadOnlyList<string> ScopeList => Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public bool HasScope(string scope) => ScopeList.Contains(scope, StringComparer.Ordinal);

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } e && e <= now;

    public void Update(string name, IEnumerable<string> scopes, Guid? branchId, DateTimeOffset? expiresAt, int? rateLimitPerMinute)
    {
        Name = name.Trim();
        Scopes = JoinScopes(scopes);
        BranchId = branchId;
        ExpiresAt = expiresAt;
        RateLimitPerMinute = rateLimitPerMinute;
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    /// <summary>Replaces the secret. The old secret stops working at once.</summary>
    public void Rotate(string keyPrefix, byte[] keyHash, DateTimeOffset now)
    {
        KeyPrefix = keyPrefix;
        KeyHash = keyHash;
        RotatedAt = now;
        LastUsedAt = null;
        LastIpHash = null;
    }

    private static string JoinScopes(IEnumerable<string> scopes) => string.Join(',', scopes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}
