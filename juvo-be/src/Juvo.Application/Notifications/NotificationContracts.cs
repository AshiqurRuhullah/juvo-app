using Juvo.Domain.Notifications;

namespace Juvo.Application.Notifications;

/// <summary>A message ready to hand to a provider. The log row already exists (<see cref="LogId"/>).</summary>
public sealed record OutboundMessage(Guid LogId, NotificationChannel Channel, string Recipient, string? Subject, string Body, string Template);

public sealed record SendOutcome(bool Success, string? ProviderResponse, string? Error)
{
    public static SendOutcome Ok(string? response = null) => new(true, response, null);

    public static SendOutcome Fail(string error) => new(false, null, error);
}

/// <summary>
/// Delivers one message on one channel (SMS, email, WhatsApp). The sandbox senders are on by default and only report success: the log row is the record. A real provider
/// (Step 14) replaces the sandbox for its channel. When several senders claim a channel the last registered one is used.
/// </summary>
public interface INotificationSender
{
    NotificationChannel Channel { get; }

    Task<SendOutcome> SendAsync(OutboundMessage message, CancellationToken cancellationToken);
}

/// <summary>Retry settings for template-driven messages. Section: <c>Notifications:Delivery</c>.</summary>
public sealed class NotificationDeliveryOptions
{
    public const string SectionName = "Notifications:Delivery";

    /// <summary>Attempts before a message becomes a dead letter that staff can resend. The outbox waits longer between attempts each time.</summary>
    public int MaxAttempts { get; set; } = 5;
}

/// <summary>Asks for a template to be sent to a customer. The dedupe key makes the send idempotent (use the outbox message id plus the template code).</summary>
public sealed record NotifyRequest(string TemplateCode, string Mobile, string? Email, Guid? CustomerId, IReadOnlyDictionary<string, string> Variables, string DedupeKey);

public sealed record NotificationDeliverMessage(Guid LogId);

public sealed record RenderedTemplate(string? Subject, string Body, string Language, int Version);

// ---- Staff ---------------------------------------------------------------------------------------------------

public sealed record TemplateDto(
    string Code, string Description, NotificationChannel Channel, string Language, string? Subject, string Body, NotificationCategory Category, int Version, bool IsActive, bool IsCustom,
    IReadOnlyList<string> Variables, string? EditedBy, DateTimeOffset? EditedAt);

public sealed record UpdateTemplateRequest(string? Subject, string? Body, bool IsActive);

public sealed record TemplateVersionDto(int Version, string? Subject, string Body, string? EditedBy, DateTimeOffset EditedAt, bool IsCurrent);

public sealed record PreviewTemplateRequest(string? Code, NotificationChannel Channel, string? Language, string? Subject, string? Body, IReadOnlyDictionary<string, string>? Variables);

public sealed record PreviewDto(string? Subject, string Body, IReadOnlyList<string> UnknownPlaceholders, IReadOnlyList<string> MissingVariables, int Characters);

public sealed record NotificationLogListQuery(NotificationStatus? Status, NotificationChannel? Channel, string? Template, string? Recipient, DateTimeOffset? From, DateTimeOffset? To);

public sealed record NotificationLogDto(
    Guid Id, NotificationChannel Channel, string Recipient, string Template, string? Subject, string Body, NotificationStatus Status, string? Language, int TemplateVersion, NotificationCategory Category,
    int Attempts, string? LastError, string? ProviderResponse, DateTimeOffset QueuedAt, DateTimeOffset SentAt);

public sealed record ChannelSettingsDto(bool SmsEnabled, bool EmailEnabled, bool WhatsAppEnabled, string DefaultLanguage);

public sealed record UpdateChannelSettingsRequest(bool? SmsEnabled, bool? EmailEnabled, bool? WhatsAppEnabled, string? DefaultLanguage);

public sealed record SetLanguageRequest(string? Language);
