using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using SkiaSharp;

namespace Juvo.Api.IntegrationTests.Returns;

/// <summary>A delivered cash-on-delivery order and the customer who owns it.</summary>
public sealed record DeliveredOrder(Guid OrderId, string Number, string Mobile, string Customer, Guid VariantId, decimal DeliveryCharge);

/// <summary>Drives the real customer and staff returns API over HTTP on top of the order helpers.</summary>
public sealed class ReturnHelper(OrderHelper orders)
{
    public OrderHelper O => orders;

    public StoreHelper H => orders.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<(string Admin, SeededProduct Product)> ProductAsync(decimal price = 500m, params (string Colour, string Size, int Stock)[] variants)
    {
        var admin = await H.AdminAsync();
        var category = await H.CategoryAsync(admin);
        var list = variants.Length == 0 ? [("Red", "M", 10)] : variants;
        var product = await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Return Tee"), price, list, weightGrams: 300);
        return (admin, product);
    }

    /// <summary>Places, confirms, packs and delivers a cash-on-delivery order; the customer signs in with the same mobile.</summary>
    public async Task<DeliveredOrder> DeliverAsync(string admin, Guid variantId, int quantity)
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

        var detail = await orders.OrderAsync(admin, id);
        return new DeliveredOrder(id, placed.GetProperty("number").GetString()!, mobile, await H.CustomerTokenAsync(mobile), variantId,
            detail.GetProperty("money").GetProperty("deliveryCharge").GetDecimal());
    }

    public Task<JsonElement> EligibilityAsync(DeliveredOrder d) =>
        H.JsonAsync(HttpMethod.Get, $"/api/v1/me/orders/{d.OrderId}/return-eligibility", HttpStatusCode.OK, bearer: d.Customer);

    /// <summary>The order line ids (they are only exposed by the eligibility answer).</summary>
    public async Task<List<Guid>> ItemIdsAsync(DeliveredOrder d) =>
        [.. (await EligibilityAsync(d)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("orderItemId").GetGuid())];

    public Task<HttpResponseMessage> TryRequestAsync(DeliveredOrder d, object[] items, string reason = "SIZE_FIT", string kind = "Return", string[]? photos = null, string? key = null, string? bearer = null) =>
        H.SendAsync(HttpMethod.Post, "/api/v1/me/returns", new { orderId = d.OrderId, kind, reasonCode = reason, details = "Does not fit", items, photoPaths = photos, idempotencyKey = key }, bearer ?? d.Customer);

    public async Task<JsonElement> RequestAsync(DeliveredOrder d, int quantity = 1, string reason = "SIZE_FIT", string? key = null)
    {
        var item = (await ItemIdsAsync(d))[0];
        return await TestApi.ExpectAsync(await TryRequestAsync(d, [new { orderItemId = item, quantity }], reason, key: key), HttpStatusCode.Created);
    }

    public Task<JsonElement> ActAsync(string staff, Guid returnId, string action, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        H.JsonAsync(HttpMethod.Post, $"/api/v1/returns/{returnId}/{action}", expected, body ?? new { }, staff);

    public Task<HttpResponseMessage> TryActAsync(string staff, Guid returnId, string action, object? body = null) =>
        H.SendAsync(HttpMethod.Post, $"/api/v1/returns/{returnId}/{action}", body ?? new { }, staff);

    public Task<JsonElement> DetailAsync(string staff, Guid returnId) => H.JsonAsync(HttpMethod.Get, $"/api/v1/returns/{returnId}", HttpStatusCode.OK, bearer: staff);

    public static object Pickup() => new { date = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6)).AddDays(1).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), courier = "Pathao", note = "Call before coming" };

    /// <summary>Approve, schedule the pickup, mark picked up and received.</summary>
    public async Task ToReceivedAsync(string admin, Guid returnId)
    {
        await ActAsync(admin, returnId, "approve");
        await ActAsync(admin, returnId, "schedule-pickup", Pickup());
        await ActAsync(admin, returnId, "picked-up");
        await ActAsync(admin, returnId, "received");
    }

    public async Task<JsonElement> InspectAsync(string admin, Guid returnId, string condition = "Good", bool restock = true, string? fault = null, bool? acceptRefund = null)
    {
        var detail = await DetailAsync(admin, returnId);
        var items = detail.GetProperty("items").EnumerateArray().Select(i => new { returnItemId = i.GetProperty("id").GetGuid(), condition, restock, acceptRefund }).ToArray();
        return await ActAsync(admin, returnId, "inspect", new { items, fault, note = "Checked at the counter" });
    }

    public async Task<JsonElement> ToInspectedAsync(string admin, Guid returnId, string condition = "Good", bool restock = true, string? fault = null)
    {
        await ToReceivedAsync(admin, returnId);
        return await InspectAsync(admin, returnId, condition, restock, fault);
    }

    public Task<JsonElement> RefundAsync(string admin, Guid returnId, decimal? amount = null, string method = "Cash", string reference = "RCPT-1") =>
        ActAsync(admin, returnId, "refund", new { method, reference, amount });

    public static byte[] Picture(int width = 1200, int height = 1200)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 80).ToArray();
    }

    public async Task<HttpResponseMessage> UploadPhotoAsync(string customer, byte[] picture)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(picture);
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", "evidence.png");
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/me/returns/photos") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", customer);
        return await H.Api.Client.SendAsync(request, Ct);
    }

    public Task BackdateDeliveryAsync(Guid orderId, int days) =>
        orders.WithDbAsync(async db => await db.Orders.IgnoreQueryFilters(["BranchScope"]).Where(o => o.Id == orderId)
            .ExecuteUpdateAsync(set => set.SetProperty(o => o.DeliveredAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddDays(-days)), Ct));

    /// <summary>Sets a platform setting for the length of one test; the caller removes it in a finally block.</summary>
    public Task SetSettingAsync(string key, string value) =>
        orders.WithDbAsync(async db =>
        {
            db.PlatformSettings.Add(new PlatformSetting(key, value));
            await db.SaveChangesAsync(Ct);
            return 0;
        });

    public Task ClearSettingAsync(string key) =>
        orders.WithDbAsync(async db => await db.PlatformSettings.Where(s => s.Key == key).ExecuteDeleteAsync(Ct));
}
