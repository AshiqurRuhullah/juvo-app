using Juvo.Domain.Common;

namespace Juvo.Domain.Orders;

public sealed record OrderPlaced(DateTimeOffset At, Guid OrderId, string Number, Guid BranchId, Guid CustomerId, string CustomerMobile, decimal GrandTotal, OrderSource Source) : DomainEvent(At);

/// <summary>One event for all three tracks: <c>Track</c> says which, <c>From</c> and <c>To</c> are the status names.</summary>
public sealed record OrderStatusChanged(DateTimeOffset At, Guid OrderId, string Number, Guid BranchId, Guid CustomerId, string CustomerMobile, OrderTrack Track, string From, string To, string? Reason)
    : DomainEvent(At);

public sealed record OrderBranchReassigned(DateTimeOffset At, Guid OrderId, string Number, Guid FromBranchId, Guid ToBranchId, string? Reason) : DomainEvent(At);

public sealed record OrderFlagged(DateTimeOffset At, Guid OrderId, string Number, int RiskScore, string FraudState) : DomainEvent(At);

/// <summary>Everything the order needs when it is placed. The service validates it first; the aggregate only stores it.</summary>
public sealed record NewOrder(
    string Number, Guid BranchId, OrderSource Source, Guid CustomerId, string CustomerMobile, string? CustomerName, string? CustomerEmail, string PaymentMethod,
    string RoutingRule, string? ZoneCode, string Currency, decimal Subtotal, decimal CouponDiscount, string? CouponCode, Guid? CouponId, decimal ManualDiscount, decimal DeliveryCharge,
    decimal AdvanceRequired, int WeightGrams, ShippingSnapshot Shipping, DateTimeOffset? PaymentHoldExpiresAt, string? IdempotencyKey, string Fingerprint, string TrackingTokenHash,
    string? DeviceId, string? ClientIp, bool OtpVerified, DateTimeOffset? TermsAcceptedAt, Guid? CreatedByStaffId, string? CreatedByName);

/// <summary>The delivery address copied onto the order (it never changes when the customer edits their address book).</summary>
public sealed record ShippingSnapshot(
    string RecipientName, string Phone, string CountryCode, Guid? DivisionId, string? DivisionName, Guid? DistrictId, string? DistrictName, Guid? UpazilaId, string? UpazilaName,
    string? Area, string AddressLine, string? Landmark, string? StateProvince, string? City, string? PostalCode, double? Latitude, double? Longitude);

/// <summary>One line of a new order with the product facts copied from the catalog at that moment (BR-001).</summary>
public sealed record NewOrderItem(
    Guid VariantId, Guid ProductId, string Name, string? NameBn, string Sku, string? ColorName, string? Size, string? ImagePath, decimal UnitPrice, int Quantity, int WeightGrams, bool TracksStock);

/// <summary>
/// An order (module D). One order has many variant lines (BR-001) and three parallel tracks: order, payment and delivery status. Every status
/// change writes an append-only history row and raises a domain event for the outbox (BR-003). Names, prices and the address are snapshots.
/// </summary>
public sealed class Order : AggregateRoot, IBranchScoped
{
    private readonly List<OrderItem> _items = [];
    private readonly List<OrderStatusHistory> _history = [];
    private readonly List<OrderNote> _notes = [];
    private readonly List<OrderRiskFlag> _flags = [];

    private Order()
    {
    }

    public string Number { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public OrderSource Source { get; private set; }

    public OrderStatus Status { get; private set; } = OrderStatus.Placed;

    public PaymentStatus PaymentStatus { get; private set; } = PaymentStatus.Pending;

    public DeliveryStatus DeliveryStatus { get; private set; } = DeliveryStatus.NotShipped;

    /// <summary><c>COD</c> or <c>SANDBOX</c> (the sandbox online gateway); Step 9 adds the real methods.</summary>
    public string PaymentMethod { get; private set; } = "COD";

    public Guid CustomerId { get; private set; }

    [AuditIgnore]
    public string CustomerMobile { get; private set; } = string.Empty;

    [AuditIgnore]
    public string? CustomerName { get; private set; }

    [AuditIgnore]
    public string? CustomerEmail { get; private set; }

    // ---- Address snapshot ----
    [AuditIgnore]
    public string ShipName { get; private set; } = string.Empty;

    [AuditIgnore]
    public string ShipPhone { get; private set; } = string.Empty;

    public string ShipCountryCode { get; private set; } = "BD";

    public Guid? ShipDivisionId { get; private set; }

    public string? ShipDivision { get; private set; }

    public Guid? ShipDistrictId { get; private set; }

    public string? ShipDistrict { get; private set; }

    public Guid? ShipUpazilaId { get; private set; }

    public string? ShipUpazila { get; private set; }

    [AuditIgnore]
    public string? ShipArea { get; private set; }

    [AuditIgnore]
    public string ShipAddressLine { get; private set; } = string.Empty;

    [AuditIgnore]
    public string? ShipLandmark { get; private set; }

    public string? ShipStateProvince { get; private set; }

    public string? ShipCity { get; private set; }

    public string? ShipPostalCode { get; private set; }

    public double? ShipLatitude { get; private set; }

    public double? ShipLongitude { get; private set; }

    // ---- Money (BDT, tax-inclusive) ----
    public string Currency { get; private set; } = "BDT";

    public decimal Subtotal { get; private set; }

    public decimal CouponDiscount { get; private set; }

    public string? CouponCode { get; private set; }

    public Guid? CouponId { get; private set; }

    /// <summary>A discount a staff member gave on a manual order (needs a permission).</summary>
    public decimal ManualDiscount { get; private set; }

    public decimal DeliveryCharge { get; private set; }

    public decimal GrandTotal { get; private set; }

    /// <summary>The part the customer must pay before the order is processed (BR-D1: COD above the threshold needs the delivery charge). Zero when none.</summary>
    public decimal AdvanceRequired { get; private set; }

    public int WeightGrams { get; private set; }

    public string? ZoneCode { get; private set; }

    // ---- Routing ----
    /// <summary>Which rule chose the fulfilling branch: <c>DefaultBranch</c>, <c>AllStock</c>, <c>Nearest</c>, <c>Manual</c>, or <c>Staff</c> after a reassign.</summary>
    public string RoutingRule { get; private set; } = "DefaultBranch";

    /// <summary>True while routing mode is Manual and nobody has confirmed or changed the branch yet.</summary>
    public bool RoutingPending { get; private set; }

    // ---- Stock and timing ----
    public bool StockDeducted { get; private set; }

    public DateTimeOffset PlacedAt { get; private set; }

    public DateTimeOffset? ConfirmedAt { get; private set; }

    public DateTimeOffset? PackedAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? CancelReason { get; private set; }

    /// <summary>For unpaid online orders: when the hold ends and the order is cancelled automatically (BR-D2).</summary>
    public DateTimeOffset? PaymentHoldExpiresAt { get; private set; }

    // ---- Identity of the request ----
    public string? IdempotencyKey { get; private set; }

    /// <summary>Hash of mobile and items: the duplicate-order guard compares it (FR-D09).</summary>
    public string Fingerprint { get; private set; } = string.Empty;

    /// <summary>SHA-256 of the public tracking token (the token is shown once at checkout).</summary>
    public string TrackingTokenHash { get; private set; } = string.Empty;

    [AuditIgnore]
    public string? DeviceId { get; private set; }

    [AuditIgnore]
    public string? ClientIp { get; private set; }

    public bool OtpVerified { get; private set; }

    public DateTimeOffset? TermsAcceptedAt { get; private set; }

    public Guid? CreatedByStaffId { get; private set; }

    // ---- Fraud (module T) ----
    public FraudState FraudState { get; private set; }

    public int RiskScore { get; private set; }

    public IReadOnlyList<OrderItem> Items => _items;

    public IReadOnlyList<OrderStatusHistory> History => _history;

    public IReadOnlyList<OrderNote> Notes => _notes;

    public IReadOnlyList<OrderRiskFlag> Flags => _flags;

    /// <summary>Held back from confirmation by the fraud review (flagged, in review, or waiting for an advance).</summary>
    public bool IsHeld => FraudState is FraudState.Flagged or FraudState.InReview or FraudState.AdvanceRequested;

    public bool IsOnlinePayment => !string.Equals(PaymentMethod, "COD", StringComparison.Ordinal);

    public static Order Place(NewOrder data, IReadOnlyList<NewOrderItem> items, DateTimeOffset now)
    {
        if (items.Count == 0)
        {
            throw new ArgumentException("An order needs at least one line.", nameof(items));
        }

        var order = new Order
        {
            Number = data.Number,
            BranchId = data.BranchId,
            Source = data.Source,
            CustomerId = data.CustomerId,
            CustomerMobile = data.CustomerMobile,
            CustomerName = data.CustomerName,
            CustomerEmail = data.CustomerEmail,
            PaymentMethod = data.PaymentMethod,
            RoutingRule = data.RoutingRule,
            RoutingPending = string.Equals(data.RoutingRule, "Manual", StringComparison.Ordinal),
            ZoneCode = data.ZoneCode,
            Currency = data.Currency,
            Subtotal = data.Subtotal,
            CouponDiscount = data.CouponDiscount,
            CouponCode = data.CouponCode,
            CouponId = data.CouponId,
            ManualDiscount = data.ManualDiscount,
            DeliveryCharge = data.DeliveryCharge,
            GrandTotal = data.Subtotal - data.CouponDiscount - data.ManualDiscount + data.DeliveryCharge,
            AdvanceRequired = data.AdvanceRequired,
            WeightGrams = data.WeightGrams,
            PaymentHoldExpiresAt = data.PaymentHoldExpiresAt,
            IdempotencyKey = data.IdempotencyKey,
            Fingerprint = data.Fingerprint,
            TrackingTokenHash = data.TrackingTokenHash,
            DeviceId = data.DeviceId,
            ClientIp = data.ClientIp,
            OtpVerified = data.OtpVerified,
            TermsAcceptedAt = data.TermsAcceptedAt,
            CreatedByStaffId = data.CreatedByStaffId,
            PlacedAt = now,
        };

        var s = data.Shipping;
        order.ShipName = s.RecipientName;
        order.ShipPhone = s.Phone;
        order.ShipCountryCode = s.CountryCode;
        order.ShipDivisionId = s.DivisionId;
        order.ShipDivision = s.DivisionName;
        order.ShipDistrictId = s.DistrictId;
        order.ShipDistrict = s.DistrictName;
        order.ShipUpazilaId = s.UpazilaId;
        order.ShipUpazila = s.UpazilaName;
        order.ShipArea = s.Area;
        order.ShipAddressLine = s.AddressLine;
        order.ShipLandmark = s.Landmark;
        order.ShipStateProvince = s.StateProvince;
        order.ShipCity = s.City;
        order.ShipPostalCode = s.PostalCode;
        order.ShipLatitude = s.Latitude;
        order.ShipLongitude = s.Longitude;

        foreach (var item in items)
        {
            order._items.Add(new OrderItem(order.Id, item));
        }

        order._history.Add(new OrderStatusHistory(order.Id, OrderTrack.Order, null, OrderStatus.Placed.ToString(), "Order placed", data.CreatedByStaffId, data.CreatedByName, now));
        order._history.Add(new OrderStatusHistory(order.Id, OrderTrack.Payment, null, PaymentStatus.Pending.ToString(), null, data.CreatedByStaffId, data.CreatedByName, now));
        order._history.Add(new OrderStatusHistory(order.Id, OrderTrack.Delivery, null, DeliveryStatus.NotShipped.ToString(), null, data.CreatedByStaffId, data.CreatedByName, now));
        order.Raise(new OrderPlaced(now, order.Id, order.Number, order.BranchId, order.CustomerId, order.CustomerMobile, order.GrandTotal, order.Source));
        return order;
    }

    /// <summary>
    /// Moves the order track. Throws when the move is not in the state machine. Also moves the delivery track to match, and the payment track
    /// where the order outcome decides it (cancel before payment, cash collected on delivery).
    /// </summary>
    public void TransitionTo(OrderStatus to, DateTimeOffset now, Guid? by, string? byName, string? reason)
    {
        if (!OrderStateMachine.CanMove(Status, to))
        {
            throw new InvalidOperationException($"An order cannot move from {Status} to {to}.");
        }

        var from = Status;
        Status = to;
        _history.Add(new OrderStatusHistory(Id, OrderTrack.Order, from.ToString(), to.ToString(), reason, by, byName, now));
        Raise(new OrderStatusChanged(now, Id, Number, BranchId, CustomerId, CustomerMobile, OrderTrack.Order, from.ToString(), to.ToString(), reason));

        switch (to)
        {
            case OrderStatus.Confirmed:
                ConfirmedAt = now;
                RoutingPending = false;
                break;
            case OrderStatus.Packed:
                PackedAt = now;
                break;
            case OrderStatus.Delivered:
                DeliveredAt = now;
                if (PaymentMethod == "COD" && PaymentStatus == PaymentStatus.Pending)
                {
                    TransitionPaymentTo(PaymentStatus.CollectedByCourier, now, by, byName, "Cash collected on delivery");
                }

                break;
            case OrderStatus.Cancelled:
                CancelledAt = now;
                CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
                if (PaymentStatus is PaymentStatus.Pending or PaymentStatus.Failed)
                {
                    TransitionPaymentTo(PaymentStatus.Cancelled, now, by, byName, "Order cancelled");
                }

                break;
            default:
                break;
        }

        var delivery = OrderStateMachine.DeliveryFor(to);
        if (delivery != DeliveryStatus)
        {
            var before = DeliveryStatus;
            DeliveryStatus = delivery;
            _history.Add(new OrderStatusHistory(Id, OrderTrack.Delivery, before.ToString(), delivery.ToString(), reason, by, byName, now));
            Raise(new OrderStatusChanged(now, Id, Number, BranchId, CustomerId, CustomerMobile, OrderTrack.Delivery, before.ToString(), delivery.ToString(), reason));
        }
    }

    public void TransitionPaymentTo(PaymentStatus to, DateTimeOffset now, Guid? by, string? byName, string? reason)
    {
        if (!OrderStateMachine.CanMove(PaymentStatus, to))
        {
            throw new InvalidOperationException($"A payment cannot move from {PaymentStatus} to {to}.");
        }

        var from = PaymentStatus;
        PaymentStatus = to;
        if (to is PaymentStatus.Paid or PaymentStatus.CollectedByCourier or PaymentStatus.Cancelled or PaymentStatus.UnderReview or PaymentStatus.Rejected)
        {
            PaymentHoldExpiresAt = null; // the hold only applies while payment is pending or failed (a failed payment can be retried)
        }

        _history.Add(new OrderStatusHistory(Id, OrderTrack.Payment, from.ToString(), to.ToString(), reason, by, byName, now));
        Raise(new OrderStatusChanged(now, Id, Number, BranchId, CustomerId, CustomerMobile, OrderTrack.Payment, from.ToString(), to.ToString(), reason));
    }

    public void MarkStockDeducted() => StockDeducted = true;

    public void MarkStockRestored() => StockDeducted = false;

    /// <summary>Moves the order to another branch (before packing). The caller moves the reservation.</summary>
    public void ReassignBranch(Guid branchId, DateTimeOffset now, Guid? by, string? byName, string? reason)
    {
        if (Status is not (OrderStatus.Placed or OrderStatus.Confirmed))
        {
            throw new InvalidOperationException("The branch can only change before the order is packed.");
        }

        var from = BranchId;
        BranchId = branchId;
        RoutingRule = "Staff";
        RoutingPending = false;
        _notes.Add(new OrderNote(Id, NoteKind.Internal, $"Branch changed. {reason}".Trim(), by, byName, now));
        Raise(new OrderBranchReassigned(now, Id, Number, from, branchId, reason));
    }

    public OrderNote AddNote(NoteKind kind, string text, Guid? by, string? byName, DateTimeOffset now)
    {
        var note = new OrderNote(Id, kind, text.Trim(), by, byName, now);
        _notes.Add(note);
        return note;
    }

    /// <summary>Records what the fraud rules found. A hold puts the order in review state Flagged; flags alone do not.</summary>
    public void RecordRisk(IReadOnlyList<(string Code, string Message, int Score, RuleAction Action)> findings, DateTimeOffset now, decimal? advance)
    {
        foreach (var f in findings)
        {
            _flags.Add(new OrderRiskFlag(Id, f.Code, f.Message, f.Score, f.Action, now));
        }

        RiskScore = findings.Sum(f => f.Score);
        if (findings.Any(f => f.Action is RuleAction.Hold or RuleAction.RequireAdvance))
        {
            FraudState = FraudState.Flagged;
            Raise(new OrderFlagged(now, Id, Number, RiskScore, FraudState.ToString()));
        }

        if (findings.Any(f => f.Action == RuleAction.RequireAdvance) && advance is { } amount && amount > AdvanceRequired)
        {
            AdvanceRequired = amount;
        }
    }

    /// <summary>Applies a review decision. Reject is completed by the caller cancelling the order.</summary>
    public FraudReviewLog Decide(FraudAction action, string? note, DateTimeOffset now, Guid? by, string? byName)
    {
        if (FraudState is FraudState.None or FraudState.Approved or FraudState.Rejected)
        {
            throw new InvalidOperationException("This order is not waiting for a fraud decision.");
        }

        var from = FraudState;
        FraudState = action switch
        {
            FraudAction.Approve => FraudState.Approved,
            FraudAction.RequestAdvance => FraudState.AdvanceRequested,
            FraudAction.Call => FraudState.InReview,
            _ => FraudState.Rejected,
        };

        return new FraudReviewLog(Id, action, from, FraudState, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), by, byName, now);
    }

    /// <summary>Asks for an advance equal to the delivery charge when none is set yet.</summary>
    public void RequireAdvance(decimal amount)
    {
        if (amount > AdvanceRequired)
        {
            AdvanceRequired = amount;
        }
    }
}

public sealed class OrderItem : Entity
{
    private OrderItem()
    {
    }

    internal OrderItem(Guid orderId, NewOrderItem item)
    {
        OrderId = orderId;
        VariantId = item.VariantId;
        ProductId = item.ProductId;
        Name = item.Name;
        NameBn = item.NameBn;
        Sku = item.Sku;
        ColorName = item.ColorName;
        Size = item.Size;
        ImagePath = item.ImagePath;
        UnitPrice = item.UnitPrice;
        Quantity = item.Quantity;
        LineTotal = item.UnitPrice * item.Quantity;
        WeightGrams = item.WeightGrams;
        TracksStock = item.TracksStock;
    }

    public Guid OrderId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string? ColorName { get; private set; }

    public string? Size { get; private set; }

    /// <summary>Stored path of the card picture at order time (not a URL).</summary>
    public string? ImagePath { get; private set; }

    public decimal UnitPrice { get; private set; }

    public int Quantity { get; private set; }

    public decimal LineTotal { get; private set; }

    public int WeightGrams { get; private set; }

    /// <summary>False for a variant that does not track stock: nothing is reserved for it.</summary>
    public bool TracksStock { get; private set; }
}

/// <summary>One status change on one of the three tracks: who, when, from, to, why. Append-only (BR-003).</summary>
public sealed class OrderStatusHistory : Entity, IAppendOnly, INotAudited
{
    private OrderStatusHistory()
    {
    }

    public OrderStatusHistory(Guid orderId, OrderTrack track, string? from, string to, string? reason, Guid? actorId, string? actorName, DateTimeOffset at)
    {
        OrderId = orderId;
        Track = track;
        FromStatus = from;
        ToStatus = to;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        ActorId = actorId;
        ActorName = actorName;
        At = at;
    }

    public Guid OrderId { get; private set; }

    public OrderTrack Track { get; private set; }

    public string? FromStatus { get; private set; }

    public string ToStatus { get; private set; } = string.Empty;

    public string? Reason { get; private set; }

    /// <summary>Null when the system acted (customer, timeout job).</summary>
    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }

    public DateTimeOffset At { get; private set; }
}

public sealed class OrderNote : Entity, INotAudited
{
    private OrderNote()
    {
    }

    internal OrderNote(Guid orderId, NoteKind kind, string text, Guid? authorId, string? authorName, DateTimeOffset at)
    {
        OrderId = orderId;
        Kind = kind;
        Text = text;
        AuthorId = authorId;
        AuthorName = authorName;
        At = at;
    }

    public Guid OrderId { get; private set; }

    public NoteKind Kind { get; private set; }

    public string Text { get; private set; } = string.Empty;

    public Guid? AuthorId { get; private set; }

    public string? AuthorName { get; private set; }

    public DateTimeOffset At { get; private set; }
}

/// <summary>A fraud rule that matched an order (module T).</summary>
public sealed class OrderRiskFlag : Entity, INotAudited
{
    private OrderRiskFlag()
    {
    }

    internal OrderRiskFlag(Guid orderId, string ruleCode, string message, int score, RuleAction action, DateTimeOffset at)
    {
        OrderId = orderId;
        RuleCode = ruleCode;
        Message = message;
        Score = score;
        Action = action;
        At = at;
    }

    public Guid OrderId { get; private set; }

    public string RuleCode { get; private set; } = string.Empty;

    public string Message { get; private set; } = string.Empty;

    public int Score { get; private set; }

    public RuleAction Action { get; private set; }

    public DateTimeOffset At { get; private set; }
}

/// <summary>A fraud review decision with the staff name and note (FR-T06). Append-only.</summary>
public sealed class FraudReviewLog : Entity, IAppendOnly, INotAudited
{
    private FraudReviewLog()
    {
    }

    internal FraudReviewLog(Guid orderId, FraudAction action, FraudState from, FraudState to, string? note, Guid? actorId, string? actorName, DateTimeOffset at)
    {
        OrderId = orderId;
        Action = action;
        FromState = from;
        ToState = to;
        Note = note;
        ActorId = actorId;
        ActorName = actorName;
        At = at;
    }

    public Guid OrderId { get; private set; }

    public FraudAction Action { get; private set; }

    public FraudState FromState { get; private set; }

    public FraudState ToState { get; private set; }

    public string? Note { get; private set; }

    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }

    public DateTimeOffset At { get; private set; }
}

/// <summary>Per branch and month counter behind the gap-free order numbers (<c>JV-DHK01-2611-000123</c>).</summary>
public sealed class OrderSequence : Entity, INotAudited
{
    private OrderSequence()
    {
    }

    public OrderSequence(Guid branchId, string period, int lastNumber)
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

/// <summary>A mobile number, e-mail, address text or device that may not check out online (FR-T03). Removing an entry keeps who and when.</summary>
public sealed class BlacklistEntry : SoftDeletableEntity
{
    private BlacklistEntry()
    {
    }

    public BlacklistEntry(BlacklistKind kind, string value, string? note)
    {
        Kind = kind;
        Value = value;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public BlacklistKind Kind { get; private set; }

    /// <summary>Normalised: mobile as +880..., e-mail and address text lower case.</summary>
    public string Value { get; private set; } = string.Empty;

    public string? Note { get; private set; }
}

/// <summary>A runtime setting (key and value), edited by staff, read with safe defaults when missing.</summary>
public sealed class PlatformSetting : Entity
{
    private PlatformSetting()
    {
    }

    public PlatformSetting(string key, string value)
    {
        Key = key;
        Value = value;
    }

    public string Key { get; private set; } = string.Empty;

    public string Value { get; private set; } = string.Empty;

    public void Set(string value) => Value = value;
}
