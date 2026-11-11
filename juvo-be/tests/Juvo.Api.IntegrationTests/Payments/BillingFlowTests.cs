using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Billing;
using Juvo.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juvo.Api.IntegrationTests.Payments;

public partial class BillingFlowTests(PaymentFactory factory) : IClassFixture<PaymentFactory>
{
    private readonly PaymentHelper _p = new(new OrderHelper(new StoreHelper(factory)));

    private StoreHelper H => _p.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [GeneratedRegex(@"^INV-HO-\d{4}-\d{6}$")]
    private static partial Regex InvoiceNumber();

    [GeneratedRegex(@"^CN-HO-\d{4}-\d{6}$")]
    private static partial Regex CreditNoteNumber();

    private async Task DrainOutboxAsync()
    {
        var processor = factory.Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    private async Task<(string Admin, Guid OrderId, string Number, string Mobile, string Token)> ConfirmedCodOrderAsync(decimal price = 800m, int quantity = 2)
    {
        var (admin, product) = await _p.ProductAsync(price);
        var mobile = StoreHelper.NewMobile();
        var placed = await _p.O.PlaceAsync(product.Variants[0].Id, quantity, mobile);
        var id = placed.GetProperty("orderId").GetGuid();
        await _p.O.ActAsync(admin, id, "confirm");
        return (admin, id, Str(placed, "number"), mobile, Str(placed, "trackingToken"));
    }

    // ---- when and how an invoice appears --------------------------------------------------------------------

    [Fact]
    public async Task An_invoice_is_issued_when_the_order_is_confirmed_and_the_customer_is_told()
    {
        var (admin, id, number, mobile, _) = await ConfirmedCodOrderAsync();
        await DrainOutboxAsync();

        var customer = await H.CustomerTokenAsync(mobile);
        var invoice = await H.JsonAsync(HttpMethod.Get, $"/api/v1/me/orders/{id}/invoice", HttpStatusCode.OK, bearer: customer);
        Assert.Matches(InvoiceNumber(), Str(invoice, "number"));
        Assert.Equal(number, Str(invoice, "orderNumber"));
        Assert.Equal(1600m, invoice.GetProperty("subtotal").GetDecimal());
        var order = await _p.OrderAsync(admin, id);
        Assert.Equal(order.GetProperty("money").GetProperty("grandTotal").GetDecimal(), invoice.GetProperty("grandTotal").GetDecimal());
        Assert.Equal(0m, invoice.GetProperty("taxTotal").GetDecimal()); // no tax rule is active
        Assert.Equal(JsonValueKind.Null, invoice.GetProperty("taxRuleCode").ValueKind);
        Assert.Single(invoice.GetProperty("lines").EnumerateArray());
        Assert.Equal(2, invoice.GetProperty("lines")[0].GetProperty("quantity").GetInt32());
        Assert.Equal("Juvo BD", Str(invoice.GetProperty("seller"), "name"));

        var staffList = await H.JsonAsync(HttpMethod.Get, $"/api/v1/billing/invoices?search={number}", HttpStatusCode.OK, bearer: admin);
        Assert.Single(staffList.GetProperty("items").EnumerateArray());

        var messages = await _p.DbAsync(db => db.NotificationLogs.AsNoTracking().Where(n => n.Recipient == "+88" + mobile && n.Template == "invoice.ready").ToListAsync(Ct));
        Assert.Single(messages);
        Assert.Contains(Str(invoice, "number"), messages[0].Body, StringComparison.Ordinal);

        // Draining again changes nothing (the outbox may deliver twice): still one invoice, one message.
        await DrainOutboxAsync();
        Assert.Equal(1, await _p.DbAsync(db => db.Invoices.IgnoreQueryFilters(["BranchScope"]).CountAsync(i => i.OrderId == id, Ct)));
    }

    [Fact]
    public async Task A_guest_downloads_the_invoice_with_the_tracking_token_and_only_after_the_order_is_confirmed()
    {
        var (admin, product) = await _p.ProductAsync();
        var placed = await _p.O.PlaceAsync(product.Variants[0].Id, 1);
        var id = placed.GetProperty("orderId").GetGuid();
        var number = Str(placed, "number");
        var token = Str(placed, "trackingToken");

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, $"/api/v1/store/orders/invoice?number={number}&token={token}", null), "INVOICE.ORDER_NOT_INVOICEABLE");
        await _p.O.ActAsync(admin, id, "confirm");

        var invoice = await H.JsonAsync(HttpMethod.Get, $"/api/v1/store/orders/invoice?number={number}&token={token}", HttpStatusCode.OK);
        Assert.Matches(InvoiceNumber(), Str(invoice, "number"));
        var again = await H.JsonAsync(HttpMethod.Get, $"/api/v1/store/orders/invoice?number={number}&token={token}", HttpStatusCode.OK);
        Assert.Equal(Str(invoice, "number"), Str(again, "number")); // the same invoice, not a second one

        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/store/orders/invoice?number={number}&token=wrong", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/store/orders/invoice?number={number}", null)).StatusCode);
        var other = await _p.O.PlaceAsync(product.Variants[0].Id, 1);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/store/orders/invoice?number={number}&token={Str(other, "trackingToken")}", null)).StatusCode);

        // Another customer cannot read it with a customer token either.
        var stranger = await H.CustomerTokenAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/me/orders/{id}/invoice", null, stranger)).StatusCode);
    }

    [Fact]
    public async Task An_issued_invoice_can_never_be_changed_or_deleted()
    {
        var (_, id, _, _, token) = await ConfirmedCodOrderAsync();
        var number = Str(await H.JsonAsync(HttpMethod.Get, $"/api/v1/store/orders/invoice?number={(await _p.DbAsync(db => db.Orders.IgnoreQueryFilters(["BranchScope"]).Where(o => o.Id == id).Select(o => o.Number).SingleAsync(Ct)))}&token={token}", HttpStatusCode.OK), "number");

        await _p.DbAsync(async db =>
        {
            var invoice = await db.Invoices.IgnoreQueryFilters(["BranchScope"]).Include(i => i.Lines).SingleAsync(i => i.Number == number, Ct);
            db.Entry(invoice).Property(i => i.GrandTotal).CurrentValue = 1m;
            var edit = await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            Assert.Contains("append-only", edit.Message, StringComparison.Ordinal);
            db.ChangeTracker.Clear();

            var again = await db.Invoices.IgnoreQueryFilters(["BranchScope"]).SingleAsync(i => i.Number == number, Ct);
            db.Invoices.Remove(again);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            db.ChangeTracker.Clear();

            var line = await db.Set<InvoiceLine>().FirstAsync(l => l.InvoiceId == again.Id, Ct);
            db.Entry(line).Property(l => l.UnitPrice).CurrentValue = 1m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            return 0;
        });
    }

    // ---- gap-free numbers ---------------------------------------------------------------------------------

    private static int Sequence(string number) => int.Parse(number[(number.LastIndexOf('-') + 1)..], System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public async Task Invoice_numbers_are_gap_free_and_never_repeat_when_many_are_issued_at_the_same_moment()
    {
        var (admin, product) = await _p.ProductAsync(price: 400m, stock: 60);
        var ids = new List<Guid>();
        for (var i = 0; i < 8; i++)
        {
            var placed = await _p.O.PlaceAsync(product.Variants[0].Id, 1);
            var id = placed.GetProperty("orderId").GetGuid();
            await _p.O.ActAsync(admin, id, "confirm");
            ids.Add(id);
        }

        // Every order is asked twice, all at once: sixteen requests race for eight invoices.
        var answers = await Task.WhenAll(ids.Concat(ids).Select(id => _p.TryStaffPostAsync(admin, $"/api/v1/billing/orders/{id}/invoice")));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.OK, a.StatusCode));
        var numbers = (await _p.DbAsync(db => db.Invoices.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Select(i => i.Number).ToListAsync(Ct)));
        Assert.All(numbers, n => Assert.Matches(InvoiceNumber(), n));
        Assert.Equal(numbers.Count, numbers.Distinct().Count());
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers.Select(Sequence).Order()); // 1, 2, 3 ... with no hole
        Assert.Equal(8, await _p.DbAsync(db => db.Invoices.IgnoreQueryFilters(["BranchScope"]).CountAsync(i => ids.Contains(i.OrderId), Ct)));
    }

    [Fact]
    public async Task Credit_note_numbers_are_gap_free_one_per_completed_refund()
    {
        var (admin, product) = await _p.ProductAsync(price: 500m, stock: 40);
        var payments = new List<Guid>();
        for (var i = 0; i < 6; i++)
        {
            var order = await _p.PlaceAsync(product.Variants[0].Id, 1);
            payments.Add((await _p.PayAsync(order)).GetProperty("paymentId").GetGuid());
        }

        var answers = await Task.WhenAll(payments.Select(id => _p.TryStaffPostAsync(admin, $"/api/v1/payments/{id}/refunds", new { amount = 100m, mode = "Gateway", reason = "Race for numbers" })));

        Assert.All(answers, a => Assert.Equal(HttpStatusCode.Created, a.StatusCode));
        var numbers = await _p.DbAsync(db => db.CreditNotes.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Select(c => c.Number).ToListAsync(Ct));
        Assert.All(numbers, n => Assert.Matches(CreditNoteNumber(), n));
        Assert.Equal(Enumerable.Range(1, numbers.Count), numbers.Select(Sequence).Order());
        // Credit notes and invoices count separately.
        Assert.True(await _p.DbAsync(db => db.CreditNotes.IgnoreQueryFilters(["BranchScope"]).Select(c => c.RefundId).Distinct().CountAsync(Ct)) >= 6);
    }

    // ---- tax ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_sample_vat_rule_is_inactive_and_unverified_and_an_active_rule_reports_tax_without_changing_a_total()
    {
        var (admin, product) = await _p.ProductAsync(price: 1000m);
        var rules = await H.JsonAsync(HttpMethod.Get, "/api/v1/billing/tax-rules", HttpStatusCode.OK, bearer: admin);
        var vat = rules.EnumerateArray().Single(r => Str(r, "code") == "VAT15");
        Assert.False(vat.GetProperty("isActive").GetBoolean());
        Assert.False(vat.GetProperty("isVerified").GetBoolean());
        Assert.Equal(15m, vat.GetProperty("ratePercent").GetDecimal());
        Assert.True(vat.GetProperty("isInclusive").GetBoolean());
        var id = vat.GetProperty("id").GetGuid();

        // Without a rule: the total is the total.
        var before = await _p.O.PlaceAsync(product.Variants[0].Id, 2);
        var totalBefore = before.GetProperty("grandTotal").GetDecimal();

        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/billing/tax-rules/{id}/activate", new { acknowledgeUnverified = false }), "INVOICE.TAX_RULE_UNVERIFIED");
        var active = await _p.StaffPostAsync(admin, $"/api/v1/billing/tax-rules/{id}/activate", new { acknowledgeUnverified = true });
        Assert.True(active.GetProperty("isActive").GetBoolean());
        try
        {
            var placed = await _p.O.PlaceAsync(product.Variants[0].Id, 2);
            var orderId = placed.GetProperty("orderId").GetGuid();
            Assert.Equal(totalBefore, placed.GetProperty("grandTotal").GetDecimal()); // prices include tax: nothing was added
            await _p.O.ActAsync(admin, orderId, "confirm");
            var invoice = (await _p.StaffPostAsync(admin, $"/api/v1/billing/orders/{orderId}/invoice"));

            var expected = TaxMath.ForOrder([2000m], 0m, invoice.GetProperty("deliveryCharge").GetDecimal(), 15m, true);
            Assert.Equal("VAT15", Str(invoice, "taxRuleCode"));
            Assert.Equal(15m, invoice.GetProperty("taxRatePercent").GetDecimal());
            Assert.Equal(expected.TaxTotal, invoice.GetProperty("taxTotal").GetDecimal());
            Assert.Equal(expected.LineTax[0], invoice.GetProperty("lines")[0].GetProperty("taxAmount").GetDecimal());
            Assert.Equal(invoice.GetProperty("grandTotal").GetDecimal() - expected.TaxTotal, invoice.GetProperty("netTotal").GetDecimal());
            Assert.Equal(totalBefore, invoice.GetProperty("grandTotal").GetDecimal());
            Assert.Equal(expected.TaxTotal, await _p.DbAsync(db => db.Orders.IgnoreQueryFilters(["BranchScope"]).Where(o => o.Id == orderId).Select(o => o.TaxAmount).SingleAsync(Ct)));

            // An order placed before the rule has no tax on its invoice, even if the invoice is issued now.
            var oldId = before.GetProperty("orderId").GetGuid();
            await _p.O.ActAsync(admin, oldId, "confirm");
            Assert.Equal(0m, (await _p.StaffPostAsync(admin, $"/api/v1/billing/orders/{oldId}/invoice")).GetProperty("taxTotal").GetDecimal());
        }
        finally
        {
            Assert.False((await _p.StaffPostAsync(admin, $"/api/v1/billing/tax-rules/{id}/deactivate")).GetProperty("isActive").GetBoolean());
        }

        // Rules: an exclusive rule is refused, codes are unique, rates are checked, only one rule is active at a time.
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, "/api/v1/billing/tax-rules", new { code = "EXCL", name = "Exclusive", ratePercent = 5m, isInclusive = false }), "INVOICE.EXCLUSIVE_NOT_SUPPORTED");
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, "/api/v1/billing/tax-rules", new { code = "vat15", name = "Again", ratePercent = 15m, isInclusive = true }), "INVOICE.TAX_RULE_CODE_TAKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await _p.TryStaffPostAsync(admin, "/api/v1/billing/tax-rules", new { code = "BAD", name = "Bad", ratePercent = 150m, isInclusive = true })).StatusCode);
        var second = await _p.StaffPostAsync(admin, "/api/v1/billing/tax-rules", new { code = "SD5", name = "Supplementary duty", ratePercent = 5m, isInclusive = true, isVerified = true }, HttpStatusCode.Created);
        await _p.StaffPostAsync(admin, $"/api/v1/billing/tax-rules/{second.GetProperty("id").GetGuid()}/activate", new { acknowledgeUnverified = false });
        await _p.StaffPostAsync(admin, $"/api/v1/billing/tax-rules/{id}/activate", new { acknowledgeUnverified = true });
        var now = (await H.JsonAsync(HttpMethod.Get, "/api/v1/billing/tax-rules", HttpStatusCode.OK, bearer: admin)).EnumerateArray().Where(r => r.GetProperty("isActive").GetBoolean()).Select(r => Str(r, "code")).ToArray();
        Assert.Equal(["VAT15"], now);
        await _p.StaffPostAsync(admin, $"/api/v1/billing/tax-rules/{id}/deactivate");
        var updated = await H.JsonAsync(HttpMethod.Put, $"/api/v1/billing/tax-rules/{id}", HttpStatusCode.OK, new { name = "Value added tax (sample)", ratePercent = 15m, isInclusive = true, appliesToDelivery = true, isVerified = false, note = "still waiting for the accountant" }, admin);
        Assert.Equal("still waiting for the accountant", Str(updated, "note"));
    }

    // ---- credit notes ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_completed_refund_issues_a_credit_note_that_shows_on_the_invoice()
    {
        var (admin, product) = await _p.ProductAsync(price: 1000m);
        var order = await _p.PlaceAsync(product.Variants[0].Id, 1);
        var paymentId = (await _p.PayAsync(order)).GetProperty("paymentId").GetGuid();
        var refund = await _p.RefundAsync(admin, paymentId, 250m);
        var noteNumber = Str(refund, "creditNoteNumber");

        var notes = await H.JsonAsync(HttpMethod.Get, $"/api/v1/billing/credit-notes?search={noteNumber}", HttpStatusCode.OK, bearer: admin);
        var note = notes.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(250m, note.GetProperty("amount").GetDecimal());
        Assert.Equal(order.Number, Str(note, "orderNumber"));
        var one = await H.JsonAsync(HttpMethod.Get, $"/api/v1/billing/credit-notes/{note.GetProperty("id").GetGuid()}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(Str(note, "invoiceNumber"), Str(one, "invoiceNumber"));

        // The invoice (issued for the refund when the order had none) lists it.
        var invoiceId = (await H.JsonAsync(HttpMethod.Get, $"/api/v1/billing/invoices?search={order.Number}", HttpStatusCode.OK, bearer: admin)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var invoice = await H.JsonAsync(HttpMethod.Get, $"/api/v1/billing/invoices/{invoiceId}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(250m, invoice.GetProperty("creditedTotal").GetDecimal());
        Assert.Equal(noteNumber, Str(invoice.GetProperty("creditNotes")[0], "number"));
        Assert.Equal("PartiallyRefunded", Str(invoice, "paymentStatus"));

        // A credit note is append-only like an invoice.
        await _p.DbAsync(async db =>
        {
            var row = await db.CreditNotes.IgnoreQueryFilters(["BranchScope"]).SingleAsync(c => c.Number == noteNumber, Ct);
            db.Entry(row).Property(c => c.Amount).CurrentValue = 1m;
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync(Ct));
            return 0;
        });
    }

    // ---- the PDF ----------------------------------------------------------------------------------------------

    private static bool IsPdf(byte[] bytes) => bytes.Length > 1500 && Encoding.ASCII.GetString(bytes, 0, 5) == "%PDF-" && Encoding.ASCII.GetString(bytes, bytes.Length - 64, 64).Contains("%%EOF", StringComparison.Ordinal);

    [Fact]
    public async Task The_invoice_pdf_is_a_real_pdf_in_english_and_in_bengali_with_the_bengali_font_embedded()
    {
        var (admin, id, number, mobile, token) = await ConfirmedCodOrderAsync();
        await _p.DbAsync(async db => await db.OrderItems.Where(i => i.OrderId == id).ExecuteUpdateAsync(set => set.SetProperty(i => i.NameBn, "কালো টি-শার্ট, আকার মাঝারি"), Ct));
        var invoiceId = (await _p.StaffPostAsync(admin, $"/api/v1/billing/orders/{id}/invoice")).GetProperty("id").GetGuid();

        var en = await H.Api.Client.GetAsync($"/api/v1/billing/invoices/{invoiceId}/pdf", Ct); // no token: refused
        Assert.Equal(HttpStatusCode.Unauthorized, en.StatusCode);

        var english = await H.SendAsync(HttpMethod.Get, $"/api/v1/billing/invoices/{invoiceId}/pdf", null, admin);
        Assert.Equal(HttpStatusCode.OK, english.StatusCode);
        Assert.Equal("application/pdf", english.Content.Headers.ContentType?.MediaType);
        Assert.EndsWith(".pdf", english.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? string.Empty, StringComparison.Ordinal);
        var englishBytes = await english.Content.ReadAsByteArrayAsync(Ct);
        Assert.True(IsPdf(englishBytes), $"not a PDF ({englishBytes.Length} bytes)");
        Assert.Contains("NotoSans", Encoding.Latin1.GetString(englishBytes), StringComparison.Ordinal); // the Latin font is embedded

        var bengali = await H.SendAsync(HttpMethod.Get, $"/api/v1/billing/invoices/{invoiceId}/pdf?lang=bn", null, admin);
        var bengaliBytes = await bengali.Content.ReadAsByteArrayAsync(Ct);
        Assert.True(IsPdf(bengaliBytes));
        Assert.Contains("NotoSansBengali", Encoding.Latin1.GetString(bengaliBytes), StringComparison.Ordinal); // the Bengali font is embedded for the Bengali text
        Assert.NotEqual(englishBytes.Length, bengaliBytes.Length);
        Assert.DoesNotContain("NotoSansBengali", Encoding.Latin1.GetString(englishBytes), StringComparison.Ordinal);

        // The customer and the guest get the same document.
        var customer = await H.CustomerTokenAsync(mobile);
        var mine = await H.SendAsync(HttpMethod.Get, $"/api/v1/me/orders/{id}/invoice/pdf", null, customer);
        Assert.True(IsPdf(await mine.Content.ReadAsByteArrayAsync(Ct)));
        var guest = await H.SendAsync(HttpMethod.Get, $"/api/v1/store/orders/invoice/pdf?number={number}&token={token}", null);
        Assert.Equal("application/pdf", guest.Content.Headers.ContentType?.MediaType);
        Assert.True(IsPdf(await guest.Content.ReadAsByteArrayAsync(Ct)));
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/store/orders/invoice/pdf?number={number}&token=nope", null)).StatusCode);
    }

    [Fact]
    public async Task A_long_invoice_runs_onto_a_second_page_and_a_credit_note_pdf_works_too()
    {
        var (admin, product) = await _p.ProductAsync(price: 100m, stock: 500);
        var mobile = StoreHelper.NewMobile();
        var cart = await _p.O.CartAsync(product.Variants[0].Id, 1);
        var order = await TestApi.ExpectAsync(await _p.O.TryCheckoutAsync(cart, mobile, new CheckoutOptions(Method: "SANDBOX", SkipOtp: true)), HttpStatusCode.Created);
        var id = order.GetProperty("orderId").GetGuid();
        var placed = new PlacedOnline(id, Str(order, "number"), Str(order, "trackingToken"), order.GetProperty("grandTotal").GetDecimal(), mobile);
        var paymentId = (await _p.PayAsync(placed)).GetProperty("paymentId").GetGuid();

        // Give the order 60 more lines by copying its single line in the database (the invoice is issued from these).
        await _p.DbAsync(async db =>
        {
            var line = await db.OrderItems.AsNoTracking().FirstAsync(i => i.OrderId == id, Ct);
            for (var n = 0; n < 60; n++)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($@"INSERT INTO OrderItems (Id, CreatedAt, OrderId, VariantId, ProductId, Name, Sku, UnitPrice, Quantity, LineTotal, WeightGrams, TracksStock) SELECT {Guid.CreateVersion7()}, CreatedAt, OrderId, VariantId, ProductId, Name + ' ' + {n.ToString(System.Globalization.CultureInfo.InvariantCulture)}, Sku, UnitPrice, Quantity, LineTotal, WeightGrams, TracksStock FROM OrderItems WHERE Id = {line.Id}", Ct);
            }

            return 0;
        });
        var invoice = await _p.StaffPostAsync(admin, $"/api/v1/billing/orders/{id}/invoice");
        Assert.Equal(61, invoice.GetProperty("lines").GetArrayLength());
        var bytes = await (await H.SendAsync(HttpMethod.Get, $"/api/v1/billing/invoices/{invoice.GetProperty("id").GetGuid()}/pdf", null, admin)).Content.ReadAsByteArrayAsync(Ct);
        Assert.True(IsPdf(bytes));
        Assert.True(Regex.Count(Encoding.Latin1.GetString(bytes), @"/Type\s*/Page\b") >= 2, "the invoice should run onto a second page");

        var refund = await _p.RefundAsync(admin, paymentId, 40m);
        var noteId = (await H.JsonAsync(HttpMethod.Get, $"/api/v1/billing/credit-notes?search={Str(refund, "creditNoteNumber")}", HttpStatusCode.OK, bearer: admin)).GetProperty("items")[0].GetProperty("id").GetGuid();
        var note = await H.SendAsync(HttpMethod.Get, $"/api/v1/billing/credit-notes/{noteId}/pdf?lang=bn", null, admin);
        Assert.Equal(HttpStatusCode.OK, note.StatusCode);
        Assert.True(IsPdf(await note.Content.ReadAsByteArrayAsync(Ct)));
    }

    // ---- permissions and branches ----------------------------------------------------------------------------

    [Fact]
    public async Task Invoice_permissions_and_branch_isolation_are_enforced()
    {
        var (admin, id, number, _, _) = await ConfirmedCodOrderAsync();
        var invoice = await _p.StaffPostAsync(admin, $"/api/v1/billing/orders/{id}/invoice");
        var invoiceId = invoice.GetProperty("id").GetGuid();

        var viewer = await _p.O.StaffTokenAsync(admin, null, "tax.invoice.view");
        var issuer = await _p.O.StaffTokenAsync(admin, null, "tax.invoice.view", "tax.invoice.issue", "tax.invoice.download");
        var nobody = await _p.O.StaffTokenAsync(admin, null, "catalog.product.view");

        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, $"/api/v1/billing/invoices/{invoiceId}", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, $"/api/v1/billing/invoices/{invoiceId}/pdf", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _p.TryStaffPostAsync(viewer, $"/api/v1/billing/orders/{id}/invoice")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _p.TryStaffPostAsync(issuer, $"/api/v1/billing/orders/{id}/invoice")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/billing/invoices", null, nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/billing/tax-rules", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/billing/credit-notes", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, "/api/v1/billing/invoices", null)).StatusCode);

        var branch = await H.Api.CreateBranchAsync(admin, "BI" + Random.Shared.Next(10, 99), "Other Billing Branch");
        var elsewhere = await _p.O.StaffTokenAsync(admin, branch, "tax.invoice.view", "tax.invoice.issue", "tax.invoice.download");
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/billing/invoices/{invoiceId}", null, elsewhere)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/billing/invoices/{invoiceId}/pdf", null, elsewhere)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _p.TryStaffPostAsync(elsewhere, $"/api/v1/billing/orders/{id}/invoice")).StatusCode);
        var list = await H.JsonAsync(HttpMethod.Get, $"/api/v1/billing/invoices?search={number}", HttpStatusCode.OK, bearer: elsewhere);
        Assert.Equal(0, list.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_default_roles_hold_the_documented_payment_and_billing_permissions()
    {
        var (admin, product) = await _p.ProductAsync(price: 1000m);
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var paymentId = (await _p.PayAsync(order)).GetProperty("paymentId").GetGuid();
        var branch = await H.Api.BranchIdAsync("HO");

        async Task<string> TokenAsync(string role)
        {
            var user = await H.Api.CreateUserAsync(admin, $"{StoreHelper.Unique("role")}@example.com", role, false, (branch, await H.Api.RoleIdAsync(role)));
            return (await H.Api.LoginAsync(user.Email, user.Password)).Access;
        }

        var accountant = await TokenAsync("Accountant");
        var orderManager = await TokenAsync("Order Manager");
        var support = await TokenAsync("Support");

        // Accountant (SRS roles matrix): sees payments, refunds, the ledger and tax; cannot start or approve refunds.
        Assert.Equal(HttpStatusCode.Forbidden, (await _p.TryStaffPostAsync(accountant, $"/api/v1/payments/{paymentId}/refunds", new { amount = 10m, mode = "Manual", reason = "Accountant refund" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, "/api/v1/payments/refunds", null, accountant)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, "/api/v1/payments/ledger", null, accountant)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, "/api/v1/billing/tax-rules", null, accountant)).StatusCode);
        // Order Manager: sees payments, records cash and advances, issues invoices; no refunds, no ledger.
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, $"/api/v1/payments/{paymentId}", null, orderManager)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _p.TryStaffPostAsync(orderManager, $"/api/v1/payments/{paymentId}/refunds", new { amount = 10m, mode = "Manual", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/payments/ledger", null, orderManager)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _p.TryStaffPostAsync(orderManager, $"/api/v1/billing/orders/{order.OrderId}/invoice")).StatusCode);
        // Support: can look, cannot change.
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, $"/api/v1/payments/{paymentId}", null, support)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _p.TryStaffPostAsync(support, $"/api/v1/billing/orders/{order.OrderId}/invoice")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _p.TryStaffPostAsync(support, $"/api/v1/payments/{paymentId}/refunds", new { amount = 10m, mode = "Manual", reason = "x" })).StatusCode);
    }
}
