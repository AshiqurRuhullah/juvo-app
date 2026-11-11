using System.Security.Cryptography;
using System.Text;
using Juvo.Shared.Results;

namespace Juvo.Application.Payments;

public sealed record GatewayInitiateRequest(
    Guid PaymentId, string OrderNumber, decimal Amount, string Currency, string Method, string? ReturnUrl, string? CustomerMobile, string? CustomerEmail, DateTimeOffset ExpiresAt);

/// <summary>Where to send the customer: the gateway's hosted page, and the gateway's own id for the payment.</summary>
public sealed record GatewayInitiation(string ProviderReference, string RedirectUrl);

public enum GatewayOutcome
{
    Pending = 1,
    Succeeded = 2,
    Failed = 3,
    Cancelled = 4,
}

/// <summary>What the gateway itself says about a payment when asked server-side. Only <see cref="GatewayOutcome.Succeeded"/> may lead to Paid.</summary>
public sealed record GatewayVerification(GatewayOutcome Outcome, string? TransactionId, decimal? Amount, string? Currency, string? Detail);

public sealed record GatewayRefundRequest(string ProviderReference, string? TransactionId, decimal Amount, string Currency, string Reference);

public enum GatewayRefundKind
{
    /// <summary>The money went back.</summary>
    Completed = 1,

    /// <summary>The gateway accepted the refund and finishes later (a webhook or a status call completes it).</summary>
    Pending = 2,

    Failed = 3,
}

public sealed record GatewayRefundOutcome(GatewayRefundKind Kind, string? ProviderRefundId, string? Detail);

public enum GatewayEventKind
{
    PaymentSucceeded = 1,
    PaymentFailed = 2,
    PaymentCancelled = 3,
    RefundCompleted = 4,
    Other = 5,
}

/// <summary>A webhook the gateway sent, after its signature was checked. <see cref="EventId"/> is unique per event at the provider (idempotency).</summary>
public sealed record GatewayWebhookEvent(string EventId, string ProviderReference, GatewayEventKind Kind, decimal? Amount, string? Currency, DateTimeOffset OccurredAt);

/// <summary>
/// One online payment provider (module I). The shop talks only to this interface; the sandbox adapter is on by default and every real provider (bKash, Nagad, SSLCommerz,
/// aamarPay, Stripe, PayPal) is a registered but unconfigured slot until Step 14. <see cref="Code"/> equals the payment method code stored on the order.
/// </summary>
public interface IPaymentGateway
{
    string Code { get; }

    string DisplayName { get; }

    /// <summary>False for a slot without credentials: every call answers <c>PAYMENT.NOT_CONFIGURED</c>.</summary>
    bool IsConfigured { get; }

    bool SupportsRefund { get; }

    /// <summary>Creates the payment at the provider and returns the hosted page address.</summary>
    Task<Result<GatewayInitiation>> InitiateAsync(GatewayInitiateRequest request, CancellationToken cancellationToken);

    /// <summary>Asks the provider (server to server) what happened. The browser's word is never enough.</summary>
    Task<Result<GatewayVerification>> VerifyAsync(string providerReference, CancellationToken cancellationToken);

    Task<Result<GatewayRefundOutcome>> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken);

    /// <summary>Checks the signature and the replay window of a webhook and reads it. Fails with <c>PAYMENT.INVALID_SIGNATURE</c>, <c>PAYMENT.WEBHOOK_EXPIRED</c> or <c>PAYMENT.WEBHOOK_MALFORMED</c>.</summary>
    Result<GatewayWebhookEvent> ParseWebhook(string body, IReadOnlyDictionary<string, string> headers, DateTimeOffset now);
}

public interface IPaymentGatewayRegistry
{
    IReadOnlyList<IPaymentGateway> All { get; }

    IPaymentGateway? Find(string code);
}

public sealed class PaymentGatewayRegistry(IEnumerable<IPaymentGateway> gateways) : IPaymentGatewayRegistry
{
    // Later registrations win, so a test or a host can replace a provider.
    private readonly Dictionary<string, IPaymentGateway> _byCode = gateways.GroupBy(g => g.Code, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<IPaymentGateway> All => [.. _byCode.Values];

    public IPaymentGateway? Find(string code) => _byCode.GetValueOrDefault(code ?? string.Empty);
}

/// <summary>A provider slot without credentials. It is registered so the method exists in the catalogue, and every call says it is not configured (real adapters come in Step 14 with the owner's keys).</summary>
public sealed class NotConfiguredGateway(string code, string displayName) : IPaymentGateway
{
    private static Error NotConfigured(string name) => Error.Failure(ErrorCodes.Payment.NotConfigured, $"{name} is not set up yet. It needs the shop's own account keys.");

    public string Code => code;

    public string DisplayName => displayName;

    public bool IsConfigured => false;

    public bool SupportsRefund => false;

    public Task<Result<GatewayInitiation>> InitiateAsync(GatewayInitiateRequest request, CancellationToken cancellationToken) => Task.FromResult<Result<GatewayInitiation>>(NotConfigured(displayName));

    public Task<Result<GatewayVerification>> VerifyAsync(string providerReference, CancellationToken cancellationToken) => Task.FromResult<Result<GatewayVerification>>(NotConfigured(displayName));

    public Task<Result<GatewayRefundOutcome>> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken) => Task.FromResult<Result<GatewayRefundOutcome>>(NotConfigured(displayName));

    public Result<GatewayWebhookEvent> ParseWebhook(string body, IReadOnlyDictionary<string, string> headers, DateTimeOffset now) => NotConfigured(displayName);
}

/// <summary>
/// The signature scheme of Juvo's own webhook format: header <c>Juvo-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex HMAC-SHA256 of "t.body"&gt;</c>.
/// The signature is compared in constant time and a timestamp outside the tolerance is refused (replay window).
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "Juvo-Signature";
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, DateTimeOffset at, string body)
    {
        var t = at.ToUnixTimeSeconds();
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        return $"t={t},v1={Convert.ToHexString(mac).ToLowerInvariant()}";
    }

    /// <summary>Null when valid; otherwise the error to answer with.</summary>
    public static Error? Check(string secret, string? header, string body, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(secret) || string.IsNullOrWhiteSpace(header))
        {
            return Error.Unauthorized(ErrorCodes.Payment.InvalidSignature, "The webhook signature is missing or invalid.");
        }

        long? t = null;
        string? v1 = null;
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            if (kv[0].Trim() == "t" && long.TryParse(kv[1], out var parsed))
            {
                t = parsed;
            }
            else if (kv[0].Trim() == "v1")
            {
                v1 = kv[1].Trim();
            }
        }

        if (t is null || v1 is null)
        {
            return Error.Unauthorized(ErrorCodes.Payment.InvalidSignature, "The webhook signature is missing or invalid.");
        }

        var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{body}"));
        byte[] given;
        try
        {
            given = Convert.FromHexString(v1);
        }
        catch (FormatException)
        {
            return Error.Unauthorized(ErrorCodes.Payment.InvalidSignature, "The webhook signature is missing or invalid.");
        }

        if (!CryptographicOperations.FixedTimeEquals(expected, given))
        {
            return Error.Unauthorized(ErrorCodes.Payment.InvalidSignature, "The webhook signature is missing or invalid.");
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(t.Value);
        return age.Duration() > Tolerance
            ? new Error(ErrorCodes.Payment.WebhookExpired, "The webhook is too old (or from the future). Send a fresh one.", ErrorType.Validation)
            : null;
    }
}
