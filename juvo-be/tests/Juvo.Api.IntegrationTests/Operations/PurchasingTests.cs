using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Inventory;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Operations;

public class PurchasingTests(OpsFactory factory) : IClassFixture<OpsFactory>
{
    private readonly OpsHelper _o = new(new OrderHelper(new StoreHelper(factory)));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly string[] Buyer =
    [
        "purchasing.supplier.view", "purchasing.supplier.create", "purchasing.supplier.edit", "purchasing.po.view", "purchasing.po.create", "purchasing.po.edit", "purchasing.po.send",
        "purchasing.po.receive", "purchasing.po.cancel", "purchasing.receipt.view", "purchasing.receipt.create",
    ];

    // ---- helpers ------------------------------------------------------------------------------------------------

    private async Task<Guid> SupplierAsync(string admin, string? name = null, int terms = 14) =>
        (await _o.PostAsync("/api/v1/suppliers", admin, new { name = name ?? StoreHelper.Unique("Supplier"), contactName = "Mr Karim", phone = "01711000000", paymentTermsDays = terms }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private async Task<SeededProduct> ProductAsync(string admin, int variants = 1, int stock = 0)
    {
        var category = await _o.H.CategoryAsync(admin);
        var list = Enumerable.Range(0, variants).Select(i => ("Red", $"S{i}", stock)).ToList();
        return await _o.H.ProductAsync(admin, category.Id, StoreHelper.Unique("PO Tee"), 500m, list);
    }

    private async Task<JsonElement> PoAsync(string bearer, Guid supplier, IEnumerable<(Guid Variant, int Quantity, decimal Cost)> lines, Guid? branch = null, string? notes = null) =>
        await _o.PostAsync("/api/v1/purchasing/orders", bearer,
            new { branchId = branch ?? await _o.HoAsync(), supplierId = supplier, expectedDate = OpsHelper.Today, notes, lines = lines.Select(l => new { variantId = l.Variant, quantity = l.Quantity, unitCost = l.Cost }).ToArray() }, HttpStatusCode.Created);

    private async Task<JsonElement> SentPoAsync(string admin, Guid supplier, Guid variant, int quantity, decimal cost)
    {
        var po = await PoAsync(admin, supplier, [(variant, quantity, cost)]);
        return await _o.PostAsync($"/api/v1/purchasing/orders/{po.GetProperty("id").GetGuid()}/send", admin);
    }

    private Task<JsonElement> ReceiveAsync(string bearer, JsonElement po, int accepted, int rejected = 0, int line = 0, HttpStatusCode expected = HttpStatusCode.Created) =>
        _o.PostAsync($"/api/v1/purchasing/orders/{po.GetProperty("id").GetGuid()}/receive", bearer,
            new { note = "Delivery", lines = new[] { new { lineId = po.GetProperty("lines")[line].GetProperty("id").GetGuid(), acceptedQuantity = accepted, rejectedQuantity = rejected } } }, expected);

    private async Task<decimal?> CostAsync(Guid productId) =>
        await _o.WithDbAsync(async d => await d.Products.IgnoreQueryFilters().Where(p => p.Id == productId).Select(p => p.CostPrice).SingleAsync(Ct));

    private Task<int> OnHandAsync(Guid variantId) => _o.O.StockAsync(variantId).ContinueWith(t => t.Result.OnHand, TaskScheduler.Default);

    // ---- the whole journey --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_purchase_order_is_created_sent_and_received_in_two_deliveries_and_stock_follows_the_ledger()
    {
        var admin = await _o.H.AdminAsync();
        var supplier = await SupplierAsync(admin);
        var product = await ProductAsync(admin);
        var variant = product.Variants[0].Id;

        var draft = await PoAsync(admin, supplier, [(variant, 10, 100m)], notes: "Eid stock");
        Assert.Equal("Draft", draft.GetProperty("status").GetString());
        Assert.Matches(@"^PO-HO-\d{4}-\d{6}$", draft.GetProperty("number").GetString());
        Assert.Equal(1000m, draft.GetProperty("total").GetDecimal());
        var id = draft.GetProperty("id").GetGuid();

        var sent = await _o.PostAsync($"/api/v1/purchasing/orders/{id}/send", admin);
        Assert.Equal("Sent", sent.GetProperty("status").GetString());
        Assert.Equal(0, await OnHandAsync(variant)); // ordering changes no stock

        // First delivery: 4 good, 1 damaged. Only the 4 go on the shelf and count toward the order.
        var first = await ReceiveAsync(admin, sent, accepted: 4, rejected: 1);
        var order1 = first.GetProperty("order");
        Assert.Equal("PartlyReceived", order1.GetProperty("status").GetString());
        var line1 = order1.GetProperty("lines")[0];
        Assert.Equal((10, 4, 1, 6), (line1.GetProperty("orderedQuantity").GetInt32(), line1.GetProperty("receivedQuantity").GetInt32(), line1.GetProperty("rejectedQuantity").GetInt32(), line1.GetProperty("outstandingQuantity").GetInt32()));
        Assert.Matches(@"^GRN-HO-\d{4}-\d{6}$", first.GetProperty("receipt").GetProperty("number").GetString());
        Assert.Equal(4, await OnHandAsync(variant));
        Assert.Equal(400m, order1.GetProperty("receivedValue").GetDecimal());

        // Too much is refused and nothing changes.
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/receive", admin,
            new { lines = new[] { new { lineId = line1.GetProperty("id").GetGuid(), acceptedQuantity = 7, rejectedQuantity = 0 } } }), "PURCHASE.OVER_RECEIPT");
        Assert.Equal(4, await OnHandAsync(variant));
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/receive", admin,
            new { lines = new[] { new { lineId = line1.GetProperty("id").GetGuid(), acceptedQuantity = 0, rejectedQuantity = 0 } } }), "PURCHASE.NOTHING_RECEIVED");

        // The rest arrives: the order is Received.
        var second = await ReceiveAsync(admin, order1, accepted: 6);
        Assert.Equal("Received", second.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(10, await OnHandAsync(variant));
        Assert.Equal(1000m, second.GetProperty("order").GetProperty("receivedValue").GetDecimal());
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/receive", admin,
            new { lines = new[] { new { lineId = line1.GetProperty("id").GetGuid(), acceptedQuantity = 1, rejectedQuantity = 0 } } }), "PURCHASE.INVALID_TRANSITION");

        // The append-only history tells the story, and so does the stock ledger.
        var detail = await _o.GetAsync($"/api/v1/purchasing/orders/{id}", admin);
        Assert.Equal(["Draft", "Sent", "PartlyReceived", "Received"], detail.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("to").GetString()).ToArray());
        Assert.Equal(2, detail.GetProperty("receipts").GetArrayLength());
        var movements = await _o.WithDbAsync(async d => await d.StockMovements.IgnoreQueryFilters(["BranchScope"]).Where(m => m.VariantId == variant && m.Reason == StockReason.PurchaseReceipt).OrderBy(m => m.CreatedAt).ToListAsync(Ct));
        Assert.Equal([4, 6], movements.Select(m => m.QuantityChange).ToArray());
        Assert.Equal([4, 10], movements.Select(m => m.OnHandAfter).ToArray());
        Assert.All(movements, m => Assert.Equal("PurchaseReceipt", m.ReferenceType));
        Assert.Equal(2, movements.Select(m => m.ReferenceId).Distinct().Count());
    }

    [Fact]
    public async Task Order_and_receipt_numbers_are_gap_free_and_in_sequence()
    {
        var admin = await _o.H.AdminAsync();
        var supplier = await SupplierAsync(admin);
        var product = await ProductAsync(admin);
        int Seq(JsonElement po) => int.Parse(po.GetProperty("number").GetString()![^6..], System.Globalization.CultureInfo.InvariantCulture);
        var a = await PoAsync(admin, supplier, [(product.Variants[0].Id, 1, 10m)]);
        var b = await PoAsync(admin, supplier, [(product.Variants[0].Id, 1, 10m)]);
        var c = await PoAsync(admin, supplier, [(product.Variants[0].Id, 1, 10m)]);
        Assert.Equal([Seq(a) + 1, Seq(a) + 2], [Seq(b), Seq(c)]);

        // A rejected request does not burn a number.
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.TryPostAsync("/api/v1/purchasing/orders", admin, new { branchId = await _o.HoAsync(), supplierId = supplier, lines = Array.Empty<object>() })).StatusCode);
        var d = await PoAsync(admin, supplier, [(product.Variants[0].Id, 1, 10m)]);
        Assert.Equal(Seq(c) + 1, Seq(d));
    }

    [Fact]
    public async Task A_draft_can_be_edited_but_a_sent_order_cannot()
    {
        var admin = await _o.H.AdminAsync();
        var supplier = await SupplierAsync(admin);
        var other = await SupplierAsync(admin);
        var product = await ProductAsync(admin, variants: 2);
        var po = await PoAsync(admin, supplier, [(product.Variants[0].Id, 5, 20m)]);
        var id = po.GetProperty("id").GetGuid();

        var edited = await _o.PutAsync($"/api/v1/purchasing/orders/{id}", admin, new
        {
            supplierId = other, expectedDate = OpsHelper.Today, notes = "changed",
            lines = new[] { new { variantId = product.Variants[0].Id, quantity = 3, unitCost = 25m }, new { variantId = product.Variants[1].Id, quantity = 2, unitCost = 30m } },
        });
        Assert.Equal(other, edited.GetProperty("supplierId").GetGuid());
        Assert.Equal(2, edited.GetProperty("lines").GetArrayLength());
        Assert.Equal(135m, edited.GetProperty("total").GetDecimal());

        await _o.PostAsync($"/api/v1/purchasing/orders/{id}/send", admin);
        await OpsHelper.ExpectCodeAsync(await _o.Api.PutAsync($"/api/v1/purchasing/orders/{id}", new { supplierId = other, lines = new[] { new { variantId = product.Variants[0].Id, quantity = 1, unitCost = 1m } } }, admin), "PURCHASE.INVALID_TRANSITION");
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/send", admin), "PURCHASE.INVALID_TRANSITION");
    }

    [Fact]
    public async Task Cancelling_is_possible_from_draft_and_sent_only_and_needs_a_reason()
    {
        var admin = await _o.H.AdminAsync();
        var supplier = await SupplierAsync(admin);
        var variant = (await ProductAsync(admin)).Variants[0].Id;

        var draft = (await PoAsync(admin, supplier, [(variant, 1, 5m)])).GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.TryPostAsync($"/api/v1/purchasing/orders/{draft}/cancel", admin, new { reason = " " })).StatusCode);
        var cancelled = await _o.PostAsync($"/api/v1/purchasing/orders/{draft}/cancel", admin, new { reason = "Wrong supplier" });
        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Equal("Wrong supplier", cancelled.GetProperty("cancelReason").GetString());
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{draft}/send", admin), "PURCHASE.INVALID_TRANSITION");
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{draft}/cancel", admin, new { reason = "again" }), "PURCHASE.INVALID_TRANSITION");

        var sent = await SentPoAsync(admin, supplier, variant, 4, 5m);
        Assert.Equal("Cancelled", (await _o.PostAsync($"/api/v1/purchasing/orders/{sent.GetProperty("id").GetGuid()}/cancel", admin, new { reason = "Supplier closed" })).GetProperty("status").GetString());

        var partly = await ReceiveAsync(admin, await SentPoAsync(admin, supplier, variant, 4, 5m), accepted: 1);
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{partly.GetProperty("order").GetProperty("id").GetGuid()}/cancel", admin, new { reason = "late" }), "PURCHASE.INVALID_TRANSITION");

        // Goods cannot be received on a cancelled or draft order.
        var draft2 = await PoAsync(admin, supplier, [(variant, 1, 5m)]);
        await OpsHelper.ExpectCodeAsync(await _o.Api.PostAsync($"/api/v1/purchasing/orders/{draft2.GetProperty("id").GetGuid()}/receive",
            new { lines = new[] { new { lineId = draft2.GetProperty("lines")[0].GetProperty("id").GetGuid(), acceptedQuantity = 1, rejectedQuantity = 0 } } }, admin), "PURCHASE.INVALID_TRANSITION");
    }

    [Fact]
    public async Task A_partly_received_order_can_be_closed_short_and_then_counts_what_arrived()
    {
        var admin = await _o.H.AdminAsync();
        var supplier = await SupplierAsync(admin);
        var variant = (await ProductAsync(admin)).Variants[0].Id;
        var received = await ReceiveAsync(admin, await SentPoAsync(admin, supplier, variant, 10, 50m), accepted: 6);
        var id = received.GetProperty("order").GetProperty("id").GetGuid();
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/close-short", admin, new { reason = "" }), "GENERAL.VALIDATION");
        var closed = await _o.PostAsync($"/api/v1/purchasing/orders/{id}/close-short", admin, new { reason = "Supplier ran out" });
        Assert.Equal("Received", closed.GetProperty("status").GetString());
        Assert.Equal(300m, closed.GetProperty("amountPayable").GetDecimal()); // 6 x 50: what arrived, not what was ordered
        Assert.Contains("Closed short", closed.GetProperty("history").EnumerateArray().Last().GetProperty("reason").GetString());
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/close-short", admin, new { reason = "again" }), "PURCHASE.INVALID_TRANSITION");
    }

    // ---- cost methods -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_product_cost_follows_the_last_cost_method_by_default_and_the_weighted_average_when_configured()
    {
        var admin = await _o.H.AdminAsync();
        var supplier = await SupplierAsync(admin);
        var product = await ProductAsync(admin);
        var variant = product.Variants[0].Id;
        Assert.Null(await CostAsync(product.Id));

        // Default LastCost: the first receipt sets it, the next one replaces it.
        await ReceiveAsync(admin, await SentPoAsync(admin, supplier, variant, 10, 100m), accepted: 10);
        Assert.Equal(100m, await CostAsync(product.Id));

        await _o.SetSettingAsync("Purchasing.CostMethod", "WeightedAverage");
        try
        {
            await ReceiveAsync(admin, await SentPoAsync(admin, supplier, variant, 10, 200m), accepted: 10); // (10x100 + 10x200) / 20
            Assert.Equal(150m, await CostAsync(product.Id));
            await ReceiveAsync(admin, await SentPoAsync(admin, supplier, variant, 5, 300m), accepted: 5); // (20x150 + 5x300) / 25
            Assert.Equal(180m, await CostAsync(product.Id));
            await ReceiveAsync(admin, await SentPoAsync(admin, supplier, variant, 3, 10m), accepted: 2, rejected: 1); // (25x180 + 2x10) / 27, the rejected unit counts for nothing
            Assert.Equal(167.41m, await CostAsync(product.Id));
        }
        finally
        {
            await _o.ClearSettingAsync("Purchasing.CostMethod");
        }

        await ReceiveAsync(admin, await SentPoAsync(admin, supplier, variant, 1, 90m), accepted: 1);
        Assert.Equal(90m, await CostAsync(product.Id)); // LastCost again
        Assert.Equal(28, await OnHandAsync(variant));
    }

    [Fact]
    public async Task Nothing_is_costed_for_rejected_units_or_for_staff_who_may_not_change_costs_until_a_person_who_may_applies_it()
    {
        var admin = await _o.H.AdminAsync();
        var ho = await _o.HoAsync();
        var supplier = await SupplierAsync(admin);
        var product = await ProductAsync(admin);
        var variant = product.Variants[0].Id;
        var clerk = await _o.StaffAsync(admin, ho, Buyer); // no catalog.product.view-cost

        var po = await PoAsync(clerk, supplier, [(variant, 8, 120m)]);
        var sent = await _o.PostAsync($"/api/v1/purchasing/orders/{po.GetProperty("id").GetGuid()}/send", clerk);
        var received = await ReceiveAsync(clerk, sent, accepted: 8);
        Assert.Equal(8, await OnHandAsync(variant)); // the stock is booked
        Assert.Null(await CostAsync(product.Id)); // the cost is not
        var receipt = received.GetProperty("receipt");
        Assert.False(receipt.GetProperty("lines")[0].GetProperty("costApplied").GetBoolean());

        var receiptId = receipt.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync($"/api/v1/purchasing/receipts/{receiptId}/apply-cost", clerk)).StatusCode);
        var applied = await _o.PostAsync($"/api/v1/purchasing/receipts/{receiptId}/apply-cost", admin);
        Assert.True(applied.GetProperty("lines")[0].GetProperty("costApplied").GetBoolean());
        Assert.Equal(120m, await CostAsync(product.Id));
        Assert.Equal(120m, await CostAsync(product.Id)); // idempotent
        await _o.PostAsync($"/api/v1/purchasing/receipts/{receiptId}/apply-cost", admin);
        Assert.Equal(120m, await CostAsync(product.Id));

        // Only rejected units arrive: no stock and no cost change.
        var rejectedOnly = await ReceiveAsync(admin, await SentPoAsync(admin, supplier, variant, 2, 999m), accepted: 0, rejected: 2);
        Assert.Equal("Sent", rejectedOnly.GetProperty("order").GetProperty("status").GetString());
        Assert.Equal(8, await OnHandAsync(variant));
        Assert.Equal(120m, await CostAsync(product.Id));
    }

    [Fact]
    public void The_cost_maths_are_exact()
    {
        Assert.Equal(100m, Juvo.Application.Purchasing.PurchaseCost.Next(Juvo.Application.Operations.CostMethod.LastCost, 50m, 10, 5, 100m));
        Assert.Equal(150m, Juvo.Application.Purchasing.PurchaseCost.Next(Juvo.Application.Operations.CostMethod.WeightedAverage, 100m, 10, 10, 200m));
        Assert.Equal(100m, Juvo.Application.Purchasing.PurchaseCost.Next(Juvo.Application.Operations.CostMethod.WeightedAverage, null, 10, 10, 100m)); // no old cost
        Assert.Equal(75m, Juvo.Application.Purchasing.PurchaseCost.Next(Juvo.Application.Operations.CostMethod.WeightedAverage, 40m, 0, 10, 75m)); // nothing on hand
        Assert.Equal(33.33m, Juvo.Application.Purchasing.PurchaseCost.Next(Juvo.Application.Operations.CostMethod.WeightedAverage, 10m, 1, 2, 45m)); // (10 + 90) / 3
    }

    // ---- supplier invoices and payments -------------------------------------------------------------------------

    [Fact]
    public async Task Supplier_payments_move_the_status_from_unpaid_to_partly_paid_to_paid_and_never_overpay()
    {
        var admin = await _o.H.AdminAsync();
        var ho = await _o.HoAsync();
        var supplier = await SupplierAsync(admin, terms: 30);
        var variant = (await ProductAsync(admin)).Variants[0].Id;
        var account = await _o.AccountAsync(admin, ho, 2000m, "Bank");
        var draft = await PoAsync(admin, supplier, [(variant, 10, 100m)]);
        var id = draft.GetProperty("id").GetGuid();

        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/payments", admin, new { amount = 100m, method = "Bank" }), "PURCHASE.PAYMENT_NOT_ALLOWED");
        await _o.PostAsync($"/api/v1/purchasing/orders/{id}/send", admin);
        Assert.Equal("Unpaid", (await _o.GetAsync($"/api/v1/purchasing/orders/{id}", admin)).GetProperty("paymentStatus").GetString());

        var part = await _o.PostAsync($"/api/v1/purchasing/orders/{id}/payments", admin, new { amount = 400m, method = "Bank", reference = "TRX-1", accountId = account, note = "Advance" });
        Assert.Equal("PartlyPaid", part.GetProperty("paymentStatus").GetString());
        Assert.Equal((400m, 600m, 1000m), (part.GetProperty("amountPaid").GetDecimal(), part.GetProperty("outstanding").GetDecimal(), part.GetProperty("amountPayable").GetDecimal()));
        Assert.Equal(1600m, await _o.BalanceAsync(admin, account)); // booked in the cash book

        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/payments", admin, new { amount = 700m, method = "Cash" }), "PURCHASE.PAYMENT_EXCEEDS_DUE");
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/payments", admin, new { amount = 0m, method = "Cash" })).StatusCode);
        var paid = await _o.PostAsync($"/api/v1/purchasing/orders/{id}/payments", admin, new { amount = 600m, method = "Cash" });
        Assert.Equal("Paid", paid.GetProperty("paymentStatus").GetString());
        Assert.Equal(2, paid.GetProperty("payments").GetArrayLength());
        Assert.Equal(0m, paid.GetProperty("outstanding").GetDecimal());
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/payments", admin, new { amount = 1m, method = "Cash" }), "PURCHASE.PAYMENT_EXCEEDS_DUE");

        // Payments are append-only: the table refuses edits and deletes.
        await Assert.ThrowsAnyAsync<Exception>(() => _o.WithDbAsync(async d =>
        {
            var row = await d.PurchasePayments.IgnoreQueryFilters(["BranchScope"]).FirstAsync(p => p.PurchaseOrderId == id, Ct);
            d.PurchasePayments.Remove(row);
            return await d.SaveChangesAsync(Ct);
        }));

        // A paid order that was paid in full cannot be cancelled.
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/cancel", admin, new { reason = "oops" }), "PURCHASE.INVALID_TRANSITION");
    }

    [Fact]
    public async Task The_supplier_invoice_sets_the_due_date_and_can_replace_the_order_value_as_what_is_owed()
    {
        var admin = await _o.H.AdminAsync();
        var supplier = await SupplierAsync(admin, terms: 10);
        var variant = (await ProductAsync(admin)).Variants[0].Id;
        var sent = await SentPoAsync(admin, supplier, variant, 10, 100m);
        var id = sent.GetProperty("id").GetGuid();

        var invoiced = await _o.PostAsync($"/api/v1/purchasing/orders/{id}/invoice", admin, new { number = "INV-5521", invoiceDate = OpsHelper.DaysAgo(20), amount = 900m });
        Assert.Equal("INV-5521", invoiced.GetProperty("supplierInvoiceNumber").GetString());
        Assert.Equal(OpsHelper.DaysAgo(10), invoiced.GetProperty("dueDate").GetString()); // invoice date plus 10 days of terms
        Assert.True(invoiced.GetProperty("isOverdue").GetBoolean());
        Assert.Equal(900m, invoiced.GetProperty("amountPayable").GetDecimal());

        var overdue = await _o.GetAsync("/api/v1/purchasing/orders?overdue=true&search=INV-5521", admin);
        Assert.Equal(1, overdue.GetProperty("total").GetInt32());
        var payable = await _o.GetAsync($"/api/v1/purchasing/orders?payable=true&supplierId={supplier}", admin);
        Assert.Equal(1, payable.GetProperty("total").GetInt32());

        await _o.PostAsync($"/api/v1/purchasing/orders/{id}/payments", admin, new { amount = 900m, method = "Bank" });
        Assert.Equal(0, (await _o.GetAsync("/api/v1/purchasing/orders?overdue=true&search=INV-5521", admin)).GetProperty("total").GetInt32()); // paid: no longer overdue
        Assert.Equal(0, (await _o.GetAsync($"/api/v1/purchasing/orders?payable=true&supplierId={supplier}", admin)).GetProperty("total").GetInt32());

        var draft = (await PoAsync(admin, supplier, [(variant, 1, 5m)])).GetProperty("id").GetGuid();
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{draft}/invoice", admin, new { number = "X", invoiceDate = OpsHelper.Today }), "PURCHASE.INVOICE_NOT_ALLOWED");
    }

    [Fact]
    public async Task A_payment_cannot_use_an_account_of_another_branch()
    {
        var admin = await _o.H.AdminAsync();
        var supplier = await SupplierAsync(admin);
        var variant = (await ProductAsync(admin)).Variants[0].Id;
        var other = await _o.Api.CreateBranchAsync(admin, "P" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), "Other");
        var foreign = await _o.AccountAsync(admin, other);
        var sent = await SentPoAsync(admin, supplier, variant, 2, 10m);
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{sent.GetProperty("id").GetGuid()}/payments", admin, new { amount = 5m, method = "Cash", accountId = foreign }), "EXPENSE.ACCOUNT_BRANCH_MISMATCH");
    }

    // ---- suggestions, print, attachments ------------------------------------------------------------------------

    [Fact]
    public async Task Low_stock_suggestions_are_grouped_by_the_last_supplier_and_shrink_when_stock_is_on_order()
    {
        var admin = await _o.H.AdminAsync();
        var ho = await _o.HoAsync();
        var supplierA = await SupplierAsync(admin, "A-" + StoreHelper.Unique("Sup"));
        var product = await ProductAsync(admin, variants: 3, stock: 2);
        var (known, fresh, fine) = (product.Variants[0].Id, product.Variants[1].Id, product.Variants[2].Id);
        async Task Reorder(Guid v, int level) => await _o.PutAsync("/api/v1/inventory/reorder-levels", admin, new { branchId = ho, variantId = v, reorderLevel = level });
        await Reorder(known, 10);
        await Reorder(fresh, 5);
        await Reorder(fine, 1); // 2 available, level 1: not low

        // `known` was ordered from supplier A before (a cancelled order does not count as a preference).
        var earlier = await PoAsync(admin, supplierA, [(known, 1, 40m)]);
        await _o.PostAsync($"/api/v1/purchasing/orders/{earlier.GetProperty("id").GetGuid()}/cancel", admin, new { reason = "test" });
        Assert.Null((await SuggestedFor(known)).SupplierId);
        var kept = await SentPoAsync(admin, supplierA, known, 1, 42m);
        await _o.PostAsync($"/api/v1/purchasing/orders/{kept.GetProperty("id").GetGuid()}/cancel", admin, new { reason = "test2" }); // cancelled orders never count
        Assert.Null((await SuggestedFor(known)).SupplierId);

        var open = await SentPoAsync(admin, supplierA, known, 3, 45m); // 3 are on order and count as stock to come
        var result = await _o.GetAsync($"/api/v1/purchasing/orders/suggestions?branchId={ho}", admin);
        var lineKnown = FindLine(result, known);
        Assert.Equal(supplierA, lineKnown.Group.GetProperty("supplierId").GetGuid());
        Assert.Equal((2, 3, 15, 45m), (lineKnown.Line.GetProperty("available").GetInt32(), lineKnown.Line.GetProperty("onOrder").GetInt32(), lineKnown.Line.GetProperty("suggestedQuantity").GetInt32(), lineKnown.Line.GetProperty("lastUnitCost").GetDecimal())); // 2*10 - 2 - 3
        var lineFresh = FindLine(result, fresh);
        Assert.Equal(JsonValueKind.Null, lineFresh.Group.GetProperty("supplierId").ValueKind); // never ordered: unassigned
        Assert.Equal(8, lineFresh.Line.GetProperty("suggestedQuantity").GetInt32()); // 2*5 - 2
        Assert.DoesNotContain(result.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("lines").EnumerateArray()), l => l.GetProperty("variantId").GetGuid() == fine);

        // Ordering the full suggestion removes the variant from the list.
        await ReceiveAsync(admin, open, accepted: 3);
        await SentPoAsync(admin, supplierA, known, 20, 45m);
        var after = await _o.GetAsync($"/api/v1/purchasing/orders/suggestions?branchId={ho}", admin);
        Assert.DoesNotContain(after.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("lines").EnumerateArray()), l => l.GetProperty("variantId").GetGuid() == known);

        async Task<(Guid? SupplierId, JsonElement Line)> SuggestedFor(Guid v)
        {
            var r = await _o.GetAsync($"/api/v1/purchasing/orders/suggestions?branchId={ho}", admin);
            var found = FindLine(r, v);
            return (found.Group.GetProperty("supplierId").ValueKind == JsonValueKind.Null ? null : found.Group.GetProperty("supplierId").GetGuid(), found.Line);
        }
    }

    private static (JsonElement Group, JsonElement Line) FindLine(JsonElement suggestions, Guid variant)
    {
        foreach (var group in suggestions.GetProperty("groups").EnumerateArray())
        {
            foreach (var line in group.GetProperty("lines").EnumerateArray())
            {
                if (line.GetProperty("variantId").GetGuid() == variant)
                {
                    return (group, line);
                }
            }
        }

        throw new Xunit.Sdk.XunitException($"Variant {variant} is not in the suggestions.");
    }

    [Fact]
    public async Task The_print_data_carries_buyer_supplier_numbered_lines_and_total()
    {
        var admin = await _o.H.AdminAsync();
        var supplier = await SupplierAsync(admin, "Print Supplier " + StoreHelper.Unique("x"));
        var product = await ProductAsync(admin, variants: 2);
        var po = await PoAsync(admin, supplier, [(product.Variants[0].Id, 2, 10m), (product.Variants[1].Id, 3, 20m)], notes: "Fragile");
        var print = await _o.GetAsync($"/api/v1/purchasing/orders/{po.GetProperty("id").GetGuid()}/print", admin);
        Assert.Equal(po.GetProperty("number").GetString(), print.GetProperty("number").GetString());
        Assert.StartsWith("Print Supplier", print.GetProperty("supplier").GetProperty("name").GetString(), StringComparison.Ordinal);
        Assert.Equal(80m, print.GetProperty("total").GetDecimal());
        Assert.Equal([1, 2], print.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("no").GetInt32()).ToArray());
        Assert.Equal("Fragile", print.GetProperty("notes").GetString());
        Assert.False(string.IsNullOrWhiteSpace(print.GetProperty("buyer").GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Supplier_documents_are_uploaded_checked_and_attached_to_an_order()
    {
        var admin = await _o.H.AdminAsync();
        var supplier = await SupplierAsync(admin);
        var variant = (await ProductAsync(admin)).Variants[0].Id;
        var po = await PoAsync(admin, supplier, [(variant, 1, 10m)]);
        var id = po.GetProperty("id").GetGuid();

        async Task<HttpResponseMessage> Upload(byte[] bytes, string name)
        {
            var form = new MultipartFormDataContent { { new ByteArrayContent(bytes), "file", name } };
            var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/purchasing/orders/uploads") { Content = form };
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin);
            return await _o.Api.Client.SendAsync(request, Ct);
        }

        Assert.Equal(HttpStatusCode.BadRequest, (await Upload("MZ-not-a-document"u8.ToArray(), "evil.pdf")).StatusCode);
        var uploaded = await TestApi.ExpectAsync(await Upload("%PDF-1.7 quotation"u8.ToArray(), "quote.pdf"), HttpStatusCode.Created);
        var withFile = await _o.PostAsync($"/api/v1/purchasing/orders/{id}/attachments", admin, new { attachmentPaths = new[] { uploaded.GetProperty("path").GetString() } });
        Assert.Equal("quote.pdf", Assert.Single(withFile.GetProperty("attachments").EnumerateArray()).GetProperty("fileName").GetString());
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/attachments", admin, new { attachmentPaths = new[] { "purchasing/9999/missing.pdf" } }), "PURCHASE.ATTACHMENT_INVALID");
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/attachments", admin, new { attachmentPaths = new[] { "../secrets.pdf" } }), "PURCHASE.ATTACHMENT_INVALID");
    }

    // ---- suppliers ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Suppliers_are_unique_by_name_archived_not_erased_and_archived_ones_cannot_be_ordered_from()
    {
        var admin = await _o.H.AdminAsync();
        var name = StoreHelper.Unique("Fabrics");
        var id = await SupplierAsync(admin, name);
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync("/api/v1/suppliers", admin, new { name }), "PURCHASE.SUPPLIER_NAME_TAKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.TryPostAsync("/api/v1/suppliers", admin, new { name = "" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _o.TryPostAsync("/api/v1/suppliers", admin, new { name = StoreHelper.Unique("Neg"), paymentTermsDays = -1 })).StatusCode);

        var variant = (await ProductAsync(admin)).Variants[0].Id;
        var po = await PoAsync(admin, id, [(variant, 1, 10m)]);
        var listed = await _o.GetAsync($"/api/v1/suppliers?search={name}", admin);
        Assert.Equal(1, Assert.Single(listed.GetProperty("items").EnumerateArray()).GetProperty("openOrders").GetInt32());

        var archived = await _o.PutAsync($"/api/v1/suppliers/{id}", admin, new { name, isActive = false });
        Assert.False(archived.GetProperty("isActive").GetBoolean());
        Assert.Equal(0, (await _o.GetAsync($"/api/v1/suppliers?active=true&search={name}", admin)).GetProperty("total").GetInt32());
        Assert.Equal(1, (await _o.GetAsync($"/api/v1/suppliers?active=false&search={name}", admin)).GetProperty("total").GetInt32());
        await OpsHelper.ExpectCodeAsync(await _o.TryPostAsync($"/api/v1/purchasing/orders/{po.GetProperty("id").GetGuid()}/send", admin), "PURCHASE.SUPPLIER_INACTIVE");
        await OpsHelper.ExpectCodeAsync(await _o.Api.PostAsync("/api/v1/purchasing/orders", new { branchId = await _o.HoAsync(), supplierId = id, lines = new[] { new { variantId = variant, quantity = 1, unitCost = 1m } } }, admin), "PURCHASE.SUPPLIER_INACTIVE");
        Assert.Equal(HttpStatusCode.OK, (await _o.TryGetAsync($"/api/v1/suppliers/{id}", admin)).StatusCode); // still readable
        Assert.Equal(HttpStatusCode.OK, (await _o.TryGetAsync($"/api/v1/purchasing/orders/{po.GetProperty("id").GetGuid()}", admin)).StatusCode); // the old order keeps its supplier
    }

    // ---- lists, branches, permissions ---------------------------------------------------------------------------

    [Fact]
    public async Task The_order_list_filters_searches_and_sorts()
    {
        var admin = await _o.H.AdminAsync();
        var supplier = await SupplierAsync(admin, StoreHelper.Unique("ListSup"));
        var variant = (await ProductAsync(admin)).Variants[0].Id;
        var a = await PoAsync(admin, supplier, [(variant, 1, 10m)]);
        var b = await SentPoAsync(admin, supplier, variant, 2, 10m);
        var all = await _o.GetAsync($"/api/v1/purchasing/orders?supplierId={supplier}&sort=number", admin);
        Assert.Equal([a.GetProperty("number").GetString(), b.GetProperty("number").GetString()], all.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("number").GetString()).ToArray());
        Assert.Equal(1, (await _o.GetAsync($"/api/v1/purchasing/orders?supplierId={supplier}&status=Sent", admin)).GetProperty("total").GetInt32());
        Assert.Equal(1, (await _o.GetAsync($"/api/v1/purchasing/orders?search={a.GetProperty("number").GetString()}", admin)).GetProperty("total").GetInt32());
        Assert.Equal(2, (await _o.GetAsync($"/api/v1/purchasing/orders?supplierId={supplier}&paymentStatus=Unpaid&branchId={await _o.HoAsync()}", admin)).GetProperty("total").GetInt32());
        var item = all.GetProperty("items")[1];
        Assert.Equal((1, 2, 0, 20m), (item.GetProperty("lineCount").GetInt32(), item.GetProperty("orderedQuantity").GetInt32(), item.GetProperty("receivedQuantity").GetInt32(), item.GetProperty("total").GetDecimal()));
    }

    [Fact]
    public async Task Branch_staff_cannot_see_or_act_on_the_orders_receipts_and_suggestions_of_another_branch()
    {
        var admin = await _o.H.AdminAsync();
        var ho = await _o.HoAsync();
        var other = await _o.Api.CreateBranchAsync(admin, "Q" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), "Branch Q");
        var staffOther = await _o.StaffAsync(admin, other, Buyer);
        var supplier = await SupplierAsync(admin);
        var variant = (await ProductAsync(admin)).Variants[0].Id;
        var sent = await SentPoAsync(admin, supplier, variant, 5, 10m);
        var id = sent.GetProperty("id").GetGuid();
        var received = await ReceiveAsync(admin, sent, accepted: 1);
        var receiptId = received.GetProperty("receipt").GetProperty("id").GetGuid();

        Assert.Equal(0, (await _o.GetAsync("/api/v1/purchasing/orders", staffOther)).GetProperty("total").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await _o.TryGetAsync($"/api/v1/purchasing/orders/{id}", staffOther)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _o.TryGetAsync($"/api/v1/purchasing/orders/{id}/print", staffOther)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/receive", staffOther, new { lines = new[] { new { lineId = Guid.NewGuid(), acceptedQuantity = 1, rejectedQuantity = 0 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/cancel", staffOther, new { reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _o.TryGetAsync($"/api/v1/purchasing/receipts/{receiptId}", staffOther)).StatusCode);
        Assert.Equal(0, (await _o.GetAsync("/api/v1/purchasing/receipts", staffOther)).GetProperty("total").GetInt32());
        var wrongBranch = await _o.Api.PostAsync("/api/v1/purchasing/orders", new { branchId = ho, supplierId = supplier, lines = new[] { new { variantId = variant, quantity = 1, unitCost = 1m } } }, staffOther);
        Assert.True(wrongBranch.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);
        Assert.True((await _o.TryGetAsync($"/api/v1/purchasing/orders/suggestions?branchId={ho}", staffOther)).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden);

        // Their own branch works, with its own numbering.
        var own = await PoAsync(staffOther, supplier, [(variant, 1, 10m)], other);
        Assert.StartsWith("PO-" + (await _o.WithDbAsync(async d => await d.Branches.Where(b => b.Id == other).Select(b => b.OrderPrefix).SingleAsync(Ct)))!, own.GetProperty("number").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_purchasing_action_needs_its_own_permission()
    {
        var admin = await _o.H.AdminAsync();
        var ho = await _o.HoAsync();
        var supplier = await SupplierAsync(admin);
        var variant = (await ProductAsync(admin)).Variants[0].Id;
        var sent = await SentPoAsync(admin, supplier, variant, 5, 10m);
        var id = sent.GetProperty("id").GetGuid();
        var lineId = sent.GetProperty("lines")[0].GetProperty("id").GetGuid();
        var viewer = await _o.StaffAsync(admin, ho, "purchasing.po.view", "purchasing.supplier.view", "purchasing.receipt.view");

        Assert.Equal(HttpStatusCode.OK, (await _o.TryGetAsync($"/api/v1/purchasing/orders/{id}", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/receive", viewer, new { lines = new[] { new { lineId, acceptedQuantity = 1, rejectedQuantity = 0 } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/cancel", viewer, new { reason = "no" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/payments", viewer, new { amount = 1m, method = "Cash" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync($"/api/v1/purchasing/orders/{id}/send", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync("/api/v1/suppliers", viewer, new { name = "Nope" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryPostAsync("/api/v1/purchasing/orders", viewer, new { supplierId = supplier, lines = new[] { new { variantId = variant, quantity = 1, unitCost = 1m } } })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryGetAsync("/api/v1/purchasing/orders/suggestions", viewer)).StatusCode);

        // The Inventory Manager default role buys but may not pay suppliers; the Accountant pays but does not receive.
        var nobody = await _o.StaffAsync(admin, ho, "orders.order.view");
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryGetAsync("/api/v1/purchasing/orders", nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _o.TryGetAsync("/api/v1/suppliers", nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _o.Api.GetAsync("/api/v1/suppliers")).StatusCode);
    }
}
