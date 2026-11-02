using Juvo.Application.Abstractions;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Ums;

/// <summary>Searches the audit log. Branch-scoped admins only see entries recorded in their own branches.</summary>
public sealed class AuditQueryService(IApplicationDbContext db, ActorContext actors)
{
    public async Task<Result<PagedResult<AuditLogDto>>> SearchAsync(PageRequest request, AuditQuery filter, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var query = db.AuditLogs.AsNoTracking().AsQueryable();
        if (!actor.IsGlobal)
        {
            var branches = actor.Access.AssignedBranchIds.ToList();
            query = query.Where(a => a.BranchId != null && branches.Contains(a.BranchId.Value));
        }

        if (!string.IsNullOrWhiteSpace(filter.Entity))
        {
            var entity = filter.Entity.Trim();
            query = query.Where(a => a.Entity == entity);
        }

        if (!string.IsNullOrWhiteSpace(filter.EntityId))
        {
            var entityId = filter.EntityId.Trim();
            query = query.Where(a => a.EntityId == entityId);
        }

        if (filter.UserId is { } userId)
        {
            query = query.Where(a => a.UserId == userId);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(a => a.BranchId == branchId);
        }

        if (filter.Action is { } action)
        {
            query = query.Where(a => a.Action == action);
        }

        if (filter.From is { } from)
        {
            query = query.Where(a => a.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(a => a.OccurredAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(a => a.Entity.Contains(term) || a.EntityId.Contains(term) || (a.CorrelationId != null && a.CorrelationId.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(a => a.OccurredAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);

        var userIds = rows.Where(r => r.UserId != null).Select(r => r.UserId!.Value).Distinct().ToList();
        var names = await db.Users.AsNoTracking().IgnoreQueryFilters().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        var items = rows.Select(a => new AuditLogDto(a.Id, a.UserId, a.UserId is { } id ? names.GetValueOrDefault(id) : null, a.BranchId,
            a.Entity, a.EntityId, a.Action, a.OldValue, a.NewValue, a.CorrelationId, a.IpAddress, a.OccurredAt)).ToList();
        return new PagedResult<AuditLogDto>(items, total, page.Page, page.PageSize);
    }
}
