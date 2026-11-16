using Juvo.Application.Abstractions;
using Juvo.Application.Notifications;
using Juvo.Application.Orders;
using Juvo.Application.Storefront;
using Juvo.Domain.Customers;
using Juvo.Domain.Marketing;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Marketing;

/// <summary>What the four automations are: names, template codes, placeholders and the text they start with (FR-AC03). Bengali texts are a first pass for native review.</summary>
public static class AutomationCatalog
{
    public sealed record Definition(
        AutomationKind Kind, string Name, string Description, string TemplateCode, NotificationCategory Category, ConsentMode Consent, bool Unsubscribe, IReadOnlyList<string> Placeholders,
        int DelayMinutes, int WindowDays, int CapDays, string SubjectEn, string SubjectBn, string BodyEn, string BodyBn);

    public static readonly IReadOnlyList<Definition> All =
    [
        new(AutomationKind.AbandonedCart, "Abandoned cart", "A signed-in customer with items in the cart who did not order. DelayMinutes is how long the cart sat untouched; the message goes once per cart and customer cap.",
            "automation.abandoned-cart", NotificationCategory.Marketing, ConsentMode.Required, true, ["customerName", "shopName", "link", "itemCount", "unsubscribeLink"],
            180, 7, 7, "You left something in your cart", "আপনার কার্টে কিছু পণ্য রয়ে গেছে",
            "Hi {customerName}, you left {itemCount} item(s) in your {shopName} cart. Complete your order: {link}",
            "হ্যালো {customerName}, আপনার {shopName} কার্টে {itemCount}টি পণ্য রয়ে গেছে। অর্ডার সম্পূর্ণ করুন: {link}"),
        new(AutomationKind.BackInStock, "Back in stock", "Tells a customer who asked, once, when a sold-out variant has stock again. DelayMinutes is the minimum wait after the request.",
            "automation.back-in-stock", NotificationCategory.Transactional, ConsentMode.HonourOptOut, false, ["customerName", "shopName", "link", "productName", "variantName"],
            0, 365, 0, "{productName} is back in stock", "{productName} আবার স্টকে আছে",
            "Good news {customerName}: {productName} ({variantName}) is back in stock at {shopName}. Order now: {link}",
            "সুখবর {customerName}: {productName} ({variantName}) {shopName}-এ আবার স্টকে এসেছে। এখনই অর্ডার করুন: {link}"),
        new(AutomationKind.ReviewRequest, "Review request", "Asks for a review a number of days after delivery, once per order, unless the order was returned. DelayMinutes counts from the delivery.",
            "automation.review-request", NotificationCategory.Marketing, ConsentMode.Required, true, ["customerName", "shopName", "link", "orderNumber", "unsubscribeLink"],
            10080, 14, 0, "How was your order?", "আপনার অর্ডার কেমন লাগলো?",
            "Hi {customerName}, how did you like order {orderNumber} from {shopName}? Tell us: {link}",
            "হ্যালো {customerName}, {shopName}-এর অর্ডার {orderNumber} কেমন লাগলো? জানান: {link}"),
        new(AutomationKind.WinBack, "Win-back", "Customers who ordered before but have not ordered or signed in for DelayMinutes. Once per customer cap.",
            "automation.win-back", NotificationCategory.Marketing, ConsentMode.Required, true, ["customerName", "shopName", "link", "unsubscribeLink"],
            86400, 30, 90, "We miss you", "আপনাকে মিস করছি",
            "Hi {customerName}, we miss you at {shopName}! See what is new: {link}",
            "হ্যালো {customerName}, {shopName}-এ আপনাকে মিস করছি! নতুন কী এসেছে দেখুন: {link}"),
    ];

    public static Definition Of(AutomationKind kind) => All.First(d => d.Kind == kind);
}

/// <summary>One person an automation could message right now.</summary>
public sealed record AutomationCandidate(
    Guid? CustomerId, string Mobile, string? Email, string? Name, string? Language, string DedupeKey, Guid? SubscriptionId, IReadOnlyDictionary<string, string> Values, string? ProductNameBn = null);

/// <summary>The decision for one candidate: send, or skip with a reason (NoConsent, OptedOut, NoContact, FrequencyCap, AlreadySent, ChannelOff).</summary>
public sealed record AutomationDecision(AutomationCandidate Candidate, string? SkipCode);

public sealed record AutomationPass(AutomationKind Kind, bool Enabled, int Candidates, int Sent, int Skipped, int Deferred, IReadOnlyList<SkipCountDto> SkippedByReason);

/// <summary>
/// Finds who each automation should message now, applies consent, contact, frequency cap and dedupe, and sends through the shared notification path. The same code answers the dry run, so
/// the dry-run count is exactly what a real run would queue (FR-AC03). A run is idempotent: every message has a dedupe key and a second run finds nothing new.
/// </summary>
public sealed class AutomationEngine(
    IApplicationDbContext db, IUnitOfWork unitOfWork, NotificationService notifications, MarketingSettingsService settingsService, MarketingMessenger messenger, StockAvailability stock,
    SettingsService settings, AutomationSeeder seeder, TimeProvider clock)
{
    public const int BatchLimit = 500;

    private IQueryable<Order> Orders => db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking();

    private async Task<Automation> RowAsync(AutomationKind kind, CancellationToken cancellationToken)
    {
        var row = await db.Automations.FirstOrDefaultAsync(a => a.Kind == kind, cancellationToken);
        if (row is null)
        {
            await seeder.EnsureSeededAsync(cancellationToken);
            row = await db.Automations.FirstAsync(a => a.Kind == kind, cancellationToken);
        }

        return row;
    }

    // ---- Who ---------------------------------------------------------------------------------------------

    private async Task<List<AutomationCandidate>> CandidatesAsync(Automation a, MarketingSettings s, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var shop = messenger.ShopLink(s);
        var orders = Orders;
        switch (a.Kind)
        {
            case AutomationKind.AbandonedCart:
            {
                var idleBefore = now.AddMinutes(-a.DelayMinutes);
                var oldest = idleBefore.AddDays(-a.WindowDays);
                var rows = await (from k in db.Carts.AsNoTracking()
                                  join c in db.Customers.AsNoTracking() on k.CustomerId equals c.Id
                                  where c.IsActive && k.Items.Any() && k.ExpiresAt > now && (k.UpdatedAt ?? k.CreatedAt) <= idleBefore && (k.UpdatedAt ?? k.CreatedAt) >= oldest
                                        && !orders.Any(o => o.CustomerId == c.Id && o.PlacedAt >= (k.UpdatedAt ?? k.CreatedAt))
                                  orderby k.Id
                                  select new { CartId = k.Id, c.Id, c.Mobile, c.Email, c.FullName, c.PreferredLanguage, Activity = k.UpdatedAt ?? k.CreatedAt, Items = k.Items.Sum(i => i.Quantity) })
                    .Take(BatchLimit).ToListAsync(cancellationToken);
                return [.. rows.Select(r => new AutomationCandidate(r.Id, r.Mobile, r.Email, r.FullName, r.PreferredLanguage, $"automation:abandoned-cart:{r.CartId:N}:{r.Activity.UtcTicks}", null,
                    new Dictionary<string, string> { ["link"] = $"{shop}/cart", ["itemCount"] = r.Items.ToString(System.Globalization.CultureInfo.InvariantCulture) }))];
            }

            case AutomationKind.ReviewRequest:
            {
                var deliveredBefore = now.AddMinutes(-a.DelayMinutes);
                var oldest = deliveredBefore.AddDays(-a.WindowDays);
                var returns = db.ReturnRequests.IgnoreQueryFilters(["BranchScope"]).AsNoTracking();
                var rows = await (from o in orders
                                  join c in db.Customers.AsNoTracking() on o.CustomerId equals c.Id
                                  where c.IsActive && o.Status == OrderStatus.Delivered && o.DeliveredAt != null && o.DeliveredAt <= deliveredBefore && o.DeliveredAt >= oldest
                                        && !returns.Any(r => r.OrderId == o.Id && r.Status != ReturnStatus.Rejected && r.Status != ReturnStatus.Cancelled)
                                  orderby o.Id
                                  select new { OrderId = o.Id, o.Number, c.Id, c.Mobile, c.Email, c.FullName, c.PreferredLanguage }).Take(BatchLimit).ToListAsync(cancellationToken);
                return [.. rows.Select(r => new AutomationCandidate(r.Id, r.Mobile, r.Email, r.FullName, r.PreferredLanguage, $"automation:review-request:{r.OrderId:N}", null,
                    new Dictionary<string, string> { ["link"] = $"{shop}/account/orders/{r.Number}", ["orderNumber"] = r.Number }))];
            }

            case AutomationKind.WinBack:
            {
                var cutoff = now.AddMinutes(-a.DelayMinutes);
                var rows = await (from c in db.Customers.AsNoTracking()
                                  where c.IsActive && c.CreatedAt < cutoff && (c.LastLoginAt == null || c.LastLoginAt < cutoff)
                                        && orders.Any(o => o.CustomerId == c.Id && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned)
                                        && !orders.Any(o => o.CustomerId == c.Id && o.PlacedAt >= cutoff)
                                  orderby c.Id
                                  select new { c.Id, c.Mobile, c.Email, c.FullName, c.PreferredLanguage, LastOrder = orders.Where(o => o.CustomerId == c.Id).Max(o => (DateTimeOffset?)o.PlacedAt) })
                    .Take(BatchLimit).ToListAsync(cancellationToken);
                return [.. rows.Select(r => new AutomationCandidate(r.Id, r.Mobile, r.Email, r.FullName, r.PreferredLanguage, $"automation:win-back:{r.Id:N}:{r.LastOrder?.UtcDateTime:yyyyMMdd}", null,
                    new Dictionary<string, string> { ["link"] = shop }))];
            }

            default:
                return await BackInStockCandidatesAsync(a, shop, now, cancellationToken);
        }
    }

    private async Task<List<AutomationCandidate>> BackInStockCandidatesAsync(Automation a, string shop, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var minAge = now.AddMinutes(-a.DelayMinutes);
        var pending = await (from sub in db.BackInStockSubscriptions.AsNoTracking()
                             join v in db.ProductVariants.AsNoTracking() on sub.VariantId equals v.Id
                             join p in db.Products.AsNoTracking() on sub.ProductId equals p.Id
                             where sub.NotifiedAt == null && sub.CreatedAt <= minAge && v.IsActive && v.TracksStock && p.Status == Juvo.Domain.Catalog.ProductStatus.Published
                             orderby sub.Id
                             select new { sub.Id, sub.Mobile, sub.Email, sub.CustomerId, VariantId = v.Id, p.Slug, p.NameEn, p.NameBn, v.ColorName, v.Size })
            .Take(BatchLimit * 4).ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return [];
        }

        var available = await stock.AvailableAsync(pending.Select(p => p.VariantId).Distinct().ToList(), cancellationToken);
        var ready = pending.Where(p => available.GetValueOrDefault(p.VariantId) > 0).Take(BatchLimit).ToList();
        var customerIds = ready.Where(r => r.CustomerId != null).Select(r => r.CustomerId!.Value).ToList();
        var customers = await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).Select(c => new { c.Id, c.FullName, c.PreferredLanguage, c.Email }).ToDictionaryAsync(c => c.Id, cancellationToken);
        return [.. ready.Select(r =>
        {
            customers.TryGetValue(r.CustomerId ?? Guid.Empty, out var customer);
            var variantName = string.Join(' ', new[] { r.ColorName, r.Size }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return new AutomationCandidate(r.CustomerId, r.Mobile, r.Email ?? customer?.Email, customer?.FullName, customer?.PreferredLanguage, $"automation:back-in-stock:{r.Id:N}", r.Id,
                new Dictionary<string, string> { ["link"] = $"{shop}/products/{r.Slug}", ["productName"] = r.NameEn, ["variantName"] = variantName }, r.NameBn);
        })];
    }

    // ---- Decisions ---------------------------------------------------------------------------------------------

    private async Task<List<AutomationDecision>> DecideAsync(Automation a, AutomationCatalog.Definition def, IReadOnlyList<AutomationCandidate> candidates, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var email = a.Channel == NotificationChannel.Email;
        var type = email ? ConsentType.MarketingEmail : ConsentType.MarketingSms;
        var ids = candidates.Where(c => c.CustomerId != null).Select(c => c.CustomerId!.Value).Distinct().ToList();
        var latest = new Dictionary<Guid, bool>();
        if (ids.Count > 0)
        {
            var rows = await db.ConsentLogs.AsNoTracking().Where(l => ids.Contains(l.CustomerId) && l.Type == type).OrderByDescending(l => l.At).ThenByDescending(l => l.Id).Select(l => new { l.CustomerId, l.Granted }).ToListAsync(cancellationToken);
            foreach (var row in rows)
            {
                latest.TryAdd(row.CustomerId, row.Granted);
            }
        }

        var capFrom = now.AddDays(-a.CapDays);
        var recent = new HashSet<Guid>();
        if (a.CapDays > 0 && ids.Count > 0)
        {
            recent = [.. await db.NotificationLogs.AsNoTracking().Where(l => l.CustomerId != null && ids.Contains(l.CustomerId.Value) && l.Template == def.TemplateCode && l.QueuedAt >= capFrom && l.Status != NotificationStatus.Skipped)
                .Select(l => l.CustomerId!.Value).Distinct().ToListAsync(cancellationToken)];
        }

        var keys = candidates.Select(c => c.DedupeKey).ToList();
        var channel = a.Channel;
        var used = (await db.NotificationLogs.AsNoTracking().Where(l => l.DedupeKey != null && keys.Contains(l.DedupeKey) && l.Channel == channel).Select(l => l.DedupeKey!).ToListAsync(cancellationToken)).ToHashSet();
        var channelOn = await ChannelOnAsync(channel, cancellationToken);

        var decisions = new List<AutomationDecision>();
        var seenCustomers = new HashSet<Guid>();
        foreach (var c in candidates)
        {
            string? skip = null;
            if (!channelOn)
            {
                skip = "ChannelOff";
            }
            else if (used.Contains(c.DedupeKey))
            {
                skip = "AlreadySent";
            }
            else if (string.IsNullOrWhiteSpace(email ? c.Email : c.Mobile))
            {
                skip = "NoContact";
            }
            else if (def.Consent == ConsentMode.Required && !(c.CustomerId is { } cid && latest.GetValueOrDefault(cid)))
            {
                skip = "NoConsent";
            }
            else if (def.Consent == ConsentMode.HonourOptOut && c.CustomerId is { } oid && latest.TryGetValue(oid, out var granted) && !granted)
            {
                skip = "OptedOut";
            }
            else if (a.CapDays > 0 && c.CustomerId is { } capId && (recent.Contains(capId) || !seenCustomers.Add(capId)))
            {
                skip = "FrequencyCap";
            }

            decisions.Add(new AutomationDecision(c, skip));
        }

        return decisions;
    }

    private async Task<bool> ChannelOnAsync(NotificationChannel channel, CancellationToken cancellationToken)
    {
        var key = channel == NotificationChannel.Email ? NotificationSettingKeys.EmailEnabled : NotificationSettingKeys.SmsEnabled;
        var values = await settings.ReadAsync([key], cancellationToken);
        return !(values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var on) && !on);
    }

    private static List<SkipCountDto> Tally(IEnumerable<AutomationDecision> decisions) =>
        [.. decisions.Where(d => d.SkipCode is not null).GroupBy(d => d.SkipCode!).Select(g => new SkipCountDto(g.Key, g.Count())).OrderBy(x => x.Reason, StringComparer.Ordinal)];

    // ---- Dry run and run -------------------------------------------------------------------------------------

    /// <summary>What a run would do now, without writing anything.</summary>
    public async Task<AutomationDryRunDto> DryRunAsync(CancellationToken cancellationToken)
    {
        var s = await settingsService.ReadAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var quiet = MarketingLogic.IsQuiet(now, s.QuietHoursEnabled, s.QuietStart, s.QuietEnd);
        await seeder.EnsureSeededAsync(cancellationToken);
        var items = new List<AutomationDryRunItemDto>();
        foreach (var kind in Enum.GetValues<AutomationKind>())
        {
            var a = await RowAsync(kind, cancellationToken);
            var def = AutomationCatalog.Of(kind);
            var candidates = await CandidatesAsync(a, s, now, cancellationToken);
            var decisions = await DecideAsync(a, def, candidates, now, cancellationToken);
            var sendable = decisions.Count(d => d.SkipCode is null);
            var deferred = a.Channel == NotificationChannel.Sms && quiet ? sendable : 0;
            items.Add(new AutomationDryRunItemDto(kind, def.Name, a.IsEnabled, candidates.Count, a.IsEnabled ? sendable - deferred : 0, sendable, a.IsEnabled ? deferred : 0, Tally(decisions)));
        }

        return new AutomationDryRunDto(now, quiet, items);
    }

    /// <summary>Runs every switched-on automation once.</summary>
    public async Task<IReadOnlyList<AutomationPass>> RunAllAsync(CancellationToken cancellationToken)
    {
        await seeder.EnsureSeededAsync(cancellationToken);
        var passes = new List<AutomationPass>();
        foreach (var kind in Enum.GetValues<AutomationKind>())
        {
            passes.Add(await RunAsync(kind, cancellationToken));
        }

        return passes;
    }

    public async Task<AutomationPass> RunAsync(AutomationKind kind, CancellationToken cancellationToken)
    {
        var enabled = await db.Automations.AsNoTracking().Where(a => a.Kind == kind).Select(a => a.IsEnabled).FirstOrDefaultAsync(cancellationToken);
        if (!enabled)
        {
            return new AutomationPass(kind, false, 0, 0, 0, 0, []);
        }

        return await unitOfWork.ExecuteInTransactionAsync(ct => RunLockedAsync(kind, ct), cancellationToken);
    }

    private async Task<AutomationPass> RunLockedAsync(AutomationKind kind, CancellationToken cancellationToken)
    {
        await db.AcquireTransactionLockAsync($"automation:{kind}", cancellationToken);
        var a = await RowAsync(kind, cancellationToken);
        if (!a.IsEnabled)
        {
            return new AutomationPass(kind, false, 0, 0, 0, 0, []);
        }

        var def = AutomationCatalog.Of(kind);
        var s = await settingsService.ReadAsync(cancellationToken);
        var now = clock.GetUtcNow();
        var candidates = await CandidatesAsync(a, s, now, cancellationToken);
        var decisions = await DecideAsync(a, def, candidates, now, cancellationToken);
        var quiet = a.Channel == NotificationChannel.Sms && MarketingLogic.IsQuiet(now, s.QuietHoursEnabled, s.QuietStart, s.QuietEnd);
        var defaults = await settings.ReadAsync([NotificationSettingKeys.DefaultLanguage], cancellationToken);
        var defaultLanguage = NotificationService.NormalizeLanguage(defaults.GetValueOrDefault(NotificationSettingKeys.DefaultLanguage)) is { Length: > 0 } d ? d : NotificationCatalog.DefaultLanguage;
        var subscriptions = decisions.Where(x => x.Candidate.SubscriptionId != null).Select(x => x.Candidate.SubscriptionId!.Value).ToList();
        var tracked = subscriptions.Count == 0 ? new Dictionary<Guid, BackInStockSubscription>() : await db.BackInStockSubscriptions.Where(x => subscriptions.Contains(x.Id)).ToDictionaryAsync(x => x.Id, cancellationToken);

        var sent = 0;
        var deferred = 0;
        var skippedLate = new List<AutomationDecision>();
        foreach (var decision in decisions)
        {
            var c = decision.Candidate;
            if (decision.SkipCode is { } code)
            {
                // A one-off request that can never be answered (no way to reach them, opted out) is closed; everything else is left for a later run.
                if (c.SubscriptionId is { } sid && code is "NoContact" or "OptedOut" && tracked.TryGetValue(sid, out var sub))
                {
                    sub.Complete("skipped:" + code, now);
                }

                if (c.SubscriptionId is { } sid2 && code == "AlreadySent" && tracked.TryGetValue(sid2, out var done))
                {
                    done.Complete("sent", now);
                }

                continue;
            }

            if (quiet)
            {
                deferred++;
                continue;
            }

            var language = NotificationService.NormalizeLanguage(c.Language) is { Length: > 0 } l ? l : defaultLanguage;
            var bodyTemplate = language == "bn" ? a.BodyBn : a.BodyEn;
            var subjectTemplate = language == "bn" ? (a.SubjectBn ?? a.SubjectEn) : a.SubjectEn;
            var values = new Dictionary<string, string>(c.Values) { ["customerName"] = CampaignDispatchService.FirstName(c.Name) };
            if (language == "bn" && !string.IsNullOrWhiteSpace(c.ProductNameBn))
            {
                values["productName"] = c.ProductNameBn;
            }

            var (subject, body) = messenger.Compose(a.Channel, subjectTemplate, bodyTemplate, values, s, c.CustomerId ?? Guid.Empty, null, def.Unsubscribe && c.CustomerId is not null);
            var recipient = (a.Channel == NotificationChannel.Email ? c.Email : c.Mobile)!.Trim();
            var result = await notifications.QueueCustomAsync(new CustomMessage(a.Channel, recipient, def.TemplateCode, subject, body, language, def.Category, c.CustomerId, c.DedupeKey, def.Consent), cancellationToken);
            if (result.Outcome == CustomOutcome.Queued)
            {
                sent++;
                if (c.SubscriptionId is { } id && tracked.TryGetValue(id, out var s1))
                {
                    s1.Complete("sent", now);
                }
            }
            else
            {
                skippedLate.Add(new AutomationDecision(c, result.SkipCode ?? "AlreadySent"));
                if (c.SubscriptionId is { } id && result.SkipCode is "NoConsent" or "OptedOut" && tracked.TryGetValue(id, out var s2))
                {
                    s2.Complete("skipped:" + result.SkipCode, now);
                }
            }
        }

        a.RecordRun(now);
        await db.SaveChangesAsync(cancellationToken);
        var all = decisions.Where(d => d.SkipCode is not null).Concat(skippedLate).ToList();
        return new AutomationPass(kind, true, candidates.Count, sent, all.Count, deferred, Tally(all));
    }
}
