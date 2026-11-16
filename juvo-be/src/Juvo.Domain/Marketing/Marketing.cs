using Juvo.Domain.Common;
using Juvo.Domain.Notifications;

namespace Juvo.Domain.Marketing;

/// <summary>A saved audience (FR-AC01). The rules are a typed definition stored as JSON; they are validated and translated to SQL, never evaluated as text.</summary>
public sealed class CustomerSegment : Entity
{
    private CustomerSegment()
    {
    }

    public CustomerSegment(string name, string? description, string definitionJson, bool isBuiltIn, string? builtInCode)
    {
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        DefinitionJson = definitionJson;
        IsBuiltIn = isBuiltIn;
        BuiltInCode = builtInCode;
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    /// <summary>The <c>SegmentDefinition</c> as JSON.</summary>
    public string DefinitionJson { get; private set; } = "{}";

    /// <summary>A segment that ships with the platform. Staff copy it to change it.</summary>
    public bool IsBuiltIn { get; private set; }

    public string? BuiltInCode { get; private set; }

    public void Update(string name, string? description, string definitionJson)
    {
        Name = name.Trim();
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        DefinitionJson = definitionJson;
    }
}

public enum CampaignStatus
{
    Draft = 1,
    Scheduled = 2,
    Sending = 3,
    Sent = 4,
    Cancelled = 5,
}

/// <summary>A marketing message to one segment on one channel (FR-AC02). Messages are written per language; a customer gets the text of their language.</summary>
public sealed class Campaign : Entity
{
    private Campaign()
    {
    }

    public Campaign(string name, Guid segmentId, NotificationChannel channel, string? subjectEn, string? subjectBn, string bodyEn, string? bodyBn, Guid? createdById, string? createdByName)
    {
        Name = name.Trim();
        SegmentId = segmentId;
        Channel = channel;
        SubjectEn = Clean(subjectEn);
        SubjectBn = Clean(subjectBn);
        BodyEn = bodyEn.Trim();
        BodyBn = Clean(bodyBn);
        CreatedById = createdById;
        CreatedByName = createdByName;
    }

    public string Name { get; private set; } = string.Empty;

    public Guid SegmentId { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public CampaignStatus Status { get; private set; } = CampaignStatus.Draft;

    public string? SubjectEn { get; private set; }

    public string? SubjectBn { get; private set; }

    public string BodyEn { get; private set; } = string.Empty;

    public string? BodyBn { get; private set; }

    public DateTimeOffset? ScheduledAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    /// <summary>True once every segment member has a recipient row (one row per customer, written before anything is sent).</summary>
    public bool RecipientsReady { get; private set; }

    public Guid? CreatedById { get; private set; }

    public string? CreatedByName { get; private set; }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public bool IsEditable => Status is CampaignStatus.Draft or CampaignStatus.Scheduled;

    public void Update(string name, Guid segmentId, NotificationChannel channel, string? subjectEn, string? subjectBn, string bodyEn, string? bodyBn)
    {
        Name = name.Trim();
        SegmentId = segmentId;
        Channel = channel;
        SubjectEn = Clean(subjectEn);
        SubjectBn = Clean(subjectBn);
        BodyEn = bodyEn.Trim();
        BodyBn = Clean(bodyBn);
    }

    public void Schedule(DateTimeOffset at)
    {
        ScheduledAt = at;
        Status = CampaignStatus.Scheduled;
    }

    public void Unschedule()
    {
        ScheduledAt = null;
        Status = CampaignStatus.Draft;
    }

    public void StartSending(DateTimeOffset now)
    {
        Status = CampaignStatus.Sending;
        StartedAt ??= now;
    }

    public void MarkRecipientsReady() => RecipientsReady = true;

    public void Complete(DateTimeOffset now)
    {
        Status = CampaignStatus.Sent;
        CompletedAt = now;
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = CampaignStatus.Cancelled;
        CancelledAt = now;
    }
}

public enum RecipientStatus
{
    /// <summary>Waiting for the send worker.</summary>
    Pending = 1,

    /// <summary>Handed to the notification log (and the outbox).</summary>
    Queued = 2,

    /// <summary>Not sent on purpose; <see cref="CampaignRecipient.SkipReason"/> says why.</summary>
    Skipped = 3,
}

/// <summary>Why a campaign did not send to a segment member. Stored as text.</summary>
public static class SkipReasons
{
    public const string NoConsent = "NoConsent";
    public const string NoContact = "NoContact";
    public const string FrequencyCap = "FrequencyCap";
    public const string ChannelOff = "ChannelOff";
    public const string Cancelled = "Cancelled";
    public const string Inactive = "Inactive";
}

/// <summary>One customer of one campaign. Written before sending, so a crashed run resumes where it stopped and never sends twice (unique per campaign and customer).</summary>
public sealed class CampaignRecipient : Entity, INotAudited
{
    private CampaignRecipient()
    {
    }

    public CampaignRecipient(Guid campaignId, Guid customerId, string? contact)
    {
        CampaignId = campaignId;
        CustomerId = customerId;
        Contact = contact;
    }

    public Guid CampaignId { get; private set; }

    public Guid CustomerId { get; private set; }

    /// <summary>The mobile number or email address the message goes to.</summary>
    public string? Contact { get; private set; }

    public RecipientStatus Status { get; private set; } = RecipientStatus.Pending;

    public string? SkipReason { get; private set; }

    public Guid? NotificationLogId { get; private set; }

    public DateTimeOffset? ProcessedAt { get; private set; }

    public void MarkQueued(Guid logId, DateTimeOffset now)
    {
        Status = RecipientStatus.Queued;
        NotificationLogId = logId;
        ProcessedAt = now;
    }

    public void MarkSkipped(string reason, Guid? logId, DateTimeOffset now)
    {
        Status = RecipientStatus.Skipped;
        SkipReason = reason;
        NotificationLogId = logId;
        ProcessedAt = now;
    }
}

public enum AutomationKind
{
    AbandonedCart = 1,
    BackInStock = 2,
    ReviewRequest = 3,
    WinBack = 4,
}

/// <summary>
/// One automation per kind (FR-AC03). Off by default. <see cref="DelayMinutes"/> is the wait between the trigger and the message (cart idle time, time after delivery,
/// days without activity); <see cref="WindowDays"/> stops a freshly switched-on automation from messaging events that are too old; <see cref="CapDays"/> is the per-customer frequency cap.
/// </summary>
public sealed class Automation : Entity
{
    private Automation()
    {
    }

    public Automation(AutomationKind kind, NotificationChannel channel, int delayMinutes, int windowDays, int capDays, string? subjectEn, string? subjectBn, string bodyEn, string bodyBn)
    {
        Kind = kind;
        Channel = channel;
        DelayMinutes = delayMinutes;
        WindowDays = windowDays;
        CapDays = capDays;
        SubjectEn = subjectEn;
        SubjectBn = subjectBn;
        BodyEn = bodyEn;
        BodyBn = bodyBn;
    }

    public AutomationKind Kind { get; private set; }

    public bool IsEnabled { get; private set; }

    public NotificationChannel Channel { get; private set; }

    public int DelayMinutes { get; private set; }

    public int WindowDays { get; private set; }

    public int CapDays { get; private set; }

    public string? SubjectEn { get; private set; }

    public string? SubjectBn { get; private set; }

    public string BodyEn { get; private set; } = string.Empty;

    public string BodyBn { get; private set; } = string.Empty;

    public DateTimeOffset? LastRunAt { get; private set; }

    public void Update(bool isEnabled, NotificationChannel channel, int delayMinutes, int windowDays, int capDays, string? subjectEn, string? subjectBn, string bodyEn, string bodyBn)
    {
        IsEnabled = isEnabled;
        Channel = channel;
        DelayMinutes = delayMinutes;
        WindowDays = windowDays;
        CapDays = capDays;
        SubjectEn = subjectEn;
        SubjectBn = subjectBn;
        BodyEn = bodyEn;
        BodyBn = bodyBn;
    }

    public void RecordRun(DateTimeOffset now) => LastRunAt = now;
}

/// <summary>A request "tell me when this is back" for a sold-out variant (FR-AC03). One pending row per variant and mobile number; notified once.</summary>
public sealed class BackInStockSubscription : Entity, INotAudited
{
    private BackInStockSubscription()
    {
    }

    public BackInStockSubscription(Guid productId, Guid variantId, string mobile, string? email, Guid? customerId)
    {
        ProductId = productId;
        VariantId = variantId;
        Mobile = mobile;
        Email = string.IsNullOrWhiteSpace(email) ? null : email.Trim();
        CustomerId = customerId;
    }

    public Guid ProductId { get; private set; }

    public Guid VariantId { get; private set; }

    public string Mobile { get; private set; } = string.Empty;

    public string? Email { get; private set; }

    public Guid? CustomerId { get; private set; }

    public DateTimeOffset? NotifiedAt { get; private set; }

    /// <summary>What happened when it was handled: <c>sent</c> or <c>skipped:&lt;reason&gt;</c>. Null while waiting.</summary>
    public string? Outcome { get; private set; }

    public void SetEmail(string? email)
    {
        if (!string.IsNullOrWhiteSpace(email))
        {
            Email = email.Trim();
        }
    }

    public void Complete(string outcome, DateTimeOffset now)
    {
        NotifiedAt = now;
        Outcome = outcome;
    }
}
