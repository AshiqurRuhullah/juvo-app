using System.Globalization;
using Juvo.Application.Abstractions;
using Juvo.Application.Billing;
using Juvo.Application.Operations;
using Juvo.Application.Orders;
using Juvo.Domain.Accounting;
using Juvo.Domain.Couriers;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Domain.Purchasing;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Reports;

/// <summary>Draws a report as a PDF (title, filters, tables, totals, the shop name). Implemented in Infrastructure with SkiaSharp.</summary>
public interface IReportPdfRenderer
{
    byte[] Render(ReportDoc document, string shopName, string lang);
}

public sealed record ExportedFile(byte[] Content, string ContentType, string FileName);

/// <summary>Writes report tables as CSV, Excel or PDF. The CSV and Excel files hold one table (the first, or the one named by <c>section</c>); the PDF holds all of them.</summary>
public sealed class ReportFileWriter(ITabularFile files, IReportPdfRenderer pdf, SettingsService settings)
{
    public static readonly string[] Formats = ["csv", "xlsx", "pdf"];

    public static string? NormalizeFormat(string? format) => string.IsNullOrWhiteSpace(format) ? "csv" : Formats.Contains(format.Trim().ToLowerInvariant()) ? format.Trim().ToLowerInvariant() : null;

    public async Task<Result<ExportedFile>> WriteAsync(ReportDoc doc, string format, string lang, string? section, string fileStem, CancellationToken ct)
    {
        if (format == "pdf")
        {
            var values = await settings.ReadAsync([BusinessSettingKeys.Name], ct);
            var shop = values.GetValueOrDefault(BusinessSettingKeys.Name) is { Length: > 0 } n ? n : "Juvo BD";
            return new ExportedFile(pdf.Render(doc, shop, lang), "application/pdf", fileStem + ".pdf");
        }

        var pick = string.IsNullOrWhiteSpace(section) ? doc.Sections[0] : doc.Sections.FirstOrDefault(x => string.Equals(x.Key, section.Trim(), StringComparison.OrdinalIgnoreCase));
        if (pick is null)
        {
            return Error.NotFound(ErrorCodes.Report.Unknown, "This report has no section called '" + section + "'. Sections: " + string.Join(", ", doc.Sections.Select(x => x.Key)) + ".");
        }

        var rows = pick.Rows.ToList();
        if (pick.Totals is not null)
        {
            rows.Add(pick.Totals);
        }

        var fmt = format == "xlsx" ? TabularFormat.Xlsx : TabularFormat.Csv;
        var bytes = files.Write(new TableData(pick.Headers, rows, pick.NumericColumns), fmt);
        return new ExportedFile(bytes, files.ContentType(fmt), fileStem + "." + files.Extension(fmt));
    }
}

/// <summary>Turns a named report into a file (module P exports). Each report names its own export permission, which also decides the branches that are included.</summary>
public sealed class ReportExportService(
    SalesReportService sales, OrdersReportService orders, InventoryReportService inventory, FinanceReportService finance, CourierReportService couriers, CustomersReportService customers, ReportFileWriter writer)
{
    public static readonly string[] Names = ["sales", "orders", "inventory", "finance", "courier", "customers"];

    public static string ExportPermission(string report) => report switch
    {
        "sales" => Permissions.Reports.SalesExport,
        "orders" => Permissions.Reports.OrdersExport,
        "inventory" => Permissions.Reports.InventoryExport,
        "finance" => Permissions.Reports.FinanceExport,
        "courier" => Permissions.Reports.CourierExport,
        _ => Permissions.Reports.CustomersExport,
    };

    private static List<KeyValuePair<string, string>> FilterList(ReportFiltersDto f, string lang, bool grouped)
    {
        var list = new List<KeyValuePair<string, string>>
        {
            new(ReportText.T("Period", lang), Cells.D(f.From) + " - " + Cells.D(f.To)),
            new(ReportText.T("Branch", lang), f.BranchCodes.Count == 0 ? ReportText.T("All branches", lang) : string.Join(", ", f.BranchCodes)),
        };
        if (grouped)
        {
            list.Add(new(ReportText.T("Group by", lang), f.GroupBy ?? string.Empty));
        }

        return list;
    }

    public async Task<Result<ExportedFile>> ExportAsync(string report, ReportQuery q, string? format, string? lang, string? section, CancellationToken ct)
    {
        var fmt = ReportFileWriter.NormalizeFormat(format);
        if (fmt is null)
        {
            return Error.Failure(ErrorCodes.Report.FormatInvalid, "format must be csv, xlsx or pdf.");
        }

        if (!ReportText.IsLang(lang))
        {
            return Ops.Invalid("lang", "lang must be en or bn.");
        }

        var l = lang ?? "en";
        var permission = ExportPermission(report);
        ReportDoc doc;
        string stem;
        switch (report)
        {
            case "sales":
            {
                var r = await sales.GetAsync(q, permission, ct);
                if (r.IsFailure)
                {
                    return r.Error!;
                }

                doc = SalesReportService.ToDoc(r.Value, l, ReportText.T("Sales report", l), FilterList(r.Value.Filters, l, true));
                stem = $"juvo-sales-{r.Value.Filters.GroupBy}-{Cells.D(r.Value.Filters.From)}-{Cells.D(r.Value.Filters.To)}";
                break;
            }

            case "orders":
            {
                var r = await orders.GetAsync(q, permission, ct);
                if (r.IsFailure)
                {
                    return r.Error!;
                }

                doc = OrdersReportService.ToDoc(r.Value, l, ReportText.T("Order funnel report", l), FilterList(r.Value.Filters, l, false));
                stem = $"juvo-orders-{Cells.D(r.Value.Filters.From)}-{Cells.D(r.Value.Filters.To)}";
                break;
            }

            case "inventory":
            {
                var r = await inventory.GetAsync(q, permission, ct);
                if (r.IsFailure)
                {
                    return r.Error!;
                }

                doc = InventoryReportService.ToDoc(r.Value, l, ReportText.T("Inventory report", l), FilterList(r.Value.Filters, l, false));
                stem = $"juvo-inventory-{Cells.D(r.Value.Filters.To)}";
                break;
            }

            case "finance":
            {
                var r = await finance.GetAsync(q, permission, ct);
                if (r.IsFailure)
                {
                    return r.Error!;
                }

                doc = FinanceReportService.ToDoc(r.Value, l, ReportText.T("Finance report", l), FilterList(r.Value.Filters, l, true));
                stem = $"juvo-finance-{r.Value.Filters.GroupBy}-{Cells.D(r.Value.Filters.From)}-{Cells.D(r.Value.Filters.To)}";
                break;
            }

            case "courier":
            {
                var r = await couriers.GetAsync(q, permission, ct);
                if (r.IsFailure)
                {
                    return r.Error!;
                }

                doc = CourierReportService.ToDoc(r.Value, l, ReportText.T("Courier performance report", l), FilterList(r.Value.Filters, l, false));
                stem = $"juvo-courier-{Cells.D(r.Value.Filters.From)}-{Cells.D(r.Value.Filters.To)}";
                break;
            }

            case "customers":
            {
                var r = await customers.GetAsync(q, permission, ct);
                if (r.IsFailure)
                {
                    return r.Error!;
                }

                doc = CustomersReportService.ToDoc(r.Value, l, ReportText.T("Customers report", l), FilterList(r.Value.Filters, l, false));
                stem = $"juvo-customers-{Cells.D(r.Value.Filters.From)}-{Cells.D(r.Value.Filters.To)}";
                break;
            }

            default:
                return Error.NotFound(ErrorCodes.Report.Unknown, "Unknown report.");
        }

        return await writer.WriteAsync(doc, fmt, l, section, stem, ct);
    }
}

/// <summary>
/// The accountant export (FR-X05): day book, approved expenses, profit and loss, open supplier payables or goods received, as CSV or Excel. Money columns are numbers, text that
/// could be read as a formula is neutralised by the file writer. Profit and loss and purchases carry cost prices, so they need <c>catalog.product.view-cost</c> as well.
/// </summary>
public sealed class AccountantExportService(ReportAccess access, PnlService pnl, IApplicationDbContext db, ReportFileWriter writer, TimeProvider clock)
{
    public static readonly string[] Kinds = ["daybook", "expenses", "pnl", "payables", "purchases"];
    public const int MaxRows = 50_000;

    private static Error TooMany() => Error.Failure(ErrorCodes.Report.RangeTooLarge, $"The export would have more than {MaxRows} rows. Choose a shorter period.");

    public async Task<Result<ExportedFile>> ExportAsync(ReportQuery q, string? kind, string? format, CancellationToken ct)
    {
        var fmt = ReportFileWriter.NormalizeFormat(format);
        if (fmt is null or "pdf")
        {
            return Error.Failure(ErrorCodes.Report.FormatInvalid, "format must be csv or xlsx.");
        }

        var k = kind?.Trim().ToLowerInvariant();
        if (k is null || !Kinds.Contains(k))
        {
            return Ops.Invalid("kind", "kind must be one of: " + string.Join(", ", Kinds) + ".");
        }

        var groupBys = k == "pnl" ? new[] { "month", "week", "day" } : null;
        var scope = await access.ScopeAsync(q, Permissions.Accounting.ExportRun, groupBys, true, ct);
        if (scope.IsFailure)
        {
            return scope.Error!;
        }

        var s = scope.Value;
        if (k is "pnl" or "purchases" && !s.CanSeeCost)
        {
            return Error.Forbidden(ErrorCodes.Report.CostHidden, "This export contains cost prices; you need the permission to see cost prices.");
        }

        var table = k switch
        {
            "daybook" => await DaybookAsync(s, ct),
            "expenses" => await ExpensesAsync(s, ct),
            "pnl" => await PnlAsync(s, ct),
            "payables" => await PayablesAsync(s, ct),
            _ => await PurchasesAsync(s, ct),
        };
        if (table.IsFailure)
        {
            return table.Error!;
        }

        var doc = new ReportDoc("accounting-" + k, "Accountant export: " + k, [], clock.GetUtcNow(), [table.Value], []);
        return await writer.WriteAsync(doc, fmt, "en", null, $"juvo-{k}-{Cells.D(s.From)}-{Cells.D(s.To)}", ct);
    }

    private async Task<Dictionary<Guid, string>> BranchCodesAsync(CancellationToken ct) => await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Code, ct);

    private async Task<Result<ReportSection>> DaybookAsync(ReportScope s, CancellationToken ct)
    {
        var from = s.From;
        var to = s.To;
        var stored = s.Apply(db.LedgerEntries.AsNoTracking()).Where(e => e.EntryDate >= from && e.EntryDate <= to);
        PaymentStatus[] paid = [PaymentStatus.Paid, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded];
        var receipts = s.Apply(db.Payments.AsNoTracking()).Where(p => p.Kind != PaymentKind.Cod && p.Kind != PaymentKind.StoreCredit && p.PaidAt != null && p.PaidAt >= s.FromAt && p.PaidAt < s.ToAt && paid.Contains(p.Status));
        var cod = s.Apply(db.CodSettlements.AsNoTracking()).Where(x => x.Status == SettlementStatus.Settled && x.SettledAt != null && x.SettledAt >= s.FromAt && x.SettledAt < s.ToAt);
        var refunds = s.Apply(db.Refunds.AsNoTracking()).Where(r => r.Status == RefundState.Completed && r.Mode != RefundMode.StoreCredit && r.CompletedAt != null && r.CompletedAt >= s.FromAt && r.CompletedAt < s.ToAt);
        if (await stored.CountAsync(ct) + await receipts.CountAsync(ct) + await cod.CountAsync(ct) + await refunds.CountAsync(ct) > MaxRows)
        {
            return TooMany();
        }

        var codes = await BranchCodesAsync(ct);
        var accounts = await db.FinanceAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var rows = new List<(DateOnly Date, DateTimeOffset At, string Branch, string Kind, string Account, string Description, decimal Amount, string Reference, bool Derived)>();
        foreach (var e in await stored.ToListAsync(ct))
        {
            rows.Add((e.EntryDate, e.CreatedAt, codes.GetValueOrDefault(e.BranchId, string.Empty), e.Kind.ToString(), accounts.GetValueOrDefault(e.AccountId, string.Empty), e.Description, e.Amount, e.SourceType ?? string.Empty, false));
        }

        foreach (var p in await receipts.Select(p => new { p.PaidAt, p.BranchId, p.Amount, p.OrderNumber, p.Method }).ToListAsync(ct))
        {
            rows.Add((Ops.BstDate(p.PaidAt!.Value), p.PaidAt.Value, codes.GetValueOrDefault(p.BranchId, string.Empty), "CustomerReceipt", string.Empty, "Payment for order " + p.OrderNumber, p.Amount, p.Method, true));
        }

        foreach (var c in await cod.Select(c => new { c.SettledAt, c.BranchId, c.ReceivedTotal, c.Number, c.CourierName }).ToListAsync(ct))
        {
            rows.Add((Ops.BstDate(c.SettledAt!.Value), c.SettledAt.Value, codes.GetValueOrDefault(c.BranchId, string.Empty), "CodSettlement", string.Empty, "COD settlement " + c.Number + " (" + c.CourierName + ")", c.ReceivedTotal ?? 0m, "CodSettlement", true));
        }

        foreach (var r in await refunds.Select(r => new { r.CompletedAt, r.BranchId, r.Amount, Order = db.Payments.Where(p => p.Id == r.PaymentId).Select(p => p.OrderNumber).FirstOrDefault() }).ToListAsync(ct))
        {
            rows.Add((Ops.BstDate(r.CompletedAt!.Value), r.CompletedAt.Value, codes.GetValueOrDefault(r.BranchId, string.Empty), "Refund", string.Empty, "Refund for order " + r.Order, -r.Amount, "Refund", true));
        }

        string[] h = ["Date", "Branch", "Kind", "Account", "Description", "Money in", "Money out", "Net", "Reference", "Read from payments"];
        var body = rows.OrderBy(r => r.Date).ThenBy(r => r.At).Select(r => (IReadOnlyList<string?>)
            [Cells.D(r.Date), r.Branch, r.Kind, r.Account, r.Description, Cells.M(r.Amount > 0 ? r.Amount : 0m), Cells.M(r.Amount < 0 ? -r.Amount : 0m), Cells.M(r.Amount), r.Reference, r.Derived ? "yes" : "no"]).ToList();
        var totals = new List<string?> { "Total", null, null, null, null, Cells.M(rows.Where(r => r.Amount > 0).Sum(r => r.Amount)), Cells.M(rows.Where(r => r.Amount < 0).Sum(r => -r.Amount)), Cells.M(rows.Sum(r => r.Amount)), null, null };
        return new ReportSection("daybook", "Day book", h, body, totals, Cells.Set("Money in", "Money out", "Net"));
    }

    private async Task<Result<ReportSection>> ExpensesAsync(ReportScope s, CancellationToken ct)
    {
        var q = s.Apply(db.Expenses.AsNoTracking()).Where(e => e.Status == ExpenseStatus.Approved && e.ExpenseDate >= s.From && e.ExpenseDate <= s.To);
        if (await q.CountAsync(ct) > MaxRows)
        {
            return TooMany();
        }

        var list = await q.OrderBy(e => e.ExpenseDate).ThenBy(e => e.Number).ToListAsync(ct);
        var codes = await BranchCodesAsync(ct);
        var categories = await db.ExpenseCategories.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.NameEn, ct);
        var accounts = await db.FinanceAccounts.AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        var suppliers = await db.Suppliers.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        string[] h = ["Number", "Date", "Branch", "Category", "Amount", "Method", "Account", "Supplier", "Reference", "Notes", "Approved by", "Auto approved"];
        var rows = list.Select(e => (IReadOnlyList<string?>)[e.Number, Cells.D(e.ExpenseDate), codes.GetValueOrDefault(e.BranchId, string.Empty), categories.GetValueOrDefault(e.CategoryId, string.Empty), Cells.M(e.Amount), e.Method.ToString(),
            accounts.GetValueOrDefault(e.AccountId, string.Empty), e.SupplierId is { } sid ? suppliers.GetValueOrDefault(sid) : null, e.Reference, e.Notes, e.DecidedByName, e.AutoApproved ? "yes" : "no"]).ToList();
        return new ReportSection("expenses", "Approved expenses", h, rows, ["Total", null, null, null, Cells.M(list.Sum(e => e.Amount)), null, null, null, null, null, null, null], Cells.Set("Amount"));
    }

    private async Task<Result<ReportSection>> PnlAsync(ReportScope s, CancellationToken ct)
    {
        var p = await pnl.BuildAsync(s, ct);
        string[] h = ["Period", "Orders", "Gross sales", "Discounts", "Refunds", "Net revenue", "Delivery collected", "Cost of goods", "Gross profit", "Courier charges paid", "Gateway fees (not recorded)", "Expenses", "Net profit", "Purchases received (information)"];
        List<string?> Row(PnlBucketDto b, string label) => [label, Cells.N(b.Orders), Cells.M(b.GrossSales), Cells.M(b.Discounts), Cells.M(b.Refunds), Cells.M(b.NetRevenue), Cells.M(b.DeliveryCollected), Cells.M(b.CostOfGoods), Cells.M(b.GrossProfit),
            Cells.M(b.CourierCharges), Cells.M(b.GatewayFees), Cells.M(b.Expenses), Cells.M(b.NetProfit), Cells.M(b.PurchasesReceived)];
        return new ReportSection("pnl", "Profit and loss", h, [.. p.Series.Select(b => (IReadOnlyList<string?>)Row(b, b.Label))], Row(p.Total, "Total"), Cells.Set(h.Skip(1).ToArray()));
    }

    private async Task<Result<ReportSection>> PayablesAsync(ReportScope s, CancellationToken ct)
    {
        // Open supplier invoices as of today (the date range does not apply). Amounts payable are computed from the lines, so this reads the open orders only.
        var open = await s.Apply(db.PurchaseOrders.AsNoTracking()).Include(p => p.Lines).Where(p => p.Status != PurchaseStatus.Cancelled && p.PaymentStatus != SupplierPaymentStatus.Paid).ToListAsync(ct);
        var today = Ops.BstDate(clock.GetUtcNow());
        var due = open.Where(p => p.Outstanding > 0m).OrderBy(p => p.DueDate ?? DateOnly.MaxValue).ThenBy(p => p.Number).ToList();
        var codes = await BranchCodesAsync(ct);
        var suppliers = await db.Suppliers.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        string[] h = ["PO number", "Supplier", "Branch", "Supplier invoice", "Invoice date", "Due date", "Payable", "Paid", "Outstanding", "Overdue days"];
        var rows = due.Select(p => (IReadOnlyList<string?>)[p.Number, suppliers.GetValueOrDefault(p.SupplierId, string.Empty), codes.GetValueOrDefault(p.BranchId, string.Empty), p.SupplierInvoiceNumber,
            p.SupplierInvoiceDate is { } d ? Cells.D(d) : null, p.DueDate is { } du ? Cells.D(du) : null, Cells.M(p.AmountPayable), Cells.M(p.AmountPaid), Cells.M(p.Outstanding),
            p.DueDate is { } dd && dd < today ? Cells.N(today.DayNumber - dd.DayNumber) : "0"]).ToList();
        return new ReportSection("payables", "Open supplier payables", h, rows, ["Total", null, null, null, null, null, Cells.M(due.Sum(p => p.AmountPayable)), Cells.M(due.Sum(p => p.AmountPaid)), Cells.M(due.Sum(p => p.Outstanding)), null],
            Cells.Set("Payable", "Paid", "Outstanding", "Overdue days"));
    }

    private async Task<Result<ReportSection>> PurchasesAsync(ReportScope s, CancellationToken ct)
    {
        var receipts = s.Apply(db.GoodsReceipts.AsNoTracking()).Where(r => r.ReceivedAt >= s.FromAt && r.ReceivedAt < s.ToAt);
        var query = from l in db.GoodsReceiptLines.AsNoTracking()
                    join r in receipts on l.ReceiptId equals r.Id
                    join p in db.PurchaseOrders.AsNoTracking() on r.PurchaseOrderId equals p.Id
                    select new { r.Number, r.ReceivedAt, r.BranchId, r.PurchaseOrderNumber, p.SupplierId, l.Sku, l.AcceptedQuantity, l.RejectedQuantity, l.UnitCost };
        if (await query.CountAsync(ct) > MaxRows)
        {
            return TooMany();
        }

        var list = await query.OrderBy(x => x.ReceivedAt).ThenBy(x => x.Number).ThenBy(x => x.Sku).ToListAsync(ct);
        var codes = await BranchCodesAsync(ct);
        var suppliers = await db.Suppliers.IgnoreQueryFilters().AsNoTracking().ToDictionaryAsync(x => x.Id, x => x.Name, ct);
        string[] h = ["Receipt", "Date", "Branch", "PO number", "Supplier", "SKU", "Accepted", "Rejected", "Unit cost", "Value"];
        var rows = list.Select(x => (IReadOnlyList<string?>)[x.Number, Cells.D(Ops.BstDate(x.ReceivedAt)), codes.GetValueOrDefault(x.BranchId, string.Empty), x.PurchaseOrderNumber, suppliers.GetValueOrDefault(x.SupplierId, string.Empty), x.Sku,
            Cells.N(x.AcceptedQuantity), Cells.N(x.RejectedQuantity), Cells.M(x.UnitCost), Cells.M(x.AcceptedQuantity * x.UnitCost)]).ToList();
        return new ReportSection("purchases", "Goods received", h, rows, ["Total", null, null, null, null, null, Cells.N(list.Sum(x => x.AcceptedQuantity)), Cells.N(list.Sum(x => x.RejectedQuantity)), null, Cells.M(list.Sum(x => x.AcceptedQuantity * x.UnitCost))],
            Cells.Set("Accepted", "Rejected", "Unit cost", "Value"));
    }
}
