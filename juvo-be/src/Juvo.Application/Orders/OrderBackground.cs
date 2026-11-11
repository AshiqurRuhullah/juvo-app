using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Orders;

/// <summary>
/// The payment-hold timeout (BR-D2): an online-payment order that is still unpaid when its hold ends is cancelled and its stock is released. Safe to run
/// from several workers: each order is re-read and re-checked inside its own transaction, so one that was paid a moment ago is left alone.
/// </summary>
public sealed class OrderTimeoutService(IApplicationDbContext db, IUnitOfWork uow, OrderWorkflow workflow, SettingsService settings, TimeProvider clock)
{
    public const int BatchSize = 50;

    /// <summary>Cancels expired unpaid orders (up to one batch). Returns how many were cancelled.</summary>
    public async Task<int> ExpireAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking()
            .Where(o => o.Status == OrderStatus.Placed && o.PaymentMethod != "COD" && (o.PaymentStatus == PaymentStatus.Pending || o.PaymentStatus == PaymentStatus.Failed) &&
                        o.PaymentHoldExpiresAt != null && o.PaymentHoldExpiresAt <= now)
            .OrderBy(o => o.PaymentHoldExpiresAt).Select(o => o.Id).Take(BatchSize).ToListAsync(cancellationToken);
        var minutes = (await settings.OrdersAsync(cancellationToken)).PaymentHoldMinutes;
        var cancelled = 0;
        foreach (var id in due)
        {
            var done = await uow.ExecuteInTransactionAsync(async ct =>
            {
                var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).Include(o => o.History).Include(o => o.Notes)
                    .FirstOrDefaultAsync(o => o.Id == id, ct);
                if (order is null || order.Status != OrderStatus.Placed || order.PaymentHoldExpiresAt is not { } until || until > now ||
                    order.PaymentStatus is not (PaymentStatus.Pending or PaymentStatus.Failed))
                {
                    return false;
                }

                var result = await workflow.CancelAsync(order, $"Payment not received within {minutes} minutes", Who.System, ct);
                if (result.IsFailure)
                {
                    return false;
                }

                await db.SaveChangesAsync(ct);
                return true;
            }, cancellationToken);
            if (done)
            {
                cancelled++;
            }
        }

        return cancelled;
    }
}

/// <summary>
/// Reacts to order events from the outbox (at-least-once, so every handler is idempotent). The customer gets a short SMS (sandbox: written to the
/// notification log) for the steps they care about; the other events are acknowledged and left to the modules that consume them (couriers, reports).
/// </summary>
public abstract class OrderEventHandler(IApplicationDbContext db, ISmsSender sms, string messageType) : IOutboxMessageHandler
{
    public string MessageType => messageType;

    public abstract Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken);

    protected static T Read<T>(OutboxMessageContext context) =>
        JsonSerializer.Deserialize<T>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException($"The {context.MessageType} message has no content.");

    /// <summary>Sends once: an identical message to the same number is not sent again when the outbox redelivers.</summary>
    protected async Task SendOnceAsync(string mobile, string template, string body, CancellationToken cancellationToken)
    {
        if (await db.NotificationLogs.AnyAsync(n => n.Recipient == mobile && n.Template == template && n.Body == body, cancellationToken))
        {
            return;
        }

        await sms.SendAsync(mobile, template, body, cancellationToken);
    }
}

public sealed class OrderPlacedHandler(IApplicationDbContext db, ISmsSender sms) : OrderEventHandler(db, sms, "domain.OrderPlaced")
{
    public override async Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = Read<OrderPlaced>(context);
        await SendOnceAsync(e.CustomerMobile, "order.placed", $"Juvo BD: we received your order {e.Number}. Total BDT {e.GrandTotal:0.##}. Thank you!", cancellationToken);
    }
}

public sealed class OrderStatusChangedHandler(IApplicationDbContext db, ISmsSender sms) : OrderEventHandler(db, sms, "domain.OrderStatusChanged")
{
    private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
    {
        [nameof(OrderStatus.Confirmed)] = "is confirmed",
        [nameof(OrderStatus.Packed)] = "is packed and will be handed to the courier soon",
        [nameof(OrderStatus.HandedToCourier)] = "was handed to the courier",
        [nameof(OrderStatus.OutForDelivery)] = "is out for delivery",
        [nameof(OrderStatus.Delivered)] = "was delivered",
        [nameof(OrderStatus.Cancelled)] = "was cancelled",
    };

    public override async Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = Read<OrderStatusChanged>(context);
        if (e.Track == OrderTrack.Order && Texts.TryGetValue(e.To, out var text))
        {
            await SendOnceAsync(e.CustomerMobile, "order.status", $"Juvo BD: your order {e.Number} {text}.", cancellationToken);
        }
    }
}

public sealed class OrderBranchReassignedHandler(IApplicationDbContext db, ISmsSender sms) : OrderEventHandler(db, sms, "domain.OrderBranchReassigned")
{
    public override Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken) => Task.CompletedTask; // staff-only change: nothing to tell the customer
}

public sealed class OrderFlaggedHandler(IApplicationDbContext db, ISmsSender sms) : OrderEventHandler(db, sms, "domain.OrderFlagged")
{
    public override Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken) => Task.CompletedTask; // the review queue reads the order itself
}
