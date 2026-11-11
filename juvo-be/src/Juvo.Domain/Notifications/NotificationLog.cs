using Juvo.Domain.Common;

namespace Juvo.Domain.Notifications;

/// <summary>Stored as an integer; append new values at the end.</summary>
public enum NotificationChannel
{
    Sms = 1,
    Email = 2,
    InApp = 3,
    WhatsApp = 4,
}

public enum NotificationStatus
{
    Sent = 1,

    /// <summary>The last attempt failed; the outbox will try again with backoff.</summary>
    Failed = 2,

    /// <summary>Waiting for the outbox to deliver it.</summary>
    Pending = 3,

    /// <summary>Every attempt failed. Staff can resend it from the log.</summary>
    DeadLetter = 4,

    /// <summary>Not sent on purpose (for example a marketing message without consent, or a switched-off channel). The reason is in <c>LastError</c>.</summary>
    Skipped = 5,
}

/// <summary>Transactional messages (order, payment, OTP) are always allowed; marketing ones need the customer's consent (FR-K, FR-E07).</summary>
public enum NotificationCategory
{
    Transactional = 1,
    Marketing = 2,
}

/// <summary>
/// Record of every message the system sends (FR-K05). The sandbox senders write here instead of calling a provider, so the whole platform runs end to end without
/// external keys. Template-driven messages start Pending, are delivered by the outbox (retry with backoff) and end Sent or DeadLetter.
/// </summary>
public sealed class NotificationLog : Entity, INotAudited
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
        QueuedAt = sentAt;
        Attempts = status == NotificationStatus.Sent ? 1 : 0;
    }

    public NotificationChannel Channel { get; private set; }

    public string Recipient { get; private set; } = string.Empty;

    /// <summary>Template key, for example <c>auth.otp</c> or <c>order.confirmed</c>.</summary>
    public string Template { get; private set; } = string.Empty;

    public string? Subject { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public NotificationStatus Status { get; private set; }

    public string? ProviderResponse { get; private set; }

    public string? CorrelationId { get; private set; }

    /// <summary>When it was sent (or last attempted).</summary>
    public DateTimeOffset SentAt { get; private set; }

    public DateTimeOffset QueuedAt { get; private set; }

    /// <summary><c>en</c> or <c>bn</c>: the language of the template that was used.</summary>
    public string? Language { get; private set; }

    public int TemplateVersion { get; private set; }

    public NotificationCategory Category { get; private set; } = NotificationCategory.Transactional;

    public Guid? CustomerId { get; private set; }

    /// <summary>Makes a send idempotent: the same key and channel is queued once (the outbox may deliver an event more than once).</summary>
    public string? DedupeKey { get; private set; }

    public int Attempts { get; private set; }

    public string? LastError { get; private set; }

    /// <summary>A message built from a template, waiting for delivery.</summary>
    public static NotificationLog Queue(
        NotificationChannel channel, string recipient, string template, string? subject, string body, string language, int templateVersion, NotificationCategory category, Guid? customerId,
        string? dedupeKey, string? correlationId, DateTimeOffset now) =>
        new(channel, recipient, template, subject, body, NotificationStatus.Pending, null, correlationId, now)
        {
            Language = language,
            TemplateVersion = templateVersion,
            Category = category,
            CustomerId = customerId,
            DedupeKey = dedupeKey,
        };

    /// <summary>A message that was not sent on purpose; kept so the reason can be seen.</summary>
    public static NotificationLog Skip(
        NotificationChannel channel, string recipient, string template, string language, NotificationCategory category, Guid? customerId, string reason, string? dedupeKey, DateTimeOffset now) =>
        new(channel, recipient, template, null, string.Empty, NotificationStatus.Skipped, null, null, now)
        {
            Language = language,
            Category = category,
            CustomerId = customerId,
            DedupeKey = dedupeKey,
            LastError = reason,
        };

    public void MarkSent(string? providerResponse, DateTimeOffset now)
    {
        Status = NotificationStatus.Sent;
        ProviderResponse = providerResponse;
        Attempts++;
        LastError = null;
        SentAt = now;
    }

    /// <summary>One failed attempt. The status is Failed while retries remain and DeadLetter on the last one.</summary>
    public void MarkAttemptFailed(string error, bool final, DateTimeOffset now)
    {
        Attempts++;
        LastError = error.Length > 500 ? error[..500] : error;
        Status = final ? NotificationStatus.DeadLetter : NotificationStatus.Failed;
        SentAt = now;
    }

    /// <summary>Staff resend a dead-lettered (or failed) message: it goes back to Pending with a fresh attempt count.</summary>
    public void Requeue(DateTimeOffset now)
    {
        Status = NotificationStatus.Pending;
        Attempts = 0;
        LastError = null;
        QueuedAt = now;
    }
}

/// <summary>
/// A message template (module K) for one code, channel and language. Editing creates a new version; the previous text is kept in <see cref="NotificationTemplateVersion"/>.
/// Placeholders look like <c>{{OrderNumber}}</c>. Templates not edited by staff come from the built-in defaults in code.
/// </summary>
public sealed class NotificationTemplate : Entity
{
    private NotificationTemplate()
    {
    }

    public NotificationTemplate(string code, NotificationChannel channel, string language, string? subject, string body, NotificationCategory category, bool isActive, Guid? by, string? byName, DateTimeOffset now)
    {
        Code = code;
        Channel = channel;
        Language = language;
        Subject = subject;
        Body = body;
        Category = category;
        Version = 1;
        IsActive = isActive;
        EditedBy = by;
        EditedByName = byName;
        EditedAt = now;
    }

    public string Code { get; private set; } = string.Empty;

    public NotificationChannel Channel { get; private set; }

    public string Language { get; private set; } = "en";

    public string? Subject { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public NotificationCategory Category { get; private set; }

    public int Version { get; private set; }

    public bool IsActive { get; private set; }

    public Guid? EditedBy { get; private set; }

    public string? EditedByName { get; private set; }

    public DateTimeOffset EditedAt { get; private set; }

    /// <summary>Saves a new text as the next version and returns the snapshot of the text it replaces.</summary>
    public NotificationTemplateVersion Edit(string? subject, string body, bool isActive, Guid? by, string? byName, DateTimeOffset now)
    {
        var previous = new NotificationTemplateVersion(Id, Version, Subject, Body, EditedBy, EditedByName, EditedAt);
        Subject = subject;
        Body = body;
        IsActive = isActive;
        Version++;
        EditedBy = by;
        EditedByName = byName;
        EditedAt = now;
        return previous;
    }
}

/// <summary>The text of an earlier template version. Append-only.</summary>
public sealed class NotificationTemplateVersion : Entity, IAppendOnly, INotAudited
{
    private NotificationTemplateVersion()
    {
    }

    /// <summary>The built-in text a template had before staff first edited it (version 0).</summary>
    public static NotificationTemplateVersion Original(Guid templateId, string? subject, string body, DateTimeOffset at) =>
        new(templateId, 0, subject, body, null, "Built-in text", at);

    internal NotificationTemplateVersion(Guid templateId, int version, string? subject, string body, Guid? by, string? byName, DateTimeOffset at)
    {
        TemplateId = templateId;
        Version = version;
        Subject = subject;
        Body = body;
        EditedBy = by;
        EditedByName = byName;
        EditedAt = at;
    }

    public Guid TemplateId { get; private set; }

    public int Version { get; private set; }

    public string? Subject { get; private set; }

    public string Body { get; private set; } = string.Empty;

    public Guid? EditedBy { get; private set; }

    public string? EditedByName { get; private set; }

    public DateTimeOffset EditedAt { get; private set; }
}
