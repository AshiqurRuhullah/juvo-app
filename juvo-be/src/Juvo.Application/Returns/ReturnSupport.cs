using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Inventory;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Returns;

/// <summary>Turns returns into the shapes the API returns. Customers never get staff names, internal notes or the fault.</summary>
internal static class ReturnViews
{
    public static List<ReturnItemDto> Items(ReturnRequest r, IFileStorage storage) =>
        [.. r.Items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Sku).Select(i =>
            new ReturnItemDto(i.Id, i.OrderItemId, i.VariantId, i.ProductId, i.Name, i.Sku, i.ColorName, i.Size, OrderViews.ImageUrl(i.ImagePath, storage), i.Quantity, i.UnitPrice, i.LineRefund,
                i.RefundAmount, i.ExchangeVariantId, i.ExchangeSku, i.Condition, i.Restock, i.Restocked, i.RefundAccepted))];

    public static List<ReturnPhotoDto> Photos(ReturnRequest r, IFileStorage storage) =>
        [.. r.Photos.OrderBy(p => p.SortOrder).Select(p => new ReturnPhotoDto(p.Path, storage.GetPublicUrl(p.Path), storage.GetPublicUrl(ImageRenditions.Thumb(p.Path))))];

    public static List<ReturnTimelineDto> StaffTimeline(ReturnRequest r) =>
        [.. r.History.OrderBy(h => h.At).ThenBy(h => h.CreatedAt).Select(h => new ReturnTimelineDto(h.FromStatus, h.ToStatus, h.Reason, h.ActorName, h.At))];

    /// <summary>Statuses and times; a reason only when the customer wrote it or the shop explained a refusal.</summary>
    public static List<ReturnTimelineDto> CustomerTimeline(ReturnRequest r) =>
        [.. r.History.OrderBy(h => h.At).ThenBy(h => h.CreatedAt).Select(h =>
            new ReturnTimelineDto(h.FromStatus, h.ToStatus,
                h.ActorId == r.CustomerId || h.ToStatus is nameof(ReturnStatus.Rejected) or nameof(ReturnStatus.RefundRejected) ? h.Reason : null, null, h.At))];

    public static ReturnPickupDto? Pickup(ReturnRequest r) =>
        r.PickupDate is null && r.PickupCourier is null && r.PickupNote is null ? null : new ReturnPickupDto(r.PickupDate, r.PickupCourier, r.PickupNote);

    /// <summary>The refund as it stands: an estimate of the items until the parcel is inspected, final afterwards.</summary>
    public static RefundBreakdownDto Breakdown(ReturnRequest r)
    {
        var final = r.InspectedAt is not null;
        return final
            ? new RefundBreakdownDto(r.ItemsRefund, r.DeliveryChargeRefund, r.ReturnCharge, r.RefundTotal, true)
            : new RefundBreakdownDto(r.Items.Sum(i => i.LineRefund), 0m, 0m, r.Kind == ReturnKind.Return ? r.Items.Sum(i => i.LineRefund) : 0m, false);
    }

    public static ReturnRefundDto? Refund(ReturnRequest r) =>
        r.Refunds.OrderBy(x => x.At).FirstOrDefault() is { } x ? new ReturnRefundDto(x.Id, x.Amount, x.Method, x.Status, x.Reference, x.ActorName, x.At) : null;

    public static bool CanCancel(ReturnRequest r) => ReturnStateMachine.CanMove(r.Status, ReturnStatus.Cancelled);
}

/// <summary>
/// Keeps the order in step with its returns (module L): an approved return moves the order to ReturnInProgress; when every unit is covered by a completed
/// return (refunded or exchanged) the order is Returned and its delivery track follows; when no return is open any more and something stays with the customer,
/// the order goes back to Delivered. Payment: a refund moves Paid (or cash collected) to PartiallyRefunded, and to Refunded once every unit was refunded.
/// </summary>
public sealed class ReturnOrderSync(IApplicationDbContext db, OrderWorkflow workflow, TimeProvider clock)
{
    /// <summary>The order with the parts the workflow writes to (lines, history, notes), ignoring the branch filter (the caller proved access).</summary>
    public Task<Order?> LoadOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).Include(o => o.History).Include(o => o.Notes).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    public async Task SyncOrderAsync(Order order, Who who, CancellationToken cancellationToken)
    {
        var returns = await db.ReturnRequests.IgnoreQueryFilters(["BranchScope"]).Include(r => r.Items).Where(r => r.OrderId == order.Id).ToListAsync(cancellationToken);
        var open = returns.Any(r => ReturnStateMachine.IsOpen(r.Status));
        var covered = returns.Where(r => ReturnStateMachine.IsCompleted(r.Status)).SelectMany(r => r.Items).GroupBy(i => i.OrderItemId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        var everything = order.Items.All(i => covered.GetValueOrDefault(i.Id) >= i.Quantity);
        var now = clock.GetUtcNow();

        if (everything && order.Status is OrderStatus.Delivered or OrderStatus.ReturnInProgress)
        {
            if (order.Status == OrderStatus.Delivered)
            {
                order.TransitionTo(OrderStatus.ReturnInProgress, now, who.Id, who.Name, "Return in progress");
            }

            order.TransitionTo(OrderStatus.Returned, now, who.Id, who.Name, "Every item was returned");
        }
        else if (open && order.Status == OrderStatus.Delivered)
        {
            order.TransitionTo(OrderStatus.ReturnInProgress, now, who.Id, who.Name, "A return was approved");
        }
        else if (!open && order.Status == OrderStatus.ReturnInProgress && !everything)
        {
            order.ReopenAsDelivered(now, who.Id, who.Name, "No return is open any more");
        }
    }

    /// <summary>After a refund: record it on the payment track (record only; the money itself is moved by staff, or by the gateway from Step 9).</summary>
    public async Task<Result> AfterRefundAsync(Order order, ReturnRequest refunded, Who who, CancellationToken cancellationToken)
    {
        var refundedQuantity = (await db.ReturnRequests.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(r => r.OrderId == order.Id && r.Status == ReturnStatus.Refunded && r.Id != refunded.Id)
            .SelectMany(r => r.Items).Select(i => new { i.OrderItemId, i.Quantity }).ToListAsync(cancellationToken))
            .GroupBy(i => i.OrderItemId).ToDictionary(g => g.Key, g => g.Sum(i => i.Quantity));
        foreach (var item in refunded.Items)
        {
            refundedQuantity[item.OrderItemId] = refundedQuantity.GetValueOrDefault(item.OrderItemId) + item.Quantity;
        }

        var all = order.Items.All(i => refundedQuantity.GetValueOrDefault(i.Id) >= i.Quantity);
        var target = all ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        if (order.PaymentStatus == target)
        {
            return Result.Success();
        }

        return workflow.ChangePayment(order, target, $"Refund for return {refunded.Number}", who);
    }

    public static bool IsPaid(Order order) =>
        order.PaymentStatus is PaymentStatus.Paid or PaymentStatus.PartiallyRefunded or PaymentStatus.CollectedByCourier or PaymentStatus.Settled;
}

/// <summary>Puts returned units back on the shelf of the fulfilling branch, with an append-only movement per line (FR-L06).</summary>
public sealed class ReturnStock(IApplicationDbContext db, OrderStock stock)
{
    public const string ReferenceType = "Return";

    /// <summary>Restocks every accepted-for-shelf line that tracks stock. Marks each line restocked so it can never happen twice.</summary>
    public async Task RestockAsync(ReturnRequest ret, Order order, CancellationToken cancellationToken)
    {
        var tracked = order.Items.Where(i => i.TracksStock).Select(i => i.Id).ToHashSet();
        var items = ret.Items.Where(i => i.Restock == true && !i.Restocked && tracked.Contains(i.OrderItemId)).ToList();
        if (items.Count == 0)
        {
            return;
        }

        var lines = items.GroupBy(i => i.VariantId).Select(g => new StockLine(g.Key, g.Sum(i => i.Quantity))).ToList();
        var variantIds = lines.Select(l => l.VariantId).ToList();
        var existing = await db.StockLevels.Where(s => s.BranchId == ret.BranchId && variantIds.Contains(s.VariantId)).Select(s => s.VariantId).ToListAsync(cancellationToken);
        foreach (var missing in variantIds.Except(existing))
        {
            db.StockLevels.Add(new StockLevel(ret.BranchId, missing));
        }

        if (variantIds.Count != existing.Count)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        await stock.RestockAsync(ret.BranchId, lines, cancellationToken);
        var onHand = await db.StockLevels.AsNoTracking().Where(s => s.BranchId == ret.BranchId && variantIds.Contains(s.VariantId)).ToDictionaryAsync(s => s.VariantId, s => s.OnHand, cancellationToken);
        foreach (var line in lines)
        {
            db.StockMovements.Add(new StockMovement(ret.BranchId, line.VariantId, line.Quantity, onHand.GetValueOrDefault(line.VariantId), StockReason.CustomerReturn, $"Return {ret.Number}", ReferenceType, ret.Id));
        }

        foreach (var item in items)
        {
            item.MarkRestocked();
        }
    }
}

/// <summary>
/// Reacts to return events from the outbox (at-least-once, so the handler is idempotent). The customer gets a short SMS (sandbox: written to the
/// notification log) at the steps they care about; an identical message to the same number is never sent twice.
/// </summary>
public sealed class ReturnStatusChangedHandler(IApplicationDbContext db, ISmsSender sms) : IOutboxMessageHandler
{
    public string MessageType => "domain.ReturnStatusChanged";

    private static readonly Dictionary<string, string> Texts = new(StringComparer.Ordinal)
    {
        [nameof(ReturnStatus.Requested)] = "we received your {0} request {1}. We will review it soon",
        [nameof(ReturnStatus.Approved)] = "your {0} request {1} is approved. We will arrange the pickup",
        [nameof(ReturnStatus.Rejected)] = "your {0} request {1} could not be approved. Please call us for details",
        [nameof(ReturnStatus.PickupScheduled)] = "the pickup for your {0} {1} is scheduled",
        [nameof(ReturnStatus.Received)] = "we received the parcel of your {0} {1} and will check it",
        [nameof(ReturnStatus.Refunded)] = "your refund for {0} {1} was sent",
        [nameof(ReturnStatus.Exchanged)] = "the replacement for your {0} {1} is on its way",
        [nameof(ReturnStatus.RefundRejected)] = "we could not complete your {0} {1} after checking the parcel. Please call us",
    };

    public async Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = JsonSerializer.Deserialize<ReturnStatusChanged>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("The ReturnStatusChanged message has no content.");
        if (!Texts.TryGetValue(e.To, out var template))
        {
            return;
        }

        var word = e.Kind == ReturnKind.Exchange ? "exchange" : "return";
        var body = "Juvo BD: " + string.Format(System.Globalization.CultureInfo.InvariantCulture, template, word, e.Number) + ".";
        if (await db.NotificationLogs.AnyAsync(n => n.Recipient == e.CustomerMobile && n.Template == "return.status" && n.Body == body, cancellationToken))
        {
            return;
        }

        await sms.SendAsync(e.CustomerMobile, "return.status", body, cancellationToken);
    }
}
