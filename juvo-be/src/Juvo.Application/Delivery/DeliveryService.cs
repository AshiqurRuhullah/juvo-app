using Juvo.Application.Abstractions;
using Juvo.Domain.Delivery;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Delivery;

/// <summary>
/// Delivery areas and charges (module G, Bangladesh and the international placeholder). The quote uses the single charge routine
/// <see cref="DeliveryPricing"/>, so the amount shown is the amount billed (BR-G2). Zones, rates and countries are edited by staff.
/// </summary>
public sealed class DeliveryService(IApplicationDbContext db, TimeProvider clock)
{
    private static readonly TimeSpan Dhaka = TimeSpan.FromHours(6);
    public const int MaxImport = 1000;

    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    // ---- Public geography -------------------------------------------------------------------------------

    public async Task<IReadOnlyList<CountryDto>> CountriesAsync(bool enabledOnly, CancellationToken cancellationToken) =>
        await db.Countries.AsNoTracking().Where(c => !enabledOnly || c.IsEnabled).OrderBy(c => c.NameEn)
            .Select(c => new CountryDto(c.Id, c.Code, c.NameEn, c.NameBn, c.PhoneCode, c.IsEnabled, c.ZoneId)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DivisionDto>> DivisionsAsync(CancellationToken cancellationToken) =>
        await db.Divisions.AsNoTracking().OrderBy(d => d.NameEn).Select(d => new DivisionDto(d.Id, d.NameEn, d.NameBn)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<DistrictDto>> DistrictsAsync(Guid? divisionId, CancellationToken cancellationToken) =>
        await db.Districts.AsNoTracking().Where(d => divisionId == null || d.DivisionId == divisionId).OrderBy(d => d.NameEn)
            .Select(d => new DistrictDto(d.Id, d.DivisionId, d.NameEn, d.NameBn, d.Latitude, d.Longitude)).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<UpazilaDto>> UpazilasAsync(Guid districtId, CancellationToken cancellationToken) =>
        await db.Upazilas.AsNoTracking().Where(u => u.DistrictId == districtId).OrderBy(u => u.NameEn)
            .Select(u => new UpazilaDto(u.Id, u.DistrictId, u.NameEn, u.NameBn)).ToListAsync(cancellationToken);

    // ---- Quote ------------------------------------------------------------------------------------------

    /// <summary>Totals of a list of variants: the price a customer pays now, and the weight.</summary>
    public async Task<(decimal Subtotal, int WeightGrams)?> TotalsAsync(IReadOnlyList<QuoteItem> items, CancellationToken cancellationToken)
    {
        var ids = items.Select(i => i.VariantId).Distinct().ToList();
        var now = clock.GetUtcNow();
        var rows = await (from v in db.ProductVariants.AsNoTracking()
                          join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                          where ids.Contains(v.Id)
                          select new { v.Id, v.PriceOverride, v.WeightGramsOverride, p.RegularPrice, p.SalePrice, p.SaleStart, p.SaleEnd, p.WeightGrams }).ToListAsync(cancellationToken);
        if (rows.Count != ids.Count)
        {
            return null;
        }

        decimal subtotal = 0;
        var weight = 0;
        foreach (var item in items)
        {
            var row = rows.First(r => r.Id == item.VariantId);
            var sale = row.SalePrice is { } s && s < row.RegularPrice && (row.SaleStart is null || row.SaleStart <= now) && (row.SaleEnd is null || row.SaleEnd >= now) ? s : row.RegularPrice;
            subtotal += (row.PriceOverride ?? sale) * item.Quantity;
            weight += (row.WeightGramsOverride ?? row.WeightGrams ?? 0) * item.Quantity;
        }

        return (subtotal, weight);
    }

    public async Task<Result<DeliveryQuoteDto>> QuoteAsync(DeliveryQuoteRequest request, CancellationToken cancellationToken)
    {
        decimal subtotal;
        int weight;
        if (request.Items is { Count: > 0 } items)
        {
            if (items.Any(i => i.Quantity is <= 0 or > 1000) || items.Count > 200)
            {
                return Invalid("items", "Each quantity must be between 1 and 1000.");
            }

            var totals = await TotalsAsync(items, cancellationToken);
            if (totals is null)
            {
                return Error.NotFound(ErrorCodes.Cart.VariantUnavailable, "A product in the list was not found.");
            }

            (subtotal, weight) = totals.Value;
        }
        else
        {
            if (request.Subtotal is < 0 or > 100_000_000 || request.WeightGrams is < 0 or > 1_000_000_000)
            {
                return Invalid("subtotal", "Amounts and weights cannot be negative.");
            }

            subtotal = request.Subtotal ?? 0;
            weight = request.WeightGrams ?? 0;
        }

        return await QuoteAmountsAsync(request.CountryCode, request.DistrictId, request.UpazilaId, subtotal, weight, cancellationToken);
    }

    public async Task<Result<DeliveryQuoteDto>> QuoteAmountsAsync(string? countryCode, Guid? districtId, Guid? upazilaId, decimal subtotal, int weightGrams, CancellationToken cancellationToken)
    {
        var code = string.IsNullOrWhiteSpace(countryCode) ? "BD" : countryCode.Trim().ToUpperInvariant();
        var country = await db.Countries.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code, cancellationToken);
        if (country is null)
        {
            return Error.NotFound(ErrorCodes.Delivery.CountryNotFound, "Country not found.");
        }

        if (!country.IsEnabled)
        {
            return Error.Conflict(ErrorCodes.Delivery.CountryDisabled, "We do not deliver to this country yet.");
        }

        DeliveryZone? zone;
        var remote = false;
        if (code == "BD")
        {
            if (districtId is null && upazilaId is null)
            {
                return Invalid("districtId", "Choose the district of the delivery address.");
            }

            Guid? district = districtId;
            if (upazilaId is { } up)
            {
                var upazila = await db.Upazilas.AsNoTracking().FirstOrDefaultAsync(u => u.Id == up, cancellationToken);
                if (upazila is null || (district is not null && upazila.DistrictId != district))
                {
                    return Error.NotFound(ErrorCodes.Delivery.AreaNotFound, "That upazila does not belong to the district.");
                }

                district = upazila.DistrictId;
            }

            if (!await db.Districts.AnyAsync(d => d.Id == district, cancellationToken))
            {
                return Error.NotFound(ErrorCodes.Delivery.AreaNotFound, "District not found.");
            }

            var zones = await db.DeliveryZones.AsNoTracking().Include(z => z.Slabs).Include(z => z.Areas)
                .Where(z => z.IsActive && z.Kind == ZoneKind.Domestic).ToListAsync(cancellationToken);
            // An upazila row wins over its district row; otherwise the fallback zone ("Rest of Bangladesh").
            var upazilaArea = upazilaId is { } u2 ? zones.SelectMany(z => z.Areas.Select(a => (Zone: z, Area: a))).FirstOrDefault(x => x.Area.UpazilaId == u2) : default;
            var districtArea = zones.SelectMany(z => z.Areas.Select(a => (Zone: z, Area: a))).FirstOrDefault(x => x.Area.DistrictId == district && x.Area.UpazilaId == null);
            var hit = upazilaArea.Zone is not null ? upazilaArea : districtArea;
            zone = hit.Zone ?? zones.FirstOrDefault(z => z.IsFallback);
            remote = hit.Zone is not null && hit.Area.IsRemote;
        }
        else
        {
            zone = country.ZoneId is { } zoneId
                ? await db.DeliveryZones.AsNoTracking().Include(z => z.Slabs).FirstOrDefaultAsync(z => z.Id == zoneId && z.IsActive, cancellationToken)
                : null;
        }

        if (zone is null)
        {
            return Error.Conflict(ErrorCodes.Delivery.NotDeliverable, "We cannot deliver to this address yet.");
        }

        var rules = zone.Rules();
        var charge = DeliveryPricing.Calculate(rules, subtotal, weightGrams, remote);
        if (charge is null)
        {
            return Error.Conflict(ErrorCodes.Delivery.Overweight, $"The parcel is heavier than the {rules.MaxWeightGrams / 1000.0:0.#} kg this zone accepts.");
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().ToOffset(Dhaka).DateTime);
        return new DeliveryQuoteDto(zone.Code, zone.NameEn, zone.NameBn, zone.Kind.ToString(), "BDT", subtotal, weightGrams, charge.BaseFee, charge.WeightFee, charge.RemoteSurcharge,
            charge.FreeWaiver, charge.Total, charge.IsFree, charge.AmountToFree, rules.DaysMin, rules.DaysMax, today.AddDays(rules.DaysMin), today.AddDays(rules.DaysMax),
            zone.Kind == ZoneKind.International);
    }

    // ---- Admin: zones -----------------------------------------------------------------------------------

    public async Task<IReadOnlyList<DeliveryZoneDto>> ZonesAsync(CancellationToken cancellationToken)
    {
        var zones = await db.DeliveryZones.AsNoTracking().Include(z => z.Slabs).Include(z => z.Areas).OrderBy(z => z.SortOrder).ThenBy(z => z.NameEn).ToListAsync(cancellationToken);
        var countries = await db.Countries.AsNoTracking().Where(c => c.ZoneId != null).Select(c => new { c.ZoneId, c.Code }).ToListAsync(cancellationToken);
        return [.. zones.Select(z => ToDto(z, [.. countries.Where(c => c.ZoneId == z.Id).Select(c => c.Code)]))];
    }

    public async Task<Result<DeliveryZoneDto>> ZoneAsync(Guid id, CancellationToken cancellationToken)
    {
        var zone = await db.DeliveryZones.AsNoTracking().Include(z => z.Slabs).Include(z => z.Areas).FirstOrDefaultAsync(z => z.Id == id, cancellationToken);
        if (zone is null)
        {
            return Error.NotFound(ErrorCodes.Delivery.ZoneNotFound, "Delivery zone not found.");
        }

        var codes = await db.Countries.AsNoTracking().Where(c => c.ZoneId == id).Select(c => c.Code).ToListAsync(cancellationToken);
        return ToDto(zone, codes);
    }

    public async Task<Result<DeliveryZoneDto>> CreateZoneAsync(CreateZoneRequest request, CancellationToken cancellationToken)
    {
        var code = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (code.Length is < 2 or > 30 || !code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'))
        {
            return Invalid("code", "Use 2 to 30 letters, digits, hyphen or underscore.");
        }

        if (string.IsNullOrWhiteSpace(request.NameEn) || request.NameEn.Trim().Length > 100)
        {
            return Invalid("nameEn", "Enter a name (up to 100 characters).");
        }

        if (request.DaysMin < 0 || request.DaysMax < request.DaysMin || request.DaysMax > 90)
        {
            return Invalid("daysMax", "Delivery days must be 0 or more, and the latest not before the earliest.");
        }

        if (await db.DeliveryZones.AnyAsync(z => z.Code == code, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Delivery.ZoneCodeTaken, "A zone with this code already exists.");
        }

        var zone = new DeliveryZone(code, request.NameEn, request.NameBn, request.Kind);
        zone.SetDetails(request.NameEn, request.NameBn, request.IsActive, request.IsFallback, request.SortOrder, request.DaysMin, request.DaysMax);
        if (zone.IsFallback)
        {
            await ClearOtherFallbacksAsync(zone.Id, cancellationToken);
        }

        db.DeliveryZones.Add(zone);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(zone, []);
    }

    public async Task<Result<DeliveryZoneDto>> UpdateZoneAsync(Guid id, UpdateZoneRequest request, CancellationToken cancellationToken)
    {
        var zone = await db.DeliveryZones.Include(z => z.Slabs).Include(z => z.Areas).FirstOrDefaultAsync(z => z.Id == id, cancellationToken);
        if (zone is null)
        {
            return Error.NotFound(ErrorCodes.Delivery.ZoneNotFound, "Delivery zone not found.");
        }

        if (string.IsNullOrWhiteSpace(request.NameEn) || request.NameEn.Trim().Length > 100)
        {
            return Invalid("nameEn", "Enter a name (up to 100 characters).");
        }

        if (request.DaysMin < 0 || request.DaysMax < request.DaysMin || request.DaysMax > 90)
        {
            return Invalid("daysMax", "Delivery days must be 0 or more, and the latest not before the earliest.");
        }

        zone.SetDetails(request.NameEn, request.NameBn, request.IsActive, request.IsFallback, request.SortOrder, request.DaysMin, request.DaysMax);
        if (zone.IsFallback)
        {
            await ClearOtherFallbacksAsync(zone.Id, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ZoneAsync(id, cancellationToken);
    }

    public async Task<Result<DeliveryZoneDto>> SaveRatesAsync(Guid id, SaveRatesRequest request, CancellationToken cancellationToken)
    {
        var zone = await db.DeliveryZones.Include(z => z.Slabs).Include(z => z.Areas).FirstOrDefaultAsync(z => z.Id == id, cancellationToken);
        if (zone is null)
        {
            return Error.NotFound(ErrorCodes.Delivery.ZoneNotFound, "Delivery zone not found.");
        }

        var slabs = request.Slabs ?? [];
        if (request.BaseFee is < 0 or > 1_000_000 || request.RemoteSurcharge is < 0 or > 1_000_000 || request.PerExtraKgFee is < 0 or > 1_000_000 ||
            request.FreeDeliveryThreshold is < 0 || request.MaxWeightGrams is <= 0 || slabs.Any(s => s.UpToGrams <= 0 || s.ExtraFee < 0) ||
            slabs.Select(s => s.UpToGrams).Distinct().Count() != slabs.Count || slabs.Count > 20)
        {
            return Invalid("rates", "Fees cannot be negative, weight limits must be positive and each weight slab must be different.");
        }

        zone.SetRates(request.BaseFee, request.FreeDeliveryThreshold, request.RemoteSurcharge, request.PerExtraKgFee, request.MaxWeightGrams);
        zone.ReplaceSlabs(slabs.Select(s => (s.UpToGrams, s.ExtraFee)));
        await db.SaveChangesAsync(cancellationToken);
        return await ZoneAsync(id, cancellationToken);
    }

    public async Task<Result<DeliveryZoneDto>> SaveAreasAsync(Guid id, SaveAreasRequest request, CancellationToken cancellationToken)
    {
        var zone = await db.DeliveryZones.Include(z => z.Slabs).Include(z => z.Areas).FirstOrDefaultAsync(z => z.Id == id, cancellationToken);
        if (zone is null)
        {
            return Error.NotFound(ErrorCodes.Delivery.ZoneNotFound, "Delivery zone not found.");
        }

        if (zone.Kind != ZoneKind.Domestic)
        {
            return Invalid("areas", "Areas apply to domestic zones; international zones are set per country.");
        }

        var areas = (request.Areas ?? []).DistinctBy(a => (a.DistrictId, a.UpazilaId)).ToList();
        var districtIds = areas.Select(a => a.DistrictId).Distinct().ToList();
        var upazilaIds = areas.Where(a => a.UpazilaId is not null).Select(a => a.UpazilaId!.Value).ToList();
        if (await db.Districts.CountAsync(d => districtIds.Contains(d.Id), cancellationToken) != districtIds.Count ||
            (upazilaIds.Count > 0 && await db.Upazilas.CountAsync(u => upazilaIds.Contains(u.Id), cancellationToken) != upazilaIds.Count))
        {
            return Error.NotFound(ErrorCodes.Delivery.AreaNotFound, "A district or upazila in the list was not found.");
        }

        // A district (or upazila) belongs to one zone: taking it for this zone removes it from the others.
        var others = await db.DeliveryZones.Include(z => z.Areas).Where(z => z.Id != id && z.Kind == ZoneKind.Domestic).ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            var keep = other.Areas.Where(a => !areas.Any(n => n.DistrictId == a.DistrictId && n.UpazilaId == a.UpazilaId)).Select(a => (a.DistrictId, a.UpazilaId, a.IsRemote)).ToList();
            if (keep.Count != other.Areas.Count)
            {
                other.ReplaceAreas(keep);
            }
        }

        zone.ReplaceAreas(areas.Select(a => (a.DistrictId, a.UpazilaId, a.IsRemote)));
        await db.SaveChangesAsync(cancellationToken);
        return await ZoneAsync(id, cancellationToken);
    }

    public async Task<Result> DeleteZoneAsync(Guid id, CancellationToken cancellationToken)
    {
        var zone = await db.DeliveryZones.FirstOrDefaultAsync(z => z.Id == id, cancellationToken);
        if (zone is null)
        {
            return Error.NotFound(ErrorCodes.Delivery.ZoneNotFound, "Delivery zone not found.");
        }

        if (zone.IsFallback || await db.Countries.AnyAsync(c => c.ZoneId == id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Delivery.NotDeliverable, "This zone is the fallback or is used by a country. Deactivate it or move its countries first.");
        }

        db.DeliveryZones.Remove(zone);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    // ---- Admin: countries and upazilas ---------------------------------------------------------------------

    public async Task<Result<CountryDto>> SaveCountryAsync(Guid? id, SaveCountryRequest request, CancellationToken cancellationToken)
    {
        var code = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (code.Length != 2 || !code.All(char.IsAsciiLetterUpper) || string.IsNullOrWhiteSpace(request.NameEn) || string.IsNullOrWhiteSpace(request.PhoneCode) || request.PhoneCode.Length > 8)
        {
            return Invalid("code", "Enter the two-letter country code, the name and the phone code.");
        }

        if (request.ZoneId is { } zoneId && !await db.DeliveryZones.AnyAsync(z => z.Id == zoneId && z.Kind == ZoneKind.International, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Delivery.ZoneNotFound, "Choose an international zone.");
        }

        Country? country;
        if (id is { } existingId)
        {
            country = await db.Countries.FirstOrDefaultAsync(c => c.Id == existingId, cancellationToken);
            if (country is null)
            {
                return Error.NotFound(ErrorCodes.Delivery.CountryNotFound, "Country not found.");
            }

            if (country.Code == "BD" && !request.IsEnabled)
            {
                return Error.Conflict(ErrorCodes.Delivery.CountryDisabled, "Bangladesh cannot be switched off.");
            }

            country.Update(request.NameEn, request.NameBn, request.PhoneCode, country.Code == "BD" ? null : request.ZoneId);
            country.SetEnabled(request.IsEnabled);
        }
        else
        {
            if (await db.Countries.AnyAsync(c => c.Code == code, cancellationToken))
            {
                return Error.Conflict(ErrorCodes.Delivery.CountryCodeTaken, "This country is already in the list.");
            }

            country = new Country(code, request.NameEn, request.NameBn, request.PhoneCode, request.IsEnabled, request.ZoneId);
            db.Countries.Add(country);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new CountryDto(country.Id, country.Code, country.NameEn, country.NameBn, country.PhoneCode, country.IsEnabled, country.ZoneId);
    }

    /// <summary>Adds upazilas the loaded dataset lacks (FR-G02: "upazila data importable"). Existing names in the district are skipped.</summary>
    public async Task<Result<ImportUpazilasResult>> ImportUpazilasAsync(IReadOnlyList<AddUpazilaRequest> items, CancellationToken cancellationToken)
    {
        if (items.Count is 0 or > MaxImport)
        {
            return Invalid("items", $"Send between 1 and {MaxImport} upazilas.");
        }

        if (items.Any(i => string.IsNullOrWhiteSpace(i.NameEn) || i.NameEn.Trim().Length > 100 || (i.NameBn?.Length ?? 0) > 100))
        {
            return Invalid("items", "Every upazila needs an English name of up to 100 characters.");
        }

        var districtIds = items.Select(i => i.DistrictId).Distinct().ToList();
        var known = await db.Districts.Where(d => districtIds.Contains(d.Id)).Select(d => d.Id).ToListAsync(cancellationToken);
        if (known.Count != districtIds.Count)
        {
            return Error.NotFound(ErrorCodes.Delivery.AreaNotFound, "A district in the list was not found.");
        }

        var existing = (await db.Upazilas.Where(u => districtIds.Contains(u.DistrictId)).Select(u => new { u.DistrictId, u.NameEn }).ToListAsync(cancellationToken))
            .Select(u => (u.DistrictId, u.NameEn.ToLowerInvariant())).ToHashSet();
        var added = 0;
        var skipped = 0;
        foreach (var item in items)
        {
            if (!existing.Add((item.DistrictId, item.NameEn.Trim().ToLowerInvariant())))
            {
                skipped++;
                continue;
            }

            db.Upazilas.Add(new Upazila(null, item.DistrictId, item.NameEn, item.NameBn));
            added++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new ImportUpazilasResult(added, skipped);
    }

    private async Task ClearOtherFallbacksAsync(Guid keep, CancellationToken cancellationToken)
    {
        foreach (var other in await db.DeliveryZones.Where(z => z.IsFallback && z.Id != keep).ToListAsync(cancellationToken))
        {
            other.SetDetails(other.NameEn, other.NameBn, other.IsActive, isFallback: false, other.SortOrder, other.DaysMin, other.DaysMax);
        }
    }

    private static DeliveryZoneDto ToDto(DeliveryZone z, IReadOnlyList<string> countryCodes) =>
        new(z.Id, z.Code, z.NameEn, z.NameBn, z.Kind, z.IsActive, z.IsFallback, z.SortOrder, z.BaseFee, z.FreeDeliveryThreshold, z.RemoteSurcharge, z.PerExtraKgFee, z.MaxWeightGrams,
            z.DaysMin, z.DaysMax, [.. z.Slabs.OrderBy(s => s.UpToGrams).Select(s => new SlabDto(s.UpToGrams, s.ExtraFee))], [.. z.Areas.Select(a => new AreaDto(a.DistrictId, a.UpazilaId, a.IsRemote))],
            countryCodes);
}
