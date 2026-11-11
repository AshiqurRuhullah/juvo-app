using Juvo.Domain.Common;

namespace Juvo.Domain.Delivery;

/// <summary>A country the shop may ship to. Bangladesh is always enabled; others are enabled by an administrator (FR-G01).</summary>
public sealed class Country : Entity
{
    private Country()
    {
    }

    public Country(string code, string nameEn, string? nameBn, string phoneCode, bool isEnabled, Guid? zoneId)
    {
        Code = code.Trim().ToUpperInvariant();
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        PhoneCode = phoneCode.Trim();
        IsEnabled = isEnabled;
        ZoneId = zoneId;
    }

    /// <summary>ISO 3166-1 alpha-2, for example <c>BD</c>.</summary>
    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    /// <summary>International dialling code without the plus sign, for example <c>880</c>.</summary>
    public string PhoneCode { get; private set; } = string.Empty;

    public bool IsEnabled { get; private set; }

    /// <summary>The international zone that prices this country (null for Bangladesh, which is priced by area).</summary>
    public Guid? ZoneId { get; private set; }

    public void SetEnabled(bool enabled) => IsEnabled = enabled;

    public void Update(string nameEn, string? nameBn, string phoneCode, Guid? zoneId)
    {
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        PhoneCode = phoneCode.Trim();
        ZoneId = zoneId;
    }
}

public sealed class Division : Entity
{
    private Division()
    {
    }

    public Division(int sourceId, string nameEn, string? nameBn)
    {
        SourceId = sourceId;
        NameEn = nameEn;
        NameBn = nameBn;
    }

    /// <summary>Id in the open dataset the rows were loaded from; keeps the seed idempotent.</summary>
    public int SourceId { get; private set; }

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }
}

public sealed class District : Entity
{
    private District()
    {
    }

    public District(int sourceId, Guid divisionId, string nameEn, string? nameBn, double? latitude, double? longitude)
    {
        SourceId = sourceId;
        DivisionId = divisionId;
        NameEn = nameEn;
        NameBn = nameBn;
        Latitude = latitude;
        Longitude = longitude;
    }

    public int SourceId { get; private set; }

    public Guid DivisionId { get; private set; }

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public double? Latitude { get; private set; }

    public double? Longitude { get; private set; }
}

public sealed class Upazila : Entity
{
    private Upazila()
    {
    }

    public Upazila(int? sourceId, Guid districtId, string nameEn, string? nameBn)
    {
        SourceId = sourceId;
        DistrictId = districtId;
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
    }

    /// <summary>Null for upazilas an administrator added by hand.</summary>
    public int? SourceId { get; private set; }

    public Guid DistrictId { get; private set; }

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }
}

public enum ZoneKind
{
    Domestic = 1,
    International = 2,
}

/// <summary>
/// A delivery zone with its charge rules (FR-G07, FR-G08). Charge = base fee + weight fee + remote surcharge; reaching the free-delivery
/// threshold waives the base and weight fees (never the remote surcharge). All values are editable by an administrator.
/// </summary>
public sealed class DeliveryZone : Entity
{
    private readonly List<DeliveryWeightSlab> _slabs = [];
    private readonly List<DeliveryZoneArea> _areas = [];

    private DeliveryZone()
    {
    }

    public DeliveryZone(string code, string nameEn, string? nameBn, ZoneKind kind)
    {
        Code = code.Trim().ToUpperInvariant();
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        Kind = kind;
    }

    public string Code { get; private set; } = string.Empty;

    public string NameEn { get; private set; } = string.Empty;

    public string? NameBn { get; private set; }

    public ZoneKind Kind { get; private set; }

    public bool IsActive { get; private set; } = true;

    /// <summary>The domestic zone used for a district no zone lists ("Rest of Bangladesh").</summary>
    public bool IsFallback { get; private set; }

    public int SortOrder { get; private set; }

    public decimal BaseFee { get; private set; }

    /// <summary>Orders at or above this subtotal ship free (base and weight fees). Null means no free delivery.</summary>
    public decimal? FreeDeliveryThreshold { get; private set; }

    public decimal RemoteSurcharge { get; private set; }

    /// <summary>Added for every started kilogram beyond the last weight slab.</summary>
    public decimal PerExtraKgFee { get; private set; }

    /// <summary>Heaviest parcel the zone accepts (international limit, BR-G3). Null means no limit.</summary>
    public int? MaxWeightGrams { get; private set; }

    public int DaysMin { get; private set; } = 1;

    public int DaysMax { get; private set; } = 3;

    public IReadOnlyList<DeliveryWeightSlab> Slabs => _slabs;

    public IReadOnlyList<DeliveryZoneArea> Areas => _areas;

    public void SetDetails(string nameEn, string? nameBn, bool isActive, bool isFallback, int sortOrder, int daysMin, int daysMax)
    {
        NameEn = nameEn.Trim();
        NameBn = string.IsNullOrWhiteSpace(nameBn) ? null : nameBn.Trim();
        IsActive = isActive;
        IsFallback = isFallback && Kind == ZoneKind.Domestic;
        SortOrder = sortOrder;
        DaysMin = Math.Max(0, daysMin);
        DaysMax = Math.Max(DaysMin, daysMax);
    }

    public void SetRates(decimal baseFee, decimal? freeDeliveryThreshold, decimal remoteSurcharge, decimal perExtraKgFee, int? maxWeightGrams)
    {
        BaseFee = baseFee;
        FreeDeliveryThreshold = freeDeliveryThreshold;
        RemoteSurcharge = remoteSurcharge;
        PerExtraKgFee = perExtraKgFee;
        MaxWeightGrams = maxWeightGrams;
    }

    /// <summary>Replaces the weight slabs (sorted by weight).</summary>
    public void ReplaceSlabs(IEnumerable<(int UpToGrams, decimal ExtraFee)> slabs)
    {
        _slabs.Clear();
        foreach (var (upTo, fee) in slabs.OrderBy(s => s.UpToGrams))
        {
            _slabs.Add(new DeliveryWeightSlab(Id, upTo, fee));
        }
    }

    public void ReplaceAreas(IEnumerable<(Guid DistrictId, Guid? UpazilaId, bool IsRemote)> areas)
    {
        _areas.Clear();
        foreach (var (district, upazila, remote) in areas)
        {
            _areas.Add(new DeliveryZoneArea(Id, district, upazila, remote));
        }
    }

    public ZoneRules Rules() =>
        new(BaseFee, FreeDeliveryThreshold, RemoteSurcharge, PerExtraKgFee, MaxWeightGrams, [.. _slabs.OrderBy(s => s.UpToGrams).Select(s => (s.UpToGrams, s.ExtraFee))], DaysMin, DaysMax);
}

/// <summary>Weight slab: a parcel up to <see cref="UpToGrams"/> adds <see cref="ExtraFee"/> to the base fee.</summary>
public sealed class DeliveryWeightSlab : Entity
{
    private DeliveryWeightSlab()
    {
    }

    internal DeliveryWeightSlab(Guid zoneId, int upToGrams, decimal extraFee)
    {
        ZoneId = zoneId;
        UpToGrams = upToGrams;
        ExtraFee = extraFee;
    }

    public Guid ZoneId { get; private set; }

    public int UpToGrams { get; private set; }

    public decimal ExtraFee { get; private set; }
}

/// <summary>A district (or one upazila of it) that belongs to a zone. An upazila row wins over its district row.</summary>
public sealed class DeliveryZoneArea : Entity
{
    private DeliveryZoneArea()
    {
    }

    internal DeliveryZoneArea(Guid zoneId, Guid districtId, Guid? upazilaId, bool isRemote)
    {
        ZoneId = zoneId;
        DistrictId = districtId;
        UpazilaId = upazilaId;
        IsRemote = isRemote;
    }

    public Guid ZoneId { get; private set; }

    public Guid DistrictId { get; private set; }

    public Guid? UpazilaId { get; private set; }

    /// <summary>Remote area: the zone's remote surcharge is added.</summary>
    public bool IsRemote { get; private set; }
}

public sealed record ZoneRules(
    decimal BaseFee,
    decimal? FreeDeliveryThreshold,
    decimal RemoteSurcharge,
    decimal PerExtraKgFee,
    int? MaxWeightGrams,
    IReadOnlyList<(int UpToGrams, decimal ExtraFee)> Slabs,
    int DaysMin,
    int DaysMax);

/// <summary>How a delivery charge was made up. <see cref="Total"/> is the amount billed (BR-G2).</summary>
public sealed record ChargeBreakdown(decimal BaseFee, decimal WeightFee, decimal RemoteSurcharge, decimal FreeWaiver, decimal Total, bool IsFree, decimal? AmountToFree);

/// <summary>The one place delivery charges are worked out, so the quote, the cart and the order always agree (BR-G2).</summary>
public static class DeliveryPricing
{
    /// <summary>Weight fee: the first slab that holds the weight; beyond the last slab, that slab's fee plus the per-kilogram fee for each started kilogram.</summary>
    public static decimal WeightFee(ZoneRules rules, int weightGrams)
    {
        if (rules.Slabs.Count == 0 || weightGrams <= 0)
        {
            return 0m;
        }

        foreach (var (upTo, fee) in rules.Slabs)
        {
            if (weightGrams <= upTo)
            {
                return fee;
            }
        }

        var last = rules.Slabs[^1];
        var extraKg = (int)Math.Ceiling((weightGrams - last.UpToGrams) / 1000.0);
        return last.ExtraFee + (extraKg * rules.PerExtraKgFee);
    }

    /// <summary>Null when the parcel is heavier than the zone accepts.</summary>
    public static ChargeBreakdown? Calculate(ZoneRules rules, decimal subtotal, int weightGrams, bool isRemote)
    {
        if (rules.MaxWeightGrams is { } max && weightGrams > max)
        {
            return null;
        }

        var weightFee = WeightFee(rules, weightGrams);
        var remote = isRemote ? rules.RemoteSurcharge : 0m;
        var free = rules.FreeDeliveryThreshold is { } threshold && subtotal >= threshold;
        var waiver = free ? rules.BaseFee + weightFee : 0m;
        var total = rules.BaseFee + weightFee + remote - waiver;
        var toFree = rules.FreeDeliveryThreshold is { } t && !free ? t - subtotal : (decimal?)null;
        return new ChargeBreakdown(rules.BaseFee, weightFee, remote, waiver, total, free, toFree);
    }
}
