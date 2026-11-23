using System.Net;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Integrations;
using Juvo.Domain.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Platform;

/// <summary>Webhook SSRF protection with the production guard, nothing relaxed (FR-AF05, NFR-009): refused when saved, refused again at delivery, never followed anywhere private.</summary>
public class WebhookSsrfTests(StrictWebhookFactory factory) : IClassFixture<StrictWebhookFactory>
{
    private readonly IntegrationsHelper _i = new(factory);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("ftp://example.com/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://127.0.0.1:8443/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://10.1.2.3/hook")]
    [InlineData("https://172.16.0.9/hook")]
    [InlineData("https://192.168.0.10/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[fd00:ec2::254]/latest/meta-data/")]
    [InlineData("https://[::ffff:127.0.0.1]/hook")]
    [InlineData("https://[::ffff:169.254.169.254]/hook")]
    [InlineData("https://0.0.0.0/hook")]
    [InlineData("https://2130706433/hook")]
    [InlineData("https://0x7f.0.0.1/hook")]
    [InlineData("https://user:password@93.184.216.34/hook")]
    [InlineData("https://this-host-does-not-exist.invalid/hook")]
    [InlineData("not a web address")]
    public async Task An_address_that_is_not_https_or_leads_to_a_private_network_is_refused_when_saved(string url)
    {
        var admin = await _i.Store.AdminAsync();

        var response = await _i.Api.PostAsync("/api/v1/integrations/webhooks", new { name = "Hook name", url, events = new[] { "order.placed" } }, admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var json = await TestApi.JsonAsync(response);
        Assert.Equal("INTEGRATIONS.URL_REFUSED", json.GetProperty("code").GetString());
        Assert.True(json.GetProperty("errors").TryGetProperty("url", out _));
        Assert.Equal(0, await _i.WithDbAsync(db => db.WebhookEndpoints.CountAsync(e => e.Url == url, Ct)));
    }

    [Fact]
    public async Task A_public_https_address_is_accepted_and_changing_an_endpoint_to_a_private_one_is_refused()
    {
        var admin = await _i.Store.AdminAsync();
        var (id, secret) = await _i.CreateWebhookAsync(admin, "https://93.184.216.34/hook", ["order.placed"]);
        Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);

        var response = await _i.Api.PutAsync($"/api/v1/integrations/webhooks/{id}", new { name = "Hook name", url = "https://10.0.0.1/hook", events = new[] { "order.placed" } }, admin);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("INTEGRATIONS.URL_REFUSED", await TestApi.ErrorCodeAsync(response));
        var kept = await TestApi.ExpectAsync(await _i.Api.GetAsync($"/api/v1/integrations/webhooks/{id}", admin), HttpStatusCode.OK);
        Assert.Equal("https://93.184.216.34/hook", kept.GetProperty("url").GetString());
    }

    [Fact]
    public async Task A_stored_private_address_is_refused_again_at_delivery_and_is_not_retried()
    {
        // A row that bypassed the check when it was saved (an old row, a database edit, or a name that now resolves to a private address).
        var admin = await _i.Store.AdminAsync();
        var (id, _) = await _i.CreateWebhookAsync(admin, "https://93.184.216.34/hook", ["order.placed"]);
        await _i.WithDbAsync(db => db.WebhookEndpoints.Where(e => e.Id == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Url, "https://169.254.169.254/latest/meta-data/"), Ct));

        var ping = await TestApi.ExpectAsync(await _i.Api.PostAsync($"/api/v1/integrations/webhooks/{id}/test", null, admin), HttpStatusCode.OK);
        Assert.Equal("Failed", ping.GetProperty("status").GetString());
        Assert.StartsWith("Refused", ping.GetProperty("error").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("169.254", ping.GetRawText(), StringComparison.Ordinal);

        var eventId = Guid.NewGuid();
        var deliveryId = await _i.WithDbAsync(async db =>
        {
            var d = new WebhookDelivery(eventId, "order.placed", id, "{}", WebhookDelivering.HashPayload("{}"), DateTimeOffset.UtcNow.AddSeconds(-1));
            db.WebhookDeliveries.Add(d);
            await db.SaveChangesAsync(Ct);
            return d.Id;
        });
        await _i.DispatchAsync();
        var delivery = await _i.WithDbAsync(db => db.WebhookDeliveries.AsNoTracking().SingleAsync(d => d.Id == deliveryId, Ct));

        Assert.Equal(WebhookDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(1, delivery.Attempt);
        Assert.StartsWith("Refused", delivery.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://127.0.0.1:9/hook")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("https://[::1]:9/hook")]
    [InlineData("https://10.0.0.1/hook")]
    public async Task The_sender_itself_will_not_open_a_connection_to_a_private_address(string url)
    {
        // The connection is made only to addresses the guard approves, so even a call that skipped the earlier checks cannot reach them.
        var sender = factory.Services.GetRequiredService<IWebhookSender>();

        var result = await sender.SendAsync(new WebhookSendRequest(url, "{}", new Dictionary<string, string>()), Ct);

        Assert.False(result.Success);
        Assert.Null(result.StatusCode);
        Assert.StartsWith("Refused", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("169.254", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task There_is_no_setting_that_relaxes_the_guard_in_production_configuration()
    {
        var guard = factory.Services.GetRequiredService<IWebhookTargetGuard>();

        Assert.Equal("Only https addresses are allowed.", await guard.CheckUrlAsync("http://93.184.216.34/", Ct));
        Assert.NotNull(await guard.CheckUrlAsync("https://127.0.0.1/", Ct));
        Assert.Null(await guard.CheckUrlAsync("https://93.184.216.34/", Ct));
        Assert.False((await guard.ResolveAsync("127.0.0.1", Ct)).Allowed);
        Assert.False((await guard.ResolveAsync("::ffff:10.0.0.1", Ct)).Allowed);
        Assert.True((await guard.ResolveAsync("8.8.8.8", Ct)).Allowed);
        Assert.Equal(typeof(Juvo.Infrastructure.Integrations.WebhookTargetGuard), guard.GetType());
    }
}
