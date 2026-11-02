using System.Security.Cryptography;
using System.Text;
using Juvo.Application.Identity;
using Juvo.Domain.Identity;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Juvo.Infrastructure.Identity;

/// <summary>
/// The HMAC key that signs access tokens. It comes from <c>Auth:SigningKey</c> (user-secrets or environment). In Development
/// only, an empty key is replaced by a random one for this run, so nothing secret is ever committed; tokens then stop working
/// after a restart. Everywhere else a missing or short key stops the application at start-up.
/// </summary>
public sealed class SigningKeyProvider
{
    public const int MinimumKeyLength = 32;

    public SigningKeyProvider(IOptions<AuthOptions> options, IHostEnvironment environment, ILogger<SigningKeyProvider> logger)
    {
        var configured = options.Value.SigningKey;
        byte[] bytes;
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    $"Auth:SigningKey is required (at least {MinimumKeyLength} characters). Set it with user-secrets or the Auth__SigningKey environment variable.");
            }

            bytes = RandomNumberGenerator.GetBytes(64);
            logger.LogWarning("Auth:SigningKey is not set. Using a temporary key for this run; sign-ins will not survive a restart.");
        }
        else
        {
            if (configured.Length < MinimumKeyLength)
            {
                throw new InvalidOperationException($"Auth:SigningKey must be at least {MinimumKeyLength} characters.");
            }

            bytes = Encoding.UTF8.GetBytes(configured);
        }

        Key = new SymmetricSecurityKey(bytes) { KeyId = "juvo-hs256" };
    }

    public SymmetricSecurityKey Key { get; }
}

/// <summary>
/// Creates short-lived JWT access tokens. The token holds who the caller is and which permission version it was issued with,
/// never the permissions themselves: those are looked up per request, so role changes take effect quickly.
/// </summary>
internal sealed class AccessTokenService(SigningKeyProvider keys, IOptions<AuthOptions> options, TimeProvider timeProvider) : IAccessTokenService
{
    public static class Claims
    {
        public const string Subject = "sub";
        public const string TokenId = "jti";
        public const string Name = "name";
        public const string SubjectType = "stype";
        public const string Session = "sid";
        public const string PermissionVersion = "pv";
        public const string Scope = "scope";
        public const string LimitedScope = "limited";
    }

    private readonly JsonWebTokenHandler _handler = new();

    public IssuedAccessToken Issue(AccessTokenRequest request)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(settings.AccessTokenMinutes);

        var claims = new Dictionary<string, object>
        {
            [Claims.Subject] = request.SubjectId.ToString(),
            [Claims.TokenId] = Guid.NewGuid().ToString("N"),
            [Claims.Name] = request.DisplayName,
            [Claims.SubjectType] = request.SubjectType == SubjectType.Customer ? "customer" : "staff",
            [Claims.Session] = request.SessionId.ToString(),
            [Claims.PermissionVersion] = request.PermissionVersion,
        };
        if (request.Limited)
        {
            claims[Claims.Scope] = Claims.LimitedScope;
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = settings.Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.HmacSha256),
        };

        return new IssuedAccessToken(_handler.CreateToken(descriptor), expires);
    }
}
