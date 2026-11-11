using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Notifications;
using Juvo.Domain.Notifications;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

/// <summary>Notification templates, the delivery log and the channels (module K). Every staff endpoint names its permission; the customer chooses their own message language.</summary>
internal static class NotificationsEndpoints
{
    public static WebApplication MapNotificationsEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapStaff(v1.MapGroup("/notifications").WithTags("Notifications (staff)"));
        v1.MapGroup("/me").WithTags("Customer account").RequireAuthorization(Policies.Customer)
            .MapPut("/notification-language", async (SetLanguageRequest r, NotificationAdminService s, CancellationToken ct) => (await s.SetCustomerLanguageAsync(r, ct)).ToHttpResult())
            .WithSummary("Choose the language (en or bn) of the messages the shop sends me.");
        return app;
    }

    private static void MapStaff(RouteGroupBuilder g)
    {
        g.MapGet("/templates", async (string? code, NotificationAdminService s, CancellationToken ct) => (await s.ListTemplatesAsync(code, ct)).ToHttpResult())
            .RequirePermission(Permissions.Notifications.TemplateView).Produces<IReadOnlyList<TemplateDto>>()
            .WithSummary("Every message template (code, channel, language) with its text, version and the placeholders it may use. Built-in texts show version 0 until staff edit them.");

        g.MapPut("/templates/{code}/{channel}/{language}", async (string code, NotificationChannel channel, string language, UpdateTemplateRequest r, NotificationAdminService s, CancellationToken ct) =>
                (await s.UpdateTemplateAsync(code, channel, language, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Notifications.TemplateEdit).Produces<TemplateDto>()
            .WithSummary("Edit a template text (a new version; the old text is kept). Unknown placeholders are refused. isActive = false switches the message off for that channel and language.");

        g.MapGet("/templates/{code}/{channel}/{language}/history", async (string code, NotificationChannel channel, string language, NotificationAdminService s, CancellationToken ct) =>
                (await s.HistoryAsync(code, channel, language, ct)).ToHttpResult())
            .RequirePermission(Permissions.Notifications.TemplateView).Produces<IReadOnlyList<TemplateVersionDto>>().WithSummary("The versions of a template, newest first (version 0 is the built-in text).");

        g.MapPost("/templates/preview", async (PreviewTemplateRequest r, NotificationAdminService s, CancellationToken ct) => (await s.PreviewAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Notifications.TemplateView).Produces<PreviewDto>()
            .WithSummary("Render a template (the text in the request, or the current one) with sample values and report unknown placeholders.");

        g.MapGet("/logs", async ([AsParameters] ListParams list, NotificationStatus? status, NotificationChannel? channel, string? template, string? recipient, DateTimeOffset? from, DateTimeOffset? to, NotificationAdminService s, CancellationToken ct) =>
                (await s.ListLogsAsync(list.ToRequest(), new NotificationLogListQuery(status, channel, template, recipient, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Notifications.LogView).Produces<PagedResult<NotificationLogDto>>()
            .WithSummary("The delivery log, newest first. Filters: status (Pending, Sent, Failed, DeadLetter, Skipped), channel, template code or prefix, recipient, dates.");

        g.MapGet("/logs/{id:guid}", async (Guid id, NotificationAdminService s, CancellationToken ct) => (await s.GetLogAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Notifications.LogView).Produces<NotificationLogDto>().WithSummary("One logged message with its attempts and last error.");

        g.MapPost("/logs/{id:guid}/resend", async (Guid id, NotificationAdminService s, CancellationToken ct) => (await s.ResendAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Notifications.LogRetry).Produces<NotificationLogDto>().WithSummary("Send a failed or dead-lettered message again (back to Pending with a fresh attempt count).");

        g.MapGet("/channels", async (NotificationAdminService s, CancellationToken ct) => (await s.GetChannelsAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.Notifications.TemplateView).Produces<ChannelSettingsDto>().WithSummary("Which channels are switched on and the default message language.");

        g.MapPut("/channels", async (UpdateChannelSettingsRequest r, NotificationAdminService s, CancellationToken ct) => (await s.UpdateChannelsAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Notifications.ChannelEdit).Produces<ChannelSettingsDto>().WithSummary("Switch SMS, email and WhatsApp on or off and set the default language (en or bn).");
    }
}
