using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Payments;
using Juvo.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Payments;

/// <summary>The sandbox gateway signs its webhooks with a fixed secret in tests, so a test can sign (and forge) webhooks itself.</summary>
public class PaymentFactory : StoreFactory
{
    public const string WebhookSecret = "test-webhook-secret-0123456789";

    protected override void ExtraSettings() => Setting("Payments:Sandbox:WebhookSecret", WebhookSecret);
}

/// <summary>A sandbox gateway whose refunds finish later (Pending).</summary>
public sealed class PendingRefundPaymentFactory : PaymentFactory
{
    protected override void ExtraSettings()
    {
        base.ExtraSettings();
        Setting("Payments:Sandbox:RefundOutcome", "Pending");
    }
}

/// <summary>A sandbox gateway that refuses refunds.</summary>
public sealed class FailingRefundPaymentFactory : PaymentFactory
{
    protected override void ExtraSettings()
    {
        base.ExtraSettings();
        Setting("Payments:Sandbox:RefundOutcome", "Failed");
    }
}

public sealed record PlacedOnline(Guid OrderId, string Number, string Token, decimal Total, string Mobile);

/// <summary>Drives the real payment API (customer and staff) on top of the order helpers.</summary>
public sealed class PaymentHelper(OrderHelper orders)
{
    public OrderHelper O => orders;

    public StoreHelper H => orders.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<(string Admin, SeededProduct Product)> ProductAsync(decimal price = 500m, int stock = 20)
    {
        var admin = await H.AdminAsync();
        var category = await H.CategoryAsync(admin);
        var product = await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Pay Tee"), price, [("Red", "M", stock)], weightGrams: 300);
        return (admin, product);
    }

    /// <summary>Places an order paid online (the sandbox gateway by default).</summary>
    public async Task<PlacedOnline> PlaceAsync(Guid variantId, int quantity = 1, string method = "SANDBOX")
    {
        var mobile = StoreHelper.NewMobile();
        var placed = await orders.PlaceAsync(variantId, quantity, mobile, new CheckoutOptions(Method: method, SkipOtp: method != "COD"));
        return new PlacedOnline(placed.GetProperty("orderId").GetGuid(), placed.GetProperty("number").GetString()!, placed.GetProperty("trackingToken").GetString()!, placed.GetProperty("grandTotal").GetDecimal(), mobile);
    }

    public Task<HttpResponseMessage> TryInitiateAsync(PlacedOnline p, string? method = null, string? returnUrl = "/checkout/return", string? token = null, string? bearer = null) =>
        H.SendAsync(HttpMethod.Post, "/api/v1/store/payments/initiate", new { orderNumber = p.Number, trackingToken = token ?? p.Token, method, returnUrl }, bearer);

    public async Task<JsonElement> InitiateAsync(PlacedOnline p, string? method = null) =>
        await TestApi.ExpectAsync(await TryInitiateAsync(p, method), HttpStatusCode.OK);

    public Task<JsonElement> StatusAsync(Guid paymentId, string token) =>
        H.JsonAsync(HttpMethod.Get, $"/api/v1/store/payments/{paymentId}/status?token={Uri.EscapeDataString(token)}", HttpStatusCode.OK);

    public Task<HttpResponseMessage> TryConfirmAsync(Guid paymentId, string token) =>
        H.SendAsync(HttpMethod.Post, $"/api/v1/store/payments/{paymentId}/confirm?token={Uri.EscapeDataString(token)}", new { });

    public Task<JsonElement> ConfirmAsync(Guid paymentId, string token) =>
        H.JsonAsync(HttpMethod.Post, $"/api/v1/store/payments/{paymentId}/confirm?token={Uri.EscapeDataString(token)}", HttpStatusCode.OK, new { });

    /// <summary>What the customer does on the sandbox pay page.</summary>
    public Task<HttpResponseMessage> TryDecideAsync(string sessionId, string outcome, bool sendWebhook = true) =>
        H.SendAsync(HttpMethod.Post, $"/api/v1/store/payments/sandbox/sessions/{sessionId}/complete", new { outcome, sendWebhook });

    public async Task<JsonElement> DecideAsync(string sessionId, string outcome, bool sendWebhook = true) =>
        await TestApi.ExpectAsync(await TryDecideAsync(sessionId, outcome, sendWebhook), HttpStatusCode.OK);

    /// <summary>Starts a payment and pays it on the sandbox page (the sandbox then sends the signed webhook). Returns the session that was started.</summary>
    public async Task<JsonElement> PayAsync(PlacedOnline p)
    {
        var session = await InitiateAsync(p);
        await DecideAsync(session.GetProperty("sandboxSessionId").GetString()!, "success");
        return session;
    }

    public static string WebhookBody(string sessionId, string type, string? eventId = null, decimal? amount = null, string currency = "BDT", string? rawType = null) =>
        JsonSerializer.Serialize(new { id = eventId ?? "evt_" + Guid.NewGuid().ToString("N"), type = rawType ?? type, sessionId, amount, currency, created = DateTimeOffset.UtcNow.ToUnixTimeSeconds() });

    public Task<HttpResponseMessage> PostWebhookAsync(string body, string? signature, string provider = "SANDBOX")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/store/payments/webhooks/{provider}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (signature is not null)
        {
            request.Headers.Add(WebhookSignature.HeaderName, signature);
        }

        return H.Api.Client.SendAsync(request, Ct);
    }

    public static string Sign(string body, DateTimeOffset? at = null, string secret = PaymentFactory.WebhookSecret) => WebhookSignature.Sign(secret, at ?? DateTimeOffset.UtcNow, body);

    public Task<HttpResponseMessage> SignedWebhookAsync(string sessionId, string type, string? eventId = null, decimal? amount = null, DateTimeOffset? at = null, string secret = PaymentFactory.WebhookSecret)
    {
        var body = WebhookBody(sessionId, type, eventId, amount);
        return PostWebhookAsync(body, Sign(body, at, secret));
    }

    // ---- staff ----------------------------------------------------------------------------------------------

    public Task<JsonElement> PaymentAsync(string staff, Guid paymentId) => H.JsonAsync(HttpMethod.Get, $"/api/v1/payments/{paymentId}", HttpStatusCode.OK, bearer: staff);

    public Task<JsonElement> ForOrderAsync(string staff, Guid orderId) => H.JsonAsync(HttpMethod.Get, $"/api/v1/payments/orders/{orderId}", HttpStatusCode.OK, bearer: staff);

    public Task<HttpResponseMessage> TryStaffPostAsync(string staff, string url, object? body = null) => H.SendAsync(HttpMethod.Post, url, body ?? new { }, staff);

    public Task<JsonElement> StaffPostAsync(string staff, string url, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) => H.JsonAsync(HttpMethod.Post, url, expected, body ?? new { }, staff);

    public Task<JsonElement> RefundAsync(string staff, Guid paymentId, decimal amount, string mode = "Gateway", string reason = "Customer returned the goods") =>
        StaffPostAsync(staff, $"/api/v1/payments/{paymentId}/refunds", new { amount, mode, reason }, HttpStatusCode.Created);

    public Task SettingsAsync(string admin, object body) => H.JsonAsync(HttpMethod.Put, "/api/v1/payments/settings", HttpStatusCode.OK, body, admin);

    public Task<T> DbAsync<T>(Func<Juvo.Infrastructure.Persistence.JuvoDbContext, Task<T>> action) => orders.WithDbAsync(action);

    public Task<Payment> PaymentRowAsync(Guid paymentId) => DbAsync(db => db.Payments.IgnoreQueryFilters(["BranchScope"]).Include(p => p.Events).AsNoTracking().SingleAsync(p => p.Id == paymentId, Ct));

    public async Task<JsonElement> OrderAsync(string admin, Guid orderId) => await orders.OrderAsync(admin, orderId);

    public async Task<HttpResponseMessage> UploadProofAsync(Guid paymentId, string token, byte[] bytes, string contentType)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        form.Add(file, "file", "proof.bin");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/store/payments/{paymentId}/proof?token={Uri.EscapeDataString(token)}") { Content = form };
        return await H.Api.Client.SendAsync(request, Ct);
    }

    /// <summary>Delivers a cash-on-delivery order up to and including Delivered and returns its id.</summary>
    public async Task<(Guid OrderId, string Number, string Mobile)> DeliverCodAsync(string admin, Guid variantId, int quantity = 1)
    {
        var mobile = StoreHelper.NewMobile();
        var placed = await orders.PlaceAsync(variantId, quantity, mobile);
        var id = placed.GetProperty("orderId").GetGuid();
        await orders.ActAsync(admin, id, "confirm");
        await orders.ActAsync(admin, id, "pack");
        foreach (var step in new[] { "HandedToCourier", "InTransit", "OutForDelivery", "Delivered" })
        {
            await orders.ActAsync(admin, id, "transition", new { to = step });
        }

        return (id, placed.GetProperty("number").GetString()!, mobile);
    }
}
