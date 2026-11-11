using Juvo.Domain.Common;

namespace Juvo.Domain.Customers;

/// <summary>
/// A saved delivery address (FR-F05, FR-G12). A Bangladesh address uses the division, district and upazila ids plus free text and a
/// pin; an international one uses the country, state or province and postal code. The pin is kept so repeat orders need no map step.
/// </summary>
public sealed class Address : SoftDeletableEntity
{
    private Address()
    {
    }

    public Address(Guid customerId, string countryCode)
    {
        CustomerId = customerId;
        CountryCode = countryCode.Trim().ToUpperInvariant();
    }

    public Guid CustomerId { get; private set; }

    public string? Label { get; private set; }

    public string RecipientName { get; private set; } = string.Empty;

    /// <summary>International format, for example +8801712345678.</summary>
    public string Phone { get; private set; } = string.Empty;

    public string CountryCode { get; private set; } = "BD";

    public Guid? DivisionId { get; private set; }

    public Guid? DistrictId { get; private set; }

    public Guid? UpazilaId { get; private set; }

    /// <summary>Union, area or locality.</summary>
    public string? Area { get; private set; }

    /// <summary>House, road and block.</summary>
    public string AddressLine { get; private set; } = string.Empty;

    public string? Landmark { get; private set; }

    /// <summary>International: state or province.</summary>
    public string? StateProvince { get; private set; }

    public string? City { get; private set; }

    public string? PostalCode { get; private set; }

    public double? Latitude { get; private set; }

    public double? Longitude { get; private set; }

    public bool IsDefault { get; private set; }

    public void Set(string? label, string recipientName, string phone, string countryCode, Guid? divisionId, Guid? districtId, Guid? upazilaId, string? area,
        string addressLine, string? landmark, string? stateProvince, string? city, string? postalCode, double? latitude, double? longitude)
    {
        Label = Clean(label);
        RecipientName = recipientName.Trim();
        Phone = phone.Trim();
        CountryCode = countryCode.Trim().ToUpperInvariant();
        DivisionId = divisionId;
        DistrictId = districtId;
        UpazilaId = upazilaId;
        Area = Clean(area);
        AddressLine = addressLine.Trim();
        Landmark = Clean(landmark);
        StateProvince = Clean(stateProvince);
        City = Clean(city);
        PostalCode = Clean(postalCode);
        Latitude = latitude;
        Longitude = longitude;
    }

    public void SetDefault(bool isDefault) => IsDefault = isDefault;

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>A product a customer saved with the heart (FR-C05). One row per customer and product.</summary>
public sealed class WishlistItem : Entity
{
    private WishlistItem()
    {
    }

    public WishlistItem(Guid customerId, Guid productId)
    {
        CustomerId = customerId;
        ProductId = productId;
    }

    public Guid CustomerId { get; private set; }

    public Guid ProductId { get; private set; }
}

public enum ConsentType
{
    MarketingSms = 1,
    MarketingEmail = 2,
    PrivacyPolicy = 3,
}

/// <summary>A consent given or withdrawn, with its time and source (FR-E07). Append-only: the latest row per type is the current state.</summary>
public sealed class ConsentLog : Entity, IAppendOnly, INotAudited
{
    private ConsentLog()
    {
    }

    public ConsentLog(Guid customerId, ConsentType type, bool granted, string source, string? ipAddress, DateTimeOffset at)
    {
        CustomerId = customerId;
        Type = type;
        Granted = granted;
        Source = source;
        IpAddress = ipAddress;
        At = at;
    }

    public Guid CustomerId { get; private set; }

    public ConsentType Type { get; private set; }

    public bool Granted { get; private set; }

    public string Source { get; private set; } = string.Empty;

    public string? IpAddress { get; private set; }

    public DateTimeOffset At { get; private set; }
}
