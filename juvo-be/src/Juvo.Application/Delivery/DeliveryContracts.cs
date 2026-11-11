using Juvo.Domain.Delivery;

namespace Juvo.Application.Delivery;

// ---- Public geo ----------------------------------------------------------------------------------------------

public sealed record CountryDto(Guid Id, string Code, string NameEn, string? NameBn, string PhoneCode, bool IsEnabled, Guid? ZoneId);

public sealed record DivisionDto(Guid Id, string NameEn, string? NameBn);

public sealed record DistrictDto(Guid Id, Guid DivisionId, string NameEn, string? NameBn, double? Latitude, double? Longitude);

public sealed record UpazilaDto(Guid Id, Guid DistrictId, string NameEn, string? NameBn);

// ---- Quote ---------------------------------------------------------------------------------------------------

public sealed record QuoteItem(Guid VariantId, int Quantity);

/// <summary>
/// Where and what: a Bangladesh address by <c>DistrictId</c> (and optionally <c>UpazilaId</c>) or another country by <c>CountryCode</c>.
/// Give the goods as <c>Items</c>, or as <c>Subtotal</c> and <c>WeightGrams</c> (the cart passes its own totals).
/// </summary>
public sealed record DeliveryQuoteRequest(string? CountryCode, Guid? DistrictId, Guid? UpazilaId, IReadOnlyList<QuoteItem>? Items, decimal? Subtotal, int? WeightGrams);

public sealed record DeliveryQuoteDto(
    string ZoneCode, string ZoneNameEn, string? ZoneNameBn, string Kind, string Currency, decimal Subtotal, int WeightGrams, decimal BaseFee, decimal WeightFee,
    decimal RemoteSurcharge, decimal FreeDeliveryWaiver, decimal Total, bool IsFree, decimal? AmountToFreeDelivery, int DaysMin, int DaysMax,
    DateOnly EarliestDelivery, DateOnly LatestDelivery, bool DutiesPaidByReceiver);

// ---- Admin ---------------------------------------------------------------------------------------------------

public sealed record SlabDto(int UpToGrams, decimal ExtraFee);

public sealed record AreaDto(Guid DistrictId, Guid? UpazilaId, bool IsRemote);

public sealed record DeliveryZoneDto(
    Guid Id, string Code, string NameEn, string? NameBn, ZoneKind Kind, bool IsActive, bool IsFallback, int SortOrder, decimal BaseFee, decimal? FreeDeliveryThreshold,
    decimal RemoteSurcharge, decimal PerExtraKgFee, int? MaxWeightGrams, int DaysMin, int DaysMax, IReadOnlyList<SlabDto> Slabs, IReadOnlyList<AreaDto> Areas,
    IReadOnlyList<string> CountryCodes);

public sealed record CreateZoneRequest(string Code, string NameEn, string? NameBn, ZoneKind Kind, bool IsActive = true, bool IsFallback = false, int SortOrder = 0, int DaysMin = 1, int DaysMax = 3);

public sealed record UpdateZoneRequest(string NameEn, string? NameBn, bool IsActive, bool IsFallback, int SortOrder, int DaysMin, int DaysMax);

public sealed record SaveRatesRequest(decimal BaseFee, decimal? FreeDeliveryThreshold, decimal RemoteSurcharge, decimal PerExtraKgFee, int? MaxWeightGrams, IReadOnlyList<SlabDto> Slabs);

/// <summary>Replaces the areas of the zone. A district listed here is taken away from any other zone that held it.</summary>
public sealed record SaveAreasRequest(IReadOnlyList<AreaDto> Areas);

public sealed record SaveCountryRequest(string Code, string NameEn, string? NameBn, string PhoneCode, bool IsEnabled, Guid? ZoneId);

public sealed record AddUpazilaRequest(Guid DistrictId, string NameEn, string? NameBn);

public sealed record ImportUpazilasRequest(IReadOnlyList<AddUpazilaRequest> Items);

public sealed record ImportUpazilasResult(int Added, int Skipped);
