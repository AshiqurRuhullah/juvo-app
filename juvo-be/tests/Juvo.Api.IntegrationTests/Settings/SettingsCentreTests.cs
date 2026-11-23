using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Loyalty;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Application.Settings;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Settings;

/// <summary>The settings centre over the real API and database: list, change, validate, reset, permissions, cache, the shared store and two wired consumers (Loyalty.Enabled, Inventory.DeductAt).</summary>
public class SettingsCentreTests(LoyaltyFactory factory) : IClassFixture<LoyaltyFactory>
{
    private const string Url = "/api/v1/settings";

    private readonly LoyaltyHelper _l = new(factory);

    private StoreHelper H => _l.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonElement Find(JsonElement list, string key) =>
        list.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("settings").EnumerateArray()).Single(s => s.GetProperty("key").GetString() == key);

    private Task<JsonElement> SetAsync(string admin, string key, object value, HttpStatusCode expected = HttpStatusCode.OK, string? version = null) =>
        H.JsonAsync(HttpMethod.Put, $"{Url}/{key}", expected, new { value, version }, admin);

    private Task<JsonElement> ResetAsync(string admin, string key) => H.JsonAsync(HttpMethod.Delete, $"{Url}/{key}", HttpStatusCode.OK, bearer: admin);

    [Fact]
    public async Task The_list_is_grouped_shows_defaults_sources_and_what_the_caller_may_change_and_never_leaks_a_sensitive_value()
    {
        var admin = await H.AdminAsync();
        var list = await H.JsonAsync(HttpMethod.Get, Url, HttpStatusCode.OK, bearer: admin);

        var groups = list.GetProperty("groups").EnumerateArray().Select(g => g.GetProperty("group").GetString()).ToList();
        Assert.Contains("features", groups);
        Assert.Contains("payments", groups);
        Assert.Contains("security", groups);

        var loyalty = Find(list, "Loyalty.Enabled");
        Assert.Equal("bool", loyalty.GetProperty("type").GetString());
        Assert.False(loyalty.GetProperty("value").GetBoolean());
        Assert.False(loyalty.GetProperty("default").GetBoolean());
        Assert.Equal("default", loyalty.GetProperty("source").GetString());
        Assert.True(loyalty.GetProperty("canEdit").GetBoolean());
        Assert.Equal("settings.feature.edit", loyalty.GetProperty("editPermission").GetString());
        Assert.False(string.IsNullOrWhiteSpace(loyalty.GetProperty("descriptionBn").GetString()));

        var deduct = Find(list, "Inventory.DeductAt");
        Assert.Equal("Packing", deduct.GetProperty("value").GetString());
        Assert.Equal(["Packing", "Delivery"], deduct.GetProperty("allowedValues").EnumerateArray().Select(v => v.GetString()).ToArray());

        // Fixed server values are shown, marked read-only and come from the configuration.
        var otp = Find(list, "Auth.Otp.Length");
        Assert.Equal("appsettings", otp.GetProperty("source").GetString());
        Assert.True(otp.GetProperty("readOnly").GetBoolean());
        Assert.False(otp.GetProperty("canEdit").GetBoolean());
        Assert.Equal("BDT", Find(list, "Currency.Store").GetProperty("value").GetString());

        // No sensitive setting exists yet; if one is added its value must be absent from the list.
        foreach (var s in list.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("settings").EnumerateArray()).Where(s => s.GetProperty("sensitive").GetBoolean()))
        {
            Assert.Equal(JsonValueKind.Null, s.GetProperty("value").ValueKind);
        }
    }

    [Fact]
    public async Task A_change_takes_effect_at_once_on_the_next_read_and_a_reset_brings_the_default_back_for_the_rewards_flag()
    {
        var admin = await H.AdminAsync();
        var customer = await _l.CustomerAsync();
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, "/api/v1/me/rewards", null, customer.Token), "LOYALTY.FEATURE_OFF"); // reads (and caches) the default
        try
        {
            var changed = await SetAsync(admin, "Loyalty.Enabled", true);
            Assert.True(changed.GetProperty("value").GetBoolean());
            Assert.Equal("db", changed.GetProperty("source").GetString());
            Assert.True(changed.GetProperty("isSet").GetBoolean());
            Assert.False(string.IsNullOrEmpty(changed.GetProperty("version").GetString()));

            Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, "/api/v1/me/rewards", null, customer.Token)).StatusCode); // the consumer sees it at once
            var staff = await H.JsonAsync(HttpMethod.Get, "/api/v1/loyalty/settings", HttpStatusCode.OK, bearer: admin); // the module's own endpoint reads the same row
            Assert.True(staff.GetProperty("enabled").GetBoolean());

            var reset = await ResetAsync(admin, "Loyalty.Enabled");
            Assert.False(reset.GetProperty("value").GetBoolean());
            Assert.Equal("default", reset.GetProperty("source").GetString());
            await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, "/api/v1/me/rewards", null, customer.Token), "LOYALTY.FEATURE_OFF");
            await ResetAsync(admin, "Loyalty.Enabled"); // resetting a setting that has no row is harmless
        }
        finally
        {
            await ResetAsync(admin, "Loyalty.Enabled");
        }
    }

    [Fact]
    public async Task A_change_made_through_the_module_endpoint_is_seen_by_the_centre_at_once()
    {
        var admin = await H.AdminAsync();
        try
        {
            Assert.False((await H.JsonAsync(HttpMethod.Get, $"{Url}/Loyalty.MaxTenderPercent", HttpStatusCode.OK, bearer: admin)).GetProperty("isSet").GetBoolean());
            await H.JsonAsync(HttpMethod.Put, "/api/v1/loyalty/settings", HttpStatusCode.OK, new { maxTenderPercent = 35 }, admin);
            var one = await H.JsonAsync(HttpMethod.Get, $"{Url}/Loyalty.MaxTenderPercent", HttpStatusCode.OK, bearer: admin);
            Assert.Equal(35, one.GetProperty("value").GetInt32());
            Assert.Equal("db", one.GetProperty("source").GetString());
        }
        finally
        {
            await ResetAsync(admin, "Loyalty.MaxTenderPercent");
        }
    }

    [Fact]
    public async Task Payment_methods_are_read_and_written_in_the_same_store_as_the_payment_settings_endpoint()
    {
        var admin = await H.AdminAsync();
        try
        {
            await SetAsync(admin, "Payments.MANUAL.Enabled", true);
            var payments = await H.JsonAsync(HttpMethod.Get, "/api/v1/payments/settings", HttpStatusCode.OK, bearer: admin);
            Assert.True(payments.GetProperty("methods").EnumerateArray().Single(m => m.GetProperty("code").GetString() == "MANUAL").GetProperty("enabled").GetBoolean());

            await H.JsonAsync(HttpMethod.Put, "/api/v1/payments/settings", HttpStatusCode.OK, new { codMaxAmount = 5000 }, admin);
            Assert.Equal(5000m, (await H.JsonAsync(HttpMethod.Get, $"{Url}/Payments.Cod.MaxAmount", HttpStatusCode.OK, bearer: admin)).GetProperty("value").GetDecimal());
        }
        finally
        {
            await ResetAsync(admin, "Payments.MANUAL.Enabled");
            await ResetAsync(admin, "Payments.Cod.MaxAmount");
        }
    }

    [Fact]
    public async Task Unknown_keys_bad_values_and_read_only_settings_are_refused_with_stable_codes()
    {
        var admin = await H.AdminAsync();
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, $"{Url}/Nope.Nothing", new { value = 1 }, admin), "SETTINGS.UNKNOWN_KEY");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Delete, $"{Url}/Nope.Nothing", null, admin), "SETTINGS.UNKNOWN_KEY");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, $"{Url}/Nope.Nothing", null, admin), "SETTINGS.UNKNOWN_KEY");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, $"{Url}/Auth.Otp.Length", new { value = 8 }, admin), "SETTINGS.READ_ONLY");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Delete, $"{Url}/Currency.Store", null, admin), "SETTINGS.READ_ONLY");

        foreach (var (key, value) in new (string, object)[]
                 {
                     ("Loyalty.Enabled", "maybe"), ("Loyalty.MaxTenderPercent", 101), ("Loyalty.MaxTenderPercent", -1), ("Loyalty.MaxTenderPercent", 12.5), ("Loyalty.PointValue", 0),
                     ("Inventory.DeductAt", "Never"), ("Inventory.DeductAt", 1), ("Notifications.DefaultLanguage", "fr"), ("Business.Email", "not an email"),
                     ("Marketing.QuietHours.Start", "25:00"), ("Business.Name", new string('x', 151)),
                 })
        {
            var response = await H.SendAsync(HttpMethod.Put, $"{Url}/{key}", new { value }, admin);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var body = await response.Content.ReadAsStringAsync(Ct);
            Assert.Contains("SETTINGS.INVALID_VALUE", body);
        }

        // A value that is not a plain JSON value is refused too, and nothing was stored by any refused call.
        Assert.Equal(HttpStatusCode.BadRequest, (await H.SendAsync(HttpMethod.Put, $"{Url}/Loyalty.Enabled", new { value = new[] { 1 } }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await H.SendAsync(HttpMethod.Put, $"{Url}/Loyalty.Enabled", new { value = (object?)null }, admin)).StatusCode);
        Assert.False(await StoredAsync("Loyalty.Enabled"));
        Assert.False(await StoredAsync("Inventory.DeductAt"));
    }

    [Fact]
    public async Task Viewing_changing_and_the_per_setting_permission_are_enforced()
    {
        var admin = await H.AdminAsync();
        var viewer = await _l.O.StaffTokenAsync(admin, null, "settings.system.view");
        var systemEditor = await _l.O.StaffTokenAsync(admin, null, "settings.system.view", "settings.system.edit");
        var featureEditor = await _l.O.StaffTokenAsync(admin, null, "settings.system.view", "settings.feature.edit");
        var nobody = await _l.O.StaffTokenAsync(admin, null, "loyalty.points.view");
        var customer = await _l.CustomerAsync();
        try
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, Url, null, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, Url, null, customer.Token)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, Url, null, nobody)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, $"{Url}/history", null, nobody)).StatusCode);

            // A viewer sees everything and can change nothing.
            var asViewer = await H.JsonAsync(HttpMethod.Get, Url, HttpStatusCode.OK, bearer: viewer);
            Assert.All(asViewer.GetProperty("groups").EnumerateArray().SelectMany(g => g.GetProperty("settings").EnumerateArray()), s => Assert.False(s.GetProperty("canEdit").GetBoolean()));
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Put, $"{Url}/Returns.WindowDays", new { value = 10 }, viewer)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Delete, $"{Url}/Returns.WindowDays", null, viewer)).StatusCode);

            // Editing needs the permission named by the setting: system settings, feature switches and payment settings are separate.
            Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Put, $"{Url}/Returns.WindowDays", new { value = 10 }, systemEditor)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Put, $"{Url}/Loyalty.Enabled", new { value = true }, systemEditor)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Put, $"{Url}/Payments.MANUAL.Enabled", new { value = true }, systemEditor)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Put, $"{Url}/Loyalty.Enabled", new { value = true }, featureEditor)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Put, $"{Url}/Returns.WindowDays", new { value = 11 }, featureEditor)).StatusCode);

            var asEditor = await H.JsonAsync(HttpMethod.Get, Url, HttpStatusCode.OK, bearer: featureEditor);
            Assert.True(Find(asEditor, "Loyalty.Enabled").GetProperty("canEdit").GetBoolean());
            Assert.False(Find(asEditor, "Returns.WindowDays").GetProperty("canEdit").GetBoolean());
            Assert.Equal(10, Find(asEditor, "Returns.WindowDays").GetProperty("value").GetInt32());
        }
        finally
        {
            await ResetAsync(admin, "Returns.WindowDays");
            await ResetAsync(admin, "Loyalty.Enabled");
        }
    }

    [Fact]
    public async Task A_stale_version_is_refused_and_the_current_one_is_accepted()
    {
        var admin = await H.AdminAsync();
        try
        {
            var first = await SetAsync(admin, "Returns.WindowDays", 9);
            var v1 = first.GetProperty("version").GetString();
            var second = await SetAsync(admin, "Returns.WindowDays", 12, version: v1);
            Assert.Equal(12, second.GetProperty("value").GetInt32());
            await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, $"{Url}/Returns.WindowDays", new { value = 15, version = v1 }, admin), "GENERAL.CONCURRENCY_CONFLICT");
            Assert.Equal(12, (await H.JsonAsync(HttpMethod.Get, $"{Url}/Returns.WindowDays", HttpStatusCode.OK, bearer: admin)).GetProperty("value").GetInt32());

            // A version for a row that was reset meanwhile is stale too.
            await ResetAsync(admin, "Returns.WindowDays");
            await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, $"{Url}/Returns.WindowDays", new { value = 15, version = second.GetProperty("version").GetString() }, admin), "GENERAL.CONCURRENCY_CONFLICT");
        }
        finally
        {
            await ResetAsync(admin, "Returns.WindowDays");
        }
    }

    [Fact]
    public async Task Stock_is_taken_off_the_shelf_at_packing_or_at_delivery_as_the_setting_says_and_the_default_is_packing()
    {
        var (admin, product) = await _l.ProductAsync(500m, 10);
        var variant = product.Variants[0].Id;
        try
        {
            await SetAsync(admin, "Inventory.DeductAt", "Delivery");
            var id = (await _l.O.PlaceAsync(variant, 3)).GetProperty("orderId").GetGuid();
            await _l.O.ActAsync(admin, id, "confirm");
            var packed = await _l.O.ActAsync(admin, id, "pack");
            Assert.False(packed.GetProperty("stockDeducted").GetBoolean());
            Assert.Equal((10, 3), await _l.O.StockAsync(variant)); // still on the shelf, only reserved

            foreach (var step in new[] { "HandedToCourier", "InTransit", "OutForDelivery", "Delivered" })
            {
                await _l.O.ActAsync(admin, id, "transition", new { to = step });
            }

            Assert.Equal((7, 0), await _l.O.StockAsync(variant)); // taken at delivery

            await ResetAsync(admin, "Inventory.DeductAt"); // back to the default, no restart and no wait
            var next = (await _l.O.PlaceAsync(variant, 2)).GetProperty("orderId").GetGuid();
            await _l.O.ActAsync(admin, next, "confirm");
            Assert.True((await _l.O.ActAsync(admin, next, "pack")).GetProperty("stockDeducted").GetBoolean());
            Assert.Equal((5, 0), await _l.O.StockAsync(variant)); // taken at packing again
        }
        finally
        {
            await ResetAsync(admin, "Inventory.DeductAt");
        }
    }

    [Fact]
    public async Task History_lists_who_changed_what_newest_first_with_old_and_new_values_and_resets()
    {
        var admin = await H.AdminAsync();
        const string key = "Returns.CustomerFaultReturnCharge";
        try
        {
            await SetAsync(admin, key, 50);
            await SetAsync(admin, key, 75.5m);
            await ResetAsync(admin, key);

            var history = await H.JsonAsync(HttpMethod.Get, $"{Url}/history?key={key}", HttpStatusCode.OK, bearer: admin);
            var rows = history.EnumerateArray().ToList();
            Assert.Equal(3, rows.Count);
            Assert.All(rows, r => Assert.Equal(key, r.GetProperty("key").GetString()));

            Assert.Equal("reset", rows[0].GetProperty("action").GetString());
            Assert.Equal("75.5", rows[0].GetProperty("oldValue").GetString());
            Assert.Equal(JsonValueKind.Null, rows[0].GetProperty("newValue").ValueKind);
            Assert.Equal("set", rows[1].GetProperty("action").GetString());
            Assert.Equal("50", rows[1].GetProperty("oldValue").GetString());
            Assert.Equal("75.5", rows[1].GetProperty("newValue").GetString());
            Assert.Equal("set", rows[2].GetProperty("action").GetString());
            Assert.Equal(JsonValueKind.Null, rows[2].GetProperty("oldValue").ValueKind);
            Assert.Equal("50", rows[2].GetProperty("newValue").GetString());
            Assert.False(string.IsNullOrWhiteSpace(rows[0].GetProperty("changedByName").GetString()));
            Assert.NotEqual(Guid.Empty, rows[0].GetProperty("changedBy").GetGuid());

            // The limit and the unfiltered list work; settings the centre does not know (internal bookkeeping) never show up.
            Assert.Single((await H.JsonAsync(HttpMethod.Get, $"{Url}/history?key={key}&limit=1", HttpStatusCode.OK, bearer: admin)).EnumerateArray());
            Assert.All((await H.JsonAsync(HttpMethod.Get, $"{Url}/history", HttpStatusCode.OK, bearer: admin)).EnumerateArray(), r => Assert.NotNull(SettingRegistry.Find(r.GetProperty("key").GetString())));
        }
        finally
        {
            await ResetAsync(admin, key);
        }
    }

    [Fact]
    public async Task Storing_a_value_equal_to_the_default_keeps_an_explicit_row_until_it_is_reset()
    {
        var admin = await H.AdminAsync();
        try
        {
            var set = await SetAsync(admin, "Returns.WindowDays", 7);
            Assert.Equal("db", set.GetProperty("source").GetString());
            Assert.True(await StoredAsync("Returns.WindowDays"));
            await ResetAsync(admin, "Returns.WindowDays");
            Assert.False(await StoredAsync("Returns.WindowDays"));
        }
        finally
        {
            await ResetAsync(admin, "Returns.WindowDays");
        }
    }

    [Fact]
    public async Task The_typed_reader_gives_defaults_without_a_row_the_stored_value_with_one_and_the_default_for_a_stored_value_that_is_no_longer_valid()
    {
        var admin = await H.AdminAsync();
        try
        {
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var reader = scope.ServiceProvider.GetRequiredService<ISettingsReader>();
                Assert.False(await reader.GetBoolAsync("Loyalty.Enabled", Ct));
                Assert.Equal(7, await reader.GetIntAsync("Returns.WindowDays", Ct));
                Assert.Equal(0.01m, await reader.GetDecimalAsync("Loyalty.EarnRatePerBdt", Ct));
                Assert.Equal("Juvo BD", await reader.GetStringAsync("Business.Name", Ct));
            }

            await SetAsync(admin, "Returns.WindowDays", 14);
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                Assert.Equal(14, await scope.ServiceProvider.GetRequiredService<ISettingsReader>().GetIntAsync("Returns.WindowDays", Ct));
            }

            // A row written by hand with garbage is reported as the default (and the list shows source "default").
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
                var row = await db.PlatformSettings.SingleAsync(s => s.Key == "Returns.WindowDays", Ct);
                row.Set("lots");
                await db.SaveChangesAsync(Ct);
            }

            await using (var scope = factory.Services.CreateAsyncScope())
            {
                Assert.Equal(7, await scope.ServiceProvider.GetRequiredService<ISettingsReader>().GetIntAsync("Returns.WindowDays", Ct));
                Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ISettingsReader>().GetBoolAsync("Nope.Nothing", Ct).GetAwaiter().GetResult());
            }

            var one = await H.JsonAsync(HttpMethod.Get, $"{Url}/Returns.WindowDays", HttpStatusCode.OK, bearer: admin);
            Assert.Equal("default", one.GetProperty("source").GetString());
            Assert.Equal(7, one.GetProperty("value").GetInt32());
        }
        finally
        {
            await ResetAsync(admin, "Returns.WindowDays");
        }
    }

    private async Task<bool> StoredAsync(string key)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JuvoDbContext>().PlatformSettings.AsNoTracking().AnyAsync(s => s.Key == key, Ct);
    }
}
