using Juvo.Application.Abstractions;
using Juvo.Application.Monitoring;
using Juvo.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Application.Backup;

/// <summary>
/// Holds the "a backup is running" flag for this process (one application server, CLAUDE.md known issue 2) and starts manual runs in the background. A run row left in
/// <c>Running</c> by a process that stopped is closed as failed the next time a run starts (<see cref="BackupRunner.RecoverInterruptedAsync"/>).
/// </summary>
public sealed class BackupCoordinator(IServiceScopeFactory scopes, ILogger<BackupCoordinator> logger)
{
    private int _running;

    /// <summary>The background task of the latest manual run (tests await it; nothing else needs it).</summary>
    public Task? Current { get; private set; }

    public bool IsRunning => Volatile.Read(ref _running) == 1;

    /// <summary>Takes the single run slot. False when a backup is already running.</summary>
    public bool TryEnter() => Interlocked.CompareExchange(ref _running, 1, 0) == 0;

    public void Exit() => Volatile.Write(ref _running, 0);

    /// <summary>Runs the already created run in the background and frees the slot when it ends, whatever happens.</summary>
    public void StartInBackground(Guid runId) =>
        Current = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<BackupRunner>().ExecuteAsync(runId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup run {RunId} stopped with an unexpected error.", runId);
            }
            finally
            {
                Exit();
            }
        });
}

/// <summary>Creates, executes and cleans up after one backup run (FR-AF03). Used by the staff endpoint (through the coordinator) and by the nightly worker.</summary>
public sealed class BackupRunner(IApplicationDbContext db, IBackupEngine engine, IOptions<BackupOptions> options, TimeProvider clock, ILogger<BackupRunner> logger)
{
    public async Task<BackupRun> BeginAsync(BackupTrigger trigger, Guid? userId, CancellationToken cancellationToken)
    {
        var run = new BackupRun(trigger, userId, clock.GetUtcNow());
        db.BackupRuns.Add(run);
        await db.SaveChangesAsync(cancellationToken);
        return run;
    }

    /// <summary>Closes runs still marked Running. Call only while holding the coordinator slot, so a live run cannot be mistaken for a stale one.</summary>
    public async Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken)
    {
        var stale = await db.BackupRuns.Where(r => r.Status == BackupStatus.Running).ToListAsync(cancellationToken);
        foreach (var run in stale)
        {
            run.Fail(clock.GetUtcNow(), "The application stopped before this backup finished.");
        }

        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return stale.Count;
    }

    /// <summary>Runs the backup of an existing run row, records the result and, after a verified backup, applies the retention.</summary>
    public async Task<BackupRun> ExecuteAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await db.BackupRuns.FirstAsync(r => r.Id == runId, cancellationToken);
        try
        {
            var database = await engine.GetDatabaseNameAsync(cancellationToken);
            var name = BackupNaming.FileName(database, clock.GetUtcNow(), run.Id);
            var result = await engine.BackupAsync(name, cancellationToken);
            if (result.Written && result.Verified)
            {
                run.Succeed(clock.GetUtcNow(), name, result.SizeBytes ?? 0, result.Compressed);
            }
            else
            {
                run.Fail(clock.GetUtcNow(), ErrorScrubber.Scrub(result.Error) ?? "The backup did not verify.", result.Written ? name : null, result.SizeBytes, result.Compressed);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Backup run {RunId} failed.", runId);
            run.Fail(clock.GetUtcNow(), ErrorScrubber.Scrub(ex.Message) ?? "The backup failed.");
        }

        await db.SaveChangesAsync(cancellationToken);

        if (run.Status == BackupStatus.Succeeded)
        {
            try
            {
                await ApplyRetentionAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Backup retention clean-up failed; the new backup is in place.");
            }
        }

        return run;
    }

    /// <summary>Deletes this service's own backup files of this database that are older than the retention. The newest verified file is never deleted. Returns the names removed.</summary>
    public async Task<IReadOnlyList<string>> ApplyRetentionAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var database = await engine.GetDatabaseNameAsync(cancellationToken);
        var keep = await db.BackupRuns.AsNoTracking().Where(r => r.Verified && r.FileName != null && r.FileDeletedAt == null)
            .OrderByDescending(r => r.StartedAt).Select(r => r.FileName).FirstOrDefaultAsync(cancellationToken);
        var expired = BackupRetention.SelectExpired(engine.ListFiles().Select(f => f.Name), database, now, options.Value.RetentionDays, keep);
        var removed = new List<string>();
        foreach (var name in expired)
        {
            if (engine.DeleteFile(name))
            {
                removed.Add(name);
            }
        }

        if (removed.Count > 0)
        {
            await db.BackupRuns.Where(r => r.FileName != null && removed.Contains(r.FileName) && r.FileDeletedAt == null)
                .ExecuteUpdateAsync(set => set.SetProperty(r => r.FileDeletedAt, now), cancellationToken);
        }

        return removed;
    }
}
