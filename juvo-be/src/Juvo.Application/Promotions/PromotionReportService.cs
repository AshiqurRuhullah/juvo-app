using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Application.Ums;
using Juvo.Domain.Orders;
using Juvo.Domain.Promotions;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Promotions;

/// <summary>
/// Campaign report (FR-Z05). The numbers come from the immutable order line breakdown (<see cref="OrderItemAdjustment"/>), never from today's promotion settings, so they stay right after a promotion
/// was changed or archived. Cancelled orders are left out. A person who sees promotions only in some branches gets the orders of those branches.
/// </summary>
public sealed class PromotionReportService(IApplicationDbContext db, OrderAccess access, TimeProvider clock)
{
    private sealed class Row
    {
        public Guid PromotionId { get; init; }

        public Guid OrderId { get; init; }

        public Guid OrderItemId { get; init; }

        public int Quantity { get; init; }

        public decimal Net { get; init; }

        public decimal Amount { get; init; }
    }

    private IQueryable<Row> Rows(List<Guid>? visible, DateTimeOffset? from, DateTimeOffset? to)
    {
        var orders = db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => o.Status != OrderStatus.Cancelled && !o.IsDeleted);
        if (visible is not null)
        {
            orders = orders.Where(o => visible.Contains(o.BranchId));
        }

        if (from is { } f)
        {
            orders = orders.Where(o => o.PlacedAt >= f);
        }

        if (to is { } t)
        {
            orders = orders.Where(o => o.PlacedAt < t);
        }

        return from a in db.OrderItemAdjustments.AsNoTracking()
               join o in orders on a.OrderId equals o.Id
               join i in db.OrderItems.AsNoTracking() on a.OrderItemId equals i.Id
               where a.SourceId != null && (a.SourceType == "Promotion" || a.SourceType == "Bundle" || a.SourceType == "BuyXGetY")
               select new Row { PromotionId = a.SourceId!.Value, OrderId = o.Id, OrderItemId = i.Id, Quantity = i.Quantity, Net = i.LineTotal - i.PromotionDiscount, Amount = a.Amount };
    }

    private static PromotionReportDto Summarize(Promotion p, IEnumerable<Row> rows, DateTimeOffset now)
    {
        var list = rows.ToList();
        // A line can carry two adjustments of one promotion (never today), so units and revenue count each line once.
        var lines = list.GroupBy(r => r.OrderItemId).Select(g => g.First()).ToList();
        return new PromotionReportDto(p.Id, p.NameEn, p.Type, p.StatusAt(now), list.Select(r => r.OrderId).Distinct().Count(), lines.Sum(r => r.Quantity), lines.Sum(r => r.Net), list.Sum(r => r.Amount));
    }

    public async Task<Result<PromotionReportDto>> OneAsync(Guid id, DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var promotion = await db.Promotions.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, cancellationToken);
        if (promotion is null)
        {
            return Error.NotFound(ErrorCodes.Promo.NotFound, "Promotion not found.");
        }

        var rows = await Rows(OrderAccess.VisibleBranches(actor, Permissions.Promotions.PromotionView), from, to).Where(r => r.PromotionId == id).ToListAsync(cancellationToken);
        return Summarize(promotion, rows, clock.GetUtcNow());
    }

    public async Task<Result<PagedResult<PromotionReportDto>>> ListAsync(PageRequest request, PromotionReportQuery filter, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var promotions = db.Promotions.AsNoTracking().AsQueryable();
        if (filter.Type is { } type)
        {
            promotions = promotions.Where(p => p.Type == type);
        }

        var page = request.Normalized;
        var total = await promotions.CountAsync(cancellationToken);
        var rows = await promotions.OrderByDescending(p => p.CreatedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var ids = rows.Select(p => p.Id).ToList();
        var data = await Rows(OrderAccess.VisibleBranches(actor, Permissions.Promotions.PromotionView), filter.From, filter.To).Where(r => ids.Contains(r.PromotionId)).ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        return new PagedResult<PromotionReportDto>([.. rows.Select(p => Summarize(p, data.Where(r => r.PromotionId == p.Id), now))], total, page.Page, page.PageSize);
    }
}
