using Juvo.Api.Configuration;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Ums;

/// <summary>
/// Security inventory: lists every API endpoint with the policy that protects it. A new endpoint that forgets its policy
/// fails these tests, and Step 13 can rely on this list for the authorization review.
/// </summary>
public class EndpointPolicyTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private sealed record Described(string Route, string Methods, bool AllowAnonymous, IReadOnlyList<string> Policies);

    private List<Described> ApiEndpoints()
    {
        var sources = factory.Services.GetServices<EndpointDataSource>();
        return sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.StartsWith("/api/", StringComparison.Ordinal) == true)
            .Select(e => new Described(
                e.RoutePattern.RawText!,
                string.Join(",", e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []),
                e.Metadata.GetMetadata<IAllowAnonymous>() is not null,
                [.. e.Metadata.GetOrderedMetadata<IAuthorizeData>().Select(a => a.Policy ?? "(default)")]))
            .ToList();
    }

    [Fact]
    public void Every_api_endpoint_is_either_public_on_purpose_or_protected_by_a_policy()
    {
        var endpoints = ApiEndpoints();

        Assert.NotEmpty(endpoints);
        var unprotected = endpoints.Where(e => !e.AllowAnonymous && e.Policies.Count == 0).Select(e => $"{e.Methods} {e.Route}").ToList();
        Assert.True(unprotected.Count == 0, "Endpoints without any policy or AllowAnonymous: " + string.Join("; ", unprotected));
    }

    [Fact]
    public void The_public_endpoints_are_exactly_the_expected_ones()
    {
        var expected = new[]
        {
            "/api/v{version:apiVersion}/system/info",
            "/api/v{version:apiVersion}/system/ping",
            "/api/v{version:apiVersion}/auth/login",
            "/api/v{version:apiVersion}/auth/refresh",
            "/api/v{version:apiVersion}/auth/logout",
            "/api/v{version:apiVersion}/auth/forgot-password",
            "/api/v{version:apiVersion}/auth/reset-password",
            "/api/v{version:apiVersion}/auth/otp/request",
            "/api/v{version:apiVersion}/auth/otp/verify",
        };

        // The public shop (Step 7). Cart merge is deliberately not here: it needs a customer token.
        var shop = new[]
        {
            "categories", "home", "products", "products/facets", "products/{slug}", "search/suggest", "geo/countries", "geo/divisions", "geo/districts",
            "geo/districts/{districtId:guid}/upazilas", "delivery/quote", "coupons/validate", "cart/", "cart/items", "cart/items/{variantId:guid}", "cart/coupon", "cart/delivery-quote",
        }.Select(r => "/api/v{version:apiVersion}/store/" + r);

        var actual = ApiEndpoints().Where(e => e.AllowAnonymous).Select(e => e.Route).Distinct().Order().ToList();

        Assert.Equal(expected.Concat(shop).Order().ToList(), actual);
    }

    [Fact]
    public void Every_admin_endpoint_requires_a_named_permission_that_exists_in_the_catalog()
    {
        var ums = ApiEndpoints().Where(e => e.Route.Contains("/ums/", StringComparison.Ordinal) || e.Route.Contains("/catalog/", StringComparison.Ordinal) ||
                                            e.Route.Contains("/customers", StringComparison.Ordinal) || e.Route.Contains("/promotions/", StringComparison.Ordinal) ||
                                            e.Route.Contains("/delivery/", StringComparison.Ordinal) && !e.Route.Contains("/store/", StringComparison.Ordinal)).ToList();

        Assert.NotEmpty(ums);
        foreach (var endpoint in ums)
        {
            var permissionPolicies = endpoint.Policies.Where(p => p.StartsWith(Policies.PermissionAnywherePrefix, StringComparison.Ordinal)
                                                                  || p.StartsWith(Policies.PermissionPrefix, StringComparison.Ordinal)).ToList();
            Assert.True(permissionPolicies.Count == 1, $"{endpoint.Methods} {endpoint.Route} must name exactly one permission, found: {string.Join(",", endpoint.Policies)}");

            var code = permissionPolicies[0][(permissionPolicies[0].IndexOf(':', StringComparison.Ordinal) + 1)..];
            Assert.True(PermissionCatalog.Exists(code), $"{endpoint.Route} requires unknown permission '{code}'");
        }
    }

    [Fact]
    public void Endpoints_that_only_need_a_signed_in_user_are_the_ones_listed_here()
    {
        var onlyAuthenticated = ApiEndpoints()
            .Where(e => !e.AllowAnonymous && e.Policies.All(p => !p.Contains(':', StringComparison.Ordinal)))
            .Select(e => $"{e.Methods} {e.Route}")
            .Order()
            .ToList();

        // Account endpoints of the signed-in person (/auth) and of the signed-in customer (/me, cart merge). Anything else needs a permission.
        Assert.All(onlyAuthenticated, e => Assert.True(
            e.Contains("/auth/", StringComparison.Ordinal) || e.Contains("/me", StringComparison.Ordinal) || e.EndsWith("/store/cart/merge", StringComparison.Ordinal), e));
    }

    [Fact]
    public void Limited_tokens_can_only_reach_the_account_setup_endpoints()
    {
        var limitedOk = ApiEndpoints()
            .Where(e => !e.AllowAnonymous && e.Policies.Contains(Policies.StaffLimited))
            .Select(e => e.Route.Replace("/api/v{version:apiVersion}", string.Empty, StringComparison.Ordinal))
            .Order()
            .ToList();

        Assert.All(limitedOk, r => Assert.StartsWith("/auth/", r, StringComparison.Ordinal));
        Assert.Contains("/auth/change-password", limitedOk);
        Assert.Contains("/auth/2fa/setup", limitedOk);
        Assert.Contains("/auth/2fa/enable", limitedOk);
    }

    [Fact]
    public void Rate_limited_endpoints_include_login_otp_and_password_reset()
    {
        var sources = factory.Services.GetServices<EndpointDataSource>();
        var limited = sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .Where(e => e.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>() is not null)
            .Select(e => e.RoutePattern.RawText!)
            .ToList();

        foreach (var route in new[] { "/auth/login", "/auth/forgot-password", "/auth/reset-password", "/auth/otp/request", "/auth/otp/verify" })
        {
            Assert.Contains(limited, l => l.EndsWith(route, StringComparison.Ordinal));
        }
    }
}
