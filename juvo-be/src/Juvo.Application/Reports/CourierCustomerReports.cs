using Juvo.Application.Abstractions;
using Juvo.Domain.Couriers;
using Juvo.Domain.Customers;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Reports;

public sealed record CourierRowDto(
    Guid CourierId, string Courier, int Parcels, int Delivered, decimal DeliveredRate, decimal? AvgDeliveryHours, int Failed, decimal FailedRate, int Returned, decimal ReturnedRate, decimal TotalCost,
    decimal CostPerParcel, decimal CodCollected, decimal CodSettled, decimal CodPending);

public sealed record CourierReportDto(ReportFiltersDto Filters, DateTimeOffset GeneratedAt, CourierRowDto Totals, IReadOnlyList<CourierRowDto> Rows, IReadOnlyList<string> Notes);

/// <summary>FR-P05: per courier, parcels booked in the period (forward parcels, cancelled ones excluded), delivery, failure and return rates, cost, and COD cash collected versus settled.</summary>
public sealed class CourierReportService(ReportAccess access, IApplicationDbContext db, TimeProvider clock)
{
    public async Task<Result<CourierReportDto>> GetAsync(ReportQuery q, string permission, CancellationToken ct)
    {
        var scope = await access.ScopeAsync(q, permission, null, true, ct);
        return scope.IsFailure ? scope.Error! : await BuildAsync(scope.Value, ct);
    }

    internal async Task<CourierReportDto> BuildAsync(ReportScope s, CancellationToken ct)
    {
        var parcels = s.Apply(db.Shipments.AsNoTracking()).Where(x => x.Kind == ShipmentKind.Forward && x.Status != ShipmentStatus.Cancelled && x.BookedAt >= s.FromAt && x.BookedAt < s.ToAt);
        var groups = await parcels.GroupBy(x => new { x.CourierId, x.CourierName }).Select(g => new
        {
            g.Key.CourierId,
            g.Key.CourierName,
            Parcels = g.Count(),
            Delivered = g.Count(x => x.Status == ShipmentStatus.Delivered),
            DeliveredMinutes = g.Where(x => x.Status == ShipmentStatus.Delivered && x.DeliveredAt != null).Sum(x => (long?)(x.DeliveredAt!.Value.ToUnixTimeSeconds() - x.BookedAt.ToUnixTimeSeconds())) / 60d ?? 0d,
            DeliveredWithTime = g.Count(x => x.Status == ShipmentStatus.Delivered && x.DeliveredAt != null),
            Failed = g.Count(x => x.Status == ShipmentStatus.DeliveryFailed || x.Status == ShipmentStatus.Returned),
            Returned = g.Count(x => x.Status == ShipmentStatus.Returned),
            Cost = g.Sum(x => x.Cost),
            Collected = g.Sum(x => x.CodCollectedAmount ?? 0m),
        }).ToListAsync(ct);

        // Cash actually received from the courier in settlements settled in the period, and the snapshot of cash collected by the courier and not settled yet.
        var settled = await s.Apply(db.CodSettlements.AsNoTracking()).Where(x => x.Status == SettlementStatus.Settled && x.SettledAt != null && x.SettledAt >= s.FromAt && x.SettledAt < s.ToAt)
            .GroupBy(x => x.CourierId).Select(g => new { g.Key, Sum = g.Sum(x => x.ReceivedTotal ?? 0m) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
        var pending = await s.Apply(db.Shipments.AsNoTracking()).Where(x => x.Kind == ShipmentKind.Forward && x.CodCollectedAmount != null && !db.CodSettlements.Any(st => st.Id == x.CodSettlementId && st.Status == SettlementStatus.Settled))
            .GroupBy(x => x.CourierId).Select(g => new { g.Key, Sum = g.Sum(x => x.CodCollectedAmount ?? 0m) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);

        var rows = groups.Select(g => new CourierRowDto(g.CourierId, g.CourierName, g.Parcels, g.Delivered, ReportMath.Percent(g.Delivered, g.Parcels),
            g.DeliveredWithTime == 0 ? null : ReportMath.Round2((decimal)g.DeliveredMinutes / 60m / g.DeliveredWithTime), g.Failed, ReportMath.Percent(g.Failed, g.Parcels), g.Returned, ReportMath.Percent(g.Returned, g.Parcels),
            ReportMath.Round2(g.Cost), g.Parcels == 0 ? 0m : ReportMath.Round2(g.Cost / g.Parcels), ReportMath.Round2(g.Collected), ReportMath.Round2(settled.GetValueOrDefault(g.CourierId)), ReportMath.Round2(pending.GetValueOrDefault(g.CourierId))))
            .OrderByDescending(r => r.Parcels).ThenBy(r => r.Courier, StringComparer.Ordinal).ToList();

        var totalParcels = groups.Sum(g => g.Parcels);
        var minutes = (decimal)groups.Sum(g => g.DeliveredMinutes);
        var withTime = groups.Sum(g => g.DeliveredWithTime);
        var totals = new CourierRowDto(Guid.Empty, "Total", totalParcels, groups.Sum(g => g.Delivered), ReportMath.Percent(groups.Sum(g => g.Delivered), totalParcels), withTime == 0 ? null : ReportMath.Round2(minutes / 60m / withTime),
            groups.Sum(g => g.Failed), ReportMath.Percent(groups.Sum(g => g.Failed), totalParcels), groups.Sum(g => g.Returned), ReportMath.Percent(groups.Sum(g => g.Returned), totalParcels), ReportMath.Round2(groups.Sum(g => g.Cost)),
            totalParcels == 0 ? 0m : ReportMath.Round2(groups.Sum(g => g.Cost) / totalParcels), ReportMath.Round2(groups.Sum(g => g.Collected)), ReportMath.Round2(settled.Values.Sum()), ReportMath.Round2(pending.Values.Sum()));
        string[] notes =
        [
            "Parcels are forward parcels booked in the period, cancelled ones excluded; rates are shares of those parcels.",
            "Failed counts parcels whose delivery failed or that came back (returned is the part that came back).",
            "Average delivery time is booking to delivery, over delivered parcels only.",
            "Cash collected is what couriers reported collecting for the parcels of the period; cash settled is what settlements settled in the period brought in; cash pending is the snapshot of collected cash not yet settled.",
        ];
        return new CourierReportDto(s.Echo(), clock.GetUtcNow(), totals, rows, notes);
    }

    public static ReportDoc ToDoc(CourierReportDto r, string lang, string title, IReadOnlyList<KeyValuePair<string, string>> filters)
    {
        string[] h = ["Courier", "Parcels", "Delivered", "Delivered %", "Avg delivery hours", "Failed %", "Returned %", "Cost per parcel", "Cash collected", "Cash settled", "Cash pending"];
        List<string?> Row(CourierRowDto x, string name) => [name, Cells.N(x.Parcels), Cells.N(x.Delivered), Cells.P(x.DeliveredRate), x.AvgDeliveryHours is { } a ? Cells.P(a) : string.Empty, Cells.P(x.FailedRate), Cells.P(x.ReturnedRate),
            Cells.M(x.CostPerParcel), Cells.M(x.CodCollected), Cells.M(x.CodSettled), Cells.M(x.CodPending)];
        var section = new ReportSection("courier", title, [.. h.Select(x => ReportText.T(x, lang))], [.. r.Rows.Select(x => (IReadOnlyList<string?>)Row(x, x.Courier))], Row(r.Totals, ReportText.T("Total", lang)),
            Cells.Set(h.Skip(1).Select(x => ReportText.T(x, lang)).ToArray()));
        return new ReportDoc("courier", title, filters, r.GeneratedAt, [section], r.Notes);
    }
}

public sealed record TopSpenderDto(Guid CustomerId, string? Name, string Mobile, int Orders, decimal Spent);

public sealed record CustomersReportDto(
    ReportFiltersDto Filters, DateTimeOffset GeneratedAt, int Customers, int NewCustomers, int ReturningCustomers, int OrdersFromNew, int OrdersFromReturning, decimal RevenueFromNew, decimal RevenueFromReturning,
    int GuestsCreated, int ConvertedToProfile, decimal ConversionRate, IReadOnlyList<TopSpenderDto> TopSpenders, IReadOnlyList<string> Notes);

/// <summary>FR-P06: customers who ordered in the period, new versus returning, top spenders, and how many guests created in the period became profile customers.</summary>
public sealed class CustomersReportService(ReportAccess access, IApplicationDbContext db, TimeProvider clock)
{
    public async Task<Result<CustomersReportDto>> GetAsync(ReportQuery q, string permission, CancellationToken ct)
    {
        var scope = await access.ScopeAsync(q, permission, null, true, ct);
        return scope.IsFailure ? scope.Error! : await BuildAsync(scope.Value, ct);
    }

    internal async Task<CustomersReportDto> BuildAsync(ReportScope s, CancellationToken ct)
    {
        var orders = s.Apply(db.Orders.AsNoTracking()).Where(o => o.PlacedAt >= s.FromAt && o.PlacedAt < s.ToAt).Where(ReportData.Countable);
        // A customer is "new" when no earlier order (in any branch) counted as a sale before the period started; only that yes/no is read across branches.
        var earlier = db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(ReportData.Countable);
        var from = s.FromAt;
        var perCustomer = orders.GroupBy(o => o.CustomerId).Select(g => new { CustomerId = g.Key, Orders = g.Count(), Spent = g.Sum(o => o.GrandTotal), IsNew = !earlier.Any(e => e.CustomerId == g.Key && e.PlacedAt < from) });
        var split = await perCustomer.GroupBy(x => x.IsNew).Select(g => new { IsNew = g.Key, Customers = g.Count(), Orders = g.Sum(x => x.Orders), Spent = g.Sum(x => x.Spent) }).ToListAsync(ct);
        var n = split.FirstOrDefault(x => x.IsNew);
        var r = split.FirstOrDefault(x => !x.IsNew);

        var top = await perCustomer.OrderByDescending(x => x.Spent).ThenBy(x => x.CustomerId).Take(s.Limit).ToListAsync(ct);
        var ids = top.Select(t => t.CustomerId).ToList();
        var people = await db.Customers.IgnoreQueryFilters().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => new { c.FullName, c.Mobile }, ct);
        var topRows = top.Select(t => new TopSpenderDto(t.CustomerId, people.GetValueOrDefault(t.CustomerId)?.FullName, people.GetValueOrDefault(t.CustomerId)?.Mobile ?? string.Empty, t.Orders, ReportMath.Round2(t.Spent))).ToList();

        // Guests created in the period: how many have since become quick or full profiles. Customers carry no branch, so for branch-limited runs only customers who ordered there count.
        var created = db.Customers.IgnoreQueryFilters().AsNoTracking().Where(c => c.CreatedAt >= s.FromAt && c.CreatedAt < s.ToAt);
        if (s.Branches is { } branches)
        {
            created = created.Where(c => db.Orders.Any(o => o.CustomerId == c.Id && branches.Contains(o.BranchId)));
        }

        var levels = await created.GroupBy(c => c.ProfileLevel).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
        var createdTotal = levels.Sum(l => l.Count);
        var converted = levels.Where(l => l.Key != ProfileLevel.Guest).Sum(l => l.Count);

        string[] notes =
        [
            "Customers are people who placed at least one order that counts as a sale in the period. New means no earlier counted order in any branch before the period started.",
            "Spent is the order grand total (goods plus delivery, after discounts).",
            "Conversion: of the customers created in the period, the share that is now a quick (verified mobile) or full profile instead of a guest. Customers are not branch data, so a branch-limited run counts only customers who ordered in that branch.",
        ];
        return new CustomersReportDto(s.Echo(), clock.GetUtcNow(), (n?.Customers ?? 0) + (r?.Customers ?? 0), n?.Customers ?? 0, r?.Customers ?? 0, n?.Orders ?? 0, r?.Orders ?? 0, ReportMath.Round2(n?.Spent ?? 0m),
            ReportMath.Round2(r?.Spent ?? 0m), createdTotal, converted, ReportMath.Percent(converted, createdTotal), topRows, notes);
    }

    public static ReportDoc ToDoc(CustomersReportDto r, string lang, string title, IReadOnlyList<KeyValuePair<string, string>> filters)
    {
        string[] sh = ["Customers", "Orders", "Revenue"];
        IReadOnlyList<IReadOnlyList<string?>> rows =
        [
            [ReportText.T("New customers", lang), Cells.N(r.NewCustomers), Cells.N(r.OrdersFromNew), Cells.M(r.RevenueFromNew)],
            [ReportText.T("Returning customers", lang), Cells.N(r.ReturningCustomers), Cells.N(r.OrdersFromReturning), Cells.M(r.RevenueFromReturning)],
        ];
        var summary = new ReportSection("summary", ReportText.T("Summary", lang), [string.Empty, .. sh.Select(x => ReportText.T(x, lang))], rows,
            [ReportText.T("Total", lang), Cells.N(r.Customers), Cells.N(r.OrdersFromNew + r.OrdersFromReturning), Cells.M(r.RevenueFromNew + r.RevenueFromReturning)], Cells.Set(sh.Select(x => ReportText.T(x, lang)).ToArray()));
        string[] th = ["Customer", "Mobile", "Orders", "Spent"];
        var top = new ReportSection("top", ReportText.T("Top spenders", lang), [.. th.Select(x => ReportText.T(x, lang))], [.. r.TopSpenders.Select(t => (IReadOnlyList<string?>)[t.Name, t.Mobile, Cells.N(t.Orders), Cells.M(t.Spent)])], null,
            Cells.Set(ReportText.T("Orders", lang), ReportText.T("Spent", lang)));
        string[] ch = ["Guests created", "Converted to profile", "Conversion %"];
        var conv = new ReportSection("conversion", ReportText.T("Conversion", lang), [.. ch.Select(x => ReportText.T(x, lang))], [[Cells.N(r.GuestsCreated), Cells.N(r.ConvertedToProfile), Cells.P(r.ConversionRate)]], null, Cells.Set(ch.Select(x => ReportText.T(x, lang)).ToArray()));
        return new ReportDoc("customers", title, filters, r.GeneratedAt, [summary, top, conv], r.Notes);
    }
}
