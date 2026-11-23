using Juvo.Domain.Customers;

namespace Juvo.Application.Privacy;

/// <summary>Pure rules of the privacy tools, kept apart from the database so they can be tested alone.</summary>
public static class PrivacyRules
{
    /// <summary>Turns the append-only consent log into a readable history, newest first, with the value each change replaced.</summary>
    public static IReadOnlyList<ConsentHistoryEntryDto> BuildConsentHistory(IEnumerable<ConsentLog> rows, bool maskIp)
    {
        var ordered = rows.OrderBy(r => r.At).ThenBy(r => r.Id).ToList();
        var last = new Dictionary<ConsentType, bool>();
        var result = new List<ConsentHistoryEntryDto>(ordered.Count);
        foreach (var row in ordered)
        {
            bool? old = last.TryGetValue(row.Type, out var previous) ? previous : null;
            last[row.Type] = row.Granted;
            result.Add(new ConsentHistoryEntryDto(
                row.Id, row.Type.ToString(), ChannelOf(row.Type), AppliesTo(row.Type), old, row.Granted, row.Source, SourceKindOf(row.Source), maskIp ? MaskIp(row.IpAddress) : row.IpAddress, row.At));
        }

        result.Reverse();
        return result;
    }

    public static string ChannelOf(ConsentType type) => type switch
    {
        ConsentType.MarketingSms => "Sms",
        ConsentType.MarketingEmail => "Email",
        _ => "Policy",
    };

    /// <summary>The SMS marketing consent is also the WhatsApp consent: the notification service sends WhatsApp only to customers who allowed SMS.</summary>
    public static IReadOnlyList<string> AppliesTo(ConsentType type) => type switch
    {
        ConsentType.MarketingSms => ["Sms", "WhatsApp"],
        ConsentType.MarketingEmail => ["Email"],
        _ => [],
    };

    public static string SourceKindOf(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            return "Other";
        }

        var s = source.Trim().ToLowerInvariant();
        if (s.StartsWith("customer", StringComparison.Ordinal))
        {
            return "Customer";
        }

        if (s.StartsWith("unsubscribe", StringComparison.Ordinal))
        {
            return "UnsubscribeLink";
        }

        if (s.StartsWith("staff", StringComparison.Ordinal))
        {
            return "Staff";
        }

        return s.StartsWith("import", StringComparison.Ordinal) ? "Import" : "Other";
    }

    /// <summary>Hides the last part of an address: <c>203.0.113.77</c> becomes <c>203.0.*.*</c>, an IPv6 address keeps its first two groups.</summary>
    public static string? MaskIp(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
        {
            return null;
        }

        var value = ip.Trim();
        var dot = value.Split('.');
        if (dot.Length == 4)
        {
            return $"{dot[0]}.{dot[1]}.*.*";
        }

        var colon = value.Split(':');
        return colon.Length > 2 ? $"{colon[0]}:{colon[1]}:*" : "***";
    }

    /// <summary>
    /// The ways one Bangladesh mobile number may be written in stored text (contact form, notification recipients, sign-in attempts): <c>+8801712345678</c>,
    /// <c>8801712345678</c> and <c>01712345678</c>. Anything that is not a +880 number comes back as itself.
    /// </summary>
    public static IReadOnlyList<string> MobileVariants(string mobile)
    {
        var value = (mobile ?? string.Empty).Trim();
        if (value.Length == 0 || PrivacyPlaceholders.IsPlaceholderMobile(value))
        {
            return [];
        }

        if (value.StartsWith("+880", StringComparison.Ordinal) && value.Length > 4)
        {
            var national = value[4..];
            return [value, "880" + national, "0" + national];
        }

        return [value];
    }

    /// <summary>The reason text staff give: trimmed, 5 to 300 characters. Null when it does not qualify.</summary>
    public static string? CleanReason(string? reason)
    {
        var value = reason?.Trim();
        return value is { Length: >= 5 and <= 300 } ? value : null;
    }

    /// <summary>The confirm token is the customer id written back, in any letter case, with or without dashes.</summary>
    public static bool ConfirmMatches(Guid customerId, string? confirm) =>
        Guid.TryParse(confirm?.Trim(), out var echoed) && echoed == customerId;
}
