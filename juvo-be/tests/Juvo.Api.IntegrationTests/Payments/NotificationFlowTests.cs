using System.Net;
using System.Text.Json;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Application.Notifications;
using Juvo.Domain.Customers;
using Juvo.Domain.Notifications;
using Juvo.Domain.Platform;
using Juvo.Infrastructure.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Juvo.Api.IntegrationTests.Payments;

/// <summary>An SMS sender the tests can switch between working and failing (the real sandbox sender sits behind it for every other channel).</summary>
public sealed class ControllableSmsSender : INotificationSender
{
    private int _failuresLeft;

    public NotificationChannel Channel => NotificationChannel.Sms;

    public bool AlwaysFail { get; set; }

    public int Attempts { get; private set; }

    public void FailNext(int times) => _failuresLeft = times;

    public void Reset()
    {
        _failuresLeft = 0;
        AlwaysFail = false;
        Attempts = 0;
    }

    public Task<SendOutcome> SendAsync(OutboundMessage message, CancellationToken cancellationToken)
    {
        Attempts++;
        if (AlwaysFail || _failuresLeft > 0)
        {
            _failuresLeft = Math.Max(0, _failuresLeft - 1);
            return Task.FromResult(SendOutcome.Fail("The SMS provider is down (test)."));
        }

        return Task.FromResult(SendOutcome.Ok("controllable"));
    }
}

public sealed class NotificationFactory : PaymentFactory
{
    public ControllableSmsSender Sms { get; } = new();

    protected override void ExtraSettings()
    {
        base.ExtraSettings();
        Setting("Notifications:Delivery:MaxAttempts", "3");
    }

    protected override void ConfigureTestServices(IServiceCollection services) => services.AddSingleton<INotificationSender>(Sms); // registered last: it takes over the SMS channel
}

public class NotificationFlowTests(NotificationFactory factory) : IClassFixture<NotificationFactory>
{
    private readonly PaymentHelper _p = new(new OrderHelper(new StoreHelper(factory)));

    private StoreHelper H => _p.H;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Str(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    private OutboxProcessor Processor => factory.Services.GetServices<IHostedService>().OfType<OutboxProcessor>().Single();

    private async Task DrainAsync()
    {
        while (await Processor.ProcessBatchAsync(Ct) > 0)
        {
        }
    }

    private Task<int> MakeRetriesDueAsync() =>
        _p.DbAsync(async db => await db.OutboxMessages.Where(m => m.Status == OutboxStatus.Pending).ExecuteUpdateAsync(set => set.SetProperty(m => m.NextAttemptAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct));

    private Task<List<NotificationLog>> LogsAsync(string mobile, string? prefix = null) =>
        _p.DbAsync(db => db.NotificationLogs.AsNoTracking().Where(n => (n.Recipient == "+88" + mobile) && (prefix == null || n.Template.StartsWith(prefix))).OrderBy(n => n.QueuedAt).ToListAsync(Ct));

    private async Task<(string Admin, SeededProduct Product)> ProductAsync()
    {
        var admin = await H.AdminAsync();
        var category = await H.CategoryAsync(admin);
        return (admin, await H.ProductAsync(admin, category.Id, StoreHelper.Unique("Notify Tee"), 500m, [("Red", "M", 50)], weightGrams: 300));
    }

    private async Task<string> PlaceCodAsync(Guid variantId, string? mobile = null)
    {
        mobile ??= StoreHelper.NewMobile();
        await _p.O.PlaceAsync(variantId, 1, mobile);
        return mobile;
    }

    // ---- sending ------------------------------------------------------------------------------------------

    [Fact]
    public async Task An_order_message_is_queued_then_delivered_once_with_its_language_and_template_version()
    {
        factory.Sms.Reset();
        var (_, product) = await ProductAsync();
        var mobile = await PlaceCodAsync(product.Variants[0].Id);

        Assert.Empty(await LogsAsync(mobile, "order.")); // nothing is sent before the outbox runs: the order request itself stays fast
        await DrainAsync();

        var log = Assert.Single(await LogsAsync(mobile, "order.placed"));
        Assert.Equal(NotificationStatus.Sent, log.Status);
        Assert.Equal(NotificationChannel.Sms, log.Channel);
        Assert.Equal("en", log.Language);
        Assert.Equal(0, log.TemplateVersion); // the built-in text
        Assert.Equal(NotificationCategory.Transactional, log.Category);
        Assert.Equal(1, log.Attempts);
        Assert.StartsWith("Juvo BD: we received your order JV-HO-", log.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("{{", log.Body, StringComparison.Ordinal);
        Assert.Equal(1, factory.Sms.Attempts);

        await DrainAsync(); // nothing more happens
        Assert.Single(await LogsAsync(mobile, "order.placed"));
        Assert.Equal(1, factory.Sms.Attempts);
    }

    [Fact]
    public async Task Asking_for_the_same_message_twice_queues_it_once()
    {
        var (_, product) = await ProductAsync();
        var mobile = await PlaceCodAsync(product.Variants[0].Id);
        using var scope = factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<NotificationService>();
        var request = new NotifyRequest("order.confirmed", "+88" + mobile, null, null, new Dictionary<string, string> { ["OrderNumber"] = "JV-X-1" }, "same-key-1");

        await service.NotifyAsync(request, Ct);
        await service.NotifyAsync(request, Ct);

        Assert.Single(await LogsAsync(mobile, "order.confirmed"));
    }

    [Fact]
    public async Task A_customer_who_chose_Bengali_gets_Bengali_messages_and_sign_in_codes()
    {
        var (_, product) = await ProductAsync();
        var mobile = StoreHelper.NewMobile();
        var customer = await H.CustomerTokenAsync(mobile);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, "/api/v1/me/notification-language", new { language = "fr" }, customer), "NOTIFY.LANGUAGE_INVALID");
        Assert.Equal(HttpStatusCode.NoContent, (await H.SendAsync(HttpMethod.Put, "/api/v1/me/notification-language", new { language = "bn" }, customer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Put, "/api/v1/me/notification-language", new { language = "bn" })).StatusCode);

        await PlaceCodAsync(product.Variants[0].Id, mobile);
        await DrainAsync();

        var log = (await LogsAsync(mobile, "order.placed")).Single();
        Assert.Equal("bn", log.Language);
        Assert.Contains("আপনার অর্ডার", log.Body, StringComparison.Ordinal);
        Assert.Contains("JV-HO-", log.Body, StringComparison.Ordinal);

        // The sign-in code is read from the template too, in the customer's language.
        var otp = await H.JsonAsync(HttpMethod.Post, "/api/v1/auth/otp/request", HttpStatusCode.Accepted, new { mobile, purpose = "SignIn" });
        var sms = (await LogsAsync(mobile, "auth.otp")).Last();
        Assert.Contains(otp.GetProperty("devCode").GetString()!, sms.Body, StringComparison.Ordinal);
        Assert.Contains("ভেরিফিকেশন কোড", sms.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sign_in_codes_use_the_shop_default_language_for_a_stranger()
    {
        var admin = await H.AdminAsync();
        await H.JsonAsync(HttpMethod.Put, "/api/v1/notifications/channels", HttpStatusCode.OK, new { defaultLanguage = "bn" }, admin);
        try
        {
            var mobile = StoreHelper.NewMobile();
            await H.JsonAsync(HttpMethod.Post, "/api/v1/auth/otp/request", HttpStatusCode.Accepted, new { mobile, purpose = "SignIn" });
            Assert.Contains("ভেরিফিকেশন কোড", (await LogsAsync(mobile, "auth.otp")).Last().Body, StringComparison.Ordinal);
        }
        finally
        {
            await H.JsonAsync(HttpMethod.Put, "/api/v1/notifications/channels", HttpStatusCode.OK, new { defaultLanguage = "en" }, admin);
        }
    }

    // ---- consent -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Marketing_messages_need_consent_and_transactional_ones_never_do()
    {
        var (_, product) = await ProductAsync();
        var mobile = await PlaceCodAsync(product.Variants[0].Id);
        var customerId = await _p.DbAsync(db => db.Customers.Where(c => c.Mobile == "+88" + mobile).Select(c => c.Id).SingleAsync(Ct));
        await DrainAsync();
        Assert.Single(await LogsAsync(mobile, "order.placed")); // no consent given, and the order message was still sent

        async Task SendPromotionAsync(string key)
        {
            using var scope = factory.Services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<NotificationService>().NotifyAsync(
                new NotifyRequest("marketing.promotion", "+88" + mobile, null, customerId, new Dictionary<string, string> { ["Message"] = "Eid sale starts today" }, key), Ct);
        }

        await SendPromotionAsync("promo-1"); // no consent
        var skipped = Assert.Single(await LogsAsync(mobile, "marketing."));
        Assert.Equal(NotificationStatus.Skipped, skipped.Status);
        Assert.Contains("not agreed", skipped.LastError, StringComparison.Ordinal);

        await _p.DbAsync(async db =>
        {
            db.ConsentLogs.Add(new ConsentLog(customerId, ConsentType.MarketingSms, true, "test", null, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync(Ct);
            return 0;
        });
        await SendPromotionAsync("promo-2");
        await DrainAsync();
        Assert.Contains(await LogsAsync(mobile, "marketing."), l => l.Status == NotificationStatus.Sent && l.Body == "Juvo BD: Eid sale starts today");

        await _p.DbAsync(async db =>
        {
            db.ConsentLogs.Add(new ConsentLog(customerId, ConsentType.MarketingSms, false, "test", null, DateTimeOffset.UtcNow.AddSeconds(5)));
            await db.SaveChangesAsync(Ct);
            return 0;
        });
        await SendPromotionAsync("promo-3"); // withdrawn: the latest answer counts
        Assert.Equal(2, (await LogsAsync(mobile, "marketing.")).Count(l => l.Status == NotificationStatus.Skipped));
        Assert.Equal(1, (await LogsAsync(mobile, "marketing.")).Count(l => l.Status == NotificationStatus.Sent));
    }

    // ---- retry and dead letter ---------------------------------------------------------------------------

    [Fact]
    public async Task A_failing_provider_is_retried_with_backoff_and_the_message_is_finally_sent()
    {
        factory.Sms.Reset();
        var (_, product) = await ProductAsync();
        await DrainAsync(); // leave no earlier message in the queue to use up the failures
        factory.Sms.FailNext(2);
        var mobile = await PlaceCodAsync(product.Variants[0].Id);

        await DrainAsync();
        var first = Assert.Single(await LogsAsync(mobile, "order.placed"));
        Assert.Equal(NotificationStatus.Failed, first.Status); // failed once: the outbox will try again later, not at once
        Assert.Equal(1, first.Attempts);
        Assert.Contains("provider is down", first.LastError, StringComparison.Ordinal);
        var waiting = await _p.DbAsync(db => db.OutboxMessages.Where(m => m.Status == OutboxStatus.Pending && m.AttemptCount > 0).ToListAsync(Ct));
        Assert.NotEmpty(waiting);
        Assert.All(waiting, m => Assert.True(m.NextAttemptAt > DateTimeOffset.UtcNow)); // backoff: not due yet

        await DrainAsync(); // nothing is due, so nothing is sent
        Assert.Equal(1, (await LogsAsync(mobile, "order.placed")).Single().Attempts);

        await MakeRetriesDueAsync();
        await DrainAsync();
        Assert.Equal(2, (await LogsAsync(mobile, "order.placed")).Single().Attempts);
        await MakeRetriesDueAsync();
        await DrainAsync();

        var done = Assert.Single(await LogsAsync(mobile, "order.placed"));
        Assert.Equal(NotificationStatus.Sent, done.Status);
        Assert.Equal(3, done.Attempts);
        Assert.Null(done.LastError);
    }

    [Fact]
    public async Task A_message_that_keeps_failing_becomes_a_dead_letter_and_staff_can_resend_it()
    {
        factory.Sms.Reset();
        var (admin, product) = await ProductAsync();
        await DrainAsync();
        factory.Sms.AlwaysFail = true;
        var mobile = await PlaceCodAsync(product.Variants[0].Id);

        for (var round = 0; round < 4; round++)
        {
            await DrainAsync();
            await MakeRetriesDueAsync();
        }

        var dead = Assert.Single(await LogsAsync(mobile, "order.placed"));
        Assert.Equal(NotificationStatus.DeadLetter, dead.Status);
        Assert.Equal(3, dead.Attempts); // the configured maximum
        Assert.Equal(0, await _p.DbAsync(db => db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.Failed, Ct))); // the outbox message itself finished; the log holds the dead letter

        var viewer = await _p.O.StaffTokenAsync(admin, null, "notifications.log.view");
        var list = await H.JsonAsync(HttpMethod.Get, "/api/v1/notifications/logs?status=DeadLetter", HttpStatusCode.OK, bearer: viewer);
        Assert.Contains(list.GetProperty("items").EnumerateArray(), x => x.GetProperty("id").GetGuid() == dead.Id && Str(x, "lastError").Contains("provider is down", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Post, $"/api/v1/notifications/logs/{dead.Id}/resend", new { }, viewer)).StatusCode);

        factory.Sms.Reset(); // the provider is back
        var resent = await H.JsonAsync(HttpMethod.Post, $"/api/v1/notifications/logs/{dead.Id}/resend", HttpStatusCode.OK, new { }, admin);
        Assert.Equal("Pending", Str(resent, "status"));
        Assert.Equal(0, resent.GetProperty("attempts").GetInt32());
        await DrainAsync();

        var sent = (await LogsAsync(mobile, "order.placed")).Single();
        Assert.Equal(NotificationStatus.Sent, sent.Status);
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Post, $"/api/v1/notifications/logs/{dead.Id}/resend", new { }, admin), "NOTIFY.NOT_RESENDABLE");
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, $"/api/v1/notifications/logs/{Guid.NewGuid()}/resend", new { }, admin)).StatusCode);
    }

    // ---- channels -----------------------------------------------------------------------------------------

    [Fact]
    public async Task Email_is_sent_when_the_customer_has_an_address_and_whatsapp_only_when_it_is_switched_on()
    {
        factory.Sms.Reset();
        var (admin, product) = await ProductAsync();
        var mobile = await PlaceCodAsync(product.Variants[0].Id);
        await _p.DbAsync(async db => await db.Customers.Where(c => c.Mobile == "+88" + mobile).ExecuteUpdateAsync(set => set.SetProperty(c => c.Email, "nadia@example.com"), Ct));
        await DrainAsync();

        var logs = await LogsAsync(mobile, "order.placed");
        Assert.Contains(logs, l => l.Channel == NotificationChannel.Sms);
        Assert.DoesNotContain(logs, l => l.Channel == NotificationChannel.WhatsApp); // off by default
        var emails = await _p.DbAsync(db => db.NotificationLogs.AsNoTracking().Where(n => n.Recipient == "nadia@example.com" && n.Template == "order.placed").ToListAsync(Ct));
        var email = Assert.Single(emails);
        Assert.Equal(NotificationStatus.Sent, email.Status);
        Assert.StartsWith("We received your order JV-HO-", email.Subject, StringComparison.Ordinal);

        await H.JsonAsync(HttpMethod.Put, "/api/v1/notifications/channels", HttpStatusCode.OK, new { whatsAppEnabled = true, emailEnabled = false }, admin);
        try
        {
            var second = await PlaceCodAsync(product.Variants[0].Id);
            await DrainAsync();
            var secondLogs = await LogsAsync(second, "order.placed");
            Assert.Contains(secondLogs, l => l.Channel == NotificationChannel.WhatsApp && l.Status == NotificationStatus.Sent);
            Assert.Contains(secondLogs, l => l.Channel == NotificationChannel.Sms);
        }
        finally
        {
            await H.JsonAsync(HttpMethod.Put, "/api/v1/notifications/channels", HttpStatusCode.OK, new { whatsAppEnabled = false, emailEnabled = true }, admin);
        }

        var channels = await H.JsonAsync(HttpMethod.Get, "/api/v1/notifications/channels", HttpStatusCode.OK, bearer: admin);
        Assert.True(channels.GetProperty("smsEnabled").GetBoolean());
        Assert.False(channels.GetProperty("whatsAppEnabled").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await H.SendAsync(HttpMethod.Put, "/api/v1/notifications/channels", new { defaultLanguage = "xx" }, admin)).StatusCode);
    }

    // ---- templates ----------------------------------------------------------------------------------------

    [Fact]
    public async Task Staff_edit_a_template_each_edit_is_a_new_version_and_the_next_message_uses_it()
    {
        factory.Sms.Reset();
        var (admin, product) = await ProductAsync();
        var url = "/api/v1/notifications/templates/order.confirmed/Sms/en";

        var listed = await H.JsonAsync(HttpMethod.Get, "/api/v1/notifications/templates?code=order.confirmed", HttpStatusCode.OK, bearer: admin);
        var builtIn = listed.EnumerateArray().Single(t => Str(t, "channel") == "Sms" && Str(t, "language") == "en");
        Assert.Equal(0, builtIn.GetProperty("version").GetInt32());
        Assert.False(builtIn.GetProperty("isCustom").GetBoolean());
        Assert.Contains("OrderNumber", builtIn.GetProperty("variables").EnumerateArray().Select(v => v.GetString()));

        var v1 = await H.JsonAsync(HttpMethod.Put, url, HttpStatusCode.OK, new { body = "Great news {{Name}}: order {{OrderNumber}} is confirmed!", isActive = true }, admin);
        Assert.Equal(1, v1.GetProperty("version").GetInt32());
        Assert.True(v1.GetProperty("isCustom").GetBoolean());
        var v2 = await H.JsonAsync(HttpMethod.Put, url, HttpStatusCode.OK, new { body = "Order {{OrderNumber}} confirmed (BDT {{Total}}).", isActive = true }, admin);
        Assert.Equal(2, v2.GetProperty("version").GetInt32());

        var history = await H.JsonAsync(HttpMethod.Get, url + "/history", HttpStatusCode.OK, bearer: admin);
        Assert.Equal([2, 1, 0], history.EnumerateArray().Select(h => h.GetProperty("version").GetInt32()).ToArray());
        Assert.StartsWith("Great news", Str(history[1], "body"), StringComparison.Ordinal);
        Assert.StartsWith("Juvo BD: your order", Str(history[2], "body"), StringComparison.Ordinal); // the built-in text is kept as version 0

        try
        {
            var mobile = StoreHelper.NewMobile();
            var placed = await _p.O.PlaceAsync(product.Variants[0].Id, 1, mobile);
            await _p.O.ActAsync(admin, placed.GetProperty("orderId").GetGuid(), "confirm");
            await DrainAsync();
            var message = (await LogsAsync(mobile, "order.confirmed")).Single();
            Assert.Equal(2, message.TemplateVersion);
            Assert.StartsWith("Order JV-HO-", message.Body, StringComparison.Ordinal);
            Assert.Contains("confirmed (BDT 5", message.Body, StringComparison.Ordinal); // 500 for the goods plus the delivery charge

            // Switched off: nothing is sent for this event on this channel and language (no fall back to the old text).
            await H.JsonAsync(HttpMethod.Put, url, HttpStatusCode.OK, new { body = "Order {{OrderNumber}} confirmed.", isActive = false }, admin);
            var quiet = StoreHelper.NewMobile();
            var second = await _p.O.PlaceAsync(product.Variants[0].Id, 1, quiet);
            await _p.O.ActAsync(admin, second.GetProperty("orderId").GetGuid(), "confirm");
            await DrainAsync();
            Assert.Empty(await LogsAsync(quiet, "order.confirmed"));
            Assert.Single(await LogsAsync(quiet, "order.placed")); // other messages are unaffected
        }
        finally
        {
            await H.JsonAsync(HttpMethod.Put, url, HttpStatusCode.OK, new { body = "Juvo BD: your order {{OrderNumber}} is confirmed.", isActive = true }, admin);
        }
    }

    [Fact]
    public async Task Templates_refuse_unknown_placeholders_bad_keys_and_missing_permissions()
    {
        var admin = await H.AdminAsync();
        var url = "/api/v1/notifications/templates/order.packed/Sms/bn";

        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, url, new { body = "অর্ডার {{OrderNumber}} {{Password}}", isActive = true }, admin), "NOTIFY.UNKNOWN_PLACEHOLDER");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, url, new { body = "  ", isActive = true }, admin), "NOTIFY.TEMPLATE_INVALID");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, "/api/v1/notifications/templates/order.packed/Email/en", new { body = "Packed {{OrderNumber}}", isActive = true }, admin), "NOTIFY.TEMPLATE_INVALID"); // an email needs a subject
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, "/api/v1/notifications/templates/order.packed/Sms/fr", new { body = "x", isActive = true }, admin), "NOTIFY.LANGUAGE_INVALID");
        await StoreHelper.ExpectCodeAsync(await H.SendAsync(HttpMethod.Put, "/api/v1/notifications/templates/nothing.here/Sms/en", new { body = "x", isActive = true }, admin), "NOTIFY.TEMPLATE_NOT_FOUND");
        Assert.Equal(HttpStatusCode.BadRequest, (await H.SendAsync(HttpMethod.Put, "/api/v1/notifications/templates/order.packed/InApp/en", new { body = "x", isActive = true }, admin)).StatusCode);

        var viewer = await _p.O.StaffTokenAsync(admin, null, "notifications.template.view", "notifications.log.view");
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, "/api/v1/notifications/templates", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Get, "/api/v1/notifications/logs", null, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Put, url, new { body = "অর্ডার {{OrderNumber}}", isActive = true }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Put, "/api/v1/notifications/channels", new { smsEnabled = false }, viewer)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, "/api/v1/notifications/templates", null)).StatusCode);
        var nobody = await _p.O.StaffTokenAsync(admin, null, "catalog.product.view");
        Assert.Equal(HttpStatusCode.Forbidden, (await H.SendAsync(HttpMethod.Get, "/api/v1/notifications/logs", null, nobody)).StatusCode);
    }

    [Fact]
    public async Task A_template_can_be_previewed_with_sample_values_in_both_languages()
    {
        var admin = await H.AdminAsync();

        var en = await H.JsonAsync(HttpMethod.Post, "/api/v1/notifications/templates/preview", HttpStatusCode.OK, new { code = "payment.received", channel = "Sms", language = "en" }, admin);
        Assert.Contains("BDT 1250", Str(en, "body"), StringComparison.Ordinal);
        Assert.Contains("JV-DHK01-2611-000123", Str(en, "body"), StringComparison.Ordinal);

        var bn = await H.JsonAsync(HttpMethod.Post, "/api/v1/notifications/templates/preview", HttpStatusCode.OK, new { code = "payment.received", channel = "Sms", language = "bn" }, admin);
        Assert.Contains("পেমেন্ট", Str(bn, "body"), StringComparison.Ordinal);

        var custom = await H.JsonAsync(HttpMethod.Post, "/api/v1/notifications/templates/preview", HttpStatusCode.OK,
            new { code = "order.placed", channel = "Sms", language = "en", body = "Hi {{Name}}, order {{OrderNumber}} {{Oops}}", variables = new Dictionary<string, string> { ["Name"] = "Rima" } }, admin);
        Assert.StartsWith("Hi Rima, order JV-DHK01-2611-000123", Str(custom, "body"), StringComparison.Ordinal);
        Assert.Equal(["Oops"], custom.GetProperty("unknownPlaceholders").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Contains("Total", custom.GetProperty("missingVariables").EnumerateArray().Select(x => x.GetString()));
    }

    [Fact]
    public async Task Payment_events_send_payment_messages_in_the_customers_language()
    {
        factory.Sms.Reset();
        var (admin, product) = await ProductAsync();
        var order = await _p.PlaceAsync(product.Variants[0].Id);
        var paid = await _p.PayAsync(order);
        await _p.RefundAsync(admin, paid.GetProperty("paymentId").GetGuid(), 100m);
        await DrainAsync();

        var logs = await LogsAsync(order.Mobile);
        Assert.Contains(logs, l => l.Template == "payment.received" && l.Body.Contains(order.Number, StringComparison.Ordinal) && l.Status == NotificationStatus.Sent);
        Assert.Contains(logs, l => l.Template == "payment.refunded" && l.Body.Contains("BDT 100", StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Template == "order.confirmed"); // paid, so confirmed
        Assert.Contains(logs, l => l.Template == "invoice.ready");
        Assert.All(logs, l => Assert.Equal(NotificationStatus.Sent, l.Status));
    }
}
