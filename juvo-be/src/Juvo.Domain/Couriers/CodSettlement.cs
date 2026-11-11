using Juvo.Domain.Common;

namespace Juvo.Domain.Couriers;

/// <summary>Open: just created, waiting for the amounts the courier really paid. Reconciled: amounts entered, differences noted. Settled: the cash is accounted for. Cancelled: dropped, the parcels can go into another batch.</summary>
public enum SettlementStatus
{
    Open = 1,
    Reconciled = 2,
    Settled = 3,
    Cancelled = 4,
}

/// <summary>What staff typed for one parcel when reconciling a batch.</summary>
public sealed record ReceivedLine(Guid ShipmentId, decimal Received, string? Note);

/// <summary>
/// A batch of cash on delivery collected by one courier in one branch, to be paid over to the shop (module H, FR-H cash settlement). Expected is what the courier reported
/// collecting; received is what arrived; the difference per parcel and for the batch (short is negative, over is positive) stays on the record. Gap-free numbers per branch.
/// </summary>
public sealed class CodSettlement : Entity, IBranchScoped
{
    private readonly List<CodSettlementItem> _items = [];

    private CodSettlement()
    {
    }

    public CodSettlement(string number, Guid branchId, Guid courierId, string courierName, IEnumerable<CodSettlementItem> items, string? note, string? byName)
    {
        Number = number;
        BranchId = branchId;
        CourierId = courierId;
        CourierName = courierName;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        CreatedByName = byName;
        _items.AddRange(items);
        ExpectedTotal = _items.Sum(i => i.Expected);
        FeesTotal = _items.Sum(i => i.CourierFee);
    }

    public string Number { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public Guid CourierId { get; private set; }

    public string CourierName { get; private set; } = string.Empty;

    public SettlementStatus Status { get; private set; } = SettlementStatus.Open;

    public decimal ExpectedTotal { get; private set; }

    public decimal? ReceivedTotal { get; private set; }

    /// <summary>Received minus expected for the whole batch: negative is short, positive is over.</summary>
    public decimal? Difference => ReceivedTotal is { } r ? r - ExpectedTotal : null;

    /// <summary>What the courier charges for these parcels. Recorded for the books; the courier's invoice is settled separately (decision S in docs/assumptions.md).</summary>
    public decimal FeesTotal { get; private set; }

    public string? Reference { get; private set; }

    public string? Note { get; private set; }

    public string? CreatedByName { get; private set; }

    public DateTimeOffset? ReconciledAt { get; private set; }

    public string? ReconciledByName { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    public string? SettledByName { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public IReadOnlyList<CodSettlementItem> Items => _items;

    /// <summary>Enters what the courier paid for every parcel. Any parcel that is short or over needs a note.</summary>
    public void Reconcile(IReadOnlyList<ReceivedLine> lines, DateTimeOffset now, string? byName)
    {
        if (Status is not (SettlementStatus.Open or SettlementStatus.Reconciled))
        {
            throw new InvalidOperationException($"A settlement that is {Status} cannot be reconciled.");
        }

        foreach (var item in _items)
        {
            var line = lines.FirstOrDefault(l => l.ShipmentId == item.ShipmentId) ?? throw new ArgumentException($"Parcel {item.ShipmentId} has no received amount.", nameof(lines));
            item.SetReceived(line.Received, line.Note);
        }

        ReceivedTotal = _items.Sum(i => i.Received ?? 0m);
        Status = SettlementStatus.Reconciled;
        ReconciledAt = now;
        ReconciledByName = byName;
    }

    public void Settle(string reference, DateTimeOffset now, string? byName)
    {
        if (Status != SettlementStatus.Reconciled)
        {
            throw new InvalidOperationException($"A settlement that is {Status} cannot be settled; reconcile it first.");
        }

        Reference = reference.Trim();
        Status = SettlementStatus.Settled;
        SettledAt = now;
        SettledByName = byName;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is SettlementStatus.Settled or SettlementStatus.Cancelled)
        {
            throw new InvalidOperationException($"A settlement that is {Status} cannot be cancelled.");
        }

        Status = SettlementStatus.Cancelled;
        CancelledAt = now;
    }
}

/// <summary>One parcel in a settlement batch with what was expected and what arrived.</summary>
public sealed class CodSettlementItem : Entity
{
    private CodSettlementItem()
    {
    }

    public CodSettlementItem(Guid shipmentId, Guid orderId, string orderNumber, string consignmentId, decimal expected, decimal courierFee)
    {
        ShipmentId = shipmentId;
        OrderId = orderId;
        OrderNumber = orderNumber;
        ConsignmentId = consignmentId;
        Expected = expected;
        CourierFee = courierFee;
    }

    public Guid SettlementId { get; private set; }

    public Guid ShipmentId { get; private set; }

    public Guid OrderId { get; private set; }

    public string OrderNumber { get; private set; } = string.Empty;

    public string ConsignmentId { get; private set; } = string.Empty;

    public decimal Expected { get; private set; }

    public decimal CourierFee { get; private set; }

    public decimal? Received { get; private set; }

    public decimal? Difference => Received is { } r ? r - Expected : null;

    public string? Note { get; private set; }

    internal void SetReceived(decimal received, string? note)
    {
        Received = received;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }
}
