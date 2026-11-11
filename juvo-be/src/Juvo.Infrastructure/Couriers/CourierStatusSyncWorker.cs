using System.ComponentModel.DataAnnotations;
using Juvo.Application.Couriers;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Couriers;

/// <summary>Settings of the courier status poll. Section: <c>Couriers:Sync</c>. Turn it off in tests and migration runs.</summary>
public sealed class CourierSyncOptions
{
    public const string SectionName = "Couriers:Sync";

    public bool Enabled { get; set; } = true;

    [Range(5, 3600)]
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>The most parcels asked per sweep, oldest check first.</summary>
    [Range(1, 1000)]
    public int BatchSize { get; set; } = 200;
}

/// <summary>
/// The safety net behind the webhooks (module J): every sweep asks each courier about the parcels that are still open and applies what it says, through the same path as a webhook, so a
/// lost webhook only delays an update. A sweep that found changes or failures is written to the job log for the job dashboard (FR-AF02).
/// </summary>
internal sealed class CourierStatusSyncWorker(IServiceScopeFactory scopeFactory, IOptions<CourierSyncOptions> options, TimeProvider timeProvider, ILogger<CourierStatusSyncWorker> logger) : BackgroundService
{
    internal const string JobName = "couriers.status-sync";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("The courier status sync job is disabled by configuration.");
            return;
        }

        logger.LogInformation("Courier status sync job started (every {Seconds}s).", settings.IntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Courier status sync sweep failed; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.IntervalSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>One sweep. Public to the assembly so tests can run it without waiting for the timer.</summary>
    internal async Task<(int Checked, int Changed, int Failed)> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var started = timeProvider.GetUtcNow();
        var result = await scope.ServiceProvider.GetRequiredService<CourierWebhookService>().PollOpenAsync(options.Value.BatchSize, cancellationToken);
        if (result.Changed > 0 || result.Failed > 0)
        {
            var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
            var job = new JobLog(JobName, $"checked:{result.Checked} changed:{result.Changed} failed:{result.Failed}", 1, null, started);
            if (result.Failed > 0)
            {
                job.Fail(timeProvider.GetUtcNow(), $"{result.Failed} parcel(s) could not be checked.");
            }
            else
            {
                job.Succeed(timeProvider.GetUtcNow());
            }

            db.JobLogs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
        }

        return result;
    }
}
