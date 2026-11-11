using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using static Juvo.Api.IntegrationTests.Couriers.CourierHelper;

namespace Juvo.Api.IntegrationTests.Couriers;

public class CourierAdminTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly CourierHelper _c = new(new OrderHelper(new StoreHelper(factory)), factory);

    private StoreHelper H => _c.H;

    private OrderHelper O => _c.O;

    private static object Save(string? code, string name, string provider = "SANDBOX", bool enabled = true, int priority = 50, bool cod = true, int? maxWeight = null, string[]? zones = null,
        decimal? chargeOverride = null, decimal baseRate = 40m, Dictionary<string, string>? settings = null, decimal perKg = 10m) =>
        new
        {
            code, name, provider, isEnabled = enabled, priority, codSupported = cod, supportsInternational = false, maxWeightGrams = maxWeight, supportedZoneCodes = zones ?? [], chargeOverride,
            baseRate, perExtraKgRate = perKg, baseRateUpToGrams = 1000, trackingUrlTemplate = "https://track.example.com/{consignment}", notes = "test", settings = settings ?? new Dictionary<string, string>(),
        };

    private async Task DeleteAsync(string admin, Guid id) =>
        Assert.Equal(HttpStatusCode.NoContent, (await H.SendAsync(HttpMethod.Delete, $"/api/v1/couriers/{id}", null, admin)).StatusCode);

    // ---- the list, the slots and what they answer -----------------------------------------------------------

    [Fact]
    public async Task The_sandbox_courier_is_on_and_the_six_real_companies_are_switched_off_slots()
    {
        var admin = await H.AdminAsync();
        var list = (await H.JsonAsync(HttpMethod.Get, "/api/v1/couriers", HttpStatusCode.OK, bearer: admin)).EnumerateArray().ToDictionary(c => Str(c, "code"));

        Assert.True(list["SANDBOX"].GetProperty("isEnabled").GetBoolean());
        Assert.True(list["SANDBOX"].GetProperty("providerConfigured").GetBoolean());
        Assert.True(list["SANDBOX"].GetProperty("isSimulator").GetBoolean());
        foreach (var code in new[] { "PATHAO", "STEADFAST", "REDX", "PAPERFLY", "ECOURIER", "DHL" })
        {
            Assert.False(list[code].GetProperty("isEnabled").GetBoolean(), code);
            Assert.False(list[code].GetProperty("providerConfigured").GetBoolean(), code);
            Assert.False(list[code].GetProperty("isSimulator").GetBoolean(), code);
        }

        var providers = await H.JsonAsync(HttpMethod.Get, "/api/v1/couriers/providers", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(7, providers.GetArrayLength());
        // nothing secret is ever part of a courier
        Assert.DoesNotContain("secret", list["SANDBOX"].GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_slot_without_keys_cannot_be_switched_on_or_used_and_says_so()
    {
        var admin = await H.AdminAsync();
        var pathao = await _c.CourierIdAsync(admin, "PATHAO");

        var on = await H.SendAsync(HttpMethod.Put, $"/api/v1/couriers/{pathao}/enabled", new { isEnabled = true }, admin);
        Assert.Equal(HttpStatusCode.Conflict, on.StatusCode);
        await StoreHelper.ExpectCodeAsync(on, "COURIER.NOT_CONFIGURED");

        var (_, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        await StoreHelper.ExpectCodeAsync(await _c.TryBookAsync(admin, order.OrderId, new { courierId = pathao }), "COURIER.DISABLED");
        var suggestion = (await H.JsonAsync(HttpMethod.Get, $"/api/v1/shipments/orders/{order.OrderId}/suggest", HttpStatusCode.OK, bearer: admin)).EnumerateArray().First(s => Str(s, "code") == "PATHAO");
        Assert.False(suggestion.GetProperty("eligible").GetBoolean());
        Assert.Contains("switched off", Str(suggestion, "reason"), StringComparison.Ordinal);

        // A new courier on an unset provider can be prepared (off), but not switched on.
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/couriers", Save("MYPATHAO", "My Pathao", "PATHAO", enabled: true), admin), "COURIER.NOT_CONFIGURED");
        var prepared = await H.JsonAsync(HttpMethod.Post, "/api/v1/couriers", HttpStatusCode.Created, Save("MYPATHAO", "My Pathao", "PATHAO", enabled: false), admin);
        await DeleteAsync(admin, prepared.GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task With_every_courier_off_booking_says_none_is_available()
    {
        var admin = await H.AdminAsync();
        var sandbox = await _c.CourierIdAsync(admin, "SANDBOX");
        var (_, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        try
        {
            await H.JsonAsync(HttpMethod.Put, $"/api/v1/couriers/{sandbox}/enabled", HttpStatusCode.OK, new { isEnabled = false }, admin);
            var refused = await _c.TryBookAsync(admin, order.OrderId);
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            await StoreHelper.ExpectCodeAsync(refused, "COURIER.NO_COURIER_AVAILABLE");
            await StoreHelper.ExpectCodeAsync(await _c.TryBookAsync(admin, order.OrderId, new { courierId = sandbox }), "COURIER.DISABLED");
        }
        finally
        {
            await H.JsonAsync(HttpMethod.Put, $"/api/v1/couriers/{sandbox}/enabled", HttpStatusCode.OK, new { isEnabled = true }, admin);
        }

        Assert.Equal("Booked", Str(await _c.BookAsync(admin, order.OrderId), "status"));
    }

    // ---- who can take a parcel ------------------------------------------------------------------------------

    [Fact]
    public async Task Weight_cash_and_zone_limits_decide_which_courier_is_suggested()
    {
        var admin = await H.AdminAsync();
        var (_, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id); // 300 g, cash on delivery
        var detail = await O.OrderAsync(admin, order.OrderId);
        var zone = Str(detail, "zoneCode");
        var zones = (await H.JsonAsync(HttpMethod.Get, "/api/v1/delivery/zones", HttpStatusCode.OK, bearer: admin)).EnumerateArray().Select(z => Str(z, "code")).Where(c => c != zone).ToArray();
        Assert.NotEmpty(zones);

        var light = await H.JsonAsync(HttpMethod.Post, "/api/v1/couriers", HttpStatusCode.Created, Save("LIGHT", "Light only", priority: 1, maxWeight: 100), admin);
        var noCod = await H.JsonAsync(HttpMethod.Post, "/api/v1/couriers", HttpStatusCode.Created, Save("NOCOD", "No cash", priority: 2, cod: false), admin);
        var elsewhereZone = await H.JsonAsync(HttpMethod.Post, "/api/v1/couriers", HttpStatusCode.Created, Save("OTHERZONE", "Other zone", priority: 3, zones: [zones[0]]), admin);
        var local = await H.JsonAsync(HttpMethod.Post, "/api/v1/couriers", HttpStatusCode.Created, Save("LOCAL", "Local zone", priority: 4, zones: [zone], baseRate: 30m), admin);
        var cheap = await H.JsonAsync(HttpMethod.Post, "/api/v1/couriers", HttpStatusCode.Created, Save("CHEAPER", "Same priority cheaper", priority: 4, zones: [zone], baseRate: 10m), admin);
        try
        {
            var suggestions = (await H.JsonAsync(HttpMethod.Get, $"/api/v1/shipments/orders/{order.OrderId}/suggest", HttpStatusCode.OK, bearer: admin)).EnumerateArray().ToList();
            JsonElement Of(string code) => suggestions.First(s => Str(s, "code") == code);

            Assert.False(Of("LIGHT").GetProperty("eligible").GetBoolean());
            Assert.Contains("at most 100 g", Str(Of("LIGHT"), "reason"), StringComparison.Ordinal);
            Assert.False(Of("NOCOD").GetProperty("eligible").GetBoolean());
            Assert.Contains("cash on delivery", Str(Of("NOCOD"), "reason"), StringComparison.Ordinal);
            Assert.False(Of("OTHERZONE").GetProperty("eligible").GetBoolean());
            Assert.Contains("zone", Str(Of("OTHERZONE"), "reason"), StringComparison.Ordinal);
            // eligible ones first, cheapest of the best priority recommended
            Assert.Equal("CHEAPER", Str(suggestions[0], "code"));
            Assert.True(suggestions[0].GetProperty("recommended").GetBoolean());
            Assert.Equal(10m, suggestions[0].GetProperty("cost").GetDecimal());
            Assert.Equal(["CHEAPER", "LOCAL", "SANDBOX"], suggestions.TakeWhile(s => s.GetProperty("eligible").GetBoolean()).Select(s => Str(s, "code")).ToArray());

            // Auto booking takes the recommendation; choosing an unfit courier by hand is refused with the reason.
            await StoreHelper.ExpectCodeAsync(await _c.TryBookAsync(admin, order.OrderId, new { courierId = light.GetProperty("id").GetGuid() }), "COURIER.WEIGHT_NOT_ALLOWED");
            await StoreHelper.ExpectCodeAsync(await _c.TryBookAsync(admin, order.OrderId, new { courierId = noCod.GetProperty("id").GetGuid() }), "COURIER.COD_NOT_SUPPORTED");
            await StoreHelper.ExpectCodeAsync(await _c.TryBookAsync(admin, order.OrderId, new { courierId = elsewhereZone.GetProperty("id").GetGuid() }), "COURIER.NOT_SERVICEABLE");
            var booked = await _c.BookAsync(admin, order.OrderId);
            Assert.Equal("CHEAPER", Str(booked, "courierCode"));
            Assert.Equal(10m, booked.GetProperty("cost").GetDecimal());
            // a courier with an open parcel cannot be archived: cancel the booking so the clean-up below works
            await H.JsonAsync(HttpMethod.Post, $"/api/v1/shipments/{booked.GetProperty("id").GetGuid()}/cancel", HttpStatusCode.OK, new { reason = "Test done" }, admin);
        }
        finally
        {
            foreach (var created in new[] { light, noCod, elsewhereZone, local, cheap })
            {
                var id = created.GetProperty("id").GetGuid();
                await H.SendAsync(HttpMethod.Delete, $"/api/v1/couriers/{id}", null, admin);
            }
        }
    }

    [Fact]
    public async Task Courier_data_is_validated_and_never_holds_secrets()
    {
        var admin = await H.AdminAsync();
        var sandbox = await _c.CourierIdAsync(admin, "SANDBOX");

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/couriers", Save("SANDBOX", "Again"), admin), "COURIER.CODE_TAKEN");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/couriers", Save("bad code!", "Bad"), admin), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/couriers", Save("NOPROV", "No provider", "NOSUCH"), admin), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/couriers", Save("NEG", "Negative", baseRate: -1m), admin), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/couriers", Save("ZON", "Zone", zones: ["NO-SUCH-ZONE"]), admin), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/couriers", Save("NOP", "Priority", priority: 0), admin), "GENERAL.VALIDATION");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, $"/api/v1/couriers/{sandbox}", Save(null, "Sandbox", settings: new Dictionary<string, string> { ["apiKey"] = "abc" }), admin), "COURIER.SETTING_INVALID");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, $"/api/v1/couriers/{sandbox}", Save(null, "Sandbox", settings: new Dictionary<string, string> { ["Client_Secret"] = "abc" }), admin), "COURIER.SETTING_INVALID");
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/couriers/{Guid.NewGuid()}", null, admin)).StatusCode);

        // A non-secret setting is stored and shown.
        var created = await H.JsonAsync(HttpMethod.Post, "/api/v1/couriers", HttpStatusCode.Created, Save("WITHSET", "With settings", settings: new Dictionary<string, string> { ["storeId"] = "12345" }), admin);
        Assert.Equal("12345", created.GetProperty("settings").GetProperty("storeId").GetString());
        var updated = await H.JsonAsync(HttpMethod.Put, $"/api/v1/couriers/{created.GetProperty("id").GetGuid()}", HttpStatusCode.OK, Save(null, "Renamed", priority: 77, baseRate: 55m), admin);
        Assert.Equal("Renamed", Str(updated, "name"));
        Assert.Equal(77, updated.GetProperty("priority").GetInt32());
        Assert.Equal(0, updated.GetProperty("openShipments").GetInt32());
        await DeleteAsync(admin, created.GetProperty("id").GetGuid());
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/couriers/{created.GetProperty("id").GetGuid()}", null, admin)).StatusCode);
    }

    [Fact]
    public async Task A_courier_with_parcels_cannot_be_archived()
    {
        var admin = await H.AdminAsync();
        var (_, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var sandbox = await _c.CourierIdAsync(admin, "SANDBOX");
        await _c.BookAsync(admin, order.OrderId);
        Assert.Equal(HttpStatusCode.Conflict, (await H.SendAsync(HttpMethod.Delete, $"/api/v1/couriers/{sandbox}", null, admin)).StatusCode);
        var one = await H.JsonAsync(HttpMethod.Get, $"/api/v1/couriers/{sandbox}", HttpStatusCode.OK, bearer: admin);
        Assert.True(one.GetProperty("openShipments").GetInt32() >= 1);
    }

    // ---- what the customer pays -----------------------------------------------------------------------------

    [Fact]
    public async Task The_charge_override_of_the_first_courier_is_the_same_number_in_the_quote_and_the_order()
    {
        var admin = await H.AdminAsync();
        var sandbox = await _c.CourierIdAsync(admin, "SANDBOX");
        var (_, product) = await _c.ProductAsync(500m);
        var district = await O.DistrictAsync();
        var quoteBody = new { countryCode = "BD", districtId = district, subtotal = 500m, weightGrams = 300 };

        var normal = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/delivery/quote", HttpStatusCode.OK, quoteBody);
        var normalCharge = normal.GetProperty("total").GetDecimal();
        try
        {
            await H.JsonAsync(HttpMethod.Put, $"/api/v1/couriers/{sandbox}", HttpStatusCode.OK, Save(null, "Sandbox courier", chargeOverride: 99m, baseRate: 60m, priority: 100), admin);
            var overridden = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/delivery/quote", HttpStatusCode.OK, quoteBody);
            Assert.Equal(99m, overridden.GetProperty("total").GetDecimal() - overridden.GetProperty("remoteSurcharge").GetDecimal());
            Assert.NotEqual(normalCharge, overridden.GetProperty("total").GetDecimal());

            var placed = await O.PlaceAsync(product.Variants[0].Id, 1);
            var order = await O.OrderAsync(admin, placed.GetProperty("orderId").GetGuid());
            Assert.Equal(overridden.GetProperty("total").GetDecimal(), order.GetProperty("money").GetProperty("deliveryCharge").GetDecimal());
        }
        finally
        {
            await H.JsonAsync(HttpMethod.Put, $"/api/v1/couriers/{sandbox}", HttpStatusCode.OK, Save(null, "Sandbox courier", chargeOverride: null, baseRate: 60m, priority: 100, perKg: 20m), admin);
        }

        Assert.Equal(normalCharge, (await H.JsonAsync(HttpMethod.Post, "/api/v1/store/delivery/quote", HttpStatusCode.OK, quoteBody)).GetProperty("total").GetDecimal());
    }

    // ---- permissions and branch isolation -------------------------------------------------------------------

    [Fact]
    public async Task Shipment_endpoints_need_their_own_permission_in_the_branch_of_the_parcel()
    {
        var admin = await H.AdminAsync();
        var ho = await H.Api.BranchIdAsync("HO");
        var other = await H.Api.CreateBranchAsync(admin, "SH" + Random.Shared.Next(100, 999), "Other shipping shop");
        var (_, product) = await _c.ProductAsync();
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var shipment = await _c.BookAsync(admin, order.OrderId);
        var id = shipment.GetProperty("id").GetGuid();

        var viewer = await _c.StaffAsync(admin, ho, "couriers.shipment.view");
        var booker = await _c.StaffAsync(admin, ho, "couriers.shipment.view", "couriers.shipment.book");
        var elsewhere = await _c.StaffAsync(admin, other, "couriers.shipment.view", "couriers.shipment.book", "couriers.shipment.cancel", "couriers.shipment.simulate", "couriers.shipment.print-label", "couriers.cod.view", "couriers.cod.settle");

        // The viewer sees but cannot act: no booking, cancel, label, simulation, bulk booking or settlement.
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, $"/api/v1/shipments/{id}", null, viewer)).StatusCode);
        Assert.Equal(1, (await _c.ListAsync(viewer, $"?orderId={order.OrderId}")).GetProperty("total").GetInt32());
        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Post, $"/api/v1/shipments/{id}/cancel"), (HttpMethod.Post, $"/api/v1/shipments/{id}/rebook"), (HttpMethod.Post, $"/api/v1/shipments/{id}/advance"),
                     (HttpMethod.Get, $"/api/v1/shipments/{id}/label"), (HttpMethod.Post, "/api/v1/shipments/bulk-book"), (HttpMethod.Get, "/api/v1/couriers/cod-settlements"),
                     (HttpMethod.Post, "/api/v1/couriers/cod-settlements"), (HttpMethod.Get, $"/api/v1/shipments/orders/{order.OrderId}/suggest"),
                 })
        {
            var denied = await H.SendAsync(method, url, method == HttpMethod.Post ? new { reason = "x", orderIds = new[] { order.OrderId } } : null, viewer);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/shipments/bulk-book", new { orderIds = new[] { order.OrderId } }, booker)).StatusCode); // book is not bulk-book
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/couriers", null, viewer)).StatusCode); // the courier list is its own permission
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/couriers", Save("NOPE", "Nope"), booker)).StatusCode);

        // A customer or an anonymous caller gets nothing either.
        var customer = await H.CustomerTokenAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/shipments", null, customer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, "/api/v1/shipments")).StatusCode);

        // Another branch: the parcel does not exist for them, in every call.
        Assert.Equal(0, (await _c.ListAsync(elsewhere, $"?orderId={order.OrderId}")).GetProperty("total").GetInt32());
        foreach (var (method, url) in new[]
                 {
                     (HttpMethod.Get, $"/api/v1/shipments/{id}"), (HttpMethod.Get, $"/api/v1/shipments/{id}/events"), (HttpMethod.Get, $"/api/v1/shipments/{id}/label"),
                     (HttpMethod.Post, $"/api/v1/shipments/{id}/cancel"), (HttpMethod.Post, $"/api/v1/shipments/{id}/advance"), (HttpMethod.Post, $"/api/v1/shipments/{id}/rebook"),
                 })
        {
            var hidden = await H.SendAsync(method, url, method == HttpMethod.Post ? new { reason = "peek" } : null, elsewhere);
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
            await StoreHelper.ExpectCodeAsync(hidden, "SHIPMENT.NOT_FOUND");
        }

        var bookElsewhere = await _c.TryBookAsync(elsewhere, order.OrderId);
        Assert.Equal(HttpStatusCode.NotFound, bookElsewhere.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"/api/v1/shipments/orders/{order.OrderId}/suggest", null, elsewhere)).StatusCode);
        // Settlements are per branch too: they cannot batch the other branch's cash.
        var courier = await _c.CourierIdAsync(admin, "SANDBOX");
        var batch = await H.SendAsync(HttpMethod.Post, "/api/v1/couriers/cod-settlements", new { courierId = courier, branchId = ho }, elsewhere);
        Assert.Equal(HttpStatusCode.NotFound, batch.StatusCode);
        Assert.Equal(0, (await H.JsonAsync(HttpMethod.Get, "/api/v1/couriers/cod-settlements", HttpStatusCode.OK, bearer: elsewhere)).GetProperty("total").GetInt32());

        // The shipment is still untouched.
        Assert.Equal("Booked", Str(await _c.ShipmentAsync(admin, id), "status"));
    }

    [Fact]
    public async Task The_default_roles_follow_the_permission_matrix()
    {
        var admin = await H.AdminAsync();
        var roles = (await H.JsonAsync(HttpMethod.Get, "/api/v1/ums/roles?pageSize=50", HttpStatusCode.OK, bearer: admin)).GetProperty("items").EnumerateArray().ToList();
        async Task<HashSet<string>> PermissionsOf(string name)
        {
            var id = roles.First(r => Str(r, "name") == name).GetProperty("id").GetGuid();
            var role = await H.JsonAsync(HttpMethod.Get, $"/api/v1/ums/roles/{id}", HttpStatusCode.OK, bearer: admin);
            return role.GetProperty("permissions").EnumerateArray().Select(p => p.GetString()!).ToHashSet();
        }

        var orderManager = await PermissionsOf("Order Manager");
        Assert.All(new[] { "couriers.shipment.view", "couriers.shipment.book", "couriers.shipment.bulk-book", "couriers.shipment.cancel", "couriers.shipment.print-label", "couriers.shipment.simulate", "couriers.courier.view", "couriers.cod.view" }, p => Assert.Contains(p, orderManager));
        Assert.DoesNotContain("couriers.cod.settle", orderManager);
        Assert.DoesNotContain("couriers.courier.edit", orderManager);

        var accountant = await PermissionsOf("Accountant");
        Assert.Contains("couriers.cod.settle", accountant);
        Assert.Contains("couriers.cod.view", accountant);
        Assert.DoesNotContain("couriers.shipment.book", accountant);

        var support = await PermissionsOf("Support");
        Assert.Contains("couriers.shipment.view", support);
        Assert.DoesNotContain("couriers.shipment.book", support);
        Assert.DoesNotContain("couriers.cod.settle", support);
    }
}
