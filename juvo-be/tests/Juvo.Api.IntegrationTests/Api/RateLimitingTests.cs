using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Support;

namespace Juvo.Api.IntegrationTests.Api;

public class RateLimitingTests : IClassFixture<RateLimitingTests.LimitedApiFactory>
{
    private readonly LimitedApiFactory _factory;

    public RateLimitingTests(LimitedApiFactory factory) => _factory = factory;

    public sealed class LimitedApiFactory : ApiFactory
    {
        protected override void ConfigureSettings()
        {
            Setting("RateLimiting:Enabled", "true");
            Setting("RateLimiting:GlobalPerMinute", "5");
        }
    }

    [Fact]
    public async Task Requests_over_the_limit_get_429_problem_details_with_retry_after()
    {
        var client = _factory.CreateClient();
        var ct = TestContext.Current.CancellationToken;

        for (var i = 0; i < 5; i++)
        {
            var ok = await client.GetAsync("/api/v1/system/info", ct);
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var limited = await client.GetAsync("/api/v1/system/info", ct);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        var json = await limited.Content.ReadFromJsonAsync<JsonElement>(ct);
        Assert.Equal("GENERAL.TOO_MANY_REQUESTS", json.GetProperty("code").GetString());
    }
}
