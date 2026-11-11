using Juvo.Domain.Orders;

namespace Juvo.Domain.Couriers;

/// <summary>The courier-side status of one parcel. Stored as an integer; append new values at the end.</summary>
public enum ShipmentStatus
{
    /// <summary>Booked with the courier, waiting for pickup. The order stays Packed.</summary>
    Booked = 1,

    PickedUp = 2,
    InTransit = 3,
    OutForDelivery = 4,
    Delivered = 5,
    DeliveryFailed = 6,
    Returned = 7,
    Cancelled = 8,
}

/// <summary>A normal parcel to the customer, or a reverse parcel that brings a return back to the shop (module L pickup).</summary>
public enum ShipmentKind
{
    Forward = 1,
    Return = 2,
}

/// <summary>Where a shipment event came from.</summary>
public enum ShipmentEventSource
{
    Staff = 1,
    Webhook = 2,
    Polling = 3,
    Sandbox = 4,
    System = 5,
}

/// <summary>
/// The allowed moves of a shipment and how each status maps onto the order tracks (SRS module H and J). A courier may skip scans, so a report of a later status is
/// expanded into the steps between (<see cref="PathTo"/>); a move that is not on any path (for example back from Delivered) is refused.
/// </summary>
public static class ShipmentStateMachine
{
    private static readonly Dictionary<ShipmentStatus, ShipmentStatus[]> Moves = new()
    {
        [ShipmentStatus.Booked] = [ShipmentStatus.PickedUp, ShipmentStatus.Cancelled],
        [ShipmentStatus.PickedUp] = [ShipmentStatus.InTransit],
        [ShipmentStatus.InTransit] = [ShipmentStatus.OutForDelivery],
        [ShipmentStatus.OutForDelivery] = [ShipmentStatus.Delivered, ShipmentStatus.DeliveryFailed],
        [ShipmentStatus.DeliveryFailed] = [ShipmentStatus.OutForDelivery, ShipmentStatus.Returned],
        [ShipmentStatus.Delivered] = [],
        [ShipmentStatus.Returned] = [],
        [ShipmentStatus.Cancelled] = [],
    };

    public static IReadOnlyList<ShipmentStatus> NextFor(ShipmentStatus from) => Moves[from];

    public static bool CanMove(ShipmentStatus from, ShipmentStatus to) => Moves[from].Contains(to);

    public static readonly ShipmentStatus[] OpenStatuses = [ShipmentStatus.Booked, ShipmentStatus.PickedUp, ShipmentStatus.InTransit, ShipmentStatus.OutForDelivery, ShipmentStatus.DeliveryFailed];

    /// <summary>The parcel is still with the courier (or waiting for it): not delivered, returned or cancelled.</summary>
    public static bool IsOpen(ShipmentStatus status) => OpenStatuses.Contains(status);

    public static bool IsFinal(ShipmentStatus status) => !IsOpen(status);

    /// <summary>
    /// The statuses to pass through to get from <paramref name="from"/> to <paramref name="to"/> (shortest path, the target last). Empty when already there or when
    /// there is no way. Never goes through Cancelled unless that is the target.
    /// </summary>
    public static IReadOnlyList<ShipmentStatus> PathTo(ShipmentStatus from, ShipmentStatus to)
    {
        if (from == to)
        {
            return [];
        }

        var previous = new Dictionary<ShipmentStatus, ShipmentStatus>();
        var queue = new Queue<ShipmentStatus>();
        queue.Enqueue(from);
        var seen = new HashSet<ShipmentStatus> { from };
        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var next in Moves[current])
            {
                if (!seen.Add(next) || (next == ShipmentStatus.Cancelled && to != ShipmentStatus.Cancelled))
                {
                    continue;
                }

                previous[next] = current;
                if (next == to)
                {
                    var path = new List<ShipmentStatus> { to };
                    var step = to;
                    while (previous[step] != from)
                    {
                        step = previous[step];
                        path.Insert(0, step);
                    }

                    return path;
                }

                queue.Enqueue(next);
            }
        }

        return [];
    }

    /// <summary>The order status a shipment status brings a forward shipment's order to. Null for Booked and Cancelled (the order is not touched).</summary>
    public static OrderStatus? OrderStatusFor(ShipmentStatus status) => status switch
    {
        ShipmentStatus.PickedUp => OrderStatus.HandedToCourier,
        ShipmentStatus.InTransit => OrderStatus.InTransit,
        ShipmentStatus.OutForDelivery => OrderStatus.OutForDelivery,
        ShipmentStatus.Delivered => OrderStatus.Delivered,
        ShipmentStatus.DeliveryFailed => OrderStatus.DeliveryFailed,
        ShipmentStatus.Returned => OrderStatus.Returned,
        _ => null,
    };

    /// <summary>The delivery-track value the order shows once the shipment has this status (a booked or cancelled parcel leaves a packed order Ready to ship).</summary>
    public static DeliveryStatus DeliveryTrackFor(ShipmentStatus status) =>
        OrderStatusFor(status) is { } order ? OrderStateMachine.DeliveryFor(order) : DeliveryStatus.ReadyToShip;
}
