using System.Text.RegularExpressions;
using Juvo.Application.Abstractions;
using Juvo.Domain.Couriers;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Couriers;

/// <summary>
/// Courier setup (module H): the list, create, change, switch on or off and archive. Couriers are shop-wide (not per branch). The page that edits them never receives or stores
/// secrets: the real adapters read their keys from configuration (Step 14), so a setting named like a key or password is refused. A courier whose adapter is not set up cannot
/// be switched on.
/// </summary>
public sealed partial class CourierAdminService(IApplicationDbContext db, ICourierProviderRegistry providers, ICurrentUser currentUser, TimeProvider clock)
{
    [GeneratedRegex("^[A-Z0-9][A-Z0-9_-]{1,29}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex("(secret|password|passwd|token|api[-_ ]?key|private|credential)", RegexOptions.IgnoreCase)]
    private static partial Regex SecretLikeKey();

    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static Error NotFound() => Error.NotFound(ErrorCodes.Courier.NotFound, "Courier not found.");

    public IReadOnlyList<CourierProviderDto> Providers() =>
        [.. providers.All.OrderBy(p => p.DisplayName, StringComparer.Ordinal).Select(p => new CourierProviderDto(p.Code, p.DisplayName, p.IsConfigured, p is ICourierSimulator))];

    public async Task<Result<IReadOnlyList<CourierDto>>> ListAsync(CancellationToken cancellationToken)
    {
        var couriers = await db.Couriers.AsNoTracking().OrderBy(c => c.Priority).ThenBy(c => c.Name).ToListAsync(cancellationToken);
        var open = ShipmentStateMachine.OpenStatuses;
        var counts = await db.Shipments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(s => open.Contains(s.Status)).GroupBy(s => s.CourierId).Select(g => new { g.Key, N = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.N, cancellationToken);
        return Result.Success<IReadOnlyList<CourierDto>>([.. couriers.Select(c => Map(c, counts.GetValueOrDefault(c.Id)))]);
    }

    public async Task<Result<CourierDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var courier = await db.Couriers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return courier is null ? NotFound() : Map(courier, await OpenCountAsync(id, cancellationToken));
    }

    private async Task<int> OpenCountAsync(Guid id, CancellationToken cancellationToken)
    {
        var open = ShipmentStateMachine.OpenStatuses;
        return await db.Shipments.IgnoreQueryFilters(["BranchScope"]).CountAsync(s => s.CourierId == id && open.Contains(s.Status), cancellationToken);
    }

    private CourierDto Map(Courier c, int openShipments)
    {
        var provider = providers.Find(c.Provider);
        return new CourierDto(c.Id, c.Code, c.Name, c.Provider, provider?.DisplayName ?? c.Provider, provider?.IsConfigured ?? false, c.IsEnabled, c.Priority, c.CodSupported, c.SupportsInternational, c.MinWeightGrams,
            c.MaxWeightGrams, c.ZoneCodes, c.ChargeOverride, c.BaseRate, c.PerExtraKgRate, c.BaseRateUpToGrams, c.TrackingUrlTemplate, c.Notes, c.Settings, provider is ICourierSimulator, openShipments, c.CreatedAt);
    }

    private async Task<Result<CourierDetails>> ValidateAsync(SaveCourierRequest r, CancellationToken cancellationToken)
    {
        var name = r.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 120)
        {
            return Invalid("name", "Give the courier a name of up to 120 characters.");
        }

        var providerCode = r.Provider?.Trim().ToUpperInvariant() ?? string.Empty;
        var provider = providers.Find(providerCode);
        if (provider is null)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["provider"] = [$"Choose one of: {string.Join(", ", providers.All.Select(p => p.Code))}."] });
        }

        if (r.IsEnabled && !provider.IsConfigured)
        {
            return Error.Conflict(ErrorCodes.Courier.NotConfigured, $"{provider.DisplayName} is not set up yet, so it cannot be switched on. It needs the shop's own account keys.");
        }

        if (r.Priority is < 1 or > 1000)
        {
            return Invalid("priority", "Priority is a number from 1 (tried first) to 1000.");
        }

        if (r.BaseRate < 0 || r.PerExtraKgRate < 0 || r.ChargeOverride is < 0 || (r.BaseRateUpToGrams ?? 1000) < 1)
        {
            return Invalid("baseRate", "Rates cannot be negative, and the base rate covers at least 1 g.");
        }

        if (r.MinWeightGrams is < 0 || r.MaxWeightGrams is < 1 || (r.MinWeightGrams is { } lo && r.MaxWeightGrams is { } hi && lo > hi))
        {
            return Invalid("maxWeightGrams", "The weight limits must be positive and the minimum cannot be above the maximum.");
        }

        if (r.TrackingUrlTemplate is { Length: > 0 } url && (url.Length > 300 || !(url.StartsWith("https://", StringComparison.Ordinal) || url.StartsWith("http://", StringComparison.Ordinal))))
        {
            return Invalid("trackingUrlTemplate", "Use a web address starting with https:// (put {consignment} where the parcel number goes).");
        }

        var settings = r.Settings ?? new Dictionary<string, string>();
        if (settings.Count > 20 || settings.Any(kv => kv.Key.Length is 0 or > 60 || kv.Value.Length > 200))
        {
            return Error.Conflict(ErrorCodes.Courier.SettingInvalid, "Use at most 20 settings; a name up to 60 and a value up to 200 characters.");
        }

        if (settings.Keys.FirstOrDefault(k => SecretLikeKey().IsMatch(k)) is { } secret)
        {
            return Error.Conflict(ErrorCodes.Courier.SettingInvalid, $"The setting '{secret}' looks like a key or password. Keys never go on this page: they are kept in the server's configuration.");
        }

        var zones = (r.SupportedZoneCodes ?? []).Select(z => z.Trim().ToUpperInvariant()).Where(z => z.Length > 0).Distinct().ToList();
        if (zones.Count > 0)
        {
            var known = await db.DeliveryZones.AsNoTracking().Where(z => zones.Contains(z.Code)).Select(z => z.Code).ToListAsync(cancellationToken);
            if (zones.FirstOrDefault(z => !known.Contains(z)) is { } unknown)
            {
                return Invalid("supportedZoneCodes", $"There is no delivery zone with the code {unknown}.");
            }
        }

        return new CourierDetails(name, providerCode, r.IsEnabled, r.Priority, r.CodSupported, r.SupportsInternational, r.MinWeightGrams, r.MaxWeightGrams, zones, r.ChargeOverride, r.BaseRate, r.PerExtraKgRate,
            r.BaseRateUpToGrams ?? 1000, r.TrackingUrlTemplate, r.Notes, settings);
    }

    public async Task<Result<CourierDto>> CreateAsync(SaveCourierRequest request, CancellationToken cancellationToken)
    {
        var code = request.Code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (!CodePattern().IsMatch(code))
        {
            return Invalid("code", "Use 2 to 30 capital letters, digits, dash or underscore, for example PATHAO.");
        }

        if (await db.Couriers.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Courier.CodeTaken, "A courier with this code exists.");
        }

        var details = await ValidateAsync(request, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        var courier = new Courier(code, details.Value);
        db.Couriers.Add(courier);
        await db.SaveChangesAsync(cancellationToken);
        return Map(courier, 0);
    }

    public async Task<Result<CourierDto>> UpdateAsync(Guid id, SaveCourierRequest request, CancellationToken cancellationToken)
    {
        var courier = await db.Couriers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (courier is null)
        {
            return NotFound();
        }

        var details = await ValidateAsync(request, cancellationToken);
        if (details.IsFailure)
        {
            return details.Error!;
        }

        courier.Apply(details.Value);
        await db.SaveChangesAsync(cancellationToken);
        return Map(courier, await OpenCountAsync(id, cancellationToken));
    }

    public async Task<Result<CourierDto>> SetEnabledAsync(Guid id, SetCourierEnabledRequest request, CancellationToken cancellationToken)
    {
        var courier = await db.Couriers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (courier is null)
        {
            return NotFound();
        }

        if (request.IsEnabled && providers.Find(courier.Provider) is not { IsConfigured: true } provider)
        {
            return Error.Conflict(ErrorCodes.Courier.NotConfigured, $"{providers.Find(courier.Provider)?.DisplayName ?? courier.Provider} is not set up yet, so it cannot be switched on. It needs the shop's own account keys.");
        }

        courier.SetEnabled(request.IsEnabled);
        await db.SaveChangesAsync(cancellationToken);
        return Map(courier, await OpenCountAsync(id, cancellationToken));
    }

    /// <summary>Archives a courier (never erased). Refused while parcels are still with it.</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var courier = await db.Couriers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (courier is null)
        {
            return NotFound();
        }

        if (await OpenCountAsync(id, cancellationToken) > 0)
        {
            return Error.Conflict(ErrorCodes.Courier.SettingInvalid, "Parcels are still with this courier. Switch it off instead, or wait until they are delivered.");
        }

        courier.SetEnabled(false);
        courier.MarkDeleted(clock.GetUtcNow(), currentUser.UserId);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }
}
