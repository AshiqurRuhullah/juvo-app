using System.Globalization;
using System.Text.RegularExpressions;
using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Platform;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Marketing;

/// <summary>Keys of the marketing settings (table <c>PlatformSettings</c>). Every key has a safe default (docs/marketing-api.md).</summary>
public static class MarketingSettingKeys
{
    public const string QuietHoursEnabled = "Marketing.QuietHours.Enabled";
    public const string QuietHoursStart = "Marketing.QuietHours.Start";
    public const string QuietHoursEnd = "Marketing.QuietHours.End";
    public const string FrequencyCapMessages = "Marketing.FrequencyCap.MaxMessages";
    public const string FrequencyCapDays = "Marketing.FrequencyCap.Days";
    public const string ThrottlePerMinute = "Marketing.ThrottlePerMinute";
    public const string AttributionWindowDays = "Marketing.AttributionWindowDays";
    public const string ShopName = "Marketing.ShopName";
    public const string LinkUrl = "Marketing.LinkUrl";
    public const string MaxNotifyMePerMobile = "Marketing.NotifyMe.MaxPerMobile";
    public const string SmsOptOutText = "Marketing.SmsOptOutText";
}

/// <summary>The marketing settings in effect. Quiet hours are clock times in Asia/Dhaka (UTC+06:00, no daylight saving).</summary>
public sealed record MarketingSettings(
    bool QuietHoursEnabled, TimeOnly QuietStart, TimeOnly QuietEnd, int FrequencyCapMessages, int FrequencyCapDays, int ThrottlePerMinute, int AttributionWindowDays,
    string ShopName, string? LinkUrl, int MaxNotifyMePerMobile, string SmsOptOutText)
{
    public const string DefaultSmsOptOut = "Stop: {unsubscribeLink}";

    public static readonly MarketingSettings Defaults = new(true, new TimeOnly(21, 0), new TimeOnly(9, 0), 3, 7, 600, 7, "Juvo BD", null, 10, DefaultSmsOptOut);
}

public sealed class MarketingSettingsService(IApplicationDbContext db, SettingsService settings)
{
    private static readonly string[] Keys =
    [
        MarketingSettingKeys.QuietHoursEnabled, MarketingSettingKeys.QuietHoursStart, MarketingSettingKeys.QuietHoursEnd, MarketingSettingKeys.FrequencyCapMessages, MarketingSettingKeys.FrequencyCapDays,
        MarketingSettingKeys.ThrottlePerMinute, MarketingSettingKeys.AttributionWindowDays, MarketingSettingKeys.ShopName, MarketingSettingKeys.LinkUrl, MarketingSettingKeys.MaxNotifyMePerMobile,
        MarketingSettingKeys.SmsOptOutText,
    ];

    public async Task<MarketingSettings> ReadAsync(CancellationToken cancellationToken)
    {
        var v = await settings.ReadAsync(Keys, cancellationToken);
        var d = MarketingSettings.Defaults;
        return new MarketingSettings(
            !v.TryGetValue(MarketingSettingKeys.QuietHoursEnabled, out var on) || !bool.TryParse(on, out var enabled) || enabled,
            Time(v, MarketingSettingKeys.QuietHoursStart, d.QuietStart),
            Time(v, MarketingSettingKeys.QuietHoursEnd, d.QuietEnd),
            (int)SettingsService.Number(v, MarketingSettingKeys.FrequencyCapMessages, d.FrequencyCapMessages, 0m),
            (int)SettingsService.Number(v, MarketingSettingKeys.FrequencyCapDays, d.FrequencyCapDays, 1m),
            (int)SettingsService.Number(v, MarketingSettingKeys.ThrottlePerMinute, d.ThrottlePerMinute, 1m),
            (int)SettingsService.Number(v, MarketingSettingKeys.AttributionWindowDays, d.AttributionWindowDays, 1m),
            v.TryGetValue(MarketingSettingKeys.ShopName, out var shop) && !string.IsNullOrWhiteSpace(shop) ? shop : d.ShopName,
            v.TryGetValue(MarketingSettingKeys.LinkUrl, out var link) && !string.IsNullOrWhiteSpace(link) ? link : null,
            (int)SettingsService.Number(v, MarketingSettingKeys.MaxNotifyMePerMobile, d.MaxNotifyMePerMobile, 1m),
            v.TryGetValue(MarketingSettingKeys.SmsOptOutText, out var optOut) && !string.IsNullOrWhiteSpace(optOut) ? optOut : d.SmsOptOutText);
    }

    private static TimeOnly Time(Dictionary<string, string> values, string key, TimeOnly fallback) =>
        values.TryGetValue(key, out var raw) && TimeOnly.TryParseExact(raw, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : fallback;

    public async Task<Result<MarketingSettings>> UpdateAsync(UpdateMarketingSettingsRequest r, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();
        void Bad(string field, string message) => errors[field] = [message];
        TimeOnly? start = null;
        TimeOnly? end = null;
        if (r.QuietHoursStart is not null)
        {
            if (TimeOnly.TryParseExact(r.QuietHoursStart, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                start = parsed;
            }
            else
            {
                Bad("quietHoursStart", "Use a time like 21:00.");
            }
        }

        if (r.QuietHoursEnd is not null)
        {
            if (TimeOnly.TryParseExact(r.QuietHoursEnd, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            {
                end = parsed;
            }
            else
            {
                Bad("quietHoursEnd", "Use a time like 09:00.");
            }
        }

        if (r.FrequencyCapMessages is < 0 or > 100)
        {
            Bad("frequencyCapMessages", "Use 0 (no cap) to 100 messages.");
        }

        if (r.FrequencyCapDays is < 1 or > 365)
        {
            Bad("frequencyCapDays", "Use 1 to 365 days.");
        }

        if (r.ThrottlePerMinute is < 1 or > 100_000)
        {
            Bad("throttlePerMinute", "Use 1 to 100000 messages per minute.");
        }

        if (r.AttributionWindowDays is < 1 or > 90)
        {
            Bad("attributionWindowDays", "Use 1 to 90 days.");
        }

        if (r.ShopName is { Length: > 60 })
        {
            Bad("shopName", "The shop name can have 60 characters.");
        }

        if (r.SmsOptOutText is { } opt && (opt.Length > 80 || !opt.Contains("{unsubscribeLink}", StringComparison.Ordinal)))
        {
            Bad("smsOptOutText", "Up to 80 characters and it must contain {unsubscribeLink}.");
        }

        if (r.LinkUrl is { Length: > 0 } link && (!Uri.TryCreate(link, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
        {
            Bad("linkUrl", "Use a full web address starting with http:// or https://.");
        }

        if (r.MaxNotifyMePerMobile is < 1 or > 100)
        {
            Bad("maxNotifyMePerMobile", "Use 1 to 100.");
        }

        if (errors.Count > 0)
        {
            return Error.Validation(errors);
        }

        if (r.QuietHoursEnabled is { } enabled)
        {
            await settings.SetAsync(MarketingSettingKeys.QuietHoursEnabled, enabled ? "true" : "false", cancellationToken);
        }

        if (start is { } st)
        {
            await settings.SetAsync(MarketingSettingKeys.QuietHoursStart, st.ToString("HH:mm", CultureInfo.InvariantCulture), cancellationToken);
        }

        if (end is { } en)
        {
            await settings.SetAsync(MarketingSettingKeys.QuietHoursEnd, en.ToString("HH:mm", CultureInfo.InvariantCulture), cancellationToken);
        }

        await SetNumberAsync(MarketingSettingKeys.FrequencyCapMessages, r.FrequencyCapMessages, cancellationToken);
        await SetNumberAsync(MarketingSettingKeys.FrequencyCapDays, r.FrequencyCapDays, cancellationToken);
        await SetNumberAsync(MarketingSettingKeys.ThrottlePerMinute, r.ThrottlePerMinute, cancellationToken);
        await SetNumberAsync(MarketingSettingKeys.AttributionWindowDays, r.AttributionWindowDays, cancellationToken);
        await SetNumberAsync(MarketingSettingKeys.MaxNotifyMePerMobile, r.MaxNotifyMePerMobile, cancellationToken);
        if (r.ShopName is not null)
        {
            await settings.SetAsync(MarketingSettingKeys.ShopName, r.ShopName.Trim(), cancellationToken);
        }

        if (r.SmsOptOutText is not null)
        {
            await settings.SetAsync(MarketingSettingKeys.SmsOptOutText, r.SmsOptOutText.Trim(), cancellationToken);
        }

        if (r.LinkUrl is not null)
        {
            await settings.SetAsync(MarketingSettingKeys.LinkUrl, r.LinkUrl.Trim(), cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ReadAsync(cancellationToken);
    }

    private async Task SetNumberAsync(string key, int? value, CancellationToken cancellationToken)
    {
        if (value is { } v)
        {
            await settings.SetAsync(key, v.ToString(CultureInfo.InvariantCulture), cancellationToken);
        }
    }

    public static MarketingSettingsDto ToDto(MarketingSettings s) => new(
        s.QuietHoursEnabled, s.QuietStart.ToString("HH:mm", CultureInfo.InvariantCulture), s.QuietEnd.ToString("HH:mm", CultureInfo.InvariantCulture), s.FrequencyCapMessages, s.FrequencyCapDays,
        s.ThrottlePerMinute, s.AttributionWindowDays, s.ShopName, s.LinkUrl, s.MaxNotifyMePerMobile, s.SmsOptOutText);
}

/// <summary>The decisions of the marketing module that are plain functions of their inputs (and so are unit tested without a database).</summary>
public static class MarketingLogic
{
    public static readonly TimeSpan Dhaka = TimeSpan.FromHours(6);

    /// <summary>The clock time in Asia/Dhaka.</summary>
    public static TimeOnly LocalTime(DateTimeOffset now) => TimeOnly.FromTimeSpan(now.ToOffset(Dhaka).TimeOfDay);

    /// <summary>True when <paramref name="now"/> falls in the quiet window <c>[start, end)</c> in Dhaka time. The window may cross midnight (21:00 to 09:00); start equal to end means no window.</summary>
    public static bool IsQuiet(DateTimeOffset now, bool enabled, TimeOnly start, TimeOnly end)
    {
        if (!enabled || start == end)
        {
            return false;
        }

        var t = LocalTime(now);
        return start < end ? t >= start && t < end : t >= start || t < end;
    }

    /// <summary>The frequency cap lets another message through while fewer than <paramref name="maxMessages"/> were sent in the window. Zero or less means no cap.</summary>
    public static bool CapAllows(int alreadySentInWindow, int maxMessages) => maxMessages <= 0 || alreadySentInWindow < maxMessages;

    /// <summary>An order belongs to a message when it was placed at or after the send and no later than <paramref name="windowDays"/> days afterwards.</summary>
    public static bool InAttributionWindow(DateTimeOffset sentAt, DateTimeOffset orderPlacedAt, int windowDays) => orderPlacedAt >= sentAt && orderPlacedAt <= sentAt.AddDays(windowDays);

    /// <summary>
    /// Last touch: an order is credited to the newest marketing message sent to the customer before the order. <paramref name="otherTouches"/> are the send times of the customer's
    /// other marketing messages; the message wins when none of them lies after it and not after the order.
    /// </summary>
    public static bool IsLastTouch(DateTimeOffset sentAt, DateTimeOffset orderPlacedAt, IEnumerable<DateTimeOffset> otherTouches) => !otherTouches.Any(t => t > sentAt && t <= orderPlacedAt);
}

/// <summary>Placeholders in marketing texts look like <c>{customerName}</c>. Unknown names are refused when a text is saved so a typing mistake never reaches a customer.</summary>
public static partial class MarketingTemplate
{
    public static readonly IReadOnlyList<string> CampaignPlaceholders = ["customerName", "shopName", "link", "unsubscribeLink"];

    [GeneratedRegex(@"\{([A-Za-z][A-Za-z0-9]*)\}")]
    private static partial Regex Token();

    public static IReadOnlyList<string> PlaceholdersIn(string? text) =>
        string.IsNullOrEmpty(text) ? [] : [.. Token().Matches(text).Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal)];

    public static IReadOnlyList<string> UnknownIn(string? text, IReadOnlyCollection<string> known) => [.. PlaceholdersIn(text).Where(p => !known.Contains(p, StringComparer.Ordinal))];

    /// <summary>Replaces every known placeholder; a placeholder without a value in <paramref name="values"/> is left as written.</summary>
    public static string Render(string text, IReadOnlyDictionary<string, string> values) =>
        Token().Replace(text, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
}
