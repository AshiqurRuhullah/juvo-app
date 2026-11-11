using Juvo.Domain.Common;

namespace Juvo.Domain.Carts;

public enum CouponKind
{
    Percent = 1,
    Fixed = 2,
}

/// <summary>Why a coupon cannot be used right now (null when it can).</summary>
public enum CouponProblem
{
    None = 0,
    Inactive,
    NotStarted,
    Expired,
    UsedUp,
    BelowMinimum,
}

/// <summary>A coupon code (FR-C07): a percentage or fixed amount, with minimum order, validity dates and a usage limit.</summary>
public sealed class Coupon : Entity
{
    private Coupon()
    {
    }

    public Coupon(string code, CouponKind kind, decimal value)
    {
        Code = Normalize(code);
        Kind = kind;
        Value = value;
    }

    public string Code { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public CouponKind Kind { get; private set; }

    /// <summary>Percent (1-100) or an amount in BDT.</summary>
    public decimal Value { get; private set; }

    public decimal MinOrderValue { get; private set; }

    /// <summary>Largest discount a percentage coupon may give. Null means no cap.</summary>
    public decimal? MaxDiscount { get; private set; }

    public DateTimeOffset? StartsAt { get; private set; }

    public DateTimeOffset? EndsAt { get; private set; }

    public int? UsageLimit { get; private set; }

    /// <summary>Counted when an order is placed with the coupon (Step 8 calls <see cref="RegisterUse"/>).</summary>
    public int UsedCount { get; private set; }

    public bool IsActive { get; private set; } = true;

    public static string Normalize(string code) => code.Trim().ToUpperInvariant();

    public void Update(string? description, CouponKind kind, decimal value, decimal minOrderValue, decimal? maxDiscount, DateTimeOffset? startsAt,
        DateTimeOffset? endsAt, int? usageLimit, bool isActive)
    {
        Description = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        Kind = kind;
        Value = value;
        MinOrderValue = minOrderValue;
        MaxDiscount = maxDiscount;
        StartsAt = startsAt;
        EndsAt = endsAt;
        UsageLimit = usageLimit;
        IsActive = isActive;
    }

    public void RegisterUse() => UsedCount++;

    public CouponProblem Check(DateTimeOffset now, decimal subtotal)
    {
        if (!IsActive)
        {
            return CouponProblem.Inactive;
        }

        if (StartsAt is { } start && now < start)
        {
            return CouponProblem.NotStarted;
        }

        if (EndsAt is { } end && now > end)
        {
            return CouponProblem.Expired;
        }

        if (UsageLimit is { } limit && UsedCount >= limit)
        {
            return CouponProblem.UsedUp;
        }

        return subtotal < MinOrderValue ? CouponProblem.BelowMinimum : CouponProblem.None;
    }

    /// <summary>The discount on <paramref name="subtotal"/>: never more than the subtotal, rounded to two decimals.</summary>
    public decimal DiscountFor(decimal subtotal)
    {
        var raw = Kind == CouponKind.Percent ? subtotal * Value / 100m : Value;
        if (Kind == CouponKind.Percent && MaxDiscount is { } cap)
        {
            raw = Math.Min(raw, cap);
        }

        return Math.Round(Math.Min(Math.Max(raw, 0m), subtotal), 2, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// A shopping cart for a guest (found by a secret token, kept hashed) or a signed-in customer (FR-C03). A cart does not reserve stock:
/// stock is reserved when an order is placed (assumptions, Q7).
/// </summary>
public sealed class Cart : Entity
{
    private readonly List<CartItem> _items = [];

    private Cart()
    {
    }

    public Cart(Guid? customerId, string? tokenHash, DateTimeOffset expiresAt)
    {
        CustomerId = customerId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
    }

    public Guid? CustomerId { get; private set; }

    /// <summary>SHA-256 of the guest token (the token itself is never stored). Null for customer carts.</summary>
    public string? TokenHash { get; private set; }

    public Guid? CouponId { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public IReadOnlyList<CartItem> Items => _items;

    public void Touch(DateTimeOffset expiresAt) => ExpiresAt = expiresAt;

    public void SetCoupon(Guid? couponId) => CouponId = couponId;

    public void AssignTo(Guid customerId)
    {
        CustomerId = customerId;
        TokenHash = null;
    }

    public CartItem? Find(Guid variantId) => _items.FirstOrDefault(i => i.VariantId == variantId);

    public CartItem Add(Guid variantId, int quantity, decimal unitPrice)
    {
        var existing = Find(variantId);
        if (existing is not null)
        {
            existing.Set(existing.Quantity + quantity, unitPrice);
            return existing;
        }

        var item = new CartItem(Id, variantId, quantity, unitPrice);
        _items.Add(item);
        return item;
    }

    public void Remove(CartItem item) => _items.Remove(item);

    public void Clear()
    {
        _items.Clear();
        CouponId = null;
    }
}

public sealed class CartItem : Entity
{
    private CartItem()
    {
    }

    internal CartItem(Guid cartId, Guid variantId, int quantity, decimal unitPrice)
    {
        CartId = cartId;
        VariantId = variantId;
        Quantity = quantity;
        UnitPriceSnapshot = unitPrice;
    }

    public Guid CartId { get; private set; }

    public Guid VariantId { get; private set; }

    public int Quantity { get; private set; }

    /// <summary>The price when the line was last changed; the cart shows a "price changed" marker when today's price differs.</summary>
    public decimal UnitPriceSnapshot { get; private set; }

    public void Set(int quantity, decimal unitPrice)
    {
        Quantity = quantity;
        UnitPriceSnapshot = unitPrice;
    }
}
