using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Application.Payments;
using Juvo.Domain.Billing;
using Juvo.Domain.Branches;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Billing;

/// <summary>Keys of the seller details printed on invoices (table <c>PlatformSettings</c>). Missing values fall back to the branch's own details.</summary>
public static class BusinessSettingKeys
{
    public const string Name = "Business.Name";
    public const string Address = "Business.Address";
    public const string Phone = "Business.Phone";
    public const string Email = "Business.Email";
    public const string TaxId = "Business.TaxId";
}

/// <summary>
/// Invoices and credit notes (module U). An invoice is issued once per order when the order is confirmed (or earlier when a refund needs one), numbered gap-free per branch
/// and month in the same transaction as the invoice itself, and is immutable afterwards. A credit note is issued for each completed refund, also gap-free and immutable.
/// </summary>
public sealed class InvoiceService(
    IApplicationDbContext db, IUnitOfWork uow, SettingsService settings, OrderAccess access, ICurrentUser currentUser, IDocumentPdfRenderer pdf, TimeProvider clock)
{
    private static Error NotFound() => Error.NotFound(ErrorCodes.Invoice.NotFound, "Invoice not found.");

    private Guid? CustomerId => currentUser is { IsAuthenticated: true, SubjectType: "customer", UserId: { } id } ? id : null;

    // ---- Numbers --------------------------------------------------------------------------------------------

    /// <summary>
    /// The next number of a kind (<c>INV</c>, <c>CN</c>) for the branch and month. The counter row is incremented inside the caller's transaction, so a rolled-back document
    /// gives its number back and concurrent issuers wait on the row lock: numbers never skip and never repeat.
    /// </summary>
    internal async Task<string> NextNumberAsync(Branch branch, string kind, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var period = OrderPlacement.LocalTime(branch, now).ToString("yyMM", System.Globalization.CultureInfo.InvariantCulture);
        var branchId = branch.Id;
        await db.AcquireTransactionLockAsync($"docseq:{branchId:N}:{kind}:{period}", cancellationToken); // one issuer at a time per counter: no gaps, no duplicates
        var bumped = await db.DocumentSequences.Where(s => s.BranchId == branchId && s.Kind == kind && s.Period == period)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastNumber, s => s.LastNumber + 1), cancellationToken);
        if (bumped == 0)
        {
            var row = new DocumentSequence(branchId, kind, period, 0);
            db.DocumentSequences.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.DocumentSequences.Remove(row); // someone created the row first
            }

            await db.DocumentSequences.Where(s => s.BranchId == branchId && s.Kind == kind && s.Period == period)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastNumber, s => s.LastNumber + 1), cancellationToken);
        }

        var next = await db.DocumentSequences.AsNoTracking().Where(s => s.BranchId == branchId && s.Kind == kind && s.Period == period).Select(s => s.LastNumber).FirstAsync(cancellationToken);
        return $"{kind}-{branch.OrderPrefix}-{period}-{next:D6}";
    }

    // ---- Issuing --------------------------------------------------------------------------------------------

    private async Task<InvoiceSellerDto> SellerAsync(Branch branch, CancellationToken cancellationToken)
    {
        var v = await settings.ReadAsync([BusinessSettingKeys.Name, BusinessSettingKeys.Address, BusinessSettingKeys.Phone, BusinessSettingKeys.Email, BusinessSettingKeys.TaxId], cancellationToken);
        string? Pick(string key, string? fallback) => v.TryGetValue(key, out var raw) && !string.IsNullOrWhiteSpace(raw) ? raw.Trim() : fallback;
        return new InvoiceSellerDto(Pick(BusinessSettingKeys.Name, "Juvo BD")!, Pick(BusinessSettingKeys.Address, branch.PickupAddress ?? branch.AddressLine), Pick(BusinessSettingKeys.Phone, branch.Phone),
            Pick(BusinessSettingKeys.Email, branch.Email), Pick(BusinessSettingKeys.TaxId, null));
    }

    /// <summary>
    /// The invoice of the order, issued now when there is none. The order must carry its lines. Runs in the caller's transaction (or its own); the caller saves.
    /// Tax comes from the rule the order was placed under, so an invoice never disagrees with the order.
    /// </summary>
    public async Task<Invoice> IssueAsync(Order order, Guid? by, string? byName, CancellationToken cancellationToken)
    {
        await db.AcquireTransactionLockAsync($"invoice:{order.Id:N}", cancellationToken); // a simultaneous issue for the same order waits and then finds this invoice
        var existing = db.Invoices.Local.FirstOrDefault(i => i.OrderId == order.Id) // issued earlier in this same unit of work, not saved yet
                       ?? await db.Invoices.IgnoreQueryFilters(["BranchScope"]).Include(i => i.Lines).FirstOrDefaultAsync(i => i.OrderId == order.Id, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var now = clock.GetUtcNow();
        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == order.BranchId, cancellationToken);
        var number = await NextNumberAsync(branch, "INV", now, cancellationToken);
        var seller = await SellerAsync(branch, cancellationToken);
        var rule = order.TaxRuleCode is { } code ? await db.TaxRules.AsNoTracking().FirstOrDefaultAsync(r => r.Code == code, cancellationToken) : null;
        var items = order.Items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Sku).ToList();
        var discount = order.CouponDiscount + order.ManualDiscount;
        var tax = TaxMath.ForOrder([.. items.Select(i => i.LineTotal)], discount, order.DeliveryCharge, order.TaxRatePercent ?? 0m, rule?.AppliesToDelivery ?? true);
        var lines = items.Select((i, n) => new NewInvoiceLine(i.Name, i.NameBn, i.Sku, string.Join(" / ", new[] { i.ColorName, i.Size }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } v ? v : null,
            i.Quantity, i.UnitPrice, i.LineTotal, tax.LineTax.Count > n ? tax.LineTax[n] : 0m)).ToList();
        var address = string.Join(", ", new[] { order.ShipName, order.ShipAddressLine, order.ShipArea, order.ShipUpazila, order.ShipDistrict ?? order.ShipCity, order.ShipDivision ?? order.ShipStateProvince, order.ShipPostalCode, order.ShipCountryCode }
            .Where(s => !string.IsNullOrWhiteSpace(s)));
        var invoice = Invoice.Issue(
            new NewInvoice(number, order.BranchId, order.Id, order.Number, order.CustomerId, order.CustomerName ?? order.ShipName, order.CustomerMobile, order.CustomerEmail, address,
                new InvoiceSeller(seller.Name, seller.Address, seller.Phone, seller.Email, seller.TaxId), order.Currency, order.Subtotal, discount, order.DeliveryCharge, order.GrandTotal,
                order.TaxRuleCode, rule?.Name, order.TaxRatePercent, tax.DeliveryTax, tax.TaxTotal, lines, by, byName), now);
        db.Invoices.Add(invoice);
        return invoice;
    }

    /// <summary>Issues the invoice of a confirmed order on its own transaction and saves (used by the outbox handler and the customer's first download). A simultaneous issue returns the winner.</summary>
    public async Task<Result<Invoice>> EnsureForOrderAsync(Guid orderId, Guid? by, string? byName, bool force, CancellationToken cancellationToken)
    {
        try
        {
            return await uow.ExecuteInTransactionAsync<Result<Invoice>>(async ct =>
            {
                var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).FirstOrDefaultAsync(o => o.Id == orderId, ct);
                if (order is null)
                {
                    return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
                }

                if (!force && order.ConfirmedAt is null)
                {
                    return Error.Conflict(ErrorCodes.Invoice.OrderNotInvoiceable, "An invoice is issued once the order is confirmed.");
                }

                var invoice = await IssueAsync(order, by, byName, ct);
                await db.SaveChangesAsync(ct);
                return invoice;
            }, cancellationToken);
        }
        catch (DbUpdateException)
        {
            var winner = await db.Invoices.IgnoreQueryFilters(["BranchScope"]).Include(i => i.Lines).AsNoTracking().FirstOrDefaultAsync(i => i.OrderId == orderId, cancellationToken);
            return winner is not null ? winner : Error.Conflict(ErrorCodes.Invoice.OrderNotInvoiceable, "The invoice could not be issued. Try again.");
        }
    }

    /// <summary>Issues the credit note of a completed refund (and the invoice first when the order has none). Does not save. One credit note per refund.</summary>
    public async Task<CreditNote> IssueCreditNoteAsync(Refund refund, Order order, Guid? by, string? byName, CancellationToken cancellationToken)
    {
        var invoice = await IssueAsync(order, by, byName, cancellationToken);
        var now = clock.GetUtcNow();
        var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == invoice.BranchId, cancellationToken);
        var number = await NextNumberAsync(branch, "CN", now, cancellationToken);
        var taxShare = invoice.GrandTotal <= 0 ? 0m : Math.Round(invoice.TaxTotal * refund.Amount / invoice.GrandTotal, 2, MidpointRounding.AwayFromZero);
        var note = new CreditNote(number, invoice, refund.Id, refund.Amount, taxShare, refund.Reason, by, byName, now);
        db.CreditNotes.Add(note);
        refund.LinkCreditNote(note.Id);
        return note;
    }

    // ---- Views ----------------------------------------------------------------------------------------------

    private async Task<InvoiceDto> ToDtoAsync(Invoice i, CancellationToken cancellationToken)
    {
        var branchCode = await db.Branches.AsNoTracking().Where(b => b.Id == i.BranchId).Select(b => b.Code).FirstAsync(cancellationToken);
        var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => o.Id == i.OrderId).Select(o => new { o.PaymentMethod, o.PaymentStatus }).FirstAsync(cancellationToken);
        var notes = await db.CreditNotes.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(c => c.InvoiceId == i.Id).OrderBy(c => c.IssuedAt).ThenBy(c => c.Number).ToListAsync(cancellationToken);
        return new InvoiceDto(i.Id, i.Number, i.OrderId, i.OrderNumber, i.BranchId, branchCode, i.IssuedAt, i.Currency, new InvoiceSellerDto(i.SellerName, i.SellerAddress, i.SellerPhone, i.SellerEmail, i.SellerTaxId),
            i.CustomerName, i.CustomerMobile, i.CustomerEmail, i.BillingAddress,
            [.. i.Lines.OrderBy(l => l.SortOrder).Select(l => new InvoiceLineDto(l.Name, l.NameBn, l.Sku, l.Variant, l.Quantity, l.UnitPrice, l.LineTotal, l.TaxAmount))],
            i.Subtotal, i.Discount, i.DeliveryCharge, i.GrandTotal, i.TaxRuleCode, i.TaxRuleName, i.TaxRatePercent, i.DeliveryTax, i.TaxTotal, i.GrandTotal - i.TaxTotal, order.PaymentMethod, order.PaymentStatus,
            notes.Sum(c => c.Amount), [.. notes.Select(c => new CreditNoteSummaryDto(c.Id, c.Number, c.Amount, c.TaxAmount, c.IssuedAt))]);
    }

    private static PdfDocumentModel ToModel(InvoiceDto d, string language)
    {
        var bn = language == "bn";
        return new PdfDocumentModel(false, bn ? "কর চালান" : "Tax invoice", d.Number, null, d.IssuedAt, language, d.Seller, d.CustomerName, d.CustomerMobile, d.CustomerEmail, d.BillingAddress, d.OrderNumber, d.Currency,
            [.. d.Lines.Select(l => new PdfLine(bn && !string.IsNullOrWhiteSpace(l.NameBn) ? l.NameBn : l.Name, l.Variant, l.Sku, l.Quantity, l.UnitPrice, l.LineTotal))], d.Subtotal, d.Discount, d.DeliveryCharge,
            d.GrandTotal, d.TaxRuleCode is null ? null : $"{d.TaxRuleName ?? d.TaxRuleCode} {d.TaxRatePercent:0.##}%", d.TaxTotal, d.NetTotal, d.PaymentMethod, d.PaymentStatus.ToString(), null);
    }

    private async Task<CreditNoteDto> ToDtoAsync(CreditNote c, CancellationToken cancellationToken)
    {
        var code = await db.Branches.AsNoTracking().Where(b => b.Id == c.BranchId).Select(b => b.Code).FirstAsync(cancellationToken);
        return new CreditNoteDto(c.Id, c.Number, c.InvoiceId, c.InvoiceNumber, c.OrderId, c.OrderNumber, c.BranchId, code, c.Amount, c.TaxAmount, c.Currency, c.Reason, c.IssuedAt);
    }

    private static string Language(string? language) => string.Equals(language, "bn", StringComparison.OrdinalIgnoreCase) ? "bn" : "en";

    private Result<(byte[] Bytes, string FileName)> Render(PdfDocumentModel model)
    {
        try
        {
            return (pdf.Render(model), $"{model.Number}.pdf");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Error.Failure(ErrorCodes.Invoice.PdfFailed, "The PDF could not be created: " + ex.Message);
        }
    }

    // ---- Staff ----------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<InvoiceListItemDto>>> ListAsync(PageRequest request, InvoiceListQuery filter, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Tax.InvoiceView);
        var query = from i in db.Invoices.AsNoTracking() join b in db.Branches.AsNoTracking() on i.BranchId equals b.Id select new { Invoice = i, BranchCode = b.Code };
        if (visible is not null)
        {
            query = query.Where(x => visible.Contains(x.Invoice.BranchId));
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(x => x.Invoice.BranchId == branchId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(x => x.Invoice.IssuedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(x => x.Invoice.IssuedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(x => x.Invoice.Number.Contains(term) || x.Invoice.OrderNumber.Contains(term) || x.Invoice.CustomerMobile.Contains(term) || x.Invoice.CustomerName.Contains(term));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(x => x.Invoice.IssuedAt).ThenByDescending(x => x.Invoice.Number).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<InvoiceListItemDto>(
            [.. rows.Select(x => new InvoiceListItemDto(x.Invoice.Id, x.Invoice.Number, x.Invoice.OrderId, x.Invoice.OrderNumber, x.Invoice.BranchId, x.BranchCode, x.Invoice.CustomerName, x.Invoice.GrandTotal, x.Invoice.TaxTotal, x.Invoice.IssuedAt))],
            total, page.Page, page.PageSize);
    }

    private async Task<(Invoice? Invoice, Error? Refusal)> StaffInvoiceAsync(Guid id, string permission, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return (null, Error.Unauthorized());
        }

        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
        if (invoice is null)
        {
            return (null, NotFound());
        }

        var refusal = await access.CheckAsync(actor, invoice.BranchId, permission, cancellationToken);
        return (refusal is { Type: ErrorType.NotFound } ? null : invoice, refusal is { Type: ErrorType.NotFound } ? NotFound() : refusal);
    }

    public async Task<Result<InvoiceDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var (invoice, refusal) = await StaffInvoiceAsync(id, Permissions.Tax.InvoiceView, cancellationToken);
        return refusal is not null ? refusal : await ToDtoAsync(invoice!, cancellationToken);
    }

    public async Task<Result<(byte[] Bytes, string FileName)>> PdfAsync(Guid id, string? language, CancellationToken cancellationToken)
    {
        var (invoice, refusal) = await StaffInvoiceAsync(id, Permissions.Tax.InvoiceDownload, cancellationToken);
        return refusal is not null ? refusal : Render(ToModel(await ToDtoAsync(invoice!, cancellationToken), Language(language)));
    }

    /// <summary>Staff issue (or fetch) the invoice of an order. Only for an order that was confirmed.</summary>
    public async Task<Result<InvoiceDto>> IssueForOrderAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var branchId = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => (Guid?)o.BranchId).FirstOrDefaultAsync(cancellationToken);
        if (branchId is null)
        {
            return Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.");
        }

        var refusal = await access.CheckAsync(actor, branchId.Value, Permissions.Tax.InvoiceIssue, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var who = access.WhoIs(actor);
        var issued = await EnsureForOrderAsync(orderId, who.Id, who.Name, false, cancellationToken);
        return issued.IsFailure ? issued.Error! : await ToDtoAsync(issued.Value, cancellationToken);
    }

    public async Task<Result<PagedResult<CreditNoteDto>>> ListCreditNotesAsync(PageRequest request, CreditNoteListQuery filter, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var visible = OrderAccess.VisibleBranches(actor, Permissions.Tax.CreditNoteView);
        var query = db.CreditNotes.AsNoTracking().AsQueryable();
        if (visible is not null)
        {
            query = query.Where(c => visible.Contains(c.BranchId));
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(c => c.BranchId == branchId);
        }

        if (filter.From is { } from)
        {
            query = query.Where(c => c.IssuedAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(c => c.IssuedAt < to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(c => c.Number.Contains(term) || c.InvoiceNumber.Contains(term) || c.OrderNumber.Contains(term));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(c => c.IssuedAt).ThenByDescending(c => c.Number).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var codes = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Code, cancellationToken);
        return new PagedResult<CreditNoteDto>([.. rows.Select(c => new CreditNoteDto(c.Id, c.Number, c.InvoiceId, c.InvoiceNumber, c.OrderId, c.OrderNumber, c.BranchId, codes.GetValueOrDefault(c.BranchId, string.Empty), c.Amount, c.TaxAmount, c.Currency, c.Reason, c.IssuedAt))],
            total, page.Page, page.PageSize);
    }

    private async Task<(CreditNote? Note, Error? Refusal)> StaffCreditNoteAsync(Guid id, string permission, CancellationToken cancellationToken)
    {
        if (await access.ActorAsync(cancellationToken) is not { } actor)
        {
            return (null, Error.Unauthorized());
        }

        var note = await db.CreditNotes.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (note is null)
        {
            return (null, Error.NotFound(ErrorCodes.Invoice.CreditNoteNotFound, "Credit note not found."));
        }

        var refusal = await access.CheckAsync(actor, note.BranchId, permission, cancellationToken);
        return refusal is { Type: ErrorType.NotFound } ? (null, Error.NotFound(ErrorCodes.Invoice.CreditNoteNotFound, "Credit note not found.")) : (refusal is null ? note : null, refusal);
    }

    public async Task<Result<CreditNoteDto>> GetCreditNoteAsync(Guid id, CancellationToken cancellationToken)
    {
        var (note, refusal) = await StaffCreditNoteAsync(id, Permissions.Tax.CreditNoteView, cancellationToken);
        return refusal is not null ? refusal : await ToDtoAsync(note!, cancellationToken);
    }

    public async Task<Result<(byte[] Bytes, string FileName)>> CreditNotePdfAsync(Guid id, string? language, CancellationToken cancellationToken)
    {
        var (note, refusal) = await StaffCreditNoteAsync(id, Permissions.Tax.CreditNoteView, cancellationToken);
        if (refusal is not null)
        {
            return refusal;
        }

        var invoice = await db.Invoices.AsNoTracking().Include(i => i.Lines).FirstAsync(i => i.Id == note!.InvoiceId, cancellationToken);
        var d = await ToDtoAsync(invoice, cancellationToken);
        var lang = Language(language);
        var model = new PdfDocumentModel(true, lang == "bn" ? "ক্রেডিট নোট" : "Credit note", note!.Number, note.InvoiceNumber, note.IssuedAt, lang, d.Seller, d.CustomerName, d.CustomerMobile, d.CustomerEmail, d.BillingAddress,
            note.OrderNumber, note.Currency, [new PdfLine(note.Reason ?? (lang == "bn" ? "ফেরত" : "Refund"), null, note.OrderNumber, 1, note.Amount, note.Amount)], note.Amount, 0m, 0m, note.Amount,
            d.TaxRuleCode is null ? null : $"{d.TaxRuleName ?? d.TaxRuleCode} {d.TaxRatePercent:0.##}%", note.TaxAmount, note.Amount - note.TaxAmount, d.PaymentMethod, d.PaymentStatus.ToString(), null);
        return Render(model);
    }

    // ---- Customer (signed-in owner, or the tracking token) ---------------------------------------------------

    private async Task<Result<Invoice>> CustomerInvoiceAsync(Guid? orderId, string? number, string? token, CancellationToken cancellationToken)
    {
        var customerId = CustomerId;
        var hash = string.IsNullOrWhiteSpace(token) ? null : OrderPlacement.Hash(token.Trim());
        if (customerId is null && hash is null)
        {
            return Error.NotFound(ErrorCodes.Invoice.NotFound, "We could not find an invoice for this order.");
        }

        var query = db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => (customerId != null && o.CustomerId == customerId) || (hash != null && o.TrackingTokenHash == hash));
        if (orderId is { } id)
        {
            query = query.Where(o => o.Id == id);
        }
        else if (!string.IsNullOrWhiteSpace(number))
        {
            var normalized = number.Trim().ToUpperInvariant();
            query = query.Where(o => o.Number == normalized);
        }
        else
        {
            return Error.NotFound(ErrorCodes.Invoice.NotFound, "We could not find an invoice for this order.");
        }

        var order = await query.Select(o => new { o.Id, o.ConfirmedAt }).FirstOrDefaultAsync(cancellationToken);
        if (order is null)
        {
            return Error.NotFound(ErrorCodes.Invoice.NotFound, "We could not find an invoice for this order.");
        }

        var existing = await db.Invoices.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Include(i => i.Lines).FirstOrDefaultAsync(i => i.OrderId == order.Id, cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        return await EnsureForOrderAsync(order.Id, null, "System", false, cancellationToken); // confirmed, but the outbox has not issued it yet
    }

    public async Task<Result<InvoiceDto>> CustomerGetAsync(Guid? orderId, string? number, string? token, CancellationToken cancellationToken)
    {
        var invoice = await CustomerInvoiceAsync(orderId, number, token, cancellationToken);
        return invoice.IsFailure ? invoice.Error! : await ToDtoAsync(invoice.Value, cancellationToken);
    }

    public async Task<Result<(byte[] Bytes, string FileName)>> CustomerPdfAsync(Guid? orderId, string? number, string? token, string? language, CancellationToken cancellationToken)
    {
        var invoice = await CustomerInvoiceAsync(orderId, number, token, cancellationToken);
        return invoice.IsFailure ? invoice.Error! : Render(ToModel(await ToDtoAsync(invoice.Value, cancellationToken), Language(language)));
    }
}
