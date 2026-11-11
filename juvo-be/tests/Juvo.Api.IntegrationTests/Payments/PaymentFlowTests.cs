using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Payments;

public class PaymentFlowTests(PaymentFactory factory) : IClassFixture<PaymentFactory>
{
    private readonly PaymentHelper _p = new(new OrderHelper(new StoreHelper(factory)));

    private StoreHelper H => _p.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    // ---- the sandbox happy path ---------------------------------------------------------------------------

    [Fact]
    public async Task A_sandbox_payment_ends_with_a_verified_Paid_payment_and_a_confirmed_order()
    {
        var (admin, product) = await _p.ProductAsync();
        var order = await _p.PlaceAsync(product.Variants[0].Id, 2);

        var session = await _p.InitiateAsync(order);
        Assert.Equal("Pending", Str(session, "status"));
        Assert.Equal("Online", Str(session, "kind"));
        Assert.Equal(order.Total, session.GetProperty("amount").GetDecimal());
        Assert.Contains("/api/v1/store/payments/sandbox/pay/", Str(session, "redirectUrl"), StringComparison.Ordinal);
        Assert.StartsWith("sbx_", Str(session, "sandboxSessionId"), StringComparison.Ordinal);
        var paymentId = session.GetProperty("paymentId").GetGuid();

        // Starting again while the page is still valid resumes the same payment.
        Assert.Equal(paymentId, (await _p.InitiateAsync(order)).GetProperty("paymentId").GetGuid());

        // The sandbox pay page itself is served.
        var page = await H.Api.Client.GetAsync(new Uri(Str(session, "redirectUrl"), UriKind.RelativeOrAbsolute), Ct);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Pay successfully", await page.Content.ReadAsStringAsync(Ct), StringComparison.Ordinal);

        var decision = await _p.DecideAsync(Str(session, "sandboxSessionId"), "success");
        Assert.Equal("success", Str(decision, "outcome"));
        Assert.Equal("/checkout/return", Str(decision, "returnUrl"));

        var status = await _p.StatusAsync(paymentId, order.Token);
        Assert.Equal("Paid", Str(status, "status"));
        Assert.Equal("Paid", Str(status, "orderPaymentStatus"));
        Assert.Equal("Confirmed", Str(status, "orderStatus")); // the default: a paid, unheld order confirms itself

        var detail = await _p.PaymentAsync(admin, paymentId);
        Assert.Equal("Paid", Str(detail, "status"));
        Assert.StartsWith("SBX-", Str(detail, "providerTransactionId"), StringComparison.Ordinal);
        Assert.Contains(detail.GetProperty("events").EnumerateArray(), e => Str(e, "type") == "Verified");
        Assert.Equal(["Created", "Initiated", "Verified"], detail.GetProperty("events").EnumerateArray().Select(e => Str(e, "type")).ToArray());
        Assert.Contains(detail.GetProperty("nextActions").EnumerateArray(), a => a.GetString() == "refund");

        // Stock stays reserved for the confirmed order; the order timeline shows the payment track.
        Assert.Equal((20, 2), await _p.O.StockAsync(product.Variants[0].Id));
        var staffOrder = await _p.OrderAsync(admin, order.OrderId);
        Assert.Contains(staffOrder.GetProperty("timeline").EnumerateArray(), t => Str(t, "to") == "Paid");
    }

    [Fact]
    public async Task With_auto_confirm_off_a_paid_order_waits_for_staff_and_can_then_be_confirmed()
    {
        var (admin, product) = await _p.ProductAsync();
        await _p.SettingsAsync(admin, new { autoConfirmPaid = false });
        try
        {
            var order = await _p.PlaceAsync(product.Variants[0].Id);
            await _p.PayAsync(order);

            var before = await _p.OrderAsync(admin, order.OrderId);
            Assert.Equal("Placed", Str(before, "status"));
            Assert.Equal("Paid", Str(before, "paymentStatus"));
            var confirmed = await _p.O.ActAsync(admin, order.OrderId, "confirm");
            Assert.Equal("Confirmed", Str(confirmed, "status"));
        }
        finally
        {
            await _p.SettingsAsync(admin, new { autoConfirmPaid = true });
        }
    }

    [Fact]
    public async Task The_browser_return_alone_never_marks_a_payment_Paid_but_the_server_side_check_does_once_the_gateway_confirms()
    {
        var (_, product) = await _p.ProductAsync();
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var session = await _p.InitiateAsync(order);
        var paymentId = session.GetProperty("paymentId").GetGuid();

        // The customer comes back without having paid: the shop asks the gateway and it says "pending".
        var returned = await _p.ConfirmAsync(paymentId, order.Token);
        Assert.Equal("Pending", Str(returned, "status"));
        Assert.Equal("Pending", Str(returned, "orderPaymentStatus"));

        // The customer paid, but no webhook arrived (sendWebhook = false): the return check asks the gateway and finds it paid.
        await _p.DecideAsync(Str(session, "sandboxSessionId"), "success", sendWebhook: false);
        Assert.Equal("Pending", Str(await _p.StatusAsync(paymentId, order.Token), "status")); // reading the status changes nothing
        var confirmed = await _p.ConfirmAsync(paymentId, order.Token);
        Assert.Equal("Paid", Str(confirmed, "status"));
        Assert.Equal("Confirmed", Str(confirmed, "orderStatus"));

        // Asking again is harmless.
        Assert.Equal("Paid", Str(await _p.ConfirmAsync(paymentId, order.Token), "status"));
    }

    // ---- webhooks ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_webhook_replayed_changes_nothing_the_second_time()
    {
        var (admin, product) = await _p.ProductAsync();
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var session = await _p.InitiateAsync(order);
        var sessionId = Str(session, "sandboxSessionId");
        var paymentId = session.GetProperty("paymentId").GetGuid();
        await _p.DecideAsync(sessionId, "success", sendWebhook: false); // the customer paid at the gateway; the webhook is sent by hand below

        var body = PaymentHelper.WebhookBody(sessionId, "payment.succeeded", "evt_replay_1", order.Total);
        var signature = PaymentHelper.Sign(body);
        var first = await TestApi.ExpectAsync(await _p.PostWebhookAsync(body, signature), HttpStatusCode.OK);
        Assert.Equal("processed", Str(first, "status"));
        Assert.Equal("Paid", Str(first, "paymentStatus"));

        var eventsAfterFirst = (await _p.PaymentAsync(admin, paymentId)).GetProperty("events").GetArrayLength();
        var historyAfterFirst = (await _p.OrderAsync(admin, order.OrderId)).GetProperty("timeline").GetArrayLength();

        var second = await TestApi.ExpectAsync(await _p.PostWebhookAsync(body, signature), HttpStatusCode.OK);
        Assert.Equal("duplicate", Str(second, "status"));
        var third = await TestApi.ExpectAsync(await _p.PostWebhookAsync(body, PaymentHelper.Sign(body)), HttpStatusCode.OK); // even with a fresh signature: same event id
        Assert.Equal("duplicate", Str(third, "status"));

        Assert.Equal(eventsAfterFirst, (await _p.PaymentAsync(admin, paymentId)).GetProperty("events").GetArrayLength());
        Assert.Equal(historyAfterFirst, (await _p.OrderAsync(admin, order.OrderId)).GetProperty("timeline").GetArrayLength());
        var row = await _p.PaymentRowAsync(paymentId);
        Assert.Single(row.Events, e => e.ProviderEventId == "SANDBOX:evt_replay_1");
    }

    [Fact]
    public async Task Forged_malformed_stale_and_unknown_webhooks_are_refused_and_change_nothing()
    {
        var (_, product) = await _p.ProductAsync();
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var session = await _p.InitiateAsync(order);
        var sessionId = Str(session, "sandboxSessionId");
        var paymentId = session.GetProperty("paymentId").GetGuid();
        var body = PaymentHelper.WebhookBody(sessionId, "payment.succeeded");

        // A bad or missing signature.
        await StoreHelper.ExpectCodeAsync(await _p.PostWebhookAsync(body, PaymentHelper.Sign(body, secret: "someone-elses-secret")), "PAYMENT.INVALID_SIGNATURE");
        await StoreHelper.ExpectCodeAsync(await _p.PostWebhookAsync(body, null), "PAYMENT.INVALID_SIGNATURE");
        await StoreHelper.ExpectCodeAsync(await _p.PostWebhookAsync(body + " ", PaymentHelper.Sign(body)), "PAYMENT.INVALID_SIGNATURE"); // body changed after signing
        Assert.Equal(HttpStatusCode.Unauthorized, (await _p.PostWebhookAsync(body, "t=1,v1=00")).StatusCode);

        // A correct signature outside the replay window.
        await StoreHelper.ExpectCodeAsync(await _p.PostWebhookAsync(body, PaymentHelper.Sign(body, DateTimeOffset.UtcNow.AddMinutes(-10))), "PAYMENT.WEBHOOK_EXPIRED");

        // Signed but not an event.
        await StoreHelper.ExpectCodeAsync(await _p.PostWebhookAsync("{\"hello\":1}", PaymentHelper.Sign("{\"hello\":1}")), "PAYMENT.WEBHOOK_MALFORMED");
        await StoreHelper.ExpectCodeAsync(await _p.PostWebhookAsync("not json", PaymentHelper.Sign("not json")), "PAYMENT.WEBHOOK_MALFORMED");

        // Unknown provider, and a provider slot without credentials.
        Assert.Equal(HttpStatusCode.NotFound, (await _p.PostWebhookAsync(body, PaymentHelper.Sign(body), "NOPE")).StatusCode);
        await StoreHelper.ExpectCodeAsync(await _p.PostWebhookAsync(body, PaymentHelper.Sign(body), "BKASH"), "PAYMENT.NOT_CONFIGURED");

        // A correctly signed "paid" for a payment nobody paid at the gateway: the gateway does not confirm it, so it is not marked paid (and a replay is still a duplicate).
        var forged = PaymentHelper.WebhookBody(sessionId, "payment.succeeded", "evt_forged");
        var answer = await TestApi.ExpectAsync(await _p.PostWebhookAsync(forged, PaymentHelper.Sign(forged)), HttpStatusCode.OK);
        Assert.Equal("not-confirmed", Str(answer, "status"));
        Assert.Equal("Pending", Str(await _p.StatusAsync(paymentId, order.Token), "status"));
        Assert.Equal("Pending", Str(await _p.StatusAsync(paymentId, order.Token), "orderPaymentStatus"));

        // A signed event for a session this shop does not know is acknowledged without effect.
        var stranger = PaymentHelper.WebhookBody("sbx_unknown", "payment.succeeded");
        Assert.Equal("ignored", Str(await TestApi.ExpectAsync(await _p.PostWebhookAsync(stranger, PaymentHelper.Sign(stranger)), HttpStatusCode.OK), "status"));
    }

    [Fact]
    public async Task A_webhook_that_claims_a_different_amount_is_not_trusted()
    {
        var (admin, product) = await _p.ProductAsync();
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var session = await _p.InitiateAsync(order);
        var sessionId = Str(session, "sandboxSessionId");
        await _p.DecideAsync(sessionId, "success", sendWebhook: false);

        var body = PaymentHelper.WebhookBody(sessionId, "payment.succeeded", "evt_wrong_amount", amount: 1m);
        var answer = await TestApi.ExpectAsync(await _p.PostWebhookAsync(body, PaymentHelper.Sign(body)), HttpStatusCode.OK);

        Assert.Equal("amount-mismatch", Str(answer, "status"));
        var detail = await _p.PaymentAsync(admin, session.GetProperty("paymentId").GetGuid());
        Assert.Equal("Pending", Str(detail, "status"));
        Assert.Contains(detail.GetProperty("events").EnumerateArray(), e => Str(e, "type") == "AmountMismatch");
    }

    // ---- failure, cancel, retry --------------------------------------------------------------------------

    [Fact]
    public async Task A_failed_payment_can_be_retried_and_the_failed_attempt_stays_in_the_history()
    {
        var (admin, product) = await _p.ProductAsync();
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var first = await _p.InitiateAsync(order);
        var firstId = first.GetProperty("paymentId").GetGuid();
        await _p.DecideAsync(Str(first, "sandboxSessionId"), "fail");

        var failed = await _p.StatusAsync(firstId, order.Token);
        Assert.Equal("Failed", Str(failed, "status"));
        Assert.Equal("Failed", Str(failed, "orderPaymentStatus"));
        Assert.True(failed.GetProperty("canRetry").GetBoolean());
        Assert.Equal(JsonValueKind.Null, failed.GetProperty("redirectUrl").ValueKind);

        var second = await _p.InitiateAsync(order);
        var secondId = second.GetProperty("paymentId").GetGuid();
        Assert.NotEqual(firstId, secondId);
        Assert.Equal("Pending", Str(await _p.StatusAsync(secondId, order.Token), "orderPaymentStatus"));
        await _p.PayAsync(order); // resumes the second attempt and pays it

        Assert.Equal("Paid", Str(await _p.StatusAsync(secondId, order.Token), "status"));
        Assert.Equal("Failed", Str((await _p.PaymentAsync(admin, firstId)), "status")); // the first attempt is never reopened
        var all = await _p.ForOrderAsync(admin, order.OrderId);
        Assert.Equal(["Failed", "Paid"], all.EnumerateArray().Select(x => Str(x, "status")).ToArray());
        Assert.Contains((await _p.OrderAsync(admin, order.OrderId)).GetProperty("timeline").EnumerateArray().Select(t => Str(t, "to")), s => s == "Failed");
    }

    [Fact]
    public async Task Cancelling_at_the_gateway_keeps_the_order_open_for_another_try()
    {
        var (_, product) = await _p.ProductAsync();
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var session = await _p.InitiateAsync(order);
        await _p.DecideAsync(Str(session, "sandboxSessionId"), "cancel");

        var status = await _p.StatusAsync(session.GetProperty("paymentId").GetGuid(), order.Token);
        Assert.Equal("Cancelled", Str(status, "status"));
        Assert.Equal("Pending", Str(status, "orderPaymentStatus"));
        Assert.Equal("Placed", Str(status, "orderStatus"));
        Assert.True(status.GetProperty("canRetry").GetBoolean());

        var paid = await _p.PayAsync(order);
        Assert.Equal("Confirmed", Str(await _p.StatusAsync(paid.GetProperty("paymentId").GetGuid(), order.Token), "orderStatus"));
    }

    [Fact]
    public async Task A_sandbox_page_can_be_used_once()
    {
        var (_, product) = await _p.ProductAsync();
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var session = await _p.InitiateAsync(order);
        var id = Str(session, "sandboxSessionId");

        await _p.DecideAsync(id, "fail");
        await StoreHelper.ExpectCodeAsync(await _p.TryDecideAsync(id, "success"), "PAYMENT.INVALID_TRANSITION");
        Assert.Equal(HttpStatusCode.BadRequest, (await _p.TryDecideAsync(id, "maybe")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _p.TryDecideAsync("sbx_nope", "success")).StatusCode);
    }

    // ---- who may pay what --------------------------------------------------------------------------------

    [Fact]
    public async Task Only_the_owner_with_the_token_or_the_signed_in_customer_can_pay_an_order()
    {
        var (_, product) = await _p.ProductAsync();
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var other = await _p.PlaceAsync(product.Variants[0].Id);

        await StoreHelper.ExpectCodeAsync(await _p.TryInitiateAsync(order, token: other.Token), "PAYMENT.NOT_FOUND"); // the token of another order
        await StoreHelper.ExpectCodeAsync(await _p.TryInitiateAsync(order, token: "nonsense"), "PAYMENT.NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/payments/initiate", new { orderNumber = order.Number }), "PAYMENT.NOT_FOUND"); // no proof at all

        var session = await _p.InitiateAsync(order);
        var paymentId = session.GetProperty("paymentId").GetGuid();
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/store/payments/{paymentId}/status?token={other.Token}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _p.TryConfirmAsync(paymentId, other.Token)).StatusCode);

        // The signed-in owner needs no token.
        var customer = await H.CustomerTokenAsync(order.Mobile);
        var viaCustomer = await TestApi.ExpectAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/payments/initiate", new { orderNumber = order.Number }, customer), HttpStatusCode.OK);
        Assert.Equal(paymentId, viaCustomer.GetProperty("paymentId").GetGuid());
        var stranger = await H.CustomerTokenAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, "/api/v1/store/payments/initiate", new { orderNumber = order.Number }, stranger)).StatusCode);

        // The token goes in a header too.
        var viaHeader = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/store/payments/{paymentId}/status");
        viaHeader.Headers.Add("X-Tracking-Token", order.Token);
        Assert.Equal(HttpStatusCode.OK, (await H.Api.Client.SendAsync(viaHeader, Ct)).StatusCode);
    }

    [Fact]
    public async Task A_cash_on_delivery_cancelled_or_paid_order_cannot_start_a_payment_and_the_return_url_is_checked()
    {
        var (_, product) = await _p.ProductAsync();
        var cod = await _p.PlaceAsync(product.Variants[0].Id, method: "COD");
        await StoreHelper.ExpectCodeAsync(await _p.TryInitiateAsync(cod), "PAYMENT.ORDER_NOT_PAYABLE");

        var order = await _p.PlaceAsync(product.Variants[0].Id);
        await StoreHelper.ExpectCodeAsync(await _p.TryInitiateAsync(order, returnUrl: "https://evil.example/steal"), "PAYMENT.RETURN_URL_INVALID");
        await StoreHelper.ExpectCodeAsync(await _p.TryInitiateAsync(order, returnUrl: "//evil.example"), "PAYMENT.RETURN_URL_INVALID");
        await StoreHelper.ExpectCodeAsync(await _p.TryInitiateAsync(order, method: "COD"), "PAYMENT.METHOD_UNAVAILABLE");
        Assert.Equal(HttpStatusCode.OK, (await _p.TryInitiateAsync(order, returnUrl: "http://localhost:3000/pay/done")).StatusCode); // the configured front-end origin is allowed

        await _p.PayAsync(order);
        await StoreHelper.ExpectCodeAsync(await _p.TryInitiateAsync(order), "PAYMENT.ALREADY_PAID");
    }

    [Fact]
    public async Task A_provider_slot_without_credentials_cannot_start_a_payment()
    {
        var (admin, product) = await _p.ProductAsync();
        await _p.SettingsAsync(admin, new { methods = new[] { new { code = "BKASH", enabled = true } } });
        try
        {
            var settings = await H.JsonAsync(HttpMethod.Get, "/api/v1/payments/settings", HttpStatusCode.OK, bearer: admin);
            var bkash = settings.GetProperty("methods").EnumerateArray().Single(m => Str(m, "code") == "BKASH");
            Assert.True(bkash.GetProperty("enabled").GetBoolean());
            Assert.False(bkash.GetProperty("available").GetBoolean());
            Assert.Equal("This payment method is not set up yet.", Str(bkash, "reason"));

            var methods = await H.JsonAsync(HttpMethod.Get, "/api/v1/store/payment-methods", HttpStatusCode.OK);
            Assert.False(methods.EnumerateArray().Single(m => Str(m, "code") == "BKASH").GetProperty("available").GetBoolean());

            var cart = await _p.O.CartAsync(product.Variants[0].Id, 1);
            await StoreHelper.ExpectCodeAsync(await _p.O.TryCheckoutAsync(cart, StoreHelper.NewMobile(), new CheckoutOptions(Method: "BKASH", SkipOtp: true)), "ORDER.PAYMENT_METHOD_UNAVAILABLE");
        }
        finally
        {
            await _p.SettingsAsync(admin, new { methods = new[] { new { code = "BKASH", enabled = false } } });
        }
    }

    // ---- manual transfers: UnderReview, approve, reject ---------------------------------------------------

    private async Task EnableManualAsync(string admin) =>
        await _p.SettingsAsync(admin, new { methods = new[] { new { code = "MANUAL", enabled = true } }, manualInstructions = "bKash personal 01700000000 (reference: your order number)" });

    [Fact]
    public async Task A_manual_transfer_is_under_review_until_staff_approve_it()
    {
        var (admin, product) = await _p.ProductAsync();
        await _p.SettingsAsync(admin, new { methods = new[] { new { code = "MANUAL", enabled = false } } }); // the default state, whatever an earlier test left
        var methods = await H.JsonAsync(HttpMethod.Get, "/api/v1/store/payment-methods", HttpStatusCode.OK);
        Assert.DoesNotContain(methods.EnumerateArray(), m => Str(m, "code") == "MANUAL"); // off by default: the owner has to write the account details first
        await EnableManualAsync(admin);

        var offered = (await H.JsonAsync(HttpMethod.Get, "/api/v1/store/payment-methods", HttpStatusCode.OK)).EnumerateArray().Single(m => Str(m, "code") == "MANUAL");
        Assert.True(offered.GetProperty("available").GetBoolean());
        Assert.Contains("01700000000", Str(offered, "instructions"), StringComparison.Ordinal);

        var order = await _p.PlaceAsync(product.Variants[0].Id, method: "MANUAL");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/payments/manual", new { orderNumber = order.Number, trackingToken = order.Token, reference = "ab" }), "PAYMENT.REFERENCE_REQUIRED");
        var submitted = await TestApi.ExpectAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/payments/manual", new { orderNumber = order.Number, trackingToken = order.Token, reference = "tx7788abc", payerName = "Nadia" }), HttpStatusCode.OK);
        var paymentId = submitted.GetProperty("paymentId").GetGuid();
        Assert.Equal("UnderReview", Str(submitted, "status"));
        Assert.Equal("UnderReview", Str(submitted, "orderPaymentStatus"));
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/payments/manual", new { orderNumber = order.Number, trackingToken = order.Token, reference = "tx7788abd" }), "PAYMENT.ALREADY_PAID");

        // The same reference cannot be used for another order.
        var other = await _p.PlaceAsync(product.Variants[0].Id, method: "MANUAL");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/payments/manual", new { orderNumber = other.Number, trackingToken = other.Token, reference = "TX7788ABC" }), "PAYMENT.REFERENCE_REUSED");

        // A proof file (a PNG here); a text file is refused.
        Assert.Equal(HttpStatusCode.OK, (await _p.UploadProofAsync(paymentId, order.Token, ReturnProofPng(), "image/png")).StatusCode);
        await StoreHelper.ExpectCodeAsync(await _p.UploadProofAsync(paymentId, order.Token, System.Text.Encoding.UTF8.GetBytes("hello"), "text/plain"), "PAYMENT.PROOF_INVALID");

        // The review queue.
        var queue = await H.JsonAsync(HttpMethod.Get, "/api/v1/payments?status=UnderReview", HttpStatusCode.OK, bearer: admin);
        Assert.Contains(queue.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == paymentId && x.GetProperty("hasProof").GetBoolean() && Str(x, "submittedReference") == "TX7788ABC");
        var detail = await _p.PaymentAsync(admin, paymentId);
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("proofUrl").ValueKind);

        // The order cannot be confirmed while the transfer is unchecked; approval pays it and (default) confirms the order.
        await StoreHelper.ExpectCodeAsync(await _p.O.TryActAsync(admin, order.OrderId, "confirm"), "ORDER.PAYMENT_PENDING");
        var approved = await _p.StaffPostAsync(admin, $"/api/v1/payments/{paymentId}/approve", new { note = "Seen in the bKash app" });
        Assert.Equal("Paid", Str(approved, "status"));
        Assert.Equal("Paid", Str(approved, "orderPaymentStatus"));
        Assert.Equal("Confirmed", Str(approved, "orderStatus"));
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/{paymentId}/approve"), "PAYMENT.NOT_UNDER_REVIEW");
        await _p.SettingsAsync(admin, new { methods = new[] { new { code = "MANUAL", enabled = false } } });
    }

    [Fact]
    public async Task Rejecting_a_manual_transfer_rejects_the_payment_and_cancels_the_order_and_frees_its_stock()
    {
        var (admin, product) = await _p.ProductAsync(stock: 5);
        await EnableManualAsync(admin);
        var order = await _p.PlaceAsync(product.Variants[0].Id, 2, "MANUAL");
        Assert.Equal((5, 2), await _p.O.StockAsync(product.Variants[0].Id));
        var submitted = await TestApi.ExpectAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/payments/manual", new { orderNumber = order.Number, trackingToken = order.Token, reference = "FAKE-REF-1" }), HttpStatusCode.OK);
        var paymentId = submitted.GetProperty("paymentId").GetGuid();

        Assert.Equal(HttpStatusCode.BadRequest, (await _p.TryStaffPostAsync(admin, $"/api/v1/payments/{paymentId}/reject", new { reason = "" })).StatusCode);
        var rejected = await _p.StaffPostAsync(admin, $"/api/v1/payments/{paymentId}/reject", new { reason = "No such transaction" });

        Assert.Equal("Rejected", Str(rejected, "status"));
        Assert.Equal("Rejected", Str(rejected, "orderPaymentStatus"));
        Assert.Equal("Cancelled", Str(rejected, "orderStatus"));
        Assert.Equal((5, 0), await _p.O.StockAsync(product.Variants[0].Id));
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/{paymentId}/reject", new { reason = "again" }), "PAYMENT.NOT_UNDER_REVIEW");
        await _p.SettingsAsync(admin, new { methods = new[] { new { code = "MANUAL", enabled = false } } });
    }

    private static byte[] ReturnProofPng() => Returns.ReturnHelper.Picture(300, 300);

    // ---- settings ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_cash_on_delivery_limit_is_a_setting()
    {
        var (admin, product) = await _p.ProductAsync(price: 500m);
        await _p.SettingsAsync(admin, new { codMaxAmount = 100 });
        try
        {
            var cart = await _p.O.CartAsync(product.Variants[0].Id, 1);
            await StoreHelper.ExpectCodeAsync(await _p.O.TryCheckoutAsync(cart, StoreHelper.NewMobile()), "ORDER.COD_NOT_AVAILABLE");
            var quote = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/checkout/quote", HttpStatusCode.OK,
                new { address = new { countryCode = "BD", districtId = await _p.O.DistrictAsync() }, paymentMethod = "COD" }, cartToken: cart);
            var cod = quote.GetProperty("paymentMethods").EnumerateArray().Single(m => Str(m, "code") == "COD");
            Assert.False(cod.GetProperty("available").GetBoolean());
            Assert.Contains("limited to BDT 100", Str(cod, "reason"), StringComparison.Ordinal);
        }
        finally
        {
            await _p.SettingsAsync(admin, new { codMaxAmount = 0 });
        }

        Assert.Equal(HttpStatusCode.Created, (await _p.O.TryCheckoutAsync(await _p.O.CartAsync(product.Variants[0].Id, 1), StoreHelper.NewMobile())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await H.SendAsync(HttpMethod.Put, "/api/v1/payments/settings", new { methods = new[] { new { code = "BITCOIN", enabled = true } } }, admin)).StatusCode);
    }

    // ---- advance and cash on delivery --------------------------------------------------------------------

    [Fact]
    public async Task An_advance_recorded_as_a_payment_lets_a_large_cash_on_delivery_order_be_confirmed()
    {
        var (admin, product) = await _p.ProductAsync(price: 12000m);
        await H.JsonAsync(HttpMethod.Put, "/api/v1/fraud/rules/HIGH_COD_NEW_CUSTOMER", HttpStatusCode.OK, new { enabled = true, threshold = 5000, action = "Flag" }, admin); // flag only: a held order could not be confirmed
        var placed = await _p.O.PlaceAsync(product.Variants[0].Id, 1);
        var id = placed.GetProperty("orderId").GetGuid();
        var required = placed.GetProperty("advanceRequired").GetDecimal();
        Assert.True(required > 0);
        await StoreHelper.ExpectCodeAsync(await _p.O.TryActAsync(admin, id, "confirm"), "ORDER.ADVANCE_REQUIRED");

        // Too much, wrong method of order, and a part of the advance are all handled.
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/orders/{id}/advance", new { amount = 99999m, method = "CASH" }), "PAYMENT.AMOUNT_MISMATCH");
        Assert.Equal(HttpStatusCode.BadRequest, (await _p.TryStaffPostAsync(admin, $"/api/v1/payments/orders/{id}/advance", new { amount = 0m })).StatusCode);
        var part = await _p.StaffPostAsync(admin, $"/api/v1/payments/orders/{id}/advance", new { amount = required - 1m, method = "BKASH", reference = "ADV-1" });
        Assert.Equal("Advance", Str(part, "kind"));
        Assert.Equal("Paid", Str(part, "status"));
        await StoreHelper.ExpectCodeAsync(await _p.O.TryActAsync(admin, id, "confirm"), "ORDER.ADVANCE_REQUIRED"); // still short by 1

        await _p.StaffPostAsync(admin, $"/api/v1/payments/orders/{id}/advance", new { amount = 1m, method = "BKASH", reference = "ADV-2" });
        var confirmed = await _p.O.ActAsync(admin, id, "confirm"); // no advanceReceived flag needed any more
        Assert.Equal("Confirmed", Str(confirmed, "status"));
        Assert.Equal(2, (await _p.ForOrderAsync(admin, id)).GetArrayLength());

        // A cash-on-delivery order that needs no advance has nothing to record.
        var small = await _p.O.PlaceAsync(product.Variants[0].Id, 1);
        var smallId = small.GetProperty("orderId").GetGuid();
        if (small.GetProperty("advanceRequired").GetDecimal() == 0m)
        {
            await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/orders/{smallId}/advance", new { amount = 10m }), "PAYMENT.NOT_COD");
        }
    }

    [Fact]
    public async Task Cash_collected_is_recorded_and_then_settled_once()
    {
        var (admin, product) = await _p.ProductAsync(price: 800m);
        var placed = await _p.O.PlaceAsync(product.Variants[0].Id, 1);
        var id = placed.GetProperty("orderId").GetGuid();
        var total = placed.GetProperty("grandTotal").GetDecimal();
        await _p.O.ActAsync(admin, id, "confirm");

        // Not before the order is out for delivery.
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/orders/{id}/cod-collect", new { reference = "C-1" }), "PAYMENT.NOTHING_TO_COLLECT");
        await _p.O.ActAsync(admin, id, "pack");
        foreach (var step in new[] { "HandedToCourier", "InTransit", "OutForDelivery" })
        {
            await _p.O.ActAsync(admin, id, "transition", new { to = step });
        }

        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/orders/{id}/cod-collect", new { amount = total + 1m }), "PAYMENT.AMOUNT_MISMATCH");
        var collected = await _p.StaffPostAsync(admin, $"/api/v1/payments/orders/{id}/cod-collect", new { amount = total, reference = "PATHAO-RCPT-9" });
        Assert.Equal("CollectedByCourier", Str(collected, "status"));
        Assert.Equal("Cod", Str(collected, "kind"));
        Assert.Equal(total, collected.GetProperty("amount").GetDecimal());
        Assert.Equal("CollectedByCourier", Str(await _p.OrderAsync(admin, id), "paymentStatus"));

        // Delivery after the cash was recorded does not collect twice.
        await _p.O.ActAsync(admin, id, "transition", new { to = "Delivered" });
        Assert.Equal("CollectedByCourier", Str(await _p.OrderAsync(admin, id), "paymentStatus"));
        Assert.Single((await _p.ForOrderAsync(admin, id)).EnumerateArray());

        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/orders/{id}/cod-settle", new { reference = "" }), "GENERAL.VALIDATION");
        var settled = await _p.StaffPostAsync(admin, $"/api/v1/payments/orders/{id}/cod-settle", new { reference = "DEPOSIT-2611-77" });
        Assert.Equal("Settled", Str(settled, "status"));
        Assert.Equal("Settled", Str(settled, "orderPaymentStatus"));
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/orders/{id}/cod-settle", new { reference = "AGAIN" }), "PAYMENT.INVALID_TRANSITION");
        var moves = (await _p.PaymentAsync(admin, settled.GetProperty("id").GetGuid())).GetProperty("events").EnumerateArray()
            .Where(e => e.GetProperty("from").ValueKind == JsonValueKind.String && Str(e, "from") != Str(e, "to")).Select(e => $"{Str(e, "from")}>{Str(e, "to")}").ToArray();
        Assert.Equal(["Pending>CollectedByCourier", "CollectedByCourier>Settled"], moves); // the append-only events list the moves in order
    }

    [Fact]
    public async Task A_cash_order_delivered_without_a_record_still_settles_and_gets_its_payment_record()
    {
        var (admin, product) = await _p.ProductAsync(price: 600m);
        var delivered = await _p.DeliverCodAsync(admin, product.Variants[0].Id);
        Assert.Equal("CollectedByCourier", Str(await _p.OrderAsync(admin, delivered.OrderId), "paymentStatus")); // the order moved on its own at delivery

        var settled = await _p.StaffPostAsync(admin, $"/api/v1/payments/orders/{delivered.OrderId}/cod-settle", new { reference = "BANK-DEP-5" });
        Assert.Equal("Settled", Str(settled, "status"));
        var ledger = await H.JsonAsync(HttpMethod.Get, "/api/v1/payments/ledger", HttpStatusCode.OK, bearer: admin);
        Assert.True(ledger.GetProperty("codSettled").GetDecimal() >= 600m);
    }

    // ---- refunds -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Refunds_give_back_part_then_all_of_a_payment_and_never_more_than_was_paid()
    {
        var (admin, product) = await _p.ProductAsync(price: 1000m);
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var session = await _p.PayAsync(order);
        var paymentId = session.GetProperty("paymentId").GetGuid();
        var total = order.Total;

        var first = await _p.RefundAsync(admin, paymentId, 100m);
        Assert.Equal("Completed", Str(first, "status")); // the sandbox gateway refunds at once
        Assert.Equal("Gateway", Str(first, "mode"));
        Assert.StartsWith("SBX-RF-", Str(first, "reference"), StringComparison.Ordinal);
        Assert.StartsWith("CN-HO-", Str(first, "creditNoteNumber"), StringComparison.Ordinal);
        var afterFirst = await _p.PaymentAsync(admin, paymentId);
        Assert.Equal("PartiallyRefunded", Str(afterFirst, "status"));
        Assert.Equal(100m, afterFirst.GetProperty("refundedAmount").GetDecimal());
        Assert.Equal("PartiallyRefunded", Str(await _p.OrderAsync(admin, order.OrderId), "paymentStatus"));

        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/{paymentId}/refunds", new { amount = total, mode = "Gateway", reason = "Too much" }), "PAYMENT.REFUND_EXCEEDS_PAID");
        var rest = await _p.RefundAsync(admin, paymentId, total - 100m);
        Assert.Equal("Completed", Str(rest, "status"));
        var done = await _p.PaymentAsync(admin, paymentId);
        Assert.Equal("Refunded", Str(done, "status"));
        Assert.Equal("Refunded", Str(await _p.OrderAsync(admin, order.OrderId), "paymentStatus"));
        Assert.Equal(0m, done.GetProperty("refundable").GetDecimal());
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/{paymentId}/refunds", new { amount = 1m, mode = "Gateway", reason = "No double refund" }), "PAYMENT.NOTHING_PAID");

        var refunds = await H.JsonAsync(HttpMethod.Get, $"/api/v1/payments/refunds?orderId={order.OrderId}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(2, refunds.GetProperty("total").GetInt32());
        // Two refunds, two credit notes, numbered one after the other.
        Assert.NotEqual(Str(first, "creditNoteNumber"), Str(rest, "creditNoteNumber"));
    }

    [Fact]
    public async Task A_manual_refund_stays_pending_until_staff_complete_it_and_a_failed_one_frees_the_money()
    {
        var (admin, product) = await _p.ProductAsync(price: 1000m);
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var paymentId = (await _p.PayAsync(order)).GetProperty("paymentId").GetGuid();

        var pending = await _p.RefundAsync(admin, paymentId, 400m, "Manual");
        Assert.Equal("Pending", Str(pending, "status"));
        Assert.Equal(JsonValueKind.Null, pending.GetProperty("creditNoteNumber").ValueKind);
        var during = await _p.PaymentAsync(admin, paymentId);
        Assert.Equal("Paid", Str(during, "status")); // nothing has been given back yet
        Assert.Equal(400m, during.GetProperty("refundPendingAmount").GetDecimal());
        Assert.Equal(order.Total - 400m, during.GetProperty("refundable").GetDecimal());

        // The same pending money cannot be asked for again.
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/{paymentId}/refunds", new { amount = order.Total, mode = "Manual", reason = "x" }), "PAYMENT.REFUND_EXCEEDS_PAID");

        var refundId = pending.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.BadRequest, (await _p.TryStaffPostAsync(admin, $"/api/v1/payments/refunds/{refundId}/complete", new { reference = "" })).StatusCode);
        var completed = await _p.StaffPostAsync(admin, $"/api/v1/payments/refunds/{refundId}/complete", new { reference = "BANK-REF-123" });
        Assert.Equal("Completed", Str(completed, "status"));
        Assert.Equal("BANK-REF-123", Str(completed, "reference"));
        Assert.StartsWith("CN-HO-", Str(completed, "creditNoteNumber"), StringComparison.Ordinal);
        Assert.Equal("PartiallyRefunded", Str(await _p.PaymentAsync(admin, paymentId), "status"));
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/refunds/{refundId}/complete", new { reference = "AGAIN" }), "PAYMENT.REFUND_NOT_PENDING");
        await StoreHelper.ExpectCodeAsync(await _p.TryStaffPostAsync(admin, $"/api/v1/payments/refunds/{refundId}/fail", new { reason = "late" }), "PAYMENT.REFUND_NOT_PENDING");

        var second = await _p.RefundAsync(admin, paymentId, 600m, "Manual");
        var failed = await _p.StaffPostAsync(admin, $"/api/v1/payments/refunds/{second.GetProperty("id").GetGuid()}/fail", new { reason = "Bank returned the transfer" });
        Assert.Equal("Failed", Str(failed, "status"));
        var after = await _p.PaymentAsync(admin, paymentId);
        Assert.Equal(order.Total - 400m, after.GetProperty("refundable").GetDecimal()); // the 600 can be refunded again
        Assert.Equal(0m, after.GetProperty("refundPendingAmount").GetDecimal());
        Assert.Equal("Pending", Str(await _p.RefundAsync(admin, paymentId, 600m, "Manual"), "status")); // and it can really be asked for again
    }

    [Fact]
    public async Task Two_refunds_at_the_same_moment_never_give_back_more_than_was_paid()
    {
        var (admin, product) = await _p.ProductAsync(price: 1000m);
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var paymentId = (await _p.PayAsync(order)).GetProperty("paymentId").GetGuid();

        var attempts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => _p.TryStaffPostAsync(admin, $"/api/v1/payments/{paymentId}/refunds", new { amount = order.Total, mode = "Manual", reason = "Race" })));

        Assert.Equal(1, attempts.Count(a => a.StatusCode == HttpStatusCode.Created));
        Assert.All(attempts.Where(a => a.StatusCode != HttpStatusCode.Created), a => Assert.Equal(HttpStatusCode.Conflict, a.StatusCode));
        var row = await _p.DbAsync(db => db.Payments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().SingleAsync(p => p.Id == paymentId, Ct));
        Assert.Equal(order.Total, row.RefundPendingAmount + row.RefundedAmount);
        Assert.Equal(1, await _p.DbAsync(db => db.Refunds.IgnoreQueryFilters(["BranchScope"]).CountAsync(r => r.PaymentId == paymentId, Ct)));
    }

    [Fact]
    public async Task Staff_complete_the_refund_of_a_cash_order_that_was_delivered_and_the_payment_record_is_created_for_it()
    {
        var (admin, product) = await _p.ProductAsync(price: 700m);
        var delivered = await _p.DeliverCodAsync(admin, product.Variants[0].Id);
        var payments = await _p.ForOrderAsync(admin, delivered.OrderId);
        Assert.Equal(0, payments.GetArrayLength()); // no payment record yet: the order's own track moved when it was delivered

        // A cash payment record appears as soon as money has to be tracked; a refund needs one. Collect first (records the cash), then refund part of it.
        var collected = await _p.StaffPostAsync(admin, $"/api/v1/payments/orders/{delivered.OrderId}/cod-collect", new { reference = "R-1" });
        var refund = await _p.RefundAsync(admin, collected.GetProperty("id").GetGuid(), 200m, "Manual");
        await _p.StaffPostAsync(admin, $"/api/v1/payments/refunds/{refund.GetProperty("id").GetGuid()}/complete", new { reference = "CASH-BACK-1" });
        Assert.Equal("PartiallyRefunded", Str(await _p.OrderAsync(admin, delivered.OrderId), "paymentStatus"));
    }

    // ---- permissions and branches ------------------------------------------------------------------------

    [Fact]
    public async Task Payment_permissions_are_enforced_per_action_and_per_branch()
    {
        var (admin, product) = await _p.ProductAsync(price: 1000m);
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var paymentId = (await _p.PayAsync(order)).GetProperty("paymentId").GetGuid();

        var viewer = await _p.O.StaffTokenAsync(admin, null, "payments.payment.view");
        var orderManager = await _p.O.StaffTokenAsync(admin, null, "payments.payment.view", "payments.payment.record");
        var nobody = await _p.O.StaffTokenAsync(admin, null, "catalog.product.view");

        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, $"/api/v1/payments/{paymentId}", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, $"/api/v1/payments/{paymentId}", null, nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _p.TryStaffPostAsync(viewer, $"/api/v1/payments/{paymentId}/refunds", new { amount = 10m, mode = "Manual", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _p.TryStaffPostAsync(orderManager, $"/api/v1/payments/{paymentId}/refunds", new { amount = 10m, mode = "Manual", reason = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _p.TryStaffPostAsync(orderManager, $"/api/v1/payments/{paymentId}/approve")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/payments/ledger", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Put, "/api/v1/payments/settings", new { codMaxAmount = 5 }, orderManager)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, $"/api/v1/payments/{paymentId}", null)).StatusCode);

        // A person who works in another branch cannot see or change this branch's payments (it looks like "not found").
        var branch = await H.Api.CreateBranchAsync(admin, "PY" + Random.Shared.Next(10, 99), "Other Payments Branch");
        var elsewhere = await _p.O.StaffTokenAsync(admin, branch, "payments.payment.view", "payments.refund.create", "payments.refund.approve");
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/payments/{paymentId}", null, elsewhere)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _p.TryStaffPostAsync(elsewhere, $"/api/v1/payments/{paymentId}/refunds", new { amount = 10m, mode = "Manual", reason = "x" })).StatusCode);
        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/payments", HttpStatusCode.OK, bearer: elsewhere);
        Assert.DoesNotContain(list.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == paymentId);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/payments/ledger", null, elsewhere)).StatusCode); // no ledger permission there either
    }

    [Fact]
    public async Task The_ledger_adds_up_online_money_refunds_and_what_is_still_open()
    {
        var (admin, product) = await _p.ProductAsync(price: 900m);
        var before = await H.JsonAsync(HttpMethod.Get, "/api/v1/payments/ledger", HttpStatusCode.OK, bearer: admin);
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var paymentId = (await _p.PayAsync(order)).GetProperty("paymentId").GetGuid();
        await _p.RefundAsync(admin, paymentId, 150m, "Gateway");
        await _p.RefundAsync(admin, paymentId, 50m, "Manual"); // stays pending

        var after = await H.JsonAsync(HttpMethod.Get, "/api/v1/payments/ledger", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(before.GetProperty("onlineReceived").GetDecimal() + order.Total, after.GetProperty("onlineReceived").GetDecimal());
        Assert.Equal(before.GetProperty("onlineRefunded").GetDecimal() + 150m, after.GetProperty("onlineRefunded").GetDecimal());
        Assert.Equal(before.GetProperty("pendingRefundCount").GetInt32() + 1, after.GetProperty("pendingRefundCount").GetInt32());
        Assert.Contains(after.GetProperty("byMethod").EnumerateArray(), m => Str(m, "method") == "SANDBOX" && m.GetProperty("received").GetDecimal() >= order.Total);
        Assert.Equal(HttpStatusCode.BadRequest, (await H.SendAsync(HttpMethod.Get, "/api/v1/payments/ledger?from=2026-12-01T00:00:00Z&to=2026-11-01T00:00:00Z", null, admin)).StatusCode);
    }

    // ---- the payment hold keeps applying ------------------------------------------------------------------

    [Fact]
    public async Task The_payment_hold_timeout_still_cancels_an_unpaid_online_order_after_a_failed_attempt()
    {
        var (_, product) = await _p.ProductAsync(stock: 5);
        var order = await _p.PlaceAsync(product.Variants[0].Id, 2);
        var session = await _p.InitiateAsync(order);
        await _p.DecideAsync(Str(session, "sandboxSessionId"), "fail");
        await _p.DbAsync(async db => await db.Orders.IgnoreQueryFilters(["BranchScope"]).Where(o => o.Id == order.OrderId)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.PaymentHoldExpiresAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));

        using var scope = factory.Services.CreateScope();
        var cancelled = await scope.ServiceProvider.GetRequiredService<Juvo.Application.Orders.OrderTimeoutService>().ExpireAsync(Ct);

        Assert.True(cancelled >= 1);
        Assert.Equal((5, 0), await _p.O.StockAsync(product.Variants[0].Id));
        Assert.Equal("Cancelled", Str(await _p.StatusAsync(session.GetProperty("paymentId").GetGuid(), order.Token), "orderStatus"));
        await StoreHelper.ExpectCodeAsync(await _p.TryInitiateAsync(order), "PAYMENT.ORDER_NOT_PAYABLE");
    }
}
