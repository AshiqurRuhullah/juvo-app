using System.Security.Claims;
using Juvo.Application.Abstractions;

namespace Juvo.Api.Http;

/// <summary>Reads the caller from the validated principal and request items. Outside a request it is anonymous.</summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string BranchItemKey = "Juvo.BranchId";
    public const string CorrelationItemKey = "Juvo.CorrelationId";

    private HttpContext? Context => accessor.HttpContext;

    public bool IsAuthenticated => Context?.User.Identity?.IsAuthenticated == true;

    public Guid? UserId
    {
        get
        {
            var raw = Context?.User.FindFirstValue("sub") ?? Context?.User.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id) ? id : null;
        }
    }

    public string? UserName => Context?.User.Identity?.Name;

    /// <summary>Set by the branch middleware (Step 3) after validating X-Branch-Id against the user's assignments.</summary>
    public Guid? BranchId => Context?.Items[BranchItemKey] as Guid?;

    public string? IpAddress => Context?.Connection.RemoteIpAddress?.ToString();

    public string? CorrelationId => Context?.Items[CorrelationItemKey] as string;
}
