using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Couriers;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Couriers;

/// <summary>Turns shipments into the shapes the API returns.</summary>
public sealed class ShipmentViews(IApplicationDbContext db, ICourierProviderRegistry providers)
{
    public async Task<Result<ShipmentDto>> DetailAsync(Guid shipmentId, CancellationToken cancellationToken)
    {
        var s = await db.Shipments.AsNoTracking().Include(x => x.Events).FirstOrDefaultAsync(x => x.Id == shipmentId, cancellationToken);
        if (s is null)
        {
            return Error.NotFound(ErrorCodes.Shipment.NotFound, "Shipment not found.");
        }

        var provider = await ProviderOfAsync(s, cancellationToken);
        return Map(s, provider is ICourierSimulator);
    }

    public async Task<ICourierProvider?> ProviderOfAsync(Shipment s, CancellationToken cancellationToken)
    {
        var courier = await db.Couriers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.Id == s.CourierId, cancellationToken);
        return providers.Find(courier?.Provider);
    }

    public static ShipmentDto Map(Shipment s, bool simulator) =>
        new(s.Id, s.OrderId, s.OrderNumber, s.BranchId, s.CourierId, s.CourierCode, s.CourierName, s.Kind, s.ReturnRequestId, s.Status, s.ConsignmentId, s.ProviderReference, s.TrackingUrl, s.WeightGrams,
            s.LengthCm, s.WidthCm, s.HeightCm, s.CodAmount, s.CodCollectedAmount, s.CodCollectedAt, s.CodSettlementId, s.Cost, s.PickupName, s.PickupPhone, s.PickupAddress, s.DeliveryName,
            s.DeliveryPhone, s.DeliveryAddress, s.DeliveryDistrict, s.Note, s.Attempts, s.FailureReason, s.LastRawStatus, s.BookedAt, s.PickedUpAt, s.DeliveredAt, s.ReturnedAt, s.CancelledAt, s.CancelReason,
            s.Status == ShipmentStatus.Booked, simulator && s.IsOpen, ShipmentStateMachine.NextFor(s.Status),
            [.. s.Events.OrderBy(e => e.OccurredAt).ThenBy(e => e.CreatedAt).Select(MapEvent)]);

    public static ShipmentEventDto MapEvent(ShipmentEvent e) =>
        new(e.Id, e.ProviderEventId, e.RawStatus, e.Status, e.IsStatusChange, e.Description, e.Location, e.OccurredAt, e.Source, e.ActorName);

    /// <summary>The parcel the customer follows: the newest forward parcel that was not cancelled, with its scans (no staff names, no internal notes).</summary>
    public async Task<TrackingShipmentDto?> TrackingOfOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var s = await db.Shipments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Include(x => x.Events)
            .Where(x => x.OrderId == orderId && x.Kind == ShipmentKind.Forward && x.Status != ShipmentStatus.Cancelled).OrderByDescending(x => x.BookedAt).FirstOrDefaultAsync(cancellationToken);
        if (s is null)
        {
            return null;
        }

        return new TrackingShipmentDto(s.CourierName, s.ConsignmentId, s.TrackingUrl, s.Status, s.Attempts,
            [.. s.Events.Where(e => e.RawStatus != "sync-note" && e.Source != ShipmentEventSource.System).OrderBy(e => e.OccurredAt).ThenBy(e => e.CreatedAt)
                .Select(e => new TrackingShipmentEventDto(e.Status, e.IsStatusChange || !string.IsNullOrWhiteSpace(e.Description) ? CustomerText(e) : null, e.Location, e.OccurredAt))]);
    }

    /// <summary>Booking text and courier notes are written for staff; the customer only gets a plain line per step.</summary>
    private static string? CustomerText(ShipmentEvent e) => e.IsStatusChange
        ? e.Status switch
        {
            ShipmentStatus.Booked => "Parcel booked with the courier",
            ShipmentStatus.PickedUp => "Picked up by the courier",
            ShipmentStatus.InTransit => "On its way",
            ShipmentStatus.OutForDelivery => "Out for delivery",
            ShipmentStatus.Delivered => "Delivered",
            ShipmentStatus.DeliveryFailed => "Delivery attempt failed",
            ShipmentStatus.Returned => "Returned to the shop",
            _ => null,
        }
        : e.Description;
}

/// <summary>Staff side of shipments (module H): list, detail, events, label data and the sandbox simulator. Booking, cancel and re-book are in <see cref="ShipmentBooking"/>.</summary>
public sealed class ShipmentService(IApplicationDbContext db, OrderAccess access, ShipmentViews views, ShipmentSync sync, TimeProvider clock)
{
    private static readonly IReadOnlySet<string> SortFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "bookedAt", "status", "order" };

    private static Error NotFound() => Error.NotFound(ErrorCodes.Shipment.NotFound, "Shipment not found.");

    private async Task<Error?> CheckAsync(Ums.Actor actor, Guid branchId, string permission, CancellationToken cancellationToken)
    {
        var refusal = await access.CheckAsync(actor, branchId, permission, cancellationToken);
        return refusal is { Type: ErrorType.NotFound } ? NotFound() : refusal;
    }

    public async Task<Result<PagedResult<ShipmentListItemDto>>> ListAsync(PageRequest request, ShipmentListQuery filter, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Couriers.ShipmentView);
        var query = db.Shipments.AsNoTracking().AsQueryable();
        if (visible is not null)
        {
            query = query.Where(s => visible.Contains(s.BranchId));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(s => s.Status == status);
        }

        if (filter.CourierId is { } courierId)
        {
            query = query.Where(s => s.CourierId == courierId);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(s => s.BranchId == branchId);
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(s => s.Kind == kind);
        }

        if (filter.OrderId is { } orderId)
        {
            query = query.Where(s => s.OrderId == orderId);
        }

        if (filter.CodOnly == true)
        {
            query = query.Where(s => s.CodAmount > 0);
        }

        if (filter.Open is { } open)
        {
            var openStatuses = ShipmentStateMachine.OpenStatuses;
            query = open ? query.Where(s => openStatuses.Contains(s.Status)) : query.Where(s => !openStatuses.Contains(s.Status));
        }

        if (filter.From is { } from)
        {
            query = query.Where(s => s.BookedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(s => s.BookedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(s => s.ConsignmentId.Contains(term) || s.OrderNumber.Contains(term) || s.DeliveryName.Contains(term) || s.DeliveryPhone.Contains(term));
        }

        var sort = request.ParseSort(SortFields) is { Count: > 0 } f ? f[0] : null;
        query = (sort?.Name.ToLowerInvariant(), sort?.Descending ?? true) switch
        {
            ("status", true) => query.OrderByDescending(s => s.Status).ThenByDescending(s => s.BookedAt),
            ("status", false) => query.OrderBy(s => s.Status).ThenByDescending(s => s.BookedAt),
            ("order", true) => query.OrderByDescending(s => s.OrderNumber),
            ("order", false) => query.OrderBy(s => s.OrderNumber),
            (_, false) => query.OrderBy(s => s.BookedAt),
            _ => query.OrderByDescending(s => s.BookedAt),
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<ShipmentListItemDto>(
            [.. rows.Select(s => new ShipmentListItemDto(s.Id, s.OrderId, s.OrderNumber, s.BranchId, s.CourierCode, s.CourierName, s.Kind, s.Status, s.ConsignmentId, s.TrackingUrl, s.DeliveryName, s.DeliveryDistrict,
                s.WeightGrams, s.CodAmount, s.CodCollectedAmount, s.Attempts, s.FailureReason, s.BookedAt, s.DeliveredAt))],
            total, page.Page, page.PageSize);
    }

    public async Task<Result<ShipmentDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var branchId = await db.Shipments.AsNoTracking().Where(s => s.Id == id).Select(s => (Guid?)s.BranchId).FirstOrDefaultAsync(cancellationToken);
        if (branchId is not { } branch)
        {
            return NotFound();
        }

        return await CheckAsync(actor, branch, Permissions.Couriers.ShipmentView, cancellationToken) is { } refusal ? refusal : await views.DetailAsync(id, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<ShipmentEventDto>>> EventsAsync(Guid id, CancellationToken cancellationToken)
    {
        var detail = await GetAsync(id, cancellationToken);
        return detail.IsFailure ? detail.Error! : Result.Success<IReadOnlyList<ShipmentEventDto>>(detail.Value.Events);
    }

    /// <summary>The data of the parcel label. The screen prints it; the barcode text is the courier's consignment id.</summary>
    public async Task<Result<ShipmentLabelDto>> LabelAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var s = await db.Shipments.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (s is null)
        {
            return NotFound();
        }

        if (await CheckAsync(actor, s.BranchId, Permissions.Couriers.ShipmentPrintLabel, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var provider = await views.ProviderOfAsync(s, cancellationToken);
        var label = provider is null ? null : await provider.LabelAsync(s.ConsignmentId, cancellationToken);
        var items = await db.OrderItems.AsNoTracking().Where(i => i.OrderId == s.OrderId).SumAsync(i => (int?)i.Quantity, cancellationToken) ?? 0;
        s.MarkLabelPrinted(clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return new ShipmentLabelDto(s.Id, s.ConsignmentId, label is { IsSuccess: true } ? label.Value.Barcode : s.ConsignmentId, s.CourierName, s.OrderNumber, s.PickupName, s.PickupPhone, s.PickupAddress,
            s.DeliveryName, s.DeliveryPhone, s.DeliveryAddress, s.DeliveryDistrict, s.WeightGrams, s.CodAmount, items, s.Note, s.TrackingUrl, label is { IsSuccess: true } ? label.Value.ProviderUrl : null, s.BookedAt,
            s.Kind == ShipmentKind.Return);
    }

    /// <summary>
    /// Sandbox only: moves the parcel along at the simulated courier and applies the report the courier would send, through the same path as a real webhook. Without a status the next
    /// step of a normal journey is taken (picked up, in transit, out for delivery, delivered).
    /// </summary>
    public async Task<Result<ShipmentDto>> AdvanceAsync(Guid id, AdvanceShipmentRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var s = await db.Shipments.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (s is null)
        {
            return NotFound();
        }

        if (await CheckAsync(actor, s.BranchId, Permissions.Couriers.ShipmentSimulate, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        if (await views.ProviderOfAsync(s, cancellationToken) is not ICourierSimulator simulator)
        {
            return Error.Conflict(ErrorCodes.Courier.NotSimulator, "Only the sandbox courier can be moved along by hand.");
        }

        if (!s.IsOpen)
        {
            return Error.Conflict(ErrorCodes.Shipment.InvalidTransition, $"A parcel that is {s.Status} has nothing more to simulate.");
        }

        var raw = request.Status?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(raw))
        {
            raw = await simulator.NextStatusAsync(s.ConsignmentId, cancellationToken);
            if (raw is null)
            {
                return Error.Conflict(ErrorCodes.Shipment.InvalidTransition, "The journey is over.");
            }
        }
        else if (!simulator.RawStatuses.Contains(raw))
        {
            return Error.Validation(new Dictionary<string, string[]> { ["status"] = [$"Use one of: {string.Join(", ", simulator.RawStatuses)}."] });
        }

        var report = await simulator.SimulateAsync(s.ConsignmentId, raw, request.Note, cancellationToken);
        if (report.IsFailure)
        {
            return report.Error!;
        }

        var applied = await sync.IngestAsync(id, report.Value, ShipmentEventSource.Sandbox, access.WhoIs(actor).Name, cancellationToken);
        return applied.IsFailure ? applied.Error! : await views.DetailAsync(id, cancellationToken);
    }

}
