using System.Security.Cryptography;
using System.Text;
using Juvo.Application.Abstractions;
using Juvo.Domain.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juvo.Application.Identity;

/// <summary>Random refresh token values. Only the SHA-256 hash is ever stored.</summary>
public static class RefreshTokenValue
{
    public static string Generate() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Starts sessions (sign-in) and rotates refresh tokens, issuing the access token each time.</summary>
public sealed class SessionIssuer(
    IApplicationDbContext db, IAccessTokenService accessTokens, TimeProvider timeProvider, IOptions<AuthOptions> options)
{
    private readonly AuthOptions _options = options.Value;

    /// <summary>New session = new token family.</summary>
    public async Task<AuthTokens> StartAsync(SubjectType subject, Guid subjectId, string displayName, int permissionVersion,
        bool limited, bool requiresPasswordChange, bool requiresTwoFactorSetup, ClientInfo client, CancellationToken cancellationToken)
    {
        var familyId = Guid.CreateVersion7();
        var tokens = Create(subject, subjectId, familyId, displayName, permissionVersion, limited,
            requiresPasswordChange, requiresTwoFactorSetup, client, out var refresh);
        db.RefreshTokens.Add(refresh);
        await db.SaveChangesAsync(cancellationToken);
        return tokens;
    }

    /// <summary>Marks <paramref name="old"/> as used and issues the next token of the same family. The caller saves.</summary>
    public AuthTokens Rotate(RefreshToken old, string displayName, int permissionVersion, bool limited,
        bool requiresPasswordChange, bool requiresTwoFactorSetup, ClientInfo client)
    {
        var tokens = Create(old.SubjectType, old.SubjectId, old.FamilyId, displayName, permissionVersion, limited,
            requiresPasswordChange, requiresTwoFactorSetup, client, out var next);
        old.MarkUsed(timeProvider.GetUtcNow(), next.Id);
        db.RefreshTokens.Add(next);
        return tokens;
    }

    /// <summary>Revokes every active token of a session. The caller saves.</summary>
    public async Task RevokeFamilyAsync(Guid familyId, string reason, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var tokens = await db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoke(now, reason);
        }
    }

    /// <summary>Revokes every session of a user (password change, deactivation, force logout). The caller saves.</summary>
    public async Task RevokeAllAsync(SubjectType subject, Guid subjectId, string reason, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var tokens = await db.RefreshTokens
            .Where(t => t.SubjectType == subject && t.SubjectId == subjectId && t.RevokedAt == null)
            .ToListAsync(cancellationToken);
        foreach (var token in tokens)
        {
            token.Revoke(now, reason);
        }
    }

    private AuthTokens Create(SubjectType subject, Guid subjectId, Guid familyId, string displayName, int permissionVersion,
        bool limited, bool requiresPasswordChange, bool requiresTwoFactorSetup, ClientInfo client, out RefreshToken refresh)
    {
        var now = timeProvider.GetUtcNow();
        var lifetime = TimeSpan.FromDays(subject == SubjectType.Customer ? _options.CustomerRefreshTokenDays : _options.StaffRefreshTokenDays);
        var value = RefreshTokenValue.Generate();
        refresh = new RefreshToken(subject, subjectId, familyId, RefreshTokenValue.Hash(value), now.Add(lifetime),
            client.IpAddress, client.UserAgent, client.DeviceName, now);

        var access = accessTokens.Issue(new AccessTokenRequest(subject, subjectId, familyId, displayName, permissionVersion, limited));
        return new AuthTokens(access.Token, access.ExpiresAt, value, refresh.ExpiresAt, "Bearer",
            subject == SubjectType.Customer ? "customer" : "staff", requiresPasswordChange, requiresTwoFactorSetup);
    }
}
