using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Payments;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Orders;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Operations;

/// <summary>A shop whose payments, couriers and operations are all sandboxed, shared by the purchasing, expense, support and report tests.</summary>
public class OpsFactory : PaymentFactory
{
}

/// <summary>Drives the real purchasing, accounting, support and report endpoints and builds the staff and settings they need.</summary>
public sealed class OpsHelper(OrderHelper orders)
{
    public OrderHelper O => orders;

    public StoreHelper H => orders.H;

    public TestApi Api => orders.H.Api;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static string Today => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6)).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public static string DaysAgo(int days) => DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6)).AddDays(-days).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    public Task<string> StaffAsync(string admin, Guid? branch, params string[] permissions) => orders.StaffTokenAsync(admin, branch, permissions);

    public Task<Guid> HoAsync() => Api.BranchIdAsync("HO");

    public async Task<JsonElement> GetAsync(string url, string bearer, HttpStatusCode expected = HttpStatusCode.OK) =>
        await TestApi.ExpectAsync(await Api.GetAsync(url, bearer), expected);

    public async Task<JsonElement> PostAsync(string url, string bearer, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) =>
        await TestApi.ExpectAsync(await Api.PostAsync(url, body ?? new { }, bearer), expected);

    public async Task<JsonElement> PutAsync(string url, string bearer, object body, HttpStatusCode expected = HttpStatusCode.OK) =>
        await TestApi.ExpectAsync(await Api.PutAsync(url, body, bearer), expected);

    public async Task<HttpResponseMessage> TryPostAsync(string url, string bearer, object? body = null) => await Api.PostAsync(url, body ?? new { }, bearer);

    public async Task<HttpResponseMessage> TryGetAsync(string url, string bearer) => await Api.GetAsync(url, bearer);

    public static async Task ExpectCodeAsync(HttpResponseMessage response, string code) => await StoreHelper.ExpectCodeAsync(response, code);

    public async Task<Guid> ExpenseCategoryAsync(string admin, string? name = null) =>
        (await PostAsync("/api/v1/expenses/categories", admin, new { nameEn = name ?? StoreHelper.Unique("Cat") }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

    public async Task<Guid> AccountAsync(string admin, Guid? branch = null, decimal opening = 0m, string type = "Cash", string? name = null) =>
        (await PostAsync("/api/v1/accounts", admin, new { branchId = branch, name = name ?? StoreHelper.Unique("Acct"), type, openingBalance = opening }, HttpStatusCode.Created)).GetProperty("id").GetGuid();

    public async Task<decimal> BalanceAsync(string bearer, Guid accountId)
    {
        var list = await GetAsync("/api/v1/accounts?includeInactive=true", bearer);
        return list.EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == accountId).GetProperty("balance").GetDecimal();
    }

    public async Task<JsonElement> ExpenseAsync(string bearer, Guid category, Guid account, decimal amount, Guid? branch = null, string? date = null, string? reference = null) =>
        await PostAsync("/api/v1/expenses", bearer, new { branchId = branch ?? await HoAsync(), categoryId = category, expenseDate = date ?? Today, amount, method = "Cash", accountId = account, reference }, HttpStatusCode.Created);

    public Task<T> WithDbAsync<T>(Func<JuvoDbContext, Task<T>> action) => orders.WithDbAsync(action);

    public async Task SetSettingAsync(string key, string value) =>
        await WithDbAsync(async db =>
        {
            var row = await db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == key, Ct);
            if (row is null)
            {
                db.PlatformSettings.Add(new PlatformSetting(key, value));
            }
            else
            {
                row.Set(value);
            }

            await db.SaveChangesAsync(Ct);
            return 0;
        });

    public async Task ClearSettingAsync(string key) => await WithDbAsync(async db => await db.PlatformSettings.Where(s => s.Key == key).ExecuteDeleteAsync(Ct));
}
