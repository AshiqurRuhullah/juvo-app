using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Payments;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Infrastructure.Outbox;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juvo.Api.IntegrationTests.Loyalty;

/// <summary>The real API with the sandbox payment gateway; the rewards feature stays off until a test switches it on through the staff settings.</summary>
public class LoyaltyFactory : PaymentFactory;

public sealed record RewardCustomer(string Mobile, string Token, Guid Id, string Phone);

/// <summary>Drives the rewards API (customer and staff) and the checkout with a tender over HTTP, and reads the ledgers straight from the database.</summary>
public sealed class LoyaltyHelper(ApiFactory factory)
{
    private readonly OrderHelper _orders = new(new StoreHelper(factory));

    public OrderHelper O => _orders;

    public StoreHelper H => _orders.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<(string Admin, SeededProduct Product)> ProductAsync(decimal price = 1000m, int stock = 50)
    {
        var admin = await H.AdminAsync();
        var category = await H.CategoryAsync(admin);
        var product = await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Reward Tee"), price, [("Red", "M", stock)], weightGrams: 300);
        return (admin, product);
    }

    /// <summary>Switches the feature on with simple numbers: one point per BDT, one BDT per point, at least 100 points, at most half an order.</summary>
    public Task EnableAsync(string admin, object? overrides = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["enabled"] = true, ["earnRatePerBdt"] = 1m, ["earnRounding"] = "Down", ["pointValue"] = 1m, ["minPointsToRedeem"] = 100, ["maxTenderPercent"] = 50, ["pointsExpiryMonths"] = 12,
            ["referralRewardReferrer"] = 100m, ["referralRewardReferee"] = 50m, ["referralMaxRewardsPerReferrer"] = 20,
        };
        if (overrides is not null)
        {
            foreach (var p in overrides.GetType().GetProperties())
            {
                body[p.Name] = p.GetValue(overrides);
            }
        }

        return H.JsonAsync(HttpMethod.Put, "/api/v1/loyalty/settings", HttpStatusCode.OK, body, admin);
    }

    public async Task<RewardCustomer> CustomerAsync(string? mobile = null)
    {
        mobile ??= StoreHelper.NewMobile();
        var token = await H.CustomerTokenAsync(mobile);
        var phone = Juvo.Shared.Contacts.PhoneNumber.Normalize(mobile)!;
        var id = await _orders.WithDbAsync(db => db.Customers.AsNoTracking().Where(c => c.Mobile == phone).Select(c => c.Id).SingleAsync(Ct));
        return new RewardCustomer(mobile, token, id, phone);
    }

    public async Task<JsonElement> AdjustAsync(string admin, Guid customerId, int? points = null, decimal? credit = null, string reason = "Test adjustment", string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/loyalty/customers/{customerId}/adjust") { Content = System.Net.Http.Json.JsonContent.Create(new { points, credit, reason }) };
        if (key is not null)
        {
            request.Headers.Add("Idempotency-Key", key);
        }

        return await TestApi.ExpectAsync(await SendAsync(request, admin), HttpStatusCode.OK);
    }

    public async Task<HttpResponseMessage> TryAdjustAsync(string bearer, Guid customerId, object body) =>
        await H.SendAsync(HttpMethod.Post, $"/api/v1/loyalty/customers/{customerId}/adjust", body, bearer);

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string bearer)
    {
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", bearer);
        return await H.Api.Client.SendAsync(request, Ct);
    }

    public async Task<HttpResponseMessage> TryCheckoutAsync(RewardCustomer c, Guid variantId, int quantity, string method = "COD", object? tender = null, string? key = null, bool? guestMobile = null, string? addressLine = null, string? phone = null)
    {
        var cart = guestMobile == true ? await _orders.CartAsync(variantId, quantity) : await PrepareCartAsync(c, variantId, quantity);
        var body = new
        {
            contact = new { mobile = c.Mobile, fullName = "Nadia Rahman" },
            address = new { recipientName = "Nadia Rahman", phone, countryCode = "BD", districtId = await _orders.DistrictAsync(), addressLine = addressLine ?? "House 12, Road 5, Dhanmondi " + c.Mobile },
            paymentMethod = method,
            acceptTerms = true,
            confirmDuplicate = true,
            idempotencyKey = key,
            tender,
        };
        return await H.SendAsync(HttpMethod.Post, "/api/v1/store/checkout", body, guestMobile == true ? null : c.Token, cart);
    }

    /// <summary>A signed-in customer has one cart of their own: empty it of this variant, then add the wanted quantity. Returns the cart token (not needed while signed in).</summary>
    public async Task<string?> PrepareCartAsync(RewardCustomer c, Guid variantId, int quantity)
    {
        await H.SendAsync(HttpMethod.Delete, $"/api/v1/store/cart/items/{variantId}", null, c.Token);
        var cart = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/cart/items", HttpStatusCode.OK, new { variantId, quantity }, c.Token);
        return cart.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() : null;
    }

    public async Task<JsonElement> PlaceAsync(RewardCustomer c, Guid variantId, int quantity, string method = "COD", object? tender = null, string? key = null, string? addressLine = null, string? phone = null) =>
        await TestApi.ExpectAsync(await TryCheckoutAsync(c, variantId, quantity, method, tender, key, null, addressLine, phone), HttpStatusCode.Created);

    public async Task<JsonElement> QuoteAsync(RewardCustomer c, Guid variantId, int quantity, object? tender, string method = "COD")
    {
        var cart = await PrepareCartAsync(c, variantId, quantity);
        var body = new
        {
            address = new { recipientName = "Nadia Rahman", countryCode = "BD", districtId = await _orders.DistrictAsync(), addressLine = "House 12, Road 5, Dhanmondi" },
            paymentMethod = method,
            tender,
        };
        return await H.JsonAsync(HttpMethod.Post, "/api/v1/store/checkout/quote", HttpStatusCode.OK, body, c.Token, cart);
    }

    /// <summary>Confirms, packs and delivers an order (cash on delivery or already paid).</summary>
    public async Task DeliverAsync(string admin, Guid orderId)
    {
        await _orders.ActAsync(admin, orderId, "confirm");
        await _orders.ActAsync(admin, orderId, "pack");
        foreach (var step in new[] { "HandedToCourier", "InTransit", "OutForDelivery", "Delivered" })
        {
            await _orders.ActAsync(admin, orderId, "transition", new { to = step });
        }
    }

    /// <summary>Packs and delivers an order that was already confirmed.</summary>
    public async Task DeliverAsyncFrom(string admin, Guid orderId)
    {
        await _orders.ActAsync(admin, orderId, "pack");
        foreach (var step in new[] { "HandedToCourier", "InTransit", "OutForDelivery", "Delivered" })
        {
            await _orders.ActAsync(admin, orderId, "transition", new { to = step });
        }
    }

    private OutboxProcessor Processor => factory.Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();

    /// <summary>Runs the outbox until it is empty (the host's own loop is off in tests): delivered orders earn points, returns take them back, messages are written.</summary>
    public async Task DrainAsync()
    {
        while (await Processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    public async Task<PointsExpiryRun> ExpireAsync()
    {
        var worker = factory.Services.GetServices<IHostedService>().OfType<PointsExpiryWorker>().Single();
        var run = await worker.SweepAsync(Ct);
        return new PointsExpiryRun(run.Customers, run.PointsExpired, run.Warned);
    }

    public Task<JsonElement> RewardsAsync(RewardCustomer c) => H.JsonAsync(HttpMethod.Get, "/api/v1/me/rewards", HttpStatusCode.OK, bearer: c.Token);

    /// <summary>The balances as stored and as rebuilt from the ledgers: they must be equal.</summary>
    public async Task<(int Points, decimal Credit, int PointsSum, decimal CreditSum)> LedgerAsync(Guid customerId) =>
        await _orders.WithDbAsync(async db =>
        {
            var account = await db.LoyaltyAccounts.AsNoTracking().Where(a => a.CustomerId == customerId).Select(a => new { a.PointsBalance, a.CreditBalance }).FirstOrDefaultAsync(Ct);
            var ps = await db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == customerId).SumAsync(e => (int?)e.Points, Ct) ?? 0;
            var cs = await db.CreditLedgerEntries.AsNoTracking().Where(e => e.CustomerId == customerId).SumAsync(e => (decimal?)e.Amount, Ct) ?? 0m;
            return (account?.PointsBalance ?? 0, account?.CreditBalance ?? 0m, ps, cs);
        });

    public async Task AssertConsistentAsync(Guid customerId)
    {
        var (p, c, ps, cs) = await LedgerAsync(customerId);
        Assert.Equal(ps, p);
        Assert.Equal(cs, c);
        Assert.True(p >= 0 && c >= 0);
    }

    public Task<T> DbAsync<T>(Func<JuvoDbContext, Task<T>> action) => _orders.WithDbAsync(action);

    public async Task<T> ScopedAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider);
    }
}

public sealed record PointsExpiryRun(int Customers, int PointsExpired, int Warned);
