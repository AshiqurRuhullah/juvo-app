using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Juvo.Application.Couriers;
using Juvo.Application.Payments;
using Juvo.Domain.Couriers;
using Juvo.Infrastructure.Persistence;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Couriers;

/// <summary>Settings of the sandbox courier. Section: <c>Couriers:Sandbox</c>. It is on by default; switch it off in production once a real courier is configured (Step 14).</summary>
public sealed class SandboxCourierOptions
{
    public const string SectionName = "Couriers:Sandbox";

    public bool Enabled { get; set; } = true;

    /// <summary>Signs the sandbox webhooks. Empty: a random secret is made for the life of the process. Set it to send signed webhooks to the endpoint from outside (or from a test).</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>Tracking page address with <c>{consignment}</c> in it. The default points at a reserved domain that never resolves: the sandbox courier has no public page.</summary>
    public string TrackingUrlTemplate { get; set; } = "https://sandbox.courier.invalid/track/{consignment}";
}

/// <summary>
/// The sandbox courier (module H): a courier that needs no account. Booking returns a deterministic consignment id (<c>SBX-</c>, eight hex digits of the order or return number and a
/// running count, so booking the same order again gives the next id); the parcel's progress is its own table row that staff, tests or a signed webhook move along; polling reads it.
/// Reports carry ids <c>consignment:version</c>, so a repeated report is recognised. Bookings and cancels are saved at once (they are the courier's own side effects).
/// </summary>
internal sealed class SandboxCourierProvider(JuvoDbContext db, IOptions<SandboxCourierOptions> options, TimeProvider clock) : ICourierProvider, ICourierSimulator
{
    private static readonly string ProcessSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public const string ProviderCode = "SANDBOX";

    public static readonly IReadOnlyList<string> Statuses = ["booked", "picked_up", "in_transit", "at_hub", "out_for_delivery", "delivered", "delivery_failed", "returned"];

    public string Code => ProviderCode;

    public string DisplayName => "Sandbox courier";

    public bool IsConfigured => options.Value.Enabled;

    public IReadOnlyList<string> RawStatuses => Statuses;

    public static string SecretOf(SandboxCourierOptions o) => string.IsNullOrWhiteSpace(o.WebhookSecret) ? ProcessSecret : o.WebhookSecret;

    /// <summary>Our status for a sandbox status word; null for a scan that changes nothing.</summary>
    public static ShipmentStatus? Map(string raw) => raw switch
    {
        "booked" => ShipmentStatus.Booked,
        "picked_up" => ShipmentStatus.PickedUp,
        "in_transit" => ShipmentStatus.InTransit,
        "out_for_delivery" => ShipmentStatus.OutForDelivery,
        "delivered" => ShipmentStatus.Delivered,
        "delivery_failed" => ShipmentStatus.DeliveryFailed,
        "returned" => ShipmentStatus.Returned,
        "cancelled" => ShipmentStatus.Cancelled,
        _ => null,
    };

    private static string ConsignmentFor(string reference, int sequence)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(reference.Trim().ToUpperInvariant())))[..8];
        return $"SBX-{hash}{sequence:00}";
    }

    public Task<Result<CourierQuote>> QuoteAsync(CourierQuoteRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<Result<CourierQuote>>(request.WeightGrams > 50_000
            ? new CourierQuote(false, null, null, null, "The sandbox courier takes parcels up to 50 kg.")
            : new CourierQuote(true, null, 1, string.Equals(request.CountryCode, "BD", StringComparison.OrdinalIgnoreCase) ? 3 : 7, null));

    public async Task<Result<CourierBooking>> BookAsync(CourierBookingRequest request, CancellationToken cancellationToken)
    {
        var count = await db.SandboxConsignments.CountAsync(c => c.Reference == request.Reference, cancellationToken);
        var consignment = ConsignmentFor(request.Reference, count + 1);
        db.SandboxConsignments.Add(new SandboxConsignment(consignment, request.Reference, request.CodAmount, request.IsReturn, clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return new CourierBooking(consignment, consignment, options.Value.TrackingUrlTemplate.Replace("{consignment}", Uri.EscapeDataString(consignment), StringComparison.Ordinal), null);
    }

    public async Task<Result> CancelAsync(string consignmentId, CancellationToken cancellationToken)
    {
        var row = await db.SandboxConsignments.FirstOrDefaultAsync(c => c.ConsignmentId == consignmentId, cancellationToken);
        if (row is null)
        {
            return Error.NotFound(ErrorCodes.Courier.ProviderError, "The sandbox courier does not know this consignment.");
        }

        if (row.RawStatus == "cancelled")
        {
            return Result.Success();
        }

        if (row.RawStatus != "booked")
        {
            return Error.Conflict(ErrorCodes.Courier.ProviderError, "The sandbox courier has already picked this parcel up.");
        }

        row.Set("cancelled", "Cancelled by the shop", clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    public async Task<Result<CourierEvent?>> FetchStatusAsync(string consignmentId, CancellationToken cancellationToken)
    {
        var row = await db.SandboxConsignments.AsNoTracking().FirstOrDefaultAsync(c => c.ConsignmentId == consignmentId, cancellationToken);
        if (row is null)
        {
            return Error.NotFound(ErrorCodes.Courier.ProviderError, "The sandbox courier does not know this consignment.");
        }

        return row.Version <= 1 ? Result.Success<CourierEvent?>(null) : Result.Success<CourierEvent?>(ToEvent(row));
    }

    private static CourierEvent ToEvent(SandboxConsignment row) =>
        new($"{row.ConsignmentId}:{row.Version}", row.ConsignmentId, row.RawStatus, Map(row.RawStatus), row.UpdatedAtUtc, null, row.Note, row.RawStatus == "delivered" && !row.IsReturn && row.CodAmount > 0 ? row.CodAmount : null);

    public Result<CourierEvent> ParseWebhook(string body, IReadOnlyDictionary<string, string> headers, DateTimeOffset now)
    {
        var signature = headers.FirstOrDefault(h => string.Equals(h.Key, WebhookSignature.HeaderName, StringComparison.OrdinalIgnoreCase)).Value;
        if (WebhookSignature.Check(SecretOf(options.Value), signature, body, now) is { } bad)
        {
            return bad.Code == ErrorCodes.Payment.WebhookExpired
                ? new Error(ErrorCodes.Courier.WebhookExpired, bad.Message, ErrorType.Validation)
                : Error.Unauthorized(ErrorCodes.Courier.InvalidSignature, "The webhook signature is missing or invalid.");
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var id = root.GetProperty("eventId").GetString();
            var consignment = root.GetProperty("consignmentId").GetString();
            var status = root.GetProperty("status").GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(consignment) || string.IsNullOrWhiteSpace(status) || id.Length > 120 || !Statuses.Contains(status) && status != "cancelled")
            {
                throw new FormatException("A required field is empty or unknown.");
            }

            var at = root.TryGetProperty("occurredAt", out var t) && t.ValueKind == JsonValueKind.String && t.TryGetDateTimeOffset(out var parsed) ? parsed : now;
            var location = root.TryGetProperty("location", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null;
            var note = root.TryGetProperty("note", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            decimal? cod = root.TryGetProperty("codCollected", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;
            return new CourierEvent(id, consignment, status, Map(status), at, location, note, cod);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new Error(ErrorCodes.Courier.WebhookMalformed, "The webhook body is not a valid event.", ErrorType.Validation);
        }
    }

    public Task<Result<CourierLabel>> LabelAsync(string consignmentId, CancellationToken cancellationToken) =>
        Task.FromResult<Result<CourierLabel>>(new CourierLabel(consignmentId, null));

    // ---- Simulator ---------------------------------------------------------------------------------------

    public async Task<string?> NextStatusAsync(string consignmentId, CancellationToken cancellationToken)
    {
        var raw = await db.SandboxConsignments.AsNoTracking().Where(c => c.ConsignmentId == consignmentId).Select(c => c.RawStatus).FirstOrDefaultAsync(cancellationToken);
        return raw switch
        {
            "booked" => "picked_up",
            "picked_up" => "in_transit",
            "in_transit" or "at_hub" => "out_for_delivery",
            "out_for_delivery" => "delivered",
            "delivery_failed" => "out_for_delivery",
            _ => null,
        };
    }

    public async Task<Result<CourierEvent>> SimulateAsync(string consignmentId, string rawStatus, string? note, CancellationToken cancellationToken)
    {
        var row = await db.SandboxConsignments.FirstOrDefaultAsync(c => c.ConsignmentId == consignmentId, cancellationToken);
        if (row is null)
        {
            return Error.NotFound(ErrorCodes.Courier.ProviderError, "The sandbox courier does not know this consignment.");
        }

        if (row.RawStatus is "cancelled" or "delivered" or "returned")
        {
            return Error.Conflict(ErrorCodes.Courier.ProviderError, $"The sandbox parcel is already {row.RawStatus}.");
        }

        row.Set(rawStatus, string.IsNullOrWhiteSpace(note) ? null : note.Trim(), clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return ToEvent(row);
    }
}
