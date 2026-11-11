using System.Net;
using System.Text;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Payments;
using Juvo.Infrastructure.Outbox;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juvo.Api.IntegrationTests.Couriers;

public sealed record PackedOrder(Guid OrderId, string Number, Guid VariantId, string Mobile, string Token, decimal Total);

/// <summary>Drives the real courier, shipment and settlement API on top of the order helpers. The sandbox courier signs its webhooks with a fixed secret in tests.</summary>
public sealed class CourierHelper(OrderHelper orders, ApiFactory factory)
{
    public const string WebhookSecret = "test-courier-webhook-secret-0123456789";

    public OrderHelper O => orders;

    public StoreHelper H => orders.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<(string Admin, SeededProduct Product)> ProductAsync(decimal price = 500m, int stock = 30)
    {
        var admin = await H.AdminAsync();
        var category = await H.CategoryAsync(admin);
        var product = await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Ship Tee"), price, [("Red", "M", stock)], weightGrams: 300);
        return (admin, product);
    }

    /// <summary>Places a cash-on-delivery order, confirms and packs it.</summary>
    public async Task<PackedOrder> PackedAsync(string admin, Guid variantId, int quantity = 1, string? mobile = null)
    {
        mobile ??= StoreHelper.NewMobile();
        var placed = await orders.PlaceAsync(variantId, quantity, mobile);
        var id = placed.GetProperty("orderId").GetGuid();
        await orders.ActAsync(admin, id, "confirm");
        await orders.ActAsync(admin, id, "pack");
        return new PackedOrder(id, placed.GetProperty("number").GetString()!, variantId, mobile, placed.GetProperty("trackingToken").GetString()!, placed.GetProperty("grandTotal").GetDecimal());
    }

    /// <summary>A packed order whose customer has signed in (same mobile), so the customer can follow it live.</summary>
    public async Task<(PackedOrder Order, string Customer)> PackedOrderForCustomerAsync(string admin, Guid variantId)
    {
        var mobile = StoreHelper.NewMobile();
        var order = await PackedAsync(admin, variantId, 1, mobile);
        return (order, await H.CustomerTokenAsync(mobile));
    }

    public Task<HttpResponseMessage> TryBookAsync(string bearer, Guid orderId, object? body = null) =>
        H.SendAsync(HttpMethod.Post, $"/api/v1/shipments/orders/{orderId}/book", body ?? new { }, bearer);

    public async Task<JsonElement> BookAsync(string bearer, Guid orderId, object? body = null) =>
        await TestApi.ExpectAsync(await TryBookAsync(bearer, orderId, body), HttpStatusCode.Created);

    public Task<JsonElement> ShipmentAsync(string bearer, Guid id) => H.JsonAsync(HttpMethod.Get, $"/api/v1/shipments/{id}", HttpStatusCode.OK, bearer: bearer);

    public Task<HttpResponseMessage> TryAdvanceAsync(string bearer, Guid id, string? status = null, string? note = null) =>
        H.SendAsync(HttpMethod.Post, $"/api/v1/shipments/{id}/advance", new { status, note }, bearer);

    public async Task<JsonElement> AdvanceAsync(string bearer, Guid id, string? status = null, string? note = null) =>
        await TestApi.ExpectAsync(await TryAdvanceAsync(bearer, id, status, note), HttpStatusCode.OK);

    /// <summary>Moves a parcel through the normal journey until it is delivered.</summary>
    public async Task DeliverAsync(string bearer, Guid shipmentId)
    {
        foreach (var _ in new[] { 1, 2, 3, 4 })
        {
            await AdvanceAsync(bearer, shipmentId);
        }
    }

    public async Task<HttpResponseMessage> WebhookAsync(string consignmentId, string status, string eventId, string? secret = null, DateTimeOffset? signedAt = null, decimal? codCollected = null,
        string? note = null, string provider = "SANDBOX", string? rawBody = null)
    {
        var body = rawBody ?? JsonSerializer.Serialize(new { eventId, consignmentId, status, occurredAt = DateTimeOffset.UtcNow, location = "Dhaka hub", note, codCollected });
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/webhooks/couriers/{provider}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.Sign(secret ?? WebhookSecret, signedAt ?? DateTimeOffset.UtcNow, body));
        return await H.Api.Client.SendAsync(request, Ct);
    }

    public Task<JsonElement> ListAsync(string bearer, string query = "") => H.JsonAsync(HttpMethod.Get, $"/api/v1/shipments{query}", HttpStatusCode.OK, bearer: bearer);

    public async Task DrainOutboxAsync()
    {
        var processor = factory.Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    public async Task<Guid> CourierIdAsync(string bearer, string code)
    {
        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/couriers", HttpStatusCode.OK, bearer: bearer);
        return list.EnumerateArray().First(c => c.GetProperty("code").GetString() == code).GetProperty("id").GetGuid();
    }

    public Task<T> DbAsync<T>(Func<JuvoDbContext, Task<T>> action) => O.WithDbAsync(action);

    public static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    public async Task<JsonElement> SettlementAsync(string bearer, Guid id) => await H.JsonAsync(HttpMethod.Get, $"/api/v1/couriers/cod-settlements/{id}", HttpStatusCode.OK, bearer: bearer);

    public async Task<string> StaffAsync(string admin, Guid? branch, params string[] permissions) => await orders.StaffTokenAsync(admin, branch, permissions);

    public async Task<int> CountOrderEventsAsync(Guid orderId, string status) =>
        await DbAsync(db => db.OrderStatusHistory.CountAsync(h => h.OrderId == orderId && h.Track == Domain.Orders.OrderTrack.Order && h.ToStatus == status, Ct));
}
