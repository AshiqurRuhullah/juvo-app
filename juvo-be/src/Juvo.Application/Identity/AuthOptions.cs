using System.ComponentModel.DataAnnotations;

namespace Juvo.Application.Identity;

/// <summary>Authentication settings. Section: <c>Auth</c>.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    [Required]
    public string Issuer { get; set; } = "juvo-api";

    [Required]
    public string Audience { get; set; } = "juvo-clients";

    /// <summary>
    /// HMAC signing key, at least 32 characters. Never commit it: use user-secrets or an environment variable.
    /// In Development an ephemeral key is generated when empty (tokens stop working after a restart).
    /// </summary>
    public string SigningKey { get; set; } = string.Empty;

    [Range(1, 120)]
    public int AccessTokenMinutes { get; set; } = 15;

    [Range(1, 90)]
    public int StaffRefreshTokenDays { get; set; } = 14;

    [Range(1, 365)]
    public int CustomerRefreshTokenDays { get; set; } = 30;

    /// <summary>Two-step sign-in is mandatory for Super Admin (SRS FR-O01). Turn off only for local development.</summary>
    public bool RequireTwoFactorForSuperAdmin { get; set; } = true;

    /// <summary>Base URL of the front end, used in password-reset links.</summary>
    [Required]
    public string FrontendBaseUrl { get; set; } = "http://localhost:3000";

    [Range(6, 128)]
    public int PasswordMinLength { get; set; } = 10;

    [Range(1, 20)]
    public int MaxFailedAccessAttempts { get; set; } = 5;

    [Range(1, 1440)]
    public int LockoutMinutes { get; set; } = 15;

    [Range(1, 1440)]
    public int PasswordResetTokenMinutes { get; set; } = 60;

    /// <summary>How long a real-time hub token lives (module J). It only has to survive until the browser connects; the browser asks again whenever it reconnects.</summary>
    [Range(1, 60)]
    public int RealtimeTokenMinutes { get; set; } = 5;

    public OtpOptions Otp { get; set; } = new();

    public SeedOptions Seed { get; set; } = new();
}

/// <summary>Customer one-time passwords (SRS FR-E05).</summary>
public sealed class OtpOptions
{
    [Range(4, 8)]
    public int Length { get; set; } = 6;

    [Range(1, 30)]
    public int ExpiryMinutes { get; set; } = 5;

    [Range(0, 600)]
    public int ResendCooldownSeconds { get; set; } = 60;

    [Range(1, 100)]
    public int MaxRequestsPerHourPerMobile { get; set; } = 5;

    /// <summary>
    /// Sandbox only: put the code in the response of the request so a tester can sign in without a real SMS or email provider
    /// (none is connected before Step 14). Must stay false in Production.
    /// </summary>
    public bool ShowCodeInResponse { get; set; }
}

/// <summary>First Super Admin account created at start-up. Leave the password empty in Production to skip it.</summary>
public sealed class SeedOptions
{
    public string Email { get; set; } = "admin@juvobd.local";

    public string UserName { get; set; } = "admin";

    public string FullName { get; set; } = "Juvo Super Admin";

    /// <summary>Empty means no account is created. Development sets a known value; the user must change it at first sign-in.</summary>
    public string Password { get; set; } = string.Empty;
}
