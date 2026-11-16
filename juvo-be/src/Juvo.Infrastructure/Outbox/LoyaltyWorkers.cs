using System.ComponentModel.DataAnnotations;
using Juvo.Application.Loyalty;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Outbox;

/// <summary>Background jobs of the rewards module. Section: <c>Loyalty</c>. Turn them off in tests and migration runs.</summary>
public sealed class LoyaltyJobOptions
{
    public const string SectionName = "Loyalty";

    /// <summary>Expire old points and warn about points that expire soon (FR-AB01).</summary>
    public bool ExpiryEnabled { get; set; } = true;

    [Range(1, 1440)]
    public int ExpiryIntervalMinutes { get; set; } = 60;
}

/// <summary>
/// Expires the points whose date has passed and sends the "expiring soon" warnings. Safe to run twice or on two servers (each customer is recomputed under their own lock).
/// A run that did something is written to the job log (FR-AF02). Does nothing for warnings while the feature flag is off; expiry of points that exist always runs.
/// </summary>
internal sealed class PointsExpiryWorker(IServiceScopeFactory scopeFactory, IOptions<LoyaltyJobOptions> options, TimeProvider timeProvider, ILogger<PointsExpiryWorker> logger) : BackgroundService
{
    internal const string JobName = "loyalty.points-expiry";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.ExpiryEnabled)
        {
            logger.LogInformation("The points expiry job is disabled by configuration.");
            return;
        }

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
                logger.LogError(ex, "Points expiry run failed; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(settings.ExpiryIntervalMinutes), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<ExpiryRun> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var started = timeProvider.GetUtcNow();
        var result = await scope.ServiceProvider.GetRequiredService<RewardsEngine>().ExpireDueAsync(cancellationToken);
        if (result.PointsExpired > 0 || result.Warned > 0)
        {
            var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
            var job = new JobLog(JobName, $"customers:{result.Customers} points:{result.PointsExpired} warned:{result.Warned}", 1, null, started);
            job.Succeed(timeProvider.GetUtcNow());
            db.JobLogs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Expired {Points} point(s) for {Customers} customer(s), warned {Warned}.", result.PointsExpired, result.Customers, result.Warned);
        }

        return result;
    }
}
