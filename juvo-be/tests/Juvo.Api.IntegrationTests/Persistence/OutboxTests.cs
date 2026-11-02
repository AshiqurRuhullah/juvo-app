using System.Collections.Concurrent;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Abstractions;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Outbox;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;

namespace Juvo.Api.IntegrationTests.Persistence;

public sealed class OutboxFactory : ApiFactory
{
    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.Zero));

    public RecordingHandler Recorder { get; } = new("test.ok");

    protected override void ConfigureSettings()
    {
        Setting("Outbox:BatchSize", "5");
        Setting("Outbox:MaxAttempts", "3");
        Setting("Outbox:BaseDelaySeconds", "5");
        Setting("Outbox:MaxDelayMinutes", "60");
    }

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.AddSingleton<TimeProvider>(Time);
        services.AddSingleton<IOutboxMessageHandler>(Recorder);
        services.AddSingleton<IOutboxMessageHandler>(new FailingHandler("test.fail"));
        services.AddScoped<IOutboxMessageHandler, LeakyHandler>();
    }
}

public sealed class RecordingHandler(string messageType) : IOutboxMessageHandler
{
    public ConcurrentBag<Guid> Handled { get; } = [];

    public string MessageType => messageType;

    public Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        Handled.Add(context.MessageId);
        return Task.CompletedTask;
    }
}

public sealed class FailingHandler(string messageType) : IOutboxMessageHandler
{
    public string MessageType => messageType;

    public Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("provider is down");
}

/// <summary>Adds a row to its own DbContext and throws without saving: nothing may leak into the claim transaction.</summary>
public sealed class LeakyHandler(JuvoDbContext db) : IOutboxMessageHandler
{
    public string MessageType => "test.leaky";

    public Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        db.JobLogs.Add(new JobLog("leaked", null, 1, null, DateTimeOffset.UtcNow));
        throw new InvalidOperationException("handler failed after changing state");
    }
}

public class OutboxTests(OutboxFactory factory) : IClassFixture<OutboxFactory>, IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Every test starts with an empty queue so leftovers from other tests cannot change the counts.</summary>
    public async ValueTask InitializeAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        await db.OutboxMessages.ExecuteDeleteAsync(Ct);
        await db.JobLogs.ExecuteDeleteAsync(Ct);
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private OutboxProcessor Processor() =>
        factory.Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();

    private async Task<Guid> EnqueueAsync(string type, string payload = "{}")
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        var message = new OutboxMessage(type, payload, factory.Time.GetUtcNow(), "corr");
        db.OutboxMessages.Add(message);
        await db.SaveChangesAsync(Ct);
        return message.Id;
    }

    private async Task<(OutboxMessage Message, List<JobLog> Jobs)> LoadAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        var message = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id, Ct);
        var jobs = await db.JobLogs.AsNoTracking().Where(j => j.Reference == id.ToString()).OrderBy(j => j.Attempt).ToListAsync(Ct);
        return (message, jobs);
    }

    [Fact]
    public async Task Pending_message_is_delivered_once_and_logged()
    {
        var id = await EnqueueAsync("test.ok", """{"orderNo":"JV-HO-2611-000001"}""");

        await Processor().ProcessBatchAsync(Ct);

        var (message, jobs) = await LoadAsync(id);
        Assert.Equal(OutboxStatus.Succeeded, message.Status);
        Assert.NotNull(message.ProcessedAt);
        Assert.Contains(id, factory.Recorder.Handled);
        var job = Assert.Single(jobs);
        Assert.Equal(JobStatus.Succeeded, job.Status);
        Assert.Equal("outbox.deliver", job.JobName);

        // A second pass must not deliver it again.
        await Processor().ProcessBatchAsync(Ct);
        Assert.Equal(1, factory.Recorder.Handled.Count(h => h == id));
    }

    [Fact]
    public async Task Failing_message_retries_with_exponential_backoff_then_fails_for_good()
    {
        var id = await EnqueueAsync("test.fail");

        await Processor().ProcessBatchAsync(Ct);
        var (afterFirst, jobsFirst) = await LoadAsync(id);
        Assert.Equal(OutboxStatus.Pending, afterFirst.Status);
        Assert.Equal(1, afterFirst.AttemptCount);
        Assert.Equal("provider is down", afterFirst.LastError);
        Assert.Equal(factory.Time.GetUtcNow().AddSeconds(5), afterFirst.NextAttemptAt);
        Assert.Equal(JobStatus.Failed, Assert.Single(jobsFirst).Status);

        // Not due yet: nothing is processed.
        Assert.Equal(0, await Processor().ProcessBatchAsync(Ct));

        factory.Time.Advance(TimeSpan.FromSeconds(6));
        await Processor().ProcessBatchAsync(Ct);
        var (afterSecond, _) = await LoadAsync(id);
        Assert.Equal(2, afterSecond.AttemptCount);
        Assert.Equal(factory.Time.GetUtcNow().AddSeconds(10), afterSecond.NextAttemptAt);

        factory.Time.Advance(TimeSpan.FromSeconds(11));
        await Processor().ProcessBatchAsync(Ct);
        var (final, jobs) = await LoadAsync(id);
        Assert.Equal(OutboxStatus.Failed, final.Status);
        Assert.Equal(3, final.AttemptCount);
        Assert.Equal(3, jobs.Count);
    }

    [Fact]
    public async Task Failed_message_can_be_requeued_manually()
    {
        var id = await EnqueueAsync("test.fail");
        for (var i = 0; i < 3; i++)
        {
            await Processor().ProcessBatchAsync(Ct);
            factory.Time.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Equal(OutboxStatus.Failed, (await LoadAsync(id)).Message.Status);

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
            var message = await db.OutboxMessages.SingleAsync(m => m.Id == id, Ct);
            message.Requeue(factory.Time.GetUtcNow());
            await db.SaveChangesAsync(Ct);
        }

        Assert.Equal(OutboxStatus.Pending, (await LoadAsync(id)).Message.Status);
    }

    [Fact]
    public async Task Message_type_without_a_handler_fails_with_a_clear_error()
    {
        var id = await EnqueueAsync("nobody.listens");

        await Processor().ProcessBatchAsync(Ct);

        var (message, _) = await LoadAsync(id);
        Assert.Equal(1, message.AttemptCount);
        Assert.Contains("No outbox handler", message.LastError, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Handler_runs_in_its_own_scope_so_a_failure_leaks_no_changes()
    {
        var id = await EnqueueAsync("test.leaky");

        await Processor().ProcessBatchAsync(Ct);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        Assert.False(await db.JobLogs.AnyAsync(j => j.JobName == "leaked", Ct));
        Assert.Equal(1, (await LoadAsync(id)).Message.AttemptCount);
    }

    [Fact]
    public async Task Competing_workers_never_deliver_the_same_message_twice()
    {
        const int count = 40;
        var ids = new List<Guid>();
        for (var i = 0; i < count; i++)
        {
            ids.Add(await EnqueueAsync("test.ok"));
        }

        async Task Worker()
        {
            while (await Processor().ProcessBatchAsync(Ct) > 0)
            {
            }
        }

        await Task.WhenAll(Worker(), Worker(), Worker(), Worker());

        var delivered = factory.Recorder.Handled.Where(ids.Contains).ToList();
        Assert.Equal(count, delivered.Count);
        Assert.Equal(count, delivered.Distinct().Count());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        Assert.Equal(count, await db.OutboxMessages.CountAsync(m => ids.Contains(m.Id) && m.Status == OutboxStatus.Succeeded, Ct));
    }
}
