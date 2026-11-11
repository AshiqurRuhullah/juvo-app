using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Orders;

/// <summary>
/// The payment-hold timeout (BR-D2): an online-payment order that is still unpaid when its hold ends is cancelled and its stock is released. Safe to run
/// from several workers: each order is re-read and re-checked inside its own transaction, so one that was paid a moment ago is left alone.
/// </summary>
public sealed class OrderTimeoutService(IApplicationDbContext db, IUnitOfWork uow, OrderWorkflow workflow, SettingsService settings, TimeProvider clock)
{
    public const int BatchSize = 50;

    /// <summary>Cancels expired unpaid orders (up to one batch). Returns how many were cancelled.</summary>
    public async Task<int> ExpireAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking()
            .Where(o => o.Status == OrderStatus.Placed && o.PaymentMethod != "COD" && (o.PaymentStatus == PaymentStatus.Pending || o.PaymentStatus == PaymentStatus.Failed) &&
                        o.PaymentHoldExpiresAt != null && o.PaymentHoldExpiresAt <= now)
            .OrderBy(o => o.PaymentHoldExpiresAt).Select(o => o.Id).Take(BatchSize).ToListAsync(cancellationToken);
        var minutes = (await settings.OrdersAsync(cancellationToken)).PaymentHoldMinutes;
        var cancelled = 0;
        foreach (var id in due)
        {
            var done = await uow.ExecuteInTransactionAsync(async ct =>
            {
                var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).Include(o => o.History).Include(o => o.Notes)
                    .FirstOrDefaultAsync(o => o.Id == id, ct);
                if (order is null || order.Status != OrderStatus.Placed || order.PaymentHoldExpiresAt is not { } until || until > now ||
                    order.PaymentStatus is not (PaymentStatus.Pending or PaymentStatus.Failed))
                {
                    return false;
                }

                var result = await workflow.CancelAsync(order, $"Payment not received within {minutes} minutes", Who.System, ct);
                if (result.IsFailure)
                {
                    return false;
                }

                await db.SaveChangesAsync(ct);
                return true;
            }, cancellationToken);
            if (done)
            {
                cancelled++;
            }
        }

        return cancelled;
    }
}
