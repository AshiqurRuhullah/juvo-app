using Juvo.Domain.Orders;
using Juvo.Domain.Payments;

namespace Juvo.Application.Payments;

/// <summary>Helpers that create the payment records cash on delivery needs. The order's own payment track moves on its own (cash is collected when the order is delivered); the payment record is added when money has to be tracked.</summary>
internal static class PaymentRecords
{
    /// <summary>
    /// The cash-on-delivery payment of an order (the total minus any advance), created when it does not exist yet and the order's payment track shows cash collected or settled.
    /// Returns null when there is nothing to track. The new payment is added to <paramref name="payments"/> and to the context; the caller saves.
    /// </summary>
    public static Payment? EnsureCod(Juvo.Application.Abstractions.IApplicationDbContext db, Order order, List<Payment> payments, DateTimeOffset now)
    {
        if (payments.FirstOrDefault(p => p.Kind == PaymentKind.Cod) is { } existing)
        {
            return existing;
        }

        var cash = order.GrandTotal - order.AdvancePaid;
        if (order.PaymentMethod != PaymentMethodCatalog.Cod || cash <= 0 || order.PaymentStatus is not (PaymentStatus.CollectedByCourier or PaymentStatus.Settled))
        {
            return null;
        }

        var created = Payment.Start(new PaymentInit(order.Id, order.Number, order.BranchId, PaymentKind.Cod, PaymentMethodCatalog.Cod, null, cash, order.Currency, null, null, null, null, null, null, null), now, null, "System");
        created.MoveTo(PaymentStatus.CollectedByCourier, "Collected", "Recorded from the order", now, null, "System");
        if (order.PaymentStatus == PaymentStatus.Settled)
        {
            created.MoveTo(PaymentStatus.Settled, "Settled", "Recorded from the order", now, null, "System");
        }

        db.Payments.Add(created);
        payments.Add(created);
        return created;
    }
}
