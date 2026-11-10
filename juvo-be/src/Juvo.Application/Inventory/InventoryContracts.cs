using Juvo.Domain.Inventory;

namespace Juvo.Application.Inventory;

// ---- Stock ---------------------------------------------------------------------------------------------------

public sealed record StockListQuery(Guid? BranchId, Guid? ProductId, bool? LowOnly);

public sealed record StockRowDto(
    Guid BranchId, string BranchCode, Guid VariantId, Guid ProductId, string ProductName, string Sku, string? ColorName, string? Size,
    int OnHand, int Reserved, int Available, int ReorderLevel, bool IsLow);

public sealed record BranchStockDto(
    Guid BranchId, string BranchCode, string BranchName, int OnHand, int Reserved, int Available, int ReorderLevel, bool IsLow);

public sealed record VariantStockDto(
    Guid VariantId, string Sku, string? ColorName, string? Size, bool TracksStock, int OnHand, int Reserved, int Available,
    IReadOnlyList<BranchStockDto> Branches);

public sealed record ProductStockDto(Guid ProductId, string ProductName, IReadOnlyList<VariantStockDto> Variants);

public sealed record AdjustLine(Guid VariantId, int QuantityChange);

/// <summary>Several lines are applied together or not at all. The branch defaults to the active branch.</summary>
public sealed record AdjustStockRequest(Guid? BranchId, StockReason Reason, string? Note, IReadOnlyList<AdjustLine> Lines);

public sealed record SetReorderLevelRequest(Guid? BranchId, Guid VariantId, int ReorderLevel);

public sealed record MovementListQuery(Guid? BranchId, Guid? VariantId, Guid? ProductId, StockReason? Reason, DateTimeOffset? From, DateTimeOffset? To);

public sealed record StockMovementDto(
    Guid Id, Guid BranchId, string BranchCode, Guid VariantId, Guid ProductId, string ProductName, string Sku, int QuantityChange, int OnHandAfter,
    StockReason Reason, string? Note, string? ReferenceType, Guid? ReferenceId, DateTimeOffset CreatedAt, string? CreatedByName);

// ---- Transfers -----------------------------------------------------------------------------------------------

public sealed record TransferListQuery(TransferStatus? Status, Guid? BranchId);

public sealed record TransferLineRequest(Guid VariantId, int Quantity);

public sealed record RequestTransferRequest(Guid FromBranchId, Guid ToBranchId, string? Note, IReadOnlyList<TransferLineRequest> Lines);

public sealed record ReasonRequest(string? Reason);

public sealed record TransferLineDto(Guid VariantId, Guid ProductId, string ProductName, string Sku, string? ColorName, string? Size, int Quantity);

public sealed record TransferDto(
    Guid Id, string Number, Guid FromBranchId, string FromBranchCode, Guid ToBranchId, string ToBranchCode, TransferStatus Status, string? Note,
    string? CancelReason, DateTimeOffset CreatedAt, DateTimeOffset? ApprovedAt, DateTimeOffset? ShippedAt, DateTimeOffset? ReceivedAt,
    DateTimeOffset? ClosedAt, IReadOnlyList<TransferLineDto> Lines, IReadOnlyList<string> AllowedActions);

// ---- Stock counts --------------------------------------------------------------------------------------------

public sealed record CountListQuery(CountStatus? Status, Guid? BranchId);

public sealed record StartCountRequest(Guid? BranchId, string? Note, IReadOnlyList<Guid>? VariantIds);

public sealed record CountEntry(Guid VariantId, int? CountedQuantity);

public sealed record SaveCountLinesRequest(IReadOnlyList<CountEntry> Lines);

public sealed record CountLineDto(
    Guid VariantId, Guid ProductId, string ProductName, string Sku, string? ColorName, string? Size, int ExpectedQuantity, int? CountedQuantity, int? Difference);

public sealed record CountListItemDto(
    Guid Id, string Number, Guid BranchId, string BranchCode, CountStatus Status, string? Note, DateTimeOffset CreatedAt, DateTimeOffset? SubmittedAt,
    DateTimeOffset? ClosedAt, int LineCount, int CountedCount, int DifferenceLines, int TotalDifference);

public sealed record CountDto(CountListItemDto Summary, IReadOnlyList<CountLineDto> Lines, IReadOnlyList<string> AllowedActions);
