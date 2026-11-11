using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Carts;
using Juvo.Application.Delivery;
using Juvo.Application.Storefront;
using Juvo.Shared.Pagination;
using Microsoft.AspNetCore.Mvc;

namespace Juvo.Api.Endpoints;

/// <summary>
/// The public shop (modules A, C and G): catalog, search, geography, delivery quote, coupons and the cart. Everything here is anonymous
/// on purpose and rate limited per client address; a signed-in customer's token is also understood (the cart then belongs to them).
/// Read endpoints are cacheable for a short time because they are identical for every visitor.
/// </summary>
internal static class StoreEndpoints
{
    private const string CartTokenHeader = "X-Cart-Token";

    public static WebApplication MapStoreEndpoints(this WebApplication app)
    {
        var store = app.MapApiV1().MapGroup("/store").WithTags("Storefront").AllowAnonymous().RequireRateLimiting(RateLimitSettings.StorefrontPolicy);

        MapCatalog(store);
        MapGeoAndDelivery(store);
        MapCart(store.MapGroup("/cart").WithTags("Cart"));

        // Merge needs a customer token. It lives outside the anonymous group on purpose: AllowAnonymous on a group would override it.
        app.MapApiV1().MapGroup("/store/cart").WithTags("Cart").RequireRateLimiting(RateLimitSettings.StorefrontPolicy)
            .MapPost("/merge", async (MergeCartRequest r, CartService s, CancellationToken ct) => (await s.MergeAsync(r, ct)).ToHttpResult())
            .RequireAuthorization(Policies.Customer).Produces<CartView>()
            .WithSummary("After sign-in: move the guest cart (its token) into the customer's cart. Quantities are clamped to what is available.");
        return app;
    }

    private static RouteHandlerBuilder Cacheable(this RouteHandlerBuilder builder, int seconds) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = $"public, max-age={seconds}";
            return await next(context);
        });

    private static void MapCatalog(RouteGroupBuilder g)
    {
        g.MapGet("/categories", async (StorefrontCatalogService s, CancellationToken ct) => (await s.CategoriesAsync(ct)).ToHttpResult())
            .Cacheable(60).Produces<IReadOnlyList<StoreCategory>>().WithSummary("Active categories as a tree with the number of published products.");

        g.MapGet("/home", async (StorefrontCatalogService s, CancellationToken ct) => (await s.HomeAsync(ct)).ToHttpResult())
            .Cacheable(30).Produces<StoreHome>().WithSummary("Home page sections: new arrivals, best sellers, featured, on sale, categories.");

        g.MapGet("/products", async ([AsParameters] ListParams list, string? category, decimal? minPrice, decimal? maxPrice, string? size, string? colour, string? brand,
                bool? inStock, bool? onSale, string? sort, string? q, StorefrontCatalogService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new StoreProductQuery(category, minPrice, maxPrice, size, colour, brand, inStock, onSale, sort, q ?? list.Search), ct)).ToHttpResult())
            .Cacheable(30).Produces<PagedResult<StoreProductCard>>()
            .WithSummary("Published products with filters (category slug, price range, size, colour, brand, inStock, onSale), sort (newest, price-asc, price-desc, popular) and search q (small spelling mistakes are tolerated).");

        g.MapGet("/products/facets", async (string? category, StorefrontCatalogService s, CancellationToken ct) => (await s.FacetsAsync(category, ct)).ToHttpResult())
            .Cacheable(60).Produces<StoreFacets>().WithSummary("Sizes, colours, brands and price range available in a category, for the filter panel.");

        g.MapGet("/products/{slug}", async (string slug, StorefrontCatalogService s, CancellationToken ct) => (await s.DetailAsync(slug, ct)).ToHttpResult())
            .Cacheable(15).Produces<StoreProductDetail>().WithSummary("One published product by its web address: variants with combined availability, pictures, size chart, related products.");

        g.MapGet("/search/suggest", async (string? q, StorefrontCatalogService s, CancellationToken ct) => (await s.SuggestAsync(q, ct)).ToHttpResult())
            .Produces<StoreSuggestions>().WithSummary("Search suggestions while typing: products and categories.");
    }

    private static void MapGeoAndDelivery(RouteGroupBuilder g)
    {
        g.MapGet("/geo/countries", async (DeliveryService s, CancellationToken ct) => Results.Ok(await s.CountriesAsync(enabledOnly: true, ct)))
            .Cacheable(300).Produces<IReadOnlyList<CountryDto>>().WithSummary("Countries the shop delivers to.");

        g.MapGet("/geo/divisions", async (DeliveryService s, CancellationToken ct) => Results.Ok(await s.DivisionsAsync(ct)))
            .Cacheable(3600).Produces<IReadOnlyList<DivisionDto>>().WithSummary("The divisions of Bangladesh.");

        g.MapGet("/geo/districts", async (Guid? divisionId, DeliveryService s, CancellationToken ct) => Results.Ok(await s.DistrictsAsync(divisionId, ct)))
            .Cacheable(3600).Produces<IReadOnlyList<DistrictDto>>().WithSummary("Districts of Bangladesh, optionally of one division.");

        g.MapGet("/geo/districts/{districtId:guid}/upazilas", async (Guid districtId, DeliveryService s, CancellationToken ct) => Results.Ok(await s.UpazilasAsync(districtId, ct)))
            .Cacheable(3600).Produces<IReadOnlyList<UpazilaDto>>().WithSummary("Upazilas of a district.");

        g.MapPost("/delivery/quote", async (DeliveryQuoteRequest r, DeliveryService s, CancellationToken ct) => (await s.QuoteAsync(r, ct)).ToHttpResult())
            .Produces<DeliveryQuoteDto>().WithSummary("Delivery charge and estimated delivery dates for an address and goods (items, or subtotal and weight). The charge shown is the charge billed.");

        g.MapPost("/coupons/validate", async (ValidateCouponRequest r, CartService s, CancellationToken ct) => (await s.ValidateCouponAsync(r, ct)).ToHttpResult())
            .RequireRateLimiting(RateLimitSettings.CheckoutPolicy).Produces<CouponCheckDto>().WithSummary("What a coupon code would give on a subtotal. Rate limited against code guessing.");
    }

    private static void MapCart(RouteGroupBuilder g)
    {
        g.MapGet("", async ([FromHeader(Name = CartTokenHeader)] string? token, CartService s, CancellationToken ct) => (await s.GetAsync(token, ct)).ToHttpResult())
            .Produces<CartView>().WithSummary("The cart (guest: send X-Cart-Token; signed-in customer: their own cart). An unknown token gives an empty cart.");

        g.MapPost("/items", async ([FromHeader(Name = CartTokenHeader)] string? token, AddCartItemRequest r, CartService s, CancellationToken ct) => (await s.AddAsync(token, r, ct)).ToHttpResult())
            .Produces<CartView>().WithSummary("Add a variant. A guest without a token gets a new cart and the answer holds its token (send it as X-Cart-Token from then on). The quantity is checked against what is available now.");

        g.MapPut("/items/{variantId:guid}", async ([FromHeader(Name = CartTokenHeader)] string? token, Guid variantId, SetQuantityRequest r, CartService s, CancellationToken ct) =>
                (await s.SetQuantityAsync(token, variantId, r, ct)).ToHttpResult())
            .Produces<CartView>().WithSummary("Set a line's quantity (0 removes it).");

        g.MapDelete("/items/{variantId:guid}", async ([FromHeader(Name = CartTokenHeader)] string? token, Guid variantId, CartService s, CancellationToken ct) => (await s.RemoveAsync(token, variantId, ct)).ToHttpResult())
            .Produces<CartView>().WithSummary("Remove a line.");

        g.MapDelete("", async ([FromHeader(Name = CartTokenHeader)] string? token, CartService s, CancellationToken ct) => (await s.ClearAsync(token, ct)).ToHttpResult())
            .Produces<CartView>().WithSummary("Empty the cart.");

        g.MapPost("/coupon", async ([FromHeader(Name = CartTokenHeader)] string? token, ApplyCouponRequest r, CartService s, CancellationToken ct) => (await s.ApplyCouponAsync(token, r, ct)).ToHttpResult())
            .RequireRateLimiting(RateLimitSettings.CheckoutPolicy).Produces<CartView>().WithSummary("Apply a coupon code to the cart.");

        g.MapDelete("/coupon", async ([FromHeader(Name = CartTokenHeader)] string? token, CartService s, CancellationToken ct) => (await s.RemoveCouponAsync(token, ct)).ToHttpResult())
            .Produces<CartView>().WithSummary("Remove the coupon.");

        g.MapPost("/delivery-quote", async ([FromHeader(Name = CartTokenHeader)] string? token, CartDeliveryRequest r, CartService carts, DeliveryService delivery, CancellationToken ct) =>
            {
                var cart = await carts.GetAsync(token, ct);
                if (cart.IsFailure)
                {
                    return ResultHttpExtensions.ToProblem(cart.Error!);
                }

                // The free-delivery threshold is checked on what the customer pays for the goods (after a coupon).
                return (await delivery.QuoteAmountsAsync(r.CountryCode, r.DistrictId, r.UpazilaId, cart.Value.Total, cart.Value.WeightGrams, ct)).ToHttpResult();
            })
            .Produces<DeliveryQuoteDto>().WithSummary("Delivery charge for the cart's goods (after any coupon) to an address.");
    }
}

public sealed record CartDeliveryRequest(string? CountryCode, Guid? DistrictId, Guid? UpazilaId);
