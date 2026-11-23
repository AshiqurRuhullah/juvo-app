using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Integrations;
using Juvo.Infrastructure.Integrations;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Platform;

/// <summary>A shop for the public API tests: sandbox sign-in codes, and the webhook worker off (the tests call the dispatcher themselves).</summary>
public sealed class PublicApiFactory : StoreFactory
{
}

/// <summary>
/// Test-only guard that lets a webhook call a listener on this machine (loopback address, plain http). It exists only here: the production guard has no switch for it.
/// Every other rule (private, link-local and metadata ranges) is unchanged.
/// </summary>
internal sealed class LoopbackWebhookTargetGuard : WebhookTargetGuard
{
    protected override bool AllowLoopback => true;

    protected override bool AllowInsecureHttp => true;
}

/// <summary>A shop whose webhooks may call the local test listener.</summary>
public sealed class WebhookFactory : StoreFactory
{

    protected override void ConfigureTestServices(IServiceCollection services) =>
        services.AddSingleton<IWebhookTargetGuard, LoopbackWebhookTargetGuard>();
}

/// <summary>A shop with the production SSRF guard (nothing relaxed).</summary>
public sealed class StrictWebhookFactory : StoreFactory
{
}

public sealed record ReceivedWebhook(string Body, NameValueCollection Headers, JsonElement Json)
{
    public string Event => Headers["Juvo-Event"]!;

    public string DeliveryId => Headers["Juvo-Delivery-Id"]!;

    public string Signature => Headers["Juvo-Signature"]!;
}

/// <summary>A small HTTP server on the loopback address that records what it is sent and answers as told.</summary>
public sealed class WebhookListener : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _stop = new();

    public WebhookListener()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        Url = $"http://127.0.0.1:{port}/hook";
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    public string Url { get; }

    public ConcurrentQueue<ReceivedWebhook> Received { get; } = new();

    /// <summary>The status to answer (and how long to wait first, and an optional redirect target), decided per request number (1 is the first).</summary>
    public Func<int, (int Status, TimeSpan Delay, string? Location)> Behaviour { get; set; } = _ => (200, TimeSpan.Zero, null);

    public int Count => Received.Count;

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = await reader.ReadToEndAsync();
            Received.Enqueue(new ReceivedWebhook(body, context.Request.Headers, JsonDocument.Parse(body).RootElement.Clone()));
            var (status, delay, location) = Behaviour(Received.Count);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _stop.Token);
            }

            context.Response.StatusCode = status;
            if (location is not null)
            {
                context.Response.RedirectLocation = location;
            }

            context.Response.Close();
        }
        catch (Exception)
        {
            // the caller gave up (timeout) or the test is over
            try
            {
                context.Response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Close();
        _stop.Dispose();
    }
}

/// <summary>Create keys and call the public API with them, and read the database directly.</summary>
public sealed class IntegrationsHelper(ApiFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public StoreHelper Store { get; } = new(factory);

    public TestApi Api => Store.Api;

    public async Task<(Guid Id, string Secret, string Prefix)> CreateKeyAsync(string admin, string[] scopes, Guid? branchId = null, int? rateLimit = null, string? name = null, DateTimeOffset? expiresAt = null)
    {
        var json = await TestApi.ExpectAsync(await Api.PostAsync("/api/v1/integrations/api-keys",
            new { name = name ?? StoreHelper.Unique("Key"), scopes, branchId, rateLimitPerMinute = rateLimit, expiresAt }, admin), HttpStatusCode.Created);
        return (json.GetProperty("key").GetProperty("id").GetGuid(), json.GetProperty("secret").GetString()!, json.GetProperty("key").GetProperty("keyPrefix").GetString()!);
    }

    public async Task<HttpResponseMessage> GetWithKeyAsync(string? secret, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (secret is not null)
        {
            request.Headers.Add("X-Api-Key", secret);
        }

        return await Api.Client.SendAsync(request, Ct);
    }

    public async Task<JsonElement> JsonWithKeyAsync(string secret, string url, HttpStatusCode expected = HttpStatusCode.OK) =>
        await TestApi.ExpectAsync(await GetWithKeyAsync(secret, url), expected);

    public async Task<T> WithDbAsync<T>(Func<JuvoDbContext, Task<T>> action)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<JuvoDbContext>());
    }

    public async Task<T> WithServiceAsync<TService, T>(Func<TService, Task<T>> action)
        where TService : notnull
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    /// <summary>Runs the outbox until it is empty, so event handlers have written their webhook deliveries.</summary>
    public async Task ProcessOutboxAsync()
    {
        var processor = factory.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>().OfType<Juvo.Infrastructure.Outbox.OutboxProcessor>().Single();
        while (await processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    /// <summary>Sends everything that is due, once.</summary>
    public Task<int> DispatchAsync() => WithServiceAsync<WebhookDispatcher, int>(d => d.DeliverDueAsync(100, Ct));

    /// <summary>Makes every pending delivery due now (skips the backoff wait).</summary>
    public Task MakeDueAsync() => WithDbAsync(async db =>
        await db.WebhookDeliveries.Where(d => d.Status == Juvo.Domain.Integrations.WebhookDeliveryStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(d => d.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct));

    public async Task<(Guid Id, string Secret)> CreateWebhookAsync(string admin, string url, string[]? events = null, string? name = null)
    {
        var json = await TestApi.ExpectAsync(await Api.PostAsync("/api/v1/integrations/webhooks",
            new { name = name ?? StoreHelper.Unique("Hook"), url, events = events ?? [.. WebhookEvents.All] }, admin), HttpStatusCode.Created);
        return (json.GetProperty("endpoint").GetProperty("id").GetGuid(), json.GetProperty("secret").GetString()!);
    }
}
