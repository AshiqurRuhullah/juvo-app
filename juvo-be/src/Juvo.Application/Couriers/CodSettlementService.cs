using Juvo.Application.Abstractions;
using Juvo.Application.Billing;
using Juvo.Application.Orders;
using Juvo.Application.Payments;
using Juvo.Domain.Couriers;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Couriers;

/// <summary>
/// Cash on delivery settlement with couriers (module H). A batch gathers the parcels one courier delivered in one branch whose cash is not yet settled. Expected is what the courier
/// reported collecting; staff enter what really arrived (reconcile); a short or over parcel needs a note. Settling marks every order's cash Settled through the same payment
/// records and order track the single "settle" call of the payment module uses, with the batch number as the reference. Numbers <c>CS-BRANCH-YYMM-000001</c> are gap-free per branch.
/// </summary>
public sealed class CodSettlementService(IApplicationDbContext db, IUnitOfWork uow, OrderAccess access, InvoiceService invoices, OrderWorkflow workflow, TimeProvider clock, ICurrentUser currentUser)
{
    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error NotFound() => Error.NotFound(ErrorCodes.Shipment.SettlementNotFound, "Settlement not found.");

    private async Task<Error?> CheckAsync(Ums.Actor actor, Guid branchId, string permission, CancellationToken cancellationToken)
    {
        var refusal = await access.CheckAsync(actor, branchId, permission, cancellationToken);
        return refusal is { Type: ErrorType.NotFound } ? NotFound() : refusal;
    }

    private IQueryable<CodSettlement> Loaded() => db.CodSettlements.Include(s => s.Items);

    // ---- Reading ----------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<SettlementListItemDto>>> ListAsync(PageRequest request, SettlementQuery filter, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Cod.View);
        var query = db.CodSettlements.AsNoTracking().AsQueryable();
        if (visible is not null)
        {
            query = query.Where(s => visible.Contains(s.BranchId));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(s => s.Status == status);
        }

        if (filter.CourierId is { } courierId)
        {
            query = query.Where(s => s.CourierId == courierId);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(s => s.BranchId == branchId);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(s => s.Number.Contains(term) || (s.Reference != null && s.Reference.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(s => s.CreatedAt).Skip(page.Skip).Take(page.PageSize).Select(s => new { Settlement = s, Parcels = s.Items.Count }).ToListAsync(cancellationToken);
        return new PagedResult<SettlementListItemDto>(
            [.. rows.Select(x => new SettlementListItemDto(x.Settlement.Id, x.Settlement.Number, x.Settlement.BranchId, x.Settlement.CourierId, x.Settlement.CourierName, x.Settlement.Status, x.Parcels,
                x.Settlement.ExpectedTotal, x.Settlement.ReceivedTotal, x.Settlement.Difference, x.Settlement.FeesTotal, x.Settlement.CreatedAt, x.Settlement.SettledAt))],
            total, page.Page, page.PageSize);
    }

    public async Task<Result<SettlementDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var s = await Loaded().AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (s is null)
        {
            return NotFound();
        }

        return await CheckAsync(actor, s.BranchId, Permissions.Cod.View, cancellationToken) is { } refusal ? refusal : Map(s);
    }

    private static SettlementDto Map(CodSettlement s) =>
        new(s.Id, s.Number, s.BranchId, s.CourierId, s.CourierName, s.Status, s.ExpectedTotal, s.ReceivedTotal, s.Difference, s.FeesTotal, s.Reference, s.Note, s.CreatedByName, s.CreatedAt, s.ReconciledAt,
            s.ReconciledByName, s.SettledAt, s.SettledByName, s.CancelledAt, s.Items.Count(i => i.Difference < 0), s.Items.Count(i => i.Difference > 0),
            [.. s.Items.OrderBy(i => i.OrderNumber).Select(i => new SettlementItemDto(i.ShipmentId, i.OrderId, i.OrderNumber, i.ConsignmentId, i.Expected, i.Received, i.Difference, i.CourierFee, i.Note))]);

    /// <summary>The parcels waiting for settlement: delivered, cash collected, in no open batch. Lets the screen show what a new batch would hold.</summary>
    public async Task<Result<IReadOnlyList<ShipmentListItemDto>>> PendingAsync(Guid courierId, Guid? branchId, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Cod.View);
        var query = db.Shipments.AsNoTracking().Where(s => s.CourierId == courierId && s.Kind == ShipmentKind.Forward && s.Status == ShipmentStatus.Delivered && s.CodAmount > 0 && s.CodSettlementId == null);
        if (visible is not null)
        {
            query = query.Where(s => visible.Contains(s.BranchId));
        }

        if (branchId is { } b)
        {
            query = query.Where(s => s.BranchId == b);
        }

        var rows = await query.OrderBy(s => s.DeliveredAt).Take(500).ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<ShipmentListItemDto>>([.. rows.Select(s => new ShipmentListItemDto(s.Id, s.OrderId, s.OrderNumber, s.BranchId, s.CourierCode, s.CourierName, s.Kind, s.Status, s.ConsignmentId,
            s.TrackingUrl, s.DeliveryName, s.DeliveryDistrict, s.WeightGrams, s.CodAmount, s.CodCollectedAmount, s.Attempts, s.FailureReason, s.BookedAt, s.DeliveredAt))]);
    }

    // ---- Create -----------------------------------------------------------------------------------------

    public async Task<Result<SettlementDto>> CreateAsync(CreateSettlementRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var branchId = request.BranchId ?? currentUser.BranchId
                       ?? await db.Branches.AsNoTracking().Where(b => b.IsDefault && b.IsActive).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(cancellationToken);
        if (branchId is not { } branch)
        {
            return Invalid("branchId", "Choose the branch whose cash you are settling.");
        }

        if (await CheckAsync(actor, branch, Permissions.Cod.Settle, cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var courier = await db.Couriers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.CourierId, cancellationToken);
        if (courier is null)
        {
            return Error.NotFound(ErrorCodes.Courier.NotFound, "Courier not found.");
        }

        var who = access.WhoIs(actor);
        try
        {
            return await uow.ExecuteInTransactionAsync<Result<SettlementDto>>(async ct =>
            {
                var query = db.Shipments.Where(s => s.CourierId == courier.Id && s.BranchId == branch && s.Kind == ShipmentKind.Forward && s.Status == ShipmentStatus.Delivered && s.CodAmount > 0 && s.CodSettlementId == null);
                if (request.ShipmentIds is { Count: > 0 } ids)
                {
                    var wanted = ids.Distinct().ToList();
                    query = query.Where(s => wanted.Contains(s.Id));
                }

                var shipments = await query.OrderBy(s => s.DeliveredAt).Take(500).ToListAsync(ct);
                if (request.ShipmentIds is { Count: > 0 } asked && shipments.Count != asked.Distinct().Count())
                {
                    return Error.Conflict(ErrorCodes.Shipment.NothingToSettle, "Some of the chosen parcels are not delivered cash on delivery parcels of this courier and branch, or are already in a batch.");
                }

                // Only cash the order still shows as collected can be settled (a refunded or already settled order is left out).
                var orderIds = shipments.Select(s => s.OrderId).ToList();
                var collected = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => orderIds.Contains(o.Id) && o.PaymentStatus == PaymentStatus.CollectedByCourier).Select(o => o.Id).ToListAsync(ct);
                shipments = [.. shipments.Where(s => collected.Contains(s.OrderId))];
                if (shipments.Count == 0)
                {
                    return Error.Conflict(ErrorCodes.Shipment.NothingToSettle, "There is no delivered cash on delivery to settle for this courier in this branch.");
                }

                var branchRow = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == branch, ct);
                var number = await invoices.NextNumberAsync(branchRow, "CS", clock.GetUtcNow(), ct);
                var items = shipments.Select(s => new CodSettlementItem(s.Id, s.OrderId, s.OrderNumber, s.ConsignmentId, s.CodCollectedAmount ?? s.CodAmount, s.Cost)).ToList();
                var settlement = new CodSettlement(number, branch, courier.Id, courier.Name, items, request.Note, who.Name);
                db.CodSettlements.Add(settlement);
                foreach (var shipment in shipments)
                {
                    shipment.AttachToSettlement(settlement.Id);
                }

                await db.SaveChangesAsync(ct);
                return Map(settlement);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Shipment.SettlementState, "A parcel changed at the same moment. Try again.");
        }
    }

    // ---- Reconcile, settle, cancel ----------------------------------------------------------------------

    private async Task<Result<SettlementDto>> RunAsync(Guid id, string permission, Func<CodSettlement, Who, CancellationToken, Task<Result>> action, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<SettlementDto>>(async ct =>
            {
                var settlement = await Loaded().FirstOrDefaultAsync(s => s.Id == id, ct);
                if (settlement is null)
                {
                    return NotFound();
                }

                if (await CheckAsync(actor, settlement.BranchId, permission, ct) is { } refusal)
                {
                    return refusal;
                }

                var done = await action(settlement, access.WhoIs(actor), ct);
                if (done.IsFailure)
                {
                    return done.Error!;
                }

                await db.SaveChangesAsync(ct);
                return Map(settlement);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Shipment.SettlementState, "The settlement or an order changed at the same moment. Reload and try again.");
        }
    }

    /// <summary>Enters what the courier really paid per parcel (a parcel left out is taken as paid in full). Short or over needs a note on that parcel. Can be repeated until settled.</summary>
    public Task<Result<SettlementDto>> ReconcileAsync(Guid id, ReconcileSettlementRequest request, CancellationToken cancellationToken) =>
        RunAsync(id, Permissions.Cod.Settle, (s, who, _) =>
        {
            if (s.Status is not (SettlementStatus.Open or SettlementStatus.Reconciled))
            {
                return Task.FromResult(Result.Failure(Error.Conflict(ErrorCodes.Shipment.SettlementState, $"A settlement that is {s.Status} cannot be reconciled.")));
            }

            var given = request.Lines ?? [];
            if (given.Select(l => l.ShipmentId).Distinct().Count() != given.Count || given.Any(l => s.Items.All(i => i.ShipmentId != l.ShipmentId)))
            {
                return Task.FromResult(Result.Failure(Invalid("lines", "Each parcel can appear once, and only parcels of this batch.")));
            }

            var lines = new List<ReceivedLine>();
            foreach (var item in s.Items)
            {
                var line = given.FirstOrDefault(l => l.ShipmentId == item.ShipmentId);
                var received = line?.Received ?? item.Expected;
                if (received < 0 || decimal.Round(received, 2) != received)
                {
                    return Task.FromResult(Result.Failure(Error.Conflict(ErrorCodes.Shipment.AmountInvalid, $"The amount for order {item.OrderNumber} must be zero or more, with at most 2 decimals.")));
                }

                if (received != item.Expected && (string.IsNullOrWhiteSpace(line?.Note) || line.Note.Trim().Length > 300))
                {
                    return Task.FromResult(Result.Failure(Error.Conflict(ErrorCodes.Shipment.NoteRequired, $"Order {item.OrderNumber} is {(received < item.Expected ? "short" : "over")} by BDT {Math.Abs(received - item.Expected):0.##}. Write a note (up to 300 characters) saying why.")));
                }

                lines.Add(new ReceivedLine(item.ShipmentId, received, line?.Note));
            }

            s.Reconcile(lines, clock.GetUtcNow(), who.Name);
            return Task.FromResult(Result.Success());
        }, cancellationToken);

    /// <summary>The cash is accounted for: every order's cash becomes Settled with the batch number, and the difference stays on the batch. Needs a reconciled batch and a deposit reference.</summary>
    public Task<Result<SettlementDto>> SettleAsync(Guid id, SettleSettlementRequest request, CancellationToken cancellationToken) =>
        RunAsync(id, Permissions.Cod.Settle, async (s, who, ct) =>
        {
            var reference = request.Reference?.Trim();
            if (string.IsNullOrEmpty(reference) || reference.Length > 120)
            {
                return Invalid("reference", "Write the deposit or transfer reference (up to 120 characters).");
            }

            if (s.Status != SettlementStatus.Reconciled)
            {
                return Error.Conflict(ErrorCodes.Shipment.SettlementState, s.Status == SettlementStatus.Open ? "Reconcile the batch first: enter what the courier paid." : $"A settlement that is {s.Status} cannot be settled.");
            }

            var now = clock.GetUtcNow();
            var orderIds = s.Items.Select(i => i.OrderId).ToList();
            var orders = await db.Orders.IgnoreQueryFilters(["BranchScope"]).Where(o => orderIds.Contains(o.Id)).ToListAsync(ct);
            var payments = await db.Payments.IgnoreQueryFilters(["BranchScope"]).Include(p => p.Events).Where(p => orderIds.Contains(p.OrderId)).ToListAsync(ct);
            foreach (var item in s.Items)
            {
                var order = orders.First(o => o.Id == item.OrderId);
                if (order.PaymentStatus != PaymentStatus.CollectedByCourier)
                {
                    continue; // settled or refunded by hand since the batch was made: nothing to move
                }

                var orderPayments = payments.Where(p => p.OrderId == order.Id).ToList();
                var cod = PaymentRecords.EnsureCod(db, order, orderPayments, now) ?? throw new InvalidOperationException("The cash payment could not be recorded.");
                var detail = $"Settlement {s.Number}, reference {reference}" + (item.Difference is { } d && d != 0 ? $"; courier paid BDT {item.Received:0.##} of BDT {item.Expected:0.##} ({(d < 0 ? "short" : "over")} {Math.Abs(d):0.##})" : string.Empty);
                if (cod.Status == PaymentStatus.CollectedByCourier)
                {
                    cod.MoveTo(PaymentStatus.Settled, "Settled", detail, now, who.Id, who.Name);
                }

                cod.RecordReceipt($"{s.Number}/{reference}");
                var moved = workflow.ChangePayment(order, PaymentStatus.Settled, $"Settlement {s.Number} ({reference})", who);
                if (moved.IsFailure)
                {
                    return moved.Error!;
                }
            }

            s.Settle(reference, now, who.Name);
            return Result.Success();
        }, cancellationToken);

    /// <summary>Drops an open or reconciled batch; its parcels can go into another one.</summary>
    public Task<Result<SettlementDto>> CancelAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Permissions.Cod.Settle, async (s, _, ct) =>
        {
            if (s.Status is SettlementStatus.Settled or SettlementStatus.Cancelled)
            {
                return Error.Conflict(ErrorCodes.Shipment.SettlementState, $"A settlement that is {s.Status} cannot be cancelled.");
            }

            var shipmentIds = s.Items.Select(i => i.ShipmentId).ToList();
            var shipments = await db.Shipments.IgnoreQueryFilters(["BranchScope"]).Where(x => shipmentIds.Contains(x.Id)).ToListAsync(ct);
            foreach (var shipment in shipments)
            {
                shipment.DetachFromSettlement();
            }

            s.Cancel(clock.GetUtcNow());
            return Result.Success();
        }, cancellationToken);
}
