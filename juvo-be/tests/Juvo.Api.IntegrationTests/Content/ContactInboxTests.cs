using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Content;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Content;

/// <summary>Contact form and admin inbox (FR-Q06): public post, validation, honeypot, limits, staff list and handling, rights, masking and audit.</summary>
public class ContactInboxTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private const string Post = "/api/v1/store/content/contact";
    private const string Inbox = "/api/v1/content/messages";

    private readonly TestApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string NewMobile() => "017" + Random.Shared.Next(10_000_000, 99_999_999);

    private static string Tag() => Guid.NewGuid().ToString("N")[..8];

    private async Task<string> AdminAsync() => (await _api.LoginAdminAsync()).Access;

    private async Task<string> StaffAsync(params string[] permissions)
    {
        var admin = await AdminAsync();
        var role = await _api.CreateRoleAsync(admin, "InboxRole" + Tag(), permissions);
        var ho = await _api.BranchIdAsync("HO");
        var user = await _api.CreateUserAsync(admin, $"ib{Tag()}@example.com", "Inbox Staff", false, (ho, role));
        return (await _api.LoginAsync(user.Email, user.Password)).Access;
    }

    private async Task<(string Reference, Guid Id, string Mobile, string Marker)> SendAsync(string? subject = "Parcel")
    {
        var mobile = NewMobile();
        var marker = "msg" + Tag();
        var receipt = await TestApi.ExpectAsync(await _api.PostAsync(Post, new { name = "Nadia " + marker, mobile, email = $"{marker}@example.com", subject, message = "Where is my parcel? " + marker, sourcePage = "/contact" }), HttpStatusCode.Accepted);
        var reference = receipt.GetProperty("reference").GetString()!;
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        var id = await db.ContactMessages.Where(m => m.Message.Contains(marker)).Select(m => m.Id).SingleAsync(Ct);
        return (reference, id, mobile, marker);
    }

    // ---- public form ----

    [Fact]
    public async Task A_visitor_can_send_a_message_and_gets_a_reference_with_202()
    {
        var sent = await SendAsync();

        Assert.Matches("^CM-[0-9A-F]{8}$", sent.Reference);
        await using var scope = factory.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<JuvoDbContext>().ContactMessages.SingleAsync(m => m.Id == sent.Id, Ct);
        Assert.Equal(ContactMessageStatus.New, stored.Status);
        Assert.StartsWith("+8801", stored.Mobile, StringComparison.Ordinal);
        Assert.Equal("/contact", stored.SourcePage);
        Assert.Equal(sent.Reference, stored.Reference);
    }

    [Fact]
    public async Task An_email_alone_is_accepted_and_a_form_with_neither_is_refused()
    {
        var ok = await _api.PostAsync(Post, new { name = "Rima", email = $"rima{Tag()}@example.com", message = "Do you ship to Sylhet?" });
        Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);

        var none = await _api.PostAsync(Post, new { name = "Rima", message = "Do you ship to Sylhet?" });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
        Assert.Equal("CONTENT.INVALID", await TestApi.ErrorCodeAsync(none));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("hello <img src=x onerror=alert(1)>")]
    public async Task Markup_is_rejected_and_nothing_is_stored(string text)
    {
        var marker = Tag();
        var response = await _api.PostAsync(Post, new { name = "Bot " + marker, mobile = NewMobile(), message = text + marker });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True((await TestApi.JsonAsync(response)).GetProperty("errors").TryGetProperty("message", out _));
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<JuvoDbContext>().ContactMessages.AnyAsync(m => m.Message.Contains(marker), Ct));
    }

    [Fact]
    public async Task Limits_and_formats_are_validated_with_field_errors()
    {
        var response = await _api.PostAsync(Post, new { name = "", mobile = "123", email = "nope", subject = new string('s', 201), message = "hi", sourcePage = "https://evil.example" });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var errors = (await TestApi.JsonAsync(response)).GetProperty("errors");
        foreach (var field in new[] { "name", "mobile", "email", "subject", "message", "sourcePage" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"missing error for {field}: {errors}");
        }
    }

    [Fact]
    public async Task A_filled_honeypot_gets_a_normal_receipt_but_nothing_is_stored()
    {
        var marker = Tag();

        var response = await _api.PostAsync(Post, new { name = "Bot", mobile = NewMobile(), message = "Cheap watches " + marker, website = "http://spam.example" });

        var receipt = await TestApi.ExpectAsync(response, HttpStatusCode.Accepted);
        Assert.Matches("^CM-[0-9A-F]{8}$", receipt.GetProperty("reference").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        Assert.False(await scope.ServiceProvider.GetRequiredService<JuvoDbContext>().ContactMessages.AnyAsync(m => m.Message.Contains(marker), Ct));
    }

    [Fact]
    public async Task The_same_mobile_or_email_is_limited_per_hour_and_the_answer_does_not_reveal_customers()
    {
        var mobile = NewMobile();
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await _api.PostAsync(Post, new { name = "Same", mobile, message = "Question number " + i })).StatusCode);
        }

        var limited = await _api.PostAsync(Post, new { name = "Same", mobile, message = "Question number 4" });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal("CONTENT.RATE_LIMITED", await TestApi.ErrorCodeAsync(limited));

        var email = $"limit{Tag()}@example.com";
        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(HttpStatusCode.Accepted, (await _api.PostAsync(Post, new { name = "Same", email, message = "Question number " + i })).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, (await _api.PostAsync(Post, new { name = "Same", email, message = "Question number 4" })).StatusCode);

        // a different sender is still welcome, and the reply shape is the same for anyone
        var other = await TestApi.ExpectAsync(await _api.PostAsync(Post, new { name = "Other", mobile = NewMobile(), message = "Hello there" }), HttpStatusCode.Accepted);
        Assert.Equal(["reference"], other.EnumerateObject().Select(p => p.Name).ToArray());
    }

    // ---- staff inbox ----

    [Fact]
    public async Task Staff_list_filter_search_open_and_handle_a_message()
    {
        var admin = await AdminAsync();
        var sent = await SendAsync("Late parcel " + Tag());

        var all = await TestApi.ExpectAsync(await _api.GetAsync($"{Inbox}?search={sent.Marker}", admin), HttpStatusCode.OK);
        Assert.Equal(1, all.GetProperty("total").GetInt32());
        var item = all.GetProperty("items")[0];
        Assert.Equal(sent.Reference, item.GetProperty("reference").GetString());
        Assert.Equal("New", item.GetProperty("status").GetString());

        var byStatus = await TestApi.ExpectAsync(await _api.GetAsync($"{Inbox}?status=Replied&search={sent.Marker}", admin), HttpStatusCode.OK);
        Assert.Equal(0, byStatus.GetProperty("total").GetInt32());

        var read = await TestApi.ExpectAsync(await _api.PostAsync($"{Inbox}/{sent.Id}/status", new { status = "Read" }, admin), HttpStatusCode.OK);
        Assert.Equal("Read", read.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, read.GetProperty("handledBy").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, read.GetProperty("handledAt").ValueKind);

        var noted = await TestApi.ExpectAsync(await _api.PostAsync($"{Inbox}/{sent.Id}/note", new { note = "Called her, parcel found" }, admin), HttpStatusCode.OK);
        Assert.Contains("Called her, parcel found", noted.GetProperty("internalNote").GetString());

        var replied = await TestApi.ExpectAsync(await _api.PostAsync($"{Inbox}/{sent.Id}/status", new { status = "Replied" }, admin), HttpStatusCode.OK);
        Assert.Equal("Replied", replied.GetProperty("status").GetString());
        Assert.Equal(1, (await TestApi.ExpectAsync(await _api.GetAsync($"{Inbox}?status=Replied&search={sent.Marker}", admin), HttpStatusCode.OK)).GetProperty("total").GetInt32());

        var archived = await TestApi.ExpectAsync(await _api.PostAsync($"{Inbox}/{sent.Id}/archive", null, admin), HttpStatusCode.OK);
        Assert.Equal("Archived", archived.GetProperty("status").GetString());

        var detail = await TestApi.ExpectAsync(await _api.GetAsync($"{Inbox}/{sent.Id}", admin), HttpStatusCode.OK);
        Assert.Contains(sent.Marker, detail.GetProperty("message").GetString());
        Assert.Equal("/contact", detail.GetProperty("sourcePage").GetString());
    }

    [Fact]
    public async Task Bad_input_and_unknown_ids_are_refused()
    {
        var admin = await AdminAsync();
        var sent = await SendAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync($"{Inbox}/{Guid.NewGuid()}", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.PostAsync($"{Inbox}/{Guid.NewGuid()}/archive", null, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.PostAsync($"{Inbox}/{sent.Id}/note", new { note = " " }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.PostAsync($"{Inbox}/{sent.Id}/note", new { note = "<b>x</b>" }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await _api.PostAsync($"{Inbox}/{sent.Id}/status", new { status = "Spam" }, admin)).StatusCode);
    }

    [Fact]
    public async Task Every_inbox_endpoint_needs_its_permission()
    {
        var sent = await SendAsync();
        var viewer = await StaffAsync("content.message.view");
        var handler = await StaffAsync("content.message.handle");
        var nobody = await StaffAsync("content.page.view");

        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync(Inbox, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync($"{Inbox}/{sent.Id}", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.PostAsync($"{Inbox}/{sent.Id}/status", new { status = "Read" }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.PostAsync($"{Inbox}/{sent.Id}/note", new { note = "x" }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.PostAsync($"{Inbox}/{sent.Id}/archive", null, viewer)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync(Inbox, handler)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync($"{Inbox}/{sent.Id}", handler)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.PostAsync($"{Inbox}/{sent.Id}/status", new { status = "Read" }, handler)).StatusCode);

        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync(Inbox, nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync(Inbox)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.PostAsync($"{Inbox}/{sent.Id}/archive", null)).StatusCode);
    }

    [Fact]
    public async Task Personal_data_is_masked_without_view_pii_and_unmasked_views_are_audited()
    {
        var sent = await SendAsync();
        var masked = await StaffAsync("content.message.view");
        var full = await StaffAsync("content.message.view", "customers.customer.view-pii");

        var detail = await TestApi.ExpectAsync(await _api.GetAsync($"{Inbox}/{sent.Id}", masked), HttpStatusCode.OK);
        Assert.True(detail.GetProperty("piiMasked").GetBoolean());
        Assert.Contains("****", detail.GetProperty("mobile").GetString());
        Assert.DoesNotContain(sent.Mobile[^8..], detail.GetProperty("mobile").GetString());
        Assert.DoesNotContain(sent.Marker, detail.GetProperty("email").GetString());

        var list = await TestApi.ExpectAsync(await _api.GetAsync($"{Inbox}?search={sent.Marker}", masked), HttpStatusCode.OK);
        Assert.Contains("****", list.GetProperty("items")[0].GetProperty("mobile").GetString());
        // a masked caller cannot find a message by its mobile or email
        Assert.Equal(0, (await TestApi.ExpectAsync(await _api.GetAsync($"{Inbox}?search={sent.Marker}@example.com", masked), HttpStatusCode.OK)).GetProperty("total").GetInt32());

        var before = await ViewedCountAsync(sent.Id);
        var open = await TestApi.ExpectAsync(await _api.GetAsync($"{Inbox}/{sent.Id}", full), HttpStatusCode.OK);
        Assert.False(open.GetProperty("piiMasked").GetBoolean());
        Assert.Equal(sent.Mobile.StartsWith('0') ? "+880" + sent.Mobile[1..] : sent.Mobile, open.GetProperty("mobile").GetString());
        Assert.Equal($"{sent.Marker}@example.com", open.GetProperty("email").GetString());
        Assert.Equal(before + 1, await ViewedCountAsync(sent.Id));
        Assert.Equal(1, (await TestApi.ExpectAsync(await _api.GetAsync($"{Inbox}?search={sent.Marker}@example.com", full), HttpStatusCode.OK)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task Changes_are_audited_without_personal_data_or_note_text()
    {
        var admin = await AdminAsync();
        var sent = await SendAsync();
        await TestApi.ExpectAsync(await _api.PostAsync($"{Inbox}/{sent.Id}/status", new { status = "Read" }, admin), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await _api.PostAsync($"{Inbox}/{sent.Id}/note", new { note = "secret-note-" + sent.Marker }, admin), HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        var logs = await db.AuditLogs.Where(a => a.Entity == nameof(ContactMessage) && a.EntityId == sent.Id.ToString()).ToListAsync(Ct);

        Assert.Contains(logs, a => a.Action == AuditAction.Created);
        Assert.Contains(logs, a => a.Action == AuditAction.Updated && (a.NewValue ?? "").Contains("Read", StringComparison.Ordinal));
        var text = string.Join(' ', logs.Select(a => (a.OldValue ?? "") + (a.NewValue ?? "")));
        Assert.DoesNotContain(sent.Marker, text, StringComparison.Ordinal);
        Assert.DoesNotContain(sent.Mobile[^8..], text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_inbox_permissions_exist_and_the_default_roles_hold_them()
    {
        var admin = await AdminAsync();
        var roles = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/ums/roles?pageSize=100", admin), HttpStatusCode.OK);
        Assert.True(roles.GetProperty("total").GetInt32() >= 7);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        foreach (var roleName in new[] { "Manager", "Support", "Content Editor" })
        {
            var codes = await Codes(db, roleName);
            Assert.Contains("content.message.view", codes);
            Assert.Contains("content.message.handle", codes);
        }

        var orderManager = await Codes(db, "Order Manager");
        Assert.DoesNotContain("content.message.view", orderManager);
    }

    private static Task<List<string>> Codes(JuvoDbContext db, string role) =>
        (from rp in db.RolePermissions join r in db.Roles on rp.RoleId equals r.Id join p in db.Permissions on rp.PermissionId equals p.Id where r.Name == role select p.Code).ToListAsync(Ct);

    private async Task<int> ViewedCountAsync(Guid id)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<JuvoDbContext>().AuditLogs
            .CountAsync(a => a.Entity == nameof(ContactMessage) && a.EntityId == id.ToString() && a.Action == AuditAction.Viewed, Ct);
    }
}

/// <summary>The per-address limit of the public form (policy <c>contact</c>) answers 429 with Retry-After. A separate class so that its window is its own.</summary>
public class ContactAddressLimitTests(ContactAddressLimitTests.LimitedFactory factory) : IClassFixture<ContactAddressLimitTests.LimitedFactory>
{
    public sealed class LimitedFactory : ApiFactory
    {
        protected override void ConfigureSettings()
        {
            Setting("RateLimiting:Enabled", "true");
            Setting("RateLimiting:ContactPerMinute", "2");
            Setting("RateLimiting:GlobalPerMinute", "10000");
        }
    }

    [Fact]
    public async Task The_form_answers_429_once_the_address_has_used_its_minute()
    {
        var api = new TestApi(factory);
        for (var i = 0; i < 2; i++)
        {
            var ok = await api.PostAsync("/api/v1/store/content/contact", new { name = "A", mobile = "017" + Random.Shared.Next(10_000_000, 99_999_999), message = "Hello there " + i });
            Assert.Equal(HttpStatusCode.Accepted, ok.StatusCode);
        }

        var limited = await api.PostAsync("/api/v1/store/content/contact", new { name = "A", mobile = "01711111111", message = "Hello there 3" });

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
    }
}
