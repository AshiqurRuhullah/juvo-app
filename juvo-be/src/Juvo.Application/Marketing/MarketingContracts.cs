using Juvo.Domain.Marketing;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;

namespace Juvo.Application.Marketing;

// ---- Segments ---------------------------------------------------------------------------------------------

/// <summary>
/// One condition of a segment. <see cref="Type"/> picks the kind and which other fields are used (the rest must be left out):
/// <c>newCustomers</c> (days), <c>repeatBuyers</c> (minOrders, optional periodDays), <c>highSpenders</c> (minSpend, optional periodDays: lifetime when missing),
/// <c>inactive</c> (days), <c>location</c> (level division|district, ids), <c>consent</c> (channel sms|email, optional granted, default true),
/// <c>lastOrderStatus</c> (status), <c>wishlist</c> (optional productIds), <c>cart</c> (optional idleHours).
/// </summary>
public sealed record SegmentRule
{
    public string Type { get; init; } = string.Empty;

    public int? Days { get; init; }

    public int? MinOrders { get; init; }

    public decimal? MinSpend { get; init; }

    public int? PeriodDays { get; init; }

    public string? Level { get; init; }

    public List<Guid>? Ids { get; init; }

    public string? Channel { get; init; }

    public bool? Granted { get; init; }

    public OrderStatus? Status { get; init; }

    public List<Guid>? ProductIds { get; init; }

    public int? IdleHours { get; init; }
}

/// <summary>Rules joined by <c>and</c> (every rule) or <c>or</c> (any rule).</summary>
public sealed record SegmentDefinition
{
    public string Combinator { get; init; } = "and";

    public List<SegmentRule> Rules { get; init; } = [];
}

public sealed record SegmentDto(Guid Id, string Name, string? Description, bool IsBuiltIn, string? BuiltInCode, SegmentDefinition Definition, DateTimeOffset CreatedAt, DateTimeOffset? UpdatedAt);

public sealed record SaveSegmentRequest(string? Name, string? Description, SegmentDefinition? Definition);

public sealed record ReachableDto(int Sms, int Email);

public sealed record SegmentCountDto(int Total, ReachableDto Reachable);

public sealed record SegmentMemberDto(
    Guid CustomerId, string? Name, string Mobile, string? Email, DateTimeOffset? LastOrderAt, int OrderCount, decimal TotalSpend, bool SmsReachable, bool EmailReachable);

public sealed record SegmentPreviewRequest(SegmentDefinition? Definition);

// ---- Campaigns --------------------------------------------------------------------------------------------

public sealed record CampaignDto(
    Guid Id, string Name, Guid SegmentId, string? SegmentName, NotificationChannel Channel, CampaignStatus Status, string? SubjectEn, string? SubjectBn, string BodyEn, string? BodyBn,
    DateTimeOffset? ScheduledAt, string? ScheduledAtDhaka, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt, DateTimeOffset? CancelledAt, bool RecipientsReady, bool DeferredByQuietHours,
    string? CreatedByName, DateTimeOffset CreatedAt, CampaignCountsDto Counts);

/// <summary>Recipient rows by state. Before sending starts everything is zero (use the preview for the expected numbers).</summary>
public sealed record CampaignCountsDto(int Recipients, int Pending, int Queued, int Skipped);

public sealed record SaveCampaignRequest(string? Name, Guid? SegmentId, NotificationChannel? Channel, string? SubjectEn, string? SubjectBn, string? BodyEn, string? BodyBn);

public sealed record ScheduleCampaignRequest(DateTimeOffset? ScheduledAt);

public sealed record SampleMessageDto(string Language, string? Subject, string Body, int Characters);

public sealed record CampaignPreviewDto(
    int SegmentMembers, int Recipients, int SkippedNoConsent, int SkippedNoContact, int SkippedFrequencyCap, bool QuietHoursNow, bool SmsDeferred, IReadOnlyList<SampleMessageDto> Samples,
    IReadOnlyList<string> UnknownPlaceholders);

public sealed record SendTestRequest(string? Language);

public sealed record SendTestDto(NotificationChannel Channel, string Recipient, Guid NotificationLogId, string Status);

public sealed record CampaignRecipientDto(Guid CustomerId, string? Name, string? Contact, RecipientStatus Status, string? SkipReason, NotificationStatus? DeliveryStatus, DateTimeOffset? ProcessedAt);

public sealed record SkipCountDto(string Reason, int Count);

public sealed record AttributionDto(int Orders, decimal Revenue, int WindowDays);

public sealed record CampaignResultsDto(
    Guid CampaignId, string Name, CampaignStatus Status, NotificationChannel Channel, int Recipients, int Pending, int InFlight, int Sent, int Delivered, int Failed, int Skipped, IReadOnlyList<SkipCountDto> SkippedByReason,
    int Unsubscribed, AttributionDto Attribution);

public sealed record CampaignListItemDto(
    Guid Id, string Name, NotificationChannel Channel, CampaignStatus Status, DateTimeOffset? ScheduledAt, DateTimeOffset? StartedAt, int Recipients, int Sent, int Failed, int Skipped, int Unsubscribed,
    int AttributedOrders, decimal AttributedRevenue);

public sealed record CampaignListQuery(CampaignStatus? Status, NotificationChannel? Channel, Guid? SegmentId);

// ---- Automations ------------------------------------------------------------------------------------------

public sealed record AutomationDto(
    Guid Id, AutomationKind Kind, string Name, string Description, bool IsEnabled, NotificationChannel Channel, int DelayMinutes, int WindowDays, int CapDays, string? SubjectEn, string? SubjectBn,
    string BodyEn, string BodyBn, IReadOnlyList<string> Placeholders, DateTimeOffset? LastRunAt);

public sealed record UpdateAutomationRequest(bool? IsEnabled, NotificationChannel? Channel, int? DelayMinutes, int? WindowDays, int? CapDays, string? SubjectEn, string? SubjectBn, string? BodyEn, string? BodyBn);

public sealed record AutomationDryRunItemDto(AutomationKind Kind, string Name, bool IsEnabled, int Candidates, int WouldSend, int WouldSendIfEnabled, int DeferredByQuietHours, IReadOnlyList<SkipCountDto> Skipped);

public sealed record AutomationDryRunDto(DateTimeOffset At, bool QuietHoursNow, IReadOnlyList<AutomationDryRunItemDto> Automations);

public sealed record AutomationResultsDto(
    AutomationKind Kind, string Name, bool IsEnabled, int Queued, int Sent, int Failed, int Skipped, AttributionDto Attribution, DateTimeOffset? LastRunAt);

// ---- Settings ---------------------------------------------------------------------------------------------

public sealed record MarketingSettingsDto(
    bool QuietHoursEnabled, string QuietHoursStart, string QuietHoursEnd, int FrequencyCapMessages, int FrequencyCapDays, int ThrottlePerMinute, int AttributionWindowDays, string ShopName, string? LinkUrl,
    int MaxNotifyMePerMobile, string SmsOptOutText);

public sealed record UpdateMarketingSettingsRequest(
    bool? QuietHoursEnabled, string? QuietHoursStart, string? QuietHoursEnd, int? FrequencyCapMessages, int? FrequencyCapDays, int? ThrottlePerMinute, int? AttributionWindowDays, string? ShopName,
    string? LinkUrl, int? MaxNotifyMePerMobile, string? SmsOptOutText);

// ---- Public -----------------------------------------------------------------------------------------------

public sealed record UnsubscribeDto(string Channel, bool Unsubscribed, bool AlreadyUnsubscribed);

public sealed record NotifyMeRequest(Guid? VariantId, string? Mobile, string? Email);

public sealed record NotifyMeDto(Guid SubscriptionId, Guid VariantId, bool AlreadySubscribed);
