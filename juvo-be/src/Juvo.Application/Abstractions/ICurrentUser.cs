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

    string? CorrelationId { get; }
}
