using Juvo.Domain.Common;

namespace Juvo.Domain.Couriers;

/// <summary>One event for every shipment status change or courier scan. Staff screens and the customer's tracking page read it; the real-time hub pushes it.</summary>
public sealed record ShipmentEventAdded(
    DateTimeOffset At, Guid ShipmentId, Guid OrderId, string OrderNumber, Guid BranchId, ShipmentKind Kind, Guid? ReturnRequestId, string ConsignmentId, string CourierName, ShipmentStatus Status,
    bool IsStatusChange, string RawStatus, string? Description, string? Location, string? TrackingUrl, DateTimeOffset EventAt) : DomainEvent(At);

/// <summary>Everything the aggregate needs to be created; the service validates it first.</summary>
public sealed record NewShipment(
    Guid OrderId, string OrderNumber, Guid BranchId, Guid CourierId, string CourierCode, string CourierName, ShipmentKind Kind, Guid? ReturnRequestId, string ConsignmentId, string? ProviderReference,
    string? TrackingUrl, int WeightGrams, int? LengthCm, int? WidthCm, int? HeightCm, decimal CodAmount, decimal Cost, string PickupName, string? PickupPhone, string PickupAddress,
    string DeliveryName, string DeliveryPhone, string DeliveryAddress, string? DeliveryDistrict, string? Note);

/// <summary>
/// A parcel booked with a courier for an order (module H), or the reverse parcel of a return. Its status follows <see cref="ShipmentStateMachine"/>; every change and every
/// courier scan appends an immutable <see cref="ShipmentEvent"/> and raises a domain event. One order has at most one open forward shipment (a filtered unique index).
/// </summary>
public sealed class Shipment : AggregateRoot, IBranchScoped
{
    private readonly List<ShipmentEvent> _events = [];

    private Shipment()
    {
    }

    public Guid OrderId { get; private set; }

    public string OrderNumber { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public Guid CourierId { get; private set; }

    public string CourierCode { get; private set; } = string.Empty;

    public string CourierName { get; private set; } = string.Empty;

    public ShipmentKind Kind { get; private set; }

    public Guid? ReturnRequestId { get; private set; }

    public ShipmentStatus Status { get; private set; } = ShipmentStatus.Booked;

    public string ConsignmentId { get; private set; } = string.Empty;

    public string? ProviderReference { get; private set; }

    public string? TrackingUrl { get; private set; }

    public int WeightGrams { get; private set; }

    public int? LengthCm { get; private set; }

    public int? WidthCm { get; private set; }

    public int? HeightCm { get; private set; }

    /// <summary>Cash the courier must collect from the customer (zero for prepaid orders and for reverse parcels).</summary>
    public decimal CodAmount { get; private set; }

    /// <summary>What the courier reported collecting (set when delivered).</summary>
    public decimal? CodCollectedAmount { get; private set; }

    public DateTimeOffset? CodCollectedAt { get; private set; }

    /// <summary>The settlement batch that includes this parcel's cash, once one was made.</summary>
    public Guid? CodSettlementId { get; private set; }

    /// <summary>What the courier charges the shop for this parcel.</summary>
    public decimal Cost { get; private set; }

    // ---- Addresses (snapshots) ----
    [AuditIgnore]
    public string PickupName { get; private set; } = string.Empty;

    [AuditIgnore]
    public string? PickupPhone { get; private set; }

    [AuditIgnore]
    public string PickupAddress { get; private set; } = string.Empty;

    [AuditIgnore]
    public string DeliveryName { get; private set; } = string.Empty;

    [AuditIgnore]
    public string DeliveryPhone { get; private set; } = string.Empty;

    [AuditIgnore]
    public string DeliveryAddress { get; private set; } = string.Empty;

    public string? DeliveryDistrict { get; private set; }

    public string? Note { get; private set; }

    // ---- Progress ----
    public int Attempts { get; private set; }

    public string? FailureReason { get; private set; }

    public string? LastRawStatus { get; private set; }

    public DateTimeOffset BookedAt { get; private set; }

    public DateTimeOffset? PickedUpAt { get; private set; }

    public DateTimeOffset? DeliveredAt { get; private set; }

    public DateTimeOffset? ReturnedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public DateTimeOffset? LabelPrintedAt { get; private set; }

    /// <summary>When the status poll last asked the courier about this parcel (so every open parcel gets its turn).</summary>
    public DateTimeOffset? LastPolledAt { get; private set; }

    public string? CancelReason { get; private set; }

    public IReadOnlyList<ShipmentEvent> Events => _events;

    public bool IsOpen => ShipmentStateMachine.IsOpen(Status);

    public static Shipment Book(NewShipment data, DateTimeOffset now, Guid? by, string? byName)
    {
        var shipment = new Shipment
        {
            OrderId = data.OrderId,
            OrderNumber = data.OrderNumber,
            BranchId = data.BranchId,
            CourierId = data.CourierId,
            CourierCode = data.CourierCode,
            CourierName = data.CourierName,
            Kind = data.Kind,
            ReturnRequestId = data.ReturnRequestId,
            ConsignmentId = data.ConsignmentId,
            ProviderReference = data.ProviderReference,
            TrackingUrl = data.TrackingUrl,
            WeightGrams = data.WeightGrams,
            LengthCm = data.LengthCm,
            WidthCm = data.WidthCm,
            HeightCm = data.HeightCm,
            CodAmount = data.CodAmount,
            Cost = data.Cost,
            PickupName = data.PickupName,
            PickupPhone = data.PickupPhone,
            PickupAddress = data.PickupAddress,
            DeliveryName = data.DeliveryName,
            DeliveryPhone = data.DeliveryPhone,
            DeliveryAddress = data.DeliveryAddress,
            DeliveryDistrict = data.DeliveryDistrict,
            Note = string.IsNullOrWhiteSpace(data.Note) ? null : data.Note.Trim(),
            BookedAt = now,
        };
        shipment.AddEvent(null, "booked", ShipmentStatus.Booked, true, $"Booked with {data.CourierName}. Consignment {data.ConsignmentId}.", null, now, ShipmentEventSource.Staff, byName);
        return shipment;
    }

    /// <summary>Moves the parcel one step on the state machine and records the event. Throws when the move is not allowed.</summary>
    public ShipmentEvent MoveTo(ShipmentStatus to, string rawStatus, string? providerEventId, string? description, string? location, DateTimeOffset occurredAt, ShipmentEventSource source, string? actorName)
    {
        if (!ShipmentStateMachine.CanMove(Status, to))
        {
            throw new InvalidOperationException($"A shipment that is {Status} cannot become {to}.");
        }

        Status = to;
        switch (to)
        {
            case ShipmentStatus.PickedUp:
                PickedUpAt = occurredAt;
                break;
            case ShipmentStatus.OutForDelivery:
                Attempts++;
                FailureReason = null;
                break;
            case ShipmentStatus.Delivered:
                DeliveredAt = occurredAt;
                break;
            case ShipmentStatus.DeliveryFailed:
                FailureReason = string.IsNullOrWhiteSpace(description) ? "Delivery failed" : description.Trim();
                break;
            case ShipmentStatus.Returned:
                ReturnedAt = occurredAt;
                break;
            case ShipmentStatus.Cancelled:
                CancelledAt = occurredAt;
                CancelReason = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
                break;
            default:
                break;
        }

        return AddEvent(providerEventId, rawStatus, to, true, description, location, occurredAt, source, actorName);
    }

    /// <summary>A courier scan that changes no status (for example "arrived at the hub"). It still shows on the timeline.</summary>
    public ShipmentEvent RecordScan(string rawStatus, string? providerEventId, string? description, string? location, DateTimeOffset occurredAt, ShipmentEventSource source, string? actorName) =>
        AddEvent(providerEventId, rawStatus, Status, false, description, location, occurredAt, source, actorName);

    /// <summary>The courier collected the cash. Allowed once.</summary>
    public void RecordCodCollected(decimal amount, DateTimeOffset at)
    {
        CodCollectedAmount ??= amount;
        CodCollectedAt ??= at;
    }

    public void AttachToSettlement(Guid settlementId) => CodSettlementId = settlementId;

    public void DetachFromSettlement() => CodSettlementId = null;

    public void MarkLabelPrinted(DateTimeOffset now) => LabelPrintedAt = now;

    private ShipmentEvent AddEvent(string? providerEventId, string rawStatus, ShipmentStatus status, bool isStatusChange, string? description, string? location, DateTimeOffset occurredAt, ShipmentEventSource source, string? actorName)
    {
        var e = new ShipmentEvent(Id, providerEventId, rawStatus, status, isStatusChange, description, location, occurredAt, source, actorName);
        _events.Add(e);
        LastRawStatus = rawStatus;
        Raise(new ShipmentEventAdded(occurredAt, Id, OrderId, OrderNumber, BranchId, Kind, ReturnRequestId, ConsignmentId, CourierName, status, isStatusChange, rawStatus, e.Description, e.Location, TrackingUrl, occurredAt));
        return e;
    }
}

/// <summary>One thing the courier or staff recorded about a parcel. Append-only. <see cref="ProviderEventId"/> is unique per shipment, so a repeated webhook or poll never adds it twice.</summary>
public sealed class ShipmentEvent : Entity, IAppendOnly, INotAudited
{
    private ShipmentEvent()
    {
    }

    internal ShipmentEvent(Guid shipmentId, string? providerEventId, string rawStatus, ShipmentStatus status, bool isStatusChange, string? description, string? location, DateTimeOffset occurredAt, ShipmentEventSource source, string? actorName)
    {
        ShipmentId = shipmentId;
        ProviderEventId = providerEventId;
        RawStatus = rawStatus;
        Status = status;
        IsStatusChange = isStatusChange;
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Location = string.IsNullOrWhiteSpace(location) ? null : location.Trim();
        OccurredAt = occurredAt;
        Source = source;
        ActorName = actorName;
    }

    public Guid ShipmentId { get; private set; }

    /// <summary>The courier's id of the event (null for events staff or the system wrote).</summary>
    public string? ProviderEventId { get; private set; }

    /// <summary>The courier's own status word, kept exactly as sent.</summary>
    public string RawStatus { get; private set; } = string.Empty;

    /// <summary>Our status after this event.</summary>
    public ShipmentStatus Status { get; private set; }

    public bool IsStatusChange { get; private set; }

    public string? Description { get; private set; }

    public string? Location { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public ShipmentEventSource Source { get; private set; }

    public string? ActorName { get; private set; }
}

/// <summary>The sandbox courier's own record of a parcel: its current raw status and a version that counts each change, so polling and webhooks can be told apart and replayed safely.</summary>
public sealed class SandboxConsignment : Entity, INotAudited
{
    private SandboxConsignment()
    {
    }

    public SandboxConsignment(string consignmentId, string reference, decimal codAmount, bool isReturn, DateTimeOffset now)
    {
        ConsignmentId = consignmentId;
        Reference = reference;
        CodAmount = codAmount;
        IsReturn = isReturn;
        RawStatus = "booked";
        Version = 1;
        UpdatedAtUtc = now;
    }

    public string ConsignmentId { get; private set; } = string.Empty;

    public string Reference { get; private set; } = string.Empty;

    public decimal CodAmount { get; private set; }

    public bool IsReturn { get; private set; }

    public string RawStatus { get; private set; } = "booked";

    public string? Note { get; private set; }

    /// <summary>Counts every change of <see cref="RawStatus"/>; the event id of a sandbox report is <c>consignment:version</c>.</summary>
    public int Version { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public void Set(string rawStatus, string? note, DateTimeOffset now)
    {
        RawStatus = rawStatus;
        Note = note;
        Version++;
        UpdatedAtUtc = now;
    }
}
