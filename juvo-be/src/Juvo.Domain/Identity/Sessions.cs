using Juvo.Domain.Common;

namespace Juvo.Domain.Identity;

public enum SubjectType
{
    Staff = 1,
    Customer = 2,
}

/// <summary>
/// A refresh token. Only the SHA-256 hash is stored. Tokens belong to a family (one sign-in on one device); every refresh
/// rotates to a new token in the same family, and presenting an already-used token revokes the whole family (reuse detection).
/// </summary>
public sealed class RefreshToken : Entity, INotAudited
{
    private RefreshToken()
    {
    }

    public RefreshToken(SubjectType subjectType, Guid subjectId, Guid familyId, string tokenHash, DateTimeOffset expiresAt,
        string? ipAddress, string? userAgent, string? deviceName, DateTimeOffset now)
    {
        SubjectType = subjectType;
        SubjectId = subjectId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        ExpiresAt = expiresAt;
        CreatedByIp = ipAddress;
        UserAgent = userAgent is { Length: > 400 } ? userAgent[..400] : userAgent;
        DeviceName = deviceName is { Length: > 100 } ? deviceName[..100] : deviceName;
        IssuedAt = now;
    }

    public SubjectType SubjectType { get; private set; }

    public Guid SubjectId { get; private set; }

    public Guid FamilyId { get; private set; }

    public string TokenHash { get; private set; } = string.Empty;

    public DateTimeOffset IssuedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    /// <summary>Set when this token was exchanged for a new one. Presenting it again is reuse.</summary>
    public DateTimeOffset? UsedAt { get; private set; }

    public Guid? ReplacedByTokenId { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public string? CreatedByIp { get; private set; }

    public string? UserAgent { get; private set; }

    public string? DeviceName { get; private set; }

    public bool IsRevoked => RevokedAt is not null;

    public bool IsUsed => UsedAt is not null;

    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAt;

    public bool IsActive(DateTimeOffset now) => !IsRevoked && !IsUsed && !IsExpired(now);

    public void MarkUsed(DateTimeOffset now, Guid replacedBy)
    {
        UsedAt = now;
        ReplacedByTokenId = replacedBy;
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null)
        {
            return;
        }

        RevokedAt = now;
        RevokedReason = reason;
    }
}

/// <summary>Every sign-in attempt, successful or not (staff and customers). Append-only.</summary>
public sealed class SignInHistory : Entity, IAppendOnly, INotAudited
{
    private SignInHistory()
    {
    }

    public SignInHistory(SubjectType subjectType, Guid? subjectId, string identifier, bool succeeded, string? failureReason,
        string? ipAddress, string? userAgent, DateTimeOffset occurredAt)
    {
        SubjectType = subjectType;
        SubjectId = subjectId;
        Identifier = identifier.Length > 200 ? identifier[..200] : identifier;
        Succeeded = succeeded;
        FailureReason = failureReason;
        IpAddress = ipAddress;
        UserAgent = userAgent is { Length: > 400 } ? userAgent[..400] : userAgent;
        OccurredAt = occurredAt;
    }

    public SubjectType SubjectType { get; private set; }

    public Guid? SubjectId { get; private set; }

    /// <summary>The email, user name or mobile number that was used.</summary>
    public string Identifier { get; private set; } = string.Empty;

    public bool Succeeded { get; private set; }

    public string? FailureReason { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }
}
