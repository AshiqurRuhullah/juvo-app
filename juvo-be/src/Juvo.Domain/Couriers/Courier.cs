using Juvo.Domain.Common;

namespace Juvo.Domain.Couriers;

/// <summary>What staff set on a courier (module H). Credentials are never part of it: they live in configuration or environment variables (Step 14).</summary>
public sealed record CourierDetails(
    string Name, string Provider, bool IsEnabled, int Priority, bool CodSupported, bool SupportsInternational, int? MinWeightGrams, int? MaxWeightGrams, IReadOnlyList<string> SupportedZoneCodes,
    decimal? ChargeOverride, decimal BaseRate, decimal PerExtraKgRate, int BaseRateUpToGrams, string? TrackingUrlTemplate, string? Notes, IReadOnlyDictionary<string, string> Settings);

/// <summary>
/// A courier company the shop ships with (module H). <see cref="Provider"/> names the adapter that talks to it (<c>SANDBOX</c> is on by default; the real ones are
/// disabled slots until Step 14). The rates are what the courier charges the SHOP and rank the suggestion; <see cref="ChargeOverride"/> is the one number that changes
/// what the CUSTOMER pays, and only for the first enabled courier (see <c>docs/assumptions.md</c> section S).
/// </summary>
public sealed class Courier : SoftDeletableEntity
{
    private Courier()
    {
    }

    public Courier(string code, CourierDetails details)
    {
        Code = code.Trim().ToUpperInvariant();
        Apply(details);
    }

    /// <summary>Short unique code, for example <c>PATHAO</c> or <c>SANDBOX</c>.</summary>
    public string Code { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    /// <summary>The adapter code (<c>SANDBOX</c>, <c>PATHAO</c>, <c>STEADFAST</c>, <c>REDX</c>, <c>PAPERFLY</c>, <c>ECOURIER</c>, <c>DHL</c>).</summary>
    public string Provider { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    /// <summary>Lower numbers are tried first when the system suggests a courier.</summary>
    public int Priority { get; private set; } = 100;

    public bool CodSupported { get; private set; } = true;

    public bool SupportsInternational { get; private set; }

    public int? MinWeightGrams { get; private set; }

    public int? MaxWeightGrams { get; private set; }

    /// <summary>Comma-separated delivery zone codes this courier serves; empty means every zone it is able to serve.</summary>
    public string SupportedZones { get; private set; } = string.Empty;

    /// <summary>A flat delivery charge for the customer when this is the first enabled courier. Null: the zone rates apply.</summary>
    public decimal? ChargeOverride { get; private set; }

    /// <summary>What the courier charges the shop for a parcel up to <see cref="BaseRateUpToGrams"/>.</summary>
    public decimal BaseRate { get; private set; }

    /// <summary>Added for each started kilogram above <see cref="BaseRateUpToGrams"/>.</summary>
    public decimal PerExtraKgRate { get; private set; }

    public int BaseRateUpToGrams { get; private set; } = 1000;

    /// <summary>Public tracking page of the courier with <c>{consignment}</c> in it.</summary>
    public string? TrackingUrlTemplate { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>JSON object of non-secret settings (store id, area code). Secrets are never stored here.</summary>
    public string SettingsJson { get; private set; } = "{}";

    public IReadOnlyList<string> ZoneCodes => SupportedZones.Length == 0 ? [] : SupportedZones.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public void Apply(CourierDetails d)
    {
        Name = d.Name.Trim();
        Provider = d.Provider.Trim().ToUpperInvariant();
        IsEnabled = d.IsEnabled;
        Priority = d.Priority;
        CodSupported = d.CodSupported;
        SupportsInternational = d.SupportsInternational;
        MinWeightGrams = d.MinWeightGrams;
        MaxWeightGrams = d.MaxWeightGrams;
        SupportedZones = string.Join(',', d.SupportedZoneCodes.Select(z => z.Trim().ToUpperInvariant()).Where(z => z.Length > 0).Distinct());
        ChargeOverride = d.ChargeOverride;
        BaseRate = d.BaseRate;
        PerExtraKgRate = d.PerExtraKgRate;
        BaseRateUpToGrams = d.BaseRateUpToGrams;
        TrackingUrlTemplate = string.IsNullOrWhiteSpace(d.TrackingUrlTemplate) ? null : d.TrackingUrlTemplate.Trim();
        Notes = string.IsNullOrWhiteSpace(d.Notes) ? null : d.Notes.Trim();
        SettingsJson = System.Text.Json.JsonSerializer.Serialize(d.Settings);
    }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public IReadOnlyDictionary<string, string> Settings =>
        System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(SettingsJson) ?? [];

    /// <summary>What this courier would charge the shop for a parcel of this weight.</summary>
    public decimal CostFor(int weightGrams)
    {
        if (weightGrams <= BaseRateUpToGrams)
        {
            return BaseRate;
        }

        return BaseRate + (int)Math.Ceiling((weightGrams - BaseRateUpToGrams) / 1000.0) * PerExtraKgRate;
    }

    public string? TrackingUrlFor(string consignmentId) =>
        TrackingUrlTemplate?.Replace("{consignment}", Uri.EscapeDataString(consignmentId), StringComparison.Ordinal);
}
