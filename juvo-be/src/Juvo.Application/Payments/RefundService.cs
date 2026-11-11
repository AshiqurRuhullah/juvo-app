using Juvo.Application.Abstractions;
using Juvo.Application.Billing;
using Juvo.Application.Orders;
using Juvo.Application.Ums;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Domain.Returns;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Payments;

/// <summary>
/// Refunds (module I): real <see cref="Refund"/> records that go back through the gateway (sandbox now) or are completed by staff with a reference. The money is reserved on the
/// payment first (the row version stops two simultaneous refunds from giving back more than was paid), the gateway is called after that, and the refund then ends Completed
/// (payment status, order payment track and a gap-free credit note follow) or Failed (the money becomes refundable again). Refunds from returns use the same rules.
/// </summary>
public sealed class RefundService(
    IApplicationDbContext db, IUnitOfWork uow, IPaymentGatewayRegistry gateways, InvoiceService invoices, OrderWorkflow workflow, OrderAccess access, TimeProvider clock)
{
    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error NotFound() => Error.NotFound(ErrorCodes.Payment.NotFound, "Payment not found.");

    private static readonly PaymentStatus[] MoneyStates =
        [PaymentStatus.Paid, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded, PaymentStatus.CollectedByCourier, PaymentStatus.Settled];

    // ---- Shared steps (no save) ----------------------------------------------------------------------------

    /// <summary>Finishes a reserved refund: the payment counts it, the credit note is issued and the order's payment track follows.</summary>
    private async Task CompleteAsync(Refund refund, Payment payment, Order order, string? reference, bool syncOrder, Who who, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        payment.CompleteRefund(refund.Amount, now, who.Id, who.Name, $"Refund of BDT {refund.Amount:0.##}");
        refund.Complete(reference, now);
        await invoices.IssueCreditNoteAsync(refund, order, who.Id, who.Name, cancellationToken);
        if (syncOrder)
        {
            await SyncOrderAsync(order, payment, who, cancellationToken);
        }
    }

    private void Fail(Refund refund, Payment payment, string reason, Who who)
    {
        var now = clock.GetUtcNow();
        payment.FailRefund(refund.Amount, now, who.Id, who.Name, reason);
        refund.Fail(reason, now);
    }

    /// <summary>Moves the order's payment track to PartiallyRefunded or Refunded from what all its payments gave back. A track that cannot move (a cancelled order) is left alone.</summary>
    private async Task SyncOrderAsync(Order order, Payment changed, Who who, CancellationToken cancellationToken)
    {
        // The payment that just changed is in memory (not saved yet): use its values, and the saved values of the order's other payments.
        var changedId = changed.Id;
        var saved = await db.Payments.IgnoreQueryFilters(["BranchScope"]).Where(p => p.OrderId == order.Id && p.Id != changedId && MoneyStates.Contains(p.Status)).Select(p => new { p.Amount, p.RefundedAmount }).ToListAsync(cancellationToken);
        var rows = saved.Concat(MoneyStates.Contains(changed.Status) ? [new { changed.Amount, changed.RefundedAmount }] : []).ToList();
        var refunded = rows.Sum(r => r.RefundedAmount);
        if (rows.Count == 0 || refunded <= 0)
        {
            return;
        }

        var target = refunded >= rows.Sum(r => r.Amount) ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        if (order.PaymentStatus != target && OrderStateMachine.CanMove(order.PaymentStatus, target))
        {
            workflow.ChangePayment(order, target, "Refund completed", who);
        }
    }

    private async Task<Result> CallGatewayAsync(Refund refund, Payment payment, Order order, bool syncOrder, Who who, CancellationToken cancellationToken)
    {
        var gateway = payment.Provider is null ? null : gateways.Find(payment.Provider);
        if (gateway is not { IsConfigured: true, SupportsRefund: true } || payment.ProviderReference is null)
        {
            return Error.Conflict(ErrorCodes.Payment.NotConfigured, "This payment cannot be refunded through its gateway. Refund it manually.");
        }

        var answered = await gateway.RefundAsync(new GatewayRefundRequest(payment.ProviderReference, payment.ProviderTransactionId, refund.Amount, payment.Currency, refund.Id.ToString("N")), cancellationToken);
        if (answered.IsFailure)
        {
            Fail(refund, payment, answered.Error!.Message, who);
            return Error.Conflict(ErrorCodes.Payment.RefundFailed, answered.Error.Message);
        }

        var outcome = answered.Value;
        switch (outcome.Kind)
        {
            case GatewayRefundKind.Completed:
                await CompleteAsync(refund, payment, order, outcome.ProviderRefundId, syncOrder, who, cancellationToken);
                break;
            case GatewayRefundKind.Failed:
                Fail(refund, payment, outcome.Detail ?? "The gateway refused the refund", who);
                return Error.Conflict(ErrorCodes.Payment.RefundFailed, outcome.Detail ?? "The gateway refused the refund.");
            default:
                refund.SetReference(outcome.ProviderRefundId); // the gateway finishes later: staff complete it from the refund list
                break;
        }

        return Result.Success();
    }

    // ---- From a return -------------------------------------------------------------------------------------

    /// <summary>The payments of an order that can give money back. A cash-on-delivery order gets its payment record the first time it is needed.</summary>
    private async Task<List<Payment>> PaymentsOfAsync(Order order, CancellationToken cancellationToken)
    {
        var payments = await db.Payments.IgnoreQueryFilters(["BranchScope"]).Include(p => p.Events).Where(p => p.OrderId == order.Id).OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        PaymentRecords.EnsureCod(db, order, payments, clock.GetUtcNow());
        return payments;
    }

    /// <summary>
    /// The money of a refunded return goes back: from the payments of the order, through the gateway for an online payment when the staff chose "original payment", otherwise
    /// as a manual refund with the staff reference. Runs inside the return's transaction; the caller saves. The order's payment track is left to the return workflow.
    /// </summary>
    public async Task<Result> RecordForReturnAsync(Order order, ReturnRequest ret, RefundMethod method, string reference, Who who, CancellationToken cancellationToken)
    {
        var payments = (await PaymentsOfAsync(order, cancellationToken)).Where(p => p.HoldsMoney && p.Refundable > 0).ToList();
        var remaining = ret.RefundTotal;
        foreach (var payment in payments)
        {
            if (remaining <= 0)
            {
                break;
            }

            var take = Math.Min(remaining, payment.Refundable);
            var useGateway = method == RefundMethod.OriginalPayment && payment.Provider is not null && gateways.Find(payment.Provider) is { IsConfigured: true, SupportsRefund: true };
            var refund = new Refund(payment, take, useGateway ? RefundMode.Gateway : RefundMode.Manual, $"Return {ret.Number}", ret.Id, who.Id, who.Name, clock.GetUtcNow());
            payment.ReserveRefund(take, clock.GetUtcNow(), who.Id, who.Name, $"Refund for return {ret.Number}");
            db.Refunds.Add(refund);
            if (useGateway)
            {
                var done = await CallGatewayAsync(refund, payment, order, false, who, cancellationToken);
                if (done.IsFailure)
                {
                    return done;
                }
            }
            else
            {
                await CompleteAsync(refund, payment, order, reference, false, who, cancellationToken);
            }

            remaining -= take;
        }

        return remaining > 0
            ? Error.Conflict(ErrorCodes.Payment.RefundExceedsPaid, "The payments of this order hold less than the refund. Check the payment records of the order.")
            : Result.Success();
    }

    // ---- Staff ---------------------------------------------------------------------------------------------

    private static RefundDto ToDto(Refund r, string orderNumber, string? creditNoteNumber) =>
        new(r.Id, r.PaymentId, r.OrderId, orderNumber, r.Amount, r.Currency, r.Mode, r.Status, r.Reason, r.Reference, r.ReturnRequestId, creditNoteNumber, r.FailureReason, r.ActorName, r.RequestedAt, r.CompletedAt);

    private async Task<RefundDto> ToDtoAsync(Refund r, CancellationToken cancellationToken)
    {
        var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => o.Id == r.OrderId).Select(o => o.Number).FirstAsync(cancellationToken);
        var note = r.CreditNoteId is { } id ? await db.CreditNotes.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(c => c.Id == id).Select(c => c.Number).FirstOrDefaultAsync(cancellationToken) : null;
        return ToDto(r, order, note);
    }

    public async Task<Result<PagedResult<RefundDto>>> ListAsync(PageRequest request, RefundListQuery filter, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Payments.RefundView);
        var query = from r in db.Refunds.AsNoTracking()
                    join o in db.Orders.AsNoTracking() on r.OrderId equals o.Id
                    select new { Refund = r, o.Number };
        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.Refund.BranchId));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(x => x.Refund.Status == status);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x => x.Refund.BranchId == branchId);
        }

        if (filter.OrderId is { } orderId)
        {
            query = query.Where(x => x.Refund.OrderId == orderId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(x => x.Refund.RequestedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(x => x.Refund.RequestedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(x => x.Number.Contains(term) || (x.Refund.Reference != null && x.Refund.Reference.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.Refund.RequestedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var noteIds = rows.Where(x => x.Refund.CreditNoteId != null).Select(x => x.Refund.CreditNoteId!.Value).ToList();
        var notes = await db.CreditNotes.AsNoTracking().Where(c => noteIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Number, cancellationToken);
        return new PagedResult<RefundDto>([.. rows.Select(x => ToDto(x.Refund, x.Number, x.Refund.CreditNoteId is { } id ? notes.GetValueOrDefault(id) : null))], total, page.Page, page.PageSize);
    }

    /// <summary>Starts a refund of a payment. A gateway refund is sent at once; a manual one stays Pending until staff complete it with the transfer reference.</summary>
    public async Task<Result<RefundDto>> CreateAsync(Guid paymentId, CreateRefundRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        if (!Enum.IsDefined(request.Mode))
        {
            return Invalid("mode", "Choose Gateway or Manual.");
        }

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500)
        {
            return Invalid("reason", "Write the reason for the refund (up to 500 characters).");
        }

        if (request.Amount <= 0 || decimal.Round(request.Amount, 2) != request.Amount)
        {
            return Invalid("amount", "Enter an amount above zero with at most 2 decimals.");
        }

        var who = access.WhoIs(actor);
        Guid refundId;
        try
        {
            var reserved = await uow.ExecuteInTransactionAsync<Result<Guid>>(async ct =>
            {
                var payment = await db.Payments.Include(p => p.Events).FirstOrDefaultAsync(p => p.Id == paymentId, ct);
                if (payment is null)
                {
                    return NotFound();
                }

                var refusal = await access.CheckAsync(actor, payment.BranchId, Permissions.Payments.RefundCreate, ct);
                if (refusal is not null)
                {
                    return refusal is { Type: ErrorType.NotFound } ? NotFound() : refusal;
                }

                if (!payment.HoldsMoney)
                {
                    return Error.Conflict(ErrorCodes.Payment.NothingPaid, "This payment has no received money to refund.");
                }

                if (request.Amount > payment.Refundable)
                {
                    return Error.Conflict(ErrorCodes.Payment.RefundExceedsPaid, $"At most BDT {payment.Refundable:0.##} can still be refunded on this payment.");
                }

                if (request.Mode == RefundMode.Gateway && gateways.Find(payment.Provider ?? string.Empty) is not { IsConfigured: true, SupportsRefund: true })
                {
                    return Error.Conflict(ErrorCodes.Payment.NotConfigured, "This payment has no gateway to refund through. Use a manual refund.");
                }

                var now = clock.GetUtcNow();
                payment.ReserveRefund(request.Amount, now, who.Id, who.Name, reason);
                var refund = new Refund(payment, request.Amount, request.Mode, reason, null, who.Id, who.Name, now);
                db.Refunds.Add(refund);
                await db.SaveChangesAsync(ct);
                return refund.Id;
            }, cancellationToken);
            if (reserved.IsFailure)
            {
                return reserved.Error!;
            }

            refundId = reserved.Value;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Payment.ConcurrentUpdate, "The payment was changed at the same moment (perhaps another refund). Check the refunds and try again.");
        }

        if (request.Mode == RefundMode.Gateway)
        {
            var sent = await FinishGatewayAsync(refundId, who, cancellationToken);
            if (sent.IsFailure)
            {
                return sent.Error!;
            }
        }

        var saved = await db.Refunds.IgnoreQueryFilters(["BranchScope"]).FirstAsync(r => r.Id == refundId, cancellationToken);
        return await ToDtoAsync(saved, cancellationToken);
    }

    private async Task<Result> FinishGatewayAsync(Guid refundId, Who who, CancellationToken cancellationToken)
    {
        try
        {
            return await uow.ExecuteInTransactionAsync<Result>(async ct =>
            {
                var refund = await db.Refunds.IgnoreQueryFilters(["BranchScope"]).FirstAsync(r => r.Id == refundId, ct);
                var payment = await db.Payments.IgnoreQueryFilters(["BranchScope"]).Include(p => p.Events).FirstAsync(p => p.Id == refund.PaymentId, ct);
                var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).FirstAsync(o => o.Id == refund.OrderId, ct);
                var done = await CallGatewayAsync(refund, payment, order, true, who, ct);
                await db.SaveChangesAsync(ct); // a failed refund is saved too (Failed, money refundable again)
                return done;
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Payment.ConcurrentUpdate, "The payment was changed while the refund was sent. Check the refund list.");
        }
    }

    /// <summary>Staff finish a Pending refund: the transfer was made (or the gateway confirmed it).</summary>
    public async Task<Result<RefundDto>> CompleteAsync(Guid refundId, CompleteRefundRequest request, CancellationToken cancellationToken) =>
        await FinishAsync(refundId, complete: true, request.Reference, null, cancellationToken);

    public async Task<Result<RefundDto>> FailAsync(Guid refundId, FailRefundRequest request, CancellationToken cancellationToken) =>
        await FinishAsync(refundId, complete: false, null, request.Reason, cancellationToken);

    private async Task<Result<RefundDto>> FinishAsync(Guid refundId, bool complete, string? reference, string? reason, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        reference = reference?.Trim();
        reason = reason?.Trim();
        if (complete && (string.IsNullOrEmpty(reference) || reference.Length > 120))
        {
            return Invalid("reference", "Write the transfer or receipt reference (up to 120 characters).");
        }

        if (!complete && (string.IsNullOrEmpty(reason) || reason.Length > 500))
        {
            return Invalid("reason", "Say why the refund failed (up to 500 characters).");
        }

        var who = access.WhoIs(actor);
        try
        {
            return await uow.ExecuteInTransactionAsync<Result<RefundDto>>(async ct =>
            {
                var refund = await db.Refunds.FirstOrDefaultAsync(r => r.Id == refundId, ct);
                if (refund is null)
                {
                    return Error.NotFound(ErrorCodes.Payment.RefundNotFound, "Refund not found.");
                }

                var refusal = await access.CheckAsync(actor, refund.BranchId, Permissions.Payments.RefundApprove, ct);
                if (refusal is not null)
                {
                    return refusal is { Type: ErrorType.NotFound } ? Error.NotFound(ErrorCodes.Payment.RefundNotFound, "Refund not found.") : refusal;
                }

                if (refund.Status != RefundState.Pending)
                {
                    return Error.Conflict(ErrorCodes.Payment.RefundNotPending, "This refund is already finished.");
                }

                var payment = await db.Payments.Include(p => p.Events).FirstAsync(p => p.Id == refund.PaymentId, ct);
                var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).FirstAsync(o => o.Id == refund.OrderId, ct);
                if (complete)
                {
                    await CompleteAsync(refund, payment, order, reference, refund.ReturnRequestId is null, who, ct);
                }
                else
                {
                    Fail(refund, payment, reason!, who);
                }

                await db.SaveChangesAsync(ct);
                return await ToDtoAsync(refund, ct);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Payment.ConcurrentUpdate, "The refund or payment was changed at the same moment. Try again.");
        }
    }
}
