using Juvo.Domain.Common;

namespace Juvo.Domain.Notifications;

public enum NotificationChannel
{
    Sms = 1,
    Email = 2,
    InApp = 3,
}

public enum NotificationStatus
{
    Sent = 1,
    Failed = 2,
}

/// <summary>
/// Record of every message the system sends (FR-K05). The sandbox senders write here instead of calling a provider,
/// so the whole platform runs end to end without external keys. Step 9 adds templates, retries and the admin viewer.
/// </summary>
public sealed class NotificationLog : Entity
{
    private NotificationLog()
    {
    }

    public NotificationLog(NotificationChannel channel, string recipient, string template, string? subject, string body,
        NotificationStatus status, string? providerResponse, string? correlationId, DateTimeOffset sentAt)
    {
        Channel = channel;
        Recipient = recipient;
        Template = template;
        Subject = subject;
        Body = body;
        Status = status;
        ProviderResponse = providerResponse;
        CorrelationId = correlationId;
        SentAt = sentAt;
    }

    public NotificationChannel Channel { get; private set; }

    public string Recipient { get; private set; } = string.Empty;

    /// <summary>Template key, for example <c>auth.otp</c> or <c>auth.password-reset</c>.</summary>
    public string Template { get; private set; } = string.Empty;

    public string? Subject { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public NotificationStatus Status { get; private set; }

    public string? ProviderResponse { get; private set; }

    public string? CorrelationId { get; private set; }

    public DateTimeOffset SentAt { get; private set; }
}
