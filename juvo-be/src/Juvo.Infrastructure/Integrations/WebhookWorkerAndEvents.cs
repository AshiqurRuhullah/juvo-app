using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Integrations;
using Juvo.Domain.Catalog;
using Juvo.Domain.Inventory;
using Juvo.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Integrations;

/// <summary>Outgoing webhook delivery job. Section: <c>Webhooks</c>.</summary>
public sealed class WebhookJobOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>Run the delivery worker. Turn off in tests that call the dispatcher themselves.</summary>
    public bool WorkerEnabled { get; set; } = true;

    [Range(1, 600)]
    public int PollIntervalSeconds { get; set; } = 5;

    [Range(1, 200)]
    public int BatchSize { get; set; } = 20;
}

/// <summary>Sends due webhook deliveries (first attempts and retries with backoff) and leaves failed ones visible for a manual redelivery.</summary>
internal sealed class WebhookDeliveryWorker(IServiceScopeFactory scopeFactory, IOptions<WebhookJobOptions> options, TimeProvider timeProvider, ILogger<WebhookDeliveryWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.WorkerEnabled)
        {
            logger.LogInformation("The webhook delivery worker is disabled by configuration.");
            return;
        }

        logger.LogInformation("Webhook delivery worker started (poll {Poll}s).", settings.PollIntervalSeconds);
        while (!stoppingToken.IsCancellationRequested)
        {
            var attempted = 0;
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                attempted = await scope.ServiceProvider.GetRequiredService<WebhookDispatcher>().DeliverDueAsync(settings.BatchSize, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Webhook delivery batch failed; will retry on the next poll.");
            }

            if (attempted == 0)
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
}

/// <summary>
/// Writes the outbox messages for the webhook events that are not domain events: a product becomes published or is changed while published (or one of its variants or
/// pictures changes), and stock changes (the handler then decides whether a variant fell to or below its reorder level). Same save, same transaction as the change. The handlers read the current state when they run, so
/// a product that was unpublished again in the meantime sends nothing.
/// </summary>
internal sealed class WebhookEventsInterceptor(TimeProvider timeProvider, ICurrentUser currentUser) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Collect(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Collect(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static readonly HashSet<string> Stamps = ["UpdatedAt", "UpdatedBy", "CreatedAt", "CreatedBy", "RowVersion"];

    private void Collect(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        context.ChangeTracker.DetectChanges();
        var published = new HashSet<Guid>();
        var updated = new HashSet<Guid>();
        var low = new HashSet<(Guid Variant, Guid Branch)>();

        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case Product product when entry.State is EntityState.Added or EntityState.Modified:
                    if (product.Status == ProductStatus.Published && (entry.State == EntityState.Added || entry.Property(nameof(Product.Status)).OriginalValue is not ProductStatus.Published))
                    {
                        published.Add(product.Id);
                    }
                    else if (product.Status == ProductStatus.Published && entry.State == EntityState.Modified && Changed(entry))
                    {
                        updated.Add(product.Id);
                    }

                    break;
                case ProductVariant variant when entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && (entry.State != EntityState.Modified || Changed(entry)):
                    updated.Add(variant.ProductId);
                    break;
                case ProductImage image when entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted && (entry.State != EntityState.Modified || Changed(entry)):
                    updated.Add(image.ProductId);
                    break;
                // Order reservations change stock with a direct SQL update that never reaches the change tracker, but each of them writes a ledger line, so a new ledger line
                // (like a changed or new stock row) marks the variant in that branch as "check whether it is low now". The handler decides, once per fall.
                case StockMovement movement when entry.State == EntityState.Added:
                    low.Add((movement.VariantId, movement.BranchId));
                    break;
                case StockLevel level when entry.State == EntityState.Added || entry.State == EntityState.Modified && Changed(entry, nameof(StockLevel.LowAlertedAt)):
                    low.Add((level.VariantId, level.BranchId));
                    break;
            }
        }

        updated.ExceptWith(published);
        if (published.Count == 0 && updated.Count == 0 && low.Count == 0)
        {
            return;
        }

        var now = timeProvider.GetUtcNow();
        var set = context.Set<OutboxMessage>();
        foreach (var id in published)
        {
            set.Add(Message(WebhookMessageTypes.ProductPublished, new ProductWebhookMessage(id), now));
        }

        foreach (var id in updated)
        {
            set.Add(Message(WebhookMessageTypes.ProductUpdated, new ProductWebhookMessage(id), now));
        }

        foreach (var (variant, branch) in low)
        {
            set.Add(Message(WebhookMessageTypes.StockLow, new StockLowWebhookMessage(variant, branch), now));
        }
    }

    private OutboxMessage Message<T>(string type, T payload, DateTimeOffset now) =>
        new(type, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), now, currentUser.CorrelationId);

    private static bool Changed(EntityEntry entry, string? alsoIgnore = null) => entry.Properties.Any(p => p.IsModified && !Stamps.Contains(p.Metadata.Name) && p.Metadata.Name != alsoIgnore);

}
