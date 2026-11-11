using System.Net;
using System.Text.Json;

namespace Juvo.Api.IntegrationTests.Storefront;

/// <summary>Reference data: the loaded geography. Own fixture so the counts are exact.</summary>
public class GeoSeedTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly StoreHelper _h = new(factory);

    [Fact]
    public async Task Bangladesh_has_8_divisions_64_districts_and_494_upazilas_each_linked_to_its_parent()
    {
        var divisions = await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/geo/divisions", HttpStatusCode.OK);
        var districts = await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/geo/districts", HttpStatusCode.OK);
        Assert.Equal(8, divisions.GetArrayLength());
        Assert.Equal(64, districts.GetArrayLength());

        var divisionIds = divisions.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToHashSet();
        Assert.All(districts.EnumerateArray(), d => Assert.Contains(d.GetProperty("divisionId").GetGuid(), divisionIds));

        var total = 0;
        foreach (var district in districts.EnumerateArray())
        {
            var upazilas = await _h.JsonAsync(HttpMethod.Get, $"/api/v1/store/geo/districts/{district.GetProperty("id").GetGuid()}/upazilas", HttpStatusCode.OK);
            total += upazilas.GetArrayLength();
        }

        Assert.Equal(494, total);
    }

    [Fact]
    public async Task Districts_can_be_listed_for_one_division_and_countries_show_only_enabled_ones()
    {
        var divisions = await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/geo/divisions", HttpStatusCode.OK);
        var dhakaDivision = divisions.EnumerateArray().Single(d => d.GetProperty("nameEn").GetString() == "Dhaka").GetProperty("id").GetGuid();

        var districts = await _h.JsonAsync(HttpMethod.Get, $"/api/v1/store/geo/districts?divisionId={dhakaDivision}", HttpStatusCode.OK);
        Assert.Equal(13, districts.GetArrayLength());

        var countries = await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/geo/countries", HttpStatusCode.OK);
        var codes = countries.EnumerateArray().Select(c => c.GetProperty("code").GetString()).ToList();
        Assert.Contains("BD", codes);
        Assert.Contains("IN", codes);
        Assert.DoesNotContain("JP", codes);
    }
}

public class StoreDeliveryTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly StoreHelper _h = new(factory);

    private const string Quote = "/api/v1/store/delivery/quote";

    private async Task<Guid> DistrictIdAsync(string name)
    {
        var districts = await _h.JsonAsync(HttpMethod.Get, "/api/v1/store/geo/districts", HttpStatusCode.OK);
        return districts.EnumerateArray().Single(d => d.GetProperty("nameEn").GetString() == name).GetProperty("id").GetGuid();
    }

    private async Task<JsonElement> QuoteAsync(object body) => await _h.JsonAsync(HttpMethod.Post, Quote, HttpStatusCode.OK, body);

    [Fact]
    public async Task Dhaka_city_is_charged_the_base_fee_and_is_free_from_the_threshold()
    {
        var dhaka = await DistrictIdAsync("Dhaka");

        var small = await QuoteAsync(new { districtId = dhaka, subtotal = 1000m, weightGrams = 500 });
        Assert.Equal("DHAKA_CITY", small.GetProperty("zoneCode").GetString());
        Assert.Equal(70m, small.GetProperty("baseFee").GetDecimal());
        Assert.Equal(70m, small.GetProperty("total").GetDecimal());
        Assert.False(small.GetProperty("isFree").GetBoolean());
        Assert.Equal(4000m, small.GetProperty("amountToFreeDelivery").GetDecimal());

        var big = await QuoteAsync(new { districtId = dhaka, subtotal = 5000m, weightGrams = 500 });
        Assert.True(big.GetProperty("isFree").GetBoolean());
        Assert.Equal(0m, big.GetProperty("total").GetDecimal());
        Assert.Equal(70m, big.GetProperty("freeDeliveryWaiver").GetDecimal());

        var justUnder = await QuoteAsync(new { districtId = dhaka, subtotal = 4999.99m, weightGrams = 500 });
        Assert.False(justUnder.GetProperty("isFree").GetBoolean());
    }

    [Fact]
    public async Task The_zone_comes_from_the_district_and_an_upazila_can_override_it()
    {
        var dhaka = await DistrictIdAsync("Dhaka");
        var savar = (await _h.JsonAsync(HttpMethod.Get, $"/api/v1/store/geo/districts/{dhaka}/upazilas", HttpStatusCode.OK))
            .EnumerateArray().Single(u => u.GetProperty("nameEn").GetString() == "Savar").GetProperty("id").GetGuid();

        Assert.Equal("DHAKA_SUBURBS", (await QuoteAsync(new { districtId = dhaka, upazilaId = savar, subtotal = 100m, weightGrams = 100 })).GetProperty("zoneCode").GetString());
        Assert.Equal("DHAKA_SUBURBS", (await QuoteAsync(new { districtId = await DistrictIdAsync("Gazipur"), subtotal = 100m, weightGrams = 100 })).GetProperty("zoneCode").GetString());
        Assert.Equal("MAJOR_CITIES", (await QuoteAsync(new { districtId = await DistrictIdAsync("Sylhet"), subtotal = 100m, weightGrams = 100 })).GetProperty("zoneCode").GetString());
        Assert.Equal("REST_OF_BD", (await QuoteAsync(new { districtId = await DistrictIdAsync("Pabna"), subtotal = 100m, weightGrams = 100 })).GetProperty("zoneCode").GetString());
    }

    [Fact]
    public async Task A_remote_district_adds_a_surcharge_that_free_delivery_never_waives()
    {
        var remote = await DistrictIdAsync("Bandarban");

        var charged = await QuoteAsync(new { districtId = remote, subtotal = 100m, weightGrams = 100 });
        var surcharge = charged.GetProperty("remoteSurcharge").GetDecimal();
        Assert.True(surcharge > 0m);
        Assert.Equal(charged.GetProperty("baseFee").GetDecimal() + charged.GetProperty("weightFee").GetDecimal() + surcharge, charged.GetProperty("total").GetDecimal());

        var free = await QuoteAsync(new { districtId = remote, subtotal = 100000m, weightGrams = 100 });
        Assert.True(free.GetProperty("isFree").GetBoolean());
        Assert.Equal(surcharge, free.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task A_heavier_parcel_costs_more_through_the_weight_slabs()
    {
        var pabna = await DistrictIdAsync("Pabna");

        var light = await QuoteAsync(new { districtId = pabna, subtotal = 100m, weightGrams = 500 });
        var heavy = await QuoteAsync(new { districtId = pabna, subtotal = 100m, weightGrams = 9000 });

        Assert.True(heavy.GetProperty("total").GetDecimal() > light.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task The_quote_can_be_priced_from_items_and_uses_their_weight()
    {
        var admin = await _h.AdminAsync();
        var cat = await _h.CategoryAsync(admin);
        var product = await _h.ProductAsync(admin, cat.Id, StoreHelper.Unique("Heavy"), 1200m, [("Red", "M", 9)], weightGrams: 1500);

        var quote = await QuoteAsync(new { districtId = await DistrictIdAsync("Dhaka"), items = new[] { new { variantId = product.Variants[0].Id, quantity = 3 } } });

        Assert.Equal(3600m, quote.GetProperty("subtotal").GetDecimal());
        Assert.Equal(4500, quote.GetProperty("weightGrams").GetInt32());
    }

    [Fact]
    public async Task International_delivery_is_priced_by_country_and_refused_where_disabled_or_too_heavy()
    {
        var india = await QuoteAsync(new { countryCode = "IN", subtotal = 3000m, weightGrams = 1000 });
        Assert.Equal("INTL_SOUTH_ASIA", india.GetProperty("zoneCode").GetString());
        Assert.Equal(1200m, india.GetProperty("baseFee").GetDecimal());
        Assert.True(india.GetProperty("dutiesPaidByReceiver").GetBoolean());
        Assert.True(india.GetProperty("daysMin").GetInt32() >= 7);

        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Post, Quote, new { countryCode = "JP", subtotal = 100m, weightGrams = 100 }), "DELIVERY.COUNTRY_DISABLED");
        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Post, Quote, new { countryCode = "ZZ", subtotal = 100m, weightGrams = 100 }), "DELIVERY.COUNTRY_NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Post, Quote, new { countryCode = "IN", subtotal = 100m, weightGrams = 25000 }), "DELIVERY.OVERWEIGHT");
    }

    [Fact]
    public async Task A_quote_without_a_place_is_rejected()
    {
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.SendAsync(HttpMethod.Post, Quote, new { subtotal = 100m, weightGrams = 100 })).StatusCode);
    }

    [Fact]
    public async Task Staff_can_change_the_rates_and_the_next_quote_uses_them()
    {
        var admin = await _h.AdminAsync();
        var zones = await _h.JsonAsync(HttpMethod.Get, "/api/v1/delivery/zones", HttpStatusCode.OK, bearer: admin);
        var zone = zones.EnumerateArray().Single(z => z.GetProperty("code").GetString() == "MAJOR_CITIES");
        var sylhet = await DistrictIdAsync("Sylhet");
        var before = (await QuoteAsync(new { districtId = sylhet, subtotal = 100m, weightGrams = 100 })).GetProperty("baseFee").GetDecimal();

        await _h.JsonAsync(HttpMethod.Put, $"/api/v1/delivery/zones/{zone.GetProperty("id").GetGuid()}/rates", HttpStatusCode.OK, new
        {
            baseFee = before + 15m,
            freeDeliveryThreshold = zone.GetProperty("freeDeliveryThreshold").ValueKind == JsonValueKind.Null ? (decimal?)null : zone.GetProperty("freeDeliveryThreshold").GetDecimal(),
            remoteSurcharge = zone.GetProperty("remoteSurcharge").GetDecimal(),
            perExtraKgFee = zone.GetProperty("perExtraKgFee").GetDecimal(),
            maxWeightGrams = (int?)null,
            slabs = zone.GetProperty("slabs").EnumerateArray().Select(s => new { upToGrams = s.GetProperty("upToGrams").GetInt32(), extraFee = s.GetProperty("extraFee").GetDecimal() }).ToArray(),
        }, admin);

        var after = (await QuoteAsync(new { districtId = sylhet, subtotal = 100m, weightGrams = 100 })).GetProperty("baseFee").GetDecimal();
        Assert.Equal(before + 15m, after);
    }

    [Fact]
    public async Task A_new_zone_takes_districts_from_the_zone_that_held_them()
    {
        var admin = await _h.AdminAsync();
        var code = StoreHelper.Unique("Z").ToUpperInvariant();
        var created = await _h.JsonAsync(HttpMethod.Post, "/api/v1/delivery/zones", HttpStatusCode.Created,
            new { code, nameEn = "Test zone", kind = "Domestic", daysMin = 1, daysMax = 2 }, admin);
        var id = created.GetProperty("id").GetGuid();
        var rangpur = await DistrictIdAsync("Rangpur");

        await _h.JsonAsync(HttpMethod.Put, $"/api/v1/delivery/zones/{id}/rates", HttpStatusCode.OK,
            new { baseFee = 33m, freeDeliveryThreshold = (decimal?)null, remoteSurcharge = 0m, perExtraKgFee = 0m, maxWeightGrams = (int?)null, slabs = Array.Empty<object>() }, admin);
        await _h.JsonAsync(HttpMethod.Put, $"/api/v1/delivery/zones/{id}/areas", HttpStatusCode.OK,
            new { areas = new[] { new { districtId = rangpur, upazilaId = (Guid?)null, isRemote = false } } }, admin);

        var quote = await QuoteAsync(new { districtId = rangpur, subtotal = 100m, weightGrams = 100 });
        Assert.Equal(code, quote.GetProperty("zoneCode").GetString());
        Assert.Equal(33m, quote.GetProperty("total").GetDecimal());

        Assert.Equal(HttpStatusCode.NoContent, (await _h.SendAsync(HttpMethod.Delete, $"/api/v1/delivery/zones/{id}", bearer: admin)).StatusCode);
        Assert.Equal("REST_OF_BD", (await QuoteAsync(new { districtId = rangpur, subtotal = 100m, weightGrams = 100 })).GetProperty("zoneCode").GetString());
    }

    [Fact]
    public async Task The_fallback_zone_cannot_be_deleted_and_zone_codes_are_unique()
    {
        var admin = await _h.AdminAsync();
        var zones = await _h.JsonAsync(HttpMethod.Get, "/api/v1/delivery/zones", HttpStatusCode.OK, bearer: admin);
        var fallback = zones.EnumerateArray().Single(z => z.GetProperty("isFallback").GetBoolean());

        var refused = await _h.SendAsync(HttpMethod.Delete, $"/api/v1/delivery/zones/{fallback.GetProperty("id").GetGuid()}", bearer: admin);
        Assert.True((int)refused.StatusCode is >= 400 and < 500);

        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Post, "/api/v1/delivery/zones",
            new { code = "DHAKA_CITY", nameEn = "Again", kind = "Domestic" }, admin), "DELIVERY.ZONE_CODE_TAKEN");
    }

    [Fact]
    public async Task Staff_can_enable_a_country_and_price_it_with_an_international_zone()
    {
        var admin = await _h.AdminAsync();
        var countries = await _h.JsonAsync(HttpMethod.Get, "/api/v1/delivery/countries", HttpStatusCode.OK, bearer: admin);
        var us = countries.EnumerateArray().Single(c => c.GetProperty("code").GetString() == "US");
        var zones = await _h.JsonAsync(HttpMethod.Get, "/api/v1/delivery/zones", HttpStatusCode.OK, bearer: admin);
        var rest = zones.EnumerateArray().Single(z => z.GetProperty("code").GetString() == "INTL_REST");
        await _h.JsonAsync(HttpMethod.Put, $"/api/v1/delivery/zones/{rest.GetProperty("id").GetGuid()}/rates", HttpStatusCode.OK,
            new { baseFee = 2500m, freeDeliveryThreshold = (decimal?)null, remoteSurcharge = 0m, perExtraKgFee = 0m, maxWeightGrams = 20000, slabs = Array.Empty<object>() }, admin);
        await _h.JsonAsync(HttpMethod.Put, $"/api/v1/delivery/zones/{rest.GetProperty("id").GetGuid()}", HttpStatusCode.OK,
            new { nameEn = rest.GetProperty("nameEn").GetString(), nameBn = (string?)null, isActive = true, isFallback = false, sortOrder = 0, daysMin = 10, daysMax = 20 }, admin);

        await _h.JsonAsync(HttpMethod.Put, $"/api/v1/delivery/countries/{us.GetProperty("id").GetGuid()}", HttpStatusCode.OK,
            new { code = "US", nameEn = us.GetProperty("nameEn").GetString(), nameBn = (string?)null, phoneCode = us.GetProperty("phoneCode").GetString(), isEnabled = true, zoneId = rest.GetProperty("id").GetGuid() }, admin);

        var quote = await QuoteAsync(new { countryCode = "US", subtotal = 100m, weightGrams = 500 });
        Assert.Equal("INTL_REST", quote.GetProperty("zoneCode").GetString());
        Assert.Equal(2500m, quote.GetProperty("total").GetDecimal());
    }

    [Fact]
    public async Task Importing_upazilas_adds_new_ones_and_skips_existing_names()
    {
        var admin = await _h.AdminAsync();
        var pabna = await DistrictIdAsync("Pabna");
        var name = StoreHelper.Unique("Newtown");

        var first = await _h.JsonAsync(HttpMethod.Post, "/api/v1/delivery/upazilas/import", HttpStatusCode.OK,
            new { items = new[] { new { districtId = pabna, nameEn = name, nameBn = (string?)null } } }, admin);
        Assert.Equal((1, 0), (first.GetProperty("added").GetInt32(), first.GetProperty("skipped").GetInt32()));

        var second = await _h.JsonAsync(HttpMethod.Post, "/api/v1/delivery/upazilas/import", HttpStatusCode.OK,
            new { items = new[] { new { districtId = pabna, nameEn = name.ToUpperInvariant(), nameBn = (string?)null } } }, admin);
        Assert.Equal((0, 1), (second.GetProperty("added").GetInt32(), second.GetProperty("skipped").GetInt32()));

        var upazilas = await _h.JsonAsync(HttpMethod.Get, $"/api/v1/store/geo/districts/{pabna}/upazilas", HttpStatusCode.OK);
        Assert.Contains(upazilas.EnumerateArray(), u => u.GetProperty("nameEn").GetString() == name);
    }

    [Fact]
    public async Task The_delivery_admin_endpoints_need_their_permissions()
    {
        var admin = await _h.AdminAsync();
        var role = await _h.Api.CreateRoleAsync(admin, StoreHelper.Unique("Viewer"), "delivery.zone.view");
        var ho = await _h.Api.BranchIdAsync("HO");
        var user = await _h.Api.CreateUserAsync(admin, $"{StoreHelper.Unique("dv")}@example.com", "Zone Viewer", false, (ho, role));
        var token = (await _h.Api.LoginAsync(user.Email, user.Password)).Access;

        Assert.Equal(HttpStatusCode.OK, (await _h.SendAsync(HttpMethod.Get, "/api/v1/delivery/zones", bearer: token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Post, "/api/v1/delivery/zones", new { code = "NOPE", nameEn = "x", kind = "Domestic" }, token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Get, "/api/v1/delivery/countries", bearer: token)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _h.SendAsync(HttpMethod.Get, "/api/v1/delivery/zones")).StatusCode);
    }
}
