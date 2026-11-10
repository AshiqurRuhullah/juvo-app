using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Infrastructure.Catalog;
using Microsoft.Extensions.DependencyInjection;
using SkiaSharp;

namespace Juvo.Api.IntegrationTests.Catalog;

/// <summary>The demo catalog: 16 published sample products with real (generated) pictures and stock, and it never mixes with a real catalog.</summary>
public class SampleCatalogTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestApi _api = new(factory);

    private async Task<int> SeedAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SampleCatalogSeeder>().SeedAsync(Ct);
    }

    [Fact]
    public async Task The_sample_catalog_has_published_products_with_pictures_variants_and_stock_and_is_added_only_once()
    {
        var admin = (await _api.LoginAdminAsync()).Access;

        Assert.Equal(16, await SeedAsync());
        Assert.Equal(0, await SeedAsync()); // idempotent

        var list = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/catalog/products?pageSize=100&status=Published", admin), HttpStatusCode.OK);
        Assert.Equal(16, list.GetProperty("total").GetInt32());
        Assert.All(list.GetProperty("items").EnumerateArray(), p =>
        {
            Assert.True(p.GetProperty("variantCount").GetInt32() >= 1);
            Assert.True(p.GetProperty("imageCount").GetInt32() >= 1);
        });
        Assert.Contains(list.GetProperty("items").EnumerateArray(), p => p.GetProperty("salePrice").ValueKind == JsonValueKind.Number);

        // One product in detail: colour pictures through the real pipeline, a size chart, a brand, tag "sample".
        var first = list.GetProperty("items").EnumerateArray().First();
        var detail = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/products/{first.GetProperty("id").GetGuid()}", admin), HttpStatusCode.OK);
        Assert.Equal("sample", detail.GetProperty("tags").GetString());
        Assert.Empty(detail.GetProperty("publishProblems").EnumerateArray());
        var image = detail.GetProperty("images").EnumerateArray().First();
        Assert.StartsWith("data:image/webp;base64,", image.GetProperty("blurDataUrl").GetString());
        var card = await _api.Client.GetAsync(image.GetProperty("cardUrl").GetString()!, Ct);
        Assert.Equal(HttpStatusCode.OK, card.StatusCode);
        using var bitmap = SKBitmap.Decode(await card.Content.ReadAsByteArrayAsync(Ct));
        Assert.Equal((800, 1000), (bitmap.Width, bitmap.Height));

        // Stock sits in Head Office and Online Fulfilment, and some rows are low.
        var ho = await _api.BranchIdAsync("HO");
        var stock = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock?branchId={ho}&pageSize=100", admin), HttpStatusCode.OK);
        Assert.True(stock.GetProperty("total").GetInt32() > 50);
        var low = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock?branchId={ho}&lowOnly=true&pageSize=100", admin), HttpStatusCode.OK);
        Assert.True(low.GetProperty("total").GetInt32() > 0);
        var online = await _api.BranchIdAsync("ONL");
        Assert.True((await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/inventory/stock?branchId={online}", admin), HttpStatusCode.OK)).GetProperty("total").GetInt32() > 0);
    }
}

public class SampleCatalogGuardTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestApi _api = new(factory);

    [Fact]
    public async Task The_sample_catalog_is_not_added_when_the_owner_already_has_products()
    {
        var admin = (await _api.LoginAdminAsync()).Access;
        var category = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/categories", new { nameEn = "Own-" + Guid.NewGuid().ToString("N")[..8], sortOrder = 0 }, admin), HttpStatusCode.Created);
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/products", new
        {
            nameEn = "My Own Product", categoryId = category.GetProperty("id").GetGuid(), regularPrice = 500, targetGroup = "Unisex", isNew = false, isFeatured = false, isBestSeller = false,
        }, admin), HttpStatusCode.Created);

        await using var scope = factory.Services.CreateAsyncScope();
        var added = await scope.ServiceProvider.GetRequiredService<SampleCatalogSeeder>().SeedAsync(Ct);

        Assert.Equal(0, added);
    }
}
