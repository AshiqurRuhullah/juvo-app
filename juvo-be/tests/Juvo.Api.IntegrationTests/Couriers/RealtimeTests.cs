using System.Net;
using System.Text.Json;
using System.Threading.Channels;
using Juvo.Api.IntegrationTests.Orders;
using Juvo.Api.IntegrationTests.Storefront;
using Juvo.Api.IntegrationTests.Support;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using static Juvo.Api.IntegrationTests.Couriers.CourierHelper;

namespace Juvo.Api.IntegrationTests.Couriers;

/// <summary>A shop whose clock the test can move (hub tokens live five minutes).</summary>
public sealed class RealtimeFactory : StoreFactory
{
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    protected override void ConfigureTestServices(IServiceCollection services) => services.AddSingleton<TimeProvider>(Time);
}

/// <summary>One browser connected to the hub, with every event it receives kept in order.</summary>
public sealed class HubClient : IAsyncDisposable
{
    private readonly Channel<(string Name, JsonElement Body)> _events = Channel.CreateUnbounded<(string, JsonElement)>();

    public HubClient(RealtimeFactory factory, string token)
    {
        Connection = new HubConnectionBuilder()
            .WithUrl(new Uri(factory.Server.BaseAddress, "/hubs/orders"), o =>
            {
                o.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling; // the in-memory test server has no real socket
                o.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();
        foreach (var name in new[] { "OrderStatusChanged", "PaymentStatusChanged", "ShipmentEventAdded", "ReturnStatusChanged", "NewOrder" })
        {
            Connection.On<JsonElement>(name, body => _events.Writer.TryWrite((name, body.Clone())));
        }
    }

    public HubConnection Connection { get; }

    public Task StartAsync() => Connection.StartAsync(TestContext.Current.CancellationToken);

    public async Task<string> JoinOrderAsync(Guid orderId) => await Connection.InvokeAsync<string>("JoinOrder", orderId, TestContext.Current.CancellationToken);

    public async Task<string> JoinBranchAsync(string branch) => await Connection.InvokeAsync<string>("JoinBranch", branch, TestContext.Current.CancellationToken);

    /// <summary>Waits for the next event (up to 10 s) or fails the test.</summary>
    public async Task<(string Name, JsonElement Body)> NextAsync()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        return await _events.Reader.ReadAsync(timeout.Token);
    }

    /// <summary>Waits until an event that matches arrives, collecting the ones before it.</summary>
    public async Task<((string Name, JsonElement Body) Match, List<(string Name, JsonElement Body)> Before)> UntilAsync(Func<string, JsonElement, bool> match)
    {
        var before = new List<(string, JsonElement)>();
        while (true)
        {
            var next = await NextAsync();
            if (match(next.Name, next.Body))
            {
                return (next, before);
            }

            before.Add(next);
        }
    }

    /// <summary>Reads events until every wanted one has been seen (in any order) and returns everything read, so a test does not depend on which message the outbox delivered first.</summary>
    public async Task<List<(string Name, JsonElement Body)>> CollectAsync(params Func<string, JsonElement, bool>[] wanted)
    {
        var seen = new List<(string Name, JsonElement Body)>();
        while (!wanted.All(w => seen.Any(e => w(e.Name, e.Body))))
        {
            seen.Add(await NextAsync());
        }

        return seen;
    }

    /// <summary>True when nothing arrives within the wait. Only used after the matching event for a sibling was already seen, so a leak would have arrived first.</summary>
    public async Task<bool> StaysQuietAsync(TimeSpan wait)
    {
        await Task.Delay(wait, TestContext.Current.CancellationToken);
        return !_events.Reader.TryPeek(out _);
    }

    public async ValueTask DisposeAsync() => await Connection.DisposeAsync();
}

public class RealtimeTests(RealtimeFactory factory) : IClassFixture<RealtimeFactory>
{
    private readonly CourierHelper _c = new(new OrderHelper(new StoreHelper(factory)), factory);

    private StoreHelper H => _c.H;

    private OrderHelper O => _c.O;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private async Task<string> HubTokenAsync(string bearer)
    {
        var token = await H.JsonAsync(HttpMethod.Post, "/api/v1/realtime/token", HttpStatusCode.OK, new { }, bearer);
        Assert.Equal("/hubs/orders", Str(token, "hubPath"));
        return Str(token, "token");
    }

    private async Task<string> GuestTokenAsync(string number, string mobile) =>
        Str(await H.JsonAsync(HttpMethod.Post, "/api/v1/store/orders/track/realtime-token", HttpStatusCode.OK, new { number, mobile }), "token");

    private static async Task<HubException> RefusedAsync(Func<Task> action)
    {
        var ex = await Assert.ThrowsAnyAsync<Exception>(action);
        Assert.Contains("REALTIME.FORBIDDEN", ex.Message, StringComparison.Ordinal);
        return Assert.IsType<HubException>(ex);
    }

    // ---- customers ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_customer_receives_the_events_of_their_own_order_and_not_of_anyone_elses()
    {
        var (admin, product) = await _c.ProductAsync();
        var mine = await _c.PackedOrderForCustomerAsync(admin, product.Variants[0].Id);
        var theirs = await _c.PackedAsync(admin, product.Variants[0].Id);

        await using var client = new HubClient(factory, await HubTokenAsync(mine.Customer));
        await client.StartAsync();
        Assert.StartsWith("order:", await client.JoinOrderAsync(mine.Order.OrderId), StringComparison.Ordinal);
        await RefusedAsync(() => client.JoinOrderAsync(theirs.OrderId));
        await RefusedAsync(() => client.JoinOrderAsync(Guid.NewGuid()));
        await RefusedAsync(() => client.JoinBranchAsync("all"));
        var hoId = await H.Api.BranchIdAsync("HO");
        await RefusedAsync(() => client.JoinBranchAsync(hoId.ToString()));

        // Someone else's parcel moves first; then mine.
        var otherShipment = await _c.BookAsync(admin, theirs.OrderId);
        await _c.AdvanceAsync(admin, otherShipment.GetProperty("id").GetGuid());
        var shipment = await _c.BookAsync(admin, mine.Order.OrderId);
        var shipmentId = shipment.GetProperty("id").GetGuid();
        await _c.AdvanceAsync(admin, shipmentId, null, "Staff only: fragile");
        await _c.DrainOutboxAsync();

        var seen = await client.CollectAsync(
            (name, body) => name == "ShipmentEventAdded" && body.GetProperty("status").GetString() == "PickedUp" && body.GetProperty("shipmentId").GetGuid() == shipmentId,
            (name, body) => name == "OrderStatusChanged" && body.GetProperty("track").GetString() == "Order" && body.GetProperty("to").GetString() == "HandedToCourier");
        Assert.All(seen, e => Assert.Equal(mine.Order.OrderId, e.Body.GetProperty("orderId").GetGuid())); // nothing about the other order, ever
        var match = seen.First(e => e.Name == "ShipmentEventAdded" && e.Body.GetProperty("status").GetString() == "PickedUp");
        Assert.Equal(mine.Order.Number, Str(match.Body, "orderNumber"));
        Assert.Equal("Sandbox courier", Str(match.Body, "courierName"));
        Assert.NotNull(match.Body.GetProperty("trackingUrl").GetString());

        // The order track change that came with the pickup, without any staff detail.
        var status = seen.First(e => e.Name == "OrderStatusChanged" && e.Body.GetProperty("track").GetString() == "Order" && e.Body.GetProperty("to").GetString() == "HandedToCourier");
        Assert.Equal("Order", Str(status.Body, "track"));
        Assert.Equal("Packed", Str(status.Body, "from"));
        var raw = JsonSerializer.Serialize(seen.Select(e => e.Body).ToList());
        Assert.DoesNotContain("reason", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("fragile", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("rawStatus", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("description", raw, StringComparison.Ordinal);

        // Payment track changes (cash collected on delivery) arrive as their own event.
        for (var i = 0; i < 3; i++)
        {
            await _c.AdvanceAsync(admin, shipmentId);
        }

        await _c.DrainOutboxAsync();
        var payment = await client.UntilAsync((name, body) => name == "PaymentStatusChanged");
        Assert.Equal("CollectedByCourier", Str(payment.Match.Body, "to"));
        Assert.Equal(mine.Order.OrderId, payment.Match.Body.GetProperty("orderId").GetGuid());
    }

    [Fact]
    public async Task A_guest_follows_exactly_the_one_order_of_their_tracking_proof()
    {
        var (admin, product) = await _c.ProductAsync();
        var mine = await _c.PackedAsync(admin, product.Variants[0].Id);
        var other = await _c.PackedAsync(admin, product.Variants[0].Id);

        // Wrong proof: no token.
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track/realtime-token", new { number = mine.Number, mobile = other.Mobile })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await H.SendAsync(HttpMethod.Post, "/api/v1/store/orders/track/realtime-token", new { token = "nonsense" })).StatusCode);
        // The tracking token works as proof too.
        var byToken = await H.JsonAsync(HttpMethod.Post, "/api/v1/store/orders/track/realtime-token", HttpStatusCode.OK, new { token = mine.Token });
        Assert.Equal(mine.OrderId, byToken.GetProperty("orderId").GetGuid());

        var hubToken = await GuestTokenAsync(mine.Number, mine.Mobile);
        await using var guest = new HubClient(factory, hubToken);
        await guest.StartAsync();
        await guest.JoinOrderAsync(mine.OrderId);
        await RefusedAsync(() => guest.JoinOrderAsync(other.OrderId));
        await RefusedAsync(() => guest.JoinBranchAsync("all"));

        await O.ActAsync(admin, other.OrderId, "cancel", new { reason = "Not this one" });
        var shipment = await _c.BookAsync(admin, mine.OrderId);
        await _c.AdvanceAsync(admin, shipment.GetProperty("id").GetGuid());
        await _c.DrainOutboxAsync();
        var (match, before) = await guest.UntilAsync((name, _) => name == "ShipmentEventAdded");
        Assert.All(before.Concat([match]), e => Assert.Equal(mine.OrderId, e.Body.GetProperty("orderId").GetGuid()));

        // A hub token opens nothing else, and an ordinary token opens nothing here.
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, "/api/v1/orders", null, hubToken)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Get, "/api/v1/me/orders", null, hubToken)).StatusCode);
        await using var wrong = new HubClient(factory, admin);
        await Assert.ThrowsAnyAsync<Exception>(wrong.StartAsync);
        await using var nothing = new HubClient(factory, "not a token");
        await Assert.ThrowsAnyAsync<Exception>(nothing.StartAsync);
    }

    // ---- staff ----------------------------------------------------------------------------------------------

    [Fact]
    public async Task Staff_receive_the_events_of_the_branches_they_follow_and_are_refused_the_others()
    {
        var admin = await H.AdminAsync();
        var ho = await H.Api.BranchIdAsync("HO");
        var other = await H.Api.CreateBranchAsync(admin, "RT" + Random.Shared.Next(100, 999), "Realtime other shop");
        var hoStaff = await _c.StaffAsync(admin, ho, "orders.order.view");
        var otherStaff = await _c.StaffAsync(admin, other, "orders.order.view");
        var noView = await _c.StaffAsync(admin, ho, "couriers.shipment.view");

        await using var hoClient = new HubClient(factory, await HubTokenAsync(hoStaff));
        await using var otherClient = new HubClient(factory, await HubTokenAsync(otherStaff));
        await using var globalClient = new HubClient(factory, await HubTokenAsync(admin));
        await using var blind = new HubClient(factory, await HubTokenAsync(noView));
        foreach (var client in new[] { hoClient, otherClient, globalClient, blind })
        {
            await client.StartAsync();
        }

        Assert.StartsWith("branch:", await hoClient.JoinBranchAsync(ho.ToString()));
        await RefusedAsync(() => hoClient.JoinBranchAsync(other.ToString())); // out of their scope
        await RefusedAsync(() => hoClient.JoinBranchAsync("all")); // not a global user
        await otherClient.JoinBranchAsync(other.ToString());
        await RefusedAsync(() => otherClient.JoinBranchAsync(ho.ToString()));
        Assert.Equal("branch:all", await globalClient.JoinBranchAsync("all"));
        await globalClient.JoinBranchAsync(other.ToString());
        await RefusedAsync(() => blind.JoinBranchAsync(ho.ToString())); // a signed-in staff member without orders.order.view
        await Assert.ThrowsAnyAsync<HubException>(() => hoClient.JoinBranchAsync("not-a-branch"));

        // A new order in the head office tells its staff (with the staff details) and the global watcher, not the other branch.
        var (_, product) = await _c.ProductAsync();
        var placed = await O.PlaceAsync(product.Variants[0].Id, 1);
        var orderId = placed.GetProperty("orderId").GetGuid();
        await O.ActAsync(admin, orderId, "confirm");
        await _c.DrainOutboxAsync();

        var hoSeen = await hoClient.CollectAsync(
            (name, body) => name == "NewOrder" && body.GetProperty("orderId").GetGuid() == orderId,
            (name, body) => name == "OrderStatusChanged" && body.GetProperty("orderId").GetGuid() == orderId && body.GetProperty("to").GetString() == "Confirmed");
        var hoNew = hoSeen.First(e => e.Name == "NewOrder" && e.Body.GetProperty("orderId").GetGuid() == orderId);
        Assert.Equal(placed.GetProperty("number").GetString(), Str(hoNew.Body, "orderNumber"));
        Assert.Equal(ho, hoNew.Body.GetProperty("branchId").GetGuid());
        Assert.Equal("Web", Str(hoNew.Body, "source"));
        var confirmed = hoSeen.First(e => e.Name == "OrderStatusChanged" && e.Body.GetProperty("orderId").GetGuid() == orderId && e.Body.GetProperty("to").GetString() == "Confirmed");
        Assert.True(confirmed.Body.TryGetProperty("reason", out _)); // staff get the reason field (null here)
        await globalClient.CollectAsync((name, body) => name == "NewOrder" && body.GetProperty("orderId").GetGuid() == orderId);
        Assert.True(await otherClient.StaysQuietAsync(TimeSpan.FromSeconds(1.5)), "the other branch must hear nothing about a head office order");
    }

    [Fact]
    public async Task Staff_whose_access_changed_after_the_token_was_made_are_refused_until_they_get_a_new_token()
    {
        var admin = await H.AdminAsync();
        var ho = await H.Api.BranchIdAsync("HO");
        var role = await H.Api.CreateRoleAsync(admin, StoreHelper.Unique("RtRole"), "orders.order.view");
        var email = $"{StoreHelper.Unique("rt")}@example.com";
        var user = await H.Api.CreateUserAsync(admin, email, "Realtime Staff", false, (ho, role));
        var session = await H.Api.LoginAsync(user.Email, user.Password);

        var oldToken = await HubTokenAsync(session.Access);
        await using var first = new HubClient(factory, oldToken);
        await first.StartAsync();
        await first.JoinBranchAsync(ho.ToString());

        // Roles change: the permission version moves on.
        Assert.Equal(HttpStatusCode.OK, (await H.SendAsync(HttpMethod.Put, $"/api/v1/ums/roles/{role}/permissions", new { permissions = new[] { "orders.order.view", "orders.order.note" } }, admin)).StatusCode);
        await RefusedAsync(() => first.JoinBranchAsync(ho.ToString())); // the connection still lives, but joining again needs current access
        await using var stale = new HubClient(factory, oldToken);
        await stale.StartAsync();
        await RefusedAsync(() => stale.JoinBranchAsync(ho.ToString()));

        // The old access token is refused at the token endpoint (stale), a refreshed session gets a working hub token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Post, "/api/v1/realtime/token", new { }, session.Access)).StatusCode);
        var refreshed = await H.Api.LoginAsync(user.Email, user.Password);
        await using var fresh = new HubClient(factory, await HubTokenAsync(refreshed.Access));
        await fresh.StartAsync();
        await fresh.JoinBranchAsync(ho.ToString());

        // A deactivated member cannot join at all.
        Assert.Equal(HttpStatusCode.NoContent, (await H.SendAsync(HttpMethod.Post, $"/api/v1/ums/users/{user.Id}/deactivate", new { }, admin)).StatusCode);
        await RefusedAsync(() => fresh.JoinBranchAsync(ho.ToString()));
    }

    // ---- token hand-off -------------------------------------------------------------------------------------

    [Fact]
    public async Task The_hub_token_is_short_lived_and_the_endpoint_needs_a_signed_in_person()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await H.SendAsync(HttpMethod.Post, "/api/v1/realtime/token", new { })).StatusCode);

        var (admin, product) = await _c.ProductAsync();
        var customer = await H.CustomerTokenAsync();
        var token = await H.JsonAsync(HttpMethod.Post, "/api/v1/realtime/token", HttpStatusCode.OK, new { }, customer);
        Assert.Equal("customer", Str(token, "role"));
        var lifetime = DateTimeOffset.Parse(Str(token, "expiresAt"), System.Globalization.CultureInfo.InvariantCulture) - factory.Time.GetUtcNow();
        Assert.InRange(lifetime.TotalSeconds, 4 * 60, 5 * 60 + 1);

        // Valid now ...
        await using (var now = new HubClient(factory, Str(token, "token")))
        {
            await now.StartAsync();
        }

        // ... and refused after it ended, while a fresh one works.
        factory.Time.Advance(TimeSpan.FromMinutes(6));
        await using (var late = new HubClient(factory, Str(token, "token")))
        {
            await Assert.ThrowsAnyAsync<Exception>(late.StartAsync);
        }

        var renewed = await H.JsonAsync(HttpMethod.Post, "/api/v1/realtime/token", HttpStatusCode.OK, new { }, customer);
        await using var again = new HubClient(factory, Str(renewed, "token"));
        await again.StartAsync();

        // A guest token ends the same way.
        var order = await _c.PackedAsync(admin, product.Variants[0].Id);
        var guest = await GuestTokenAsync(order.Number, order.Mobile);
        factory.Time.Advance(TimeSpan.FromMinutes(6));
        await using var expiredGuest = new HubClient(factory, guest);
        await Assert.ThrowsAnyAsync<Exception>(expiredGuest.StartAsync);
    }
}
