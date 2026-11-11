using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Application.Ums;
using Juvo.Domain.Branches;
using Juvo.Domain.Couriers;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Couriers;

/// <summary>
/// Books, cancels and re-books parcels (module H). A packed order is booked with a courier chosen by <see cref="CourierSelector"/> or by hand; the courier's answer (consignment id,
/// tracking page) is stored with the pickup address of the FULFILLING branch and the customer's delivery address. The order stays Packed until the courier reports pickup. Nothing
/// is left half done: a booking whose save fails is cancelled with the courier again.
/// </summary>
public sealed class ShipmentBooking(
    IApplicationDbContext db, OrderAccess access, CourierSelector selector, ICourierProviderRegistry providers, ShipmentViews views, TimeProvider clock)
{
    public const int DefaultWeightGrams = 500;
    public const int BulkLimit = 50;

    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error ShipmentNotFound() => Error.NotFound(ErrorCodes.Shipment.NotFound, "Shipment not found.");

    // ---- Suggest ----------------------------------------------------------------------------------------

    /// <summary>The couriers checked against this order: the eligible ones first in suggestion order (the first is recommended), then the ones that cannot take it, each with its reason.</summary>
    public async Task<Result<IReadOnlyList<CourierSuggestionDto>>> SuggestAsync(Guid orderId, int? weightGrams, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
        }

        if (await access.CheckAsync(actor, order.BranchId, Permissions.Couriers.ShipmentBook, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var weight = WeightOf(order, weightGrams);
        var checks = await selector.CheckAllAsync(CourierSelector.FactsOf(order, weight), cancellationToken);
        var ranked = CourierSelector.Rank(checks);
        var list = ranked.Select((c, i) => Suggestion(c, i == 0)).Concat(checks.Where(c => !c.Eligible).Select(c => Suggestion(c, false))).ToList();
        return list;
    }

    private static CourierSuggestionDto Suggestion(CourierCheck c, bool recommended) =>
        new(c.Courier.Id, c.Courier.Code, c.Courier.Name, c.Courier.Provider, c.Eligible, c.Reason, c.Cost, c.DaysMin, c.DaysMax, recommended);

    private static int WeightOf(Order order, int? requested) => requested ?? (order.WeightGrams > 0 ? order.WeightGrams : DefaultWeightGrams);

    // ---- Book -------------------------------------------------------------------------------------------

    public async Task<Result<ShipmentDto>> BookAsync(Guid orderId, BookShipmentRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var booked = await BookForActorAsync(actor, orderId, request.CourierId, request, cancellationToken);
        return booked.IsFailure ? booked.Error! : await views.DetailAsync(booked.Value.Id, cancellationToken);
    }

    private async Task<Result<Shipment>> BookForActorAsync(Actor actor, Guid orderId, Guid? courierId, BookShipmentRequest request, CancellationToken cancellationToken)
    {
        var order = await db.Orders.AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
        }

        if (await access.CheckAsync(actor, order.BranchId, Permissions.Couriers.ShipmentBook, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        return await BookCoreAsync(order, courierId, request, access.WhoIs(actor), cancellationToken);
    }

    private async Task<Result<Shipment>> BookCoreAsync(Order order, Guid? courierId, BookShipmentRequest request, Who who, CancellationToken cancellationToken)
    {
        if (order.Status != OrderStatus.Packed)
        {
            return Error.Conflict(ErrorCodes.Shipment.OrderNotReady, $"Pack the order first. An order that is {order.Status} cannot be booked with a courier.");
        }

        var open = ShipmentStateMachine.OpenStatuses;
        if (await db.Shipments.AnyAsync(s => s.OrderId == order.Id && s.Kind == ShipmentKind.Forward && open.Contains(s.Status), cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Shipment.AlreadyBooked, "This order already has a parcel with a courier. Cancel it first to book again.");
        }

        var weight = WeightOf(order, request.WeightGrams);
        if (weight is < 1 or > 100_000 || Bad(request.LengthCm) || Bad(request.WidthCm) || Bad(request.HeightCm))
        {
            return Invalid("weightGrams", "Give the weight in grams (1 to 100000) and the size in centimetres (1 to 300).");
        }

        if (request.Note is { Length: > 500 })
        {
            return Invalid("note", "The note can have at most 500 characters.");
        }

        var facts = CourierSelector.FactsOf(order, weight);
        var chosen = await ChooseAsync(courierId, facts, cancellationToken);
        if (chosen.IsFailure)
        {
            return chosen.Error!;
        }

        var (check, courier) = (chosen.Value, chosen.Value.Courier);
        var provider = providers.Find(courier.Provider)!;
        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == order.BranchId, cancellationToken);
        var pickup = new CourierParty(branch.NameEn, branch.Phone, PickupAddressOf(branch), null, "BD");
        var delivery = new CourierParty(order.ShipName, order.ShipPhone, DeliveryAddressOf(order), order.ShipDistrict ?? order.ShipCity, order.ShipCountryCode);

        var booking = await provider.BookAsync(
            new CourierBookingRequest(order.Number, false, pickup, delivery, weight, request.LengthCm, request.WidthCm, request.HeightCm, facts.CodAmount, request.Note, courier.Settings), cancellationToken);
        if (booking.IsFailure)
        {
            return booking.Error!;
        }

        var consignment = booking.Value;
        var shipment = Shipment.Book(
            new NewShipment(order.Id, order.Number, order.BranchId, courier.Id, courier.Code, courier.Name, ShipmentKind.Forward, null, consignment.ConsignmentId, consignment.ProviderReference,
                consignment.TrackingUrl ?? courier.TrackingUrlFor(consignment.ConsignmentId), weight, request.LengthCm, request.WidthCm, request.HeightCm, facts.CodAmount, consignment.Price ?? check.Cost ?? courier.CostFor(weight),
                pickup.Name, pickup.Phone, pickup.Address, delivery.Name, delivery.Phone ?? string.Empty, delivery.Address, delivery.District, request.Note),
            clock.GetUtcNow(), who.Id, who.Name);
        db.Shipments.Add(shipment);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.DiscardUnsavedAdditions(); // the failed save left the new parcel tracked; drop it so the courier call below can save
            await TryCancelAtCourierAsync(provider, consignment.ConsignmentId, cancellationToken);
            return Error.Conflict(ErrorCodes.Shipment.AlreadyBooked, "This order was booked with a courier at the same moment. The extra booking was cancelled.");
        }

        return shipment;
    }

    private static bool Bad(int? cm) => cm is { } v && (v < 1 || v > 300);

    private static async Task TryCancelAtCourierAsync(ICourierProvider provider, string consignmentId, CancellationToken cancellationToken)
    {
        try
        {
            await provider.CancelAsync(consignmentId, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException)
        {
            // best effort: the duplicate booking is reported to staff through the error
        }
    }

    private async Task<Result<CourierCheck>> ChooseAsync(Guid? courierId, ParcelFacts facts, CancellationToken cancellationToken)
    {
        if (courierId is { } id)
        {
            var courier = await db.Couriers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (courier is null)
            {
                return Error.NotFound(ErrorCodes.Courier.NotFound, "Courier not found.");
            }

            var check = await selector.CheckAsync(courier, facts, cancellationToken);
            return check.Eligible ? check : Error.Conflict(check.ErrorCode ?? ErrorCodes.Courier.NotServiceable, check.Reason ?? "This courier cannot take the parcel.");
        }

        var ranked = CourierSelector.Rank(await selector.CheckAllAsync(facts, cancellationToken));
        var best = ranked.Count > 0 ? ranked[0] : null;
        return best is null
            ? Error.Conflict(ErrorCodes.Courier.NoCourierAvailable, "No courier can take this parcel: none is switched on and set up for this address, weight and payment method.")
            : best;
    }

    internal static string PickupAddressOf(Branch branch) =>
        !string.IsNullOrWhiteSpace(branch.PickupAddress) ? branch.PickupAddress.Trim() : !string.IsNullOrWhiteSpace(branch.AddressLine) ? branch.AddressLine.Trim() : branch.NameEn;

    internal static string DeliveryAddressOf(Order o) =>
        string.Join(", ", new[] { o.ShipAddressLine, o.ShipLandmark, o.ShipArea, o.ShipUpazila, o.ShipDistrict ?? o.ShipCity, o.ShipDivision ?? o.ShipStateProvince, o.ShipPostalCode, o.ShipCountryCode == "BD" ? null : o.ShipCountryCode }
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => p!.Trim()));

    // ---- Bulk -------------------------------------------------------------------------------------------

    public async Task<Result<BulkBookResult>> BulkBookAsync(BulkBookRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var ids = request.OrderIds?.Distinct().ToList() ?? [];
        if (ids.Count is 0 or > BulkLimit)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["orderIds"] = [$"Choose between 1 and {BulkLimit} orders."] }, "Choose between 1 and 50 orders.");
        }

        var results = new List<BulkBookItemResult>();
        foreach (var id in ids)
        {
            var number = await db.Orders.AsNoTracking().Where(o => o.Id == id).Select(o => o.Number).FirstOrDefaultAsync(cancellationToken);
            var one = await BookForActorAsync(actor, id, request.CourierId, new BookShipmentRequest(request.CourierId, null, null, null, null, null), cancellationToken);
            results.Add(one.IsSuccess
                ? new BulkBookItemResult(id, number, true, one.Value.Id, one.Value.ConsignmentId, null, null)
                : new BulkBookItemResult(id, number, false, null, null, one.Error!.Code, one.Error.Message));
        }

        return new BulkBookResult(results.Count(r => r.Ok), results.Count(r => !r.Ok), results);
    }

    // ---- Cancel and re-book -----------------------------------------------------------------------------

    public async Task<Result<ShipmentDto>> CancelAsync(Guid shipmentId, CancelShipmentRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var done = await CancelCoreAsync(actor, shipmentId, request.Reason, cancellationToken);
        return done.IsFailure ? done.Error! : await views.DetailAsync(shipmentId, cancellationToken);
    }

    private async Task<Result> CancelCoreAsync(Actor actor, Guid shipmentId, string? reason, CancellationToken cancellationToken)
    {
        var text = reason?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 300)
        {
            return Invalid("reason", "Give the reason for cancelling the booking (up to 300 characters).");
        }

        var shipment = await db.Shipments.Include(s => s.Events).FirstOrDefaultAsync(s => s.Id == shipmentId, cancellationToken);
        if (shipment is null)
        {
            return ShipmentNotFound();
        }

        if (await access.CheckAsync(actor, shipment.BranchId, Permissions.Couriers.ShipmentCancel, cancellationToken) is { } refusal)
        {
            return refusal.Type == ErrorType.NotFound ? ShipmentNotFound() : refusal;
        }

        if (shipment.Status != ShipmentStatus.Booked)
        {
            return Error.Conflict(ErrorCodes.Shipment.CannotCancel, $"A parcel that is {shipment.Status} cannot be cancelled any more: only a booking the courier has not picked up.");
        }

        var courier = await db.Couriers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.Id == shipment.CourierId, cancellationToken);
        var provider = providers.Find(courier?.Provider);
        if (provider is null)
        {
            return Error.Conflict(ErrorCodes.Courier.UnknownProvider, "The adapter of this courier does not exist any more.");
        }

        var cancelled = await provider.CancelAsync(shipment.ConsignmentId, cancellationToken);
        if (cancelled.IsFailure)
        {
            return cancelled.Error!;
        }

        var who = access.WhoIs(actor);
        shipment.MoveTo(ShipmentStatus.Cancelled, "cancelled", null, text, null, clock.GetUtcNow(), ShipmentEventSource.Staff, who.Name);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Shipment.InvalidTransition, "The parcel changed at the same moment. Reload and try again.");
        }

        return Result.Success();
    }

    /// <summary>Cancels the booking (when it is still only booked) and books the order again, with the same or another courier. Two steps: if the new booking fails the old one stays cancelled and staff can book again.</summary>
    public async Task<Result<ShipmentDto>> RebookAsync(Guid shipmentId, RebookShipmentRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var existing = await db.Shipments.AsNoTracking().FirstOrDefaultAsync(s => s.Id == shipmentId, cancellationToken);
        if (existing is null)
        {
            return ShipmentNotFound();
        }

        if (existing.Kind != ShipmentKind.Forward)
        {
            return Error.Conflict(ErrorCodes.Shipment.InvalidTransition, "Only the parcel to the customer can be booked again.");
        }

        if (existing.Status == ShipmentStatus.Booked)
        {
            var cancelled = await CancelCoreAsync(actor, shipmentId, string.IsNullOrWhiteSpace(request.Reason) ? "Booked again" : request.Reason, cancellationToken);
            if (cancelled.IsFailure)
            {
                return cancelled.Error!;
            }
        }
        else if (existing.Status != ShipmentStatus.Cancelled)
        {
            return Error.Conflict(ErrorCodes.Shipment.CannotCancel, $"A parcel that is {existing.Status} cannot be booked again.");
        }

        var booked = await BookForActorAsync(actor, existing.OrderId, request.CourierId, new BookShipmentRequest(request.CourierId, request.WeightGrams ?? existing.WeightGrams, request.LengthCm ?? existing.LengthCm, request.WidthCm ?? existing.WidthCm, request.HeightCm ?? existing.HeightCm, request.Note ?? existing.Note), cancellationToken);
        return booked.IsFailure ? booked.Error! : await views.DetailAsync(booked.Value.Id, cancellationToken);
    }

    // ---- Return pickup (module L) -----------------------------------------------------------------------

    /// <summary>
    /// Books the reverse parcel of a return: the courier collects it at the customer's address and brings it to the shop. Adds the shipment without saving (the return service saves with
    /// the return). Returns the shipment so the return can show the courier.
    /// </summary>
    public async Task<Result<Shipment>> BookReturnPickupAsync(ReturnRequest ret, Order order, Guid? courierId, DateOnly pickupDate, string? note, Who who, CancellationToken cancellationToken)
    {
        var weight = Math.Max(order.Items.Where(i => ret.Items.Any(r => r.OrderItemId == i.Id)).Sum(i => i.WeightGrams * ret.Items.First(r => r.OrderItemId == i.Id).Quantity), 0);
        weight = weight > 0 ? weight : DefaultWeightGrams;
        var facts = new ParcelFacts(order.ShipCountryCode, order.ZoneCode, order.ShipDistrict ?? order.ShipCity, weight, 0m);
        var chosen = await ChooseAsync(courierId, facts, cancellationToken);
        if (chosen.IsFailure)
        {
            return chosen.Error!;
        }

        var courier = chosen.Value.Courier;
        var provider = providers.Find(courier.Provider)!;
        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == ret.BranchId, cancellationToken);
        var customer = new CourierParty(order.ShipName, order.ShipPhone, DeliveryAddressOf(order), order.ShipDistrict ?? order.ShipCity, order.ShipCountryCode);
        var shop = new CourierParty(branch.NameEn, branch.Phone, PickupAddressOf(branch), null, "BD");
        var text = $"Return {ret.Number}, pickup {pickupDate:yyyy-MM-dd}. {note}".Trim();
        var booking = await provider.BookAsync(new CourierBookingRequest(ret.Number, true, customer, shop, weight, null, null, null, 0m, text, courier.Settings), cancellationToken);
        if (booking.IsFailure)
        {
            return booking.Error!;
        }

        var consignment = booking.Value;
        var shipment = Shipment.Book(
            new NewShipment(order.Id, order.Number, ret.BranchId, courier.Id, courier.Code, courier.Name, ShipmentKind.Return, ret.Id, consignment.ConsignmentId, consignment.ProviderReference,
                consignment.TrackingUrl ?? courier.TrackingUrlFor(consignment.ConsignmentId), weight, null, null, null, 0m, consignment.Price ?? chosen.Value.Cost ?? courier.CostFor(weight),
                customer.Name, customer.Phone, customer.Address, shop.Name, shop.Phone ?? string.Empty, shop.Address, null, text),
            clock.GetUtcNow(), who.Id, who.Name);
        db.Shipments.Add(shipment);
        return shipment;
    }
}
