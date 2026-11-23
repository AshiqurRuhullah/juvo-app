using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Juvo.Application.Integrations;

/// <summary>The outgoing event types (FR-AF05). <c>ping</c> is only sent by the test button and cannot be subscribed to.</summary>
public static class WebhookEvents
{
    public const string OrderPlaced = "order.placed";
    public const string OrderStatusChanged = "order.status_changed";
    public const string ProductPublished = "product.published";
    public const string ProductUpdated = "product.updated";
    public const string StockLow = "stock.low";
    public const string Ping = "ping";

    public static readonly IReadOnlyList<string> All = [OrderPlaced, OrderStatusChanged, ProductPublished, ProductUpdated, StockLow];

    public static bool IsKnown(string type) => All.Contains(type, StringComparer.Ordinal);
}

/// <summary>Header names and the timing rules of outgoing webhooks. The signature scheme is the one the payment webhooks use (<see cref="Juvo.Application.Payments.WebhookSignature"/>), in reverse.</summary>
public static class WebhookDelivering
{
    public const string SignatureHeader = Juvo.Application.Payments.WebhookSignature.HeaderName;
    public const string EventHeader = "Juvo-Event";
    public const string DeliveryIdHeader = "Juvo-Delivery-Id";
    public const int MaxAttempts = 5;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    /// <summary>The wait after a failed attempt: 30 s, 2 min, 8 min, 32 min. Null after the fifth attempt (the delivery is then Failed).</summary>
    public static TimeSpan? DelayAfter(int attemptsMade) =>
        attemptsMade is < 1 || attemptsMade >= MaxAttempts ? null : TimeSpan.FromSeconds(30 * Math.Pow(4, attemptsMade - 1));

    public static string HashPayload(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

    /// <summary>A new signing secret: <c>whsec_</c> followed by 256 random bits.</summary>
    public static string NewSecret() => "whsec_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

    /// <summary>The header value for a body at a time (same format as the payment webhooks).</summary>
    public static string Sign(string secret, DateTimeOffset at, string body) => Juvo.Application.Payments.WebhookSignature.Sign(secret, at, body);
}

/// <summary>
/// Decides whether an address may be called by a webhook (SSRF protection). Everything that is not a normal public unicast address is refused:
/// loopback, private, link-local (including the cloud metadata address 169.254.169.254), shared address space, documentation and reserved ranges, multicast and
/// the unspecified address. IPv4-mapped, NAT64 and 6to4 IPv6 forms are judged by the IPv4 address inside.
/// </summary>
public static class SsrfAddressClassifier
{
    public static bool IsBlocked(IPAddress address, bool allowLoopback = false)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return !allowLoopback;
        }

        return address.AddressFamily switch
        {
            AddressFamily.InterNetwork => BlockedV4(address.GetAddressBytes()),
            AddressFamily.InterNetworkV6 => BlockedV6(address, address.GetAddressBytes()),
            _ => true,
        };
    }

    private static bool BlockedV4(byte[] b) =>
        b[0] == 0                                          // 0.0.0.0/8 "this network"
        || b[0] == 10                                      // private
        || b[0] == 100 && b[1] is >= 64 and <= 127         // 100.64/10 shared address space
        || b[0] == 127                                     // loopback (when not allowed above)
        || b[0] == 169 && b[1] == 254                      // link-local and metadata
        || b[0] == 172 && b[1] is >= 16 and <= 31          // private
        || b[0] == 192 && b[1] == 0 && b[2] is 0 or 2      // 192.0.0/24 protocol assignments, 192.0.2/24 documentation
        || b[0] == 192 && b[1] == 168                      // private
        || b[0] == 192 && b[1] == 88 && b[2] == 99         // 6to4 relay
        || b[0] == 198 && b[1] is 18 or 19                 // benchmarking
        || b[0] == 198 && b[1] == 51 && b[2] == 100        // documentation
        || b[0] == 203 && b[1] == 0 && b[2] == 113         // documentation
        || b[0] >= 224;                                    // multicast, reserved, broadcast

    private static bool BlockedV6(IPAddress address, byte[] b)
    {
        if (address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None))
        {
            return true;
        }

        if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.IsIPv6UniqueLocal)
        {
            return true;
        }

        // 64:ff9b::/96 NAT64 and 2002::/16 6to4 carry an IPv4 address: judge that one.
        if (b[0] == 0x00 && b[1] == 0x64 && b[2] == 0xff && b[3] == 0x9b && b[4..12].All(x => x == 0))
        {
            return IsBlocked(new IPAddress(b[12..16]));
        }

        if (b[0] == 0x20 && b[1] == 0x02)
        {
            return IsBlocked(new IPAddress(b[2..6]));
        }

        // 2001:db8::/32 documentation, 2001::/32 Teredo, ::/96 IPv4-compatible (deprecated), 100::/64 discard.
        return b[0] == 0x20 && b[1] == 0x01 && (b[2] == 0x0d && b[3] == 0xb8 || b[2] == 0 && b[3] == 0)
               || b[..12].All(x => x == 0)
               || b[0] == 0x01 && b[1] == 0x00 && b[2..8].All(x => x == 0);
    }
}
