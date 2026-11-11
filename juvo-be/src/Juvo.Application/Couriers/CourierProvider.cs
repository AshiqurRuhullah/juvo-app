using Juvo.Domain.Couriers;
using Juvo.Shared.Results;

namespace Juvo.Application.Couriers;

/// <summary>One end of a parcel's journey, as the courier needs it.</summary>
public sealed record CourierParty(string Name, string? Phone, string Address, string? District, string CountryCode);

public sealed record CourierQuoteRequest(string CountryCode, string? ZoneCode, string? District, int WeightGrams, decimal CodAmount, IReadOnlyDictionary<string, string> Settings);

/// <summary>Whether the courier serves the address, and what it would charge the shop and how long it takes (null when the courier does not say).</summary>
public sealed record CourierQuote(bool Serviceable, decimal? Price, int? DaysMin, int? DaysMax, string? Reason);

public sealed record CourierBookingRequest(
    string Reference, bool IsReturn, CourierParty Pickup, CourierParty Delivery, int WeightGrams, int? LengthCm, int? WidthCm, int? HeightCm, decimal CodAmount, string? Note,
    IReadOnlyDictionary<string, string> Settings);

public sealed record CourierBooking(string ConsignmentId, string? ProviderReference, string? TrackingUrl, decimal? Price);

/// <summary>
/// One status report about a parcel: from a webhook, a poll or the sandbox simulator. <see cref="EventId"/> is unique per report at the courier (idempotency).
/// <see cref="Mapped"/> is our status for the courier's <see cref="RawStatus"/>; null for a scan that changes nothing (for example "arrived at the hub").
/// </summary>
public sealed record CourierEvent(string EventId, string ConsignmentId, string RawStatus, ShipmentStatus? Mapped, DateTimeOffset OccurredAt, string? Location, string? Note, decimal? CodCollected);

/// <summary>What the label needs from the courier: the barcode text and, when the courier makes its own label, its address.</summary>
public sealed record CourierLabel(string Barcode, string? ProviderUrl);

/// <summary>
/// One courier company (module H). The shop talks only to this interface; the sandbox adapter is on by default and every real company (Pathao, Steadfast, RedX, Paperfly,
/// eCourier, DHL) is a registered but unconfigured slot until Step 14. <see cref="Code"/> equals <c>Courier.Provider</c>.
/// </summary>
public interface ICourierProvider
{
    string Code { get; }

    string DisplayName { get; }

    /// <summary>False for a slot without credentials: every call answers <c>COURIER.NOT_CONFIGURED</c>.</summary>
    bool IsConfigured { get; }

    /// <summary>Serviceability and price for an address and weight.</summary>
    Task<Result<CourierQuote>> QuoteAsync(CourierQuoteRequest request, CancellationToken cancellationToken);

    Task<Result<CourierBooking>> BookAsync(CourierBookingRequest request, CancellationToken cancellationToken);

    /// <summary>Cancels a booking that was not picked up yet.</summary>
    Task<Result> CancelAsync(string consignmentId, CancellationToken cancellationToken);

    /// <summary>The courier's latest report about the parcel (used by the polling job). Null when it has nothing new to say.</summary>
    Task<Result<CourierEvent?>> FetchStatusAsync(string consignmentId, CancellationToken cancellationToken);

    /// <summary>Checks signature and replay window of a webhook and reads it. Fails with <c>COURIER.INVALID_SIGNATURE</c>, <c>COURIER.WEBHOOK_EXPIRED</c> or <c>COURIER.WEBHOOK_MALFORMED</c>.</summary>
    Result<CourierEvent> ParseWebhook(string body, IReadOnlyDictionary<string, string> headers, DateTimeOffset now);

    Task<Result<CourierLabel>> LabelAsync(string consignmentId, CancellationToken cancellationToken);
}

/// <summary>An adapter that can act as the courier itself (the sandbox). Staff and tests use it to move a parcel along without a real courier.</summary>
public interface ICourierSimulator
{
    /// <summary>The raw statuses the simulator knows, in the order a normal journey goes through them.</summary>
    IReadOnlyList<string> RawStatuses { get; }

    /// <summary>Works out the next raw status for a parcel from its current one (for example booked to picked_up). Null when the journey is over.</summary>
    Task<string?> NextStatusAsync(string consignmentId, CancellationToken cancellationToken);

    /// <summary>Sets the parcel's status at the simulated courier and returns the report the courier would send.</summary>
    Task<Result<CourierEvent>> SimulateAsync(string consignmentId, string rawStatus, string? note, CancellationToken cancellationToken);
}

public interface ICourierProviderRegistry
{
    IReadOnlyList<ICourierProvider> All { get; }

    ICourierProvider? Find(string? code);
}

public sealed class CourierProviderRegistry(IEnumerable<ICourierProvider> providers) : ICourierProviderRegistry
{
    // Later registrations win, so a test or a host can replace an adapter.
    private readonly Dictionary<string, ICourierProvider> _byCode = providers.GroupBy(p => p.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<ICourierProvider> All => [.. _byCode.Values];

    public ICourierProvider? Find(string? code) => _byCode.GetValueOrDefault(code ?? string.Empty);
}

/// <summary>A courier company slot without credentials. It exists so the courier can be listed and prepared, and every call says it is not set up (the real adapter comes in Step 14 with the owner's keys).</summary>
public sealed class NotConfiguredCourierProvider(string code, string displayName) : ICourierProvider
{
    private Error NotConfigured() => Error.Conflict(ErrorCodes.Courier.NotConfigured, $"{displayName} is not set up yet. It needs the shop's own account keys.");

    public string Code => code;

    public string DisplayName => displayName;

    public bool IsConfigured => false;

    public Task<Result<CourierQuote>> QuoteAsync(CourierQuoteRequest request, CancellationToken cancellationToken) => Task.FromResult<Result<CourierQuote>>(NotConfigured());

    public Task<Result<CourierBooking>> BookAsync(CourierBookingRequest request, CancellationToken cancellationToken) => Task.FromResult<Result<CourierBooking>>(NotConfigured());

    public Task<Result> CancelAsync(string consignmentId, CancellationToken cancellationToken) => Task.FromResult<Result>(NotConfigured());

    public Task<Result<CourierEvent?>> FetchStatusAsync(string consignmentId, CancellationToken cancellationToken) => Task.FromResult<Result<CourierEvent?>>(NotConfigured());

    public Result<CourierEvent> ParseWebhook(string body, IReadOnlyDictionary<string, string> headers, DateTimeOffset now) => NotConfigured();

    public Task<Result<CourierLabel>> LabelAsync(string consignmentId, CancellationToken cancellationToken) => Task.FromResult<Result<CourierLabel>>(NotConfigured());
}
