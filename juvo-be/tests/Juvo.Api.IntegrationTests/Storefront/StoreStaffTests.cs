using System.Net;
using System.Text.Json;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Storefront;

public class StoreStaffTests(StoreFactory factory) : IClassFixture<StoreFactory>
{
    private readonly StoreHelper _h = new(factory);

    private async Task<string> StaffTokenAsync(string admin, params string[] permissions)
    {
        var role = await _h.Api.CreateRoleAsync(admin, StoreHelper.Unique("Role"), permissions);
        var ho = await _h.Api.BranchIdAsync("HO");
        var user = await _h.Api.CreateUserAsync(admin, $"{StoreHelper.Unique("st")}@example.com", "Staff Member", false, (ho, role));
        return (await _h.Api.LoginAsync(user.Email, user.Password)).Access;
    }

    private async Task<(string Token, Guid Id, string Mobile)> NewCustomerAsync()
    {
        var mobile = StoreHelper.NewMobile();
        var token = await _h.CustomerTokenAsync(mobile);
        await _h.JsonAsync(HttpMethod.Put, "/api/v1/me", HttpStatusCode.OK, new { fullName = "Nadia Rahman", email = "nadia@example.com" }, token);
        var profile = await _h.JsonAsync(HttpMethod.Get, "/api/v1/me", HttpStatusCode.OK, bearer: token);
        return (token, profile.GetProperty("id").GetGuid(), profile.GetProperty("mobile").GetString()!);
    }

    private async Task<int> ViewedCountAsync(Guid customerId)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        return await db.AuditLogs.CountAsync(a => a.Entity == "Customer" && a.EntityId == customerId.ToString() && a.Action == AuditAction.Viewed, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Without_view_pii_a_staff_member_sees_masked_contact_details_and_nothing_is_logged()
    {
        var admin = await _h.AdminAsync();
        var customer = await NewCustomerAsync();
        var viewer = await StaffTokenAsync(admin, "customers.customer.view");
        var before = await ViewedCountAsync(customer.Id);

        var detail = await _h.JsonAsync(HttpMethod.Get, $"/api/v1/customers/{customer.Id}", HttpStatusCode.OK, bearer: viewer);

        Assert.True(detail.GetProperty("piiMasked").GetBoolean());
        var shown = detail.GetProperty("customer");
        Assert.NotEqual(customer.Mobile, shown.GetProperty("mobile").GetString());
        Assert.Contains("****", shown.GetProperty("mobile").GetString());
        Assert.Equal("n***@example.com", shown.GetProperty("email").GetString());
        Assert.Equal(before, await ViewedCountAsync(customer.Id));

        var list = await _h.JsonAsync(HttpMethod.Get, "/api/v1/customers", HttpStatusCode.OK, bearer: viewer);
        Assert.DoesNotContain(customer.Mobile, list.ToString());
    }

    [Fact]
    public async Task With_view_pii_the_details_are_shown_and_every_view_is_audit_logged()
    {
        var admin = await _h.AdminAsync();
        var customer = await NewCustomerAsync();
        var before = await ViewedCountAsync(customer.Id);

        var detail = await _h.JsonAsync(HttpMethod.Get, $"/api/v1/customers/{customer.Id}", HttpStatusCode.OK, bearer: admin);

        Assert.False(detail.GetProperty("piiMasked").GetBoolean());
        Assert.Equal(customer.Mobile, detail.GetProperty("customer").GetProperty("mobile").GetString());
        Assert.Equal(before + 1, await ViewedCountAsync(customer.Id));

        await _h.JsonAsync(HttpMethod.Get, $"/api/v1/customers/{customer.Id}", HttpStatusCode.OK, bearer: admin);
        Assert.Equal(before + 2, await ViewedCountAsync(customer.Id));
    }

    [Fact]
    public async Task Searching_customers_by_mobile_needs_view_pii()
    {
        var admin = await _h.AdminAsync();
        var customer = await NewCustomerAsync();
        var viewer = await StaffTokenAsync(admin, "customers.customer.view");

        var withPii = await _h.JsonAsync(HttpMethod.Get, $"/api/v1/customers?search={Uri.EscapeDataString(customer.Mobile)}", HttpStatusCode.OK, bearer: admin);
        var withoutPii = await _h.JsonAsync(HttpMethod.Get, $"/api/v1/customers?search={Uri.EscapeDataString(customer.Mobile)}", HttpStatusCode.OK, bearer: viewer);

        Assert.Equal(1, withPii.GetProperty("total").GetInt32());
        Assert.Equal(0, withoutPii.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_customer_endpoints_need_permissions_and_a_customer_token_is_not_staff()
    {
        var admin = await _h.AdminAsync();
        var customer = await NewCustomerAsync();
        var nobody = await StaffTokenAsync(admin, "delivery.zone.view");

        Assert.Equal(HttpStatusCode.Unauthorized, (await _h.SendAsync(HttpMethod.Get, "/api/v1/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Get, "/api/v1/customers", bearer: customer.Token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Post, $"/api/v1/customers/{customer.Id}/deactivate", bearer: await StaffTokenAsync(admin, "customers.customer.view"))).StatusCode);
    }

    [Fact]
    public async Task A_deactivated_customer_is_signed_out_cannot_sign_in_and_can_be_activated_again()
    {
        var admin = await _h.AdminAsync();
        var customer = await NewCustomerAsync();
        var mobile = customer.Mobile.Replace("+88", string.Empty, StringComparison.Ordinal);

        // Sign in once more to hold a refresh token of a live session.
        var otp = await _h.JsonAsync(HttpMethod.Post, "/api/v1/auth/otp/request", HttpStatusCode.Accepted, new { mobile });
        var session = await _h.JsonAsync(HttpMethod.Post, "/api/v1/auth/otp/verify", HttpStatusCode.OK, new { mobile, code = otp.GetProperty("devCode").GetString() });
        var refresh = session.GetProperty("refreshToken").GetString();

        var item = await _h.JsonAsync(HttpMethod.Post, $"/api/v1/customers/{customer.Id}/deactivate", HttpStatusCode.OK, bearer: admin);
        Assert.False(item.GetProperty("isActive").GetBoolean());

        // The existing sessions end: their refresh tokens no longer work...
        Assert.Equal(HttpStatusCode.Unauthorized, (await _h.SendAsync(HttpMethod.Post, "/api/v1/auth/refresh", new { refreshToken = refresh })).StatusCode);

        // ...and a new sign-in is refused.
        var request = await _h.SendAsync(HttpMethod.Post, "/api/v1/auth/otp/request", new { mobile });
        if (request.StatusCode == HttpStatusCode.Accepted)
        {
            var code = (await Juvo.Api.IntegrationTests.Support.TestApi.ExpectAsync(request, HttpStatusCode.Accepted)).GetProperty("devCode").GetString();
            await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Post, "/api/v1/auth/otp/verify", new { mobile, code }), "AUTH.ACCOUNT_DISABLED");
        }
        else
        {
            await StoreHelper.ExpectCodeAsync(request, "AUTH.ACCOUNT_DISABLED");
        }

        var active = await _h.JsonAsync(HttpMethod.Post, $"/api/v1/customers/{customer.Id}/activate", HttpStatusCode.OK, bearer: admin);
        Assert.True(active.GetProperty("isActive").GetBoolean());
        var again = await _h.CustomerTokenAsync(mobile);
        Assert.Equal(HttpStatusCode.OK, (await _h.SendAsync(HttpMethod.Get, "/api/v1/me", bearer: again)).StatusCode);
    }

    [Fact]
    public async Task An_unknown_customer_is_not_found()
    {
        var admin = await _h.AdminAsync();

        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Get, $"/api/v1/customers/{Guid.NewGuid()}", bearer: admin), "CUSTOMER.NOT_FOUND");
    }

    // ---- coupons (staff) ----

    [Fact]
    public async Task Coupons_can_be_created_changed_and_listed_and_a_code_is_unique()
    {
        var admin = await _h.AdminAsync();
        var code = StoreHelper.Unique("EID").ToUpperInvariant();

        var created = await _h.CouponAsync(admin, code.ToLowerInvariant(), "Percent", 15m, minOrder: 500m, maxDiscount: 200m, usageLimit: 100);
        Assert.Equal(code, created.GetProperty("code").GetString());
        var id = created.GetProperty("id").GetGuid();

        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Post, "/api/v1/promotions/coupons",
            new { code, kind = "Fixed", value = 10m, minOrderValue = 0m }, admin), "COUPON.CODE_TAKEN");

        var changed = await _h.JsonAsync(HttpMethod.Put, $"/api/v1/promotions/coupons/{id}", HttpStatusCode.OK,
            new { code, description = "Eid sale", kind = "Percent", value = 25m, minOrderValue = 500m, maxDiscount = 300m, isActive = true }, admin);
        Assert.Equal(25m, changed.GetProperty("value").GetDecimal());
        Assert.Equal("Eid sale", changed.GetProperty("description").GetString());

        var list = await _h.JsonAsync(HttpMethod.Get, $"/api/v1/promotions/coupons?search={code}", HttpStatusCode.OK, bearer: admin);
        Assert.Contains(list.GetProperty("items").EnumerateArray(), c => c.GetProperty("code").GetString() == code);
    }

    [Fact]
    public async Task A_percentage_over_100_or_a_negative_value_is_rejected()
    {
        var admin = await _h.AdminAsync();

        Assert.Equal(HttpStatusCode.BadRequest, (await _h.SendAsync(HttpMethod.Post, "/api/v1/promotions/coupons",
            new { code = StoreHelper.Unique("BAD").ToUpperInvariant(), kind = "Percent", value = 150m, minOrderValue = 0m }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _h.SendAsync(HttpMethod.Post, "/api/v1/promotions/coupons",
            new { code = StoreHelper.Unique("NEG").ToUpperInvariant(), kind = "Fixed", value = -5m, minOrderValue = 0m }, admin)).StatusCode);
    }

    [Fact]
    public async Task An_unused_coupon_is_deleted_and_the_coupon_endpoints_need_permissions()
    {
        var admin = await _h.AdminAsync();
        var created = await _h.CouponAsync(admin, StoreHelper.Unique("DEL").ToUpperInvariant(), "Fixed", 10m);
        var id = created.GetProperty("id").GetGuid();
        var viewer = await StaffTokenAsync(admin, "promotions.coupon.view");

        Assert.Equal(HttpStatusCode.OK, (await _h.SendAsync(HttpMethod.Get, "/api/v1/promotions/coupons", bearer: viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Delete, $"/api/v1/promotions/coupons/{id}", bearer: viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _h.SendAsync(HttpMethod.Post, "/api/v1/promotions/coupons", new { code = "X", kind = "Fixed", value = 1m }, viewer)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await _h.SendAsync(HttpMethod.Delete, $"/api/v1/promotions/coupons/{id}", bearer: admin)).StatusCode);
        await StoreHelper.ExpectCodeAsync(await _h.SendAsync(HttpMethod.Get, $"/api/v1/promotions/coupons/{id}", bearer: admin), "COUPON.NOT_FOUND");
    }
}
