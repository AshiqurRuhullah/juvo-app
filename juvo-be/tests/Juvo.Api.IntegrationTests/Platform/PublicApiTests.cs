using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Platform;

/// <summary>The public read API (FR-AF05): key authentication, scopes, branch limit, personal data masking, rate limit, and answers that match the staff data.</summary>
public class PublicApiTests(PublicApiFactory factory) : IClassFixture<PublicApiFactory>
{
    private readonly IntegrationsHelper _i = new(factory);
    private readonly OrderHelper _o = new(new StoreHelper(factory));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private StoreHelper Store => _i.Store;

    private async Task<(string Admin, SeededProduct Product)> ProductAsync(decimal price = 800m, int stock = 20, bool publish = true)
    {
        var admin = await Store.AdminAsync();
        var category = await Store.CategoryAsync(admin);
        var product = await Store.ProductAsync(admin, category.Id, StoreHelper.Unique("Pub Tee"), price, [("Red", "M", stock)], weightGrams: 300, publish: publish);
        return (admin, product);
    }

    private static async Task<string> CodeAsync(HttpResponseMessage response) => await TestApi.ErrorCodeAsync(response);

    // ---- authentication --------------------------------------------------------------------------------------

    [Fact]
    public async Task A_call_without_a_key_or_with_a_wrong_key_is_refused_with_the_same_answer()
    {
        var fake = "jv_live_" + new string('a', 8) + "_" + new string('b', 43);

        var missing = await _i.GetWithKeyAsync(null, "/api/public/v1/products");
        var wrong = await _i.GetWithKeyAsync(fake, "/api/public/v1/products");
        var garbage = await _i.GetWithKeyAsync("not-a-key", "/api/public/v1/products");

        foreach (var response in new[] { missing, wrong, garbage })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Contains("ApiKey", response.Headers.WwwAuthenticate.ToString(), StringComparison.Ordinal);
            Assert.True(response.Headers.Contains("X-Correlation-Id"));
        }

        var json = await TestApi.JsonAsync(missing);
        Assert.Equal("PUBLICAPI.INVALID_KEY", json.GetProperty("code").GetString());
        Assert.Equal(401, json.GetProperty("status").GetInt32());
        Assert.Equal("PUBLICAPI.INVALID_KEY", await CodeAsync(wrong));
        Assert.Equal("PUBLICAPI.INVALID_KEY", await CodeAsync(garbage));
    }

    [Fact]
    public async Task A_right_prefix_with_a_wrong_secret_is_refused_like_an_unknown_key()
    {
        var admin = await Store.AdminAsync();
        var (_, secret, prefix) = await _i.CreateKeyAsync(admin, ["products:read"]);
        var forged = prefix + "_" + new string('Z', 43);

        var response = await _i.GetWithKeyAsync(forged, "/api/public/v1/products");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("PUBLICAPI.INVALID_KEY", await CodeAsync(response));
        Assert.Equal(HttpStatusCode.OK, (await _i.GetWithKeyAsync(secret, "/api/public/v1/products")).StatusCode);
    }

    [Fact]
    public async Task A_disabled_key_and_an_expired_key_are_refused_with_their_own_codes()
    {
        var admin = await Store.AdminAsync();
        var (disabledId, disabled, _) = await _i.CreateKeyAsync(admin, ["products:read"]);
        var (expiredId, expired, _) = await _i.CreateKeyAsync(admin, ["products:read"], expiresAt: DateTimeOffset.UtcNow.AddHours(1));
        await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/api-keys/{disabledId}/disable", null, admin), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await _i.GetWithKeyAsync(expired, "/api/public/v1/products")).StatusCode);
        await _i.WithDbAsync(db => db.ApiKeys.Where(k => k.Id == expiredId).ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));

        var disabledResponse = await _i.GetWithKeyAsync(disabled, "/api/public/v1/products");
        var expiredResponse = await _i.GetWithKeyAsync(expired, "/api/public/v1/products");

        Assert.Equal(HttpStatusCode.Unauthorized, disabledResponse.StatusCode);
        Assert.Equal("PUBLICAPI.KEY_DISABLED", await CodeAsync(disabledResponse));
        Assert.Equal(HttpStatusCode.Unauthorized, expiredResponse.StatusCode);
        Assert.Equal("PUBLICAPI.KEY_EXPIRED", await CodeAsync(expiredResponse));
    }

    [Fact]
    public async Task A_key_without_the_scope_of_an_endpoint_gets_403_and_one_with_it_gets_200()
    {
        var admin = await Store.AdminAsync();
        var (_, stockOnly, _) = await _i.CreateKeyAsync(admin, ["stock:read"]);

        foreach (var url in new[] { "/api/public/v1/orders", "/api/public/v1/products", "/api/public/v1/customers" })
        {
            var denied = await _i.GetWithKeyAsync(stockOnly, url);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            Assert.Equal("PUBLICAPI.SCOPE_MISSING", await CodeAsync(denied));
        }

        Assert.Equal(HttpStatusCode.OK, (await _i.GetWithKeyAsync(stockOnly, "/api/public/v1/stock")).StatusCode);
    }

    [Fact]
    public async Task A_staff_token_does_not_open_the_public_api_and_a_key_does_not_open_the_staff_api()
    {
        var admin = await Store.AdminAsync();
        var (_, secret, _) = await _i.CreateKeyAsync(admin, ["orders:read", "products:read", "stock:read", "customers:read", "pii:read"]);

        var viaBearer = new HttpRequestMessage(HttpMethod.Get, "/api/public/v1/products");
        viaBearer.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", admin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _i.Api.Client.SendAsync(viaBearer, Ct)).StatusCode);

        foreach (var url in new[] { "/api/v1/orders", "/api/v1/catalog/products", "/api/v1/integrations/api-keys" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await _i.GetWithKeyAsync(secret, url)).StatusCode);
        }
    }

    [Fact]
    public async Task Using_a_key_records_when_and_from_which_address_hash_without_flooding_the_audit_trail()
    {
        var admin = await Store.AdminAsync();
        var (id, secret, _) = await _i.CreateKeyAsync(admin, ["products:read"]);
        for (var n = 0; n < 3; n++)
        {
            await _i.JsonWithKeyAsync(secret, "/api/public/v1/products?pageSize=1");
        }

        var key = await TestApi.ExpectAsync(await _i.Api.GetAsync($"/api/v1/integrations/api-keys/{id}", admin), HttpStatusCode.OK);
        Assert.NotEqual(JsonValueKind.Null, key.GetProperty("lastUsedAt").ValueKind);
        var ipHash = key.GetProperty("lastIpHash").GetString(); // the in-memory test host has no remote address, so it is null here; the hash itself is unit tested
        Assert.True(ipHash is null || ipHash.Length == 16);
        var audits = await _i.WithDbAsync(db => db.AuditLogs.CountAsync(a => a.Entity == "ApiKey" && a.EntityId == id.ToString(), Ct));
        Assert.Equal(1, audits); // only the creation
    }

    // ---- rate limit ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_key_over_its_limit_gets_429_with_retry_after_and_other_keys_are_not_affected()
    {
        var admin = await Store.AdminAsync();
        var (_, limited, _) = await _i.CreateKeyAsync(admin, ["products:read"], rateLimit: 3);
        var (_, other, _) = await _i.CreateKeyAsync(admin, ["products:read"], rateLimit: 3);

        HttpResponseMessage? refused = null;
        for (var n = 0; n < 8 && refused is null; n++)
        {
            var response = await _i.GetWithKeyAsync(limited, "/api/public/v1/products?pageSize=1");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                refused = response;
            }
        }

        Assert.NotNull(refused);
        Assert.True(int.Parse(refused.Headers.GetValues("Retry-After").Single(), System.Globalization.CultureInfo.InvariantCulture) is >= 1 and <= 60);
        Assert.Equal("3", refused.Headers.GetValues("X-RateLimit-Limit").Single());
        var json = await TestApi.JsonAsync(refused);
        Assert.Equal("PUBLICAPI.RATE_LIMITED", json.GetProperty("code").GetString());
        Assert.Equal(429, json.GetProperty("status").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await _i.GetWithKeyAsync(other, "/api/public/v1/products?pageSize=1")).StatusCode);
    }

    // ---- orders ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Orders_match_the_staff_data_and_hide_customer_details_unless_the_key_has_pii_read()
    {
        var (admin, product) = await ProductAsync(price: 800m);
        var mobile = StoreHelper.NewMobile();
        var placed = await _o.PlaceAsync(product.Variants[0].Id, 2, mobile);
        var orderId = placed.GetProperty("orderId").GetGuid();
        var number = placed.GetProperty("number").GetString()!;
        var staff = await _o.OrderAsync(admin, orderId);
        var (_, plain, _) = await _i.CreateKeyAsync(admin, ["orders:read"]);
        var (_, withPii, _) = await _i.CreateKeyAsync(admin, ["orders:read", "pii:read"]);

        var order = await _i.JsonWithKeyAsync(plain, $"/api/public/v1/orders/{number}");

        Assert.Equal(number, order.GetProperty("number").GetString());
        Assert.Equal(orderId, order.GetProperty("id").GetGuid());
        Assert.Equal(staff.GetProperty("money").GetProperty("grandTotal").GetDecimal(), order.GetProperty("grandTotal").GetDecimal());
        Assert.Equal(staff.GetProperty("money").GetProperty("subtotal").GetDecimal(), order.GetProperty("subtotal").GetDecimal());
        Assert.Equal(staff.GetProperty("money").GetProperty("deliveryCharge").GetDecimal(), order.GetProperty("deliveryCharge").GetDecimal());
        Assert.Equal("BDT", order.GetProperty("currency").GetString());
        Assert.Equal("Placed", order.GetProperty("status").GetString());
        Assert.Equal("HO", order.GetProperty("branchCode").GetString());
        var line = order.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(product.Variants[0].Sku, line.GetProperty("sku").GetString());
        Assert.Equal(2, line.GetProperty("quantity").GetInt32());
        Assert.Equal(1600m, line.GetProperty("lineTotal").GetDecimal());

        // Masked without pii:read: no name, no e-mail, no street address, the mobile number only as ******.
        var text = order.GetRawText();
        Assert.DoesNotContain(mobile, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Nadia", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Dhanmondi", text, StringComparison.Ordinal);
        Assert.Contains("****", order.GetProperty("customer").GetProperty("mobile").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, order.GetProperty("customer").GetProperty("name").ValueKind);
        Assert.Equal(JsonValueKind.Null, order.GetProperty("shipTo").GetProperty("addressLine").ValueKind);
        Assert.Equal("Dhaka", order.GetProperty("shipTo").GetProperty("district").GetString());

        // Never exposed to any key.
        foreach (var forbidden in new[] { "costPrice", "unitCost", "notes", "fraud", "riskScore", "trackingToken", "clientIp", "deviceId", "fingerprint", "idempotencyKey" })
        {
            Assert.DoesNotContain(forbidden, text, StringComparison.OrdinalIgnoreCase);
        }

        var full = await _i.JsonWithKeyAsync(withPii, $"/api/public/v1/orders/{number}");
        Assert.Equal("+88" + mobile, full.GetProperty("customer").GetProperty("mobile").GetString());
        Assert.Equal("Nadia Rahman", full.GetProperty("customer").GetProperty("name").GetString());
        Assert.Contains("Dhanmondi", full.GetProperty("shipTo").GetProperty("addressLine").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_order_list_filters_by_status_and_dates_and_caps_the_page_size_at_100()
    {
        var (admin, product) = await ProductAsync();
        var first = await _o.PlaceAsync(product.Variants[0].Id, 1);
        var second = await _o.PlaceAsync(product.Variants[0].Id, 1);
        await _o.ActAsync(admin, second.GetProperty("orderId").GetGuid(), "confirm");
        var (_, secret, _) = await _i.CreateKeyAsync(admin, ["orders:read"]);
        var firstNumber = first.GetProperty("number").GetString()!;
        var secondNumber = second.GetProperty("number").GetString()!;

        static IEnumerable<string> Numbers(JsonElement page) => page.GetProperty("items").EnumerateArray().Select(o => o.GetProperty("number").GetString()!);

        var confirmed = Numbers(await _i.JsonWithKeyAsync(secret, "/api/public/v1/orders?status=Confirmed&pageSize=100")).ToList();
        Assert.Contains(secondNumber, confirmed);
        Assert.DoesNotContain(firstNumber, confirmed);
        var placedList = Numbers(await _i.JsonWithKeyAsync(secret, "/api/public/v1/orders?status=placed&pageSize=100")).ToList();
        Assert.Contains(firstNumber, placedList);
        Assert.DoesNotContain(secondNumber, placedList);

        var tomorrow = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        var yesterday = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(-1).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty(Numbers(await _i.JsonWithKeyAsync(secret, $"/api/public/v1/orders?placedFrom={tomorrow}")));
        Assert.Empty(Numbers(await _i.JsonWithKeyAsync(secret, $"/api/public/v1/orders?updatedSince={tomorrow}")));
        Assert.Empty(Numbers(await _i.JsonWithKeyAsync(secret, $"/api/public/v1/orders?placedTo={yesterday}")));
        Assert.Contains(firstNumber, Numbers(await _i.JsonWithKeyAsync(secret, $"/api/public/v1/orders?placedFrom={yesterday}&pageSize=100")));

        var capped = await _i.JsonWithKeyAsync(secret, "/api/public/v1/orders?pageSize=5000");
        Assert.Equal(100, capped.GetProperty("pageSize").GetInt32());
        var paged = await _i.JsonWithKeyAsync(secret, "/api/public/v1/orders?pageSize=1&page=2");
        Assert.Single(paged.GetProperty("items").EnumerateArray());
        Assert.True(paged.GetProperty("total").GetInt32() >= 2);

        var bad = await _i.GetWithKeyAsync(secret, "/api/public/v1/orders?status=Bogus");
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _i.GetWithKeyAsync(secret, "/api/public/v1/orders/JV-HO-0000-000000")).StatusCode);
    }

    [Fact]
    public async Task A_key_limited_to_another_branch_sees_no_order_and_no_stock_of_head_office()
    {
        var (admin, product) = await ProductAsync(stock: 12);
        var placed = await _o.PlaceAsync(product.Variants[0].Id, 3);
        var number = placed.GetProperty("number").GetString()!;
        var sku = product.Variants[0].Sku;
        var otherBranch = await _i.Api.CreateBranchAsync(admin, "PA" + Random.Shared.Next(10, 99).ToString(System.Globalization.CultureInfo.InvariantCulture), "Public API shop");
        var ho = await _i.Api.BranchIdAsync("HO");
        var (_, everything, _) = await _i.CreateKeyAsync(admin, ["orders:read", "stock:read"]);
        var (_, homeOnly, _) = await _i.CreateKeyAsync(admin, ["orders:read", "stock:read"], branchId: ho);
        var (_, elsewhere, _) = await _i.CreateKeyAsync(admin, ["orders:read", "stock:read"], branchId: otherBranch);

        var all = (await _i.JsonWithKeyAsync(everything, $"/api/public/v1/stock?sku={sku}")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(12, all.GetProperty("onHand").GetInt32());
        Assert.Equal(9, all.GetProperty("available").GetInt32());
        var branch = all.GetProperty("branches").EnumerateArray().Single(b => b.GetProperty("branchCode").GetString() == "HO");
        Assert.Equal(12, branch.GetProperty("onHand").GetInt32());
        Assert.Equal(3, branch.GetProperty("reserved").GetInt32());
        Assert.Equal(9, branch.GetProperty("available").GetInt32());

        var home = (await _i.JsonWithKeyAsync(homeOnly, $"/api/public/v1/stock?sku={sku}")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(12, home.GetProperty("onHand").GetInt32());
        Assert.Equal(HttpStatusCode.OK, (await _i.GetWithKeyAsync(homeOnly, $"/api/public/v1/orders/{number}")).StatusCode);

        var away = (await _i.JsonWithKeyAsync(elsewhere, $"/api/public/v1/stock?sku={sku}")).GetProperty("items").EnumerateArray().Single();
        Assert.Equal(0, away.GetProperty("onHand").GetInt32());
        Assert.Empty(away.GetProperty("branches").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _i.GetWithKeyAsync(elsewhere, $"/api/public/v1/orders/{number}")).StatusCode);
        var awayOrders = await _i.JsonWithKeyAsync(elsewhere, "/api/public/v1/orders?pageSize=100");
        Assert.DoesNotContain(awayOrders.GetProperty("items").EnumerateArray(), o => o.GetProperty("number").GetString() == number);
    }

    // ---- products --------------------------------------------------------------------------------------------

    [Fact]
    public async Task Products_show_published_items_only_with_prices_and_without_cost()
    {
        var (admin, published) = await ProductAsync(price: 1200m);
        var (_, draft) = await ProductAsync(price: 999m, publish: false);
        var (_, secret, _) = await _i.CreateKeyAsync(admin, ["products:read"]);

        var detail = await _i.JsonWithKeyAsync(secret, $"/api/public/v1/products/{published.Slug}");

        Assert.Equal(published.Id, detail.GetProperty("id").GetGuid());
        Assert.Equal(1200m, detail.GetProperty("price").GetDecimal());
        Assert.Equal(1200m, detail.GetProperty("regularPrice").GetDecimal());
        Assert.Equal("BDT", detail.GetProperty("currency").GetString());
        var variant = detail.GetProperty("variants").EnumerateArray().Single();
        Assert.Equal(published.Variants[0].Sku, variant.GetProperty("sku").GetString());
        Assert.Equal(1200m, variant.GetProperty("price").GetDecimal());
        Assert.Single(detail.GetProperty("images").EnumerateArray());
        Assert.DoesNotContain("cost", detail.GetRawText(), StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.NotFound, (await _i.GetWithKeyAsync(secret, $"/api/public/v1/products/{draft.Slug}")).StatusCode);
        var list = await _i.JsonWithKeyAsync(secret, "/api/public/v1/products?pageSize=100");
        var slugs = list.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("slug").GetString()!).ToList();
        Assert.Contains(published.Slug, slugs);
        Assert.DoesNotContain(draft.Slug, slugs);

        var future = Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("o", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Empty((await _i.JsonWithKeyAsync(secret, $"/api/public/v1/products?updatedSince={future}")).GetProperty("items").EnumerateArray());
        Assert.Equal(HttpStatusCode.NotFound, (await _i.GetWithKeyAsync(secret, "/api/public/v1/products/no-such-product")).StatusCode);
    }

    [Fact]
    public async Task A_product_on_sale_shows_the_sale_price_as_the_price()
    {
        var admin = await Store.AdminAsync();
        var category = await Store.CategoryAsync(admin);
        var product = await Store.ProductAsync(admin, category.Id, StoreHelper.Unique("Sale Tee"), 1000m, [("Blue", "L", 5)], salePrice: 700m);
        var (_, secret, _) = await _i.CreateKeyAsync(admin, ["products:read"]);

        var detail = await _i.JsonWithKeyAsync(secret, $"/api/public/v1/products/{product.Slug}");

        Assert.Equal(700m, detail.GetProperty("price").GetDecimal());
        Assert.Equal(1000m, detail.GetProperty("regularPrice").GetDecimal());
        Assert.True(detail.GetProperty("onSale").GetBoolean());
    }

    // ---- customers -------------------------------------------------------------------------------------------

    [Fact]
    public async Task Customers_are_masked_unless_the_key_has_pii_read()
    {
        var (admin, product) = await ProductAsync();
        var mobile = StoreHelper.NewMobile();
        var placed = await _o.PlaceAsync(product.Variants[0].Id, 1, mobile);
        var customerId = (await _o.OrderAsync(admin, placed.GetProperty("orderId").GetGuid())).GetProperty("customerId").GetGuid();
        var (_, plain, _) = await _i.CreateKeyAsync(admin, ["customers:read"]);
        var (_, withPii, _) = await _i.CreateKeyAsync(admin, ["customers:read", "pii:read"]);

        var masked = await _i.JsonWithKeyAsync(plain, $"/api/public/v1/customers/{customerId}");
        Assert.DoesNotContain(mobile, masked.GetRawText(), StringComparison.Ordinal);
        Assert.Contains("****", masked.GetProperty("mobile").GetString(), StringComparison.Ordinal);
        Assert.Equal(JsonValueKind.Null, masked.GetProperty("name").ValueKind);
        Assert.Equal(1, masked.GetProperty("orderCount").GetInt32());

        var full = await _i.JsonWithKeyAsync(withPii, $"/api/public/v1/customers/{customerId}");
        Assert.Equal("+88" + mobile, full.GetProperty("mobile").GetString());

        var list = await _i.JsonWithKeyAsync(plain, "/api/public/v1/customers?pageSize=100");
        Assert.DoesNotContain(mobile, list.GetRawText(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotFound, (await _i.GetWithKeyAsync(plain, $"/api/public/v1/customers/{Guid.NewGuid()}")).StatusCode);
    }

    // ---- documentation ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_public_api_has_its_own_openapi_document_and_the_staff_document_does_not_mix_with_it()
    {
        var pub = await TestApi.ExpectAsync(await _i.Api.GetAsync("/openapi/public-v1.json"), HttpStatusCode.OK);
        var staff = await TestApi.ExpectAsync(await _i.Api.GetAsync("/openapi/v1.json"), HttpStatusCode.OK);

        var pubPaths = pub.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();
        var staffPaths = staff.GetProperty("paths").EnumerateObject().Select(p => p.Name).ToList();

        Assert.Contains("/api/public/v1/orders", pubPaths);
        Assert.Contains("/api/public/v1/orders/{number}", pubPaths);
        Assert.Contains("/api/public/v1/products", pubPaths);
        Assert.Contains("/api/public/v1/products/{slug}", pubPaths);
        Assert.Contains("/api/public/v1/stock", pubPaths);
        Assert.All(pubPaths, p => Assert.StartsWith("/api/public/v1/", p, StringComparison.Ordinal));
        Assert.DoesNotContain(staffPaths, p => p.StartsWith("/api/public/", StringComparison.Ordinal));
        Assert.Contains(staffPaths, p => p.StartsWith("/api/v1/integrations/api-keys", StringComparison.Ordinal));
        Assert.Equal("Juvo BD public API", pub.GetProperty("info").GetProperty("title").GetString());
    }
}
