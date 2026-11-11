using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Couriers;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Couriers;

/// <summary>
/// When an order is cancelled while a parcel is booked but not yet picked up, the booking is cancelled with it (the courier is told, and the shipment records why). A courier that
/// refuses is noted on the timeline; the order cancellation still goes through, because the customer's right to cancel before pickup does not depend on the courier.
/// </summary>
public sealed class ShipmentCancellation(IApplicationDbContext db, ICourierProviderRegistry providers, TimeProvider clock)
{
    public async Task CancelBookingOfOrderAsync(Order order, string? reason, Who who, CancellationToken cancellationToken)
    {
        var shipment = await db.Shipments.IgnoreQueryFilters(["BranchScope"]).Include(s => s.Events)
            .FirstOrDefaultAsync(s => s.OrderId == order.Id && s.Kind == ShipmentKind.Forward && s.Status == ShipmentStatus.Booked, cancellationToken);
        if (shipment is null)
        {
            return;
        }

        var note = string.IsNullOrWhiteSpace(reason) ? "The order was cancelled." : $"The order was cancelled: {reason.Trim()}";
        var courier = await db.Couriers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.Id == shipment.CourierId, cancellationToken);
        if (providers.Find(courier?.Provider) is { } provider)
        {
            try
            {
                var cancelled = await provider.CancelAsync(shipment.ConsignmentId, cancellationToken);
                if (cancelled.IsFailure)
                {
                    note += $" The courier could not cancel the booking ({cancelled.Error!.Message}); cancel consignment {shipment.ConsignmentId} with the courier directly.";
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or InvalidOperationException)
            {
                note += $" The courier could not be reached; cancel consignment {shipment.ConsignmentId} with the courier directly.";
            }
        }

        shipment.MoveTo(ShipmentStatus.Cancelled, "cancelled", null, note, null, clock.GetUtcNow(), ShipmentEventSource.System, who.Name);
    }
}
