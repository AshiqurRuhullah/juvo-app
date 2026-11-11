namespace Juvo.Domain.Orders;

/// <summary>
/// The allowed transitions of the order and payment tracks, exactly as written in CLAUDE.md section 7 / SRS 11.1. Anything not listed is refused.
/// </summary>
public static class OrderStateMachine
{
    private static readonly Dictionary<OrderStatus, OrderStatus[]> Order = new()
    {
        [OrderStatus.Placed] = [OrderStatus.Confirmed, OrderStatus.Cancelled],
        [OrderStatus.Confirmed] = [OrderStatus.Packed, OrderStatus.Cancelled],
        [OrderStatus.Packed] = [OrderStatus.HandedToCourier, OrderStatus.Cancelled],
        [OrderStatus.HandedToCourier] = [OrderStatus.InTransit],
        [OrderStatus.InTransit] = [OrderStatus.OutForDelivery],
        [OrderStatus.OutForDelivery] = [OrderStatus.Delivered, OrderStatus.DeliveryFailed],
        [OrderStatus.DeliveryFailed] = [OrderStatus.OutForDelivery, OrderStatus.Returned],
        [OrderStatus.Delivered] = [OrderStatus.ReturnInProgress],
        [OrderStatus.ReturnInProgress] = [OrderStatus.Returned],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Returned] = [],
    };

    private static readonly Dictionary<PaymentStatus, PaymentStatus[]> Payment = new()
    {
        [PaymentStatus.Pending] = [PaymentStatus.Paid, PaymentStatus.Failed, PaymentStatus.Cancelled, PaymentStatus.UnderReview, PaymentStatus.CollectedByCourier],
        [PaymentStatus.UnderReview] = [PaymentStatus.Paid, PaymentStatus.Rejected],
        [PaymentStatus.Paid] = [PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded],
        [PaymentStatus.PartiallyRefunded] = [PaymentStatus.Refunded],
        [PaymentStatus.CollectedByCourier] = [PaymentStatus.Settled],
        [PaymentStatus.Failed] = [PaymentStatus.Pending, PaymentStatus.Cancelled],
        [PaymentStatus.Cancelled] = [],
        [PaymentStatus.Rejected] = [],
        [PaymentStatus.Refunded] = [],
        [PaymentStatus.Settled] = [],
    };

    public static IReadOnlyList<OrderStatus> NextFor(OrderStatus from) => Order[from];

    public static IReadOnlyList<PaymentStatus> NextFor(PaymentStatus from) => Payment[from];

    public static bool CanMove(OrderStatus from, OrderStatus to) => Order[from].Contains(to);

    public static bool CanMove(PaymentStatus from, PaymentStatus to) => Payment[from].Contains(to);

    /// <summary>The statuses from which a customer or staff member may still cancel (before courier pickup).</summary>
    public static bool CanCancel(OrderStatus from) => CanMove(from, OrderStatus.Cancelled);

    /// <summary>The delivery track value that goes with an order status.</summary>
    public static DeliveryStatus DeliveryFor(OrderStatus status) => status switch
    {
        OrderStatus.Placed or OrderStatus.Confirmed => DeliveryStatus.NotShipped,
        OrderStatus.Packed => DeliveryStatus.ReadyToShip,
        OrderStatus.HandedToCourier => DeliveryStatus.HandedToCourier,
        OrderStatus.InTransit => DeliveryStatus.InTransit,
        OrderStatus.OutForDelivery => DeliveryStatus.OutForDelivery,
        OrderStatus.Delivered or OrderStatus.ReturnInProgress => DeliveryStatus.Delivered,
        OrderStatus.DeliveryFailed => DeliveryStatus.Failed,
        OrderStatus.Returned => DeliveryStatus.Returned,
        OrderStatus.Cancelled => DeliveryStatus.Cancelled,
        _ => DeliveryStatus.NotShipped,
    };
}
