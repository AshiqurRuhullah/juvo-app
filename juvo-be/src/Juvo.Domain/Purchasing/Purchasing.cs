using Juvo.Domain.Accounting;
using Juvo.Domain.Common;

namespace Juvo.Domain.Purchasing;

/// <summary>A supplier (module W). Archived, never erased, so old purchase orders keep their supplier.</summary>
public sealed class Supplier : SoftDeletableEntity
{
    private Supplier()
    {
    }

    public Supplier(string name, string? contactName, string? phone, string? email, string? address, int paymentTermsDays, string? taxId, string? notes)
    {
        Update(name, contactName, phone, email, address, paymentTermsDays, taxId, notes);
    }

    public string Name { get; private set; } = string.Empty;

    public string? ContactName { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? Address { get; private set; }

    /// <summary>Days after the supplier invoice date that payment is due (0 = on delivery).</summary>
    public int PaymentTermsDays { get; private set; }

    public string? TaxId { get; private set; }

    public string? Notes { get; private set; }

    public bool IsActive { get; private set; } = true;

    public void Update(string name, string? contactName, string? phone, string? email, string? address, int paymentTermsDays, string? taxId, string? notes)
    {
        Name = name.Trim();
        ContactName = Clean(contactName);
        Phone = Clean(phone);
        Email = Clean(email);
        Address = Clean(address);
        PaymentTermsDays = Math.Max(0, paymentTermsDays);
        TaxId = Clean(taxId);
        Notes = Clean(notes);
    }

    public void SetActive(bool active) => IsActive = active;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>Draft, Sent, PartlyReceived, Received; Cancelled from Draft or Sent only. Stored as an integer; append new values at the end.</summary>
public enum PurchaseStatus
{
    Draft = 1,
    Sent = 2,
    PartlyReceived = 3,
    Received = 4,
    Cancelled = 5,
}

public enum SupplierPaymentStatus
{
    Unpaid = 1,
    PartlyPaid = 2,
    Paid = 3,
}

/// <summary>The allowed moves of a purchase order (CLAUDE.md section 7): Draft to Sent; Sent to PartlyReceived or Received; PartlyReceived to Received; Draft and Sent can be cancelled.</summary>
public static class PurchaseStateMachine
{
    private static readonly Dictionary<PurchaseStatus, PurchaseStatus[]> Moves = new()
    {
        [PurchaseStatus.Draft] = [PurchaseStatus.Sent, PurchaseStatus.Cancelled],
        [PurchaseStatus.Sent] = [PurchaseStatus.PartlyReceived, PurchaseStatus.Received, PurchaseStatus.Cancelled],
        [PurchaseStatus.PartlyReceived] = [PurchaseStatus.Received],
        [PurchaseStatus.Received] = [],
        [PurchaseStatus.Cancelled] = [],
    };

    public static IReadOnlyList<PurchaseStatus> NextFor(PurchaseStatus from) => Moves[from];

    public static bool CanMove(PurchaseStatus from, PurchaseStatus to) => Moves[from].Contains(to);

    public static bool IsFinal(PurchaseStatus status) => Moves[status].Length == 0;
}

/// <summary>A line of a purchase order as the staff member typed it (the names are copied from the variant when it is saved).</summary>
public sealed record NewPurchaseLine(Guid VariantId, Guid ProductId, string Sku, string ProductName, string? ColorName, string? Size, int Quantity, decimal UnitCost);

/// <summary>What arrived for one line: units that go on the shelf and units that were damaged or refused.</summary>
public sealed record ReceivedLine(Guid LineId, int Accepted, int Rejected, string? Note);

/// <summary>
/// A purchase order of one branch (module W). Lines are by variant with an ordered and a received quantity and a unit cost. It follows
/// <see cref="PurchaseStateMachine"/>; every status change writes an append-only history row. Goods are received in as many partial receipts as needed;
/// received means accepted onto the shelf (rejected units are counted apart and never count toward completion). Supplier payments are recorded against the order.
/// </summary>
public sealed class PurchaseOrder : Entity, IBranchScoped
{
    private readonly List<PurchaseOrderLine> _lines = [];
    private readonly List<PurchaseStatusHistory> _history = [];
    private readonly List<PurchaseAttachment> _attachments = [];

    private PurchaseOrder()
    {
    }

    public PurchaseOrder(string number, Guid branchId, Guid supplierId, DateOnly? expectedDate, string? notes, IReadOnlyList<NewPurchaseLine> lines, DateTimeOffset now, Guid? by, string? byName)
    {
        Number = number;
        BranchId = branchId;
        SupplierId = supplierId;
        ExpectedDate = expectedDate;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        SetLines(lines);
        _history.Add(new PurchaseStatusHistory(Id, null, PurchaseStatus.Draft, "Created", by, byName, now));
    }

    public string Number { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public Guid SupplierId { get; private set; }

    public PurchaseStatus Status { get; private set; } = PurchaseStatus.Draft;

    public DateOnly? ExpectedDate { get; private set; }

    public string? Notes { get; private set; }

    public string Currency { get; private set; } = "BDT";

    public DateTimeOffset? SentAt { get; private set; }

    public DateTimeOffset? FirstReceivedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? CancelledAt { get; private set; }

    public string? CancelReason { get; private set; }

    // ---- Supplier invoice and payment ----
    public string? SupplierInvoiceNumber { get; private set; }

    public DateOnly? SupplierInvoiceDate { get; private set; }

    public DateOnly? DueDate { get; private set; }

    /// <summary>The amount on the supplier's invoice, when it differs from the order value.</summary>
    public decimal? InvoiceAmount { get; private set; }

    public decimal AmountPaid { get; private set; }

    public SupplierPaymentStatus PaymentStatus { get; private set; } = SupplierPaymentStatus.Unpaid;

    public IReadOnlyList<PurchaseOrderLine> Lines => _lines;

    public IReadOnlyList<PurchaseStatusHistory> History => _history;

    public IReadOnlyList<PurchaseAttachment> Attachments => _attachments;

    /// <summary>The value of everything ordered.</summary>
    public decimal Total => _lines.Sum(l => l.OrderedQuantity * l.UnitCost);

    /// <summary>The value of what was accepted onto the shelf so far.</summary>
    public decimal ReceivedValue => _lines.Sum(l => l.ReceivedQuantity * l.UnitCost);

    /// <summary>What the shop owes: the supplier's invoice amount when recorded, the received value once the order is Received, otherwise the order value.</summary>
    public decimal AmountPayable => InvoiceAmount ?? (Status == PurchaseStatus.Received ? ReceivedValue : Total);

    public decimal Outstanding => Math.Max(0, AmountPayable - AmountPaid);

    public bool IsOverdue(DateOnly today) => DueDate is { } due && due < today && Status != PurchaseStatus.Cancelled && PaymentStatus != SupplierPaymentStatus.Paid;

    public void UpdateDraft(Guid supplierId, DateOnly? expectedDate, string? notes, IReadOnlyList<NewPurchaseLine> lines)
    {
        Require(PurchaseStatus.Draft, "edited");
        SupplierId = supplierId;
        ExpectedDate = expectedDate;
        Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        SetLines(lines);
    }

    private void SetLines(IReadOnlyList<NewPurchaseLine> lines)
    {
        if (lines.Count == 0 || lines.Any(l => l.Quantity < 1 || l.UnitCost < 0))
        {
            throw new ArgumentException("A purchase order needs at least one line with a positive quantity and a cost of zero or more.", nameof(lines));
        }

        _lines.Clear();
        foreach (var group in lines.GroupBy(l => l.VariantId))
        {
            var first = group.First();
            var quantity = group.Sum(l => l.Quantity);
            // A variant listed twice is merged; the cost of the last line wins.
            _lines.Add(new PurchaseOrderLine(Id, first.VariantId, first.ProductId, first.Sku, first.ProductName, first.ColorName, first.Size, quantity, group.Last().UnitCost));
        }
    }

    public void Send(DateTimeOffset now, Guid? by, string? byName)
    {
        MoveTo(PurchaseStatus.Sent, "Sent to the supplier", now, by, byName);
        SentAt = now;
    }

    public void Cancel(string reason, DateTimeOffset now, Guid? by, string? byName)
    {
        MoveTo(PurchaseStatus.Cancelled, reason, now, by, byName);
        CancelledAt = now;
        CancelReason = reason.Trim();
    }

    /// <summary>
    /// Books what arrived: accepted units count toward the order. Moves the order to PartlyReceived or Received. The caller has already checked the
    /// quantities with <see cref="OutstandingOf"/>; this throws if they do not fit.
    /// </summary>
    public void Receive(IReadOnlyList<ReceivedLine> lines, DateTimeOffset now, Guid? by, string? byName)
    {
        if (Status is not (PurchaseStatus.Sent or PurchaseStatus.PartlyReceived))
        {
            throw new InvalidOperationException($"Goods cannot be received on an order that is {Status}.");
        }

        foreach (var received in lines)
        {
            var line = _lines.First(l => l.Id == received.LineId);
            line.Book(received.Accepted, received.Rejected);
        }

        FirstReceivedAt ??= now;
        var complete = _lines.All(l => l.OutstandingQuantity == 0);
        if (complete)
        {
            MoveTo(PurchaseStatus.Received, "Everything was received", now, by, byName);
            CompletedAt = now;
        }
        else if (Status == PurchaseStatus.Sent)
        {
            MoveTo(PurchaseStatus.PartlyReceived, "First delivery received", now, by, byName);
        }

        RefreshPaymentStatus();
    }

    /// <summary>Stops waiting for the rest of a partly received order (the supplier will not deliver more).</summary>
    public void CloseShort(string reason, DateTimeOffset now, Guid? by, string? byName)
    {
        MoveTo(PurchaseStatus.Received, $"Closed short: {reason.Trim()}", now, by, byName);
        CompletedAt = now;
        RefreshPaymentStatus();
    }

    public int OutstandingOf(Guid lineId) => _lines.First(l => l.Id == lineId).OutstandingQuantity;

    public void RecordInvoice(string number, DateOnly invoiceDate, DateOnly dueDate, decimal? amount)
    {
        if (Status is PurchaseStatus.Draft or PurchaseStatus.Cancelled)
        {
            throw new InvalidOperationException($"An invoice cannot be recorded on an order that is {Status}.");
        }

        SupplierInvoiceNumber = number.Trim();
        SupplierInvoiceDate = invoiceDate;
        DueDate = dueDate;
        InvoiceAmount = amount;
        RefreshPaymentStatus();
    }

    /// <summary>Adds a payment to the total paid. The service has checked the order state and the amount.</summary>
    public void ApplyPayment(decimal amount)
    {
        AmountPaid += amount;
        RefreshPaymentStatus();
    }

    public PurchaseAttachment AddAttachment(string path, string fileName, string contentType, long size)
    {
        var attachment = new PurchaseAttachment(Id, path, fileName, contentType, size);
        _attachments.Add(attachment);
        return attachment;
    }

    private void RefreshPaymentStatus()
    {
        PaymentStatus = AmountPaid <= 0 ? SupplierPaymentStatus.Unpaid
            : AmountPaid >= AmountPayable ? SupplierPaymentStatus.Paid
            : SupplierPaymentStatus.PartlyPaid;
    }

    private void MoveTo(PurchaseStatus to, string reason, DateTimeOffset now, Guid? by, string? byName)
    {
        if (!PurchaseStateMachine.CanMove(Status, to))
        {
            throw new InvalidOperationException($"A purchase order that is {Status} cannot become {to}.");
        }

        _history.Add(new PurchaseStatusHistory(Id, Status, to, reason, by, byName, now));
        Status = to;
    }

    private void Require(PurchaseStatus expected, string verb)
    {
        if (Status != expected)
        {
            throw new InvalidOperationException($"Only a {expected} purchase order can be {verb}; this one is {Status}.");
        }
    }
}

public sealed class PurchaseOrderLine : Entity
{
    private PurchaseOrderLine()
    {
    }

    internal PurchaseOrderLine(Guid purchaseOrderId, Guid variantId, Guid productId, string sku, string productName, string? colorName, string? size, int orderedQuantity, decimal unitCost)
    {
        PurchaseOrderId = purchaseOrderId;
        VariantId = variantId;
        ProductId = productId;
        Sku = sku;
        ProductName = productName;
        ColorName = colorName;
        Size = size;
        OrderedQuantity = orderedQuantity;
        UnitCost = unitCost;
    }

    public Guid PurchaseOrderId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public string ProductName { get; private set; } = string.Empty;

    public string? ColorName { get; private set; }

    public string? Size { get; private set; }

    public int OrderedQuantity { get; private set; }

    /// <summary>Units accepted onto the shelf so far.</summary>
    public int ReceivedQuantity { get; private set; }

    /// <summary>Units that arrived damaged or refused; they are not in stock and do not count toward completion.</summary>
    public int RejectedQuantity { get; private set; }

    public decimal UnitCost { get; private set; }

    public int OutstandingQuantity => Math.Max(0, OrderedQuantity - ReceivedQuantity);

    internal void Book(int accepted, int rejected)
    {
        if (accepted < 0 || rejected < 0 || ReceivedQuantity + accepted > OrderedQuantity)
        {
            throw new InvalidOperationException("The received quantity is more than what was ordered.");
        }

        ReceivedQuantity += accepted;
        RejectedQuantity += rejected;
    }
}

/// <summary>One status change of a purchase order: who, when, from, to, why. Append-only (BR-003 applied to purchasing).</summary>
public sealed class PurchaseStatusHistory : Entity, IAppendOnly, INotAudited
{
    private PurchaseStatusHistory()
    {
    }

    internal PurchaseStatusHistory(Guid purchaseOrderId, PurchaseStatus? from, PurchaseStatus to, string? reason, Guid? actorId, string? actorName, DateTimeOffset at)
    {
        PurchaseOrderId = purchaseOrderId;
        FromStatus = from;
        ToStatus = to;
        Reason = reason;
        ActorId = actorId;
        ActorName = actorName;
        At = at;
    }

    public Guid PurchaseOrderId { get; private set; }

    public PurchaseStatus? FromStatus { get; private set; }

    public PurchaseStatus ToStatus { get; private set; }

    public string? Reason { get; private set; }

    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }

    public DateTimeOffset At { get; private set; }
}

public sealed class PurchaseAttachment : Entity
{
    private PurchaseAttachment()
    {
    }

    internal PurchaseAttachment(Guid purchaseOrderId, string path, string fileName, string contentType, long size)
    {
        PurchaseOrderId = purchaseOrderId;
        Path = path;
        FileName = fileName;
        ContentType = contentType;
        Size = size;
    }

    public Guid PurchaseOrderId { get; private set; }

    public string Path { get; private set; } = string.Empty;

    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;

    public long Size { get; private set; }
}

/// <summary>
/// One delivery against a purchase order (a goods receipt): a number, who and when, and per line the accepted and the rejected units. Accepted units were added to the
/// branch stock through the stock ledger (reason PurchaseReceipt). The record is immutable except the flag that the cost price was applied.
/// </summary>
public sealed class GoodsReceipt : Entity, IBranchScoped
{
    private readonly List<GoodsReceiptLine> _lines = [];

    private GoodsReceipt()
    {
    }

    public GoodsReceipt(string number, Guid purchaseOrderId, string purchaseOrderNumber, Guid branchId, DateTimeOffset receivedAt, Guid? receivedBy, string? receivedByName, string? note)
    {
        Number = number;
        PurchaseOrderId = purchaseOrderId;
        PurchaseOrderNumber = purchaseOrderNumber;
        BranchId = branchId;
        ReceivedAt = receivedAt;
        ReceivedBy = receivedBy;
        ReceivedByName = receivedByName;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
    }

    public string Number { get; private set; } = string.Empty;

    public Guid PurchaseOrderId { get; private set; }

    public string PurchaseOrderNumber { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public DateTimeOffset ReceivedAt { get; private set; }

    public Guid? ReceivedBy { get; private set; }

    public string? ReceivedByName { get; private set; }

    public string? Note { get; private set; }

    public IReadOnlyList<GoodsReceiptLine> Lines => _lines;

    public GoodsReceiptLine AddLine(PurchaseOrderLine line, int accepted, int rejected, string? note, int productOnHandBefore, decimal? productCostBefore, bool costApplied)
    {
        var added = new GoodsReceiptLine(Id, line.Id, line.VariantId, line.ProductId, line.Sku, accepted, rejected, line.UnitCost, note, productOnHandBefore, productCostBefore, costApplied);
        _lines.Add(added);
        return added;
    }
}

public sealed class GoodsReceiptLine : Entity
{
    private GoodsReceiptLine()
    {
    }

    internal GoodsReceiptLine(Guid receiptId, Guid purchaseLineId, Guid variantId, Guid productId, string sku, int accepted, int rejected, decimal unitCost, string? note, int productOnHandBefore,
        decimal? productCostBefore, bool costApplied)
    {
        ReceiptId = receiptId;
        PurchaseLineId = purchaseLineId;
        VariantId = variantId;
        ProductId = productId;
        Sku = sku;
        AcceptedQuantity = accepted;
        RejectedQuantity = rejected;
        UnitCost = unitCost;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ProductOnHandBefore = productOnHandBefore;
        ProductCostBefore = productCostBefore;
        CostApplied = costApplied;
    }

    public Guid ReceiptId { get; private set; }

    public Guid PurchaseLineId { get; private set; }

    public Guid VariantId { get; private set; }

    public Guid ProductId { get; private set; }

    public string Sku { get; private set; } = string.Empty;

    public int AcceptedQuantity { get; private set; }

    public int RejectedQuantity { get; private set; }

    public decimal UnitCost { get; private set; }

    public string? Note { get; private set; }

    /// <summary>Units of the whole product (all variants, all branches) on hand just before this receipt; the base of the weighted-average cost.</summary>
    public int ProductOnHandBefore { get; private set; }

    /// <summary>The product's cost price just before this receipt.</summary>
    public decimal? ProductCostBefore { get; private set; }

    /// <summary>False when the receiving user may not see or change cost prices; a person who may can apply it later.</summary>
    public bool CostApplied { get; private set; }

    public void MarkCostApplied() => CostApplied = true;
}

/// <summary>A payment to a supplier against a purchase order. Append-only; an optional account books it into the cash book.</summary>
public sealed class PurchasePayment : Entity, IBranchScoped, IAppendOnly, INotAudited
{
    private PurchasePayment()
    {
    }

    public PurchasePayment(Guid purchaseOrderId, Guid branchId, decimal amount, MoneyMethod method, string? reference, DateOnly paidOn, Guid? accountId, string? note, Guid? actorId, string? actorName)
    {
        PurchaseOrderId = purchaseOrderId;
        BranchId = branchId;
        Amount = amount;
        Method = method;
        Reference = string.IsNullOrWhiteSpace(reference) ? null : reference.Trim();
        PaidOn = paidOn;
        AccountId = accountId;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        ActorId = actorId;
        ActorName = actorName;
    }

    public Guid PurchaseOrderId { get; private set; }

    public Guid BranchId { get; private set; }

    public decimal Amount { get; private set; }

    public MoneyMethod Method { get; private set; }

    public string? Reference { get; private set; }

    public DateOnly PaidOn { get; private set; }

    public Guid? AccountId { get; private set; }

    public string? Note { get; private set; }

    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }
}
