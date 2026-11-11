using Juvo.Application.Abstractions;
using Juvo.Domain.Couriers;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Couriers;

/// <summary>
/// Receives courier webhooks (module H) and runs the status poll. The adapter checks the signature and the replay window; an event id seen before is acknowledged and ignored
/// (idempotent); a report about a parcel we do not know is acknowledged without effect so the courier stops retrying. Everything goes through <see cref="ShipmentSync"/>.
/// </summary>
public sealed class CourierWebhookService(IApplicationDbContext db, ICourierProviderRegistry providers, ShipmentSync sync, TimeProvider clock)
{
    public async Task<Result<CourierWebhookResultDto>> ProcessAsync(string providerCode, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var provider = providers.Find(providerCode);
        if (provider is null)
        {
            return Error.NotFound(ErrorCodes.Courier.NotFound, "Unknown courier provider.");
        }

        if (!provider.IsConfigured)
        {
            return Error.Conflict(ErrorCodes.Courier.NotConfigured, "This courier is not set up.");
        }

        var parsed = provider.ParseWebhook(body, headers, clock.GetUtcNow());
        if (parsed.IsFailure)
        {
            return parsed.Error!;
        }

        var report = parsed.Value;
        var shipment = await (from s in db.Shipments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking()
                              join c in db.Couriers.IgnoreQueryFilters().AsNoTracking() on s.CourierId equals c.Id
                              where c.Provider == provider.Code && s.ConsignmentId == report.ConsignmentId
                              select new { s.Id, s.Status }).FirstOrDefaultAsync(cancellationToken);
        if (shipment is null)
        {
            return new CourierWebhookResultDto("ignored", null);
        }

        var applied = await sync.IngestAsync(shipment.Id, report, ShipmentEventSource.Webhook, $"{provider.DisplayName} webhook", cancellationToken);
        if (applied.IsFailure)
        {
            return applied.Error!;
        }

        var state = await db.Shipments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(s => s.Id == shipment.Id).Select(s => s.Status).FirstAsync(cancellationToken);
        return new CourierWebhookResultDto(applied.Value.Kind switch { SyncKind.Duplicate => "duplicate", SyncKind.Recorded => "recorded", _ => "processed" }, state.ToString());
    }

    /// <summary>
    /// Asks the courier about every open parcel (oldest check first, at most <paramref name="limit"/> per sweep) and applies what it reports. A courier that fails is skipped; the
    /// next sweep tries again. Returns how many parcels were asked and how many changed.
    /// </summary>
    public async Task<(int Checked, int Changed, int Failed)> PollOpenAsync(int limit, CancellationToken cancellationToken)
    {
        var open = ShipmentStateMachine.OpenStatuses;
        var rows = await (from s in db.Shipments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking()
                          join c in db.Couriers.IgnoreQueryFilters().AsNoTracking() on s.CourierId equals c.Id
                          where open.Contains(s.Status) && !s.IsDeleted
                          orderby s.LastPolledAt ?? s.BookedAt
                          select new { s.Id, s.ConsignmentId, c.Provider }).Take(limit).ToListAsync(cancellationToken);
        int asked = 0, changed = 0, failed = 0;
        foreach (var row in rows)
        {
            var provider = providers.Find(row.Provider);
            if (provider is not { IsConfigured: true })
            {
                continue;
            }

            asked++;
            var polledAt = clock.GetUtcNow();
            await db.Shipments.IgnoreQueryFilters(["BranchScope"]).Where(s => s.Id == row.Id).ExecuteUpdateAsync(set => set.SetProperty(s => s.LastPolledAt, polledAt), cancellationToken);
            try
            {
                var latest = await provider.FetchStatusAsync(row.ConsignmentId, cancellationToken);
                if (latest.IsFailure)
                {
                    failed++;
                    continue;
                }

                if (latest.Value is not { } report)
                {
                    continue;
                }

                var applied = await sync.IngestAsync(row.Id, report, ShipmentEventSource.Polling, $"{provider.DisplayName} poll", cancellationToken);
                if (applied.IsFailure)
                {
                    failed++;
                }
                else if (applied.Value.Kind != SyncKind.Duplicate)
                {
                    changed++;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TimeoutException or DbUpdateException or InvalidOperationException)
            {
                failed++;
            }
        }

        return (asked, changed, failed);
    }
}
