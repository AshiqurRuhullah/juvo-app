using Juvo.Application.Identity;
using Juvo.Application.Realtime;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Juvo.Infrastructure.Identity;

/// <summary>
/// Creates the hub tokens (module J). Same signing key and issuer as access tokens, but the audience is <c>&lt;audience&gt;:realtime</c>, so a hub token is rejected by every ordinary
/// endpoint and an access token is rejected by the hub. Short lived (<c>Auth:RealtimeTokenMinutes</c>, default 5) because the browser puts it in the connection address.
/// </summary>
public sealed class RealtimeTokenService(SigningKeyProvider keys, IOptions<AuthOptions> options, TimeProvider timeProvider) : IRealtimeTokenService
{
    public const string AudienceSuffix = ":realtime";

    public static string AudienceOf(AuthOptions o) => o.Audience + AudienceSuffix;

    private readonly JsonWebTokenHandler _handler = new();

    public IssuedRealtimeToken Issue(RealtimeTokenRequest request)
    {
        var settings = options.Value;
        var now = timeProvider.GetUtcNow();
        var expires = now.AddMinutes(settings.RealtimeTokenMinutes);
        var claims = new Dictionary<string, object>
        {
            ["sub"] = request.SubjectId.ToString(),
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["name"] = request.DisplayName,
            ["stype"] = request.SubjectType,
            ["pv"] = request.PermissionVersion,
            ["scope"] = "realtime",
        };
        if (request.TrackedOrderId is { } order)
        {
            claims["order"] = order.ToString();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = settings.Issuer,
            Audience = AudienceOf(settings),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            Claims = claims,
            SigningCredentials = new SigningCredentials(keys.Key, SecurityAlgorithms.HmacSha256),
        };
        return new IssuedRealtimeToken(_handler.CreateToken(descriptor), expires);
    }
}
