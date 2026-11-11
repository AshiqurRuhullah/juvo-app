using Juvo.Application.Abstractions;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Options;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Outbox;

/// <summary>
/// Background worker that delivers outbox messages. Rows are claimed with <c>UPDLOCK, READPAST</c> so several
/// instances never process the same message. Each attempt writes a <see cref="JobLog"/> row; failures retry with
/// exponential backoff and become <see cref="OutboxStatus.Failed"/> after the maximum attempts (visible for manual retry).
/// </summary>
internal sealed class OutboxProcessor(
    IServiceScopeFactory scopeFactory,
    IOptions<OutboxOptions> options,
    TimeProvider timeProvider,
    ILogger<OutboxProcessor> logger) : BackgroundService
{
    internal const string JobName = "outbox.deliver";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("Outbox worker is disabled by configuration.");
            return;
        }

        logger.LogInformation("Outbox worker started (poll {Poll}s, batch {Batch}).", settings.PollIntervalSeconds, settings.BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = 0;
            try
            {
                processed = await ProcessBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox batch failed; will retry on the next poll.");
            }

            if (processed == 0)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), timeProvider, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Claims and delivers one batch. Returns the number of messages handled (success or failure).</summary>
    internal async Task<int> ProcessBatchAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value;
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();

        var strategy = db.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async ct =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            var now = timeProvider.GetUtcNow();

            // Table name matches OutboxMessageConfiguration (ToTable("OutboxMessages")). UPDLOCK + READPAST lets several
            // worker instances claim different rows without blocking each other.
            var batch = await db.OutboxMessages
                .FromSql($"""
                    SELECT TOP ({settings.BatchSize}) * FROM [dbo].[OutboxMessages] WITH (UPDLOCK, READPAST, ROWLOCK)
                    WHERE [Status] = 0 AND [NextAttemptAt] <= {now} ORDER BY [NextAttemptAt]
                    """)
                .ToListAsync(ct);

            foreach (var message in batch)
            {
                await DeliverAsync(db, message, settings, ct);
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return batch.Count;
        }, cancellationToken);
    }

    private async Task DeliverAsync(
        JuvoDbContext db, OutboxMessage message, OutboxOptions settings, CancellationToken cancellationToken)
    {
        var started = timeProvider.GetUtcNow();
        var job = new JobLog(JobName, message.Id.ToString(), message.AttemptCount + 1, message.CorrelationId, started);
        db.JobLogs.Add(job);

        try
        {
            // Each handler runs in its own scope (own DbContext), so a failing handler cannot leak partial changes
            // into the claim transaction. Delivery is at-least-once: handlers must be idempotent.
            await using var handlerScope = scopeFactory.CreateAsyncScope();
            // Several handlers may listen to one message type (for example the customer message and the invoice for an order event). They all run; if one throws the message is
            // retried, so every handler must be idempotent (they all are: at-least-once delivery already requires that).
            var handlers = handlerScope.ServiceProvider.GetServices<IOutboxMessageHandler>()
                .Where(h => string.Equals(h.MessageType, message.Type, StringComparison.Ordinal)).ToList();
            if (handlers.Count == 0)
            {
                throw new InvalidOperationException($"No outbox handler is registered for message type '{message.Type}'.");
            }

            var context = new OutboxMessageContext(message.Id, message.Type, message.Payload, message.AttemptCount + 1, message.CorrelationId);
            foreach (var handler in handlers)
            {
                await handler.HandleAsync(context, cancellationToken);
            }

            var finished = timeProvider.GetUtcNow();
            message.MarkSucceeded(finished);
            job.Succeed(finished);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var finished = timeProvider.GetUtcNow();
            logger.LogWarning(ex, "Outbox message {MessageId} ({Type}) failed on attempt {Attempt}.", message.Id, message.Type, message.AttemptCount + 1);
            message.MarkFailed(finished, ex.Message, settings.MaxAttempts,
                TimeSpan.FromSeconds(settings.BaseDelaySeconds), TimeSpan.FromMinutes(settings.MaxDelayMinutes));
            job.Fail(finished, ex.Message);
        }
    }
}
