using Juvo.Application.Abstractions;
using Juvo.Application.Storefront;
using Juvo.Domain.Branches;
using Juvo.Domain.Inventory;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Orders;

public sealed record StockLine(Guid VariantId, int Quantity);

/// <summary>
/// Moves stock for orders: reserve at placement, release on cancel, failure, timeout or branch change, deduct at packing (or delivery, by
/// setting). Every change is one atomic conditional UPDATE (<c>OnHand - Reserved &gt;= quantity</c> in the WHERE clause), so SQL Server takes
/// the row lock and two buyers can never take the last unit (NFR-019); the row version moves with every update and the database
/// check constraints are a second wall. Lines are always touched in variant order so two orders cannot deadlock each other.
/// Every step also writes an append-only <see cref="StockMovement"/>.
/// </summary>
public sealed class OrderStock(IApplicationDbContext db)
{
    public const string ReferenceType = "Order";

    /// <summary>Reserves every line in the branch or none. Returns the variant that could not be reserved, or null when all were.</summary>
    public async Task<Guid?> TryReserveAsync(Guid branchId, IReadOnlyList<StockLine> lines, CancellationToken cancellationToken)
    {
        var done = new List<StockLine>();
        foreach (var line in lines.OrderBy(l => l.VariantId))
        {
            var quantity = line.Quantity;
            var changed = await db.StockLevels
                .Where(s => s.BranchId == branchId && s.VariantId == line.VariantId && s.OnHand - s.Reserved >= quantity)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.Reserved, s => s.Reserved + quantity), cancellationToken);
            if (changed == 0)
            {
                await ReleaseAsync(branchId, done, cancellationToken); // undo what this call already took
                return line.VariantId;
            }

            done.Add(line);
        }

        return null;
    }

    public async Task ReleaseAsync(Guid branchId, IReadOnlyList<StockLine> lines, CancellationToken cancellationToken)
    {
        foreach (var line in lines.OrderBy(l => l.VariantId))
        {
            var quantity = line.Quantity;
            await db.StockLevels
                .Where(s => s.BranchId == branchId && s.VariantId == line.VariantId && s.Reserved >= quantity)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.Reserved, s => s.Reserved - quantity), cancellationToken);
        }
    }

    /// <summary>Packing: reserved units leave the shelf. Returns the variant that could not be deducted, or null.</summary>
    public async Task<Guid?> TryDeductAsync(Guid branchId, IReadOnlyList<StockLine> lines, CancellationToken cancellationToken)
    {
        var done = new List<StockLine>();
        foreach (var line in lines.OrderBy(l => l.VariantId))
        {
            var quantity = line.Quantity;
            var changed = await db.StockLevels
                .Where(s => s.BranchId == branchId && s.VariantId == line.VariantId && s.Reserved >= quantity && s.OnHand >= quantity)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.Reserved, s => s.Reserved - quantity).SetProperty(s => s.OnHand, s => s.OnHand - quantity), cancellationToken);
            if (changed == 0)
            {
                foreach (var undo in done)
                {
                    var q = undo.Quantity;
                    await db.StockLevels.Where(s => s.BranchId == branchId && s.VariantId == undo.VariantId)
                        .ExecuteUpdateAsync(set => set.SetProperty(s => s.Reserved, s => s.Reserved + q).SetProperty(s => s.OnHand, s => s.OnHand + q), cancellationToken);
                }

                return line.VariantId;
            }

            done.Add(line);
        }

        return null;
    }

    /// <summary>A packed order was cancelled or returned: the units go back on the shelf.</summary>
    public async Task RestockAsync(Guid branchId, IReadOnlyList<StockLine> lines, CancellationToken cancellationToken)
    {
        foreach (var line in lines.OrderBy(l => l.VariantId))
        {
            var quantity = line.Quantity;
            await db.StockLevels.Where(s => s.BranchId == branchId && s.VariantId == line.VariantId)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.OnHand, s => s.OnHand + quantity), cancellationToken);
        }
    }

    /// <summary>Writes one ledger line per stock line (after the quantities changed, so the on-hand figure after the change is correct).</summary>
    public async Task RecordAsync(Guid branchId, IReadOnlyList<StockLine> lines, StockReason reason, int sign, Order order, CancellationToken cancellationToken)
    {
        var variantIds = lines.Select(l => l.VariantId).ToList();
        var onHand = await db.StockLevels.AsNoTracking().Where(s => s.BranchId == branchId && variantIds.Contains(s.VariantId))
            .ToDictionaryAsync(s => s.VariantId, s => s.OnHand, cancellationToken);
        foreach (var line in lines)
        {
            db.StockMovements.Add(new StockMovement(branchId, line.VariantId, sign * line.Quantity, onHand.GetValueOrDefault(line.VariantId), reason,
                $"Order {order.Number}", ReferenceType, order.Id));
        }
    }

    /// <summary>The lines of an order that hold stock (variants that do not track stock hold nothing).</summary>
    public static List<StockLine> LinesOf(Order order) =>
        [.. order.Items.Where(i => i.TracksStock).GroupBy(i => i.VariantId).Select(g => new StockLine(g.Key, g.Sum(i => i.Quantity)))];
}

/// <summary>The fulfilling-branch choice (SRS 4A.3): default branch, a branch that holds every line, the nearest such branch, or manual.</summary>
public sealed class OrderRouter(IApplicationDbContext db, StockAvailability availability)
{
    /// <summary>
    /// Branches that could fulfil the whole order, best first, and the rule that ranked them. Only branches that sell online take part.
    /// Orders are not split across branches: one branch must hold every line.
    /// </summary>
    public async Task<(IReadOnlyList<Guid> Branches, string Rule)> CandidatesAsync(
        IReadOnlyList<StockLine> lines, RoutingMode mode, double? latitude, double? longitude, Guid? districtId, CancellationToken cancellationToken)
    {
        var rule = mode.ToString();
        var sellable = await availability.BranchIdsAsync(cancellationToken);
        var branches = await db.Branches.AsNoTracking().Where(b => sellable.Contains(b.Id)).ToListAsync(cancellationToken);
        if (branches.Count == 0)
        {
            return ([], rule);
        }

        var variantIds = lines.Select(l => l.VariantId).ToList();
        var levels = lines.Count == 0
            ? []
            : await db.StockLevels.AsNoTracking().Where(l => variantIds.Contains(l.VariantId) && sellable.Contains(l.BranchId)).ToListAsync(cancellationToken);

        bool Holds(Branch b) => lines.All(line => levels.FirstOrDefault(l => l.BranchId == b.Id && l.VariantId == line.VariantId) is { } level && level.Available >= line.Quantity);
        int Spare(Branch b) => levels.Where(l => l.BranchId == b.Id && variantIds.Contains(l.VariantId)).Sum(l => l.Available);

        var all = branches.Where(Holds).ToList();
        IEnumerable<Branch> ordered;
        switch (mode)
        {
            case RoutingMode.DefaultBranch:
                ordered = all.OrderByDescending(b => b.IsDefault).ThenByDescending(Spare).ThenBy(b => b.Code);
                break;
            case RoutingMode.Nearest:
                var pointLat = latitude;
                var pointLon = longitude;
                if ((pointLat is null || pointLon is null) && districtId is { } districtKey)
                {
                    var district = await db.Districts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == districtKey, cancellationToken);
                    (pointLat, pointLon) = (district?.Latitude, district?.Longitude);
                }

                ordered = pointLat is null || pointLon is null
                    ? all.OrderByDescending(b => b.IsDefault).ThenBy(b => b.Code)
                    : all.OrderBy(b => b.Latitude is null || b.Longitude is null ? double.MaxValue : Haversine(pointLat.Value, pointLon.Value, b.Latitude.Value, b.Longitude.Value))
                        .ThenByDescending(b => b.IsDefault).ThenBy(b => b.Code);
                break;
            default: // AllStock and Manual: the default branch when it holds everything, otherwise the branch with the most spare stock
                ordered = all.OrderByDescending(b => b.IsDefault).ThenByDescending(Spare).ThenBy(b => b.Code);
                break;
        }

        return ([.. ordered.Select(b => b.Id)], rule);
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        static double Rad(double deg) => deg * Math.PI / 180.0;
        var dLat = Rad(lat2 - lat1);
        var dLon = Rad(lon2 - lon1);
        var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(Rad(lat1)) * Math.Cos(Rad(lat2)) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
