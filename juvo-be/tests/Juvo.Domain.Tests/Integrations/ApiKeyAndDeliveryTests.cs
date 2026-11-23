using Juvo.Domain.Integrations;

namespace Juvo.Domain.Tests.Integrations;

public class ApiKeyAndDeliveryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);

    private static ApiKey Key(DateTimeOffset? expires = null) => new("ERP sync", "jv_live_aaaaaaaa", new byte[32], ["stock:read", "orders:read", "orders:read"], null, expires, null);

    [Fact]
    public void A_key_keeps_its_scopes_sorted_and_without_repeats()
    {
        var key = Key();

        Assert.Equal(["orders:read", "stock:read"], key.ScopeList);
        Assert.True(key.HasScope("orders:read"));
        Assert.False(key.HasScope("pii:read"));
        Assert.True(key.IsEnabled);
    }

    [Fact]
    public void A_key_expires_at_its_expiry_time_and_never_without_one()
    {
        Assert.False(Key().IsExpired(Now.AddYears(50)));
        Assert.False(Key(Now.AddMinutes(1)).IsExpired(Now));
        Assert.True(Key(Now).IsExpired(Now));
    }

    [Fact]
    public void Rotating_replaces_prefix_and_hash_and_forgets_the_last_use()
    {
        var key = Key();
        var newHash = new byte[32];
        newHash[0] = 7;

        key.Rotate("jv_live_bbbbbbbb", newHash, Now);

        Assert.Equal("jv_live_bbbbbbbb", key.KeyPrefix);
        Assert.Equal(7, key.KeyHash[0]);
        Assert.Equal(Now, key.RotatedAt);
        Assert.Null(key.LastUsedAt);
    }

    [Fact]
    public void A_delivery_retries_until_the_fifth_failure_and_then_stays_failed()
    {
        var delivery = new WebhookDelivery(Guid.NewGuid(), "order.placed", Guid.NewGuid(), "{}", "hash", Now);

        delivery.RecordFailure(500, "HTTP 500", TimeSpan.FromSeconds(30), Now);
        Assert.Equal(WebhookDeliveryStatus.Pending, delivery.Status);
        Assert.Equal(1, delivery.Attempt);
        Assert.Equal(Now.AddSeconds(30), delivery.NextAttemptAt);

        delivery.RecordFailure(null, "No answer", null, Now.AddMinutes(1));
        Assert.Equal(WebhookDeliveryStatus.Failed, delivery.Status);
        Assert.Equal(2, delivery.Attempt);
    }

    [Fact]
    public void Success_clears_the_error_and_a_redelivery_starts_again_from_zero()
    {
        var delivery = new WebhookDelivery(Guid.NewGuid(), "stock.low", Guid.NewGuid(), "{}", "hash", Now);
        delivery.RecordFailure(502, "HTTP 502", null, Now);
        delivery.Redeliver(Now.AddHours(1));

        Assert.Equal(WebhookDeliveryStatus.Pending, delivery.Status);
        Assert.Equal(0, delivery.Attempt);
        Assert.Equal(1, delivery.Redeliveries);
        Assert.Equal(Now.AddHours(1), delivery.NextAttemptAt);

        delivery.RecordSuccess(200, Now.AddHours(1));
        Assert.Equal(WebhookDeliveryStatus.Succeeded, delivery.Status);
        Assert.Null(delivery.Error);
        Assert.Equal(Now.AddHours(1), delivery.DeliveredAt);
    }

    [Fact]
    public void An_error_text_is_cut_to_300_characters()
    {
        var delivery = new WebhookDelivery(Guid.NewGuid(), "ping", Guid.NewGuid(), "{}", "hash", Now);

        delivery.RecordFailure(null, new string('x', 900), null, Now);

        Assert.Equal(300, delivery.Error!.Length);
    }

    [Fact]
    public void An_endpoint_knows_which_events_it_subscribes_to()
    {
        var endpoint = new WebhookEndpoint("Shop", "https://example.com/hook", "protected", ["stock.low", "order.placed", "stock.low"]);

        Assert.Equal(["order.placed", "stock.low"], endpoint.EventList);
        Assert.True(endpoint.Subscribes("stock.low"));
        Assert.False(endpoint.Subscribes("order.status_changed"));
        Assert.False(endpoint.Subscribes("stock"));
    }
}
