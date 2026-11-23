using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Integrations;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juvo.Application.Integrations;

/// <summary>
/// Turns events into delivery rows (one per subscribed, enabled endpoint) and sends them. A delivery is claimed with one conditional update (a short lease), so two workers
/// never send the same attempt; a crash in the middle of an attempt means the delivery is tried again when the lease ends (at-least-once; receivers ignore repeats by the
/// event id, which is the same on every retry).
/// </summary>
public sealed class WebhookFanout(IApplicationDbContext db, TimeProvider clock)
{
    public static string Serialize(WebhookEnvelope envelope) => JsonSerializer.Serialize(envelope, JsonSerializerOptions.Web);

    public Task<bool> AnySubscriberAsync(string eventType, CancellationToken ct) =>
        db.WebhookEndpoints.AsNoTracking().AnyAsync(e => e.IsEnabled && (e.Events == eventType || e.Events.StartsWith(eventType + ",") || e.Events.EndsWith("," + eventType) || e.Events.Contains("," + eventType + ",")), ct);

    /// <summary>Writes the delivery rows for an event. Idempotent: an endpoint that already has a delivery for this event id gets none.</summary>
    public async Task<int> EnqueueAsync(Guid eventId, string eventType, object data, CancellationToken ct)
    {
        var endpoints = (await db.WebhookEndpoints.AsNoTracking().Where(e => e.IsEnabled).ToListAsync(ct)).Where(e => e.Subscribes(eventType)).ToList();
        if (endpoints.Count == 0)
        {
            return 0;
        }

        var ids = endpoints.Select(e => e.Id).ToList();
        var existing = await db.WebhookDeliveries.AsNoTracking().Where(d => d.EventId == eventId && ids.Contains(d.EndpointId)).Select(d => d.EndpointId).ToListAsync(ct);
        var now = clock.GetUtcNow();
        var body = Serialize(new WebhookEnvelope(eventId, eventType, now, data));
        var hash = WebhookDelivering.HashPayload(body);
        var added = 0;
        foreach (var endpoint in endpoints.Where(e => !existing.Contains(e.Id)))
        {
            db.WebhookDeliveries.Add(new WebhookDelivery(eventId, eventType, endpoint.Id, body, hash, now));
            added++;
        }

        await db.SaveChangesAsync(ct); // also saves anything the caller changed in the same transaction (the stock alert mark)

        return added;
    }
}

public sealed class WebhookDispatcher(IApplicationDbContext db, ISecretProtector protector, IWebhookSender sender, IWebhookTargetGuard guard, TimeProvider clock, ILogger<WebhookDispatcher> logger)
{
    /// <summary>How long a claimed delivery is left alone before another worker may take it.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromMinutes(2);

    /// <summary>Sends every delivery that is due (oldest first, at most <paramref name="batchSize"/>). Returns how many were attempted.</summary>
    public async Task<int> DeliverDueAsync(int batchSize, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var due = await db.WebhookDeliveries.AsNoTracking().Where(d => d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAt <= now)
            .OrderBy(d => d.NextAttemptAt).Select(d => d.Id).Take(batchSize).ToListAsync(ct);
        var attempted = 0;
        foreach (var id in due)
        {
            if (await DeliverNowAsync(id, singleTry: false, ct))
            {
                attempted++;
            }
        }

        return attempted;
    }

    /// <summary>Claims and attempts one delivery. False when it was not due or another worker has it. With <paramref name="singleTry"/> a failure is final (no retry is scheduled).</summary>
    public async Task<bool> DeliverNowAsync(Guid deliveryId, bool singleTry, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var leaseEnds = now + Lease;
        var claimed = await db.WebhookDeliveries.Where(d => d.Id == deliveryId && d.Status == WebhookDeliveryStatus.Pending && d.NextAttemptAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, leaseEnds), ct);
        if (claimed == 0)
        {
            return false;
        }

        var delivery = await db.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == deliveryId, ct);
        var endpoint = await db.WebhookEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == delivery.EndpointId, ct);
        var nextAttempt = delivery.Attempt + 1;
        TimeSpan? retry = singleTry ? null : WebhookDelivering.DelayAfter(nextAttempt);

        if (endpoint is null || !endpoint.IsEnabled && delivery.EventType != WebhookEvents.Ping)
        {
            delivery.RecordFailure(null, "The endpoint is disabled or was removed. Enable it and redeliver.", null, clock.GetUtcNow());
        }
        else if (protector.Unprotect(endpoint.SecretProtected) is not { } secret)
        {
            delivery.RecordFailure(null, "The signing secret cannot be read. Rotate the secret and redeliver.", null, clock.GetUtcNow());
        }
        else if (await guard.CheckUrlAsync(endpoint.Url, ct) is { } refusal)
        {
            // Refused addresses are never retried: the destination is not one we may call.
            delivery.RecordFailure(null, "Refused: " + refusal, null, clock.GetUtcNow());
        }
        else
        {
            var at = clock.GetUtcNow();
            var request = new WebhookSendRequest(endpoint.Url, delivery.Payload, new Dictionary<string, string>
            {
                [WebhookDelivering.SignatureHeader] = WebhookDelivering.Sign(secret, at, delivery.Payload),
                [WebhookDelivering.EventHeader] = delivery.EventType,
                [WebhookDelivering.DeliveryIdHeader] = delivery.Id.ToString(),
            });
            var result = await sender.SendAsync(request, ct);
            var finished = clock.GetUtcNow();
            if (result.Success)
            {
                delivery.RecordSuccess(result.StatusCode!.Value, finished);
            }
            else
            {
                delivery.RecordFailure(result.StatusCode, result.Error ?? $"HTTP {result.StatusCode}", retry, finished);
                logger.LogInformation("Webhook delivery {DeliveryId} ({Event}) attempt {Attempt} failed: {Reason}", delivery.Id, delivery.EventType, delivery.Attempt, delivery.Error);
            }
        }

        var d = delivery;
        await db.WebhookDeliveries.Where(x => x.Id == deliveryId).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.Attempt, d.Attempt).SetProperty(x => x.Status, d.Status).SetProperty(x => x.HttpStatus, d.HttpStatus).SetProperty(x => x.Error, d.Error)
            .SetProperty(x => x.NextAttemptAt, d.NextAttemptAt).SetProperty(x => x.LastAttemptAt, d.LastAttemptAt).SetProperty(x => x.DeliveredAt, d.DeliveredAt), ct);
        return true;
    }
}

/// <summary>Base of the outbox handlers that turn a platform event into webhook deliveries. All are idempotent (the event id is the outbox message id).</summary>
public abstract class WebhookEventHandler(WebhookFanout fanout, string messageType, string eventType) : IOutboxMessageHandler
{
    public string MessageType => messageType;

    public async Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        if (!await fanout.AnySubscriberAsync(eventType, cancellationToken))
        {
            return;
        }

        if (await BuildAsync(context, cancellationToken) is { } data)
        {
            await fanout.EnqueueAsync(context.MessageId, eventType, data, cancellationToken);
        }
    }

    /// <summary>The event data, or null when there is nothing to send (the thing no longer exists or is not public).</summary>
    protected abstract Task<object?> BuildAsync(OutboxMessageContext context, CancellationToken cancellationToken);

    protected static T Read<T>(OutboxMessageContext context) =>
        JsonSerializer.Deserialize<T>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException($"The {context.MessageType} message has no content.");
}

public sealed class WebhookOrderPlacedHandler(WebhookFanout fanout, PublicApiService api) : WebhookEventHandler(fanout, "domain.OrderPlaced", WebhookEvents.OrderPlaced)
{
    protected override async Task<object?> BuildAsync(OutboxMessageContext context, CancellationToken cancellationToken) =>
        await api.OrderForWebhookAsync(Read<OrderPlaced>(context).OrderId, cancellationToken);
}

public sealed class WebhookOrderStatusHandler(WebhookFanout fanout, PublicApiService api) : WebhookEventHandler(fanout, "domain.OrderStatusChanged", WebhookEvents.OrderStatusChanged)
{
    protected override async Task<object?> BuildAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = Read<OrderStatusChanged>(context);
        var order = await api.OrderForWebhookAsync(e.OrderId, cancellationToken);
        return order is null ? null : new OrderStatusChangedData(e.OrderId, e.Number, e.Track.ToString(), e.From, e.To, order);
    }
}

/// <summary>Written by the save interceptor when a product becomes published, is changed while published, or gets a changed variant or picture.</summary>
public sealed record ProductWebhookMessage(Guid ProductId);

public sealed record StockLowWebhookMessage(Guid VariantId, Guid BranchId);

public sealed class WebhookProductPublishedHandler(WebhookFanout fanout, PublicApiService api) : WebhookEventHandler(fanout, WebhookMessageTypes.ProductPublished, WebhookEvents.ProductPublished)
{
    protected override async Task<object?> BuildAsync(OutboxMessageContext context, CancellationToken cancellationToken) =>
        await api.ProductForWebhookAsync(Read<ProductWebhookMessage>(context).ProductId, cancellationToken);
}

public sealed class WebhookProductUpdatedHandler(WebhookFanout fanout, PublicApiService api) : WebhookEventHandler(fanout, WebhookMessageTypes.ProductUpdated, WebhookEvents.ProductUpdated)
{
    protected override async Task<object?> BuildAsync(OutboxMessageContext context, CancellationToken cancellationToken) =>
        await api.ProductForWebhookAsync(Read<ProductWebhookMessage>(context).ProductId, cancellationToken);
}

/// <summary>
/// "Something changed in this variant's stock in this branch": sends <c>stock.low</c> when it is at or below its reorder level and no alert was sent for the current fall
/// (the row remembers it in <c>LowAlertedAt</c> until the stock is above its level again). The alert mark and the delivery rows are saved together, so a repeat of the same
/// message sends nothing twice and a crash in between loses nothing (the message is retried).
/// </summary>
public sealed class WebhookStockLowHandler(IApplicationDbContext db, WebhookFanout fanout, PublicApiService api, TimeProvider clock) : IOutboxMessageHandler
{
    public string MessageType => WebhookMessageTypes.StockLow;

    public async Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<StockLowWebhookMessage>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("The stock message has no content.");
        var level = await db.StockLevels.IgnoreQueryFilters(["BranchScope"]).FirstOrDefaultAsync(s => s.VariantId == message.VariantId && s.BranchId == message.BranchId, cancellationToken);
        if (level is null)
        {
            return;
        }

        if (!level.IsLow)
        {
            if (level.LowAlertedAt is not null)
            {
                level.ClearLowAlert();
                await db.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        if (level.LowAlertedAt is not null || !await fanout.AnySubscriberAsync(WebhookEvents.StockLow, cancellationToken))
        {
            return;
        }

        var data = await api.StockLowForWebhookAsync(message.VariantId, message.BranchId, cancellationToken);
        if (data is null)
        {
            return;
        }

        level.MarkLowAlerted(clock.GetUtcNow());
        await fanout.EnqueueAsync(context.MessageId, WebhookEvents.StockLow, data, cancellationToken); // saves the alert mark together with the deliveries
    }
}


/// <summary>Outbox message names written by the save interceptor (the order events reuse the domain event messages).</summary>
public static class WebhookMessageTypes
{
    public const string ProductPublished = "webhook.product.published";
    public const string ProductUpdated = "webhook.product.updated";
    public const string StockLow = "webhook.stock.low";
}
