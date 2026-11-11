using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Returns;

/// <summary>
/// The customer side of returns (module L, FR-F04): check what may come back, ask for a return or exchange (with pictures), follow it, cancel it while it is only
/// Requested or Approved. Everything is limited to the signed-in customer's own orders and returns; anything else looks like "not found".
/// </summary>
public sealed class CustomerReturnsService(
    IApplicationDbContext db, IUnitOfWork uow, ICurrentUser currentUser, ReturnPolicy policy, ReturnOrderSync orderSync, IImageProcessor images, IFileStorage storage,
    TimeProvider clock)
{
    public const int MaxPhotos = 5;
    private const int MaxLines = 50;

    private Guid? CustomerId => currentUser is { IsAuthenticated: true, SubjectType: "customer", UserId: { } id } ? id : null;

    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error NotFound() => Error.NotFound(ErrorCodes.Return.NotFound, "Return not found.");

    private IQueryable<Order> OrdersOf(Guid customerId) => db.Orders.IgnoreQueryFilters(["BranchScope"]).Where(o => o.CustomerId == customerId && !o.IsDeleted);

    private IQueryable<ReturnRequest> ReturnsOf(Guid customerId) =>
        db.ReturnRequests.IgnoreQueryFilters(["BranchScope"]).Include(r => r.Items).Include(r => r.History).Include(r => r.Photos).Include(r => r.Refunds)
            .Where(r => r.CustomerId == customerId && !r.IsDeleted);

    // ---- Eligibility ------------------------------------------------------------------------------------

    public async Task<Result<ReturnEligibilityDto>> EligibilityAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var order = await OrdersOf(id).AsNoTracking().Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
        }

        var config = await policy.ReadAsync(cancellationToken);
        var problem = policy.OrderProblem(order, config, out var deadline);
        var claimed = await policy.ClaimedAsync(order.Id, cancellationToken);
        var blocked = await policy.NonReturnableAsync(order.Items.Select(i => i.ProductId), cancellationToken);
        var lines = order.Items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Sku).Select(i =>
        {
            var (quantity, _) = claimed.GetValueOrDefault(i.Id);
            var left = i.Quantity - quantity;
            var reason = blocked.Contains(i.ProductId) ? "NON_RETURNABLE" : left <= 0 ? "ALL_RETURNED" : null;
            var returnable = problem is null && reason is null ? left : 0;
            var unit = i.Quantity == 0 ? 0m : Math.Round(ReturnMath.NetLine(order, i) / i.Quantity, 2, MidpointRounding.AwayFromZero);
            return new ReturnableLineDto(i.Id, i.VariantId, i.ProductId, i.Name, i.NameBn, i.Sku, i.ColorName, i.Size, OrderViews.ImageUrl(i.ImagePath, storage), i.UnitPrice, i.Quantity, quantity, returnable, unit,
                returnable > 0, returnable > 0 ? null : reason ?? (problem is null ? null : "ORDER_NOT_ELIGIBLE"));
        }).ToList();

        var eligible = problem is null && lines.Any(l => l.Returnable);
        string? code = problem?.Code;
        var message = problem?.Message;
        if (problem is null && !eligible)
        {
            code = ErrorCodes.Return.ItemNotReturnable;
            message = "None of the items of this order can be returned.";
        }

        return new ReturnEligibilityDto(order.Id, order.Number, eligible, code, message, order.DeliveredAt, deadline, config.WindowDays, lines,
            [.. ReturnReason.All.Select(r => new ReturnReasonDto(r.Code, r.LabelEn, r.LabelBn))], [nameof(ReturnKind.Return), nameof(ReturnKind.Exchange)]);
    }

    // ---- Pictures ---------------------------------------------------------------------------------------

    /// <summary>Stores one picture (JPEG, PNG or WebP, at least 1000 px on the long side, at most 10 MB) as WebP with a thumbnail. The path goes into the return request.</summary>
    public async Task<Result<ReturnPhotoUploadDto>> UploadPhotoAsync(Stream content, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > ImageRenditions.MaxBytes)
            {
                return Invalid("file", "The picture must be 10 MB or smaller.");
            }

            buffer.Write(chunk, 0, read);
        }

        var (image, problem) = images.Process(buffer.ToArray(), 0.5, 0.5);
        if (image is null)
        {
            var message = problem switch
            {
                ImageProblem.TooSmall => $"The picture must be at least {ImageRenditions.MinLongSide} px on its longer side.",
                ImageProblem.TooLarge => "The picture has too many pixels. Resize it and try again.",
                _ => "The file is not a JPEG, PNG or WebP picture.",
            };
            return Error.Validation(new Dictionary<string, string[]> { ["file"] = [message] }, message);
        }

        var path = $"{Prefix(id)}{clock.GetUtcNow():yyMM}/{Guid.NewGuid():N}.webp";
        await storage.SaveAsync(path, new MemoryStream(image.Main), "image/webp", cancellationToken);
        await storage.SaveAsync(ImageRenditions.Thumb(path), new MemoryStream(image.Thumb), "image/webp", cancellationToken);
        return new ReturnPhotoUploadDto(path, storage.GetPublicUrl(path), storage.GetPublicUrl(ImageRenditions.Thumb(path)));
    }

    private static string Prefix(Guid customerId) => $"returns/{customerId:N}/";

    // ---- Create -----------------------------------------------------------------------------------------

    public async Task<Result<MyReturnDto>> CreateAsync(CreateReturnRequest request, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var key = string.IsNullOrWhiteSpace(request.IdempotencyKey) ? null : request.IdempotencyKey.Trim();
        if (key is { Length: > 80 })
        {
            return Invalid("idempotencyKey", "The key can have at most 80 characters.");
        }

        if (!Enum.IsDefined(request.Kind))
        {
            return Invalid("kind", "Choose Return or Exchange.");
        }

        if (ReturnReason.Find(request.ReasonCode) is not { } reason)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["reasonCode"] = ["Choose one of the listed reasons."] }, "Choose one of the listed reasons.");
        }

        if (request.Details is { Length: > 1000 })
        {
            return Invalid("details", "The details can have at most 1000 characters.");
        }

        var wanted = request.Items ?? [];
        if (wanted.Count is 0 or > MaxLines || wanted.Any(l => l.Quantity < 1) || wanted.Select(l => l.OrderItemId).Distinct().Count() != wanted.Count)
        {
            return Invalid("items", "Choose between 1 and 50 different lines, each with a quantity of at least 1.");
        }

        var photos = (request.PhotoPaths ?? []).Distinct().ToList();
        if (photos.Count > MaxPhotos)
        {
            return Invalid("photoPaths", $"You can attach at most {MaxPhotos} pictures.");
        }

        foreach (var photo in photos)
        {
            if (!photo.StartsWith(Prefix(id), StringComparison.Ordinal) || photo.Contains("..", StringComparison.Ordinal) || !await storage.ExistsAsync(photo, cancellationToken))
            {
                return Error.Validation(new Dictionary<string, string[]> { ["photoPaths"] = ["Upload the pictures first and send the paths you got back."] }, "A picture is not valid.");
            }
        }

        try
        {
            return await uow.ExecuteInTransactionAsync(async ct =>
            {
                if (key is not null)
                {
                    var existing = await ReturnsOf(id).AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == key, ct);
                    if (existing is not null)
                    {
                        return existing.OrderId == request.OrderId
                            ? Result.Success(ToDto(existing))
                            : Result.Failure<MyReturnDto>(Error.Conflict(ErrorCodes.General.IdempotencyKeyReused, "This key was already used for another return request."));
                    }
                }

                var order = await OrdersOf(id).Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == request.OrderId, ct);
                if (order is null)
                {
                    return Result.Failure<MyReturnDto>(Error.NotFound(ErrorCodes.Order.NotFound, "Order not found."));
                }

                // One request at a time per order: this write takes the row lock, so two requests cannot both claim the last unit.
                await db.Orders.IgnoreQueryFilters(["BranchScope"]).Where(o => o.Id == order.Id).ExecuteUpdateAsync(set => set.SetProperty(o => o.Fingerprint, o => o.Fingerprint), ct);

                var config = await policy.ReadAsync(ct);
                if (policy.OrderProblem(order, config, out _) is { } problem)
                {
                    return Result.Failure<MyReturnDto>(Error.Conflict(problem.Code, problem.Message));
                }

                var claimed = await policy.ClaimedAsync(order.Id, ct);
                var blocked = await policy.NonReturnableAsync(order.Items.Select(i => i.ProductId), ct);
                var lines = new List<NewReturnItem>();
                foreach (var line in wanted)
                {
                    var item = order.Items.FirstOrDefault(i => i.Id == line.OrderItemId);
                    if (item is null)
                    {
                        return Result.Failure<MyReturnDto>(Invalid("items", "A line does not belong to this order."));
                    }

                    if (blocked.Contains(item.ProductId))
                    {
                        return Result.Failure<MyReturnDto>(Error.Conflict(ErrorCodes.Return.ItemNotReturnable, $"{item.Name} cannot be returned."));
                    }

                    var (quantity, refunded) = claimed.GetValueOrDefault(item.Id);
                    if (line.Quantity > item.Quantity - quantity)
                    {
                        return Result.Failure<MyReturnDto>(Error.Conflict(ErrorCodes.Return.QuantityExceeded,
                            $"You can return at most {Math.Max(0, item.Quantity - quantity)} of {item.Name} (you bought {item.Quantity}, {quantity} already in a return)."));
                    }

                    string? exchangeSku = null;
                    Guid? exchangeVariant = null;
                    if (request.Kind == ReturnKind.Exchange)
                    {
                        exchangeVariant = line.ExchangeVariantId ?? item.VariantId;
                        var target = await db.ProductVariants.AsNoTracking().FirstOrDefaultAsync(v => v.Id == exchangeVariant, ct);
                        if (target is null || !target.IsActive || target.ProductId != item.ProductId)
                        {
                            return Result.Failure<MyReturnDto>(Invalid("items", $"Choose a size or colour of {item.Name} for the exchange."));
                        }

                        exchangeSku = target.Sku;
                    }

                    var refund = ReturnMath.LineRefund(order, item, line.Quantity, quantity, refunded);
                    lines.Add(new NewReturnItem(item.Id, item.VariantId, item.ProductId, item.Name, item.Sku, item.ColorName, item.Size, item.ImagePath, line.Quantity, item.UnitPrice, refund, exchangeVariant, exchangeSku));
                }

                var now = clock.GetUtcNow();
                var branch = await db.Branches.FirstAsync(b => b.Id == order.BranchId, ct);
                var number = await NextNumberAsync(branch, now, ct);
                var name = order.CustomerName ?? order.ShipName;
                var created = ReturnRequest.Request(
                    new NewReturn(number, order.BranchId, order.Id, order.Number, id, order.CustomerMobile, name, request.Kind, reason.Code, request.Details, reason.DefaultFault, key, photos), lines, id,
                    string.IsNullOrWhiteSpace(name) ? "Customer" : name, now);
                db.ReturnRequests.Add(created);
                await db.SaveChangesAsync(ct);
                return Result.Success(ToDto(created));
            }, cancellationToken);
        }
        catch (DbUpdateException) when (key is not null)
        {
            // Two identical calls raced: the other one won. Answer with the request it created.
            var existing = await ReturnsOf(id).AsNoTracking().FirstOrDefaultAsync(r => r.IdempotencyKey == key, cancellationToken);
            return existing is null ? Error.Conflict(ErrorCodes.General.Conflict, "The request could not be saved. Please try again.") : ToDto(existing);
        }
    }

    /// <summary><c>RT-&lt;BRANCHCODE&gt;-&lt;yyMM&gt;-&lt;6 digits&gt;</c>, gap-free per branch and month like order numbers: the counter row is locked until the transaction ends.</summary>
    private async Task<string> NextNumberAsync(Domain.Branches.Branch branch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var period = OrderPlacement.LocalTime(branch, now).ToString("yyMM", System.Globalization.CultureInfo.InvariantCulture);
        var branchId = branch.Id;
        var bumped = await db.ReturnSequences.Where(s => s.BranchId == branchId && s.Period == period)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastNumber, s => s.LastNumber + 1), cancellationToken);
        if (bumped == 0)
        {
            var row = new ReturnSequence(branchId, period, 0);
            db.ReturnSequences.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.ReturnSequences.Remove(row); // someone created the row first
            }

            await db.ReturnSequences.Where(s => s.BranchId == branchId && s.Period == period)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastNumber, s => s.LastNumber + 1), cancellationToken);
        }

        var next = await db.ReturnSequences.AsNoTracking().Where(s => s.BranchId == branchId && s.Period == period).Select(s => s.LastNumber).FirstAsync(cancellationToken);
        return $"RT-{branch.OrderPrefix}-{period}-{next:D6}";
    }

    // ---- Reading and cancelling -------------------------------------------------------------------------

    public async Task<Result<PagedResult<MyReturnListItemDto>>> ListAsync(PageRequest request, string? filter, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var query = ReturnsOf(id).AsNoTracking();
        query = filter?.Trim().ToLowerInvariant() switch
        {
            "active" => query.Where(r => r.Status == ReturnStatus.Requested || r.Status == ReturnStatus.Approved || r.Status == ReturnStatus.PickupScheduled || r.Status == ReturnStatus.PickedUp ||
                                         r.Status == ReturnStatus.Received || r.Status == ReturnStatus.Inspected),
            "closed" => query.Where(r => r.Status == ReturnStatus.Rejected || r.Status == ReturnStatus.Refunded || r.Status == ReturnStatus.Exchanged || r.Status == ReturnStatus.RefundRejected ||
                                         r.Status == ReturnStatus.Cancelled),
            _ => query,
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(r => r.RequestedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var items = rows.Select(r =>
        {
            var first = r.Items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Sku).FirstOrDefault();
            return new MyReturnListItemDto(r.Id, r.Number, r.OrderId, r.OrderNumber, r.Kind, r.Status, r.Items.Sum(i => i.Quantity), first?.Name, OrderViews.ImageUrl(first?.ImagePath, storage),
                ReturnViews.Breakdown(r).Total, r.RequestedAt, ReturnViews.CanCancel(r));
        }).ToList();
        return new PagedResult<MyReturnListItemDto>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<MyReturnDto>> GetAsync(Guid returnId, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var found = await ReturnsOf(id).AsNoTracking().FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);
        return found is null ? NotFound() : ToDto(found);
    }

    private MyReturnDto ToDto(ReturnRequest r) =>
        new(r.Id, r.Number, r.OrderId, r.OrderNumber, r.Kind, r.Status, r.ReasonCode, r.ReasonText, r.RejectReason, r.RequestedAt, ReturnViews.Items(r, storage), ReturnViews.Photos(r, storage),
            ReturnViews.CustomerTimeline(r), ReturnViews.Pickup(r), ReturnViews.Breakdown(r), ReturnViews.Refund(r) is { } paid ? paid with { ActorName = null } : null, r.ReplacementOrderNumber,
            r.ReplacementOrderId, ReturnViews.CanCancel(r));

    /// <summary>The customer cancels while the request is only Requested or Approved. When an approved request held the order in ReturnInProgress, the order goes back to Delivered.</summary>
    public async Task<Result<MyReturnDto>> CancelAsync(Guid returnId, CancelReturnRequest request, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Cancelled by the customer" : request.Reason.Trim();
        if (reason.Length > 300)
        {
            return Invalid("reason", "The reason can have at most 300 characters.");
        }

        return await uow.ExecuteInTransactionAsync(async ct =>
        {
            var found = await ReturnsOf(id).FirstOrDefaultAsync(r => r.Id == returnId, ct);
            if (found is null)
            {
                return Result.Failure<MyReturnDto>(NotFound());
            }

            if (!ReturnStateMachine.CanMove(found.Status, ReturnStatus.Cancelled))
            {
                return Result.Failure<MyReturnDto>(Error.Conflict(ErrorCodes.Return.InvalidTransition, "This return can no longer be cancelled: the parcel is already on its way or the return is closed."));
            }

            var order = await orderSync.LoadOrderAsync(found.OrderId, ct) ?? throw new InvalidOperationException("The order of a return is missing.");
            var name = found.CustomerName;
            var who = Who.Customer(id, name);
            found.Cancel(reason, id, who.Name, clock.GetUtcNow());
            await db.SaveChangesAsync(ct);
            await orderSync.SyncOrderAsync(order, who, ct);
            await db.SaveChangesAsync(ct);
            return Result.Success(ToDto(found));
        }, cancellationToken);
    }
}

public sealed record CancelReturnRequest(string? Reason);
