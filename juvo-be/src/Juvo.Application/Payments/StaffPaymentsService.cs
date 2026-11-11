using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Application.Ums;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Payments;

/// <summary>
/// The shop's side of payments (module I): the payment list and detail, the queue of manual transfers to approve or reject, advances for large cash-on-delivery orders, cash
/// collection and settlement, and the ledger. Reads are limited to the branches where the actor holds the permission; every change is proven in the payment's own branch
/// (another branch's payment looks like "not found" and is audit-logged).
/// </summary>
public sealed class StaffPaymentsService(IApplicationDbContext db, IUnitOfWork uow, OrderAccess access, PaymentOutcomes outcomes, OrderWorkflow workflow, IFileStorage storage, TimeProvider clock)
{
    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error NotFound() => Error.NotFound(ErrorCodes.Payment.NotFound, "Payment not found.");

    private static readonly PaymentStatus[] MoneyStates =
        [PaymentStatus.Paid, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded, PaymentStatus.CollectedByCourier, PaymentStatus.Settled];

    private async Task<Error?> CheckAsync(Actor actor, Guid branchId, string permission, CancellationToken cancellationToken)
    {
        var refusal = await access.CheckAsync(actor, branchId, permission, cancellationToken);
        return refusal is { Type: ErrorType.NotFound } ? NotFound() : refusal;
    }

    // ---- Reading -------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<PaymentListItemDto>>> ListAsync(PageRequest request, PaymentListQuery filter, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Payments.PaymentView);
        var query = from p in db.Payments.AsNoTracking() join b in db.Branches.AsNoTracking() on p.BranchId equals b.Id select new { Payment = p, BranchCode = b.Code };
        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.Payment.BranchId));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(x => x.Payment.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(filter.Method))
        {
            var method = filter.Method.Trim().ToUpperInvariant();
            query = query.Where(x => x.Payment.Method == method);
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(x => x.Payment.Kind == kind);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x => x.Payment.BranchId == branchId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(x => x.Payment.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(x => x.Payment.CreatedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(x => x.Payment.OrderNumber.Contains(term) || (x.Payment.SubmittedReference != null && x.Payment.SubmittedReference.Contains(term)) ||
                                     (x.Payment.ProviderTransactionId != null && x.Payment.ProviderTransactionId.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.Payment.CreatedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<PaymentListItemDto>(
            [.. rows.Select(x => new PaymentListItemDto(x.Payment.Id, x.Payment.OrderId, x.Payment.OrderNumber, x.Payment.BranchId, x.BranchCode, x.Payment.Kind, x.Payment.Method, x.Payment.Amount, x.Payment.Currency,
                x.Payment.Status, x.Payment.RefundedAmount, x.Payment.SubmittedReference, x.Payment.ProofPath != null, x.Payment.CreatedAt, x.Payment.PaidAt))],
            total, page.Page, page.PageSize);
    }

    private async Task<PaymentDetailDto> DetailAsync(Payment p, CancellationToken cancellationToken)
    {
        var branchCode = await db.Branches.AsNoTracking().Where(b => b.Id == p.BranchId).Select(b => b.Code).FirstAsync(cancellationToken);
        var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => o.Id == p.OrderId).Select(o => new { o.Status, o.PaymentStatus }).FirstAsync(cancellationToken);
        var refunds = await db.Refunds.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(r => r.PaymentId == p.Id).OrderBy(r => r.RequestedAt).ToListAsync(cancellationToken);
        var noteIds = refunds.Where(r => r.CreditNoteId != null).Select(r => r.CreditNoteId!.Value).ToList();
        var notes = await db.CreditNotes.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(c => noteIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, cancellationToken);
        var actions = new List<string>();
        if (p.Status == PaymentStatus.UnderReview)
        {
            actions.AddRange(["approve", "reject"]);
        }

        if (p.HoldsMoney && p.Refundable > 0)
        {
            actions.Add("refund");
        }

        if (p.Kind == PaymentKind.Cod && p.Status == PaymentStatus.CollectedByCourier)
        {
            actions.Add("settle");
        }

        return new PaymentDetailDto(p.Id, p.OrderId, p.OrderNumber, p.BranchId, branchCode, p.Kind, p.Method, p.Provider, p.Amount, p.Currency, p.Status, p.RefundedAmount, p.RefundPendingAmount, p.Refundable,
            p.ProviderTransactionId, p.SubmittedReference, p.PayerName, p.ProofPath is null ? null : storage.GetPublicUrl(p.ProofPath), p.ReceivedReference, p.FailureReason, p.CreatedAt, p.PaidAt, p.SettledAt,
            order.Status, order.PaymentStatus,
            [.. p.Events.OrderBy(e => e.At).ThenBy(e => e.CreatedAt).Select(e => new PaymentEventDto(e.Type, e.FromStatus, e.ToStatus, e.Amount, e.Detail, e.ActorName, e.At))],
            [.. refunds.Select(r => new RefundDto(r.Id, r.PaymentId, r.OrderId, p.OrderNumber, r.Amount, r.Currency, r.Mode, r.Status, r.Reason, r.Reference, r.ReturnRequestId,
                r.CreditNoteId is { } id ? notes.GetValueOrDefault(id) : null, r.FailureReason, r.ActorName, r.RequestedAt, r.CompletedAt))],
            actions);
    }

    public async Task<Result<PaymentDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var payment = await db.Payments.AsNoTracking().Include(p => p.Events).FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (payment is null)
        {
            return NotFound();
        }

        return await CheckAsync(actor, payment.BranchId, Permissions.Payments.PaymentView, cancellationToken) is { } refusal ? refusal : await DetailAsync(payment, cancellationToken);
    }

    /// <summary>The payments of one order (for the order screen).</summary>
    public async Task<Result<IReadOnlyList<PaymentListItemDto>>> ForOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Payments.PaymentView);
        var rows = await (from p in db.Payments.AsNoTracking() join b in db.Branches.AsNoTracking() on p.BranchId equals b.Id where p.OrderId == orderId select new { Payment = p, BranchCode = b.Code })
            .OrderBy(x => x.Payment.CreatedAt).ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<PaymentListItemDto>>([.. rows.Where(x => visible is null || visible.Contains(x.Payment.BranchId)).Select(x =>
            new PaymentListItemDto(x.Payment.Id, x.Payment.OrderId, x.Payment.OrderNumber, x.Payment.BranchId, x.BranchCode, x.Payment.Kind, x.Payment.Method, x.Payment.Amount, x.Payment.Currency, x.Payment.Status,
                x.Payment.RefundedAmount, x.Payment.SubmittedReference, x.Payment.ProofPath != null, x.Payment.CreatedAt, x.Payment.PaidAt))]);
    }

    // ---- Manual transfers ----------------------------------------------------------------------------------

    private async Task<Result<PaymentDetailDto>> RunAsync(Guid id, string permission, Func<Payment, Order, Who, CancellationToken, Task<Result>> action, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<PaymentDetailDto>>(async ct =>
            {
                var payment = await db.Payments.Include(p => p.Events).FirstOrDefaultAsync(p => p.Id == id, ct);
                if (payment is null)
                {
                    return NotFound();
                }

                if (await CheckAsync(actor, payment.BranchId, permission, ct) is { } refusal)
                {
                    return refusal;
                }

                var order = await outcomes.LoadOrderAsync(payment.OrderId, ct) ?? throw new InvalidOperationException("The order of a payment is missing.");
                var done = await action(payment, order, access.WhoIs(actor), ct);
                if (done.IsFailure)
                {
                    throw new PaymentRejected(done.Error!);
                }

                await db.SaveChangesAsync(ct);
                return await DetailAsync(payment, ct);
            }, cancellationToken);
        }
        catch (PaymentRejected rejected)
        {
            return rejected.Error;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Payment.ConcurrentUpdate, "The payment or order was changed at the same moment. Reload and try again.");
        }
    }

    /// <summary>Staff checked the transfer and the money is there: the payment is Paid and the order may go on.</summary>
    public Task<Result<PaymentDetailDto>> ApproveAsync(Guid id, ReviewDecisionRequest request, CancellationToken cancellationToken) =>
        RunAsync(id, Permissions.Payments.PaymentVerify, async (p, order, who, ct) =>
        {
            if (p.Status != PaymentStatus.UnderReview)
            {
                return Error.Conflict(ErrorCodes.Payment.NotUnderReview, "Only a payment under review can be approved.");
            }

            var note = request.Note?.Trim();
            return await outcomes.PaidAsync(p, order, null, null, string.IsNullOrEmpty(note) ? "Approved by staff" : $"Approved by staff: {note}", who, ct);
        }, cancellationToken);

    public Task<Result<PaymentDetailDto>> RejectAsync(Guid id, RejectPaymentRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        return string.IsNullOrEmpty(reason) || reason.Length > 500
            ? Task.FromResult<Result<PaymentDetailDto>>(Invalid("reason", "Say why the payment is rejected (up to 500 characters)."))
            : RunAsync(id, Permissions.Payments.PaymentVerify, (p, order, who, ct) => outcomes.RejectedAsync(p, order, reason, who, ct), cancellationToken);
    }

    // ---- Cash on delivery ----------------------------------------------------------------------------------

    private async Task<Result<PaymentDetailDto>> RunForOrderAsync(Guid orderId, string permission, Func<Order, List<Payment>, Who, CancellationToken, Task<Result<Payment>>> action, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<PaymentDetailDto>>(async ct =>
            {
                var order = await db.Orders.Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
                if (order is null)
                {
                    return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
                }

                var refusal = await access.CheckAsync(actor, order.BranchId, permission, ct);
                if (refusal is not null)
                {
                    return refusal;
                }

                var payments = await db.Payments.Include(p => p.Events).Where(p => p.OrderId == order.Id).OrderBy(p => p.CreatedAt).ToListAsync(ct);
                var done = await action(order, payments, access.WhoIs(actor), ct);
                if (done.IsFailure)
                {
                    throw new PaymentRejected(done.Error!);
                }

                await db.SaveChangesAsync(ct);
                return await DetailAsync(done.Value, ct);
            }, cancellationToken);
        }
        catch (PaymentRejected rejected)
        {
            return rejected.Error;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Payment.ConcurrentUpdate, "The payment or order was changed at the same moment. Reload and try again.");
        }
    }

    /// <summary>
    /// Staff received an advance for a large cash-on-delivery order (BR-D1). It is a payment record (Paid) and counts toward the required advance, which lets the order be
    /// confirmed. This replaces the temporary "advanceReceived" flag of the confirm call, which still works.
    /// </summary>
    public Task<Result<PaymentDetailDto>> RecordAdvanceAsync(Guid orderId, RecordAdvanceRequest request, CancellationToken cancellationToken) =>
        RunForOrderAsync(orderId, Permissions.Payments.PaymentRecord, (order, payments, who, ct) =>
        {
            var method = string.IsNullOrWhiteSpace(request.Method) ? "CASH" : request.Method.Trim().ToUpperInvariant();
            if (method.Length > 30 || !method.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or ' '))
            {
                return Task.FromResult<Result<Payment>>(Invalid("method", "Use up to 30 letters or digits, for example CASH, BKASH or BANK."));
            }

            if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
            {
                return Task.FromResult<Result<Payment>>(Invalid("amount", "Enter an amount above zero with at most 2 decimals."));
            }

            if (order.PaymentMethod != PaymentMethodCatalog.Cod || order.AdvanceRequired <= 0)
            {
                return Task.FromResult<Result<Payment>>(Error.Conflict(ErrorCodes.Payment.NotCod, "An advance is only recorded for a cash-on-delivery order that needs one."));
            }

            if (order.Status is not (OrderStatus.Placed or OrderStatus.Confirmed))
            {
                return Task.FromResult<Result<Payment>>(Error.Conflict(ErrorCodes.Payment.OrderNotPayable, "An advance can be recorded before the order is packed."));
            }

            if (order.AdvancePaid + request.Amount > order.GrandTotal)
            {
                return Task.FromResult<Result<Payment>>(Error.Conflict(ErrorCodes.Payment.AmountMismatch, "The advances would be more than the order total."));
            }

            var now = clock.GetUtcNow();
            var reference = request.Reference?.Trim();
            var payment = Payment.Start(new PaymentInit(order.Id, order.Number, order.BranchId, PaymentKind.Advance, method, null, request.Amount, order.Currency, null, null, null, null, null, null, string.IsNullOrEmpty(reference) ? null : reference),
                now, who.Id, who.Name);
            payment.ConfirmPaid(null, now, null, "Received by staff", who.Id, who.Name);
            db.Payments.Add(payment);
            order.RecordAdvance(request.Amount, now, who.Id, who.Name, reference);
            return Task.FromResult<Result<Payment>>(payment);
        }, cancellationToken);

    /// <summary>Cash was collected from the customer by the courier. Usually the order's payment track moves on its own when the order is delivered; this records the amount and the courier receipt reference.</summary>
    public Task<Result<PaymentDetailDto>> CollectCodAsync(Guid orderId, CodCollectRequest request, CancellationToken cancellationToken) =>
        RunForOrderAsync(orderId, Permissions.Payments.PaymentRecord, (order, payments, who, ct) =>
        {
            if (order.PaymentMethod != PaymentMethodCatalog.Cod)
            {
                return Task.FromResult<Result<Payment>>(Error.Conflict(ErrorCodes.Payment.NotCod, "This is not a cash-on-delivery order."));
            }

            var expected = order.GrandTotal - order.AdvancePaid;
            if (expected <= 0 || order.PaymentStatus is not (PaymentStatus.Pending or PaymentStatus.CollectedByCourier))
            {
                return Task.FromResult<Result<Payment>>(Error.Conflict(ErrorCodes.Payment.NothingToCollect, "There is no cash to collect on this order."));
            }

            if (order.Status is not (OrderStatus.OutForDelivery or OrderStatus.Delivered))
            {
                return Task.FromResult<Result<Payment>>(Error.Conflict(ErrorCodes.Payment.NothingToCollect, "Cash is collected when the order is out for delivery or delivered."));
            }

            if (request.Amount is { } amount && amount != expected)
            {
                return Task.FromResult<Result<Payment>>(Error.Conflict(ErrorCodes.Payment.AmountMismatch, $"The cash to collect is BDT {expected:0.##}."));
            }

            var now = clock.GetUtcNow();
            if (order.PaymentStatus == PaymentStatus.Pending)
            {
                var moved = workflow.ChangePayment(order, PaymentStatus.CollectedByCourier, "Cash collected by the courier", who);
                if (moved.IsFailure)
                {
                    return Task.FromResult<Result<Payment>>(moved.Error!);
                }
            }

            var cod = PaymentRecords.EnsureCod(db, order, payments, now) ?? throw new InvalidOperationException("The cash payment could not be recorded.");
            cod.RecordReceipt(request.Reference);
            cod.Note("Collected", $"Cash of BDT {expected:0.##} collected{(string.IsNullOrWhiteSpace(request.Reference) ? string.Empty : $", receipt {request.Reference!.Trim()}")}", now, who.Id, who.Name);
            return Task.FromResult<Result<Payment>>(cod);
        }, cancellationToken);

    /// <summary>The courier handed the collected cash over to the shop (settlement). The payment and the order's payment track become Settled.</summary>
    public Task<Result<PaymentDetailDto>> SettleCodAsync(Guid orderId, CodSettleRequest request, CancellationToken cancellationToken) =>
        RunForOrderAsync(orderId, Permissions.Cod.Settle, (order, payments, who, ct) =>
        {
            if (order.PaymentMethod != PaymentMethodCatalog.Cod)
            {
                return Task.FromResult<Result<Payment>>(Error.Conflict(ErrorCodes.Payment.NotCod, "This is not a cash-on-delivery order."));
            }

            if (order.PaymentStatus != PaymentStatus.CollectedByCourier)
            {
                return Task.FromResult<Result<Payment>>(Error.Conflict(ErrorCodes.Payment.InvalidTransition, $"Only cash that was collected can be settled; the payment is {order.PaymentStatus}."));
            }

            var reference = request.Reference?.Trim();
            if (string.IsNullOrEmpty(reference) || reference.Length > 120)
            {
                return Task.FromResult<Result<Payment>>(Invalid("reference", "Write the settlement or deposit reference (up to 120 characters)."));
            }

            var now = clock.GetUtcNow();
            var cod = PaymentRecords.EnsureCod(db, order, payments, now) ?? throw new InvalidOperationException("The cash payment could not be recorded.");
            if (cod.Status == PaymentStatus.CollectedByCourier)
            {
                cod.MoveTo(PaymentStatus.Settled, "Settled", $"Settlement {reference}", now, who.Id, who.Name);
            }

            cod.RecordReceipt(reference);
            var moved = workflow.ChangePayment(order, PaymentStatus.Settled, $"Settlement {reference}", who);
            return Task.FromResult<Result<Payment>>(moved.IsFailure ? moved.Error! : cod);
        }, cancellationToken);

    // ---- Ledger ----------------------------------------------------------------------------------------------

    /// <summary>Money in and out for a period, per method, with what is still waiting: cash not yet settled, transfers under review, refunds pending.</summary>
    public async Task<Result<LedgerDto>> LedgerAsync(LedgerQuery query, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var now = clock.GetUtcNow();
        var periodStart = query.From ?? new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var periodEnd = query.To ?? now;
        if (periodEnd <= periodStart)
        {
            return Invalid("to", "The end must be after the start.");
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Payments.LedgerView);
        var branch = query.BranchId;
        var payments = db.Payments.AsNoTracking().Where(p => visible == null || visible.Contains(p.BranchId));
        var refunds = db.Refunds.AsNoTracking().Where(r => visible == null || visible.Contains(r.BranchId));
        var orders = db.Orders.AsNoTracking().Where(o => visible == null || visible.Contains(o.BranchId));
        if (branch is { } b)
        {
            payments = payments.Where(p => p.BranchId == b);
            refunds = refunds.Where(r => r.BranchId == b);
            orders = orders.Where(o => o.BranchId == b);
        }

        var received = await payments.Where(p => p.Kind != PaymentKind.Cod && MoneyStates.Contains(p.Status) && p.PaidAt >= periodStart && p.PaidAt < periodEnd)
            .Select(p => new { p.Method, p.Amount, p.RefundedAmount }).ToListAsync(cancellationToken);
        var onlineRefunded = await (from r in refunds join p in payments on r.PaymentId equals p.Id where r.Status == RefundState.Completed && r.CompletedAt >= periodStart && r.CompletedAt < periodEnd && p.Kind != PaymentKind.Cod select r.Amount)
            .SumAsync(cancellationToken);
        var allRefunded = await refunds.Where(r => r.Status == RefundState.Completed && r.CompletedAt >= periodStart && r.CompletedAt < periodEnd).SumAsync(r => (decimal?)r.Amount, cancellationToken) ?? 0m;
        var cod = orders.Where(o => o.PaymentMethod == PaymentMethodCatalog.Cod && o.DeliveredAt >= periodStart && o.DeliveredAt < periodEnd &&
                                    (o.PaymentStatus == PaymentStatus.CollectedByCourier || o.PaymentStatus == PaymentStatus.Settled || o.PaymentStatus == PaymentStatus.PartiallyRefunded || o.PaymentStatus == PaymentStatus.Refunded));
        var codCollected = await cod.SumAsync(o => (decimal?)(o.GrandTotal - o.AdvancePaid), cancellationToken) ?? 0m;
        var codSettled = await cod.Where(o => o.PaymentStatus == PaymentStatus.Settled).SumAsync(o => (decimal?)(o.GrandTotal - o.AdvancePaid), cancellationToken) ?? 0m;
        var codOutstanding = await orders.Where(o => o.PaymentMethod == PaymentMethodCatalog.Cod && o.PaymentStatus == PaymentStatus.CollectedByCourier).SumAsync(o => (decimal?)(o.GrandTotal - o.AdvancePaid), cancellationToken) ?? 0m;
        var review = await payments.Where(p => p.Status == PaymentStatus.UnderReview).GroupBy(_ => 1).Select(g => new { Count = g.Count(), Sum = g.Sum(p => p.Amount) }).FirstOrDefaultAsync(cancellationToken);
        var pending = await refunds.Where(r => r.Status == RefundState.Pending).GroupBy(_ => 1).Select(g => new { Count = g.Count(), Sum = g.Sum(r => r.Amount) }).FirstOrDefaultAsync(cancellationToken);
        var onlineReceived = received.Sum(r => r.Amount);
        return new LedgerDto(periodStart, periodEnd, onlineReceived, onlineRefunded, codCollected, codSettled, codOutstanding, review?.Count ?? 0, review?.Sum ?? 0m, pending?.Count ?? 0, pending?.Sum ?? 0m,
            onlineReceived + codCollected - allRefunded,
            [.. received.GroupBy(r => r.Method).OrderBy(g => g.Key).Select(g => new LedgerMethodDto(g.Key, g.Count(), g.Sum(r => r.Amount), g.Sum(r => r.RefundedAmount)))]);
    }
}
