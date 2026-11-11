using System.Globalization;
using Juvo.Application.Abstractions;
using Juvo.Domain.Orders;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Orders;

/// <summary>Keys of the runtime settings the order flow reads (table <c>PlatformSettings</c>). Every key has a safe default.</summary>
public static class SettingKeys
{
    public const string OtpMode = "Checkout.OtpMode";
    public const string CodAdvanceThreshold = "Checkout.CodAdvanceThreshold";
    public const string DuplicateWindowMinutes = "Checkout.DuplicateWindowMinutes";
    public const string PaymentHoldMinutes = "Orders.PaymentHoldMinutes";
    public const string DeductAt = "Inventory.DeductAt";
    public const string RoutingMode = "Orders.RoutingMode";
    public const string MaxManualQuantity = "Orders.MaxManualQuantity";

    public static string RuleEnabled(string code) => $"Fraud.{code}.Enabled";

    public static string RuleThreshold(string code) => $"Fraud.{code}.Threshold";

    public static string RuleAction(string code) => $"Fraud.{code}.Action";
}

public enum OtpMode
{
    Off = 0,
    CodOnly = 1,
    Always = 2,
}

public enum DeductAt
{
    Packing = 1,
    Delivery = 2,
}

public enum RoutingMode
{
    DefaultBranch = 1,
    AllStock = 2,
    Nearest = 3,
    Manual = 4,
}

/// <summary>The order settings as read for one request.</summary>
public sealed record OrderSettings(
    OtpMode OtpMode, decimal CodAdvanceThreshold, int DuplicateWindowMinutes, int PaymentHoldMinutes, DeductAt DeductAt, RoutingMode RoutingMode, int MaxManualQuantity);

/// <summary>Reads and writes <see cref="PlatformSetting"/> rows. Missing or unreadable values fall back to the defaults documented in docs/assumptions.md section M.</summary>
public sealed class SettingsService(IApplicationDbContext db)
{
    public static readonly OrderSettings Defaults = new(OtpMode.CodOnly, 10_000m, 5, 30, DeductAt.Packing, RoutingMode.AllStock, 100);

    public async Task<Dictionary<string, string>> ReadAsync(IReadOnlyCollection<string> keys, CancellationToken cancellationToken) =>
        await db.PlatformSettings.AsNoTracking().Where(s => keys.Contains(s.Key)).ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);

    public async Task<OrderSettings> OrdersAsync(CancellationToken cancellationToken)
    {
        string[] keys = [SettingKeys.OtpMode, SettingKeys.CodAdvanceThreshold, SettingKeys.DuplicateWindowMinutes, SettingKeys.PaymentHoldMinutes, SettingKeys.DeductAt, SettingKeys.RoutingMode, SettingKeys.MaxManualQuantity];
        var values = await ReadAsync(keys, cancellationToken);
        return new OrderSettings(
            EnumValue(values, SettingKeys.OtpMode, Defaults.OtpMode),
            Number(values, SettingKeys.CodAdvanceThreshold, Defaults.CodAdvanceThreshold, 0m),
            (int)Number(values, SettingKeys.DuplicateWindowMinutes, Defaults.DuplicateWindowMinutes, 0m),
            (int)Number(values, SettingKeys.PaymentHoldMinutes, Defaults.PaymentHoldMinutes, 1m),
            EnumValue(values, SettingKeys.DeductAt, Defaults.DeductAt),
            EnumValue(values, SettingKeys.RoutingMode, Defaults.RoutingMode),
            (int)Number(values, SettingKeys.MaxManualQuantity, Defaults.MaxManualQuantity, 1m));
    }

    /// <summary>Stores a value (adds the row when new). The caller saves.</summary>
    public async Task SetAsync(string key, string value, CancellationToken cancellationToken)
    {
        var row = await db.PlatformSettings.FirstOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (row is null)
        {
            db.PlatformSettings.Add(new PlatformSetting(key, value));
        }
        else
        {
            row.Set(value);
        }
    }

    public static T EnumValue<T>(IReadOnlyDictionary<string, string> values, string key, T fallback)
        where T : struct, Enum =>
        values.TryGetValue(key, out var raw) && System.Enum.TryParse<T>(raw, ignoreCase: true, out var parsed) && System.Enum.IsDefined(parsed) ? parsed : fallback;

    public static decimal Number(IReadOnlyDictionary<string, string> values, string key, decimal fallback, decimal min) =>
        values.TryGetValue(key, out var raw) && decimal.TryParse(raw, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) && parsed >= min ? parsed : fallback;
}
