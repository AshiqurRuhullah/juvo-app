using System.Security.Claims;
using Juvo.Api.Configuration;
using Juvo.Application.Identity;
using Juvo.Application.Realtime;
using Juvo.Infrastructure.Identity;
using Juvo.Shared.Results;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Juvo.Api.Realtime;

/// <summary>The authentication scheme of the hub: a short-lived hub token (audience <c>&lt;audience&gt;:realtime</c>), never an access token.</summary>
public static class RealtimeAuth
{
    public const string Scheme = "Realtime";

    public const string HubPath = "/hubs/orders";

    public static IServiceCollection AddRealtimeAuth(this IServiceCollection services)
    {
        services.AddOptions<JwtBearerOptions>(Scheme).Configure<SigningKeyProvider, IOptions<AuthOptions>, TimeProvider>((bearer, keys, auth, clock) =>
        {
            bearer.MapInboundClaims = false;
            bearer.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = auth.Value.Issuer,
                ValidateAudience = true,
                ValidAudience = RealtimeTokenService.AudienceOf(auth.Value),
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = keys.Key,
                ValidateLifetime = true,
                LifetimeValidator = (notBefore, expires, _, _) =>
                {
                    // One clock for issuing and validating, so tests can move time.
                    var now = clock.GetUtcNow().UtcDateTime;
                    var skew = TimeSpan.FromSeconds(10);
                    if (expires is null)
                    {
                        throw new SecurityTokenNoExpirationException("The token has no expiry.");
                    }

                    if (notBefore is { } start && start > now + skew)
                    {
                        throw new SecurityTokenNotYetValidException("The token is not valid yet.") { NotBefore = start };
                    }

                    if (expires.Value < now - skew)
                    {
                        throw new SecurityTokenExpiredException("The token has expired.") { Expires = expires.Value };
                    }

                    return true;
                },
                ClockSkew = TimeSpan.FromSeconds(10),
                NameClaimType = "name",
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            };
            bearer.Events = new JwtBearerEvents
            {
                // A WebSocket cannot send an Authorization header from a browser: the token travels as ?access_token= on the hub path only.
                OnMessageReceived = context =>
                {
                    if (context.Request.Path.StartsWithSegments(HubPath, StringComparison.OrdinalIgnoreCase) && context.Request.Query["access_token"] is { Count: > 0 } token)
                    {
                        context.Token = token;
                    }

                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    var user = context.Principal;
                    var type = user?.FindFirstValue("stype");
                    if (user is null || user.FindFirstValue("scope") != "realtime" || type is not ("staff" or "customer" or "track") || !Guid.TryParse(user.FindFirstValue("sub"), out _))
                    {
                        context.Fail("not a hub token");
                    }

                    return Task.CompletedTask;
                },
            };
        });
        return services;
    }
}

/// <summary>
/// The real-time hub (module J), <c>/hubs/orders</c>. Connect with a hub token (<c>POST /api/v1/realtime/token</c>, or <c>POST /api/v1/store/orders/track/realtime-token</c> for a guest), then
/// join what you may follow: <c>JoinOrder(orderId)</c> (a customer: their own orders; a guest: the one order of the token; staff: orders of their branches) and <c>JoinBranch(branchId | "all")</c>
/// (staff). A refused join throws a <see cref="HubException"/> whose message is <c>REALTIME.FORBIDDEN</c>. Events: <c>OrderStatusChanged</c>, <c>PaymentStatusChanged</c>, <c>ShipmentEventAdded</c>,
/// <c>ReturnStatusChanged</c> and, for staff, <c>NewOrder</c>. Delivery is at-least-once: a client must tolerate the same event twice.
/// </summary>
[Authorize(AuthenticationSchemes = RealtimeAuth.Scheme)]
public sealed class OrdersHub(RealtimeAccess access) : Hub
{
    private RealtimePrincipal Principal()
    {
        var user = Context.User ?? throw new HubException(ErrorCodes.Realtime.TokenInvalid);
        var type = user.FindFirstValue("stype");
        _ = Guid.TryParse(user.FindFirstValue("sub"), out var id);
        _ = int.TryParse(user.FindFirstValue("pv"), out var pv);
        Guid? order = Guid.TryParse(user.FindFirstValue("order"), out var o) ? o : null;
        return new RealtimePrincipal(type ?? string.Empty, id, pv, order);
    }

    private async Task<string> JoinAsync(Result<string> group)
    {
        if (group.IsFailure)
        {
            throw new HubException(group.Error!.Code);
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, group.Value, Context.ConnectionAborted);
        return group.Value;
    }

    /// <summary>Follow one order. Returns the group name.</summary>
    public async Task<string> JoinOrder(Guid orderId) => await JoinAsync(await access.OrderGroupAsync(Principal(), orderId, Context.ConnectionAborted));

    public async Task LeaveOrder(Guid orderId) => await Groups.RemoveFromGroupAsync(Context.ConnectionId, RealtimeNames.OrderGroup(orderId), Context.ConnectionAborted);

    /// <summary>Staff: follow a branch (a branch id) or every branch you may see (<c>all</c>). Returns the group name.</summary>
    public async Task<string> JoinBranch(string branch) => await JoinAsync(await access.BranchGroupAsync(Principal(), branch ?? string.Empty, Context.ConnectionAborted));

    public async Task LeaveBranch(string branch)
    {
        var group = string.Equals(branch, RealtimeNames.AllBranches, StringComparison.OrdinalIgnoreCase) ? RealtimeNames.AllBranchesGroup : Guid.TryParse(branch, out var id) ? RealtimeNames.BranchGroup(id) : null;
        if (group is not null)
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        }
    }
}

/// <summary>Pushes outbox events to the hub groups: the customer-safe payload to the order group, the staff payload to the branch groups (and the all-branches group).</summary>
internal sealed class SignalRRealtimePublisher(IHubContext<OrdersHub> hub) : IRealtimePublisher
{
    public async Task PublishAsync(RealtimeEnvelope message, CancellationToken cancellationToken)
    {
        if (message.Customer is { } customer)
        {
            await hub.Clients.Group(RealtimeNames.OrderGroup(message.OrderId)).SendAsync(message.Event, customer, cancellationToken);
        }

        if (message.Staff is { } staff)
        {
            await hub.Clients.Groups(RealtimeNames.BranchGroup(message.BranchId), RealtimeNames.AllBranchesGroup).SendAsync(message.Event, staff, cancellationToken);
        }
    }
}
