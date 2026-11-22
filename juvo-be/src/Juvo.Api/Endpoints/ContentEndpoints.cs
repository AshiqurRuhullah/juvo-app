using System.Security.Cryptography;
using System.Text.Json;
using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Content;
using Juvo.Domain.Content;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Site content (module Q): the staff screens (pages, brand profile, social links, trust badges) and the public reads for the shop.
/// Content is global, so its permissions are not branch scoped. Public reads return only published or enabled data and carry a short cache lifetime and an ETag.
/// </summary>
internal static class ContentEndpoints
{
    private static readonly JsonSerializerOptions HashOptions = new(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public static WebApplication MapContentEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapStaff(v1.MapGroup("/content").WithTags("Site content"));
        MapInbox(v1.MapGroup("/content/messages").WithTags("Contact inbox"));
        MapPublic(v1.MapGroup("/store/content").WithTags("Storefront").AllowAnonymous().RequireRateLimiting(RateLimitSettings.StorefrontPolicy));
        return app;
    }

    private static void MapStaff(RouteGroupBuilder g)
    {
        g.MapGet("/pages", async (ContentAdminService s, CancellationToken ct) => (await s.ListPagesAsync(ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.PageView).Produces<IReadOnlyList<PageDto>>().WithSummary("The six fixed pages (about, faq, delivery-policy, return-policy, privacy-policy, terms) with their state.");

        g.MapGet("/pages/{key}", async (string key, ContentAdminService s, CancellationToken ct) => (await s.GetPageAsync(key, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.PageView).Produces<PageDto>().WithSummary("One page with both languages and its meta fields.");

        g.MapPut("/pages/{key}", async (string key, UpdatePageRequest r, ContentAdminService s, CancellationToken ct) => (await s.UpdatePageAsync(key, r, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.PageEdit).Produces<PageDto>()
            .WithSummary("Save the texts of a page. The body is markdown; raw HTML and script are rejected (CONTENT.BODY_UNSAFE). Emptying a published page takes it down.");

        g.MapPost("/pages/{key}/publish", async (string key, ContentAdminService s, CancellationToken ct) => (await s.PublishPageAsync(key, true, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.PagePublish).Produces<PageDto>().WithSummary("Publish a page. Needs an English title and an English body (CONTENT.PAGE_INCOMPLETE otherwise).");

        g.MapPost("/pages/{key}/unpublish", async (string key, ContentAdminService s, CancellationToken ct) => (await s.PublishPageAsync(key, false, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.PagePublish).Produces<PageDto>().WithSummary("Take a page off the shop (back to draft).");

        g.MapGet("/brand", async (ContentAdminService s, CancellationToken ct) => (await s.GetBrandAsync(ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.BrandView).Produces<BrandDto>().WithSummary("The brand and contact profile.");

        g.MapPut("/brand", async (UpdateBrandRequest r, ContentAdminService s, CancellationToken ct) => (await s.UpdateBrandAsync(r, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.BrandEdit).Produces<BrandDto>().WithSummary("Save the brand profile (name, logo file reference, address, phone, WhatsApp, email, working hours, map point).");

        g.MapGet("/social", async (ContentAdminService s, CancellationToken ct) => (await s.ListSocialAsync(ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.SocialEdit).Produces<IReadOnlyList<SocialLinkDto>>().WithSummary("All social links, enabled or not.");

        g.MapPut("/social/{platform}", async (string platform, SaveSocialLinkRequest r, ContentAdminService s, CancellationToken ct) => (await s.SaveSocialAsync(platform, r, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.SocialEdit).Produces<SocialLinkDto>()
            .WithSummary("Create or change the link of facebook, instagram, tiktok, youtube or whatsapp. https only; WhatsApp also takes a mobile number.");

        g.MapDelete("/social/{platform}", async (string platform, ContentAdminService s, CancellationToken ct) => (await s.DeleteSocialAsync(platform, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.SocialEdit).WithSummary("Remove the link of one platform.");

        g.MapGet("/badges", async (ContentAdminService s, CancellationToken ct) => (await s.ListBadgesAsync(ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.BadgeEdit).Produces<IReadOnlyList<TrustBadgeDto>>().WithSummary("The four trust badges (secure-payment, delivery-coverage, return-policy, support-hours).");

        g.MapPut("/badges/{key}", async (string key, SaveTrustBadgeRequest r, ContentAdminService s, CancellationToken ct) => (await s.SaveBadgeAsync(key, r, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.BadgeEdit).Produces<TrustBadgeDto>().WithSummary("Word, switch on or off, and order one badge. A badge needs English text to be switched on.");
    }

    private static void MapInbox(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, ContactMessageStatus? status, ContactInboxService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new ContactMessageQuery(status), ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.MessageView).Produces<PagedResult<ContactMessageListItemDto>>()
            .WithSummary("Contact-form messages, newest first. Filter by status (New, Read, Replied, Archived); search name, subject and text (also mobile and email with customers.customer.view-pii). Mobile and email are masked without that permission; unmasked views are audit-logged.");

        g.MapGet("/{id:guid}", async (Guid id, ContactInboxService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.MessageView).Produces<ContactMessageDto>().WithSummary("One message with its internal notes (personal data masked without view-pii).");

        g.MapPost("/{id:guid}/status", async (Guid id, SetContactStatusRequest r, ContactInboxService s, CancellationToken ct) => (await s.SetStatusAsync(id, r.Status, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.MessageHandle).Produces<ContactMessageDto>().WithSummary("Set the status (New, Read, Replied, Archived). Handled by and time are stamped; New clears them.");

        g.MapPost("/{id:guid}/note", async (Guid id, AddContactNoteRequest r, ContactInboxService s, CancellationToken ct) => (await s.AddNoteAsync(id, r.Note, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.MessageHandle).Produces<ContactMessageDto>().WithSummary("Add an internal note (kept with author and time, never shown to the sender).");

        g.MapPost("/{id:guid}/archive", async (Guid id, ContactInboxService s, CancellationToken ct) => (await s.SetStatusAsync(id, ContactMessageStatus.Archived, ct)).ToHttpResult())
            .RequirePermission(ContentPermissions.MessageHandle).Produces<ContactMessageDto>().WithSummary("Archive a message (it is never deleted).");
    }

    private static void MapPublic(RouteGroupBuilder g)
    {
        g.MapPost("/contact", async (ContactFormRequest r, ContactPublicService s, CancellationToken ct) =>
                (await s.SubmitAsync(r, ct)).ToHttpResult(v => Results.Accepted(null, v)))
            .RequireRateLimiting(RateLimitSettings.ContactPolicy).Produces<ContactReceiptDto>(StatusCodes.Status202Accepted)
            .WithSummary("The public contact form: name, mobile or email (one is required), optional subject, message, optional source page and an empty honeypot field `website`. Answers 202 with a reference, the same whoever the sender is. Plain text only. Limited per address and per mobile or email (CONTENT.RATE_LIMITED, 429).");

        g.MapGet("/pages/{key}", async (string key, HttpContext http, ContentPublicService s, CancellationToken ct) => Cached(http, await s.GetPageAsync(key, ct)))
            .Produces<PublicPageDto>().WithSummary("A published page in both languages. A draft or unknown page answers 404 CONTENT.NOT_FOUND.");

        g.MapGet("/brand", async (HttpContext http, ContentPublicService s, CancellationToken ct) => Cached(http, await s.GetBrandAsync(ct)))
            .Produces<PublicBrandDto>().WithSummary("Business name, logo, address, phone, WhatsApp, email, working hours and map point for the footer and contact page.");

        g.MapGet("/social", async (HttpContext http, ContentPublicService s, CancellationToken ct) => Cached(http, await s.GetSocialAsync(ct)))
            .Produces<IReadOnlyList<PublicSocialLinkDto>>().WithSummary("Enabled social links, in display order.");

        g.MapGet("/badges", async (HttpContext http, ContentPublicService s, CancellationToken ct) => Cached(http, await s.GetBadgesAsync(ct)))
            .Produces<IReadOnlyList<PublicTrustBadgeDto>>().WithSummary("Enabled trust badges with their text in both languages, in display order.");
    }

    /// <summary>200 with a strong ETag and a one-minute public cache, or 304 when the caller already holds the current version. Failures go out as normal problem details.</summary>
    private static IResult Cached<T>(HttpContext http, Result<T> result)
    {
        if (result.IsFailure)
        {
            return result.ToHttpResult();
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(result.Value, HashOptions);
        var etag = "\"" + Convert.ToHexString(SHA256.HashData(bytes))[..32].ToLowerInvariant() + "\"";
        http.Response.Headers.CacheControl = "public, max-age=60";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.Vary = "Accept-Language";
        var sent = http.Request.Headers.IfNoneMatch.ToString();
        if (sent.Length > 0 && sent.Split(',').Any(v => v.Trim() == etag || v.Trim() == "W/" + etag))
        {
            return Results.StatusCode(StatusCodes.Status304NotModified);
        }

        return Results.Json(result.Value, HashOptions);
    }
}
