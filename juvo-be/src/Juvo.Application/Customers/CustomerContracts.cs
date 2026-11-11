using Juvo.Domain.Customers;

namespace Juvo.Application.Customers;

public sealed record ConsentDto(string Type, bool Granted, DateTimeOffset? At);

public sealed record CustomerProfileDto(
    Guid Id, string Mobile, string? Email, string? FullName, ProfileLevel ProfileLevel, bool MobileVerified, IReadOnlyList<ConsentDto> Consents, DateTimeOffset CreatedAt);

public sealed record UpdateProfileRequest(string? FullName, string? Email);

public sealed record SetConsentRequest(ConsentType Type, bool Granted);

/// <summary>A tiny overview for the account home. Orders are added in Step 8 (the numbers are then filled from the orders module).</summary>
public sealed record CustomerSummaryDto(int AddressCount, int WishlistCount, int CartItemCount, AddressDto? DefaultAddress, int ActiveOrderCount, int OrderCount);

public sealed record AddressDto(
    Guid Id, string? Label, string RecipientName, string Phone, string CountryCode, Guid? DivisionId, Guid? DistrictId, Guid? UpazilaId, string? Area, string AddressLine,
    string? Landmark, string? StateProvince, string? City, string? PostalCode, double? Latitude, double? Longitude, bool IsDefault, bool PinOutsideCountry);

public sealed record SaveAddressRequest(
    string? Label, string RecipientName, string Phone, string? CountryCode, Guid? DistrictId, Guid? UpazilaId, string? Area, string AddressLine, string? Landmark,
    string? StateProvince, string? City, string? PostalCode, double? Latitude, double? Longitude, bool IsDefault = false);

public sealed record WishlistItemDto(
    Guid ProductId, string Slug, string Name, string? NameBn, decimal Price, decimal RegularPrice, string? ImageUrl, bool Available, DateTimeOffset AddedAt);

public sealed record AddWishlistRequest(Guid ProductId);

public sealed record MoveToCartRequest(Guid VariantId, int Quantity = 1);

// ---- Staff view of customers ------------------------------------------------------------------------------

public sealed record CustomerListQuery(bool? IsActive, ProfileLevel? Level);

public sealed record CustomerListItemDto(Guid Id, string Mobile, string? Email, string? FullName, ProfileLevel ProfileLevel, bool MobileVerified, bool IsActive, DateTimeOffset? LastLoginAt, DateTimeOffset CreatedAt);

/// <summary><c>PiiMasked</c> is true when the staff member lacks <c>customers.customer.view-pii</c>: mobile, email and address details are then hidden.</summary>
public sealed record CustomerDetailDto(
    CustomerListItemDto Customer, bool PiiMasked, IReadOnlyList<AddressDto> Addresses, IReadOnlyList<ConsentDto> Consents, int WishlistCount, int SignInCount, DateTimeOffset? LastSignInAt);
