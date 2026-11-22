using Juvo.Application.Abstractions;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Settings;

/// <summary>A stored <c>PlatformSettings</c> row as the reader sees it.</summary>
public sealed record StoredSetting(Guid Id, string Value, byte[] RowVersion, DateTimeOffset? UpdatedAt, Guid? UpdatedBy);

/// <summary>
/// Reads the runtime settings through a short in-process cache (one application server, CLAUDE.md known issue 2). A save that touches a setting empties the cache at once
/// (<see cref="SettingsCache.Invalidate"/>, called by the save interceptor), and an entry never lives longer than <see cref="SettingsCache.Lifetime"/> even if a write
/// came from another process or from raw SQL.
/// </summary>
public interface ISettingsReader
{
    /// <summary>The stored values of the keys that have a row. Keys without a row are absent (the caller falls back to its default).</summary>
    Task<IReadOnlyDictionary<string, string>> ReadAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken);

    /// <summary>Every stored row (the table is small).</summary>
    Task<IReadOnlyDictionary<string, StoredSetting>> SnapshotAsync(CancellationToken cancellationToken);

    /// <summary>A registered setting as a typed value; the definition's default when there is no row or the stored text is not valid.</summary>
    Task<bool> GetBoolAsync(string key, CancellationToken cancellationToken);

    Task<int> GetIntAsync(string key, CancellationToken cancellationToken);

    Task<decimal> GetDecimalAsync(string key, CancellationToken cancellationToken);

    Task<string> GetStringAsync(string key, CancellationToken cancellationToken);
}

/// <summary>The shared (singleton) snapshot behind <see cref="ISettingsReader"/>.</summary>
public sealed class SettingsCache(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(10);

    private readonly object _gate = new();
    private IReadOnlyDictionary<string, StoredSetting>? _snapshot;
    private DateTimeOffset _loadedAt;
    private long _generation;

    public long Generation
    {
        get
        {
            lock (_gate)
            {
                return _generation;
            }
        }
    }

    public bool TryGet(out IReadOnlyDictionary<string, StoredSetting> snapshot)
    {
        lock (_gate)
        {
            if (_snapshot is not null && clock.GetUtcNow() - _loadedAt < Lifetime)
            {
                snapshot = _snapshot;
                return true;
            }
        }

        snapshot = new Dictionary<string, StoredSetting>();
        return false;
    }

    /// <summary>Keeps a freshly loaded snapshot unless a write emptied the cache while it was being read.</summary>
    public void Store(IReadOnlyDictionary<string, StoredSetting> snapshot, long generationBeforeLoad)
    {
        lock (_gate)
        {
            if (generationBeforeLoad == _generation)
            {
                _snapshot = snapshot;
                _loadedAt = clock.GetUtcNow();
            }
        }
    }

    public void Invalidate()
    {
        lock (_gate)
        {
            _generation++;
            _snapshot = null;
        }
    }
}

public sealed class SettingsReader(IApplicationDbContext db, SettingsCache cache) : ISettingsReader
{
    public async Task<IReadOnlyDictionary<string, StoredSetting>> SnapshotAsync(CancellationToken cancellationToken)
    {
        if (cache.TryGet(out var cached))
        {
            return cached;
        }

        var generation = cache.Generation;
        var rows = await db.PlatformSettings.AsNoTracking().Select(s => new { s.Key, s.Id, s.Value, s.RowVersion, s.UpdatedAt, s.UpdatedBy }).ToListAsync(cancellationToken);
        var snapshot = rows.ToDictionary(r => r.Key, r => new StoredSetting(r.Id, r.Value, r.RowVersion, r.UpdatedAt, r.UpdatedBy), StringComparer.Ordinal);
        cache.Store(snapshot, generation);
        return snapshot;
    }

    public async Task<IReadOnlyDictionary<string, string>> ReadAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken)
    {
        var snapshot = await SnapshotAsync(cancellationToken);
        var result = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            if (snapshot.TryGetValue(key, out var row))
            {
                result[key] = row.Value;
            }
        }

        return result;
    }

    public async Task<bool> GetBoolAsync(string key, CancellationToken cancellationToken) => bool.Parse(await ValueAsync(key, SettingType.Bool, cancellationToken));

    public async Task<int> GetIntAsync(string key, CancellationToken cancellationToken) =>
        int.Parse(await ValueAsync(key, SettingType.Whole, cancellationToken), System.Globalization.CultureInfo.InvariantCulture);

    public async Task<decimal> GetDecimalAsync(string key, CancellationToken cancellationToken) =>
        decimal.Parse(await ValueAsync(key, SettingType.Number, cancellationToken), System.Globalization.CultureInfo.InvariantCulture);

    public Task<string> GetStringAsync(string key, CancellationToken cancellationToken) => ValueAsync(key, null, cancellationToken);

    /// <summary>The normalised stored text, or the definition's default when there is no row or the row does not pass the definition.</summary>
    private async Task<string> ValueAsync(string key, SettingType? expected, CancellationToken cancellationToken)
    {
        var definition = SettingRegistry.Find(key) ?? throw new InvalidOperationException($"Setting '{key}' is not registered.");
        if (expected is { } type && definition.Type != type)
        {
            throw new InvalidOperationException($"Setting '{key}' is a {definition.Type}, not a {type}.");
        }

        var snapshot = await SnapshotAsync(cancellationToken);
        if (snapshot.TryGetValue(key, out var row) && definition.Normalize(row.Value) is { IsSuccess: true } ok)
        {
            return ok.Value;
        }

        return definition.Default;
    }
}
