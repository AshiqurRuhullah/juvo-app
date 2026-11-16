using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Application.Marketing;
using Juvo.Domain.Customers;
using Juvo.Domain.Marketing;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Api.IntegrationTests.Marketing;

/// <summary>Campaigns (FR-AC02, FR-AC04): preview, consent, unsubscribe, quiet hours, caps, dedupe, resume, scheduling, results and attribution.</summary>
public class CampaignTests(MarketingFactory factory) : IClassFixture<MarketingFactory>
{
    private readonly MarketingHelper _m = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly object Everyone = MarketingHelper.Rules("and", new { type = "newCustomers", days = 30 });

    private sealed record Shop(string Staff, Guid Segment);

    /// <summary>Hides old customers, resets the marketing settings (quiet hours off so the test does not depend on the time of day) and makes a segment of everyone created in the last 30 days.</summary>
    private async Task<Shop> PrepareAsync()
    {
        await _m.ResetCustomersAsync();
        foreach (var key in new[]
                 {
                     MarketingSettingKeys.QuietHoursEnabled, MarketingSettingKeys.QuietHoursStart, MarketingSettingKeys.QuietHoursEnd, MarketingSettingKeys.FrequencyCapMessages, MarketingSettingKeys.FrequencyCapDays,
                     MarketingSettingKeys.ThrottlePerMinute, MarketingSettingKeys.AttributionWindowDays, MarketingSettingKeys.ShopName, MarketingSettingKeys.LinkUrl, "Notifications.SmsEnabled", "Notifications.EmailEnabled",
                 })
        {
            await _m.SetPlatformSettingAsync(key, null);
        }

        var staff = await _m.StaffAsync();
        await _m.PutAsync(MarketingHelper.SettingsUrl, staff, new { quietHoursEnabled = false });
        return new Shop(staff, await _m.SegmentAsync(staff, Everyone));
    }

    private Task<Guid> Sms(string name, string? language = null) => _m.CustomerAsync(name, createdDaysAgo: 5, sms: true, language: language);

    private Task<Guid> Mail(string name) => _m.CustomerAsync(name, createdDaysAgo: 5, email: true);

    private Task<JsonElement> SendAsync(Shop s, Guid campaign) => _m.PostAsync($"{MarketingHelper.CampaignsUrl}/{campaign}/send", s.Staff);

    private Task<List<NotificationLog>> CampaignLogsAsync(params Guid[] customers) => _m.LogsAsync(CampaignDispatchService.TemplateCode, customers);

    private static int Skipped(JsonElement results, string reason) =>
        results.GetProperty("skippedByReason").EnumerateArray().Where(x => x.GetProperty("reason").GetString() == reason).Select(x => x.GetProperty("count").GetInt32()).SingleOrDefault();

    // ---- preview ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_preview_gives_the_real_counts_and_the_message_as_a_customer_would_see_it()
    {
        var s = await PrepareAsync();
        await Mail("Nadia Rahman");
        await Mail("Rina Akter");
        await _m.CustomerAsync("No Consent", createdDaysAgo: 5, emailAddress: "noconsent@example.com");
        await _m.CustomerAsync("Withdrawn", createdDaysAgo: 5, email: true);
        await _m.ConsentAsync(await _m.DbAsync(db => db.Customers.Where(c => c.FullName == "Withdrawn").Select(c => c.Id).SingleAsync(Ct)), ConsentType.MarketingEmail, false, DateTimeOffset.UtcNow);
        await _m.CustomerAsync("No Address", createdDaysAgo: 5); // no email address at all
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment, "Email", "Hi {customerName}, new at {shopName}: {link}", "হ্যালো {customerName}, {shopName}-এ নতুন: {link}", "Sale at {shopName}");

        var preview = await _m.PostAsync($"{MarketingHelper.CampaignsUrl}/{campaign}/preview", s.Staff);

        Assert.Equal(5, preview.GetProperty("segmentMembers").GetInt32());
        Assert.Equal(2, preview.GetProperty("recipients").GetInt32());
        Assert.Equal(2, preview.GetProperty("skippedNoConsent").GetInt32()); // No Consent and Withdrawn have an address but no current consent
        Assert.Equal(1, preview.GetProperty("skippedNoContact").GetInt32()); // No Address
        Assert.Equal(0, preview.GetProperty("skippedFrequencyCap").GetInt32());
        var samples = preview.GetProperty("samples").EnumerateArray().ToList();
        Assert.Equal(["en", "bn"], samples.Select(x => x.GetProperty("language").GetString()!).ToArray());
        Assert.Equal("Sale at Juvo BD", samples[0].GetProperty("subject").GetString());
        var en = samples[0].GetProperty("body").GetString()!;
        Assert.StartsWith("Hi ", en);
        Assert.Contains("new at Juvo BD:", en);
        Assert.Contains("Unsubscribe: http", en); // an email always carries the link
        Assert.Contains("হ্যালো", samples[1].GetProperty("body").GetString());
        Assert.Empty(preview.GetProperty("unknownPlaceholders").EnumerateArray());
    }

    // ---- consent ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_campaign_never_reaches_a_person_without_consent_even_when_the_segment_contains_them()
    {
        var s = await PrepareAsync();
        var yes = await Sms("Yes Person");
        var none = await _m.CustomerAsync("Never Asked", createdDaysAgo: 5);
        var withdrew = await Sms("Withdrew");
        await _m.ConsentAsync(withdrew, ConsentType.MarketingSms, false, DateTimeOffset.UtcNow);
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment);

        var sent = await SendAsync(s, campaign);
        await _m.DrainOutboxAsync();

        Assert.Equal("Sent", sent.GetProperty("status").GetString());
        Assert.Single(await CampaignLogsAsync(yes));
        Assert.Empty(await CampaignLogsAsync(none, withdrew)); // not a Pending, Sent or Skipped row: nothing was even written
        var results = await _m.ResultsAsync(s.Staff, campaign);
        Assert.Equal(3, results.GetProperty("recipients").GetInt32());
        Assert.Equal(1, results.GetProperty("sent").GetInt32());
        Assert.Equal(2, Skipped(results, "NoConsent"));
        var recipients = await _m.GetAsync($"{MarketingHelper.CampaignsUrl}/{campaign}/recipients?reason=NoConsent", s.Staff);
        Assert.Equal(2, recipients.GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Consent_withdrawn_after_the_recipients_were_fixed_still_stops_the_message()
    {
        var s = await PrepareAsync();
        var stays = await Sms("Stays");
        var leaves = await Sms("Leaves");
        await _m.SetQuietAsync(true);
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment);
        var started = await SendAsync(s, campaign); // quiet hours: recipients are fixed, nothing is sent yet
        Assert.True(started.GetProperty("deferredByQuietHours").GetBoolean());
        Assert.Equal(2, started.GetProperty("counts").GetProperty("pending").GetInt32());

        await _m.ConsentAsync(leaves, ConsentType.MarketingSms, false, DateTimeOffset.UtcNow);
        await _m.SetQuietAsync(false);
        await _m.SweepCampaignsAsync();
        await _m.DrainOutboxAsync();

        var logs = await CampaignLogsAsync(stays, leaves);
        Assert.Equal(NotificationStatus.Sent, Assert.Single(logs, l => l.CustomerId == stays).Status);
        Assert.Equal(NotificationStatus.Skipped, Assert.Single(logs, l => l.CustomerId == leaves).Status); // recorded as skipped, never queued
        var results = await _m.ResultsAsync(s.Staff, campaign);
        Assert.Equal(1, results.GetProperty("sent").GetInt32());
        Assert.Equal(1, Skipped(results, "NoConsent"));
    }

    // ---- unsubscribe -----------------------------------------------------------------------------------------

    [Fact]
    public async Task The_unsubscribe_link_turns_consent_off_once_and_is_safe_to_open_again()
    {
        var s = await PrepareAsync();
        var leaving = await Mail("Leaving Customer");
        var staying = await Mail("Staying Customer");
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment, "Email");
        await SendAsync(s, campaign);
        var body = Assert.Single(await CampaignLogsAsync(leaving)).Body;
        var token = MarketingHelper.TokenIn(body);

        var first = await _m.GetAsync($"/api/v1/store/unsubscribe?token={token}", string.Empty);
        var second = await _m.H.JsonAsync(HttpMethod.Post, $"/api/v1/store/unsubscribe?token={token}", HttpStatusCode.OK);

        Assert.Equal("email", first.GetProperty("channel").GetString());
        Assert.True(first.GetProperty("unsubscribed").GetBoolean());
        Assert.False(first.GetProperty("alreadyUnsubscribed").GetBoolean());
        Assert.True(second.GetProperty("alreadyUnsubscribed").GetBoolean());
        var rows = await _m.DbAsync(db => db.ConsentLogs.AsNoTracking().Where(l => l.CustomerId == leaving && l.Type == ConsentType.MarketingEmail).OrderBy(l => l.At).ToListAsync(Ct));
        Assert.Equal(2, rows.Count); // the grant and exactly one withdrawal, with its time and source
        Assert.False(rows[1].Granted);
        Assert.Equal($"unsubscribe:{campaign:N}", rows[1].Source);

        var consented = await _m.CountAsync(s.Staff, MarketingHelper.Rules("and", new { type = "consent", channel = "email" }));
        Assert.Equal(1, consented.GetProperty("total").GetInt32()); // only Staying Customer is left
        var results = await _m.ResultsAsync(s.Staff, campaign);
        Assert.Equal(1, results.GetProperty("unsubscribed").GetInt32());

        var again = await _m.CampaignAsync(s.Staff, s.Segment, "Email", name: StoreHelper.Unique("Second"));
        await SendAsync(s, again);
        Assert.DoesNotContain(await CampaignLogsAsync(leaving), l => l.DedupeKey!.Contains(again.ToString("N"), StringComparison.Ordinal));
        Assert.Equal(1, Skipped(await _m.ResultsAsync(s.Staff, again), "NoConsent"));
        Assert.NotEqual(Guid.Empty, staying);
    }

    [Fact]
    public async Task An_sms_carries_a_short_opt_out_that_turns_off_sms_only()
    {
        var s = await PrepareAsync();
        var both = await _m.CustomerAsync("Both Channels", createdDaysAgo: 5, sms: true, email: true);
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment, "Sms", "Sale {customerName}");
        await SendAsync(s, campaign);
        var body = Assert.Single(await CampaignLogsAsync(both)).Body;

        Assert.Contains("Stop: http", body);
        Assert.StartsWith("Sale Both", body);
        var result = await _m.GetAsync($"/api/v1/store/unsubscribe?token={MarketingHelper.TokenIn(body)}", string.Empty);

        Assert.Equal("sms", result.GetProperty("channel").GetString());
        Assert.Equal(0, (await _m.CountAsync(s.Staff, MarketingHelper.Rules("and", new { type = "consent", channel = "sms" }))).GetProperty("total").GetInt32());
        Assert.Equal(1, (await _m.CountAsync(s.Staff, MarketingHelper.Rules("and", new { type = "consent", channel = "email" }))).GetProperty("total").GetInt32()); // email consent is untouched
    }

    [Fact]
    public async Task A_damaged_or_missing_unsubscribe_token_is_refused()
    {
        var s = await PrepareAsync();
        var person = await Sms("Token Person");
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment);
        await SendAsync(s, campaign);
        var token = MarketingHelper.TokenIn(Assert.Single(await CampaignLogsAsync(person)).Body);
        var tampered = token[..^3] + (token.EndsWith("AAA", StringComparison.Ordinal) ? "BBB" : "AAA");

        foreach (var bad in new[] { tampered, "garbage", "a.b", string.Empty })
        {
            await StoreHelper.ExpectCodeAsync(await _m.H.SendAsync(HttpMethod.Get, $"/api/v1/store/unsubscribe?token={bad}"), "MARKETING.UNSUBSCRIBE_TOKEN_INVALID");
        }

        await StoreHelper.ExpectCodeAsync(await _m.H.SendAsync(HttpMethod.Post, "/api/v1/store/unsubscribe"), "MARKETING.UNSUBSCRIBE_TOKEN_INVALID");
        Assert.True(await _m.DbAsync(db => db.ConsentLogs.AnyAsync(l => l.CustomerId == person && l.Granted, Ct))); // nothing changed
    }

    // ---- quiet hours, cap, throttle ---------------------------------------------------------------------------

    [Fact]
    public async Task Quiet_hours_hold_sms_back_until_they_end_but_not_email()
    {
        var s = await PrepareAsync();
        var both = await _m.CustomerAsync("Night Owl", createdDaysAgo: 5, sms: true, email: true);
        await _m.SetQuietAsync(true);
        var sms = await _m.CampaignAsync(s.Staff, s.Segment, "Sms", name: StoreHelper.Unique("Night sms"));
        var email = await _m.CampaignAsync(s.Staff, s.Segment, "Email", name: StoreHelper.Unique("Night mail"));

        var smsSent = await SendAsync(s, sms);
        var emailSent = await SendAsync(s, email);

        Assert.Equal("Sending", smsSent.GetProperty("status").GetString());
        Assert.True(smsSent.GetProperty("deferredByQuietHours").GetBoolean());
        Assert.Equal("Sent", emailSent.GetProperty("status").GetString());
        var logs = await CampaignLogsAsync(both);
        Assert.Equal(NotificationChannel.Email, Assert.Single(logs).Channel); // the SMS waits
        var preview = await _m.PostAsync($"{MarketingHelper.CampaignsUrl}/{sms}/preview", s.Staff);
        Assert.True(preview.GetProperty("quietHoursNow").GetBoolean());
        Assert.True(preview.GetProperty("smsDeferred").GetBoolean());

        await _m.SweepCampaignsAsync(); // still quiet: nothing moves
        Assert.Single(await CampaignLogsAsync(both));

        await _m.SetQuietAsync(false);
        await _m.SweepCampaignsAsync();

        Assert.Equal(2, (await CampaignLogsAsync(both)).Count);
        Assert.Equal("Sent", (await _m.GetAsync($"{MarketingHelper.CampaignsUrl}/{sms}", s.Staff)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task The_frequency_cap_skips_a_customer_who_already_received_enough_messages()
    {
        var s = await PrepareAsync();
        var person = await Sms("Capped Person");
        var other = await Sms("Fresh Person");
        await _m.PutAsync(MarketingHelper.SettingsUrl, s.Staff, new { frequencyCapMessages = 1, frequencyCapDays = 7 });
        var first = await _m.CampaignAsync(s.Staff, await _m.SegmentAsync(s.Staff, MarketingHelper.Rules("and", new { type = "consent", channel = "sms" }), "Just first"), name: StoreHelper.Unique("One"));
        await SendAsync(s, first);
        var third = await _m.DbAsync(db => db.NotificationLogs.Where(l => l.CustomerId == other).ExecuteDeleteAsync(Ct)); // Fresh Person never got the first one
        Assert.Equal(1, third);

        var second = await _m.CampaignAsync(s.Staff, s.Segment, name: StoreHelper.Unique("Two"));
        var preview = await _m.PostAsync($"{MarketingHelper.CampaignsUrl}/{second}/preview", s.Staff);
        Assert.Equal(1, preview.GetProperty("skippedFrequencyCap").GetInt32()); // Capped Person
        Assert.Equal(1, preview.GetProperty("recipients").GetInt32()); // Fresh Person
        await SendAsync(s, second);

        var results = await _m.ResultsAsync(s.Staff, second);
        Assert.Equal(1, Skipped(results, "FrequencyCap"));
        Assert.Equal(1, results.GetProperty("sent").GetInt32() + results.GetProperty("inFlight").GetInt32());
        Assert.Single(await CampaignLogsAsync(person), l => l.Status != NotificationStatus.Skipped); // still only the first message
    }

    [Fact]
    public async Task The_throttle_sends_at_most_the_allowed_number_per_minute_and_the_worker_carries_on()
    {
        var s = await PrepareAsync();
        var people = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            people.Add(await Sms($"Throttled {i}"));
        }

        await _m.PutAsync(MarketingHelper.SettingsUrl, s.Staff, new { throttlePerMinute = 2, frequencyCapMessages = 0 });
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment);

        var started = await SendAsync(s, campaign);
        Assert.Equal("Sending", started.GetProperty("status").GetString());
        Assert.Equal(2, started.GetProperty("counts").GetProperty("queued").GetInt32());
        await _m.SweepCampaignsAsync(); // the same minute: no more
        Assert.Equal(2, (await CampaignLogsAsync([.. people])).Count);

        for (var round = 0; round < 3; round++)
        {
            await _m.DbAsync(db => db.CampaignRecipients.Where(r => r.CampaignId == campaign).ExecuteUpdateAsync(s2 => s2.SetProperty(r => r.ProcessedAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-5)), Ct));
            await _m.SweepCampaignsAsync();
        }

        Assert.Equal(5, (await CampaignLogsAsync([.. people])).Count);
        Assert.Equal("Sent", (await _m.GetAsync($"{MarketingHelper.CampaignsUrl}/{campaign}", s.Staff)).GetProperty("status").GetString());
    }

    // ---- dedupe and resume -----------------------------------------------------------------------------------

    [Fact]
    public async Task A_customer_reachable_in_two_ways_is_messaged_once()
    {
        var s = await PrepareAsync();
        var person = await _m.CustomerAsync("Twice Reachable", createdDaysAgo: 5, sms: true);
        await _m.WishAsync(person, (await _m.ProductAsync()).Id);
        var segment = await _m.SegmentAsync(s.Staff, MarketingHelper.Rules("or", new { type = "newCustomers", days = 30 }, new { type = "wishlist" }, new { type = "consent", channel = "sms" }));
        var campaign = await _m.CampaignAsync(s.Staff, segment);

        await SendAsync(s, campaign);
        await _m.SweepCampaignsAsync();
        await _m.SweepCampaignsAsync();

        Assert.Single(await CampaignLogsAsync(person));
        Assert.Equal(1, (await _m.ResultsAsync(s.Staff, campaign)).GetProperty("recipients").GetInt32());
    }

    [Fact]
    public async Task A_run_that_stopped_half_way_resumes_without_sending_twice()
    {
        var s = await PrepareAsync();
        var c1 = await Sms("Resume One");
        var c2 = await Sms("Resume Two");
        var c3 = await Sms("Resume Three");
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment);
        var mobile2 = await _m.DbAsync(db => db.Customers.Where(c => c.Id == c2).Select(c => c.Mobile).SingleAsync(Ct));
        var preQueued = await _m.DbAsync(async db =>
        {
            // The crash: the first recipient row exists, and the second customer's message was queued but its recipient row was never marked.
            db.CampaignRecipients.Add(new CampaignRecipient(campaign, c1, "x"));
            var log = NotificationLog.Queue(NotificationChannel.Sms, mobile2, CampaignDispatchService.TemplateCode, null, "earlier text", "en", 0, NotificationCategory.Marketing, c2, CampaignDispatchService.DedupeKey(campaign, c2), null, DateTimeOffset.UtcNow);
            db.NotificationLogs.Add(log);
            await db.Campaigns.Where(c => c.Id == campaign).ExecuteUpdateAsync(set => set.SetProperty(c => c.Status, CampaignStatus.Sending), Ct);
            await db.SaveChangesAsync(Ct);
            return log.Id;
        });

        await _m.SweepCampaignsAsync(); // the worker picks the Sending campaign up
        await _m.SweepCampaignsAsync();

        var logs = await CampaignLogsAsync(c1, c2, c3);
        Assert.Equal(3, logs.Count); // one message each, no double
        Assert.Equal("earlier text", Assert.Single(logs, l => l.CustomerId == c2).Body); // the earlier message was adopted, not replaced
        var recipients = await _m.DbAsync(db => db.CampaignRecipients.AsNoTracking().Where(r => r.CampaignId == campaign).ToListAsync(Ct));
        Assert.Equal(3, recipients.Count);
        Assert.Equal(preQueued, Assert.Single(recipients, r => r.CustomerId == c2).NotificationLogId);
        Assert.All(recipients, r => Assert.Equal(RecipientStatus.Queued, r.Status));
        Assert.Equal("Sent", (await _m.GetAsync($"{MarketingHelper.CampaignsUrl}/{campaign}", s.Staff)).GetProperty("status").GetString());
    }

    // ---- schedule, cancel -------------------------------------------------------------------------------------

    [Fact]
    public async Task A_scheduled_campaign_is_started_by_the_worker_when_its_time_has_come()
    {
        var s = await PrepareAsync();
        var person = await Sms("Scheduled Person");
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment);
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Post, $"{MarketingHelper.CampaignsUrl}/{campaign}/schedule", s.Staff, new { scheduledAt = DateTimeOffset.UtcNow.AddHours(-1) }), "MARKETING.SCHEDULE_INVALID");

        var at = DateTimeOffset.UtcNow.AddHours(2).ToOffset(TimeSpan.FromHours(6));
        var scheduled = await _m.PostAsync($"{MarketingHelper.CampaignsUrl}/{campaign}/schedule", s.Staff, new { scheduledAt = at });
        Assert.Equal("Scheduled", scheduled.GetProperty("status").GetString());
        Assert.EndsWith("(Asia/Dhaka)", scheduled.GetProperty("scheduledAtDhaka").GetString());
        await _m.SweepCampaignsAsync();
        Assert.Empty(await CampaignLogsAsync(person)); // not yet

        await _m.DbAsync(db => db.Campaigns.Where(c => c.Id == campaign).ExecuteUpdateAsync(set => set.SetProperty(c => c.ScheduledAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));
        var sweep = await _m.SweepCampaignsAsync();

        Assert.Equal(1, sweep.Started);
        Assert.Single(await CampaignLogsAsync(person));
        Assert.Equal("Sent", (await _m.GetAsync($"{MarketingHelper.CampaignsUrl}/{campaign}", s.Staff)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_cancelled_campaign_sends_nothing_more()
    {
        var s = await PrepareAsync();
        var person = await Sms("Cancelled Person");
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment);
        await _m.PostAsync($"{MarketingHelper.CampaignsUrl}/{campaign}/schedule", s.Staff, new { scheduledAt = DateTimeOffset.UtcNow.AddHours(2) });

        var cancelled = await _m.PostAsync($"{MarketingHelper.CampaignsUrl}/{campaign}/cancel", s.Staff);
        await _m.DbAsync(db => db.Campaigns.Where(c => c.Id == campaign).ExecuteUpdateAsync(set => set.SetProperty(c => c.ScheduledAt, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-1)), Ct));
        await _m.SweepCampaignsAsync();

        Assert.Equal("Cancelled", cancelled.GetProperty("status").GetString());
        Assert.Empty(await CampaignLogsAsync(person));
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Post, $"{MarketingHelper.CampaignsUrl}/{campaign}/send", s.Staff), "MARKETING.CAMPAIGN_INVALID_STATE");

        // Cancelling in the middle of a send: the rest is skipped as Cancelled.
        var late = await Sms("Late Person");
        await _m.SetQuietAsync(true);
        var second = await _m.CampaignAsync(s.Staff, s.Segment, name: StoreHelper.Unique("Mid"));
        await SendAsync(s, second);
        await _m.PostAsync($"{MarketingHelper.CampaignsUrl}/{second}/cancel", s.Staff);
        await _m.SetQuietAsync(false);
        await _m.SweepCampaignsAsync();
        Assert.Empty(await CampaignLogsAsync(late));
        Assert.Equal(2, Skipped(await _m.ResultsAsync(s.Staff, second), "Cancelled"));
    }

    // ---- results and attribution -----------------------------------------------------------------------------

    [Fact]
    public async Task The_results_and_attributed_orders_match_a_hand_calculation()
    {
        var s = await PrepareAsync();
        var p = await Sms("Attr P");
        var q = await Sms("Attr Q");
        var r = await Sms("Attr R");
        var n = await _m.CustomerAsync("Attr N", createdDaysAgo: 5); // no consent
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment);
        await _m.OrderAsync(q, 1, 999m); // before the message: not counted
        await SendAsync(s, campaign);
        await _m.DrainOutboxAsync();

        await _m.OrderAsync(p, -1, 5000m); // 1 day after: counted
        await _m.OrderAsync(p, -2, 2000m, OrderStatus.Cancelled); // cancelled: not counted
        await _m.OrderAsync(q, -3, 3000m); // counted
        await _m.OrderAsync(q, -9, 4000m); // 9 days after: outside the 7-day window
        await _m.OrderAsync(n, -1, 1111m); // never received a message
        Assert.NotEqual(Guid.Empty, r);

        var results = await _m.ResultsAsync(s.Staff, campaign);

        Assert.Equal(4, results.GetProperty("recipients").GetInt32());
        Assert.Equal(3, results.GetProperty("sent").GetInt32());
        Assert.Equal(3, results.GetProperty("delivered").GetInt32());
        Assert.Equal(0, results.GetProperty("failed").GetInt32());
        Assert.Equal(1, Skipped(results, "NoConsent"));
        Assert.Equal(2, results.GetProperty("attribution").GetProperty("orders").GetInt32()); // P 5000 and Q 3000
        Assert.Equal(8000m, results.GetProperty("attribution").GetProperty("revenue").GetDecimal());
        Assert.Equal(7, results.GetProperty("attribution").GetProperty("windowDays").GetInt32());

        var list = await _m.GetAsync($"{MarketingHelper.CampaignsUrl}?search={Uri.EscapeDataString(results.GetProperty("name").GetString()!)}", s.Staff);
        var row = Assert.Single(list.GetProperty("items").EnumerateArray());
        Assert.Equal(8000m, row.GetProperty("attributedRevenue").GetDecimal());
        Assert.Equal(2, row.GetProperty("attributedOrders").GetInt32());
        Assert.Equal(3, row.GetProperty("sent").GetInt32());

        await _m.PutAsync(MarketingHelper.SettingsUrl, s.Staff, new { attributionWindowDays = 10 });
        var wider = (await _m.ResultsAsync(s.Staff, campaign)).GetProperty("attribution");
        Assert.Equal(3, wider.GetProperty("orders").GetInt32());
        Assert.Equal(12000m, wider.GetProperty("revenue").GetDecimal());

        // A failed message is counted as failed, not as sent.
        var failedLog = (await CampaignLogsAsync(p)).Single().Id;
        await _m.DbAsync(db => db.NotificationLogs.Where(l => l.Id == failedLog).ExecuteUpdateAsync(set => set.SetProperty(l => l.Status, NotificationStatus.DeadLetter), Ct));
        var after = await _m.ResultsAsync(s.Staff, campaign);
        Assert.Equal(2, after.GetProperty("sent").GetInt32());
        Assert.Equal(1, after.GetProperty("failed").GetInt32());
    }

    [Fact]
    public async Task An_order_is_credited_to_the_newest_message_before_it()
    {
        var s = await PrepareAsync();
        var t = await Sms("Touched Twice");
        var first = await _m.CampaignAsync(s.Staff, s.Segment, name: StoreHelper.Unique("First"));
        var second = await _m.CampaignAsync(s.Staff, s.Segment, name: StoreHelper.Unique("Second"));
        await SendAsync(s, first);
        await _m.DrainOutboxAsync();
        await Task.Delay(50, Ct);
        await SendAsync(s, second);
        await _m.DrainOutboxAsync();
        await _m.OrderAsync(t, -1, 2500m);

        var one = (await _m.ResultsAsync(s.Staff, first)).GetProperty("attribution");
        var two = (await _m.ResultsAsync(s.Staff, second)).GetProperty("attribution");

        Assert.Equal(0, one.GetProperty("orders").GetInt32());
        Assert.Equal(1, two.GetProperty("orders").GetInt32());
        Assert.Equal(2500m, two.GetProperty("revenue").GetDecimal());
    }

    // ---- test message, texts, permissions ---------------------------------------------------------------------

    [Fact]
    public async Task A_test_message_goes_to_the_staff_members_own_contact_and_is_not_counted()
    {
        var s = await PrepareAsync();
        await Sms("Someone");
        var email = await _m.CampaignAsync(s.Staff, s.Segment, "Email", subjectEn: "Winter sale");
        var sms = await _m.CampaignAsync(s.Staff, s.Segment, "Sms");

        var sent = await _m.PostAsync($"{MarketingHelper.CampaignsUrl}/{email}/test", s.Staff, new { language = "en" });

        Assert.Equal("Email", sent.GetProperty("channel").GetString());
        Assert.Contains("@example.com", sent.GetProperty("recipient").GetString());
        var log = await _m.DbAsync(db => db.NotificationLogs.AsNoTracking().SingleAsync(l => l.Id == sent.GetProperty("notificationLogId").GetGuid(), Ct));
        Assert.Equal("marketing.campaign-test", log.Template);
        Assert.Equal(NotificationCategory.Transactional, log.Category);
        Assert.Equal("[Test] Winter sale", log.Subject);
        Assert.Equal(0, (await _m.ResultsAsync(s.Staff, email)).GetProperty("recipients").GetInt32());

        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Post, $"{MarketingHelper.CampaignsUrl}/{sms}/test", s.Staff, new { }), "MARKETING.TEST_NO_CONTACT");
        var staffId = await _m.DbAsync(db => db.Users.Where(u => u.Email != null && u.Email == sent.GetProperty("recipient").GetString()).Select(u => u.Id).SingleAsync(Ct));
        await _m.DbAsync(db => db.Users.Where(u => u.Id == staffId).ExecuteUpdateAsync(set => set.SetProperty(u => u.PhoneNumber, "+8801700000000"), Ct));
        var smsSent = await _m.PostAsync($"{MarketingHelper.CampaignsUrl}/{sms}/test", s.Staff, new { });
        Assert.Equal("+8801700000000", smsSent.GetProperty("recipient").GetString());
    }

    [Fact]
    public async Task A_customer_gets_the_text_of_their_language_with_their_name_filled_in()
    {
        var s = await PrepareAsync();
        var bangla = await Sms("Nadia Rahman", "bn");
        var english = await Sms("Rina Akter", "en");
        var fallback = await Sms("Karim Uddin", "bn");
        var campaign = await _m.CampaignAsync(s.Staff, s.Segment, bodyEn: "Hello {customerName}, visit {shopName}", bodyBn: "হ্যালো {customerName}, {shopName} দেখুন");
        await SendAsync(s, campaign);

        var logs = await CampaignLogsAsync(bangla, english, fallback);

        Assert.StartsWith("হ্যালো Nadia, Juvo BD দেখুন", Assert.Single(logs, l => l.CustomerId == bangla).Body);
        Assert.StartsWith("Hello Rina, visit Juvo BD", Assert.Single(logs, l => l.CustomerId == english).Body);
        Assert.Equal("bn", Assert.Single(logs, l => l.CustomerId == fallback).Language);
        Assert.All(logs, l => Assert.Equal(NotificationCategory.Marketing, l.Category));
    }

    [Fact]
    public async Task Bad_campaign_data_and_wrong_states_are_refused_with_stable_codes()
    {
        var s = await PrepareAsync();
        var person = await Sms("Someone Else");
        var url = MarketingHelper.CampaignsUrl;

        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Post, url, s.Staff, new { name = "X", segmentId = s.Segment, channel = "Sms", bodyEn = "Hi {nickname}" }), "MARKETING.CAMPAIGN_INVALID");
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Post, url, s.Staff, new { name = "X", segmentId = s.Segment, channel = "Email", bodyEn = "Hi" }), "MARKETING.CAMPAIGN_INVALID"); // subject missing
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Post, url, s.Staff, new { name = "X", segmentId = s.Segment, channel = "WhatsApp", bodyEn = "Hi" }), "MARKETING.CAMPAIGN_INVALID");
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Post, url, s.Staff, new { name = "X", segmentId = Guid.NewGuid(), channel = "Sms", bodyEn = "Hi" }), "MARKETING.SEGMENT_NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Get, $"{url}/{Guid.NewGuid()}", s.Staff), "MARKETING.CAMPAIGN_NOT_FOUND");

        var campaign = await _m.CampaignAsync(s.Staff, s.Segment);
        await SendAsync(s, campaign);
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Post, $"{url}/{campaign}/send", s.Staff), "MARKETING.CAMPAIGN_INVALID_STATE"); // already sent
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Put, $"{url}/{campaign}", s.Staff, new { name = "Y", segmentId = s.Segment, channel = "Sms", bodyEn = "Hi" }), "MARKETING.CAMPAIGN_NOT_EDITABLE");

        await _m.SetPlatformSettingAsync("Notifications.SmsEnabled", "false");
        var other = await _m.CampaignAsync(s.Staff, s.Segment, name: StoreHelper.Unique("Off"));
        await StoreHelper.ExpectCodeAsync(await _m.TryAsync(HttpMethod.Post, $"{url}/{other}/send", s.Staff), "MARKETING.CHANNEL_OFF");
        Assert.NotEqual(Guid.Empty, person);
        var list = await _m.GetAsync(url, s.Staff);
        Assert.True(list.TryGetProperty("items", out _) && list.TryGetProperty("total", out _) && list.TryGetProperty("page", out _) && list.TryGetProperty("pageSize", out _));
    }

    [Fact]
    public async Task Sending_needs_its_own_permission_and_the_settings_are_validated()
    {
        var s = await PrepareAsync();
        await Sms("Permission Person");
        var creator = await _m.StaffAsync("marketing.campaign.view", "marketing.campaign.create", "marketing.segment.view");
        var sender = await _m.StaffAsync("marketing.campaign.view", "marketing.campaign.send");
        var campaign = await _m.CampaignAsync(creator, s.Segment);

        Assert.Equal(HttpStatusCode.Forbidden, (await _m.TryAsync(HttpMethod.Post, $"{MarketingHelper.CampaignsUrl}/{campaign}/send", creator)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _m.TryAsync(HttpMethod.Post, $"{MarketingHelper.CampaignsUrl}/{campaign}/schedule", creator, new { scheduledAt = DateTimeOffset.UtcNow.AddDays(1) })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _m.TryAsync(HttpMethod.Post, MarketingHelper.CampaignsUrl, sender, new { name = "Z", segmentId = s.Segment, channel = "Sms", bodyEn = "Hi" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _m.TryAsync(HttpMethod.Put, MarketingHelper.SettingsUrl, creator, new { throttlePerMinute = 10 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _m.TryAsync(HttpMethod.Post, $"{MarketingHelper.CampaignsUrl}/{campaign}/send", sender)).StatusCode);

        var bad = await _m.TryAsync(HttpMethod.Put, MarketingHelper.SettingsUrl, sender, new { quietHoursStart = "25:99", throttlePerMinute = 0, smsOptOutText = "no link here" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = JsonDocument.Parse(await bad.Content.ReadAsStringAsync(Ct)).RootElement.GetProperty("errors");
        Assert.True(errors.TryGetProperty("quietHoursStart", out _));
        Assert.True(errors.TryGetProperty("throttlePerMinute", out _));
        Assert.True(errors.TryGetProperty("smsOptOutText", out _));
    }
}
