using System.Diagnostics;
using System.Reflection;
using Juvo.Application.Abstractions;
using Juvo.Application.Couriers;
using Juvo.Application.Notifications;
using Juvo.Application.Orders;
using Juvo.Application.Payments;
using Juvo.Application.Ums;
using Juvo.Domain.Notifications;
using Juvo.Domain.Platform;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Monitoring;

/// <summary>One integration slot. <c>CanTest</c> is true only where a connectivity check is safe (no real provider, no message to a customer).</summary>
public sealed record IntegrationHealthDto(
    string Key, string Kind, string Name, bool Enabled, string Mode, IntegrationStatus Status, DateTimeOffset? LastSuccessAt, DateTimeOffset? LastFailureAt, string? LastError,
    int ConsecutiveFailures, bool CanTest);

public sealed record IntegrationTestResultDto(string Key, bool Ok, string Message, int DurationMs, DateTimeOffset At);

public sealed record SystemStatusDto(
    string Version, DateTimeOffset StartedAt, long UptimeSeconds, bool DatabaseReachable, OutboxSummaryDto Outbox, int FailedJobs24h, int SlowRequests, int SlowThresholdMs,
    IReadOnlyList<SlowRoute> SlowestRoutes, IReadOnlyList<ErrorSample> RecentErrors, long RequestsSeen, int RequestsKept, string Scope);

/// <summary>
/// Integration health and monitoring basics (FR-AF02, FR-AF04). Health is built from the adapters that exist (payment gateways, couriers, SMS, e-mail, WhatsApp) and the outcomes the
/// recording decorators and the notification delivery report to <see cref="IIntegrationHealthRecorder"/>. The recorder and the request buffer live in memory of this process only.
/// </summary>
public sealed class SystemHealthService(
    IApplicationDbContext db, ActorContext actors, IPaymentGatewayRegistry gateways, ICourierProviderRegistry couriers, IEnumerable<INotificationSender> senders, SettingsService settings,
    IIntegrationHealthRecorder recorder, RequestMonitor monitor, TimeProvider clock)
{
    private static readonly NotificationChannel[] Channels = [NotificationChannel.Sms, NotificationChannel.Email, NotificationChannel.WhatsApp];

    public static string ChannelKey(NotificationChannel channel) => channel.ToString().ToLowerInvariant();

    private async Task<Error?> RequireAsync(CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        return actor.Access.HasAnywhere(Permissions.SystemOps.HealthView) ? null : Error.Forbidden();
    }

    private IntegrationHealthDto Row(string key, string kind, string name, bool enabled, bool sandbox, bool canTest)
    {
        var state = recorder.Get(key);
        var failures = state?.ConsecutiveFailures ?? 0;
        return new IntegrationHealthDto(key, kind, name, enabled, sandbox ? "Sandbox" : "Live", IntegrationStatusRules.Derive(enabled, sandbox, failures),
            state?.LastSuccessAt, state?.LastFailureAt, state?.LastError, failures, canTest);
    }

    private static bool IsSandbox(object adapter) => RecordingAdapters.Unwrap(adapter) is ISandboxAdapter;

    private async Task<List<IntegrationHealthDto>> RowsAsync(CancellationToken cancellationToken)
    {
        var rows = new List<IntegrationHealthDto>();
        foreach (var g in gateways.All.OrderBy(g => g.DisplayName, StringComparer.Ordinal))
        {
            rows.Add(Row(RecordingPaymentGateway.KeyOf(g.Code), "payment", g.DisplayName, g.IsConfigured, IsSandbox(g), false));
        }

        foreach (var c in couriers.All.OrderBy(c => c.DisplayName, StringComparer.Ordinal))
        {
            rows.Add(Row(RecordingCourierProvider.KeyOf(c.Code), "courier", c.DisplayName, c.IsConfigured, IsSandbox(c), c.IsConfigured && IsSandbox(c)));
        }

        var values = await settings.ReadAsync([NotificationSettingKeys.SmsEnabled, NotificationSettingKeys.EmailEnabled, NotificationSettingKeys.WhatsAppEnabled], cancellationToken);
        bool On(string key, bool fallback) => values.TryGetValue(key, out var raw) && bool.TryParse(raw, out var on) ? on : fallback;
        foreach (var channel in Channels)
        {
            var sender = senders.LastOrDefault(s => s.Channel == channel);
            var enabled = sender is not null && channel switch
            {
                NotificationChannel.Sms => On(NotificationSettingKeys.SmsEnabled, true),
                NotificationChannel.Email => On(NotificationSettingKeys.EmailEnabled, true),
                _ => On(NotificationSettingKeys.WhatsAppEnabled, false),
            };
            rows.Add(Row(ChannelKey(channel), ChannelKey(channel), channel == NotificationChannel.Sms ? "SMS" : channel == NotificationChannel.Email ? "E-mail" : "WhatsApp", enabled, sender is null || IsSandbox(sender), false));
        }

        return rows;
    }

    public async Task<Result<IReadOnlyList<IntegrationHealthDto>>> IntegrationsAsync(CancellationToken cancellationToken)
    {
        if (await RequireAsync(cancellationToken) is { } refusal)
        {
            return refusal;
        }

        return Result.Success<IReadOnlyList<IntegrationHealthDto>>(await RowsAsync(cancellationToken));
    }

    /// <summary>Runs a safe check. Only a sandbox courier offers one (a quote, which reads nothing and writes nothing); every other slot answers 409 <c>SYSTEM.NOT_SUPPORTED</c>.</summary>
    public async Task<Result<IntegrationTestResultDto>> TestAsync(string key, CancellationToken cancellationToken)
    {
        if (await RequireAsync(cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var row = (await RowsAsync(cancellationToken)).FirstOrDefault(r => string.Equals(r.Key, key, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return Error.NotFound(ErrorCodes.Platform.UnknownIntegration, "There is no integration with this key.");
        }

        if (!row.CanTest || !key.StartsWith("courier:", StringComparison.OrdinalIgnoreCase) || couriers.Find(key["courier:".Length..]) is not { } courier)
        {
            return Error.Conflict(ErrorCodes.Platform.NotSupported, "This integration has no safe connectivity check. It is checked by the calls the shop makes to it.");
        }

        var started = Stopwatch.GetTimestamp();
        var outcome = await courier.QuoteAsync(new CourierQuoteRequest("BD", null, null, 500, 0m, new Dictionary<string, string>()), cancellationToken);
        var ms = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return new IntegrationTestResultDto(row.Key, outcome.IsSuccess, outcome.IsSuccess ? "The courier answered a price check." : ErrorScrubber.Scrub(outcome.Error?.Message) ?? "The check failed.", ms, clock.GetUtcNow());
    }

    public async Task<Result<SystemStatusDto>> StatusAsync(CancellationToken cancellationToken)
    {
        if (await RequireAsync(cancellationToken) is { } refusal)
        {
            return refusal;
        }

        var now = clock.GetUtcNow();
        bool reachable;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            await db.JobLogs.AsNoTracking().AnyAsync(timeout.Token);
            reachable = true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            reachable = false;
        }

        OutboxSummaryDto outbox = new(0, 0, 0, null, null);
        var failed24h = 0;
        if (reachable)
        {
            outbox = await SystemJobsService.OutboxSummaryRowAsync(db, now, cancellationToken);
            var since = now.AddHours(-24);
            failed24h = await db.JobLogs.AsNoTracking().CountAsync(j => j.StartedAt >= since && j.Status == JobStatus.Failed, cancellationToken);
        }

        var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
        var version = typeof(SystemHealthService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";
        var kept = monitor.Requests();
        return new SystemStatusDto(
            version, new DateTimeOffset(started, TimeSpan.Zero), (long)Math.Max(0, (now.UtcDateTime - started).TotalSeconds), reachable, outbox, failed24h, monitor.SlowCount(), monitor.SlowThresholdMs,
            monitor.SlowestRoutes(), monitor.Errors(), monitor.RequestsSeen, kept.Count, "process: the request and error figures are kept in memory of this server and start again at zero after a restart");
    }
}
