using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Juvo.Api.IntegrationTests.Support;
using Juvo.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Juvo.Api.IntegrationTests.Ums;

public sealed class SandboxOtpFactory : ApiFactory
{
    protected override void ConfigureSettings()
    {
        Setting("Auth:Otp:ShowCodeInResponse", "true");
        Setting("Auth:Otp:ResendCooldownSeconds", "0");
    }
}

/// <summary>The one-time code can go by SMS or by email, and in sandbox mode the response shows it (no real provider is connected yet).</summary>
public partial class CustomerOtpChannelTests(SandboxOtpFactory factory) : IClassFixture<SandboxOtpFactory>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly TestApi _api = new(factory);

    [GeneratedRegex(@"\b(\d{6})\b")]
    private static partial Regex SixDigits();

    private static string NewMobile() => "017" + Random.Shared.Next(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private async Task<JsonElement> RequestAsync(object body, HttpStatusCode expected = HttpStatusCode.Accepted)
    {
        var response = await _api.PostAsync("/api/v1/auth/otp/request", body);
        return await TestApi.ExpectAsync(response, expected);
    }

    [Fact]
    public async Task In_sandbox_mode_the_response_carries_the_code_and_a_masked_destination_and_the_sms_has_the_same_code()
    {
        var mobile = NewMobile();

        var json = await RequestAsync(new { mobile });

        Assert.Equal("sms", json.GetProperty("channel").GetString());
        var sentTo = json.GetProperty("sentTo").GetString()!;
        Assert.Contains("****", sentTo, StringComparison.Ordinal);
        Assert.EndsWith(mobile[^2..], sentTo, StringComparison.Ordinal);
        var devCode = json.GetProperty("devCode").GetString()!;
        Assert.Matches(@"^\d{6}$", devCode);
        var sms = await _api.LastMessageToAsync("+88" + mobile);
        Assert.Equal(devCode, SixDigits().Match(sms.Body).Groups[1].Value);

        // The code works.
        var verify = await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code = devCode });
        await TestApi.ExpectAsync(verify, HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_code_can_be_sent_by_email_instead_and_the_address_is_kept_on_the_new_profile()
    {
        var mobile = NewMobile();
        var email = $"buyer-{Guid.NewGuid():N}@example.com"[..24] + "@example.com";

        var json = await RequestAsync(new { mobile, channel = "email", email });

        Assert.Equal("email", json.GetProperty("channel").GetString());
        Assert.Equal(email[0] + "***" + email[email.IndexOf('@', StringComparison.Ordinal)..], json.GetProperty("sentTo").GetString());
        var mail = await _api.LastMessageToAsync(email);
        var code = SixDigits().Match(mail.Body).Groups[1].Value;
        Assert.Equal(json.GetProperty("devCode").GetString(), code);

        var verify = await _api.PostAsync("/api/v1/auth/otp/verify", new { mobile, code, email });
        await TestApi.ExpectAsync(verify, HttpStatusCode.OK);

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        var customer = await db.Customers.SingleAsync(c => c.Mobile == "+88" + mobile, Ct);
        Assert.Equal(email, customer.Email);
        Assert.True(customer.MobileVerified);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-an-email")]
    [InlineData("a@b")]
    public async Task Asking_for_an_email_code_needs_a_valid_address(string? email)
    {
        var response = await _api.PostAsync("/api/v1/auth/otp/request", new { mobile = NewMobile(), channel = "email", email });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("AUTH.INVALID_EMAIL", await TestApi.ErrorCodeAsync(response));
    }

    [Fact]
    public async Task A_code_sent_by_email_does_not_send_an_sms()
    {
        var mobile = NewMobile();
        var email = $"x{Guid.NewGuid():N}"[..12] + "@example.com";

        await RequestAsync(new { mobile, channel = "email", email });

        await using var scope = factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JuvoDbContext>();
        Assert.False(await db.NotificationLogs.AnyAsync(n => n.Recipient == "+88" + mobile, Ct));
        Assert.True(await db.NotificationLogs.AnyAsync(n => n.Recipient == email, Ct));
    }
}
