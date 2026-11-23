using Juvo.Application.Couriers;
using Juvo.Application.Payments;
using Juvo.Shared.Results;

namespace Juvo.Application.Monitoring;

/// <summary>Wraps a courier adapter and reports the outcome of each provider call to the integration health. Webhook parsing is not recorded (a bad signature is the sender's fault).</summary>
public class RecordingCourierProvider(ICourierProvider inner, IIntegrationHealthRecorder recorder) : ICourierProvider, IWrappedAdapter
{
    public static string KeyOf(string code) => $"courier:{code}";

    public object Inner => inner;

    private string Key => KeyOf(inner.Code);

    public string Code => inner.Code;

    public string DisplayName => inner.DisplayName;

    public bool IsConfigured => inner.IsConfigured;

    public Task<Result<CourierQuote>> QuoteAsync(CourierQuoteRequest request, CancellationToken cancellationToken) =>
        IntegrationCall.TrackAsync(recorder, Key, () => inner.QuoteAsync(request, cancellationToken));

    public Task<Result<CourierBooking>> BookAsync(CourierBookingRequest request, CancellationToken cancellationToken) =>
        IntegrationCall.TrackAsync(recorder, Key, () => inner.BookAsync(request, cancellationToken));

    public Task<Result> CancelAsync(string consignmentId, CancellationToken cancellationToken) =>
        IntegrationCall.TrackAsync(recorder, Key, () => inner.CancelAsync(consignmentId, cancellationToken));

    public Task<Result<CourierEvent?>> FetchStatusAsync(string consignmentId, CancellationToken cancellationToken) =>
        IntegrationCall.TrackAsync(recorder, Key, () => inner.FetchStatusAsync(consignmentId, cancellationToken));

    public Result<CourierEvent> ParseWebhook(string body, IReadOnlyDictionary<string, string> headers, DateTimeOffset now) => inner.ParseWebhook(body, headers, now);

    public Task<Result<CourierLabel>> LabelAsync(string consignmentId, CancellationToken cancellationToken) =>
        IntegrationCall.TrackAsync(recorder, Key, () => inner.LabelAsync(consignmentId, cancellationToken));
}

/// <summary>The same decorator for an adapter that is also a simulator, so <c>provider is ICourierSimulator</c> keeps working.</summary>
public sealed class RecordingSimulatingCourierProvider(ICourierProvider inner, IIntegrationHealthRecorder recorder) : RecordingCourierProvider(inner, recorder), ICourierSimulator
{
    private readonly ICourierSimulator _simulator = (ICourierSimulator)inner;

    public IReadOnlyList<string> RawStatuses => _simulator.RawStatuses;

    public Task<string?> NextStatusAsync(string consignmentId, CancellationToken cancellationToken) => _simulator.NextStatusAsync(consignmentId, cancellationToken);

    public Task<Result<CourierEvent>> SimulateAsync(string consignmentId, string rawStatus, string? note, CancellationToken cancellationToken) =>
        _simulator.SimulateAsync(consignmentId, rawStatus, note, cancellationToken);
}

/// <summary>Wraps a payment gateway adapter and reports the outcome of each provider call to the integration health.</summary>
public sealed class RecordingPaymentGateway(IPaymentGateway inner, IIntegrationHealthRecorder recorder) : IPaymentGateway, IWrappedAdapter
{
    public static string KeyOf(string code) => $"payment:{code}";

    public object Inner => inner;

    private string Key => KeyOf(inner.Code);

    public string Code => inner.Code;

    public string DisplayName => inner.DisplayName;

    public bool IsConfigured => inner.IsConfigured;

    public bool SupportsRefund => inner.SupportsRefund;

    public Task<Result<GatewayInitiation>> InitiateAsync(GatewayInitiateRequest request, CancellationToken cancellationToken) =>
        IntegrationCall.TrackAsync(recorder, Key, () => inner.InitiateAsync(request, cancellationToken));

    public Task<Result<GatewayVerification>> VerifyAsync(string providerReference, CancellationToken cancellationToken) =>
        IntegrationCall.TrackAsync(recorder, Key, () => inner.VerifyAsync(providerReference, cancellationToken));

    public Task<Result<GatewayRefundOutcome>> RefundAsync(GatewayRefundRequest request, CancellationToken cancellationToken) =>
        IntegrationCall.TrackAsync(recorder, Key, () => inner.RefundAsync(request, cancellationToken));

    public Result<GatewayWebhookEvent> ParseWebhook(string body, IReadOnlyDictionary<string, string> headers, DateTimeOffset now) => inner.ParseWebhook(body, headers, now);
}

public static class RecordingAdapters
{
    public static ICourierProvider Wrap(ICourierProvider provider, IIntegrationHealthRecorder recorder) =>
        provider is IWrappedAdapter ? provider
        : provider is ICourierSimulator ? new RecordingSimulatingCourierProvider(provider, recorder)
        : new RecordingCourierProvider(provider, recorder);

    public static IPaymentGateway Wrap(IPaymentGateway gateway, IIntegrationHealthRecorder recorder) =>
        gateway is IWrappedAdapter ? gateway : new RecordingPaymentGateway(gateway, recorder);

    /// <summary>The adapter underneath any recording decorators.</summary>
    public static object Unwrap(object adapter)
    {
        while (adapter is IWrappedAdapter wrapped)
        {
            adapter = wrapped.Inner;
        }

        return adapter;
    }
}
