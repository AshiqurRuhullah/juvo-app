using Juvo.Application.Abstractions;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Returns;

/// <summary>Keys of the runtime settings of the return flow (table <c>PlatformSettings</c>).</summary>
public static class ReturnSettingKeys
{
    public const string WindowDays = "Returns.WindowDays";
    public const string CustomerFaultReturnCharge = "Returns.CustomerFaultReturnCharge";
    public const string RefundDeliveryOnSellerFault = "Returns.RefundDeliveryOnSellerFault";
}

/// <summary>The return settings as read for one request (defaults: 7 days, no return charge, delivery charge refunded when the seller is at fault).</summary>
public sealed record ReturnSettings(int WindowDays, decimal CustomerFaultReturnCharge, bool RefundDeliveryOnSellerFault)
{
    public static readonly ReturnSettings Defaults = new(7, 0m, true);
}

/// <summary>The money rules of module L (FR-L07), kept as plain functions so they can be tested without a database.</summary>
public static class ReturnMath
{
    /// <summary>What the customer paid for the whole line after the order's discounts: the line total times (subtotal minus discounts) over subtotal.</summary>
    public static decimal NetLine(Order order, OrderItem item)
    {
        var subtotal = order.Items.Sum(i => i.LineTotal);
        if (subtotal <= 0)
        {
            return 0m;
        }

        var discount = Math.Min(subtotal, order.CouponDiscount + order.ManualDiscount);
        return Math.Round(item.LineTotal * (subtotal - discount) / subtotal, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The refund of <paramref name="quantity"/> units of a line. A share of the net line value; the last units take whatever is left, so that
    /// several partial returns add up to exactly the net line value (no cent is lost or paid twice).
    /// </summary>
    public static decimal LineRefund(Order order, OrderItem item, int quantity, int alreadyReturned, decimal alreadyRefunded)
    {
        var net = NetLine(order, item);
        var left = Math.Max(0m, net - alreadyRefunded);
        if (quantity >= item.Quantity - alreadyReturned)
        {
            return left;
        }

        return Math.Min(left, Math.Round(net * quantity / item.Quantity, 2, MidpointRounding.AwayFromZero));
    }
}

/// <summary>Reads the return settings and works out what of an order can still be returned.</summary>
public sealed class ReturnPolicy(IApplicationDbContext db, Orders.SettingsService settings, TimeProvider clock)
{
    public async Task<ReturnSettings> ReadAsync(CancellationToken cancellationToken)
    {
        var values = await settings.ReadAsync([ReturnSettingKeys.WindowDays, ReturnSettingKeys.CustomerFaultReturnCharge, ReturnSettingKeys.RefundDeliveryOnSellerFault], cancellationToken);
        var refundDelivery = values.TryGetValue(ReturnSettingKeys.RefundDeliveryOnSellerFault, out var raw) && bool.TryParse(raw, out var parsed) ? parsed : ReturnSettings.Defaults.RefundDeliveryOnSellerFault;
        return new ReturnSettings(
            (int)Orders.SettingsService.Number(values, ReturnSettingKeys.WindowDays, ReturnSettings.Defaults.WindowDays, 0m),
            Orders.SettingsService.Number(values, ReturnSettingKeys.CustomerFaultReturnCharge, ReturnSettings.Defaults.CustomerFaultReturnCharge, 0m),
            refundDelivery);
    }

    /// <summary>What is already claimed on each line of the order by returns that still count (anything but rejected, cancelled or refund-rejected).</summary>
    public async Task<Dictionary<Guid, (int Quantity, decimal Refund)>> ClaimedAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var rows = await db.ReturnRequests.IgnoreQueryFilters(["BranchScope"]).AsNoTracking()
            .Where(r => r.OrderId == orderId && r.Status != ReturnStatus.Rejected && r.Status != ReturnStatus.Cancelled && r.Status != ReturnStatus.RefundRejected)
            .SelectMany(r => r.Items).Select(i => new { i.OrderItemId, i.Quantity, i.LineRefund }).ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.OrderItemId).ToDictionary(g => g.Key, g => (g.Sum(x => x.Quantity), g.Sum(x => x.LineRefund)));
    }

    /// <summary>Why the order as a whole cannot be returned, or null. Only delivered orders (or ones with an earlier partial return) and only inside the window.</summary>
    public (string Code, string Message)? OrderProblem(Order order, ReturnSettings config, out DateTimeOffset? deadline)
    {
        deadline = order.DeliveredAt?.AddDays(config.WindowDays);
        if (order.Status is not (OrderStatus.Delivered or OrderStatus.ReturnInProgress) || order.DeliveredAt is null)
        {
            return (Juvo.Shared.Results.ErrorCodes.Return.OrderNotEligible, "Only an order that was delivered can be returned.");
        }

        if (clock.GetUtcNow() > deadline)
        {
            return (Juvo.Shared.Results.ErrorCodes.Return.WindowExpired, $"The return window of {config.WindowDays} days after delivery is over.");
        }

        return null;
    }

    /// <summary>Products flagged as non-returnable (soft-deleted or archived products are included: the flag belongs to what was sold).</summary>
    public async Task<HashSet<Guid>> NonReturnableAsync(IEnumerable<Guid> productIds, CancellationToken cancellationToken)
    {
        var ids = productIds.Distinct().ToList();
        var flagged = await db.Products.IgnoreQueryFilters().AsNoTracking().Where(p => ids.Contains(p.Id) && p.NonReturnable).Select(p => p.Id).ToListAsync(cancellationToken);
        return [.. flagged];
    }
}
