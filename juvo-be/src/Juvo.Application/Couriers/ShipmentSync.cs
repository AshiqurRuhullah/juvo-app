using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Application.Returns;
using Juvo.Domain.Couriers;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Couriers;

public enum SyncKind
{
    /// <summary>One or more status steps were applied.</summary>
    Applied = 1,

    /// <summary>The same courier event id was seen before: nothing changed.</summary>
    Duplicate = 2,

    /// <summary>Recorded on the timeline without a status change (a scan, a repeat of the current status, or a report that does not fit).</summary>
    Recorded = 3,
}

public sealed record SyncOutcome(SyncKind Kind, ShipmentStatus Status);

/// <summary>
/// The one place courier reports change shipments (module H and J). Webhooks, the polling job, the sandbox simulator and staff all come through here, so every report is
/// idempotent by its event id, skipped scans are filled in (a report of "delivered" after "picked up" also records the steps between), and the order tracks follow through
/// the normal <see cref="OrderWorkflow"/> (history, notifications, stock, cash collected). A forward parcel moves its order; a reverse parcel moves its return request.
/// </summary>
public sealed class ShipmentSync(IApplicationDbContext db, IUnitOfWork uow, OrderWorkflow workflow, ReturnOrderSync returnSync, TimeProvider clock)
{
    private static string Inferred(string eventId, ShipmentStatus step) => $"{eventId}#{step}";

    /// <summary>
    /// Applies a courier event to a shipment that is loaded with its events. Does not save: the caller saves inside its transaction. Never throws for a report that does not fit
    /// (it is recorded as a note on the timeline), so a late or odd report from a courier cannot break the order.
    /// </summary>
    public async Task<SyncOutcome> ApplyAsync(Shipment shipment, CourierEvent report, ShipmentEventSource source, string? actorName, CancellationToken cancellationToken)
    {
        if (shipment.Events.Any(e => e.ProviderEventId is { } id && (id == report.EventId || id.StartsWith(report.EventId + "#", StringComparison.Ordinal))))
        {
            return new SyncOutcome(SyncKind.Duplicate, shipment.Status);
        }

        var who = new Who(null, actorName ?? shipment.CourierName);
        var at = report.OccurredAt;

        if (report.Mapped is not { } target)
        {
            shipment.RecordScan(report.RawStatus, report.EventId, report.Note, report.Location, at, source, actorName);
            return new SyncOutcome(SyncKind.Recorded, shipment.Status);
        }

        if (target == shipment.Status)
        {
            shipment.RecordScan(report.RawStatus, report.EventId, report.Note, report.Location, at, source, actorName);
            return new SyncOutcome(SyncKind.Recorded, shipment.Status);
        }

        var path = ShipmentStateMachine.PathTo(shipment.Status, target);
        if (path.Count == 0)
        {
            shipment.RecordScan(report.RawStatus, report.EventId, $"Ignored: a parcel that is {shipment.Status} cannot become {target}. {report.Note}".Trim(), report.Location, at, source, actorName);
            return new SyncOutcome(SyncKind.Recorded, shipment.Status);
        }

        for (var i = 0; i < path.Count; i++)
        {
            var step = path[i];
            var last = i == path.Count - 1;
            var description = last ? report.Note : $"Passed through {step} (the courier skipped this scan).";
            shipment.MoveTo(step, last ? report.RawStatus : "inferred", last ? report.EventId : Inferred(report.EventId, step), description, last ? report.Location : null, at, source, actorName);
            await FollowAsync(shipment, step, last ? report : null, who, at, cancellationToken);
        }

        return new SyncOutcome(SyncKind.Applied, shipment.Status);
    }

    private async Task FollowAsync(Shipment shipment, ShipmentStatus step, CourierEvent? report, Who who, DateTimeOffset at, CancellationToken cancellationToken)
    {
        if (step == ShipmentStatus.Delivered && shipment.Kind == ShipmentKind.Forward && shipment.CodAmount > 0)
        {
            shipment.RecordCodCollected(report?.CodCollected ?? shipment.CodAmount, at);
        }

        string? problem = shipment.Kind == ShipmentKind.Forward
            ? await FollowOrderAsync(shipment, step, report?.Note, who, cancellationToken)
            : await FollowReturnAsync(shipment, step, who, cancellationToken);
        if (problem is not null)
        {
            shipment.RecordScan("sync-note", null, problem, null, clock.GetUtcNow(), ShipmentEventSource.System, "System");
        }
    }

    private async Task<string?> FollowOrderAsync(Shipment shipment, ShipmentStatus step, string? reason, Who who, CancellationToken cancellationToken)
    {
        if (ShipmentStateMachine.OrderStatusFor(step) is not { } target)
        {
            return null;
        }

        var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).Include(o => o.History).Include(o => o.Notes).FirstOrDefaultAsync(o => o.Id == shipment.OrderId, cancellationToken);
        if (order is null)
        {
            return "The order of this parcel was not found.";
        }

        if (order.Status == target)
        {
            return null; // staff already recorded this step by hand
        }

        if (!OrderStateMachine.CanMove(order.Status, target))
        {
            return $"The order is {order.Status} and was not moved to {target}.";
        }

        var moved = await workflow.AdvanceAsync(order, target, string.IsNullOrWhiteSpace(reason) ? $"{shipment.CourierName}: {step}" : reason.Trim(), who, cancellationToken);
        if (moved.IsFailure)
        {
            return $"The order was not moved to {target}: {moved.Error!.Message}";
        }

        if (step == ShipmentStatus.Returned && order.PaymentStatus is PaymentStatus.Paid or PaymentStatus.PartiallyRefunded)
        {
            order.AddNote(NoteKind.Internal, "The parcel came back to the shop. Refund what the customer paid in advance or online.", who.Id, who.Name, clock.GetUtcNow());
        }

        return null;
    }

    private async Task<string?> FollowReturnAsync(Shipment shipment, ShipmentStatus step, Who who, CancellationToken cancellationToken)
    {
        if (step is not (ShipmentStatus.PickedUp or ShipmentStatus.Delivered) || shipment.ReturnRequestId is not { } returnId)
        {
            return null;
        }

        var ret = await db.ReturnRequests.IgnoreQueryFilters(["BranchScope"]).FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);
        if (ret is null)
        {
            return "The return of this parcel was not found.";
        }

        var now = clock.GetUtcNow();
        if (step == ShipmentStatus.PickedUp && ret.Status == ReturnStatus.PickupScheduled)
        {
            ret.MarkPickedUp(who.Id, who.Name, now);
        }
        else if (step == ShipmentStatus.Delivered && ret.Status == ReturnStatus.PickedUp)
        {
            ret.MarkReceived(who.Id, who.Name, now);
        }
        else
        {
            return $"The return is {ret.Status}; it was not moved by the courier's {step} report.";
        }

        if (await returnSync.LoadOrderAsync(ret.OrderId, cancellationToken) is { } order)
        {
            await returnSync.SyncOrderAsync(order, who, cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Loads the shipment (by its id), applies the report and saves, in one transaction. A repeat of an event id that raced with another call answers Duplicate. Used by the webhook, the
    /// polling job and the simulator.
    /// </summary>
    public async Task<Result<SyncOutcome>> IngestAsync(Guid shipmentId, CourierEvent report, ShipmentEventSource source, string? actorName, CancellationToken cancellationToken)
    {
        try
        {
            return await uow.ExecuteInTransactionAsync<Result<SyncOutcome>>(async ct =>
            {
                var shipment = await db.Shipments.IgnoreQueryFilters(["BranchScope"]).Include(s => s.Events).FirstOrDefaultAsync(s => s.Id == shipmentId, ct);
                if (shipment is null)
                {
                    return Error.NotFound(ErrorCodes.Shipment.NotFound, "Shipment not found.");
                }

                var outcome = await ApplyAsync(shipment, report, source, actorName, ct);
                if (outcome.Kind != SyncKind.Duplicate)
                {
                    await db.SaveChangesAsync(ct);
                }

                return outcome;
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Shipment.InvalidTransition, "The shipment or order changed at the same moment. Try again.");
        }
        catch (DbUpdateException)
        {
            // A simultaneous replay won the unique index on the event id: this one is a duplicate.
            if (await db.ShipmentEvents.AsNoTracking().AnyAsync(e => e.ShipmentId == shipmentId && e.ProviderEventId == report.EventId, cancellationToken))
            {
                return new SyncOutcome(SyncKind.Duplicate, ShipmentStatus.Booked);
            }

            throw;
        }
    }
}
