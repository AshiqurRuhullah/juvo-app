using Juvo.Application.Abstractions;
using Juvo.Domain.Couriers;
using Juvo.Domain.Orders;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Couriers;

/// <summary>What the selector needs to know about the parcel.</summary>
public sealed record ParcelFacts(string CountryCode, string? ZoneCode, string? District, int WeightGrams, decimal CodAmount);

/// <summary>One courier checked against one parcel: usable or not, why not, and the price and time it quoted.</summary>
public sealed record CourierCheck(Courier Courier, bool Eligible, string? ErrorCode, string? Reason, decimal? Cost, int? DaysMin, int? DaysMax);

/// <summary>
/// Chooses a courier for a parcel (FR-H auto suggest): enabled couriers with a working adapter, cash on delivery where needed, weight and zone limits, the courier's own
/// serviceability answer; ranked by priority, then by what it costs the shop, then by name. Staff can always pick another eligible courier by hand.
/// </summary>
public sealed class CourierSelector(IApplicationDbContext db, ICourierProviderRegistry providers)
{
    public static decimal CodDue(Order order) =>
        order.PaymentMethod == "COD" && order.PaymentStatus is PaymentStatus.Pending ? Math.Max(0m, order.GrandTotal - order.AdvancePaid) : 0m;

    public static ParcelFacts FactsOf(Order order, int weightGrams) => new(order.ShipCountryCode, order.ZoneCode, order.ShipDistrict ?? order.ShipCity, weightGrams, CodDue(order));

    public async Task<IReadOnlyList<CourierCheck>> CheckAllAsync(ParcelFacts parcel, CancellationToken cancellationToken)
    {
        var couriers = await db.Couriers.AsNoTracking().OrderBy(c => c.Priority).ThenBy(c => c.Name).ToListAsync(cancellationToken);
        var checks = new List<CourierCheck>();
        foreach (var courier in couriers)
        {
            checks.Add(await CheckAsync(courier, parcel, cancellationToken));
        }

        return checks;
    }

    /// <summary>The eligible couriers in the order they are suggested; the first one is the recommendation.</summary>
    public static IReadOnlyList<CourierCheck> Rank(IEnumerable<CourierCheck> checks) =>
        [.. checks.Where(c => c.Eligible).OrderBy(c => c.Courier.Priority).ThenBy(c => c.Cost ?? decimal.MaxValue).ThenBy(c => c.Courier.Name, StringComparer.Ordinal)];

    public async Task<CourierCheck> CheckAsync(Courier courier, ParcelFacts parcel, CancellationToken cancellationToken)
    {
        CourierCheck No(string code, string reason) => new(courier, false, code, reason, null, null, null);

        if (!courier.IsEnabled)
        {
            return No(ErrorCodes.Courier.Disabled, $"{courier.Name} is switched off.");
        }

        var provider = providers.Find(courier.Provider);
        if (provider is null)
        {
            return No(ErrorCodes.Courier.UnknownProvider, $"{courier.Name} uses an adapter that does not exist ({courier.Provider}).");
        }

        if (!provider.IsConfigured)
        {
            return No(ErrorCodes.Courier.NotConfigured, $"{courier.Name} is not set up yet. It needs the shop's own account keys.");
        }

        if (parcel.CodAmount > 0 && !courier.CodSupported)
        {
            return No(ErrorCodes.Courier.CodNotSupported, $"{courier.Name} does not collect cash on delivery.");
        }

        if (courier.MinWeightGrams is { } min && parcel.WeightGrams < min)
        {
            return No(ErrorCodes.Courier.WeightNotAllowed, $"{courier.Name} takes parcels of at least {min} g.");
        }

        if (courier.MaxWeightGrams is { } max && parcel.WeightGrams > max)
        {
            return No(ErrorCodes.Courier.WeightNotAllowed, $"{courier.Name} takes parcels of at most {max} g.");
        }

        var international = !string.Equals(parcel.CountryCode, "BD", StringComparison.OrdinalIgnoreCase);
        if (international && !courier.SupportsInternational)
        {
            return No(ErrorCodes.Courier.NotServiceable, $"{courier.Name} does not ship abroad.");
        }

        if (!international && courier.ZoneCodes.Count > 0 && (parcel.ZoneCode is null || !courier.ZoneCodes.Contains(parcel.ZoneCode, StringComparer.OrdinalIgnoreCase)))
        {
            return No(ErrorCodes.Courier.NotServiceable, $"{courier.Name} does not serve this delivery zone.");
        }

        var quote = await provider.QuoteAsync(new CourierQuoteRequest(parcel.CountryCode, parcel.ZoneCode, parcel.District, parcel.WeightGrams, parcel.CodAmount, courier.Settings), cancellationToken);
        if (quote.IsFailure)
        {
            return No(quote.Error!.Code, quote.Error.Message);
        }

        if (!quote.Value.Serviceable)
        {
            return No(ErrorCodes.Courier.NotServiceable, quote.Value.Reason ?? $"{courier.Name} cannot deliver to this address.");
        }

        return new CourierCheck(courier, true, null, null, quote.Value.Price ?? courier.CostFor(parcel.WeightGrams), quote.Value.DaysMin, quote.Value.DaysMax);
    }
}
