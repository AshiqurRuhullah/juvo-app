using System.Security.Cryptography;
using System.Text;

namespace Juvo.Application.Integrations;

/// <summary>The fixed catalogue of scopes a public API key can hold (FR-AF05). No write scope exists in this step.</summary>
public static class ApiKeyScopes
{
    public const string OrdersRead = "orders:read";
    public const string ProductsRead = "products:read";
    public const string StockRead = "stock:read";
    public const string CustomersRead = "customers:read";

    /// <summary>Lets the key see customer names, mobile numbers, e-mail and delivery addresses unmasked. Off by default.</summary>
    public const string PiiRead = "pii:read";

    public static readonly IReadOnlyList<ScopeInfo> All =
    [
        new(OrdersRead, "Read orders (no customer details unless pii:read).", false),
        new(ProductsRead, "Read published products and their variants and prices.", false),
        new(StockRead, "Read available and on-hand stock per variant and branch.", false),
        new(CustomersRead, "Read customers (masked unless pii:read).", false),
        new(PiiRead, "See customer names, mobile numbers, e-mail and addresses unmasked.", true),
    ];

    public static bool IsKnown(string scope) => All.Any(s => string.Equals(s.Scope, scope, StringComparison.Ordinal));
}

public sealed record ScopeInfo(string Scope, string Description, bool Sensitive);

/// <summary>Creating, hashing and checking keys. Pure functions, no I/O.</summary>
public static class ApiKeyRules
{
    public const string KeyStart = "jv_live_";
    public const int IdLength = 8;

    /// <summary>Length of the visible prefix: <c>jv_live_</c> plus the 8 character public id.</summary>
    public const int PrefixLength = 16;

    private const string IdAlphabet = "abcdefghijklmnopqrstuvwxyz0123456789";

    public sealed record NewKey(string Key, string Prefix, byte[] Hash);

    /// <summary>A new random key: <c>jv_live_</c> + 8 lower-case letters or digits + <c>_</c> + 43 URL-safe characters (256 bits).</summary>
    public static NewKey Generate()
    {
        var id = RandomNumberGenerator.GetString(IdAlphabet, IdLength);
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', 'A').Replace('/', 'B');
        var key = $"{KeyStart}{id}_{secret}";
        return new NewKey(key, PrefixOf(key)!, Hash(key));
    }

    public static byte[] Hash(string key) => SHA256.HashData(Encoding.UTF8.GetBytes(key));

    /// <summary>The lookup prefix of a presented key, or null when it cannot be one of ours (wrong start or too short).</summary>
    public static string? PrefixOf(string? key) =>
        key is { Length: > PrefixLength + 8 } && key.StartsWith(KeyStart, StringComparison.Ordinal) ? key[..PrefixLength] : null;

    /// <summary>Constant-time comparison of the presented key against a stored hash.</summary>
    public static bool Matches(string presentedKey, byte[] storedHash) => CryptographicOperations.FixedTimeEquals(Hash(presentedKey), storedHash);

    /// <summary>A hash that matches nothing, compared when no row was found so the time taken does not tell whether the prefix exists.</summary>
    public static readonly byte[] DummyHash = SHA256.HashData("juvo-no-such-key"u8);

    /// <summary>Hash of the caller address for "last used from" (first 16 hex characters; the address is not stored).</summary>
    public static string? HashIp(string? ip) =>
        string.IsNullOrWhiteSpace(ip) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("juvo-ip:" + ip)))[..16].ToLowerInvariant();

    /// <summary>The policy name for a scope: <c>apikey:orders:read</c>.</summary>
    public const string PolicyPrefix = "apikey:";
}

/// <summary>Fixed-window request counter per key (in memory, one application server: known issue 2 in CLAUDE.md).</summary>
public sealed class ApiKeyRateLimiter(TimeProvider clock)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, Window> _windows = new();
    private long _lastSweep;

    private sealed class Window(long start)
    {
        public long Start = start;
        public int Count;
    }

    public sealed record Decision(bool Allowed, int Limit, int Remaining, int RetryAfterSeconds);

    /// <summary>Counts one request of <paramref name="keyId"/>. <paramref name="limit"/> is the permitted requests per minute.</summary>
    public Decision Hit(Guid keyId, int limit)
    {
        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var windowStart = now - (now % 60);
        Sweep(windowStart);
        var window = _windows.GetOrAdd(keyId, _ => new Window(windowStart));
        lock (window)
        {
            if (window.Start != windowStart)
            {
                window.Start = windowStart;
                window.Count = 0;
            }

            if (window.Count >= limit)
            {
                return new Decision(false, limit, 0, (int)Math.Max(1, windowStart + 60 - now));
            }

            window.Count++;
            return new Decision(true, limit, limit - window.Count, 0);
        }
    }

    private void Sweep(long windowStart)
    {
        var last = Interlocked.Read(ref _lastSweep);
        if (windowStart - last < 300 || Interlocked.CompareExchange(ref _lastSweep, windowStart, last) != last)
        {
            return;
        }

        foreach (var (id, w) in _windows)
        {
            if (w.Start < windowStart - 60)
            {
                _windows.TryRemove(id, out _);
            }
        }
    }
}

/// <summary>Settings read by the public API.</summary>
public static class PublicApiSettingKeys
{
    /// <summary>Requests per minute one key may make unless the key has its own limit.</summary>
    public const string RateLimitPerMinute = "PublicApi.RateLimitPerMinute";

    public const int DefaultRateLimitPerMinute = 60;
}
