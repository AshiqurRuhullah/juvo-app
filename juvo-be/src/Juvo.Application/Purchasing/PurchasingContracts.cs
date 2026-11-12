using Juvo.Application.Accounting;
using Juvo.Domain.Accounting;
using Juvo.Domain.Purchasing;
using Juvo.Shared.Pagination;

namespace Juvo.Application.Purchasing;

// ---- Suppliers ----

public sealed record SupplierDto(
    Guid Id, string Name, string? ContactName, string? Phone, string? Email, string? Address, int PaymentTermsDays, string? TaxId, string? Notes, bool IsActive, int OpenOrders, DateTimeOffset CreatedAt);

public sealed record SaveSupplierRequest(string Name, string? ContactName, string? Phone, string? Email, string? Address, int? PaymentTermsDays, string? TaxId, string? Notes, bool? IsActive);

// ---- Purchase orders ----

public sealed record PurchaseLineRequest(Guid VariantId, int Quantity, decimal UnitCost);

public sealed record SavePurchaseOrderRequest(Guid? BranchId, Guid SupplierId, DateOnly? ExpectedDate, string? Notes, IReadOnlyList<PurchaseLineRequest> Lines, IReadOnlyList<string>? AttachmentPaths);

public sealed record PurchaseListQuery(
    PurchaseStatus? Status, SupplierPaymentStatus? PaymentStatus, Guid? SupplierId, Guid? BranchId, bool? Overdue, bool? Payable, DateTimeOffset? From, DateTimeOffset? To);

public sealed record PurchaseListItemDto(
    Guid Id, string Number, Guid BranchId, string BranchCode, Guid SupplierId, string SupplierName, PurchaseStatus Status, SupplierPaymentStatus PaymentStatus, DateOnly? ExpectedDate,
    decimal Total, decimal ReceivedValue, decimal AmountPayable, decimal AmountPaid, decimal Outstanding, DateOnly? DueDate, bool IsOverdue, int LineCount, int OrderedQuantity, int ReceivedQuantity,
    DateTimeOffset CreatedAt);

public sealed record PurchaseLineDto(
    Guid Id, Guid VariantId, Guid ProductId, string Sku, string ProductName, string? ColorName, string? Size, int OrderedQuantity, int ReceivedQuantity, int RejectedQuantity, int OutstandingQuantity,
    decimal UnitCost, decimal LineTotal);

public sealed record PurchaseHistoryDto(PurchaseStatus? From, PurchaseStatus To, string? Reason, string? ActorName, DateTimeOffset At);

public sealed record PurchasePaymentDto(Guid Id, decimal Amount, MoneyMethod Method, string? Reference, DateOnly PaidOn, Guid? AccountId, string? AccountName, string? Note, string? ActorName, DateTimeOffset CreatedAt);

public sealed record PurchaseAttachmentDto(Guid Id, string FileName, string ContentType, long Size, string Url);

public sealed record ReceiptSummaryDto(Guid Id, string Number, DateTimeOffset ReceivedAt, string? ReceivedByName, int AcceptedQuantity, int RejectedQuantity);

public sealed record PurchaseDetailDto(
    Guid Id, string Number, Guid BranchId, string BranchCode, Guid SupplierId, string SupplierName, PurchaseStatus Status, SupplierPaymentStatus PaymentStatus, DateOnly? ExpectedDate, string? Notes, string Currency,
    decimal Total, decimal ReceivedValue, decimal AmountPayable, decimal AmountPaid, decimal Outstanding, string? SupplierInvoiceNumber, DateOnly? SupplierInvoiceDate, DateOnly? DueDate, decimal? InvoiceAmount,
    bool IsOverdue, DateTimeOffset? SentAt, DateTimeOffset? FirstReceivedAt, DateTimeOffset? CompletedAt, DateTimeOffset? CancelledAt, string? CancelReason, DateTimeOffset CreatedAt,
    IReadOnlyList<PurchaseLineDto> Lines, IReadOnlyList<PurchaseHistoryDto> History, IReadOnlyList<ReceiptSummaryDto> Receipts, IReadOnlyList<PurchasePaymentDto> Payments,
    IReadOnlyList<PurchaseAttachmentDto> Attachments, IReadOnlyList<string> NextStatuses);

public sealed record ReceiveLineRequest(Guid LineId, int AcceptedQuantity, int RejectedQuantity, string? Note);

public sealed record ReceiveGoodsRequest(string? Note, IReadOnlyList<ReceiveLineRequest> Lines);

public sealed record GoodsReceiptLineDto(Guid Id, Guid PurchaseLineId, Guid VariantId, string Sku, int AcceptedQuantity, int RejectedQuantity, decimal UnitCost, bool CostApplied, string? Note);

public sealed record GoodsReceiptDto(
    Guid Id, string Number, Guid PurchaseOrderId, string PurchaseOrderNumber, Guid BranchId, DateTimeOffset ReceivedAt, string? ReceivedByName, string? Note, IReadOnlyList<GoodsReceiptLineDto> Lines);

public sealed record ReceiveResultDto(GoodsReceiptDto Receipt, PurchaseDetailDto Order);

public sealed record ReceiptListQuery(Guid? PurchaseOrderId, Guid? BranchId, DateTimeOffset? From, DateTimeOffset? To);

public sealed record CancelPurchaseRequest(string? Reason);

public sealed record RecordSupplierPaymentRequest(decimal Amount, MoneyMethod Method, string? Reference, DateOnly? PaidOn, Guid? AccountId, string? Note);

public sealed record RecordSupplierInvoiceRequest(string Number, DateOnly InvoiceDate, DateOnly? DueDate, decimal? Amount);

public sealed record AddPurchaseAttachmentsRequest(IReadOnlyList<string> AttachmentPaths);

/// <summary>Everything a print or PDF of the purchase order needs, as data. The front end lays it out.</summary>
public sealed record PurchasePrintDto(
    string Number, PurchaseStatus Status, string Currency, DateTimeOffset CreatedAt, DateOnly? ExpectedDate, string? Notes, PrintPartyDto Buyer, PrintPartyDto Supplier, IReadOnlyList<PrintLineDto> Lines,
    decimal Total, string? SupplierInvoiceNumber, DateOnly? DueDate);

public sealed record PrintPartyDto(string Name, string? Contact, string? Phone, string? Email, string? Address);

public sealed record PrintLineDto(int No, string Sku, string Description, int Quantity, decimal UnitCost, decimal LineTotal);

// ---- Low stock suggestions ----

public sealed record SuggestedLineDto(Guid VariantId, Guid ProductId, string Sku, string ProductName, string? ColorName, string? Size, int OnHand, int Reserved, int Available, int ReorderLevel, int OnOrder, int SuggestedQuantity, decimal? LastUnitCost);

public sealed record SuggestionGroupDto(Guid? SupplierId, string? SupplierName, IReadOnlyList<SuggestedLineDto> Lines);

public sealed record SuggestionsDto(Guid BranchId, string BranchCode, IReadOnlyList<SuggestionGroupDto> Groups, int TotalLines);
