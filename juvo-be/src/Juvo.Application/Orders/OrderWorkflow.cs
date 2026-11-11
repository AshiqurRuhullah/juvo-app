using Juvo.Application.Abstractions;
using Juvo.Application.Ums;
using Juvo.Domain.Inventory;
using Juvo.Domain.Orders;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Orders;

/// <summary>Who performs a change: a staff member, the customer, or the system (null id).</summary>
public sealed record Who(Guid? Id, string? Name)
{
    public static readonly Who System = new(null, "System");

    public static Who Customer(Guid id, string? name) => new(id, string.IsNullOrWhiteSpace(name) ? "Customer" : name);
}

/// <summary>Branch and permission checks for the order services (stock and orders are per branch, so a permission is proven in the order's branch).</summary>
public sealed class OrderAccess(ActorContext actors, IApplicationDbContext db, IAccessDeniedAuditor auditor, ICurrentUser currentUser)
{
    public async Task<Actor?> ActorAsync(CancellationToken cancellationToken) => await actors.GetAsync(cancellationToken);

    public Who WhoIs(Actor actor) => new(actor.Id, currentUser.UserName ?? "Staff");

    /// <summary>Null when the actor holds the permission in the branch; otherwise the refusal (and an audit entry).</summary>
    public async Task<Error?> CheckAsync(Actor actor, Guid branchId, string permission, CancellationToken cancellationToken)
    {
        if (!actor.IsGlobal && !actor.Access.BranchPermissions.ContainsKey(branchId))
        {
            await auditor.RecordAsync("Order", $"Order access to branch {branchId} refused (not assigned).", cancellationToken);
            return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
        }

        if (!actor.Can(permission, branchId))
        {
            await auditor.RecordAsync("Order", $"Order action {permission} in branch {branchId} refused.", cancellationToken);
            return Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "You do not hold this permission in that branch.");
        }

        return null;
    }

    /// <summary>The branches where the actor holds the permission; null means every branch.</summary>
    public static List<Guid>? VisibleBranches(Actor actor, string permission) =>
        actor.IsGlobal ? null : [.. actor.Access.BranchPermissions.Where(kv => kv.Value.Contains(permission)).Select(kv => kv.Key)];

    public Task<bool> BranchSellsAsync(Guid branchId, CancellationToken cancellationToken) =>
        db.Branches.AnyAsync(b => b.Id == branchId && b.IsActive, cancellationToken);
}

/// <summary>
/// Every status change of an order and what it does to stock. The methods change the aggregate and stock but do not save: the caller saves
/// (inside its transaction). All checks are done before anything is changed, so a refused call changes nothing.
/// </summary>
public sealed class OrderWorkflow(IApplicationDbContext db, OrderStock stock, SettingsService settings, TimeProvider clock)
{
    private static Result Refuse(string code, string message) => Error.Conflict(code, message);

    private static Result Invalid(Order order, OrderStatus to) =>
        Refuse(ErrorCodes.Order.InvalidTransition, $"An order that is {order.Status} cannot become {to}.");

    // ---- Confirm ----------------------------------------------------------------------------------------

    public Result Confirm(Order order, bool advanceReceived, Who who)
    {
        if (!OrderStateMachine.CanMove(order.Status, OrderStatus.Confirmed))
        {
            return Invalid(order, OrderStatus.Confirmed);
        }

        if (order.IsHeld)
        {
            return Refuse(ErrorCodes.Order.HeldForReview, "This order is held for a fraud review. Decide it in the review queue first.");
        }

        if (order.IsOnlinePayment && order.PaymentStatus is not PaymentStatus.Paid)
        {
            return Refuse(ErrorCodes.Order.PaymentPending, "The online payment has not been received yet.");
        }

        if (!order.IsOnlinePayment && order.AdvanceRequired > 0 && order.PaymentStatus == PaymentStatus.Pending)
        {
            if (order.AdvancePaid >= order.AdvanceRequired)
            {
                // the advance was recorded as a payment (module I): nothing more to do
            }
            else if (!advanceReceived)
            {
                return Refuse(ErrorCodes.Order.AdvanceRequired, $"This order needs an advance of BDT {order.AdvanceRequired:0.##} first. Record it as a payment (POST /payments/orders/{{id}}/advance), then confirm.");
            }
            else
            {
                order.AddNote(NoteKind.Internal, $"Advance of BDT {order.AdvanceRequired:0.##} marked as received by staff.", who.Id, who.Name, clock.GetUtcNow());
            }
        }

        order.TransitionTo(OrderStatus.Confirmed, clock.GetUtcNow(), who.Id, who.Name, null);
        return Result.Success();
    }

    // ---- Pack -------------------------------------------------------------------------------------------

    public async Task<Result> PackAsync(Order order, Who who, CancellationToken cancellationToken)
    {
        if (!OrderStateMachine.CanMove(order.Status, OrderStatus.Packed))
        {
            return Invalid(order, OrderStatus.Packed);
        }

        var config = await settings.OrdersAsync(cancellationToken);
        if (config.DeductAt == DeductAt.Packing)
        {
            var failed = await DeductAsync(order, cancellationToken);
            if (failed is not null)
            {
                return failed;
            }
        }

        order.TransitionTo(OrderStatus.Packed, clock.GetUtcNow(), who.Id, who.Name, null);
        return Result.Success();
    }

    private async Task<Result?> DeductAsync(Order order, CancellationToken cancellationToken)
    {
        if (order.StockDeducted)
        {
            return null;
        }

        var lines = OrderStock.LinesOf(order);
        if (lines.Count > 0)
        {
            if (await stock.TryDeductAsync(order.BranchId, lines, cancellationToken) is not null)
            {
                return Refuse(ErrorCodes.Order.StockInsufficient, "The reserved stock for this order is not on the shelf of the branch. Check the branch stock first.");
            }

            await stock.RecordAsync(order.BranchId, lines, StockReason.OrderPacked, -1, order, cancellationToken);
        }

        order.MarkStockDeducted();
        return null;
    }

    // ---- Cancel -----------------------------------------------------------------------------------------

    public async Task<Result> CancelAsync(Order order, string? reason, Who who, CancellationToken cancellationToken)
    {
        if (!OrderStateMachine.CanCancel(order.Status))
        {
            return Refuse(ErrorCodes.Order.CannotCancel, "This order can no longer be cancelled: it has left the shop or is already closed.");
        }

        await GiveStockBackAsync(order, cancellationToken);
        order.TransitionTo(OrderStatus.Cancelled, clock.GetUtcNow(), who.Id, who.Name, reason);
        if (order.PaymentStatus is PaymentStatus.Paid or PaymentStatus.PartiallyRefunded)
        {
            order.AddNote(NoteKind.Internal, "The order was cancelled after payment: a refund is due.", who.Id, who.Name, clock.GetUtcNow());
        }

        if (order.CouponId is { } couponId)
        {
            await db.Coupons.Where(c => c.Id == couponId && c.UsedCount > 0).ExecuteUpdateAsync(set => set.SetProperty(c => c.UsedCount, c => c.UsedCount - 1), cancellationToken);
        }

        return Result.Success();
    }

    /// <summary>Returns a reservation (nothing left the shelf yet) or restocks the shelf (the order was already packed).</summary>
    private async Task GiveStockBackAsync(Order order, CancellationToken cancellationToken)
    {
        var lines = OrderStock.LinesOf(order);
        if (lines.Count == 0)
        {
            return;
        }

        if (order.StockDeducted)
        {
            await stock.RestockAsync(order.BranchId, lines, cancellationToken);
            await stock.RecordAsync(order.BranchId, lines, StockReason.OrderRestocked, 1, order, cancellationToken);
            order.MarkStockRestored();
        }
        else
        {
            await stock.ReleaseAsync(order.BranchId, lines, cancellationToken);
            await stock.RecordAsync(order.BranchId, lines, StockReason.OrderReleased, 0, order, cancellationToken);
        }
    }

    // ---- Delivery steps (couriers drive these from Step 10; staff can record them now) -------------------

    public static readonly IReadOnlyList<OrderStatus> DeliverySteps =
    [
        OrderStatus.HandedToCourier, OrderStatus.InTransit, OrderStatus.OutForDelivery, OrderStatus.Delivered, OrderStatus.DeliveryFailed,
        OrderStatus.ReturnInProgress, OrderStatus.Returned,
    ];

    public async Task<Result> AdvanceAsync(Order order, OrderStatus to, string? reason, Who who, CancellationToken cancellationToken)
    {
        if (!DeliverySteps.Contains(to) || !OrderStateMachine.CanMove(order.Status, to))
        {
            return Invalid(order, to);
        }

        if (to == OrderStatus.Delivered)
        {
            var config = await settings.OrdersAsync(cancellationToken);
            if (config.DeductAt == DeductAt.Delivery && await DeductAsync(order, cancellationToken) is { } failed)
            {
                return failed;
            }
        }

        if (to == OrderStatus.Returned)
        {
            await GiveStockBackAsync(order, cancellationToken);
        }

        order.TransitionTo(to, clock.GetUtcNow(), who.Id, who.Name, reason);
        return Result.Success();
    }

    // ---- Branch change ----------------------------------------------------------------------------------

    /// <summary>Moves the reservation to another branch (it must hold every line) and the order with it. Only before packing.</summary>
    public async Task<Result> ReassignAsync(Order order, Guid branchId, string? reason, Who who, CancellationToken cancellationToken)
    {
        if (order.Status is not (OrderStatus.Placed or OrderStatus.Confirmed))
        {
            return Refuse(ErrorCodes.Order.InvalidTransition, "The branch can only be changed before the order is packed.");
        }

        if (order.BranchId == branchId)
        {
            return Refuse(ErrorCodes.Order.NothingToDo, "The order is already in this branch.");
        }

        var lines = OrderStock.LinesOf(order);
        if (lines.Count > 0)
        {
            if (await stock.TryReserveAsync(branchId, lines, cancellationToken) is not null)
            {
                return Refuse(ErrorCodes.Order.StockInsufficient, "The other branch does not have every item of this order.");
            }

            await stock.RecordAsync(branchId, lines, StockReason.OrderReserved, 0, order, cancellationToken);
            await stock.ReleaseAsync(order.BranchId, lines, cancellationToken);
            await stock.RecordAsync(order.BranchId, lines, StockReason.OrderReleased, 0, order, cancellationToken);
        }

        order.ReassignBranch(branchId, clock.GetUtcNow(), who.Id, who.Name, reason);
        return Result.Success();
    }

    // ---- Payment (called by the payment module in Step 9, and by the timeout job) -------------------------

    public Result ChangePayment(Order order, PaymentStatus to, string? reason, Who who)
    {
        if (!OrderStateMachine.CanMove(order.PaymentStatus, to))
        {
            return Refuse(ErrorCodes.Order.InvalidTransition, $"A payment that is {order.PaymentStatus} cannot become {to}.");
        }

        order.TransitionPaymentTo(to, clock.GetUtcNow(), who.Id, who.Name, reason);
        return Result.Success();
    }
}
