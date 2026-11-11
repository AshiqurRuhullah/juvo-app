using Juvo.Application.Delivery;
using Juvo.Domain.Orders;

namespace Juvo.Application.Orders;

// ---- Checkout (public and customer) ------------------------------------------------------------------------

/// <summary>A delivery address typed at checkout. Bangladesh: <c>DistrictId</c> (and optionally <c>UpazilaId</c>); abroad: <c>CountryCode</c>, <c>City</c>. <c>Phone</c> defaults to the contact mobile.</summary>
public sealed record CheckoutAddress(
    string RecipientName, string? Phone, string? CountryCode, Guid? DistrictId, Guid? UpazilaId, string? Area, string AddressLine, string? Landmark, string? StateProvince, string? City,
    string? PostalCode, double? Latitude, double? Longitude);

public sealed record CheckoutContact(string Mobile, string? FullName, string? Email);

/// <summary>Everything needed for live totals: where the parcel goes and how it is paid. The goods come from the cart (<c>X-Cart-Token</c> or the customer's cart).</summary>
public sealed record CheckoutQuoteRequest(Guid? AddressId, CheckoutAddress? Address, string? PaymentMethod, string? Mobile);

public sealed record QuoteLineDto(
    Guid VariantId, Guid ProductId, string Name, string? NameBn, string Sku, string? ColorName, string? Size, string? ImageUrl, decimal UnitPrice, int Quantity, decimal LineTotal, string? Problem);

public sealed record PaymentMethodDto(string Code, string Name, bool Available, string? Reason);

/// <summary>
/// Server-side totals. <c>PayNow</c> is what must be paid before the order is processed (the whole total for an online method, the advance for a large COD order,
/// otherwise zero); <c>PayOnDelivery</c> is the rest. Prices include tax. <c>Problems</c> lists codes such as <c>OUT_OF_STOCK</c>, <c>REDUCED</c>, <c>COUPON.EXPIRED</c>.
/// </summary>
public sealed record CheckoutQuoteDto(
    string Currency, bool TaxIncluded, IReadOnlyList<QuoteLineDto> Lines, int ItemCount, decimal Subtotal, string? CouponCode, decimal CouponDiscount, DeliveryQuoteDto? Delivery,
    string? DeliveryError, decimal DeliveryCharge, decimal GrandTotal, string PaymentMethod, decimal AdvanceRequired, decimal PayNow, decimal PayOnDelivery, bool OtpRequired,
    IReadOnlyList<PaymentMethodDto> PaymentMethods, IReadOnlyList<string> Problems, bool CanPlace);

/// <summary>
/// Place the order. <c>ExpectedTotal</c> is the grand total the customer saw: when today's total differs the answer is 409 <c>ORDER.TOTAL_CHANGED</c> (FR-D06).
/// <c>ConfirmDuplicate</c> answers the duplicate warning (FR-D09). <c>IdempotencyKey</c> (or the <c>Idempotency-Key</c> header) makes a repeated call return the same order.
/// </summary>
public sealed record CheckoutRequest(
    CheckoutContact Contact, Guid? AddressId, CheckoutAddress? Address, bool SaveAddress, string PaymentMethod, string? Note, bool AcceptTerms, string? OtpCode, bool ConfirmDuplicate,
    decimal? ExpectedTotal, string? IdempotencyKey, string? DeviceId);

public sealed record CheckoutResultDto(
    Guid OrderId, string Number, string? TrackingToken, string Status, string PaymentStatus, string PaymentMethod, decimal GrandTotal, decimal AdvanceRequired, decimal PayNow,
    decimal PayOnDelivery, DateOnly? EarliestDelivery, DateOnly? LatestDelivery, bool OfferProfile, bool Replayed, DateTimeOffset? PaymentDeadline);

public sealed record TrackRequest(string? Number, string? Mobile, string? Token);

// ---- Shared views -----------------------------------------------------------------------------------------

public sealed record OrderLineDto(
    Guid VariantId, Guid ProductId, string Name, string? NameBn, string Sku, string? ColorName, string? Size, string? ImageUrl, decimal UnitPrice, int Quantity, decimal LineTotal);

public sealed record TimelineDto(OrderTrack Track, string? From, string To, string? Reason, string? ActorName, DateTimeOffset At);

public sealed record NoteDto(Guid Id, NoteKind Kind, string Text, string? AuthorName, DateTimeOffset At);

public sealed record ShippingDto(
    string RecipientName, string Phone, string CountryCode, string? Division, string? District, string? Upazila, string? Area, string AddressLine, string? Landmark, string? StateProvince,
    string? City, string? PostalCode, double? Latitude, double? Longitude);

public sealed record MoneyDto(string Currency, decimal Subtotal, decimal CouponDiscount, string? CouponCode, decimal ManualDiscount, decimal DeliveryCharge, decimal GrandTotal, decimal AdvanceRequired);

// ---- Customer panel and public tracking ---------------------------------------------------------------------

public sealed record MyOrderListItemDto(
    Guid Id, string Number, OrderStatus Status, PaymentStatus PaymentStatus, DeliveryStatus DeliveryStatus, DateTimeOffset PlacedAt, decimal GrandTotal, int ItemCount, string? FirstItemName,
    string? FirstItemImageUrl, bool CanCancel);

public sealed record MyOrderDto(
    Guid Id, string Number, OrderStatus Status, PaymentStatus PaymentStatus, DeliveryStatus DeliveryStatus, string PaymentMethod, DateTimeOffset PlacedAt, MoneyDto Money,
    ShippingDto Shipping, IReadOnlyList<OrderLineDto> Items, IReadOnlyList<TimelineDto> Timeline, IReadOnlyList<NoteDto> Notes, bool CanCancel, DateTimeOffset? PaymentDeadline,
    Couriers.TrackingShipmentDto? Shipment = null);

/// <summary>What anyone with the order number and mobile (or the tracking token) may see: no street address, no contact details.</summary>
public sealed record TrackingDto(
    string Number, OrderStatus Status, PaymentStatus PaymentStatus, DeliveryStatus DeliveryStatus, string PaymentMethod, DateTimeOffset PlacedAt, string? District, decimal GrandTotal,
    IReadOnlyList<OrderLineDto> Items, IReadOnlyList<TimelineDto> Timeline, bool CanCancel, Couriers.TrackingShipmentDto? Shipment = null);

public sealed record CancelOrderRequest(string? Reason);

// ---- Staff ------------------------------------------------------------------------------------------------

public sealed record OrderListQuery(
    OrderStatus? Status, PaymentStatus? PaymentStatus, DeliveryStatus? DeliveryStatus, Guid? BranchId, OrderSource? Source, FraudState? FraudState, DateTimeOffset? From, DateTimeOffset? To,
    bool? Held);

public sealed record OrderListItemDto(
    Guid Id, string Number, Guid BranchId, string BranchCode, OrderSource Source, OrderStatus Status, PaymentStatus PaymentStatus, DeliveryStatus DeliveryStatus, string PaymentMethod,
    string? CustomerName, string CustomerMobile, string? District, int ItemCount, decimal GrandTotal, FraudState FraudState, int RiskScore, bool IsHeld, DateTimeOffset PlacedAt);

public sealed record OrderDetailDto(
    Guid Id, string Number, Guid BranchId, string BranchCode, string BranchName, OrderSource Source, OrderStatus Status, PaymentStatus PaymentStatus, DeliveryStatus DeliveryStatus,
    string PaymentMethod, Guid CustomerId, string CustomerMobile, string? CustomerName, string? CustomerEmail, ShippingDto Shipping, MoneyDto Money, int WeightGrams, string? ZoneCode,
    string RoutingRule, bool RoutingPending, bool StockDeducted, FraudState FraudState, int RiskScore, bool IsHeld, DateTimeOffset PlacedAt, DateTimeOffset? ConfirmedAt, DateTimeOffset? PackedAt,
    DateTimeOffset? DeliveredAt, DateTimeOffset? CancelledAt, string? CancelReason, DateTimeOffset? PaymentHoldExpiresAt, bool OtpVerified, Guid? CreatedByStaffId,
    IReadOnlyList<OrderLineDto> Items, IReadOnlyList<TimelineDto> Timeline, IReadOnlyList<NoteDto> Notes, IReadOnlyList<RiskFlagDto> Flags, IReadOnlyList<FraudLogDto> FraudLog,
    CustomerOrderStatsDto CustomerStats, IReadOnlyList<string> NextOrderStatuses, IReadOnlyList<string> NextPaymentStatuses);

public sealed record ConfirmOrderRequest(bool AdvanceReceived = false);

public sealed record TransitionRequest(OrderStatus To, string? Reason);

public sealed record AddNoteRequest(NoteKind Kind, string Text);

public sealed record ReassignRequest(Guid BranchId, string? Reason);

public sealed record BulkOrderRequest(string Action, IReadOnlyList<Guid> Ids, string? Reason, bool AdvanceReceived = false);

public sealed record BulkItemResult(Guid OrderId, string? Number, bool Ok, string? Code, string? Message);

public sealed record BulkOrderResult(string Action, int Succeeded, int Failed, IReadOnlyList<BulkItemResult> Results, IReadOnlyList<PackingSlipDto> Slips);

public sealed record PackingSlipLineDto(string Sku, string Name, string? ColorName, string? Size, int Quantity, decimal UnitPrice, decimal LineTotal);

/// <summary>The data the UI prints as a packing slip. <c>CollectOnDelivery</c> is the amount the courier must collect (cash on delivery) or zero.</summary>
public sealed record PackingSlipDto(
    string Number, DateTimeOffset PlacedAt, string BranchName, string? BranchPickupAddress, string? BranchPhone, ShippingDto Shipping, string? CustomerNote, string PaymentMethod,
    PaymentStatus PaymentStatus, decimal Subtotal, decimal Discount, decimal DeliveryCharge, decimal GrandTotal, decimal CollectOnDelivery, IReadOnlyList<PackingSlipLineDto> Lines, int TotalQuantity,
    int WeightGrams);

/// <summary>Staff-created order for the social order desk (module S). Lines are given by <c>Sku</c> or <c>VariantId</c>.</summary>
public sealed record ManualOrderLine(Guid? VariantId, string? Sku, int Quantity);

public sealed record ManualOrderRequest(
    string CustomerMobile, string? CustomerName, string? CustomerEmail, OrderSource Source, Guid? BranchId, CheckoutAddress Address, IReadOnlyList<ManualOrderLine> Lines, string PaymentMethod,
    string? CouponCode, decimal? Discount, decimal? DeliveryChargeOverride, string? CustomerNote, string? InternalNote, string? IdempotencyKey, bool ConfirmDuplicate);

public sealed record OrderSourceCountDto(OrderSource Source, int Orders, decimal Total);
// ---- Public cancel, manual quote ----------------------------------------------------------------------------

public sealed record TrackCancelRequest(string? Number, string? Mobile, string? Token, string? Reason);

public sealed record ManualQuoteDto(
    string Currency, decimal Subtotal, decimal CouponDiscount, string? CouponCode, decimal ManualDiscount, decimal DeliveryCharge, decimal StandardDeliveryCharge, decimal GrandTotal, decimal AdvanceRequired,
    int WeightGrams, string? ZoneCode, string PaymentMethod, Guid BranchId, string RoutingRule, IReadOnlyList<ManualQuoteLineDto> Lines, DateOnly? EarliestDelivery, DateOnly? LatestDelivery);

public sealed record ManualQuoteLineDto(Guid VariantId, string Sku, string Name, string? ColorName, string? Size, decimal UnitPrice, int Quantity, decimal LineTotal, int AvailableInBranch);
