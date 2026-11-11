using System.Security.Cryptography;
using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Application.Ums;
using Juvo.Domain.Inventory;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Juvo.Shared.Contacts;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Returns;

/// <summary>
/// Staff side of returns (module L): queue, detail with timeline, approve, reject, pickup, receive, inspect (with restock), refund, exchange, notes, bulk and
/// the dashboard summary. Reads are limited to the branches where the actor holds the permission; every action is proven in the return's own branch (a wrong branch
/// looks like "not found" and is audit-logged). A change, its stock effect and the order tracks it moves are saved in one transaction.
/// </summary>
public sealed class ReturnsService(
    IApplicationDbContext db, IUnitOfWork uow, OrderAccess access, OrderStock orderStock, ReturnStock returnStock, ReturnOrderSync orderSync, OrderPlacement placement, ReturnPolicy policy,
    IFileStorage storage, Payments.RefundService refunds, TimeProvider clock)
{
    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error NotFound() => Error.NotFound(ErrorCodes.Return.NotFound, "Return not found.");

    private static readonly IReadOnlySet<string> SortFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "requestedAt", "number", "status" };

    private IQueryable<ReturnRequest> Loaded() =>
        db.ReturnRequests.Include(r => r.Items).Include(r => r.History).Include(r => r.Notes).Include(r => r.Photos).Include(r => r.Refunds);

    private async Task<Error?> CheckAsync(Actor actor, Guid branchId, string permission, CancellationToken cancellationToken)
    {
        var refusal = await access.CheckAsync(actor, branchId, permission, cancellationToken);
        return refusal is { Type: ErrorType.NotFound } ? NotFound() : refusal;
    }

    // ---- Reading ----------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<ReturnListItemDto>>> ListAsync(PageRequest request, ReturnListQuery filter, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Returns.View);
        var query = from r in db.ReturnRequests.AsNoTracking()
                    join b in db.Branches.AsNoTracking() on r.BranchId equals b.Id
                    select new { Return = r, BranchCode = b.Code };
        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.Return.BranchId));
        }

        if (filter.Status is { } status)
        {
            query = query.Where(x => x.Return.Status == status);
        }

        if (filter.Kind is { } kind)
        {
            query = query.Where(x => x.Return.Kind == kind);
        }

        if (filter.Fault is { } fault)
        {
            query = query.Where(x => x.Return.Fault == fault);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x => x.Return.BranchId == branchId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(x => x.Return.RequestedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(x => x.Return.RequestedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            var mobile = PhoneNumber.Normalize(term);
            query = query.Where(x => x.Return.Number.Contains(term) || x.Return.OrderNumber.Contains(term) || x.Return.CustomerMobile.Contains(term) ||
                                     (mobile != null && x.Return.CustomerMobile == mobile) || (x.Return.CustomerName != null && x.Return.CustomerName.Contains(term)));
        }

        var sort = request.ParseSort(SortFields) is { Count: > 0 } f ? f[0] : null;
        query = (sort?.Name.ToLowerInvariant(), sort?.Descending ?? true) switch
        {
            ("number", true) => query.OrderByDescending(x => x.Return.Number),
            ("number", false) => query.OrderBy(x => x.Return.Number),
            ("status", true) => query.OrderByDescending(x => x.Return.Status).ThenByDescending(x => x.Return.RequestedAt),
            ("status", false) => query.OrderBy(x => x.Return.Status).ThenByDescending(x => x.Return.RequestedAt),
            (_, false) => query.OrderBy(x => x.Return.RequestedAt),
            _ => query.OrderByDescending(x => x.Return.RequestedAt),
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(page.Skip).Take(page.PageSize)
            .Select(x => new { x.Return, x.BranchCode, Qty = x.Return.Items.Sum(i => i.Quantity), Estimate = x.Return.Items.Sum(i => i.LineRefund) }).ToListAsync(cancellationToken);
        return new PagedResult<ReturnListItemDto>(
            [.. rows.Select(x => new ReturnListItemDto(x.Return.Id, x.Return.Number, x.Return.OrderId, x.Return.OrderNumber, x.Return.BranchId, x.BranchCode, x.Return.Kind, x.Return.Status, x.Return.Fault,
                x.Return.ReasonCode, x.Return.CustomerName, x.Return.CustomerMobile, x.Qty, x.Return.InspectedAt is null ? (x.Return.Kind == ReturnKind.Return ? x.Estimate : 0m) : x.Return.RefundTotal,
                x.Return.RequestedAt))],
            total, page.Page, page.PageSize);
    }

    public async Task<Result<ReturnDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var ret = await Loaded().AsNoTracking().FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        if (ret is null)
        {
            return NotFound();
        }

        var refusal = await CheckAsync(actor, ret.BranchId, Permissions.Returns.View, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().FirstAsync(o => o.Id == ret.OrderId, cancellationToken);
        return await DetailAsync(ret, order, cancellationToken);
    }

    private async Task<ReturnDetailDto> DetailAsync(ReturnRequest r, Order order, CancellationToken cancellationToken)
    {
        var branchCode = await db.Branches.AsNoTracking().Where(b => b.Id == r.BranchId).Select(b => b.Code).FirstAsync(cancellationToken);
        return new ReturnDetailDto(
            r.Id, r.Number, r.BranchId, branchCode, r.OrderId, r.OrderNumber, r.CustomerId, r.CustomerMobile, r.CustomerName, r.Kind, r.Status, r.Fault, r.ReasonCode, r.ReasonText, r.RejectReason,
            r.RequestedAt, r.ApprovedAt, r.RejectedAt, r.PickupScheduledAt, r.PickedUpAt, r.ReceivedAt, r.InspectedAt, r.RefundedAt, r.ExchangedAt, r.CancelledAt, ReturnViews.Pickup(r),
            ReturnViews.Breakdown(r), ReturnViews.Refund(r), r.ReplacementOrderId, r.ReplacementOrderNumber, ReturnViews.Items(r, storage), ReturnViews.Photos(r, storage), ReturnViews.StaffTimeline(r),
            [.. r.Notes.OrderBy(n => n.At).Select(n => new ReturnNoteDto(n.Id, n.Text, n.AuthorName, n.At))],
            new ReturnOrderDto(order.Status, order.PaymentStatus, order.DeliveryStatus, order.PaymentMethod, order.GrandTotal, order.DeliveryCharge, order.DeliveredAt),
            [.. ReturnStateMachine.NextFor(r.Status).Select(s => s.ToString())]);
    }

    public async Task<Result<ReturnSummaryDto>> SummaryAsync(CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Returns.View);
        var returns = db.ReturnRequests.AsNoTracking().AsQueryable();
        if (visible is not null)
        {
            returns = returns.Where(r => visible.Contains(r.BranchId));
        }

        var counts = await returns.GroupBy(r => new { r.Status, r.Kind }).Select(g => new { g.Key.Status, g.Key.Kind, Count = g.Count() }).ToListAsync(cancellationToken);
        int Of(ReturnStatus s, ReturnKind? k = null) => counts.Where(c => c.Status == s && (k is null || c.Kind == k)).Sum(c => c.Count);

        var local = clock.GetUtcNow().ToOffset(TimeSpan.FromHours(6));
        var monthStart = new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, TimeSpan.FromHours(6));
        var refunded = await (from refund in db.ReturnRefunds.AsNoTracking()
                              join r in returns on refund.ReturnRequestId equals r.Id
                              where refund.At >= monthStart
                              select refund.Amount).SumAsync(cancellationToken);
        var open = counts.Where(c => !ReturnStateMachine.IsFinal(c.Status)).Sum(c => c.Count);
        return new ReturnSummaryDto(open, Of(ReturnStatus.Requested), Of(ReturnStatus.Approved), Of(ReturnStatus.PickupScheduled) + Of(ReturnStatus.PickedUp), Of(ReturnStatus.Received),
            Of(ReturnStatus.Inspected, ReturnKind.Return), Of(ReturnStatus.Inspected, ReturnKind.Exchange), refunded,
            [.. Enum.GetValues<ReturnStatus>().Select(s => new ReturnCountDto(s, Of(s)))]);
    }

    // ---- Changing ---------------------------------------------------------------------------------------

    private async Task<Result<ReturnDetailDto>> RunAsync(Guid id, string permission, Func<ReturnRequest, Order, Who, CancellationToken, Task<Result>> action, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<ReturnDetailDto>>(async ct =>
            {
                var ret = await Loaded().FirstOrDefaultAsync(r => r.Id == id, ct);
                if (ret is null)
                {
                    return NotFound();
                }

                var refusal = await CheckAsync(actor, ret.BranchId, permission, ct);
                if (refusal is not null)
                {
                    return refusal;
                }

                var order = await orderSync.LoadOrderAsync(ret.OrderId, ct) ?? throw new InvalidOperationException("The order of a return is missing.");
                var who = access.WhoIs(actor);
                var done = await action(ret, order, who, ct);
                if (done.IsFailure)
                {
                    throw new PlacementRejected(done.Error!); // undo any stock change made so far
                }

                await db.SaveChangesAsync(ct);
                await orderSync.SyncOrderAsync(order, who, ct);
                await db.SaveChangesAsync(ct);
                return Result.Success(await DetailAsync(ret, order, ct));
            }, cancellationToken);
        }
        catch (PlacementRejected rejected)
        {
            return rejected.Error;
        }
    }

    private static Result? Guard(ReturnRequest ret, ReturnStatus to)
    {
        if (ReturnStateMachine.CanMove(ret.Status, to))
        {
            return null;
        }

        return Error.Conflict(ErrorCodes.Return.InvalidTransition, $"A return that is {ret.Status} cannot become {to}.");
    }

    public Task<Result<ReturnDetailDto>> ApproveAsync(Guid id, ApproveReturnRequest request, CancellationToken cancellationToken) =>
        request.Note is { Length: > 500 }
            ? Task.FromResult<Result<ReturnDetailDto>>(Invalid("note", "The note can have at most 500 characters."))
            : RunAsync(id, Permissions.Returns.Approve, (r, _, who, _) =>
            {
                if (Guard(r, ReturnStatus.Approved) is { } refused)
                {
                    return Task.FromResult(refused);
                }

                r.Approve(who.Id, who.Name, request.Note, clock.GetUtcNow());
                return Task.FromResult(Result.Success());
            }, cancellationToken);

    public async Task<Result<ReturnDetailDto>> RejectAsync(Guid id, RejectReturnRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500)
        {
            return Invalid("reason", "Give the reason for rejecting the return (up to 500 characters).");
        }

        return await RunAsync(id, Permissions.Returns.Reject, (r, _, who, _) =>
        {
            if (Guard(r, ReturnStatus.Rejected) is { } refused)
            {
                return Task.FromResult(refused);
            }

            r.Reject(reason, who.Id, who.Name, clock.GetUtcNow());
            return Task.FromResult(Result.Success());
        }, cancellationToken);
    }

    public async Task<Result<ReturnDetailDto>> SchedulePickupAsync(Guid id, SchedulePickupRequest request, CancellationToken cancellationToken)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(6)).DateTime);
        if (request.Date < today || request.Date > today.AddDays(60))
        {
            return Invalid("date", "Choose a pickup date from today up to 60 days ahead.");
        }

        if (request.Courier is { Length: > 100 } || request.Note is { Length: > 500 })
        {
            return Invalid("note", "The courier can have 100 and the note 500 characters.");
        }

        return await RunAsync(id, Permissions.Returns.Manage, (r, _, who, _) =>
        {
            if (Guard(r, ReturnStatus.PickupScheduled) is { } refused)
            {
                return Task.FromResult(refused);
            }

            r.SchedulePickup(request.Date, request.Courier, request.Note, who.Id, who.Name, clock.GetUtcNow());
            return Task.FromResult(Result.Success());
        }, cancellationToken);
    }

    public Task<Result<ReturnDetailDto>> MarkPickedUpAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Permissions.Returns.Manage, (r, _, who, _) =>
        {
            if (Guard(r, ReturnStatus.PickedUp) is { } refused)
            {
                return Task.FromResult(refused);
            }

            r.MarkPickedUp(who.Id, who.Name, clock.GetUtcNow());
            return Task.FromResult(Result.Success());
        }, cancellationToken);

    public Task<Result<ReturnDetailDto>> MarkReceivedAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Permissions.Returns.Receive, (r, _, who, _) =>
        {
            if (Guard(r, ReturnStatus.Received) is { } refused)
            {
                return Task.FromResult(refused);
            }

            r.MarkReceived(who.Id, who.Name, clock.GetUtcNow());
            return Task.FromResult(Result.Success());
        }, cancellationToken);

    public async Task<Result<ReturnDetailDto>> InspectAsync(Guid id, InspectReturnRequest request, CancellationToken cancellationToken)
    {
        if (request.Note is { Length: > 500 })
        {
            return Invalid("note", "The note can have at most 500 characters.");
        }

        if (request.Fault is { } given && !Enum.IsDefined(given))
        {
            return Invalid("fault", "Choose Customer or Seller.");
        }

        return await RunAsync(id, Permissions.Returns.Inspect, async (r, order, who, ct) =>
        {
            if (Guard(r, ReturnStatus.Inspected) is { } refused)
            {
                return refused;
            }

            var lines = request.Items ?? [];
            var ids = r.Items.Select(i => i.Id).ToHashSet();
            if (lines.Count != ids.Count || lines.Select(l => l.ReturnItemId).Distinct().Count() != lines.Count || lines.Any(l => !ids.Contains(l.ReturnItemId)))
            {
                return Invalid("items", "Give one inspection result for every line of the return.");
            }

            foreach (var line in lines)
            {
                if (!Enum.IsDefined(line.Condition))
                {
                    return Invalid("items", "Choose Unopened, Good or Damaged for every line.");
                }

                if (line.Restock && line.Condition == ItemCondition.Damaged)
                {
                    return Invalid("items", "A damaged item cannot go back on the shelf. It is recorded as damaged instead.");
                }
            }

            var config = await policy.ReadAsync(ct);
            var fault = request.Fault ?? r.Fault;
            var deliveryRefund = 0m;
            if (r.Kind == ReturnKind.Return && fault == ReturnFault.Seller && config.RefundDeliveryOnSellerFault && order.DeliveryCharge > 0)
            {
                var given1 = await db.ReturnRequests.IgnoreQueryFilters(["BranchScope"]).AnyAsync(
                    x => x.OrderId == order.Id && x.Id != r.Id && x.DeliveryChargeRefund > 0 && x.Status != ReturnStatus.RefundRejected && x.Status != ReturnStatus.Cancelled && x.Status != ReturnStatus.Rejected, ct);
                deliveryRefund = given1 ? 0m : order.DeliveryCharge;
            }

            var returnCharge = r.Kind == ReturnKind.Return && fault == ReturnFault.Customer ? config.CustomerFaultReturnCharge : 0m;
            r.Inspect([.. lines.Select(l => new ItemInspection(l.ReturnItemId, l.Condition, l.Restock, l.AcceptRefund ?? true))], fault, deliveryRefund, returnCharge, request.Note, who.Id, who.Name, clock.GetUtcNow());
            await returnStock.RestockAsync(r, order, ct);
            return Result.Success();
        }, cancellationToken);
    }

    public async Task<Result<ReturnDetailDto>> RefundAsync(Guid id, RefundReturnRequest request, CancellationToken cancellationToken)
    {
        var reference = request.Reference?.Trim();
        if (!Enum.IsDefined(request.Method))
        {
            return Invalid("method", "Choose OriginalPayment, Cash, BankTransfer or Wallet.");
        }

        if (string.IsNullOrEmpty(reference) || reference.Length > 120)
        {
            return Invalid("reference", "Write the transfer or receipt reference (up to 120 characters).");
        }

        return await RunAsync(id, Permissions.Returns.Refund, async (r, order, who, ct) =>
        {
            if (r.Status == ReturnStatus.Refunded || r.Refunds.Count > 0)
            {
                return Error.Conflict(ErrorCodes.Return.AlreadyRefunded, "This return was already refunded.");
            }

            if (r.Kind != ReturnKind.Return)
            {
                return Error.Conflict(ErrorCodes.Return.WrongKind, "An exchange has no refund. Complete it as an exchange.");
            }

            if (Guard(r, ReturnStatus.Refunded) is { } refused)
            {
                return refused;
            }

            if (r.RefundTotal <= 0)
            {
                return Error.Conflict(ErrorCodes.Return.NothingToRefund, "There is nothing to refund on this return. Reject the refund instead.");
            }

            if (request.Amount is { } amount && amount != r.RefundTotal)
            {
                return Error.Conflict(ErrorCodes.Return.RefundAmountMismatch, $"The refund is BDT {r.RefundTotal:0.##}, not BDT {amount:0.##}.");
            }

            if (!ReturnOrderSync.IsPaid(order))
            {
                return Error.Conflict(ErrorCodes.Return.PaymentNotReceived, "The order has no received payment to refund.");
            }

            var before = await db.ReturnRefunds.AsNoTracking().Where(x => x.OrderId == order.Id).SumAsync(x => x.Amount, ct);
            if (before + r.RefundTotal > order.GrandTotal)
            {
                return Error.Conflict(ErrorCodes.Return.RefundAmountMismatch, "The refunds of this order would be more than the order total.");
            }

            r.Refund(request.Method, reference, who.Id, who.Name, clock.GetUtcNow());
            var money = await refunds.RecordForReturnAsync(order, r, request.Method, reference, who, ct); // the payment module: real Refund records, gateway call, credit note
            if (money.IsFailure)
            {
                return money;
            }

            return await orderSync.AfterRefundAsync(order, r, who, ct);
        }, cancellationToken);
    }

    public async Task<Result<ReturnDetailDto>> RejectRefundAsync(Guid id, RejectReturnRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 500)
        {
            return Invalid("reason", "Give the reason why nothing is refunded or exchanged (up to 500 characters).");
        }

        return await RunAsync(id, Permissions.Returns.Reject, (r, _, who, _) =>
        {
            if (Guard(r, ReturnStatus.RefundRejected) is { } refused)
            {
                return Task.FromResult(refused);
            }

            r.RejectRefund(reason, who.Id, who.Name, clock.GetUtcNow());
            return Task.FromResult(Result.Success());
        }, cancellationToken);
    }

    /// <summary>
    /// Completes an exchange (FR-L05): a linked order with no charge is placed for the replacement variants, its stock reserved in the fulfilling branch; it then
    /// goes through confirm, pack and delivery like any order. Refused (and nothing changed) when a replacement is not in stock.
    /// </summary>
    public async Task<Result<ReturnDetailDto>> ExchangeAsync(Guid id, ExchangeReturnRequest request, CancellationToken cancellationToken)
    {
        if (request.Note is { Length: > 500 })
        {
            return Invalid("note", "The note can have at most 500 characters.");
        }

        return await RunAsync(id, Permissions.Returns.Exchange, async (r, order, who, ct) =>
        {
            if (r.Kind != ReturnKind.Exchange)
            {
                return Error.Conflict(ErrorCodes.Return.WrongKind, "A return is refunded, not exchanged.");
            }

            if (Guard(r, ReturnStatus.Exchanged) is { } refused)
            {
                return refused;
            }

            var overrides = (request.Lines ?? []).ToDictionary(l => l.ReturnItemId, l => l.VariantId);
            if (overrides.Keys.Any(k => r.Items.All(i => i.Id != k)))
            {
                return Invalid("lines", "A replacement was given for a line that is not part of this return.");
            }

            var replace = r.Items.Where(i => i.RefundAccepted).ToList();
            if (replace.Count == 0)
            {
                return Error.Conflict(ErrorCodes.Return.NothingToRefund, "No line of this exchange was accepted at inspection. Reject the exchange instead.");
            }

            var wanted = replace.Select(i => (Item: i, VariantId: overrides.GetValueOrDefault(i.Id, i.ExchangeVariantId ?? i.VariantId))).ToList();
            var variantIds = wanted.Select(w => w.VariantId).Distinct().ToList();
            var variants = await db.ProductVariants.Where(v => variantIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
            var productIds = variants.Values.Select(v => v.ProductId).Distinct().ToList();
            var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var items = new List<NewOrderItem>();
            foreach (var (item, variantId) in wanted)
            {
                if (!variants.TryGetValue(variantId, out var variant) || !variant.IsActive || variant.ProductId != item.ProductId || !products.TryGetValue(variant.ProductId, out var product))
                {
                    return Error.Conflict(ErrorCodes.Return.ReplacementUnavailable, $"The replacement for {item.Sku} is not an active variant of the same product.");
                }

                items.Add(new NewOrderItem(variant.Id, product.Id, product.NameEn, product.NameBn, variant.Sku, variant.ColorName, variant.Size, item.ImagePath, 0m, item.Quantity,
                    variant.WeightGramsOverride ?? product.WeightGrams ?? 0, variant.TracksStock));
            }

            var now = clock.GetUtcNow();
            var branch = await db.Branches.FirstAsync(b => b.Id == r.BranchId, ct);
            var stockLines = items.Where(i => i.TracksStock).GroupBy(i => i.VariantId).Select(g => new StockLine(g.Key, g.Sum(i => i.Quantity))).ToList();
            if (stockLines.Count > 0 && await orderStock.TryReserveAsync(branch.Id, stockLines, ct) is not null)
            {
                return Error.Conflict(ErrorCodes.Return.ReplacementUnavailable, "A replacement item is not in stock in this branch. Refund the customer instead, or restock first.");
            }

            var shipping = new ShippingSnapshot(order.ShipName, order.ShipPhone, order.ShipCountryCode, order.ShipDivisionId, order.ShipDivision, order.ShipDistrictId, order.ShipDistrict, order.ShipUpazilaId,
                order.ShipUpazila, order.ShipArea, order.ShipAddressLine, order.ShipLandmark, order.ShipStateProvince, order.ShipCity, order.ShipPostalCode, order.ShipLatitude, order.ShipLongitude);
            var number = await placement.NextNumberAsync(branch, now, ct);
            var data = new NewOrder(
                number, branch.Id, OrderSource.Other, r.CustomerId, r.CustomerMobile, r.CustomerName, order.CustomerEmail, "COD", "Exchange", order.ZoneCode, order.Currency, 0m, 0m, null, null, 0m, 0m, 0m, 0m,
                items.Sum(i => i.WeightGrams * i.Quantity), shipping, null, $"exchange:{r.Id:N}", $"exchange:{r.Number}", Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                null, null, true, null, who.Id, who.Name);
            var replacement = Order.Place(data, items, now);
            replacement.AddNote(NoteKind.Internal, $"Replacement for order {order.Number} (return {r.Number}). No charge.{(string.IsNullOrWhiteSpace(request.Note) ? string.Empty : " " + request.Note.Trim())}", who.Id, who.Name, now);
            db.Orders.Add(replacement);
            if (stockLines.Count > 0)
            {
                await orderStock.RecordAsync(branch.Id, stockLines, StockReason.OrderReserved, 0, replacement, ct);
            }

            r.Exchange(replacement.Id, replacement.Number, who.Id, who.Name, now);
            return Result.Success();
        }, cancellationToken);
    }

    public async Task<Result<ReturnDetailDto>> AddNoteAsync(Guid id, ReturnNoteRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Text) || request.Text.Trim().Length > 1000)
        {
            return Invalid("text", "Write a note of up to 1000 characters.");
        }

        return await RunAsync(id, Permissions.Returns.Manage, (r, _, who, _) =>
        {
            r.AddNote(request.Text, who.Id, who.Name, clock.GetUtcNow());
            return Task.FromResult(Result.Success());
        }, cancellationToken);
    }

    // ---- Bulk -------------------------------------------------------------------------------------------

    public async Task<Result<BulkReturnResult>> BulkAsync(BulkReturnRequest request, CancellationToken cancellationToken)
    {
        var action = (request.Action ?? string.Empty).Trim().ToLowerInvariant();
        if (action is not ("approve" or "reject"))
        {
            return Invalid("action", "Choose approve or reject.");
        }

        var ids = request.Ids?.Distinct().ToList() ?? [];
        if (ids.Count is 0 or > 100)
        {
            return Invalid("ids", "Choose between 1 and 100 returns.");
        }

        if (action == "reject" && string.IsNullOrWhiteSpace(request.Reason))
        {
            return Invalid("reason", "Give the reason for rejecting the returns.");
        }

        var results = new List<BulkReturnItemResult>();
        foreach (var id in ids)
        {
            var number = await db.ReturnRequests.AsNoTracking().Where(r => r.Id == id).Select(r => r.Number).FirstOrDefaultAsync(cancellationToken);
            Result outcome = action == "approve"
                ? await ApproveAsync(id, new ApproveReturnRequest(null), cancellationToken)
                : await RejectAsync(id, new RejectReturnRequest(request.Reason), cancellationToken);
            results.Add(new BulkReturnItemResult(id, number, outcome.IsSuccess, outcome.Error?.Code, outcome.Error?.Message));
        }

        return new BulkReturnResult(action, results.Count(r => r.Ok), results.Count(r => !r.Ok), results);
    }
}
