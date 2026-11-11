using Juvo.Domain.Orders;
using Juvo.Domain.Payments;

namespace Juvo.Application.Payments;

// ---- Customer side -----------------------------------------------------------------------------------------

/// <summary>Start (or resume) an online payment. Identify the order by id or number, plus the tracking token shown at checkout (not needed when signed in as the owner).</summary>
public sealed record InitiatePaymentRequest(Guid? OrderId, string? OrderNumber, string? TrackingToken, string? Method, string? ReturnUrl);

/// <summary>Tell the shop you paid by bank or mobile transfer. Staff check the reference (the payment becomes UnderReview).</summary>
public sealed record ManualPaymentRequest(Guid? OrderId, string? OrderNumber, string? TrackingToken, string? Reference, string? PayerName);

/// <summary>What the customer did on the sandbox pay page: <c>success</c>, <c>fail</c> or <c>cancel</c>. <c>SendWebhook</c> (default true) makes the sandbox call the shop's signed webhook; false leaves it to the browser return to ask the gateway.</summary>
public sealed record SandboxDecisionRequest(string? Outcome, bool? SendWebhook);

/// <summary>What the checkout page needs to follow a payment: where to send the customer, and the status of the payment and of the order.</summary>
public sealed record PaymentSessionDto(
    Guid PaymentId, string OrderNumber, PaymentKind Kind, string Method, decimal Amount, string Currency, PaymentStatus Status, string? RedirectUrl, DateTimeOffset? ExpiresAt, string? ReturnUrl,
    string? FailureReason, OrderStatus OrderStatus, PaymentStatus OrderPaymentStatus, bool CanRetry, string? SandboxSessionId);

public sealed record SandboxSessionDto(string SessionId, string OrderNumber, decimal Amount, string Currency, string Status, DateTimeOffset ExpiresAt, string? ReturnUrl);

public sealed record SandboxDecisionDto(string SessionId, string Outcome, string? ReturnUrl, PaymentStatus PaymentStatus);

public sealed record WebhookResultDto(string Status, string? PaymentStatus);

// ---- Staff side --------------------------------------------------------------------------------------------

public sealed record PaymentListQuery(PaymentStatus? Status, string? Method, PaymentKind? Kind, Guid? BranchId, DateTimeOffset? From, DateTimeOffset? To);

public sealed record PaymentListItemDto(
    Guid Id, Guid OrderId, string OrderNumber, Guid BranchId, string BranchCode, PaymentKind Kind, string Method, decimal Amount, string Currency, PaymentStatus Status, decimal RefundedAmount,
    string? SubmittedReference, bool HasProof, DateTimeOffset CreatedAt, DateTimeOffset? PaidAt);

public sealed record PaymentEventDto(string Type, PaymentStatus? From, PaymentStatus? To, decimal? Amount, string? Detail, string? Actor, DateTimeOffset At);

public sealed record RefundDto(
    Guid Id, Guid PaymentId, Guid OrderId, string OrderNumber, decimal Amount, string Currency, RefundMode Mode, RefundState Status, string? Reason, string? Reference, Guid? ReturnRequestId,
    string? CreditNoteNumber, string? FailureReason, string? RequestedBy, DateTimeOffset RequestedAt, DateTimeOffset? CompletedAt);

public sealed record PaymentDetailDto(
    Guid Id, Guid OrderId, string OrderNumber, Guid BranchId, string BranchCode, PaymentKind Kind, string Method, string? Provider, decimal Amount, string Currency, PaymentStatus Status,
    decimal RefundedAmount, decimal RefundPendingAmount, decimal Refundable, string? ProviderTransactionId, string? SubmittedReference, string? PayerName, string? ProofUrl, string? ReceivedReference, string? FailureReason,
    DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, DateTimeOffset? SettledAt, OrderStatus OrderStatus, PaymentStatus OrderPaymentStatus, IReadOnlyList<PaymentEventDto> Events, IReadOnlyList<RefundDto> Refunds,
    IReadOnlyList<string> NextActions);

public sealed record ReviewDecisionRequest(string? Note);

public sealed record RejectPaymentRequest(string? Reason);

public sealed record RecordAdvanceRequest(decimal Amount, string? Method, string? Reference);

public sealed record CodCollectRequest(decimal? Amount, string? Reference);

public sealed record CodSettleRequest(string? Reference);

public sealed record CreateRefundRequest(decimal Amount, RefundMode Mode, string? Reason);

public sealed record CompleteRefundRequest(string? Reference);

public sealed record FailRefundRequest(string? Reason);

public sealed record RefundListQuery(RefundState? Status, Guid? BranchId, Guid? OrderId, DateTimeOffset? From, DateTimeOffset? To);

public sealed record LedgerQuery(DateTimeOffset? From, DateTimeOffset? To, Guid? BranchId);

public sealed record LedgerMethodDto(string Method, int Payments, decimal Received, decimal Refunded);

/// <summary>Money in and out for a period. Online, manual and advance payments come from the payments table; cash on delivery comes from the orders (cash is counted when delivered).</summary>
public sealed record LedgerDto(
    DateTimeOffset From, DateTimeOffset To, decimal OnlineReceived, decimal OnlineRefunded, decimal CodCollected, decimal CodSettled, decimal CodOutstanding, int UnderReviewCount, decimal UnderReviewAmount,
    int PendingRefundCount, decimal PendingRefundAmount, decimal Net, IReadOnlyList<LedgerMethodDto> ByMethod);
