using System.Text.RegularExpressions;
using Juvo.Domain.Common;

namespace Juvo.Domain.Branches;

public enum BranchType
{
    HeadOffice = 1,
    Shop = 2,
    Warehouse = 3,
    OnlineFulfilment = 4,
}

/// <summary>
/// A location that runs part of the business: head office, shop, warehouse or online fulfilment centre.
/// A branch with history is deactivated, never erased (soft delete only for mistakes).
/// </summary>
public sealed partial class Branch : SoftDeletableEntity
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9-]{1,19}$")]
    private static partial Regex CodeFormat();

    private Branch()
    {
    }

    public Branch(string code, string nameEn, string? nameBn, BranchType type)
    {
        Code = NormalizeCode(code);
        NameEn = nameEn.Trim();
        NameBn = nameBn?.Trim();
        Type = type;
        OrderPrefix = Code.Replace("-", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>Unique short code such as <c>HO</c> or <c>DHK-01</c>. Used in order and invoice numbers.</summary>
    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public BranchType Type { get; private set; }

    public Guid? ParentBranchId { get; private set; }

    public string? AddressLine { get; private set; }

    public double? Latitude { get; private set; }

    public double? Longitude { get; private set; }

    public string? Phone { get; private set; }

    public string? Email { get; private set; }

    public string? WorkingHours { get; private set; }

    public string TimeZoneId { get; private set; } = "Asia/Dhaka";

    public string DefaultCurrency { get; private set; } = "BDT";

    /// <summary>Prefix in order, invoice and credit-note numbers, for example <c>DHK01</c> in <c>JV-DHK01-2611-000123</c>.</summary>
    public string OrderPrefix { get; private set; } = string.Empty;

    /// <summary>Pickup address the couriers use for this branch.</summary>
    public string? PickupAddress { get; private set; }

    /// <summary>Courier store ids for this branch as JSON, for example <c>{"pathao":"123"}</c>.</summary>
    public string? CourierStoreIds { get; private set; }

    /// <summary>Delivery zone preselected for this branch (set once zones exist, Step 7).</summary>
    public Guid? DefaultDeliveryZoneId { get; private set; }

    public bool IsActive { get; private set; } = true;

    public bool IsDefault { get; private set; }

    public static bool IsValidCode(string? code) => code is not null && CodeFormat().IsMatch(code.Trim().ToUpperInvariant());

    public static string NormalizeCode(string code)
    {
        var normalized = code.Trim().ToUpperInvariant();
        if (!CodeFormat().IsMatch(normalized))
        {
            throw new ArgumentException("Branch code must be 2-20 characters: letters, digits and hyphen.", nameof(code));
        }

        return normalized;
    }

    public void UpdateDetails(string nameEn, string? nameBn, BranchType type, Guid? parentBranchId, string? addressLine,
        double? latitude, double? longitude, string? phone, string? email, string? workingHours, string timeZoneId,
        string defaultCurrency, string? pickupAddress, string? courierStoreIds, Guid? defaultDeliveryZoneId)
    {
        if (parentBranchId == Id)
        {
            throw new ArgumentException("A branch cannot be its own parent.", nameof(parentBranchId));
        }

        NameEn = nameEn.Trim();
        NameBn = nameBn?.Trim();
        Type = type;
        ParentBranchId = parentBranchId;
        AddressLine = addressLine?.Trim();
        Latitude = latitude;
        Longitude = longitude;
        Phone = phone?.Trim();
        Email = email?.Trim();
        WorkingHours = workingHours?.Trim();
        TimeZoneId = timeZoneId.Trim();
        DefaultCurrency = defaultCurrency.Trim().ToUpperInvariant();
        PickupAddress = pickupAddress?.Trim();
        CourierStoreIds = courierStoreIds;
        DefaultDeliveryZoneId = defaultDeliveryZoneId;
    }

    /// <summary>Changes the number prefix. Existing numbers keep their old prefix; sequences are per prefix.</summary>
    public void SetOrderPrefix(string prefix)
    {
        var normalized = prefix.Trim().ToUpperInvariant();
        if (!Regex.IsMatch(normalized, "^[A-Z0-9]{1,10}$"))
        {
            throw new ArgumentException("Order prefix must be 1-10 letters or digits.", nameof(prefix));
        }

        OrderPrefix = normalized;
    }

    public void Activate() => IsActive = true;

    public void Deactivate()
    {
        IsActive = false;
        IsDefault = false;
    }

    public void MarkDefault() => IsDefault = true;

    public void UnmarkDefault() => IsDefault = false;
}
