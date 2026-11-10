using Juvo.Domain.Common;

namespace Juvo.Domain.Inventory;

/// <summary>Why the on-hand quantity of a variant changed. Stored as an integer; append new values at the end.</summary>
public enum StockReason
{
    OpeningStock = 1,
    Correction = 2,
    Damage = 3,
    Loss = 4,
    Found = 5,
    CustomerReturn = 6,
    TransferOut = 7,
    TransferIn = 8,
    TransferCancelled = 9,
    StockCount = 10,
}

/// <summary>
/// Stock of one variant in one branch (one stock location per branch). <c>Available = OnHand - Reserved</c>. Reserved units are
/// promised to open orders (reserve at placement, deduct at packing: see docs/assumptions.md). The database refuses negative
/// stock and <c>Reserved &gt; OnHand</c>, and the row version stops two writers from overwriting each other (NFR-019).
/// </summary>
public sealed class StockLevel : Entity, IBranchScoped, INotAudited
{
    private StockLevel()
    {
    }

    public StockLevel(Guid branchId, Guid variantId)
    {
        BranchId = branchId;
        VariantId = variantId;
    }

    public Guid BranchId { get; private set; }

    public Guid VariantId { get; private set; }

    public int OnHand { get; private set; }

    public int Reserved { get; private set; }

    /// <summary>Low-stock threshold: the variant is low when available stock is at or below it. Zero means no alert.</summary>
    public int ReorderLevel { get; private set; }

    public int Available => OnHand - Reserved;

    public bool IsLow => ReorderLevel > 0 && Available <= ReorderLevel;

    /// <summary>Changes on-hand by <paramref name="delta"/>. Refused when it would go below what is already reserved.</summary>
    public bool TryChangeOnHand(int delta)
    {
        var next = (long)OnHand + delta;
        if (next < Reserved || next > int.MaxValue)
        {
            return false;
        }

        OnHand = (int)next;
        return true;
    }

    /// <summary>Promises units to an order. Refused when fewer than <paramref name="quantity"/> are available.</summary>
    public bool TryReserve(int quantity)
    {
        if (quantity <= 0 || Available < quantity)
        {
            return false;
        }

        Reserved += quantity;
        return true;
    }

    /// <summary>Gives reserved units back (order cancelled, failed or timed out).</summary>
    public bool TryRelease(int quantity)
    {
        if (quantity <= 0 || Reserved < quantity)
        {
            return false;
        }

        Reserved -= quantity;
        return true;
    }

    /// <summary>Packing: reserved units leave the shelf (on-hand and reserved both drop).</summary>
    public bool TryDeduct(int quantity)
    {
        if (quantity <= 0 || Reserved < quantity)
        {
            return false;
        }

        Reserved -= quantity;
        OnHand -= quantity;
        return true;
    }

    public void SetReorderLevel(int level) => ReorderLevel = Math.Max(0, level);
}

/// <summary>
/// One line of the stock ledger: append-only, never edited or deleted (SRS 12.2). The on-hand quantity after the change is kept so the
/// history can be read without replaying it.
/// </summary>
public sealed class StockMovement : Entity, IBranchScoped, IAppendOnly, INotAudited
{
    private StockMovement()
    {
    }

    public StockMovement(Guid branchId, Guid variantId, int quantityChange, int onHandAfter, StockReason reason, string? note,
        string? referenceType, Guid? referenceId)
    {
        BranchId = branchId;
        VariantId = variantId;
        QuantityChange = quantityChange;
        OnHandAfter = onHandAfter;
        Reason = reason;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ReferenceType = referenceType;
        ReferenceId = referenceId;
    }

    public Guid BranchId { get; private set; }

    public Guid VariantId { get; private set; }

    /// <summary>Change of on-hand: positive adds stock, negative removes it.</summary>
    public int QuantityChange { get; private set; }

    public int OnHandAfter { get; private set; }

    public StockReason Reason { get; private set; }

    public string? Note { get; private set; }

    /// <summary>What caused it: <c>Transfer</c>, <c>Count</c> (later <c>Order</c>, <c>PurchaseReceipt</c>).</summary>
    public string? ReferenceType { get; private set; }

    public Guid? ReferenceId { get; private set; }
}

public enum TransferStatus
{
    Requested = 1,
    Approved = 2,
    InTransit = 3,
    Received = 4,
    Cancelled = 5,
    Rejected = 6,
}

/// <summary>
/// Moves stock between two branches (SRS 4A.3): Requested, Approved, In transit, Received. Stock leaves the source when the goods
/// ship and enters the destination when they are received, each with a movement on its own side. The record belongs to two
/// branches, so it is not branch-filtered: the service lets a user see it when they hold the permission in either branch.
/// </summary>
public sealed class StockTransfer : Entity
{
    private readonly List<StockTransferLine> _lines = [];

    private StockTransfer()
    {
    }

    public StockTransfer(Guid fromBranchId, Guid toBranchId, string? note, Func<Guid, string> numberFor)
    {
        if (fromBranchId == toBranchId)
        {
            throw new ArgumentException("A transfer needs two different branches.", nameof(toBranchId));
        }

        Number = numberFor(Id);
        FromBranchId = fromBranchId;
        ToBranchId = toBranchId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public string Number { get; private set; } = string.Empty;

    public Guid FromBranchId { get; private set; }

    public Guid ToBranchId { get; private set; }

    public TransferStatus Status { get; private set; } = TransferStatus.Requested;

    public string? Note { get; private set; }

    public string? CancelReason { get; private set; }

    public DateTimeOffset? ApprovedAt { get; private set; }

    public Guid? ApprovedBy { get; private set; }

    public DateTimeOffset? ShippedAt { get; private set; }

    public Guid? ShippedBy { get; private set; }

    public DateTimeOffset? ReceivedAt { get; private set; }

    public Guid? ReceivedBy { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public Guid? ClosedBy { get; private set; }

    public IReadOnlyList<StockTransferLine> Lines => _lines;

    public void AddLine(Guid variantId, int quantity)
    {
        if (quantity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(quantity), "Quantity must be positive.");
        }

        var existing = _lines.FirstOrDefault(l => l.VariantId == variantId);
        if (existing is null)
        {
            _lines.Add(new StockTransferLine(Id, variantId, quantity));
        }
        else
        {
            existing.AddQuantity(quantity);
        }
    }

    public void Approve(DateTimeOffset now, Guid? by)
    {
        Require(TransferStatus.Requested);
        Status = TransferStatus.Approved;
        ApprovedAt = now;
        ApprovedBy = by;
    }

    public void Reject(DateTimeOffset now, Guid? by, string? reason)
    {
        Require(TransferStatus.Requested);
        Close(TransferStatus.Rejected, now, by, reason);
    }

    public void Ship(DateTimeOffset now, Guid? by)
    {
        Require(TransferStatus.Approved);
        Status = TransferStatus.InTransit;
        ShippedAt = now;
        ShippedBy = by;
    }

    public void Receive(DateTimeOffset now, Guid? by)
    {
        Require(TransferStatus.InTransit);
        Status = TransferStatus.Received;
        ReceivedAt = now;
        ReceivedBy = by;
        ClosedAt = now;
        ClosedBy = by;
    }

    /// <summary>Cancels a transfer that has not been received. The caller returns the stock when it was already in transit.</summary>
    public void Cancel(DateTimeOffset now, Guid? by, string? reason)
    {
        if (Status is not (TransferStatus.Requested or TransferStatus.Approved or TransferStatus.InTransit))
        {
            throw new InvalidOperationException($"A {Status} transfer cannot be cancelled.");
        }

        Close(TransferStatus.Cancelled, now, by, reason);
    }

    private void Close(TransferStatus status, DateTimeOffset now, Guid? by, string? reason)
    {
        Status = status;
        CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        ClosedAt = now;
        ClosedBy = by;
    }

    private void Require(TransferStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"The transfer is {Status}, expected {expected}.");
        }
    }
}

public sealed class StockTransferLine : Entity
{
    private StockTransferLine()
    {
    }

    internal StockTransferLine(Guid transferId, Guid variantId, int quantity)
    {
        TransferId = transferId;
        VariantId = variantId;
        Quantity = quantity;
    }

    public Guid TransferId { get; private set; }

    public Guid VariantId { get; private set; }

    public int Quantity { get; private set; }

    internal void AddQuantity(int quantity) => Quantity += quantity;
}

public enum CountStatus
{
    InProgress = 1,
    Submitted = 2,
    Approved = 3,
    Cancelled = 4,
}

/// <summary>
/// A stocktake in one branch: lines snapshot the system quantity when the count starts, staff enter what they counted, a manager reviews
/// the differences and approval books them as stock movements (reason <see cref="StockReason.StockCount"/>).
/// </summary>
public sealed class StockCount : Entity, IBranchScoped
{
    private readonly List<StockCountLine> _lines = [];

    private StockCount()
    {
    }

    public StockCount(Guid branchId, string? note, Func<Guid, string> numberFor)
    {
        Number = numberFor(Id);
        BranchId = branchId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public string Number { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public CountStatus Status { get; private set; } = CountStatus.InProgress;

    public string? Note { get; private set; }

    public DateTimeOffset? SubmittedAt { get; private set; }

    public Guid? SubmittedBy { get; private set; }

    public DateTimeOffset? ClosedAt { get; private set; }

    public Guid? ClosedBy { get; private set; }

    public IReadOnlyList<StockCountLine> Lines => _lines;

    public void AddLine(Guid variantId, int expectedQuantity) => _lines.Add(new StockCountLine(Id, variantId, expectedQuantity));

    public void Submit(DateTimeOffset now, Guid? by)
    {
        Require(CountStatus.InProgress);
        Status = CountStatus.Submitted;
        SubmittedAt = now;
        SubmittedBy = by;
    }

    /// <summary>Sends a submitted count back so more lines can be counted.</summary>
    public void Reopen()
    {
        Require(CountStatus.Submitted);
        Status = CountStatus.InProgress;
        SubmittedAt = null;
        SubmittedBy = null;
    }

    public void Approve(DateTimeOffset now, Guid? by)
    {
        Require(CountStatus.Submitted);
        Status = CountStatus.Approved;
        ClosedAt = now;
        ClosedBy = by;
    }

    public void Cancel(DateTimeOffset now, Guid? by)
    {
        if (Status is not (CountStatus.InProgress or CountStatus.Submitted))
        {
            throw new InvalidOperationException($"A {Status} count cannot be cancelled.");
        }

        Status = CountStatus.Cancelled;
        ClosedAt = now;
        ClosedBy = by;
    }

    private void Require(CountStatus expected)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"The count is {Status}, expected {expected}.");
        }
    }
}

public sealed class StockCountLine : Entity
{
    private StockCountLine()
    {
    }

    internal StockCountLine(Guid countId, Guid variantId, int expectedQuantity)
    {
        CountId = countId;
        VariantId = variantId;
        ExpectedQuantity = expectedQuantity;
    }

    public Guid CountId { get; private set; }

    public Guid VariantId { get; private set; }

    /// <summary>The system on-hand quantity when the count started.</summary>
    public int ExpectedQuantity { get; private set; }

    /// <summary>What staff counted; null while not counted (uncounted lines are ignored on approval).</summary>
    public int? CountedQuantity { get; private set; }

    public int? Difference => CountedQuantity is { } counted ? counted - ExpectedQuantity : null;

    public void SetCounted(int? quantity) => CountedQuantity = quantity;
}
