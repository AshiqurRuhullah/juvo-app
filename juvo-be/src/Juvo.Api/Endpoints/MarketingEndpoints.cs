using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Marketing;
using Juvo.Domain.Marketing;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Marketing (module AC): customer segments, consent-based SMS and email campaigns, automations and their results, plus the two public endpoints (unsubscribe, back-in-stock request).
/// Marketing is a global module: its permissions are not branch scoped. Consent decides who is messaged; the endpoints cannot override it.
/// </summary>
internal static class MarketingEndpoints
{
    public static WebApplication MapMarketingEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapSegments(v1.MapGroup("/marketing/segments").WithTags("Marketing segments"));
        MapCampaigns(v1.MapGroup("/marketing/campaigns").WithTags("Marketing campaigns"));
        MapAutomations(v1.MapGroup("/marketing/automations").WithTags("Marketing automations"));
        MapSettings(v1.MapGroup("/marketing/settings").WithTags("Marketing settings"));
        MapPublic(v1.MapGroup("/store").WithTags("Storefront").AllowAnonymous().RequireRateLimiting(RateLimitSettings.CheckoutPolicy));
        return app;
    }

    private static void MapSegments(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, SegmentsService s, CancellationToken ct) => (await s.ListAsync(list.ToRequest(), ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.SegmentView).Produces<PagedResult<SegmentDto>>()
            .WithSummary("Saved segments (the built-in ones first). Search by name.");

        g.MapGet("/{id:guid}", async (Guid id, SegmentsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.SegmentView).Produces<SegmentDto>().WithSummary("One segment with its rules.");

        g.MapPost("", async (SaveSegmentRequest r, SegmentsService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/marketing/segments/{v.Id}", v)))
            .RequirePermission(MarketingPermissions.SegmentEdit).Produces<SegmentDto>(StatusCodes.Status201Created)
            .WithSummary("Create a segment from typed rules (newCustomers, repeatBuyers, highSpenders, inactive, location, consent, lastOrderStatus, wishlist, cart) joined by and/or.");

        g.MapPut("/{id:guid}", async (Guid id, SaveSegmentRequest r, SegmentsService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.SegmentEdit).Produces<SegmentDto>().WithSummary("Change a segment. Built-in segments cannot be changed: copy them first.");

        g.MapDelete("/{id:guid}", async (Guid id, SegmentsService s, CancellationToken ct) => (await s.DeleteAsync(id, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.SegmentEdit).WithSummary("Delete a segment that no campaign uses (not a built-in one).");

        g.MapPost("/{id:guid}/copy", async (Guid id, SegmentsService s, CancellationToken ct) => (await s.CopyAsync(id, ct)).ToHttpResult(v => Results.Created($"/api/v1/marketing/segments/{v.Id}", v)))
            .RequirePermission(MarketingPermissions.SegmentEdit).Produces<SegmentDto>(StatusCodes.Status201Created).WithSummary("Copy a segment (also a built-in one) under a new name.");

        g.MapGet("/{id:guid}/count", async (Guid id, SegmentsService s, CancellationToken ct) => (await s.CountAsync(id, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.SegmentView).Produces<SegmentCountDto>()
            .WithSummary("Live count of active customers in the segment and how many are reachable by SMS and by email (consented and contactable).");

        g.MapPost("/preview-count", async (SegmentPreviewRequest r, SegmentsService s, CancellationToken ct) => (await s.PreviewCountAsync(r, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.SegmentView).Produces<SegmentCountDto>().WithSummary("The same counts for rules that are not saved yet.");

        g.MapGet("/{id:guid}/members", async (Guid id, [AsParameters] ListParams list, SegmentsService s, CancellationToken ct) => (await s.MembersAsync(id, list.ToRequest(), ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.SegmentView).Produces<PagedResult<SegmentMemberDto>>()
            .WithSummary("Members, paged: name, mobile and email (masked without customers.customer.view-pii), last order, order count, spend and per-channel reachability. Sort: name, spend, -spend, lastOrder, -lastOrder, -createdAt.");
    }

    private static void MapCampaigns(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, CampaignStatus? status, Domain.Notifications.NotificationChannel? channel, Guid? segmentId, CampaignsService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new CampaignListQuery(status, channel, segmentId), ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.CampaignView).Produces<PagedResult<CampaignListItemDto>>()
            .WithSummary("The campaign report: each campaign with recipients, sent, failed, skipped, unsubscribed and attributed orders and revenue. Filters: status, channel, segmentId; search by name.");

        g.MapGet("/{id:guid}", async (Guid id, CampaignsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.CampaignView).Produces<CampaignDto>().WithSummary("One campaign with its texts, schedule and recipient counts.");

        g.MapPost("", async (SaveCampaignRequest r, CampaignsService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/marketing/campaigns/{v.Id}", v)))
            .RequirePermission(MarketingPermissions.CampaignCreate).Produces<CampaignDto>(StatusCodes.Status201Created)
            .WithSummary("Create a draft campaign: segment, channel (Sms or Email), English text (Bengali optional) with {customerName}, {shopName}, {link}, {unsubscribeLink}.");

        g.MapPut("/{id:guid}", async (Guid id, SaveCampaignRequest r, CampaignsService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.CampaignCreate).Produces<CampaignDto>().WithSummary("Change a draft or scheduled campaign.");

        g.MapDelete("/{id:guid}", async (Guid id, CampaignsService s, CancellationToken ct) => (await s.DeleteAsync(id, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.CampaignCreate).WithSummary("Delete a campaign that never started.");

        g.MapPost("/{id:guid}/preview", async (Guid id, CampaignsService s, CancellationToken ct) => (await s.PreviewAsync(id, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.CampaignView).Produces<CampaignPreviewDto>()
            .WithSummary("What sending would do now: members, recipients, skipped for no consent, for missing contact and for the frequency cap, and the message as a sample customer would see it in each language.");

        g.MapPost("/{id:guid}/schedule", async (Guid id, ScheduleCampaignRequest r, CampaignsService s, CancellationToken ct) => (await s.ScheduleAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.CampaignSend).Produces<CampaignDto>()
            .WithSummary("Send at a time (a full date-time with offset, for example 2026-12-01T10:00:00+06:00 for Asia/Dhaka). The send worker starts it.");

        g.MapPost("/{id:guid}/send", async (Guid id, CampaignsService s, CancellationToken ct) => (await s.SendNowAsync(id, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.CampaignSend).Produces<CampaignDto>()
            .WithSummary("Send now. Recipients are fixed, then messages go out throttled; SMS waits for the end of quiet hours. The send worker carries on with what is left.");

        g.MapPost("/{id:guid}/cancel", async (Guid id, CampaignsService s, CancellationToken ct) => (await s.CancelAsync(id, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.CampaignSend).Produces<CampaignDto>().WithSummary("Cancel a draft, scheduled or sending campaign. Messages already queued stay; nothing more is sent.");

        g.MapPost("/{id:guid}/test", async (Guid id, SendTestRequest r, CampaignsService s, CancellationToken ct) => (await s.SendTestAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.CampaignCreate).Produces<SendTestDto>()
            .WithSummary("Send the campaign text to your own email or phone (from your staff profile) to see it. Not counted in any result.");

        g.MapGet("/{id:guid}/recipients", async (Guid id, [AsParameters] ListParams list, RecipientStatus? status, string? reason, CampaignsService s, CancellationToken ct) =>
                (await s.RecipientsAsync(id, list.ToRequest(), status, reason, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.CampaignView).Produces<PagedResult<CampaignRecipientDto>>()
            .WithSummary("Recipient rows with their state, skip reason (NoConsent, NoContact, FrequencyCap, ChannelOff, Cancelled) and delivery status. Contacts are masked.");

        g.MapGet("/{id:guid}/results", async (Guid id, CampaignsService s, CancellationToken ct) => (await s.ResultsAsync(id, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.CampaignView).Produces<CampaignResultsDto>()
            .WithSummary("Recipients, sent, delivered, failed, skipped by reason, unsubscribed and the orders attributed (last touch inside the attribution window) with their revenue.");
    }

    private static void MapAutomations(RouteGroupBuilder g)
    {
        g.MapGet("", async (AutomationsService s, CancellationToken ct) => (await s.ListAsync(ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.AutomationView).Produces<IReadOnlyList<AutomationDto>>().WithSummary("The four automations (abandoned cart, back in stock, review request, win-back), all off until switched on.");

        g.MapGet("/{kind}", async (AutomationKind kind, AutomationsService s, CancellationToken ct) => (await s.GetAsync(kind, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.AutomationView).Produces<AutomationDto>().WithSummary("One automation by kind (AbandonedCart, BackInStock, ReviewRequest, WinBack).");

        g.MapPut("/{kind}", async (AutomationKind kind, UpdateAutomationRequest r, AutomationsService s, CancellationToken ct) => (await s.UpdateAsync(kind, r, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.AutomationEdit).Produces<AutomationDto>().WithSummary("Switch on or off, change the delay, window, frequency cap, channel and the English and Bengali texts.");

        g.MapPost("/dry-run", async (AutomationsService s, CancellationToken ct) => (await s.DryRunAsync(ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.AutomationView).Produces<AutomationDryRunDto>()
            .WithSummary("How many messages each automation would send right now, with the reasons for the others. Nothing is sent or written.");

        g.MapGet("/{kind}/results", async (AutomationKind kind, AutomationsService s, CancellationToken ct) => (await s.ResultsAsync(kind, ct)).ToHttpResult())
            .RequirePermission(MarketingPermissions.AutomationView).Produces<AutomationResultsDto>().WithSummary("Queued, sent, failed and skipped messages of the automation and the orders attributed to them.");
    }

    private static void MapSettings(RouteGroupBuilder g)
    {
        g.MapGet("", async (MarketingSettingsService s, CancellationToken ct) => Results.Ok(MarketingSettingsService.ToDto(await s.ReadAsync(ct))))
            .RequirePermission(MarketingPermissions.CampaignView).Produces<MarketingSettingsDto>().WithSummary("Quiet hours, frequency cap, throttle, attribution window and the shop name used in messages.");

        g.MapPut("", async (UpdateMarketingSettingsRequest r, MarketingSettingsService s, CancellationToken ct) =>
            {
                var result = await s.UpdateAsync(r, ct);
                return result.IsSuccess ? Results.Ok(MarketingSettingsService.ToDto(result.Value)) : ResultHttpExtensions.ToProblem(result.Error!);
            })
            .RequirePermission(MarketingPermissions.CampaignSend).Produces<MarketingSettingsDto>().WithSummary("Change the marketing settings (only the fields you send).");
    }

    private static void MapPublic(RouteGroupBuilder g)
    {
        g.MapGet("/unsubscribe", async (string? token, UnsubscribeService s, CancellationToken ct) => (await s.ApplyAsync(token, ct)).ToHttpResult())
            .Produces<UnsubscribeDto>().WithSummary("The unsubscribe link of a message: turns the marketing consent of that channel off. Safe to open twice.");

        g.MapPost("/unsubscribe", async (HttpRequest request, string? token, UnsubscribeService s, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(token) && request.HasFormContentType)
                {
                    token = (await request.ReadFormAsync(ct))["token"].ToString();
                }

                return (await s.ApplyAsync(token, ct)).ToHttpResult();
            })
            .DisableAntiforgery().Produces<UnsubscribeDto>().WithSummary("The same as GET (the token in the query or a form field), for one-click unsubscribe from mail clients.");

        g.MapPost("/products/{slug}/notify-me", async (string slug, NotifyMeRequest r, BackInStockService s, CancellationToken ct) => (await s.SubscribeAsync(slug, r, ct)).ToHttpResult())
            .Produces<NotifyMeDto>().WithSummary("Ask to be told when a sold-out variant is back. A signed-in customer needs only the variant; a guest gives a mobile number. Once per variant and number.");
    }
}
