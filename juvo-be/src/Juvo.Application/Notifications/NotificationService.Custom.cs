using Juvo.Domain.Customers;
using Juvo.Domain.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Notifications;

/// <summary>How a custom message treats the customer's marketing consent.</summary>
public enum ConsentMode
{
    /// <summary>The latest consent decision for the channel must be "yes" (marketing messages).</summary>
    Required = 1,

    /// <summary>Sent unless the customer opted out: the latest decision for the channel is "no" (a message the customer asked for, such as back in stock).</summary>
    HonourOptOut = 2,

    /// <summary>No consent check (a test message to a staff member's own contact).</summary>
    None = 3,
}

/// <summary>A message whose text was already written (a campaign or automation text) instead of coming from the template catalogue.</summary>
public sealed record CustomMessage(
    NotificationChannel Channel, string Recipient, string TemplateCode, string? Subject, string Body, string Language, NotificationCategory Category, Guid? CustomerId, string DedupeKey, ConsentMode Consent);

public enum CustomOutcome
{
    Queued = 1,
    Skipped = 2,

    /// <summary>The dedupe key was used before: nothing new was written.</summary>
    AlreadyQueued = 3,
}

/// <param name="SkipCode">Stable reason when skipped: NoConsent, OptedOut or ChannelOff.</param>
public sealed record CustomQueueResult(CustomOutcome Outcome, Guid? LogId, NotificationStatus? ExistingStatus, string? SkipCode);

public sealed partial class NotificationService
{
    private (bool Sms, bool Email, bool WhatsApp, string Language)? _channelsCache;

    /// <summary>
    /// The shared send path for texts that are not catalogue templates. Same rules as <see cref="NotifyAsync"/>: channel switched on, dedupe key idempotent, marketing needs the
    /// customer's consent (otherwise a Skipped row records why), and delivery goes through the outbox with retry. Adds rows to the context; the caller saves (so a batch is one transaction).
    /// </summary>
    public async Task<CustomQueueResult> QueueCustomAsync(CustomMessage message, CancellationToken cancellationToken)
    {
        _channelsCache ??= await ChannelsAsync(cancellationToken);
        var channels = _channelsCache.Value;
        var on = message.Channel switch
        {
            NotificationChannel.Sms => channels.Sms,
            NotificationChannel.Email => channels.Email,
            NotificationChannel.WhatsApp => channels.WhatsApp,
            _ => true,
        };

        var existing = await db.NotificationLogs.AsNoTracking().Where(n => n.DedupeKey == message.DedupeKey && n.Channel == message.Channel).Select(n => new { n.Id, n.Status }).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return new CustomQueueResult(CustomOutcome.AlreadyQueued, existing.Id, existing.Status, null);
        }

        var now = clock.GetUtcNow();
        string? skip = null;
        if (!on)
        {
            skip = "ChannelOff";
        }
        else if (message.Consent != ConsentMode.None)
        {
            var type = message.Channel == NotificationChannel.Email ? ConsentType.MarketingEmail : ConsentType.MarketingSms;
            if (message.Consent == ConsentMode.Required && !await HasConsentAsync(message.CustomerId, type, cancellationToken))
            {
                skip = "NoConsent";
            }
            else if (message.Consent == ConsentMode.HonourOptOut && message.CustomerId is { } id && await LatestConsentAsync(id, type, cancellationToken) == false)
            {
                skip = "OptedOut";
            }
        }

        if (skip is not null)
        {
            var reason = skip switch
            {
                "ChannelOff" => "The channel is switched off.",
                "OptedOut" => "The customer opted out of this channel.",
                _ => "The customer has not agreed to marketing messages on this channel.",
            };
            var skipped = NotificationLog.Skip(message.Channel, message.Recipient, message.TemplateCode, message.Language, message.Category, message.CustomerId, reason, message.DedupeKey, now);
            db.NotificationLogs.Add(skipped);
            return new CustomQueueResult(CustomOutcome.Skipped, skipped.Id, null, skip);
        }

        var log = NotificationLog.Queue(message.Channel, message.Recipient, message.TemplateCode, message.Subject, message.Body, message.Language, 0, message.Category, message.CustomerId, message.DedupeKey,
            currentUser.CorrelationId, now);
        db.NotificationLogs.Add(log);
        outbox.Enqueue("notification.deliver", new NotificationDeliverMessage(log.Id));
        return new CustomQueueResult(CustomOutcome.Queued, log.Id, null, null);
    }

    private async Task<bool?> LatestConsentAsync(Guid customerId, ConsentType type, CancellationToken cancellationToken) =>
        await db.ConsentLogs.AsNoTracking().Where(c => c.CustomerId == customerId && c.Type == type).OrderByDescending(c => c.At).ThenByDescending(c => c.Id).Select(c => (bool?)c.Granted).FirstOrDefaultAsync(cancellationToken);
}
