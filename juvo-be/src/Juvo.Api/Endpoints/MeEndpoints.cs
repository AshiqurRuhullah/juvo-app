using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Carts;
using Juvo.Application.Customers;

namespace Juvo.Api.Endpoints;

/// <summary>The signed-in customer's own account (modules E and F). Only a customer token is accepted; staff tokens are refused.</summary>
internal static class MeEndpoints
{
    public static WebApplication MapMeEndpoints(this WebApplication app)
    {
        var me = app.MapApiV1().MapGroup("/me").WithTags("Customer account").RequireAuthorization(Policies.Customer);

        me.MapGet("", async (CustomerAccountService s, CancellationToken ct) => (await s.GetProfileAsync(ct)).ToHttpResult())
            .Produces<CustomerProfileDto>().WithSummary("My profile.");

        me.MapPut("", async (UpdateProfileRequest r, CustomerAccountService s, CancellationToken ct) => (await s.UpdateProfileAsync(r, ct)).ToHttpResult())
            .Produces<CustomerProfileDto>().WithSummary("Change my name and email (the mobile number is my identity and cannot be changed here).");

        me.MapGet("/summary", async (CustomerAccountService s, CancellationToken ct) => (await s.SummaryAsync(ct)).ToHttpResult())
            .Produces<CustomerSummaryDto>().WithSummary("Counts for the account home: addresses, wishlist, cart items, default address (orders are filled in Step 8).");

        me.MapGet("/consent", async (CustomerAccountService s, CancellationToken ct) => (await s.GetConsentAsync(ct)).ToHttpResult())
            .Produces<IReadOnlyList<ConsentDto>>().WithSummary("My consents (marketing SMS, marketing email, privacy policy).");

        me.MapPut("/consent", async (SetConsentRequest r, CustomerAccountService s, CancellationToken ct) => (await s.SetConsentAsync(r, ct)).ToHttpResult())
            .Produces<IReadOnlyList<ConsentDto>>().WithSummary("Give or withdraw one consent. Each change is logged with its time.");

        var addresses = me.MapGroup("/addresses");
        addresses.MapGet("", async (CustomerAccountService s, CancellationToken ct) => (await s.AddressesAsync(ct)).ToHttpResult())
            .Produces<IReadOnlyList<AddressDto>>().WithSummary("My address book, default first.");
        addresses.MapPost("", async (SaveAddressRequest r, CustomerAccountService s, CancellationToken ct) =>
                (await s.AddAddressAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/me/addresses/{v.Id}", v)))
            .Produces<AddressDto>(StatusCodes.Status201Created).WithSummary("Add an address (Bangladesh: district and optional upazila; abroad: country, city). The first one becomes the default.");
        addresses.MapPut("/{id:guid}", async (Guid id, SaveAddressRequest r, CustomerAccountService s, CancellationToken ct) => (await s.UpdateAddressAsync(id, r, ct)).ToHttpResult())
            .Produces<AddressDto>().WithSummary("Change an address.");
        addresses.MapPost("/{id:guid}/default", async (Guid id, CustomerAccountService s, CancellationToken ct) => (await s.SetDefaultAddressAsync(id, ct)).ToHttpResult())
            .Produces<AddressDto>().WithSummary("Make this address the default.");
        addresses.MapDelete("/{id:guid}", async (Guid id, CustomerAccountService s, CancellationToken ct) => (await s.DeleteAddressAsync(id, ct)).ToHttpResult())
            .Produces(StatusCodes.Status204NoContent).WithSummary("Remove an address.");

        var wishlist = me.MapGroup("/wishlist");
        wishlist.MapGet("", async (WishlistService s, CancellationToken ct) => (await s.ListAsync(ct)).ToHttpResult())
            .Produces<IReadOnlyList<WishlistItemDto>>().WithSummary("My wishlist, newest first.");
        wishlist.MapPost("", async (AddWishlistRequest r, WishlistService s, CancellationToken ct) => (await s.AddAsync(r.ProductId, ct)).ToHttpResult())
            .Produces(StatusCodes.Status204NoContent).WithSummary("Save a product (saving it twice changes nothing).");
        wishlist.MapDelete("/{productId:guid}", async (Guid productId, WishlistService s, CancellationToken ct) => (await s.RemoveAsync(productId, ct)).ToHttpResult())
            .Produces(StatusCodes.Status204NoContent).WithSummary("Remove a product.");
        wishlist.MapPost("/{productId:guid}/move-to-cart", async (Guid productId, MoveToCartRequest r, WishlistService s, CancellationToken ct) => (await s.MoveToCartAsync(productId, r, ct)).ToHttpResult())
            .Produces<CartView>().WithSummary("Put a variant of this product in my cart and take the product off the wishlist.");

        return app;
    }
}
