using Juvo.Domain.Identity;

namespace Juvo.Application.Identity;

/// <summary>What goes into an access token.</summary>
public sealed record AccessTokenRequest(
    SubjectType SubjectType,
    Guid SubjectId,
    Guid SessionId,
    string DisplayName,
    int PermissionVersion,
    bool Limited);

public sealed record IssuedAccessToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Creates signed JWT access tokens. Implemented in Infrastructure.</summary>
public interface IAccessTokenService
{
    IssuedAccessToken Issue(AccessTokenRequest request);
}

/// <summary>Where a request comes from, for the session list and sign-in history.</summary>
public sealed record ClientInfo(string? IpAddress, string? UserAgent, string? DeviceName);

/// <summary>Result of a sign-in or refresh: a short-lived access token and a rotating refresh token.</summary>
public sealed record AuthTokens(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    string TokenType,
    string SubjectType,
    bool RequiresPasswordChange,
    bool RequiresTwoFactorSetup);

/// <summary>Cached facts about a user that every request needs: is the account usable and which permission version is current.</summary>
public sealed record UserState(bool Exists, bool IsActive, int PermissionVersion);

/// <summary>Per-user cache with a short lifetime. Services call <see cref="Invalidate"/> whenever access changes.</summary>
public interface IUserStateCache
{
    Task<UserState> GetStaffAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<bool> CustomerExistsAsync(Guid customerId, CancellationToken cancellationToken = default);

    void Invalidate(Guid userId);
}

/// <summary>What a staff member may do: permission codes per branch, plus those that apply everywhere.</summary>
public sealed class UserAccess
{
    public static readonly UserAccess None = new();

    public bool IsGlobalScope { get; init; }

    /// <summary>Holds a Super Admin role (globally). Has every permission, including future ones.</summary>
    public bool HasAllPermissions { get; init; }

    /// <summary>Permissions from roles assigned without a branch. Effective everywhere, for global-scope users only.</summary>
    public IReadOnlySet<string> GlobalPermissions { get; init; } = new HashSet<string>();

    /// <summary>Permission codes by branch id, from roles assigned to that branch.</summary>
    public IReadOnlyDictionary<Guid, IReadOnlySet<string>> BranchPermissions { get; init; } =
        new Dictionary<Guid, IReadOnlySet<string>>();

    /// <summary>The branches the user is assigned to (directly).</summary>
    public IReadOnlyCollection<Guid> AssignedBranchIds => BranchPermissions.Keys.ToList();

    public bool Has(string permission, Guid? branchId)
    {
        if (HasAllPermissions)
        {
            return true;
        }

        if (GlobalPermissions.Contains(permission))
        {
            return true;
        }

        return branchId is { } id && BranchPermissions.TryGetValue(id, out var set) && set.Contains(permission);
    }

    public bool HasAnywhere(string permission) =>
        HasAllPermissions || GlobalPermissions.Contains(permission) || BranchPermissions.Values.Any(s => s.Contains(permission));
}
