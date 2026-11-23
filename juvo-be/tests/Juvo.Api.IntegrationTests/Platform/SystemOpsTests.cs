using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Loyalty;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Abstractions;
using Juvo.Application.Monitoring;
using Juvo.Domain.Notifications;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Platform;

/// <summary>Jobs, queues, failed sends, integration health and the status page over the real API and database (FR-AF02, FR-AF04). The outbox worker is off in tests, so queue rows stay as written.</summary>
public class SystemOpsTests(LoyaltyFactory factory) : IClassFixture<LoyaltyFactory>
{
    private const string Jobs = "/api/v1/system/jobs";
    private const string Secret = "demo_ABCDEF0123456789ABCDEF0123456789";

    private readonly LoyaltyHelper _l = new(factory);

    private StoreHelper H => _l.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Unique(string prefix) => StoreHelper.Unique(prefix);

    private Task<JsonElement> GetAsync(string url, string bearer, HttpStatusCode expected = HttpStatusCode.OK) => H.JsonAsync(HttpMethod.Get, url, expected, bearer: bearer);

    private Task<JsonElement> PostAsync(string url, string bearer, object? body = null, HttpStatusCode expected = HttpStatusCode.OK) => H.JsonAsync(HttpMethod.Post, url, expected, body ?? new { }, bearer);

    private async Task<string> ErrorAsync(string url, string bearer, object body, HttpStatusCode expected)
    {
        var response = await H.SendAsync(HttpMethod.Post, url, body, bearer);
        Assert.Equal(expected, response.StatusCode);
        return await TestApi.ErrorCodeAsync(response);
    }

    private static string[] Ids(params Guid[] ids) => [.. ids.Select(i => i.ToString())];

    // ---- Job runs --------------------------------------------------------------------------------------

    [Fact]
    public async Task Job_runs_list_filter_summarise_and_never_show_secrets_or_stack_traces()
    {
        var admin = await H.AdminAsync();
        var job = Unique("test.job");
        var now = DateTimeOffset.UtcNow;
        var failedId = Guid.Empty;
        await _l.DbAsync(async db =>
        {
            var ok1 = new JobLog(job, "ref-1", 1, "corr-1", now.AddMinutes(-30));
            ok1.Succeed(now.AddMinutes(-30).AddSeconds(2));
            var ok2 = new JobLog(job, "ref-2", 1, "corr-2", now.AddMinutes(-20));
            ok2.Succeed(now.AddMinutes(-20).AddSeconds(1));
            var bad = new JobLog(job, "ref-3", 2, "corr-3", now.AddMinutes(-10));
            bad.Fail(now.AddMinutes(-10).AddSeconds(1), $"Courier call failed: api_key={Secret} for +8801712345678\n   at Juvo.Infrastructure.Couriers.Worker.Run() in C:\\src\\Worker.cs:line 9");
            var old = new JobLog(job, "ref-0", 1, null, now.AddDays(-3));
            old.Fail(now.AddDays(-3).AddSeconds(1), "old failure");
            db.JobLogs.AddRange(ok1, ok2, bad, old);
            await db.SaveChangesAsync(Ct);
            failedId = bad.Id;
            return 0;
        });

        var all = await GetAsync($"{Jobs}?job={job}", admin);
        Assert.Equal(4, all.GetProperty("total").GetInt32());
        Assert.Equal("ref-3", all.GetProperty("items")[0].GetProperty("reference").GetString()); // newest first

        Assert.Equal(1, (await GetAsync($"{Jobs}?job={job}&outcome=Failed&from={Uri.EscapeDataString(now.AddHours(-1).ToString("O"))}", admin)).GetProperty("total").GetInt32());
        Assert.Equal(2, (await GetAsync($"{Jobs}?job={job}&outcome=Failed", admin)).GetProperty("total").GetInt32());
        Assert.Equal(1, (await GetAsync($"{Jobs}?job={job}&reference=ref-2", admin)).GetProperty("total").GetInt32());
        Assert.Equal(1, (await GetAsync($"{Jobs}?job={job}&search=corr-3", admin)).GetProperty("total").GetInt32());
        Assert.Equal(3, (await GetAsync($"{Jobs}?job={job}&to={Uri.EscapeDataString(now.AddMinutes(-5).ToString("O"))}&from={Uri.EscapeDataString(now.AddHours(-1).ToString("O"))}", admin)).GetProperty("total").GetInt32());
        var paged = await GetAsync($"{Jobs}?job={job}&pageSize=2&page=2", admin);
        Assert.Equal(2, paged.GetProperty("items").GetArrayLength());
        Assert.Equal(4, paged.GetProperty("total").GetInt32());

        // The error text is cut to the first line and scrubbed, in the list and in the detail.
        var detail = await GetAsync($"{Jobs}/{failedId}", admin);
        var error = detail.GetProperty("error").GetString()!;
        Assert.DoesNotContain(Secret, error);
        Assert.DoesNotContain("1712345678", error);
        Assert.DoesNotContain("Worker.cs", error);
        Assert.DoesNotContain(" at ", error);
        Assert.Contains("Courier call failed", error);
        Assert.Equal("Failed", detail.GetProperty("outcome").GetString());
        Assert.Equal(1000, detail.GetProperty("durationMs").GetInt32());
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"{Jobs}/{Guid.NewGuid()}", null, admin)).StatusCode);

        var summary = (await GetAsync($"{Jobs}/summary", admin)).EnumerateArray().Single(r => r.GetProperty("jobName").GetString() == job);
        Assert.Equal(3, summary.GetProperty("runs24h").GetInt32());
        Assert.Equal(1, summary.GetProperty("failures24h").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, summary.GetProperty("lastSuccessAt").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, summary.GetProperty("lastFailureAt").ValueKind);
        Assert.DoesNotContain(Secret, summary.GetProperty("lastError").GetString());

        var kinds = (await GetAsync($"{Jobs}/kinds", admin)).EnumerateArray().ToDictionary(k => k.GetProperty("kind").GetString()!);
        Assert.True(kinds["outbox"].GetProperty("canRetry").GetBoolean());
        Assert.True(kinds["notifications"].GetProperty("canRetry").GetBoolean());
        Assert.False(kinds["courier-bookings"].GetProperty("recorded").GetBoolean());
        Assert.False(kinds["webhooks"].GetProperty("recorded").GetBoolean());
    }

    // ---- Outbox ----------------------------------------------------------------------------------------

    private async Task<(Guid Pending, Guid Retrying, Guid Dead, Guid Done)> SeedOutboxAsync(string type)
    {
        var now = DateTimeOffset.UtcNow;
        return await _l.DbAsync(async db =>
        {
            var pending = new OutboxMessage(type, "{}", now, "corr-p");
            var retrying = new OutboxMessage(type, "{}", now, "corr-r");
            retrying.MarkFailed(now, "Provider said no: token=" + Secret, 5, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
            var dead = new OutboxMessage(type, "{}", now, "corr-d");
            dead.MarkFailed(now, "Gave up for +8801812345678", 1, TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(10));
            var done = new OutboxMessage(type, "{}", now, "corr-s");
            done.MarkSucceeded(now);
            db.OutboxMessages.AddRange(pending, retrying, dead, done);
            await db.SaveChangesAsync(Ct);
            return (pending.Id, retrying.Id, dead.Id, done.Id);
        });
    }

    private Task<OutboxMessage> OutboxRowAsync(Guid id) => _l.DbAsync(db => db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id, Ct));

    [Fact]
    public async Task The_outbox_view_lists_open_messages_by_state_and_hides_finished_ones_and_secrets()
    {
        var admin = await H.AdminAsync();
        var type = Unique("test.type");
        var (pending, retrying, dead, done) = await SeedOutboxAsync(type);

        var open = await GetAsync($"{Jobs}/outbox?type={type}", admin);
        Assert.Equal(3, open.GetProperty("total").GetInt32());
        var byId = open.GetProperty("items").EnumerateArray().ToDictionary(i => Guid.Parse(i.GetProperty("id").GetString()!));
        Assert.DoesNotContain(done, byId.Keys);
        Assert.Equal("Pending", byId[pending].GetProperty("state").GetString());
        Assert.Equal("Retrying", byId[retrying].GetProperty("state").GetString());
        Assert.Equal("DeadLettered", byId[dead].GetProperty("state").GetString());
        Assert.Equal(1, byId[retrying].GetProperty("attempts").GetInt32());
        Assert.DoesNotContain(Secret, byId[retrying].GetProperty("lastError").GetString());
        Assert.DoesNotContain("1812345678", byId[dead].GetProperty("lastError").GetString());
        Assert.False(byId[pending].TryGetProperty("payload", out _));

        Assert.Equal(1, (await GetAsync($"{Jobs}/outbox?type={type}&state=DeadLettered", admin)).GetProperty("total").GetInt32());
        Assert.Equal(1, (await GetAsync($"{Jobs}/outbox?type={type}&state=Retrying", admin)).GetProperty("total").GetInt32());
        Assert.Equal(1, (await GetAsync($"{Jobs}/outbox?type={type}&state=Pending", admin)).GetProperty("total").GetInt32());
        Assert.Equal(1, (await GetAsync($"{Jobs}/outbox?type={type}&search=corr-d", admin)).GetProperty("total").GetInt32());

        var one = await GetAsync($"{Jobs}/outbox/{dead}", admin);
        Assert.Equal(type, one.GetProperty("type").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Get, $"{Jobs}/outbox/{Guid.NewGuid()}", null, admin)).StatusCode);

        var summary = await GetAsync($"{Jobs}/outbox/summary", admin);
        Assert.True(summary.GetProperty("deadLettered").GetInt32() >= 1);
        Assert.True(summary.GetProperty("retrying").GetInt32() >= 1);
        Assert.True(summary.GetProperty("pending").GetInt32() >= 1);
        Assert.NotEqual(JsonValueKind.Null, summary.GetProperty("oldestOpenAt").ValueKind);
    }

    [Fact]
    public async Task Requeueing_one_message_resets_it_safely_audits_it_and_refuses_what_is_not_failed()
    {
        var admin = await H.AdminAsync();
        var type = Unique("test.type");
        var (pending, retrying, dead, done) = await SeedOutboxAsync(type);

        var result = await PostAsync($"{Jobs}/outbox/{dead}/requeue", admin);
        Assert.Equal(1, result.GetProperty("requeued").GetInt32());
        var row = await OutboxRowAsync(dead);
        Assert.Equal(OutboxStatus.Pending, row.Status);
        Assert.Equal(0, row.AttemptCount);
        Assert.Null(row.ProcessedAt);
        Assert.True(row.NextAttemptAt <= DateTimeOffset.UtcNow.AddSeconds(5));

        // A message that is fresh, or already delivered, is not touched and answers 409; an unknown one 404.
        Assert.Equal("SYSTEM.NOT_REQUEUEABLE", await ErrorAsync($"{Jobs}/outbox/{pending}/requeue", admin, new { }, HttpStatusCode.Conflict));
        Assert.Equal("SYSTEM.NOT_REQUEUEABLE", await ErrorAsync($"{Jobs}/outbox/{done}/requeue", admin, new { }, HttpStatusCode.Conflict));
        Assert.Equal("SYSTEM.MESSAGE_NOT_FOUND", await ErrorAsync($"{Jobs}/outbox/{Guid.NewGuid()}/requeue", admin, new { }, HttpStatusCode.NotFound));
        Assert.Equal(OutboxStatus.Succeeded, (await OutboxRowAsync(done)).Status);

        // A retrying message is put back with no waiting.
        Assert.Equal(1, (await PostAsync($"{Jobs}/outbox/{retrying}/requeue", admin)).GetProperty("requeued").GetInt32());
        Assert.Equal(0, (await OutboxRowAsync(retrying)).AttemptCount);

        var audits = await _l.DbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Entity == "OutboxMessage" && a.EntityId == dead.ToString()).ToListAsync(Ct));
        var audit = Assert.Single(audits);
        Assert.NotNull(audit.UserId);
        Assert.Contains("\"action\":\"requeue\"", audit.NewValue);
        Assert.Contains("\"done\":1", audit.NewValue);
    }

    [Fact]
    public async Task Bulk_requeue_reports_what_it_did_and_skipped_and_is_limited_to_100()
    {
        var admin = await H.AdminAsync();
        var type = Unique("test.type");
        var (pending, retrying, dead, done) = await SeedOutboxAsync(type);
        var missing = Guid.NewGuid();

        var result = await PostAsync($"{Jobs}/outbox/requeue", admin, new { ids = Ids(dead, retrying, pending, done, missing, dead) });
        Assert.Equal(5, result.GetProperty("requested").GetInt32()); // the duplicate is counted once
        Assert.Equal(2, result.GetProperty("requeued").GetInt32());
        var skipped = result.GetProperty("skipped").EnumerateArray().ToDictionary(s => Guid.Parse(s.GetProperty("id").GetString()!), s => s.GetProperty("reason").GetString()!);
        Assert.Equal(3, skipped.Count);
        Assert.Equal("Not found.", skipped[missing]);
        Assert.Contains("delivered", skipped[done]);
        Assert.Equal(OutboxStatus.Pending, (await OutboxRowAsync(dead)).Status);
        Assert.Equal(0, (await OutboxRowAsync(dead)).AttemptCount);

        var audit = await _l.DbAsync(db => db.AuditLogs.AsNoTracking().Where(a => a.Entity == "OutboxMessage" && a.EntityId == "bulk" && a.NewValue!.Contains(dead.ToString())).ToListAsync(Ct));
        Assert.Contains("\"requested\":5", Assert.Single(audit).NewValue);

        // A second run finds nothing left to requeue (idempotent).
        var again = await PostAsync($"{Jobs}/outbox/requeue", admin, new { ids = Ids(dead) });
        Assert.Equal(0, again.GetProperty("requeued").GetInt32());

        // Exactly 100 is allowed; 101, an empty list and a missing list are refused and nothing changes.
        Assert.Equal(0, (await PostAsync($"{Jobs}/outbox/requeue", admin, new { ids = Ids([.. Enumerable.Range(0, 100).Select(_ => Guid.NewGuid())]) })).GetProperty("requeued").GetInt32());
        Assert.Equal("SYSTEM.TOO_MANY", await ErrorAsync($"{Jobs}/outbox/requeue", admin, new { ids = Ids([.. Enumerable.Range(0, 101).Select(_ => Guid.NewGuid())]) }, HttpStatusCode.BadRequest));
        await ErrorAsync($"{Jobs}/outbox/requeue", admin, new { ids = Array.Empty<string>() }, HttpStatusCode.BadRequest);
        await ErrorAsync($"{Jobs}/outbox/requeue", admin, new { }, HttpStatusCode.BadRequest);
    }

    // ---- Failed notification sends ---------------------------------------------------------------------

    private async Task<(Guid Dead, Guid Failed, Guid Sent, Guid Pending)> SeedNotificationsAsync(string template)
    {
        var now = DateTimeOffset.UtcNow;
        return await _l.DbAsync(async db =>
        {
            NotificationLog Make(NotificationChannel channel, string recipient, string body)
            {
                var log = NotificationLog.Queue(channel, recipient, template, null, body, "en", 1, NotificationCategory.Transactional, null, Unique("dedupe"), "corr", now);
                db.NotificationLogs.Add(log);
                return log;
            }

            var dead = Make(NotificationChannel.Sms, "+8801712345678", "Your private order text");
            dead.MarkAttemptFailed("SMS gateway refused: key=" + Secret, true, now);
            var failed = Make(NotificationChannel.Email, "nadia@example.com", "Body");
            failed.MarkAttemptFailed("Mailbox full", false, now);
            var sent = Make(NotificationChannel.Sms, "+8801912345678", "Body");
            sent.MarkSent("ok", now);
            var pending = Make(NotificationChannel.WhatsApp, "+8801612345678", "Body");
            await db.SaveChangesAsync(Ct);
            return (dead.Id, failed.Id, sent.Id, pending.Id);
        });
    }

    private Task<NotificationLog> LogRowAsync(Guid id) => _l.DbAsync(db => db.NotificationLogs.AsNoTracking().SingleAsync(n => n.Id == id, Ct));

    [Fact]
    public async Task Failed_sends_are_listed_masked_and_retry_goes_through_the_normal_delivery_path_once()
    {
        var admin = await H.AdminAsync();
        var template = Unique("test.tpl");
        var (dead, failed, sent, pending) = await SeedNotificationsAsync(template);

        var list = await GetAsync($"{Jobs}/notifications/failed?template={template}", admin);
        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var text = list.ToString();
        Assert.DoesNotContain("1712345678", text);
        Assert.DoesNotContain("nadia@example.com", text);
        Assert.DoesNotContain("private order text", text);
        Assert.DoesNotContain(Secret, text);
        var deadRow = list.GetProperty("items").EnumerateArray().Single(i => Guid.Parse(i.GetProperty("id").GetString()!) == dead);
        Assert.EndsWith("5678", deadRow.GetProperty("recipientMasked").GetString());
        Assert.Equal("DeadLetter", deadRow.GetProperty("status").GetString());
        Assert.Equal(1, (await GetAsync($"{Jobs}/notifications/failed?template={template}&channel=Email", admin)).GetProperty("total").GetInt32());

        var result = await PostAsync($"{Jobs}/notifications/{dead}/retry", admin);
        Assert.Equal(1, result.GetProperty("requeued").GetInt32());
        var row = await LogRowAsync(dead);
        Assert.Equal(NotificationStatus.Pending, row.Status);
        Assert.Equal(0, row.Attempts);
        var queued = await _l.DbAsync(db => db.OutboxMessages.AsNoTracking().CountAsync(m => m.Type == "notification.deliver" && m.Payload.Contains(dead.ToString()), Ct));
        Assert.Equal(1, queued);

        // A second retry (a double click) queues nothing more.
        Assert.Equal("SYSTEM.NOT_REQUEUEABLE", await ErrorAsync($"{Jobs}/notifications/{dead}/retry", admin, new { }, HttpStatusCode.Conflict));
        Assert.Equal(1, await _l.DbAsync(db => db.OutboxMessages.AsNoTracking().CountAsync(m => m.Type == "notification.deliver" && m.Payload.Contains(dead.ToString()), Ct)));
        Assert.Equal("SYSTEM.NOT_REQUEUEABLE", await ErrorAsync($"{Jobs}/notifications/{sent}/retry", admin, new { }, HttpStatusCode.Conflict));
        Assert.Equal("NOTIFY.LOG_NOT_FOUND", await ErrorAsync($"{Jobs}/notifications/{Guid.NewGuid()}/retry", admin, new { }, HttpStatusCode.NotFound));

        var bulk = await PostAsync($"{Jobs}/notifications/retry", admin, new { ids = Ids(dead, failed, sent, pending, Guid.NewGuid()) });
        Assert.Equal(5, bulk.GetProperty("requested").GetInt32());
        Assert.Equal(1, bulk.GetProperty("requeued").GetInt32()); // only the e-mail that was still Failed
        Assert.Equal(4, bulk.GetProperty("skipped").GetArrayLength());
        Assert.Equal(NotificationStatus.Pending, (await LogRowAsync(failed)).Status);
        Assert.Equal(NotificationStatus.Sent, (await LogRowAsync(sent)).Status);

        await ErrorAsync($"{Jobs}/notifications/retry", admin, new { ids = Ids([.. Enumerable.Range(0, 101).Select(_ => Guid.NewGuid())]) }, HttpStatusCode.BadRequest);
        var audits = await _l.DbAsync(db => db.AuditLogs.AsNoTracking().CountAsync(a => a.Entity == "NotificationLog" && (a.EntityId == dead.ToString() || (a.EntityId == "bulk" && a.NewValue!.Contains(failed.ToString()))), Ct));
        Assert.Equal(3, audits); // the first retry, the refused repeat (no change, still recorded) and the bulk call
    }

    // ---- Permissions -----------------------------------------------------------------------------------

    [Fact]
    public async Task Every_endpoint_needs_its_permission_and_view_does_not_allow_retry_or_health()
    {
        var admin = await H.AdminAsync();
        var nobody = await _l.O.StaffTokenAsync(admin, null, "loyalty.points.view");
        var viewer = await _l.O.StaffTokenAsync(admin, null, "system.jobs.view");
        var retrier = await _l.O.StaffTokenAsync(admin, null, "system.jobs.view", "system.jobs.retry");
        var health = await _l.O.StaffTokenAsync(admin, null, "system.health.view");
        var customer = await _l.CustomerAsync();
        var id = Guid.NewGuid();

        var reads = new[] { Jobs, $"{Jobs}/summary", $"{Jobs}/kinds", $"{Jobs}/{id}", $"{Jobs}/outbox", $"{Jobs}/outbox/summary", $"{Jobs}/outbox/{id}", $"{Jobs}/notifications/failed" };
        var writes = new[] { $"{Jobs}/outbox/requeue", $"{Jobs}/outbox/{id}/requeue", $"{Jobs}/notifications/retry", $"{Jobs}/notifications/{id}/retry" };
        var healthReads = new[] { "/api/v1/system/integrations/health", "/api/v1/system/status" };

        foreach (var url in reads.Concat(healthReads))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, url, null, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, url, null, customer.Token)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, url, null, nobody)).StatusCode);
        }

        foreach (var url in reads)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, url, null, health)).StatusCode);
        }

        foreach (var url in healthReads)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, url, null, viewer)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, url, null, health)).StatusCode);
        }

        foreach (var url in writes)
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Post, url, new { ids = Array.Empty<string>() }, null)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, url, new { ids = Array.Empty<string>() }, nobody)).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, url, new { ids = Array.Empty<string>() }, viewer)).StatusCode);
            Assert.NotEqual(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, url, new { ids = Array.Empty<string>() }, retrier)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, "/api/v1/system/integrations/courier:SANDBOX/test", new { }, viewer)).StatusCode);
        foreach (var url in reads)
        {
            Assert.Equal(HttpStatusCode.OK == (await H.SendAsync(HttpMethod.Get, url, null, viewer)).StatusCode, !url.Contains(id.ToString()));
        }
    }

    // ---- Integration health ----------------------------------------------------------------------------

    private static JsonElement Row(JsonElement rows, string key) => rows.EnumerateArray().Single(r => r.GetProperty("key").GetString() == key);

    [Fact]
    public async Task Integration_health_lists_every_slot_with_sandbox_and_disabled_statuses_and_scrubbed_failures()
    {
        var admin = await H.AdminAsync();
        var rows = await GetAsync("/api/v1/system/integrations/health", admin);

        var gateway = Row(rows, "payment:SANDBOX");
        Assert.Equal("Sandbox", gateway.GetProperty("status").GetString());
        Assert.Equal("Sandbox", gateway.GetProperty("mode").GetString());
        Assert.True(gateway.GetProperty("enabled").GetBoolean());
        Assert.Equal("payment", gateway.GetProperty("kind").GetString());
        Assert.False(gateway.GetProperty("canTest").GetBoolean());

        foreach (var key in new[] { "payment:BKASH", "payment:NAGAD", "payment:SSLCOMMERZ", "payment:AAMARPAY", "payment:STRIPE", "payment:PAYPAL", "courier:PATHAO", "courier:STEADFAST", "courier:REDX", "courier:PAPERFLY", "courier:ECOURIER", "courier:DHL" })
        {
            var slot = Row(rows, key);
            Assert.Equal("Disabled", slot.GetProperty("status").GetString());
            Assert.False(slot.GetProperty("enabled").GetBoolean());
            Assert.False(slot.GetProperty("canTest").GetBoolean());
        }

        var courier = Row(rows, "courier:SANDBOX");
        Assert.Equal("Sandbox", courier.GetProperty("status").GetString());
        Assert.True(courier.GetProperty("canTest").GetBoolean());
        Assert.Equal("Sandbox", Row(rows, "sms").GetProperty("status").GetString());
        Assert.Equal("Sandbox", Row(rows, "email").GetProperty("status").GetString());
        Assert.Equal("Disabled", Row(rows, "whatsapp").GetProperty("status").GetString()); // off by default

        // A failure reported by an adapter shows up with the message scrubbed and the count; a slot that is switched off stays Disabled.
        var recorder = factory.Services.GetRequiredService<IIntegrationHealthRecorder>();
        recorder.Failure("courier:PATHAO", $"401 from Pathao: token={Secret} for +8801712345678");
        var after = Row(await GetAsync("/api/v1/system/integrations/health", admin), "courier:PATHAO");
        Assert.Equal(1, after.GetProperty("consecutiveFailures").GetInt32());
        Assert.NotEqual(JsonValueKind.Null, after.GetProperty("lastFailureAt").ValueKind);
        Assert.DoesNotContain(Secret, after.GetProperty("lastError").GetString());
        Assert.DoesNotContain("1712345678", after.GetProperty("lastError").GetString());
        Assert.Equal("Disabled", after.GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_notification_delivery_reports_its_outcome_to_the_channel_health()
    {
        var admin = await H.AdminAsync();
        var (dead, _, _, pending) = await SeedNotificationsAsync(Unique("test.tpl"));
        var recorder = factory.Services.GetRequiredService<IIntegrationHealthRecorder>();
        var before = recorder.Get("whatsapp")?.Successes ?? 0;

        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var handler = scope.ServiceProvider.GetServices<IOutboxMessageHandler>().Single(h => h.MessageType == "notification.deliver");
            await handler.HandleAsync(new OutboxMessageContext(Guid.NewGuid(), "notification.deliver", JsonSerializer.Serialize(new { logId = pending }), 1, null), Ct);
        }

        Assert.Equal(NotificationStatus.Sent, (await LogRowAsync(pending)).Status);
        Assert.Equal(before + 1, recorder.Get("whatsapp")!.Successes);
        Assert.NotNull(recorder.Get("whatsapp")!.LastSuccessAt);

        // The dashboard shows the channel as Disabled while WhatsApp is switched off, whatever was recorded.
        Assert.Equal("Disabled", Row(await GetAsync("/api/v1/system/integrations/health", admin), "whatsapp").GetProperty("status").GetString());
        Assert.NotEqual(NotificationStatus.Sent, (await LogRowAsync(dead)).Status);
    }

    [Fact]
    public async Task The_connectivity_check_runs_only_where_it_is_safe()
    {
        var admin = await H.AdminAsync();

        var result = await PostAsync("/api/v1/system/integrations/courier:SANDBOX/test", admin);
        Assert.True(result.GetProperty("ok").GetBoolean());
        Assert.True(result.GetProperty("durationMs").GetInt32() >= 0);
        var row = Row(await GetAsync("/api/v1/system/integrations/health", admin), "courier:SANDBOX");
        Assert.NotEqual(JsonValueKind.Null, row.GetProperty("lastSuccessAt").ValueKind);
        Assert.Equal(0, row.GetProperty("consecutiveFailures").GetInt32());

        foreach (var key in new[] { "payment:SANDBOX", "payment:BKASH", "courier:PATHAO", "sms", "email", "whatsapp" })
        {
            Assert.Equal("SYSTEM.NOT_SUPPORTED", await ErrorAsync($"/api/v1/system/integrations/{Uri.EscapeDataString(key)}/test", admin, new { }, HttpStatusCode.Conflict));
        }

        Assert.Equal("SYSTEM.UNKNOWN_INTEGRATION", await ErrorAsync("/api/v1/system/integrations/nothing/test", admin, new { }, HttpStatusCode.NotFound));
    }

    // ---- Status ----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_status_page_shows_version_uptime_database_backlog_slow_routes_and_scrubbed_errors_without_personal_data()
    {
        var admin = await H.AdminAsync();
        var monitor = factory.Services.GetRequiredService<RequestMonitor>();
        var type = Unique("test.type");
        await SeedOutboxAsync(type);
        await _l.DbAsync(async db =>
        {
            var failed = new JobLog(Unique("test.job"), null, 1, null, DateTimeOffset.UtcNow.AddMinutes(-2));
            failed.Fail(DateTimeOffset.UtcNow.AddMinutes(-2), "boom");
            db.JobLogs.Add(failed);
            await db.SaveChangesAsync(Ct);
            return 0;
        });

        var slowRoute = "/test/slow/" + Unique("route") + "/{id:guid}";
        monitor.RecordRequest("GET", slowRoute, 200, TimeSpan.FromSeconds(30));
        try
        {
            throw new InvalidOperationException($"explosion for +8801712345678 token={Secret}");
        }
        catch (Exception ex)
        {
            monitor.RecordError("corr-status-test", slowRoute, ex);
        }

        // A real request with a query string and an id in the path: only the route template is kept.
        var probe = Guid.NewGuid();
        await H.SendAsync(HttpMethod.Get, $"{Jobs}/{probe}?search=private-term-{probe:N}", null, admin);

        var status = await GetAsync("/api/v1/system/status", admin);
        Assert.False(string.IsNullOrWhiteSpace(status.GetProperty("version").GetString()));
        Assert.True(status.GetProperty("databaseReachable").GetBoolean());
        Assert.True(status.GetProperty("uptimeSeconds").GetInt64() >= 0);
        Assert.True(status.GetProperty("failedJobs24h").GetInt32() >= 1);
        Assert.True(status.GetProperty("slowRequests").GetInt32() >= 1);
        Assert.True(status.GetProperty("requestsSeen").GetInt64() >= 1);
        Assert.Contains("process", status.GetProperty("scope").GetString());
        var outbox = status.GetProperty("outbox");
        Assert.True(outbox.GetProperty("deadLettered").GetInt32() >= 1);
        Assert.True(outbox.GetProperty("oldestOpenAgeSeconds").GetInt32() >= 0);
        Assert.Contains(status.GetProperty("slowestRoutes").EnumerateArray(), r => r.GetProperty("route").GetString() == slowRoute && r.GetProperty("maxMs").GetInt32() >= 30000);

        var error = status.GetProperty("recentErrors").EnumerateArray().First(e => e.GetProperty("correlationId").GetString() == "corr-status-test");
        Assert.Equal("InvalidOperationException", error.GetProperty("exceptionType").GetString());
        var message = error.GetProperty("message").GetString()!;
        Assert.DoesNotContain(Secret, message);
        Assert.DoesNotContain("1712345678", message);

        var whole = status.ToString();
        Assert.DoesNotContain(probe.ToString(), whole);
        Assert.DoesNotContain("private-term", whole);
        Assert.DoesNotContain("   at ", whole);

        var sample = monitor.Requests().First(r => r.Route.EndsWith("/system/jobs/{id:guid}", StringComparison.Ordinal));
        Assert.DoesNotContain(probe.ToString(), sample.Route);
        Assert.DoesNotContain("?", sample.Route);
        Assert.Equal(404, sample.Status);
    }
}
