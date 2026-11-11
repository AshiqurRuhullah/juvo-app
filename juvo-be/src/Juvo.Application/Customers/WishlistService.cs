using Juvo.Application.Abstractions;
using Juvo.Application.Carts;
using Juvo.Application.Catalog;
using Juvo.Application.Storefront;
using Juvo.Domain.Catalog;
using Juvo.Domain.Customers;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Customers;

/// <summary>The customer's wishlist (FR-C05, FR-F08). Guests keep theirs on the device; signed-in customers keep it on the account.</summary>
public sealed class WishlistService(IApplicationDbContext db, ICurrentUser currentUser, StockAvailability stock, CartService carts, IFileStorage storage, TimeProvider clock)
{
    private Guid? CustomerId => currentUser is { IsAuthenticated: true, SubjectType: "customer", UserId: { } id } ? id : null;

    public async Task<Result<IReadOnlyList<WishlistItemDto>>> ListAsync(CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var now = clock.GetUtcNow();
        var rows = await (from w in db.WishlistItems.AsNoTracking()
                          join p in db.Products.AsNoTracking() on w.ProductId equals p.Id
                          where w.CustomerId == id
                          orderby w.CreatedAt descending
                          select new { Item = w, Product = p }).ToListAsync(cancellationToken);
        var productIds = rows.Select(r => r.Product.Id).ToList();
        var images = await db.ProductImages.AsNoTracking().Where(i => productIds.Contains(i.ProductId)).OrderBy(i => i.SortOrder).ToListAsync(cancellationToken);
        var variants = await db.ProductVariants.AsNoTracking().Where(v => productIds.Contains(v.ProductId) && v.IsActive).ToListAsync(cancellationToken);
        var available = await stock.AvailableAsync([.. variants.Select(v => v.Id)], cancellationToken);

        var items = rows.Select(r =>
        {
            var image = images.FirstOrDefault(i => i.ProductId == r.Product.Id);
            var sellable = r.Product.Status == ProductStatus.Published && variants.Any(v => v.ProductId == r.Product.Id && stock.Shown(v, available) > 0);
            return new WishlistItemDto(r.Product.Id, r.Product.Slug, r.Product.NameEn, r.Product.NameBn, r.Product.EffectivePrice(now), r.Product.RegularPrice,
                image is null ? null : ProductMediaService.ToDto(image, storage).CardUrl, sellable, r.Item.CreatedAt);
        }).ToList();
        return Result.Success<IReadOnlyList<WishlistItemDto>>(items);
    }

    /// <summary>Adding a product that is already saved is fine and changes nothing.</summary>
    public async Task<Result> AddAsync(Guid productId, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        if (!await db.Products.AnyAsync(p => p.Id == productId && p.Status == ProductStatus.Published, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Catalog.ProductNotFound, "Product not found.");
        }

        if (await db.WishlistItems.AnyAsync(w => w.CustomerId == id && w.ProductId == productId, cancellationToken))
        {
            return Result.Success();
        }

        if (await db.WishlistItems.CountAsync(w => w.CustomerId == id, cancellationToken) >= stock.Options.MaxWishlistItems)
        {
            return Error.Conflict(ErrorCodes.Customer.WishlistFull, $"Your wishlist can hold {stock.Options.MaxWishlistItems} items.");
        }

        db.WishlistItems.Add(new WishlistItem(id, productId));
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result> RemoveAsync(Guid productId, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var item = await db.WishlistItems.FirstOrDefaultAsync(w => w.CustomerId == id && w.ProductId == productId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound(ErrorCodes.Customer.WishlistItemNotFound, "That product is not on your wishlist.");
        }

        db.WishlistItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    /// <summary>Puts a chosen variant of a wishlist product in the cart, then takes it off the wishlist. The cart's stock checks apply.</summary>
    public async Task<Result<CartView>> MoveToCartAsync(Guid productId, MoveToCartRequest request, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var item = await db.WishlistItems.FirstOrDefaultAsync(w => w.CustomerId == id && w.ProductId == productId, cancellationToken);
        if (item is null)
        {
            return Error.NotFound(ErrorCodes.Customer.WishlistItemNotFound, "That product is not on your wishlist.");
        }

        if (!await db.ProductVariants.AnyAsync(v => v.Id == request.VariantId && v.ProductId == productId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Cart.VariantUnavailable, "That variant does not belong to this product.");
        }

        var added = await carts.AddAsync(null, new AddCartItemRequest(request.VariantId, request.Quantity < 1 ? 1 : request.Quantity), cancellationToken);
        if (added.IsFailure)
        {
            return added;
        }

        db.WishlistItems.Remove(item);
        await db.SaveChangesAsync(cancellationToken);
        return added;
    }
}
