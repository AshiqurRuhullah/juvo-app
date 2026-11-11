using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Carts;
using Juvo.Application.Customers;
using Juvo.Application.Delivery;
using Juvo.Domain.Customers;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

/// <summary>Staff side of Step 7: customers, coupons, delivery zones, countries and upazilas. Every endpoint names its permission.</summary>
internal static class StoreAdminEndpoints
{
    public static WebApplication MapStoreAdminEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapCustomers(v1.MapGroup("/customers").WithTags("Customers (staff)"));
        MapCoupons(v1.MapGroup("/promotions/coupons").WithTags("Coupons"));
        MapDelivery(v1.MapGroup("/delivery").WithTags("Delivery (staff)"));
        return app;
    }

    private static void MapCustomers(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, bool? isActive, ProfileLevel? level, CustomersAdminService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new CustomerListQuery(isActive, level), ct)).ToHttpResult())
            .RequirePermission(Permissions.Customers.CustomerView).Produces<PagedResult<CustomerListItemDto>>()
            .WithSummary("List customers. Mobile and email are masked unless you hold customers.customer.view-pii; unmasked views are audit-logged.");

        g.MapGet("/{id:guid}", async (Guid id, CustomersAdminService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Customers.CustomerView).Produces<CustomerDetailDto>()
            .WithSummary("One customer with addresses, consents and sign-in summary (personal data masked without view-pii; unmasked views are audit-logged).");

        g.MapPost("/{id:guid}/deactivate", async (Guid id, CustomersAdminService s, CancellationToken ct) => (await s.SetActiveAsync(id, false, ct)).ToHttpResult())
            .RequirePermission(Permissions.Customers.CustomerBlock).Produces<CustomerListItemDto>().WithSummary("Deactivate: the customer cannot sign in and their sessions end.");

        g.MapPost("/{id:guid}/activate", async (Guid id, CustomersAdminService s, CancellationToken ct) => (await s.SetActiveAsync(id, true, ct)).ToHttpResult())
            .RequirePermission(Permissions.Customers.CustomerBlock).Produces<CustomerListItemDto>().WithSummary("Activate a deactivated customer.");
    }

    private static void MapCoupons(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, bool? isActive, CouponsService s, CancellationToken ct) => (await s.ListAsync(list.ToRequest(), new CouponListQuery(isActive), ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.CouponView).Produces<PagedResult<CouponDto>>().WithSummary("List coupons.");
        g.MapGet("/{id:guid}", async (Guid id, CouponsService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.CouponView).Produces<CouponDto>().WithSummary("One coupon.");
        g.MapPost("", async (SaveCouponRequest r, CouponsService s, CancellationToken ct) => (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/promotions/coupons/{v.Id}", v)))
            .RequirePermission(Permissions.Promotions.CouponCreate).Produces<CouponDto>(StatusCodes.Status201Created).WithSummary("Create a coupon (percentage or fixed, minimum order, dates, usage limit).");
        g.MapPut("/{id:guid}", async (Guid id, SaveCouponRequest r, CouponsService s, CancellationToken ct) => (await s.UpdateAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.CouponEdit).Produces<CouponDto>().WithSummary("Change a coupon (the code stays).");
        g.MapDelete("/{id:guid}", async (Guid id, CouponsService s, CancellationToken ct) => (await s.DeleteAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Promotions.CouponDelete).Produces(StatusCodes.Status204NoContent).WithSummary("Delete a coupon that was never used; otherwise switch it off.");
    }

    private static void MapDelivery(RouteGroupBuilder g)
    {
        g.MapGet("/zones", async (DeliveryService s, CancellationToken ct) => Results.Ok(await s.ZonesAsync(ct)))
            .RequirePermission(Permissions.Delivery.ZoneView).Produces<IReadOnlyList<DeliveryZoneDto>>().WithSummary("Delivery zones with their rates, weight slabs, areas and countries.");
        g.MapGet("/zones/{id:guid}", async (Guid id, DeliveryService s, CancellationToken ct) => (await s.ZoneAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Delivery.ZoneView).Produces<DeliveryZoneDto>().WithSummary("One zone.");
        g.MapPost("/zones", async (CreateZoneRequest r, DeliveryService s, CancellationToken ct) => (await s.CreateZoneAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/delivery/zones/{v.Id}", v)))
            .RequirePermission(Permissions.Delivery.ZoneCreate).Produces<DeliveryZoneDto>(StatusCodes.Status201Created).WithSummary("Add a zone (rates are set afterwards).");
        g.MapPut("/zones/{id:guid}", async (Guid id, UpdateZoneRequest r, DeliveryService s, CancellationToken ct) => (await s.UpdateZoneAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Delivery.ZoneEdit).Produces<DeliveryZoneDto>().WithSummary("Change a zone's name, status, fallback flag and estimated days.");
        g.MapPut("/zones/{id:guid}/rates", async (Guid id, SaveRatesRequest r, DeliveryService s, CancellationToken ct) => (await s.SaveRatesAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Delivery.RateEdit).Produces<DeliveryZoneDto>().WithSummary("Set base fee, weight slabs, per-extra-kg fee, free-delivery threshold, remote surcharge and the maximum parcel weight.");
        g.MapPut("/zones/{id:guid}/areas", async (Guid id, SaveAreasRequest r, DeliveryService s, CancellationToken ct) => (await s.SaveAreasAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Delivery.ZoneEdit).Produces<DeliveryZoneDto>().WithSummary("Set the districts (and upazila overrides) of a domestic zone; a district moves here from any other zone.");
        g.MapDelete("/zones/{id:guid}", async (Guid id, DeliveryService s, CancellationToken ct) => (await s.DeleteZoneAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Delivery.ZoneDelete).Produces(StatusCodes.Status204NoContent).WithSummary("Delete a zone that is neither the fallback nor used by a country.");

        g.MapGet("/countries", async (DeliveryService s, CancellationToken ct) => Results.Ok(await s.CountriesAsync(enabledOnly: false, ct)))
            .RequirePermission(Permissions.Delivery.CountryView).Produces<IReadOnlyList<CountryDto>>().WithSummary("All countries, enabled or not.");
        g.MapPost("/countries", async (SaveCountryRequest r, DeliveryService s, CancellationToken ct) => (await s.SaveCountryAsync(null, r, ct)).ToHttpResult(v => Results.Created($"/api/v1/delivery/countries/{v.Id}", v)))
            .RequirePermission(Permissions.Delivery.CountryEnable).Produces<CountryDto>(StatusCodes.Status201Created).WithSummary("Add a country.");
        g.MapPut("/countries/{id:guid}", async (Guid id, SaveCountryRequest r, DeliveryService s, CancellationToken ct) => (await s.SaveCountryAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Delivery.CountryEnable).Produces<CountryDto>().WithSummary("Enable or disable a country and choose the international zone that prices it.");

        g.MapPost("/upazilas/import", async (ImportUpazilasRequest r, DeliveryService s, CancellationToken ct) => (await s.ImportUpazilasAsync(r.Items, ct)).ToHttpResult())
            .RequirePermission(Permissions.Delivery.ZoneEdit).Produces<ImportUpazilasResult>().WithSummary("Add upazilas the loaded dataset lacks (existing names in a district are skipped).");
    }
}
