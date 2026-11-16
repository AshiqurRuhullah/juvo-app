using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Domain.Notifications;
using Juvo.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juvo.Api.IntegrationTests.Operations;

/// <summary>The reopen window, the auto-close job and the messages a customer receives (module Y, FR-Y02 and FR-Y04).</summary>
public class SupportAutomationTests(OpsFactory factory) : IClassFixture<OpsFactory>
{
    private readonly SupportHelper _s = new(factory);

    private StoreHelper H => _s.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> StaffAsync()
    {
        var admin = await H.AdminAsync();
        return await _s.StaffAsync(admin, await _s.O.HoAsync(), SupportHelper.AllSupport);
    }

    private TicketAutoCloseWorker Worker() => factory.Services.GetServices<IHostedService>().OfType<TicketAutoCloseWorker>().Single();

    private Task<List<NotificationLog>> ByTemplateAsync(string template, string mobile) =>
        _s.DbAsync(db => db.NotificationLogs.AsNoTracking().Where(n => n.Template == template && n.Recipient == SupportHelper.Normalised(mobile)).ToListAsync(Ct));

    // ---- the reopen window ---------------------------------------------------------------------------------------

    [Fact]
    public async Task A_resolved_ticket_can_be_reopened_inside_the_window_and_not_after_it()
    {
        var staff = await StaffAsync();
        var customer = await H.CustomerTokenAsync();
        await _s.SetPlatformSettingAsync("Support.ReopenDays", "3");
        try
        {
            var inside = (await _s.MyCreateAsync(customer)).GetProperty("id").GetGuid();
            var outside = (await _s.MyCreateAsync(customer, subject: "Another")).GetProperty("id").GetGuid();
            await _s.ActAsync(staff, inside, "resolve");
            await _s.ActAsync(staff, outside, "resolve");
            await _s.SetResolvedAtAsync(inside, DateTimeOffset.UtcNow.AddDays(-2));
            await _s.SetResolvedAtAsync(outside, DateTimeOffset.UtcNow.AddDays(-4));

            Assert.True((await _s.GetAsync(staff, inside)).GetProperty("canReopen").GetBoolean());
            Assert.False((await _s.GetAsync(staff, outside)).GetProperty("canReopen").GetBoolean());
            Assert.False((await _s.MyGetAsync(customer, outside)).GetProperty("canReopen").GetBoolean());

            await StoreHelper.ExpectCodeAsync(await _s.TryMyActAsync(customer, outside, "reopen"), "TICKET.REOPEN_WINDOW_EXPIRED");
            await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staff, outside, "reopen"), "TICKET.REOPEN_WINDOW_EXPIRED");
            await StoreHelper.ExpectCodeAsync(await _s.TryMyActAsync(customer, outside, "reply", new { text = "Hello again" }), "TICKET.REOPEN_WINDOW_EXPIRED");
            Assert.Equal("Resolved", (await _s.GetAsync(staff, outside)).GetProperty("status").GetString()); // a refused reopening changes nothing
            Assert.Single((await _s.MyGetAsync(customer, outside)).GetProperty("messages").EnumerateArray()); // and stores no message

            Assert.Equal("Open", (await _s.MyActAsync(customer, inside, "reopen")).GetProperty("status").GetString());
        }
        finally
        {
            await _s.SetPlatformSettingAsync("Support.ReopenDays", null);
        }
    }

    [Fact]
    public async Task A_closed_ticket_is_reopened_inside_the_window_counted_from_the_closing()
    {
        var staff = await StaffAsync();
        var id = (await _s.GuestTicketAsync(staff)).GetProperty("id").GetGuid();
        await _s.ActAsync(staff, id, "resolve");
        await _s.ActAsync(staff, id, "close");
        await _s.SetResolvedAtAsync(id, DateTimeOffset.UtcNow.AddDays(-30)); // resolved long ago, closed just now: the closing counts

        Assert.Equal("Open", (await _s.ActAsync(staff, id, "reopen")).GetProperty("status").GetString());

        await _s.ActAsync(staff, id, "close");
        await _s.SetClosedAtAsync(id, DateTimeOffset.UtcNow.AddDays(-8));
        await StoreHelper.ExpectCodeAsync(await _s.TryActAsync(staff, id, "reopen"), "TICKET.REOPEN_WINDOW_EXPIRED"); // the default window is 7 days
    }

    // ---- the auto-close job ---------------------------------------------------------------------------------------

    [Fact]
    public async Task The_job_closes_tickets_resolved_longer_than_the_setting_ago_once_and_names_the_system()
    {
        var staff = await StaffAsync();
        var old = (await _s.GuestTicketAsync(staff)).GetProperty("id").GetGuid();
        var recent = (await _s.GuestTicketAsync(staff)).GetProperty("id").GetGuid();
        var open = (await _s.GuestTicketAsync(staff)).GetProperty("id").GetGuid();
        var reopened = (await _s.GuestTicketAsync(staff)).GetProperty("id").GetGuid();
        foreach (var id in new[] { old, recent, reopened })
        {
            await _s.ActAsync(staff, id, "resolve");
        }

        await _s.SetResolvedAtAsync(old, DateTimeOffset.UtcNow.AddDays(-8));
        await _s.SetResolvedAtAsync(recent, DateTimeOffset.UtcNow.AddDays(-6)); // the default is 7 days
        await _s.SetResolvedAtAsync(reopened, DateTimeOffset.UtcNow.AddDays(-6));
        await _s.ActAsync(staff, reopened, "reopen"); // reopened in time: the job must not touch it

        var first = await Worker().SweepAsync(Ct);
        var second = await Worker().SweepAsync(Ct);

        Assert.Equal(7, first.Days);
        Assert.True(first.Closed >= 1);
        Assert.Equal(0, second.Closed); // idempotent
        var closed = await _s.GetAsync(staff, old);
        Assert.Equal("Closed", closed.GetProperty("status").GetString());
        var rows = closed.GetProperty("history").EnumerateArray().ToList();
        Assert.Equal(["Open", "Resolved", "Closed"], rows.Select(r => r.GetProperty("toStatus").GetString()));
        Assert.Equal("System", rows[^1].GetProperty("actorName").GetString());
        Assert.Contains("automatically", rows[^1].GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Contains(closed.GetProperty("messages").EnumerateArray(), m => m.GetProperty("kind").GetString() == "System");
        Assert.Equal("Resolved", (await _s.GetAsync(staff, recent)).GetProperty("status").GetString());
        Assert.Equal("Open", (await _s.GetAsync(staff, open)).GetProperty("status").GetString());
        Assert.Equal("Open", (await _s.GetAsync(staff, reopened)).GetProperty("status").GetString());
        var log = await _s.DbAsync(db => db.JobLogs.AsNoTracking().CountAsync(j => j.JobName == "support.ticket-auto-close", Ct));
        Assert.True(log >= 1);
    }

    [Fact]
    public async Task The_number_of_days_is_a_setting()
    {
        var staff = await StaffAsync();
        var id = (await _s.GuestTicketAsync(staff)).GetProperty("id").GetGuid();
        await _s.ActAsync(staff, id, "resolve");
        await _s.SetResolvedAtAsync(id, DateTimeOffset.UtcNow.AddDays(-2));
        await _s.SetPlatformSettingAsync("Support.AutoCloseDays", "1");
        try
        {
            var result = await Worker().SweepAsync(Ct);

            Assert.Equal(1, result.Days);
            Assert.Equal("Closed", (await _s.GetAsync(staff, id)).GetProperty("status").GetString());
        }
        finally
        {
            await _s.SetPlatformSettingAsync("Support.AutoCloseDays", null);
        }
    }

    // ---- customer messages -----------------------------------------------------------------------------------------

    [Fact]
    public async Task A_new_ticket_a_public_reply_and_a_resolution_each_send_one_message_and_a_note_sends_none()
    {
        var staff = await StaffAsync();
        var mobile = StoreHelper.NewMobile();
        var ticket = await _s.GuestTicketAsync(staff, mobile, "nadia@example.com");
        var id = ticket.GetProperty("id").GetGuid();
        var number = ticket.GetProperty("number").GetString()!;

        await _s.ReplyAsync(staff, id, "INTERNAL-ONLY: check the courier log", isInternal: true);
        await _s.ReplyAsync(staff, id, "We found your parcel, it arrives tomorrow.");
        await _s.ActAsync(staff, id, "resolve");
        await _s.DrainOutboxAsync();
        await _s.DrainOutboxAsync(); // a second pass must not send anything again

        var received = await ByTemplateAsync("ticket.received", mobile);
        var sms = Assert.Single(received);
        Assert.Equal(NotificationChannel.Sms, sms.Channel);
        Assert.Contains(number, sms.Body, StringComparison.Ordinal);
        Assert.Equal("en", sms.Language);
        var replied = await ByTemplateAsync("ticket.replied", mobile);
        var replySms = Assert.Single(replied);
        Assert.DoesNotContain("parcel", replySms.Body, StringComparison.Ordinal); // an SMS only says that there is an answer
        var resolved = await ByTemplateAsync("ticket.resolved", mobile);
        Assert.Single(resolved);

        // The guest left an e-mail address, so every message also goes there, and the reply text is in the e-mail.
        var emails = await _s.DbAsync(db => db.NotificationLogs.AsNoTracking().Where(n => n.Recipient == "nadia@example.com" && n.Body.Contains(number)).OrderBy(n => n.QueuedAt).ToListAsync(Ct));
        Assert.Equal(3, emails.Count);
        Assert.Contains(emails, e => e.Template == "ticket.replied" && e.Body.Contains("We found your parcel", StringComparison.Ordinal));
        Assert.All(emails, e => Assert.DoesNotContain("INTERNAL-ONLY", e.Body, StringComparison.Ordinal));
        var everything = await _s.DbAsync(db => db.NotificationLogs.AsNoTracking().Where(n => n.Body.Contains("INTERNAL-ONLY")).CountAsync(Ct));
        Assert.Equal(0, everything);
        Assert.All(received.Concat(replied).Concat(resolved).Concat(emails), n => Assert.Equal(NotificationStatus.Sent, n.Status));
    }

    [Fact]
    public async Task A_customer_with_a_bengali_preference_gets_bengali_messages_without_any_marketing_consent()
    {
        var staff = await StaffAsync();
        var mobile = StoreHelper.NewMobile();
        var customer = await H.CustomerTokenAsync(mobile);
        await _s.DbAsync(db => db.Customers.Where(c => c.Mobile == SupportHelper.Normalised(mobile)).ExecuteUpdateAsync(s => s.SetProperty(c => c.PreferredLanguage, "bn"), Ct));

        var created = await _s.MyCreateAsync(customer);
        await _s.ReplyAsync(staff, created.GetProperty("id").GetGuid(), "আমরা দেখছি।");
        await _s.DrainOutboxAsync();

        var logs = await _s.DbAsync(db => db.NotificationLogs.AsNoTracking().Where(n => n.Recipient == SupportHelper.Normalised(mobile) && n.Template.StartsWith("ticket.")).ToListAsync(Ct));
        Assert.Equal(2, logs.Count); // received and replied, once each
        Assert.All(logs, l => Assert.Equal("bn", l.Language));
        Assert.All(logs, l => Assert.Contains(l.Body, ch => ch is >= 'ঀ' and <= '৿'));
        Assert.All(logs, l => Assert.Equal(NotificationCategory.Transactional, l.Category));
        var mine = await _s.MyGetAsync(customer, created.GetProperty("id").GetGuid());
        Assert.Equal(2, mine.GetProperty("messages").GetArrayLength());
    }

    [Fact]
    public async Task A_ticket_without_an_email_sends_the_sms_only()
    {
        var staff = await StaffAsync();
        var mobile = StoreHelper.NewMobile();
        var ticket = await _s.GuestTicketAsync(staff, mobile);
        await _s.ReplyAsync(staff, ticket.GetProperty("id").GetGuid(), "Hello");
        await _s.DrainOutboxAsync();

        var logs = await _s.DbAsync(db => db.NotificationLogs.AsNoTracking().Where(n => n.Body.Contains(ticket.GetProperty("number").GetString()!)).ToListAsync(Ct));

        Assert.Equal(2, logs.Count);
        Assert.All(logs, l => Assert.Equal(NotificationChannel.Sms, l.Channel));
    }

    [Fact]
    public async Task Ticket_events_reach_the_outbox_with_their_own_message_types()
    {
        var staff = await StaffAsync();
        var ticket = await _s.GuestTicketAsync(staff);
        await _s.ReplyAsync(staff, ticket.GetProperty("id").GetGuid(), "Hi");
        await _s.ActAsync(staff, ticket.GetProperty("id").GetGuid(), "resolve");

        var types = await _s.DbAsync(db => db.OutboxMessages.Select(m => m.Type).Distinct().ToListAsync(Ct));

        Assert.Contains("domain.TicketCreated", types);
        Assert.Contains("domain.TicketReplied", types);
        Assert.Contains("domain.TicketResolved", types);
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, "/api/v1/system/ping")).StatusCode);
        Assert.NotEqual(JsonValueKind.Undefined, ticket.ValueKind);
    }
}
