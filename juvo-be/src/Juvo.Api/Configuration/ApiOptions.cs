using System.ComponentModel.DataAnnotations;

namespace Juvo.Api.Configuration;

/// <summary>Front-end origins allowed to call the API with credentials. Section: <c>Cors</c>.</summary>
public sealed class CorsSettings : IValidatableObject
{
    public const string SectionName = "Cors";
    public const string PolicyName = "Frontend";

    [Required]
    [MinLength(1, ErrorMessage = "At least one allowed origin is required.")]
    public string[] AllowedOrigins { get; set; } = ["http://localhost:3000"];

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var origin in AllowedOrigins)
        {
            // Credentials are allowed, so wildcards are never acceptable.
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https")
                || origin.Contains('*', StringComparison.Ordinal)
                || uri.AbsolutePath != "/")
            {
                yield return new ValidationResult(
                    $"Cors:AllowedOrigins contains an invalid origin '{origin}'. Use scheme and host only, for example https://juvobd.com.",
                    [nameof(AllowedOrigins)]);
            }
        }
    }
}

/// <summary>Per-client request limits per minute. Section: <c>RateLimiting</c>.</summary>
public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimiting";
    public const string AuthPolicy = "auth";
    public const string OtpPolicy = "otp";
    public const string CheckoutPolicy = "checkout";
    public const string StorefrontPolicy = "storefront";

    public bool Enabled { get; set; } = true;

    [Range(1, 100000)]
    public int GlobalPerMinute { get; set; } = 300;

    [Range(1, 1000)]
    public int AuthPerMinute { get; set; } = 10;

    [Range(1, 1000)]
    public int OtpPerMinute { get; set; } = 5;

    [Range(1, 1000)]
    public int CheckoutPerMinute { get; set; } = 20;

    /// <summary>Anonymous shop traffic that changes or searches data (cart, quote, suggestions): per client address.</summary>
    [Range(1, 10000)]
    public int StorefrontPerMinute { get; set; } = 120;
}

/// <summary>Interactive API documentation. Section: <c>OpenApi</c>.</summary>
public sealed class OpenApiSettings
{
    public const string SectionName = "OpenApi";

    /// <summary>Serve /openapi/v1.json and the /scalar UI. On in Development; leave off in Production unless needed.</summary>
    public bool Enabled { get; set; }
}
