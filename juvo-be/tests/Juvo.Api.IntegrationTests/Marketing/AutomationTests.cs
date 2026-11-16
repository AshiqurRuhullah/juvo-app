using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Application.Marketing;
using Juvo.Domain.Customers;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Marketing;

/// <summary>The four automations (FR-AC03): off by default, dry run, abandoned cart, review request, win-back and back in stock, each idempotent.</summary>
public class AutomationTests(MarketingFactory factory) : IClassFixture<MarketingFactory>
{
    private readonly MarketingHelper _m = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Cart = "AbandonedCart";
    private const string Stock = "BackInStock";
    private const string Review = "ReviewRequest";
    private const string Win = "WinBack";

    /// <summary>Hides old customers, removes automation rows, subscriptions and their messages, resets the settings and returns a staff token.</summary>
    private async Task<string> PrepareAsync()
    {
        await _m.ResetCustomersAsync();
        await _m.DbAsync(async db =>
        {
            await db.BackInStockSubscriptions.ExecuteDeleteAsync(Ct);
            await db.Automations.ExecuteDeleteAsync(Ct);
            return await db.NotificationLogs.Where(l => l.Template.StartsWith("automation.")).ExecuteDeleteAsync(Ct);
        });
        foreach (var key in new[] { MarketingSettingKeys.QuietHoursEnabled, MarketingSettingKeys.QuietHoursStart, MarketingSettingKeys.QuietHoursEnd, MarketingSettingKeys.AttributionWindowDays, MarketingSettingKeys.MaxNotifyMePerMobile })
        {
            await _m.SetPlatformSettingAsync(key, null);
        }

        var staff = await _m.StaffAsync();
        await _m.PutAsync(MarketingHelper.SettingsUrl, staff, new { quietHoursEnabled = false });
        return staff;
    }

    private Task<JsonElement> EnableAsync(string staff, string kind, object? more = null)
    {
        var body = new Dictionary<string, object?> { ["isEnabled"] = true };
        foreach (var p in more?.GetType().GetProperties() ?? [])
        {
            body[p.Name] = p.GetValue(more);
        }

        return _m.PutAsync($"{MarketingHelper.AutomationsUrl}/{kind}", staff, body);
    }

    private async Task<JsonElement> DryRunAsync(string staff, string kind)
    {
        var all = await _m.PostAsync($"{MarketingHelper.AutomationsUrl}/dry-run", staff);
        return all.GetProperty("automations").EnumerateArray().Single(a => a.GetProperty("kind").GetString() == kind);
    }

    private Task<List<NotificationLog>> LogsAsync(string template, params Guid[] customers) => _m.LogsAsync(template, customers);

    private static int Skipped(JsonElement item, string reason) =>
        item.GetProperty("skipped").EnumerateArray().Where(x => x.GetProperty("reason").GetString() == reason).Select(x => x.GetProperty("count").GetInt32()).SingleOrDefault();

    private Task<Guid> Consenting(string name, int createdDaysAgo = 200, int? lastLoginDaysAgo = null) => _m.CustomerAsync(name, createdDaysAgo, lastLoginDaysAgo, sms: true);

    // ---- the switch --------------------------------------------------------------------------------------------

    [Fact]
    public async Task All_four_automations_start_switched_off_with_texts_in_both_languages()
    {
        var staff = await PrepareAsync();

        var list = await _m.GetAsync(MarketingHelper.AutomationsUrl, staff);

        var items = list.EnumerateArray().ToList();
        Assert.Equal([Cart, Stock, Review, Win], items.Select(a => a.GetProperty("kind").GetString()!).ToArray());
        Assert.All(items, a =>
        {
            Assert.False(a.GetProperty("isEnabled").GetBoolean());
            Assert.NotEmpty(a.GetProperty("bodyEn").GetString()!);
            Assert.NotEmpty(a.GetProperty("bodyBn").GetString()!);
            Assert.NotEmpty(a.GetProperty("placeholders").EnumerateArray());
        });
        Assert.Equal(180, items[0].GetProperty("delayMinutes").GetInt32());
        Assert.Equal(7 * 24 * 60, items[2].GetProperty("delayMinutes").GetInt32());
        Assert.Equal(60 * 24 * 60, items[3].GetProperty("delayMinutes").GetInt32());
    }

    [Fact]
    public async Task A_switched_off_automation_sends_nothing_but_the_dry_run_shows_what_switching_on_would_do()
    {
        var staff = await PrepareAsync();
        var person = await Consenting("Off Person");
        await _m.CartAsync(person, idleHours: 5);

        var passes = await _m.SweepAutomationsAsync();
        var dry = await DryRunAsync(staff, Cart);

        Assert.All(passes, p => Assert.Equal(0, p.Sent));
        Assert.Empty(await LogsAsync("automation.abandoned-cart", person));
        Assert.False(dry.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(0, dry.GetProperty("wouldSend").GetInt32());
        Assert.Equal(1, dry.GetProperty("wouldSendIfEnabled").GetInt32());
    }

    // ---- abandoned cart ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Abandoned_cart_messages_the_right_customers_once_and_respects_the_cap()
    {
        var staff = await PrepareAsync();
        var a1 = await Consenting("Cart One");
        var a2 = await _m.CustomerAsync("Cart NoConsent", 200);
        var a3 = await Consenting("Cart Recent");
        var a4 = await Consenting("Cart Ancient");
        var a5 = await Consenting("Cart Bought");
        await _m.CartAsync(a1, idleHours: 5);
        await _m.CartAsync(a2, idleHours: 5);
        await _m.CartAsync(a3, idleHours: 1); // idle for less than the 3-hour delay
        await _m.CartAsync(a4, idleHours: 24 * 10); // older than delay plus the 7-day window
        await _m.CartAsync(a5, idleHours: 5);
        await _m.OrderAsync(a5, 1.0 / 24, 800m); // ordered after the cart was last touched: converted
        await EnableAsync(staff, Cart);

        var dry = await DryRunAsync(staff, Cart);
        Assert.Equal(2, dry.GetProperty("candidates").GetInt32()); // a1 and a2
        Assert.Equal(1, dry.GetProperty("wouldSend").GetInt32());
        Assert.Equal(1, Skipped(dry, "NoConsent"));
        Assert.Empty(await LogsAsync("automation.abandoned-cart", a1)); // a dry run writes nothing

        var first = (await _m.SweepAutomationsAsync()).Single(p => p.Kind.ToString() == Cart);
        var second = (await _m.SweepAutomationsAsync()).Single(p => p.Kind.ToString() == Cart);

        Assert.Equal(1, first.Sent); // the dry-run number is what the run sent
        Assert.Equal(0, second.Sent);
        var log = Assert.Single(await LogsAsync("automation.abandoned-cart", a1, a2, a3, a4, a5));
        Assert.Equal(a1, log.CustomerId);
        Assert.Equal(NotificationCategory.Marketing, log.Category);
        Assert.Contains("2 item(s)", log.Body);
        Assert.Contains("/cart", log.Body);
        Assert.Contains("Stop: http", log.Body); // marketing carries the opt-out
        Assert.Equal(1, (await DryRunAsync(staff, Cart)).GetProperty("skipped").EnumerateArray().Single(x => x.GetProperty("reason").GetString() == "AlreadySent").GetProperty("count").GetInt32());

        // The customer touches the cart again: a new trigger, but the 7-day cap holds the message back.
        await _m.DbAsync(db => db.Carts.Where(c => c.CustomerId == a1).ExecuteUpdateAsync(set => set.SetProperty(c => c.UpdatedAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(-4)), Ct));
        var capped = await DryRunAsync(staff, Cart);
        Assert.Equal(0, capped.GetProperty("wouldSend").GetInt32());
        Assert.Equal(1, Skipped(capped, "FrequencyCap"));

        var results = await _m.GetAsync($"{MarketingHelper.AutomationsUrl}/{Cart}/results", staff);
        Assert.Equal(1, results.GetProperty("queued").GetInt32());
        Assert.Equal(0, results.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task Automation_sms_waits_for_quiet_hours_to_end()
    {
        var staff = await PrepareAsync();
        var person = await Consenting("Night Cart");
        await _m.CartAsync(person, idleHours: 5);
        await EnableAsync(staff, Cart);
        await _m.SetQuietAsync(true);

        var dry = await DryRunAsync(staff, Cart);
        var quiet = (await _m.SweepAutomationsAsync()).Single(p => p.Kind.ToString() == Cart);

        Assert.Equal(0, dry.GetProperty("wouldSend").GetInt32());
        Assert.Equal(1, dry.GetProperty("deferredByQuietHours").GetInt32());
        Assert.Equal(0, quiet.Sent);
        Assert.Equal(1, quiet.Deferred);
        Assert.Empty(await LogsAsync("automation.abandoned-cart", person));

        await _m.SetQuietAsync(false);
        Assert.Equal(1, (await _m.SweepAutomationsAsync()).Single(p => p.Kind.ToString() == Cart).Sent);
    }

    // ---- review request ----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_review_request_goes_out_once_per_delivered_order_after_the_delay_and_never_for_a_returned_one()
    {
        var staff = await PrepareAsync();
        var r1 = await Consenting("Review One");
        var r2 = await Consenting("Review Early");
        var r3 = await Consenting("Review Returned");
        var r4 = await _m.CustomerAsync("Review NoConsent", 200);
        var r5 = await Consenting("Review Old");
        var r6 = await Consenting("Review Return Request");
        var o1 = await _m.OrderAsync(r1, 9, 900m, deliveredDaysAgo: 8);
        await _m.OrderAsync(r2, 3, 900m, deliveredDaysAgo: 2); // delivered 2 days ago: before the 7-day delay
        await _m.OrderAsync(r3, 9, 900m, OrderStatus.Returned);
        await _m.OrderAsync(r4, 9, 900m, deliveredDaysAgo: 8);
        await _m.OrderAsync(r5, 40, 900m, deliveredDaysAgo: 30); // older than delay plus the 14-day window
        var o6 = await _m.OrderAsync(r6, 9, 900m, deliveredDaysAgo: 8);
        await _m.ReturnAsync(o6); // the order is still Delivered but a return was asked for
        await EnableAsync(staff, Review);

        var dry = await DryRunAsync(staff, Review);
        Assert.Equal(2, dry.GetProperty("candidates").GetInt32()); // r1 and r4
        Assert.Equal(1, dry.GetProperty("wouldSend").GetInt32());

        var first = (await _m.SweepAutomationsAsync()).Single(p => p.Kind.ToString() == Review);
        var second = (await _m.SweepAutomationsAsync()).Single(p => p.Kind.ToString() == Review);

        Assert.Equal(1, first.Sent);
        Assert.Equal(0, second.Sent);
        var log = Assert.Single(await LogsAsync("automation.review-request", r1, r2, r3, r4, r5, r6));
        Assert.Equal(r1, log.CustomerId);
        var number = await _m.DbAsync(db => db.Orders.IgnoreQueryFilters().Where(o => o.Id == o1).Select(o => o.Number).SingleAsync(Ct));
        Assert.Contains(number, log.Body);
        Assert.Contains($"/account/orders/{number}", log.Body);
    }

    // ---- win-back ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Win_back_messages_past_buyers_who_went_quiet_once_per_cap_period()
    {
        var staff = await PrepareAsync();
        var w1 = await Consenting("Win One");
        var w2 = await Consenting("Win Active Login", lastLoginDaysAgo: 5);
        var w3 = await Consenting("Win Recent Order");
        var w4 = await Consenting("Win Never Bought");
        var w5 = await _m.CustomerAsync("Win NoConsent", 200);
        await _m.OrderAsync(w1, 100, 700m);
        await _m.OrderAsync(w2, 100, 700m);
        await _m.OrderAsync(w3, 10, 700m);
        await _m.OrderAsync(w5, 100, 700m);
        await EnableAsync(staff, Win);

        var dry = await DryRunAsync(staff, Win);
        Assert.Equal(2, dry.GetProperty("candidates").GetInt32()); // w1 and w5
        Assert.Equal(1, dry.GetProperty("wouldSend").GetInt32());

        Assert.Equal(1, (await _m.SweepAutomationsAsync()).Single(p => p.Kind.ToString() == Win).Sent);
        Assert.Equal(0, (await _m.SweepAutomationsAsync()).Single(p => p.Kind.ToString() == Win).Sent);

        Assert.Equal(w1, Assert.Single(await LogsAsync("automation.win-back", w1, w2, w3, w4, w5)).CustomerId);
        var results = await _m.GetAsync($"{MarketingHelper.AutomationsUrl}/{Win}/results", staff);
        Assert.Equal(1, results.GetProperty("queued").GetInt32());
    }

    // ---- back in stock -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Back_in_stock_subscribers_are_told_once_when_stock_returns()
    {
        var staff = await PrepareAsync();
        var admin = await _m.H.AdminAsync();
        var product = await _m.ProductAsync(stock: 0);
        var variant = product.Variants[0].Id;
        var url = $"/api/v1/store/products/{product.Slug}/notify-me";
        var guestMobile = StoreHelper.NewMobile();
        var optedOut = await _m.CustomerAsync("Opted Out", 200, sms: true);
        await _m.ConsentAsync(optedOut, ConsentType.MarketingSms, false, DateTimeOffset.UtcNow);
        var optedOutMobile = await _m.DbAsync(db => db.Customers.Where(c => c.Id == optedOut).Select(c => c.Mobile).SingleAsync(Ct));
        var neverAsked = await _m.CustomerAsync("Never Consented", 200); // no marketing consent: a requested alert is still allowed
        var neverMobile = await _m.DbAsync(db => db.Customers.Where(c => c.Id == neverAsked).Select(c => c.Mobile).SingleAsync(Ct));
        var signedIn = await _m.H.CustomerTokenAsync();

        var guest = await _m.H.JsonAsync(HttpMethod.Post, url, HttpStatusCode.OK, new { variantId = variant, mobile = guestMobile });
        var again = await _m.H.JsonAsync(HttpMethod.Post, url, HttpStatusCode.OK, new { variantId = variant, mobile = guestMobile });
        await _m.H.JsonAsync(HttpMethod.Post, url, HttpStatusCode.OK, new { variantId = variant, mobile = optedOutMobile });
        await _m.H.JsonAsync(HttpMethod.Post, url, HttpStatusCode.OK, new { variantId = variant, mobile = neverMobile });
        var mine = await _m.H.JsonAsync(HttpMethod.Post, url, HttpStatusCode.OK, new { variantId = variant }, signedIn);

        Assert.False(guest.GetProperty("alreadySubscribed").GetBoolean());
        Assert.True(again.GetProperty("alreadySubscribed").GetBoolean());
        Assert.Equal(guest.GetProperty("subscriptionId").GetGuid(), again.GetProperty("subscriptionId").GetGuid());
        Assert.False(mine.GetProperty("alreadySubscribed").GetBoolean());
        Assert.Equal(4, await _m.DbAsync(db => db.BackInStockSubscriptions.CountAsync(s => s.VariantId == variant, Ct))); // guest, opted out, never asked, signed in

        await EnableAsync(staff, Stock);
        Assert.Equal(0, (await _m.SweepAutomationsAsync()).Single(p => p.Kind.ToString() == Stock).Sent); // still sold out
        Assert.Equal(HttpStatusCode.OK, (await _m.H.SendAsync(HttpMethod.Post, url, new { variantId = variant, mobile = guestMobile })).StatusCode); // still sold out: asking again is fine

        await _m.H.SetStockChangeAsync(admin, variant, 5);
        var dry = await DryRunAsync(staff, Stock);
        Assert.Equal(4, dry.GetProperty("candidates").GetInt32());
        Assert.Equal(3, dry.GetProperty("wouldSend").GetInt32());
        Assert.Equal(1, Skipped(dry, "OptedOut"));

        var first = (await _m.SweepAutomationsAsync()).Single(p => p.Kind.ToString() == Stock);
        var second = (await _m.SweepAutomationsAsync()).Single(p => p.Kind.ToString() == Stock);

        Assert.Equal(3, first.Sent);
        Assert.Equal(0, second.Sent); // once per subscription
        var logs = await _m.DbAsync(db => db.NotificationLogs.AsNoTracking().Where(l => l.Template == "automation.back-in-stock").ToListAsync(Ct));
        Assert.Equal(3, logs.Count);
        Assert.Contains(logs, l => l.Recipient == "+88" + guestMobile);
        Assert.Contains(logs, l => l.Recipient == neverMobile);
        Assert.DoesNotContain(logs, l => l.Recipient == optedOutMobile);
        Assert.All(logs, l => Assert.Equal(NotificationCategory.Transactional, l.Category));
        Assert.All(logs, l => Assert.Contains($"/products/{product.Slug}", l.Body));
        Assert.All(logs, l => Assert.Contains(product.Name, l.Body));
        var outcomes = await _m.DbAsync(db => db.BackInStockSubscriptions.AsNoTracking().Where(s => s.VariantId == variant).Select(s => s.Outcome).ToListAsync(Ct));
        Assert.Equal(3, outcomes.Count(o => o == "sent"));
        Assert.Equal(1, outcomes.Count(o => o == "skipped:OptedOut"));

        // Back in stock now: a new request is refused, and the finished one can be made again after it sold out.
        await StoreHelper.ExpectCodeAsync(await _m.H.SendAsync(HttpMethod.Post, url, new { variantId = variant, mobile = StoreHelper.NewMobile() }), "MARKETING.VARIANT_IN_STOCK");
    }

    [Fact]
    public async Task The_notify_me_request_is_checked()
    {
        await PrepareAsync();
        var product = await _m.ProductAsync(stock: 0);
        var variant = product.Variants[0].Id;
        var url = $"/api/v1/store/products/{product.Slug}/notify-me";

        await StoreHelper.ExpectCodeAsync(await _m.H.SendAsync(HttpMethod.Post, url, new { variantId = variant, mobile = "12" }), "MARKETING.CONTACT_REQUIRED");
        await StoreHelper.ExpectCodeAsync(await _m.H.SendAsync(HttpMethod.Post, url, new { variantId = variant, mobile = StoreHelper.NewMobile(), email = "not-an-email" }), "MARKETING.CONTACT_REQUIRED");
        await StoreHelper.ExpectCodeAsync(await _m.H.SendAsync(HttpMethod.Post, url, new { variantId = Guid.NewGuid(), mobile = StoreHelper.NewMobile() }), "MARKETING.VARIANT_NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await _m.H.SendAsync(HttpMethod.Post, "/api/v1/store/products/no-such-product/notify-me", new { variantId = variant, mobile = StoreHelper.NewMobile() }), "MARKETING.VARIANT_NOT_FOUND");
        var stocked = await _m.ProductAsync();
        await StoreHelper.ExpectCodeAsync(await _m.H.SendAsync(HttpMethod.Post, $"/api/v1/store/products/{stocked.Slug}/notify-me", new { variantId = stocked.Variants[0].Id, mobile = StoreHelper.NewMobile() }), "MARKETING.VARIANT_IN_STOCK");

        // One number may wait for only so many items.
        await _m.SetPlatformSettingAsync(MarketingSettingKeys.MaxNotifyMePerMobile, "1");
        var mobile = StoreHelper.NewMobile();
        Assert.Equal(HttpStatusCode.OK, (await _m.H.SendAsync(HttpMethod.Post, url, new { variantId = variant, mobile })).StatusCode);
        var second = await _m.ProductAsync(stock: 0);
        var limited = await _m.H.SendAsync(HttpMethod.Post, $"/api/v1/store/products/{second.Slug}/notify-me", new { variantId = second.Variants[0].Id, mobile });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        await StoreHelper.ExpectCodeAsync(limited, "MARKETING.SUBSCRIPTION_LIMIT");
    }

    // ---- changing an automation --------------------------------------------------------------------------------

    [Fact]
    public async Task An_automation_can_be_changed_within_its_limits_and_only_with_the_edit_permission()
    {
        var staff = await PrepareAsync();
        var viewer = await _m.StaffAsync("marketing.automation.view");
        var url = $"{MarketingHelper.AutomationsUrl}/{Cart}";

        var changed = await _m.PutAsync(url, staff, new { isEnabled = true, delayMinutes = 60, capDays = 3, bodyEn = "Come back {customerName}! {link}", bodyBn = "ফিরে আসুন {customerName}! {link}" });

        Assert.True(changed.GetProperty("isEnabled").GetBoolean());
        Assert.Equal(60, changed.GetProperty("delayMinutes").GetInt32());
        Assert.Equal(3, changed.GetProperty("capDays").GetInt32());
        Assert.Equal(60, (await _m.GetAsync(url, viewer)).GetProperty("delayMinutes").GetInt32());
        Assert.Equal(HttpStatusCode.Forbidden, (await _m.TryAsync(HttpMethod.Put, url, viewer, new { isEnabled = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _m.TryAsync(HttpMethod.Get, MarketingHelper.AutomationsUrl, await _m.StaffAsync("marketing.campaign.view"))).StatusCode);

        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Put, url, staff, new { bodyEn = "Hi {nickname}" }), "MARKETING.AUTOMATION_INVALID");
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Put, url, staff, new { delayMinutes = -1 }), "MARKETING.AUTOMATION_INVALID");
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Put, url, staff, new { capDays = 400 }), "MARKETING.AUTOMATION_INVALID");
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Put, url, staff, new { channel = "WhatsApp" }), "MARKETING.AUTOMATION_INVALID");
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Put, url, staff, new { channel = "Email", subjectEn = "" }), "MARKETING.AUTOMATION_INVALID");
        Assert.Equal(HttpStatusCode.BadRequest, (await _m.TryAsync(HttpMethod.Get, $"{MarketingHelper.AutomationsUrl}/Nonsense", staff)).StatusCode);
    }
}
