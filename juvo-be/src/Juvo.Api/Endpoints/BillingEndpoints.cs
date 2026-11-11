using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Billing;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Mvc;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Tax rules, invoices and credit notes (module U). Staff endpoints name their permission and the services prove it again in the document's own branch. The customer downloads
/// the invoice of their own order with a customer token, or with the tracking token shown at checkout.
/// </summary>
internal static class BillingEndpoints
{
    private const string TrackingTokenHeader = "X-Tracking-Token";

    private static IResult Pdf(Result<(byte[] Bytes, string FileName)> result) =>
        result.IsFailure ? ResultHttpExtensions.ToProblem(result.Error!) : Results.File(result.Value.Bytes, "application/pdf", result.Value.FileName);

    public static WebApplication MapBillingEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapStaff(v1.MapGroup("/billing").WithTags("Billing (staff)"));
        MapMe(v1.MapGroup("/me/orders").WithTags("Customer orders").RequireAuthorization(Policies.Customer));
        MapPublic(v1.MapGroup("/store/orders").WithTags("Checkout").AllowAnonymous().RequireRateLimiting(RateLimitSettings.CheckoutPolicy));
        return app;
    }

    private static void MapStaff(RouteGroupBuilder g)
    {
        g.MapGet("/tax-rules", async (TaxService s, CancellationToken ct) => (await s.ListAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.Tax.RuleView).Produces<IReadOnlyList<TaxRuleDto>>()
            .WithSummary("Tax rules. The seeded 15% VAT sample is inactive and unverified. Prices are tax-inclusive, so a rule only reports the tax inside the price.");

        g.MapPost("/tax-rules", async (SaveTaxRuleRequest r, TaxService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/billing/tax-rules/{v.Id}", v)))
            .RequirePermission(Permissions.Tax.RuleEdit).Produces<TaxRuleDto>(StatusCodes.Status201Created).WithSummary("Create a tax rule (inclusive only).");

        g.MapPut("/tax-rules/{id:guid}", async (Guid id, SaveTaxRuleRequest r, TaxService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Tax.RuleEdit).Produces<TaxRuleDto>().WithSummary("Change a rule's name, rate, scope, verified flag and note. Orders already placed keep the rate they were placed under.");

        g.MapPost("/tax-rules/{id:guid}/activate", async (Guid id, ActivateTaxRuleRequest r, TaxService s, CancellationToken ct) => (await s.SetActiveAsync(id, true, r.AcknowledgeUnverified, ct)).ToHttpResult())
            .RequirePermission(Permissions.Tax.RuleEdit).Produces<TaxRuleDto>().WithSummary("Make this the active rule (any other becomes inactive). An unverified rule needs acknowledgeUnverified = true.");

        g.MapPost("/tax-rules/{id:guid}/deactivate", async (Guid id, TaxService s, CancellationToken ct) => (await s.SetActiveAsync(id, false, false, ct)).ToHttpResult())
            .RequirePermission(Permissions.Tax.RuleEdit).Produces<TaxRuleDto>().WithSummary("Switch the rule off: new orders then carry no tax breakdown.");

        g.MapGet("/invoices", async ([AsParameters] ListParams list, Guid? branchId, DateTimeOffset? from, DateTimeOffset? to, InvoiceService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new InvoiceListQuery(branchId, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Tax.InvoiceView).Produces<PagedResult<InvoiceListItemDto>>().WithSummary("Invoices of the branches where you may view them; search by number, order number, mobile or name.");

        g.MapGet("/invoices/{id:guid}", async (Guid id, InvoiceService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Tax.InvoiceView).Produces<InvoiceDto>().WithSummary("One invoice: seller, customer, lines with tax, totals, current payment status and the credit notes issued against it. Invoices never change once issued.");

        g.MapGet("/invoices/{id:guid}/pdf", async (Guid id, string? lang, InvoiceService s, CancellationToken ct) => Pdf(await s.PdfAsync(id, lang, ct)))
            .RequirePermission(Permissions.Tax.InvoiceDownload).Produces<byte[]>(StatusCodes.Status200OK, "application/pdf")
            .WithSummary("The invoice as an A4 PDF (lang = en or bn).");

        g.MapPost("/orders/{orderId:guid}/invoice", async (Guid orderId, InvoiceService s, CancellationToken ct) => (await s.IssueForOrderAsync(orderId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Tax.InvoiceIssue).Produces<InvoiceDto>()
            .WithSummary("Issue the invoice of a confirmed order, or return the one that exists (it is also issued automatically when the order is confirmed). One invoice per order, numbered gap-free per branch.");

        g.MapGet("/credit-notes", async ([AsParameters] ListParams list, Guid? branchId, DateTimeOffset? from, DateTimeOffset? to, InvoiceService s, CancellationToken ct) =>
                (await s.ListCreditNotesAsync(list.ToRequest(), new CreditNoteListQuery(branchId, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Tax.CreditNoteView).Produces<PagedResult<CreditNoteDto>>().WithSummary("Credit notes: one is issued for every completed refund.");

        g.MapGet("/credit-notes/{id:guid}", async (Guid id, InvoiceService s, CancellationToken ct) => (await s.GetCreditNoteAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Tax.CreditNoteView).Produces<CreditNoteDto>().WithSummary("One credit note.");

        g.MapGet("/credit-notes/{id:guid}/pdf", async (Guid id, string? lang, InvoiceService s, CancellationToken ct) => Pdf(await s.CreditNotePdfAsync(id, lang, ct)))
            .RequirePermission(Permissions.Tax.CreditNoteView).Produces<byte[]>(StatusCodes.Status200OK, "application/pdf").WithSummary("The credit note as an A4 PDF.");
    }

    private static void MapMe(RouteGroupBuilder g)
    {
        g.MapGet("/{id:guid}/invoice", async (Guid id, InvoiceService s, CancellationToken ct) => (await s.CustomerGetAsync(id, null, null, ct)).ToHttpResult())
            .Produces<InvoiceDto>().WithSummary("The invoice of one of my orders (issued when the order is confirmed).");

        g.MapGet("/{id:guid}/invoice/pdf", async (Guid id, string? lang, InvoiceService s, CancellationToken ct) => Pdf(await s.CustomerPdfAsync(id, null, null, lang, ct)))
            .Produces<byte[]>(StatusCodes.Status200OK, "application/pdf").WithSummary("The invoice of one of my orders as a PDF (lang = en or bn).");
    }

    private static void MapPublic(RouteGroupBuilder g)
    {
        g.MapGet("/invoice", async (string? number, string? token, [FromHeader(Name = TrackingTokenHeader)] string? header, InvoiceService s, CancellationToken ct) =>
                (await s.CustomerGetAsync(null, number, string.IsNullOrWhiteSpace(token) ? header : token, ct)).ToHttpResult())
            .Produces<InvoiceDto>().WithSummary("The invoice of an order for a guest: order number plus the tracking token shown at checkout. Anything that does not match answers 404.");

        g.MapGet("/invoice/pdf", async (string? number, string? token, string? lang, [FromHeader(Name = TrackingTokenHeader)] string? header, InvoiceService s, CancellationToken ct) =>
                Pdf(await s.CustomerPdfAsync(null, number, string.IsNullOrWhiteSpace(token) ? header : token, lang, ct)))
            .Produces<byte[]>(StatusCodes.Status200OK, "application/pdf").WithSummary("The invoice PDF for a guest (same identification).");
    }
}
