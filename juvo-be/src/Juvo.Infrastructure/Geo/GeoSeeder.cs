using System.Reflection;
using System.Text.Json;
using Juvo.Domain.Delivery;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Juvo.Infrastructure.Geo;

/// <summary>
/// Loads the geography of Bangladesh (divisions, districts, upazilas) and the starting delivery zones, countries and rates. Idempotent:
/// geography rows are matched by the id of the dataset, zones and countries by code, and anything an administrator changed or removed
/// is left alone. The dataset is <c>nuhil/bangladesh-geocode</c> (MIT licence, see Geo/Data/LICENSE-nuhil-bangladesh-geocode.txt).
/// The zone and rate numbers are PLACEHOLDERS for the owner to edit (docs/assumptions.md, section K).
/// </summary>
public sealed class GeoSeeder(JuvoDbContext db, ILogger<GeoSeeder> logger)
{
    private sealed record DivisionRow(int Id, string Name, string? Bn);

    private sealed record DistrictRow(int Id, int DivisionId, string Name, string? Bn, double? Lat, double? Lon);

    private sealed record UpazilaRow(int Id, int DistrictId, string Name, string? Bn);

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // ISO 3166-1 alpha-2 code, English name, international dialling code. All disabled except Bangladesh and India (sandbox, assumptions C6).
    private static readonly (string Code, string Name, string Phone)[] Countries =
    [
        ("BD", "Bangladesh", "880"), ("IN", "India", "91"), ("PK", "Pakistan", "92"), ("NP", "Nepal", "977"), ("LK", "Sri Lanka", "94"), ("BT", "Bhutan", "975"),
        ("MV", "Maldives", "960"), ("AE", "United Arab Emirates", "971"), ("SA", "Saudi Arabia", "966"), ("QA", "Qatar", "974"), ("KW", "Kuwait", "965"), ("OM", "Oman", "968"),
        ("BH", "Bahrain", "973"), ("MY", "Malaysia", "60"), ("SG", "Singapore", "65"), ("GB", "United Kingdom", "44"), ("US", "United States", "1"), ("CA", "Canada", "1"),
        ("AU", "Australia", "61"), ("DE", "Germany", "49"), ("FR", "France", "33"), ("IT", "Italy", "39"), ("JP", "Japan", "81"), ("CN", "China", "86"),
    ];

    public async Task<int> SeedAsync(CancellationToken cancellationToken = default)
    {
        var added = await SeedGeographyAsync(cancellationToken);
        added += await SeedDeliveryAsync(cancellationToken);
        logger.LogInformation("Geography and delivery seed done: {Count} row(s) added.", added);
        return added;
    }

    private static T[] Read<T>(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<T[]>(stream, Json) ?? [];
    }

    private async Task<int> SeedGeographyAsync(CancellationToken cancellationToken)
    {
        var added = 0;
        var divisions = await db.Divisions.ToDictionaryAsync(d => d.SourceId, cancellationToken);
        foreach (var row in Read<DivisionRow>("divisions.json").Where(r => !divisions.ContainsKey(r.Id)))
        {
            var division = new Division(row.Id, row.Name, row.Bn);
            db.Divisions.Add(division);
            divisions[row.Id] = division;
            added++;
        }

        var districts = await db.Districts.ToDictionaryAsync(d => d.SourceId, cancellationToken);
        foreach (var row in Read<DistrictRow>("districts.json").Where(r => !districts.ContainsKey(r.Id)))
        {
            var district = new District(row.Id, divisions[row.DivisionId].Id, row.Name, row.Bn, row.Lat, row.Lon);
            db.Districts.Add(district);
            districts[row.Id] = district;
            added++;
        }

        var knownUpazilas = (await db.Upazilas.Where(u => u.SourceId != null).Select(u => u.SourceId!.Value).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var row in Read<UpazilaRow>("upazilas.json").Where(r => !knownUpazilas.Contains(r.Id)))
        {
            db.Upazilas.Add(new Upazila(row.Id, districts[row.DistrictId].Id, row.Name, row.Bn));
            added++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return added;
    }

    private async Task<int> SeedDeliveryAsync(CancellationToken cancellationToken)
    {
        var added = 0;
        var zoneCodes = (await db.DeliveryZones.Select(z => z.Code).ToListAsync(cancellationToken)).ToHashSet();
        var districts = await db.Districts.AsNoTracking().ToDictionaryAsync(d => d.NameEn, d => d.Id, cancellationToken);

        Guid? District(string name) => districts.TryGetValue(name, out var id) ? id : null;

        DeliveryZone NewZone(string code, string nameEn, string? nameBn, ZoneKind kind, int order, bool fallback, int daysMin, int daysMax, decimal baseFee, decimal? free,
            decimal remote, decimal perKg, int? maxGrams, params (int UpTo, decimal Fee)[] slabs)
        {
            var zone = new DeliveryZone(code, nameEn, nameBn, kind);
            zone.SetDetails(nameEn, nameBn, true, fallback, order, daysMin, daysMax);
            zone.SetRates(baseFee, free, remote, perKg, maxGrams);
            zone.ReplaceSlabs(slabs);
            return zone;
        }

        if (zoneCodes.Add("DHAKA_CITY"))
        {
            var zone = NewZone("DHAKA_CITY", "Inside Dhaka", "ঢাকার ভিতরে", ZoneKind.Domestic, 1, false, 1, 2, 70, 5000, 0, 20, null, (1000, 0), (2000, 20), (5000, 60));
            if (District("Dhaka") is { } dhaka)
            {
                zone.ReplaceAreas([(dhaka, null, false)]);
            }

            db.DeliveryZones.Add(zone);
            added++;
        }

        if (zoneCodes.Add("DHAKA_SUBURBS"))
        {
            var zone = NewZone("DHAKA_SUBURBS", "Dhaka suburbs", "ঢাকার আশেপাশে", ZoneKind.Domestic, 2, false, 2, 3, 100, 6000, 0, 25, null, (1000, 0), (2000, 25), (5000, 70));
            var areas = new List<(Guid, Guid?, bool)>();
            foreach (var name in new[] { "Gazipur", "Narayanganj", "Munshiganj", "Manikganj" })
            {
                if (District(name) is { } id)
                {
                    areas.Add((id, null, false));
                }
            }

            // The upazilas of Dhaka district in the dataset lie around the city, so they are priced as suburbs.
            if (District("Dhaka") is { } dhaka)
            {
                var around = await db.Upazilas.AsNoTracking().Where(u => u.DistrictId == dhaka).Select(u => u.Id).ToListAsync(cancellationToken);
                areas.AddRange(around.Select(id => (dhaka, (Guid?)id, false)));
            }

            zone.ReplaceAreas(areas);
            db.DeliveryZones.Add(zone);
            added++;
        }

        if (zoneCodes.Add("MAJOR_CITIES"))
        {
            var zone = NewZone("MAJOR_CITIES", "Major cities", "প্রধান শহর", ZoneKind.Domestic, 3, false, 2, 4, 110, 6000, 0, 30, null, (1000, 0), (2000, 30), (5000, 80));
            var areas = new List<(Guid, Guid?, bool)>();
            foreach (var name in new[] { "Chattogram", "Rajshahi", "Khulna", "Sylhet", "Barisal", "Rangpur", "Mymensingh" })
            {
                if (District(name) is { } id)
                {
                    areas.Add((id, null, false));
                }
            }

            zone.ReplaceAreas(areas);
            db.DeliveryZones.Add(zone);
            added++;
        }

        if (zoneCodes.Add("REST_OF_BD"))
        {
            var zone = NewZone("REST_OF_BD", "Rest of Bangladesh", "বাংলাদেশের অন্যান্য এলাকা", ZoneKind.Domestic, 4, true, 3, 5, 130, 7000, 50, 35, null, (1000, 0), (2000, 35), (5000, 90));
            var areas = new List<(Guid, Guid?, bool)>();
            foreach (var name in new[] { "Rangamati", "Khagrachhari", "Bandarban" })
            {
                if (District(name) is { } id)
                {
                    areas.Add((id, null, true)); // hill districts: remote surcharge applies
                }
            }

            zone.ReplaceAreas(areas);
            db.DeliveryZones.Add(zone);
            added++;
        }

        DeliveryZone? southAsia = null;
        if (zoneCodes.Add("INTL_SOUTH_ASIA"))
        {
            southAsia = NewZone("INTL_SOUTH_ASIA", "South Asia", null, ZoneKind.International, 10, false, 7, 14, 1200, null, 0, 600, 20000, (500, 0), (1000, 300), (2000, 700));
            db.DeliveryZones.Add(southAsia);
            added++;
        }

        if (zoneCodes.Add("INTL_REST"))
        {
            var zone = NewZone("INTL_REST", "Rest of the world", null, ZoneKind.International, 11, false, 10, 21, 2500, null, 0, 900, 20000, (500, 0), (1000, 500), (2000, 1200));
            zone.SetDetails(zone.NameEn, zone.NameBn, false, false, 11, 10, 21); // placeholder, off until the owner prices it
            db.DeliveryZones.Add(zone);
            added++;
        }

        await db.SaveChangesAsync(cancellationToken);

        var southAsiaId = southAsia?.Id ?? await db.DeliveryZones.Where(z => z.Code == "INTL_SOUTH_ASIA").Select(z => (Guid?)z.Id).FirstOrDefaultAsync(cancellationToken);
        var countryCodes = (await db.Countries.Select(c => c.Code).ToListAsync(cancellationToken)).ToHashSet();
        foreach (var (code, name, phone) in Countries.Where(c => !countryCodes.Contains(c.Code)))
        {
            var enabled = code is "BD" or "IN";
            db.Countries.Add(new Country(code, name, null, phone, enabled, code == "IN" ? southAsiaId : null));
            added++;
        }

        await db.SaveChangesAsync(cancellationToken);
        return added;
    }
}
