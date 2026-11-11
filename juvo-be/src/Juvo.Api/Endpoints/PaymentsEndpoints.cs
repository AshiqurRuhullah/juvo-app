using System.Text;
using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Payments;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Infrastructure.Payments;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Mvc;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Payments (module I). The customer side (start a payment, follow it, the browser return, a manual transfer) and the gateway webhook are anonymous on purpose and rate limited:
/// the customer proves the order with the tracking token shown at checkout (or a customer token), and the webhook proves itself with the provider's signature. Every staff endpoint
/// names its permission; the services prove it again in the payment's own branch.
/// </summary>
internal static class PaymentsEndpoints
{
    private const string TrackingTokenHeader = "X-Tracking-Token";

    public static WebApplication MapPaymentsEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapStore(v1.MapGroup("/store").WithTags("Payments").AllowAnonymous().RequireRateLimiting(RateLimitSettings.StorefrontPolicy));
        MapStaff(v1.MapGroup("/payments").WithTags("Payments (staff)"));
        return app;
    }

    private static void MapStore(RouteGroupBuilder g)
    {
        g.MapGet("/payment-methods", async (string? country, decimal? amount, CustomerPaymentsService s, CancellationToken ct) => Results.Ok(await s.MethodsAsync(country, amount, ct)))
            .Produces<List<PaymentMethodView>>()
            .WithSummary("The payment methods the shop offers (country and amount decide whether each is available, with the reason when not). For the checkout page.");

        g.MapPost("/payments/initiate", async (InitiatePaymentRequest r, [FromHeader(Name = TrackingTokenHeader)] string? header, CustomerPaymentsService s, CancellationToken ct) =>
                (await s.InitiateAsync(string.IsNullOrWhiteSpace(r.TrackingToken) ? r with { TrackingToken = header } : r, ct)).ToHttpResult())
            .RequireRateLimiting(RateLimitSettings.CheckoutPolicy).Produces<PaymentSessionDto>()
            .WithSummary("Start (or resume) the online payment of an order: identify it by orderId or orderNumber plus the tracking token (or sign in as its owner). The answer holds redirectUrl, where to send the customer, and the payment id to follow.");

        g.MapGet("/payments/{id:guid}/status", async (Guid id, string? token, [FromHeader(Name = TrackingTokenHeader)] string? header, CustomerPaymentsService s, CancellationToken ct) =>
                (await s.StatusAsync(id, string.IsNullOrWhiteSpace(token) ? header : token, ct)).ToHttpResult())
            .Produces<PaymentSessionDto>()
            .WithSummary("Where a payment stands, for polling: payment status, order status and order payment status. Reads the shop's records only (the webhook or the return check changes them).");

        g.MapPost("/payments/{id:guid}/confirm", async (Guid id, string? token, [FromHeader(Name = TrackingTokenHeader)] string? header, CustomerPaymentsService s, CancellationToken ct) =>
                (await s.ConfirmAsync(id, string.IsNullOrWhiteSpace(token) ? header : token, ct)).ToHttpResult())
            .Produces<PaymentSessionDto>()
            .WithSummary("Call this when the browser returns from the gateway page. The shop asks the gateway server to server and only a confirmed success makes the payment Paid; nothing in the return URL is trusted.");

        g.MapPost("/payments/manual", async (ManualPaymentRequest r, [FromHeader(Name = TrackingTokenHeader)] string? header, CustomerPaymentsService s, CancellationToken ct) =>
                (await s.SubmitManualAsync(string.IsNullOrWhiteSpace(r.TrackingToken) ? r with { TrackingToken = header } : r, ct)).ToHttpResult())
            .RequireRateLimiting(RateLimitSettings.CheckoutPolicy).Produces<PaymentSessionDto>()
            .WithSummary("Report a bank or mobile transfer: the transaction id (and who paid). The payment becomes UnderReview until staff approve or reject it.");

        g.MapPost("/payments/{id:guid}/proof", async (Guid id, IFormFile file, string? token, [FromHeader(Name = TrackingTokenHeader)] string? header, CustomerPaymentsService s, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return (await s.UploadProofAsync(id, string.IsNullOrWhiteSpace(token) ? header : token, stream, ct)).ToHttpResult();
            })
            .DisableAntiforgery().WithMetadata(new RequestSizeLimitAttribute(6 * 1024 * 1024)).Produces<PaymentSessionDto>()
            .WithSummary("Attach a screenshot or receipt (JPEG, PNG, WebP or PDF, up to 5 MB) to a manual transfer that is waiting for review.");

        g.MapPost("/payments/webhooks/{provider}", async (string provider, HttpRequest request, PaymentWebhookService s, CancellationToken ct) =>
            {
                using var reader = new StreamReader(request.Body, Encoding.UTF8);
                var body = await reader.ReadToEndAsync(ct);
                var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                return (await s.ProcessAsync(provider, body, headers, ct)).ToHttpResult();
            })
            .WithMetadata(new RequestSizeLimitAttribute(64 * 1024)).Produces<WebhookResultDto>()
            .WithSummary("Gateway webhook (the provider calls this, not the browser). The signature header and the replay window are checked, an event id seen before changes nothing, and a success is verified with the gateway before the payment becomes Paid.");

        // The sandbox gateway pay page and its JSON form (a front end may draw its own page from these).
        g.MapGet("/payments/sandbox/sessions/{sessionId}", async (string sessionId, SandboxPayService s, CancellationToken ct) => (await s.GetAsync(sessionId, ct)).ToHttpResult())
            .Produces<SandboxSessionDto>().WithSummary("Sandbox gateway only: the payment session behind a sandbox pay page.");

        g.MapPost("/payments/sandbox/sessions/{sessionId}/complete", async (string sessionId, SandboxDecisionRequest r, SandboxPayService s, CancellationToken ct) => (await s.DecideAsync(sessionId, r, ct)).ToHttpResult())
            .Produces<SandboxDecisionDto>()
            .WithSummary("Sandbox gateway only: what the customer chose on the pay page (success, fail or cancel). The sandbox sends the shop a signed webhook (unless sendWebhook is false) and answers with the returnUrl to send the browser to.");

        g.MapGet("/payments/sandbox/pay/{sessionId}", async (string sessionId, SandboxPayService s, CancellationToken ct) =>
            {
                var session = await s.GetAsync(sessionId, ct);
                return session.IsFailure ? ResultHttpExtensions.ToProblem(session.Error!) : Results.Content(SandboxPage.Html(session.Value), "text/html; charset=utf-8");
            })
            .ExcludeFromDescription()
            .WithSummary("Sandbox gateway only: a small hosted pay page with three buttons (pay, fail, cancel).");
    }

    private static void MapStaff(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, PaymentStatus? status, string? method, PaymentKind? kind, Guid? branchId, DateTimeOffset? from, DateTimeOffset? to, StaffPaymentsService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new PaymentListQuery(status, method, kind, branchId, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.PaymentView).Produces<PagedResult<PaymentListItemDto>>()
            .WithSummary("Payments of the branches where you may view payments. Filters: status (UnderReview is the manual-transfer queue), method, kind, branch, date range; search by order number, transfer reference or gateway transaction id.");

        g.MapGet("/{id:guid}", async (Guid id, StaffPaymentsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.PaymentView).Produces<PaymentDetailDto>()
            .WithSummary("One payment with its append-only events, its refunds and the actions that are open (approve, reject, refund, settle).");

        g.MapGet("/orders/{orderId:guid}", async (Guid orderId, StaffPaymentsService s, CancellationToken ct) => (await s.ForOrderAsync(orderId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.PaymentView).Produces<IReadOnlyList<PaymentListItemDto>>().WithSummary("The payments of one order.");

        g.MapPost("/{id:guid}/approve", async (Guid id, ReviewDecisionRequest r, StaffPaymentsService s, CancellationToken ct) => (await s.ApproveAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.PaymentVerify).Produces<PaymentDetailDto>()
            .WithSummary("The transfer was checked and the money is there: the payment becomes Paid (and the order is confirmed when the setting is on).");

        g.MapPost("/{id:guid}/reject", async (Guid id, RejectPaymentRequest r, StaffPaymentsService s, CancellationToken ct) => (await s.RejectAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.PaymentVerify).Produces<PaymentDetailDto>()
            .WithSummary("The transfer could not be verified: the payment is Rejected and the order is cancelled (stock released).");

        g.MapPost("/orders/{orderId:guid}/advance", async (Guid orderId, RecordAdvanceRequest r, StaffPaymentsService s, CancellationToken ct) => (await s.RecordAdvanceAsync(orderId, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.PaymentRecord).Produces<PaymentDetailDto>()
            .WithSummary("Record an advance received for a large cash-on-delivery order. Once it covers the required advance the order can be confirmed without the advanceReceived flag.");

        g.MapPost("/orders/{orderId:guid}/cod-collect", async (Guid orderId, CodCollectRequest r, StaffPaymentsService s, CancellationToken ct) => (await s.CollectCodAsync(orderId, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.PaymentRecord).Produces<PaymentDetailDto>()
            .WithSummary("Record the cash the courier collected (amount must equal the total minus any advance) with the courier receipt reference.");

        g.MapPost("/orders/{orderId:guid}/cod-settle", async (Guid orderId, CodSettleRequest r, StaffPaymentsService s, CancellationToken ct) => (await s.SettleCodAsync(orderId, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Cod.Settle).Produces<PaymentDetailDto>()
            .WithSummary("The courier handed the collected cash to the shop: the payment and the order's payment track become Settled.");

        g.MapGet("/ledger", async (DateTimeOffset? from, DateTimeOffset? to, Guid? branchId, StaffPaymentsService s, CancellationToken ct) => (await s.LedgerAsync(new LedgerQuery(from, to, branchId), ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.LedgerView).Produces<LedgerDto>()
            .WithSummary("Money in and out for a period (default: this month), per method, with cash not yet settled, transfers under review and refunds still pending. Branch filter for consolidated or single-branch views.");

        g.MapGet("/refunds", async ([AsParameters] ListParams list, RefundState? status, Guid? branchId, Guid? orderId, DateTimeOffset? from, DateTimeOffset? to, RefundService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new RefundListQuery(status, branchId, orderId, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.RefundView).Produces<PagedResult<RefundDto>>().WithSummary("Refunds, newest first. Filters: status (Pending are waiting to be completed), branch, order, dates.");

        g.MapPost("/{id:guid}/refunds", async (Guid id, CreateRefundRequest r, RefundService s, CancellationToken ct) => (await s.CreateAsync(id, r, ct)).ToHttpResult(v => Results.Created($"/api/v1/payments/refunds?orderId={v.OrderId}", v)))
            .RequirePermission(Permissions.Payments.RefundCreate).Produces<RefundDto>(StatusCodes.Status201Created)
            .WithSummary("Refund part or all of a payment. Mode Gateway sends it through the gateway at once; mode Manual stays Pending until staff complete it with the transfer reference. The refund can never exceed what was paid.");

        g.MapPost("/refunds/{refundId:guid}/complete", async (Guid refundId, CompleteRefundRequest r, RefundService s, CancellationToken ct) => (await s.CompleteAsync(refundId, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.RefundApprove).Produces<RefundDto>()
            .WithSummary("Finish a Pending refund: the money was sent (reference required). The payment status, the order's payment track and a gap-free credit note follow.");

        g.MapPost("/refunds/{refundId:guid}/fail", async (Guid refundId, FailRefundRequest r, RefundService s, CancellationToken ct) => (await s.FailAsync(refundId, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.RefundApprove).Produces<RefundDto>().WithSummary("A Pending refund did not happen: the money is refundable again.");

        g.MapGet("/settings", async (PaymentSettingsService s, CancellationToken ct) => (await s.GetAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.GatewayView).Produces<PaymentSettingsDto>().WithSummary("Payment methods (switched on or off, available or not and why), the cash-on-delivery limit, the transfer instructions and the auto-confirm setting.");

        g.MapPut("/settings", async (UpdatePaymentSettingsRequest r, PaymentSettingsService s, CancellationToken ct) => (await s.UpdateAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Payments.GatewayEdit).Produces<PaymentSettingsDto>().WithSummary("Switch methods on or off, set the cash-on-delivery limit (0 = none), the account details customers see for transfers, and whether paid orders confirm themselves.");
    }
}

/// <summary>The sandbox gateway's hosted pay page: three buttons. Plain HTML and a little script (no external files), sandbox only.</summary>
internal static class SandboxPage
{
    public static string Html(SandboxSessionDto s)
    {
        var amount = $"{s.Currency} {s.Amount:N2}";
        var canDecide = s.Status == "Pending";
        static string Enc(string value) => System.Net.WebUtility.HtmlEncode(value);
        return $$"""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>Sandbox payment</title>
            <style>body{font-family:system-ui,sans-serif;background:#EBE5DB;color:#1C0807;margin:0;display:grid;place-items:center;min-height:100vh}
            main{background:#fff;border-radius:12px;padding:24px;max-width:380px;width:calc(100% - 32px);box-shadow:0 2px 12px rgba(0,0,0,.12)}
            h1{font-size:20px;margin:0 0 4px}p{margin:6px 0}.tag{color:#DB0100;font-weight:700}
            button{display:block;width:100%;min-height:48px;margin-top:10px;font-size:16px;border-radius:8px;border:1px solid #1C0807;background:#fff;cursor:pointer}
            button.pay{background:#DB0100;color:#fff;border-color:#DB0100}button:disabled{opacity:.5}</style></head>
            <body><main>
            <p class="tag">SANDBOX PAYMENT (no real money)</p>
            <h1>Order {{Enc(s.OrderNumber)}}</h1>
            <p>Amount: <strong>{{Enc(amount)}}</strong></p>
            <p id="msg">{{(canDecide ? "Choose what should happen." : "This page is " + Enc(s.Status.ToLowerInvariant()) + ".")}}</p>
            <button class="pay" data-o="success" {{(canDecide ? "" : "disabled")}}>Pay successfully</button>
            <button data-o="fail" {{(canDecide ? "" : "disabled")}}>Fail the payment</button>
            <button data-o="cancel" {{(canDecide ? "" : "disabled")}}>Cancel</button>
            </main>
            <script>
            const base = location.pathname.replace('/sandbox/pay/', '/sandbox/sessions/');
            for (const b of document.querySelectorAll('button')) b.addEventListener('click', async () => {
              document.querySelectorAll('button').forEach(x => x.disabled = true);
              const r = await fetch(base + '/complete', {method:'POST', headers:{'Content-Type':'application/json'}, body: JSON.stringify({outcome: b.dataset.o})});
              const j = await r.json().catch(() => ({}));
              document.getElementById('msg').textContent = r.ok ? 'Done. Payment status: ' + j.paymentStatus : (j.title || 'Something went wrong.');
              if (r.ok && j.returnUrl) setTimeout(() => { location.href = j.returnUrl + (j.returnUrl.includes('?') ? '&' : '?') + 'outcome=' + j.outcome; }, 600);
            });
            </script></body></html>
            """;
    }
}
