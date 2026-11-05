using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Catalog;
using Juvo.Infrastructure.Catalog;
using Juvo.Infrastructure.Persistence;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Catalog;

public class CatalogTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly TestApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid():N}"[..20];

    private async Task<Guid> CategoryAsync(string token, string? name = null, Guid? parent = null)
    {
        var json = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/categories",
            new { nameEn = name ?? Unique("Cat"), sortOrder = 0, parentId = parent }, token), HttpStatusCode.Created);
        return json.GetProperty("id").GetGuid();
    }

    private static object Product(Guid category, string? name = null, decimal price = 1500, decimal? sale = null, decimal? cost = 700, string? slug = null) => new
    {
        nameEn = name ?? Unique("Tee"), slug, categoryId = category, regularPrice = price, salePrice = sale, costPrice = cost,
        targetGroup = "Unisex", isNew = false, isFeatured = false, isBestSeller = false,
    };

    private async Task<JsonElement> CreateProductAsync(string token, Guid category, string? name = null, decimal price = 1500, decimal? sale = null) =>
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/products", Product(category, name, price, sale), token), HttpStatusCode.Created);

    private async Task AddImageAsync(Guid productId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        db.ProductImages.Add(new ProductImage(productId, $"products/test/{Guid.NewGuid():N}.webp", null, 0));
        await db.SaveChangesAsync(Ct);
    }

    // ---- seed ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Seed_adds_the_four_top_level_categories_once()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<CatalogSeeder>();
        var first = await seeder.SeedAsync(Ct);
        var second = await seeder.SeedAsync(Ct);

        Assert.True(first >= 0);
        Assert.Equal(0, second);
        var admin = await _api.LoginAdminAsync();
        var tree = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/catalog/categories", admin.Access), HttpStatusCode.OK);
        var roots = tree.EnumerateArray().Select(n => n.GetProperty("nameEn").GetString()).ToList();
        Assert.Contains("Clothing", roots);
        Assert.Contains("Shoes", roots);
        Assert.Contains("Ladies' Dresses", roots);
        Assert.Contains("Purses", roots);
        var clothing = tree.EnumerateArray().First(n => n.GetProperty("nameEn").GetString() == "Clothing");
        Assert.NotEmpty(clothing.GetProperty("children").EnumerateArray());
    }

    // ---- categories ---------------------------------------------------------------------------------------

    [Fact]
    public async Task Categories_form_a_tree_with_unique_slugs_and_no_loops()
    {
        var admin = await _api.LoginAdminAsync();
        var name = Unique("Dresses");
        var root = await CategoryAsync(admin.Access, name);
        var child = await CategoryAsync(admin.Access, "Party " + name, root);

        var slugDuplicate = await _api.PostAsync("/api/v1/catalog/categories", new { nameEn = "x", slug = SlugRules(name), sortOrder = 0 }, admin.Access);
        Assert.Equal("CATALOG.SLUG_TAKEN", await TestApi.ErrorCodeAsync(slugDuplicate));

        var self = await _api.PutAsync($"/api/v1/catalog/categories/{root}", new { nameEn = name, sortOrder = 0, parentId = root, isActive = true }, admin.Access);
        Assert.Equal("CATALOG.CATEGORY_CYCLE", await TestApi.ErrorCodeAsync(self));

        var loop = await _api.PutAsync($"/api/v1/catalog/categories/{root}", new { nameEn = name, sortOrder = 0, parentId = child, isActive = true }, admin.Access);
        Assert.Equal("CATALOG.CATEGORY_CYCLE", await TestApi.ErrorCodeAsync(loop));

        var inUse = await _api.DeleteAsync($"/api/v1/catalog/categories/{root}", admin.Access);
        Assert.Equal("CATALOG.CATEGORY_IN_USE", await TestApi.ErrorCodeAsync(inUse));

        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/catalog/categories/{child}", admin.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/catalog/categories/{root}", admin.Access)).StatusCode);
    }

    [Fact]
    public async Task A_slug_is_made_from_the_english_name_and_numbered_when_taken()
    {
        var admin = await _api.LoginAdminAsync();
        var name = Unique("Scarves");
        var first = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/categories", new { nameEn = name, nameBn = "স্কার্ফ", sortOrder = 0 }, admin.Access), HttpStatusCode.Created);
        var second = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/categories", new { nameEn = name, sortOrder = 0 }, admin.Access), HttpStatusCode.Created);

        Assert.Equal(SlugRules(name), first.GetProperty("slug").GetString());
        Assert.Equal(SlugRules(name) + "-2", second.GetProperty("slug").GetString());
    }

    private static string SlugRules(string name) => Juvo.Application.Catalog.SlugRules.Slugify(name);

    // ---- products -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_product_is_a_draft_and_publishing_needs_an_image_and_an_active_variant()
    {
        var admin = await _api.LoginAdminAsync();
        var category = await CategoryAsync(admin.Access);
        var product = await CreateProductAsync(admin.Access, category);
        var id = product.GetProperty("id").GetGuid();
        Assert.Equal("Draft", product.GetProperty("status").GetString());
        Assert.Equal(2, product.GetProperty("publishProblems").GetArrayLength());

        var blocked = await _api.PostAsync($"/api/v1/catalog/products/{id}/publish", null, admin.Access);
        var body = await TestApi.ExpectAsync(blocked, HttpStatusCode.BadRequest);
        Assert.Equal(2, body.GetProperty("errors").GetProperty("publish").GetArrayLength());

        await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{id}/variants",
            new { sku = Unique("sku"), colorName = "Black", colorHex = "#000000", size = "M" }, admin.Access), HttpStatusCode.Created);
        var stillBlocked = await _api.PostAsync($"/api/v1/catalog/products/{id}/publish", null, admin.Access);
        var one = await TestApi.ExpectAsync(stillBlocked, HttpStatusCode.BadRequest);
        Assert.Equal(1, one.GetProperty("errors").GetProperty("publish").GetArrayLength());

        await AddImageAsync(id);
        var published = await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{id}/publish", null, admin.Access), HttpStatusCode.OK);
        Assert.Equal("Published", published.GetProperty("status").GetString());
        Assert.Empty(published.GetProperty("publishProblems").EnumerateArray());

        var delete = await _api.DeleteAsync($"/api/v1/catalog/products/{id}", admin.Access);
        Assert.Equal("CATALOG.PUBLISH_RULES", await TestApi.ErrorCodeAsync(delete));

        var archived = await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{id}/archive", null, admin.Access), HttpStatusCode.OK);
        Assert.Equal("Archived", archived.GetProperty("status").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/catalog/products/{id}", admin.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync($"/api/v1/catalog/products/{id}", admin.Access)).StatusCode);
    }

    [Fact]
    public async Task The_sale_price_must_be_lower_than_the_regular_price_and_dates_must_make_sense()
    {
        var admin = await _api.LoginAdminAsync();
        var category = await CategoryAsync(admin.Access);

        var equal = await _api.PostAsync("/api/v1/catalog/products", Product(category, price: 1000, sale: 1000), admin.Access);
        var json = await TestApi.ExpectAsync(equal, HttpStatusCode.BadRequest);
        Assert.True(json.GetProperty("errors").TryGetProperty("salePrice", out _));

        var backwards = await _api.PostAsync("/api/v1/catalog/products", new
        {
            nameEn = Unique("Sale"), categoryId = category, regularPrice = 1000, salePrice = 800, saleStart = "2026-12-10T00:00:00Z", saleEnd = "2026-12-01T00:00:00Z",
            targetGroup = "Unisex",
        }, admin.Access);
        Assert.True((await TestApi.ExpectAsync(backwards, HttpStatusCode.BadRequest)).GetProperty("errors").TryGetProperty("saleEnd", out _));

        var ok = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/products", Product(category, price: 1000, sale: 800), admin.Access), HttpStatusCode.Created);
        Assert.Equal(800m, ok.GetProperty("salePrice").GetDecimal());
    }

    [Fact]
    public async Task Sale_price_applies_only_inside_its_dates()
    {
        var details = new ProductDetails("Tee", null, "tee", Guid.NewGuid(), null, null, null, null, null, null, TargetGroup.Unisex, null, null, null, null,
            1000, 800, new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 12, 10, 0, 0, 0, TimeSpan.Zero), null, false, false, false, null, null, null, null, null);
        var product = new Product(details);

        Assert.Equal(1000, product.EffectivePrice(new DateTimeOffset(2026, 11, 30, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(800, product.EffectivePrice(new DateTimeOffset(2026, 12, 5, 0, 0, 0, TimeSpan.Zero)));
        Assert.Equal(1000, product.EffectivePrice(new DateTimeOffset(2026, 12, 11, 0, 0, 0, TimeSpan.Zero)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Product_slug_must_be_unique_and_missing_category_is_refused()
    {
        var admin = await _api.LoginAdminAsync();
        var category = await CategoryAsync(admin.Access);
        var slug = Unique("my-tee").ToLowerInvariant().Replace('_', '-');
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/products", Product(category, slug: slug), admin.Access), HttpStatusCode.Created);

        var duplicate = await _api.PostAsync("/api/v1/catalog/products", Product(category, slug: slug), admin.Access);
        Assert.Equal("CATALOG.SLUG_TAKEN", await TestApi.ErrorCodeAsync(duplicate));

        var missing = await _api.PostAsync("/api/v1/catalog/products", Product(Guid.NewGuid()), admin.Access);
        Assert.Equal("CATALOG.CATEGORY_NOT_FOUND", await TestApi.ErrorCodeAsync(missing));
    }

    // ---- variants -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_sku_is_unique_across_products_case_insensitively_and_freed_when_the_variant_is_removed()
    {
        var admin = await _api.LoginAdminAsync();
        var category = await CategoryAsync(admin.Access);
        var a = (await CreateProductAsync(admin.Access, category)).GetProperty("id").GetGuid();
        var b = (await CreateProductAsync(admin.Access, category)).GetProperty("id").GetGuid();
        var sku = Unique("sku").ToUpperInvariant();

        var first = await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{a}/variants", new { sku = sku.ToLowerInvariant(), colorName = "Red", size = "S" }, admin.Access), HttpStatusCode.Created);
        Assert.Equal(sku, first.GetProperty("sku").GetString());

        var clash = await _api.PostAsync($"/api/v1/catalog/products/{b}/variants", new { sku, colorName = "Red", size = "S" }, admin.Access);
        Assert.Equal("CATALOG.SKU_TAKEN", await TestApi.ErrorCodeAsync(clash));

        var sameCombo = await _api.PostAsync($"/api/v1/catalog/products/{a}/variants", new { sku = Unique("other"), colorName = "Red", size = "S" }, admin.Access);
        Assert.Equal("CATALOG.SKU_TAKEN", await TestApi.ErrorCodeAsync(sameCombo));

        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/catalog/products/{a}/variants/{first.GetProperty("id").GetGuid()}", admin.Access)).StatusCode);
        await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{b}/variants", new { sku, colorName = "Red", size = "S" }, admin.Access), HttpStatusCode.Created);
    }

    [Fact]
    public async Task Variant_input_is_validated()
    {
        var admin = await _api.LoginAdminAsync();
        var category = await CategoryAsync(admin.Access);
        var id = (await CreateProductAsync(admin.Access, category)).GetProperty("id").GetGuid();

        var bad = await _api.PostAsync($"/api/v1/catalog/products/{id}/variants", new { sku = "has space", colorHex = "red", priceOverride = -1 }, admin.Access);
        var errors = (await TestApi.ExpectAsync(bad, HttpStatusCode.BadRequest)).GetProperty("errors");
        Assert.True(errors.TryGetProperty("sku", out _));
        Assert.True(errors.TryGetProperty("colorHex", out _));
        Assert.True(errors.TryGetProperty("priceOverride", out _));
    }

    // ---- duplicate, list, cost visibility ------------------------------------------------------------------

    [Fact]
    public async Task Duplicate_makes_a_draft_copy_with_new_skus()
    {
        var admin = await _api.LoginAdminAsync();
        var category = await CategoryAsync(admin.Access);
        var id = (await CreateProductAsync(admin.Access, category, Unique("Orig"))).GetProperty("id").GetGuid();
        var sku = Unique("dup").ToUpperInvariant();
        await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{id}/variants", new { sku, colorName = "Blue", size = "L" }, admin.Access), HttpStatusCode.Created);
        await AddImageAsync(id);
        await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{id}/publish", null, admin.Access), HttpStatusCode.OK);

        var copy = await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{id}/duplicate", new { }, admin.Access), HttpStatusCode.Created);

        Assert.NotEqual(id, copy.GetProperty("id").GetGuid());
        Assert.Equal("Draft", copy.GetProperty("status").GetString());
        Assert.EndsWith("(copy)", copy.GetProperty("nameEn").GetString());
        var variant = copy.GetProperty("variants")[0];
        Assert.Equal(sku + "-COPY", variant.GetProperty("sku").GetString());
        Assert.Equal(1, copy.GetProperty("images").GetArrayLength());
    }

    [Fact]
    public async Task List_filters_by_status_category_tree_and_searches_by_sku()
    {
        var admin = await _api.LoginAdminAsync();
        var parent = await CategoryAsync(admin.Access);
        var child = await CategoryAsync(admin.Access, parent: parent);
        var other = await CategoryAsync(admin.Access);
        var inChild = (await CreateProductAsync(admin.Access, child)).GetProperty("id").GetGuid();
        var inOther = (await CreateProductAsync(admin.Access, other)).GetProperty("id").GetGuid();
        var sku = Unique("find").ToUpperInvariant();
        await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/catalog/products/{inChild}/variants", new { sku, colorName = "Green", size = "M" }, admin.Access), HttpStatusCode.Created);

        var byParent = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/products?categoryId={parent}", admin.Access), HttpStatusCode.OK);
        Assert.Equal([inChild], byParent.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).ToArray());

        var bySku = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/products?search={sku}", admin.Access), HttpStatusCode.OK);
        Assert.Equal(inChild, bySku.GetProperty("items")[0].GetProperty("id").GetGuid());

        var published = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/products?status=Published&categoryId={other}", admin.Access), HttpStatusCode.OK);
        Assert.Equal(0, published.GetProperty("total").GetInt32());
        Assert.NotEqual(inOther, Guid.Empty);
    }

    [Fact]
    public async Task Cost_price_is_hidden_and_protected_without_the_view_cost_permission()
    {
        var admin = await _api.LoginAdminAsync();
        var category = await CategoryAsync(admin.Access);
        var id = (await CreateProductAsync(admin.Access, category)).GetProperty("id").GetGuid();

        var role = await _api.CreateRoleAsync(admin.Access, Unique("Editor"), Permissions.Catalog.ProductView, Permissions.Catalog.ProductEdit, Permissions.Catalog.CategoryView);
        var email = $"{Unique("ed")}@juvo.test";
        var user = await _api.CreateUserAsync(admin.Access, email, "Content Editor", global: true, (null, role));
        var editor = await _api.LoginAsync(user.Email, user.Password);

        var seen = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/products/{id}", editor.Access), HttpStatusCode.OK);
        Assert.Equal(JsonValueKind.Null, seen.GetProperty("costPrice").ValueKind);
        Assert.True(seen.GetProperty("costHidden").GetBoolean());

        // Saving without being able to see the cost must not wipe or change it.
        var update = Product(category, "Renamed", 1600, null, cost: 1);
        await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/catalog/products/{id}", update, editor.Access), HttpStatusCode.OK);
        var asAdmin = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/catalog/products/{id}", admin.Access), HttpStatusCode.OK);
        Assert.Equal(700m, asAdmin.GetProperty("costPrice").GetDecimal());
        Assert.Equal("Renamed", asAdmin.GetProperty("nameEn").GetString());
    }

    [Fact]
    public async Task Staff_without_catalog_permissions_are_refused()
    {
        var admin = await _api.LoginAdminAsync();
        var role = await _api.CreateRoleAsync(admin.Access, Unique("NoCat"), Permissions.Catalog.ProductView);
        var user = await _api.CreateUserAsync(admin.Access, $"{Unique("nc")}@juvo.test", "Viewer", global: true, (null, role));
        var viewer = await _api.LoginAsync(user.Email, user.Password);
        var category = await CategoryAsync(admin.Access);

        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/catalog/products", viewer.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.PostAsync("/api/v1/catalog/products", Product(category), viewer.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/catalog/categories", viewer.Access)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync("/api/v1/catalog/products")).StatusCode);
    }

    [Fact]
    public async Task Product_changes_are_written_to_the_audit_log()
    {
        var admin = await _api.LoginAdminAsync();
        var category = await CategoryAsync(admin.Access);
        var id = (await CreateProductAsync(admin.Access, category)).GetProperty("id").GetGuid();
        await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/catalog/products/{id}", Product(category, "Audited", 1700), admin.Access), HttpStatusCode.OK);

        var logs = await TestApi.ExpectAsync(await _api.GetAsync($"/api/v1/ums/audit-logs?entity=Product&entityId={id}", admin.Access), HttpStatusCode.OK);
        Assert.True(logs.GetProperty("total").GetInt32() >= 2);
    }

    // ---- brands and size charts ----------------------------------------------------------------------------

    [Fact]
    public async Task Brands_can_be_created_and_are_protected_while_in_use()
    {
        var admin = await _api.LoginAdminAsync();
        var brand = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/brands", new { nameEn = Unique("Juvo") }, admin.Access), HttpStatusCode.Created);
        var brandId = brand.GetProperty("id").GetGuid();
        var category = await CategoryAsync(admin.Access);
        var body = Product(category);
        var json = JsonSerializer.SerializeToElement(body);
        var withBrand = JsonSerializer.Deserialize<Dictionary<string, object?>>(json.GetRawText())!;
        withBrand["brandId"] = brandId;
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/products", withBrand, admin.Access), HttpStatusCode.Created);

        var delete = await _api.DeleteAsync($"/api/v1/catalog/brands/{brandId}", admin.Access);
        Assert.Equal("CATALOG.BRAND_IN_USE", await TestApi.ErrorCodeAsync(delete));
    }

    [Fact]
    public async Task Size_charts_need_a_rectangular_table()
    {
        var admin = await _api.LoginAdminAsync();
        var bad = await _api.PostAsync("/api/v1/catalog/size-charts", new { nameEn = "Bad", tableJson = """{"columns":["A","B"],"rows":[["1"]]}""" }, admin.Access);
        Assert.True((await TestApi.ExpectAsync(bad, HttpStatusCode.BadRequest)).GetProperty("errors").TryGetProperty("tableJson", out _));

        var notJson = await _api.PostAsync("/api/v1/catalog/size-charts", new { nameEn = "Bad", tableJson = "nope" }, admin.Access);
        Assert.Equal(HttpStatusCode.BadRequest, notJson.StatusCode);

        var ok = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/catalog/size-charts",
            new { nameEn = Unique("Tops"), tableJson = """{"columns":["Size","Chest"],"rows":[["S","36"],["M","38"]]}""" }, admin.Access), HttpStatusCode.Created);
        Assert.Equal(HttpStatusCode.NoContent, (await _api.DeleteAsync($"/api/v1/catalog/size-charts/{ok.GetProperty("id").GetGuid()}", admin.Access)).StatusCode);
    }
}
