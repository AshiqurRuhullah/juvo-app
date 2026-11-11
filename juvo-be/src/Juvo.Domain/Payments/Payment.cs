using Juvo.Domain.Common;
using Juvo.Domain.Orders;

namespace Juvo.Domain.Payments;

/// <summary>What kind of money movement a <see cref="Payment"/> records.</summary>
public enum PaymentKind
{
    /// <summary>Paid on the website through a gateway (hosted page).</summary>
    Online = 1,

    /// <summary>A bank or mobile-money transfer the customer reports with a reference; staff verify it (UnderReview).</summary>
    Manual = 2,

    /// <summary>Cash the courier collects on delivery, later settled to the shop.</summary>
    Cod = 3,

    /// <summary>An advance staff received for a large cash-on-delivery order (BR-D1).</summary>
    Advance = 4,
}

public enum RefundMode
{
    /// <summary>The money goes back through the gateway.</summary>
    Gateway = 1,

    /// <summary>Staff transfer the money (cash, bank, wallet) and write down the reference.</summary>
    Manual = 2,
}

/// <summary>Pending refunds finish later; Completed and Failed are final.</summary>
public enum RefundState
{
    Pending = 1,
    Completed = 2,
    Failed = 3,
}

public sealed record PaymentInit(
    Guid OrderId, string OrderNumber, Guid BranchId, PaymentKind Kind, string Method, string? Provider, decimal Amount, string Currency, string? ProviderReference, string? RedirectUrl,
    string? ReturnUrl, DateTimeOffset? ExpiresAt, string? SubmittedReference, string? PayerName, string? ReceivedReference);

/// <summary>
/// One payment attempt or record for an order (module I). Its status uses the payment states of CLAUDE.md section 7 and the same transition table as the order's payment
/// track; every change appends a <see cref="PaymentEvent"/>. A retry after Failed is a NEW payment, never a reopened one. The refund total is guarded by the row version,
/// so two simultaneous refunds can never give back more than was paid.
/// </summary>
public sealed class Payment : Entity, IBranchScoped
{
    private readonly List<PaymentEvent> _events = [];

    private Payment()
    {
    }

    public Guid OrderId { get; private set; }

    public string OrderNumber { get; private set; } = string.Empty;

    public Guid BranchId { get; private set; }

    public PaymentKind Kind { get; private set; }

    /// <summary>The method code the customer chose: <c>SANDBOX</c>, <c>BKASH</c>, <c>MANUAL</c>, <c>COD</c> and so on.</summary>
    public string Method { get; private set; } = string.Empty;

    /// <summary>The gateway code that handled it (null for cash and manual payments).</summary>
    public string? Provider { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "BDT";

    public PaymentStatus Status { get; private set; } = PaymentStatus.Pending;

    /// <summary>The gateway's session or payment id (used to verify and to find the payment from a webhook).</summary>
    public string? ProviderReference { get; private set; }

    /// <summary>The gateway's transaction id after a verified payment.</summary>
    public string? ProviderTransactionId { get; private set; }

    public string? RedirectUrl { get; private set; }

    public string? ReturnUrl { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>For manual payments: the transaction id the customer typed (bank or mobile-money reference).</summary>
    [AuditIgnore]
    public string? SubmittedReference { get; private set; }

    [AuditIgnore]
    public string? PayerName { get; private set; }

    /// <summary>Stored path of the proof picture or PDF the customer uploaded (manual payments).</summary>
    public string? ProofPath { get; private set; }

    /// <summary>For cash payments and settlements: the courier or branch receipt reference staff wrote down.</summary>
    public string? ReceivedReference { get; private set; }

    public string? FailureReason { get; private set; }

    public DateTimeOffset? PaidAt { get; private set; }

    public DateTimeOffset? VerifiedAt { get; private set; }

    public DateTimeOffset? SettledAt { get; private set; }

    /// <summary>Refunds that are completed. Together with <see cref="RefundPendingAmount"/> never more than <see cref="Amount"/>.</summary>
    public decimal RefundedAmount { get; private set; }

    public IReadOnlyList<PaymentEvent> Events => _events;

    /// <summary>The money is held: paid online, reported and approved, collected by the courier, or settled.</summary>
    public bool HoldsMoney => Status is PaymentStatus.Paid or PaymentStatus.PartiallyRefunded or PaymentStatus.CollectedByCourier or PaymentStatus.Settled;

    /// <summary>Refunds reserved but not finished yet (Pending).</summary>
    public decimal RefundPendingAmount { get; private set; }

    public decimal Refundable => Math.Max(0m, Amount - RefundedAmount - RefundPendingAmount);

    public static Payment Start(PaymentInit i, DateTimeOffset now, Guid? by, string? byName)
    {
        if (i.Amount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(i), "A payment needs an amount above zero.");
        }

        var payment = new Payment
        {
            OrderId = i.OrderId,
            OrderNumber = i.OrderNumber,
            BranchId = i.BranchId,
            Kind = i.Kind,
            Method = i.Method,
            Provider = i.Provider,
            Amount = i.Amount,
            Currency = i.Currency,
            ProviderReference = i.ProviderReference,
            RedirectUrl = i.RedirectUrl,
            ReturnUrl = i.ReturnUrl,
            ExpiresAt = i.ExpiresAt,
            SubmittedReference = i.SubmittedReference,
            PayerName = i.PayerName,
            ReceivedReference = i.ReceivedReference,
        };
        payment._events.Add(new PaymentEvent(payment.Id, "Created", null, PaymentStatus.Pending, i.Amount, null, $"{i.Kind} payment by {i.Method}", by, byName, now));
        return payment;
    }

    /// <summary>
    /// Moves to another status. Throws when the move is not in the payment state machine. A refund status can only be reached through
    /// <see cref="ApplyRefundTotal"/>, so the amounts and the status can never disagree.
    /// </summary>
    public void MoveTo(PaymentStatus to, string type, string? detail, DateTimeOffset now, Guid? by, string? byName, string? providerEventId = null)
    {
        if (!OrderStateMachine.CanMove(Status, to))
        {
            throw new InvalidOperationException($"A payment cannot move from {Status} to {to}.");
        }

        var from = Status;
        Status = to;
        switch (to)
        {
            case PaymentStatus.Paid:
                PaidAt = now;
                break;
            case PaymentStatus.CollectedByCourier:
                PaidAt = now;
                break;
            case PaymentStatus.Settled:
                SettledAt = now;
                break;
            case PaymentStatus.Failed or PaymentStatus.Rejected or PaymentStatus.Cancelled:
                FailureReason = string.IsNullOrWhiteSpace(detail) ? FailureReason : detail.Trim();
                break;
            default:
                break;
        }

        _events.Add(new PaymentEvent(Id, type, from, to, Amount, providerEventId, detail, by, byName, now));
    }

    /// <summary>Records the gateway's confirmation (the caller verified it server-side first) and moves to Paid.</summary>
    public void ConfirmPaid(string? transactionId, DateTimeOffset now, string? providerEventId, string? detail, Guid? by, string? byName)
    {
        ProviderTransactionId = string.IsNullOrWhiteSpace(transactionId) ? ProviderTransactionId : transactionId;
        VerifiedAt = now;
        MoveTo(PaymentStatus.Paid, "Verified", detail ?? "Confirmed by the gateway", now, by, byName, providerEventId);
    }

    /// <summary>The customer reported a manual transfer; staff must check it.</summary>
    public void SubmitForReview(string reference, string? payer, DateTimeOffset now, Guid? by, string? byName)
    {
        SubmittedReference = reference.Trim();
        PayerName = string.IsNullOrWhiteSpace(payer) ? null : payer.Trim();
        MoveTo(PaymentStatus.UnderReview, "Submitted", $"Reference {SubmittedReference}", now, by, byName);
    }

    public void AttachProof(string path, DateTimeOffset now, Guid? by, string? byName)
    {
        ProofPath = path;
        _events.Add(new PaymentEvent(Id, "ProofAdded", Status, Status, null, null, "A proof file was added", by, byName, now));
    }

    public void Note(string type, string detail, DateTimeOffset now, Guid? by, string? byName, string? providerEventId = null) =>
        _events.Add(new PaymentEvent(Id, type, Status, Status, null, providerEventId, detail, by, byName, now));

    public void AttachGateway(string? providerReference, string? redirectUrl)
    {
        ProviderReference = providerReference;
        RedirectUrl = redirectUrl;
    }

    /// <summary>Records the receipt reference of cash or of a settlement.</summary>
    public void RecordReceipt(string? reference) => ReceivedReference = string.IsNullOrWhiteSpace(reference) ? ReceivedReference : reference.Trim();

    /// <summary>
    /// Sets money aside for a refund that is not finished yet (Pending). The reserved amount counts against <see cref="Refundable"/> at once, so two refunds can never
    /// give back more than was paid; the row version makes simultaneous reservations fail instead of both succeeding.
    /// </summary>
    public void ReserveRefund(decimal amount, DateTimeOffset now, Guid? by, string? byName, string detail)
    {
        if (!HoldsMoney)
        {
            throw new InvalidOperationException("Only a payment that holds money can be refunded.");
        }

        if (amount <= 0 || amount > Refundable)
        {
            throw new InvalidOperationException("The refund is more than what is left to refund.");
        }

        RefundPendingAmount += amount;
        Note("RefundRequested", detail, now, by, byName);
    }

    /// <summary>A reserved refund finished: the money counts as given back and the status becomes PartiallyRefunded or Refunded.</summary>
    public void CompleteRefund(decimal amount, DateTimeOffset now, Guid? by, string? byName, string detail)
    {
        if (amount <= 0 || amount > RefundPendingAmount)
        {
            throw new InvalidOperationException("There is no reserved refund of this amount.");
        }

        RefundPendingAmount -= amount;
        RefundedAmount += amount;
        var target = RefundedAmount >= Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        if (Status != target)
        {
            MoveTo(target, "Refunded", detail, now, by, byName);
        }
        else
        {
            Note("Refunded", detail, now, by, byName);
        }
    }

    /// <summary>A reserved refund failed: the money is refundable again and the status stays as it was.</summary>
    public void FailRefund(decimal amount, DateTimeOffset now, Guid? by, string? byName, string detail)
    {
        RefundPendingAmount = Math.Max(0m, RefundPendingAmount - amount);
        Note("RefundFailed", detail, now, by, byName);
    }

    /// <summary>The order was cancelled or timed out while this attempt was still open: close it.</summary>
    public bool CloseAsCancelled(string reason, DateTimeOffset now, Guid? by, string? byName)
    {
        if (!OrderStateMachine.CanMove(Status, PaymentStatus.Cancelled))
        {
            return false;
        }

        MoveTo(PaymentStatus.Cancelled, "Cancelled", reason, now, by, byName);
        return true;
    }
}

/// <summary>An append-only fact about a payment: created, verified, webhook seen, failed, refunded. Webhook rows carry the provider event id (unique) for idempotency.</summary>
public sealed class PaymentEvent : Entity, IAppendOnly, INotAudited
{
    private PaymentEvent()
    {
    }

    internal PaymentEvent(Guid paymentId, string type, PaymentStatus? from, PaymentStatus? to, decimal? amount, string? providerEventId, string? detail, Guid? actorId, string? actorName, DateTimeOffset at)
    {
        PaymentId = paymentId;
        Type = type;
        FromStatus = from;
        ToStatus = to;
        Amount = amount;
        ProviderEventId = providerEventId;
        Detail = detail is { Length: > 500 } ? detail[..500] : detail;
        ActorId = actorId;
        ActorName = actorName;
        At = at;
    }

    public Guid PaymentId { get; private set; }

    public string Type { get; private set; } = string.Empty;

    public PaymentStatus? FromStatus { get; private set; }

    public PaymentStatus? ToStatus { get; private set; }

    public decimal? Amount { get; private set; }

    /// <summary>The gateway's event id, prefixed with the provider (for example <c>SANDBOX:evt_123</c>). Unique when set, so a replayed webhook is recognised.</summary>
    public string? ProviderEventId { get; private set; }

    public string? Detail { get; private set; }

    /// <summary>Null when the system or the gateway acted.</summary>
    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }

    public DateTimeOffset At { get; private set; }
}

/// <summary>
/// Money given back for a payment (module I, FR-I refunds). Created Pending when a transfer by staff is still to be done, or when the gateway answers later;
/// Completed or Failed is final. A refund can come from a return (<see cref="ReturnRequestId"/>) or be started by staff. The payment's refunded total counts a
/// Pending refund already, so no refund can be given twice.
/// </summary>
public sealed class Refund : Entity, IBranchScoped
{
    private Refund()
    {
    }

    public Refund(Payment payment, decimal amount, RefundMode mode, string? reason, Guid? returnRequestId, Guid? by, string? byName, DateTimeOffset now)
    {
        PaymentId = payment.Id;
        OrderId = payment.OrderId;
        BranchId = payment.BranchId;
        Amount = amount;
        Currency = payment.Currency;
        Mode = mode;
        Reason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        ReturnRequestId = returnRequestId;
        ActorId = by;
        ActorName = byName;
        RequestedAt = now;
    }

    public Guid PaymentId { get; private set; }

    public Guid OrderId { get; private set; }

    public Guid BranchId { get; private set; }

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "BDT";

    public RefundMode Mode { get; private set; }

    public RefundState Status { get; private set; } = RefundState.Pending;

    public string? Reason { get; private set; }

    /// <summary>The transfer or receipt reference (manual) or the gateway's refund id.</summary>
    public string? Reference { get; private set; }

    public Guid? ReturnRequestId { get; private set; }

    public Guid? CreditNoteId { get; private set; }

    public string? FailureReason { get; private set; }

    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public void Complete(string? reference, DateTimeOffset now)
    {
        if (Status != RefundState.Pending)
        {
            throw new InvalidOperationException("Only a pending refund can be completed.");
        }

        Status = RefundState.Completed;
        Reference = string.IsNullOrWhiteSpace(reference) ? Reference : reference.Trim();
        CompletedAt = now;
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        if (Status != RefundState.Pending)
        {
            throw new InvalidOperationException("Only a pending refund can fail.");
        }

        Status = RefundState.Failed;
        FailureReason = reason.Trim();
        CompletedAt = now;
    }

    public void LinkCreditNote(Guid creditNoteId) => CreditNoteId = creditNoteId;

    /// <summary>The gateway's id of a refund it has accepted but not finished.</summary>
    public void SetReference(string? reference) => Reference = string.IsNullOrWhiteSpace(reference) ? Reference : reference.Trim();
}

/// <summary>
/// A payment session of the sandbox gateway: what the customer did on the sandbox pay page. The sandbox adapter answers server-side verification from this row,
/// the way a real gateway answers from its own records. Not used by any real provider.
/// </summary>
public sealed class SandboxGatewaySession : Entity, INotAudited
{
    private SandboxGatewaySession()
    {
    }

    public SandboxGatewaySession(string sessionId, Guid paymentId, string orderNumber, decimal amount, string currency, DateTimeOffset expiresAt)
    {
        SessionId = sessionId;
        PaymentId = paymentId;
        OrderNumber = orderNumber;
        Amount = amount;
        Currency = currency;
        ExpiresAt = expiresAt;
    }

    public string SessionId { get; private set; } = string.Empty;

    public Guid PaymentId { get; private set; }

    public string OrderNumber { get; private set; } = string.Empty;

    public decimal Amount { get; private set; }

    public string Currency { get; private set; } = "BDT";

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>1 = success, 2 = failed, 3 = cancelled; null while the customer has not decided.</summary>
    public int? Outcome { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public bool Decide(int outcome, DateTimeOffset now)
    {
        if (Outcome is not null)
        {
            return false;
        }

        Outcome = outcome;
        CompletedAt = now;
        return true;
    }
}
