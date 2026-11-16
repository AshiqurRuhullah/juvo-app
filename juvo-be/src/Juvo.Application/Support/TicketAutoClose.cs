using Juvo.Application.Abstractions;
using Juvo.Application.Operations;
using Juvo.Application.Orders;
using Juvo.Domain.Support;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Support;

public sealed record AutoCloseResult(int Candidates, int Closed, int Days);

/// <summary>
/// Closes tickets that stayed Resolved for the configured number of days (setting <c>Support.AutoCloseDays</c>, default 7). Safe to run any number of times: a ticket is closed once,
/// the history row names "System", and a customer who reopened or replied in the meantime keeps their ticket because it is no longer Resolved.
/// </summary>
public sealed class TicketAutoCloseService(IApplicationDbContext db, TicketCore core, OpsSettingsService settings, TimeProvider clock)
{
    private const int BatchSize = 200;

    public async Task<AutoCloseResult> CloseDueAsync(CancellationToken cancellationToken)
    {
        var days = (await settings.ReadAsync(cancellationToken)).AutoCloseDays;
        var now = clock.GetUtcNow();
        var cutoff = now.AddDays(-days);
        var ids = await db.Tickets.AsNoTracking().Where(t => t.Status == TicketStatus.Resolved && t.ResolvedAt != null && t.ResolvedAt <= cutoff).OrderBy(t => t.ResolvedAt).Select(t => t.Id)
            .Take(BatchSize).ToListAsync(cancellationToken);
        var closed = 0;
        foreach (var id in ids)
        {
            var ticket = await core.LoadAsync(id, cancellationToken);
            if (ticket is null || ticket.Status != TicketStatus.Resolved || ticket.ResolvedAt is not { } at || at > cutoff)
            {
                continue;
            }

            var reason = $"Closed automatically after {days} days resolved";
            ticket.MoveTo(TicketStatus.Closed, reason, null, Who.System.Name, now);
            ticket.AddMessage(TicketMessageKind.System, $"This request was closed automatically after {days} days. You can reopen it from your account.", null, Who.System.Name, [], now);
            await db.SaveChangesAsync(cancellationToken); // a concurrent change fails the sweep; the next one retries
            closed++;
        }

        return new AutoCloseResult(ids.Count, closed, days);
    }
}
