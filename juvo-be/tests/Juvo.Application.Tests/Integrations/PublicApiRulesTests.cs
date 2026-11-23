using System.Net;
using Juvo.Application.Integrations;
using Juvo.Application.Payments;
using Juvo.Shared.Results;
using Juvo.Shared.Security;

namespace Juvo.Application.Tests.Integrations;

public class PublicApiRulesTests
{
    private sealed class Clock(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    // ---- keys ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_generated_key_has_the_documented_shape_and_its_prefix_finds_it()
    {
        var key = ApiKeyRules.Generate();

        Assert.StartsWith("jv_live_", key.Key, StringComparison.Ordinal);
        Assert.Equal(16, key.Prefix.Length);
        Assert.StartsWith(key.Prefix, key.Key, StringComparison.Ordinal);
        Assert.Equal(key.Prefix, ApiKeyRules.PrefixOf(key.Key));
        Assert.Equal(8 + 8 + 1 + 43, key.Key.Length);
        Assert.Equal(32, key.Hash.Length);
        Assert.DoesNotContain(key.Key, key.Prefix, StringComparison.Ordinal);
    }

    [Fact]
    public void Keys_are_random_and_only_the_exact_key_matches_its_hash()
    {
        var a = ApiKeyRules.Generate();
        var b = ApiKeyRules.Generate();

        Assert.NotEqual(a.Key, b.Key);
        Assert.NotEqual(a.Prefix, b.Prefix);
        Assert.True(ApiKeyRules.Matches(a.Key, a.Hash));
        Assert.False(ApiKeyRules.Matches(b.Key, a.Hash));
        Assert.False(ApiKeyRules.Matches(a.Key + "x", a.Hash));
        Assert.False(ApiKeyRules.Matches(a.Key[..^1] + (a.Key[^1] == 'A' ? 'B' : 'A'), a.Hash));
        Assert.False(ApiKeyRules.Matches(a.Key, ApiKeyRules.DummyHash));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("jv_live_short")]
    [InlineData("zz_demo_abcdefgh_0123456789012345678901234567890123456789")]
    [InlineData("Bearer jv_live_abcdefgh_0123456789012345678901234567890123456789")]
    public void Anything_that_cannot_be_one_of_our_keys_has_no_prefix(string? presented) => Assert.Null(ApiKeyRules.PrefixOf(presented));

    [Fact]
    public void The_address_hash_is_short_stable_and_not_the_address()
    {
        var hash = ApiKeyRules.HashIp("203.0.113.9");

        Assert.Equal(16, hash!.Length);
        Assert.Equal(hash, ApiKeyRules.HashIp("203.0.113.9"));
        Assert.NotEqual(hash, ApiKeyRules.HashIp("203.0.113.10"));
        Assert.DoesNotContain("203", hash, StringComparison.Ordinal);
        Assert.Null(ApiKeyRules.HashIp(null));
    }

    [Fact]
    public void The_scope_catalogue_is_fixed_has_no_write_scope_and_marks_pii_as_sensitive()
    {
        Assert.Equal(["orders:read", "products:read", "stock:read", "customers:read", "pii:read"], ApiKeyScopes.All.Select(s => s.Scope));
        Assert.All(ApiKeyScopes.All, s => Assert.EndsWith(":read", s.Scope, StringComparison.Ordinal));
        Assert.True(ApiKeyScopes.All.Single(s => s.Scope == ApiKeyScopes.PiiRead).Sensitive);
        Assert.True(ApiKeyScopes.IsKnown("orders:read"));
        Assert.False(ApiKeyScopes.IsKnown("stock:write"));
        Assert.False(ApiKeyScopes.IsKnown("ORDERS:READ"));
    }

    [Fact]
    public void The_integration_permissions_exist_and_the_manager_role_is_denied_them()
    {
        foreach (var code in new[] { "integrations.apikey.view", "integrations.apikey.manage", "integrations.webhook.view", "integrations.webhook.manage" })
        {
            Assert.True(PermissionCatalog.Exists(code), code);
        }

        var manager = DefaultRoles.All.Single(r => r.Name == DefaultRoles.Manager);
        Assert.DoesNotContain(PermissionPattern.Expand(manager), c => c.StartsWith("integrations.", StringComparison.Ordinal));
    }

    // ---- rate limit ------------------------------------------------------------------------------------------

    [Fact]
    public void The_rate_limiter_counts_per_key_per_minute_and_says_how_long_to_wait()
    {
        var clock = new Clock(new DateTimeOffset(2026, 11, 23, 8, 0, 20, TimeSpan.Zero));
        var limiter = new ApiKeyRateLimiter(clock);
        var key = Guid.NewGuid();
        var other = Guid.NewGuid();

        Assert.True(limiter.Hit(key, 2).Allowed);
        var second = limiter.Hit(key, 2);
        Assert.True(second.Allowed);
        Assert.Equal(0, second.Remaining);

        var third = limiter.Hit(key, 2);
        Assert.False(third.Allowed);
        Assert.Equal(40, third.RetryAfterSeconds);
        Assert.True(limiter.Hit(other, 2).Allowed);

        clock.Now = new DateTimeOffset(2026, 11, 23, 8, 1, 0, TimeSpan.Zero);
        Assert.True(limiter.Hit(key, 2).Allowed);
    }

    // ---- signatures and retries ------------------------------------------------------------------------------

    [Fact]
    public void The_outgoing_signature_is_the_payment_scheme_and_verifies_with_the_same_check()
    {
        var at = new DateTimeOffset(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
        var secret = "unit-test-secret-" + Guid.NewGuid().ToString("N");
        const string body = "{\"id\":\"1\",\"type\":\"ping\"}";

        var header = WebhookDelivering.Sign(secret, at, body);

        Assert.Matches("^t=\\d+,v1=[0-9a-f]{64}$", header);
        Assert.StartsWith($"t={at.ToUnixTimeSeconds()},", header, StringComparison.Ordinal);
        Assert.Null(WebhookSignature.Check(secret, header, body, at.AddSeconds(30)));
        Assert.Equal(ErrorCodes.Payment.InvalidSignature, WebhookSignature.Check(secret, header, body + " ", at)!.Code);
        Assert.Equal(ErrorCodes.Payment.InvalidSignature, WebhookSignature.Check("another-secret", header, body, at)!.Code);
        Assert.Equal(ErrorCodes.Payment.WebhookExpired, WebhookSignature.Check(secret, header, body, at.AddMinutes(6))!.Code);
        Assert.Equal("Juvo-Signature", WebhookDelivering.SignatureHeader);
    }

    [Fact]
    public void The_backoff_waits_30_seconds_then_grows_by_four_and_stops_after_five_attempts()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), WebhookDelivering.DelayAfter(1));
        Assert.Equal(TimeSpan.FromMinutes(2), WebhookDelivering.DelayAfter(2));
        Assert.Equal(TimeSpan.FromMinutes(8), WebhookDelivering.DelayAfter(3));
        Assert.Equal(TimeSpan.FromMinutes(32), WebhookDelivering.DelayAfter(4));
        Assert.Null(WebhookDelivering.DelayAfter(5));
        Assert.Null(WebhookDelivering.DelayAfter(0));
        Assert.Equal(5, WebhookDelivering.MaxAttempts);
    }

    [Fact]
    public void Secrets_and_payload_hashes_have_the_documented_shape()
    {
        var secret = WebhookDelivering.NewSecret();

        Assert.Matches("^whsec_[0-9a-f]{64}$", secret);
        Assert.NotEqual(secret, WebhookDelivering.NewSecret());
        Assert.Equal(64, WebhookDelivering.HashPayload("{}").Length);
        Assert.Equal(WebhookDelivering.HashPayload("{}"), WebhookDelivering.HashPayload("{}"));
        Assert.NotEqual(WebhookDelivering.HashPayload("{}"), WebhookDelivering.HashPayload("{ }"));
    }

    [Fact]
    public void The_event_catalogue_has_the_five_events_and_ping_is_not_subscribable()
    {
        Assert.Equal(["order.placed", "order.status_changed", "product.published", "product.updated", "stock.low"], WebhookEvents.All);
        Assert.False(WebhookEvents.IsKnown(WebhookEvents.Ping));
        Assert.False(WebhookEvents.IsKnown("order.deleted"));
    }

    // ---- SSRF ------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.8.8.8")]
    [InlineData("10.0.0.5")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("169.254.169.254")]
    [InlineData("169.254.0.1")]
    [InlineData("0.0.0.0")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.254")]
    [InlineData("192.0.0.8")]
    [InlineData("192.0.2.1")]
    [InlineData("198.18.0.1")]
    [InlineData("198.51.100.7")]
    [InlineData("203.0.113.7")]
    [InlineData("224.0.0.1")]
    [InlineData("240.0.0.1")]
    [InlineData("255.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456:789a::1")]
    [InlineData("fec0::1")]
    [InlineData("ff02::1")]
    [InlineData("2001:db8::1")]
    [InlineData("::ffff:127.0.0.1")]
    [InlineData("::ffff:10.1.2.3")]
    [InlineData("::ffff:169.254.169.254")]
    [InlineData("::ffff:192.168.0.1")]
    [InlineData("64:ff9b::7f00:1")]
    [InlineData("64:ff9b::a9fe:a9fe")]
    [InlineData("2002:7f00:1::1")]
    [InlineData("2002:c0a8:101::1")]
    [InlineData("fd00:ec2::254")]
    public void Addresses_inside_private_loopback_link_local_and_reserved_ranges_are_blocked(string text) =>
        Assert.True(SsrfAddressClassifier.IsBlocked(IPAddress.Parse(text)), text);

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.1")]
    [InlineData("169.253.1.1")]
    [InlineData("192.169.0.1")]
    [InlineData("198.20.0.1")]
    [InlineData("203.0.114.1")]
    [InlineData("93.184.216.34")]
    [InlineData("2606:4700:4700::1111")]
    [InlineData("2a00:1450:4001:81b::200e")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("64:ff9b::808:808")]
    [InlineData("2002:808:808::1")]
    public void Ordinary_public_addresses_are_allowed(string text) => Assert.False(SsrfAddressClassifier.IsBlocked(IPAddress.Parse(text)), text);

    [Fact]
    public void Loopback_is_only_allowed_when_the_test_override_asks_for_it_and_nothing_else_opens_up()
    {
        Assert.False(SsrfAddressClassifier.IsBlocked(IPAddress.Loopback, allowLoopback: true));
        Assert.False(SsrfAddressClassifier.IsBlocked(IPAddress.IPv6Loopback, allowLoopback: true));
        Assert.False(SsrfAddressClassifier.IsBlocked(IPAddress.Parse("::ffff:127.0.0.1"), allowLoopback: true));
        Assert.True(SsrfAddressClassifier.IsBlocked(IPAddress.Parse("10.0.0.1"), allowLoopback: true));
        Assert.True(SsrfAddressClassifier.IsBlocked(IPAddress.Parse("169.254.169.254"), allowLoopback: true));
    }
}
