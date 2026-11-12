using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Accounting;
using Juvo.Application.Purchasing;
using Juvo.Domain.Purchasing;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Mvc;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Suppliers, purchase orders, goods receipts, supplier invoices and payments, and low-stock suggestions (module W). Every endpoint names its permission; the services prove it again in the
/// branch of the order, so another branch's order looks like "not found".
/// </summary>
internal static class PurchasingEndpoints
{
    public static WebApplication MapPurchasingEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapSuppliers(v1.MapGroup("/suppliers").WithTags("Suppliers"));
        MapOrders(v1.MapGroup("/purchasing/orders").WithTags("Purchasing"));
        MapReceipts(v1.MapGroup("/purchasing").WithTags("Purchasing"));
        return app;
    }

    private static void MapSuppliers(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, bool? active, SuppliersService s, CancellationToken ct) => (await s.ListAsync(list.ToRequest(), active, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.SupplierView).Produces<PagedResult<SupplierDto>>().WithSummary("Suppliers by name. Filter active=true|false; search name, contact, phone or email.");

        g.MapGet("/{id:guid}", async (Guid id, SuppliersService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.SupplierView).Produces<SupplierDto>().WithSummary("One supplier with the number of open purchase orders.");

        g.MapPost("", async (SaveSupplierRequest r, SuppliersService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/suppliers/{v.Id}", v)))
            .RequirePermission(Permissions.Purchasing.SupplierCreate).Produces<SupplierDto>(StatusCodes.Status201Created).WithSummary("Add a supplier: contact, payment terms (days), tax id, notes.");

        g.MapPut("/{id:guid}", async (Guid id, SaveSupplierRequest r, SuppliersService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.SupplierEdit).Produces<SupplierDto>().WithSummary("Change a supplier, or archive it with isActive=false (suppliers are never erased).");
    }

    private static void MapOrders(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, PurchaseStatus? status, SupplierPaymentStatus? paymentStatus, Guid? supplierId, Guid? branchId, bool? overdue, bool? payable, DateTimeOffset? from, DateTimeOffset? to,
                PurchasingService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new PurchaseListQuery(status, paymentStatus, supplierId, branchId, overdue, payable, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.PoView).Produces<PagedResult<PurchaseListItemDto>>()
            .WithSummary("Purchase orders of the branches where you may view them, newest first. Filters: status, paymentStatus, supplier, branch, overdue (supplier invoice past due and unpaid), payable (not fully paid), date range; search number, supplier or invoice number; sort createdAt, number, status, expected, due.");

        g.MapGet("/{id:guid}", async (Guid id, PurchasingService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.PoView).Produces<PurchaseDetailDto>()
            .WithSummary("One purchase order: lines with ordered, received and rejected units and unit cost, the status history, receipts, supplier payments, attachments and the statuses it may move to.");

        g.MapGet("/{id:guid}/print", async (Guid id, PurchasingService s, CancellationToken ct) => (await s.PrintAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.PoView).Produces<PurchasePrintDto>().WithSummary("The purchase order as print data (buyer, supplier, numbered lines, total) for a print view or PDF.");

        g.MapPost("", async (SavePurchaseOrderRequest r, PurchasingService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/purchasing/orders/{v.Id}", v)))
            .RequirePermission(Permissions.Purchasing.PoCreate).Produces<PurchaseDetailDto>(StatusCodes.Status201Created)
            .WithSummary("Create a draft purchase order in a branch (the active branch unless branchId is given): supplier, expected date, notes, lines by variant with quantity and unit cost. The number is PO-<BRANCH>-<YYMM>-<6 digits>.");

        g.MapPut("/{id:guid}", async (Guid id, SavePurchaseOrderRequest r, PurchasingService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.PoEdit).Produces<PurchaseDetailDto>().WithSummary("Change a draft purchase order (supplier, date, notes, lines).");

        g.MapPost("/uploads", async (IFormFile file, PurchasingService s, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return (await s.UploadAsync(stream, file.FileName, ct)).ToHttpResult(v => Results.Created(v.Url, v));
            })
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(10 * 1024 * 1024)).RequirePermission(Permissions.Purchasing.PoEdit).Produces<AttachmentUploadDto>(StatusCodes.Status201Created)
            .WithSummary("Upload a supplier document (JPEG, PNG, WebP or PDF, up to 8 MB). Send the returned path in attachmentPaths when saving the order or in POST /{id}/attachments.");

        g.MapPost("/{id:guid}/attachments", async (Guid id, AddPurchaseAttachmentsRequest r, PurchasingService s, CancellationToken ct) => (await s.AddAttachmentsAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.PoEdit).Produces<PurchaseDetailDto>().WithSummary("Attach uploaded files to the order at any stage.");

        g.MapPost("/{id:guid}/send", async (Guid id, PurchasingService s, CancellationToken ct) => (await s.SendAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.PoSend).Produces<PurchaseDetailDto>().WithSummary("Mark a draft as sent to the supplier (Draft to Sent).");

        g.MapPost("/{id:guid}/receive", async (Guid id, ReceiveGoodsRequest r, PurchasingService s, CancellationToken ct) => (await s.ReceiveAsync(id, r, ct)).ToHttpResult(v => Results.Created($"/api/v1/purchasing/receipts/{v.Receipt.Id}", v)))
            .RequirePermission(Permissions.Purchasing.PoReceive).Produces<ReceiveResultDto>(StatusCodes.Status201Created)
            .WithSummary("Book a delivery, full or partial: per line the accepted units (added to the branch stock) and the rejected units (damaged or refused, not added). More than still expected is refused. The product cost price is updated by the configured method only when you may change costs.");

        g.MapPost("/{id:guid}/close-short", async (Guid id, CancelPurchaseRequest r, PurchasingService s, CancellationToken ct) => (await s.CloseShortAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.PoReceive).Produces<PurchaseDetailDto>().WithSummary("Stop waiting for the rest of a partly received order (PartlyReceived to Received), with a reason.");

        g.MapPost("/{id:guid}/cancel", async (Guid id, CancelPurchaseRequest r, PurchasingService s, CancellationToken ct) => (await s.CancelAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.PoCancel).Produces<PurchaseDetailDto>().WithSummary("Cancel a draft or sent order with a reason (not once goods arrived or money was paid).");

        g.MapPost("/{id:guid}/invoice", async (Guid id, RecordSupplierInvoiceRequest r, PurchasingService s, CancellationToken ct) => (await s.RecordInvoiceAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.PoEdit).Produces<PurchaseDetailDto>()
            .WithSummary("Record the supplier's invoice: number, date, due date (default invoice date plus the supplier's payment terms) and optionally the invoiced amount, which then replaces the order value as what is owed.");

        g.MapPost("/{id:guid}/payments", async (Guid id, RecordSupplierPaymentRequest r, PurchasingService s, CancellationToken ct) => (await s.RecordPaymentAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.PayablePay).Produces<PurchaseDetailDto>()
            .WithSummary("Record a payment to the supplier (amount, method, reference, date, optional cash or bank account that is then booked in the cash book). The payment status becomes PartlyPaid or Paid. Payments are append-only.");

        g.MapGet("/suggestions", async (Guid? branchId, PurchasingService s, CancellationToken ct) => (await s.SuggestionsAsync(branchId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.PoCreate).Produces<SuggestionsDto>()
            .WithSummary("Variants at or below their reorder level in a branch, with a suggested quantity (twice the reorder level minus available and already on order), grouped by the supplier the variant was last ordered from.");
    }

    private static void MapReceipts(RouteGroupBuilder g)
    {
        g.MapGet("/receipts", async ([AsParameters] ListParams list, Guid? purchaseOrderId, Guid? branchId, DateTimeOffset? from, DateTimeOffset? to, PurchasingService s, CancellationToken ct) =>
                (await s.ReceiptsAsync(list.ToRequest(), new ReceiptListQuery(purchaseOrderId, branchId, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.ReceiptView).Produces<PagedResult<GoodsReceiptDto>>().WithSummary("Goods receipts, newest first. Filter by purchase order, branch and date; search receipt or order number.");

        g.MapGet("/receipts/{id:guid}", async (Guid id, PurchasingService s, CancellationToken ct) => (await s.GetReceiptAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Purchasing.ReceiptView).Produces<GoodsReceiptDto>().WithSummary("One goods receipt with its lines.");

        g.MapPost("/receipts/{id:guid}/apply-cost", async (Guid id, PurchasingService s, CancellationToken ct) => (await s.ApplyCostAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Catalog.ProductViewCost).Produces<GoodsReceiptDto>()
            .WithSummary("Apply the cost price of a receipt that was booked by someone who may not change costs. Needs the cost permission.");
    }
}
