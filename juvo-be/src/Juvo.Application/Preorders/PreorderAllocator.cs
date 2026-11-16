using Juvo.Application.Abstractions;
using Juvo.Application.Notifications;
using Juvo.Application.Orders;
using Juvo.Domain.Inventory;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juvo.Application.Preorders;

/// <summary>
/// Gives arriving stock to waiting pre-order lines (module AA, FR-AA03). Oldest order first, per branch and variant, all or nothing per line (an order is not split) and strictly in order: when the
/// oldest waiting line does not fit, younger ones do not jump the queue. The work for one branch and variant runs in ONE transaction under an application lock, and the stock is taken with the same
/// atomic conditional update orders use for reserving, so two receipts or two staff actions never hand out the same unit twice. An order whose last waiting line was served becomes Ready (and the customer
/// is told once, <c>order.preorder-ready</c>, deduplicated by order) in a second small transaction per order.
/// </summary>
public sealed class PreorderAllocator(IApplicationDbContext db, IUnitOfWork uow, OrderStock stock, NotificationService notifications, TimeProvider clock, ILogger<PreorderAllocator> logger)
{
    /// <summary>Called after a purchase receipt put stock into <paramref name="branchId"/>. Never throws: the receipt stays valid and staff can run the allocation again.</summary>
    public async Task AllocateAfterReceiptAsync(Guid branchId, IReadOnlyCollection<Guid> variantIds, CancellationToken cancellationToken)
    {
        try
        {
            await AllocateAsync([branchId], variantIds, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Pre-order allocation after a receipt failed for branch {BranchId}; it can be run again from the pre-order queue.", branchId);
        }
    }

    /// <summary>Allocates stock to waiting lines in the given branches (null: every branch) and variants (null: every variant).</summary>
    public async Task<AllocationResultDto> AllocateAsync(IReadOnlyCollection<Guid>? branchIds, IReadOnlyCollection<Guid>? variantIds, CancellationToken cancellationToken)
    {
        var waiting = from i in db.OrderItems.AsNoTracking()
                      join o in db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking() on i.OrderId equals o.Id
                      where i.BackorderedQuantity > 0 && o.PreorderState == PreorderState.Waiting && o.Status != OrderStatus.Cancelled && !o.IsDeleted
                      select new { ItemId = i.Id, OrderId = o.Id, o.Number, o.BranchId, i.VariantId, o.PlacedAt, ItemCreatedAt = i.CreatedAt };
        if (branchIds is not null)
        {
            var branches = branchIds.ToList();
            waiting = waiting.Where(x => branches.Contains(x.BranchId));
        }

        if (variantIds is not null)
        {
            var variants = variantIds.ToList();
            waiting = waiting.Where(x => variants.Contains(x.VariantId));
        }

        var rows = await waiting.ToListAsync(cancellationToken);
        var touched = new HashSet<Guid>();
        var allocated = 0;
        foreach (var group in rows.GroupBy(x => (x.BranchId, x.VariantId)))
        {
            var queue = group.OrderBy(x => x.PlacedAt).ThenBy(x => x.Number).ThenBy(x => x.ItemCreatedAt).ToList();
            try
            {
                allocated += await uow.ExecuteInTransactionAsync(async ct =>
                {
                    await db.AcquireTransactionLockAsync($"preorder:{group.Key.BranchId:N}:{group.Key.VariantId:N}", ct);
                    var done = 0;
                    foreach (var entry in queue)
                    {
                        var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == entry.OrderId, ct);
                        if (order is null || order.PreorderState != PreorderState.Waiting || order.Status == OrderStatus.Cancelled || order.IsDeleted)
                        {
                            continue;
                        }

                        var item = order.Items.FirstOrDefault(i => i.Id == entry.ItemId);
                        if (item is null || item.BackorderedQuantity == 0)
                        {
                            continue; // somebody else served it while this call waited for the lock
                        }

                        StockLine[] line = [new StockLine(entry.VariantId, item.BackorderedQuantity)];
                        if (await stock.TryReserveAsync(order.BranchId, line, ct) is not null)
                        {
                            break; // not enough stock for the oldest line: the queue waits (no jumping ahead)
                        }

                        await stock.RecordAsync(order.BranchId, line, StockReason.OrderReserved, 0, order, ct);
                        order.AllocatePreorderLine(item, clock.GetUtcNow());
                        await db.SaveChangesAsync(ct);
                        touched.Add(order.Id);
                        done++;
                    }

                    return done;
                }, cancellationToken);
            }
            catch (Exception ex) when (ex is DbUpdateException)
            {
                logger.LogWarning(ex, "Pre-order allocation for branch {BranchId} variant {VariantId} was rolled back.", group.Key.BranchId, group.Key.VariantId);
            }
        }

        var ready = new List<Guid>();
        foreach (var orderId in touched)
        {
            try
            {
                if (await SettleAsync(orderId, cancellationToken))
                {
                    ready.Add(orderId);
                }
            }
            catch (Exception ex) when (ex is DbUpdateException)
            {
                logger.LogWarning(ex, "Settling pre-order {OrderId} failed; the next allocation run settles it.", orderId);
            }
        }

        return new AllocationResultDto(allocated, ready.Count, ready);
    }

    /// <summary>When no line of the order waits any more, the order becomes Ready and the customer is told (once).</summary>
    private Task<bool> SettleAsync(Guid orderId, CancellationToken cancellationToken) =>
        uow.ExecuteInTransactionAsync(async ct =>
        {
            await db.AcquireTransactionLockAsync($"preorder-order:{orderId:N}", ct);
            if (await db.OrderItems.AsNoTracking().AnyAsync(i => i.OrderId == orderId && i.BackorderedQuantity > 0, ct))
            {
                return false;
            }

            var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).FirstOrDefaultAsync(o => o.Id == orderId, ct);
            if (order is null || order.Status == OrderStatus.Cancelled || !order.MarkPreorderReady())
            {
                return false;
            }

            await db.SaveChangesAsync(ct);
            await notifications.NotifyAsync(new NotifyRequest("order.preorder-ready", order.CustomerMobile, order.CustomerEmail, order.CustomerId,
                NotifyVariables.Of(("OrderNumber", order.Number), ("Total", NotifyVariables.Money(order.GrandTotal)), ("Name", order.CustomerName)), $"preorder-ready:{order.Id:N}"), ct);
            return true;
        }, cancellationToken);
}
