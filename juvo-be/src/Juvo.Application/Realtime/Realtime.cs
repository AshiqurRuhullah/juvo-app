using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Application.Ums;
using Juvo.Domain.Couriers;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Realtime;

/// <summary>The names of the hub events and groups, shared by the publisher and the hub (module J real-time).</summary>
public static class RealtimeNames
{
    public const string OrderStatusChanged = "OrderStatusChanged";
    public const string PaymentStatusChanged = "PaymentStatusChanged";
    public const string ShipmentEventAdded = "ShipmentEventAdded";
    public const string ReturnStatusChanged = "ReturnStatusChanged";
    public const string NewOrder = "NewOrder";

    public const string AllBranches = "all";

    public static string OrderGroup(Guid orderId) => $"order:{orderId:N}";

    public static string BranchGroup(Guid branchId) => $"branch:{branchId:N}";

    public const string AllBranchesGroup = "branch:all";
}

/// <summary>
/// One thing to push. <see cref="Customer"/> goes to the order group (the customer and anyone with the tracking token): no staff names, no internal reasons. <see cref="Staff"/> goes to
/// the branch groups. Both are plain objects that serialise to camelCase JSON. A null payload means that audience gets nothing.
/// </summary>
public sealed record RealtimeEnvelope(string Event, Guid OrderId, Guid BranchId, object? Customer, object? Staff);

/// <summary>Pushes to connected browsers. The API implements it with SignalR; without a host (tests of the application layer) the null publisher does nothing.</summary>
public interface IRealtimePublisher
{
    Task PublishAsync(RealtimeEnvelope message, CancellationToken cancellationToken);
}

public sealed class NullRealtimePublisher : IRealtimePublisher
{
    public Task PublishAsync(RealtimeEnvelope message, CancellationToken cancellationToken) => Task.CompletedTask;
}

// ---- Outbox handlers (at-least-once: a client may see an event twice and must tolerate it) ---------------------

public abstract class RealtimeHandler(IRealtimePublisher publisher, string messageType) : IOutboxMessageHandler
{
    public string MessageType => messageType;

    public abstract Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken);

    protected static T Read<T>(OutboxMessageContext context) =>
        JsonSerializer.Deserialize<T>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException($"The {context.MessageType} message has no content.");

    protected Task PublishAsync(RealtimeEnvelope message, CancellationToken cancellationToken) => publisher.PublishAsync(message, cancellationToken);
}

/// <summary>Order, delivery and payment track changes. The delivery and order tracks arrive as <c>OrderStatusChanged</c> (with <c>track</c>); the payment track as <c>PaymentStatusChanged</c>.</summary>
public sealed class RealtimeOrderStatusHandler(IRealtimePublisher publisher) : RealtimeHandler(publisher, "domain.OrderStatusChanged")
{
    public override Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = Read<OrderStatusChanged>(context);
        if (e.Track == OrderTrack.Payment)
        {
            var payload = new { orderId = e.OrderId, orderNumber = e.Number, from = e.From, to = e.To, at = e.At };
            return PublishAsync(new RealtimeEnvelope(RealtimeNames.PaymentStatusChanged, e.OrderId, e.BranchId, payload, new { payload.orderId, payload.orderNumber, payload.from, payload.to, payload.at, reason = e.Reason }), cancellationToken);
        }

        var customer = new { orderId = e.OrderId, orderNumber = e.Number, track = e.Track.ToString(), from = e.From, to = e.To, at = e.At };
        return PublishAsync(new RealtimeEnvelope(RealtimeNames.OrderStatusChanged, e.OrderId, e.BranchId, customer, new { customer.orderId, customer.orderNumber, customer.track, customer.from, customer.to, customer.at, reason = e.Reason }), cancellationToken);
    }
}

/// <summary>A new order tells the staff of its branch (never the customer: they were just told on the confirmation page).</summary>
public sealed class RealtimeOrderPlacedHandler(IRealtimePublisher publisher) : RealtimeHandler(publisher, "domain.OrderPlaced")
{
    public override Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = Read<OrderPlaced>(context);
        return PublishAsync(new RealtimeEnvelope(RealtimeNames.NewOrder, e.OrderId, e.BranchId, null,
            new { orderId = e.OrderId, orderNumber = e.Number, branchId = e.BranchId, total = e.GrandTotal, source = e.Source.ToString(), at = e.At }), cancellationToken);
    }
}

/// <summary>Courier scans and status changes. The customer gets the same scan text the tracking page shows; staff also get the raw status.</summary>
public sealed class RealtimeShipmentHandler(IRealtimePublisher publisher) : RealtimeHandler(publisher, "domain.ShipmentEventAdded")
{
    public override Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = Read<ShipmentEventAdded>(context);
        var customer = e.Kind == ShipmentKind.Forward
            ? new { shipmentId = e.ShipmentId, orderId = e.OrderId, orderNumber = e.OrderNumber, status = e.Status.ToString(), isStatusChange = e.IsStatusChange, location = e.Location, trackingUrl = e.TrackingUrl, courierName = e.CourierName, consignmentId = e.ConsignmentId, at = e.EventAt }
            : null;
        var staff = new
        {
            shipmentId = e.ShipmentId, orderId = e.OrderId, orderNumber = e.OrderNumber, kind = e.Kind.ToString(), returnRequestId = e.ReturnRequestId, status = e.Status.ToString(), isStatusChange = e.IsStatusChange,
            rawStatus = e.RawStatus, description = e.Description, location = e.Location, trackingUrl = e.TrackingUrl, courierName = e.CourierName, consignmentId = e.ConsignmentId, at = e.EventAt,
        };
        return PublishAsync(new RealtimeEnvelope(RealtimeNames.ShipmentEventAdded, e.OrderId, e.BranchId, customer, staff), cancellationToken);
    }
}

/// <summary>Return steps for the customer who asked and for staff.</summary>
public sealed class RealtimeReturnHandler(IRealtimePublisher publisher) : RealtimeHandler(publisher, "domain.ReturnStatusChanged")
{
    public override Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = Read<ReturnStatusChanged>(context);
        var customer = new { returnId = e.ReturnId, returnNumber = e.Number, orderId = e.OrderId, orderNumber = e.OrderNumber, kind = e.Kind.ToString(), from = e.From, to = e.To, at = e.At };
        return PublishAsync(new RealtimeEnvelope(RealtimeNames.ReturnStatusChanged, e.OrderId, e.BranchId, customer,
            new { customer.returnId, customer.returnNumber, customer.orderId, customer.orderNumber, customer.kind, customer.from, customer.to, customer.at, reason = e.Reason }), cancellationToken);
    }
}

// ---- Who may join which group ---------------------------------------------------------------------------------

/// <summary>Who is connecting, read from the validated realtime token: a staff member, a signed-in customer, or a guest holding the tracking token of one order.</summary>
public sealed record RealtimePrincipal(string SubjectType, Guid SubjectId, int PermissionVersion, Guid? TrackedOrderId)
{
    public const string Staff = "staff";
    public const string Customer = "customer";
    public const string Track = "track";
}

/// <summary>
/// The rules of the hub (module J). A customer joins only the orders that belong to them; a guest only the one order of their tracking token; staff join the orders and branches
/// where they hold <c>orders.order.view</c> (the "all" group only when their scope is global). Anything else is refused with the same answer whether the order exists or not, so
/// nobody can probe for order ids. Staff access is read fresh at every join, so a role change or deactivation takes effect at the next join (and stale tokens do not connect).
/// </summary>
public sealed class RealtimeAccess(IApplicationDbContext db, IUserStateCache states, AccessService accessService)
{
    private static Error Refused() => Error.Forbidden(ErrorCodes.Realtime.Forbidden, "You cannot follow this.");

    public async Task<Result<string>> OrderGroupAsync(RealtimePrincipal who, Guid orderId, CancellationToken cancellationToken)
    {
        var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => o.Id == orderId && !o.IsDeleted).Select(o => new { o.BranchId, o.CustomerId }).FirstOrDefaultAsync(cancellationToken);
        if (order is null)
        {
            return Refused();
        }

        switch (who.SubjectType)
        {
            case RealtimePrincipal.Track:
                return who.TrackedOrderId == orderId ? RealtimeNames.OrderGroup(orderId) : Refused();
            case RealtimePrincipal.Customer:
                return await states.CustomerExistsAsync(who.SubjectId, cancellationToken) && order.CustomerId == who.SubjectId ? RealtimeNames.OrderGroup(orderId) : Refused();
            case RealtimePrincipal.Staff:
                var actor = await ActorAsync(who, cancellationToken);
                return actor is not null && Can(actor, order.BranchId) ? RealtimeNames.OrderGroup(orderId) : Refused();
            default:
                return Refused();
        }
    }

    /// <summary>The group for <paramref name="branch"/> (a branch id or <c>all</c>). Staff only.</summary>
    public async Task<Result<string>> BranchGroupAsync(RealtimePrincipal who, string branch, CancellationToken cancellationToken)
    {
        if (who.SubjectType != RealtimePrincipal.Staff || await ActorAsync(who, cancellationToken) is not { } actor)
        {
            return Refused();
        }

        if (string.Equals(branch, RealtimeNames.AllBranches, StringComparison.OrdinalIgnoreCase))
        {
            return actor.IsGlobal && actor.Can(Permissions.Orders.OrderView, null) ? RealtimeNames.AllBranchesGroup : Refused();
        }

        if (!Guid.TryParse(branch, out var branchId))
        {
            return Error.Validation(new Dictionary<string, string[]> { ["branch"] = ["Use a branch id or \"all\"."] });
        }

        return Can(actor, branchId) ? RealtimeNames.BranchGroup(branchId) : Refused();
    }

    private static bool Can(Actor actor, Guid branchId) =>
        (actor.IsGlobal || actor.Access.BranchPermissions.ContainsKey(branchId)) && actor.Can(Permissions.Orders.OrderView, branchId);

    private async Task<Actor?> ActorAsync(RealtimePrincipal who, CancellationToken cancellationToken)
    {
        var state = await states.GetStaffAsync(who.SubjectId, cancellationToken);
        if (!state.Exists || !state.IsActive || state.PermissionVersion != who.PermissionVersion)
        {
            return null; // deactivated, or roles changed since the token was made: get a new token
        }

        return new Actor(who.SubjectId, await accessService.GetAsync(who.SubjectId, state.PermissionVersion, cancellationToken));
    }
}
