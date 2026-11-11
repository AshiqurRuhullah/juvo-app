using Juvo.Application.Abstractions;
using Juvo.Domain.Orders;
using Juvo.Shared.Contacts;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Orders;

/// <summary>
/// Staff side of orders (modules D, S and J basics): list, detail with the three tracks and timeline, confirm, pack, cancel, delivery steps, notes, bulk
/// actions, branch change, packing slip data and the social order desk. Reads are limited to the branches where the actor holds the permission; every action is
/// proven in the order's own branch (a wrong branch looks like "not found" and is audit-logged). Changes run in one transaction with their stock effect.
/// </summary>
public sealed class OrdersService(
    IApplicationDbContext db, IUnitOfWork uow, OrderAccess access, OrderWorkflow workflow, OrderPlacement placement, CheckoutService checkout, FraudService fraud, IFileStorage storage,
    ICurrentUser currentUser, TimeProvider clock)
{
    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static readonly IReadOnlySet<string> SortFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "placedAt", "total", "number", "status" };

    // ---- Reading ----------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<OrderListItemDto>>> ListAsync(PageRequest request, OrderListQuery filter, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Orders.OrderView);
        var query = from o in db.Orders.AsNoTracking()
                    join b in db.Branches.AsNoTracking() on o.BranchId equals b.Id
                    select new { Order = o, BranchCode = b.Code };
        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.Order.BranchId));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(x => x.Order.Status == status);
        }

        if (filter.PaymentStatus is { } payment)
        {
            query = query.Where(x => x.Order.PaymentStatus == payment);
        }

        if (filter.DeliveryStatus is { } deliveryStatus)
        {
            query = query.Where(x => x.Order.DeliveryStatus == deliveryStatus);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x => x.Order.BranchId == branchId);
        }

        if (filter.Source is { } source)
        {
            query = query.Where(x => x.Order.Source == source);
        }

        if (filter.FraudState is { } fraudState)
        {
            query = query.Where(x => x.Order.FraudState == fraudState);
        }

        if (filter.From is { } from)
        {
            query = query.Where(x => x.Order.PlacedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(x => x.Order.PlacedAt < to);
        }

        if (filter.Held is { } held)
        {
            query = held
                ? query.Where(x => x.Order.FraudState == FraudState.Flagged || x.Order.FraudState == FraudState.InReview || x.Order.FraudState == FraudState.AdvanceRequested)
                : query.Where(x => !(x.Order.FraudState == FraudState.Flagged || x.Order.FraudState == FraudState.InReview || x.Order.FraudState == FraudState.AdvanceRequested));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            var mobile = PhoneNumber.Normalize(term);
            query = query.Where(x => x.Order.Number.Contains(term) || x.Order.CustomerMobile.Contains(term) || (mobile != null && (x.Order.CustomerMobile == mobile || x.Order.ShipPhone == mobile)) ||
                                     (x.Order.CustomerName != null && x.Order.CustomerName.Contains(term)) || x.Order.ShipName.Contains(term));
        }

        var sort = request.ParseSort(SortFields) is { Count: > 0 } f ? f[0] : null;
        query = (sort?.Name.ToLowerInvariant(), sort?.Descending ?? true) switch
        {
            ("total", true) => query.OrderByDescending(x => x.Order.GrandTotal),
            ("total", false) => query.OrderBy(x => x.Order.GrandTotal),
            ("number", true) => query.OrderByDescending(x => x.Order.Number),
            ("number", false) => query.OrderBy(x => x.Order.Number),
            ("status", true) => query.OrderByDescending(x => x.Order.Status).ThenByDescending(x => x.Order.PlacedAt),
            ("status", false) => query.OrderBy(x => x.Order.Status).ThenByDescending(x => x.Order.PlacedAt),
            (_, false) => query.OrderBy(x => x.Order.PlacedAt),
            _ => query.OrderByDescending(x => x.Order.PlacedAt),
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(page.Skip).Take(page.PageSize)
            .Select(x => new { x.Order, x.BranchCode, Qty = x.Order.Items.Sum(i => i.Quantity) }).ToListAsync(cancellationToken);
        return new PagedResult<OrderListItemDto>(
            [.. rows.Select(x => new OrderListItemDto(x.Order.Id, x.Order.Number, x.Order.BranchId, x.BranchCode, x.Order.Source, x.Order.Status, x.Order.PaymentStatus, x.Order.DeliveryStatus,
                x.Order.PaymentMethod, x.Order.CustomerName ?? x.Order.ShipName, x.Order.CustomerMobile, x.Order.ShipDistrict ?? x.Order.ShipCity, x.Qty, x.Order.GrandTotal,
                x.Order.FraudState, x.Order.RiskScore, x.Order.IsHeld, x.Order.PlacedAt))],
            total, page.Page, page.PageSize);
    }

    private IQueryable<Order> Loaded() =>
        db.Orders.Include(o => o.Items).Include(o => o.History).Include(o => o.Notes).Include(o => o.Flags);

    public async Task<Result<OrderDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var order = await Loaded().AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
        }

        var refusal = await access.CheckAsync(actor, order.BranchId, Permissions.Orders.OrderView, cancellationToken);
        return refusal is not null ? refusal : await DetailAsync(order, cancellationToken);
    }

    private async Task<OrderDetailDto> DetailAsync(Order o, CancellationToken cancellationToken)
    {
        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == o.BranchId, cancellationToken);
        var stats = await fraud.StatsAsync(o.CustomerId, cancellationToken);
        var log = await fraud.LogAsync(o.Id, cancellationToken);
        return new OrderDetailDto(
            o.Id, o.Number, o.BranchId, branch.Code, branch.NameEn, o.Source, o.Status, o.PaymentStatus, o.DeliveryStatus, o.PaymentMethod, o.CustomerId, o.CustomerMobile, o.CustomerName,
            o.CustomerEmail, OrderViews.Shipping(o), OrderViews.Money(o), o.WeightGrams, o.ZoneCode, o.RoutingRule, o.RoutingPending, o.StockDeducted, o.FraudState, o.RiskScore, o.IsHeld, o.PlacedAt,
            o.ConfirmedAt, o.PackedAt, o.DeliveredAt, o.CancelledAt, o.CancelReason, o.PaymentHoldExpiresAt, o.OtpVerified, o.CreatedByStaffId, OrderViews.Lines(o, storage),
            OrderViews.StaffTimeline(o), [.. o.Notes.OrderBy(n => n.At).Select(n => new NoteDto(n.Id, n.Kind, n.Text, n.AuthorName, n.At))],
            [.. o.Flags.OrderBy(f => f.At).Select(f => new RiskFlagDto(f.RuleCode, f.Message, f.Score, f.Action, f.At))], log, stats,
            [.. OrderStateMachine.NextFor(o.Status).Select(s => s.ToString())], [.. OrderStateMachine.NextFor(o.PaymentStatus).Select(s => s.ToString())]);
    }

    // ---- Changing ---------------------------------------------------------------------------------------

    private async Task<Result<T>> RunAsync<T>(Guid id, string permission, Func<Order, Who, CancellationToken, Task<Result>> action, Func<Order, CancellationToken, Task<T>> view, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<T>>(async ct =>
            {
                var order = await Loaded().FirstOrDefaultAsync(o => o.Id == id, ct);
                if (order is null)
                {
                    return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
                }

                var refusal = await access.CheckAsync(actor, order.BranchId, permission, ct);
                if (refusal is not null)
                {
                    return refusal;
                }

                var done = await action(order, access.WhoIs(actor), ct);
                if (done.IsFailure)
                {
                    throw new PlacementRejected(done.Error!); // undo any stock change made so far
                }

                await db.SaveChangesAsync(ct);
                return Result.Success(await view(order, ct));
            }, cancellationToken);
        }
        catch (PlacementRejected rejected)
        {
            return rejected.Error;
        }
    }

    private Task<Result<OrderDetailDto>> MutateAsync(Guid id, string permission, Func<Order, Who, CancellationToken, Task<Result>> action, CancellationToken cancellationToken) =>
        RunAsync(id, permission, action, DetailAsync, cancellationToken);

    public Task<Result<OrderDetailDto>> ConfirmAsync(Guid id, ConfirmOrderRequest request, CancellationToken cancellationToken) =>
        MutateAsync(id, Permissions.Orders.OrderConfirm, (o, who, _) => Task.FromResult(workflow.Confirm(o, request.AdvanceReceived, who)), cancellationToken);

    public Task<Result<OrderDetailDto>> PackAsync(Guid id, CancellationToken cancellationToken) =>
        MutateAsync(id, Permissions.Orders.OrderPack, (o, who, ct) => workflow.PackAsync(o, who, ct), cancellationToken);

    public async Task<Result<OrderDetailDto>> CancelAsync(Guid id, CancelOrderRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason))
        {
            return Invalid("reason", "Give the reason for cancelling the order.");
        }

        if (reason.Length > 300)
        {
            return Invalid("reason", "The reason can have at most 300 characters.");
        }

        return await MutateAsync(id, Permissions.Orders.OrderCancel, (o, who, ct) => workflow.CancelAsync(o, reason, who, ct), cancellationToken);
    }

    /// <summary>Records a delivery step (handed to courier, in transit, out for delivery, delivered, failed, return). Only moves the state machine allows are accepted.</summary>
    public Task<Result<OrderDetailDto>> TransitionAsync(Guid id, TransitionRequest request, CancellationToken cancellationToken) =>
        request.Reason is { Length: > 300 }
            ? Task.FromResult<Result<OrderDetailDto>>(Invalid("reason", "The reason can have at most 300 characters."))
            : MutateAsync(id, Permissions.Orders.OrderEdit, (o, who, ct) => workflow.AdvanceAsync(o, request.To, request.Reason, who, ct), cancellationToken);

    public async Task<Result<OrderDetailDto>> AddNoteAsync(Guid id, AddNoteRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Trim().Length > 1000 || !Enum.IsDefined(request.Kind))
        {
            return Invalid("text", "Write a note of up to 1000 characters.");
        }

        return await MutateAsync(id, Permissions.Orders.OrderNote, (o, who, _) =>
        {
            o.AddNote(request.Kind, request.Text, who.Id, who.Name, clock.GetUtcNow());
            return Task.FromResult(Result.Success());
        }, cancellationToken);
    }

    public async Task<Result<OrderDetailDto>> ReassignAsync(Guid id, ReassignRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var target = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BranchId, cancellationToken);
        if (target is null || !target.IsActive)
        {
            return Error.NotFound(ErrorCodes.Ums.BranchNotFound, "Branch not found.");
        }

        if (!actor.IsGlobal && !actor.Access.BranchPermissions.ContainsKey(target.Id))
        {
            return Error.Forbidden(ErrorCodes.Order.BranchNotAllowed, "You can only move an order to a branch you are assigned to.");
        }

        return await MutateAsync(id, Permissions.Orders.OrderReassignBranch, (o, who, ct) => workflow.ReassignAsync(o, request.BranchId, request.Reason?.Trim(), who, ct), cancellationToken);
    }

    // ---- Bulk -------------------------------------------------------------------------------------------

    public async Task<Result<BulkOrderResult>> BulkAsync(BulkOrderRequest request, CancellationToken cancellationToken)
    {
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("confirm" or "cancel" or "print"))
        {
            return Invalid("action", "Choose confirm, cancel or print.");
        }

        var ids = request.Ids?.Distinct().ToList() ?? [];
        if (ids.Count is 0 or > 100)
        {
            return Invalid("ids", "Choose between 1 and 100 orders.");
        }

        if (action == "cancel" && string.IsNullOrWhiteSpace(request.Reason))
        {
            return Invalid("reason", "Give the reason for cancelling the orders.");
        }

        var results = new List<BulkItemResult>();
        var slips = new List<PackingSlipDto>();
        foreach (var id in ids)
        {
            var number = await db.Orders.AsNoTracking().Where(o => o.Id == id).Select(o => o.Number).FirstOrDefaultAsync(cancellationToken);
            Result outcome;
            switch (action)
            {
                case "confirm":
                    outcome = await ConfirmAsync(id, new ConfirmOrderRequest(request.AdvanceReceived), cancellationToken);
                    break;
                case "cancel":
                    outcome = await CancelAsync(id, new CancelOrderRequest(request.Reason), cancellationToken);
                    break;
                default:
                    var slip = await SlipAsync(id, cancellationToken);
                    if (slip.IsSuccess)
                    {
                        slips.Add(slip.Value);
                    }

                    outcome = slip;
                    break;
            }

            results.Add(new BulkItemResult(id, number, outcome.IsSuccess, outcome.Error?.Code, outcome.Error?.Message));
        }

        return new BulkOrderResult(action, results.Count(r => r.Ok), results.Count(r => !r.Ok), results, slips);
    }

    // ---- Packing slip -----------------------------------------------------------------------------------

    public async Task<Result<PackingSlipDto>> SlipAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var order = await Loaded().AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
        }

        var refusal = await access.CheckAsync(actor, order.BranchId, Permissions.Orders.OrderPrint, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == order.BranchId, cancellationToken);
        var discount = order.CouponDiscount + order.ManualDiscount;
        // A confirmed order with an advance requirement had the advance marked as received (confirmation insists on it), so the courier collects the rest.
        var advanceTaken = order.ConfirmedAt is not null ? order.AdvanceRequired : 0m;
        var collect = order.IsOnlinePayment || order.PaymentStatus == PaymentStatus.Paid ? 0m : Math.Max(0m, order.GrandTotal - advanceTaken);
        var lines = order.Items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Sku).Select(i => new PackingSlipLineDto(i.Sku, i.Name, i.ColorName, i.Size, i.Quantity, i.UnitPrice, i.LineTotal)).ToList();
        var customerNote = string.Join(" / ", order.Notes.Where(n => n.Kind == NoteKind.Customer).OrderBy(n => n.At).Select(n => n.Text));
        return new PackingSlipDto(order.Number, order.PlacedAt, branch.NameEn, branch.PickupAddress ?? branch.AddressLine, branch.Phone, OrderViews.Shipping(order),
            string.IsNullOrEmpty(customerNote) ? null : customerNote, order.PaymentMethod, order.PaymentStatus, order.Subtotal, discount, order.DeliveryCharge, order.GrandTotal, collect, lines,
            lines.Sum(l => l.Quantity), order.WeightGrams);
    }

    // ---- Social order desk ------------------------------------------------------------------------------

    public async Task<Result<OrderDetailDto>> CreateManualAsync(ManualOrderRequest request, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        // The branch of the request, else the one the staff member works in, else the default branch (a head-office user with "All branches").
        var branchId = request.BranchId ?? currentUser.BranchId
                       ?? await db.Branches.AsNoTracking().Where(b => b.IsDefault && b.IsActive).Select(b => (Guid?)b.Id).FirstOrDefaultAsync(cancellationToken);
        if (branchId is not { } targetBranch)
        {
            return Invalid("branchId", "Choose the branch that takes this order.");
        }

        var refusal = await access.CheckAsync(actor, targetBranch, Permissions.Orders.OrderCreate, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        if (!await access.BranchSellsAsync(targetBranch, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Order.BranchNotAllowed, "This branch is not active.");
        }

        if (!Enum.IsDefined(request.Source) || request.Source == OrderSource.Web)
        {
            return Invalid("source", "Choose where the order came from: Facebook, WhatsApp, Phone, WalkIn or Other.");
        }

        var mobile = PhoneNumber.Normalize(request.CustomerMobile);
        if (mobile is null)
        {
            return new Error(ErrorCodes.Auth.InvalidMobile, "Enter a valid mobile number, for example 01712345678.", ErrorType.Validation,
                new Dictionary<string, string[]> { ["customerMobile"] = ["Enter a valid mobile number."] });
        }

        var discount = request.Discount ?? 0m;
        if (discount > 0 && !actor.Can(Permissions.Orders.OrderApplyDiscount, targetBranch))
        {
            return Error.Forbidden(ErrorCodes.Auth.Forbidden, "You do not have permission to give a discount.");
        }

        if (request.DeliveryChargeOverride is { } overrideCharge && (overrideCharge < 0 || !actor.Can(Permissions.Orders.OrderOverrideDelivery, targetBranch)))
        {
            return overrideCharge < 0
                ? Invalid("deliveryChargeOverride", "The delivery charge cannot be negative.")
                : Error.Forbidden(ErrorCodes.Auth.Forbidden, "You do not have permission to change the delivery charge.");
        }

        if (request.Lines is null or { Count: 0 } || request.Lines.Count > 50)
        {
            return Invalid("lines", "Add between 1 and 50 items.");
        }

        var wanted = new List<StockLine>();
        for (var n = 0; n < request.Lines.Count; n++)
        {
            var line = request.Lines[n];
            Guid? variantId = line.VariantId;
            if (variantId is null && !string.IsNullOrWhiteSpace(line.Sku))
            {
                var sku = line.Sku.Trim().ToUpperInvariant();
                variantId = await db.ProductVariants.AsNoTracking().Where(v => v.Sku == sku).Select(v => (Guid?)v.Id).FirstOrDefaultAsync(cancellationToken);
            }

            if (variantId is null)
            {
                return Invalid($"lines[{n}].sku", "This SKU was not found.");
            }

            if (line.Quantity < 1)
            {
                return Invalid($"lines[{n}].quantity", "Quantity must be at least 1.");
            }

            wanted.Add(new StockLine(variantId.Value, line.Quantity));
        }

        var merged = wanted.GroupBy(w => w.VariantId).Select(g => new StockLine(g.Key, g.Sum(x => x.Quantity))).ToList();
        var shipping = await checkout.ResolveAddressAsync(null, null, request.Address, mobile, cancellationToken);
        if (shipping.IsFailure)
        {
            return shipping.Error!;
        }

        var who = access.WhoIs(actor);
        var input = new PlacementInput(
            request.Source, who, true, mobile, request.CustomerName?.Trim(), request.CustomerEmail?.Trim(), null, false, shipping.Value, merged, request.CouponCode, discount,
            request.DeliveryChargeOverride, request.PaymentMethod, request.CustomerNote?.Trim(), request.InternalNote?.Trim(), request.IdempotencyKey?.Trim(), request.ConfirmDuplicate, null, null,
            currentUser.IpAddress, null, true, targetBranch, null);
        var placed = await placement.PlaceAsync(input, cancellationToken);
        if (placed.IsFailure)
        {
            return placed.Error!;
        }

        var order = await Loaded().AsNoTracking().FirstAsync(o => o.Id == placed.Value.Order.Id, cancellationToken);
        return await DetailAsync(order, cancellationToken);
    }

    /// <summary>Orders and value per source in a period, for the owner's daily count (FR-S06).</summary>
    public async Task<Result<IReadOnlyList<OrderSourceCountDto>>> SourceCountsAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Orders.SourceView);
        var query = db.Orders.AsNoTracking().Where(o => o.Status != OrderStatus.Cancelled);
        if (visible is not null)
        {
            query = query.Where(o => visible.Contains(o.BranchId));
        }

        if (from is { } f)
        {
            query = query.Where(o => o.PlacedAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(o => o.PlacedAt < t);
        }

        var rows = await query.GroupBy(o => o.Source).Select(g => new { Source = g.Key, Orders = g.Count(), Total = g.Sum(o => o.GrandTotal) }).ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<OrderSourceCountDto>>([.. Enum.GetValues<OrderSource>().Select(s => rows.FirstOrDefault(r => r.Source == s) is { } r ? new OrderSourceCountDto(s, r.Orders, r.Total) : new OrderSourceCountDto(s, 0, 0m))]);
    }
}
