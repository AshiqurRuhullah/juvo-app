using Juvo.Application.Abstractions;
using Juvo.Application.Operations;
using Juvo.Domain.Couriers;
using Juvo.Domain.Inventory;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Reports;

public sealed record StockSummaryDto(int Variants, int OnHand, int Reserved, int Available, decimal ValueAtPrice, decimal? ValueAtCost, int UnitsWithoutCost);

public sealed record StockBranchRowDto(Guid BranchId, string BranchCode, int OnHand, decimal ValueAtPrice, decimal? ValueAtCost);

public sealed record LowStockRowDto(Guid VariantId, string Sku, string Product, Guid BranchId, string BranchCode, int OnHand, int Reserved, int Available, int ReorderLevel);

public sealed record MovementRowDto(string Reason, int Movements, int In, int Out, int Net);

public sealed record DeadStockRowDto(Guid VariantId, string Sku, string Product, int OnHand, decimal ValueAtPrice, decimal? ValueAtCost, DateTimeOffset? LastSoldAt);

public sealed record InventoryReportDto(
    ReportFiltersDto Filters, DateTimeOffset GeneratedAt, bool CostHidden, StockSummaryDto Summary, IReadOnlyList<StockBranchRowDto> ByBranch, int LowStockCount, IReadOnlyList<LowStockRowDto> LowStock,
    IReadOnlyList<MovementRowDto> Movements, int DeadStockCount, decimal DeadStockValueAtPrice, IReadOnlyList<DeadStockRowDto> DeadStock, IReadOnlyList<string> Notes);

/// <summary>FR-P03: stock value at cost and at price, low stock, movements of the period and dead stock (no sale for N days, parameter <c>days</c>). Stock is a snapshot of now.</summary>
public sealed class InventoryReportService(ReportAccess access, IApplicationDbContext db, TimeProvider clock)
{
    public async Task<Result<InventoryReportDto>> GetAsync(ReportQuery q, string permission, CancellationToken ct)
    {
        var scope = await access.ScopeAsync(q, permission, null, true, ct);
        return scope.IsFailure ? scope.Error! : await BuildAsync(scope.Value, ct);
    }

    internal async Task<InventoryReportDto> BuildAsync(ReportScope s, CancellationToken ct)
    {
        var stock = from l in s.Apply(db.StockLevels.AsNoTracking())
                    join v in db.ProductVariants.AsNoTracking() on l.VariantId equals v.Id
                    join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                    select new { l.BranchId, l.VariantId, l.OnHand, l.Reserved, l.ReorderLevel, v.Sku, Product = p.NameEn, Price = v.PriceOverride ?? p.RegularPrice, p.CostPrice };

        var sum = await stock.GroupBy(x => 1).Select(g => new
        {
            Variants = g.Count(),
            OnHand = g.Sum(x => x.OnHand),
            Reserved = g.Sum(x => x.Reserved),
            AtPrice = g.Sum(x => x.OnHand * x.Price),
            AtCost = g.Sum(x => x.OnHand * (x.CostPrice ?? 0m)),
            NoCost = g.Sum(x => x.CostPrice == null ? x.OnHand : 0),
        }).FirstOrDefaultAsync(ct);
        var summary = new StockSummaryDto(sum?.Variants ?? 0, sum?.OnHand ?? 0, sum?.Reserved ?? 0, (sum?.OnHand ?? 0) - (sum?.Reserved ?? 0), ReportMath.Round2(sum?.AtPrice ?? 0m),
            s.CanSeeCost ? ReportMath.Round2(sum?.AtCost ?? 0m) : null, s.CanSeeCost ? sum?.NoCost ?? 0 : 0);

        var branchGroups = await stock.GroupBy(x => x.BranchId).Select(g => new { g.Key, OnHand = g.Sum(x => x.OnHand), AtPrice = g.Sum(x => x.OnHand * x.Price), AtCost = g.Sum(x => x.OnHand * (x.CostPrice ?? 0m)) }).ToListAsync(ct);
        var codes = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var byBranch = branchGroups.Select(b => new StockBranchRowDto(b.Key, codes.GetValueOrDefault(b.Key, string.Empty), b.OnHand, ReportMath.Round2(b.AtPrice), s.CanSeeCost ? ReportMath.Round2(b.AtCost) : null))
            .OrderBy(b => b.BranchCode, StringComparer.Ordinal).ToList();

        var low = stock.Where(x => x.ReorderLevel > 0 && x.OnHand - x.Reserved <= x.ReorderLevel);
        var lowCount = await low.CountAsync(ct);
        var lowRows = (await low.OrderBy(x => x.OnHand - x.Reserved).ThenBy(x => x.Sku).Take(s.Limit).ToListAsync(ct))
            .Select(x => new LowStockRowDto(x.VariantId, x.Sku, x.Product, x.BranchId, codes.GetValueOrDefault(x.BranchId, string.Empty), x.OnHand, x.Reserved, x.OnHand - x.Reserved, x.ReorderLevel)).ToList();

        var moves = await s.Apply(db.StockMovements.AsNoTracking()).Where(m => m.CreatedAt >= s.FromAt && m.CreatedAt < s.ToAt).GroupBy(m => m.Reason)
            .Select(g => new { g.Key, Count = g.Count(), In = g.Sum(m => m.QuantityChange > 0 ? m.QuantityChange : 0), Out = g.Sum(m => m.QuantityChange < 0 ? -m.QuantityChange : 0) }).ToListAsync(ct);
        var movements = moves.OrderBy(m => (int)m.Key).Select(m => new MovementRowDto(m.Key.ToString(), m.Count, m.In, m.Out, m.In - m.Out)).ToList();

        // Dead stock: units on hand of variants without a counted sale in the last N days (in the same branches).
        var cutoff = s.Now.AddDays(-s.Days);
        var soldVariants = from i in db.OrderItems.AsNoTracking()
                           join o in s.Apply(db.Orders.AsNoTracking()).Where(ReportData.Countable).Where(o => o.PlacedAt >= cutoff) on i.OrderId equals o.Id
                           select i.VariantId;
        var dead = stock.Where(x => x.OnHand > 0 && !soldVariants.Contains(x.VariantId))
            .GroupBy(x => new { x.VariantId, x.Sku, x.Product })
            .Select(g => new { g.Key.VariantId, g.Key.Sku, g.Key.Product, OnHand = g.Sum(x => x.OnHand), AtPrice = g.Sum(x => x.OnHand * x.Price), AtCost = g.Sum(x => x.OnHand * (x.CostPrice ?? 0m)) });
        var deadCount = await dead.CountAsync(ct);
        var deadValue = await dead.SumAsync(x => (decimal?)x.AtPrice, ct) ?? 0m;
        var deadRows = await dead.OrderByDescending(x => x.AtPrice).ThenBy(x => x.Sku).Take(s.Limit).ToListAsync(ct);
        var ids = deadRows.Select(d => d.VariantId).ToList();
        var lastSold = await (from i in db.OrderItems.AsNoTracking()
                              join o in s.Apply(db.Orders.AsNoTracking()).Where(ReportData.Countable) on i.OrderId equals o.Id
                              where ids.Contains(i.VariantId)
                              group o by i.VariantId into g
                              select new { g.Key, At = g.Max(o => o.PlacedAt) }).ToDictionaryAsync(x => x.Key, x => (DateTimeOffset?)x.At, ct);
        var deadDtos = deadRows.Select(d => new DeadStockRowDto(d.VariantId, d.Sku, d.Product, d.OnHand, ReportMath.Round2(d.AtPrice), s.CanSeeCost ? ReportMath.Round2(d.AtCost) : null, lastSold.GetValueOrDefault(d.VariantId))).ToList();

        string[] notes =
        [
            "Stock figures are a snapshot of now for the branches in scope; value at price uses the regular price (variant price override when set), value at cost uses the product cost price.",
            "Units of products without a cost price are counted in unitsWithoutCost and add nothing to the value at cost.",
            "Movements are the stock movements created in the period; In and Out are units.",
            $"Dead stock is stock on hand of variants that had no counted sale in the last {s.Days} days (parameter days).",
        ];
        return new InventoryReportDto(s.Echo(), clock.GetUtcNow(), !s.CanSeeCost, summary, byBranch, lowCount, lowRows, movements, deadCount, ReportMath.Round2(deadValue), deadDtos, notes);
    }

    public static ReportDoc ToDoc(InventoryReportDto r, string lang, string title, IReadOnlyList<KeyValuePair<string, string>> filters)
    {
        string[] bh = ["Branch", "On hand", "Value at price", "Value at cost"];
        var branch = new ReportSection("summary", ReportText.T("By branch", lang), [.. bh.Select(x => ReportText.T(x, lang))], [.. r.ByBranch.Select(b => (IReadOnlyList<string?>)[b.BranchCode, Cells.N(b.OnHand), Cells.M(b.ValueAtPrice), Cells.M(b.ValueAtCost)])],
            [ReportText.T("Total", lang), Cells.N(r.Summary.OnHand), Cells.M(r.Summary.ValueAtPrice), Cells.M(r.Summary.ValueAtCost)], Cells.Set(bh.Skip(1).Select(x => ReportText.T(x, lang)).ToArray()));
        string[] lh = ["SKU", "Product", "Branch", "On hand", "Reserved", "Available", "Reorder level"];
        var low = new ReportSection("low", ReportText.T("Low stock", lang), [.. lh.Select(x => ReportText.T(x, lang))],
            [.. r.LowStock.Select(x => (IReadOnlyList<string?>)[x.Sku, x.Product, x.BranchCode, Cells.N(x.OnHand), Cells.N(x.Reserved), Cells.N(x.Available), Cells.N(x.ReorderLevel)])], null, Cells.Set(lh.Skip(3).Select(x => ReportText.T(x, lang)).ToArray()));
        string[] mh = ["Reason", "Movements", "In", "Out"];
        var mv = new ReportSection("movements", ReportText.T("Stock movements", lang), [.. mh.Select(x => ReportText.T(x, lang))], [.. r.Movements.Select(x => (IReadOnlyList<string?>)[x.Reason, Cells.N(x.Movements), Cells.N(x.In), Cells.N(x.Out)])],
            null, Cells.Set(mh.Skip(1).Select(x => ReportText.T(x, lang)).ToArray()));
        string[] dh = ["SKU", "Product", "On hand", "Value at price", "Value at cost", "Last sold"];
        var dead = new ReportSection("dead", ReportText.T("Dead stock", lang), [.. dh.Select(x => ReportText.T(x, lang))],
            [.. r.DeadStock.Select(x => (IReadOnlyList<string?>)[x.Sku, x.Product, Cells.N(x.OnHand), Cells.M(x.ValueAtPrice), Cells.M(x.ValueAtCost), x.LastSoldAt is { } t ? Cells.D(Ops.BstDate(t)) : string.Empty])], null,
            Cells.Set(dh.Skip(2).Take(3).Select(x => ReportText.T(x, lang)).ToArray()));
        return new ReportDoc("inventory", title, filters, r.GeneratedAt, [branch, low, mv, dead], r.Notes);
    }
}

public sealed record DashboardPointDto(DateOnly Date, int Orders, decimal Sales);

public sealed record StatusCountDto(string Status, int Count);

public sealed record PendingActionsDto(int ToConfirm, int ToPack, int HeldByFraud, int ReturnsWaiting, int LowStock);

public sealed record TopProductDto(Guid ProductId, string Name, int Units, decimal Revenue);

public sealed record DashboardDto(
    DateTimeOffset GeneratedAt, Guid? BranchId, bool Consolidated, DateOnly Today, DashboardPointDto TodayTotals, IReadOnlyList<DashboardPointDto> Series, IReadOnlyList<StatusCountDto> OrdersByStatus, PendingActionsDto Pending,
    IReadOnlyList<TopProductDto> TopProducts, decimal CodPending, decimal CodInTransit, IReadOnlyList<string> Notes);

/// <summary>The admin dashboard numbers. Each figure is one cheap grouped query; sales are order grand totals (goods after discounts plus delivery) of orders that count as sales.</summary>
public sealed class DashboardService(ReportAccess access, ReportData data, IApplicationDbContext db, TimeProvider clock)
{
    public async Task<Result<DashboardDto>> GetAsync(Guid? branchId, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = Ops.BstDate(now);
        var probe = await access.ScopeAsync(new ReportQuery(today.AddDays(-13), today, branchId), Permissions.Reports.DashboardView, null, false, ct);
        if (probe.IsFailure)
        {
            return probe.Error!;
        }

        var s = probe.Value;
        var orders = (await data.OrderGroupsAsync(s, ReportData.OrderDay, ct)).ToDictionary(g => ReportMath.DayFromKey(g.Key));
        var sales = await s.Apply(db.Orders.AsNoTracking()).Where(o => o.PlacedAt >= s.FromAt && o.PlacedAt < s.ToAt).Where(ReportData.Countable)
            .GroupBy(ReportData.OrderDay).Select(g => new { g.Key, Sum = g.Sum(o => o.GrandTotal) }).ToDictionaryAsync(x => ReportMath.DayFromKey(x.Key), x => x.Sum, ct);
        var series = ReportMath.Buckets(s.From, s.To, "day").Select(d => new DashboardPointDto(d, orders.TryGetValue(d, out var o) ? o.Orders : 0, ReportMath.Round2(sales.GetValueOrDefault(d)))).ToList();

        var since = Ops.StartOfDay(today.AddDays(-29));
        var byStatus = await s.Apply(db.Orders.AsNoTracking()).Where(o => o.PlacedAt >= since).GroupBy(o => o.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var statuses = byStatus.OrderBy(x => (int)x.Key).Select(x => new StatusCountDto(x.Key.ToString(), x.Count)).ToList();

        var open = s.Apply(db.Orders.AsNoTracking());
        var heldStates = new[] { FraudState.Flagged, FraudState.InReview, FraudState.AdvanceRequested };
        var toConfirm = await open.CountAsync(o => o.Status == OrderStatus.Placed && !heldStates.Contains(o.FraudState), ct);
        var toPack = await open.CountAsync(o => o.Status == OrderStatus.Confirmed, ct);
        var held = await open.CountAsync(o => heldStates.Contains(o.FraudState) && (o.Status == OrderStatus.Placed || o.Status == OrderStatus.Confirmed || o.Status == OrderStatus.Packed), ct);
        var returns = await s.Apply(db.ReturnRequests.AsNoTracking()).CountAsync(r => r.Status == ReturnStatus.Requested || r.Status == ReturnStatus.Received, ct);
        var low = await s.Apply(db.StockLevels.AsNoTracking()).CountAsync(l => l.ReorderLevel > 0 && l.OnHand - l.Reserved <= l.ReorderLevel, ct);

        var monthScope = ReportScope.System(today.AddDays(-29), today, now);
        var ms = new ReportScope { From = monthScope.From, To = monthScope.To, FromAt = monthScope.FromAt, ToAt = monthScope.ToAt, Branches = s.Branches, CanSeeCost = false, Now = now };
        var top = (await data.ItemGroupsAsync(ms, l => new { l.ProductId, l.Name }, ct)).OrderByDescending(x => x.Units).ThenBy(x => x.Key.Name).Take(5)
            .Select(x => new TopProductDto(x.Key.ProductId, x.Key.Name, x.Units, ReportMath.Round2(x.Lines - x.DiscountShare))).ToList();

        var pending = await s.Apply(db.Shipments.AsNoTracking()).Where(x => x.Kind == ShipmentKind.Forward && x.CodCollectedAmount != null && !db.CodSettlements.Any(st => st.Id == x.CodSettlementId && st.Status == SettlementStatus.Settled))
            .SumAsync(x => x.CodCollectedAmount, ct) ?? 0m;
        var transit = await s.Apply(db.Shipments.AsNoTracking()).Where(x => x.Kind == ShipmentKind.Forward && x.CodAmount > 0m && x.CodCollectedAmount == null &&
                (x.Status == ShipmentStatus.Booked || x.Status == ShipmentStatus.PickedUp || x.Status == ShipmentStatus.InTransit || x.Status == ShipmentStatus.OutForDelivery)).SumAsync(x => (decimal?)x.CodAmount, ct) ?? 0m;

        string[] notes =
        [
            "Today and the 14-day series are Bangladesh days; sales are order grand totals of orders that count as sales (not cancelled).",
            "Orders by status covers orders placed in the last 30 days; pending actions are counted over all open orders.",
            "Top products are by units in the last 30 days; revenue is the line value after the shared order discount.",
        ];
        return new DashboardDto(now, branchId, branchId is null, today, series[^1], series, statuses, new PendingActionsDto(toConfirm, toPack, held, returns, low), top, ReportMath.Round2(pending), ReportMath.Round2(transit), notes);
    }
}
