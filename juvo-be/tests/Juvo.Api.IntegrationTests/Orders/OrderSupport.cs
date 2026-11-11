using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Orders;

public sealed record CheckoutOptions(
    string Method = "COD", string? OtpCode = null, bool SkipOtp = false, decimal? Expected = null, string? Key = null, string? Device = null, bool ConfirmDuplicate = false, bool AcceptTerms = true,
    string? Note = null, string? AddressLine = null, string? Coupon = null);

/// <summary>Drives the real checkout and the staff order API over HTTP, and reads stock straight from the database.</summary>
public sealed class OrderHelper(StoreHelper h)
{
    private Guid? _district;

    public StoreHelper H => h;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async Task<Guid> DistrictAsync()
    {
        if (_district is { } cached)
        {
            return cached;
        }

        var districts = await h.JsonAsync(HttpMethod.Get, "/api/v1/store/geo/districts", HttpStatusCode.OK);
        _district = districts.EnumerateArray().First(d => d.GetProperty("nameEn").GetString() == "Dhaka").GetProperty("id").GetGuid();
        return _district.Value;
    }

    /// <summary>Adds a variant to a new guest cart and returns the cart token.</summary>
    public async Task<string> CartAsync(Guid variantId, int quantity)
    {
        var cart = await h.JsonAsync(HttpMethod.Post, "/api/v1/store/cart/items", HttpStatusCode.OK, new { variantId, quantity });
        return cart.GetProperty("token").GetString()!;
    }

    public async Task AddToCartAsync(string token, Guid variantId, int quantity) =>
        await h.JsonAsync(HttpMethod.Post, "/api/v1/store/cart/items", HttpStatusCode.OK, new { variantId, quantity }, cartToken: token);

    public async Task<string> OtpAsync(string mobile)
    {
        var json = await h.JsonAsync(HttpMethod.Post, "/api/v1/auth/otp/request", HttpStatusCode.Accepted, new { mobile, purpose = "ConfirmOrder" });
        return json.GetProperty("devCode").GetString()!;
    }

    public async Task<HttpResponseMessage> TryCheckoutAsync(string? cartToken, string mobile, CheckoutOptions? options = null, string? bearer = null)
    {
        var o = options ?? new CheckoutOptions();
        var otp = o.OtpCode ?? (!o.SkipOtp && bearer is null && o.Method == "COD" ? await OtpAsync(mobile) : null);
        var body = new
        {
            contact = new { mobile, fullName = "Nadia Rahman" },
            address = new { recipientName = "Nadia Rahman", countryCode = "BD", districtId = await DistrictAsync(), addressLine = o.AddressLine ?? "House 12, Road 5, Dhanmondi" },
            paymentMethod = o.Method,
            note = o.Note,
            acceptTerms = o.AcceptTerms,
            otpCode = otp,
            confirmDuplicate = o.ConfirmDuplicate,
            expectedTotal = o.Expected,
            idempotencyKey = o.Key,
            deviceId = o.Device,
        };
        return await h.SendAsync(HttpMethod.Post, "/api/v1/store/checkout", body, bearer, cartToken);
    }

    public async Task<JsonElement> PlaceAsync(Guid variantId, int quantity, string? mobile = null, CheckoutOptions? options = null)
    {
        mobile ??= StoreHelper.NewMobile();
        var cart = await CartAsync(variantId, quantity);
        return await TestApi.ExpectAsync(await TryCheckoutAsync(cart, mobile, options), HttpStatusCode.Created);
    }

    public async Task<(int OnHand, int Reserved)> StockAsync(Guid variantId, string branchCode = "HO")
    {
        await using var scope = h.Api.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        var row = await (from s in db.StockLevels.AsNoTracking()
                         join b in db.Branches.AsNoTracking() on s.BranchId equals b.Id
                         where s.VariantId == variantId && b.Code == branchCode
                         select new { s.OnHand, s.Reserved }).FirstOrDefaultAsync(Ct);
        return row is null ? (0, 0) : (row.OnHand, row.Reserved);
    }

    public Task<JsonElement> OrderAsync(string admin, Guid orderId) => h.JsonAsync(HttpMethod.Get, $"/api/v1/orders/{orderId}", HttpStatusCode.OK, bearer: admin);

    public Task<JsonElement> ActAsync(string admin, Guid orderId, string action, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        h.JsonAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/{action}", expected, body ?? new { }, admin);

    public Task<HttpResponseMessage> TryActAsync(string bearer, Guid orderId, string action, object? body = null) =>
        h.SendAsync(HttpMethod.Post, $"/api/v1/orders/{orderId}/{action}", body ?? new { }, bearer);

    /// <summary>A staff user in the given branch whose role holds exactly the given permissions.</summary>
    public async Task<string> StaffTokenAsync(string admin, Guid? branchId, params string[] permissions)
    {
        var role = await h.Api.CreateRoleAsync(admin, StoreHelper.Unique("Role"), permissions);
        var branch = branchId ?? await h.Api.BranchIdAsync("HO");
        var user = await h.Api.CreateUserAsync(admin, $"{StoreHelper.Unique("st")}@example.com", "Staff Member", false, (branch, role));
        return (await h.Api.LoginAsync(user.Email, user.Password)).Access;
    }

    public async Task<T> WithDbAsync<T>(Func<JuvoDbContext, Task<T>> action)
    {
        await using var scope = h.Api.Factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<JuvoDbContext>());
    }
}
