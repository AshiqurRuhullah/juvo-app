using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Billing;
using Juvo.Application.Notifications;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Orders;

/// <summary>
/// Reacts to order events from the outbox (at-least-once, so every handler is idempotent). The customer is told by SMS and email (and WhatsApp when switched on) through the
/// notification templates, in their language; the log row is deduplicated by the outbox message id, so a redelivered event sends nothing twice. The other events are acknowledged
/// and left to the modules that consume them (couriers, reports).
/// </summary>
public abstract class OrderEventHandler(NotificationService notifications, string messageType) : IOutboxMessageHandler
{
    public string MessageType => messageType;

    public abstract Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken);

    protected static T Read<T>(OutboxMessageContext context) =>
        JsonSerializer.Deserialize<T>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException($"The {context.MessageType} message has no content.");

    protected Task NotifyAsync(OutboxMessageContext context, string code, string mobile, Guid customerId, Dictionary<string, string> variables, CancellationToken cancellationToken) =>
        notifications.NotifyAsync(new NotifyRequest(code, mobile, null, customerId, variables, $"{context.MessageId:N}:{code}"), cancellationToken);
}

public sealed class OrderPlacedHandler(NotificationService notifications) : OrderEventHandler(notifications, "domain.OrderPlaced")
{
    public override Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = Read<OrderPlaced>(context);
        return NotifyAsync(context, "order.placed", e.CustomerMobile, e.CustomerId, NotifyVariables.Of(("OrderNumber", e.Number), ("Total", NotifyVariables.Money(e.GrandTotal))), cancellationToken);
    }
}

/// <summary>Order track and payment track changes become customer messages: order confirmed, packed, delivered; payment received, failed, refunded.</summary>
public sealed class OrderStatusChangedHandler(IApplicationDbContext db, NotificationService notifications) : OrderEventHandler(notifications, "domain.OrderStatusChanged")
{
    private static readonly Dictionary<string, string> OrderCodes = new(StringComparer.Ordinal)
    {
        [nameof(OrderStatus.Confirmed)] = "order.confirmed",
        [nameof(OrderStatus.Packed)] = "order.packed",
        [nameof(OrderStatus.HandedToCourier)] = "order.handed-to-courier",
        [nameof(OrderStatus.OutForDelivery)] = "order.out-for-delivery",
        [nameof(OrderStatus.Delivered)] = "order.delivered",
        [nameof(OrderStatus.DeliveryFailed)] = "order.delivery-failed",
        [nameof(OrderStatus.Returned)] = "order.returned-to-sender",
        [nameof(OrderStatus.Cancelled)] = "order.cancelled",
    };

    private static readonly Dictionary<string, string> PaymentCodes = new(StringComparer.Ordinal)
    {
        [nameof(PaymentStatus.Paid)] = "payment.received",
        [nameof(PaymentStatus.Failed)] = "payment.failed",
        [nameof(PaymentStatus.UnderReview)] = "payment.under-review",
        [nameof(PaymentStatus.Rejected)] = "payment.rejected",
        [nameof(PaymentStatus.PartiallyRefunded)] = "payment.refunded",
        [nameof(PaymentStatus.Refunded)] = "payment.refunded",
    };

    public override async Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = Read<OrderStatusChanged>(context);
        if (e.Track == OrderTrack.Order && e.From != nameof(OrderStatus.ReturnInProgress) && OrderCodes.TryGetValue(e.To, out var orderCode))
        {
            var total = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => o.Id == e.OrderId).Select(o => o.GrandTotal).FirstOrDefaultAsync(cancellationToken);
            await NotifyAsync(context, orderCode, e.CustomerMobile, e.CustomerId, NotifyVariables.Of(("OrderNumber", e.Number), ("Total", NotifyVariables.Money(total))), cancellationToken);
        }
        else if (e.Track == OrderTrack.Payment && PaymentCodes.TryGetValue(e.To, out var paymentCode))
        {
            var amount = await AmountAsync(e, cancellationToken);
            await NotifyAsync(context, paymentCode, e.CustomerMobile, e.CustomerId, NotifyVariables.Of(("OrderNumber", e.Number), ("Amount", NotifyVariables.Money(amount)), ("Reason", e.Reason)), cancellationToken);
        }
    }

    /// <summary>The money the message talks about: what was paid, or what the latest refund gave back.</summary>
    private async Task<decimal> AmountAsync(OrderStatusChanged e, CancellationToken cancellationToken)
    {
        if (e.To is nameof(PaymentStatus.PartiallyRefunded) or nameof(PaymentStatus.Refunded))
        {
            var refund = await db.Refunds.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(r => r.OrderId == e.OrderId && r.Status == RefundState.Completed).OrderByDescending(r => r.CompletedAt).Select(r => (decimal?)r.Amount).FirstOrDefaultAsync(cancellationToken);
            if (refund is { } given)
            {
                return given;
            }
        }

        var paid = await db.Payments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(p => p.OrderId == e.OrderId).OrderByDescending(p => p.CreatedAt).Select(p => (decimal?)p.Amount).FirstOrDefaultAsync(cancellationToken);
        return paid ?? await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => o.Id == e.OrderId).Select(o => o.GrandTotal).FirstOrDefaultAsync(cancellationToken);
    }
}

/// <summary>Issues the invoice when an order is confirmed (decision Q in docs/assumptions.md) and tells the customer it is ready. Idempotent: an order has one invoice.</summary>
public sealed class InvoiceOnConfirmedHandler(InvoiceService invoices, NotificationService notifications) : IOutboxMessageHandler
{
    public string MessageType => "domain.OrderStatusChanged";

    public async Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = JsonSerializer.Deserialize<OrderStatusChanged>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("The OrderStatusChanged message has no content.");
        if (e.Track != OrderTrack.Order || e.To != nameof(OrderStatus.Confirmed))
        {
            return;
        }

        var issued = await invoices.EnsureForOrderAsync(e.OrderId, null, "System", false, cancellationToken);
        if (issued.IsFailure)
        {
            throw new InvalidOperationException(issued.Error!.Message); // the outbox retries
        }

        await notifications.NotifyAsync(new NotifyRequest("invoice.ready", e.CustomerMobile, null, e.CustomerId,
            NotifyVariables.Of(("OrderNumber", e.Number), ("InvoiceNumber", issued.Value.Number)), $"{context.MessageId:N}:invoice.ready"), cancellationToken);
    }
}

public sealed class OrderBranchReassignedHandler(NotificationService notifications) : OrderEventHandler(notifications, "domain.OrderBranchReassigned")
{
    public override Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken) => Task.CompletedTask; // staff-only change: nothing to tell the customer
}

public sealed class OrderFlaggedHandler(NotificationService notifications) : OrderEventHandler(notifications, "domain.OrderFlagged")
{
    public override Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken) => Task.CompletedTask; // the review queue reads the order itself
}
