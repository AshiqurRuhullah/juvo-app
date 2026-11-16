using Juvo.Application.Abstractions;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Reports;

/// <summary>One row of the sales report. Net = gross minus discount (goods only); Total = net plus delivery charged.</summary>
public sealed record SalesRowDto(string Key, string Label, int Orders, int Units, decimal Gross, decimal Discount, decimal Net, decimal Delivery, decimal Total);

public sealed record SalesReportDto(ReportFiltersDto Filters, DateTimeOffset GeneratedAt, SalesRowDto Totals, IReadOnlyList<SalesRowDto> Rows, bool Truncated, IReadOnlyList<string> Notes);

/// <summary>FR-P01: sales by day, week, month, product, category, size, colour, channel, delivery zone or payment method. Only orders that count as sales are included.</summary>
public sealed class SalesReportService(ReportAccess access, ReportData data, IApplicationDbContext db, TimeProvider clock)
{
    public static readonly string[] GroupBys = ["day", "week", "month", "product", "category", "size", "color", "channel", "zone", "payment"];

    public async Task<Result<SalesReportDto>> GetAsync(ReportQuery q, string permission, CancellationToken ct)
    {
        var scope = await access.ScopeAsync(q, permission, GroupBys, true, ct);
        return scope.IsFailure ? scope.Error! : await BuildAsync(scope.Value, ct);
    }

    private static SalesRowDto Row(string key, string label, int orders, int units, decimal gross, decimal discount, decimal delivery) =>
        new(key, label, orders, units, ReportMath.Round2(gross), ReportMath.Round2(discount), ReportMath.Round2(gross - discount), ReportMath.Round2(delivery), ReportMath.Round2(gross - discount + delivery));

    internal async Task<SalesReportDto> BuildAsync(ReportScope s, CancellationToken ct)
    {
        var notes = new List<string> { "Only orders that count as sales are included (not cancelled, not returned without delivery). Gross is the tax-inclusive value of the lines." };
        var rows = new List<SalesRowDto>();

        // Totals always come from the order rows, so they are exact whatever the grouping.
        var all = await data.OrderGroupsAsync(s, o => 1, ct);
        var units = await data.ItemGroupsAsync(s, l => 1, ct);
        var totals = Row("total", "Total", all.Sum(a => a.Orders), units.Sum(u => u.Units), all.Sum(a => a.Gross), all.Sum(a => a.Discount), all.Sum(a => a.Delivery));

        switch (s.GroupBy)
        {
            case "day" or "week" or "month":
            {
                var orders = await data.OrderGroupsAsync(s, ReportData.OrderDay, ct);
                var items = await data.ItemGroupsAsync(s, l => l.Day, ct);
                var byBucket = orders.GroupBy(o => ReportMath.BucketStart(ReportMath.DayFromKey(o.Key), s.GroupBy)).ToDictionary(g => g.Key);
                var unitsBy = items.GroupBy(i => ReportMath.BucketStart(ReportMath.DayFromKey(i.Key), s.GroupBy)).ToDictionary(g => g.Key, g => g.Sum(x => x.Units));
                foreach (var b in ReportMath.Buckets(s.From, s.To, s.GroupBy))
                {
                    var g = byBucket.GetValueOrDefault(b);
                    var label = ReportMath.BucketLabel(b, s.GroupBy);
                    rows.Add(Row(label, label, g?.Sum(x => x.Orders) ?? 0, unitsBy.GetValueOrDefault(b), g?.Sum(x => x.Gross) ?? 0m, g?.Sum(x => x.Discount) ?? 0m, g?.Sum(x => x.Delivery) ?? 0m));
                }

                break;
            }

            case "channel":
                rows.AddRange(await OrderLevelAsync(s, o => o.Source, l => l.Source, k => k.ToString() ?? string.Empty, k => k.ToString() ?? string.Empty, ct));
                break;
            case "zone":
            {
                var names = await db.DeliveryZones.AsNoTracking().ToDictionaryAsync(z => z.Code, z => z.NameEn, ct);
                rows.AddRange(await OrderLevelAsync(s, o => o.ZoneCode ?? "", l => l.ZoneCode ?? "", k => k, k => k.Length == 0 ? "(no zone)" : names.GetValueOrDefault(k, k), ct));
                break;
            }

            case "payment":
                rows.AddRange(await OrderLevelAsync(s, o => o.PaymentMethod, l => l.PaymentMethod, k => k, k => k, ct));
                break;
            default:
                rows.AddRange(await ItemLevelAsync(s, notes, ct));
                break;
        }

        var truncated = false;
        if (s.GroupBy is not ("day" or "week" or "month"))
        {
            rows = [.. rows.OrderByDescending(r => r.Total).ThenBy(r => r.Label, StringComparer.Ordinal)];
            if (rows.Count > s.Limit)
            {
                rows = rows.Take(s.Limit).ToList();
                truncated = true;
            }
        }

        return new SalesReportDto(s.Echo(), clock.GetUtcNow(), totals, rows, truncated, notes);
    }

    private async Task<List<SalesRowDto>> OrderLevelAsync<TKey>(ReportScope s, System.Linq.Expressions.Expression<Func<Order, TKey>> orderKey, System.Linq.Expressions.Expression<Func<Line, TKey>> lineKey,
        Func<TKey, string> key, Func<TKey, string> label, CancellationToken ct)
        where TKey : notnull
    {
        var orders = await data.OrderGroupsAsync(s, orderKey, ct);
        var units = (await data.ItemGroupsAsync(s, lineKey, ct)).ToDictionary(i => i.Key, i => i.Units);
        return [.. orders.Select(o => Row(key(o.Key), label(o.Key), o.Orders, units.GetValueOrDefault(o.Key), o.Gross, o.Discount, o.Delivery))];
    }

    /// <summary>Product, category, size and colour rows are lines: an order with two products counts in both rows, the order discount is shared by line value, delivery is not shared.</summary>
    private async Task<List<SalesRowDto>> ItemLevelAsync(ReportScope s, List<string> notes, CancellationToken ct)
    {
        notes.Add("Rows are built from order lines: an order with several products counts once in each row, the order discount is shared by line value, and delivery is not shared (0). The totals row is exact.");
        switch (s.GroupBy)
        {
            case "product":
            {
                var g = await data.ItemGroupsAsync(s, l => l.ProductId, ct);
                var ids = g.Select(x => x.Key).ToList();
                var names = await db.OrderItems.AsNoTracking().Where(i => ids.Contains(i.ProductId)).GroupBy(i => i.ProductId).Select(x => new { x.Key, Name = x.Max(i => i.Name) ?? "?" }).ToDictionaryAsync(x => x.Key, x => x.Name, ct);
                return [.. g.Select(x => Row(x.Key.ToString(), names.GetValueOrDefault(x.Key) ?? "?", x.Orders, x.Units, x.Lines, x.DiscountShare, 0m))];
            }

            case "category":
            {
                var g = await data.ItemGroupsAsync(s, l => l.CategoryId, ct);
                var ids = g.Where(x => x.Key != null).Select(x => x.Key!.Value).ToList();
                var names = await db.Categories.IgnoreQueryFilters().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.NameEn, ct);
                return [.. g.Select(x => Row(x.Key?.ToString() ?? "", x.Key is { } id ? names.GetValueOrDefault(id, "?") : "(unknown)", x.Orders, x.Units, x.Lines, x.DiscountShare, 0m))];
            }

            case "size":
            {
                var g = await data.ItemGroupsAsync(s, l => l.Size ?? "", ct);
                return [.. g.Select(x => Row(x.Key, x.Key.Length == 0 ? "(no size)" : x.Key, x.Orders, x.Units, x.Lines, x.DiscountShare, 0m))];
            }

            default:
            {
                var g = await data.ItemGroupsAsync(s, l => l.ColorName ?? "", ct);
                return [.. g.Select(x => Row(x.Key, x.Key.Length == 0 ? "(no colour)" : x.Key, x.Orders, x.Units, x.Lines, x.DiscountShare, 0m))];
            }
        }
    }

    public static ReportDoc ToDoc(SalesReportDto r, string lang, string title, IReadOnlyList<KeyValuePair<string, string>> filters)
    {
        string[] h = ["Label", "Orders", "Units", "Gross", "Discount", "Net", "Delivery", "Grand total"];
        List<string?> Row(SalesRowDto x, string label) => [label, Cells.N(x.Orders), Cells.N(x.Units), Cells.M(x.Gross), Cells.M(x.Discount), Cells.M(x.Net), Cells.M(x.Delivery), Cells.M(x.Total)];
        var section = new ReportSection("sales", title, [.. h.Select(x => ReportText.T(x, lang))], [.. r.Rows.Select(x => (IReadOnlyList<string?>)Row(x, x.Label))], Row(r.Totals, ReportText.T("Total", lang)),
            Cells.Set(h.Skip(1).Select(x => ReportText.T(x, lang)).ToArray()));
        return new ReportDoc("sales", title, filters, r.GeneratedAt, [section], r.Notes);
    }
}

public sealed record FunnelStageDto(string Stage, int Count, decimal RateOfPlaced, decimal RateOfPrevious);

public sealed record ReasonRowDto(string Code, string Label, int Count, decimal Share, string? Fault);

public sealed record OrdersReportDto(
    ReportFiltersDto Filters, DateTimeOffset GeneratedAt, int Placed, IReadOnlyList<FunnelStageDto> Funnel, decimal CancelledRate, decimal ReturnedRate, IReadOnlyList<ReasonRowDto> CancellationReasons,
    IReadOnlyList<ReasonRowDto> ReturnReasons, IReadOnlyList<string> Notes);

/// <summary>FR-P02: how many of the orders placed in the period were confirmed, packed, shipped, delivered, cancelled or returned, plus why orders were cancelled and returned.</summary>
public sealed class OrdersReportService(ReportAccess access, IApplicationDbContext db, TimeProvider clock)
{
    private static readonly string[] FunnelHeaders = ["Stage", "Count", "Rate %"];

    private static readonly OrderStatus[] Shipped =
        [OrderStatus.HandedToCourier, OrderStatus.InTransit, OrderStatus.OutForDelivery, OrderStatus.Delivered, OrderStatus.DeliveryFailed, OrderStatus.ReturnInProgress, OrderStatus.Returned];

    public async Task<Result<OrdersReportDto>> GetAsync(ReportQuery q, string permission, CancellationToken ct)
    {
        var scope = await access.ScopeAsync(q, permission, null, true, ct);
        return scope.IsFailure ? scope.Error! : await BuildAsync(scope.Value, ct);
    }

    internal async Task<OrdersReportDto> BuildAsync(ReportScope s, CancellationToken ct)
    {
        var placed = s.Apply(db.Orders.AsNoTracking()).Where(o => o.PlacedAt >= s.FromAt && o.PlacedAt < s.ToAt);
        var c = await placed.GroupBy(o => 1).Select(g => new
        {
            Placed = g.Count(),
            Confirmed = g.Count(o => o.ConfirmedAt != null),
            Packed = g.Count(o => o.PackedAt != null),
            Shipped = g.Count(o => Shipped.Contains(o.Status)),
            Delivered = g.Count(o => o.DeliveredAt != null),
            Cancelled = g.Count(o => o.Status == OrderStatus.Cancelled),
            Returned = g.Count(o => o.Status == OrderStatus.Returned),
        }).FirstOrDefaultAsync(ct);
        var counts = new (string Stage, int Count)[]
        {
            ("Placed", c?.Placed ?? 0), ("Confirmed", c?.Confirmed ?? 0), ("Packed", c?.Packed ?? 0), ("Shipped", c?.Shipped ?? 0), ("Delivered", c?.Delivered ?? 0),
            ("Cancelled", c?.Cancelled ?? 0), ("Returned", c?.Returned ?? 0),
        };
        var total = c?.Placed ?? 0;
        var funnel = new List<FunnelStageDto>();
        for (var i = 0; i < counts.Length; i++)
        {
            var previous = i is 0 ? total : i <= 4 ? counts[i - 1].Count : total;
            funnel.Add(new FunnelStageDto(counts[i].Stage, counts[i].Count, ReportMath.Percent(counts[i].Count, total), ReportMath.Percent(counts[i].Count, previous)));
        }

        var cancelled = await placed.Where(o => o.Status == OrderStatus.Cancelled).GroupBy(o => o.CancelReason ?? "").Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(g => g.Count).Take(s.Limit).ToListAsync(ct);
        var cancelTotal = counts[5].Count;
        var cancelRows = cancelled.Select(x => new ReasonRowDto(x.Key, x.Key.Length == 0 ? "(no reason given)" : x.Key, x.Count, ReportMath.Percent(x.Count, cancelTotal), null)).ToList();

        var returns = await s.Apply(db.ReturnRequests.AsNoTracking()).Where(r => r.RequestedAt >= s.FromAt && r.RequestedAt < s.ToAt && r.Status != ReturnStatus.Cancelled)
            .GroupBy(r => new { r.ReasonCode, r.Fault }).Select(g => new { g.Key.ReasonCode, g.Key.Fault, Count = g.Count() }).OrderByDescending(g => g.Count).ToListAsync(ct);
        var returnTotal = returns.Sum(r => r.Count);
        var returnRows = returns.Take(s.Limit).Select(r => new ReasonRowDto(r.ReasonCode, ReturnReason.All.FirstOrDefault(x => x.Code == r.ReasonCode)?.LabelEn ?? r.ReasonCode, r.Count, ReportMath.Percent(r.Count, returnTotal), r.Fault.ToString())).ToList();

        string[] notes =
        [
            "The funnel counts the orders placed in the period and where they are now. Delivered counts every order that was ever delivered, including ones returned later.",
            "Rates are shares of the orders placed; the second rate is the step from the previous stage (cancelled and returned are measured against placed).",
            "Return reasons count return requests raised in the period (cancelled requests excluded).",
        ];
        return new OrdersReportDto(s.Echo(), clock.GetUtcNow(), total, funnel, ReportMath.Percent(counts[5].Count, total), ReportMath.Percent(counts[6].Count, total), cancelRows, returnRows, notes);
    }

    public static ReportDoc ToDoc(OrdersReportDto r, string lang, string title, IReadOnlyList<KeyValuePair<string, string>> filters)
    {
        var funnel = new ReportSection("funnel", ReportText.T("Funnel", lang), [.. FunnelHeaders.Select(x => ReportText.T(x, lang))],
            [.. r.Funnel.Select(f => (IReadOnlyList<string?>)[ReportText.T(f.Stage, lang), Cells.N(f.Count), Cells.P(f.RateOfPlaced)])], null, Cells.Set(ReportText.T("Count", lang), ReportText.T("Rate %", lang)));
        string[] rh = ["Reason", "Count", "Share %", "Fault"];
        ReportSection Reasons(string key, string name, IReadOnlyList<ReasonRowDto> rows) => new(key, ReportText.T(name, lang), [.. rh.Select(x => ReportText.T(x, lang))],
            [.. rows.Select(x => (IReadOnlyList<string?>)[x.Label, Cells.N(x.Count), Cells.P(x.Share), x.Fault])], null, Cells.Set(ReportText.T("Count", lang), ReportText.T("Share %", lang)));
        return new ReportDoc("orders", title, filters, r.GeneratedAt, [funnel, Reasons("cancellations", "Cancellation reasons", r.CancellationReasons), Reasons("returns", "Return reasons", r.ReturnReasons)], r.Notes);
    }
}
