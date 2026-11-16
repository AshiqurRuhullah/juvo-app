using Juvo.Application.Abstractions;
using Juvo.Application.Inventory;
using Juvo.Application.Operations;
using Juvo.Domain.Support;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Support;

/// <summary>
/// The support report (module Y, FR-Y05). Days are Bangladesh days. Backlog figures (open now, by status and priority, oldest) are as of now; period figures cover tickets created (new, by
/// category, by assignee, first response) or resolved (resolved per day, time to resolution) between the two dates. Without a branch it is the consolidated view of every branch the
/// caller may report on, global tickets included; with a branch it is that branch only.
/// </summary>
public sealed class SupportReportService(IApplicationDbContext db, InventoryAccess access, TimeProvider clock)
{
    public const int MaxDays = 366;
    public const int OldestCount = 10;

    private sealed record Row(Guid Id, string Number, string Subject, TicketStatus Status, TicketPriority Priority, string Category, Guid? AssigneeId, string? AssigneeName, DateTimeOffset CreatedAt,
        DateTimeOffset? FirstResponseAt, DateTimeOffset? ResolvedAt, DateTimeOffset ResponseDueAt, DateTimeOffset ResolveDueAt);

    public async Task<Result<SupportReportDto>> BuildAsync(DateOnly? from, DateOnly? to, Guid? branchId, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var now = clock.GetUtcNow();
        var today = Ops.BstDate(now);
        var end = to ?? today;
        var start = from ?? end.AddDays(-29);
        if (start > end || end.DayNumber - start.DayNumber + 1 > MaxDays)
        {
            return TicketLimits.Invalid("from", $"Choose a period of 1 to {MaxDays} days.");
        }

        var query = db.Tickets.AsNoTracking().AsQueryable();
        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Support.ReportView);
        if (branchId is { } only)
        {
            if (await access.CheckAsync(actor.Value, only, Permissions.Support.ReportView, cancellationToken) is { } refusal)
            {
                return refusal;
            }

            query = query.Where(t => t.BranchId == only);
        }
        else if (visible is not null)
        {
            var global = actor.Value.Access.HasAnywhere(Permissions.Support.ReportView);
            query = query.Where(t => (t.BranchId != null && visible.Contains(t.BranchId.Value)) || (global && t.BranchId == null));
        }

        var rows = await query.Select(t => new Row(t.Id, t.Number, t.Subject, t.Status, t.Priority, t.Category, t.AssigneeId, t.AssigneeName, t.CreatedAt, t.FirstResponseAt, t.ResolvedAt, t.ResponseDueAt, t.ResolveDueAt))
            .ToListAsync(cancellationToken);

        bool InPeriod(DateTimeOffset at) => Ops.BstDate(at) >= start && Ops.BstDate(at) <= end;
        var created = rows.Where(r => InPeriod(r.CreatedAt)).ToList();
        var resolved = rows.Where(r => r.ResolvedAt is { } at && InPeriod(at)).ToList();
        var open = rows.Where(r => TicketStateMachine.IsActive(r.Status)).ToList();
        var overdue = open.Count(r => r.Status is TicketStatus.Open or TicketStatus.InProgress && ((r.FirstResponseAt is null && now > r.ResponseDueAt) || now > r.ResolveDueAt));

        var daily = new List<DayCountDto>();
        for (var day = start; day <= end; day = day.AddDays(1))
        {
            daily.Add(new DayCountDto(day, created.Count(r => Ops.BstDate(r.CreatedAt) == day), resolved.Count(r => Ops.BstDate(r.ResolvedAt!.Value) == day)));
        }

        var firstResponse = TicketStats.Duration([.. created.Where(r => r.FirstResponseAt is not null).Select(r => (r.FirstResponseAt!.Value - r.CreatedAt).TotalMinutes)]);
        var resolution = TicketStats.Duration([.. resolved.Select(r => (r.ResolvedAt!.Value - r.CreatedAt).TotalMinutes)]);
        var activeStatuses = new[] { TicketStatus.Open, TicketStatus.InProgress, TicketStatus.Waiting };
        var byAssignee = created.GroupBy(r => r.AssigneeId).Select(g => new AssigneeLoadDto(
            g.Key, g.Key is null ? "Unassigned" : g.Select(r => r.AssigneeName).FirstOrDefault(n => n is not null) ?? "Staff", g.Count(), g.Count(r => TicketStateMachine.IsActive(r.Status)), g.Count(r => !TicketStateMachine.IsActive(r.Status))))
            .OrderByDescending(a => a.Total).ThenBy(a => a.Name, StringComparer.Ordinal).ToList();

        return new SupportReportDto(
            start, end, branchId, created.Count, resolved.Count, open.Count, overdue,
            [.. activeStatuses.Select(s => new CountDto(s.ToString(), open.Count(r => r.Status == s)))],
            [.. Enum.GetValues<TicketPriority>().Select(p => new CountDto(p.ToString(), open.Count(r => r.Priority == p)))],
            daily, firstResponse, resolution,
            [.. created.GroupBy(r => r.Category).Select(g => new CountDto(g.Key, g.Count())).OrderByDescending(c => c.Count).ThenBy(c => c.Key, StringComparer.Ordinal)],
            byAssignee,
            [.. open.OrderBy(r => r.CreatedAt).Take(OldestCount).Select(r => new OldestOpenDto(r.Id, r.Number, r.Subject, r.Status, r.Priority, r.CreatedAt, Math.Round((now - r.CreatedAt).TotalHours, 1)))]);
    }
}
