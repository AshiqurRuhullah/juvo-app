using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Loyalty;
using Juvo.Domain.Loyalty;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Loyalty;

/// <summary>Points, store credit and gift cards as a tender at checkout: the total never changes, only what is left to pay (FR-AB01, AB03, AB04).</summary>
public class LoyaltyTenderTests(LoyaltyFactory factory) : IClassFixture<LoyaltyFactory>
{
    private readonly LoyaltyHelper _l = new(factory);

    private StoreHelper H => _l.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static decimal Num(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    [Fact]
    public async Task Points_pay_part_of_a_cash_order_and_the_quote_and_the_order_agree_while_the_total_stays_the_same()
    {
        var (admin, product) = await _l.ProductAsync(1000m);
        var variant = product.Variants[0].Id;
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        await _l.AdjustAsync(admin, c.Id, points: 500);

        var without = await _l.QuoteAsync(c, variant, 1, null);
        var quote = await _l.QuoteAsync(c, variant, 1, new { points = 200 });
        var grand = Num(quote, "grandTotal");
        Assert.Equal(Num(without, "grandTotal"), grand); // a tender is not a discount
        var tender = quote.GetProperty("tender");
        Assert.Equal(200, tender.GetProperty("pointsUsed").GetInt32());
        Assert.Equal(200m, Num(tender, "tenderTotal"));
        Assert.Equal(grand - 200m, Num(tender, "payableAfterTender"));
        Assert.Equal(grand - 200m, Num(quote, "payOnDelivery"));
        Assert.True(quote.GetProperty("canPlace").GetBoolean());

        var placed = await _l.PlaceAsync(c, variant, 1, "COD", new { points = 200 });
        Assert.Equal(grand, Num(placed, "grandTotal"));
        Assert.Equal(200m, Num(placed, "tenderTotal"));
        Assert.Equal(grand - 200m, Num(placed, "payOnDelivery"));
        var orderId = placed.GetProperty("orderId").GetGuid();

        var (order, payments, tenderRow, entries) = await _l.DbAsync(async db => (
            await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().SingleAsync(o => o.Id == orderId, Ct),
            await db.Payments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(p => p.OrderId == orderId).ToListAsync(Ct),
            await db.OrderTenders.AsNoTracking().SingleAsync(t => t.OrderId == orderId, Ct),
            await db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == c.Id).ToListAsync(Ct)));
        Assert.Equal(grand, order.GrandTotal);
        Assert.Equal(200m, order.AdvancePaid); // counted as received, so cash on delivery shrinks
        var payment = Assert.Single(payments);
        Assert.Equal((PaymentKind.StoreCredit, "POINTS", 200m, PaymentStatus.Paid), (payment.Kind, payment.Method, payment.Amount, payment.Status));
        Assert.Equal((200, 200m), (tenderRow.PointsUsed, tenderRow.PointsAmount));
        var redeem = Assert.Single(entries, e => e.Kind == PointsEntryKind.Redeem);
        Assert.Equal((-200, 300), (redeem.Points, redeem.BalanceAfter));
        Assert.Equal(300, (await _l.RewardsAsync(c)).GetProperty("pointsBalance").GetInt32());
        await _l.AssertConsistentAsync(c.Id);
    }

    [Fact]
    public async Task The_minimum_the_cap_and_the_balances_are_enforced_and_nothing_changes_when_a_tender_is_refused()
    {
        var (admin, product) = await _l.ProductAsync(1000m);
        var variant = product.Variants[0].Id;
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        await _l.AdjustAsync(admin, c.Id, points: 2000);

        await StoreHelper.ExpectCodeAsync(await _l.TryCheckoutAsync(c, variant, 1, "COD", new { points = 50 }), "LOYALTY.BELOW_MINIMUM_POINTS");
        await StoreHelper.ExpectCodeAsync(await _l.TryCheckoutAsync(c, variant, 1, "COD", new { points = 600 }), "LOYALTY.TENDER_EXCEEDS_CAP"); // half of about 1,060 is 530
        await StoreHelper.ExpectCodeAsync(await _l.TryCheckoutAsync(c, variant, 1, "COD", new { points = 100, credit = 10m }), "LOYALTY.INSUFFICIENT_CREDIT");
        await StoreHelper.ExpectCodeAsync(await _l.TryCheckoutAsync(c, variant, 1, "COD", new { points = 3000 }), "LOYALTY.INSUFFICIENT_POINTS");
        var quote = await _l.QuoteAsync(c, variant, 1, new { points = 600 });
        Assert.Equal("LOYALTY.TENDER_EXCEEDS_CAP", Str(quote.GetProperty("tender"), "problem"));
        Assert.False(quote.GetProperty("canPlace").GetBoolean());

        Assert.Equal(2000, (await _l.LedgerAsync(c.Id)).Points);
        Assert.Equal(0, await _l.DbAsync(db => db.OrderTenders.CountAsync(t => t.CustomerId == c.Id, Ct)));

        // The usable amount is what the rules allow right now.
        var usable = await H.JsonAsync(HttpMethod.Get, "/api/v1/me/rewards/usable?orderTotal=1000", HttpStatusCode.OK, bearer: c.Token);
        Assert.Equal(500m, Num(usable, "maxTenderAmount"));
        Assert.Equal(500, usable.GetProperty("maxPoints").GetInt32());
        Assert.Equal(0m, Num(usable, "maxCredit"));
    }

    [Fact]
    public async Task The_same_idempotency_key_never_redeems_twice()
    {
        var (admin, product) = await _l.ProductAsync(1000m);
        var variant = product.Variants[0].Id;
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        await _l.AdjustAsync(admin, c.Id, points: 400);
        var key = StoreHelper.Unique("idem");

        var first = await _l.PlaceAsync(c, variant, 1, "COD", new { points = 200 }, key);
        var again = await TestApi.ExpectAsync(await _l.TryCheckoutAsync(c, variant, 1, "COD", new { points = 200 }, key), HttpStatusCode.Created);
        Assert.Equal(first.GetProperty("orderId").GetGuid(), again.GetProperty("orderId").GetGuid());
        Assert.True(again.GetProperty("replayed").GetBoolean());

        var (points, _, _, _) = await _l.LedgerAsync(c.Id);
        Assert.Equal(200, points);
        Assert.Equal(1, await _l.DbAsync(db => db.PointsLedgerEntries.CountAsync(e => e.CustomerId == c.Id && e.Kind == PointsEntryKind.Redeem, Ct)));
    }

    [Fact]
    public async Task Parallel_redemptions_of_the_same_balance_succeed_only_as_far_as_the_balance_reaches_and_one_key_counts_once()
    {
        var admin = await H.AdminAsync();
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        await _l.AdjustAsync(admin, c.Id, points: 300, credit: 200m); // three redemptions of 100 points can be paid

        // Eight "orders" redeem 100 points each at the same moment, every one in its own scope and transaction like a real request.
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => _l.ScopedAsync(async sp =>
            (await sp.GetRequiredService<LoyaltyLedger>().PostPointsAsync(new PointsPosting(c.Id, PointsEntryKind.Redeem, -100, "parallel test", $"order:{Guid.NewGuid():N}:points"), Ct)).Error?.Code ?? "OK")));
        Assert.Equal(3, results.Count(r => r == "OK"));
        Assert.All(results.Where(r => r != "OK"), r => Assert.Equal("LOYALTY.INSUFFICIENT_POINTS", r));
        Assert.Equal(0, (await _l.LedgerAsync(c.Id)).Points);
        Assert.Equal(3, await _l.DbAsync(db => db.PointsLedgerEntries.CountAsync(e => e.CustomerId == c.Id && e.Kind == PointsEntryKind.Redeem, Ct)));

        // The same order (the same key) arriving six times at once spends the credit once; the others see the first result.
        var same = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => _l.ScopedAsync(async sp =>
            (await sp.GetRequiredService<LoyaltyLedger>().PostCreditAsync(new CreditPosting(c.Id, CreditEntryKind.Spend, -50m, "same order", "order:one-and-only:credit"), Ct)).Value)));
        Assert.Equal(1, same.Count(r => !r.Replayed));
        Assert.Equal(150m, (await _l.LedgerAsync(c.Id)).Credit);
        Assert.All(same, r => Assert.Equal(-50m, r.Changed));

        // A gift card of 300 spent by five orders at once pays three of them.
        var card = await H.JsonAsync(HttpMethod.Post, "/api/v1/loyalty/gift-cards", HttpStatusCode.Created, new { amount = 300m, sendToRecipient = false }, admin);
        var cardId = card.GetProperty("card").GetProperty("id").GetGuid();
        var spent = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => _l.ScopedAsync(async sp =>
            (await sp.GetRequiredService<LoyaltyLedger>().SpendGiftCardAsync(cardId, 100m, GiftCardEntryKind.Spend, $"order:{Guid.NewGuid():N}", null, null, null, null, null, Ct)).IsSuccess)));
        Assert.Equal(3, spent.Count(s => s));
        Assert.Equal(0m, await _l.DbAsync(db => db.GiftCards.Where(g => g.Id == cardId).Select(g => g.Balance).SingleAsync(Ct)));
        await _l.AssertConsistentAsync(c.Id);
    }

    [Fact]
    public async Task Store_credit_and_a_gift_card_pay_a_whole_order_which_is_then_paid_and_cancelling_it_gives_everything_back_as_credit()
    {
        var (admin, product) = await _l.ProductAsync(1000m);
        var variant = product.Variants[0].Id;
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        await _l.AdjustAsync(admin, c.Id, credit: 300m);
        var card = await H.JsonAsync(HttpMethod.Post, "/api/v1/loyalty/gift-cards", HttpStatusCode.Created, new { amount = 5000m, note = "test", sendToRecipient = false }, admin);
        var code = Str(card, "code");
        var cardId = card.GetProperty("card").GetProperty("id").GetGuid();

        var placed = await _l.PlaceAsync(c, variant, 1, "COD", new { credit = 100m, giftCardCode = code });
        var grand = Num(placed, "grandTotal");
        Assert.Equal("Paid", Str(placed, "paymentStatus"));
        Assert.Equal(0m, Num(placed, "payNow"));
        Assert.Equal(0m, Num(placed, "payOnDelivery"));
        Assert.Equal(grand, Num(placed, "tenderTotal"));
        var orderId = placed.GetProperty("orderId").GetGuid();

        var methods = await _l.DbAsync(db => db.Payments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(p => p.OrderId == orderId).OrderBy(p => p.Method).Select(p => new { p.Method, p.Amount }).ToListAsync(Ct));
        Assert.Equal([("CREDIT", 100m), ("GIFTCARD", grand - 100m)], methods.Select(m => (m.Method, m.Amount)).ToArray());
        var detail = await H.JsonAsync(HttpMethod.Get, $"/api/v1/loyalty/gift-cards/{cardId}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(5000m - (grand - 100m), Num(detail.GetProperty("card"), "balance"));
        Assert.Equal(200m, (await _l.LedgerAsync(c.Id)).Credit);

        // The customer cancels before the order leaves the shop: the credit and the card value come back as store credit.
        await H.JsonAsync(HttpMethod.Post, $"/api/v1/me/orders/{orderId}/cancel", HttpStatusCode.OK, new { reason = "Changed my mind" }, c.Token);
        var (_, credit, _, creditSum) = await _l.LedgerAsync(c.Id);
        Assert.Equal(300m + (grand - 100m), credit);
        Assert.Equal(credit, creditSum);
        var refunds = await _l.DbAsync(db => db.Refunds.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(r => r.OrderId == orderId).ToListAsync(Ct));
        Assert.Equal(2, refunds.Count);
        Assert.All(refunds, r => Assert.Equal((RefundMode.StoreCredit, RefundState.Completed), (r.Mode, r.Status)));
        Assert.Equal("Refunded", Str(await _l.O.OrderAsync(admin, orderId), "paymentStatus"));

        // A second cancel changes nothing.
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/me/orders/{orderId}/cancel", new { reason = "Again" }, c.Token), "ORDER.CANNOT_CANCEL");
        Assert.Equal(credit, (await _l.LedgerAsync(c.Id)).Credit);
    }

    [Fact]
    public async Task Cancelling_an_order_paid_partly_with_points_returns_the_points_and_the_rest_follows_the_payment_methods()
    {
        var (admin, product) = await _l.ProductAsync(1000m);
        var variant = product.Variants[0].Id;
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        await _l.AdjustAsync(admin, c.Id, points: 500);
        var placed = await _l.PlaceAsync(c, variant, 1, "COD", new { points = 300 });
        Assert.Equal(200, (await _l.LedgerAsync(c.Id)).Points);
        var orderId = placed.GetProperty("orderId").GetGuid();

        await _l.O.ActAsync(admin, orderId, "cancel", new { reason = "Out of stock at packing" });

        var (points, _, sum, _) = await _l.LedgerAsync(c.Id);
        Assert.Equal(500, points);
        Assert.Equal(sum, points);
        var kinds = await _l.DbAsync(db => db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == c.Id && e.OrderId == orderId).OrderBy(e => e.At).Select(e => e.Kind).ToListAsync(Ct));
        Assert.Equal([PointsEntryKind.Redeem, PointsEntryKind.Release], kinds);
        var payment = await _l.DbAsync(db => db.Payments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().SingleAsync(p => p.OrderId == orderId, Ct));
        Assert.Equal((PaymentStatus.Refunded, 300m), (payment.Status, payment.RefundedAmount));
        Assert.Equal("Cancelled", Str(await _l.O.OrderAsync(admin, orderId), "paymentStatus"));
    }

    [Fact]
    public async Task An_online_order_paid_partly_with_credit_asks_the_gateway_only_for_the_rest()
    {
        var (admin, product) = await _l.ProductAsync(1000m);
        var variant = product.Variants[0].Id;
        await _l.EnableAsync(admin);
        var c = await _l.CustomerAsync();
        await _l.AdjustAsync(admin, c.Id, credit: 200m);
        var placed = await _l.PlaceAsync(c, variant, 1, "SANDBOX", new { credit = 200m });
        var grand = Num(placed, "grandTotal");
        Assert.Equal("Pending", Str(placed, "paymentStatus"));
        Assert.Equal(grand - 200m, Num(placed, "payNow"));

        var session = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/payments/initiate", HttpStatusCode.OK,
            new { orderNumber = Str(placed, "number"), trackingToken = Str(placed, "trackingToken"), method = (string?)null, returnUrl = "/checkout/return" });
        Assert.Equal(grand - 200m, Num(session, "amount"));
    }

    [Fact]
    public async Task A_guest_can_pay_with_a_gift_card_but_points_and_credit_need_a_sign_in()
    {
        var (admin, product) = await _l.ProductAsync(1000m);
        var variant = product.Variants[0].Id;
        await _l.EnableAsync(admin);
        var mobile = StoreHelper.NewMobile();
        var otp = await _l.O.OtpAsync(mobile);
        var cart = await _l.O.CartAsync(variant, 1);
        var address = new { recipientName = "Guest", countryCode = "BD", districtId = await _l.O.DistrictAsync(), addressLine = "House 3, Road 4" };

        var signIn = await H.SendAsync(HttpMethod.Post, "/api/v1/store/checkout", new { contact = new { mobile }, address, paymentMethod = "COD", acceptTerms = true, otpCode = otp, tender = new { points = 100 } }, null, cart);
        await StoreHelper.ExpectCodeAsync(signIn, "LOYALTY.TENDER_NEEDS_SIGN_IN");

        var card = await H.JsonAsync(HttpMethod.Post, "/api/v1/loyalty/gift-cards", HttpStatusCode.Created, new { amount = 300m, sendToRecipient = false }, admin);
        var paid = await TestApi.ExpectAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/store/checkout",
            new { contact = new { mobile }, address, paymentMethod = "COD", acceptTerms = true, otpCode = await _l.O.OtpAsync(mobile), tender = new { giftCardCode = Str(card, "code").ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal) } }, null, cart), HttpStatusCode.Created);
        Assert.Equal(300m, Num(paid, "tenderTotal")); // codes are case and separator insensitive
    }
}
