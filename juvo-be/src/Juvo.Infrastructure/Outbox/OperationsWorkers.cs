using System.ComponentModel.DataAnnotations;
using Juvo.Application.Accounting;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Outbox;

/// <summary>Background jobs of purchasing, expenses and support. Section: <c>Operations</c>. Turn them off in tests and migration runs.</summary>
public sealed class OperationsJobOptions
{
    public const string SectionName = "Operations";

    /// <summary>Generate the monthly recurring expenses (module X).</summary>
    public bool RecurringExpensesEnabled { get; set; } = true;

    [Range(1, 1440)]
    public int RecurringExpensesIntervalMinutes { get; set; } = 60;

    /// <summary>Close tickets that stayed Resolved for the configured number of days (module Y).</summary>
    public bool TicketAutoCloseEnabled { get; set; } = true;

    [Range(1, 1440)]
    public int TicketAutoCloseIntervalMinutes { get; set; } = 30;
}

/// <summary>Creates the monthly expenses that are due, once per template and month. A run that created something is written to the job log (FR-AF02).</summary>
internal sealed class RecurringExpenseWorker(IServiceScopeFactory scopeFactory, IOptions<OperationsJobOptions> options, TimeProvider timeProvider, ILogger<RecurringExpenseWorker> logger) : BackgroundService
{
    internal const string JobName = "expenses.recurring";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.RecurringExpensesEnabled)
        {
            logger.LogInformation("The recurring expense job is disabled by configuration.");
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
                logger.LogError(ex, "Recurring expense run failed; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMinutes(settings.RecurringExpensesIntervalMinutes), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<GenerateRecurringResult> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var started = timeProvider.GetUtcNow();
        var result = await scope.ServiceProvider.GetRequiredService<RecurringExpenseService>().GenerateDueAsync(null, cancellationToken);
        if (result.Created > 0)
        {
            var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
            var job = new JobLog(JobName, $"templates:{result.Templates} created:{result.Created} auto-approved:{result.AutoApproved}", 1, null, started);
            job.Succeed(timeProvider.GetUtcNow());
            db.JobLogs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Created {Count} recurring expense(s).", result.Created);
        }

        return result;
    }
}
