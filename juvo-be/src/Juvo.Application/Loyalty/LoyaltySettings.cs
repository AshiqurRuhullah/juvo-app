using System.Globalization;
using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Domain.Loyalty;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Loyalty;

/// <summary>Keys of the loyalty settings (table <c>PlatformSettings</c>). The feature flag is <see cref="Enabled"/> and is OFF by default.</summary>
public static class LoyaltySettingKeys
{
    public const string Enabled = "Loyalty.Enabled";
    public const string EarnRatePerBdt = "Loyalty.EarnRatePerBdt";
    public const string EarnRounding = "Loyalty.EarnRounding";
    public const string PointValue = "Loyalty.PointValue";
    public const string MinPointsToRedeem = "Loyalty.MinPointsToRedeem";
    public const string MaxTenderPercent = "Loyalty.MaxTenderPercent";
    public const string PointsExpiryMonths = "Loyalty.PointsExpiryMonths";
    public const string ExpiryWarningDays = "Loyalty.ExpiryWarningDays";
    public const string ReferralRewardReferrer = "Loyalty.ReferralRewardReferrer";
    public const string ReferralRewardReferee = "Loyalty.ReferralRewardReferee";
    public const string ReferralMaxRewards = "Loyalty.ReferralMaxRewardsPerReferrer";
    public const string GiftCardMinAmount = "Loyalty.GiftCardMinAmount";
    public const string GiftCardMaxAmount = "Loyalty.GiftCardMaxAmount";
    public const string GiftCardValidityMonths = "Loyalty.GiftCardDefaultValidityMonths";

    public static readonly IReadOnlyList<string> All =
    [
        Enabled, EarnRatePerBdt, EarnRounding, PointValue, MinPointsToRedeem, MaxTenderPercent, PointsExpiryMonths, ExpiryWarningDays, ReferralRewardReferrer, ReferralRewardReferee,
        ReferralMaxRewards, GiftCardMinAmount, GiftCardMaxAmount, GiftCardValidityMonths,
    ];
}

/// <summary>The loyalty settings as read for one request. Money is BDT.</summary>
public sealed record LoyaltySettings(
    bool Enabled, decimal EarnRatePerBdt, PointsRounding EarnRounding, decimal PointValue, int MinPointsToRedeem, int MaxTenderPercent, int PointsExpiryMonths, int ExpiryWarningDays,
    decimal ReferralRewardReferrer, decimal ReferralRewardReferee, int ReferralMaxRewardsPerReferrer, decimal GiftCardMinAmount, decimal GiftCardMaxAmount, int GiftCardDefaultValidityMonths)
{
    /// <summary>1 point per 100 BDT, each point worth 1 BDT, at least 100 points to use, points and credit may pay at most half an order, points last a year.</summary>
    public static readonly LoyaltySettings Defaults = new(false, 0.01m, PointsRounding.Down, 1m, 100, 50, 12, 14, 100m, 100m, 20, 100m, 20_000m, 12);
}

/// <summary>Every field is optional: only the ones sent change.</summary>
public sealed record UpdateLoyaltySettingsRequest(
    bool? Enabled, decimal? EarnRatePerBdt, PointsRounding? EarnRounding, decimal? PointValue, int? MinPointsToRedeem, int? MaxTenderPercent, int? PointsExpiryMonths, int? ExpiryWarningDays,
    decimal? ReferralRewardReferrer, decimal? ReferralRewardReferee, int? ReferralMaxRewardsPerReferrer, decimal? GiftCardMinAmount, decimal? GiftCardMaxAmount, int? GiftCardDefaultValidityMonths);

/// <summary>Reads and validates the loyalty settings (FR-AB01). Staff changes are audited through the generic settings audit (every <c>PlatformSetting</c> change is logged with old and new value).</summary>
public sealed class LoyaltySettingsService(IApplicationDbContext db, SettingsService settings)
{
    public static Error FeatureOff() => Error.Conflict(ErrorCodes.Loyalty.FeatureOff, "Rewards (points, store credit, gift cards and referrals) are not switched on.");

    public async Task<LoyaltySettings> ReadAsync(CancellationToken cancellationToken)
    {
        var v = await settings.ReadAsync(LoyaltySettingKeys.All, cancellationToken);
        var d = LoyaltySettings.Defaults;
        return new LoyaltySettings(
            v.TryGetValue(LoyaltySettingKeys.Enabled, out var on) && bool.TryParse(on, out var enabled) ? enabled : d.Enabled,
            SettingsService.Number(v, LoyaltySettingKeys.EarnRatePerBdt, d.EarnRatePerBdt, 0m),
            SettingsService.EnumValue(v, LoyaltySettingKeys.EarnRounding, d.EarnRounding),
            SettingsService.Number(v, LoyaltySettingKeys.PointValue, d.PointValue, 0.01m),
            (int)SettingsService.Number(v, LoyaltySettingKeys.MinPointsToRedeem, d.MinPointsToRedeem, 0m),
            (int)SettingsService.Number(v, LoyaltySettingKeys.MaxTenderPercent, d.MaxTenderPercent, 0m),
            (int)SettingsService.Number(v, LoyaltySettingKeys.PointsExpiryMonths, d.PointsExpiryMonths, 0m),
            (int)SettingsService.Number(v, LoyaltySettingKeys.ExpiryWarningDays, d.ExpiryWarningDays, 0m),
            SettingsService.Number(v, LoyaltySettingKeys.ReferralRewardReferrer, d.ReferralRewardReferrer, 0m),
            SettingsService.Number(v, LoyaltySettingKeys.ReferralRewardReferee, d.ReferralRewardReferee, 0m),
            (int)SettingsService.Number(v, LoyaltySettingKeys.ReferralMaxRewards, d.ReferralMaxRewardsPerReferrer, 0m),
            SettingsService.Number(v, LoyaltySettingKeys.GiftCardMinAmount, d.GiftCardMinAmount, 0.01m),
            SettingsService.Number(v, LoyaltySettingKeys.GiftCardMaxAmount, d.GiftCardMaxAmount, 0.01m),
            (int)SettingsService.Number(v, LoyaltySettingKeys.GiftCardValidityMonths, d.GiftCardDefaultValidityMonths, 0m));
    }

    /// <summary>The settings, or the "feature off" error when the flag is off (used by every customer endpoint and every hook).</summary>
    public async Task<Result<LoyaltySettings>> RequireEnabledAsync(CancellationToken cancellationToken)
    {
        var s = await ReadAsync(cancellationToken);
        return s.Enabled ? s : FeatureOff();
    }

    public async Task<Result<LoyaltySettings>> UpdateAsync(UpdateLoyaltySettingsRequest r, CancellationToken cancellationToken)
    {
        var errors = new Dictionary<string, string[]>();

        void Check(string field, bool bad, string message)
        {
            if (bad)
            {
                errors[field] = [message];
            }
        }

        static bool TooManyDecimals(decimal value, int places) => decimal.Round(value, places) != value;

        if (r.EarnRatePerBdt is { } rate)
        {
            Check("earnRatePerBdt", rate < 0 || rate > 100 || TooManyDecimals(rate, 4), "Use 0 to 100 points per BDT with at most 4 decimals (0.01 means 1 point per 100 BDT).");
        }

        if (r.EarnRounding is { } rounding)
        {
            Check("earnRounding", !Enum.IsDefined(rounding), "Choose Down, Nearest or Up.");
        }

        if (r.PointValue is { } value)
        {
            Check("pointValue", value < 0.01m || value > 1000m || TooManyDecimals(value, 2), "A point is worth 0.01 to 1000 BDT, with at most 2 decimals.");
        }

        if (r.MinPointsToRedeem is { } min)
        {
            Check("minPointsToRedeem", min is < 0 or > 1_000_000, "Use 0 to 1,000,000 points.");
        }

        if (r.MaxTenderPercent is { } pct)
        {
            Check("maxTenderPercent", pct is < 0 or > 100, "Use 0 to 100 percent (0 stops points and credit from paying an order).");
        }

        if (r.PointsExpiryMonths is { } months)
        {
            Check("pointsExpiryMonths", months is < 0 or > 120, "Use 0 (never) to 120 months.");
        }

        if (r.ExpiryWarningDays is { } days)
        {
            Check("expiryWarningDays", days is < 0 or > 180, "Use 0 (no warning) to 180 days.");
        }

        foreach (var (field, reward) in new[] { ("referralRewardReferrer", r.ReferralRewardReferrer), ("referralRewardReferee", r.ReferralRewardReferee) })
        {
            if (reward is { } amount)
            {
                Check(field, amount < 0 || amount > 100_000m || TooManyDecimals(amount, 2), "Use 0 to 100,000 BDT with at most 2 decimals.");
            }
        }

        if (r.ReferralMaxRewardsPerReferrer is { } cap)
        {
            Check("referralMaxRewardsPerReferrer", cap is < 0 or > 10_000, "Use 0 (no limit) to 10,000.");
        }

        var current = await ReadAsync(cancellationToken);
        var gMin = r.GiftCardMinAmount ?? current.GiftCardMinAmount;
        var gMax = r.GiftCardMaxAmount ?? current.GiftCardMaxAmount;
        if (r.GiftCardMinAmount is not null || r.GiftCardMaxAmount is not null)
        {
            Check("giftCardMinAmount", gMin < 0.01m || TooManyDecimals(gMin, 2), "Use an amount of at least 0.01 with at most 2 decimals.");
            Check("giftCardMaxAmount", gMax > 10_000_000m || gMax < gMin || TooManyDecimals(gMax, 2), "The maximum must be at least the minimum and at most 10,000,000.");
        }

        if (r.GiftCardDefaultValidityMonths is { } validity)
        {
            Check("giftCardDefaultValidityMonths", validity is < 0 or > 120, "Use 0 (never expires) to 120 months.");
        }

        if (errors.Count > 0)
        {
            return Error.Validation(errors);
        }

        async Task Set(string key, string? text)
        {
            if (text is not null)
            {
                await settings.SetAsync(key, text, cancellationToken);
            }
        }

        static string N(decimal v) => v.ToString(CultureInfo.InvariantCulture);
        static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

        await Set(LoyaltySettingKeys.Enabled, r.Enabled is { } e ? (e ? "true" : "false") : null);
        await Set(LoyaltySettingKeys.EarnRatePerBdt, r.EarnRatePerBdt is { } a ? N(a) : null);
        await Set(LoyaltySettingKeys.EarnRounding, r.EarnRounding?.ToString());
        await Set(LoyaltySettingKeys.PointValue, r.PointValue is { } b ? N(b) : null);
        await Set(LoyaltySettingKeys.MinPointsToRedeem, r.MinPointsToRedeem is { } c ? I(c) : null);
        await Set(LoyaltySettingKeys.MaxTenderPercent, r.MaxTenderPercent is { } d ? I(d) : null);
        await Set(LoyaltySettingKeys.PointsExpiryMonths, r.PointsExpiryMonths is { } f ? I(f) : null);
        await Set(LoyaltySettingKeys.ExpiryWarningDays, r.ExpiryWarningDays is { } g ? I(g) : null);
        await Set(LoyaltySettingKeys.ReferralRewardReferrer, r.ReferralRewardReferrer is { } h ? N(h) : null);
        await Set(LoyaltySettingKeys.ReferralRewardReferee, r.ReferralRewardReferee is { } i ? N(i) : null);
        await Set(LoyaltySettingKeys.ReferralMaxRewards, r.ReferralMaxRewardsPerReferrer is { } j ? I(j) : null);
        await Set(LoyaltySettingKeys.GiftCardMinAmount, r.GiftCardMinAmount is { } k ? N(k) : null);
        await Set(LoyaltySettingKeys.GiftCardMaxAmount, r.GiftCardMaxAmount is { } l ? N(l) : null);
        await Set(LoyaltySettingKeys.GiftCardValidityMonths, r.GiftCardDefaultValidityMonths is { } m ? I(m) : null);
        await db.SaveChangesAsync(cancellationToken);
        return await ReadAsync(cancellationToken);
    }
}
