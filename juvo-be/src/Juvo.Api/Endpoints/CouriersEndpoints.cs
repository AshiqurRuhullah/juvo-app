using System.Text;
using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Couriers;
using Juvo.Application.Orders;
using Juvo.Application.Realtime;
using Juvo.Domain.Couriers;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Mvc;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Couriers, shipments and cash on delivery settlement (module H), the courier webhook, and the real-time tokens (module J). The webhook proves itself with the courier's signature and is
/// anonymous on purpose (rate limited); the guest token endpoint proves the order with the tracking token; everything else names its permission, and the services prove it again in the
/// shipment's own branch.
/// </summary>
internal static class CouriersEndpoints
{
    public static WebApplication MapCouriersEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapCouriers(v1.MapGroup("/couriers").WithTags("Couriers (staff)"));
        MapSettlements(v1.MapGroup("/couriers/cod-settlements").WithTags("COD settlement (staff)"));
        MapShipments(v1.MapGroup("/shipments").WithTags("Shipments (staff)"));
        MapWebhook(v1.MapGroup("/webhooks").WithTags("Courier webhooks").AllowAnonymous().RequireRateLimiting(RateLimitSettings.StorefrontPolicy));
        MapRealtime(v1.MapGroup("/realtime").WithTags("Real-time"));
        MapGuest(v1.MapGroup("/store/orders/track").WithTags("Checkout").AllowAnonymous().RequireRateLimiting(RateLimitSettings.CheckoutPolicy));
        return app;
    }

    private static void MapCouriers(RouteGroupBuilder g)
    {
        g.MapGet("", async (CourierAdminService s, CancellationToken ct) => (await s.ListAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.CourierView).Produces<IReadOnlyList<CourierDto>>()
            .WithSummary("The couriers with their rates, priority, switches and how many parcels are with each. Credentials are never shown (they are not stored here).");

        g.MapGet("/providers", (CourierAdminService s) => Results.Ok(s.Providers()))
            .RequirePermission(Permissions.Couriers.CourierView).Produces<IReadOnlyList<CourierProviderDto>>()
            .WithSummary("The adapters the system has (the sandbox and the six real companies) and whether each is set up.");

        g.MapGet("/{id:guid}", async (Guid id, CourierAdminService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.CourierView).Produces<CourierDto>().WithSummary("One courier.");

        g.MapPost("", async (SaveCourierRequest r, CourierAdminService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/couriers/{v.Id}", v)))
            .RequirePermission(Permissions.Couriers.CourierEdit).Produces<CourierDto>(StatusCodes.Status201Created)
            .WithSummary("Add a courier. The adapter must exist; a courier whose adapter is not set up cannot be switched on. Secret-like settings are refused.");

        g.MapPut("/{id:guid}", async (Guid id, SaveCourierRequest r, CourierAdminService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.CourierEdit).Produces<CourierDto>()
            .WithSummary("Change a courier: name, adapter, priority, weight and zone limits, cash on delivery, the customer charge override (first switched-on courier only) and the rates the courier charges the shop.");

        g.MapPut("/{id:guid}/enabled", async (Guid id, SetCourierEnabledRequest r, CourierAdminService s, CancellationToken ct) => (await s.SetEnabledAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.CourierEdit).Produces<CourierDto>().WithSummary("Switch a courier on or off. A courier whose adapter is not set up (COURIER.NOT_CONFIGURED) cannot be switched on.");

        g.MapDelete("/{id:guid}", async (Guid id, CourierAdminService s, CancellationToken ct) => (await s.DeleteAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.CourierEdit).Produces(StatusCodes.Status204NoContent)
            .WithSummary("Archive a courier (never erased). Refused while parcels are with it.");
    }

    private static void MapShipments(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, ShipmentStatus? status, Guid? courierId, Guid? branchId, ShipmentKind? kind, Guid? orderId, bool? codOnly, bool? open, DateTimeOffset? from, DateTimeOffset? to,
                ShipmentService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new ShipmentListQuery(status, courierId, branchId, kind, orderId, codOnly, open, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.ShipmentView).Produces<PagedResult<ShipmentListItemDto>>()
            .WithSummary("Parcels of the branches where you may view shipments. Filters: status, courier, branch, kind (Forward or Return), order, cash on delivery only, open only, booking date range; search by consignment, order number, recipient name or phone; sort bookedAt (default newest), status, order.");

        g.MapGet("/{id:guid}", async (Guid id, ShipmentService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.ShipmentView).Produces<ShipmentDto>()
            .WithSummary("One parcel with its pickup and delivery addresses, cash to collect, attempts, failure reason, the next statuses it may reach and every event (raw status, our status, source).");

        g.MapGet("/{id:guid}/events", async (Guid id, ShipmentService s, CancellationToken ct) => (await s.EventsAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.ShipmentView).Produces<IReadOnlyList<ShipmentEventDto>>().WithSummary("The immutable event log of a parcel, oldest first.");

        g.MapGet("/{id:guid}/label", async (Guid id, ShipmentService s, CancellationToken ct) => (await s.LabelAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.ShipmentPrintLabel).Produces<ShipmentLabelDto>()
            .WithSummary("The data of the parcel label: barcode text, both addresses, weight, cash to collect and item count. The screen prints it. Records that a label was printed.");

        g.MapGet("/orders/{orderId:guid}/suggest", async (Guid orderId, int? weightGrams, ShipmentBooking s, CancellationToken ct) => (await s.SuggestAsync(orderId, weightGrams, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.ShipmentBook).Produces<IReadOnlyList<CourierSuggestionDto>>()
            .WithSummary("The couriers checked against an order: the ones that can take it first (the first is recommended: priority, then cost), then the ones that cannot with the reason.");

        g.MapPost("/orders/{orderId:guid}/book", async (Guid orderId, BookShipmentRequest r, ShipmentBooking s, CancellationToken ct) =>
                (await s.BookAsync(orderId, r, ct)).ToHttpResult(v => Results.Created($"/api/v1/shipments/{v.Id}", v)))
            .RequirePermission(Permissions.Couriers.ShipmentBook).Produces<ShipmentDto>(StatusCodes.Status201Created)
            .WithSummary("Book a packed order with a courier (the suggested one unless courierId is given). The parcel carries the fulfilling branch's pickup address and the cash to collect; the order stays Packed until the courier reports pickup.");

        g.MapPost("/bulk-book", async (BulkBookRequest r, ShipmentBooking s, CancellationToken ct) => (await s.BulkBookAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.ShipmentBulkBook).Produces<BulkBookResult>()
            .WithSummary("Book up to 50 packed orders, each with its suggested courier (or one courier for all). Each order is handled on its own; the answer lists the consignment or the error code per order.");

        g.MapPost("/{id:guid}/cancel", async (Guid id, CancelShipmentRequest r, ShipmentBooking s, CancellationToken ct) => (await s.CancelAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.ShipmentCancel).Produces<ShipmentDto>()
            .WithSummary("Cancel a booking the courier has not picked up (reason required). The courier is told. The order stays Packed and can be booked again.");

        g.MapPost("/{id:guid}/rebook", async (Guid id, RebookShipmentRequest r, ShipmentBooking s, CancellationToken ct) => (await s.RebookAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.ShipmentBook).Produces<ShipmentDto>()
            .WithSummary("Cancel a booked (not yet picked up) parcel and book the order again, with the same or another courier.");

        g.MapPost("/{id:guid}/advance", async (Guid id, AdvanceShipmentRequest r, ShipmentService s, CancellationToken ct) => (await s.AdvanceAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Couriers.ShipmentSimulate).Produces<ShipmentDto>()
            .WithSummary("Sandbox courier only: move the parcel along (without a status, the next step: picked up, in transit, out for delivery, delivered; or give picked_up, in_transit, at_hub, out_for_delivery, delivered, delivery_failed, returned). Goes through the same path as a webhook.");
    }

    private static void MapSettlements(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, SettlementStatus? status, Guid? courierId, Guid? branchId, CodSettlementService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new SettlementQuery(status, courierId, branchId), ct)).ToHttpResult())
            .RequirePermission(Permissions.Cod.View).Produces<PagedResult<SettlementListItemDto>>()
            .WithSummary("Settlement batches of the branches where you may view cash on delivery. Filters: status, courier, branch; search by number or reference.");

        g.MapGet("/pending", async (Guid courierId, Guid? branchId, CodSettlementService s, CancellationToken ct) => (await s.PendingAsync(courierId, branchId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Cod.View).Produces<IReadOnlyList<ShipmentListItemDto>>()
            .WithSummary("The delivered cash on delivery parcels of a courier that are in no batch yet (what a new batch would hold).");

        g.MapGet("/{id:guid}", async (Guid id, CodSettlementService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Cod.View).Produces<SettlementDto>().WithSummary("One batch with every parcel: expected, received, difference (negative is short, positive is over) and the note.");

        g.MapPost("", async (CreateSettlementRequest r, CodSettlementService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/couriers/cod-settlements/{v.Id}", v)))
            .RequirePermission(Permissions.Cod.Settle).Produces<SettlementDto>(StatusCodes.Status201Created)
            .WithSummary("Make a batch of a courier's delivered cash on delivery parcels in one branch (all waiting ones, or the shipmentIds you give). Gap-free number CS-BRANCH-YYMM-000001.");

        g.MapPost("/{id:guid}/reconcile", async (Guid id, ReconcileSettlementRequest r, CodSettlementService s, CancellationToken ct) => (await s.ReconcileAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Cod.Settle).Produces<SettlementDto>()
            .WithSummary("Enter what the courier really paid per parcel (a parcel left out counts as paid in full). A short or over parcel needs a note. Can be repeated until the batch is settled.");

        g.MapPost("/{id:guid}/settle", async (Guid id, SettleSettlementRequest r, CodSettlementService s, CancellationToken ct) => (await s.SettleAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Cod.Settle).Produces<SettlementDto>()
            .WithSummary("Close a reconciled batch with the deposit reference: every order's cash becomes Settled (payment record and order payment track), the differences stay on the batch.");

        g.MapPost("/{id:guid}/cancel", async (Guid id, CodSettlementService s, CancellationToken ct) => (await s.CancelAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Cod.Settle).Produces<SettlementDto>().WithSummary("Drop an open or reconciled batch; its parcels can go into another one.");
    }

    private static void MapWebhook(RouteGroupBuilder g)
    {
        g.MapPost("/couriers/{provider}", async (string provider, HttpRequest request, CourierWebhookService s, CancellationToken ct) =>
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                return (await s.ProcessAsync(provider, body, headers, ct)).ToHttpResult();
            })
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024)).Produces<CourierWebhookResultDto>()
            .WithSummary("Courier webhook (the courier calls this, not the browser). The signature header (Juvo-Signature: t=unix,v1=hmac) and the 5 minute replay window are checked, an event id seen before changes nothing (status duplicate), and a parcel we do not know is acknowledged as ignored.");
    }

    private static void MapRealtime(RouteGroupBuilder g)
    {
        g.MapPost("/token", async (RealtimeTokens s, CancellationToken ct) => (await s.ForSignedInAsync(ct)).ToHttpResult())
            .RequireAuthorization(Policies.Authenticated).Produces<RealtimeTokenDto>()
            .WithSummary("A short-lived token for the real-time hub, for the signed-in staff member or customer (call it from the BFF with the access token, then connect the browser to the hub path with it).");
    }

    private static void MapGuest(RouteGroupBuilder g)
    {
        g.MapPost("/realtime-token", async (TrackRequest r, RealtimeTokens s, CancellationToken ct) => (await s.ForTrackedOrderAsync(r, ct)).ToHttpResult())
            .Produces<RealtimeTokenDto>()
            .WithSummary("A short-lived hub token for ONE order, for a guest who proves it with the tracking token or order number plus mobile. It cannot follow any other order.");

        g.MapPost("/cancel", async (TrackCancelRequest r, CheckoutService s, CancellationToken ct) => (await s.CancelTrackedAsync(r, ct)).ToHttpResult())
            .Produces<TrackingDto>()
            .WithSummary("Cancel an order from the tracking page (no sign-in): prove it with the order number plus mobile, or the tracking token. Only while it is Placed, Confirmed or Packed (ORDER.CANNOT_CANCEL after). Rate limited.");
    }
}
