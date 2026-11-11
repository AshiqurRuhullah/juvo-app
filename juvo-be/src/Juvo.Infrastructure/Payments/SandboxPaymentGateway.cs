using System.Security.Cryptography;
using System.Text.Json;
using Juvo.Application.Payments;
using Juvo.Domain.Payments;
using Juvo.Infrastructure.Persistence;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Payments;

/// <summary>Settings of the sandbox gateway. Section: <c>Payments:Sandbox</c>. It is on by default; switch it off in production once a real provider is configured (Step 14).</summary>
public sealed class SandboxGatewayOptions
{
    public const string SectionName = "Payments:Sandbox";

    public bool Enabled { get; set; } = true;

    /// <summary>Signs the sandbox webhooks. Empty: a random secret is made for the life of the process (enough, because the sandbox calls the shop's own webhook itself). Set it to test the endpoint from outside.</summary>
    public string? WebhookSecret { get; set; }

    /// <summary>The pay page of the front end, with <c>{session}</c> in it (for example <c>http://localhost:3000/pay/sandbox/{session}</c>). Empty: the API's own sandbox page is used.</summary>
    public string? PayPageUrl { get; set; }

    /// <summary>Origin of the API for the built-in sandbox page link (for example <c>http://localhost:5080</c>). Empty: a path starting with <c>/api/v1/...</c>.</summary>
    public string? PublicApiBaseUrl { get; set; }

    /// <summary>What a gateway refund answers: <c>Completed</c> (default), <c>Pending</c> (finishes later; staff complete it) or <c>Failed</c>. For trying the refund flows.</summary>
    public string RefundOutcome { get; set; } = "Completed";
}

/// <summary>
/// The sandbox payment gateway (module I): a hosted-page style provider that needs no keys. Initiate makes a session and returns the sandbox pay page; the customer picks success,
/// failure or cancel there; the sandbox then sends the shop a signed webhook (the same signature scheme a real provider uses) and answers server-side verification from its session.
/// </summary>
internal sealed class SandboxPaymentGateway(JuvoDbContext db, IOptions<SandboxGatewayOptions> options) : IPaymentGateway
{
    private static readonly string ProcessSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public const string EventSucceeded = "payment.succeeded";
    public const string EventFailed = "payment.failed";
    public const string EventCancelled = "payment.cancelled";

    public string Code => PaymentMethodCatalog.Sandbox;

    public string DisplayName => "Sandbox gateway";

    public bool IsConfigured => options.Value.Enabled;

    public bool SupportsRefund => true;

    public static string SecretOf(SandboxGatewayOptions o) => string.IsNullOrWhiteSpace(o.WebhookSecret) ? ProcessSecret : o.WebhookSecret;

    public Task<Result<GatewayInitiation>> InitiateAsync(GatewayInitiateRequest request, CancellationToken cancellationToken)
    {
        var sessionId = "sbx_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_').TrimEnd('=');
        db.SandboxGatewaySessions.Add(new SandboxGatewaySession(sessionId, request.PaymentId, request.OrderNumber, request.Amount, request.Currency, request.ExpiresAt)); // saved with the payment
        var o = options.Value;
        var url = !string.IsNullOrWhiteSpace(o.PayPageUrl)
            ? o.PayPageUrl.Replace("{session}", sessionId, StringComparison.Ordinal)
            : $"{(o.PublicApiBaseUrl ?? string.Empty).TrimEnd('/')}/api/v1/store/payments/sandbox/pay/{sessionId}";
        return Task.FromResult<Result<GatewayInitiation>>(new GatewayInitiation(sessionId, url));
    }

    public async Task<Result<GatewayVerification>> VerifyAsync(string providerReference, CancellationToken cancellationToken)
    {
        var session = await db.SandboxGatewaySessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == providerReference, cancellationToken);
        if (session is null)
        {
            return Error.Failure(ErrorCodes.Payment.GatewayError, "The sandbox gateway does not know this session.");
        }

        return session.Outcome switch
        {
            1 => new GatewayVerification(GatewayOutcome.Succeeded, "SBX-" + session.SessionId[4..], session.Amount, session.Currency, "Paid on the sandbox page"),
            2 => new GatewayVerification(GatewayOutcome.Failed, null, session.Amount, session.Currency, "The payment failed on the sandbox page"),
            3 => new GatewayVerification(GatewayOutcome.Cancelled, null, session.Amount, session.Currency, "The customer cancelled on the sandbox page"),
            _ => new GatewayVerification(GatewayOutcome.Pending, null, session.Amount, session.Currency, "Waiting for the customer"),
        };
    }

    public Task<Result<GatewayRefundOutcome>> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken)
    {
        var id = "SBX-RF-" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
        var outcome = options.Value.RefundOutcome?.Trim().ToLowerInvariant() switch
        {
            "pending" => new GatewayRefundOutcome(GatewayRefundKind.Pending, id, "The sandbox accepted the refund and finishes later"),
            "failed" => new GatewayRefundOutcome(GatewayRefundKind.Failed, null, "The sandbox refused the refund"),
            _ => new GatewayRefundOutcome(GatewayRefundKind.Completed, id, "Refunded by the sandbox"),
        };
        return Task.FromResult<Result<GatewayRefundOutcome>>(outcome);
    }

    public Result<GatewayWebhookEvent> ParseWebhook(string body, IReadOnlyDictionary<string, string> headers, DateTimeOffset now)
    {
        headers.TryGetValue(WebhookSignature.HeaderName, out var signature);
        if (WebhookSignature.Check(SecretOf(options.Value), signature, body, now) is { } bad)
        {
            return bad;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var id = root.GetProperty("id").GetString();
            var type = root.GetProperty("type").GetString();
            var session = root.GetProperty("sessionId").GetString();
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(session))
            {
                throw new FormatException("A required field is empty.");
            }

            var kind = type switch
            {
                EventSucceeded => GatewayEventKind.PaymentSucceeded,
                EventFailed => GatewayEventKind.PaymentFailed,
                EventCancelled => GatewayEventKind.PaymentCancelled,
                "refund.completed" => GatewayEventKind.RefundCompleted,
                _ => GatewayEventKind.Other,
            };
            decimal? amount = root.TryGetProperty("amount", out var a) && a.ValueKind == JsonValueKind.Number ? a.GetDecimal() : null;
            var currency = root.TryGetProperty("currency", out var c) ? c.GetString() : null;
            var created = root.TryGetProperty("created", out var t) && t.ValueKind == JsonValueKind.Number ? DateTimeOffset.FromUnixTimeSeconds(t.GetInt64()) : now;
            return new GatewayWebhookEvent(id, session, kind, amount, currency, created);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            return new Error(ErrorCodes.Payment.WebhookMalformed, "The webhook body is not a valid event.", ErrorType.Validation);
        }
    }
}

/// <summary>
/// What the sandbox pay page does when the customer decides. It records the decision on the gateway session and, like a real provider, delivers a signed webhook to the shop's own
/// webhook processing. Only exists for the sandbox adapter.
/// </summary>
public sealed class SandboxPayService(JuvoDbContext db, IOptions<SandboxGatewayOptions> options, PaymentWebhookService webhooks, TimeProvider clock)
{
    private static Error NotFound() => Error.NotFound(ErrorCodes.Payment.NotFound, "This sandbox payment page does not exist.");

    private static string StatusOf(SandboxGatewaySession s, DateTimeOffset now) => s.Outcome switch
    {
        1 => "Succeeded",
        2 => "Failed",
        3 => "Cancelled",
        _ => s.ExpiresAt <= now ? "Expired" : "Pending",
    };

    public async Task<Result<SandboxSessionDto>> GetAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return NotFound();
        }

        var session = await db.SandboxGatewaySessions.AsNoTracking().FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);
        if (session is null)
        {
            return NotFound();
        }

        var returnUrl = await db.Payments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(p => p.Id == session.PaymentId).Select(p => p.ReturnUrl).FirstOrDefaultAsync(cancellationToken);
        return new SandboxSessionDto(session.SessionId, session.OrderNumber, session.Amount, session.Currency, StatusOf(session, clock.GetUtcNow()), session.ExpiresAt, returnUrl);
    }

    public async Task<Result<SandboxDecisionDto>> DecideAsync(string sessionId, SandboxDecisionRequest request, CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled)
        {
            return NotFound();
        }

        var (code, type) = request.Outcome?.Trim().ToLowerInvariant() switch
        {
            "success" => (1, SandboxPaymentGateway.EventSucceeded),
            "fail" => (2, SandboxPaymentGateway.EventFailed),
            "cancel" => (3, SandboxPaymentGateway.EventCancelled),
            _ => (0, string.Empty),
        };
        if (code == 0)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["outcome"] = ["Use success, fail or cancel."] });
        }

        var now = clock.GetUtcNow();
        var session = await db.SandboxGatewaySessions.FirstOrDefaultAsync(s => s.SessionId == sessionId, cancellationToken);
        if (session is null)
        {
            return NotFound();
        }

        if (session.ExpiresAt <= now)
        {
            return Error.Conflict(ErrorCodes.Payment.OrderNotPayable, "This payment page has expired.");
        }

        if (!session.Decide(code, now))
        {
            return Error.Conflict(ErrorCodes.Payment.InvalidTransition, "This payment page was already used.");
        }

        await db.SaveChangesAsync(cancellationToken);
        if (request.SendWebhook != false)
        {
            var body = JsonSerializer.Serialize(new
            {
                id = "evt_" + Guid.NewGuid().ToString("N"),
                type,
                sessionId = session.SessionId,
                amount = session.Amount,
                currency = session.Currency,
                created = now.ToUnixTimeSeconds(),
            });
            var headers = new Dictionary<string, string> { [WebhookSignature.HeaderName] = WebhookSignature.Sign(SandboxPaymentGateway.SecretOf(options.Value), now, body) };
            await webhooks.ProcessAsync(PaymentMethodCatalog.Sandbox, body, headers, cancellationToken);
        }

        var payment = await db.Payments.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(p => p.Id == session.PaymentId).Select(p => new { p.ReturnUrl, p.Status }).FirstAsync(cancellationToken);
        return new SandboxDecisionDto(session.SessionId, request.Outcome!.Trim().ToLowerInvariant(), payment.ReturnUrl, payment.Status);
    }
}
