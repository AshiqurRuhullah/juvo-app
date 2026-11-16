using System.Globalization;
using System.Linq.Expressions;
using Juvo.Application.Abstractions;
using Juvo.Application.Notifications;
using Juvo.Application.Orders;
using Juvo.Domain.Customers;
using Juvo.Domain.Marketing;
using Juvo.Domain.Notifications;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Marketing;

/// <summary>Campaigns: write, preview with real counts, schedule, send, cancel, test and read the results (module AC, FR-AC02, FR-AC04).</summary>
public sealed class CampaignsService(
    IApplicationDbContext db, SegmentQueryBuilder builder, CampaignDispatchService dispatch, MarketingSettingsService settingsService, MarketingMessenger messenger, AttributionService attribution,
    NotificationService notifications, SettingsService settings, ICurrentUser currentUser, TimeProvider clock)
{
    private static Error NotFound() => Error.NotFound(ErrorCodes.Marketing.CampaignNotFound, "Campaign not found.");

    private static Error Invalid(string field, string message) => new(ErrorCodes.Marketing.CampaignInvalid, "The campaign is not valid.", ErrorType.Validation, new Dictionary<string, string[]> { [field] = [message] });

    private static Error State(string message) => Error.Conflict(ErrorCodes.Marketing.CampaignInvalidState, message);

    private static string Dhaka(DateTimeOffset at) => at.ToOffset(MarketingLogic.Dhaka).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " (Asia/Dhaka)";

    private async Task<CampaignDto> ToDtoAsync(Campaign c, MarketingSettings s, CancellationToken cancellationToken)
    {
        var segmentName = await db.CustomerSegments.AsNoTracking().Where(x => x.Id == c.SegmentId).Select(x => x.Name).FirstOrDefaultAsync(cancellationToken);
        var rows = await db.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == c.Id).GroupBy(r => r.Status).Select(g => new { Status = g.Key, N = g.Count() }).ToListAsync(cancellationToken);
        int Of(RecipientStatus st) => rows.Where(r => r.Status == st).Sum(r => r.N);
        var deferred = c.Status == CampaignStatus.Sending && c.Channel == NotificationChannel.Sms && MarketingLogic.IsQuiet(clock.GetUtcNow(), s.QuietHoursEnabled, s.QuietStart, s.QuietEnd);
        return new CampaignDto(
            c.Id, c.Name, c.SegmentId, segmentName, c.Channel, c.Status, c.SubjectEn, c.SubjectBn, c.BodyEn, c.BodyBn, c.ScheduledAt, c.ScheduledAt is { } at ? Dhaka(at) : null, c.StartedAt, c.CompletedAt, c.CancelledAt,
            c.RecipientsReady, deferred, c.CreatedByName, c.CreatedAt, new CampaignCountsDto(rows.Sum(r => r.N), Of(RecipientStatus.Pending), Of(RecipientStatus.Queued), Of(RecipientStatus.Skipped)));
    }

    // ---- Reading --------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<CampaignListItemDto>>> ListAsync(PageRequest request, CampaignListQuery filter, CancellationToken cancellationToken)
    {
        var query = db.Campaigns.AsNoTracking().AsQueryable();
        if (filter.Status is { } status)
        {
            query = query.Where(c => c.Status == status);
        }

        if (filter.Channel is { } channel)
        {
            query = query.Where(c => c.Channel == channel);
        }

        if (filter.SegmentId is { } segmentId)
        {
            query = query.Where(c => c.SegmentId == segmentId);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(c => c.Name.Contains(term));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var window = (await settingsService.ReadAsync(cancellationToken)).AttributionWindowDays;
        var items = new List<CampaignListItemDto>();
        foreach (var c in rows)
        {
            var results = await ResultsOfAsync(c, window, cancellationToken);
            items.Add(new CampaignListItemDto(c.Id, c.Name, c.Channel, c.Status, c.ScheduledAt, c.StartedAt, results.Recipients, results.Sent, results.Failed, results.Skipped, results.Unsubscribed, results.Attribution.Orders, results.Attribution.Revenue));
        }

        return new PagedResult<CampaignListItemDto>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<CampaignDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return campaign is null ? NotFound() : await ToDtoAsync(campaign, await settingsService.ReadAsync(cancellationToken), cancellationToken);
    }

    // ---- Writing --------------------------------------------------------------------------------------------

    private async Task<Error?> ValidateAsync(SaveCampaignRequest r, CancellationToken cancellationToken)
    {
        var name = r.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120)
        {
            return Invalid("name", "Give the campaign a name (up to 120 characters).");
        }

        if (r.SegmentId is not { } segmentId || !await db.CustomerSegments.AnyAsync(s => s.Id == segmentId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Marketing.SegmentNotFound, "Segment not found.");
        }

        if (r.Channel is not (NotificationChannel.Sms or NotificationChannel.Email))
        {
            return Invalid("channel", "A campaign goes out by Sms or Email.");
        }

        var max = r.Channel == NotificationChannel.Sms ? 1000 : 20000;
        if (string.IsNullOrWhiteSpace(r.BodyEn) || r.BodyEn.Length > max)
        {
            return Invalid("bodyEn", $"Write the English message (up to {max} characters).");
        }

        if (r.BodyBn is { Length: > 0 } && r.BodyBn.Length > max)
        {
            return Invalid("bodyBn", $"The Bengali message can have {max} characters.");
        }

        if (r.Channel == NotificationChannel.Email && (string.IsNullOrWhiteSpace(r.SubjectEn) || r.SubjectEn.Length > 200))
        {
            return Invalid("subjectEn", "An email needs an English subject (up to 200 characters).");
        }

        if (r.SubjectBn is { Length: > 200 })
        {
            return Invalid("subjectBn", "The Bengali subject can have 200 characters.");
        }

        foreach (var (field, text) in new[] { ("bodyEn", r.BodyEn), ("bodyBn", r.BodyBn), ("subjectEn", r.SubjectEn), ("subjectBn", r.SubjectBn) })
        {
            if (MarketingTemplate.UnknownIn(text, MarketingTemplate.CampaignPlaceholders) is { Count: > 0 } unknown)
            {
                return Invalid(field, $"Unknown placeholder {{{unknown[0]}}}. Use {string.Join(", ", MarketingTemplate.CampaignPlaceholders.Select(p => "{" + p + "}"))}.");
            }
        }

        return null;
    }

    public async Task<Result<CampaignDto>> CreateAsync(SaveCampaignRequest r, CancellationToken cancellationToken)
    {
        if (await ValidateAsync(r, cancellationToken) is { } error)
        {
            return error;
        }

        var staff = await StaffNameAsync(cancellationToken);
        var campaign = new Campaign(r.Name!, r.SegmentId!.Value, r.Channel!.Value, r.SubjectEn, r.SubjectBn, r.BodyEn!, r.BodyBn, currentUser.UserId, staff);
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(campaign, await settingsService.ReadAsync(cancellationToken), cancellationToken);
    }

    public async Task<Result<CampaignDto>> UpdateAsync(Guid id, SaveCampaignRequest r, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (campaign is null)
        {
            return NotFound();
        }

        if (!campaign.IsEditable)
        {
            return Error.Conflict(ErrorCodes.Marketing.CampaignNotEditable, "Only a draft or scheduled campaign can be changed.");
        }

        if (await ValidateAsync(r, cancellationToken) is { } error)
        {
            return error;
        }

        campaign.Update(r.Name!, r.SegmentId!.Value, r.Channel!.Value, r.SubjectEn, r.SubjectBn, r.BodyEn!, r.BodyBn);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(campaign, await settingsService.ReadAsync(cancellationToken), cancellationToken);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (campaign is null)
        {
            return NotFound();
        }

        if (campaign.Status is not (CampaignStatus.Draft or CampaignStatus.Scheduled) && campaign.StartedAt is not null)
        {
            return State("A campaign that started sending is kept for the record. Cancel it instead.");
        }

        db.Campaigns.Remove(campaign);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ---- Preview --------------------------------------------------------------------------------------------

    public async Task<Result<CampaignPreviewDto>> PreviewAsync(Guid id, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (campaign is null)
        {
            return NotFound();
        }

        var s = await settingsService.ReadAsync(cancellationToken);
        var json = await db.CustomerSegments.AsNoTracking().Where(x => x.Id == campaign.SegmentId).Select(x => x.DefinitionJson).FirstAsync(cancellationToken);
        var members = builder.Members(SegmentJson.Read(json));
        var email = campaign.Channel == NotificationChannel.Email;
        var type = email ? ConsentType.MarketingEmail : ConsentType.MarketingSms;
        var consented = builder.Consented(type);
        Expression<Func<Customer, bool>> contact = email ? c => c.Email != null && c.Email != string.Empty : c => c.Mobile != string.Empty;
        var now = clock.GetUtcNow();
        var capFrom = now.AddDays(-s.FrequencyCapDays);
        var capMax = s.FrequencyCapMessages;
        Expression<Func<Customer, bool>> capped = c => db.NotificationLogs.Count(l => l.CustomerId == c.Id && l.Category == NotificationCategory.Marketing && l.Template == CampaignDispatchService.TemplateCode
            && l.QueuedAt >= capFrom && (l.Status == NotificationStatus.Pending || l.Status == NotificationStatus.Sent || l.Status == NotificationStatus.Failed)) >= capMax;

        var total = await members.CountAsync(cancellationToken);
        var noContact = await members.Where(Not(contact)).CountAsync(cancellationToken);
        var noConsent = await members.Where(contact).Where(Not(consented)).CountAsync(cancellationToken);
        var reachable = members.Where(contact).Where(consented);
        var reachableCount = await reachable.CountAsync(cancellationToken);
        var cappedCount = capMax > 0 ? await reachable.Where(capped).CountAsync(cancellationToken) : 0;

        var sample = await members.OrderBy(c => c.Id).Select(c => new { c.Id, c.FullName }).FirstOrDefaultAsync(cancellationToken);
        var values = new Dictionary<string, string> { ["customerName"] = CampaignDispatchService.FirstName(sample?.FullName ?? "Sample Customer") };
        var samples = new List<SampleMessageDto>();
        foreach (var language in new[] { "en", "bn" })
        {
            var body = language == "bn" ? campaign.BodyBn : campaign.BodyEn;
            if (string.IsNullOrWhiteSpace(body))
            {
                continue;
            }

            var subject = language == "bn" && !string.IsNullOrWhiteSpace(campaign.SubjectBn) ? campaign.SubjectBn : campaign.SubjectEn;
            var (subjectText, bodyText) = messenger.Compose(campaign.Channel, subject, body, values, s, sample?.Id ?? Guid.Empty, campaign.Id, includeUnsubscribe: true);
            samples.Add(new SampleMessageDto(language, subjectText, bodyText, bodyText.Length));
        }

        var unknown = new[] { campaign.BodyEn, campaign.BodyBn, campaign.SubjectEn, campaign.SubjectBn }.SelectMany(t => MarketingTemplate.UnknownIn(t, MarketingTemplate.CampaignPlaceholders)).Distinct().ToList();
        var quiet = MarketingLogic.IsQuiet(now, s.QuietHoursEnabled, s.QuietStart, s.QuietEnd);
        return new CampaignPreviewDto(total, reachableCount - cappedCount, noConsent, noContact, cappedCount, quiet, quiet && campaign.Channel == NotificationChannel.Sms, samples, unknown);
    }

    private static Expression<Func<Customer, bool>> Not(Expression<Func<Customer, bool>> e) => Expression.Lambda<Func<Customer, bool>>(Expression.Not(e.Body), e.Parameters);

    // ---- Schedule, send, cancel -------------------------------------------------------------------------------

    private async Task<Error?> CanSendAsync(Campaign campaign, CancellationToken cancellationToken)
    {
        if (campaign.Status is not (CampaignStatus.Draft or CampaignStatus.Scheduled))
        {
            return State($"A {campaign.Status.ToString().ToLowerInvariant()} campaign cannot be sent.");
        }

        var key = campaign.Channel == NotificationChannel.Email ? NotificationSettingKeys.EmailEnabled : NotificationSettingKeys.SmsEnabled;
        var values = await settings.ReadAsync([key], cancellationToken);
        if (values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var on) && !on)
        {
            return Error.Conflict(ErrorCodes.Marketing.ChannelOff, $"The {campaign.Channel} channel is switched off in the notification settings.");
        }

        return null;
    }

    public async Task<Result<CampaignDto>> ScheduleAsync(Guid id, ScheduleCampaignRequest r, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (campaign is null)
        {
            return NotFound();
        }

        if (await CanSendAsync(campaign, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var now = clock.GetUtcNow();
        if (r.ScheduledAt is not { } at || at <= now || at > now.AddDays(365))
        {
            return new Error(ErrorCodes.Marketing.ScheduleInvalid, "Give a time in the future, within a year.", ErrorType.Validation, new Dictionary<string, string[]> { ["scheduledAt"] = ["Use a future time with its offset, for example 2026-12-01T10:00:00+06:00."] });
        }

        campaign.Schedule(at);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(campaign, await settingsService.ReadAsync(cancellationToken), cancellationToken);
    }

    public async Task<Result<CampaignDto>> SendNowAsync(Guid id, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (campaign is null)
        {
            return NotFound();
        }

        if (await CanSendAsync(campaign, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        await dispatch.StartAsync(id, cancellationToken);
        await dispatch.ProcessAsync(id, cancellationToken); // the first batch goes out now; the worker carries on with the rest
        return await GetAsync(id, cancellationToken);
    }

    public async Task<Result<CampaignDto>> CancelAsync(Guid id, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (campaign is null)
        {
            return NotFound();
        }

        if (campaign.Status is not (CampaignStatus.Draft or CampaignStatus.Scheduled or CampaignStatus.Sending))
        {
            return State($"A {campaign.Status.ToString().ToLowerInvariant()} campaign cannot be cancelled.");
        }

        var now = clock.GetUtcNow();
        campaign.Cancel(now);
        await db.SaveChangesAsync(cancellationToken);
        await db.CampaignRecipients.Where(r => r.CampaignId == id && r.Status == RecipientStatus.Pending)
            .ExecuteUpdateAsync(set => set.SetProperty(r => r.Status, RecipientStatus.Skipped).SetProperty(r => r.SkipReason, SkipReasons.Cancelled).SetProperty(r => r.ProcessedAt, now), cancellationToken);
        return await ToDtoAsync(campaign, await settingsService.ReadAsync(cancellationToken), cancellationToken);
    }

    // ---- Test message -------------------------------------------------------------------------------------

    private async Task<string?> StaffNameAsync(CancellationToken cancellationToken) =>
        currentUser.UserId is { } id ? await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.FullName).FirstOrDefaultAsync(cancellationToken) : null;

    /// <summary>Sends the campaign text to the signed-in staff member's own email or phone (no consent needed: it is their own contact). Not counted in any result.</summary>
    public async Task<Result<SendTestDto>> SendTestAsync(Guid id, SendTestRequest r, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (campaign is null)
        {
            return NotFound();
        }

        if (currentUser.UserId is not { } userId)
        {
            return Error.Unauthorized();
        }

        var staff = await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => new { u.FullName, u.Email, u.PhoneNumber }).FirstOrDefaultAsync(cancellationToken);
        var recipient = campaign.Channel == NotificationChannel.Email ? staff?.Email : staff?.PhoneNumber;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            return Error.Conflict(ErrorCodes.Marketing.TestNoContact, campaign.Channel == NotificationChannel.Email ? "Your staff profile has no email address to send the test to." : "Your staff profile has no phone number to send the test to.");
        }

        var language = NotificationService.NormalizeLanguage(r.Language) is { Length: > 0 } l ? l : "en";
        var bodyTemplate = language == "bn" && !string.IsNullOrWhiteSpace(campaign.BodyBn) ? campaign.BodyBn : campaign.BodyEn;
        var subjectTemplate = language == "bn" && !string.IsNullOrWhiteSpace(campaign.SubjectBn) ? campaign.SubjectBn : campaign.SubjectEn;
        var s = await settingsService.ReadAsync(cancellationToken);
        var values = new Dictionary<string, string> { ["customerName"] = CampaignDispatchService.FirstName(staff!.FullName) };
        var (subject, body) = messenger.Compose(campaign.Channel, subjectTemplate, bodyTemplate, values, s, Guid.Empty, campaign.Id, includeUnsubscribe: true);
        var result = await notifications.QueueCustomAsync(
            new CustomMessage(campaign.Channel, recipient.Trim(), "marketing.campaign-test", subject is null ? null : "[Test] " + subject, body, language, NotificationCategory.Transactional, null,
                $"campaign-test:{campaign.Id:N}:{userId:N}:{clock.GetUtcNow().UtcTicks}", ConsentMode.None), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        var status = result.Outcome == CustomOutcome.Queued ? NotificationStatus.Pending.ToString() : NotificationStatus.Skipped.ToString();
        return new SendTestDto(campaign.Channel, recipient.Trim(), result.LogId ?? Guid.Empty, status);
    }

    // ---- Recipients and results ---------------------------------------------------------------------------

    public async Task<Result<PagedResult<CampaignRecipientDto>>> RecipientsAsync(Guid id, PageRequest request, RecipientStatus? status, string? reason, CancellationToken cancellationToken)
    {
        if (!await db.Campaigns.AnyAsync(c => c.Id == id, cancellationToken))
        {
            return NotFound();
        }

        var query = db.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == id);
        if (status is { } st)
        {
            query = query.Where(r => r.Status == st);
        }

        if (!string.IsNullOrWhiteSpace(reason))
        {
            var code = reason.Trim();
            query = query.Where(r => r.SkipReason == code);
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await (from r in query.OrderBy(r => r.Id).Skip(page.Skip).Take(page.PageSize)
                          join c in db.Customers.IgnoreQueryFilters().AsNoTracking() on r.CustomerId equals c.Id
                          join l in db.NotificationLogs.AsNoTracking() on r.NotificationLogId equals l.Id into logs
                          from l in logs.DefaultIfEmpty()
                          select new { r.CustomerId, c.FullName, r.Contact, r.Status, r.SkipReason, Delivery = (NotificationStatus?)l.Status, r.ProcessedAt }).ToListAsync(cancellationToken);
        var items = rows.Select(r => new CampaignRecipientDto(r.CustomerId, r.FullName, MaskContact(r.Contact), r.Status, r.SkipReason, r.Delivery, r.ProcessedAt)).ToList();
        return new PagedResult<CampaignRecipientDto>(items, total, page.Page, page.PageSize);
    }

    private static string? MaskContact(string? contact) =>
        string.IsNullOrEmpty(contact) ? null : contact.Contains('@', StringComparison.Ordinal) ? Customers.CustomersAdminService.MaskEmail(contact) : Customers.CustomersAdminService.MaskMobile(contact);

    public async Task<Result<CampaignResultsDto>> ResultsAsync(Guid id, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return campaign is null ? NotFound() : await ResultsOfAsync(campaign, (await settingsService.ReadAsync(cancellationToken)).AttributionWindowDays, cancellationToken);
    }

    private async Task<CampaignResultsDto> ResultsOfAsync(Campaign c, int windowDays, CancellationToken cancellationToken)
    {
        var recipients = db.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == c.Id);
        var byStatus = await recipients.GroupBy(r => r.Status).Select(g => new { Status = g.Key, N = g.Count() }).ToListAsync(cancellationToken);
        int Of(RecipientStatus s) => byStatus.Where(x => x.Status == s).Sum(x => x.N);
        var skipped = await recipients.Where(r => r.Status == RecipientStatus.Skipped).GroupBy(r => r.SkipReason).Select(g => new { Reason = g.Key, N = g.Count() }).ToListAsync(cancellationToken);
        var logs = from r in recipients where r.NotificationLogId != null join l in db.NotificationLogs.AsNoTracking() on r.NotificationLogId equals l.Id select l;
        var byDelivery = await logs.GroupBy(l => l.Status).Select(g => new { Status = g.Key, N = g.Count() }).ToListAsync(cancellationToken);
        int Logs(params NotificationStatus[] statuses) => byDelivery.Where(x => statuses.Contains(x.Status)).Sum(x => x.N);
        var sent = Logs(NotificationStatus.Sent);
        var failed = Logs(NotificationStatus.Failed, NotificationStatus.DeadLetter);
        var inFlight = Of(RecipientStatus.Queued) - sent - failed;
        var source = $"unsubscribe:{c.Id:N}";
        var unsubscribed = await db.ConsentLogs.AsNoTracking().Where(l => l.Source == source && !l.Granted).Select(l => l.CustomerId).Distinct().CountAsync(cancellationToken);
        var attributed = await attribution.ForLogsAsync(logs, windowDays, cancellationToken);
        return new CampaignResultsDto(
            c.Id, c.Name, c.Status, c.Channel, byStatus.Sum(x => x.N), Of(RecipientStatus.Pending), Math.Max(0, inFlight), sent, sent, failed, Of(RecipientStatus.Skipped),
            [.. skipped.Select(x => new SkipCountDto(x.Reason ?? "Unknown", x.N))], unsubscribed, attributed);
    }
}
