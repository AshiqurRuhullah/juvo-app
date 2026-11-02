using System.Security.Claims;
using Juvo.Application.Abstractions;
using Juvo.Application.Identity;

namespace Juvo.Api.Http;

/// <summary>Reads the caller from the validated principal and request items. Outside a request it is anonymous.</summary>
internal sealed class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public const string BranchItemKey = "Juvo.BranchContext";
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

    public string? UserName => Context?.User.FindFirstValue("name");

    public string? SubjectType => IsAuthenticated ? Context?.User.FindFirstValue("stype") : null;

    public Guid? SessionId => Guid.TryParse(Context?.User.FindFirstValue("sid"), out var id) ? id : null;

    public bool IsLimitedToken => string.Equals(Context?.User.FindFirstValue("scope"), "limited", StringComparison.Ordinal);

    /// <summary>The active branch chosen with X-Branch-Id and validated by the branch middleware.</summary>
    public Guid? BranchId => (Context?.Items[BranchItemKey] as BranchContextData)?.ActiveBranchId;

    public string? IpAddress => Context?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent => Context?.Request.Headers.UserAgent.ToString() is { Length: > 0 } agent ? agent : null;

    public string? CorrelationId => Context?.Items[CorrelationItemKey] as string;
}

/// <summary>The branch scope of the current request (set by <see cref="BranchContextMiddleware"/>); unscoped when none was set.</summary>
internal sealed class HttpBranchContext(IHttpContextAccessor accessor) : IBranchContext
{
    private BranchContextData Data =>
        accessor.HttpContext?.Items[HttpCurrentUser.BranchItemKey] as BranchContextData ?? BranchContextData.Unscoped;

    public Guid? ActiveBranchId => Data.ActiveBranchId;

    public bool IsAllBranches => Data.IsAllBranches;

    public bool EnforceScope => Data.EnforceScope;

    public IReadOnlyCollection<Guid> AllowedBranchIds => Data.AllowedBranchIds;
}

/// <summary>
/// Validates <c>X-Branch-Id</c> against the signed-in staff member's assignments on every request (the header is never trusted
/// by itself) and stores the resulting branch scope for the data layer. Refused attempts are audit-logged.
/// </summary>
internal sealed class BranchContextMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-Branch-Id";

    public async Task InvokeAsync(HttpContext context, ICurrentUser currentUser, BranchContextResolver resolver, IAccessDeniedAuditor auditor)
    {
        if (currentUser is { IsAuthenticated: true, SubjectType: "staff", IsLimitedToken: false, UserId: { } userId })
        {
            var header = context.Request.Headers[HeaderName].ToString();
            var resolved = await resolver.ResolveAsync(userId, header, context.RequestAborted);
            if (resolved.IsFailure)
            {
                if (resolved.Error!.Code == Shared.Results.ErrorCodes.Auth.BranchForbidden)
                {
                    await auditor.RecordAsync("Branch", $"X-Branch-Id '{header}' refused", context.RequestAborted);
                }

                await ResultHttpExtensions.ToProblem(resolved.Error!).ExecuteAsync(context);
                return;
            }

            context.Items[HttpCurrentUser.BranchItemKey] = resolved.Value;
        }

        await next(context);
    }
}
