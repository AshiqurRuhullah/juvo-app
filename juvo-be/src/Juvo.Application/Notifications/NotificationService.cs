using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Customers;
using Juvo.Domain.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juvo.Application.Notifications;

/// <summary>Keys of the notification settings (table <c>PlatformSettings</c>).</summary>
public static class NotificationSettingKeys
{
    public const string SmsEnabled = "Notifications.SmsEnabled";
    public const string EmailEnabled = "Notifications.EmailEnabled";
    public const string WhatsAppEnabled = "Notifications.WhatsAppEnabled";
    public const string DefaultLanguage = "Notifications.DefaultLanguage";
}

/// <summary>
/// Template-driven notifications (module K). An event handler asks for a template code; this finds the customer's language and consent, picks the template text for every
/// channel the customer can be reached on (SMS, email, WhatsApp), renders the placeholders and writes one log row per channel in state Pending together with an outbox
/// message that delivers it later with retry and backoff. Transactional messages (orders, payments, sign-in codes) are always allowed; marketing messages need the customer's
/// consent and are otherwise logged as Skipped.
/// </summary>
public sealed class NotificationService(IApplicationDbContext db, IOutboxWriter outbox, SettingsService settings, ICurrentUser currentUser, TimeProvider clock)
{
    public static string NormalizeLanguage(string? language) => string.Equals(language?.Trim(), "bn", StringComparison.OrdinalIgnoreCase) ? "bn" : string.Equals(language?.Trim(), "en", StringComparison.OrdinalIgnoreCase) ? "en" : string.Empty;

    private async Task<(bool Sms, bool Email, bool WhatsApp, string Language)> ChannelsAsync(CancellationToken cancellationToken)
    {
        var v = await settings.ReadAsync([NotificationSettingKeys.SmsEnabled, NotificationSettingKeys.EmailEnabled, NotificationSettingKeys.WhatsAppEnabled, NotificationSettingKeys.DefaultLanguage], cancellationToken);
        bool On(string key, bool fallback) => v.TryGetValue(key, out var raw) && bool.TryParse(raw, out var on) ? on : fallback;
        var language = NormalizeLanguage(v.GetValueOrDefault(NotificationSettingKeys.DefaultLanguage));
        return (On(NotificationSettingKeys.SmsEnabled, true), On(NotificationSettingKeys.EmailEnabled, true), On(NotificationSettingKeys.WhatsAppEnabled, false), language.Length == 0 ? NotificationCatalog.DefaultLanguage : language);
    }

    /// <summary>The template text for a code, channel and language: the staff-edited row when there is an active one, otherwise the built-in default; falls back to English. Null when there is no text.</summary>
    public async Task<(string? Subject, string Body, string Language, int Version, NotificationCategory Category)?> ResolveAsync(string code, NotificationChannel channel, string language, CancellationToken cancellationToken)
    {
        var definition = NotificationCatalog.Find(code);
        if (definition is null)
        {
            return null;
        }

        foreach (var lang in language == NotificationCatalog.DefaultLanguage ? [language] : new[] { language, NotificationCatalog.DefaultLanguage })
        {
            var row = await db.NotificationTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Code == definition.Code && t.Channel == channel && t.Language == lang, cancellationToken);
            if (row is not null)
            {
                if (row.IsActive)
                {
                    return (row.Subject, row.Body, lang, row.Version, row.Category);
                }

                continue; // switched off by staff: do not fall back to the built-in text of the same language
            }

            if (NotificationCatalog.DefaultFor(definition.Code, channel, lang) is { } fallback)
            {
                return (fallback.Subject, fallback.Body, lang, 0, definition.Category);
            }
        }

        return null;
    }

    /// <summary>Renders one template to text (for messages sent at once, such as sign-in codes). Null when the template has no text for the channel.</summary>
    public async Task<RenderedTemplate?> RenderAsync(string code, NotificationChannel channel, string? language, IReadOnlyDictionary<string, string> variables, CancellationToken cancellationToken)
    {
        var lang = NormalizeLanguage(language);
        if (lang.Length == 0)
        {
            lang = (await ChannelsAsync(cancellationToken)).Language;
        }

        if (await ResolveAsync(code, channel, lang, cancellationToken) is not { } t)
        {
            return null;
        }

        return new RenderedTemplate(t.Subject is null ? null : NotificationCatalog.Render(t.Subject, variables), NotificationCatalog.Render(t.Body, variables), t.Language, t.Version);
    }

    /// <summary>The language of a customer's messages: their choice, else the shop default.</summary>
    public async Task<string> LanguageOfAsync(string? mobile, Guid? customerId, CancellationToken cancellationToken)
    {
        string? preferred = null;
        if (customerId is { } id)
        {
            preferred = await db.Customers.AsNoTracking().Where(c => c.Id == id).Select(c => c.PreferredLanguage).FirstOrDefaultAsync(cancellationToken);
        }
        else if (!string.IsNullOrEmpty(mobile))
        {
            preferred = await db.Customers.AsNoTracking().Where(c => c.Mobile == mobile).Select(c => c.PreferredLanguage).FirstOrDefaultAsync(cancellationToken);
        }

        var lang = NormalizeLanguage(preferred);
        return lang.Length > 0 ? lang : (await ChannelsAsync(cancellationToken)).Language;
    }

    private async Task<bool> HasConsentAsync(Guid? customerId, ConsentType type, CancellationToken cancellationToken)
    {
        if (customerId is not { } id)
        {
            return false;
        }

        var latest = await db.ConsentLogs.AsNoTracking().Where(c => c.CustomerId == id && c.Type == type).OrderByDescending(c => c.At).ThenByDescending(c => c.Id).Select(c => (bool?)c.Granted).FirstOrDefaultAsync(cancellationToken);
        return latest == true;
    }

    /// <summary>Queues the template for every channel the customer can be reached on. Saves. Safe to call twice with the same dedupe key.</summary>
    public async Task NotifyAsync(NotifyRequest request, CancellationToken cancellationToken)
    {
        var definition = NotificationCatalog.Find(request.TemplateCode);
        if (definition is null)
        {
            return;
        }

        var customer = request.CustomerId is { } id
            ? await db.Customers.AsNoTracking().Where(c => c.Id == id).Select(c => new { c.Id, c.Email, c.PreferredLanguage, c.FullName }).FirstOrDefaultAsync(cancellationToken)
            : await db.Customers.AsNoTracking().Where(c => c.Mobile == request.Mobile).Select(c => new { c.Id, c.Email, c.PreferredLanguage, c.FullName }).FirstOrDefaultAsync(cancellationToken);
        var channels = await ChannelsAsync(cancellationToken);
        var language = NormalizeLanguage(customer?.PreferredLanguage);
        if (language.Length == 0)
        {
            language = channels.Language;
        }

        var variables = new Dictionary<string, string>(request.Variables, StringComparer.Ordinal);
        if (!variables.TryGetValue("Name", out var given) || string.IsNullOrWhiteSpace(given))
        {
            variables["Name"] = customer?.FullName ?? string.Empty;
        }

        var email = string.IsNullOrWhiteSpace(request.Email) ? customer?.Email : request.Email;
        var plan = new List<(NotificationChannel Channel, string Recipient, ConsentType Consent)>();
        if (channels.Sms && !string.IsNullOrWhiteSpace(request.Mobile))
        {
            plan.Add((NotificationChannel.Sms, request.Mobile, ConsentType.MarketingSms));
        }

        if (channels.Email && !string.IsNullOrWhiteSpace(email))
        {
            plan.Add((NotificationChannel.Email, email.Trim(), ConsentType.MarketingEmail));
        }

        if (channels.WhatsApp && !string.IsNullOrWhiteSpace(request.Mobile))
        {
            plan.Add((NotificationChannel.WhatsApp, request.Mobile, ConsentType.MarketingSms));
        }

        var now = clock.GetUtcNow();
        foreach (var (channel, recipient, consent) in plan)
        {
            if (await ResolveAsync(definition.Code, channel, language, cancellationToken) is not { } text)
            {
                continue; // no text for this channel
            }

            if (await db.NotificationLogs.AnyAsync(n => n.DedupeKey == request.DedupeKey && n.Channel == channel, cancellationToken))
            {
                continue; // already queued (the outbox delivers an event at least once)
            }

            if (text.Category == NotificationCategory.Marketing && !await HasConsentAsync(customer?.Id, consent, cancellationToken))
            {
                db.NotificationLogs.Add(NotificationLog.Skip(channel, recipient, definition.Code, text.Language, text.Category, customer?.Id, "The customer has not agreed to marketing messages on this channel.", request.DedupeKey, now));
                continue;
            }

            var subject = text.Subject is null ? null : NotificationCatalog.Render(text.Subject, variables);
            var log = NotificationLog.Queue(channel, recipient, definition.Code, subject, NotificationCatalog.Render(text.Body, variables), text.Language, text.Version, text.Category, customer?.Id,
                request.DedupeKey, currentUser.CorrelationId, now);
            db.NotificationLogs.Add(log);
            outbox.Enqueue("notification.deliver", new NotificationDeliverMessage(log.Id));
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Staff: resend -------------------------------------------------------------------------------------

    /// <summary>Puts a dead-lettered or failed message back in the queue.</summary>
    internal async Task<bool> RequeueAsync(NotificationLog log, CancellationToken cancellationToken)
    {
        if (log.Status is not (NotificationStatus.DeadLetter or NotificationStatus.Failed))
        {
            return false;
        }

        log.Requeue(clock.GetUtcNow());
        outbox.Enqueue("notification.deliver", new NotificationDeliverMessage(log.Id));
        await db.SaveChangesAsync(cancellationToken);
        return true;
    }
}

/// <summary>
/// Delivers a queued notification through its channel's sender (outbox message <c>notification.deliver</c>). A failed attempt is recorded and the handler throws so the outbox
/// retries with exponential backoff; after the configured attempts the log row becomes DeadLetter (the outbox message is then finished, and staff can resend from the log).
/// Idempotent: a message that is already Sent is left alone.
/// </summary>
public sealed class NotificationDeliverHandler(IApplicationDbContext db, IEnumerable<INotificationSender> senders, IOptions<NotificationDeliveryOptions> options, TimeProvider clock) : IOutboxMessageHandler
{
    public string MessageType => "notification.deliver";

    public async Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var message = JsonSerializer.Deserialize<NotificationDeliverMessage>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("The notification message has no content.");
        var log = await db.NotificationLogs.FirstOrDefaultAsync(n => n.Id == message.LogId, cancellationToken);
        if (log is null || log.Status is NotificationStatus.Sent or NotificationStatus.Skipped or NotificationStatus.DeadLetter)
        {
            return;
        }

        var sender = senders.LastOrDefault(s => s.Channel == log.Channel);
        SendOutcome outcome;
        if (sender is null)
        {
            outcome = SendOutcome.Fail($"No sender is set up for {log.Channel}.");
        }
        else
        {
            try
            {
                outcome = await sender.SendAsync(new OutboundMessage(log.Id, log.Channel, log.Recipient, log.Subject, log.Body, log.Template), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                outcome = SendOutcome.Fail(ex.Message);
            }
        }

        var now = clock.GetUtcNow();
        if (outcome.Success)
        {
            log.MarkSent(outcome.ProviderResponse, now);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var final = context.Attempt >= Math.Max(1, options.Value.MaxAttempts);
        log.MarkAttemptFailed(outcome.Error ?? "The message was not sent.", final, now);
        await db.SaveChangesAsync(cancellationToken);
        if (!final)
        {
            throw new InvalidOperationException(outcome.Error ?? "The message was not sent.");
        }
    }
}

/// <summary>Reacts to events by queueing template messages. Shared by the order, payment and return handlers.</summary>
public static class NotifyVariables
{
    public static Dictionary<string, string> Of(params (string Key, string? Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value ?? string.Empty);

    public static string Money(decimal amount) => amount.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
}
