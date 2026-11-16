using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Domain.Customers;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Marketing;

/// <summary>Segments (FR-AC01): every rule type against customers whose orders, logins, addresses and consent are known, with the answers counted by hand.</summary>
public class SegmentTests(MarketingFactory factory) : IClassFixture<MarketingFactory>
{
    private readonly MarketingHelper _m = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed record World(
        string Staff, Guid A, Guid B, Guid C, Guid D, Guid E, Guid F, Guid G, Guid H, Guid I, Guid J, Guid K, Guid L, Guid ProductId, Guid DhakaDivision, Guid DhakaDistrict, Guid OtherDivision, Guid OtherDistrict);

    /// <summary>
    /// The customers (everything older was hidden first):
    /// A new, 5 days old, nothing else. B 100 days old, orders 10 days ago (6000) and 40 days ago (5000), both Delivered. C 100 days old, one Delivered order 20 days ago (12000), signed in yesterday.
    /// D 100 days old, orders 3 days ago (100, Cancelled), 70 days ago (1000) and 80 days ago (1000). E 90 days old, no order, last sign-in 70 days ago. F 90 days old, one Delivered order 30 days ago (500).
    /// G new (10 days), consents to SMS and email. H 200 days old, one Delivered order 150 days ago (700), last sign-in 100 days ago. I consented to SMS then withdrew. J consents to SMS only.
    /// K withdrew SMS and then consented again. L is deactivated (and new): never counted.
    /// </summary>
    private async Task<World> BuildAsync()
    {
        await _m.ResetCustomersAsync();
        var staff = await _m.StaffAsync();
        var product = await _m.ProductAsync();
        var a = await _m.CustomerAsync("Seg A", createdDaysAgo: 5);
        var b = await _m.CustomerAsync("Seg B");
        var c = await _m.CustomerAsync("Seg C", lastLoginDaysAgo: 1);
        var d = await _m.CustomerAsync("Seg D");
        var e = await _m.CustomerAsync("Seg E", createdDaysAgo: 90, lastLoginDaysAgo: 70);
        var f = await _m.CustomerAsync("Seg F", createdDaysAgo: 90);
        var g = await _m.CustomerAsync("Seg G", createdDaysAgo: 10, sms: true, email: true);
        var h = await _m.CustomerAsync("Seg H", createdDaysAgo: 200, lastLoginDaysAgo: 100);
        var i = await _m.CustomerAsync("Seg I", createdDaysAgo: 50);
        var j = await _m.CustomerAsync("Seg J", createdDaysAgo: 50);
        var k = await _m.CustomerAsync("Seg K", createdDaysAgo: 50);
        var l = await _m.CustomerAsync("Seg L", createdDaysAgo: 3, active: false);

        await _m.OrderAsync(b, 10, 6000m);
        await _m.OrderAsync(b, 40, 5000m);
        await _m.OrderAsync(c, 20, 12000m);
        await _m.OrderAsync(d, 3, 100m, OrderStatus.Cancelled);
        await _m.OrderAsync(d, 70, 1000m);
        await _m.OrderAsync(d, 80, 1000m);
        await _m.OrderAsync(f, 30, 500m);
        await _m.OrderAsync(h, 150, 700m);

        var now = DateTimeOffset.UtcNow;
        await _m.ConsentAsync(i, ConsentType.MarketingSms, true, now.AddDays(-20));
        await _m.ConsentAsync(i, ConsentType.MarketingSms, false, now.AddDays(-5));
        await _m.ConsentAsync(j, ConsentType.MarketingSms, true, now.AddDays(-20));
        await _m.ConsentAsync(k, ConsentType.MarketingSms, false, now.AddDays(-20));
        await _m.ConsentAsync(k, ConsentType.MarketingSms, true, now.AddDays(-5));

        var districts = await _m.DistrictsAsync();
        var dhaka = await _m.DbAsync(db => db.Districts.Where(x => x.NameEn == "Dhaka").Select(x => new { x.DivisionId, x.Id }).FirstAsync(Ct));
        var other = districts.First(x => x.Division != dhaka.DivisionId);
        await _m.AddressAsync(b, dhaka.DivisionId, dhaka.Id);
        await _m.AddressAsync(c, other.Division, other.District);
        await _m.AddressAsync(d, dhaka.DivisionId, dhaka.Id, isDefault: false); // not the default address: does not count

        await _m.WishAsync(c, product.Id);
        await _m.CartAsync(f, idleHours: 48);
        return new World(staff, a, b, c, d, e, f, g, h, i, j, k, l, product.Id, dhaka.DivisionId, dhaka.Id, other.Division, other.District);
    }

    private async Task<int> TotalAsync(World w, object definition) => (await _m.CountAsync(w.Staff, definition)).GetProperty("total").GetInt32();

    private static object One(object rule, string combinator = "and") => MarketingHelper.Rules(combinator, rule);

    [Fact]
    public async Task Every_rule_type_counts_exactly_the_customers_worked_out_by_hand()
    {
        var w = await BuildAsync();

        Assert.Equal(2, await TotalAsync(w, One(new { type = "newCustomers", days = 30 }))); // A (5 days) and G (10 days); L is new but deactivated
        Assert.Equal(2, await TotalAsync(w, One(new { type = "repeatBuyers", minOrders = 2 }))); // B (2) and D (2: the cancelled order does not count)
        Assert.Equal(1, await TotalAsync(w, One(new { type = "repeatBuyers", minOrders = 2, periodDays = 45 }))); // only B has two inside 45 days
        Assert.Equal(2, await TotalAsync(w, One(new { type = "highSpenders", minSpend = 10000 }))); // B 11000, C 12000
        Assert.Equal(1, await TotalAsync(w, One(new { type = "highSpenders", minSpend = 10000, periodDays = 30 }))); // C only: B has 6000 inside 30 days
        Assert.Equal(2, await TotalAsync(w, One(new { type = "inactive", days = 60 }))); // E (no order, signed in 70 days ago) and H; D has an order 3 days ago
        Assert.Equal(1, await TotalAsync(w, One(new { type = "location", level = "district", ids = new[] { w.DhakaDistrict } }))); // B; D's Dhaka address is not the default
        Assert.Equal(1, await TotalAsync(w, One(new { type = "location", level = "division", ids = new[] { w.OtherDivision } }))); // C
        Assert.Equal(2, await TotalAsync(w, One(new { type = "location", level = "district", ids = new[] { w.DhakaDistrict, w.OtherDistrict } }))); // B and C
        Assert.Equal(3, await TotalAsync(w, One(new { type = "consent", channel = "sms" }))); // G, J and K (I withdrew)
        Assert.Equal(1, await TotalAsync(w, One(new { type = "consent", channel = "email" }))); // G
        Assert.Equal(8, await TotalAsync(w, One(new { type = "consent", channel = "sms", granted = false }))); // the other 8 active customers
        Assert.Equal(4, await TotalAsync(w, One(new { type = "lastOrderStatus", status = "Delivered" }))); // B, C, F, H (D's newest order is the cancelled one)
        Assert.Equal(1, await TotalAsync(w, One(new { type = "lastOrderStatus", status = "Cancelled" }))); // D
        Assert.Equal(1, await TotalAsync(w, One(new { type = "wishlist" }))); // C
        Assert.Equal(1, await TotalAsync(w, One(new { type = "wishlist", productIds = new[] { w.ProductId } }))); // C
        Assert.Equal(0, await TotalAsync(w, One(new { type = "wishlist", productIds = new[] { Guid.NewGuid() } })));
        Assert.Equal(1, await TotalAsync(w, One(new { type = "cart" }))); // F
        Assert.Equal(1, await TotalAsync(w, One(new { type = "cart", idleHours = 24 }))); // F's cart was touched 48 hours ago
        Assert.Equal(0, await TotalAsync(w, One(new { type = "cart", idleHours = 72 })));
    }

    [Fact]
    public async Task Rules_combine_with_and_or_or()
    {
        var w = await BuildAsync();

        Assert.Equal(4, await TotalAsync(w, MarketingHelper.Rules("or", new { type = "newCustomers", days = 30 }, new { type = "inactive", days = 60 }))); // A, G, E, H
        Assert.Equal(1, await TotalAsync(w, MarketingHelper.Rules("and", new { type = "highSpenders", minSpend = 10000 }, new { type = "location", level = "district", ids = new[] { w.DhakaDistrict } }))); // B
        Assert.Equal(0, await TotalAsync(w, MarketingHelper.Rules("and", new { type = "newCustomers", days = 30 }, new { type = "repeatBuyers", minOrders = 2 })));
        Assert.Equal(3, await TotalAsync(w, MarketingHelper.Rules("or", new { type = "repeatBuyers", minOrders = 2 }, new { type = "wishlist" }))); // B, D, C
    }

    [Fact]
    public async Task The_reachable_counts_need_consent_and_a_contact_on_the_channel()
    {
        var w = await BuildAsync();

        var all = await _m.CountAsync(w.Staff, One(new { type = "consent", channel = "sms" })); // G, J, K

        Assert.Equal(3, all.GetProperty("total").GetInt32());
        Assert.Equal(3, all.GetProperty("reachable").GetProperty("sms").GetInt32());
        Assert.Equal(1, all.GetProperty("reachable").GetProperty("email").GetInt32()); // only G also agreed to email and has an address

        var everyone = await _m.CountAsync(w.Staff, MarketingHelper.Rules("or", new { type = "consent", channel = "sms", granted = false }, new { type = "consent", channel = "sms" }));
        Assert.Equal(11, everyone.GetProperty("total").GetInt32()); // 12 customers minus the deactivated L
        Assert.Equal(3, everyone.GetProperty("reachable").GetProperty("sms").GetInt32());
        Assert.Equal(1, everyone.GetProperty("reachable").GetProperty("email").GetInt32());

        // Consent without a contact is not reachable: G agreed to email, then loses the address.
        await _m.DbAsync(db => db.Customers.Where(c => c.Id == w.G).ExecuteUpdateAsync(s => s.SetProperty(c => c.Email, (string?)null), Ct));
        Assert.Equal(0, (await _m.CountAsync(w.Staff, One(new { type = "consent", channel = "email" }))).GetProperty("reachable").GetProperty("email").GetInt32());
    }

    [Fact]
    public async Task The_member_list_shows_name_orders_spend_and_reachability_paged_and_masked()
    {
        var w = await BuildAsync();
        var segment = await _m.SegmentAsync(w.Staff, One(new { type = "repeatBuyers", minOrders = 2 }));

        var list = await _m.GetAsync($"{MarketingHelper.SegmentsUrl}/{segment}/members?sort=-spend", w.Staff);

        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var items = list.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal("Seg B", items[0].GetProperty("name").GetString());
        Assert.Equal(2, items[0].GetProperty("orderCount").GetInt32());
        Assert.Equal(11000m, items[0].GetProperty("totalSpend").GetDecimal());
        Assert.Equal(2000m, items[1].GetProperty("totalSpend").GetDecimal()); // D: the cancelled order is not counted
        Assert.Equal(2, items[1].GetProperty("orderCount").GetInt32());
        Assert.False(items[0].GetProperty("smsReachable").GetBoolean());
        Assert.Contains("****", items[0].GetProperty("mobile").GetString()); // staff without customers.customer.view-pii see masked numbers
        Assert.NotEqual(JsonValueKind.Null, items[0].GetProperty("lastOrderAt").ValueKind);

        var firstPage = await _m.GetAsync($"{MarketingHelper.SegmentsUrl}/{segment}/members?pageSize=1&page=2&sort=-spend", w.Staff);
        Assert.Equal("Seg D", Assert.Single(firstPage.GetProperty("items").EnumerateArray()).GetProperty("name").GetString());
        Assert.Equal(2, firstPage.GetProperty("total").GetInt32());

        var found = await _m.GetAsync($"{MarketingHelper.SegmentsUrl}/{segment}/members?search=Seg%20D", w.Staff);
        Assert.Equal(1, found.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_four_built_in_segments_exist_can_be_copied_and_not_changed_or_deleted()
    {
        var w = await BuildAsync();

        var list = await _m.GetAsync(MarketingHelper.SegmentsUrl, w.Staff);

        var builtIn = list.GetProperty("items").EnumerateArray().Where(s => s.GetProperty("isBuiltIn").GetBoolean()).ToList();
        Assert.Equal(["high-spenders", "inactive-60", "new-customers", "repeat-buyers"], builtIn.Select(s => s.GetProperty("builtInCode").GetString()!).Order().ToArray());

        var repeat = builtIn.Single(s => s.GetProperty("builtInCode").GetString() == "repeat-buyers").GetProperty("id").GetGuid();
        Assert.Equal(2, (await _m.GetAsync($"{MarketingHelper.SegmentsUrl}/{repeat}/count", w.Staff)).GetProperty("total").GetInt32());

        var copy = await _m.PostAsync($"{MarketingHelper.SegmentsUrl}/{repeat}/copy", w.Staff, null, HttpStatusCode.Created);
        Assert.False(copy.GetProperty("isBuiltIn").GetBoolean());
        Assert.Equal("Repeat buyers (copy)", copy.GetProperty("name").GetString());
        Assert.Equal(2, copy.GetProperty("definition").GetProperty("rules")[0].GetProperty("minOrders").GetInt32());

        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Put, $"{MarketingHelper.SegmentsUrl}/{repeat}", w.Staff, new { name = "Changed", definition = One(new { type = "wishlist" }) }), "MARKETING.SEGMENT_BUILT_IN");
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Delete, $"{MarketingHelper.SegmentsUrl}/{repeat}", w.Staff), "MARKETING.SEGMENT_BUILT_IN");

        var copyId = copy.GetProperty("id").GetGuid();
        var updated = await _m.PutAsync($"{MarketingHelper.SegmentsUrl}/{copyId}", w.Staff, new { name = "Three orders", definition = One(new { type = "repeatBuyers", minOrders = 3 }) });
        Assert.Equal(3, updated.GetProperty("definition").GetProperty("rules")[0].GetProperty("minOrders").GetInt32());
        Assert.Equal(HttpStatusCode.NoContent, (await _m.TryAsync(HttpMethod.Delete, $"{MarketingHelper.SegmentsUrl}/{copyId}", w.Staff)).StatusCode);
    }

    [Fact]
    public async Task A_bad_definition_is_refused_with_the_field_and_a_segment_in_use_cannot_be_deleted()
    {
        var w = await BuildAsync();

        var bad = await _m.TryAsync(HttpMethod.Post, MarketingHelper.SegmentsUrl, w.Staff, new { name = "Broken", definition = MarketingHelper.Rules("and", new { type = "highSpenders", minSpend = -5 }, new { type = "dance" }) });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var body = JsonDocument.Parse(await bad.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("MARKETING.SEGMENT_INVALID", body.GetProperty("code").GetString());
        Assert.True(body.GetProperty("errors").TryGetProperty("rules[0].minSpend", out _));
        Assert.True(body.GetProperty("errors").TryGetProperty("rules[1].type", out _));

        var segment = await _m.SegmentAsync(w.Staff, One(new { type = "wishlist" }), "Wishers");
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Post, MarketingHelper.SegmentsUrl, w.Staff, new { name = "Wishers", definition = One(new { type = "wishlist" }) }), "MARKETING.SEGMENT_INVALID"); // name taken
        await _m.CampaignAsync(w.Staff, segment);
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Delete, $"{MarketingHelper.SegmentsUrl}/{segment}", w.Staff), "MARKETING.SEGMENT_IN_USE");
    }

    [Fact]
    public async Task Segment_endpoints_need_their_permissions()
    {
        var w = await BuildAsync();
        var viewer = await _m.StaffAsync("marketing.segment.view");
        var nobody = await _m.StaffAsync("support.ticket.view");

        Assert.Equal(HttpStatusCode.OK, (await _m.TryAsync(HttpMethod.Get, MarketingHelper.SegmentsUrl, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _m.TryAsync(HttpMethod.Post, MarketingHelper.SegmentsUrl, viewer, new { name = "X", definition = One(new { type = "wishlist" }) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _m.TryAsync(HttpMethod.Get, MarketingHelper.SegmentsUrl, nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _m.TryAsync(HttpMethod.Get, MarketingHelper.SegmentsUrl, null)).StatusCode);
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Get, $"{MarketingHelper.SegmentsUrl}/{Guid.NewGuid()}/members", w.Staff), "MARKETING.SEGMENT_NOT_FOUND");
    }

    [Fact]
    public async Task Staff_with_the_view_pii_permission_see_the_real_numbers()
    {
        var w = await BuildAsync();
        var segment = await _m.SegmentAsync(w.Staff, One(new { type = "consent", channel = "email" })); // G
        var pii = await _m.StaffAsync("marketing.segment.view", "customers.customer.view-pii");

        var item = Assert.Single((await _m.GetAsync($"{MarketingHelper.SegmentsUrl}/{segment}/members", pii)).GetProperty("items").EnumerateArray());

        Assert.DoesNotContain("*", item.GetProperty("mobile").GetString());
        Assert.StartsWith("+8801", item.GetProperty("mobile").GetString());
        Assert.True(item.GetProperty("emailReachable").GetBoolean());
        Assert.True(item.GetProperty("smsReachable").GetBoolean());
    }
}
