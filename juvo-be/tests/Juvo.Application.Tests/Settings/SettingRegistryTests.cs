using System.Globalization;
using System.Text.Json;
using Juvo.Application.Billing;
using Juvo.Application.Loyalty;
using Juvo.Application.Marketing;
using Juvo.Application.Notifications;
using Juvo.Application.Orders;
using Juvo.Application.Payments;
using Juvo.Application.Returns;
using Juvo.Application.Settings;
using Juvo.Shared.Security;

namespace Juvo.Application.Tests.Settings;

public class SettingRegistryTests
{
    private static SettingDefinition Def(string key) => SettingRegistry.Find(key) ?? throw new InvalidOperationException(key);

    private static string Num(decimal v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    [Fact]
    public void Every_definition_is_complete_and_its_default_passes_its_own_checks()
    {
        Assert.Equal(SettingRegistry.All.Count, SettingRegistry.All.Select(d => d.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (var d in SettingRegistry.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(d.DescriptionEn), d.Key);
            Assert.False(string.IsNullOrWhiteSpace(d.DescriptionBn), d.Key);
            Assert.Contains(d.Group, SettingGroups.Order);
            Assert.True(PermissionCatalog.Exists(d.EditPermission), $"{d.Key}: unknown permission {d.EditPermission}");
            var normalised = d.Normalize(d.Default);
            Assert.True(normalised.IsSuccess, $"{d.Key}: default '{d.Default}' is refused");
            Assert.Equal(d.Default, normalised.Value);
            if (d.Type == SettingType.Choice)
            {
                Assert.NotEmpty(d.AllowedValues!);
            }
        }
    }

    [Fact]
    public void The_defaults_repeat_what_the_modules_use_when_nothing_is_stored()
    {
        var l = LoyaltySettings.Defaults;
        Assert.Equal(l.Enabled.ToString().ToLowerInvariant(), Def(LoyaltySettingKeys.Enabled).Default);
        Assert.Equal(Num(l.EarnRatePerBdt), Def(LoyaltySettingKeys.EarnRatePerBdt).Default);
        Assert.Equal(l.EarnRounding.ToString(), Def(LoyaltySettingKeys.EarnRounding).Default);
        Assert.Equal(l.MaxTenderPercent.ToString(CultureInfo.InvariantCulture), Def(LoyaltySettingKeys.MaxTenderPercent).Default);
        Assert.Equal(Num(l.GiftCardMaxAmount), Def(LoyaltySettingKeys.GiftCardMaxAmount).Default);

        var o = SettingsService.Defaults;
        Assert.Equal(o.OtpMode.ToString(), Def(SettingKeys.OtpMode).Default);
        Assert.Equal(o.DeductAt.ToString(), Def(SettingKeys.DeductAt).Default);
        Assert.Equal(o.RoutingMode.ToString(), Def(SettingKeys.RoutingMode).Default);
        Assert.Equal(o.PaymentHoldMinutes.ToString(CultureInfo.InvariantCulture), Def(SettingKeys.PaymentHoldMinutes).Default);
        Assert.Equal(Num(o.CodAdvanceThreshold), Def(SettingKeys.CodAdvanceThreshold).Default);

        var r = ReturnSettings.Defaults;
        Assert.Equal(r.WindowDays.ToString(CultureInfo.InvariantCulture), Def(ReturnSettingKeys.WindowDays).Default);
        Assert.Equal(r.RefundDeliveryOnSellerFault.ToString().ToLowerInvariant(), Def(ReturnSettingKeys.RefundDeliveryOnSellerFault).Default);

        var m = MarketingSettings.Defaults;
        Assert.Equal(m.QuietStart.ToString("HH:mm", CultureInfo.InvariantCulture), Def(MarketingSettingKeys.QuietHoursStart).Default);
        Assert.Equal(m.ThrottlePerMinute.ToString(CultureInfo.InvariantCulture), Def(MarketingSettingKeys.ThrottlePerMinute).Default);

        foreach (var method in PaymentMethodCatalog.All)
        {
            Assert.Equal(method.DefaultEnabled.ToString().ToLowerInvariant(), Def(PaymentSettingKeys.Enabled(method.Code)).Default);
        }

        Assert.Equal("true", Def(NotificationSettingKeys.SmsEnabled).Default);
        Assert.Equal("false", Def(NotificationSettingKeys.WhatsAppEnabled).Default);
        Assert.Equal("Juvo BD", Def(BusinessSettingKeys.Name).Default);
    }

    [Theory]
    [InlineData("TRUE", "true")]
    [InlineData(" false ", "false")]
    public void A_flag_accepts_true_and_false_in_any_case(string raw, string stored) => Assert.Equal(stored, Def(LoyaltySettingKeys.Enabled).Normalize(raw).Value);

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("")]
    public void A_flag_refuses_anything_else(string raw)
    {
        var result = Def(LoyaltySettingKeys.Enabled).Normalize(raw);
        Assert.True(result.IsFailure);
        Assert.Equal("SETTINGS.INVALID_VALUE", result.Error!.Code);
        Assert.Contains("value", result.Error.ValidationErrors.Keys);
    }

    [Theory]
    [InlineData("0", true)]
    [InlineData("100", true)]
    [InlineData("101", false)]
    [InlineData("-1", false)]
    [InlineData("12.5", false)]
    [InlineData("abc", false)]
    public void A_whole_number_must_be_a_number_inside_its_range(string raw, bool ok) => Assert.Equal(ok, Def(LoyaltySettingKeys.MaxTenderPercent).Normalize(raw).IsSuccess);

    [Theory]
    [InlineData("0.5", "0.5")]
    [InlineData("1", "1")]
    [InlineData("1000", "1000")]
    [InlineData("0.00001", null)]
    [InlineData("-1", null)]
    [InlineData("1000.5", null)]
    public void A_decimal_is_limited_in_range_and_decimals(string raw, string? stored)
    {
        var result = Def(LoyaltySettingKeys.PointValue).Normalize(raw);
        Assert.Equal(stored is not null, result.IsSuccess);
        if (stored is not null)
        {
            Assert.Equal(stored, result.Value);
        }
    }

    [Fact]
    public void A_choice_is_matched_without_case_and_stored_with_its_canonical_spelling()
    {
        Assert.Equal("Delivery", Def(SettingKeys.DeductAt).Normalize("delivery").Value);
        Assert.Equal("bn", Def(NotificationSettingKeys.DefaultLanguage).Normalize("BN").Value);
        var bad = Def(SettingKeys.DeductAt).Normalize("Never");
        Assert.True(bad.IsFailure);
        Assert.Contains("Packing", bad.Error!.Message);
        Assert.True(Def(SettingKeys.DeductAt).Normalize("2").IsFailure);
    }

    [Fact]
    public void A_text_is_trimmed_limited_and_checked_for_its_format()
    {
        Assert.Equal("Juvo", Def(BusinessSettingKeys.Name).Normalize("  Juvo ").Value);
        Assert.True(Def(BusinessSettingKeys.Name).Normalize(new string('x', 151)).IsFailure);
        Assert.Equal(string.Empty, Def(BusinessSettingKeys.Email).Normalize("  ").Value);
        Assert.True(Def(BusinessSettingKeys.Email).Normalize("owner@juvo.example").IsSuccess);
        Assert.True(Def(BusinessSettingKeys.Email).Normalize("not an email").IsFailure);
        Assert.True(Def(MarketingSettingKeys.LinkUrl).Normalize("https://juvo.example").IsSuccess);
        Assert.True(Def(MarketingSettingKeys.LinkUrl).Normalize("javascript:alert(1)").IsFailure);
        Assert.True(Def(MarketingSettingKeys.QuietHoursStart).Normalize("21:30").IsSuccess);
        Assert.True(Def(MarketingSettingKeys.QuietHoursStart).Normalize("25:00").IsFailure);
        Assert.True(Def(MarketingSettingKeys.QuietHoursStart).Normalize(string.Empty).IsFailure);
    }

    [Fact]
    public void Json_values_become_the_text_a_setting_stores()
    {
        static string? Of(string json) => SettingDefinition.FromJson(JsonDocument.Parse(json).RootElement);
        Assert.Equal("true", Of("true"));
        Assert.Equal("false", Of("false"));
        Assert.Equal("12", Of("12"));
        Assert.Equal("0.5", Of("0.5"));
        Assert.Equal("text", Of("\"text\""));
        Assert.Null(Of("null"));
        Assert.Null(Of("[1]"));
        Assert.Null(Of("{}"));
    }

    [Fact]
    public void Typed_values_follow_the_definition_type()
    {
        Assert.Equal(true, Def(LoyaltySettingKeys.Enabled).Typed("true"));
        Assert.Equal(50, Def(LoyaltySettingKeys.MaxTenderPercent).Typed("50"));
        Assert.Equal(0.01m, Def(LoyaltySettingKeys.EarnRatePerBdt).Typed("0.01"));
        Assert.Equal("Packing", Def(SettingKeys.DeductAt).Typed("Packing"));
    }

    [Fact]
    public void Unknown_keys_are_not_found_and_no_definition_is_sensitive_yet()
    {
        Assert.Null(SettingRegistry.Find("Nope.Nothing"));
        Assert.Null(SettingRegistry.Find(null));
        Assert.False(SettingRegistry.IsSensitive("Nope.Nothing"));
        Assert.False(SettingRegistry.IsSensitive(LoyaltySettingKeys.Enabled));
    }
}
