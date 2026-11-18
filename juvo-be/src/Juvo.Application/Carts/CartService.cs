using System.Security.Cryptography;
using System.Text;
using Juvo.Application.Abstractions;
using Juvo.Application.Catalog;
using Juvo.Application.Preorders;
using Juvo.Application.Pricing;
using Juvo.Application.Storefront;
using Juvo.Domain.Carts;
using Juvo.Domain.Catalog;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Carts;

/// <summary>
/// Shopping carts for guests and customers (module C). A guest cart is found by a secret token that is only stored hashed; a customer has
/// one cart. A cart never reserves stock (assumptions Q7): it only checks the quantity against what is available now, and checkout
/// (Step 8) checks again and reserves.
/// </summary>
public sealed class CartService(IApplicationDbContext db, ICurrentUser currentUser, StockAvailability stock, IFileStorage storage, IPricingEngine pricing, TimeProvider clock)
{
    private StorefrontOptions Options => stock.Options;

    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private Guid? CustomerId => currentUser is { IsAuthenticated: true, SubjectType: "customer", UserId: { } id } ? id : null;

    // ---- Finding the cart -------------------------------------------------------------------------------

    private async Task<(Cart? Cart, string? NewToken)> ResolveAsync(string? token, bool create, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        if (CustomerId is { } customerId)
        {
            var mine = await db.Carts.Include(c => c.Items).FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
            if (mine is null && create)
            {
                mine = new Cart(customerId, null, now.AddDays(Options.CartDays));
                db.Carts.Add(mine);
            }

            return (mine, null);
        }

        if (!string.IsNullOrWhiteSpace(token) && token.Length <= 100)
        {
            var hash = Hash(token.Trim());
            var guest = await db.Carts.Include(c => c.Items).FirstOrDefaultAsync(c => c.TokenHash == hash && c.ExpiresAt > now, cancellationToken);
            if (guest is not null)
            {
                return (guest, null);
            }
        }

        if (!create)
        {
            return (null, null);
        }

        var fresh = NewToken();
        var cart = new Cart(null, Hash(fresh), now.AddDays(Options.CartDays));
        db.Carts.Add(cart);
        return (cart, fresh);
    }

    /// <summary>The cart of the signed-in customer or the guest token (tracked, with its lines), or null. Used by checkout.</summary>
    public async Task<Cart?> FindAsync(string? token, CancellationToken cancellationToken) =>
        (await ResolveAsync(token, create: false, cancellationToken)).Cart;

    // ---- Reading ----------------------------------------------------------------------------------------

    public async Task<Result<CartView>> GetAsync(string? token, CancellationToken cancellationToken)
    {
        var (cart, _) = await ResolveAsync(token, create: false, cancellationToken);
        return cart is null ? Empty() : await ViewAsync(cart, null, cancellationToken);
    }

    private static CartView Empty() => new(null, null, [], 0, 0m, 0m, 0m, 0, null, false, false, null);

    /// <summary>Lines with today's prices and stock, the coupon, and the totals. Every price comes from the pricing engine (module Z), so the cart shows what checkout will bill.</summary>
    public async Task<CartView> ViewAsync(Cart cart, string? newToken, CancellationToken cancellationToken) =>
        (await ViewCoreAsync(cart, newToken, null, cancellationToken)).View;

    private async Task<(CartView View, Error? CouponError)> ViewCoreAsync(Cart cart, string? newToken, string? couponCode, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var variantIds = cart.Items.Select(i => i.VariantId).ToList();
        var rows = await (from v in db.ProductVariants.AsNoTracking()
                          join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                          where variantIds.Contains(v.Id)
                          select new { Variant = v, Product = p }).ToListAsync(cancellationToken);
        var productIds = rows.Select(r => r.Product.Id).Distinct().ToList();
        var images = await db.ProductImages.AsNoTracking().Where(i => productIds.Contains(i.ProductId)).OrderBy(i => i.SortOrder).ToListAsync(cancellationToken);
        var available = await stock.AvailableAsync(variantIds, cancellationToken);

        // What can be ordered now per line (a line the shop has no stock of may still be a pre-order).
        var items = new List<(CartItem Item, ProductVariant Variant, Product Product, int Max, int Effective, bool IsPreorder, PreorderInfo Pre)>();
        foreach (var item in cart.Items)
        {
            var row = rows.FirstOrDefault(r => r.Variant.Id == item.VariantId);
            if (row is null)
            {
                continue; // the variant was removed from the shop: the line disappears
            }

            var (variant, product) = (row.Variant, row.Product);
            var sellable = variant.IsActive && product.Status == ProductStatus.Published;
            var pre = PreorderRules.For(product, variant);
            var isPre = sellable && PreorderRules.IsPreorderLine(pre, variant, available.GetValueOrDefault(variant.Id));
            var max = !sellable ? 0 : isPre ? Options.MaxQuantityPerLine : stock.Orderable(variant, available);
            items.Add((item, variant, product, max, Math.Min(item.Quantity, max), isPre, pre));
        }

        Coupon? couponRow = null;
        if (couponCode is not null)
        {
            var normalized = Coupon.Normalize(couponCode);
            couponRow = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);
        }
        else if (cart.CouponId is { } couponId)
        {
            couponRow = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Id == couponId, cancellationToken);
        }

        var request = new PricingRequest([.. items.Select(i => new PricingLineRequest(i.Variant.Id, i.Effective))], CustomerId is { } cid ? new PricingCustomer(cid, null) : null,
            couponRow is null ? null : [couponRow.Code], PricingChannel.Web, now);
        var priced = await pricing.PriceAsync(request, cancellationToken);
        if (priced.IsFailure)
        {
            throw new InvalidOperationException("The cart could not be priced: " + priced.Error!.Message);
        }

        var result = priced.Value;
        var lines = new List<CartLineDto>();
        var weight = 0;
        var itemCount = 0;
        var preorderAdvance = 0m;
        foreach (var (item, variant, product, max, effective, isPre, pre) in items)
        {
            var line = result.Lines.First(l => l.VariantId == variant.Id);
            var problem = max <= 0 ? "OUT_OF_STOCK" : item.Quantity > max ? "REDUCED" : null;
            var image = images.Where(i => i.ProductId == product.Id).OrderBy(i => string.Equals(i.ColorName, variant.ColorName, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(i => i.SortOrder).FirstOrDefault();
            var changed = line.UnitPrice != item.UnitPriceSnapshot;
            var promotion = line.Adjustments.FirstOrDefault(a => a.SourceType == "Promotion");
            lines.Add(new CartLineDto(variant.Id, product.Id, product.Slug, product.NameEn, product.NameBn, variant.Sku, variant.ColorName, variant.Size,
                image is null ? null : ProductMediaService.ToDto(image, storage).CardUrl, line.UnitPrice, changed ? item.UnitPriceSnapshot : null, changed, item.Quantity, max, problem, line.LineTotal,
                line.ListPrice, line.OrderDiscount, line.Adjustments, promotion?.LabelEn, promotion?.LabelBn, isPre, isPre ? pre.ExpectedShipDate : null,
                isPre ? PreorderRules.LabelEn(pre.ExpectedShipDate) : null, isPre ? PreorderRules.LabelBn(pre.ExpectedShipDate) : null));
            itemCount += effective;
            weight += (variant.WeightGramsOverride ?? product.WeightGrams ?? 0) * effective;
            if (isPre && effective > 0)
            {
                preorderAdvance += PreorderRules.Advance(line.NetTotal, pre.AdvancePercent);
            }
        }

        CartCouponDto? couponDto = null;
        if (result.Coupon is { } outcome)
        {
            couponDto = new CartCouponDto(outcome.Code, couponRow?.Description, outcome.Discount, outcome.Problem, outcome.Applied, outcome.NotAppliedReasonEn, outcome.NotAppliedReasonBn);
        }

        var t = result.Totals;
        var view = new CartView(cart.Id, newToken, lines, itemCount, t.Subtotal, t.CouponDiscount, t.GoodsTotal, weight, couponDto, lines.Any(l => l.Problem is not null),
            lines.Any(l => l.PriceChanged), cart.ExpiresAt, t.PromotionDiscount, t.ListSubtotal, result.Explanation, result.FreeDelivery, lines.Any(l => l.IsPreorder), preorderAdvance);
        return (view, result.CouponError);
    }

    /// <summary>The unit price a customer pays for one unit now (promotions included), stored when a line changes so the cart can show "price changed".</summary>
    private async Task<decimal> SnapshotAsync(ProductVariant variant, Product product, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var priced = await pricing.PriceAsync(new PricingRequest([new PricingLineRequest(variant.Id, 1)], CustomerId is { } id ? new PricingCustomer(id, null) : null, null, PricingChannel.Web, now), cancellationToken);
        return priced.IsSuccess ? priced.Value.Lines[0].UnitPrice : variant.PriceOverride ?? product.EffectivePrice(now);
    }

    // ---- Changing ---------------------------------------------------------------------------------------

    public async Task<Result<CartView>> AddAsync(string? token, AddCartItemRequest request, CancellationToken cancellationToken)
    {
        if (request.Quantity is < 1 or > 1000)
        {
            return Invalid("quantity", "Quantity must be at least 1.");
        }

        var now = clock.GetUtcNow();
        var found = await FindSellableAsync(request.VariantId, cancellationToken);
        if (found.IsFailure)
        {
            return found.Error!;
        }

        var (variant, product) = found.Value;
        var (cart, newToken) = await ResolveAsync(token, create: true, cancellationToken);
        var existing = cart!.Find(variant.Id)?.Quantity ?? 0;
        if (existing == 0 && cart.Items.Count >= Options.MaxCartLines)
        {
            return Error.Conflict(ErrorCodes.Cart.TooManyLines, $"A cart can hold at most {Options.MaxCartLines} different items.");
        }

        var limit = await LimitAsync(variant, product, cancellationToken);
        var wanted = existing + request.Quantity;
        if (wanted > limit)
        {
            return StockError(limit, existing);
        }

        cart.Add(variant.Id, request.Quantity, await SnapshotAsync(variant, product, cancellationToken));
        cart.Touch(now.AddDays(Options.CartDays));
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(cart, newToken, cancellationToken);
    }

    /// <summary>Sets a line's quantity; zero removes the line.</summary>
    public async Task<Result<CartView>> SetQuantityAsync(string? token, Guid variantId, SetQuantityRequest request, CancellationToken cancellationToken)
    {
        if (request.Quantity is < 0 or > 1000)
        {
            return Invalid("quantity", "Quantity must be 0 or more.");
        }

        var (cart, _) = await ResolveAsync(token, create: false, cancellationToken);
        var item = cart?.Find(variantId);
        if (cart is null || item is null)
        {
            return Error.NotFound(ErrorCodes.Cart.ItemNotFound, "That item is not in your cart.");
        }

        var now = clock.GetUtcNow();
        if (request.Quantity == 0)
        {
            cart.Remove(item);
        }
        else
        {
            var found = await FindSellableAsync(variantId, cancellationToken);
            if (found.IsFailure)
            {
                return found.Error!;
            }

            var (variant, product) = found.Value;
            var limit = await LimitAsync(variant, product, cancellationToken);
            if (request.Quantity > limit)
            {
                return StockError(limit, 0);
            }

            item.Set(request.Quantity, await SnapshotAsync(variant, product, cancellationToken));
        }

        cart.Touch(now.AddDays(Options.CartDays));
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(cart, null, cancellationToken);
    }

    public Task<Result<CartView>> RemoveAsync(string? token, Guid variantId, CancellationToken cancellationToken) =>
        SetQuantityAsync(token, variantId, new SetQuantityRequest(0), cancellationToken);

    public async Task<Result<CartView>> ClearAsync(string? token, CancellationToken cancellationToken)
    {
        var (cart, _) = await ResolveAsync(token, create: false, cancellationToken);
        if (cart is null)
        {
            return Empty();
        }

        cart.Clear();
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(cart, null, cancellationToken);
    }

    // ---- Coupon -----------------------------------------------------------------------------------------

    public async Task<Result<CartView>> ApplyCouponAsync(string? token, ApplyCouponRequest request, CancellationToken cancellationToken)
    {
        var (cart, _) = await ResolveAsync(token, create: false, cancellationToken);
        if (cart is null || cart.Items.Count == 0)
        {
            return Error.Conflict(ErrorCodes.Cart.Empty, "Add something to the cart before using a coupon.");
        }

        var coupon = await FindCouponAsync(request.Code, cancellationToken);
        if (coupon is null)
        {
            return Error.NotFound(ErrorCodes.Coupon.NotFound, "This coupon code does not exist.");
        }

        // The engine decides whether the coupon is valid for this cart and whether it combines with the promotions (module Z).
        var (view, couponError) = await ViewCoreAsync(cart, null, coupon.Code, cancellationToken);
        if (couponError is not null)
        {
            return couponError;
        }

        cart.SetCoupon(coupon.Id);
        await db.SaveChangesAsync(cancellationToken);
        return view;
    }

    public async Task<Result<CartView>> RemoveCouponAsync(string? token, CancellationToken cancellationToken)
    {
        var (cart, _) = await ResolveAsync(token, create: false, cancellationToken);
        if (cart is null)
        {
            return Empty();
        }

        cart.SetCoupon(null);
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(cart, null, cancellationToken);
    }

    /// <summary>Tells a visitor what a coupon would give on a subtotal, without touching any cart.</summary>
    public async Task<Result<CouponCheckDto>> ValidateCouponAsync(ValidateCouponRequest request, CancellationToken cancellationToken)
    {
        if (request.Subtotal < 0 || request.Subtotal > 100_000_000)
        {
            return Invalid("subtotal", "The subtotal must be 0 or more.");
        }

        var coupon = await FindCouponAsync(request.Code, cancellationToken);
        if (coupon is null)
        {
            return Error.NotFound(ErrorCodes.Coupon.NotFound, "This coupon code does not exist.");
        }

        var problem = coupon.Check(clock.GetUtcNow(), request.Subtotal);
        return problem != CouponProblem.None
            ? CouponCodes.ToError(problem, coupon)
            : new CouponCheckDto(coupon.Code, coupon.Description, coupon.Kind, coupon.Value, coupon.MinOrderValue, coupon.DiscountFor(request.Subtotal));
    }

    // ---- Merge after sign-in ----------------------------------------------------------------------------

    /// <summary>
    /// Moves the lines of a guest cart into the signed-in customer's cart (FR-C03). Quantities are added and clamped to what is available;
    /// the guest cart is deleted. Safe to call again: a token that no longer exists changes nothing.
    /// </summary>
    public async Task<Result<CartView>> MergeAsync(MergeCartRequest request, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } customerId)
        {
            return Error.Unauthorized();
        }

        var now = clock.GetUtcNow();
        var mine = await db.Carts.Include(c => c.Items).FirstOrDefaultAsync(c => c.CustomerId == customerId, cancellationToken);
        if (mine is null)
        {
            mine = new Cart(customerId, null, now.AddDays(Options.CartDays));
            db.Carts.Add(mine);
        }

        var hash = Hash((request.Token ?? string.Empty).Trim());
        var guest = string.IsNullOrWhiteSpace(request.Token) ? null : await db.Carts.Include(c => c.Items).FirstOrDefaultAsync(c => c.TokenHash == hash && c.CustomerId == null, cancellationToken);
        if (guest is not null)
        {
            foreach (var line in guest.Items.ToList())
            {
                var found = await FindSellableAsync(line.VariantId, cancellationToken);
                if (found.IsFailure || (mine.Find(line.VariantId) is null && mine.Items.Count >= Options.MaxCartLines))
                {
                    continue;
                }

                var (variant, product) = found.Value;
                var limit = await LimitAsync(variant, product, cancellationToken);
                var existing = mine.Find(variant.Id)?.Quantity ?? 0;
                var quantity = Math.Min(existing + line.Quantity, limit);
                if (quantity > 0)
                {
                    mine.Add(variant.Id, quantity - existing, await SnapshotAsync(variant, product, cancellationToken));
                }
            }

            if (mine.CouponId is null && guest.CouponId is { } coupon)
            {
                mine.SetCoupon(coupon);
            }

            db.Carts.Remove(guest);
        }

        mine.Touch(now.AddDays(Options.CartDays));
        await db.SaveChangesAsync(cancellationToken);
        return await ViewAsync(mine, null, cancellationToken);
    }

    // ---- Helpers ----------------------------------------------------------------------------------------

    private async Task<Result<(ProductVariant Variant, Product Product)>> FindSellableAsync(Guid variantId, CancellationToken cancellationToken)
    {
        var row = await (from v in db.ProductVariants.AsNoTracking()
                         join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                         join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                         where v.Id == variantId && v.IsActive && p.Status == ProductStatus.Published && c.IsActive
                         select new { Variant = v, Product = p }).FirstOrDefaultAsync(cancellationToken);
        return row is null
            ? Error.NotFound(ErrorCodes.Cart.VariantUnavailable, "This product is not available.")
            : (row.Variant, row.Product);
    }

    private async Task<int> LimitAsync(ProductVariant variant, Product product, CancellationToken cancellationToken)
    {
        var available = await stock.AvailableAsync([variant.Id], cancellationToken);
        var limit = stock.Orderable(variant, available);
        // An item the shop has none of can still be ordered when it allows pre-orders (module AA).
        return limit <= 0 && PreorderRules.IsPreorderLine(PreorderRules.For(product, variant), variant, available.GetValueOrDefault(variant.Id)) ? Options.MaxQuantityPerLine : limit;
    }

    private static Error StockError(int limit, int alreadyInCart) =>
        limit <= 0
            ? Error.Conflict(ErrorCodes.Cart.StockInsufficient, "This item is out of stock.")
            : Error.Conflict(ErrorCodes.Cart.StockInsufficient, alreadyInCart > 0 ? $"Only {limit} can be ordered; you already have {alreadyInCart} in your cart." : $"Only {limit} can be ordered.");

    private async Task<Coupon?> FindCouponAsync(string? code, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 40)
        {
            return null;
        }

        var normalized = Coupon.Normalize(code);
        return await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);
    }
}

/// <summary>Coupon problems as stable error codes and messages.</summary>
internal static class CouponCodes
{
    public static string For(CouponProblem problem) => problem switch
    {
        CouponProblem.Inactive => ErrorCodes.Coupon.Inactive,
        CouponProblem.NotStarted => ErrorCodes.Coupon.NotStarted,
        CouponProblem.Expired => ErrorCodes.Coupon.Expired,
        CouponProblem.UsedUp => ErrorCodes.Coupon.UsedUp,
        _ => ErrorCodes.Coupon.BelowMinimum,
    };

    public static Error ToError(CouponProblem problem, Coupon coupon) => problem switch
    {
        CouponProblem.Inactive => Error.Conflict(ErrorCodes.Coupon.Inactive, "This coupon is not active."),
        CouponProblem.NotStarted => Error.Conflict(ErrorCodes.Coupon.NotStarted, "This coupon is not valid yet."),
        CouponProblem.Expired => Error.Conflict(ErrorCodes.Coupon.Expired, "This coupon has expired."),
        CouponProblem.UsedUp => Error.Conflict(ErrorCodes.Coupon.UsedUp, "This coupon has been used up."),
        _ => Error.Conflict(ErrorCodes.Coupon.BelowMinimum, $"Spend at least {coupon.MinOrderValue:0.##} to use this coupon."),
    };
}
