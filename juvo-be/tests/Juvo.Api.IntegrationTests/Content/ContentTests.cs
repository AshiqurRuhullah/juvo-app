using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Content;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Content;

/// <summary>Site content (module Q, FR-Q01, Q02, Q03, Q07): staff edit, rights, public reads that hide drafts, seed state and audit.</summary>
public class ContentTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly TestApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string[] Unique(string prefix) => [prefix + Guid.NewGuid().ToString("N")[..8]];

    private async Task<string> AdminAsync() => (await _api.LoginAdminAsync()).Access;

    private async Task<string> StaffAsync(params string[] permissions)
    {
        var admin = await AdminAsync();
        var role = await _api.CreateRoleAsync(admin, Unique("ContentRole")[0], permissions);
        var ho = await _api.BranchIdAsync("HO");
        var user = await _api.CreateUserAsync(admin, $"{Unique("ct")[0]}@example.com", "Content Staff", false, (ho, role));
        return (await _api.LoginAsync(user.Email, user.Password)).Access;
    }

    private Task<HttpResponseMessage> PublicGetAsync(string path) => _api.GetAsync("/api/v1/store/content/" + path);

    // ---- pages ----

    [Fact]
    public async Task A_page_is_edited_published_hidden_when_unpublished_and_audited()
    {
        var admin = await AdminAsync();
        var key = "return-policy";

        Assert.Equal(HttpStatusCode.NotFound, (await PublicGetAsync($"pages/{key}")).StatusCode);

        var saved = await TestApi.ExpectAsync(await _api.PutAsync($"/api/v1/content/pages/{key}", new
        {
            titleEn = "Return policy",
            titleBn = "ফেরত নীতি",
            bodyEn = "# Returns\r\n\r\nSeven days.",
            bodyBn = "সাত দিন।",
            metaTitleEn = "Returns",
            metaDescriptionEn = "How returns work",
        }, admin), HttpStatusCode.OK);
        Assert.False(saved.GetProperty("isPublished").GetBoolean());
        Assert.True(saved.GetProperty("canPublish").GetBoolean());
        Assert.Equal("# Returns\n\nSeven days.", saved.GetProperty("bodyEn").GetString());

        // still a draft for customers
        var draft = await PublicGetAsync($"pages/{key}");
        Assert.Equal(HttpStatusCode.NotFound, draft.StatusCode);
        Assert.Equal("CONTENT.NOT_FOUND", await TestApi.ErrorCodeAsync(draft));

        await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/content/pages/{key}/publish", null, admin), HttpStatusCode.OK);

        var response = await PublicGetAsync($"pages/{key}");
        var page = await TestApi.ExpectAsync(response, HttpStatusCode.OK);
        Assert.Equal("Return policy", page.GetProperty("title").GetProperty("en").GetString());
        Assert.Equal("ফেরত নীতি", page.GetProperty("title").GetProperty("bn").GetString());
        Assert.Equal("সাত দিন।", page.GetProperty("body").GetProperty("bn").GetString());
        Assert.Equal("Returns", page.GetProperty("metaTitle").GetProperty("en").GetString());
        Assert.False(page.TryGetProperty("isPublished", out _)); // nothing staff-only leaks

        // cache friendly: ETag, short max-age and 304 when unchanged
        Assert.Contains("max-age=60", response.Headers.CacheControl!.ToString());
        var etag = response.Headers.ETag!.Tag;
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/v1/store/content/pages/{key}");
        request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        Assert.Equal(HttpStatusCode.NotModified, (await _api.Client.SendAsync(request, Ct)).StatusCode);

        await TestApi.ExpectAsync(await _api.PostAsync($"/api/v1/content/pages/{key}/unpublish", null, admin), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.NotFound, (await PublicGetAsync($"pages/{key}")).StatusCode);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        var id = await db.ContentPages.Where(p => p.Key == key).Select(p => p.Id).SingleAsync(Ct);
        var audited = await db.AuditLogs.CountAsync(a => a.Entity == nameof(ContentPage) && a.EntityId == id.ToString() && a.Action == AuditAction.Updated, Ct);
        Assert.True(audited >= 2, $"expected the edit and the publish to be audited, found {audited}");
    }

    [Fact]
    public async Task An_empty_page_cannot_be_published()
    {
        var admin = await AdminAsync();

        var response = await _api.PostAsync("/api/v1/content/pages/faq/publish", null, admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("CONTENT.PAGE_INCOMPLETE", await TestApi.ErrorCodeAsync(response));
        Assert.Equal(HttpStatusCode.NotFound, (await PublicGetAsync("pages/faq")).StatusCode);
    }

    [Theory]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("hello <img src=x onerror=alert(1)>")]
    [InlineData("[x](javascript:alert(1))")]
    public async Task Unsafe_markdown_is_rejected_and_nothing_is_stored(string body)
    {
        var admin = await AdminAsync();

        var response = await _api.PutAsync("/api/v1/content/pages/terms", new { titleEn = "Terms", bodyEn = body }, admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("CONTENT.BODY_UNSAFE", await TestApi.ErrorCodeAsync(response));
        var stored = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/content/pages/terms", admin), HttpStatusCode.OK);
        Assert.DoesNotContain("script", stored.GetProperty("bodyEn").ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task An_unknown_page_key_is_not_found_for_staff_and_the_public()
    {
        var admin = await AdminAsync();

        Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync("/api/v1/content/pages/pricing", admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.PutAsync("/api/v1/content/pages/pricing", new { titleEn = "x" }, admin)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await PublicGetAsync("pages/pricing")).StatusCode);
    }

    [Fact]
    public async Task Emptying_a_published_page_takes_it_off_the_shop()
    {
        var admin = await AdminAsync();
        await TestApi.ExpectAsync(await _api.PutAsync("/api/v1/content/pages/about", new { titleEn = "About", bodyEn = "Story" }, admin), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/content/pages/about/publish", null, admin), HttpStatusCode.OK);
        Assert.Equal(HttpStatusCode.OK, (await PublicGetAsync("pages/about")).StatusCode);

        var saved = await TestApi.ExpectAsync(await _api.PutAsync("/api/v1/content/pages/about", new { titleEn = "About", bodyEn = "" }, admin), HttpStatusCode.OK);

        Assert.False(saved.GetProperty("isPublished").GetBoolean());
        Assert.Equal(HttpStatusCode.NotFound, (await PublicGetAsync("pages/about")).StatusCode);
    }

    // ---- brand ----

    [Fact]
    public async Task The_brand_profile_is_validated_saved_and_shown_publicly()
    {
        var admin = await AdminAsync();

        var bad = await _api.PutAsync("/api/v1/content/brand", new { businessName = "", email = "nope", whatsApp = "123", latitude = 91m, longitude = 90m, logoFileRef = "../x" }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        var errors = (await TestApi.JsonAsync(bad)).GetProperty("errors");
        foreach (var field in new[] { "businessName", "email", "whatsApp", "latitude", "logoFileRef" })
        {
            Assert.True(errors.TryGetProperty(field, out _), $"missing error for {field}: {errors}");
        }

        var good = await TestApi.ExpectAsync(await _api.PutAsync("/api/v1/content/brand", new
        {
            businessName = "Juvo BD",
            logoFileRef = "content/logo.webp",
            addressEn = "Dhaka",
            addressBn = "ঢাকা",
            phone = "+880 1712-345678",
            whatsApp = "01712345678",
            email = "juvobd@gmail.com",
            workingHoursEn = "10am to 8pm",
            latitude = 23.8103m,
            longitude = 90.4125m,
        }, admin), HttpStatusCode.OK);
        Assert.Equal("+8801712345678", good.GetProperty("whatsApp").GetString());

        var response = await PublicGetAsync("brand");
        var brand = await TestApi.ExpectAsync(response, HttpStatusCode.OK);
        Assert.Equal("/files/content/logo.webp", brand.GetProperty("logoUrl").GetString());
        Assert.Equal("ঢাকা", brand.GetProperty("address").GetProperty("bn").GetString());
        Assert.Equal("10am to 8pm", brand.GetProperty("workingHours").GetProperty("en").GetString());
        Assert.Equal(23.8103m, brand.GetProperty("latitude").GetDecimal());
        Assert.NotNull(response.Headers.ETag);
    }

    // ---- social links ----

    [Fact]
    public async Task Social_links_are_https_only_and_the_public_sees_only_enabled_ones()
    {
        var admin = await AdminAsync();

        var http = await _api.PutAsync("/api/v1/content/social/instagram", new { url = "http://instagram.com/juvobd", isEnabled = true, sortOrder = 2 }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, http.StatusCode);
        Assert.Equal("CONTENT.URL_INVALID", await TestApi.ErrorCodeAsync(http));
        Assert.Equal(HttpStatusCode.NotFound, (await _api.PutAsync("/api/v1/content/social/myspace", new { url = "https://myspace.com/x", isEnabled = true, sortOrder = 1 }, admin)).StatusCode);

        await TestApi.ExpectAsync(await _api.PutAsync("/api/v1/content/social/instagram", new { url = "https://instagram.com/juvobd", isEnabled = true, sortOrder = 3 }, admin), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await _api.PutAsync("/api/v1/content/social/tiktok", new { url = "https://tiktok.com/@juvobd", isEnabled = false, sortOrder = 4 }, admin), HttpStatusCode.OK);
        var whatsapp = await TestApi.ExpectAsync(await _api.PutAsync("/api/v1/content/social/whatsapp", new { url = "01712345678", isEnabled = true, sortOrder = 2 }, admin), HttpStatusCode.OK);
        Assert.Equal("https://wa.me/8801712345678", whatsapp.GetProperty("url").GetString());

        var shown = (await TestApi.ExpectAsync(await PublicGetAsync("social"), HttpStatusCode.OK)).EnumerateArray().Select(s => s.GetProperty("platform").GetString()).ToArray();
        Assert.Equal(["Facebook", "WhatsApp", "Instagram"], shown);

        await TestApi.ExpectAsync(await _api.DeleteAsync("/api/v1/content/social/instagram", admin), HttpStatusCode.NoContent);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.DeleteAsync("/api/v1/content/social/instagram", admin)).StatusCode);
        shown = (await TestApi.ExpectAsync(await PublicGetAsync("social"), HttpStatusCode.OK)).EnumerateArray().Select(s => s.GetProperty("platform").GetString()).ToArray();
        Assert.DoesNotContain("Instagram", shown);
    }

    // ---- badges ----

    [Fact]
    public async Task A_badge_needs_text_to_be_enabled_and_the_public_sees_only_enabled_ones()
    {
        var admin = await AdminAsync();

        var empty = await _api.PutAsync("/api/v1/content/badges/secure-payment", new { isEnabled = true, sortOrder = 1 }, admin);
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _api.PutAsync("/api/v1/content/badges/free-gift", new { textEn = "x", isEnabled = false, sortOrder = 1 }, admin)).StatusCode);

        await TestApi.ExpectAsync(await _api.PutAsync("/api/v1/content/badges/support-hours", new { textEn = "Support 10am to 8pm", textBn = "সহায়তা", isEnabled = true, sortOrder = 5 }, admin), HttpStatusCode.OK);
        await TestApi.ExpectAsync(await _api.PutAsync("/api/v1/content/badges/delivery-coverage", new { textEn = "Delivery all over Bangladesh", isEnabled = false, sortOrder = 2 }, admin), HttpStatusCode.OK);

        var shown = await TestApi.ExpectAsync(await PublicGetAsync("badges"), HttpStatusCode.OK);
        var badge = Assert.Single(shown.EnumerateArray());
        Assert.Equal("support-hours", badge.GetProperty("key").GetString());
        Assert.Equal("সহায়তা", badge.GetProperty("text").GetProperty("bn").GetString());
    }

    // ---- rights ----

    [Fact]
    public async Task Staff_need_the_right_permission_for_each_endpoint()
    {
        var viewer = await StaffAsync("content.page.view");
        var editor = await StaffAsync("content.page.edit");
        var nobody = await StaffAsync("catalog.product.view");
        var body = new { titleEn = "T", bodyEn = "B" };

        Assert.Equal(HttpStatusCode.OK, (await _api.GetAsync("/api/v1/content/pages", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.PutAsync("/api/v1/content/pages/about", body, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _api.PutAsync("/api/v1/content/pages/about", body, editor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.PostAsync("/api/v1/content/pages/about/publish", null, editor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/content/pages", nobody)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/content/brand", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.PutAsync("/api/v1/content/brand", new { businessName = "X" }, editor)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/content/social", viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _api.PutAsync("/api/v1/content/badges/secure-payment", new { textEn = "x", isEnabled = false, sortOrder = 1 }, editor)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _api.GetAsync("/api/v1/content/pages")).StatusCode);
    }

    [Fact]
    public async Task The_content_editor_role_holds_every_content_permission()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();

        var codes = await (from r in db.Roles join rp in db.RolePermissions on r.Id equals rp.RoleId join p in db.Permissions on rp.PermissionId equals p.Id where r.Name == "Content Editor" select p.Code).ToListAsync(Ct);

        foreach (var code in new[] { "content.page.view", "content.page.edit", "content.page.publish", "content.brand.view", "content.brand.edit", "content.social.edit", "content.badge.edit" })
        {
            Assert.Contains(code, codes);
        }
    }
}
