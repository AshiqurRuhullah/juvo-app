using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Domain.Customers;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juvo.Application.Marketing;

/// <summary>Section <c>Marketing</c> of the configuration.</summary>
public sealed class MarketingOptions
{
    public const string SectionName = "Marketing";

    /// <summary>The address the unsubscribe link points to (the token is added as <c>?token=</c>). The default is the public API; the web shop may point it at its own page that calls the same endpoint.</summary>
    public string UnsubscribeUrl { get; set; } = "http://localhost:5080/api/v1/store/unsubscribe";
}

/// <summary>What an unsubscribe link carries. <see cref="CampaignId"/> is empty for links that do not belong to a campaign.</summary>
public sealed record UnsubscribeClaims(Guid CustomerId, NotificationChannel Channel, Guid? CampaignId);

/// <summary>Signs and checks unsubscribe tokens (HMAC with a key derived from the platform signing key). A token never expires: an opt-out link must keep working.</summary>
public interface IUnsubscribeTokens
{
    string Create(Guid customerId, NotificationChannel channel, Guid? campaignId);

    UnsubscribeClaims? Verify(string? token);
}

/// <summary>Builds the final text of a marketing message: placeholders, unsubscribe link (email) or opt-out instruction (SMS).</summary>
public sealed class MarketingMessenger(IUnsubscribeTokens tokens, IOptions<MarketingOptions> options, IOptions<AuthOptions> auth)
{
    public string UnsubscribeLink(Guid customerId, NotificationChannel channel, Guid? campaignId) =>
        $"{options.Value.UnsubscribeUrl}?token={tokens.Create(customerId, channel, campaignId)}";

    /// <summary>The page {link} points to: the setting <c>Marketing.LinkUrl</c> or the shop home.</summary>
    public string ShopLink(MarketingSettings settings) => settings.LinkUrl ?? auth.Value.FrontendBaseUrl.TrimEnd('/');

    public (string? Subject, string Body) Compose(
        NotificationChannel channel, string? subjectTemplate, string bodyTemplate, IReadOnlyDictionary<string, string> values, MarketingSettings settings, Guid customerId, Guid? campaignId, bool includeUnsubscribe)
    {
        var map = new Dictionary<string, string>(values, StringComparer.Ordinal);
        map.TryAdd("shopName", settings.ShopName);
        map.TryAdd("link", ShopLink(settings));
        if (includeUnsubscribe)
        {
            map["unsubscribeLink"] = UnsubscribeLink(customerId, channel, campaignId);
        }

        var body = MarketingTemplate.Render(bodyTemplate, map);
        if (includeUnsubscribe && !bodyTemplate.Contains("{unsubscribeLink}", StringComparison.Ordinal))
        {
            body = channel == NotificationChannel.Email
                ? $"{body}\n\n--\nUnsubscribe: {map["unsubscribeLink"]}"
                : $"{body} {MarketingTemplate.Render(settings.SmsOptOutText, map)}";
        }

        var subject = subjectTemplate is null ? null : MarketingTemplate.Render(subjectTemplate, map);
        return (subject, body);
    }
}

/// <summary>Orders credited to marketing messages: last touch inside the attribution window (FR-AC04). Reads the message log joined to orders; the order table is not changed.</summary>
public sealed class AttributionService(IApplicationDbContext db)
{
    /// <summary>
    /// Orders placed by a customer after a Sent message of <paramref name="logs"/> and within <paramref name="windowDays"/> days of it, not Cancelled or Returned, where no later marketing
    /// message reached the customer between the message and the order (the newest touch wins).
    /// </summary>
    public async Task<AttributionDto> ForLogsAsync(IQueryable<NotificationLog> logs, int windowDays, CancellationToken cancellationToken)
    {
        var orders = db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned);
        var touches = db.NotificationLogs.AsNoTracking().Where(n => n.Category == NotificationCategory.Marketing && n.Status == NotificationStatus.Sent);
        var credited =
            (from l in logs.Where(x => x.Status == NotificationStatus.Sent && x.CustomerId != null)
             from o in orders.Where(o => o.CustomerId == l.CustomerId && o.PlacedAt >= l.SentAt && o.PlacedAt <= l.SentAt.AddDays(windowDays))
             where !touches.Any(n => n.CustomerId == l.CustomerId && n.Id != l.Id && n.SentAt > l.SentAt && n.SentAt <= o.PlacedAt)
             select new { o.Id, o.GrandTotal }).Distinct();
        var count = await credited.CountAsync(cancellationToken);
        var revenue = count == 0 ? 0m : await credited.SumAsync(x => x.GrandTotal, cancellationToken);
        return new AttributionDto(count, revenue, windowDays);
    }
}

/// <summary>Reads the customer facts a marketing message needs, for many customers at once.</summary>
public sealed class CustomerFacts(IApplicationDbContext db)
{
    public sealed record Facts(Guid Id, string? Name, string Mobile, string? Email, string? Language);

    public async Task<Dictionary<Guid, Facts>> LoadAsync(IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken) =>
        await db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id)).Select(c => new Facts(c.Id, c.FullName, c.Mobile, c.Email, c.PreferredLanguage)).ToDictionaryAsync(f => f.Id, cancellationToken);

    /// <summary>The customers of <paramref name="ids"/> whose latest decision on <paramref name="type"/> is "yes".</summary>
    public async Task<HashSet<Guid>> ConsentedAsync(IReadOnlyCollection<Guid> ids, ConsentType type, CancellationToken cancellationToken)
    {
        // Latest row per customer: newest first, first one seen wins. Only the rows of these customers and this type are read.
        var rows = await db.ConsentLogs.AsNoTracking().Where(l => ids.Contains(l.CustomerId) && l.Type == type).OrderByDescending(l => l.At).ThenByDescending(l => l.Id)
            .Select(l => new { l.CustomerId, l.Granted }).ToListAsync(cancellationToken);
        var seen = new HashSet<Guid>();
        var yes = new HashSet<Guid>();
        foreach (var row in rows)
        {
            if (seen.Add(row.CustomerId) && row.Granted)
            {
                yes.Add(row.CustomerId);
            }
        }

        return yes;
    }
}
