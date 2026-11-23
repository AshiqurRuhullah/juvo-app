using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Domain.Integrations;
using Juvo.Domain.Platform;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Integrations;

public sealed record WebhookEndpointDto(
    Guid Id, string Name, string Url, IReadOnlyList<string> Events, bool IsEnabled, DateTimeOffset? SecretRotatedAt, DateTimeOffset CreatedAt, int FailedDeliveries);

/// <summary>The signing secret is in this response only.</summary>
public sealed record WebhookEndpointCreatedDto(WebhookEndpointDto Endpoint, string Secret);

public sealed record WebhookEndpointRequest(string? Name, string? Url, IReadOnlyList<string>? Events, bool? IsEnabled);

public sealed record WebhookDeliveryDto(
    Guid Id, Guid EventId, string EventType, Guid EndpointId, string? EndpointName, int Attempt, int MaxAttempts, WebhookDeliveryStatus Status, int? HttpStatus, string? Error,
    DateTimeOffset NextAttemptAt, DateTimeOffset? LastAttemptAt, DateTimeOffset? DeliveredAt, string PayloadHash, int Redeliveries, DateTimeOffset CreatedAt);

public sealed record WebhookDeliveryDetailDto(WebhookDeliveryDto Delivery, string Payload);

public sealed record WebhookDeliveryQuery(Guid? EndpointId, WebhookDeliveryStatus? Status, string? EventType, DateTimeOffset? From, DateTimeOffset? To);

/// <summary>
/// Staff management of outgoing webhooks (FR-AF05): endpoints (https only, SSRF-checked, secret shown once and stored protected), a test ping, the delivery log with
/// filters, and manual redelivery. Delivery itself is <see cref="WebhookDispatcher"/>.
/// </summary>
public sealed class WebhookService(IApplicationDbContext db, ISecretProtector protector, IWebhookTargetGuard guard, WebhookDispatcher dispatcher, ICurrentUser currentUser, TimeProvider clock)
{
    public const int MaxEndpoints = 20;

    private static Error NotFound() => Error.NotFound(ErrorCodes.PublicApi.WebhookNotFound, "Webhook not found.");

    private static Error Invalid(string code, string field, string message) =>
        new(code, message, ErrorType.Validation, new Dictionary<string, string[]> { [field] = [message] });

    public async Task<Result<PagedResult<WebhookEndpointDto>>> ListAsync(PageRequest request, CancellationToken ct)
    {
        var page = request.Normalized;
        var endpoints = db.WebhookEndpoints.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(page.Search))
        {
            var term = page.Search.Trim();
            endpoints = endpoints.Where(e => e.Name.Contains(term) || e.Url.Contains(term));
        }

        var total = await endpoints.CountAsync(ct);
        var rows = await endpoints.OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(ct);
        return PagedResult<WebhookEndpointDto>.Create(await ToDtosAsync(rows, ct), total, page);
    }

    public async Task<Result<WebhookEndpointDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var endpoint = await db.WebhookEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        return endpoint is null ? NotFound() : (await ToDtosAsync([endpoint], ct))[0];
    }

    public static IReadOnlyList<string> EventTypes() => WebhookEvents.All;

    public async Task<Result<WebhookEndpointCreatedDto>> CreateAsync(WebhookEndpointRequest request, CancellationToken ct)
    {
        if (await ValidateAsync(request, ct) is { } error)
        {
            return error;
        }

        if (await db.WebhookEndpoints.CountAsync(ct) >= MaxEndpoints)
        {
            return Invalid(ErrorCodes.General.Validation, "url", $"At most {MaxEndpoints} webhooks can be set up.");
        }

        var secret = WebhookDelivering.NewSecret();
        var endpoint = new WebhookEndpoint(request.Name!, request.Url!.Trim(), protector.Protect(secret), request.Events!);
        if (request.IsEnabled == false)
        {
            endpoint.SetEnabled(false);
        }

        db.WebhookEndpoints.Add(endpoint);
        await db.SaveChangesAsync(ct);
        return new WebhookEndpointCreatedDto((await ToDtosAsync([endpoint], ct))[0], secret);
    }

    public async Task<Result<WebhookEndpointDto>> UpdateAsync(Guid id, WebhookEndpointRequest request, CancellationToken ct)
    {
        var endpoint = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (endpoint is null)
        {
            return NotFound();
        }

        if (await ValidateAsync(request, ct) is { } error)
        {
            return error;
        }

        endpoint.Update(request.Name!, request.Url!.Trim(), request.Events!, request.IsEnabled ?? endpoint.IsEnabled);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([endpoint], ct))[0];
    }

    public async Task<Result<WebhookEndpointDto>> SetEnabledAsync(Guid id, bool enabled, CancellationToken ct)
    {
        var endpoint = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (endpoint is null)
        {
            return NotFound();
        }

        endpoint.SetEnabled(enabled);
        await db.SaveChangesAsync(ct);
        return (await ToDtosAsync([endpoint], ct))[0];
    }

    public async Task<Result<WebhookEndpointCreatedDto>> RotateSecretAsync(Guid id, CancellationToken ct)
    {
        var endpoint = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (endpoint is null)
        {
            return NotFound();
        }

        var secret = WebhookDelivering.NewSecret();
        var now = clock.GetUtcNow();
        endpoint.RotateSecret(protector.Protect(secret), now);
        Audit(endpoint.Id, AuditAction.Updated, new { @event = "secret-rotated" }, now);
        await db.SaveChangesAsync(ct);
        return new WebhookEndpointCreatedDto((await ToDtosAsync([endpoint], ct))[0], secret);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var endpoint = await db.WebhookEndpoints.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (endpoint is null)
        {
            return NotFound();
        }

        // The delivery log of a removed endpoint goes with it (its payloads carry no personal data, and the audit trail keeps who removed the endpoint).
        await db.WebhookDeliveries.Where(d => d.EndpointId == id).ExecuteDeleteAsync(ct);
        db.WebhookEndpoints.Remove(endpoint);
        await db.SaveChangesAsync(ct);
        return Result.Success();
    }

    /// <summary>Sends a <c>ping</c> event right now and returns what happened. A failed ping is a normal answer (the delivery shows the reason), not an error.</summary>
    public async Task<Result<WebhookDeliveryDto>> PingAsync(Guid id, CancellationToken ct)
    {
        var endpoint = await db.WebhookEndpoints.AsNoTracking().FirstOrDefaultAsync(e => e.Id == id, ct);
        if (endpoint is null)
        {
            return NotFound();
        }

        var now = clock.GetUtcNow();
        var eventId = Guid.CreateVersion7();
        var body = WebhookFanout.Serialize(new WebhookEnvelope(eventId, WebhookEvents.Ping, now, new PingData("This is a test event from Juvo BD.")));
        var delivery = new WebhookDelivery(eventId, WebhookEvents.Ping, id, body, WebhookDelivering.HashPayload(body), now);
        db.WebhookDeliveries.Add(delivery);
        Audit(id, AuditAction.Updated, new { @event = "ping", deliveryId = delivery.Id }, now);
        await db.SaveChangesAsync(ct);

        // A ping is a single try: whatever the outcome, no retry is scheduled (the staff member is watching).
        await dispatcher.DeliverNowAsync(delivery.Id, singleTry: true, ct);
        var saved = await db.WebhookDeliveries.AsNoTracking().FirstAsync(d => d.Id == delivery.Id, ct);
        return ToDto(saved, endpoint.Name);
    }

    public async Task<Result<PagedResult<WebhookDeliveryDto>>> DeliveriesAsync(PageRequest request, WebhookDeliveryQuery query, CancellationToken ct)
    {
        var page = request.Normalized;
        var rows = db.WebhookDeliveries.AsNoTracking().AsQueryable();
        if (query.EndpointId is { } endpointId)
        {
            rows = rows.Where(d => d.EndpointId == endpointId);
        }

        if (query.Status is { } status)
        {
            rows = rows.Where(d => d.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            var type = query.EventType.Trim();
            rows = rows.Where(d => d.EventType == type);
        }

        if (query.From is { } from)
        {
            rows = rows.Where(d => d.CreatedAt >= from);
        }

        if (query.To is { } to)
        {
            rows = rows.Where(d => d.CreatedAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(page.Search) && Guid.TryParse(page.Search.Trim(), out var eventId))
        {
            rows = rows.Where(d => d.EventId == eventId || d.Id == eventId);
        }

        var total = await rows.CountAsync(ct);
        var items = await rows.OrderByDescending(d => d.CreatedAt).ThenBy(d => d.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(ct);
        var ids = items.Select(d => d.EndpointId).Distinct().ToList();
        var names = await db.WebhookEndpoints.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Name, ct);
        return PagedResult<WebhookDeliveryDto>.Create([.. items.Select(d => ToDto(d, names.GetValueOrDefault(d.EndpointId)))], total, page);
    }

    public async Task<Result<WebhookDeliveryDetailDto>> DeliveryAsync(Guid id, CancellationToken ct)
    {
        var delivery = await db.WebhookDeliveries.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, ct);
        if (delivery is null)
        {
            return Error.NotFound(ErrorCodes.PublicApi.DeliveryNotFound, "Delivery not found.");
        }

        var name = await db.WebhookEndpoints.AsNoTracking().Where(e => e.Id == delivery.EndpointId).Select(e => e.Name).FirstOrDefaultAsync(ct);
        return new WebhookDeliveryDetailDto(ToDto(delivery, name), delivery.Payload);
    }

    /// <summary>Sends the same event again (same event id, same body) to the same endpoint. Allowed for Failed and Succeeded deliveries; a pending one is still being worked on.</summary>
    public async Task<Result<WebhookDeliveryDto>> RedeliverAsync(Guid id, CancellationToken ct)
    {
        var delivery = await db.WebhookDeliveries.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (delivery is null)
        {
            return Error.NotFound(ErrorCodes.PublicApi.DeliveryNotFound, "Delivery not found.");
        }

        if (delivery.Status == WebhookDeliveryStatus.Pending)
        {
            return Error.Conflict(ErrorCodes.PublicApi.NotRedeliverable, "This delivery is still waiting for its next attempt.");
        }

        var now = clock.GetUtcNow();
        delivery.Redeliver(now);
        Audit(delivery.EndpointId, AuditAction.Updated, new { @event = "redeliver", deliveryId = delivery.Id, eventType = delivery.EventType }, now);
        await db.SaveChangesAsync(ct);
        var name = await db.WebhookEndpoints.AsNoTracking().Where(e => e.Id == delivery.EndpointId).Select(e => e.Name).FirstOrDefaultAsync(ct);
        return ToDto(delivery, name);
    }

    private async Task<Error?> ValidateAsync(WebhookEndpointRequest r, CancellationToken ct)
    {
        var name = r.Name?.Trim() ?? string.Empty;
        if (name.Length is < 2 or > 80)
        {
            return Invalid(ErrorCodes.General.Validation, "name", "Give the webhook a name of 2 to 80 characters.");
        }

        if (r.Events is not { Count: > 0 })
        {
            return Invalid(ErrorCodes.PublicApi.InvalidEvent, "events", "Choose at least one event.");
        }

        if (r.Events.FirstOrDefault(e => !WebhookEvents.IsKnown(e)) is { } unknown)
        {
            return Invalid(ErrorCodes.PublicApi.InvalidEvent, "events", $"Unknown event '{unknown}'.");
        }

        var url = r.Url?.Trim() ?? string.Empty;
        if (url.Length is 0 or > 500)
        {
            return Invalid(ErrorCodes.PublicApi.UrlRefused, "url", "Give the https address that should receive the events (up to 500 characters).");
        }

        if (await guard.CheckUrlAsync(url, ct) is { } refusal)
        {
            return Invalid(ErrorCodes.PublicApi.UrlRefused, "url", refusal);
        }

        return null;
    }

    private void Audit(Guid endpointId, AuditAction action, object detail, DateTimeOffset now) =>
        db.AuditLogs.Add(new AuditLog(currentUser.UserId, currentUser.BranchId, nameof(WebhookEndpoint), endpointId.ToString(), action, null,
            JsonSerializer.Serialize(detail), currentUser.CorrelationId, currentUser.IpAddress, now));

    private static WebhookDeliveryDto ToDto(WebhookDelivery d, string? endpointName) => new(
        d.Id, d.EventId, d.EventType, d.EndpointId, endpointName, d.Attempt, WebhookDelivering.MaxAttempts, d.Status, d.HttpStatus, d.Error, d.NextAttemptAt, d.LastAttemptAt, d.DeliveredAt,
        d.PayloadHash, d.Redeliveries, d.CreatedAt);

    private async Task<IReadOnlyList<WebhookEndpointDto>> ToDtosAsync(IReadOnlyList<WebhookEndpoint> endpoints, CancellationToken ct)
    {
        var ids = endpoints.Select(e => e.Id).ToList();
        var failed = await db.WebhookDeliveries.AsNoTracking().Where(d => ids.Contains(d.EndpointId) && d.Status == WebhookDeliveryStatus.Failed)
            .GroupBy(d => d.EndpointId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        return [.. endpoints.Select(e => new WebhookEndpointDto(e.Id, e.Name, e.Url, e.EventList, e.IsEnabled, e.SecretRotatedAt, e.CreatedAt, failed.GetValueOrDefault(e.Id)))];
    }
}
