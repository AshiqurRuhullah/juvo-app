using Juvo.Domain.Common;

namespace Juvo.Domain.Returns;

/// <summary>One event for every return status change; <c>From</c> is null for the request itself.</summary>
public sealed record ReturnStatusChanged(
    DateTimeOffset At, Guid ReturnId, string Number, Guid BranchId, Guid OrderId, string OrderNumber, Guid CustomerId, string CustomerMobile, ReturnKind Kind, string? From, string To, string? Reason)
    : DomainEvent(At);

public sealed record NewReturn(
    string Number, Guid BranchId, Guid OrderId, string OrderNumber, Guid CustomerId, string CustomerMobile, string? CustomerName, ReturnKind Kind, string ReasonCode, string? ReasonText,
    ReturnFault Fault, string? IdempotencyKey, IReadOnlyList<string> PhotoPaths);

/// <summary>A line of a new return with the order line facts and the refund of that line already worked out by the service.</summary>
public sealed record NewReturnItem(
    Guid OrderItemId, Guid VariantId, Guid ProductId, string Name, string Sku, string? ColorName, string? Size, string? ImagePath, int Quantity, decimal UnitPrice, decimal LineRefund,
    Guid? ExchangeVariantId, string? ExchangeSku);

/// <summary>What staff found for one line.</summary>
public sealed record ItemInspection(Guid ReturnItemId, ItemCondition Condition, bool Restock, bool AcceptRefund);

/// <summary>
/// A return or exchange request (module L). One request belongs to one order and one customer, has lines (an order line and a quantity), and follows the
/// state machine in <see cref="ReturnStateMachine"/>. Every change writes an append-only history row and raises a domain event (BR-003). The prices and names
/// are snapshots of the order, so later catalog edits do not change what is refunded.
/// </summary>
public sealed class ReturnRequest : AggregateRoot, IBranchScoped
{
    private readonly List<ReturnItem> _items = [];
    private readonly List<ReturnStatusHistory> _history = [];
    private readonly List<ReturnNote> _notes = [];
    private readonly List<ReturnPhoto> _photos = [];
    private readonly List<ReturnRefund> _refunds = [];

    private ReturnRequest()
    {
    }

    public string Number { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public Guid OrderId { get; private set; }

    public string OrderNumber { get; private set; } = string.Empty;

    public Guid CustomerId { get; private set; }

    [AuditIgnore]
    public string CustomerMobile { get; private set; } = string.Empty;

    [AuditIgnore]
    public string? CustomerName { get; private set; }

    public ReturnKind Kind { get; private set; }

    public ReturnStatus Status { get; private set; } = ReturnStatus.Requested;

    public string ReasonCode { get; private set; } = string.Empty;

    [AuditIgnore]
    public string? ReasonText { get; private set; }

    /// <summary>Starts from the reason the customer chose; the inspector can change it.</summary>
    public ReturnFault Fault { get; private set; }

    public string? IdempotencyKey { get; private set; }

    public string? RejectReason { get; private set; }

    // ---- Pickup ----
    public DateOnly? PickupDate { get; private set; }

    public string? PickupCourier { get; private set; }

    public string? PickupNote { get; private set; }

    // ---- Money (BDT) ----
    /// <summary>Sum of the accepted line refunds.</summary>
    public decimal ItemsRefund { get; private set; }

    /// <summary>The outbound delivery charge given back (only when the seller is at fault and it was not refunded before).</summary>
    public decimal DeliveryChargeRefund { get; private set; }

    /// <summary>The courier return charge kept from the refund when the customer is at fault (FR-L07).</summary>
    public decimal ReturnCharge { get; private set; }

    /// <summary>What goes back to the customer: items plus delivery charge minus return charge, never below zero. Set at inspection.</summary>
    public decimal RefundTotal { get; private set; }

    public Guid? ReplacementOrderId { get; private set; }

    public string? ReplacementOrderNumber { get; private set; }

    // ---- Timestamps ----
    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public DateTimeOffset? RejectedAt { get; private set; }

    public DateTimeOffset? PickupScheduledAt { get; private set; }

    public DateTimeOffset? PickedUpAt { get; private set; }

    public DateTimeOffset? ReceivedAt { get; private set; }

    public DateTimeOffset? InspectedAt { get; private set; }

    public DateTimeOffset? RefundedAt { get; private set; }

    public DateTimeOffset? ExchangedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public IReadOnlyList<ReturnItem> Items => _items;

    public IReadOnlyList<ReturnStatusHistory> History => _history;

    public IReadOnlyList<ReturnNote> Notes => _notes;

    public IReadOnlyList<ReturnPhoto> Photos => _photos;

    public IReadOnlyList<ReturnRefund> Refunds => _refunds;

    public int TotalQuantity => _items.Sum(i => i.Quantity);

    public static ReturnRequest Request(NewReturn data, IReadOnlyList<NewReturnItem> items, Guid? byId, string? byName, DateTimeOffset now)
    {
        if (items.Count == 0 || items.Any(i => i.Quantity < 1))
        {
            throw new ArgumentException("A return needs at least one line with a positive quantity.", nameof(items));
        }

        var request = new ReturnRequest
        {
            Number = data.Number,
            BranchId = data.BranchId,
            OrderId = data.OrderId,
            OrderNumber = data.OrderNumber,
            CustomerId = data.CustomerId,
            CustomerMobile = data.CustomerMobile,
            CustomerName = data.CustomerName,
            Kind = data.Kind,
            ReasonCode = data.ReasonCode,
            ReasonText = string.IsNullOrWhiteSpace(data.ReasonText) ? null : data.ReasonText.Trim(),
            Fault = data.Fault,
            IdempotencyKey = data.IdempotencyKey,
            RequestedAt = now,
        };

        foreach (var item in items)
        {
            request._items.Add(new ReturnItem(request.Id, item));
        }

        var order = 0;
        foreach (var path in data.PhotoPaths)
        {
            request._photos.Add(new ReturnPhoto(request.Id, path, order++));
        }

        request.ItemsRefund = request._items.Sum(i => i.RefundAmount);
        request._history.Add(new ReturnStatusHistory(request.Id, null, ReturnStatus.Requested, data.ReasonCode, byId, byName, now));
        request.Raise(new ReturnStatusChanged(now, request.Id, request.Number, request.BranchId, request.OrderId, request.OrderNumber, request.CustomerId, request.CustomerMobile, request.Kind, null,
            ReturnStatus.Requested.ToString(), data.ReasonCode));
        return request;
    }

    private void Move(ReturnStatus to, Guid? by, string? byName, string? reason, DateTimeOffset now)
    {
        if (!ReturnStateMachine.CanMove(Status, to))
        {
            throw new InvalidOperationException($"A return cannot move from {Status} to {to}.");
        }

        var from = Status;
        Status = to;
        _history.Add(new ReturnStatusHistory(Id, from, to, reason, by, byName, now));
        Raise(new ReturnStatusChanged(now, Id, Number, BranchId, OrderId, OrderNumber, CustomerId, CustomerMobile, Kind, from.ToString(), to.ToString(),
            string.IsNullOrWhiteSpace(reason) ? null : reason.Trim()));
    }

    public void Approve(Guid? by, string? byName, string? note, DateTimeOffset now)
    {
        Move(ReturnStatus.Approved, by, byName, note, now);
        ApprovedAt = now;
    }

    public void Reject(string reason, Guid? by, string? byName, DateTimeOffset now)
    {
        Move(ReturnStatus.Rejected, by, byName, reason, now);
        RejectedAt = now;
        RejectReason = reason.Trim();
    }

    public void SchedulePickup(DateOnly date, string? courier, string? note, Guid? by, string? byName, DateTimeOffset now)
    {
        Move(ReturnStatus.PickupScheduled, by, byName, note, now);
        PickupScheduledAt = now;
        PickupDate = date;
        PickupCourier = string.IsNullOrWhiteSpace(courier) ? null : courier.Trim();
        PickupNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public void MarkPickedUp(Guid? by, string? byName, DateTimeOffset now)
    {
        Move(ReturnStatus.PickedUp, by, byName, null, now);
        PickedUpAt = now;
    }

    public void MarkReceived(Guid? by, string? byName, DateTimeOffset now)
    {
        Move(ReturnStatus.Received, by, byName, null, now);
        ReceivedAt = now;
    }

    /// <summary>
    /// Records what staff found. The service works out <paramref name="deliveryChargeRefund"/> and <paramref name="returnCharge"/> from the final fault and the policy;
    /// the totals are summed here. An exchange refunds nothing.
    /// </summary>
    public void Inspect(IReadOnlyList<ItemInspection> inspections, ReturnFault fault, decimal deliveryChargeRefund, decimal returnCharge, string? note, Guid? by, string? byName, DateTimeOffset now)
    {
        if (Status != ReturnStatus.Received)
        {
            throw new InvalidOperationException($"A return cannot move from {Status} to {ReturnStatus.Inspected}.");
        }

        var results = _items.Select(item => (Item: item, Found: inspections.FirstOrDefault(i => i.ReturnItemId == item.Id)
                                                                 ?? throw new ArgumentException($"Line {item.Id} has no inspection result.", nameof(inspections)))).ToList();
        foreach (var (item, found) in results)
        {
            item.Inspect(found.Condition, found.Restock, found.AcceptRefund);
        }

        Fault = fault;
        if (Kind == ReturnKind.Return)
        {
            ItemsRefund = _items.Sum(i => i.RefundAmount);
            DeliveryChargeRefund = ItemsRefund > 0 ? deliveryChargeRefund : 0m;
            ReturnCharge = Math.Min(returnCharge, ItemsRefund + DeliveryChargeRefund);
            RefundTotal = Math.Max(0m, ItemsRefund + DeliveryChargeRefund - ReturnCharge);
        }
        else
        {
            ItemsRefund = 0m;
            DeliveryChargeRefund = 0m;
            ReturnCharge = 0m;
            RefundTotal = 0m;
        }

        Move(ReturnStatus.Inspected, by, byName, note, now);
        InspectedAt = now;
    }

    /// <summary>Gives the money back. Only a Return (not an Exchange) that was inspected and has something to refund.</summary>
    public ReturnRefund Refund(RefundMethod method, string reference, Guid? by, string? byName, DateTimeOffset now)
    {
        if (Kind != ReturnKind.Return)
        {
            throw new InvalidOperationException("An exchange has no refund.");
        }

        if (_refunds.Count > 0)
        {
            throw new InvalidOperationException("This return was already refunded.");
        }

        if (RefundTotal <= 0)
        {
            throw new InvalidOperationException("There is nothing to refund on this return.");
        }

        Move(ReturnStatus.Refunded, by, byName, $"Refunded BDT {RefundTotal:0.##} ({method})", now);
        RefundedAt = now;
        var refund = new ReturnRefund(Id, OrderId, RefundTotal, method, RefundStatus.Completed, reference.Trim(), by, byName, now);
        _refunds.Add(refund);
        return refund;
    }

    /// <summary>Gives the customer the other size or colour: the service created the linked zero-value order.</summary>
    public void Exchange(Guid replacementOrderId, string replacementOrderNumber, Guid? by, string? byName, DateTimeOffset now)
    {
        if (Kind != ReturnKind.Exchange)
        {
            throw new InvalidOperationException("A return is refunded, not exchanged.");
        }

        Move(ReturnStatus.Exchanged, by, byName, $"Replacement order {replacementOrderNumber}", now);
        ExchangedAt = now;
        ReplacementOrderId = replacementOrderId;
        ReplacementOrderNumber = replacementOrderNumber;
    }

    /// <summary>The inspection found nothing to give back (item not as returned, used or damaged by the customer).</summary>
    public void RejectRefund(string reason, Guid? by, string? byName, DateTimeOffset now)
    {
        Move(ReturnStatus.RefundRejected, by, byName, reason, now);
        RejectReason = reason.Trim();
    }

    public void Cancel(string? reason, Guid? by, string? byName, DateTimeOffset now)
    {
        Move(ReturnStatus.Cancelled, by, byName, reason, now);
        CancelledAt = now;
    }

    public ReturnNote AddNote(string text, Guid? by, string? byName, DateTimeOffset now)
    {
        var note = new ReturnNote(Id, text.Trim(), by, byName, now);
        _notes.Add(note);
        return note;
    }
}

public sealed class ReturnItem : Entity
{
    private ReturnItem()
    {
    }

    internal ReturnItem(Guid returnId, NewReturnItem item)
    {
        ReturnRequestId = returnId;
        OrderItemId = item.OrderItemId;
        VariantId = item.VariantId;
        ProductId = item.ProductId;
        Name = item.Name;
        Sku = item.Sku;
        ColorName = item.ColorName;
        Size = item.Size;
        ImagePath = item.ImagePath;
        Quantity = item.Quantity;
        UnitPrice = item.UnitPrice;
        LineRefund = item.LineRefund;
        RefundAmount = item.LineRefund;
        ExchangeVariantId = item.ExchangeVariantId;
        ExchangeSku = item.ExchangeSku;
    }

    public Guid ReturnRequestId { get; private set; }

    public Guid OrderItemId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Sku { get; private set; } = string.Empty;

    public string? ColorName { get; private set; }

    public string? Size { get; private set; }

    public string? ImagePath { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>The price the customer paid per unit (order snapshot, tax inclusive).</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>The line refund worked out at request time: price times quantity minus the proportional share of the order discounts.</summary>
    public decimal LineRefund { get; private set; }

    /// <summary>What this line really refunds: <see cref="LineRefund"/>, or zero when the inspector did not accept it.</summary>
    public decimal RefundAmount { get; private set; }

    public Guid? ExchangeVariantId { get; private set; }

    public string? ExchangeSku { get; private set; }

    public ItemCondition? Condition { get; private set; }

    public bool? Restock { get; private set; }

    /// <summary>True once the units went back on the shelf (never twice).</summary>
    public bool Restocked { get; private set; }

    public bool RefundAccepted { get; private set; } = true;

    internal void Inspect(ItemCondition condition, bool restock, bool acceptRefund)
    {
        Condition = condition;
        Restock = restock;
        RefundAccepted = acceptRefund;
        RefundAmount = acceptRefund ? LineRefund : 0m;
    }

    public void MarkRestocked() => Restocked = true;
}

/// <summary>One status change of a return: who, when, from, to, why. Append-only (BR-003).</summary>
public sealed class ReturnStatusHistory : Entity, IAppendOnly, INotAudited
{
    private ReturnStatusHistory()
    {
    }

    public ReturnStatusHistory(Guid returnId, ReturnStatus? from, ReturnStatus to, string? reason, Guid? actorId, string? actorName, DateTimeOffset at)
    {
        ReturnRequestId = returnId;
        FromStatus = from?.ToString();
        ToStatus = to.ToString();
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        ActorId = actorId;
        ActorName = actorName;
        At = at;
    }

    public Guid ReturnRequestId { get; private set; }

    public string? FromStatus { get; private set; }

    public string ToStatus { get; private set; } = string.Empty;

    public string? Reason { get; private set; }

    /// <summary>The staff member or the customer; null when the system acted.</summary>
    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }

    public DateTimeOffset At { get; private set; }
}

/// <summary>An internal staff note on a return.</summary>
public sealed class ReturnNote : Entity, INotAudited
{
    private ReturnNote()
    {
    }

    internal ReturnNote(Guid returnId, string text, Guid? authorId, string? authorName, DateTimeOffset at)
    {
        ReturnRequestId = returnId;
        Text = text;
        AuthorId = authorId;
        AuthorName = authorName;
        At = at;
    }

    public Guid ReturnRequestId { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public Guid? AuthorId { get; private set; }

    public string? AuthorName { get; private set; }

    public DateTimeOffset At { get; private set; }
}

/// <summary>A picture the customer attached as evidence (stored through <c>IFileStorage</c>; the thumbnail sits next to the main file).</summary>
public sealed class ReturnPhoto : Entity, INotAudited
{
    private ReturnPhoto()
    {
    }

    internal ReturnPhoto(Guid returnId, string path, int sortOrder)
    {
        ReturnRequestId = returnId;
        Path = path;
        SortOrder = sortOrder;
    }

    public Guid ReturnRequestId { get; private set; }

    public string Path { get; private set; } = string.Empty;

    public int SortOrder { get; private set; }
}

/// <summary>
/// Money given back to the customer. Append-only. Step 9 (payments) adds the gateway call for original-payment refunds; until then staff do the transfer
/// themselves and write down the reference, which is why every refund here is Completed.
/// </summary>
public sealed class ReturnRefund : Entity, IAppendOnly, INotAudited
{
    private ReturnRefund()
    {
    }

    internal ReturnRefund(Guid returnId, Guid orderId, decimal amount, RefundMethod method, RefundStatus status, string reference, Guid? actorId, string? actorName, DateTimeOffset at)
    {
        ReturnRequestId = returnId;
        OrderId = orderId;
        Amount = amount;
        Method = method;
        Status = status;
        Reference = reference;
        ActorId = actorId;
        ActorName = actorName;
        At = at;
    }

    public Guid ReturnRequestId { get; private set; }

    public Guid OrderId { get; private set; }

    public decimal Amount { get; private set; }

    public RefundMethod Method { get; private set; }

    public RefundStatus Status { get; private set; }

    public string Reference { get; private set; } = string.Empty;

    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }

    public DateTimeOffset At { get; private set; }
}

/// <summary>Per branch and month counter behind the gap-free return numbers (<c>RT-DHK01-2611-000007</c>).</summary>
public sealed class ReturnSequence : Entity, INotAudited
{
    private ReturnSequence()
    {
    }

    public ReturnSequence(Guid branchId, string period, int lastNumber)
    {
        BranchId = branchId;
        Period = period;
        LastNumber = lastNumber;
    }

    public Guid BranchId { get; private set; }

    /// <summary>Year and month as <c>yyMM</c>.</summary>
    public string Period { get; private set; } = string.Empty;

    public int LastNumber { get; private set; }
}
