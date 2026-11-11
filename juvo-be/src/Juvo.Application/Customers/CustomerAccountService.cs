using Juvo.Application.Abstractions;
using Juvo.Domain.Customers;
using Juvo.Shared.Contacts;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Customers;

/// <summary>
/// The signed-in customer's own account (modules E and F): profile, consent, address book and the account summary. Every query is
/// filtered by the customer id taken from the token, never from the request, so one customer can never reach another's data.
/// </summary>
public sealed class CustomerAccountService(IApplicationDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    public const int MaxAddresses = 10;

    // Rough box around Bangladesh, only used to warn about a pin that is clearly elsewhere (FR-G05).
    private const double MinLat = 20.5, MaxLat = 26.7, MinLon = 88.0, MaxLon = 92.8;

    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private Guid? CustomerId => currentUser is { IsAuthenticated: true, SubjectType: "customer", UserId: { } id } ? id : null;

    private static Error NotFound() => Error.NotFound(ErrorCodes.Customer.NotFound, "Customer not found.");

    // ---- Profile ----------------------------------------------------------------------------------------

    public async Task<Result<CustomerProfileDto>> GetProfileAsync(CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return customer is null ? NotFound() : await ProfileAsync(customer, cancellationToken);
    }

    public async Task<Result<CustomerProfileDto>> UpdateProfileAsync(UpdateProfileRequest request, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var name = request.FullName?.Trim();
        var email = request.Email?.Trim();
        if (name is { Length: > 120 })
        {
            return Invalid("fullName", "The name can have at most 120 characters.");
        }

        if (!string.IsNullOrEmpty(email) && !(email.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(email, out var parsed) && parsed.Address == email && email.Contains('.', StringComparison.Ordinal)))
        {
            return Invalid("email", "Enter a valid email address.");
        }

        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer is null)
        {
            return NotFound();
        }

        customer.UpdateProfile(string.IsNullOrEmpty(name) ? null : name, string.IsNullOrEmpty(email) ? null : email);
        await db.SaveChangesAsync(cancellationToken);
        return await ProfileAsync(customer, cancellationToken);
    }

    /// <summary>Gives or withdraws a consent. The log is append-only; the newest row of each type is the current state (FR-E07).</summary>
    public async Task<Result<IReadOnlyList<ConsentDto>>> SetConsentAsync(SetConsentRequest request, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        if (!Enum.IsDefined(request.Type))
        {
            return Invalid("type", "Unknown consent type.");
        }

        db.ConsentLogs.Add(new ConsentLog(id, request.Type, request.Granted, "customer-profile", currentUser.IpAddress, clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success(await ConsentsAsync(id, cancellationToken));
    }

    public async Task<Result<IReadOnlyList<ConsentDto>>> GetConsentAsync(CancellationToken cancellationToken) =>
        CustomerId is { } id ? Result.Success(await ConsentsAsync(id, cancellationToken)) : Error.Unauthorized();

    internal async Task<IReadOnlyList<ConsentDto>> ConsentsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var rows = await db.ConsentLogs.AsNoTracking().Where(c => c.CustomerId == customerId).OrderByDescending(c => c.At).ThenByDescending(c => c.Id).ToListAsync(cancellationToken);
        return [.. Enum.GetValues<ConsentType>().Select(type => rows.FirstOrDefault(r => r.Type == type) is { } latest
            ? new ConsentDto(type.ToString(), latest.Granted, latest.At)
            : new ConsentDto(type.ToString(), false, null))];
    }

    private async Task<CustomerProfileDto> ProfileAsync(Customer c, CancellationToken cancellationToken) =>
        new(c.Id, c.Mobile, c.Email, c.FullName, c.ProfileLevel, c.MobileVerified, await ConsentsAsync(c.Id, cancellationToken), c.CreatedAt);

    // ---- Summary ----------------------------------------------------------------------------------------

    public async Task<Result<CustomerSummaryDto>> SummaryAsync(CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var addresses = await db.Addresses.AsNoTracking().Where(a => a.CustomerId == id).OrderByDescending(a => a.IsDefault).ThenBy(a => a.CreatedAt).ToListAsync(cancellationToken);
        var wishlist = await db.WishlistItems.CountAsync(w => w.CustomerId == id, cancellationToken);
        var cartItems = await db.Carts.Where(c => c.CustomerId == id).SelectMany(c => c.Items).SumAsync(i => (int?)i.Quantity, cancellationToken) ?? 0;
        // Orders arrive in Step 8: their counts are filled here from the orders module.
        return new CustomerSummaryDto(addresses.Count, wishlist, cartItems, addresses.FirstOrDefault() is { } first ? ToDto(first) : null, 0, 0);
    }

    // ---- Addresses --------------------------------------------------------------------------------------

    public async Task<Result<IReadOnlyList<AddressDto>>> AddressesAsync(CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var rows = await db.Addresses.AsNoTracking().Where(a => a.CustomerId == id).OrderByDescending(a => a.IsDefault).ThenBy(a => a.CreatedAt).ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<AddressDto>>([.. rows.Select(ToDto)]);
    }

    public async Task<Result<AddressDto>> AddAddressAsync(SaveAddressRequest request, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        if (await db.Addresses.CountAsync(a => a.CustomerId == id, cancellationToken) >= MaxAddresses)
        {
            return Invalid("addressLine", $"You can save at most {MaxAddresses} addresses. Remove one first.");
        }

        var checkedRequest = await ValidateAsync(request, cancellationToken);
        if (checkedRequest.IsFailure)
        {
            return checkedRequest.Error!;
        }

        var v = checkedRequest.Value;
        var address = new Address(id, v.Country);
        Apply(address, request, v);
        var first = !await db.Addresses.AnyAsync(a => a.CustomerId == id, cancellationToken);
        var makeDefault = first || request.IsDefault;
        if (makeDefault)
        {
            await ClearDefaultAsync(id, null, cancellationToken);
        }

        address.SetDefault(makeDefault);
        db.Addresses.Add(address);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(address);
    }

    public async Task<Result<AddressDto>> UpdateAddressAsync(Guid addressId, SaveAddressRequest request, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var address = await db.Addresses.FirstOrDefaultAsync(a => a.Id == addressId && a.CustomerId == id, cancellationToken);
        if (address is null)
        {
            return Error.NotFound(ErrorCodes.Customer.AddressNotFound, "Address not found.");
        }

        var checkedRequest = await ValidateAsync(request, cancellationToken);
        if (checkedRequest.IsFailure)
        {
            return checkedRequest.Error!;
        }

        Apply(address, request, checkedRequest.Value);
        if (request.IsDefault && !address.IsDefault)
        {
            await ClearDefaultAsync(id, addressId, cancellationToken);
            address.SetDefault(true);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(address);
    }

    public async Task<Result<AddressDto>> SetDefaultAddressAsync(Guid addressId, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var address = await db.Addresses.FirstOrDefaultAsync(a => a.Id == addressId && a.CustomerId == id, cancellationToken);
        if (address is null)
        {
            return Error.NotFound(ErrorCodes.Customer.AddressNotFound, "Address not found.");
        }

        await ClearDefaultAsync(id, addressId, cancellationToken);
        address.SetDefault(true);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(address);
    }

    public async Task<Result> DeleteAddressAsync(Guid addressId, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var address = await db.Addresses.FirstOrDefaultAsync(a => a.Id == addressId && a.CustomerId == id, cancellationToken);
        if (address is null)
        {
            return Error.NotFound(ErrorCodes.Customer.AddressNotFound, "Address not found.");
        }

        var wasDefault = address.IsDefault;
        db.Addresses.Remove(address); // soft delete: orders keep their own copy of the address text
        await db.SaveChangesAsync(cancellationToken);
        if (wasDefault && await db.Addresses.Where(a => a.CustomerId == id).OrderBy(a => a.CreatedAt).FirstOrDefaultAsync(cancellationToken) is { } next)
        {
            next.SetDefault(true);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }

    private async Task ClearDefaultAsync(Guid customerId, Guid? except, CancellationToken cancellationToken)
    {
        foreach (var other in await db.Addresses.Where(a => a.CustomerId == customerId && a.IsDefault && a.Id != except).ToListAsync(cancellationToken))
        {
            other.SetDefault(false);
        }
    }

    private sealed record Checked(string Country, string Phone, Guid? DivisionId);

    private async Task<Result<Checked>> ValidateAsync(SaveAddressRequest r, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(r.RecipientName) || r.RecipientName.Trim().Length > 120)
        {
            errors["recipientName"] = ["Enter the name of the person who receives the parcel."];
        }

        var phone = PhoneNumber.Normalize(r.Phone);
        if (phone is null)
        {
            errors["phone"] = ["Enter a valid phone number."];
        }

        if (string.IsNullOrWhiteSpace(r.AddressLine) || r.AddressLine.Trim().Length > 300)
        {
            errors["addressLine"] = ["Enter the house, road and block (up to 300 characters)."];
        }

        if (r.Latitude is { } lat && (lat is < -90 or > 90) || r.Longitude is { } lon && (lon is < -180 or > 180) || (r.Latitude is null) != (r.Longitude is null))
        {
            errors["latitude"] = ["Send both latitude and longitude, within the valid range."];
        }

        var country = string.IsNullOrWhiteSpace(r.CountryCode) ? "BD" : r.CountryCode.Trim().ToUpperInvariant();
        Guid? division = null;
        if (!await db.Countries.AnyAsync(c => c.Code == country, cancellationToken))
        {
            errors["countryCode"] = ["Choose a country from the list."];
        }
        else if (country == "BD")
        {
            if (r.DistrictId is not { } districtId)
            {
                errors["districtId"] = ["Choose the district."];
            }
            else
            {
                var district = await db.Districts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == districtId, cancellationToken);
                if (district is null)
                {
                    errors["districtId"] = ["District not found."];
                }
                else
                {
                    division = district.DivisionId;
                    if (r.UpazilaId is { } upazilaId && !await db.Upazilas.AnyAsync(u => u.Id == upazilaId && u.DistrictId == districtId, cancellationToken))
                    {
                        errors["upazilaId"] = ["This upazila does not belong to the district."];
                    }
                }
            }
        }
        else if (string.IsNullOrWhiteSpace(r.City))
        {
            errors["city"] = ["Enter the city."];
        }

        return errors.Count > 0 ? Error.Validation(errors) : new Checked(country, phone!, division);
    }

    private static void Apply(Address address, SaveAddressRequest r, Checked v) =>
        address.Set(r.Label, r.RecipientName, v.Phone, v.Country, v.DivisionId, v.Country == "BD" ? r.DistrictId : null, v.Country == "BD" ? r.UpazilaId : null, r.Area, r.AddressLine,
            r.Landmark, v.Country == "BD" ? null : r.StateProvince, r.City, r.PostalCode, r.Latitude, r.Longitude);

    private static AddressDto ToDto(Address a) =>
        new(a.Id, a.Label, a.RecipientName, a.Phone, a.CountryCode, a.DivisionId, a.DistrictId, a.UpazilaId, a.Area, a.AddressLine, a.Landmark, a.StateProvince, a.City, a.PostalCode,
            a.Latitude, a.Longitude, a.IsDefault, a.CountryCode == "BD" && a.Latitude is { } la && a.Longitude is { } lo && (la is < MinLat or > MaxLat || lo is < MinLon or > MaxLon));
}
