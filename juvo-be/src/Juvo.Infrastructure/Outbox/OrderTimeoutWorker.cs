using Juvo.Application.Orders;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Options;
using Juvo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Outbox;

/// <summary>
/// Runs the payment-hold timeout (BR-D2) on a timer: unpaid online-payment orders whose hold ended are cancelled and their stock released.
/// A sweep that cancelled something is recorded in the job log for the job dashboard (FR-AF02).
/// </summary>
internal sealed class OrderTimeoutWorker(IServiceScopeFactory scopeFactory, IOptions<OrderJobOptions> options, TimeProvider timeProvider, ILogger<OrderTimeoutWorker> logger) : BackgroundService
{
    internal const string JobName = "orders.payment-timeout";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.HoldJobEnabled)
        {
            logger.LogInformation("The payment-hold timeout job is disabled by configuration.");
            return;
        }

        logger.LogInformation("Payment-hold timeout job started (every {Seconds}s).", settings.HoldJobIntervalSeconds);
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
                logger.LogError(ex, "Payment-hold timeout sweep failed; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.HoldJobIntervalSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var started = timeProvider.GetUtcNow();
        var cancelled = await scope.ServiceProvider.GetRequiredService<OrderTimeoutService>().ExpireAsync(cancellationToken);
        if (cancelled > 0)
        {
            var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
            var job = new JobLog(JobName, $"cancelled:{cancelled}", 1, null, started);
            job.Succeed(timeProvider.GetUtcNow());
            db.JobLogs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Cancelled {Count} unpaid order(s) whose payment hold ended.", cancelled);
        }

        return cancelled;
    }
}
