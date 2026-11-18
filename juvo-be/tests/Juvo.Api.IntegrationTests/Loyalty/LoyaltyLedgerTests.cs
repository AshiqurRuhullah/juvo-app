using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Returns;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Loyalty;
using Juvo.Domain.Loyalty;
using Juvo.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Loyalty;

/// <summary>While the flag is off nothing shows and nothing accrues; staff can still set the feature up (the flag is OFF by default).</summary>
public class LoyaltyOffTests(LoyaltyFactory factory) : IClassFixture<LoyaltyFactory>
{
    private readonly LoyaltyHelper _l = new(factory);

    private StoreHelper H => _l.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_customer_endpoint_answers_feature_off_nothing_accrues_and_staff_can_still_read_and_change_the_settings()
    {
        var (admin, product) = await _l.ProductAsync(500m);
        var settings = await H.JsonAsync(HttpMethod.Get, "/api/v1/loyalty/settings", HttpStatusCode.OK, bearer: admin);
        Assert.False(settings.GetProperty("enabled").GetBoolean());

        var c = await _l.CustomerAsync();
        foreach (var url in new[] { "/api/v1/me/rewards", "/api/v1/me/rewards/points", "/api/v1/me/rewards/credit", "/api/v1/me/rewards/referral", "/api/v1/me/rewards/usable?orderTotal=100" })
        {
            await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, url, null, c.Token), "LOYALTY.FEATURE_OFF");
        }

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/me/rewards/gift-cards/redeem", new { code = "AAAA-BBBB-CCCC-DDDD" }, c.Token), "LOYALTY.FEATURE_OFF");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/me/rewards/referral/apply", new { code = "ABCDEFGH" }, c.Token), "LOYALTY.FEATURE_OFF");
        await StoreHelper.ExpectCodeAsync(await _l.TryCheckoutAsync(c, product.Variants[0].Id, 1, "COD", new { points = 100 }), "LOYALTY.FEATURE_OFF");
        var quote = await _l.QuoteAsync(c, product.Variants[0].Id, 1, new { points = 100 });
        Assert.Equal("LOYALTY.FEATURE_OFF", quote.GetProperty("tender").GetProperty("problem").GetString());

        // A delivered order earns nothing while the feature is off, and the staff screens (settings, balances, gift cards) keep working.
        var placed = await _l.PlaceAsync(c, product.Variants[0].Id, 1);
        await _l.DeliverAsync(admin, placed.GetProperty("orderId").GetGuid());
        await _l.DrainAsync();
        Assert.Equal(0, await _l.DbAsync(db => db.PointsLedgerEntries.CountAsync(e => e.CustomerId == c.Id, Ct)));
        var staffView = await H.JsonAsync(HttpMethod.Get, $"/api/v1/loyalty/customers/{c.Id}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(0, staffView.GetProperty("pointsBalance").GetInt32());
        await H.JsonAsync(HttpMethod.Post, "/api/v1/loyalty/gift-cards", HttpStatusCode.Created, new { amount = 500m, sendToRecipient = false }, admin);
    }
}

public class LoyaltyLedgerTests(LoyaltyFactory factory) : IClassFixture<LoyaltyFactory>
{
    private readonly LoyaltyHelper _l = new(factory);

    private StoreHelper H => _l.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static decimal Num(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    [Fact]
    public async Task Settings_are_validated_changed_by_staff_and_every_change_is_audited()
    {
        var admin = await H.AdminAsync();
        foreach (var bad in new object[] { new { maxTenderPercent = 150 }, new { pointValue = 0m }, new { earnRatePerBdt = -1m }, new { pointsExpiryMonths = 500 }, new { giftCardMinAmount = 500m, giftCardMaxAmount = 100m } })
        {
            var response = await H.SendAsync(HttpMethod.Put, "/api/v1/loyalty/settings", bad, admin);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        var saved = await H.JsonAsync(HttpMethod.Put, "/api/v1/loyalty/settings", HttpStatusCode.OK,
            new { enabled = true, earnRatePerBdt = 0.05m, earnRounding = "Nearest", pointValue = 0.5m, minPointsToRedeem = 200, maxTenderPercent = 30, pointsExpiryMonths = 6, expiryWarningDays = 10, referralRewardReferrer = 75m, referralRewardReferee = 25m }, admin);
        Assert.Equal((0.05m, "Nearest", 0.5m, 200, 30, 6), (Num(saved, "earnRatePerBdt"), saved.GetProperty("earnRounding").GetString(), Num(saved, "pointValue"), saved.GetProperty("minPointsToRedeem").GetInt32(),
            saved.GetProperty("maxTenderPercent").GetInt32(), saved.GetProperty("pointsExpiryMonths").GetInt32()));
        Assert.True(await _l.DbAsync(db => db.AuditLogs.AnyAsync(a => a.Entity == "PlatformSetting" && a.NewValue != null && a.NewValue.Contains("Loyalty.PointValue"), Ct)));
        await _l.EnableAsync(admin); // back to the simple numbers the other tests use
    }

    [Fact]
    public async Task Points_are_earned_when_the_order_is_delivered_on_the_goods_value_once_and_the_customer_is_told_once()
    {
        var (admin, product) = await _l.ProductAsync(1000m);
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        var placed = await _l.PlaceAsync(c, product.Variants[0].Id, 2);
        var orderId = placed.GetProperty("orderId").GetGuid();
        var delivery = Num((await _l.O.OrderAsync(admin, orderId)).GetProperty("money"), "deliveryCharge");

        await _l.O.ActAsync(admin, orderId, "confirm");
        await _l.DrainAsync();
        Assert.Equal(0, (await _l.LedgerAsync(c.Id)).Points); // nothing before delivery

        await _l.DeliverAsyncFrom(admin, orderId);
        await _l.DrainAsync();
        await _l.DrainAsync();
        var expected = (int)decimal.Floor(Num(placed, "grandTotal") - delivery); // 1 point per BDT, delivery not counted
        Assert.Equal(expected, (await _l.RewardsAsync(c)).GetProperty("pointsBalance").GetInt32());

        var entry = await _l.DbAsync(db => db.PointsLedgerEntries.AsNoTracking().SingleAsync(e => e.CustomerId == c.Id && e.Kind == PointsEntryKind.Earn, Ct));
        Assert.Equal($"earn:{orderId:N}", entry.ReferenceKey);
        Assert.InRange(entry.ExpiresAt!.Value, DateTimeOffset.UtcNow.AddMonths(12).AddMinutes(-5), DateTimeOffset.UtcNow.AddMonths(12).AddMinutes(5));

        // The event runs again (the outbox delivers at least once): nothing is earned twice and no second message is written.
        await _l.ScopedAsync(async sp =>
        {
            await sp.GetRequiredService<RewardsEngine>().OnOrderDeliveredAsync(orderId, Ct);
            return true;
        });
        Assert.Equal(expected, (await _l.LedgerAsync(c.Id)).Points);
        Assert.Equal(1, await _l.DbAsync(db => db.NotificationLogs.CountAsync(n => n.Template == "loyalty.points-earned" && n.Recipient == c.Phone, Ct)));
        await _l.AssertConsistentAsync(c.Id);
    }

    [Fact]
    public async Task A_refunded_return_takes_back_the_matching_share_of_the_earned_points_never_below_zero()
    {
        var (admin, product) = await _l.ProductAsync(1000m);
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        var placed = await _l.PlaceAsync(c, product.Variants[0].Id, 2);
        var orderId = placed.GetProperty("orderId").GetGuid();
        var delivery = Num((await _l.O.OrderAsync(admin, orderId)).GetProperty("money"), "deliveryCharge");
        await _l.DeliverAsync(admin, orderId);
        await _l.DrainAsync();
        var goods = Num(placed, "grandTotal") - delivery;
        var earned = (int)decimal.Floor(goods);
        Assert.Equal(earned, (await _l.LedgerAsync(c.Id)).Points);

        var returns = new ReturnHelper(_l.O);
        var d = new DeliveredOrder(orderId, placed.GetProperty("number").GetString()!, c.Mobile, c.Token, product.Variants[0].Id, delivery);
        var returnId = (await returns.RequestAsync(d, quantity: 1)).GetProperty("id").GetGuid();
        await returns.ToInspectedAsync(admin, returnId);
        var refunded = await returns.RefundAsync(admin, returnId, method: "Cash", reference: "RCPT-LOY-1");
        await _l.DrainAsync();
        var refund = Num(refunded.GetProperty("refundPaid"), "amount");

        var reversed = await _l.DbAsync(db => db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == c.Id && e.Kind == PointsEntryKind.Reverse).ToListAsync(Ct));
        var entry = Assert.Single(reversed);
        Assert.Equal(-(int)decimal.Round(earned * Math.Min(1m, refund / goods), 0, MidpointRounding.AwayFromZero), entry.Points);
        Assert.Equal(returnId, entry.ReturnRequestId);
        Assert.Equal(earned + entry.Points, (await _l.LedgerAsync(c.Id)).Points);

        // Spend what is left, then process the same return event again: the key stops a second reversal and the balance stays at zero or more.
        var left = (await _l.LedgerAsync(c.Id)).Points;
        await _l.AdjustAsync(admin, c.Id, points: -left, reason: "Spent elsewhere (test)");
        await _l.ScopedAsync(async sp =>
        {
            await sp.GetRequiredService<RewardsEngine>().OnReturnRefundedAsync(returnId, Ct);
            return true;
        });
        Assert.Equal(1, await _l.DbAsync(db => db.PointsLedgerEntries.CountAsync(e => e.CustomerId == c.Id && e.Kind == PointsEntryKind.Reverse, Ct)));
        await _l.AssertConsistentAsync(c.Id);
    }

    [Fact]
    public async Task Points_expire_after_their_date_warn_before_and_a_rerun_does_nothing()
    {
        var admin = await H.AdminAsync();
        await _l.EnableAsync(admin, new { expiryWarningDays = 14 });
        var c = await _l.CustomerAsync();
        await _l.AdjustAsync(admin, c.Id, points: 300);
        var lotId = await _l.DbAsync(db => db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == c.Id).Select(e => e.Id).SingleAsync(Ct));

        // Five days before the date: one warning, and a second run does not repeat it.
        await _l.DbAsync(db => db.PointsLedgerEntries.Where(e => e.Id == lotId).ExecuteUpdateAsync(s => s.SetProperty(e => e.ExpiresAt, DateTimeOffset.UtcNow.AddDays(5)), Ct));
        var first = await _l.ExpireAsync();
        Assert.Equal((0, 1), (first.PointsExpired, first.Warned));
        Assert.Equal(0, (await _l.ExpireAsync()).Warned);
        Assert.Equal(1, await _l.DbAsync(db => db.NotificationLogs.CountAsync(n => n.Template == "loyalty.points-expiring" && n.Recipient == c.Phone, Ct)));
        Assert.Equal(300, (await _l.LedgerAsync(c.Id)).Points);

        // Points that were already used are not expired twice: 100 left the account, 50 new ones are not due yet.
        await _l.AdjustAsync(admin, c.Id, points: -100, reason: "Used (test)");
        await _l.AdjustAsync(admin, c.Id, points: 50, reason: "Fresh points (test)");
        await _l.DbAsync(db => db.PointsLedgerEntries.Where(e => e.Id == lotId).ExecuteUpdateAsync(s => s.SetProperty(e => e.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));
        var run = await _l.ExpireAsync();
        Assert.Equal((1, 200), (run.Customers, run.PointsExpired)); // 300 in the old lot minus the 100 that were used
        Assert.Equal(50, (await _l.LedgerAsync(c.Id)).Points);
        var again = await _l.ExpireAsync();
        Assert.Equal(0, again.PointsExpired);
        Assert.Equal(1, await _l.DbAsync(db => db.PointsLedgerEntries.CountAsync(e => e.CustomerId == c.Id && e.Kind == PointsEntryKind.Expire, Ct)));
        await _l.AssertConsistentAsync(c.Id);
    }

    [Fact]
    public async Task Staff_adjust_balances_with_a_reason_never_below_zero_and_a_repeated_key_changes_nothing()
    {
        var admin = await H.AdminAsync();
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();

        var added = await _l.AdjustAsync(admin, c.Id, points: 200, credit: 75.5m, reason: "Goodwill for a late parcel", key: "adj-1");
        Assert.Equal((200, 75.5m), (added.GetProperty("pointsBalance").GetInt32(), Num(added, "creditBalance")));
        await _l.AdjustAsync(admin, c.Id, points: 200, credit: 75.5m, reason: "Goodwill for a late parcel", key: "adj-1"); // same key
        Assert.Equal((200, 75.5m), ((await _l.LedgerAsync(c.Id)).Points, (await _l.LedgerAsync(c.Id)).Credit));

        await StoreHelper.ExpectCodeAsync(await _l.TryAdjustAsync(admin, c.Id, new { points = -201, reason = "Too much" }), "LOYALTY.INSUFFICIENT_POINTS");
        await StoreHelper.ExpectCodeAsync(await _l.TryAdjustAsync(admin, c.Id, new { points = 10, credit = -80m, reason = "Too much credit" }), "LOYALTY.INSUFFICIENT_CREDIT");
        Assert.Equal(200, (await _l.LedgerAsync(c.Id)).Points); // a refused half leaves nothing behind
        Assert.Equal(HttpStatusCode.BadRequest, (await _l.TryAdjustAsync(admin, c.Id, new { points = 5, reason = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _l.TryAdjustAsync(admin, c.Id, new { reason = "Nothing to change" })).StatusCode);

        var entry = await _l.DbAsync(db => db.CreditLedgerEntries.AsNoTracking().SingleAsync(e => e.CustomerId == c.Id, Ct));
        Assert.Equal((CreditEntryKind.Adjust, 75.5m, "Goodwill for a late parcel"), (entry.Kind, entry.Amount, entry.Reason));
        Assert.False(string.IsNullOrEmpty(entry.ActorName));
        await _l.AssertConsistentAsync(c.Id);
    }

    [Fact]
    public async Task Permissions_and_customer_isolation_a_customer_only_sees_their_own_ledger()
    {
        var admin = await H.AdminAsync();
        await _l.EnableAsync(admin);
        var a = await _l.CustomerAsync();
        var b = await _l.CustomerAsync();
        await _l.AdjustAsync(admin, a.Id, points: 111, reason: "A only");
        await _l.AdjustAsync(admin, b.Id, points: 222, credit: 9m, reason: "B only");

        var pointsOfA = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/rewards/points", HttpStatusCode.OK, bearer: a.Token);
        Assert.Equal([111], pointsOfA.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("points").GetInt32()).ToArray());
        Assert.Equal(0, (await H.JsonAsync(HttpMethod.Get, "/api/v1/me/rewards/credit", HttpStatusCode.OK, bearer: a.Token)).GetProperty("total").GetInt32());
        Assert.Equal(222, (await _l.RewardsAsync(b)).GetProperty("pointsBalance").GetInt32());

        // A customer token never opens a staff screen; a staff member without the permission is refused too.
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, $"/api/v1/loyalty/customers/{b.Id}", null, a.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/loyalty/settings", null, a.Token)).StatusCode);
        var viewer = await _l.O.StaffTokenAsync(admin, null, "loyalty.points.view");
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, $"/api/v1/loyalty/customers/{b.Id}", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _l.TryAdjustAsync(viewer, b.Id, new { points = 5, reason = "Not allowed" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Put, "/api/v1/loyalty/settings", new { enabled = false }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/loyalty/gift-cards", new { amount = 500m }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/loyalty/referrals", null, viewer)).StatusCode);
        // An unknown customer is a clean "not found".
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, $"/api/v1/loyalty/customers/{Guid.NewGuid()}", null, admin), "LOYALTY.CUSTOMER_NOT_FOUND");
        // The staff view compares the stored balance with the sum of the ledger.
        var staff = await H.JsonAsync(HttpMethod.Get, $"/api/v1/loyalty/customers/{b.Id}", HttpStatusCode.OK, bearer: admin);
        Assert.True(staff.GetProperty("ledgerConsistent").GetBoolean());
    }

    [Fact]
    public void The_loyalty_endpoints_all_name_a_customer_policy_or_a_loyalty_permission()
    {
        var sources = factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>();
        var endpoints = sources.SelectMany(s => s.Endpoints).OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } t && (t.Contains("/me/rewards", StringComparison.Ordinal) || t.Contains("/loyalty", StringComparison.Ordinal))).ToList();
        Assert.True(endpoints.Count >= 17);
        foreach (var e in endpoints)
        {
            Assert.Null(e.Metadata.GetMetadata<Microsoft.AspNetCore.Authorization.IAllowAnonymous>());
            var policies = e.Metadata.GetOrderedMetadata<Microsoft.AspNetCore.Authorization.IAuthorizeData>().Select(a => a.Policy ?? "(default)").ToList();
            Assert.NotEmpty(policies);
            if (e.RoutePattern.RawText!.Contains("/loyalty", StringComparison.Ordinal))
            {
                Assert.Contains(policies, p => p.Contains("loyalty.", StringComparison.Ordinal));
            }
        }
    }
}
