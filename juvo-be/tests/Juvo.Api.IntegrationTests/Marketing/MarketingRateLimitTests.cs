using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Storefront;

namespace Juvo.Api.IntegrationTests.Marketing;

/// <summary>The two public marketing endpoints (unsubscribe, notify me) are rate limited per client address.</summary>
public class MarketingRateLimitTests(MarketingRateLimitTests.LimitedFactory factory) : IClassFixture<MarketingRateLimitTests.LimitedFactory>
{
    public sealed class LimitedFactory : MarketingFactory
    {
        protected override void ExtraSettings()
        {
            base.ExtraSettings();
            Setting("RateLimiting:Enabled", "true");
            Setting("RateLimiting:CheckoutPerMinute", "3");
        }
    }

    private readonly StoreHelper _h = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Notify_me_answers_429_with_retry_after_once_the_limit_is_used_up()
    {
        var body = new { mobile = StoreHelper.NewMobile() };

        for (var i = 0; i < 3; i++)
        {
            var allowed = await _h.SendAsync(HttpMethod.Post, "/api/v1/store/products/no-such-product/notify-me", body);
            Assert.Equal(HttpStatusCode.NotFound, allowed.StatusCode); // reaches the endpoint, which has nothing to subscribe to
        }

        var limited = await _h.SendAsync(HttpMethod.Post, "/api/v1/store/products/no-such-product/notify-me", body);

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.Contains("Retry-After"));
        var json = JsonDocument.Parse(await limited.Content.ReadAsStringAsync(Ct)).RootElement;
        Assert.Equal("GENERAL.TOO_MANY_REQUESTS", json.GetProperty("code").GetString());
    }

}

/// <summary>
/// Separate class (so a separate factory and limiter) from the notify-me test: the limit is per client address, and two tests
/// in one class would share one window, making each pass or fail depending on which ran first.
/// </summary>
public class MarketingUnsubscribeRateLimitTests(MarketingRateLimitTests.LimitedFactory factory) : IClassFixture<MarketingRateLimitTests.LimitedFactory>
{
    private readonly StoreHelper _h = new(factory);

    [Fact]
    public async Task The_unsubscribe_endpoint_shares_the_limit()
    {
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 6; i++)
        {
            last = (await _h.SendAsync(HttpMethod.Get, "/api/v1/store/unsubscribe?token=garbage")).StatusCode;
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, last);
    }
}
