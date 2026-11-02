using System.Net;
using System.Text.RegularExpressions;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Domain.Customers;
using Juvo.Domain.Identity;
using Juvo.Domain.Notifications;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Juvo.Api.IntegrationTests.Ums;

public sealed class OtpFactory : ApiFactory
{
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureSettings()
    {
        Setting("Auth:Otp:ResendCooldownSeconds", "60");
        Setting("Auth:Otp:MaxRequestsPerHourPerMobile", "5");
    }

    protected override void ConfigureTestServices(IServiceCollection services) => services.AddSingleton<TimeProvider>(Time);
}

public partial class CustomerOtpTests(OtpFactory factory) : IClassFixture<OtpFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestApi _api = new(factory);

    [GeneratedRegex(@"\b(\d{6})\b")]
    private static partial Regex SixDigits();

    private static string NewMobile() => "017" + Random.Shared.Next(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<string> RequestCodeAsync(string mobile)
    {
        var response = await _api.PostAsync("/api/v1/auth/otp/request", new { mobile });
        await TestApi.ExpectAsync(response, HttpStatusCode.Accepted);
        var normalized = Juvo.Shared.Contacts.PhoneNumber.Normalize(mobile)!;
        var sms = await _api.LastMessageToAsync(normalized);
        return SixDigits().Match(sms.Body).Groups[1].Value;
    }

    [Fact]
    public async Task Requesting_a_code_sends_an_sms_to_the_normalized_number_and_never_returns_the_code()
    {
        var mobile = NewMobile();

        var response = await _api.PostAsync("/api/v1/auth/otp/request", new { mobile });
        var raw = await response.Content.ReadAsStringAsync(Ct);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var json = System.Text.Json.JsonDocument.Parse(raw).RootElement;

        Assert.Equal("+88" + mobile, json.GetProperty("mobile").GetString());
        Assert.Equal(300, json.GetProperty("expiresInSeconds").GetInt32());

        var code = SixDigits().Match((await _api.LastMessageToAsync("+88" + mobile)).Body).Groups[1].Value;
        Assert.DoesNotContain(code, raw, StringComparison.Ordinal);

        var sms = await _api.LastMessageToAsync("+88" + mobile);
        Assert.Equal(NotificationChannel.Sms, sms.Channel);
        Assert.Equal("auth.otp", sms.Template);
        Assert.Matches(@"\d{6}", sms.Body);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("not a number")]
    [InlineData("01212345678")]
    public async Task Invalid_mobile_numbers_are_rejected(string mobile)
    {
        var response = await _api.PostAsync("/api/v1/auth/otp/request", new { mobile });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("AUTH.INVALID_MOBILE", await TestApi.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task Correct_code_signs_the_customer_in_and_creates_a_quick_profile()
    {
        var mobile = NewMobile();
        var code = await RequestCodeAsync(mobile);

        var verify = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code, deviceName = "Pixel" }), HttpStatusCode.OK);

        Assert.Equal("customer", verify.GetProperty("subjectType").GetString());
        var access = verify.GetProperty("accessToken").GetString()!;
        var me = await TestApi.ExpectAsync(await _api.GetAsync("/api/v1/auth/me", access), HttpStatusCode.OK);
        Assert.Equal("customer", me.GetProperty("subjectType").GetString());
        var customer = me.GetProperty("customer");
        Assert.Equal("+88" + mobile, customer.GetProperty("mobile").GetString());
        Assert.Equal("Quick", customer.GetProperty("profileLevel").GetString());
        Assert.True(customer.GetProperty("mobileVerified").GetBoolean());
    }

    [Fact]
    public async Task Signing_in_again_with_the_same_number_reuses_the_same_customer()
    {
        var mobile = NewMobile();
        var first = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code = await RequestCodeAsync(mobile) }), HttpStatusCode.OK);
        factory.Time.Advance(TimeSpan.FromSeconds(61)); // resend cooldown
        var second = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile = "+88" + mobile, code = await RequestCodeAsync("+88" + mobile) }), HttpStatusCode.OK);

        Assert.NotNull(first.GetProperty("accessToken").GetString());
        Assert.NotNull(second.GetProperty("accessToken").GetString());
        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        Assert.Equal(1, await db.Customers.CountAsync(c => c.Mobile == "+88" + mobile, Ct));
    }

    [Fact]
    public async Task Wrong_code_is_refused_and_five_wrong_codes_lock_that_code()
    {
        var mobile = NewMobile();
        var code = await RequestCodeAsync(mobile);
        var wrong = code == "000000" ? "111111" : "000000";

        for (var i = 0; i < 4; i++)
        {
            var bad = await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code = wrong });
            Assert.Equal("AUTH.OTP_INVALID", await TestApi.ErrorCodeAsync(bad));
        }

        var fifth = await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code = wrong });
        Assert.Equal("AUTH.OTP_TOO_MANY_ATTEMPTS", await TestApi.ErrorCodeAsync(fifth));

        // Even the right code no longer works: ask for a new one.
        var right = await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code });
        Assert.Equal(HttpStatusCode.TooManyRequests, right.StatusCode);
    }

    [Fact]
    public async Task A_used_code_cannot_be_used_twice_and_a_new_request_replaces_the_old_code()
    {
        var mobile = NewMobile();
        var code = await RequestCodeAsync(mobile);
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code }), HttpStatusCode.OK);

        var again = await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code });

        Assert.Equal(HttpStatusCode.BadRequest, again.StatusCode);
        Assert.Equal("AUTH.OTP_EXPIRED", await TestApi.ErrorCodeAsync(again));
    }

    [Fact]
    public async Task Code_expires_after_five_minutes()
    {
        var mobile = NewMobile();
        var code = await RequestCodeAsync(mobile);

        factory.Time.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var late = await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code });
        Assert.Equal(HttpStatusCode.BadRequest, late.StatusCode);
        Assert.Equal("AUTH.OTP_EXPIRED", await TestApi.ErrorCodeAsync(late));
    }

    [Fact]
    public async Task Asking_again_straight_away_hits_the_resend_cooldown()
    {
        var mobile = NewMobile();
        await RequestCodeAsync(mobile);

        var second = await _api.PostAsync("/api/v1/auth/otp/request", new { mobile });

        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("AUTH.OTP_COOLDOWN", await TestApi.ErrorCodeAsync(second));
    }

    [Fact]
    public async Task Hourly_limit_per_number_is_enforced()
    {
        var mobile = NewMobile();
        for (var i = 0; i < 5; i++)
        {
            await RequestCodeAsync(mobile);
            factory.Time.Advance(TimeSpan.FromSeconds(61));
        }

        var sixth = await _api.PostAsync("/api/v1/auth/otp/request", new { mobile });

        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.StatusCode);
        Assert.Equal("GENERAL.TOO_MANY_REQUESTS", await TestApi.ErrorCodeAsync(sixth));
    }

    [Fact]
    public async Task Otp_codes_are_stored_hashed_never_in_clear()
    {
        var mobile = NewMobile();
        var code = await RequestCodeAsync(mobile);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        var stored = await db.OtpCodes.Where(o => o.Mobile == "+88" + mobile).Select(o => o.CodeHash).ToListAsync(Ct);

        Assert.NotEmpty(stored);
        Assert.DoesNotContain(code, stored);
    }

    [Fact]
    public async Task A_customer_token_cannot_use_staff_endpoints_and_can_refresh_and_sign_out()
    {
        var mobile = NewMobile();
        var verify = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code = await RequestCodeAsync(mobile) }), HttpStatusCode.OK);
        var access = verify.GetProperty("accessToken").GetString()!;
        var refresh = verify.GetProperty("refreshToken").GetString()!;

        Assert.Equal(HttpStatusCode.Forbidden, (await _api.GetAsync("/api/v1/ums/users", access)).StatusCode);

        var refreshed = await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/refresh", new { refreshToken = refresh }), HttpStatusCode.OK);
        Assert.Equal("customer", refreshed.GetProperty("subjectType").GetString());

        Assert.Equal(HttpStatusCode.NoContent, (await _api.PostAsync("/api/v1/auth/logout", new { refreshToken = refreshed.GetProperty("refreshToken").GetString() })).StatusCode);
    }

    [Fact]
    public async Task Customer_sign_ins_are_recorded_in_the_history()
    {
        var mobile = NewMobile();
        await TestApi.ExpectAsync(await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code = await RequestCodeAsync(mobile) }), HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        Assert.True(await db.SignInHistory.AnyAsync(s => s.SubjectType == SubjectType.Customer && s.Identifier == "+88" + mobile && s.Succeeded, Ct));
    }
}
