using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using SkiaSharp;

namespace Juvo.Api.IntegrationTests.Storefront;

/// <summary>A shop with sandbox sign-in codes (the response carries the code), so customers can sign in without a provider.</summary>
public class StoreFactory : ApiFactory
{
    protected override void ConfigureSettings()
    {
        Setting("Auth:Otp:ShowCodeInResponse", "true");
        Setting("Auth:Otp:ResendCooldownSeconds", "0");
        Setting("Auth:Otp:MaxRequestsPerHourPerMobile", "100");
        ExtraSettings();
    }

    protected virtual void ExtraSettings()
    {
    }
}

/// <summary>Only the Head Office branch sells online (settings: Storefront:FulfilmentBranchCodes).</summary>
public sealed class HeadOfficeOnlyStoreFactory : StoreFactory
{
    protected override void ExtraSettings() => Setting("Storefront:FulfilmentBranchCodes:0", "HO");
}

public sealed record SeededVariant(Guid Id, string Sku, string Colour, string Size);

public sealed record SeededProduct(Guid Id, string Slug, string Name, Guid CategoryId, IReadOnlyList<SeededVariant> Variants);

/// <summary>Builds real data through the admin API (category, product, variants, a picture, stock) and signs customers in.</summary>
public sealed class StoreHelper(ApiFactory factory)
{
    private readonly TestApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public TestApi Api => _api;

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..(prefix.Length + 9)];

    public static string NewMobile() => "017" + Random.Shared.Next(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async Task<string> AdminAsync() => (await _api.LoginAdminAsync()).Access;

    public async Task<(Guid Id, string Slug)> CategoryAsync(string admin, string? name = null, Guid? parentId = null, bool active = true)
    {
        var json = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/categories",
            new { nameEn = name ?? Unique("Cat"), sortOrder = 0, parentId, isActive = active }, admin), HttpStatusCode.Created);
        return (json.GetProperty("id").GetGuid(), json.GetProperty("slug").GetString()!);
    }

    private static byte[] Picture()
    {
        using var bitmap = new SKBitmap(1000, 1250);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Teal);
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Png, 80).ToArray();
    }

    /// <summary>A product with the given variants (colour, size, stock in Head Office), one picture, published unless told otherwise.</summary>
    public async Task<SeededProduct> ProductAsync(
        string admin, Guid categoryId, string name, decimal price, IReadOnlyList<(string Colour, string Size, int Stock)> variants, decimal? salePrice = null, bool publish = true,
        string? brandName = null, bool isNew = false, bool isBestSeller = false, bool isFeatured = false, int? weightGrams = null, int images = 1, string? description = null)
    {
        Guid? brandId = null;
        if (brandName is not null)
        {
            var brands = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/brands?search={Uri.EscapeDataString(brandName)}", admin), HttpStatusCode.OK);
            brandId = brands.GetProperty("items").EnumerateArray().FirstOrDefault(b => b.GetProperty("nameEn").GetString() == brandName) is { ValueKind: JsonValueKind.Object } found
                ? found.GetProperty("id").GetGuid()
                : (await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/brands", new { nameEn = brandName }, admin), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        }

        var product = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/products", new
        {
            nameEn = name, categoryId, brandId, regularPrice = price, salePrice, targetGroup = "Unisex", isNew, isFeatured, isBestSeller, weightGrams, description,
            shortDescription = "Tagline of " + name,
        }, admin), HttpStatusCode.Created);
        var id = product.GetProperty("id").GetGuid();
        var slug = product.GetProperty("slug").GetString()!;

        var ho = await _api.BranchIdAsync("HO");
        var seeded = new List<SeededVariant>();
        var prefix = "S" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var order = 0;
        foreach (var (colour, size, stock) in variants)
        {
            var sku = $"{prefix}-{colour[..Math.Min(3, colour.Length)].ToUpperInvariant()}-{size.ToUpperInvariant()}";
            var variant = await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{id}/variants",
                new { sku, colorName = colour, colorHex = "#336699", size, sortOrder = order++ }, admin), HttpStatusCode.Created);
            var variantId = variant.GetProperty("id").GetGuid();
            seeded.Add(new SeededVariant(variantId, sku, colour, size));
            if (stock > 0)
            {
                await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/inventory/adjustments",
                    new { branchId = ho, reason = "OpeningStock", lines = new[] { new { variantId, quantityChange = stock } } }, admin), HttpStatusCode.OK);
            }
        }

        for (var i = 0; i < images; i++)
        {
            var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(Picture());
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(file, "file", "photo.png");
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/catalog/products/{id}/images") { Content = form };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", admin);
            var response = await _api.Client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }

        if (publish)
        {
            await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{id}/publish", null, admin), HttpStatusCode.OK);
        }

        return new SeededProduct(id, slug, name, categoryId, seeded);
    }

    /// <summary>Signs a customer in with the sandbox code and returns the access token.</summary>
    public async Task<string> CustomerTokenAsync(string? mobile = null)
    {
        mobile ??= NewMobile();
        var request = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/otp/request", new { mobile }), HttpStatusCode.Accepted);
        var code = request.GetProperty("devCode").GetString()!;
        var verified = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code }), HttpStatusCode.OK);
        return verified.GetProperty("accessToken").GetString()!;
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body = null, string? bearer = null, string? cartToken = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = System.Net.Http.Json.JsonContent.Create(body);
        }

        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        if (cartToken is not null)
        {
            request.Headers.Add("X-Cart-Token", cartToken);
        }

        return await _api.Client.SendAsync(request, Ct);
    }

    public async Task<JsonElement> JsonAsync(HttpMethod method, string url, HttpStatusCode expected, object? body = null, string? bearer = null, string? cartToken = null) =>
        await TestApi.ExpectAsync(await SendAsync(method, url, body, bearer, cartToken), expected);

    /// <summary>Changes the regular price (and optionally the sale price) of a product through the admin API.</summary>
    public async Task ChangePriceAsync(string admin, SeededProduct product, decimal regularPrice, decimal? salePrice = null)
    {
        await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/catalog/products/{product.Id}", new
        {
            nameEn = product.Name, slug = product.Slug, categoryId = product.CategoryId, targetGroup = "Unisex", regularPrice, salePrice,
        }, admin), HttpStatusCode.OK);
    }

    public async Task<JsonElement> CouponAsync(string admin, string code, string kind, decimal value, decimal minOrder = 0m, decimal? maxDiscount = null,
        DateTimeOffset? startsAt = null, DateTimeOffset? endsAt = null, int? usageLimit = null, bool active = true) =>
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/promotions/coupons", new
        {
            code, kind, value, minOrderValue = minOrder, maxDiscount, startsAt, endsAt, usageLimit, isActive = active,
        }, admin), HttpStatusCode.Created);

    /// <summary>Asserts a 4xx answer that carries the given error code.</summary>
    public static async Task ExpectCodeAsync(HttpResponseMessage response, string code)
    {
        var text = await response.Content.ReadAsStringAsync(Ct);
        Assert.True((int)response.StatusCode is >= 400 and < 500, $"Expected a client error but got {(int)response.StatusCode}: {text}");
        Assert.Equal(code, JsonDocument.Parse(text).RootElement.GetProperty("code").GetString());
    }

    /// <summary>Removes (or adds) stock of one variant in Head Office through the admin API.</summary>
    public async Task SetStockChangeAsync(string admin, Guid variantId, int change, string reason = "Correction")
    {
        var ho = await _api.BranchIdAsync("HO");
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/inventory/adjustments",
            new { branchId = ho, reason, lines = new[] { new { variantId, quantityChange = change } } }, admin), HttpStatusCode.OK);
    }
}
