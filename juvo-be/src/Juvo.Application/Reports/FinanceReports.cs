using Juvo.Application.Abstractions;
using Juvo.Domain.Couriers;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Reports;

/// <summary>
/// One period (a day, week or month) of the profit and loss. Cost fields are null when the actor may not see cost prices. <c>GatewayFees</c> is always 0 and
/// <c>GatewayFeesRecorded</c> false: the payment records do not store what a gateway keeps, so the figure is honestly "not recorded" and never invented.
/// </summary>
public sealed record PnlBucketDto(
    string Label, DateOnly Start, int Orders, decimal GrossSales, decimal Discounts, decimal Refunds, decimal NetRevenue, decimal DeliveryCollected, decimal? CostOfGoods, int CostMissingLines,
    decimal? GrossProfit, decimal CourierCharges, decimal GatewayFees, bool GatewayFeesRecorded, decimal Expenses, decimal PurchasesReceived, decimal? NetProfit);

public sealed record ExpenseLineDto(Guid CategoryId, string Category, decimal Amount, int Count);

public sealed record PnlDto(ReportFiltersDto Filters, DateTimeOffset GeneratedAt, bool CostHidden, PnlBucketDto Total, IReadOnlyList<PnlBucketDto> Series, IReadOnlyList<ExpenseLineDto> ExpensesByCategory, IReadOnlyList<string> Notes);

public sealed record CodBlockDto(decimal Collected, decimal Settled, decimal PendingWithCouriers, decimal InTransit);

public sealed record FinanceBucketDto(
    string Label, DateOnly Start, int Orders, decimal GrossSales, decimal Discounts, decimal Refunds, decimal NetRevenue, decimal DeliveryCollected, decimal CourierCharges, decimal GatewayFees,
    decimal? CostOfGoods, decimal? GrossProfit);

public sealed record FinanceReportDto(
    ReportFiltersDto Filters, DateTimeOffset GeneratedAt, bool CostHidden, FinanceBucketDto Totals, IReadOnlyList<FinanceBucketDto> Rows, CodBlockDto Cod, decimal DeliveryBalance, IReadOnlyList<string> Notes);

internal static class FinanceNotes
{
    public static readonly string[] All =
    [
        "Revenue counts orders placed in the period that were not cancelled (and not returned without delivery), at the tax-inclusive prices stored on the order.",
        "Refunds are the completed refunds of the period for such orders; a refund can belong to an order of an earlier period.",
        "Cost of goods uses the cost price copied onto each order line when it was placed; lines placed before that was recorded have no cost and are counted in costMissingLines.",
        "Cost of goods is not reversed for goods that came back to stock after a return.",
        "Courier charges are the cost of parcels booked in the period (cancelled parcels excluded).",
        "Gateway fees are not recorded by the system: the figure is 0 and gatewayFeesRecorded is false.",
        "Purchases received are shown for information only; they are stock until sold and are not part of the profit.",
    ];
}

/// <summary>Builds the per-period money figures from system data. Automatic entries (refunds, courier charges, purchases) are read here and never copied into the ledger.</summary>
public sealed class FinanceBuilder(ReportData data)
{
    public async Task<(PnlBucketDto Total, List<PnlBucketDto> Series)> BuildAsync(ReportScope s, CancellationToken ct)
    {
        var orders = (await data.OrderGroupsAsync(s, ReportData.OrderDay, ct)).ToDictionary(g => ReportMath.DayFromKey(g.Key));
        var items = (await data.ItemGroupsAsync(s, l => l.Day, ct)).ToDictionary(g => ReportMath.DayFromKey(g.Key));
        var refunds = await data.RefundsByDayAsync(s, ct);
        var courier = await data.CourierChargesByDayAsync(s, ct);
        var expenses = await data.ExpensesByDayAsync(s, ct);
        var purchases = await data.PurchasesByDayAsync(s, ct);

        var days = orders.Keys.Concat(refunds.Keys).Concat(courier.Keys).Concat(expenses.Keys).Concat(purchases.Keys).Concat(items.Keys).Distinct().ToList();
        var perBucket = days.GroupBy(d => ReportMath.BucketStart(d, s.GroupBy)).ToDictionary(g => g.Key, g => g.ToList());

        PnlBucketDto Make(string label, DateOnly start, IEnumerable<DateOnly> ds)
        {
            var list = ds.ToList();
            decimal Sum(Dictionary<DateOnly, decimal> d) => list.Sum(x => d.GetValueOrDefault(x));
            var count = list.Sum(x => orders.TryGetValue(x, out var o) ? o.Orders : 0);
            var gross = list.Sum(x => orders.TryGetValue(x, out var o) ? o.Gross : 0m);
            var disc = list.Sum(x => orders.TryGetValue(x, out var o) ? o.Discount : 0m);
            var del = list.Sum(x => orders.TryGetValue(x, out var o) ? o.Delivery : 0m);
            var cost = list.Sum(x => items.TryGetValue(x, out var i) ? i.Cost : 0m);
            var missing = list.Sum(x => items.TryGetValue(x, out var i) ? i.MissingCostLines : 0);
            var refund = Sum(refunds);
            var net = gross - disc - refund;
            var courierSum = Sum(courier);
            var expense = Sum(expenses);
            decimal? cogs = s.CanSeeCost ? ReportMath.Round2(cost) : null;
            return new PnlBucketDto(label, start, count, ReportMath.Round2(gross), ReportMath.Round2(disc), ReportMath.Round2(refund), ReportMath.Round2(net), ReportMath.Round2(del), cogs, s.CanSeeCost ? missing : 0,
                s.CanSeeCost ? ReportMath.Round2(net - cost) : null, ReportMath.Round2(courierSum), 0m, false, ReportMath.Round2(expense), ReportMath.Round2(Sum(purchases)),
                s.CanSeeCost ? ReportMath.Round2(net + del - cost - courierSum - expense) : null);
        }

        var series = ReportMath.Buckets(s.From, s.To, s.GroupBy)
            .Select(b => Make(ReportMath.BucketLabel(b, s.GroupBy), b, perBucket.GetValueOrDefault(b) ?? [])).ToList();
        return (Make("Total", s.From, days), series);
    }

    /// <summary>COD figures: cash the couriers reported collecting in the period, settled in the period, and the snapshot of what is still with couriers or on its way.</summary>
    public static async Task<CodBlockDto> CodAsync(ReportScope s, IApplicationDbContext db, CancellationToken ct)
    {
        var collected = await s.Apply(db.Shipments.AsNoTracking()).Where(x => x.Kind == ShipmentKind.Forward && x.CodCollectedAt != null && x.CodCollectedAt >= s.FromAt && x.CodCollectedAt < s.ToAt)
            .SumAsync(x => x.CodCollectedAmount, ct) ?? 0m;
        var settled = await s.Apply(db.CodSettlements.AsNoTracking()).Where(x => x.Status == SettlementStatus.Settled && x.SettledAt != null && x.SettledAt >= s.FromAt && x.SettledAt < s.ToAt)
            .SumAsync(x => x.ReceivedTotal, ct) ?? 0m;
        var pending = await s.Apply(db.Shipments.AsNoTracking()).Where(x => x.Kind == ShipmentKind.Forward && x.CodCollectedAmount != null && !db.CodSettlements.Any(st => st.Id == x.CodSettlementId && st.Status == SettlementStatus.Settled))
            .SumAsync(x => x.CodCollectedAmount, ct) ?? 0m;
        var transit = await s.Apply(db.Shipments.AsNoTracking()).Where(x => x.Kind == ShipmentKind.Forward && x.CodAmount > 0m && x.CodCollectedAmount == null && (x.Status == ShipmentStatus.Booked || x.Status == ShipmentStatus.PickedUp || x.Status == ShipmentStatus.InTransit || x.Status == ShipmentStatus.OutForDelivery))
            .SumAsync(x => (decimal?)x.CodAmount, ct) ?? 0m;
        return new CodBlockDto(ReportMath.Round2(collected), ReportMath.Round2(settled), ReportMath.Round2(pending), ReportMath.Round2(transit));
    }
}

/// <summary>Profit and loss (FR-X04): the period total and a series, with automatic entries read from system data (FR-X02).</summary>
public sealed class PnlService(ReportAccess access, FinanceBuilder builder, ReportData data, TimeProvider clock)
{
    public async Task<Result<PnlDto>> GetAsync(ReportQuery q, CancellationToken ct)
    {
        var scope = await access.ScopeAsync(q, Permissions.Accounting.PnlView, ["day", "week", "month"], true, ct);
        return scope.IsFailure ? scope.Error! : await BuildAsync(scope.Value, ct);
    }

    internal async Task<PnlDto> BuildAsync(ReportScope s, CancellationToken ct)
    {
        var (total, series) = await builder.BuildAsync(s, ct);
        var cats = (await data.ExpensesByCategoryAsync(s, ct)).Select(c => new ExpenseLineDto(c.CategoryId, c.Name, ReportMath.Round2(c.Amount), c.Count)).ToList();
        return new PnlDto(s.Echo(), clock.GetUtcNow(), !s.CanSeeCost, total, series, cats, FinanceNotes.All);
    }
}

/// <summary>FR-P04 finance report: revenue, discounts, delivery collected versus paid to couriers, gateway fees, refunds, COD pending versus settled and gross profit.</summary>
public sealed class FinanceReportService(ReportAccess access, FinanceBuilder builder, IApplicationDbContext db, TimeProvider clock)
{
    public async Task<Result<FinanceReportDto>> GetAsync(ReportQuery q, string permission, CancellationToken ct)
    {
        var scope = await access.ScopeAsync(q, permission, ["day", "week", "month"], true, ct);
        return scope.IsFailure ? scope.Error! : await BuildAsync(scope.Value, ct);
    }

    internal async Task<FinanceReportDto> BuildAsync(ReportScope s, CancellationToken ct)
    {
        var (total, series) = await builder.BuildAsync(s, ct);
        var cod = await FinanceBuilder.CodAsync(s, db, ct);
        FinanceBucketDto Map(PnlBucketDto b) => new(b.Label, b.Start, b.Orders, b.GrossSales, b.Discounts, b.Refunds, b.NetRevenue, b.DeliveryCollected, b.CourierCharges, b.GatewayFees, b.CostOfGoods, b.GrossProfit);
        var t = Map(total);
        return new FinanceReportDto(s.Echo(), clock.GetUtcNow(), !s.CanSeeCost, t, [.. series.Select(Map)], cod, ReportMath.Round2(t.DeliveryCollected - t.CourierCharges), FinanceNotes.All);
    }

    public static ReportDoc ToDoc(FinanceReportDto r, string lang, string title, IReadOnlyList<KeyValuePair<string, string>> filters)
    {
        string[] h = ["Period", "Orders", "Gross", "Discounts", "Refunds", "Net revenue", "Delivery collected", "Courier charges paid", "Gateway fees (not recorded)", "Cost of goods", "Gross profit"];
        List<string?> Row(FinanceBucketDto b, string label) =>
            [label, Cells.N(b.Orders), Cells.M(b.GrossSales), Cells.M(b.Discounts), Cells.M(b.Refunds), Cells.M(b.NetRevenue), Cells.M(b.DeliveryCollected), Cells.M(b.CourierCharges), Cells.M(b.GatewayFees), Cells.M(b.CostOfGoods), Cells.M(b.GrossProfit)];
        var main = new ReportSection("finance", ReportText.T("Finance report", lang), [.. h.Select(x => ReportText.T(x, lang))], [.. r.Rows.Select(b => (IReadOnlyList<string?>)Row(b, b.Label))], Row(r.Totals, ReportText.T("Total", lang)), Cells.Set(h.Skip(1).Select(x => ReportText.T(x, lang)).ToArray()));
        string[] ch = ["Line", "Amount"];
        IReadOnlyList<IReadOnlyList<string?>> codRows =
        [
            ["COD collected", Cells.M(r.Cod.Collected)], ["COD settled", Cells.M(r.Cod.Settled)], ["COD pending with couriers", Cells.M(r.Cod.PendingWithCouriers)], ["COD in transit", Cells.M(r.Cod.InTransit)],
        ];
        var cod = new ReportSection("cod", "COD", [.. ch.Select(x => ReportText.T(x, lang))], [.. codRows.Select(x => (IReadOnlyList<string?>)[ReportText.T(x[0]!, lang), x[1]])], null, Cells.Set(ReportText.T("Amount", lang)));
        return new ReportDoc("finance", title, filters, r.GeneratedAt, [main, cod], r.Notes);
    }
}
