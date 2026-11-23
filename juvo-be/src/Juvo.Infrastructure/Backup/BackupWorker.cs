using Juvo.Application.Backup;
using Juvo.Application.Settings;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Backup;

/// <summary>
/// The nightly backup (FR-AF03). Checks once a minute: when the <c>Backup.Enabled</c> setting is on and the configured Asia/Dhaka time has passed (within the catch-up window)
/// without a scheduled run since, it takes a backup, writes a <c>JobLog</c> row (so it shows in the jobs screen and in the failed-jobs count) and applies the retention.
/// One attempt per night: a failed run is not retried until the next night (it is visible in the jobs page and the backup status). Does nothing when
/// <c>Backup:WorkerEnabled</c> is false (tests, migration runs).
/// </summary>
internal sealed class BackupWorker(IServiceScopeFactory scopeFactory, BackupCoordinator coordinator, IOptions<BackupOptions> options, TimeProvider timeProvider, ILogger<BackupWorker> logger)
    : BackgroundService
{
    internal static readonly TimeSpan CheckEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkerEnabled)
        {
            logger.LogInformation("The backup job is disabled by configuration.");
            return;
        }

        if (!BackupSchedule.TryParseTime(options.Value.DailyAt, out _))
        {
            logger.LogError("Backup:DailyAt '{Value}' is not a time written HH:mm; the nightly backup will not run.", options.Value.DailyAt);
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunIfDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Backup schedule check failed; will retry.");
            }

            try
            {
                await Task.Delay(CheckEvery, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Takes the nightly backup when it is due. Returns the run, or null when nothing was due (disabled, not yet time, already taken, or a manual run holds the slot).</summary>
    internal async Task<BackupRun?> RunIfDueAsync(CancellationToken cancellationToken)
    {
        await using (var probe = scopeFactory.CreateAsyncScope())
        {
            if (!await probe.ServiceProvider.GetRequiredService<ISettingsReader>().GetBoolAsync(BackupSettingKeys.Enabled, cancellationToken))
            {
                return null;
            }

            var at = BackupSchedule.TryParseTime(options.Value.DailyAt, out var parsed) ? parsed : new TimeOnly(2, 30);
            var db = probe.ServiceProvider.GetRequiredService<JuvoDbContext>();
            var lastScheduled = await db.BackupRuns.AsNoTracking().Where(r => r.Trigger == BackupTrigger.Schedule).OrderByDescending(r => r.StartedAt)
                .Select(r => (DateTimeOffset?)r.StartedAt).FirstOrDefaultAsync(cancellationToken);
            if (!BackupSchedule.IsDue(timeProvider.GetUtcNow(), at, lastScheduled))
            {
                return null;
            }
        }

        if (!coordinator.TryEnter())
        {
            return null;
        }

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var runner = scope.ServiceProvider.GetRequiredService<BackupRunner>();
            var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
            await runner.RecoverInterruptedAsync(cancellationToken);

            var started = timeProvider.GetUtcNow();
            var run = await runner.BeginAsync(BackupTrigger.Schedule, null, cancellationToken);
            var job = new JobLog(BackupSettingKeys.JobName, run.Id.ToString(), 1, null, started);
            db.JobLogs.Add(job);
            await db.SaveChangesAsync(cancellationToken);

            run = await runner.ExecuteAsync(run.Id, cancellationToken);
            if (run.Status == BackupStatus.Succeeded)
            {
                job.Succeed(timeProvider.GetUtcNow());
                logger.LogInformation("Nightly backup {File} written and verified.", run.FileName);
            }
            else
            {
                job.Fail(timeProvider.GetUtcNow(), run.Error ?? "The backup failed.");
                logger.LogError("Nightly backup failed: {Error}", run.Error);
            }

            await db.SaveChangesAsync(cancellationToken);
            return run;
        }
        finally
        {
            coordinator.Exit();
        }
    }
}
