using System.Linq.Expressions;
using Juvo.Application.Abstractions;
using Juvo.Domain.Couriers;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Reports;

/// <summary>Order-level sums of one group (a day, a channel, a zone ...). Orders, gross, discount and delivery come from the order rows.</summary>
public sealed class OrderAgg<TKey>
{
    public TKey Key { get; set; } = default!;

    public int Orders { get; set; }

    public decimal Gross { get; set; }

    public decimal Discount { get; set; }

    public decimal Delivery { get; set; }
}

/// <summary>Line-level sums of one group.</summary>
public sealed class ItemAgg<TKey>
{
    public TKey Key { get; set; } = default!;

    public int Units { get; set; }

    public decimal Lines { get; set; }

    public decimal DiscountShare { get; set; }

    public int Orders { get; set; }

    public decimal Cost { get; set; }

    public int MissingCostLines { get; set; }
}

/// <summary>One order line joined with the facts of its order. Built only inside SQL queries.</summary>
public sealed class Line
{
    public Guid OrderId { get; set; }

    public Guid ProductId { get; set; }

    public Guid VariantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Size { get; set; }

    public string? ColorName { get; set; }

    public int Quantity { get; set; }

    public decimal LineTotal { get; set; }

    public decimal? UnitCost { get; set; }

    public decimal OrderSubtotal { get; set; }

    public decimal OrderDiscount { get; set; }

    public OrderSource Source { get; set; }

    public string? ZoneCode { get; set; }

    public string PaymentMethod { get; set; } = string.Empty;

    public int Day { get; set; }

    public Guid? CategoryId { get; set; }
}

/// <summary>
/// Set-based aggregates over orders, refunds, shipments, expenses and purchases, shared by the reports, the profit and loss, the dashboard and the daily summary. Everything is
/// grouped and summed in SQL; only the aggregated rows come back. A day is a Bangladesh day, encoded as days since 1970-01-01 of the Bangladesh local time.
/// </summary>
public sealed class ReportData(IApplicationDbContext db)
{
    /// <summary>
    /// An order that counts as a sale: not cancelled, and not returned without ever having been delivered (a failed COD parcel that came back never earned anything).
    /// A delivered order that is returned later still counts; its refund is subtracted separately.
    /// </summary>
    public static readonly Expression<Func<Order, bool>> Countable = o => o.Status != OrderStatus.Cancelled && !(o.Status == OrderStatus.Returned && o.DeliveredAt == null);

    public IQueryable<Order> Orders(ReportScope s) =>
        s.Apply(db.Orders.AsNoTracking()).Where(o => o.PlacedAt >= s.FromAt && o.PlacedAt < s.ToAt).Where(Countable);

    public IQueryable<Line> Lines(ReportScope s)
    {
        var orders = Orders(s);
        return from i in db.OrderItems.AsNoTracking()
               join o in orders on i.OrderId equals o.Id
               join p in db.Products.IgnoreQueryFilters().AsNoTracking() on i.ProductId equals p.Id into products
               from p in products.DefaultIfEmpty()
               select new Line
               {
                   OrderId = o.Id, ProductId = i.ProductId, VariantId = i.VariantId, Name = i.Name, Size = i.Size, ColorName = i.ColorName, Quantity = i.Quantity, LineTotal = i.LineTotal, UnitCost = i.UnitCost,
                   OrderSubtotal = o.Subtotal, OrderDiscount = o.CouponDiscount + o.ManualDiscount, Source = o.Source, ZoneCode = o.ZoneCode, PaymentMethod = o.PaymentMethod,
                   Day = (int)((o.PlacedAt.ToUnixTimeSeconds() + 21600L) / 86400L),
                   CategoryId = p == null ? (Guid?)null : p.CategoryId,
               };
    }

    public async Task<List<OrderAgg<TKey>>> OrderGroupsAsync<TKey>(ReportScope s, Expression<Func<Order, TKey>> key, CancellationToken ct) =>
        await Orders(s).GroupBy(key).Select(g => new OrderAgg<TKey>
        {
            Key = g.Key, Orders = g.Count(), Gross = g.Sum(o => o.Subtotal), Discount = g.Sum(o => o.CouponDiscount + o.ManualDiscount), Delivery = g.Sum(o => o.DeliveryCharge),
        }).ToListAsync(ct);

    public async Task<List<ItemAgg<TKey>>> ItemGroupsAsync<TKey>(ReportScope s, Expression<Func<Line, TKey>> key, CancellationToken ct) =>
        await Lines(s).GroupBy(key).Select(g => new ItemAgg<TKey>
        {
            Key = g.Key,
            Units = g.Sum(l => l.Quantity),
            Lines = g.Sum(l => l.LineTotal),
            DiscountShare = g.Sum(l => l.OrderSubtotal > 0m ? l.LineTotal * l.OrderDiscount / l.OrderSubtotal : 0m),
            Orders = g.Select(l => l.OrderId).Distinct().Count(),
            Cost = g.Sum(l => l.UnitCost != null ? l.UnitCost.Value * l.Quantity : 0m),
            MissingCostLines = g.Count(l => l.UnitCost == null),
        }).ToListAsync(ct);

    /// <summary>The order-level day key (Bangladesh day) as an expression for grouping orders.</summary>
    public static readonly Expression<Func<Order, int>> OrderDay = o =>
        (int)((o.PlacedAt.ToUnixTimeSeconds() + 21600L) / 86400L);

    // ---- Money that is read from other records, never copied ---------------------------------------------------------

    /// <summary>Completed refunds by the Bangladesh day they completed, of orders that count as sales.</summary>
    public async Task<Dictionary<DateOnly, decimal>> RefundsByDayAsync(ReportScope s, CancellationToken ct)
    {
        var counted = db.Orders.AsNoTracking().Where(Countable);
        var rows = await s.Apply(db.Refunds.AsNoTracking())
            .Where(r => r.Status == RefundState.Completed && r.CompletedAt != null && r.CompletedAt >= s.FromAt && r.CompletedAt < s.ToAt && counted.Any(o => o.Id == r.OrderId))
            .GroupBy(r => (int)((r.CompletedAt!.Value.ToUnixTimeSeconds() + 21600L) / 86400L))
            .Select(g => new { g.Key, Sum = g.Sum(r => r.Amount) }).ToListAsync(ct);
        return rows.ToDictionary(r => ReportMath.DayFromKey(r.Key), r => r.Sum);
    }

    /// <summary>What couriers charge the shop: the cost of every parcel booked in the period that was not cancelled (forward and return parcels).</summary>
    public async Task<Dictionary<DateOnly, decimal>> CourierChargesByDayAsync(ReportScope s, CancellationToken ct)
    {
        var rows = await s.Apply(db.Shipments.AsNoTracking())
            .Where(x => x.BookedAt >= s.FromAt && x.BookedAt < s.ToAt && x.Status != ShipmentStatus.Cancelled)
            .GroupBy(x => (int)((x.BookedAt.ToUnixTimeSeconds() + 21600L) / 86400L))
            .Select(g => new { g.Key, Sum = g.Sum(x => x.Cost) }).ToListAsync(ct);
        return rows.ToDictionary(r => ReportMath.DayFromKey(r.Key), r => r.Sum);
    }

    /// <summary>Approved expenses by business date.</summary>
    public async Task<Dictionary<DateOnly, decimal>> ExpensesByDayAsync(ReportScope s, CancellationToken ct)
    {
        var rows = await s.Apply(db.Expenses.AsNoTracking())
            .Where(e => e.Status == Domain.Accounting.ExpenseStatus.Approved && e.ExpenseDate >= s.From && e.ExpenseDate <= s.To)
            .GroupBy(e => e.ExpenseDate).Select(g => new { g.Key, Sum = g.Sum(e => e.Amount) }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Key, r => r.Sum);
    }

    public async Task<List<(Guid CategoryId, string Name, decimal Amount, int Count)>> ExpensesByCategoryAsync(ReportScope s, CancellationToken ct)
    {
        var groups = await s.Apply(db.Expenses.AsNoTracking())
            .Where(e => e.Status == Domain.Accounting.ExpenseStatus.Approved && e.ExpenseDate >= s.From && e.ExpenseDate <= s.To)
            .GroupBy(e => e.CategoryId).Select(g => new { g.Key, Sum = g.Sum(e => e.Amount), Count = g.Count() }).ToListAsync(ct);
        var ids = groups.Select(g => g.Key).ToList();
        var names = await db.ExpenseCategories.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.NameEn, ct);
        return [.. groups.OrderByDescending(g => g.Sum).Select(g => (g.Key, names.GetValueOrDefault(g.Key, "(category removed)"), g.Sum, g.Count))];
    }

    /// <summary>Goods received (accepted quantity at the receipt's unit cost) by receipt day. Information only: purchases are stock, not an expense, until the goods are sold.</summary>
    public async Task<Dictionary<DateOnly, decimal>> PurchasesByDayAsync(ReportScope s, CancellationToken ct)
    {
        var receipts = s.Apply(db.GoodsReceipts.AsNoTracking()).Where(r => r.ReceivedAt >= s.FromAt && r.ReceivedAt < s.ToAt);
        var rows = await (from l in db.GoodsReceiptLines.AsNoTracking()
                          join r in receipts on l.ReceiptId equals r.Id
                          group new { l, r } by (int)((r.ReceivedAt.ToUnixTimeSeconds() + 21600L) / 86400L) into g
                          select new { g.Key, Sum = g.Sum(x => x.l.AcceptedQuantity * x.l.UnitCost) }).ToListAsync(ct);
        return rows.ToDictionary(r => ReportMath.DayFromKey(r.Key), r => r.Sum);
    }

    /// <summary>Customer payments (not COD), settled COD cash and completed refunds in the period, as the day book reads them.</summary>
    public async Task<(decimal Receipts, decimal CodSettled, decimal Refunds)> CashReceivedAsync(ReportScope s, CancellationToken ct)
    {
        PaymentStatus[] paid = [PaymentStatus.Paid, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded];
        var receipts = await s.Apply(db.Payments.AsNoTracking()).Where(p => p.Kind != PaymentKind.Cod && p.PaidAt != null && p.PaidAt >= s.FromAt && p.PaidAt < s.ToAt && paid.Contains(p.Status)).SumAsync(p => (decimal?)p.Amount, ct) ?? 0m;
        var cod = await s.Apply(db.CodSettlements.AsNoTracking()).Where(x => x.Status == SettlementStatus.Settled && x.SettledAt != null && x.SettledAt >= s.FromAt && x.SettledAt < s.ToAt).SumAsync(x => x.ReceivedTotal, ct) ?? 0m;
        var refunds = await s.Apply(db.Refunds.AsNoTracking()).Where(r => r.Status == RefundState.Completed && r.CompletedAt != null && r.CompletedAt >= s.FromAt && r.CompletedAt < s.ToAt).SumAsync(r => (decimal?)r.Amount, ct) ?? 0m;
        return (receipts, cod, refunds);
    }
}
