using Juvo.Application.Delivery;
using Juvo.Shared.Results;

namespace Juvo.Application.Pricing;

public static class FreeDeliveryRules
{
    /// <summary>The qualifying free-delivery offer for a zone (an offer limited to zones needs a known zone code), the cheapest threshold first.</summary>
    public static FreeDeliveryOffer? Pick(IEnumerable<FreeDeliveryOffer>? offers, string? zoneCode) =>
        offers?.Where(o => o.Qualifies && (o.ZoneCodes.Count == 0 || (zoneCode is not null && o.ZoneCodes.Contains(zoneCode, StringComparer.OrdinalIgnoreCase)))).OrderBy(o => o.MinCartValue).FirstOrDefault();
}

/// <summary>
/// The delivery charge with the free-delivery promotions of the pricing engine applied. The charge itself is still worked out by <see cref="DeliveryService"/> (zone, weight, courier
/// override, remote surcharge, zone threshold); when a promotion makes it free the charge becomes zero and the waived amount is reported like any other waiver, so the advance of a
/// large cash on delivery order (the standard charge) keeps working. The same call is used by the cart, the checkout quote, placement and the staff desk (BR-G2).
/// </summary>
public sealed class PricedDelivery(DeliveryService delivery)
{
    public async Task<Result<DeliveryQuoteDto>> QuoteAsync(
        string? countryCode, Guid? districtId, Guid? upazilaId, decimal goodsTotal, int weightGrams, IEnumerable<FreeDeliveryOffer>? offers, CancellationToken cancellationToken)
    {
        var quote = await delivery.QuoteAmountsAsync(countryCode, districtId, upazilaId, goodsTotal, weightGrams, cancellationToken);
        if (quote.IsFailure)
        {
            return quote;
        }

        return Apply(quote.Value, offers);
    }

    /// <summary>Waives the charge of a quote when a qualifying free-delivery offer applies to its zone; the waived amount is reported like a threshold waiver.</summary>
    public static DeliveryQuoteDto Apply(DeliveryQuoteDto q, IEnumerable<FreeDeliveryOffer>? offers)
    {
        var offer = FreeDeliveryRules.Pick(offers, q.ZoneCode);
        if (offer is null || q.IsFree || q.Total <= 0m)
        {
            return q;
        }

        return q with { FreeDeliveryWaiver = q.FreeDeliveryWaiver + q.Total, Total = 0m, IsFree = true, AmountToFreeDelivery = null, PromotionLabelEn = offer.LabelEn, PromotionLabelBn = offer.LabelBn };
    }
}
