using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Orders;
using Juvo.Application.Preorders;
using Juvo.Application.Promotions;
using Juvo.Domain.Orders;
using Juvo.Domain.Promotions;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Mvc;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Promotions and the pricing engine (module Z) and pre-orders (module AA) for staff. Promotions are global, like coupons (the coupon endpoints stay under <c>/promotions/coupons</c>). Every endpoint
/// names its permission. The pre-order queue and allocation are branch-checked in the service (an order in another branch looks like "not found").
/// </summary>
internal static class PromotionsEndpoints
{
    public static WebApplication MapPromotionsEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapPromotions(v1.MapGroup("/promotions").WithTags("Promotions"));
        MapPreorders(v1);
        return app;
    }

    private static void MapPromotions(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, PromotionType? type, PromotionStatus? status, bool? liveNow, PromotionsService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new PromotionListQuery(type, status, liveNow), ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.PromotionView).Produces<PagedResult<PromotionListItemDto>>()
            .WithSummary("List promotions (newest first). Filters: type, derived status (Draft, Scheduled, Active, Ended, Archived), liveNow; search by name or campaign address.");

        g.MapPost("/preview", async (PreviewRequest r, PromotionsService s, CancellationToken ct) => (await s.PreviewAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.PromotionView).Produces<PreviewResultDto>()
            .WithSummary("Price a sample cart with a chosen promotion set (draft ones too) or with the promotions running at a given time, and see per line prices, adjustments, totals and the explanation. Nothing is saved or counted.");

        g.MapGet("/report", async ([AsParameters] ListParams list, DateTimeOffset? from, DateTimeOffset? to, PromotionType? type, PromotionReportService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new PromotionReportQuery(from, to, type), ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.PromotionView).Produces<PagedResult<PromotionReportDto>>()
            .WithSummary("Campaign report: per promotion the orders, units, revenue and discount cost, from the order line breakdown (cancelled orders left out).");

        g.MapGet("/{id:guid}", async (Guid id, PromotionsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.PromotionView).Produces<PromotionDetailDto>().WithSummary("One promotion with its settings and targets (names resolved).");

        g.MapGet("/{id:guid}/report", async (Guid id, DateTimeOffset? from, DateTimeOffset? to, PromotionReportService s, CancellationToken ct) => (await s.OneAsync(id, from, to, ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.PromotionView).Produces<PromotionReportDto>().WithSummary("The report of one promotion: orders, units, revenue after promotion discounts, discount cost.");

        g.MapPost("", async (SavePromotionRequest r, PromotionsService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/promotions/{v.Id}", v)))
            .RequirePermission(Permissions.Promotions.PromotionCreate).Produces<PromotionDetailDto>(StatusCodes.Status201Created)
            .WithSummary("Create a promotion as a Draft: flash sale, product discount, bundle, buy X get Y, free delivery or festival campaign.");

        g.MapPut("/{id:guid}", async (Guid id, SavePromotionRequest r, PromotionsService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.PromotionEdit).Produces<PromotionDetailDto>().WithSummary("Change a promotion and its targets (the type cannot change; an archived promotion cannot be changed).");

        g.MapPost("/{id:guid}/activate", async (Guid id, PromotionsService s, CancellationToken ct) => (await s.ActivateAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.PromotionEdit).Produces<PromotionDetailDto>().WithSummary("Switch the promotion on. It runs inside its schedule (Scheduled before the start, Active, Ended after the end).");

        g.MapPost("/{id:guid}/deactivate", async (Guid id, PromotionsService s, CancellationToken ct) => (await s.DeactivateAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.PromotionEdit).Produces<PromotionDetailDto>().WithSummary("Switch the promotion off (back to Draft).");

        g.MapPost("/{id:guid}/archive", async (Guid id, PromotionsService s, CancellationToken ct) => (await s.ArchiveAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.PromotionDelete).Produces<PromotionDetailDto>().WithSummary("Archive the promotion: it stops and leaves the shop but stays in the reports and on the orders that used it.");

        g.MapDelete("/{id:guid}", async (Guid id, PromotionsService s, CancellationToken ct) => (await s.ArchiveAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.PromotionDelete).Produces<PromotionDetailDto>().WithSummary("Same as archive (a promotion is never erased).");

        g.MapPost("/{id:guid}/duplicate", async (Guid id, PromotionsService s, CancellationToken ct) => (await s.DuplicateAsync(id, ct)).ToHttpResult(v => Results.Created($"/api/v1/promotions/{v.Id}", v)))
            .RequirePermission(Permissions.Promotions.PromotionCreate).Produces<PromotionDetailDto>(StatusCodes.Status201Created).WithSummary("Copy a promotion (and its targets) as a new Draft.");

        g.MapPost("/{id:guid}/images/{kind}", async (Guid id, string kind, IFormFile file, PromotionsService s, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return (await s.SetImageAsync(id, kind, stream, ct)).ToHttpResult();
            })
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(12 * 1024 * 1024))
            .RequirePermission(Permissions.Promotions.PromotionEdit).Produces<PromotionDetailDto>()
            .WithSummary("Upload the hero or banner picture of a campaign (kind = hero or banner; JPEG, PNG or WebP, at least 1000 px on the long side, at most 10 MB).");
    }

    private static void MapPreorders(RouteGroupBuilder v1)
    {
        var orders = v1.MapGroup("/orders/preorders").WithTags("Pre-orders (staff)");
        orders.MapGet("", async ([AsParameters] ListParams list, PreorderState? state, Guid? branchId, Guid? variantId, DateOnly? expectedBefore, PreorderService s, CancellationToken ct) =>
                (await s.QueueAsync(list.ToRequest(), new PreorderQuery(state, branchId, variantId, expectedBefore), ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderView).Produces<PagedResult<PreorderQueueItemDto>>()
            .WithSummary("The pre-order queue, oldest first: orders with lines that wait for stock (Waiting) or got it (Ready). Filters: state, branch, variant, expected ship date; search order, mobile, name or SKU.");

        orders.MapPost("/allocate", async (AllocatePreordersRequest r, PreorderService s, CancellationToken ct) => (await s.AllocateAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderEdit).Produces<AllocationResultDto>()
            .WithSummary("Give available stock to waiting pre-order lines, oldest order first (also done automatically after a purchase receipt). An order whose last line was served becomes Ready and the customer is told once.");

        var catalog = v1.MapGroup("/catalog/products").WithTags("Pre-orders (staff)");
        catalog.MapGet("/{id:guid}/preorder", async (Guid id, PreorderService s, CancellationToken ct) => (await s.GetSettingsAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductView).Produces<PreorderSettingsDto>().WithSummary("The pre-order switch of a product and of each variant (with what each variant effectively allows).");

        catalog.MapPut("/{id:guid}/preorder", async (Guid id, PreorderSettingsRequest r, PreorderService s, CancellationToken ct) => (await s.SetProductAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductEdit).Produces<PreorderSettingsDto>()
            .WithSummary("Allow or forbid pre-orders for a product when it is out of stock, with the expected ship date and an optional advance percentage.");

        catalog.MapPut("/{id:guid}/variants/{variantId:guid}/preorder", async (Guid id, Guid variantId, PreorderSettingsRequest r, PreorderService s, CancellationToken ct) => (await s.SetVariantAsync(id, variantId, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductEdit).Produces<PreorderSettingsDto>()
            .WithSummary("Override the product's pre-order setting for one variant (allowPreorder null inherits the product).");
    }
}
