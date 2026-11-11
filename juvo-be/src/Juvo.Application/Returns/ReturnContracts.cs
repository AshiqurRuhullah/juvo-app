using Juvo.Application.Orders;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;

namespace Juvo.Application.Returns;

// ---- Customer ------------------------------------------------------------------------------------------------

public sealed record ReturnReasonDto(string Code, string LabelEn, string LabelBn);

/// <summary>One order line with what may still come back. <c>BlockedReason</c> is a code (<c>NON_RETURNABLE</c>, <c>ALL_RETURNED</c>) when <c>ReturnableQuantity</c> is zero.</summary>
public sealed record ReturnableLineDto(
    Guid OrderItemId, Guid VariantId, Guid ProductId, string Name, string? NameBn, string Sku, string? ColorName, string? Size, string? ImageUrl, decimal UnitPrice, int Quantity, int AlreadyReturned,
    int ReturnableQuantity, decimal RefundPerUnit, bool Returnable, string? BlockedReason);

/// <summary>
/// Whether (and how) the customer may return this order. <c>Eligible</c> is false with a <c>ReasonCode</c> (<c>RETURN.ORDER_NOT_ELIGIBLE</c>, <c>RETURN.WINDOW_EXPIRED</c>,
/// <c>RETURN.ITEM_NOT_RETURNABLE</c>) when nothing can be returned. <c>RefundPerUnit</c> is the price paid per unit after the order discount (the delivery charge is not part of it).
/// </summary>
public sealed record ReturnEligibilityDto(
    Guid OrderId, string OrderNumber, bool Eligible, string? ReasonCode, string? Message, DateTimeOffset? DeliveredAt, DateTimeOffset? Deadline, int WindowDays,
    IReadOnlyList<ReturnableLineDto> Items, IReadOnlyList<ReturnReasonDto> Reasons, IReadOnlyList<string> Kinds);

public sealed record CreateReturnLine(Guid OrderItemId, int Quantity, Guid? ExchangeVariantId);

/// <summary>
/// Ask for a return or an exchange. <c>PhotoPaths</c> come from <c>POST /me/returns/photos</c> (up to 5). An exchange line names the variant the customer wants instead
/// (same product; defaults to the same variant). <c>IdempotencyKey</c> (or the <c>Idempotency-Key</c> header) makes a repeated call return the same request.
/// </summary>
public sealed record CreateReturnRequest(
    Guid OrderId, ReturnKind Kind, string ReasonCode, string? Details, IReadOnlyList<CreateReturnLine>? Items, IReadOnlyList<string>? PhotoPaths, string? IdempotencyKey);

public sealed record ReturnPhotoUploadDto(string Path, string Url, string ThumbUrl);

public sealed record ReturnPhotoDto(string Path, string Url, string ThumbUrl);

public sealed record ReturnItemDto(
    Guid Id, Guid OrderItemId, Guid VariantId, Guid ProductId, string Name, string Sku, string? ColorName, string? Size, string? ImageUrl, int Quantity, decimal UnitPrice, decimal LineRefund,
    decimal RefundAmount, Guid? ExchangeVariantId, string? ExchangeSku, ItemCondition? Condition, bool? Restock, bool Restocked, bool RefundAccepted);

public sealed record ReturnTimelineDto(string? From, string To, string? Reason, string? ActorName, DateTimeOffset At);

public sealed record ReturnRefundDto(Guid Id, decimal Amount, RefundMethod Method, RefundStatus Status, string Reference, string? ActorName, DateTimeOffset At);

public sealed record ReturnPickupDto(DateOnly? Date, string? Courier, string? Note);

public sealed record RefundBreakdownDto(decimal Items, decimal DeliveryCharge, decimal ReturnCharge, decimal Total, bool Final);

public sealed record MyReturnListItemDto(
    Guid Id, string Number, Guid OrderId, string OrderNumber, ReturnKind Kind, ReturnStatus Status, int Quantity, string? FirstItemName, string? FirstItemImageUrl, decimal Refund,
    DateTimeOffset RequestedAt, bool CanCancel);

/// <summary>The customer's view: the amounts they will get (an estimate until the parcel is inspected), a timeline without staff names, no internal notes.</summary>
public sealed record MyReturnDto(
    Guid Id, string Number, Guid OrderId, string OrderNumber, ReturnKind Kind, ReturnStatus Status, string ReasonCode, string? ReasonText, string? RejectReason, DateTimeOffset RequestedAt,
    IReadOnlyList<ReturnItemDto> Items, IReadOnlyList<ReturnPhotoDto> Photos, IReadOnlyList<ReturnTimelineDto> Timeline, ReturnPickupDto? Pickup, RefundBreakdownDto Refund,
    ReturnRefundDto? RefundPaid, string? ReplacementOrderNumber, Guid? ReplacementOrderId, bool CanCancel);

// ---- Staff ---------------------------------------------------------------------------------------------------

public sealed record ReturnListQuery(
    ReturnStatus? Status, ReturnKind? Kind, ReturnFault? Fault, Guid? BranchId, DateTimeOffset? From, DateTimeOffset? To);

public sealed record ReturnListItemDto(
    Guid Id, string Number, Guid OrderId, string OrderNumber, Guid BranchId, string BranchCode, ReturnKind Kind, ReturnStatus Status, ReturnFault Fault, string ReasonCode, string? CustomerName,
    string CustomerMobile, int Quantity, decimal Refund, DateTimeOffset RequestedAt);

public sealed record ReturnNoteDto(Guid Id, string Text, string? AuthorName, DateTimeOffset At);

public sealed record ReturnOrderDto(
    OrderStatus Status, PaymentStatus PaymentStatus, DeliveryStatus DeliveryStatus, string PaymentMethod, decimal GrandTotal, decimal DeliveryCharge, DateTimeOffset? DeliveredAt);

public sealed record ReturnDetailDto(
    Guid Id, string Number, Guid BranchId, string BranchCode, Guid OrderId, string OrderNumber, Guid CustomerId, string CustomerMobile, string? CustomerName, ReturnKind Kind, ReturnStatus Status,
    ReturnFault Fault, string ReasonCode, string? ReasonText, string? RejectReason, DateTimeOffset RequestedAt, DateTimeOffset? ApprovedAt, DateTimeOffset? RejectedAt, DateTimeOffset? PickupScheduledAt,
    DateTimeOffset? PickedUpAt, DateTimeOffset? ReceivedAt, DateTimeOffset? InspectedAt, DateTimeOffset? RefundedAt, DateTimeOffset? ExchangedAt, DateTimeOffset? CancelledAt, ReturnPickupDto? Pickup,
    RefundBreakdownDto Refund, ReturnRefundDto? RefundPaid, Guid? ReplacementOrderId, string? ReplacementOrderNumber, IReadOnlyList<ReturnItemDto> Items, IReadOnlyList<ReturnPhotoDto> Photos,
    IReadOnlyList<ReturnTimelineDto> Timeline, IReadOnlyList<ReturnNoteDto> Notes, ReturnOrderDto Order, IReadOnlyList<string> NextStatuses);

public sealed record ApproveReturnRequest(string? Note);

public sealed record RejectReturnRequest(string? Reason);

/// <summary>Pickup of a return. With <c>UseCourier</c> (or a <c>CourierId</c>) the system books a reverse parcel with the courier (sandbox by default) and the courier's events move the return to PickedUp and Received; without it staff do those steps by hand as before.</summary>
public sealed record SchedulePickupRequest(DateOnly Date, string? Courier, string? Note, bool UseCourier = false, Guid? CourierId = null);

public sealed record InspectLine(Guid ReturnItemId, ItemCondition Condition, bool Restock, bool? AcceptRefund);

/// <summary>Inspection result for every line. <c>Fault</c> overrides the fault the customer's reason pointed to (it decides the delivery charge and return charge).</summary>
public sealed record InspectReturnRequest(IReadOnlyList<InspectLine>? Items, ReturnFault? Fault, string? Note);

/// <summary><c>Amount</c> is optional; when sent it must equal the amount the server computed (RETURN.REFUND_AMOUNT_MISMATCH otherwise). <c>Reference</c> is the transfer or receipt number.</summary>
public sealed record RefundReturnRequest(RefundMethod Method, string? Reference, decimal? Amount);

public sealed record ExchangeLine(Guid ReturnItemId, Guid VariantId);

/// <summary>Complete an exchange: a zero-value replacement order is created and stock reserved. <c>Lines</c> may override the variants the customer asked for.</summary>
public sealed record ExchangeReturnRequest(IReadOnlyList<ExchangeLine>? Lines, string? Note);

public sealed record ReturnNoteRequest(string? Text);

public sealed record BulkReturnRequest(string? Action, IReadOnlyList<Guid>? Ids, string? Reason);

public sealed record BulkReturnItemResult(Guid Id, string? Number, bool Ok, string? Code, string? Message);

public sealed record BulkReturnResult(string Action, int Succeeded, int Failed, IReadOnlyList<BulkReturnItemResult> Results);

public sealed record ReturnCountDto(ReturnStatus Status, int Count);

/// <summary>For the dashboard: how many returns wait for each step, and the money paid back this month.</summary>
public sealed record ReturnSummaryDto(
    int Open, int AwaitingApproval, int AwaitingPickup, int InTransit, int AwaitingInspection, int AwaitingRefund, int AwaitingExchange, decimal RefundedThisMonth, IReadOnlyList<ReturnCountDto> ByStatus);
