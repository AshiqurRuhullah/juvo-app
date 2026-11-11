namespace Juvo.Domain.Orders;

/// <summary>The order track (CLAUDE.md section 7). Stored as an integer; append new values at the end.</summary>
public enum OrderStatus
{
    Placed = 1,
    Confirmed = 2,
    Packed = 3,
    HandedToCourier = 4,
    InTransit = 5,
    OutForDelivery = 6,
    Delivered = 7,
    Cancelled = 8,
    DeliveryFailed = 9,
    ReturnInProgress = 10,
    Returned = 11,
}

/// <summary>The payment track.</summary>
public enum PaymentStatus
{
    Pending = 1,
    Paid = 2,
    Failed = 3,
    Cancelled = 4,
    UnderReview = 5,
    Rejected = 6,
    PartiallyRefunded = 7,
    Refunded = 8,
    CollectedByCourier = 9,
    Settled = 10,
}

/// <summary>The delivery track. It follows the order track (see <see cref="OrderStateMachine.DeliveryFor"/>) until couriers drive it in Step 10.</summary>
public enum DeliveryStatus
{
    NotShipped = 1,
    ReadyToShip = 2,
    HandedToCourier = 3,
    InTransit = 4,
    OutForDelivery = 5,
    Delivered = 6,
    Failed = 7,
    Returned = 8,
    Cancelled = 9,
}

/// <summary>Which of the three parallel tracks a history row belongs to.</summary>
public enum OrderTrack
{
    Order = 1,
    Payment = 2,
    Delivery = 3,
}

/// <summary>Where the order came from (FR-D10, FR-S02).</summary>
public enum OrderSource
{
    Web = 1,
    Facebook = 2,
    WhatsApp = 3,
    Phone = 4,
    WalkIn = 5,
    Other = 6,
}

public enum NoteKind
{
    /// <summary>Staff only.</summary>
    Internal = 1,

    /// <summary>Written by the customer at checkout (gift wrap, delivery instruction) or by staff for the customer.</summary>
    Customer = 2,
}

/// <summary>Module T review state of an order. Flagged, InReview and AdvanceRequested hold the order back from confirmation.</summary>
public enum FraudState
{
    None = 0,
    Flagged = 1,
    InReview = 2,
    AdvanceRequested = 3,
    Approved = 4,
    Rejected = 5,
}

public enum FraudAction
{
    Approve = 1,
    RequestAdvance = 2,
    Call = 3,
    Reject = 4,
}

/// <summary>What happens when a fraud rule matches.</summary>
public enum RuleAction
{
    /// <summary>Record the flag and show it; the order is not held.</summary>
    Flag = 1,

    /// <summary>Hold the order for manual review.</summary>
    Hold = 2,

    /// <summary>Hold the order and ask for the delivery charge in advance.</summary>
    RequireAdvance = 3,

    /// <summary>Refuse the checkout with a neutral message.</summary>
    Reject = 4,
}

public enum BlacklistKind
{
    Mobile = 1,
    Email = 2,
    Address = 3,
    Device = 4,
}
