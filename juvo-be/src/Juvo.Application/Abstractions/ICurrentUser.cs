namespace Juvo.Application.Abstractions;

/// <summary>The caller of the current request. Implemented by the API layer from the validated token and headers.</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    string? UserName { get; }

    /// <summary>The active branch chosen with the X-Branch-Id header, already validated against the user's assignments.</summary>
    Guid? BranchId { get; }

    string? IpAddress { get; }

    string? UserAgent { get; }

    /// <summary>The refresh-token family (session) of the access token in use, when there is one.</summary>
    Guid? SessionId { get; }

    /// <summary>True for a limited token (password change or two-factor setup still pending).</summary>
    bool IsLimitedToken { get; }

    /// <summary>The kind of caller: <c>staff</c>, <c>customer</c> or null when anonymous.</summary>
    string? SubjectType { get; }

    string? CorrelationId { get; }
}
