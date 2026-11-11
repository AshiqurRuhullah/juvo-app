using Juvo.Application.Notifications;
using Juvo.Application.Payments;
using Juvo.Domain.Notifications;

namespace Juvo.Application.Tests.Payments;

public class WebhookSignatureTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 10, 0, 0, TimeSpan.Zero);
    private const string Secret = "unit-test-secret";
    private const string Body = """{"id":"evt_1","type":"payment.succeeded"}""";

    [Fact]
    public void A_correctly_signed_fresh_webhook_is_accepted()
    {
        var header = WebhookSignature.Sign(Secret, Now, Body);
        Assert.Null(WebhookSignature.Check(Secret, header, Body, Now.AddSeconds(30)));
    }

    [Fact]
    public void A_changed_body_a_wrong_secret_or_a_missing_header_is_refused()
    {
        var header = WebhookSignature.Sign(Secret, Now, Body);

        Assert.Equal("PAYMENT.INVALID_SIGNATURE", WebhookSignature.Check(Secret, header, Body + " ", Now)!.Code);
        Assert.Equal("PAYMENT.INVALID_SIGNATURE", WebhookSignature.Check("another-secret", header, Body, Now)!.Code);
        Assert.Equal("PAYMENT.INVALID_SIGNATURE", WebhookSignature.Check(Secret, null, Body, Now)!.Code);
        Assert.Equal("PAYMENT.INVALID_SIGNATURE", WebhookSignature.Check(Secret, "garbage", Body, Now)!.Code);
        Assert.Equal("PAYMENT.INVALID_SIGNATURE", WebhookSignature.Check(Secret, "t=1,v1=zz", Body, Now)!.Code);
        Assert.Equal("PAYMENT.INVALID_SIGNATURE", WebhookSignature.Check(string.Empty, header, Body, Now)!.Code);
    }

    [Fact]
    public void A_correct_signature_outside_the_replay_window_is_refused_as_expired()
    {
        var header = WebhookSignature.Sign(Secret, Now, Body);

        Assert.Equal("PAYMENT.WEBHOOK_EXPIRED", WebhookSignature.Check(Secret, header, Body, Now.AddMinutes(6))!.Code);
        Assert.Equal("PAYMENT.WEBHOOK_EXPIRED", WebhookSignature.Check(Secret, header, Body, Now.AddMinutes(-6))!.Code);
        Assert.Null(WebhookSignature.Check(Secret, header, Body, Now.AddMinutes(4)));
    }

    [Fact]
    public async Task A_provider_slot_without_credentials_answers_not_configured_to_everything()
    {
        var slot = new NotConfiguredGateway("BKASH", "bKash");

        Assert.False(slot.IsConfigured);
        var started = await slot.InitiateAsync(new GatewayInitiateRequest(Guid.NewGuid(), "JV-1", 10m, "BDT", "BKASH", null, null, null, Now), TestContext.Current.CancellationToken);
        Assert.Equal("PAYMENT.NOT_CONFIGURED", started.Error!.Code);
        Assert.Equal("PAYMENT.NOT_CONFIGURED", (await slot.VerifyAsync("x", TestContext.Current.CancellationToken)).Error!.Code);
        Assert.Equal("PAYMENT.NOT_CONFIGURED", (await slot.RefundAsync(new GatewayRefundRequest("x", null, 1m, "BDT", "r"), TestContext.Current.CancellationToken)).Error!.Code);
        Assert.Equal("PAYMENT.NOT_CONFIGURED", slot.ParseWebhook("{}", new Dictionary<string, string>(), Now).Error!.Code);
    }

    [Fact]
    public void The_registry_finds_methods_by_code_and_the_last_registration_wins()
    {
        var registry = new PaymentGatewayRegistry([new NotConfiguredGateway("BKASH", "first"), new NotConfiguredGateway("BKASH", "second"), new NotConfiguredGateway("NAGAD", "Nagad")]);

        Assert.Equal("second", registry.Find("bkash")!.DisplayName);
        Assert.NotNull(registry.Find("NAGAD"));
        Assert.Null(registry.Find("UNKNOWN"));
    }

    [Fact]
    public void The_payment_method_catalogue_lists_cod_the_sandbox_manual_and_the_six_provider_slots()
    {
        var codes = PaymentMethodCatalog.All.Select(m => m.Code).ToList();

        Assert.Equal(["COD", "SANDBOX", "MANUAL", "BKASH", "NAGAD", "SSLCOMMERZ", "AAMARPAY", "STRIPE", "PAYPAL"], codes);
        Assert.Equal(["COD", "SANDBOX"], PaymentMethodCatalog.All.Where(m => m.DefaultEnabled).Select(m => m.Code));
    }
}

public class NotificationTemplateTests
{
    [Fact]
    public void Placeholders_are_replaced_and_missing_values_become_empty()
    {
        var text = NotificationCatalog.Render("Order {{OrderNumber}} total {{ Total }} for {{Name}} {{Unknown}}.",
            new Dictionary<string, string> { ["OrderNumber"] = "JV-1", ["Total"] = "1250", ["Name"] = "Nadia" });

        Assert.Equal("Order JV-1 total 1250 for Nadia .", text);
    }

    [Fact]
    public void Every_default_uses_only_placeholders_its_code_declares()
    {
        foreach (var t in NotificationCatalog.Defaults)
        {
            var definition = NotificationCatalog.Find(t.Code);
            Assert.NotNull(definition);
            foreach (var p in NotificationCatalog.PlaceholdersIn(t.Body).Concat(NotificationCatalog.PlaceholdersIn(t.Subject)))
            {
                Assert.True(definition.Variables.Contains(p), $"{t.Code} {t.Channel} {t.Language} uses {{{{{p}}}}} which is not declared");
            }
        }
    }

    [Fact]
    public void Every_code_has_an_english_and_a_bengali_sms_and_an_email_has_a_subject()
    {
        foreach (var d in NotificationCatalog.Definitions)
        {
            foreach (var language in NotificationCatalog.Languages)
            {
                Assert.NotNull(NotificationCatalog.DefaultFor(d.Code, NotificationChannel.Sms, language));
            }
        }

        Assert.All(NotificationCatalog.Defaults.Where(t => t.Channel == NotificationChannel.Email), t => Assert.False(string.IsNullOrWhiteSpace(t.Subject)));
    }

    [Fact]
    public void Bengali_defaults_are_written_in_Bengali_script_and_keep_the_same_placeholders_as_english()
    {
        foreach (var t in NotificationCatalog.Defaults.Where(t => t.Language == "bn"))
        {
            Assert.Contains(t.Body, ch => ch is >= 'ঀ' and <= '৿');
            var english = NotificationCatalog.DefaultFor(t.Code, t.Channel, "en");
            Assert.NotNull(english);
            // Bengali may leave a placeholder out (for example {{KindBn}} replaces {{Kind}}), but never invents one.
            var allowed = NotificationCatalog.PlaceholdersIn(english.Body).Concat(["KindBn", "Kind"]).ToHashSet();
            Assert.All(NotificationCatalog.PlaceholdersIn(t.Body), p => Assert.Contains(p, allowed));
        }
    }

    [Fact]
    public void The_marketing_template_is_the_only_one_that_needs_consent()
    {
        Assert.Equal(["marketing.promotion"], NotificationCatalog.Definitions.Where(d => d.Category == NotificationCategory.Marketing).Select(d => d.Code));
    }

    [Theory]
    [InlineData("bn", "bn")]
    [InlineData(" EN ", "en")]
    [InlineData("fr", "")]
    [InlineData(null, "")]
    public void Languages_are_normalised_to_en_or_bn(string? input, string expected) => Assert.Equal(expected, NotificationService.NormalizeLanguage(input));
}
