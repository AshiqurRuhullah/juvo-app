using Juvo.Domain.Carts;

namespace Juvo.Application.Carts;

/// <summary>
/// One line of the cart as the customer sees it. <c>UnitPrice</c> is today's price; when it differs from the price at the time the line
/// was last changed, <c>PriceChanged</c> is true and <c>PreviousUnitPrice</c> holds the old one. <c>Problem</c> is
/// <c>OUT_OF_STOCK</c> or <c>REDUCED</c> (fewer available than in the cart) or null; <c>MaxQuantity</c> is what can be ordered now.
/// </summary>
public sealed record CartLineDto(
    Guid VariantId, Guid ProductId, string Slug, string Name, string? NameBn, string Sku, string? ColorName, string? Size, string? ImageUrl,
    decimal UnitPrice, decimal? PreviousUnitPrice, bool PriceChanged, int Quantity, int MaxQuantity, string? Problem, decimal LineTotal);

/// <summary><c>Problem</c> is a coupon code such as <c>COUPON.EXPIRED</c> when the attached coupon no longer applies (the discount is then zero).</summary>
public sealed record CartCouponDto(string Code, string? Description, decimal Discount, string? Problem);

/// <summary>
/// The whole cart. Totals count each line at the quantity that can be ordered now (never more than is available, nothing for an
/// out-of-stock line). <c>Token</c> is only filled when a guest cart was just created: keep it and send it as <c>X-Cart-Token</c>.
/// </summary>
public sealed record CartView(
    Guid? Id, string? Token, IReadOnlyList<CartLineDto> Lines, int ItemCount, decimal Subtotal, decimal Discount, decimal Total, int WeightGrams,
    CartCouponDto? Coupon, bool HasProblems, bool PriceChanged, DateTimeOffset? ExpiresAt);

public sealed record AddCartItemRequest(Guid VariantId, int Quantity);

public sealed record SetQuantityRequest(int Quantity);

public sealed record ApplyCouponRequest(string Code);

public sealed record MergeCartRequest(string Token);

public sealed record ValidateCouponRequest(string Code, decimal Subtotal);

public sealed record CouponCheckDto(string Code, string? Description, CouponKind Kind, decimal Value, decimal MinOrderValue, decimal Discount);

// ---- Admin coupons -----------------------------------------------------------------------------------------

public sealed record CouponDto(
    Guid Id, string Code, string? Description, CouponKind Kind, decimal Value, decimal MinOrderValue, decimal? MaxDiscount, DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt, int? UsageLimit, int UsedCount, bool IsActive);

public sealed record SaveCouponRequest(
    string Code, string? Description, CouponKind Kind, decimal Value, decimal MinOrderValue, decimal? MaxDiscount, DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt, int? UsageLimit, bool IsActive = true);

public sealed record CouponListQuery(bool? IsActive);
