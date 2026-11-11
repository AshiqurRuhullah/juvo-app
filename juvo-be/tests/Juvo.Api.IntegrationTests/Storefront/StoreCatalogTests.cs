using System.Net;
using System.Text.Json;

namespace Juvo.Api.IntegrationTests.Storefront;

public class StoreCatalogTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly StoreHelper _h = new(factory);

    private static string Names(JsonElement page) =>
        string.Join("|", page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("nameEn").GetString()));

    private async Task<JsonElement> ListAsync(string query) =>
        await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/products" + query, HttpStatusCode.OK);

    [Fact]
    public async Task The_catalog_is_public_and_only_shows_published_products()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        await _h.ProductAsync(admin, cat.Id, "Visible Tee", 500m, [("Red", "M", 5)]);
        await _h.ProductAsync(admin, cat.Id, "Draft Tee", 500m, [("Red", "M", 5)], publish: false);

        var page = await ListAsync($"?category={cat.Slug}");

        Assert.Equal("Visible Tee", Names(page));
        Assert.Equal(HttpStatusCode.NotFound, (await _h.SendAsync(HttpMethod.Get, "/api/v1/store/products/" + (await _h.JsonAsync(HttpMethod.Get, "/api/v1/catalog/products?search=Draft+Tee", HttpStatusCode.OK, bearer: admin)).GetProperty("items")[0].GetProperty("slug").GetString())).StatusCode);
    }

    [Fact]
    public async Task Prices_filter_and_sort_work_and_the_sale_price_is_what_is_paid()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        await _h.ProductAsync(admin, cat.Id, "Cheap", 300m, [("Red", "M", 5)]);
        await _h.ProductAsync(admin, cat.Id, "Mid", 1000m, [("Red", "M", 5)], salePrice: 800m);
        await _h.ProductAsync(admin, cat.Id, "Dear", 3000m, [("Red", "M", 5)]);

        Assert.Equal("Cheap|Mid|Dear", Names(await ListAsync($"?category={cat.Slug}&sort=price-asc")));
        Assert.Equal("Dear|Mid|Cheap", Names(await ListAsync($"?category={cat.Slug}&sort=price-desc")));
        Assert.Equal("Mid", Names(await ListAsync($"?category={cat.Slug}&minPrice=500&maxPrice=900")));
        Assert.Equal("Mid", Names(await ListAsync($"?category={cat.Slug}&onSale=true")));

        var mid = (await ListAsync($"?category={cat.Slug}&onSale=true")).GetProperty("items")[0];
        Assert.Equal(800m, mid.GetProperty("price").GetDecimal());
        Assert.Equal(1000m, mid.GetProperty("regularPrice").GetDecimal());
        Assert.Equal(20, mid.GetProperty("discountPercent").GetInt32());
    }

    [Fact]
    public async Task Size_colour_brand_and_stock_filters_narrow_the_list()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        var brand = StoreHelper.Unique("Brand");
        await _h.ProductAsync(admin, cat.Id, "Blue Large", 500m, [("Blue", "L", 4)], brandName: brand);
        await _h.ProductAsync(admin, cat.Id, "Red Small", 500m, [("Red", "S", 4)]);
        await _h.ProductAsync(admin, cat.Id, "Gone", 500m, [("Green", "M", 0)]);

        Assert.Equal("Blue Large", Names(await ListAsync($"?category={cat.Slug}&size=L")));
        Assert.Equal("Red Small", Names(await ListAsync($"?category={cat.Slug}&colour=Red")));
        Assert.Equal("Blue Large", Names(await ListAsync($"?category={cat.Slug}&brand={Uri.EscapeDataString(brand)}")));
        var inStock = Names(await ListAsync($"?category={cat.Slug}&inStock=true"));
        Assert.DoesNotContain("Gone", inStock);
        Assert.Contains("Red Small", inStock);
    }

    [Fact]
    public async Task Search_finds_by_name_and_forgives_small_spelling_mistakes()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        await _h.ProductAsync(admin, cat.Id, "Cotton Shirt", 500m, [("Red", "M", 5)]);
        await _h.ProductAsync(admin, cat.Id, "Leather Wallet", 900m, [("Brown", "M", 5)]);

        Assert.Equal("Cotton Shirt", Names(await ListAsync($"?category={cat.Slug}&q=shirt")));
        Assert.Equal("Cotton Shirt", Names(await ListAsync($"?category={cat.Slug}&q=shrit")));
        Assert.Equal("Leather Wallet", Names(await ListAsync($"?category={cat.Slug}&q=walet")));
        Assert.Equal(string.Empty, Names(await ListAsync($"?category={cat.Slug}&q=zzzzzz")));
    }

    [Fact]
    public async Task Detail_shows_variants_with_capped_availability_and_never_cost_or_branches()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        var product = await _h.ProductAsync(admin, cat.Id, "Detail Tee", 700m, [("Red", "M", 25), ("Red", "L", 2), ("Red", "XL", 0)]);

        var response = await _h.SendAsync(HttpMethod.Get, "/api/v1/store/products/" + product.Slug);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.DoesNotContain("costPrice", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("branch", text, StringComparison.OrdinalIgnoreCase);

        var variants = JsonDocument.Parse(text).RootElement.GetProperty("variants").EnumerateArray().ToList();
        var m = variants.Single(v => v.GetProperty("size").GetString() == "M");
        var l = variants.Single(v => v.GetProperty("size").GetString() == "L");
        var xl = variants.Single(v => v.GetProperty("size").GetString() == "XL");
        Assert.Equal(10, m.GetProperty("available").GetInt32()); // 25 on hand, shown as "10 or more"
        Assert.False(m.GetProperty("lowStock").GetBoolean());
        Assert.Equal(2, l.GetProperty("available").GetInt32());
        Assert.True(l.GetProperty("lowStock").GetBoolean());
        Assert.False(xl.GetProperty("inStock").GetBoolean());
    }

    [Fact]
    public async Task Categories_form_a_tree_that_hides_inactive_ones_and_counts_published_products()
    {
        var admin = await _h.AdminAsync();
        var parent = await _h.CategoryAsync(admin);
        var child = await _h.CategoryAsync(admin, parentId: parent.Id);
        var hidden = await _h.CategoryAsync(admin, parentId: parent.Id, active: false);
        await _h.ProductAsync(admin, child.Id, "In Child", 500m, [("Red", "M", 5)]);

        var tree = await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/categories", HttpStatusCode.OK);
        var node = tree.EnumerateArray().Single(c => c.GetProperty("slug").GetString() == parent.Slug);
        var slugs = node.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("slug").GetString()).ToList();

        Assert.Contains(child.Slug, slugs);
        Assert.DoesNotContain(hidden.Slug, slugs);
        Assert.Equal(1, node.GetProperty("productCount").GetInt32());
    }

    [Fact]
    public async Task Facets_and_suggestions_reflect_the_published_products()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        await _h.ProductAsync(admin, cat.Id, "Facet Frock", 400m, [("Pink", "S", 3), ("Pink", "M", 3)]);
        await _h.ProductAsync(admin, cat.Id, "Facet Gown", 2400m, [("Black", "L", 3)]);

        var facets = await _h.JsonAsync(HttpMethod.Get, $"/api/v1/store/products/facets?category={cat.Slug}", HttpStatusCode.OK);
        Assert.Equal(["L", "M", "S"], facets.GetProperty("sizes").EnumerateArray().Select(s => s.GetString()!).Order().ToArray());
        Assert.Equal(400m, facets.GetProperty("minPrice").GetDecimal());
        Assert.Equal(2400m, facets.GetProperty("maxPrice").GetDecimal());

        var suggest = await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/search/suggest?q=Facet+Fro", HttpStatusCode.OK);
        Assert.Contains(suggest.GetProperty("products").EnumerateArray(), p => p.GetProperty("name").GetString() == "Facet Frock");
    }

    [Fact]
    public async Task Home_lists_flagged_products_and_reads_are_cacheable()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        var name = StoreHelper.Unique("Fresh");
        await _h.ProductAsync(admin, cat.Id, name, 500m, [("Red", "M", 5)], isNew: true);

        var response = await _h.SendAsync(HttpMethod.Get, "/api/v1/store/home");
        var home = await Juvo.Api.IntegrationTests.Support.TestApi.ExpectAsync(response, HttpStatusCode.OK);

        Assert.Contains(home.GetProperty("newArrivals").EnumerateArray(), p => p.GetProperty("nameEn").GetString() == name);
        Assert.Contains("max-age", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Fulfilment_branches_decide_which_stock_is_for_sale()
    {
        // Default factory: every active branch counts. The head-office-only factory is covered in StoreBranchTests.
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        var product = await _h.ProductAsync(admin, cat.Id, "Everywhere", 500m, [("Red", "M", 4)]);

        var detail = await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/products/" + product.Slug, HttpStatusCode.OK);

        Assert.Equal(4, detail.GetProperty("variants")[0].GetProperty("available").GetInt32());
    }
}

public class StoreBranchTests(HeadOfficeOnlyStoreFactory factory) : IClassFixture<HeadOfficeOnlyStoreFactory>
{
    private readonly StoreHelper _h = new(factory);

    [Fact]
    public async Task Stock_in_a_branch_that_does_not_sell_online_is_not_shown()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        var product = await _h.ProductAsync(admin, cat.Id, "Head Office Only", 500m, [("Red", "M", 3)]);

        // Put extra units in a second branch: customers must not see them.
        var shopId = await _h.Api.CreateBranchAsync(admin, StoreHelper.Unique("SH")[..8].ToUpperInvariant(), "Shop Two");
        await Juvo.Api.IntegrationTests.Support.TestApi.ExpectAsync(await _h.Api.PostAsync("/api/v1/inventory/adjustments",
            new { branchId = shopId, reason = "OpeningStock", lines = new[] { new { variantId = product.Variants[0].Id, quantityChange = 9 } } }, admin),
            HttpStatusCode.OK);

        var detail = await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/products/" + product.Slug, HttpStatusCode.OK);

        Assert.Equal(3, detail.GetProperty("variants")[0].GetProperty("available").GetInt32());
    }
}
