using System.ComponentModel.DataAnnotations;
using Juvo.Application.Reports;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Reports;

/// <summary>Background job of the reports module. Section: <c>Reports</c>. The owner's email and send hour are runtime settings (docs/reports-api.md); with no email the job does nothing.</summary>
public sealed class ReportJobOptions
{
    public const string SectionName = "Reports";

    /// <summary>Run the daily summary job. Turn off in tests and migration runs.</summary>
    public bool DailySummaryEnabled { get; set; } = true;

    [Range(1, 1440)]
    public int DailySummaryIntervalMinutes { get; set; } = 15;
}

/// <summary>Sends yesterday's summary to the owner once per day (FR-P07). Idempotent: the date of the last summary is stored.</summary>
internal sealed class DailySummaryWorker(IServiceScopeFactory scopeFactory, IOptions<ReportJobOptions> options, TimeProvider timeProvider, ILogger<DailySummaryWorker> logger) : BackgroundService
{
    internal const string JobName = "reports.daily-summary";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.DailySummaryEnabled)
        {
            logger.LogInformation("The daily summary job is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Daily summary run failed; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(settings.DailySummaryIntervalMinutes), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<DailySummaryOutcome> RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var started = timeProvider.GetUtcNow();
        var outcome = await scope.ServiceProvider.GetRequiredService<DailySummaryService>().SendIfDueAsync(cancellationToken);
        if (outcome == DailySummaryOutcome.Sent)
        {
            var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
            var job = new JobLog(JobName, "sent", 1, null, started);
            job.Succeed(timeProvider.GetUtcNow());
            db.JobLogs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Daily summary sent.");
        }

        return outcome;
    }
}
