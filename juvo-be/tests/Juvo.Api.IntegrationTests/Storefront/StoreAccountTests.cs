using System.Net;
using System.Text.Json;

namespace Juvo.Api.IntegrationTests.Storefront;

public class StoreAccountTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly StoreHelper _h = new(factory);

    private const string Me = "/api/v1/me";

    private async Task<Guid> DistrictIdAsync(string name)
    {
        var districts = await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/geo/districts", HttpStatusCode.OK);
        return districts.EnumerateArray().Single(d => d.GetProperty("nameEn").GetString() == name).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> AddAddressAsync(string customer, Guid districtId, string? label = null, bool isDefault = false) =>
        await _h.JsonAsync(HttpMethod.Post, Me + "/addresses", HttpStatusCode.Created, new
        {
            label, recipientName = "Rahim Uddin", phone = "01712345678", districtId, addressLine = "House 5, Road 3, Block B", isDefault,
        }, customer);

    [Fact]
    public async Task The_account_endpoints_need_a_customer_sign_in_and_refuse_staff()
    {
        var admin = await _h.AdminAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, (await _h.SendAsync(HttpMethod.Get, Me)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Get, Me, bearer: admin)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _h.SendAsync(HttpMethod.Get, Me + "/addresses")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _h.SendAsync(HttpMethod.Get, Me + "/wishlist")).StatusCode);
    }

    [Fact]
    public async Task A_customer_can_read_and_change_their_profile_but_not_their_mobile()
    {
        var mobile = StoreHelper.NewMobile();
        var customer = await _h.CustomerTokenAsync(mobile);

        var profile = await _h.JsonAsync(HttpMethod.Get, Me, HttpStatusCode.OK, bearer: customer);
        Assert.Equal("+88" + mobile, profile.GetProperty("mobile").GetString());

        var changed = await _h.JsonAsync(HttpMethod.Put, Me, HttpStatusCode.OK, new { fullName = "Karim Hossain", email = "karim@example.com" }, customer);
        Assert.Equal("Karim Hossain", changed.GetProperty("fullName").GetString());
        Assert.Equal("karim@example.com", changed.GetProperty("email").GetString());
        Assert.Equal("+88" + mobile, changed.GetProperty("mobile").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await _h.SendAsync(HttpMethod.Put, Me, new { fullName = "K", email = "not-an-email" }, customer)).StatusCode);
    }

    [Fact]
    public async Task Consent_changes_are_recorded_and_can_be_withdrawn()
    {
        var customer = await _h.CustomerTokenAsync();

        var granted = await _h.JsonAsync(HttpMethod.Put, Me + "/consent", HttpStatusCode.OK, new { type = "MarketingSms", granted = true }, customer);
        var sms = granted.EnumerateArray().Single(c => c.GetProperty("type").GetString() == "MarketingSms");
        Assert.True(sms.GetProperty("granted").GetBoolean());
        Assert.NotEqual(JsonValueKind.Null, sms.GetProperty("at").ValueKind);

        var withdrawn = await _h.JsonAsync(HttpMethod.Put, Me + "/consent", HttpStatusCode.OK, new { type = "MarketingSms", granted = false }, customer);
        Assert.False(withdrawn.EnumerateArray().Single(c => c.GetProperty("type").GetString() == "MarketingSms").GetProperty("granted").GetBoolean());
    }

    [Fact]
    public async Task The_first_address_becomes_the_default_and_a_new_default_replaces_it()
    {
        var customer = await _h.CustomerTokenAsync();
        var dhaka = await DistrictIdAsync("Dhaka");
        var pabna = await DistrictIdAsync("Pabna");

        var first = await AddAddressAsync(customer, dhaka, "Home");
        Assert.True(first.GetProperty("isDefault").GetBoolean());
        Assert.Equal("8801712345678", first.GetProperty("phone").GetString()!.TrimStart('+'));

        var second = await AddAddressAsync(customer, pabna, "Office", isDefault: true);
        Assert.True(second.GetProperty("isDefault").GetBoolean());

        var list = await _h.JsonAsync(HttpMethod.Get, Me + "/addresses", HttpStatusCode.OK, bearer: customer);
        Assert.Equal(2, list.GetArrayLength());
        Assert.Equal("Office", list[0].GetProperty("label").GetString());          // default first
        Assert.Single(list.EnumerateArray(), a => a.GetProperty("isDefault").GetBoolean());

        await _h.JsonAsync(HttpMethod.Post, $"{Me}/addresses/{first.GetProperty("id").GetGuid()}/default", HttpStatusCode.OK, bearer: customer);
        var after = await _h.JsonAsync(HttpMethod.Get, Me + "/addresses", HttpStatusCode.OK, bearer: customer);
        Assert.Equal("Home", after[0].GetProperty("label").GetString());
    }

    [Fact]
    public async Task Deleting_the_default_address_promotes_another_one()
    {
        var customer = await _h.CustomerTokenAsync();
        var dhaka = await DistrictIdAsync("Dhaka");
        var first = await AddAddressAsync(customer, dhaka, "Home");
        await AddAddressAsync(customer, dhaka, "Office");

        Assert.Equal(HttpStatusCode.NoContent, (await _h.SendAsync(HttpMethod.Delete, $"{Me}/addresses/{first.GetProperty("id").GetGuid()}", bearer: customer)).StatusCode);

        var list = await _h.JsonAsync(HttpMethod.Get, Me + "/addresses", HttpStatusCode.OK, bearer: customer);
        Assert.Single(list.EnumerateArray());
        Assert.True(list[0].GetProperty("isDefault").GetBoolean());
    }

    [Fact]
    public async Task Address_validation_checks_the_phone_district_upazila_and_the_map_pin()
    {
        var customer = await _h.CustomerTokenAsync();
        var dhaka = await DistrictIdAsync("Dhaka");
        var pabna = await DistrictIdAsync("Pabna");
        var savar = (await _h.JsonAsync(HttpMethod.Get, $"/api/v1/store/geo/districts/{dhaka}/upazilas", HttpStatusCode.OK))
            .EnumerateArray().Single(u => u.GetProperty("nameEn").GetString() == "Savar").GetProperty("id").GetGuid();

        async Task<HttpStatusCode> TryAsync(object body) => (await _h.SendAsync(HttpMethod.Post, Me + "/addresses", body, customer)).StatusCode;

        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new { recipientName = "A", phone = "123", districtId = dhaka, addressLine = "x" }));
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new { recipientName = "A", phone = "01712345678", addressLine = "x" })); // no district
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new { recipientName = "A", phone = "01712345678", districtId = pabna, upazilaId = savar, addressLine = "x" })); // wrong district
        Assert.Equal(HttpStatusCode.BadRequest, await TryAsync(new { recipientName = "A", phone = "01712345678", districtId = dhaka, addressLine = "x", latitude = 23.8 })); // half a pin
        Assert.Equal(HttpStatusCode.Created, await TryAsync(new { recipientName = "A", phone = "01712345678", districtId = dhaka, upazilaId = savar, addressLine = "x", latitude = 23.8, longitude = 90.4 }));
    }

    [Fact]
    public async Task An_address_abroad_needs_a_city_and_no_district()
    {
        var customer = await _h.CustomerTokenAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await _h.SendAsync(HttpMethod.Post, Me + "/addresses",
            new { recipientName = "A", phone = "+919876543210", countryCode = "IN", addressLine = "12 MG Road" }, customer)).StatusCode);

        var created = await _h.JsonAsync(HttpMethod.Post, Me + "/addresses", HttpStatusCode.Created,
            new { recipientName = "A", phone = "+919876543210", countryCode = "IN", city = "Kolkata", stateProvince = "West Bengal", postalCode = "700001", addressLine = "12 MG Road" }, customer);

        Assert.Equal("IN", created.GetProperty("countryCode").GetString());
        Assert.Equal(JsonValueKind.Null, created.GetProperty("districtId").ValueKind);
    }

    [Fact]
    public async Task One_customer_cannot_read_change_or_delete_anothers_address()
    {
        var a = await _h.CustomerTokenAsync();
        var b = await _h.CustomerTokenAsync();
        var dhaka = await DistrictIdAsync("Dhaka");
        var address = await AddAddressAsync(a, dhaka);
        var id = address.GetProperty("id").GetGuid();

        Assert.Empty((await _h.JsonAsync(HttpMethod.Get, Me + "/addresses", HttpStatusCode.OK, bearer: b)).EnumerateArray());
        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Delete, $"{Me}/addresses/{id}", bearer: b), "CUSTOMER.ADDRESS_NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Post, $"{Me}/addresses/{id}/default", bearer: b), "CUSTOMER.ADDRESS_NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Put, $"{Me}/addresses/{id}",
            new { recipientName = "Hacker", phone = "01712345678", districtId = dhaka, addressLine = "x" }, b), "CUSTOMER.ADDRESS_NOT_FOUND");

        Assert.Equal("Rahim Uddin", (await _h.JsonAsync(HttpMethod.Get, Me + "/addresses", HttpStatusCode.OK, bearer: a))[0].GetProperty("recipientName").GetString());
    }

    [Fact]
    public async Task The_wishlist_keeps_a_product_once_and_is_private()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        var product = await _h.ProductAsync(admin, cat.Id, StoreHelper.Unique("Wish"), 500m, [("Red", "M", 5)]);
        var a = await _h.CustomerTokenAsync();
        var b = await _h.CustomerTokenAsync();

        Assert.Equal(HttpStatusCode.NoContent, (await _h.SendAsync(HttpMethod.Post, Me + "/wishlist", new { productId = product.Id }, a)).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await _h.SendAsync(HttpMethod.Post, Me + "/wishlist", new { productId = product.Id }, a)).StatusCode);

        var mine = await _h.JsonAsync(HttpMethod.Get, Me + "/wishlist", HttpStatusCode.OK, bearer: a);
        Assert.Single(mine.EnumerateArray());
        Assert.Equal(product.Slug, mine[0].GetProperty("slug").GetString());
        Assert.Empty((await _h.JsonAsync(HttpMethod.Get, Me + "/wishlist", HttpStatusCode.OK, bearer: b)).EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await _h.SendAsync(HttpMethod.Delete, $"{Me}/wishlist/{product.Id}", bearer: a)).StatusCode);
        Assert.Empty((await _h.JsonAsync(HttpMethod.Get, Me + "/wishlist", HttpStatusCode.OK, bearer: a)).EnumerateArray());
    }

    [Fact]
    public async Task Moving_a_wishlist_product_to_the_cart_takes_it_off_the_wishlist()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        var product = await _h.ProductAsync(admin, cat.Id, StoreHelper.Unique("Move"), 250m, [("Red", "M", 5)]);
        var customer = await _h.CustomerTokenAsync();
        await _h.SendAsync(HttpMethod.Post, Me + "/wishlist", new { productId = product.Id }, customer);

        var cart = await _h.JsonAsync(HttpMethod.Post, $"{Me}/wishlist/{product.Id}/move-to-cart", HttpStatusCode.OK, new { variantId = product.Variants[0].Id, quantity = 2 }, customer);

        Assert.Equal(500m, cart.GetProperty("subtotal").GetDecimal());
        Assert.Empty((await _h.JsonAsync(HttpMethod.Get, Me + "/wishlist", HttpStatusCode.OK, bearer: customer)).EnumerateArray());
    }

    [Fact]
    public async Task The_summary_counts_addresses_wishlist_and_cart_items()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        var product = await _h.ProductAsync(admin, cat.Id, StoreHelper.Unique("Sum"), 250m, [("Red", "M", 5)]);
        var customer = await _h.CustomerTokenAsync();
        await AddAddressAsync(customer, await DistrictIdAsync("Dhaka"));
        await _h.SendAsync(HttpMethod.Post, Me + "/wishlist", new { productId = product.Id }, customer);
        await _h.JsonAsync(HttpMethod.Post, "/api/v1/store/cart/items", HttpStatusCode.OK, new { variantId = product.Variants[0].Id, quantity = 3 }, customer);

        var summary = await _h.JsonAsync(HttpMethod.Get, Me + "/summary", HttpStatusCode.OK, bearer: customer);

        Assert.Equal(1, summary.GetProperty("addressCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("wishlistCount").GetInt32());
        Assert.Equal(3, summary.GetProperty("cartItemCount").GetInt32());
        Assert.Equal(0, summary.GetProperty("orderCount").GetInt32());
    }
}
