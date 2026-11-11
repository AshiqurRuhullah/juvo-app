using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Application.Ums;
using Juvo.Domain.Notifications;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Notifications;

/// <summary>
/// Staff side of notifications (module K): browse and edit templates (every edit is a new version, the old text is kept), preview a template with sample values, read the
/// delivery log, resend a message that failed, switch channels, and the signed-in customer's own message language. Permissions are global (messages are not branch data).
/// </summary>
public sealed class NotificationAdminService(
    IApplicationDbContext db, IUnitOfWork uow, ActorContext actors, ICurrentUser currentUser, NotificationService notifications, SettingsService settings, TimeProvider clock)
{
    private static readonly NotificationChannel[] EditableChannels = [NotificationChannel.Sms, NotificationChannel.Email, NotificationChannel.WhatsApp];

    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private async Task<(Actor? Actor, Error? Refusal)> RequireAsync(string permission, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return (null, Error.Unauthorized());
        }

        return actor.Access.HasAnywhere(permission) ? (actor, null) : (null, Error.Forbidden());
    }

    // ---- Templates -------------------------------------------------------------------------------------------

    private static TemplateDto FromDefault(TemplateDefinition d, TemplateDefault t) =>
        new(d.Code, d.Description, t.Channel, t.Language, t.Subject, t.Body, d.Category, 0, true, false, d.Variables, null, null);

    private static TemplateDto FromRow(TemplateDefinition d, NotificationTemplate t) =>
        new(d.Code, d.Description, t.Channel, t.Language, t.Subject, t.Body, t.Category, t.Version, t.IsActive, true, d.Variables, t.EditedByName, t.EditedAt);

    /// <summary>Every template: the built-in defaults with the staff-edited rows on top, plus edited rows for channels that have no default.</summary>
    public async Task<Result<IReadOnlyList<TemplateDto>>> ListTemplatesAsync(string? code, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Notifications.TemplateView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var rows = await db.NotificationTemplates.AsNoTracking().ToListAsync(cancellationToken);
        var list = new List<TemplateDto>();
        foreach (var d in NotificationCatalog.Definitions.Where(d => string.IsNullOrWhiteSpace(code) || string.Equals(d.Code, code.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            var seen = new HashSet<(NotificationChannel, string)>();
            foreach (var t in NotificationCatalog.Defaults.Where(t => t.Code == d.Code))
            {
                seen.Add((t.Channel, t.Language));
                var row = rows.FirstOrDefault(r => r.Code == d.Code && r.Channel == t.Channel && r.Language == t.Language);
                list.Add(row is null ? FromDefault(d, t) : FromRow(d, row));
            }

            list.AddRange(rows.Where(r => r.Code == d.Code && !seen.Contains((r.Channel, r.Language))).Select(r => FromRow(d, r)));
        }

        return Result.Success<IReadOnlyList<TemplateDto>>([.. list.OrderBy(t => t.Code).ThenBy(t => t.Channel).ThenBy(t => t.Language)]);
    }

    private static Error? ValidateKey(string code, NotificationChannel channel, string language, out TemplateDefinition? definition)
    {
        definition = NotificationCatalog.Find(code);
        if (definition is null)
        {
            return Error.NotFound(ErrorCodes.Notify.TemplateNotFound, "There is no template with this code.");
        }

        if (!EditableChannels.Contains(channel))
        {
            return new Error(ErrorCodes.Notify.TemplateInvalid, "Templates exist for SMS, email and WhatsApp.", ErrorType.Validation, new Dictionary<string, string[]> { ["channel"] = ["Choose SMS, Email or WhatsApp."] });
        }

        return NotificationService.NormalizeLanguage(language).Length == 0
            ? new Error(ErrorCodes.Notify.LanguageInvalid, "The language is en or bn.", ErrorType.Validation, new Dictionary<string, string[]> { ["language"] = ["Use en or bn."] })
            : null;
    }

    private static Error? ValidateText(TemplateDefinition definition, NotificationChannel channel, string? subject, string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > 2000)
        {
            return new Error(ErrorCodes.Notify.TemplateInvalid, "Write the message text (up to 2000 characters).", ErrorType.Validation, new Dictionary<string, string[]> { ["body"] = ["Write the message text (up to 2000 characters)."] });
        }

        if (channel == NotificationChannel.Email && (string.IsNullOrWhiteSpace(subject) || subject.Length > 300))
        {
            return new Error(ErrorCodes.Notify.TemplateInvalid, "An email needs a subject (up to 300 characters).", ErrorType.Validation, new Dictionary<string, string[]> { ["subject"] = ["An email needs a subject (up to 300 characters)."] });
        }

        var unknown = NotificationCatalog.PlaceholdersIn(body).Concat(NotificationCatalog.PlaceholdersIn(subject)).Where(p => !definition.Variables.Contains(p)).Distinct().ToList();
        return unknown.Count == 0
            ? null
            : new Error(ErrorCodes.Notify.UnknownPlaceholder, $"These placeholders are not available for {definition.Code}: {string.Join(", ", unknown.Select(u => "{{" + u + "}}"))}. Available: {string.Join(", ", definition.Variables.Select(v => "{{" + v + "}}"))}.",
                ErrorType.Validation, new Dictionary<string, string[]> { ["body"] = [$"Unknown placeholders: {string.Join(", ", unknown)}."] });
    }

    public async Task<Result<TemplateDto>> UpdateTemplateAsync(string code, NotificationChannel channel, string language, UpdateTemplateRequest request, CancellationToken cancellationToken)
    {
        var (actor, refusal) = await RequireAsync(Permissions.Notifications.TemplateEdit, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        language = NotificationService.NormalizeLanguage(language);
        if (ValidateKey(code, channel, language, out var definition) is { } badKey)
        {
            return badKey;
        }

        var subject = request.Subject?.Trim();
        var body = request.Body?.Trim();
        if (ValidateText(definition!, channel, subject, body) is { } badText)
        {
            return badText;
        }

        return await uow.ExecuteInTransactionAsync<Result<TemplateDto>>(async ct =>
        {
            var now = clock.GetUtcNow();
            var row = await db.NotificationTemplates.FirstOrDefaultAsync(t => t.Code == definition!.Code && t.Channel == channel && t.Language == language, ct);
            var userName = currentUser.UserName ?? "Staff";
            if (row is null)
            {
                // The first edit of a built-in text: the row is version 1, the built-in text stays in the history as version 0.
                row = new NotificationTemplate(definition!.Code, channel, language, channel == NotificationChannel.Email ? subject : null, body!, definition.Category, request.IsActive, actor!.Id, userName, now);
                db.NotificationTemplates.Add(row);
                if (NotificationCatalog.DefaultFor(definition.Code, channel, language) is { } original)
                {
                    db.NotificationTemplateVersions.Add(NotificationTemplateVersion.Original(row.Id, original.Subject, original.Body, now));
                }
            }
            else
            {
                db.NotificationTemplateVersions.Add(row.Edit(channel == NotificationChannel.Email ? subject : null, body!, request.IsActive, actor!.Id, userName, now));
            }

            await db.SaveChangesAsync(ct);
            return FromRow(definition!, row);
        }, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<TemplateVersionDto>>> HistoryAsync(string code, NotificationChannel channel, string language, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Notifications.TemplateView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        language = NotificationService.NormalizeLanguage(language);
        if (ValidateKey(code, channel, language, out var definition) is { } badKey)
        {
            return badKey;
        }

        var row = await db.NotificationTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Code == definition!.Code && t.Channel == channel && t.Language == language, cancellationToken);
        if (row is null)
        {
            return Result.Success<IReadOnlyList<TemplateVersionDto>>([]);
        }

        var versions = await db.NotificationTemplateVersions.AsNoTracking().Where(v => v.TemplateId == row.Id).OrderByDescending(v => v.Version).ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<TemplateVersionDto>>([new TemplateVersionDto(row.Version, row.Subject, row.Body, row.EditedByName, row.EditedAt, true), .. versions.Select(v => new TemplateVersionDto(v.Version, v.Subject, v.Body, v.EditedByName, v.EditedAt, false))]);
    }

    private static readonly Dictionary<string, string> Samples = new(StringComparer.Ordinal)
    {
        ["OrderNumber"] = "JV-DHK01-2611-000123", ["Total"] = "1250", ["Amount"] = "1250", ["Name"] = "Nadia Rahman", ["Reason"] = "Reference not found", ["Code"] = "482913", ["Minutes"] = "5",
        ["InvoiceNumber"] = "INV-DHK01-2611-000045", ["ReturnNumber"] = "RT-DHK01-2611-000007", ["Kind"] = "return", ["KindBn"] = "রিটার্ন", ["Message"] = "Eid sale starts today",
    };

    /// <summary>Renders a text (the one in the request, or the current template) with sample values and says which placeholders are unknown.</summary>
    public async Task<Result<PreviewDto>> PreviewAsync(PreviewTemplateRequest request, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Notifications.TemplateView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var language = NotificationService.NormalizeLanguage(request.Language);
        if (language.Length == 0)
        {
            language = NotificationCatalog.DefaultLanguage;
        }

        if (ValidateKey(request.Code ?? string.Empty, request.Channel, language, out var definition) is { } badKey)
        {
            return badKey;
        }

        var subject = request.Subject;
        var body = request.Body;
        if (string.IsNullOrWhiteSpace(body))
        {
            if (await notifications.ResolveAsync(definition!.Code, request.Channel, language, cancellationToken) is not { } current)
            {
                return Error.NotFound(ErrorCodes.Notify.TemplateNotFound, "This template has no text for this channel and language yet.");
            }

            subject = current.Subject;
            body = current.Body;
        }

        var values = new Dictionary<string, string>(definition!.Variables.ToDictionary(v => v, v => Samples.GetValueOrDefault(v, v)), StringComparer.Ordinal);
        foreach (var (key, value) in request.Variables ?? new Dictionary<string, string>())
        {
            values[key] = value;
        }

        var used = NotificationCatalog.PlaceholdersIn(body).Concat(NotificationCatalog.PlaceholdersIn(subject)).Distinct().ToList();
        var rendered = NotificationCatalog.Render(body, values);
        return new PreviewDto(subject is null ? null : NotificationCatalog.Render(subject, values), rendered, [.. used.Where(u => !definition.Variables.Contains(u))], [.. definition.Variables.Where(v => !used.Contains(v))], rendered.Length);
    }

    // ---- Log ---------------------------------------------------------------------------------------------------

    private static NotificationLogDto ToDto(NotificationLog n) =>
        new(n.Id, n.Channel, n.Recipient, n.Template, n.Subject, n.Body, n.Status, n.Language, n.TemplateVersion, n.Category, n.Attempts, n.LastError, n.ProviderResponse, n.QueuedAt, n.SentAt);

    public async Task<Result<PagedResult<NotificationLogDto>>> ListLogsAsync(PageRequest request, NotificationLogListQuery filter, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Notifications.LogView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var query = db.NotificationLogs.AsNoTracking().AsQueryable();
        if (filter.Status is { } status)
        {
            query = query.Where(n => n.Status == status);
        }

        if (filter.Channel is { } channel)
        {
            query = query.Where(n => n.Channel == channel);
        }

        if (!string.IsNullOrWhiteSpace(filter.Template))
        {
            var template = filter.Template.Trim();
            query = query.Where(n => n.Template == template || n.Template.StartsWith(template));
        }

        if (!string.IsNullOrWhiteSpace(filter.Recipient))
        {
            var recipient = filter.Recipient.Trim();
            query = query.Where(n => n.Recipient.Contains(recipient));
        }

        if (filter.From is { } from)
        {
            query = query.Where(n => n.QueuedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(n => n.QueuedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(n => n.Body.Contains(term) || n.Recipient.Contains(term) || n.Template.Contains(term));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(n => n.QueuedAt).ThenByDescending(n => n.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<NotificationLogDto>([.. rows.Select(ToDto)], total, page.Page, page.PageSize);
    }

    public async Task<Result<NotificationLogDto>> GetLogAsync(Guid id, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Notifications.LogView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var log = await db.NotificationLogs.AsNoTracking().FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
        return log is null ? Error.NotFound(ErrorCodes.Notify.LogNotFound, "Notification not found.") : ToDto(log);
    }

    /// <summary>Resends a message that failed or became a dead letter.</summary>
    public async Task<Result<NotificationLogDto>> ResendAsync(Guid id, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Notifications.LogRetry, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var log = await db.NotificationLogs.FirstOrDefaultAsync(n => n.Id == id, cancellationToken);
        if (log is null)
        {
            return Error.NotFound(ErrorCodes.Notify.LogNotFound, "Notification not found.");
        }

        return await notifications.RequeueAsync(log, cancellationToken)
            ? ToDto(log)
            : Error.Conflict(ErrorCodes.Notify.NotResendable, "Only a message that failed can be resent.");
    }

    // ---- Channels and language -------------------------------------------------------------------------------

    public async Task<Result<ChannelSettingsDto>> GetChannelsAsync(CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Notifications.TemplateView, cancellationToken);
        return refusal is not null ? refusal : await ReadChannelsAsync(cancellationToken);
    }

    private async Task<ChannelSettingsDto> ReadChannelsAsync(CancellationToken cancellationToken)
    {
        var v = await settings.ReadAsync([NotificationSettingKeys.SmsEnabled, NotificationSettingKeys.EmailEnabled, NotificationSettingKeys.WhatsAppEnabled, NotificationSettingKeys.DefaultLanguage], cancellationToken);
        bool On(string key, bool fallback) => v.TryGetValue(key, out var raw) && bool.TryParse(raw, out var on) ? on : fallback;
        var language = NotificationService.NormalizeLanguage(v.GetValueOrDefault(NotificationSettingKeys.DefaultLanguage));
        return new ChannelSettingsDto(On(NotificationSettingKeys.SmsEnabled, true), On(NotificationSettingKeys.EmailEnabled, true), On(NotificationSettingKeys.WhatsAppEnabled, false), language.Length == 0 ? "en" : language);
    }

    public async Task<Result<ChannelSettingsDto>> UpdateChannelsAsync(UpdateChannelSettingsRequest request, CancellationToken cancellationToken)
    {
        var (_, refusal) = await RequireAsync(Permissions.Notifications.ChannelEdit, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (request.DefaultLanguage is { } lang && NotificationService.NormalizeLanguage(lang).Length == 0)
        {
            return Invalid("defaultLanguage", "Use en or bn.");
        }

        if (request.SmsEnabled is { } sms)
        {
            await settings.SetAsync(NotificationSettingKeys.SmsEnabled, sms ? "true" : "false", cancellationToken);
        }

        if (request.EmailEnabled is { } email)
        {
            await settings.SetAsync(NotificationSettingKeys.EmailEnabled, email ? "true" : "false", cancellationToken);
        }

        if (request.WhatsAppEnabled is { } wa)
        {
            await settings.SetAsync(NotificationSettingKeys.WhatsAppEnabled, wa ? "true" : "false", cancellationToken);
        }

        if (request.DefaultLanguage is { } language)
        {
            await settings.SetAsync(NotificationSettingKeys.DefaultLanguage, NotificationService.NormalizeLanguage(language), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ReadChannelsAsync(cancellationToken);
    }

    /// <summary>The signed-in customer chooses the language of the messages they get.</summary>
    public async Task<Result> SetCustomerLanguageAsync(SetLanguageRequest request, CancellationToken cancellationToken)
    {
        if (currentUser is not { IsAuthenticated: true, SubjectType: "customer", UserId: { } id })
        {
            return Error.Unauthorized();
        }

        var language = NotificationService.NormalizeLanguage(request.Language);
        if (language.Length == 0)
        {
            return new Error(ErrorCodes.Notify.LanguageInvalid, "Choose en or bn.", ErrorType.Validation, new Dictionary<string, string[]> { ["language"] = ["Choose en or bn."] });
        }

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer is null)
        {
            return Error.NotFound(ErrorCodes.Customer.NotFound, "Customer not found.");
        }

        customer.SetPreferredLanguage(language);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
