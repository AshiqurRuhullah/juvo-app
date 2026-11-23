using Juvo.Application.Couriers;
using Juvo.Application.Monitoring;
using Juvo.Shared.Results;
using Microsoft.Extensions.Options;

namespace Juvo.Application.Tests.Monitoring;

public class MonitoringTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly Clock Time = new(new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero));

    private static RequestMonitor Monitor(int requests = 500, int errors = 100, int slowMs = 1000) =>
        new(Options.Create(new MonitoringOptions { RequestBufferSize = requests, ErrorBufferSize = errors, SlowRequestMilliseconds = slowMs }), Time);

    // ---- Scrubbing -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("Call failed: api_key=demo_ABCDEF1234567890 rejected", "api_key=[hidden]")]
    [InlineData("HTTP 401 with Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.payload.sig", "Authorization: [hidden]")]
    [InlineData("secret: \"topsecretvalue\" is wrong", "secret: [hidden]")]
    [InlineData("password=hunter2&user=bob", "password=[hidden]")]
    public void Scrub_hides_keys_tokens_and_passwords(string input, string expectedFragment)
    {
        var text = ErrorScrubber.Scrub(input)!;
        Assert.Contains(expectedFragment, text);
        Assert.DoesNotContain("ABCDEF1234567890", text);
        Assert.DoesNotContain("eyJhbGci", text);
        Assert.DoesNotContain("topsecretvalue", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void Scrub_hides_phone_numbers_and_emails_but_keeps_guids_and_short_numbers()
    {
        var id = Guid.CreateVersion7();
        var text = ErrorScrubber.Scrub($"SMS to +8801712345678 and 01812345678 for nadia@example.com failed on message {id} after 3 tries")!;

        Assert.DoesNotContain("1712345678", text);
        Assert.DoesNotContain("1812345678", text);
        Assert.DoesNotContain("nadia@example.com", text);
        Assert.Contains(id.ToString(), text);
        Assert.Contains("after 3 tries", text);
    }

    [Fact]
    public void Scrub_keeps_only_the_first_line_and_never_a_stack_trace()
    {
        var text = ErrorScrubber.Scrub("Boom happened\n   at Juvo.Application.Foo.Bar() in C:\\src\\Foo.cs:line 12\n   at System.Threading.Tasks.Task.Run()")!;
        Assert.Equal("Boom happened", text);

        Assert.Equal("Timed out", ErrorScrubber.Scrub("Timed out at Juvo.Infrastructure.Thing.Run()"));
        Assert.Null(ErrorScrubber.Scrub("   "));
        Assert.Null(ErrorScrubber.Scrub(null));
    }

    [Fact]
    public void Scrub_truncates_long_text()
    {
        var text = ErrorScrubber.Scrub(new string('x', 5) + " " + string.Join(' ', Enumerable.Repeat("word", 200)))!;
        Assert.True(text.Length <= ErrorScrubber.DefaultMaxLength);
        Assert.EndsWith("…", text);
    }

    // ---- Status rules ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false, 0, IntegrationStatus.Disabled)]
    [InlineData(false, true, 5, IntegrationStatus.Disabled)]
    [InlineData(true, true, 0, IntegrationStatus.Sandbox)]
    [InlineData(true, true, 9, IntegrationStatus.Sandbox)]
    [InlineData(true, false, 0, IntegrationStatus.Healthy)]
    [InlineData(true, false, 1, IntegrationStatus.Degraded)]
    [InlineData(true, false, 2, IntegrationStatus.Degraded)]
    [InlineData(true, false, 3, IntegrationStatus.Down)]
    [InlineData(true, false, 10, IntegrationStatus.Down)]
    public void Status_is_derived_from_flags_and_failures_in_a_row(bool enabled, bool sandbox, int failures, IntegrationStatus expected) =>
        Assert.Equal(expected, IntegrationStatusRules.Derive(enabled, sandbox, failures));

    [Fact]
    public void The_recorder_counts_failures_in_a_row_and_a_success_resets_them_and_scrubs_the_message()
    {
        var recorder = new IntegrationHealthRecorder(Time);
        Assert.Null(recorder.Get("courier:PATHAO"));

        recorder.Failure("courier:PATHAO", "401 token=abcdef0123456789abcdef0123456789abcd rejected");
        recorder.Failure("courier:PATHAO", "timeout");
        var failed = recorder.Get("courier:PATHAO")!;
        Assert.Equal(2, failed.ConsecutiveFailures);
        Assert.Equal("timeout", failed.LastError);
        Assert.Null(failed.LastSuccessAt);

        Time.Now = Time.Now.AddMinutes(1);
        recorder.Success("courier:PATHAO");
        var healed = recorder.Get("courier:PATHAO")!;
        Assert.Equal(0, healed.ConsecutiveFailures);
        Assert.Equal(Time.Now, healed.LastSuccessAt);
        Assert.Equal(2, healed.Failures);
        Assert.Equal(1, healed.Successes);

        recorder.Failure("sms", "token=abcdef0123456789abcdef0123456789abcd");
        Assert.DoesNotContain("abcdef0123456789", recorder.Get("sms")!.LastError);
    }

    // ---- Decorator -------------------------------------------------------------------------------------

    private sealed class FakeCourier(Func<Result<CourierQuote>> quote) : ICourierProvider, ISandboxAdapter
    {
        public string Code => "FAKE";

        public string DisplayName => "Fake";

        public bool IsConfigured => true;

        public Task<Result<CourierQuote>> QuoteAsync(CourierQuoteRequest request, CancellationToken cancellationToken) => Task.FromResult(quote());

        public Task<Result<CourierBooking>> BookAsync(CourierBookingRequest request, CancellationToken cancellationToken) => throw new InvalidOperationException("provider exploded");

        public Task<Result> CancelAsync(string consignmentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<Result<CourierEvent?>> FetchStatusAsync(string consignmentId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Result<CourierEvent> ParseWebhook(string body, IReadOnlyDictionary<string, string> headers, DateTimeOffset now) => Error.Unauthorized("COURIER.INVALID_SIGNATURE", "bad");

        public Task<Result<CourierLabel>> LabelAsync(string consignmentId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private static readonly CourierQuoteRequest Quote = new("BD", null, null, 500, 0m, new Dictionary<string, string>());

    [Fact]
    public async Task The_courier_decorator_records_provider_failures_and_exceptions_but_not_our_own_refusals()
    {
        var recorder = new IntegrationHealthRecorder(Time);
        var ok = true;
        var wrapped = RecordingAdapters.Wrap(new FakeCourier(() => ok ? new CourierQuote(true, null, 1, 2, null) : Error.Failure("COURIER.PROVIDER_ERROR", "down")), recorder);

        await wrapped.QuoteAsync(Quote, CancellationToken.None);
        Assert.Equal(0, recorder.Get("courier:FAKE")!.ConsecutiveFailures);

        ok = false;
        await wrapped.QuoteAsync(Quote, CancellationToken.None);
        Assert.Equal(1, recorder.Get("courier:FAKE")!.ConsecutiveFailures);

        await Assert.ThrowsAsync<InvalidOperationException>(() => wrapped.BookAsync(null!, CancellationToken.None));
        Assert.Equal(2, recorder.Get("courier:FAKE")!.ConsecutiveFailures);
        Assert.Equal("provider exploded", recorder.Get("courier:FAKE")!.LastError);

        // A bad webhook signature is the sender's fault and not recorded; a slot without keys is not a failure either.
        wrapped.ParseWebhook("{}", new Dictionary<string, string>(), Time.Now);
        Assert.Equal(2, recorder.Get("courier:FAKE")!.ConsecutiveFailures);
        var slot = RecordingAdapters.Wrap(new NotConfiguredCourierProvider("DHL", "DHL"), recorder);
        await slot.QuoteAsync(Quote, CancellationToken.None);
        Assert.Null(recorder.Get("courier:DHL"));

        Assert.IsType<FakeCourier>(RecordingAdapters.Unwrap(wrapped));
        Assert.IsAssignableFrom<ISandboxAdapter>(RecordingAdapters.Unwrap(wrapped));
        Assert.Same(wrapped, RecordingAdapters.Wrap(wrapped, recorder));
    }

    // ---- Ring buffer -----------------------------------------------------------------------------------

    [Fact]
    public void The_request_buffer_keeps_only_the_newest_entries_newest_first()
    {
        var monitor = Monitor(requests: 5);
        for (var i = 1; i <= 8; i++)
        {
            monitor.RecordRequest("GET", $"/r{i}", 200, TimeSpan.FromMilliseconds(i));
        }

        var kept = monitor.Requests();
        Assert.Equal(5, kept.Count);
        Assert.Equal(["/r8", "/r7", "/r6", "/r5", "/r4"], kept.Select(r => r.Route).ToArray());
        Assert.Equal(8, monitor.RequestsSeen);
    }

    [Fact]
    public void Slow_requests_are_counted_and_grouped_by_route_template()
    {
        var monitor = Monitor(slowMs: 500);
        monitor.RecordRequest("GET", "/api/v1/orders/{id:guid}", 200, TimeSpan.FromMilliseconds(900));
        monitor.RecordRequest("GET", "/api/v1/orders/{id:guid}", 200, TimeSpan.FromMilliseconds(700));
        monitor.RecordRequest("POST", "/api/v1/checkout", 201, TimeSpan.FromMilliseconds(1500));
        monitor.RecordRequest("GET", "/api/v1/fast", 200, TimeSpan.FromMilliseconds(20));
        monitor.RecordRequest("GET", null, 404, TimeSpan.FromMilliseconds(5));

        Assert.Equal(3, monitor.SlowCount());
        var slowest = monitor.SlowestRoutes();
        Assert.Equal(2, slowest.Count);
        Assert.Equal("/api/v1/checkout", slowest[0].Route);
        Assert.Equal(1500, slowest[0].MaxMs);
        Assert.Equal(2, slowest[1].Count);
        Assert.Equal(800, slowest[1].AverageMs);
        Assert.Contains(monitor.Requests(), r => r.Route == "(no route)");
    }

    [Fact]
    public void Errors_keep_the_exception_type_and_a_scrubbed_single_line_message_and_never_the_stack()
    {
        var monitor = Monitor(errors: 3);
        for (var i = 0; i < 5; i++)
        {
            Exception ex;
            try
            {
                throw new InvalidOperationException($"failed for +880171234567{i} token=abcdef0123456789abcdef0123456789abcd");
            }
            catch (Exception caught)
            {
                ex = caught;
            }

            monitor.RecordError($"corr-{i}", "/api/v1/orders/{id:guid}", ex);
        }

        var errors = monitor.Errors();
        Assert.Equal(3, errors.Count);
        Assert.Equal("corr-4", errors[0].CorrelationId);
        Assert.All(errors, e =>
        {
            Assert.Equal("InvalidOperationException", e.ExceptionType);
            Assert.DoesNotContain("abcdef0123456789", e.Message);
            Assert.DoesNotContain("+8801712345", e.Message);
            Assert.DoesNotContain(" at ", e.Message);
        });
    }

    [Fact]
    public void The_buffer_is_safe_under_concurrent_writers()
    {
        var monitor = Monitor(requests: 50);
        Parallel.For(0, 8, worker =>
        {
            for (var i = 0; i < 500; i++)
            {
                monitor.RecordRequest("GET", "/x", 200, TimeSpan.FromMilliseconds(1));
                monitor.Requests();
            }
        });

        Assert.Equal(4000, monitor.RequestsSeen);
        Assert.Equal(50, monitor.Requests().Count);
    }
}
