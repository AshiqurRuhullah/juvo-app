using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using Juvo.Application.Marketing;
using Juvo.Domain.Notifications;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Identity;
using Juvo.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Juvo.Infrastructure.Outbox;

/// <summary>Background jobs of the marketing module. Section: <c>MarketingJobs</c>. Turn them off in tests and migration runs.</summary>
public sealed class MarketingJobOptions
{
    public const string SectionName = "MarketingJobs";

    /// <summary>Start scheduled campaigns and send the batches of campaigns that are sending.</summary>
    public bool CampaignSendEnabled { get; set; } = true;

    [Range(5, 3600)]
    public int CampaignSendIntervalSeconds { get; set; } = 30;

    /// <summary>Run the abandoned cart, back-in-stock, review request and win-back automations (each one also has its own switch, off by default).</summary>
    public bool AutomationsEnabled { get; set; } = true;

    [Range(10, 86400)]
    public int AutomationIntervalSeconds { get; set; } = 120;
}

/// <summary>Signs unsubscribe tokens with HMAC-SHA256. The key is derived from the platform signing key, so it needs no extra secret.</summary>
internal sealed class UnsubscribeTokenService : IUnsubscribeTokens
{
    private const int PayloadLength = 33;
    private const int MacLength = 12;
    private readonly byte[] _key;

    public UnsubscribeTokenService(SigningKeyProvider keys) => _key = HMACSHA256.HashData(keys.Key.Key, "juvo.marketing.unsubscribe.v1"u8);

    private byte[] Mac(ReadOnlySpan<byte> payload) => HMACSHA256.HashData(_key, payload)[..MacLength];

    public string Create(Guid customerId, NotificationChannel channel, Guid? campaignId)
    {
        var payload = new byte[PayloadLength];
        customerId.TryWriteBytes(payload.AsSpan(0, 16));
        payload[16] = (byte)channel;
        (campaignId ?? Guid.Empty).TryWriteBytes(payload.AsSpan(17, 16));
        return Base64Url(payload) + "." + Base64Url(Mac(payload));
    }

    public UnsubscribeClaims? Verify(string? token)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
        {
            return null;
        }

        var parts = token.Trim().Split('.');
        if (parts.Length != 2 || FromBase64Url(parts[0]) is not { Length: PayloadLength } payload || FromBase64Url(parts[1]) is not { } mac)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(mac, Mac(payload)) || !Enum.IsDefined((NotificationChannel)payload[16]) || (NotificationChannel)payload[16] is not (NotificationChannel.Sms or NotificationChannel.Email))
        {
            return null;
        }

        var campaign = new Guid(payload.AsSpan(17, 16));
        return new UnsubscribeClaims(new Guid(payload.AsSpan(0, 16)), (NotificationChannel)payload[16], campaign == Guid.Empty ? null : campaign);
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        try
        {
            return Convert.FromBase64String(s);
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Starts scheduled campaigns and sends batches of campaigns in state Sending (throttled, held back by quiet hours for SMS). Resumable and idempotent (FR-AC02).</summary>
internal sealed class CampaignSendWorker(IServiceScopeFactory scopeFactory, IOptions<MarketingJobOptions> options, TimeProvider timeProvider, ILogger<CampaignSendWorker> logger) : BackgroundService
{
    internal const string JobName = "marketing.campaign-send";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.CampaignSendEnabled)
        {
            logger.LogInformation("The campaign send job is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Campaign send run failed; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.CampaignSendIntervalSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<CampaignSweep> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var started = timeProvider.GetUtcNow();
        var result = await scope.ServiceProvider.GetRequiredService<CampaignDispatchService>().RunDueAsync(cancellationToken);
        if (result.Started > 0 || result.Queued > 0)
        {
            var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
            var job = new JobLog(JobName, $"started:{result.Started} sending:{result.Sending} queued:{result.Queued}", 1, null, started);
            job.Succeed(timeProvider.GetUtcNow());
            db.JobLogs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Campaigns: started {Started}, queued {Queued} message(s).", result.Started, result.Queued);
        }

        return result;
    }
}

/// <summary>Runs the switched-on automations (abandoned cart, back in stock, review request, win-back). Idempotent per customer and trigger (FR-AC03).</summary>
internal sealed class AutomationWorker(IServiceScopeFactory scopeFactory, IOptions<MarketingJobOptions> options, TimeProvider timeProvider, ILogger<AutomationWorker> logger) : BackgroundService
{
    internal const string JobName = "marketing.automations";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.AutomationsEnabled)
        {
            logger.LogInformation("The automation job is disabled by configuration.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SweepAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Automation run failed; will retry.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(settings.AutomationIntervalSeconds), timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    internal async Task<IReadOnlyList<AutomationPass>> SweepAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var started = timeProvider.GetUtcNow();
        var passes = await scope.ServiceProvider.GetRequiredService<AutomationEngine>().RunAllAsync(cancellationToken);
        var sent = passes.Sum(p => p.Sent);
        if (sent > 0)
        {
            var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
            var job = new JobLog(JobName, string.Join(' ', passes.Where(p => p.Sent > 0).Select(p => $"{p.Kind}:{p.Sent}")), 1, null, started);
            job.Succeed(timeProvider.GetUtcNow());
            db.JobLogs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Automations queued {Count} message(s).", sent);
        }

        return passes;
    }
}
