using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Operations;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Payments;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Carts;
using Juvo.Domain.Customers;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;
using Juvo.Infrastructure.Outbox;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juvo.Api.IntegrationTests.Marketing;

/// <summary>A shop with sandbox payments, couriers and senders for the marketing tests. The background jobs are off: the tests run them by hand.</summary>
public class MarketingFactory : PaymentFactory
{
}

/// <summary>Builds known customers, orders, consent and carts straight through the domain classes so every segment count and result can be checked by hand.</summary>
public sealed class MarketingHelper(MarketingFactory factory)
{
    public const string SegmentsUrl = "/api/v1/marketing/segments";
    public const string CampaignsUrl = "/api/v1/marketing/campaigns";
    public const string AutomationsUrl = "/api/v1/marketing/automations";
    public const string SettingsUrl = "/api/v1/marketing/settings";

    public static readonly string[] AllMarketing =
    [
        "marketing.segment.view", "marketing.segment.edit", "marketing.campaign.view", "marketing.campaign.create", "marketing.campaign.send", "marketing.automation.view", "marketing.automation.edit",
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SeededProduct? _product;

    public StoreHelper H { get; } = new(factory);

    public OpsHelper O => new(new OrderHelper(H));

    public Task<T> DbAsync<T>(Func<JuvoDbContext, Task<T>> action) => O.WithDbAsync(action);

    // ---- people and rights -------------------------------------------------------------------------------

    public async Task<string> StaffAsync(params string[] permissions)
    {
        var admin = await H.AdminAsync();
        return await O.StaffAsync(admin, await O.HoAsync(), permissions.Length == 0 ? AllMarketing : permissions);
    }

    /// <summary>Hides every customer that exists so far (the soft delete filter), so the next test counts only the customers it creates itself.</summary>
    public Task ResetCustomersAsync() => DbAsync(db => db.Customers.ExecuteUpdateAsync(s => s.SetProperty(c => c.IsDeleted, true), Ct));

    public static string NewMobile() => "+88017" + Random.Shared.Next(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public async Task<Guid> CustomerAsync(
        string name, int createdDaysAgo = 100, int? lastLoginDaysAgo = null, bool sms = false, bool email = false, string? emailAddress = null, string? language = null, bool active = true, string? mobile = null)
    {
        var address = email || emailAddress is not null ? emailAddress ?? $"{Guid.NewGuid():N}@example.com" : null;
        var customer = new Customer(mobile ?? NewMobile(), ProfileLevel.Full);
        customer.UpdateProfile(name, address);
        customer.SetPreferredLanguage(language);
        var now = DateTimeOffset.UtcNow;
        var id = await DbAsync(async db =>
        {
            db.Customers.Add(customer);
            await db.SaveChangesAsync(Ct);
            await db.Customers.Where(c => c.Id == customer.Id).ExecuteUpdateAsync(
                s => s.SetProperty(c => c.CreatedAt, now.AddDays(-createdDaysAgo)).SetProperty(c => c.LastLoginAt, lastLoginDaysAgo == null ? (DateTimeOffset?)null : now.AddDays(-lastLoginDaysAgo.Value)).SetProperty(c => c.IsActive, active), Ct);
            return customer.Id;
        });
        if (sms)
        {
            await ConsentAsync(id, ConsentType.MarketingSms, true, now.AddDays(-createdDaysAgo + 1));
        }

        if (email)
        {
            await ConsentAsync(id, ConsentType.MarketingEmail, true, now.AddDays(-createdDaysAgo + 1));
        }

        return id;
    }

    public Task ConsentAsync(Guid customerId, ConsentType type, bool granted, DateTimeOffset at) =>
        DbAsync(async db =>
        {
            db.ConsentLogs.Add(new ConsentLog(customerId, type, granted, "test", null, at));
            return await db.SaveChangesAsync(Ct);
        });

    public Task<(Guid Division, Guid District)[]> DistrictsAsync() =>
        DbAsync(async db => (await db.Districts.AsNoTracking().OrderBy(d => d.NameEn).Take(80).Select(d => new { d.DivisionId, d.Id }).ToListAsync(Ct)).Select(d => (d.DivisionId, d.Id)).ToArray());

    public Task AddressAsync(Guid customerId, Guid division, Guid district, bool isDefault = true) =>
        DbAsync(async db =>
        {
            var address = new Address(customerId, "BD");
            address.Set("Home", "Test Person", "+8801711111111", "BD", division, district, null, null, "House 1, Road 2", null, null, null, null, null, null);
            address.SetDefault(isDefault);
            db.Addresses.Add(address);
            return await db.SaveChangesAsync(Ct);
        });

    // ---- products, orders, carts -------------------------------------------------------------------------

    public async Task<SeededProduct> ProductAsync(int stock = 20)
    {
        if (_product is not null && stock == 20)
        {
            return _product;
        }

        var admin = await H.AdminAsync();
        var category = await H.CategoryAsync(admin);
        var product = await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Mkt Tee"), 800m, [("Red", "M", stock)]);
        if (stock == 20)
        {
            _product = product;
        }

        return product;
    }

    private static int _orderNumber;

    /// <summary>An order of the customer, placed <paramref name="daysAgo"/> days ago, with the given total (no delivery charge) and status.</summary>
    public async Task<Guid> OrderAsync(Guid customerId, double daysAgo, decimal total, OrderStatus status = OrderStatus.Delivered, double? deliveredDaysAgo = null)
    {
        var product = await ProductAsync();
        var variant = product.Variants[0];
        var branch = await O.HoAsync();
        var n = Interlocked.Increment(ref _orderNumber);
        var placedAt = DateTimeOffset.UtcNow.AddDays(-daysAgo);
        return await DbAsync(async db =>
        {
            var customer = await db.Customers.IgnoreQueryFilters().AsNoTracking().FirstAsync(c => c.Id == customerId, Ct);
            var shipping = new ShippingSnapshot("Test Person", "+8801711111111", "BD", null, "Dhaka", null, "Dhaka", null, null, null, "House 1", null, null, null, null, null, null);
            var data = new NewOrder($"JV-MKT-{n:D6}", branch, OrderSource.Web, customerId, customer.Mobile, customer.FullName, customer.Email, "COD", "DefaultBranch", null, "BDT", total, 0m, null, null, 0m, 0m, 0m, 0m, 0,
                shipping, null, null, "fp-m" + n, "hash-m" + n, null, null, false, null, null, null);
            var order = Order.Place(data, [new NewOrderItem(variant.Id, product.Id, product.Name, null, variant.Sku, variant.Colour, variant.Size, null, total, 1, 0, true)], placedAt);
            db.Orders.Add(order);
            await db.SaveChangesAsync(Ct);
            DateTimeOffset? delivered = status == OrderStatus.Delivered ? DateTimeOffset.UtcNow.AddDays(-(deliveredDaysAgo ?? daysAgo)) : null;

            await db.Orders.IgnoreQueryFilters().Where(o => o.Id == order.Id).ExecuteUpdateAsync(s => s.SetProperty(o => o.Status, status).SetProperty(o => o.DeliveredAt, delivered), Ct);
            return order.Id;
        });
    }

    /// <summary>A return request on the order (the order status stays Delivered).</summary>
    public Task ReturnAsync(Guid orderId) =>
        DbAsync(async db =>
        {
            var order = await db.Orders.IgnoreQueryFilters().Include(o => o.Items).FirstAsync(o => o.Id == orderId, Ct);
            var item = order.Items[0];
            var request = Juvo.Domain.Returns.ReturnRequest.Request(
                new Juvo.Domain.Returns.NewReturn($"RET-MKT-{Guid.NewGuid():N}"[..20], order.BranchId, order.Id, order.Number, order.CustomerId, order.CustomerMobile, order.CustomerName, Juvo.Domain.Returns.ReturnKind.Return, "SIZE_FIT", null, Juvo.Domain.Returns.ReturnFault.Customer, null, []),
                [new Juvo.Domain.Returns.NewReturnItem(item.Id, item.VariantId, item.ProductId, item.Name, item.Sku, item.ColorName, item.Size, null, 1, item.UnitPrice, item.UnitPrice, null, null)], null, "test", DateTimeOffset.UtcNow);
            db.ReturnRequests.Add(request);
            return await db.SaveChangesAsync(Ct);
        });

    public Task WishAsync(Guid customerId, Guid productId) =>
        DbAsync(async db =>
        {
            db.WishlistItems.Add(new WishlistItem(customerId, productId));
            return await db.SaveChangesAsync(Ct);
        });

    /// <summary>A cart with one unit of the product's variant for the customer, last touched <paramref name="idleHours"/> hours ago.</summary>
    public async Task<Guid> CartAsync(Guid customerId, double idleHours)
    {
        var product = await ProductAsync();
        return await DbAsync(async db =>
        {
            var cart = new Cart(customerId, null, DateTimeOffset.UtcNow.AddDays(30));
            cart.Add(product.Variants[0].Id, 2, 800m);
            db.Carts.Add(cart);
            await db.SaveChangesAsync(Ct);
            await db.Carts.Where(c => c.Id == cart.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.UpdatedAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddHours(-idleHours)), Ct);
            return cart.Id;
        });
    }

    // ---- the API -------------------------------------------------------------------------------------------

    public Task<JsonElement> GetAsync(string url, string bearer, HttpStatusCode expected = HttpStatusCode.OK) => H.JsonAsync(HttpMethod.Get, url, expected, bearer: bearer);

    public Task<JsonElement> PostAsync(string url, string bearer, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) => H.JsonAsync(HttpMethod.Post, url, expected, body ?? new { }, bearer);

    public Task<JsonElement> PutAsync(string url, string bearer, object body, HttpStatusCode expected = HttpStatusCode.OK) => H.JsonAsync(HttpMethod.Put, url, expected, body, bearer);

    public Task<HttpResponseMessage> TryAsync(HttpMethod method, string url, string? bearer, object? body = null) => H.SendAsync(method, url, body, bearer);

    public static object Rules(string combinator, params object[] rules) => new { combinator, rules };

    public async Task<Guid> SegmentAsync(string staff, object definition, string? name = null) =>
        (await PostAsync(SegmentsUrl, staff, new { name = name ?? StoreHelper.Unique("Seg"), definition }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

    public async Task<JsonElement> CountAsync(string staff, object definition) => await PostAsync($"{SegmentsUrl}/preview-count", staff, new { definition });

    public async Task<Guid> CampaignAsync(string staff, Guid segment, string channel = "Sms", string bodyEn = "Hi {customerName}, sale at {shopName}: {link}", string? bodyBn = null, string? subjectEn = null, string? name = null) =>
        (await PostAsync(CampaignsUrl, staff, new { name = name ?? StoreHelper.Unique("Camp"), segmentId = segment, channel, subjectEn = subjectEn ?? (channel == "Email" ? "Sale" : null), bodyEn, bodyBn }, HttpStatusCode.Created))
        .GetProperty("id").GetGuid();

    public async Task<JsonElement> ResultsAsync(string staff, Guid campaign) => await GetAsync($"{CampaignsUrl}/{campaign}/results", staff);

    // ---- jobs and settings ---------------------------------------------------------------------------------

    public async Task DrainOutboxAsync()
    {
        var processor = factory.Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    public Task<Juvo.Application.Marketing.CampaignSweep> SweepCampaignsAsync() => factory.Services.GetServices<IHostedService>().OfType<CampaignSendWorker>().Single().SweepAsync(Ct);

    public Task<IReadOnlyList<Juvo.Application.Marketing.AutomationPass>> SweepAutomationsAsync() => factory.Services.GetServices<IHostedService>().OfType<AutomationWorker>().Single().SweepAsync(Ct);

    public Task<List<NotificationLog>> LogsAsync(string template, params Guid[] customers) =>
        DbAsync(db => db.NotificationLogs.AsNoTracking().Where(l => l.Template == template && l.CustomerId != null && customers.Contains(l.CustomerId.Value)).OrderBy(l => l.QueuedAt).ToListAsync(Ct));

    /// <summary>Makes the quiet hours cover now (true) or never (false), in Dhaka time.</summary>
    public async Task SetQuietAsync(bool quietNow)
    {
        var local = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(6));
        var start = local.AddHours(-1).ToString("HH:00", System.Globalization.CultureInfo.InvariantCulture);
        var end = local.AddHours(1).ToString("HH:00", System.Globalization.CultureInfo.InvariantCulture);
        var staff = await StaffAsync("marketing.campaign.view", "marketing.campaign.send");
        await PutAsync(SettingsUrl, staff, quietNow ? new { quietHoursEnabled = true, quietHoursStart = start, quietHoursEnd = end } : new { quietHoursEnabled = false });
    }

    /// <summary>The token of the unsubscribe link inside a message body.</summary>
    public static string TokenIn(string body)
    {
        var marker = "token=";
        var from = body.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        var end = from;
        while (end < body.Length && !char.IsWhiteSpace(body[end]))
        {
            end++;
        }

        return body[from..end];
    }

    public Task SetPlatformSettingAsync(string key, string? value) => value is null ? O.ClearSettingAsync(key) : O.SetSettingAsync(key, value);
}
