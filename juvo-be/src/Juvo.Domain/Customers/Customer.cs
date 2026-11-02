using Juvo.Domain.Common;

namespace Juvo.Domain.Customers;

public enum ProfileLevel
{
    /// <summary>Created from checkout details only; never signed in.</summary>
    Guest = 1,

    /// <summary>Mobile number verified by OTP (and optional email).</summary>
    Quick = 2,

    /// <summary>Quick profile plus name and optional details.</summary>
    Full = 3,
}

/// <summary>
/// One person who buys. The normalized mobile number (+880...) is the unique key (BR-006).
/// This is the thin version needed for sign-in; addresses, wishlist and consent come in Step 7.
/// </summary>
public sealed class Customer : SoftDeletableEntity
{
    private Customer()
    {
    }

    public Customer(string mobile, ProfileLevel level)
    {
        Mobile = mobile;
        ProfileLevel = level;
    }

    public string Mobile { get; private set; } = string.Empty;

    public string? Email { get; private set; }

    public string? FullName { get; private set; }

    public ProfileLevel ProfileLevel { get; private set; }

    public bool MobileVerified { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public void VerifyMobile(DateTimeOffset now)
    {
        MobileVerified = true;
        if (ProfileLevel == ProfileLevel.Guest)
        {
            ProfileLevel = ProfileLevel.Quick;
        }

        LastLoginAt = now;
    }

    public void UpdateProfile(string? fullName, string? email)
    {
        FullName = fullName?.Trim();
        Email = email?.Trim();
        if (!string.IsNullOrWhiteSpace(FullName) && ProfileLevel == ProfileLevel.Quick)
        {
            ProfileLevel = ProfileLevel.Full;
        }
    }
}

public enum OtpPurpose
{
    SignIn = 1,
    ConfirmOrder = 2,
}

/// <summary>A one-time password sent by SMS (SRS FR-E05): 6 digits, 5 minutes, limited attempts. Only a hash is stored.</summary>
public sealed class OtpCode : Entity, INotAudited
{
    public const int MaxAttempts = 5;

    private OtpCode()
    {
    }

    public OtpCode(string mobile, OtpPurpose purpose, string codeHash, DateTimeOffset expiresAt, string? requestedIp)
    {
        Mobile = mobile;
        Purpose = purpose;
        CodeHash = codeHash;
        ExpiresAt = expiresAt;
        RequestedIp = requestedIp;
    }

    public string Mobile { get; private set; } = string.Empty;

    public OtpPurpose Purpose { get; private set; }

    public string CodeHash { get; private set; } = string.Empty;

    public DateTimeOffset ExpiresAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    public string? RequestedIp { get; private set; }

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt && Attempts < MaxAttempts;

    public void RegisterFailedAttempt() => Attempts++;

    public void Consume(DateTimeOffset now) => ConsumedAt = now;
}
