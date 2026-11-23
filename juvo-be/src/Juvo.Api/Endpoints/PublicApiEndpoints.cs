using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Integrations;
using Juvo.Shared.Pagination;

namespace Juvo.Api.Endpoints;

/// <summary>
/// The public REST API (FR-AF05), a read-only surface under <c>/api/public/v1</c> for integrations (accounting, ERP, marketplaces, dashboards). It is authenticated by an API key in
/// the <c>X-Api-Key</c> header, never by the staff or customer JWT, and has its own OpenAPI document (<c>/openapi/public-v1.json</c>). Each endpoint needs one scope
/// (orders:read, products:read, stock:read, customers:read); customer details are masked unless the key also holds pii:read.
/// </summary>
internal static class PublicApiEndpoints
{
    public const string DocumentName = "public-v1";

    public static WebApplication MapPublicApiEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/public/v1")
            .WithGroupName(DocumentName)
            .RequireAuthorization(Policies.PublicApi)
            .AddEndpointFilter<PublicApiRateLimitFilter>()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        var orders = api.MapGroup("/orders").WithTags("Public orders");
        orders.MapGet("", async (HttpContext http, string? status, DateTimeOffset? placedFrom, DateTimeOffset? placedTo, DateTimeOffset? updatedSince, int? page, int? pageSize, PublicApiService s, CancellationToken ct) =>
                (await s.OrdersAsync(ApiKeyAuthenticationHandler.Caller(http.User), new PublicOrderQuery(status, placedFrom, placedTo, updatedSince), page, pageSize, ct)).ToHttpResult())
            .RequireApiScope(ApiKeyScopes.OrdersRead).Produces<PagedResult<PublicOrderDto>>().ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Orders, newest first. Filters: status, placedFrom, placedTo, updatedSince (ISO 8601). Paging: page, pageSize (at most 100). A key with a branch limit sees that branch only. Customer details are masked unless the key has pii:read.");

        orders.MapGet("/{number}", async (HttpContext http, string number, PublicApiService s, CancellationToken ct) =>
                (await s.OrderAsync(ApiKeyAuthenticationHandler.Caller(http.User), number, ct)).ToHttpResult())
            .RequireApiScope(ApiKeyScopes.OrdersRead).Produces<PublicOrderDto>().ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("One order by its number, for example JV-DHK01-2611-000123.");

        var products = api.MapGroup("/products").WithTags("Public products");
        products.MapGet("", async (DateTimeOffset? updatedSince, int? page, int? pageSize, PublicApiService s, CancellationToken ct) =>
                (await s.ProductsAsync(updatedSince, page, pageSize, ct)).ToHttpResult())
            .RequireApiScope(ApiKeyScopes.ProductsRead).Produces<PagedResult<PublicProductDto>>()
            .WithSummary("Published products with variants, prices (BDT, tax-inclusive) and pictures. No cost price. Filter: updatedSince. Paging: page, pageSize (at most 100).");

        products.MapGet("/{slug}", async (string slug, PublicApiService s, CancellationToken ct) => (await s.ProductAsync(slug, ct)).ToHttpResult())
            .RequireApiScope(ApiKeyScopes.ProductsRead).Produces<PublicProductDto>().ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("One published product by its slug.");

        api.MapGet("/stock", async (HttpContext http, string? sku, int? page, int? pageSize, PublicApiService s, CancellationToken ct) =>
                (await s.StockAsync(ApiKeyAuthenticationHandler.Caller(http.User), sku, page, pageSize, ct)).ToHttpResult())
            .WithTags("Public stock")
            .RequireApiScope(ApiKeyScopes.StockRead).Produces<PagedResult<PublicStockDto>>()
            .WithSummary("Stock per variant (SKU): on hand and available per branch, and the totals of the branches the key may see. Filter: sku. Paging: page, pageSize (at most 100).");

        var customers = api.MapGroup("/customers").WithTags("Public customers");
        customers.MapGet("", async (HttpContext http, DateTimeOffset? updatedSince, int? page, int? pageSize, PublicApiService s, CancellationToken ct) =>
                (await s.CustomersAsync(ApiKeyAuthenticationHandler.Caller(http.User), updatedSince, page, pageSize, ct)).ToHttpResult())
            .RequireApiScope(ApiKeyScopes.CustomersRead).Produces<PagedResult<PublicCustomerDto>>()
            .WithSummary("Customers, newest first. Mobile number and e-mail are masked and the name is left out unless the key has pii:read.");

        customers.MapGet("/{id:guid}", async (HttpContext http, Guid id, PublicApiService s, CancellationToken ct) =>
                (await s.CustomerAsync(ApiKeyAuthenticationHandler.Caller(http.User), id, ct)).ToHttpResult())
            .RequireApiScope(ApiKeyScopes.CustomersRead).Produces<PublicCustomerDto>().ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("One customer.");

        return app;
    }
}
