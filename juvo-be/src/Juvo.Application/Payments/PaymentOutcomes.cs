using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Payments;

/// <summary>
/// What happens to a payment AND to its order when a payment ends one way or another (module I). Every path to Paid goes through here: the gateway webhook, the
/// browser-return check and the staff approval of a manual transfer, so the rules cannot differ. The methods change the aggregates but do not save: the caller
/// saves inside its transaction. Nothing here ever marks a payment Paid without the caller having verified it with the gateway (or a staff member having approved it).
/// </summary>
public sealed class PaymentOutcomes(IApplicationDbContext db, OrderWorkflow workflow, PaymentSettingsService settings, TimeProvider clock)
{
    /// <summary>The order with its lines, ignoring the branch filter (callers prove access themselves: a token, the customer id or a branch permission).</summary>
    public Task<Order?> LoadOrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    /// <summary>The payment became Paid. Idempotent. When the order is still open its payment track moves to Paid and (setting) the order is confirmed.</summary>
    public async Task<Result> PaidAsync(Payment payment, Order order, string? transactionId, string? providerEventId, string detail, Who who, CancellationToken cancellationToken)
    {
        if (payment.Status == PaymentStatus.Paid)
        {
            return Result.Success();
        }

        var now = clock.GetUtcNow();
        if (payment.Status == PaymentStatus.Failed)
        {
            payment.MoveTo(PaymentStatus.Pending, "Reopened", "The gateway reported success after a failure", now, who.Id, who.Name);
        }

        if (!OrderStateMachine.CanMove(payment.Status, PaymentStatus.Paid))
        {
            return Error.Conflict(ErrorCodes.Payment.InvalidTransition, $"A payment that is {payment.Status} cannot become Paid.");
        }

        payment.ConfirmPaid(transactionId, now, providerEventId, detail, who.Id, who.Name);

        if (order.PaymentStatus == PaymentStatus.Failed)
        {
            workflow.ChangePayment(order, PaymentStatus.Pending, "Payment retried", who);
        }

        if (order.PaymentStatus is PaymentStatus.Pending or PaymentStatus.UnderReview)
        {
            var moved = workflow.ChangePayment(order, PaymentStatus.Paid, detail, who);
            if (moved.IsFailure)
            {
                return moved;
            }

            var config = await settings.ReadAsync(cancellationToken);
            if (config.AutoConfirmPaid && order.Status == OrderStatus.Placed && !order.IsHeld)
            {
                workflow.Confirm(order, false, Who.System); // a refusal (for example a held order) just leaves it for staff
            }
        }
        else
        {
            order.AddNote(NoteKind.Internal, $"A payment of BDT {payment.Amount:0.##} ({payment.Method}) was received while the order payment is {order.PaymentStatus}. A refund may be due.", who.Id, who.Name, now);
        }

        return Result.Success();
    }

    /// <summary>The gateway reported a failure. The order's payment track moves to Failed so the customer can try again before the hold ends.</summary>
    public Result Failed(Payment payment, Order order, string reason, string? providerEventId, Who who)
    {
        if (payment.Status == PaymentStatus.Failed)
        {
            return Result.Success();
        }

        if (payment.Status != PaymentStatus.Pending)
        {
            return Error.Conflict(ErrorCodes.Payment.InvalidTransition, $"A payment that is {payment.Status} cannot fail.");
        }

        var now = clock.GetUtcNow();
        payment.MoveTo(PaymentStatus.Failed, "Failed", reason, now, who.Id, who.Name, providerEventId);
        if (order.PaymentStatus == PaymentStatus.Pending)
        {
            workflow.ChangePayment(order, PaymentStatus.Failed, reason, who);
        }

        return Result.Success();
    }

    /// <summary>The customer backed out at the gateway (or the session expired). The order stays Pending so the customer can start again until the hold ends.</summary>
    public Result Cancelled(Payment payment, string reason, string? providerEventId, Who who)
    {
        if (payment.Status == PaymentStatus.Cancelled)
        {
            return Result.Success();
        }

        if (payment.Status != PaymentStatus.Pending)
        {
            return Error.Conflict(ErrorCodes.Payment.InvalidTransition, $"A payment that is {payment.Status} cannot be cancelled.");
        }

        payment.MoveTo(PaymentStatus.Cancelled, "Cancelled", reason, clock.GetUtcNow(), who.Id, who.Name, providerEventId);
        return Result.Success();
    }

    /// <summary>Staff found the transfer reference wrong or the money missing. The payment is Rejected and so is the order's payment track, which ends the order: it is cancelled and its stock released.</summary>
    public async Task<Result> RejectedAsync(Payment payment, Order order, string reason, Who who, CancellationToken cancellationToken)
    {
        if (payment.Status != PaymentStatus.UnderReview)
        {
            return Error.Conflict(ErrorCodes.Payment.NotUnderReview, "Only a payment under review can be rejected.");
        }

        var now = clock.GetUtcNow();
        payment.MoveTo(PaymentStatus.Rejected, "Rejected", reason, now, who.Id, who.Name);
        if (order.PaymentStatus == PaymentStatus.UnderReview)
        {
            workflow.ChangePayment(order, PaymentStatus.Rejected, reason, who);
        }

        if (OrderStateMachine.CanCancel(order.Status))
        {
            await workflow.CancelAsync(order, $"Payment rejected: {reason}", who, cancellationToken);
        }

        return Result.Success();
    }
}
