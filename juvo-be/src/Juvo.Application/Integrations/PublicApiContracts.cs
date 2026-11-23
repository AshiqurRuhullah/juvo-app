namespace Juvo.Application.Integrations;

/// <summary>Who is calling the public API: the key's branch limit and whether it may see personal data. Built from the authenticated key.</summary>
public sealed record ApiCaller(Guid KeyId, Guid? BranchId, bool Pii);

public sealed record PublicOrderLine(Guid VariantId, Guid ProductId, string Sku, string Name, string? Colour, string? Size, int Quantity, decimal UnitPrice, decimal LineTotal);

/// <summary>Contact of the buyer. Without <c>pii:read</c> only the id and a masked mobile number are filled.</summary>
public sealed record PublicOrderCustomer(Guid Id, string Mobile, string? Name, string? Email);

/// <summary>Where the order goes. The area names are always shown; the recipient and street are filled only with <c>pii:read</c>.</summary>
public sealed record PublicOrderShipping(string Country, string? Division, string? District, string? Upazila, string? City, string? Recipient, string? Phone, string? AddressLine, string? Landmark, string? PostalCode);

/// <summary>An order as the public API and the order webhooks show it. No cost, notes, fraud data or tracking secrets. Money is in <see cref="Currency"/> (BDT).</summary>
public sealed record PublicOrderDto(
    Guid Id, string Number, string Status, string PaymentStatus, string DeliveryStatus, string PaymentMethod, string Source, string BranchCode, string Currency,
    decimal Subtotal, decimal DiscountTotal, decimal DeliveryCharge, decimal TaxAmount, decimal GrandTotal, decimal AdvancePaid,
    DateTimeOffset PlacedAt, DateTimeOffset? ConfirmedAt, DateTimeOffset? PackedAt, DateTimeOffset? DeliveredAt, DateTimeOffset? CancelledAt, DateTimeOffset UpdatedAt,
    PublicOrderCustomer Customer, PublicOrderShipping ShipTo, IReadOnlyList<PublicOrderLine> Items);

public sealed record PublicImage(string Url, string? AltText);

public sealed record PublicVariant(Guid Id, string Sku, string? Colour, string? Size, decimal Price, bool IsActive);

/// <summary>A published product. No cost price. Prices are tax-inclusive BDT; <see cref="Price"/> is what a customer pays now (the sale price inside its dates).</summary>
public sealed record PublicProductDto(
    Guid Id, string Slug, string NameEn, string? NameBn, string? ShortDescription, string? Description, string? Material, string TargetGroup, Guid CategoryId, Guid? BrandId,
    string? Brand, string Currency, decimal RegularPrice, decimal Price, bool OnSale, bool IsNew, bool IsFeatured, string? Tags, int? WeightGrams,
    DateTimeOffset? PublishedAt, DateTimeOffset UpdatedAt, IReadOnlyList<PublicImage> Images, IReadOnlyList<PublicVariant> Variants);

public sealed record PublicStockBranch(string BranchCode, int OnHand, int Reserved, int Available, int ReorderLevel);

/// <summary>Stock of one variant. The totals add up the branches the key may see.</summary>
public sealed record PublicStockDto(Guid VariantId, string Sku, Guid ProductId, string ProductSlug, bool TracksStock, int OnHand, int Available, IReadOnlyList<PublicStockBranch> Branches);

public sealed record PublicCustomerDto(Guid Id, string Mobile, string? Name, string? Email, string ProfileLevel, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int OrderCount);

public sealed record PublicOrderQuery(string? Status, DateTimeOffset? PlacedFrom, DateTimeOffset? PlacedTo, DateTimeOffset? UpdatedSince);

/// <summary>Body of an outgoing webhook: <c>{ id, type, createdAt, data }</c>. The id is the same on every retry, so a receiver can ignore repeats.</summary>
public sealed record WebhookEnvelope(Guid Id, string Type, DateTimeOffset CreatedAt, object Data);

public sealed record OrderStatusChangedData(Guid OrderId, string Number, string Track, string From, string To, PublicOrderDto Order);

public sealed record StockLowData(Guid VariantId, string Sku, string ProductSlug, string BranchCode, int OnHand, int Available, int ReorderLevel);

public sealed record PingData(string Message);
