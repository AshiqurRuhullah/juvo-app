using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Loyalty;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Loyalty;

/// <summary>Gift cards (FR-AB03): created by staff, the code shown once and stored as a hash, moved into store credit by the customer, with guessing protection.</summary>
public class GiftCardTests(LoyaltyFactory factory) : IClassFixture<LoyaltyFactory>
{
    private readonly LoyaltyHelper _l = new(factory);

    private StoreHelper H => _l.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private static decimal Num(JsonElement e, string name) => e.GetProperty(name).GetDecimal();

    private async Task<(Guid Id, string Code)> NewCardAsync(string admin, decimal amount = 500m, object? extra = null)
    {
        var body = new Dictionary<string, object?> { ["amount"] = amount, ["sendToRecipient"] = false };
        if (extra is not null)
        {
            foreach (var p in extra.GetType().GetProperties())
            {
                body[p.Name] = p.GetValue(extra);
            }
        }

        var created = await H.JsonAsync(HttpMethod.Post, "/api/v1/loyalty/gift-cards", HttpStatusCode.Created, body, admin);
        return (created.GetProperty("card").GetProperty("id").GetGuid(), Str(created, "code"));
    }

    [Fact]
    public async Task A_created_card_shows_its_code_once_and_the_database_only_holds_a_hash_within_the_limits()
    {
        var admin = await H.AdminAsync();
        await _l.EnableAsync(admin);
        var created = await H.JsonAsync(HttpMethod.Post, "/api/v1/loyalty/gift-cards", HttpStatusCode.Created, new { amount = 750m, note = "Eid gift", sendToRecipient = false }, admin);
        var code = Str(created, "code");
        var card = created.GetProperty("card");
        var id = card.GetProperty("id").GetGuid();
        Assert.Matches("^[A-Z2-9]{4}(-[A-Z2-9]{4}){3}$", code);
        Assert.Equal(("Active", 750m, 750m), (Str(card, "status"), Num(card, "balance"), Num(card, "initialAmount")));
        Assert.Equal(code[^4..], Str(card, "codeHint"));
        Assert.InRange(card.GetProperty("expiresAt").GetDateTimeOffset(), DateTimeOffset.UtcNow.AddMonths(12).AddMinutes(-5), DateTimeOffset.UtcNow.AddMonths(12).AddMinutes(5));

        // No screen can show the code again: not the detail, not the list.
        var detail = await (await H.SendAsync(HttpMethod.Get, $"/api/v1/loyalty/gift-cards/{id}", null, admin)).Content.ReadAsStringAsync(Ct);
        var list = await (await H.SendAsync(HttpMethod.Get, "/api/v1/loyalty/gift-cards?status=active", null, admin)).Content.ReadAsStringAsync(Ct);
        Assert.DoesNotContain(code, detail, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(code.Replace("-", string.Empty, StringComparison.Ordinal), list, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(id.ToString(), list, StringComparison.OrdinalIgnoreCase);
        var row = await _l.DbAsync(db => db.GiftCards.AsNoTracking().SingleAsync(c => c.Id == id, Ct));
        Assert.Equal(64, row.CodeHash.Length);
        Assert.DoesNotContain(code.Replace("-", string.Empty, StringComparison.Ordinal), row.CodeHash, StringComparison.OrdinalIgnoreCase);

        // Limits: below the minimum, above the maximum and a fraction of a paisa are refused.
        foreach (var amount in new[] { 50m, 25000m, 100.555m })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await H.SendAsync(HttpMethod.Post, "/api/v1/loyalty/gift-cards", new { amount, sendToRecipient = false }, admin)).StatusCode);
        }
    }

    [Fact]
    public async Task A_customer_moves_a_card_into_store_credit_once_and_nobody_can_use_it_again()
    {
        var admin = await H.AdminAsync();
        await _l.EnableAsync(admin);
        var (cardId, code) = await NewCardAsync(admin, 400m);
        var a = await _l.CustomerAsync();
        var b = await _l.CustomerAsync();

        var redeemed = await H.JsonAsync(HttpMethod.Post, "/api/v1/me/rewards/gift-cards/redeem", HttpStatusCode.OK, new { code = code.ToLowerInvariant() }, a.Token);
        Assert.Equal((400m, 400m), (Num(redeemed, "added"), Num(redeemed, "creditBalance")));
        Assert.Equal(400m, Num(await _l.RewardsAsync(a), "creditBalance"));
        var detail = await H.JsonAsync(HttpMethod.Get, $"/api/v1/loyalty/gift-cards/{cardId}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(("Used", 0m), (Str(detail.GetProperty("card"), "status"), Num(detail.GetProperty("card"), "balance")));
        Assert.Equal(["Issue", "RedeemToCredit"], detail.GetProperty("entries").EnumerateArray().Select(e => Str(e, "kind")).ToArray());
        var entry = await _l.DbAsync(db => db.CreditLedgerEntries.AsNoTracking().SingleAsync(e => e.CustomerId == a.Id, Ct));
        Assert.Equal((CreditEntryKind.GiftCardRedeem, 400m, cardId), (entry.Kind, entry.Amount, entry.GiftCardId));

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/me/rewards/gift-cards/redeem", new { code }, a.Token), "LOYALTY.GIFT_CARD_EMPTY");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/me/rewards/gift-cards/redeem", new { code }, b.Token), "LOYALTY.GIFT_CARD_EMPTY");
        Assert.Equal(0m, (await _l.LedgerAsync(b.Id)).Credit);
        await _l.AssertConsistentAsync(a.Id);
    }

    [Fact]
    public async Task Two_customers_redeeming_the_same_card_at_the_same_moment_get_the_money_once()
    {
        var admin = await H.AdminAsync();
        await _l.EnableAsync(admin);
        var (_, code) = await NewCardAsync(admin, 600m);
        var customers = new[] { await _l.CustomerAsync(), await _l.CustomerAsync(), await _l.CustomerAsync() };

        var responses = await Task.WhenAll(customers.Select(c => H.SendAsync(HttpMethod.Post, "/api/v1/me/rewards/gift-cards/redeem", new { code }, c.Token)));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));
        foreach (var r in responses.Where(r => r.StatusCode != HttpStatusCode.OK))
        {
            await StoreHelper.ExpectCodeAsync(r, "LOYALTY.GIFT_CARD_EMPTY");
        }

        var total = 0m;
        foreach (var c in customers)
        {
            total += (await _l.LedgerAsync(c.Id)).Credit;
            await _l.AssertConsistentAsync(c.Id);
        }

        Assert.Equal(600m, total);
    }

    [Fact]
    public async Task An_expired_or_deactivated_card_cannot_be_used_and_deactivating_twice_is_harmless()
    {
        var admin = await H.AdminAsync();
        await _l.EnableAsync(admin);
        var (expiredId, expiredCode) = await NewCardAsync(admin, 300m);
        var (offId, offCode) = await NewCardAsync(admin, 300m);
        var c = await _l.CustomerAsync();

        await _l.DbAsync(db => db.GiftCards.Where(g => g.Id == expiredId).ExecuteUpdateAsync(s => s.SetProperty(g => g.ExpiresAt, DateTimeOffset.UtcNow.AddDays(-1)), Ct));
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/me/rewards/gift-cards/redeem", new { code = expiredCode }, c.Token), "LOYALTY.GIFT_CARD_EXPIRED");
        var expired = await H.JsonAsync(HttpMethod.Get, "/api/v1/loyalty/gift-cards?status=expired", HttpStatusCode.OK, bearer: admin);
        Assert.Contains(expired.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetGuid() == expiredId);

        var first = await H.JsonAsync(HttpMethod.Post, $"/api/v1/loyalty/gift-cards/{offId}/deactivate", HttpStatusCode.OK, new { }, admin);
        Assert.Equal("Deactivated", Str(first, "status"));
        await H.JsonAsync(HttpMethod.Post, $"/api/v1/loyalty/gift-cards/{offId}/deactivate", HttpStatusCode.OK, new { }, admin);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/me/rewards/gift-cards/redeem", new { code = offCode }, c.Token), "LOYALTY.GIFT_CARD_INACTIVE");
        Assert.Equal(1, await _l.DbAsync(db => db.GiftCardEntries.CountAsync(e => e.GiftCardId == offId && e.Kind == GiftCardEntryKind.Deactivate, Ct)));
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Get, $"/api/v1/loyalty/gift-cards/{Guid.NewGuid()}", null, admin), "LOYALTY.GIFT_CARD_NOT_FOUND");
        Assert.Equal(0m, (await _l.LedgerAsync(c.Id)).Credit);
    }

    [Fact]
    public async Task Guessing_codes_locks_code_entry_even_for_the_right_code_until_the_lock_ends()
    {
        var admin = await H.AdminAsync();
        await _l.EnableAsync(admin);
        var (_, code) = await NewCardAsync(admin, 200m);
        var c = await _l.CustomerAsync();

        for (var i = 0; i < 5; i++)
        {
            await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/me/rewards/gift-cards/redeem", new { code = $"WRNG-{i:0000}-AAAA-BBBB" }, c.Token), "LOYALTY.GIFT_CARD_INVALID");
        }

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, "/api/v1/me/rewards/gift-cards/redeem", new { code }, c.Token), "LOYALTY.CODE_LOCKED");
        Assert.Equal(0m, (await _l.LedgerAsync(c.Id)).Credit);

        await _l.DbAsync(db => db.LoyaltyAccounts.Where(a => a.CustomerId == c.Id).ExecuteUpdateAsync(s => s.SetProperty(a => a.CodeLockedUntil, DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));
        var ok = await H.JsonAsync(HttpMethod.Post, "/api/v1/me/rewards/gift-cards/redeem", HttpStatusCode.OK, new { code }, c.Token);
        Assert.Equal(200m, Num(ok, "added"));
    }

    [Fact]
    public async Task Sending_a_card_to_a_recipient_writes_one_message_with_the_code_and_not_sending_writes_none()
    {
        var admin = await H.AdminAsync();
        await _l.EnableAsync(admin);
        var mobile = StoreHelper.NewMobile();
        var created = await H.JsonAsync(HttpMethod.Post, "/api/v1/loyalty/gift-cards", HttpStatusCode.Created, new { amount = 250m, recipientMobile = mobile, sendToRecipient = true }, admin);
        var code = Str(created, "code");

        var logs = await _l.DbAsync(db => db.NotificationLogs.AsNoTracking().Where(n => n.Template == "loyalty.gift-card-received").ToListAsync(Ct));
        var sms = Assert.Single(logs, n => n.Recipient == Juvo.Shared.Contacts.PhoneNumber.Normalize(mobile));
        Assert.Contains(code, sms.Body, StringComparison.Ordinal);
        Assert.Contains("250", sms.Body, StringComparison.Ordinal);

        var before = await _l.DbAsync(db => db.NotificationLogs.CountAsync(n => n.Template == "loyalty.gift-card-received", Ct));
        await H.JsonAsync(HttpMethod.Post, "/api/v1/loyalty/gift-cards", HttpStatusCode.Created, new { amount = 250m, recipientMobile = mobile, sendToRecipient = false }, admin);
        Assert.Equal(before, await _l.DbAsync(db => db.NotificationLogs.CountAsync(n => n.Template == "loyalty.gift-card-received", Ct)));
        Assert.Equal(HttpStatusCode.BadRequest, (await H.SendAsync(HttpMethod.Post, "/api/v1/loyalty/gift-cards", new { amount = 250m, sendToRecipient = true }, admin)).StatusCode);
    }
}
