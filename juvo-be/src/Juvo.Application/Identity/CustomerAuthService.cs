using System.Security.Cryptography;
using System.Text;
using Juvo.Application.Abstractions;
using Juvo.Domain.Customers;
using Juvo.Domain.Identity;
using Juvo.Shared.Contacts;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juvo.Application.Identity;

/// <summary>The mobile number is always the identity. <c>Channel</c> says where the code goes: <c>sms</c> (default) or <c>email</c> (then <c>Email</c> is required).</summary>
public sealed record RequestOtpRequest(string Mobile, string? Purpose, string? Email = null, string? Channel = null);

/// <summary><c>SentTo</c> is masked (017*****78, a***@example.com). <c>DevCode</c> is only filled in sandbox mode (<c>Auth:Otp:ShowCodeInResponse</c>).</summary>
public sealed record RequestOtpResponse(string Mobile, int ExpiresInSeconds, int ResendAfterSeconds, string Channel, string SentTo, string? DevCode);

/// <summary><c>Email</c> (the address the code was sent to) is saved on a new quick profile after a correct code.</summary>
public sealed record VerifyOtpRequest(string Mobile, string Code, string? DeviceName, string? Email = null);

/// <summary>
/// Customer sign-in by mobile number and one-time password (SRS module E). The mobile number is the identity: verifying it
/// creates the quick profile when there is none. Guests keep ordering without any of this.
/// </summary>
public sealed class CustomerAuthService(
    IApplicationDbContext db,
    SessionIssuer sessions,
    ISmsSender smsSender,
    IEmailSender emailSender,
    TimeProvider timeProvider,
    IOptions<AuthOptions> options)
{
    private readonly OtpOptions _otp = options.Value.Otp;

    public async Task<Result<RequestOtpResponse>> RequestOtpAsync(RequestOtpRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        var mobile = PhoneNumber.Normalize(request.Mobile);
        if (mobile is null)
        {
            return new Error(ErrorCodes.Auth.InvalidMobile, "Enter a valid mobile number, for example 01712345678.", ErrorType.Validation,
                new Dictionary<string, string[]> { ["mobile"] = ["Enter a valid mobile number."] });
        }

        var viaEmail = string.Equals(request.Channel, "email", StringComparison.OrdinalIgnoreCase);
        var email = request.Email?.Trim();
        if (viaEmail && !IsEmail(email))
        {
            return new Error(ErrorCodes.Auth.InvalidEmail, "Enter a valid email address to receive the code.", ErrorType.Validation,
                new Dictionary<string, string[]> { ["email"] = ["Enter a valid email address."] });
        }

        var now = timeProvider.GetUtcNow();

        var latest = await db.OtpCodes.Where(o => o.Mobile == mobile).OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(cancellationToken);
        if (latest is not null && now < latest.CreatedAt.AddSeconds(_otp.ResendCooldownSeconds))
        {
            var wait = (int)Math.Ceiling((latest.CreatedAt.AddSeconds(_otp.ResendCooldownSeconds) - now).TotalSeconds);
            return new Error(ErrorCodes.Auth.OtpCooldown, $"Please wait {wait} seconds before asking for another code.", ErrorType.TooManyRequests);
        }

        var lastHour = await db.OtpCodes.CountAsync(o => o.Mobile == mobile && o.CreatedAt > now.AddHours(-1), cancellationToken);
        if (lastHour >= _otp.MaxRequestsPerHourPerMobile)
        {
            return new Error(ErrorCodes.General.TooManyRequests, "Too many codes requested. Try again later.", ErrorType.TooManyRequests);
        }

        // Only the newest code stays valid.
        var open = await db.OtpCodes.Where(o => o.Mobile == mobile && o.ConsumedAt == null).ToListAsync(cancellationToken);
        foreach (var old in open)
        {
            old.Consume(now);
        }

        var purpose = Enum.TryParse<OtpPurpose>(request.Purpose, ignoreCase: true, out var parsed) ? parsed : OtpPurpose.SignIn;
        var code = GenerateCode(_otp.Length);
        db.OtpCodes.Add(new OtpCode(mobile, purpose, Hash(mobile, code), now.AddMinutes(_otp.ExpiryMinutes), client.IpAddress));
        await db.SaveChangesAsync(cancellationToken);

        var text = $"Your Juvo BD verification code is {code}. It is valid for {_otp.ExpiryMinutes} minutes. Do not share it.";
        if (viaEmail)
        {
            await emailSender.SendAsync(email!, "auth.otp", "Your Juvo BD verification code", text, cancellationToken);
        }
        else
        {
            await smsSender.SendAsync(mobile, "auth.otp", text, cancellationToken);
        }

        return new RequestOtpResponse(mobile, _otp.ExpiryMinutes * 60, _otp.ResendCooldownSeconds, viaEmail ? "email" : "sms",
            viaEmail ? MaskEmail(email!) : MaskMobile(mobile), _otp.ShowCodeInResponse ? code : null);
    }

    public async Task<Result<AuthTokens>> VerifyOtpAsync(VerifyOtpRequest request, ClientInfo client, CancellationToken cancellationToken)
    {
        var mobile = PhoneNumber.Normalize(request.Mobile);
        if (mobile is null || string.IsNullOrWhiteSpace(request.Code))
        {
            return new Error(ErrorCodes.Auth.OtpInvalid, "The code is not correct.", ErrorType.Validation);
        }

        var now = timeProvider.GetUtcNow();
        var otp = await db.OtpCodes.Where(o => o.Mobile == mobile && o.ConsumedAt == null)
            .OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(cancellationToken);

        if (otp is null || now >= otp.ExpiresAt)
        {
            await RecordAsync(null, mobile, false, "otp-expired", client, cancellationToken);
            return new Error(ErrorCodes.Auth.OtpExpired, "The code has expired. Ask for a new one.", ErrorType.Validation);
        }

        if (otp.Attempts >= OtpCode.MaxAttempts)
        {
            return new Error(ErrorCodes.Auth.OtpTooManyAttempts, "Too many wrong codes. Ask for a new one.", ErrorType.TooManyRequests);
        }

        var supplied = Hash(mobile, request.Code.Trim());
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(otp.CodeHash)))
        {
            otp.RegisterFailedAttempt();
            await db.SaveChangesAsync(cancellationToken);
            await RecordAsync(null, mobile, false, "otp-wrong", client, cancellationToken);
            return otp.Attempts >= OtpCode.MaxAttempts
                ? new Error(ErrorCodes.Auth.OtpTooManyAttempts, "Too many wrong codes. Ask for a new one.", ErrorType.TooManyRequests)
                : new Error(ErrorCodes.Auth.OtpInvalid, "The code is not correct.", ErrorType.Validation);
        }

        otp.Consume(now);
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Mobile == mobile, cancellationToken);
        if (customer is null)
        {
            customer = new Customer(mobile, ProfileLevel.Quick);
            db.Customers.Add(customer);
        }

        customer.VerifyMobile(now);
        if (string.IsNullOrEmpty(customer.Email) && IsEmail(request.Email?.Trim()))
        {
            customer.UpdateProfile(customer.FullName, request.Email!.Trim());
        }

        await db.SaveChangesAsync(cancellationToken);

        var tokens = await sessions.StartAsync(SubjectType.Customer, customer.Id, customer.FullName ?? customer.Mobile, 0,
            false, false, false, client with { DeviceName = request.DeviceName ?? client.DeviceName }, cancellationToken);
        await RecordAsync(customer.Id, mobile, true, null, client, cancellationToken);
        return tokens;
    }

    private async Task RecordAsync(Guid? customerId, string mobile, bool ok, string? reason, ClientInfo client, CancellationToken cancellationToken)
    {
        db.SignInHistory.Add(new SignInHistory(SubjectType.Customer, customerId, mobile, ok, reason, client.IpAddress, client.UserAgent,
            timeProvider.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
    }

    private static bool IsEmail(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(value, out var address) && address.Address == value &&
        value.Contains('.', StringComparison.Ordinal);

    private static string MaskMobile(string mobile) => mobile.Length <= 6 ? "***" : string.Concat(mobile.AsSpan(0, mobile.Length - 6), "****", mobile.AsSpan(mobile.Length - 2));

    private static string MaskEmail(string email)
    {
        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 1 ? "***" + email[at..] : email[0] + "***" + email[at..];
    }

    private static string GenerateCode(int length)
    {
        var digits = new char[length];
        for (var i = 0; i < length; i++)
        {
            digits[i] = (char)('0' + RandomNumberGenerator.GetInt32(10));
        }

        return new string(digits);
    }

    private static string Hash(string mobile, string code) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes($"{mobile}:{code}")));
}
