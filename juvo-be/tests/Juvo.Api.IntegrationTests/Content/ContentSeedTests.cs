using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Content;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Content;

/// <summary>The state right after start-up: nothing invented, only the SRS facts. Own database, so no other test has edited anything yet.</summary>
public class ContentSeedTests(ApiFactory factory) : IClassFixture<ApiFactory>
{
    private readonly TestApi _api = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> AdminAsync() => (await _api.LoginAdminAsync()).Access;

    [Fact]
    public async Task The_seed_has_six_empty_draft_pages_the_brand_the_facebook_link_and_four_empty_badges()
    {
        var admin = await AdminAsync();

        var pages = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/content/pages", admin), HttpStatusCode.OK);
        Assert.Equal(ContentPageKeys.All, pages.EnumerateArray().Select(p => p.GetProperty("key").GetString()!).ToArray());
        Assert.All(pages.EnumerateArray(), p =>
        {
            Assert.False(p.GetProperty("isPublished").GetBoolean());
            Assert.Equal(JsonValueKind.Null, p.GetProperty("bodyEn").ValueKind);
            Assert.Equal(JsonValueKind.Null, p.GetProperty("titleEn").ValueKind);
        });

        var brand = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/content/brand", admin), HttpStatusCode.OK);
        Assert.Equal("juvobd@gmail.com", brand.GetProperty("email").GetString());

        var social = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/content/social", admin), HttpStatusCode.OK);
        var facebook = Assert.Single(social.EnumerateArray());
        Assert.Equal("Facebook", facebook.GetProperty("platform").GetString());
        Assert.Equal("https://facebook.com/juvobd", facebook.GetProperty("url").GetString());

        var badges = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/content/badges", admin), HttpStatusCode.OK);
        Assert.Equal(TrustBadgeKeys.All.Count, badges.GetArrayLength());
        Assert.All(badges.EnumerateArray(), b =>
        {
            Assert.False(b.GetProperty("isEnabled").GetBoolean());
            Assert.Equal(JsonValueKind.Null, b.GetProperty("textEn").ValueKind);
        });
    }

    [Fact]
    public async Task Seeding_again_changes_nothing()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var seeder = scope.ServiceProvider.GetRequiredService<Juvo.Infrastructure.Content.ContentSeeder>();

        Assert.Equal(0, await seeder.SeedAsync(Ct));
    }


    [Fact]
    public async Task The_public_sees_no_pages_and_no_badges_but_the_brand_email_and_the_facebook_link()
    {
        foreach (var key in ContentPageKeys.All)
        {
            Assert.Equal(HttpStatusCode.NotFound, (await _api.GetAsync($"/api/v1/store/content/pages/{key}")).StatusCode);
        }

        var badges = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/store/content/badges"), HttpStatusCode.OK);
        Assert.Equal(0, badges.GetArrayLength());

        var social = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/store/content/social"), HttpStatusCode.OK);
        Assert.Equal("https://facebook.com/juvobd", Assert.Single(social.EnumerateArray()).GetProperty("url").GetString());

        var brand = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/store/content/brand"), HttpStatusCode.OK);
        Assert.Equal("juvobd@gmail.com", brand.GetProperty("email").GetString());
        Assert.Equal(JsonValueKind.Null, brand.GetProperty("logoUrl").ValueKind);
    }
}
