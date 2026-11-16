using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;

namespace Juvo.Api.IntegrationTests.Operations;

public class SupportCannedReplyTests(OpsFactory factory) : IClassFixture<OpsFactory>
{
    private const string Url = "/api/v1/support/canned-replies";

    private readonly SupportHelper _s = new(factory);

    private StoreHelper H => _s.H;

    private async Task<(string Admin, Guid Ho, string Editor)> SetupAsync()
    {
        var admin = await H.AdminAsync();
        var ho = await _s.O.HoAsync();
        return (admin, ho, await _s.StaffAsync(admin, ho, SupportHelper.AllSupport));
    }

    private Task<JsonElement> CreateAsync(string bearer, string title, string body, string language = "en", string? category = null) =>
        H.JsonAsync(HttpMethod.Post, Url, HttpStatusCode.Created, new { title, body, language, category }, bearer);

    [Fact]
    public async Task Replies_are_kept_in_english_and_in_bengali_and_listed_by_language()
    {
        var (_, _, editor) = await SetupAsync();
        var title = StoreHelper.Unique("Sorry");
        var en = await CreateAsync(editor, title, "Sorry {customerName}, your order {orderNumber} is late.", "en", "delivery");
        var bn = await CreateAsync(editor, title, "দুঃখিত {customerName}, আপনার অর্ডার {orderNumber} দেরি হচ্ছে।", "bn", "delivery");

        Assert.Equal(["customerName", "orderNumber"], en.GetProperty("placeholders").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal("bn", bn.GetProperty("language").GetString());

        var onlyBn = await H.JsonAsync(HttpMethod.Get, $"{Url}?language=bn", HttpStatusCode.OK, bearer: editor);
        Assert.Contains(onlyBn.EnumerateArray(), r => r.GetProperty("id").GetGuid() == bn.GetProperty("id").GetGuid());
        Assert.DoesNotContain(onlyBn.EnumerateArray(), r => r.GetProperty("id").GetGuid() == en.GetProperty("id").GetGuid());
        var all = await H.JsonAsync(HttpMethod.Get, $"{Url}?category=delivery", HttpStatusCode.OK, bearer: editor);
        Assert.Equal(2, all.EnumerateArray().Count(r => r.GetProperty("title").GetString() == title));
    }

    [Fact]
    public async Task A_reply_can_be_edited_switched_off_and_deleted_and_titles_are_unique_per_language()
    {
        var (_, _, editor) = await SetupAsync();
        var title = StoreHelper.Unique("Thanks");
        var reply = await CreateAsync(editor, title, "Thank you.");
        var id = reply.GetProperty("id").GetGuid();

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, Url, new { title, body = "Again", language = "en" }, editor), "GENERAL.CONFLICT");
        await CreateAsync(editor, title, "ধন্যবাদ।", "bn"); // the same title in the other language is fine

        var changed = await H.JsonAsync(HttpMethod.Put, $"{Url}/{id}", HttpStatusCode.OK, new { title, body = "Thank you, {customerName}.", language = "en", isActive = false }, editor);
        Assert.False(changed.GetProperty("isActive").GetBoolean());
        var active = await H.JsonAsync(HttpMethod.Get, Url, HttpStatusCode.OK, bearer: editor);
        Assert.DoesNotContain(active.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);
        var withInactive = await H.JsonAsync(HttpMethod.Get, Url + "?includeInactive=true", HttpStatusCode.OK, bearer: editor);
        Assert.Contains(withInactive.EnumerateArray(), r => r.GetProperty("id").GetGuid() == id);

        Assert.Equal(HttpStatusCode.NoContent, (await H.SendAsync(HttpMethod.Delete, $"{Url}/{id}", bearer: editor)).StatusCode);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Delete, $"{Url}/{id}", bearer: editor), "TICKET.CANNED_REPLY_NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, $"{Url}/{Guid.NewGuid()}", new { title = "x", body = "y", language = "en" }, editor), "TICKET.CANNED_REPLY_NOT_FOUND");
    }

    [Theory]
    [InlineData("", "Body", "en")]
    [InlineData("Title", "", "en")]
    [InlineData("Title", "Body", "fr")]
    [InlineData("Title", "Hello {customerNme}", "en")]
    public async Task A_reply_needs_a_title_a_body_a_known_language_and_only_known_placeholders(string title, string body, string language)
    {
        var (_, _, editor) = await SetupAsync();

        var response = await H.SendAsync(HttpMethod.Post, Url, new { title = title.Length == 0 ? title : StoreHelper.Unique(title), body, language }, editor);

        await StoreHelper.ExpectCodeAsync(response, "GENERAL.VALIDATION");
    }

    [Fact]
    public async Task Rendering_fills_the_placeholders_from_the_ticket_in_both_languages()
    {
        var (_, _, editor) = await SetupAsync();
        var ticket = await _s.StaffCreateAsync(editor, StoreHelper.NewMobile(), subject: "Late", message: "Where is it?", category: "delivery");
        var id = ticket.GetProperty("id").GetGuid();
        var number = ticket.GetProperty("number").GetString()!;
        var en = await CreateAsync(editor, StoreHelper.Unique("En"), "Hello {customerName}, ticket {ticketNumber}.");
        var bn = await CreateAsync(editor, StoreHelper.Unique("Bn"), "হ্যালো {customerName}, টিকিট {ticketNumber}।", "bn");

        var english = await H.JsonAsync(HttpMethod.Post, Url + "/render", HttpStatusCode.OK, new { cannedReplyId = en.GetProperty("id").GetGuid(), ticketId = id }, editor);
        var bengali = await H.JsonAsync(HttpMethod.Post, Url + "/render", HttpStatusCode.OK, new { cannedReplyId = bn.GetProperty("id").GetGuid(), ticketId = id }, editor);

        Assert.Equal($"Hello Walk In, ticket {number}.", english.GetProperty("text").GetString());
        Assert.Equal($"হ্যালো Walk In, টিকিট {number}।", bengali.GetProperty("text").GetString());
        Assert.Equal(0, english.GetProperty("emptyPlaceholders").GetArrayLength());

        // A text that is typed, not saved, is previewed the same way; a placeholder with no value (no order here) is reported.
        var typed = await H.JsonAsync(HttpMethod.Post, Url + "/render", HttpStatusCode.OK, new { body = "Order {orderNumber} for {customerName} {oops}", ticketId = id }, editor);
        Assert.Equal("Order  for Walk In {oops}", typed.GetProperty("text").GetString());
        Assert.Equal(["orderNumber"], typed.GetProperty("emptyPlaceholders").EnumerateArray().Select(p => p.GetString()));
        Assert.Equal(["oops"], typed.GetProperty("unknownPlaceholders").EnumerateArray().Select(p => p.GetString()));

        var noTicket = await H.JsonAsync(HttpMethod.Post, Url + "/render", HttpStatusCode.OK, new { body = "Hi {customerName}" }, editor);
        Assert.Equal("Hi ", noTicket.GetProperty("text").GetString());
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, Url + "/render", new { cannedReplyId = Guid.NewGuid() }, editor), "TICKET.CANNED_REPLY_NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, Url + "/render", new { body = "x", ticketId = Guid.NewGuid() }, editor), "TICKET.NOT_FOUND");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, Url + "/render", new { }, editor), "GENERAL.VALIDATION");
    }

    [Fact]
    public async Task Rendering_for_a_ticket_of_another_branch_looks_like_not_found()
    {
        var (admin, ho, editor) = await SetupAsync();
        var (orderNumber, mobile, _) = await _s.OrderAsync(admin);
        var ticket = await _s.StaffCreateAsync(editor, mobile, orderNumber);
        var other = await H.Api.CreateBranchAsync(admin, "S" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant(), "Canned Branch B");
        var outsider = await _s.StaffAsync(admin, other, SupportHelper.AllSupport);

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, Url + "/render", new { body = "Hi", ticketId = ticket.GetProperty("id").GetGuid() }, outsider), "TICKET.NOT_FOUND");
        Assert.NotEqual(Guid.Empty, ho);
    }

    [Fact]
    public async Task Viewing_and_editing_need_their_own_permissions()
    {
        var (admin, ho, _) = await SetupAsync();
        var viewer = await _s.StaffAsync(admin, ho, "support.canned-reply.view");
        var nobody = await _s.StaffAsync(admin, ho, "support.ticket.view");

        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, Url, bearer: viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Post, Url + "/render", new { body = "x" }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, Url, new { title = "x", body = "y", language = "en" }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Delete, $"{Url}/{Guid.NewGuid()}", bearer: viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, Url, bearer: nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, Url)).StatusCode);
    }
}
