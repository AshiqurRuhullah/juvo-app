using Juvo.Application.Abstractions;
using Juvo.Application.Orders;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Realtime;

/// <summary>What goes into a hub token: who connects (and, for a guest, the one order they may follow).</summary>
public sealed record RealtimeTokenRequest(string SubjectType, Guid SubjectId, string DisplayName, int PermissionVersion, Guid? TrackedOrderId);

public sealed record IssuedRealtimeToken(string Token, DateTimeOffset ExpiresAt);

/// <summary>Creates the short-lived hub tokens. Implemented in Infrastructure with the same signing key as access tokens but another audience, so a hub token opens nothing but the hub.</summary>
public interface IRealtimeTokenService
{
    IssuedRealtimeToken Issue(RealtimeTokenRequest request);
}

/// <summary>The answer of the token endpoints: the token, when it ends, and where to connect (a path on the API's own origin).</summary>
public sealed record RealtimeTokenDto(string Token, DateTimeOffset ExpiresAt, string HubPath, string Role, Guid? OrderId);

/// <summary>
/// Hands out hub tokens (module J). The browser cannot open a WebSocket with the httpOnly cookies the front end keeps its tokens in, so its server (the BFF) asks here, with the real
/// access token, for a token that lives a few minutes and is good only for the hub. Staff and customers get one for themselves; a guest gets one for exactly the order they proved
/// with the tracking token (or number plus mobile).
/// </summary>
public sealed class RealtimeTokens(ICurrentUser currentUser, IRealtimeTokenService tokens, CheckoutService checkout, IApplicationDbContext db)
{
    public const string HubPath = "/hubs/orders";

    public async Task<Result<RealtimeTokenDto>> ForSignedInAsync(CancellationToken cancellationToken)
    {
        if (currentUser is not { IsAuthenticated: true, UserId: { } id, IsLimitedToken: false } || currentUser.SubjectType is not ("staff" or "customer"))
        {
            return Error.Unauthorized();
        }

        var pv = 0;
        if (currentUser.SubjectType == "staff")
        {
            pv = await db.Users.AsNoTracking().Where(u => u.Id == id).Select(u => u.PermissionVersion).FirstOrDefaultAsync(cancellationToken);
        }

        var issued = tokens.Issue(new RealtimeTokenRequest(currentUser.SubjectType, id, currentUser.UserName ?? currentUser.SubjectType, pv, null));
        return new RealtimeTokenDto(issued.Token, issued.ExpiresAt, HubPath, currentUser.SubjectType, null);
    }

    public async Task<Result<RealtimeTokenDto>> ForTrackedOrderAsync(TrackRequest request, CancellationToken cancellationToken)
    {
        var order = await checkout.FindTrackedAsync(request, false, cancellationToken);
        if (order is null)
        {
            return Error.NotFound(ErrorCodes.Order.NotFound, "We could not find an order with these details.");
        }

        var issued = tokens.Issue(new RealtimeTokenRequest(RealtimePrincipal.Track, order.Id, "guest", 0, order.Id));
        return new RealtimeTokenDto(issued.Token, issued.ExpiresAt, HubPath, RealtimePrincipal.Track, order.Id);
    }
}
