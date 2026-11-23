using Juvo.Domain.Common;

namespace Juvo.Domain.Integrations;

/// <summary>A subscriber of outgoing webhooks (FR-AF05). The signing secret is stored protected (data protection), never in clear text.</summary>
public sealed class WebhookEndpoint : Entity
{
    private WebhookEndpoint()
    {
    }

    public WebhookEndpoint(string name, string url, string secretProtected, IEnumerable<string> events)
    {
        Name = name.Trim();
        Url = url;
        SecretProtected = secretProtected;
        Events = Join(events);
    }

    public string Name { get; private set; } = string.Empty;

    public string Url { get; private set; } = string.Empty;

    [AuditIgnore]
    public string SecretProtected { get; private set; } = string.Empty;

    /// <summary>Comma separated event types (see the event catalogue).</summary>
    public string Events { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; } = true;

    public DateTimeOffset? SecretRotatedAt { get; private set; }

    public IReadOnlyList<string> EventList => Events.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public bool Subscribes(string eventType) => EventList.Contains(eventType, StringComparer.Ordinal);

    public void Update(string name, string url, IEnumerable<string> events, bool enabled)
    {
        Name = name.Trim();
        Url = url;
        Events = Join(events);
        IsEnabled = enabled;
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public void RotateSecret(string secretProtected, DateTimeOffset now)
    {
        SecretProtected = secretProtected;
        SecretRotatedAt = now;
    }

    private static string Join(IEnumerable<string> events) => string.Join(',', events.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
}

public enum WebhookDeliveryStatus
{
    /// <summary>Waiting for its first or next attempt.</summary>
    Pending = 0,
    Succeeded = 1,

    /// <summary>All attempts used. Stays visible; staff can redeliver.</summary>
    Failed = 2,
}

/// <summary>
/// One event for one endpoint. The payload is kept so a retry or a manual redelivery sends exactly the same body (it carries ids and public-API fields, no personal data).
/// (EventId, EndpointId) is unique, so the fan-out from the outbox is idempotent.
/// </summary>
public sealed class WebhookDelivery : Entity, INotAudited
{
    private WebhookDelivery()
    {
    }

    public WebhookDelivery(Guid eventId, string eventType, Guid endpointId, string payload, string payloadHash, DateTimeOffset now)
    {
        EventId = eventId;
        EventType = eventType;
        EndpointId = endpointId;
        Payload = payload;
        PayloadHash = payloadHash;
        NextAttemptAt = now;
    }

    public Guid EventId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public Guid EndpointId { get; private set; }

    public string Payload { get; private set; } = string.Empty;

    /// <summary>SHA-256 (hex) of the body.</summary>
    public string PayloadHash { get; private set; } = string.Empty;

    /// <summary>Attempts made so far.</summary>
    public int Attempt { get; private set; }

    public WebhookDeliveryStatus Status { get; private set; } = WebhookDeliveryStatus.Pending;

    public int? HttpStatus { get; private set; }

    /// <summary>A scrubbed one-line reason (no URL, no response body).</summary>
    public string? Error { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public int Redeliveries { get; private set; }

    public void RecordSuccess(int httpStatus, DateTimeOffset now)
    {
        Attempt++;
        Status = WebhookDeliveryStatus.Succeeded;
        HttpStatus = httpStatus;
        Error = null;
        LastAttemptAt = now;
        DeliveredAt = now;
    }

    /// <summary>Records a failed attempt: schedules the retry after <paramref name="retryIn"/>, or marks the delivery Failed when it is null.</summary>
    public void RecordFailure(int? httpStatus, string error, TimeSpan? retryIn, DateTimeOffset now)
    {
        Attempt++;
        HttpStatus = httpStatus;
        Error = error.Length > 300 ? error[..300] : error;
        LastAttemptAt = now;
        if (retryIn is { } delay)
        {
            Status = WebhookDeliveryStatus.Pending;
            NextAttemptAt = now + delay;
        }
        else
        {
            Status = WebhookDeliveryStatus.Failed;
        }
    }

    /// <summary>Manual redelivery: the attempt count starts again and the delivery is due now.</summary>
    public void Redeliver(DateTimeOffset now)
    {
        Status = WebhookDeliveryStatus.Pending;
        Attempt = 0;
        Redeliveries++;
        NextAttemptAt = now;
        DeliveredAt = null;
    }
}
