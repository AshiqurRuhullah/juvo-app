using Juvo.Application.Abstractions;
using Juvo.Application.Notifications;
using Juvo.Application.Orders;
using Juvo.Domain.Customers;
using Juvo.Domain.Marketing;
using Juvo.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Marketing;

/// <summary>What one pass over a campaign did.</summary>
public sealed record DispatchPass(int Queued, int Skipped, bool Deferred, bool Finished);

/// <summary>
/// Turns a campaign into messages. First every segment member gets a recipient row (written before anything is sent, in batches, resumable); then batches of pending recipients go through
/// the shared notification path (consent is checked there, never bypassed), limited by the throttle and held back by quiet hours for SMS. Each batch is one transaction under a lock,
/// and every message has the dedupe key <c>campaign:&lt;id&gt;:&lt;customer&gt;</c>, so a crash or a second worker can never send twice (FR-AC02).
/// </summary>
public sealed class CampaignDispatchService(
    IApplicationDbContext db, IUnitOfWork unitOfWork, SegmentQueryBuilder builder, NotificationService notifications, MarketingSettingsService settingsService, MarketingMessenger messenger,
    CustomerFacts facts, SettingsService settings, TimeProvider clock)
{
    public const string TemplateCode = "marketing.campaign";
    public const int MaterialiseBatch = 500;
    public const int SendBatch = 200;

    public static string DedupeKey(Guid campaignId, Guid customerId) => $"campaign:{campaignId:N}:{customerId:N}";

    private static bool HasContact(NotificationChannel channel, string mobile, string? email) =>
        channel == NotificationChannel.Email ? !string.IsNullOrWhiteSpace(email) : !string.IsNullOrWhiteSpace(mobile);

    /// <summary>Moves a Draft or Scheduled campaign to Sending and writes its recipient rows.</summary>
    public async Task StartAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.FirstAsync(c => c.Id == campaignId, cancellationToken);
        if (campaign.Status is CampaignStatus.Draft or CampaignStatus.Scheduled)
        {
            campaign.StartSending(clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        }

        await MaterialiseAsync(campaignId, cancellationToken);
    }

    /// <summary>One recipient row per active segment member that has none yet. Safe to call again after a crash: only the missing rows are added.</summary>
    public async Task MaterialiseAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.FirstAsync(c => c.Id == campaignId, cancellationToken);
        if (campaign.RecipientsReady || campaign.Status != CampaignStatus.Sending)
        {
            return;
        }

        var json = await db.CustomerSegments.AsNoTracking().Where(s => s.Id == campaign.SegmentId).Select(s => s.DefinitionJson).FirstAsync(cancellationToken);
        var members = builder.Members(SegmentJson.Read(json));
        var type = campaign.Channel == NotificationChannel.Email ? ConsentType.MarketingEmail : ConsentType.MarketingSms;
        var now = clock.GetUtcNow();
        while (true)
        {
            var batch = await members.Where(c => !db.CampaignRecipients.Any(r => r.CampaignId == campaignId && r.CustomerId == c.Id)).OrderBy(c => c.Id).Take(MaterialiseBatch)
                .Select(c => new { c.Id, c.Mobile, c.Email }).ToListAsync(cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            var consented = await facts.ConsentedAsync(batch.Select(b => b.Id).ToList(), type, cancellationToken);
            foreach (var member in batch)
            {
                var contact = campaign.Channel == NotificationChannel.Email ? member.Email : member.Mobile;
                var row = new CampaignRecipient(campaignId, member.Id, string.IsNullOrWhiteSpace(contact) ? null : contact);
                if (!HasContact(campaign.Channel, member.Mobile, member.Email))
                {
                    row.MarkSkipped(SkipReasons.NoContact, null, now);
                }
                else if (!consented.Contains(member.Id))
                {
                    row.MarkSkipped(SkipReasons.NoConsent, null, now);
                }

                db.CampaignRecipients.Add(row);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        campaign.MarkRecipientsReady();
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Sends the next batch of a Sending campaign (respecting throttle and quiet hours) and completes it when nothing is pending.</summary>
    public async Task<DispatchPass> ProcessAsync(Guid campaignId, CancellationToken cancellationToken)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == campaignId, cancellationToken);
        if (campaign is null || campaign.Status != CampaignStatus.Sending)
        {
            return new DispatchPass(0, 0, false, true);
        }

        if (!campaign.RecipientsReady)
        {
            await MaterialiseAsync(campaignId, cancellationToken);
        }

        var s = await settingsService.ReadAsync(cancellationToken);
        var now = clock.GetUtcNow();
        if (campaign.Channel == NotificationChannel.Sms && MarketingLogic.IsQuiet(now, s.QuietHoursEnabled, s.QuietStart, s.QuietEnd))
        {
            return new DispatchPass(0, 0, true, false);
        }

        var minuteAgo = now.AddMinutes(-1);
        var lastMinute = await db.CampaignRecipients.AsNoTracking().CountAsync(r => r.CampaignId == campaignId && r.Status == RecipientStatus.Queued && r.ProcessedAt >= minuteAgo, cancellationToken);
        var allowance = s.ThrottlePerMinute - lastMinute;
        var pass = new DispatchPass(0, 0, false, false);
        if (allowance > 0)
        {
            pass = await unitOfWork.ExecuteInTransactionAsync(ct => SendBatchAsync(campaignId, Math.Min(allowance, SendBatch), s, ct), cancellationToken);
        }

        return pass;
    }

    private async Task<DispatchPass> SendBatchAsync(Guid campaignId, int size, MarketingSettings s, CancellationToken cancellationToken)
    {
        await db.AcquireTransactionLockAsync($"campaign-send:{campaignId:N}", cancellationToken);
        var campaign = await db.Campaigns.FirstAsync(c => c.Id == campaignId, cancellationToken);
        if (campaign.Status != CampaignStatus.Sending)
        {
            return new DispatchPass(0, 0, false, true);
        }

        var now = clock.GetUtcNow();
        var pending = await db.CampaignRecipients.Where(r => r.CampaignId == campaignId && r.Status == RecipientStatus.Pending).OrderBy(r => r.Id).Take(size).ToListAsync(cancellationToken);
        var customers = await facts.LoadAsync(pending.Select(r => r.CustomerId).ToList(), cancellationToken);
        var capFrom = now.AddDays(-s.FrequencyCapDays);
        var ids = pending.Select(r => r.CustomerId).ToList();
        var recent = s.FrequencyCapMessages <= 0
            ? new Dictionary<Guid, int>()
            : await db.NotificationLogs.AsNoTracking()
                .Where(l => l.CustomerId != null && ids.Contains(l.CustomerId.Value) && l.Category == NotificationCategory.Marketing && l.Template == TemplateCode && l.QueuedAt >= capFrom
                            && (l.Status == NotificationStatus.Pending || l.Status == NotificationStatus.Sent || l.Status == NotificationStatus.Failed))
                .GroupBy(l => l.CustomerId!.Value).Select(g => new { Id = g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Id, x => x.N, cancellationToken);
        var defaults = await settings.ReadAsync([NotificationSettingKeys.DefaultLanguage], cancellationToken);
        var defaultLanguage = NotificationService.NormalizeLanguage(defaults.GetValueOrDefault(NotificationSettingKeys.DefaultLanguage)) is { Length: > 0 } d ? d : NotificationCatalog.DefaultLanguage;

        var queued = 0;
        var skipped = 0;
        foreach (var recipient in pending)
        {
            if (!customers.TryGetValue(recipient.CustomerId, out var customer))
            {
                recipient.MarkSkipped(SkipReasons.Inactive, null, now);
                skipped++;
                continue;
            }

            var contact = campaign.Channel == NotificationChannel.Email ? customer.Email : customer.Mobile;
            if (string.IsNullOrWhiteSpace(contact))
            {
                recipient.MarkSkipped(SkipReasons.NoContact, null, now);
                skipped++;
                continue;
            }

            if (!MarketingLogic.CapAllows(recent.GetValueOrDefault(recipient.CustomerId), s.FrequencyCapMessages))
            {
                recipient.MarkSkipped(SkipReasons.FrequencyCap, null, now);
                skipped++;
                continue;
            }

            var language = NotificationService.NormalizeLanguage(customer.Language) is { Length: > 0 } l ? l : defaultLanguage;
            var bodyTemplate = language == "bn" && !string.IsNullOrWhiteSpace(campaign.BodyBn) ? campaign.BodyBn : campaign.BodyEn;
            var subjectTemplate = language == "bn" && !string.IsNullOrWhiteSpace(campaign.SubjectBn) ? campaign.SubjectBn : campaign.SubjectEn;
            var values = new Dictionary<string, string> { ["customerName"] = FirstName(customer.Name) };
            var (subject, body) = messenger.Compose(campaign.Channel, subjectTemplate, bodyTemplate, values, s, customer.Id, campaignId, includeUnsubscribe: true);
            var result = await notifications.QueueCustomAsync(
                new CustomMessage(campaign.Channel, contact.Trim(), TemplateCode, subject, body, language, NotificationCategory.Marketing, customer.Id, DedupeKey(campaignId, customer.Id), ConsentMode.Required), cancellationToken);
            switch (result.Outcome)
            {
                case CustomOutcome.Queued:
                    recipient.MarkQueued(result.LogId!.Value, now);
                    queued++;
                    break;
                case CustomOutcome.AlreadyQueued when result.ExistingStatus != NotificationStatus.Skipped:
                    recipient.MarkQueued(result.LogId!.Value, now); // a crash left the message without its recipient mark: adopt it, never send again
                    queued++;
                    break;
                case CustomOutcome.AlreadyQueued:
                    recipient.MarkSkipped(SkipReasons.NoConsent, result.LogId, now);
                    skipped++;
                    break;
                default:
                    recipient.MarkSkipped(result.SkipCode == "ChannelOff" ? SkipReasons.ChannelOff : SkipReasons.NoConsent, result.LogId, now);
                    skipped++;
                    break;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var remaining = await db.CampaignRecipients.AnyAsync(r => r.CampaignId == campaignId && r.Status == RecipientStatus.Pending, cancellationToken);
        if (!remaining && campaign.RecipientsReady)
        {
            campaign.Complete(clock.GetUtcNow());
            await db.SaveChangesAsync(cancellationToken);
        }

        return new DispatchPass(queued, skipped, false, !remaining && campaign.RecipientsReady);
    }

    /// <summary>The customer's first name for greetings (the whole name when it is a single word), or an empty string.</summary>
    public static string FirstName(string? fullName) => string.IsNullOrWhiteSpace(fullName) ? string.Empty : fullName.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];

    /// <summary>What the scheduled-send worker does: start campaigns whose time has come, then send batches until nothing more can go now.</summary>
    public async Task<CampaignSweep> RunDueAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var started = 0;
        var queued = 0;
        var due = await db.Campaigns.AsNoTracking().Where(c => c.Status == CampaignStatus.Scheduled && c.ScheduledAt <= now).OrderBy(c => c.ScheduledAt).Select(c => c.Id).ToListAsync(cancellationToken);
        foreach (var id in due)
        {
            await StartAsync(id, cancellationToken);
            started++;
        }

        var sending = await db.Campaigns.AsNoTracking().Where(c => c.Status == CampaignStatus.Sending).OrderBy(c => c.StartedAt).Select(c => c.Id).ToListAsync(cancellationToken);
        foreach (var id in sending)
        {
            for (var pass = 0; pass < 100; pass++)
            {
                var result = await ProcessAsync(id, cancellationToken);
                queued += result.Queued;
                if (result.Finished || result.Deferred || result.Queued + result.Skipped == 0)
                {
                    break;
                }
            }
        }

        return new CampaignSweep(started, sending.Count, queued);
    }
}

public sealed record CampaignSweep(int Started, int Sending, int Queued);
