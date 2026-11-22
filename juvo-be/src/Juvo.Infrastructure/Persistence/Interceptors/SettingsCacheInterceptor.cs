using Juvo.Application.Settings;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Juvo.Infrastructure.Persistence.Interceptors;

/// <summary>
/// Empties the settings cache after a save that added, changed or removed a <see cref="PlatformSetting"/> row, whoever made it (the settings centre or an older module endpoint),
/// so the next read sees the new value (FR-AF01). Bulk SQL (<c>ExecuteUpdate</c>) bypasses this; the cache lifetime covers that case.
/// </summary>
internal sealed class SettingsCacheInterceptor(SettingsCache cache) : SaveChangesInterceptor
{
    private bool _touched;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Remember(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Remember(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush();
        return base.SavedChanges(eventData, result);
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush();
        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _touched = false;

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _touched = false;
        return Task.CompletedTask;
    }

    private void Remember(DbContext? context)
    {
        if (context is not null && context.ChangeTracker.Entries<PlatformSetting>().Any(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            _touched = true;
        }
    }

    private void Flush()
    {
        if (_touched)
        {
            _touched = false;
            cache.Invalidate();
        }
    }
}
