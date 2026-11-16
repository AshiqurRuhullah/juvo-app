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

/// <summary>Referral codes and rewards (FR-AB02) with the simple fraud rules, and refunds that go to store credit (FR-AB03).</summary>
public class ReferralTests(LoyaltyFactory factory) : IClassFixture<LoyaltyFactory>
{
    private readonly LoyaltyHelper _l = new(factory);

    private StoreHelper H => _l.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private async Task<string> CodeAsync(RewardCustomer c) => Str(await H.JsonAsync(HttpMethod.Get, "/api/v1/me/rewards/referral", HttpStatusCode.OK, bearer: c.Token), "code");

    private Task<HttpResponseMessage> ApplyAsync(RewardCustomer c, string code) => H.SendAsync(HttpMethod.Post, "/api/v1/me/rewards/referral/apply", new { code }, c.Token);

    private async Task<ReferralStatus> StatusOfAsync(Guid refereeId) => await _l.DbAsync(db => db.Referrals.AsNoTracking().Where(r => r.RefereeId == refereeId).Select(r => r.Status).SingleAsync(Ct));

    /// <summary>The referee orders and the order is delivered and the events are processed.</summary>
    private async Task<(Guid OrderId, JsonElement Placed)> DeliveredAsync(string admin, RewardCustomer c, Guid variantId, int quantity = 1, string? addressLine = null, string? phone = null)
    {
        var placed = await _l.PlaceAsync(c, variantId, quantity, addressLine: addressLine, phone: phone);
        var id = placed.GetProperty("orderId").GetGuid();
        await _l.DeliverAsync(admin, id);
        await _l.DrainAsync();
        return (id, placed);
    }

    [Fact]
    public async Task A_code_is_made_once_hard_to_guess_and_accepted_in_any_case_with_the_rules_for_who_may_use_it()
    {
        var (admin, product) = await _l.ProductAsync(500m);
        await _l.EnableAsync(admin);
        var a = await _l.CustomerAsync();
        var b = await _l.CustomerAsync();

        var code = await CodeAsync(a);
        Assert.Matches("^[A-Z2-9]{8}$", code);
        Assert.Equal(code, await CodeAsync(a)); // made on the first request, then the same
        Assert.Equal(1, await _l.DbAsync(db => db.ReferralCodes.CountAsync(r => r.CustomerId == a.Id, Ct)));

        await StoreHelper.ExpectCodeAsync(await ApplyAsync(a, code), "LOYALTY.REFERRAL_SELF");
        await StoreHelper.ExpectCodeAsync(await ApplyAsync(b, "NOSUCH12"), "LOYALTY.REFERRAL_CODE_INVALID");

        var applied = await TestApi.ExpectAsync(await ApplyAsync(b, $" {code[..4].ToLowerInvariant()}-{code[4..].ToLowerInvariant()} "), HttpStatusCode.OK);
        Assert.Equal("Pending", Str(applied, "myStatus"));
        await StoreHelper.ExpectCodeAsync(await ApplyAsync(b, code), "LOYALTY.REFERRAL_ALREADY_APPLIED");

        // Only a new customer: somebody who already ordered cannot enter a code.
        var veteran = await _l.CustomerAsync();
        await _l.PlaceAsync(veteran, product.Variants[0].Id, 1);
        await StoreHelper.ExpectCodeAsync(await ApplyAsync(veteran, code), "LOYALTY.REFERRAL_NOT_ELIGIBLE");

        var mine = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/rewards/referral", HttpStatusCode.OK, bearer: a.Token);
        Assert.Equal((0, 1), (mine.GetProperty("rewarded").GetInt32(), mine.GetProperty("pending").GetInt32()));
    }

    [Fact]
    public async Task Both_sides_get_the_reward_only_after_the_first_order_is_delivered_and_only_once()
    {
        var (admin, product) = await _l.ProductAsync(500m);
        await _l.EnableAsync(admin);
        var a = await _l.CustomerAsync();
        var b = await _l.CustomerAsync();
        await TestApi.ExpectAsync(await ApplyAsync(b, await CodeAsync(a)), HttpStatusCode.OK);

        var placed = await _l.PlaceAsync(b, product.Variants[0].Id, 1);
        var orderId = placed.GetProperty("orderId").GetGuid();
        await _l.O.ActAsync(admin, orderId, "confirm");
        await _l.DrainAsync();
        Assert.Equal((0m, 0m), ((await _l.LedgerAsync(a.Id)).Credit, (await _l.LedgerAsync(b.Id)).Credit)); // not before delivery
        Assert.Equal(ReferralStatus.Pending, await StatusOfAsync(b.Id));

        await _l.DeliverAsyncFrom(admin, orderId);
        await _l.DrainAsync();
        await _l.DrainAsync();
        Assert.Equal((100m, 50m), ((await _l.LedgerAsync(a.Id)).Credit, (await _l.LedgerAsync(b.Id)).Credit));
        Assert.Equal(ReferralStatus.Rewarded, await StatusOfAsync(b.Id));

        await _l.ScopedAsync(async sp =>
        {
            await sp.GetRequiredService<RewardsEngine>().OnOrderDeliveredAsync(orderId, Ct); // the event again
            return true;
        });
        Assert.Equal((100m, 50m), ((await _l.LedgerAsync(a.Id)).Credit, (await _l.LedgerAsync(b.Id)).Credit));
        Assert.Equal(1, await _l.DbAsync(db => db.NotificationLogs.CountAsync(n => n.Template == "loyalty.referral-reward" && n.Recipient == a.Phone, Ct)));
        Assert.Equal(1, await _l.DbAsync(db => db.NotificationLogs.CountAsync(n => n.Template == "loyalty.referral-welcome" && n.Recipient == b.Phone, Ct)));

        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/loyalty/referrals?status=Rewarded", HttpStatusCode.OK, bearer: admin);
        Assert.Contains(list.GetProperty("items").EnumerateArray(), i => i.GetProperty("refereeId").GetGuid() == b.Id && i.GetProperty("referrerReward").GetDecimal() == 100m);
        await _l.AssertConsistentAsync(a.Id);
        await _l.AssertConsistentAsync(b.Id);
    }

    [Fact]
    public async Task The_fraud_rules_reject_the_same_phone_the_same_address_a_repeated_household_and_a_referrer_over_the_limit()
    {
        var (admin, product) = await _l.ProductAsync(500m, stock: 100);
        await _l.EnableAsync(admin, new { referralMaxRewardsPerReferrer = 1 });
        var a = await _l.CustomerAsync();
        var code = await CodeAsync(a);

        // The referrer orders to "Home of A"; a friend then orders to the very same address: no reward.
        await _l.PlaceAsync(a, product.Variants[0].Id, 1, addressLine: "Home of A, Road 1");
        var sameAddress = await _l.CustomerAsync();
        await TestApi.ExpectAsync(await ApplyAsync(sameAddress, code), HttpStatusCode.OK);
        await DeliveredAsync(admin, sameAddress, product.Variants[0].Id, addressLine: "home of a, road 1");
        Assert.Equal(ReferralStatus.Rejected, await StatusOfAsync(sameAddress.Id));
        Assert.Contains("same address", await _l.DbAsync(db => db.Referrals.AsNoTracking().Where(r => r.RefereeId == sameAddress.Id).Select(r => r.Note).SingleAsync(Ct)), StringComparison.Ordinal);

        // A parcel sent to the referrer's own mobile number is not a new household either.
        var samePhone = await _l.CustomerAsync();
        await TestApi.ExpectAsync(await ApplyAsync(samePhone, code), HttpStatusCode.OK);
        await DeliveredAsync(admin, samePhone, product.Variants[0].Id, addressLine: "Somewhere else 9", phone: a.Mobile);
        Assert.Equal(ReferralStatus.Rejected, await StatusOfAsync(samePhone.Id));

        // A genuine friend is rewarded; a second genuine friend finds the referrer at the limit; a third customer at the first friend's address is a repeated household.
        var friend = await _l.CustomerAsync();
        await TestApi.ExpectAsync(await ApplyAsync(friend, code), HttpStatusCode.OK);
        await DeliveredAsync(admin, friend, product.Variants[0].Id, addressLine: "Friend House 21");
        Assert.Equal(ReferralStatus.Rewarded, await StatusOfAsync(friend.Id));

        var second = await _l.CustomerAsync();
        await TestApi.ExpectAsync(await ApplyAsync(second, code), HttpStatusCode.OK);
        await DeliveredAsync(admin, second, product.Variants[0].Id, addressLine: "Another House 33");
        Assert.Equal(ReferralStatus.Rejected, await StatusOfAsync(second.Id));
        Assert.Contains("limit", await _l.DbAsync(db => db.Referrals.AsNoTracking().Where(r => r.RefereeId == second.Id).Select(r => r.Note).SingleAsync(Ct)), StringComparison.Ordinal);

        var other = await _l.CustomerAsync();
        var otherCode = await CodeAsync(other);
        var twin = await _l.CustomerAsync();
        await TestApi.ExpectAsync(await ApplyAsync(twin, otherCode), HttpStatusCode.OK);
        await DeliveredAsync(admin, twin, product.Variants[0].Id, addressLine: "Friend House 21");
        Assert.Equal(ReferralStatus.Rejected, await StatusOfAsync(twin.Id));

        // Only the genuine friend and the referrer were paid, once.
        Assert.Equal(100m, (await _l.LedgerAsync(a.Id)).Credit);
        Assert.Equal(50m, (await _l.LedgerAsync(friend.Id)).Credit);
        Assert.Equal(0m, (await _l.LedgerAsync(sameAddress.Id)).Credit + (await _l.LedgerAsync(samePhone.Id)).Credit + (await _l.LedgerAsync(second.Id)).Credit + (await _l.LedgerAsync(twin.Id)).Credit);
    }

    [Fact]
    public async Task When_the_whole_first_order_is_returned_the_rewards_are_taken_back_as_far_as_the_balances_allow()
    {
        var (admin, product) = await _l.ProductAsync(500m);
        await _l.EnableAsync(admin);
        var a = await _l.CustomerAsync();
        var b = await _l.CustomerAsync();
        await TestApi.ExpectAsync(await ApplyAsync(b, await CodeAsync(a)), HttpStatusCode.OK);
        var (orderId, placed) = await DeliveredAsync(admin, b, product.Variants[0].Id);
        Assert.Equal(ReferralStatus.Rewarded, await StatusOfAsync(b.Id));
        Assert.Equal(50m, (await _l.LedgerAsync(b.Id)).Credit);

        // The new customer spends part of the welcome reward, then returns the order.
        await _l.AdjustAsync(admin, b.Id, credit: -20m, reason: "Spent (test)");
        var returns = new ReturnHelper(_l.O);
        var delivery = (await _l.O.OrderAsync(admin, orderId)).GetProperty("money").GetProperty("deliveryCharge").GetDecimal();
        var d = new DeliveredOrder(orderId, Str(placed, "number"), b.Mobile, b.Token, product.Variants[0].Id, delivery);
        var returnId = (await returns.RequestAsync(d, quantity: 1)).GetProperty("id").GetGuid();
        await returns.ToInspectedAsync(admin, returnId);
        await returns.RefundAsync(admin, returnId, method: "Cash", reference: "RCPT-REF-1");
        await _l.DrainAsync();

        Assert.Equal(ReferralStatus.Reversed, await StatusOfAsync(b.Id));
        Assert.Equal(0m, (await _l.LedgerAsync(a.Id)).Credit); // the referrer's 100 are taken back
        Assert.Equal(0m, (await _l.LedgerAsync(b.Id)).Credit); // only the 30 that were left, never below zero
        await _l.AssertConsistentAsync(a.Id);
        await _l.AssertConsistentAsync(b.Id);
    }
}

public class RefundToCreditTests(LoyaltyFactory factory) : IClassFixture<LoyaltyFactory>
{
    private readonly LoyaltyHelper _l = new(factory);

    private StoreHelper H => _l.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private async Task<DeliveredOrder> DeliveredAsync(string admin, RewardCustomer c, Guid variantId, int quantity, object? tender = null)
    {
        var placed = await _l.PlaceAsync(c, variantId, quantity, tender: tender);
        var id = placed.GetProperty("orderId").GetGuid();
        await _l.DeliverAsync(admin, id);
        var delivery = (await _l.O.OrderAsync(admin, id)).GetProperty("money").GetProperty("deliveryCharge").GetDecimal();
        return new DeliveredOrder(id, Str(placed, "number"), c.Mobile, c.Token, variantId, delivery);
    }

    [Fact]
    public async Task A_return_refund_taken_as_store_credit_completes_at_once_into_the_ledger_with_a_credit_note_and_no_cash()
    {
        var (admin, product) = await _l.ProductAsync(800m);
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        var d = await DeliveredAsync(admin, c, product.Variants[0].Id, 1);
        var returns = new ReturnHelper(_l.O);
        var returnId = (await returns.RequestAsync(d)).GetProperty("id").GetGuid();
        await returns.ToInspectedAsync(admin, returnId);

        var refunded = await returns.RefundAsync(admin, returnId, method: "StoreCredit", reference: "");
        var amount = refunded.GetProperty("refundPaid").GetProperty("amount").GetDecimal();

        var ledger = await _l.LedgerAsync(c.Id);
        Assert.Equal((amount, amount), (ledger.Credit, ledger.CreditSum));
        var entry = await _l.DbAsync(db => db.CreditLedgerEntries.AsNoTracking().SingleAsync(e => e.CustomerId == c.Id && e.Kind == CreditEntryKind.Refund, Ct));
        Assert.Equal((amount, returnId), (entry.Amount, entry.ReturnRequestId));
        var refund = await _l.DbAsync(db => db.Refunds.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().SingleAsync(r => r.OrderId == d.OrderId, Ct));
        Assert.Equal((RefundMode.StoreCredit, RefundState.Completed, amount), (refund.Mode, refund.Status, refund.Amount));
        Assert.NotNull(refund.CreditNoteId);
        Assert.Equal(0, await _l.DbAsync(db => db.CreditLedgerEntries.CountAsync(e => e.CustomerId == c.Id && e.Kind != CreditEntryKind.Refund, Ct)));
    }

    [Fact]
    public async Task A_return_on_an_order_paid_partly_with_points_gives_the_tender_share_back_as_points_and_the_rest_as_money()
    {
        var (admin, product) = await _l.ProductAsync(1000m);
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        await _l.AdjustAsync(admin, c.Id, points: 500);
        var d = await DeliveredAsync(admin, c, product.Variants[0].Id, 1, new { points = 400 });
        await _l.DrainAsync();
        var returns = new ReturnHelper(_l.O);
        var returnId = (await returns.RequestAsync(d)).GetProperty("id").GetGuid();
        await returns.ToInspectedAsync(admin, returnId);
        var refunded = await returns.RefundAsync(admin, returnId, method: "Cash", reference: "RCPT-TENDER-1");
        var total = refunded.GetProperty("refundPaid").GetProperty("amount").GetDecimal();

        var refunds = await _l.DbAsync(db => db.Refunds.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(r => r.OrderId == d.OrderId).ToListAsync(Ct));
        var tenderRefund = Assert.Single(refunds, r => r.Mode == RefundMode.StoreCredit);
        var cashRefund = Assert.Single(refunds, r => r.Mode == RefundMode.Manual);
        Assert.Equal(total, tenderRefund.Amount + cashRefund.Amount); // together exactly the refund of the return
        // The cash on delivery payment held the total minus the 400 paid with points: the tender part of the refund is in proportion to the two.
        Assert.Equal(PointsMath.TenderShareOfRefund(total, 400m, 1000m + d.DeliveryCharge - 400m), tenderRefund.Amount);
        Assert.NotNull(tenderRefund.CreditNoteId);
        Assert.NotNull(cashRefund.CreditNoteId);

        // The share of the tender went back as points (rounded to a whole point), recorded as a release linked to the return.
        var release = await _l.DbAsync(db => db.PointsLedgerEntries.AsNoTracking().SingleAsync(e => e.CustomerId == c.Id && e.Kind == PointsEntryKind.Release, Ct));
        Assert.Equal((int)decimal.Round(400m * tenderRefund.Amount / 400m, 0, MidpointRounding.AwayFromZero), release.Points);
        Assert.Equal(returnId, release.ReturnRequestId);
        await _l.AssertConsistentAsync(c.Id);
        var tender = await _l.DbAsync(db => db.OrderTenders.AsNoTracking().SingleAsync(t => t.OrderId == d.OrderId, Ct));
        Assert.Equal(release.Points, tender.PointsReturned);
    }

    [Fact]
    public async Task Staff_can_refund_a_cash_payment_to_store_credit_but_never_more_than_was_paid()
    {
        var (admin, product) = await _l.ProductAsync(600m);
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        var placed = await _l.PlaceAsync(c, product.Variants[0].Id, 1, "SANDBOX");
        var orderId = placed.GetProperty("orderId").GetGuid();
        var session = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/payments/initiate", HttpStatusCode.OK,
            new { orderNumber = Str(placed, "number"), trackingToken = Str(placed, "trackingToken"), method = (string?)null, returnUrl = "/checkout/return" });
        await H.JsonAsync(HttpMethod.Post, $"/api/v1/store/payments/sandbox/sessions/{Str(session, "sandboxSessionId")}/complete", HttpStatusCode.OK, new { outcome = "success", sendWebhook = true });
        var paymentId = session.GetProperty("paymentId").GetGuid();
        await H.JsonAsync(HttpMethod.Post, $"/api/v1/store/payments/{paymentId}/confirm?token={Uri.EscapeDataString(Str(placed, "trackingToken"))}", HttpStatusCode.OK, new { });

        var created = await H.JsonAsync(HttpMethod.Post, $"/api/v1/payments/{paymentId}/refunds", HttpStatusCode.Created, new { amount = 250m, mode = "StoreCredit", reason = "Goodwill as store credit" }, admin);
        Assert.Equal(("StoreCredit", "Completed"), (Str(created, "mode"), Str(created, "status")));
        Assert.Equal(250m, (await _l.LedgerAsync(c.Id)).Credit);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/payments/{paymentId}/refunds", new { amount = 1000m, mode = "StoreCredit", reason = "Too much" }, admin), "PAYMENT.REFUND_EXCEEDS_PAID");
        Assert.Equal(250m, (await _l.LedgerAsync(c.Id)).Credit);
        Assert.Equal("PartiallyRefunded", Str(await _l.O.OrderAsync(admin, orderId), "paymentStatus"));
        await _l.AssertConsistentAsync(c.Id);
    }
}
