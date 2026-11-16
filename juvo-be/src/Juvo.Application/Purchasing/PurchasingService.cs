using Juvo.Application.Abstractions;
using Juvo.Application.Accounting;
using Juvo.Application.Inventory;
using Juvo.Application.Operations;
using Juvo.Application.Orders;
using Juvo.Application.Ums;
using Juvo.Domain.Accounting;
using Juvo.Domain.Inventory;
using Juvo.Domain.Purchasing;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Purchasing;

/// <summary>
/// Purchase orders, goods receipts, supplier invoices and payments, and the low-stock suggestions (module W). A purchase order follows Draft, Sent, PartlyReceived, Received (Cancelled
/// from Draft or Sent); every change writes an append-only history row. A receipt adds the accepted units to the branch stock through the stock ledger, in the same transaction, and may
/// update the product cost price by the configured method (setting <c>Purchasing.CostMethod</c>) when the person receiving may change costs. Everything is proven in the order's own branch.
/// </summary>
public sealed class PurchasingService(
    IApplicationDbContext db, IUnitOfWork uow, InventoryAccess access, OrderAccess orderAccess, NumberAllocator numbers, StockLedger stock, LedgerWriter ledger, OpsSettingsService settings,
    AttachmentStore attachments, Preorders.PreorderAllocator preorders, TimeProvider clock)
{
    private const string Folder = "purchasing";
    private const int MaxLines = 200;

    private static Error NotFound() => Error.NotFound(ErrorCodes.Purchase.NotFound, "Purchase order not found.");

    private static readonly IReadOnlySet<string> SortFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "createdAt", "number", "status", "expected", "due" };

    private async Task<Error?> CheckAsync(Actor actor, Guid branchId, string permission, CancellationToken cancellationToken)
    {
        var refusal = await access.CheckAsync(actor, branchId, permission, cancellationToken);
        return refusal is { Type: ErrorType.NotFound } ? NotFound() : refusal;
    }

    private IQueryable<PurchaseOrder> Loaded() => db.PurchaseOrders.Include(o => o.Lines).Include(o => o.History).Include(o => o.Attachments);

    // ---- Reading -----------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<PurchaseListItemDto>>> ListAsync(PageRequest request, PurchaseListQuery filter, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Purchasing.PoView);
        var query = from o in db.PurchaseOrders.AsNoTracking() join s in db.Suppliers.AsNoTracking() on o.SupplierId equals s.Id select new { Order = o, SupplierName = s.Name };
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

        if (filter.SupplierId is { } supplierId)
        {
            query = query.Where(x => x.Order.SupplierId == supplierId);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x => x.Order.BranchId == branchId);
        }

        if (filter.Payable == true)
        {
            query = query.Where(x => x.Order.PaymentStatus != SupplierPaymentStatus.Paid && (x.Order.Status == PurchaseStatus.Sent || x.Order.Status == PurchaseStatus.PartlyReceived || x.Order.Status == PurchaseStatus.Received));
        }

        if (filter.Overdue == true)
        {
            var today = Ops.BstDate(clock.GetUtcNow());
            query = query.Where(x => x.Order.DueDate != null && x.Order.DueDate < today && x.Order.PaymentStatus != SupplierPaymentStatus.Paid && x.Order.Status != PurchaseStatus.Cancelled);
        }

        if (filter.From is { } from)
        {
            query = query.Where(x => x.Order.CreatedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(x => x.Order.CreatedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(x => x.Order.Number.Contains(term) || x.SupplierName.Contains(term) || (x.Order.SupplierInvoiceNumber != null && x.Order.SupplierInvoiceNumber.Contains(term)));
        }

        var sort = request.ParseSort(SortFields) is { Count: > 0 } f ? f[0] : null;
        query = (sort?.Name.ToLowerInvariant(), sort?.Descending ?? true) switch
        {
            ("number", true) => query.OrderByDescending(x => x.Order.Number),
            ("number", false) => query.OrderBy(x => x.Order.Number),
            ("status", true) => query.OrderByDescending(x => x.Order.Status).ThenByDescending(x => x.Order.CreatedAt),
            ("status", false) => query.OrderBy(x => x.Order.Status).ThenByDescending(x => x.Order.CreatedAt),
            ("expected", true) => query.OrderByDescending(x => x.Order.ExpectedDate),
            ("expected", false) => query.OrderBy(x => x.Order.ExpectedDate),
            ("due", true) => query.OrderByDescending(x => x.Order.DueDate),
            ("due", false) => query.OrderBy(x => x.Order.DueDate),
            (_, false) => query.OrderBy(x => x.Order.CreatedAt),
            _ => query.OrderByDescending(x => x.Order.CreatedAt),
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(page.Skip).Take(page.PageSize).Select(x => new { x.Order, x.SupplierName, Lines = x.Order.Lines.ToList() }).ToListAsync(cancellationToken);
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Code, cancellationToken);
        var today2 = Ops.BstDate(clock.GetUtcNow());
        var items = rows.Select(x =>
        {
            var o = x.Order;
            var ordered = x.Lines.Sum(l => l.OrderedQuantity);
            var received = x.Lines.Sum(l => l.ReceivedQuantity);
            var totalValue = x.Lines.Sum(l => l.OrderedQuantity * l.UnitCost);
            var receivedValue = x.Lines.Sum(l => l.ReceivedQuantity * l.UnitCost);
            var payable = o.InvoiceAmount ?? (o.Status == PurchaseStatus.Received ? receivedValue : totalValue);
            return new PurchaseListItemDto(o.Id, o.Number, o.BranchId, branches.GetValueOrDefault(o.BranchId, string.Empty), o.SupplierId, x.SupplierName, o.Status, o.PaymentStatus, o.ExpectedDate, totalValue, receivedValue,
                payable, o.AmountPaid, Math.Max(0m, payable - o.AmountPaid), o.DueDate, o.IsOverdue(today2), x.Lines.Count, ordered, received, o.CreatedAt);
        }).ToList();
        return new PagedResult<PurchaseListItemDto>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<PurchaseDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var order = await Loaded().AsNoTracking().FirstOrDefaultAsync(o => o.Id == id, cancellationToken);
        if (order is null)
        {
            return NotFound();
        }

        return await CheckAsync(actor.Value, order.BranchId, Permissions.Purchasing.PoView, cancellationToken) is { } refusal ? refusal : await DetailAsync(order, cancellationToken);
    }

    private async Task<PurchaseDetailDto> DetailAsync(PurchaseOrder o, CancellationToken cancellationToken)
    {
        var branch = await db.Branches.AsNoTracking().Where(b => b.Id == o.BranchId).Select(b => b.Code).FirstAsync(cancellationToken);
        var supplier = await db.Suppliers.AsNoTracking().IgnoreQueryFilters().Where(s => s.Id == o.SupplierId).Select(s => s.Name).FirstAsync(cancellationToken);
        var receipts = await db.GoodsReceipts.AsNoTracking().Where(r => r.PurchaseOrderId == o.Id).OrderBy(r => r.ReceivedAt)
            .Select(r => new ReceiptSummaryDto(r.Id, r.Number, r.ReceivedAt, r.ReceivedByName, r.Lines.Sum(l => l.AcceptedQuantity), r.Lines.Sum(l => l.RejectedQuantity))).ToListAsync(cancellationToken);
        var payments = await db.PurchasePayments.AsNoTracking().Where(p => p.PurchaseOrderId == o.Id).OrderBy(p => p.CreatedAt).ToListAsync(cancellationToken);
        var accountIds = payments.Where(p => p.AccountId != null).Select(p => p.AccountId!.Value).Distinct().ToList();
        var accounts = await db.FinanceAccounts.AsNoTracking().Where(a => accountIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, cancellationToken);
        var today = Ops.BstDate(clock.GetUtcNow());
        return new PurchaseDetailDto(o.Id, o.Number, o.BranchId, branch, o.SupplierId, supplier, o.Status, o.PaymentStatus, o.ExpectedDate, o.Notes, o.Currency, o.Total, o.ReceivedValue, o.AmountPayable, o.AmountPaid,
            o.Outstanding, o.SupplierInvoiceNumber, o.SupplierInvoiceDate, o.DueDate, o.InvoiceAmount, o.IsOverdue(today), o.SentAt, o.FirstReceivedAt, o.CompletedAt, o.CancelledAt, o.CancelReason, o.CreatedAt,
            [.. o.Lines.OrderBy(l => l.CreatedAt).ThenBy(l => l.Sku).Select(l => new PurchaseLineDto(l.Id, l.VariantId, l.ProductId, l.Sku, l.ProductName, l.ColorName, l.Size, l.OrderedQuantity, l.ReceivedQuantity,
                l.RejectedQuantity, l.OutstandingQuantity, l.UnitCost, l.OrderedQuantity * l.UnitCost))],
            [.. o.History.OrderBy(h => h.At).ThenBy(h => h.CreatedAt).Select(h => new PurchaseHistoryDto(h.FromStatus, h.ToStatus, h.Reason, h.ActorName, h.At))],
            receipts,
            [.. payments.Select(p => new PurchasePaymentDto(p.Id, p.Amount, p.Method, p.Reference, p.PaidOn, p.AccountId, p.AccountId is { } a ? accounts.GetValueOrDefault(a) : null, p.Note, p.ActorName, p.CreatedAt))],
            [.. o.Attachments.OrderBy(a => a.CreatedAt).Select(a => new PurchaseAttachmentDto(a.Id, a.FileName, a.ContentType, a.Size, attachments.UrlOf(a.Path)))],
            [.. PurchaseStateMachine.NextFor(o.Status).Select(s => s.ToString())]);
    }

    public async Task<Result<PurchasePrintDto>> PrintAsync(Guid id, CancellationToken cancellationToken)
    {
        var detail = await GetAsync(id, cancellationToken);
        if (detail.IsFailure)
        {
            return detail.Error!;
        }

        var d = detail.Value;
        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == d.BranchId, cancellationToken);
        var supplier = await db.Suppliers.AsNoTracking().IgnoreQueryFilters().FirstAsync(s => s.Id == d.SupplierId, cancellationToken);
        var business = await db.PlatformSettings.AsNoTracking().Where(s => s.Key == Juvo.Application.Billing.BusinessSettingKeys.Name).Select(s => s.Value).FirstOrDefaultAsync(cancellationToken);
        var lines = d.Lines.Select((l, i) => new PrintLineDto(i + 1, l.Sku, string.Join(" / ", new[] { l.ProductName, l.ColorName, l.Size }.Where(x => !string.IsNullOrWhiteSpace(x))), l.OrderedQuantity, l.UnitCost, l.LineTotal)).ToList();
        return new PurchasePrintDto(d.Number, d.Status, d.Currency, d.CreatedAt, d.ExpectedDate, d.Notes,
            new PrintPartyDto(string.IsNullOrWhiteSpace(business) ? "Juvo BD" : business, branch.NameEn, branch.Phone, branch.Email, branch.PickupAddress ?? branch.AddressLine),
            new PrintPartyDto(supplier.Name, supplier.ContactName, supplier.Phone, supplier.Email, supplier.Address), lines, d.Total, d.SupplierInvoiceNumber, d.DueDate);
    }

    // ---- Writing -----------------------------------------------------------------------------------------

    private async Task<Result<List<NewPurchaseLine>>> BuildLinesAsync(IReadOnlyList<PurchaseLineRequest>? requested, CancellationToken cancellationToken)
    {
        if (requested is null || requested.Count is 0 or > MaxLines)
        {
            return Ops.Invalid("lines", $"Send between 1 and {MaxLines} lines.");
        }

        if (requested.Any(l => l.Quantity is < 1 or > 1_000_000 || l.UnitCost is < 0 or > 100_000_000m))
        {
            return Ops.Invalid("lines", "Every line needs a quantity from 1 to 1,000,000 and a unit cost from 0 to 100,000,000.");
        }

        var ids = requested.Select(l => l.VariantId).Distinct().ToList();
        var variants = await stock.VariantsAsync(ids, cancellationToken);
        if (ids.Any(id => !variants.ContainsKey(id)))
        {
            return Error.NotFound(ErrorCodes.Purchase.VariantNotFound, "A product variant on the order was not found.");
        }

        return requested.Select(l =>
        {
            var v = variants[l.VariantId];
            return new NewPurchaseLine(v.VariantId, v.ProductId, v.Sku, v.ProductName, v.ColorName, v.Size, l.Quantity, Math.Round(l.UnitCost, 2, MidpointRounding.AwayFromZero));
        }).ToList();
    }

    private async Task<Error?> CheckSupplierAsync(Guid supplierId, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == supplierId, cancellationToken);
        return supplier is null ? Error.NotFound(ErrorCodes.Purchase.SupplierNotFound, "Supplier not found.")
            : !supplier.IsActive ? Error.Conflict(ErrorCodes.Purchase.SupplierInactive, "This supplier is archived. Reactivate it first.")
            : null;
    }

    public async Task<Result<PurchaseDetailDto>> CreateAsync(SavePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        var target = await access.TargetAsync(request.BranchId, Permissions.Purchasing.PoCreate, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        if (Ops.TooLong(request.Notes, 1000))
        {
            return Ops.Invalid("notes", "The notes can have at most 1000 characters.");
        }

        if (await CheckSupplierAsync(request.SupplierId, cancellationToken) is { } supplierError)
        {
            return supplierError;
        }

        var lines = await BuildLinesAsync(request.Lines, cancellationToken);
        if (lines.IsFailure)
        {
            return lines.Error!;
        }

        var files = await attachments.ResolveAsync(Folder, request.AttachmentPaths, ErrorCodes.Purchase.AttachmentInvalid, 10, cancellationToken);
        if (files.IsFailure)
        {
            return files.Error!;
        }

        var who = orderAccess.WhoIs(target.Value.Actor);
        return await uow.ExecuteInTransactionAsync<Result<PurchaseDetailDto>>(async ct =>
        {
            var now = clock.GetUtcNow();
            var number = await numbers.NextForBranchAsync(target.Value.Branch, "PO", now, ct);
            var order = new PurchaseOrder(number, target.Value.Branch.Id, request.SupplierId, request.ExpectedDate, request.Notes, lines.Value, now, who.Id, who.Name);
            foreach (var f in files.Value)
            {
                order.AddAttachment(f.Path, f.FileName, f.ContentType, f.Size);
            }

            db.PurchaseOrders.Add(order);
            await db.SaveChangesAsync(ct);
            return await DetailAsync(order, ct);
        }, cancellationToken);
    }

    private async Task<Result<PurchaseDetailDto>> RunAsync(Guid id, string permission, Func<PurchaseOrder, Actor, Who, CancellationToken, Task<Result>> action, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        return await uow.ExecuteInTransactionAsync<Result<PurchaseDetailDto>>(async ct =>
        {
            var order = await Loaded().FirstOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
            {
                return NotFound();
            }

            if (await CheckAsync(actor.Value, order.BranchId, permission, ct) is { } refusal)
            {
                return refusal;
            }

            var done = await action(order, actor.Value, orderAccess.WhoIs(actor.Value), ct);
            if (done.IsFailure)
            {
                return done.Error!;
            }

            await db.SaveChangesAsync(ct);
            return await DetailAsync(order, ct);
        }, cancellationToken);
    }

    private static Result Guard(PurchaseOrder order, PurchaseStatus to) =>
        PurchaseStateMachine.CanMove(order.Status, to) ? Result.Success() : Error.Conflict(ErrorCodes.Purchase.InvalidTransition, $"A purchase order that is {order.Status} cannot become {to}.");

    public async Task<Result<PurchaseDetailDto>> UpdateAsync(Guid id, SavePurchaseOrderRequest request, CancellationToken cancellationToken)
    {
        if (Ops.TooLong(request.Notes, 1000))
        {
            return Ops.Invalid("notes", "The notes can have at most 1000 characters.");
        }

        var lines = await BuildLinesAsync(request.Lines, cancellationToken);
        if (lines.IsFailure)
        {
            return lines.Error!;
        }

        var files = await attachments.ResolveAsync(Folder, request.AttachmentPaths, ErrorCodes.Purchase.AttachmentInvalid, 10, cancellationToken);
        if (files.IsFailure)
        {
            return files.Error!;
        }

        return await RunAsync(id, Permissions.Purchasing.PoEdit, async (o, _, _, ct) =>
        {
            if (o.Status != PurchaseStatus.Draft)
            {
                return Error.Conflict(ErrorCodes.Purchase.InvalidTransition, $"Only a draft purchase order can be edited; this one is {o.Status}.");
            }

            if (await CheckSupplierAsync(request.SupplierId, ct) is { } supplierError)
            {
                return supplierError;
            }

            // Lines are replaced: detach the old ones so the context deletes them and inserts the new rows.
            foreach (var old in o.Lines.ToList())
            {
                db.PurchaseOrderLines.Remove(old);
            }

            o.UpdateDraft(request.SupplierId, request.ExpectedDate, request.Notes, lines.Value);
            foreach (var f in files.Value.Where(f => o.Attachments.All(a => a.Path != f.Path)))
            {
                o.AddAttachment(f.Path, f.FileName, f.ContentType, f.Size);
            }

            return Result.Success();
        }, cancellationToken);
    }

    public Task<Result<PurchaseDetailDto>> SendAsync(Guid id, CancellationToken cancellationToken) =>
        RunAsync(id, Permissions.Purchasing.PoSend, async (o, _, who, ct) =>
        {
            if (Guard(o, PurchaseStatus.Sent) is { IsFailure: true } refused)
            {
                return refused;
            }

            if (await CheckSupplierAsync(o.SupplierId, ct) is { } supplierError)
            {
                return supplierError;
            }

            o.Send(clock.GetUtcNow(), who.Id, who.Name);
            return Result.Success();
        }, cancellationToken);

    public async Task<Result<PurchaseDetailDto>> CancelAsync(Guid id, CancelPurchaseRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 300)
        {
            return Ops.Invalid("reason", "Give the reason for cancelling the order (up to 300 characters).");
        }

        return await RunAsync(id, Permissions.Purchasing.PoCancel, (o, _, who, _) =>
        {
            if (Guard(o, PurchaseStatus.Cancelled) is { IsFailure: true } refused)
            {
                return Task.FromResult(refused);
            }

            if (o.AmountPaid > 0)
            {
                return Task.FromResult<Result>(Error.Conflict(ErrorCodes.Purchase.InvalidTransition, "Payments were already recorded against this order, so it cannot be cancelled."));
            }

            o.Cancel(reason, clock.GetUtcNow(), who.Id, who.Name);
            return Task.FromResult(Result.Success());
        }, cancellationToken);
    }

    public async Task<Result<PurchaseDetailDto>> CloseShortAsync(Guid id, CancelPurchaseRequest request, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > 300)
        {
            return Ops.Invalid("reason", "Give the reason for closing the order short (up to 300 characters).");
        }

        return await RunAsync(id, Permissions.Purchasing.PoReceive, (o, _, who, _) =>
        {
            if (o.Status != PurchaseStatus.PartlyReceived)
            {
                return Task.FromResult<Result>(Error.Conflict(ErrorCodes.Purchase.InvalidTransition, $"Only a partly received order can be closed short; this one is {o.Status}."));
            }

            o.CloseShort(reason, clock.GetUtcNow(), who.Id, who.Name);
            return Task.FromResult(Result.Success());
        }, cancellationToken);
    }

    public async Task<Result<PurchaseDetailDto>> RecordInvoiceAsync(Guid id, RecordSupplierInvoiceRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Number) || Ops.TooLong(request.Number, 80) || request.Amount is < 0 or > 1_000_000_000m)
        {
            return Ops.Invalid("number", "Give the supplier's invoice number (up to 80 characters) and an amount of zero or more.");
        }

        return await RunAsync(id, Permissions.Purchasing.PoEdit, async (o, _, _, ct) =>
        {
            if (o.Status is PurchaseStatus.Draft or PurchaseStatus.Cancelled)
            {
                return Error.Conflict(ErrorCodes.Purchase.InvoiceNotAllowed, $"An invoice cannot be recorded on an order that is {o.Status}.");
            }

            var terms = await db.Suppliers.AsNoTracking().IgnoreQueryFilters().Where(s => s.Id == o.SupplierId).Select(s => s.PaymentTermsDays).FirstAsync(ct);
            o.RecordInvoice(request.Number, request.InvoiceDate, request.DueDate ?? request.InvoiceDate.AddDays(terms), request.Amount is { } a ? Math.Round(a, 2, MidpointRounding.AwayFromZero) : null);
            return Result.Success();
        }, cancellationToken);
    }

    public async Task<Result<PurchaseDetailDto>> RecordPaymentAsync(Guid id, RecordSupplierPaymentRequest request, CancellationToken cancellationToken)
    {
        if (request.Amount <= 0 || request.Amount > 1_000_000_000m || !Enum.IsDefined(request.Method) || Ops.TooLong(request.Reference, 120) || Ops.TooLong(request.Note, 300))
        {
            return Ops.Invalid("amount", "The amount must be positive; the reference can have 120 and the note 300 characters.");
        }

        var amount = Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero);
        return await RunAsync(id, Permissions.Purchasing.PayablePay, async (o, _, who, ct) =>
        {
            if (o.Status is PurchaseStatus.Draft or PurchaseStatus.Cancelled)
            {
                return Error.Conflict(ErrorCodes.Purchase.PaymentNotAllowed, $"A payment cannot be recorded on an order that is {o.Status}.");
            }

            if (amount > o.Outstanding)
            {
                return Error.Conflict(ErrorCodes.Purchase.PaymentExceedsDue, $"The payment is more than the BDT {o.Outstanding:0.##} still due.");
            }

            FinanceAccount? account = null;
            if (request.AccountId is { } accountId)
            {
                account = await db.FinanceAccounts.FirstOrDefaultAsync(a => a.Id == accountId, ct);
                if (account is null)
                {
                    return Error.NotFound(ErrorCodes.Expense.AccountNotFound, "Account not found.");
                }

                if (account.BranchId != o.BranchId)
                {
                    return Error.Conflict(ErrorCodes.Expense.AccountBranchMismatch, "The account belongs to another branch.");
                }

                if (!account.IsActive)
                {
                    return Error.Conflict(ErrorCodes.Expense.AccountInactive, "This account is not in use any more.");
                }
            }

            var paidOn = request.PaidOn ?? Ops.BstDate(clock.GetUtcNow());
            var payment = new PurchasePayment(o.Id, o.BranchId, amount, request.Method, request.Reference, paidOn, account?.Id, request.Note, who.Id, who.Name);
            db.PurchasePayments.Add(payment);
            o.ApplyPayment(amount);
            if (account is not null)
            {
                ledger.Add(account, paidOn, LedgerEntryKind.SupplierPayment, -amount, $"Payment for {o.Number}" + (string.IsNullOrWhiteSpace(request.Reference) ? string.Empty : $" ({request.Reference.Trim()})"), "PurchasePayment", payment.Id, null, who.Name);
            }

            return Result.Success();
        }, cancellationToken);
    }

    public async Task<Result<PurchaseDetailDto>> AddAttachmentsAsync(Guid id, AddPurchaseAttachmentsRequest request, CancellationToken cancellationToken)
    {
        var files = await attachments.ResolveAsync(Folder, request.AttachmentPaths, ErrorCodes.Purchase.AttachmentInvalid, 10, cancellationToken);
        if (files.IsFailure)
        {
            return files.Error!;
        }

        return await RunAsync(id, Permissions.Purchasing.PoEdit, (o, _, _, _) =>
        {
            foreach (var f in files.Value.Where(f => o.Attachments.All(a => a.Path != f.Path)))
            {
                o.AddAttachment(f.Path, f.FileName, f.ContentType, f.Size);
            }

            return Task.FromResult(Result.Success());
        }, cancellationToken);
    }

    public async Task<Result<AttachmentUploadDto>> UploadAsync(Stream content, string? fileName, CancellationToken cancellationToken)
    {
        var saved = await attachments.SaveAsync(Folder, content, fileName, ErrorCodes.Purchase.AttachmentInvalid, cancellationToken);
        return saved.IsFailure ? saved.Error! : new AttachmentUploadDto(saved.Value.Path, saved.Value.FileName, saved.Value.ContentType, saved.Value.Size, saved.Value.Url);
    }

    // ---- Receiving goods ----------------------------------------------------------------------------------

    public async Task<Result<ReceiveResultDto>> ReceiveAsync(Guid id, ReceiveGoodsRequest request, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        if (request.Lines is null || request.Lines.Count == 0 || Ops.TooLong(request.Note, 500) || request.Lines.Any(l => Ops.TooLong(l.Note, 300)))
        {
            return Ops.Invalid("lines", "Send at least one line; notes can have at most 500 (receipt) and 300 (line) characters.");
        }

        if (request.Lines.Any(l => l.AcceptedQuantity < 0 || l.RejectedQuantity < 0 || l.AcceptedQuantity > 1_000_000 || l.RejectedQuantity > 1_000_000) || request.Lines.GroupBy(l => l.LineId).Any(g => g.Count() > 1))
        {
            return Ops.Invalid("lines", "Quantities cannot be negative, and every line may appear once.");
        }

        var received = request.Lines.Where(l => l.AcceptedQuantity + l.RejectedQuantity > 0).ToList();
        if (received.Count == 0)
        {
            return Error.Failure(ErrorCodes.Purchase.NothingReceived, "Nothing was received: enter an accepted or a rejected quantity on at least one line.");
        }

        Guid receivedBranch = Guid.Empty;
        List<Guid> receivedVariants = [];
        var outcome = await uow.ExecuteInTransactionAsync<Result<ReceiveResultDto>>(async ct =>
        {
            var order = await Loaded().FirstOrDefaultAsync(o => o.Id == id, ct);
            if (order is null)
            {
                return NotFound();
            }

            if (await CheckAsync(actor.Value, order.BranchId, Permissions.Purchasing.PoReceive, ct) is { } refusal)
            {
                return refusal;
            }

            if (order.Status is not (PurchaseStatus.Sent or PurchaseStatus.PartlyReceived))
            {
                return Error.Conflict(ErrorCodes.Purchase.InvalidTransition, $"Goods can only be received on a sent or partly received order; this one is {order.Status}.");
            }

            foreach (var line in received)
            {
                var po = order.Lines.FirstOrDefault(l => l.Id == line.LineId);
                if (po is null)
                {
                    return Ops.Invalid("lines", "A line does not belong to this order.");
                }

                if (line.AcceptedQuantity > po.OutstandingQuantity)
                {
                    return Error.Conflict(ErrorCodes.Purchase.OverReceipt, $"{po.Sku}: {line.AcceptedQuantity} accepted but only {po.OutstandingQuantity} are still expected.");
                }
            }

            var branch = await db.Branches.FirstAsync(b => b.Id == order.BranchId, ct);
            var who = orderAccess.WhoIs(actor.Value);
            var now = clock.GetUtcNow();
            var config = await settings.ReadAsync(ct);
            var mayChangeCost = actor.Value.Access.HasAnywhere(Permissions.Catalog.ProductViewCost);

            var receiptNumber = await numbers.NextForBranchAsync(branch, "GRN", now, ct);
            var receipt = new GoodsReceipt(receiptNumber, order.Id, order.Number, order.BranchId, now, who.Id, who.Name, request.Note);

            // The product is the unit of cost: on hand across every variant and branch, kept running while several variants of one product arrive together.
            var productIds = received.Select(l => order.Lines.First(x => x.Id == l.LineId).ProductId).Distinct().ToList();
            var onHand = await db.StockLevels.AsNoTracking().Where(s => db.ProductVariants.Any(v => v.Id == s.VariantId && productIds.Contains(v.ProductId)))
                .Join(db.ProductVariants.AsNoTracking(), s => s.VariantId, v => v.Id, (s, v) => new { v.ProductId, s.OnHand }).GroupBy(x => x.ProductId)
                .Select(g => new { g.Key, Sum = g.Sum(x => x.OnHand) }).ToDictionaryAsync(x => x.Key, x => x.Sum, ct);
            var products = await db.Products.Where(p => productIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
            var tracked = (await stock.VariantsAsync([.. received.Select(l => order.Lines.First(x => x.Id == l.LineId).VariantId)], ct));

            foreach (var line in received)
            {
                var po = order.Lines.First(l => l.Id == line.LineId);
                var product = products[po.ProductId];
                var before = onHand.GetValueOrDefault(po.ProductId);
                var costBefore = product.CostPrice;
                var applied = false;
                if (line.AcceptedQuantity > 0)
                {
                    if (tracked.TryGetValue(po.VariantId, out var info) && info.TracksStock)
                    {
                        var level = await stock.LevelAsync(order.BranchId, po.VariantId, ct);
                        stock.TryChange(level, line.AcceptedQuantity, StockReason.PurchaseReceipt, $"Receipt {receiptNumber} for {order.Number}", "PurchaseReceipt", receipt.Id);
                    }

                    if (mayChangeCost)
                    {
                        product.SetCostPrice(PurchaseCost.Next(config.CostMethod, costBefore, before, line.AcceptedQuantity, po.UnitCost));
                        applied = true;
                    }

                    onHand[po.ProductId] = before + line.AcceptedQuantity;
                }

                receipt.AddLine(po, line.AcceptedQuantity, line.RejectedQuantity, line.Note, before, costBefore, applied || line.AcceptedQuantity == 0);
            }

            order.Receive([.. received.Select(l => new ReceivedLine(l.LineId, l.AcceptedQuantity, l.RejectedQuantity, l.Note))], now, who.Id, who.Name);
            db.GoodsReceipts.Add(receipt);
            await db.SaveChangesAsync(ct);
            receivedBranch = order.BranchId;
            receivedVariants = [.. received.Where(l => l.AcceptedQuantity > 0).Select(l => order.Lines.First(x => x.Id == l.LineId).VariantId).Distinct()];
            return new ReceiveResultDto(ReceiptDto(receipt), await DetailAsync(order, ct));
        }, cancellationToken);

        // Module AA: the new stock goes to waiting pre-orders, oldest first. A problem here never undoes the receipt; staff can run the allocation again.
        if (outcome.IsSuccess && receivedBranch != Guid.Empty && receivedVariants.Count > 0)
        {
            await preorders.AllocateAfterReceiptAsync(receivedBranch, receivedVariants, cancellationToken);
        }

        return outcome;
    }

    private static GoodsReceiptDto ReceiptDto(GoodsReceipt r) =>
        new(r.Id, r.Number, r.PurchaseOrderId, r.PurchaseOrderNumber, r.BranchId, r.ReceivedAt, r.ReceivedByName, r.Note,
            [.. r.Lines.Select(l => new GoodsReceiptLineDto(l.Id, l.PurchaseLineId, l.VariantId, l.Sku, l.AcceptedQuantity, l.RejectedQuantity, l.UnitCost, l.CostApplied, l.Note))]);

    public async Task<Result<PagedResult<GoodsReceiptDto>>> ReceiptsAsync(PageRequest request, ReceiptListQuery filter, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var visible = InventoryAccess.VisibleBranches(actor.Value, Permissions.Purchasing.ReceiptView);
        var query = db.GoodsReceipts.AsNoTracking().Include(r => r.Lines).AsQueryable();
        if (visible is not null)
        {
            query = query.Where(r => visible.Contains(r.BranchId));
        }

        if (filter.PurchaseOrderId is { } poId)
        {
            query = query.Where(r => r.PurchaseOrderId == poId);
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(r => r.BranchId == branchId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(r => r.ReceivedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(r => r.ReceivedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(r => r.Number.Contains(term) || r.PurchaseOrderNumber.Contains(term));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(r => r.ReceivedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<GoodsReceiptDto>([.. rows.Select(ReceiptDto)], total, page.Page, page.PageSize);
    }

    public async Task<Result<GoodsReceiptDto>> GetReceiptAsync(Guid id, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        var receipt = await db.GoodsReceipts.AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id, cancellationToken);
        var missing = Error.NotFound(ErrorCodes.Purchase.ReceiptNotFound, "Goods receipt not found.");
        if (receipt is null)
        {
            return missing;
        }

        var refusal = await access.CheckAsync(actor.Value, receipt.BranchId, Permissions.Purchasing.ReceiptView, cancellationToken);
        return refusal is { Type: ErrorType.NotFound } ? missing : refusal is not null ? refusal : ReceiptDto(receipt);
    }

    /// <summary>
    /// Applies the cost of a receipt that was booked by someone who may not change costs. Only a person who may see and change costs can do it. A receipt that a newer receipt of the same product
    /// has overtaken is marked done without changing the cost (the newer one decides).
    /// </summary>
    public async Task<Result<GoodsReceiptDto>> ApplyCostAsync(Guid id, CancellationToken cancellationToken)
    {
        var actor = await access.ActorAsync(cancellationToken);
        if (actor.IsFailure)
        {
            return actor.Error!;
        }

        if (!actor.Value.Access.HasAnywhere(Permissions.Catalog.ProductViewCost))
        {
            return Error.Forbidden(ErrorCodes.Purchase.CostNotAllowed, "Only staff who may change cost prices can apply the cost of a receipt.");
        }

        return await uow.ExecuteInTransactionAsync<Result<GoodsReceiptDto>>(async ct =>
        {
            var receipt = await db.GoodsReceipts.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id, ct);
            var missing = Error.NotFound(ErrorCodes.Purchase.ReceiptNotFound, "Goods receipt not found.");
            if (receipt is null)
            {
                return missing;
            }

            var refusal = await access.CheckAsync(actor.Value, receipt.BranchId, Permissions.Purchasing.ReceiptView, ct);
            if (refusal is not null)
            {
                return refusal.Type == ErrorType.NotFound ? missing : refusal;
            }

            var config = await settings.ReadAsync(ct);
            foreach (var line in receipt.Lines.Where(l => !l.CostApplied && l.AcceptedQuantity > 0).OrderBy(l => l.CreatedAt))
            {
                var newer = await (from l in db.GoodsReceiptLines.AsNoTracking() join r in db.GoodsReceipts.AsNoTracking() on l.ReceiptId equals r.Id
                                   where l.ProductId == line.ProductId && l.AcceptedQuantity > 0 && r.ReceivedAt > receipt.ReceivedAt
                                   select l.Id).AnyAsync(ct);
                if (!newer)
                {
                    var product = await db.Products.FirstAsync(p => p.Id == line.ProductId, ct);
                    product.SetCostPrice(PurchaseCost.Next(config.CostMethod, line.ProductCostBefore, line.ProductOnHandBefore, line.AcceptedQuantity, line.UnitCost));
                }

                line.MarkCostApplied();
            }

            await db.SaveChangesAsync(ct);
            return ReceiptDto(receipt);
        }, cancellationToken);
    }

    // ---- Low stock suggestions ----------------------------------------------------------------------------

    public async Task<Result<SuggestionsDto>> SuggestionsAsync(Guid? branchId, CancellationToken cancellationToken)
    {
        var target = await access.TargetAsync(branchId, Permissions.Purchasing.PoCreate, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var branch = target.Value.Branch;
        var low = await (from l in db.StockLevels.AsNoTracking()
                         join v in db.ProductVariants.AsNoTracking() on l.VariantId equals v.Id
                         join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                         where l.BranchId == branch.Id && l.ReorderLevel > 0 && l.OnHand - l.Reserved <= l.ReorderLevel && v.IsActive && v.TracksStock && p.Status != Domain.Catalog.ProductStatus.Archived
                         select new { Level = l, Variant = v, Product = p }).ToListAsync(cancellationToken);
        var variantIds = low.Select(x => x.Variant.Id).ToList();

        var onOrder = await (from l in db.PurchaseOrderLines.AsNoTracking() join o in db.PurchaseOrders.AsNoTracking() on l.PurchaseOrderId equals o.Id
                             where o.BranchId == branch.Id && (o.Status == PurchaseStatus.Sent || o.Status == PurchaseStatus.PartlyReceived) && variantIds.Contains(l.VariantId)
                             group l by l.VariantId into g
                             select new { g.Key, Open = g.Sum(x => x.OrderedQuantity - x.ReceivedQuantity) }).ToDictionaryAsync(x => x.Key, x => x.Open, cancellationToken);

        var history = await (from l in db.PurchaseOrderLines.AsNoTracking() join o in db.PurchaseOrders.AsNoTracking().IgnoreQueryFilters() on l.PurchaseOrderId equals o.Id
                             where variantIds.Contains(l.VariantId) && o.Status != PurchaseStatus.Cancelled
                             select new { l.VariantId, o.SupplierId, l.UnitCost, o.CreatedAt }).ToListAsync(cancellationToken);
        var latest = history.GroupBy(h => h.VariantId).ToDictionary(g => g.Key, g => g.OrderByDescending(h => h.CreatedAt).First());
        var supplierIds = latest.Values.Select(h => h.SupplierId).Distinct().ToList();
        var suppliers = await db.Suppliers.AsNoTracking().Where(s => supplierIds.Contains(s.Id) && s.IsActive).ToDictionaryAsync(s => s.Id, s => s.Name, cancellationToken);

        var lines = new List<(Guid? SupplierId, SuggestedLineDto Line)>();
        foreach (var x in low)
        {
            var open = onOrder.GetValueOrDefault(x.Variant.Id);
            var need = (2 * x.Level.ReorderLevel) - x.Level.Available - open;
            if (need <= 0)
            {
                continue;
            }

            latest.TryGetValue(x.Variant.Id, out var last);
            Guid? supplier = last is not null && suppliers.ContainsKey(last.SupplierId) ? last.SupplierId : null;
            lines.Add((supplier, new SuggestedLineDto(x.Variant.Id, x.Product.Id, x.Variant.Sku, x.Product.NameEn, x.Variant.ColorName, x.Variant.Size, x.Level.OnHand, x.Level.Reserved, x.Level.Available,
                x.Level.ReorderLevel, open, need, last?.UnitCost)));
        }

        var groups = lines.GroupBy(l => l.SupplierId).OrderBy(g => g.Key is null ? 1 : 0).ThenBy(g => g.Key is { } id ? suppliers[id] : string.Empty, StringComparer.Ordinal)
            .Select(g => new SuggestionGroupDto(g.Key, g.Key is { } id ? suppliers[id] : null, [.. g.Select(l => l.Line).OrderBy(l => l.ProductName, StringComparer.Ordinal).ThenBy(l => l.Sku, StringComparer.Ordinal)])).ToList();
        return new SuggestionsDto(branch.Id, branch.Code, groups, lines.Count);
    }
}

/// <summary>The cost methods of a goods receipt (setting <c>Purchasing.CostMethod</c>), as pure maths so they can be tested alone.</summary>
public static class PurchaseCost
{
    /// <summary>
    /// The new product cost after <paramref name="received"/> units arrived at <paramref name="unitCost"/>. LastCost: the unit cost of the newest receipt. WeightedAverage: units on hand
    /// (at the old cost) and units received (at the new cost) averaged by quantity; with no old cost or nothing on hand it is the unit cost. Rounded to 2 decimals.
    /// </summary>
    public static decimal Next(CostMethod method, decimal? oldCost, int onHandBefore, int received, decimal unitCost)
    {
        if (method == CostMethod.LastCost || oldCost is null || onHandBefore <= 0)
        {
            return Math.Round(unitCost, 2, MidpointRounding.AwayFromZero);
        }

        var average = ((onHandBefore * oldCost.Value) + (received * unitCost)) / (onHandBefore + received);
        return Math.Round(average, 2, MidpointRounding.AwayFromZero);
    }
}
