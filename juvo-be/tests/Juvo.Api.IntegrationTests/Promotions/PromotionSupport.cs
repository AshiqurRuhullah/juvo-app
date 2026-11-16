using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using SkiaSharp;

namespace Juvo.Api.IntegrationTests.Promotions;

/// <summary>A shop whose clock the tests control (promotion windows, flash sales). Tests that move the clock keep the move small, so access tokens stay valid.</summary>
public class PromoFactory : StoreFactory
{
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureTestServices(IServiceCollection services) => services.AddSingleton<TimeProvider>(Time);
}

/// <summary>Builds promotions through the real staff API and prices carts through the real shop API.</summary>
public sealed class PromoHelper(OrderHelper orders, PromoFactory factory)
{
    public OrderHelper O => orders;

    public StoreHelper H => orders.H;

    public DateTimeOffset Now => factory.Time.GetUtcNow();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static decimal Num(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    public static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    public async Task<(string Admin, SeededProduct Product)> ProductAsync(decimal price, int stock = 10, decimal? salePrice = null, string? name = null, int? weightGrams = 300)
    {
        var admin = await H.AdminAsync();
        var category = await H.CategoryAsync(admin);
        var product = await H.ProductAsync(admin, category.Id, name ?? StoreHelper.Unique("Promo Tee"), price, [("Red", "M", stock)], salePrice: salePrice, weightGrams: weightGrams);
        return (admin, product);
    }

    public async Task<SeededProduct> MoreProductAsync(string admin, decimal price, int stock = 10, string? name = null, Guid? categoryId = null)
    {
        var category = categoryId ?? (await H.CategoryAsync(admin)).Id;
        return await H.ProductAsync(admin, category, name ?? StoreHelper.Unique("Promo Item"), price, [("Blue", "L", stock)], weightGrams: 300);
    }

    public static object Target(string kind, Guid? id, string role = "Main", int quantity = 1) => new { kind, targetId = id, role, quantity };

    public static object ProductTarget(SeededProduct p, string role = "Main", int quantity = 1) => Target("Product", p.Id, role, quantity);

    public async Task<JsonElement> CreateAsync(string admin, object body, HttpStatusCode expected = HttpStatusCode.Created) =>
        await H.JsonAsync(HttpMethod.Post, "/api/v1/promotions", expected, body, admin);

    public async Task<JsonElement> ActivateAsync(string admin, Guid id) => await H.JsonAsync(HttpMethod.Post, $"/api/v1/promotions/{id}/activate", HttpStatusCode.OK, new { }, admin);

    /// <summary>Creates and switches on a promotion, returning its detail.</summary>
    public async Task<JsonElement> LiveAsync(string admin, object body)
    {
        var created = await CreateAsync(admin, body);
        return await ActivateAsync(admin, created.GetProperty("id").GetGuid());
    }

    public static object ProductDiscount(string name, string kind, decimal value, IEnumerable<object> targets, string stackMode = "StacksWithOrderPromotions", int priority = 0, DateTimeOffset? starts = null, DateTimeOffset? ends = null,
        string? channels = null, decimal? minCart = null, int? usageLimit = null) =>
        new { nameEn = name, nameBn = name + " বাংলা", type = "ProductDiscount", startsAt = starts, endsAt = ends, priority, stackMode, channels, minCartValue = minCart, usageLimit, discountKind = kind, discountValue = value, targets = targets.ToArray() };

    public static object FlashSale(string name, string kind, decimal value, IEnumerable<object> targets, DateTimeOffset starts, DateTimeOffset ends, string stackMode = "StacksWithOrderPromotions", int priority = 0) =>
        new { nameEn = name, nameBn = name + " বাংলা", type = "FlashSale", startsAt = starts, endsAt = ends, priority, stackMode, discountKind = kind, discountValue = value, targets = targets.ToArray() };

    public static object Bundle(string name, decimal price, params object[] items) => new { nameEn = name, type = "Bundle", bundlePrice = price, targets = items };

    public static object BuyXGetY(string name, int buy, int get, decimal percent, params object[] targets) =>
        new { nameEn = name, type = "BuyXGetY", buyQuantity = buy, getQuantity = get, getDiscountPercent = percent, targets };

    public static object FreeDelivery(string name, decimal min, string? stackMode = null) => new { nameEn = name, nameBn = name + " বাংলা", type = "FreeDelivery", minCartValue = min, stackMode = stackMode ?? "StacksWithCoupon" };

    // ---- the shop ----------------------------------------------------------------------------------------

    public Task<JsonElement> CartAsync(string token) => H.JsonAsync(HttpMethod.Get, "/api/v1/store/cart", HttpStatusCode.OK, cartToken: token);

    public async Task<JsonElement> QuoteAsync(string token, string method = "COD") =>
        await H.JsonAsync(HttpMethod.Post, "/api/v1/store/checkout/quote", HttpStatusCode.OK, new { address = new { countryCode = "BD", districtId = await O.DistrictAsync(), recipientName = "N", addressLine = "x" }, paymentMethod = method }, cartToken: token);

    public Task<JsonElement> ApplyCouponAsync(string token, string code) => H.JsonAsync(HttpMethod.Post, "/api/v1/store/cart/coupon", HttpStatusCode.OK, new { code }, cartToken: token);

    /// <summary>A guest cart holding one line per (variant, quantity); the token comes back.</summary>
    public async Task<string> CartOfAsync(params (Guid Variant, int Quantity)[] lines)
    {
        var token = await O.CartAsync(lines[0].Variant, lines[0].Quantity);
        foreach (var (variant, quantity) in lines.Skip(1))
        {
            await O.AddToCartAsync(token, variant, quantity);
        }

        return token;
    }

    public async Task<JsonElement> PlaceCartAsync(string token, string? mobile = null, decimal? expected = null, string method = "COD", string? coupon = null) =>
        await TestApi.ExpectAsync(await O.TryCheckoutAsync(token, mobile ?? StoreHelper.NewMobile(), new CheckoutOptions(Method: method, Expected: expected, Coupon: coupon)), HttpStatusCode.Created);

    public async Task DrainOutboxAsync()
    {
        var processor = factory.Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    public static byte[] Picture(int width = 1200, int height = 1200)
    {
        using var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Crimson);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 80).ToArray();
    }

    public async Task<HttpResponseMessage> UploadAsync(string admin, Guid promotionId, string kind)
    {
        var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Picture());
        file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
        form.Add(file, "file", kind + ".png");
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/promotions/{promotionId}/images/{kind}") { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
        return await H.Api.Client.SendAsync(request, Ct);
    }
}
