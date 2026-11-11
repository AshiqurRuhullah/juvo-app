using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Orders;
using Juvo.Domain.Orders;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Mvc;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Orders for the shop, the customer and the staff (modules D, S, T and J basics). The public checkout and tracking are anonymous on purpose and rate limited;
/// the customer panel needs a customer token; every staff endpoint names its permission (services prove it again in the order's own branch).
/// </summary>
internal static class OrdersEndpoints
{
    private const string CartTokenHeader = "X-Cart-Token";
    private const string IdempotencyHeader = "Idempotency-Key";

    public static WebApplication MapOrdersEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapStore(v1.MapGroup("/store").WithTags("Checkout").AllowAnonymous().RequireRateLimiting(RateLimitSettings.StorefrontPolicy));
        MapMe(v1.MapGroup("/me/orders").WithTags("Customer orders").RequireAuthorization(Policies.Customer));
        MapStaff(v1.MapGroup("/orders").WithTags("Orders (staff)"));
        MapFraud(v1.MapGroup("/fraud").WithTags("Fraud (staff)"));
        return app;
    }

    private static void MapStore(RouteGroupBuilder g)
    {
        g.MapPost("/checkout/quote", async ([FromHeader(Name = CartTokenHeader)] string? token, CheckoutQuoteRequest r, CheckoutService s, CancellationToken ct) =>
                (await s.QuoteAsync(token, r, ct)).ToHttpResult())
            .Produces<CheckoutQuoteDto>()
            .WithSummary("Live checkout totals for the cart and an address: items, coupon, delivery charge, grand total (prices include tax), advance for large cash on delivery orders, payment methods, whether an OTP is needed.");

        g.MapPost("/checkout", async ([FromHeader(Name = CartTokenHeader)] string? token, [FromHeader(Name = IdempotencyHeader)] string? key, CheckoutRequest r, CheckoutService s, CancellationToken ct) =>
                (await s.CheckoutAsync(token, string.IsNullOrWhiteSpace(r.IdempotencyKey) ? r with { IdempotencyKey = key } : r, ct)).ToHttpResult(v => Results.Created($"/api/v1/store/orders/track?number={v.Number}", v)))
            .RequireRateLimiting(RateLimitSettings.CheckoutPolicy).Produces<CheckoutResultDto>(StatusCodes.Status201Created)
            .WithSummary("Place the order from the cart. Guest (a mobile number is enough) or signed-in customer. Totals are recalculated; stock is reserved; the answer holds the order number and a tracking token (shown once). Send Idempotency-Key to make retries safe.");

        g.MapPost("/orders/track", async (TrackRequest r, CheckoutService s, CancellationToken ct) => (await s.TrackAsync(r, ct)).ToHttpResult())
            .RequireRateLimiting(RateLimitSettings.CheckoutPolicy).Produces<TrackingDto>()
            .WithSummary("Track an order by order number and mobile number, or by tracking token. Anything that does not match answers 404.");

        g.MapGet("/orders/track", async (string? number, string? mobile, string? token, CheckoutService s, CancellationToken ct) => (await s.TrackAsync(new TrackRequest(number, mobile, token), ct)).ToHttpResult())
            .RequireRateLimiting(RateLimitSettings.CheckoutPolicy).Produces<TrackingDto>()
            .WithSummary("Same as the POST form, for tracking links (?number=&mobile= or ?token=).");
    }

    private static void MapMe(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, string? filter, CustomerOrdersService s, CancellationToken ct) => (await s.ListAsync(list.ToRequest(), filter, ct)).ToHttpResult())
            .Produces<PagedResult<MyOrderListItemDto>>().WithSummary("My orders, newest first. filter: active, delivered, cancelled, returned.");

        g.MapGet("/{id:guid}", async (Guid id, CustomerOrdersService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .Produces<MyOrderDto>().WithSummary("One of my orders with items, amounts, the three statuses and the timeline.");

        g.MapPost("/{id:guid}/cancel", async (Guid id, CancelOrderRequest r, CustomerOrdersService s, CancellationToken ct) => (await s.CancelAsync(id, r, ct)).ToHttpResult())
            .Produces<MyOrderDto>().WithSummary("Cancel my order while it is still in the shop (before courier pickup). Reserved stock goes back at once.");
    }

    private static void MapStaff(RouteGroupBuilder g)
    {
        g.MapGet("", async ([AsParameters] ListParams list, OrderStatus? status, PaymentStatus? paymentStatus, DeliveryStatus? deliveryStatus, Guid? branchId, OrderSource? source,
                FraudState? fraudState, DateTimeOffset? from, DateTimeOffset? to, bool? held, OrdersService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new OrderListQuery(status, paymentStatus, deliveryStatus, branchId, source, fraudState, from, to, held), ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderView).Produces<PagedResult<OrderListItemDto>>()
            .WithSummary("Orders of the branches where you may view orders. Filters: the three status tracks, branch, source, fraud state, held, date range; search by number, mobile or name; sort placedAt (default newest), total, number, status.");

        g.MapGet("/{id:guid}", async (Guid id, OrdersService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderView).Produces<OrderDetailDto>()
            .WithSummary("One order: items, addresses, amounts, three tracks, full timeline, notes, risk flags, fraud log, customer history and the statuses it may move to.");

        g.MapPost("/{id:guid}/confirm", async (Guid id, ConfirmOrderRequest r, OrdersService s, CancellationToken ct) => (await s.ConfirmAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderConfirm).Produces<OrderDetailDto>()
            .WithSummary("Confirm an order. Refused while it is held for a fraud review, while an online payment is pending, or (advanceReceived = false) when an advance is required.");

        g.MapPost("/{id:guid}/pack", async (Guid id, OrdersService s, CancellationToken ct) => (await s.PackAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderPack).Produces<OrderDetailDto>()
            .WithSummary("Pack a confirmed order. With the default setting the reserved stock leaves the shelf now (Inventory.DeductAt).");

        g.MapPost("/{id:guid}/cancel", async (Guid id, CancelOrderRequest r, OrdersService s, CancellationToken ct) => (await s.CancelAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderCancel).Produces<OrderDetailDto>()
            .WithSummary("Cancel an order that is Placed, Confirmed or Packed, with a reason. Stock is released (or returned to the shelf when already packed).");

        g.MapPost("/{id:guid}/transition", async (Guid id, TransitionRequest r, OrdersService s, CancellationToken ct) => (await s.TransitionAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderEdit).Produces<OrderDetailDto>()
            .WithSummary("Record a delivery step: HandedToCourier, InTransit, OutForDelivery, Delivered, DeliveryFailed, ReturnInProgress, Returned. Only moves the state machine allows (couriers drive this in Step 10).");

        g.MapPost("/{id:guid}/notes", async (Guid id, AddNoteRequest r, OrdersService s, CancellationToken ct) => (await s.AddNoteAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderNote).Produces<OrderDetailDto>().WithSummary("Add an internal note (staff only) or a customer-visible note.");

        g.MapPost("/{id:guid}/reassign", async (Guid id, ReassignRequest r, OrdersService s, CancellationToken ct) => (await s.ReassignAsync(id, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderReassignBranch).Produces<OrderDetailDto>()
            .WithSummary("Move an order to another fulfilling branch before packing: the reservation is released here and taken there (the other branch must hold every item).");

        g.MapGet("/{id:guid}/packing-slip", async (Guid id, OrdersService s, CancellationToken ct) => (await s.SlipAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderPrint).Produces<PackingSlipDto>().WithSummary("The data of the packing slip (the screen prints it).");

        g.MapPost("/bulk", async (BulkOrderRequest r, OrdersService s, CancellationToken ct) => (await s.BulkAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderBulkAction).Produces<BulkOrderResult>()
            .WithSummary("Confirm, cancel or print up to 100 orders. Each order is handled on its own (permission, state, stock); the answer lists success or the error code per order.");

        g.MapPost("/manual", async (ManualOrderRequest r, OrdersService s, CancellationToken ct) =>
                (await s.CreateManualAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/orders/{v.Id}", v)))
            .RequirePermission(Permissions.Orders.OrderCreate).Produces<OrderDetailDto>(StatusCodes.Status201Created)
            .WithSummary("Social order desk: create an order for a customer found by mobile (Facebook, WhatsApp, phone, walk-in). Items by SKU or variant; discount and delivery override need their own permissions.");

        g.MapPost("/manual/quote", async (ManualOrderRequest r, OrdersService s, CancellationToken ct) => (await s.QuoteManualAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.OrderCreate).Produces<ManualQuoteDto>()
            .WithSummary("Social order desk preview: the same checks and prices as creating the order (coupon, delivery charge, discount and override permissions, stock in the branch), nothing saved or reserved. The totals equal the order that create makes.");

        g.MapGet("/sources/summary", async (DateTimeOffset? from, DateTimeOffset? to, OrdersService s, CancellationToken ct) => (await s.SourceCountsAsync(from, to, ct)).ToHttpResult())
            .RequirePermission(Permissions.Orders.SourceView).Produces<IReadOnlyList<OrderSourceCountDto>>().WithSummary("Orders and value per source in a period (the owner's daily count).");
    }

    private static void MapFraud(RouteGroupBuilder g)
    {
        g.MapGet("/reviews", async ([AsParameters] ListParams list, FraudState? state, FraudService s, CancellationToken ct) => (await s.QueueAsync(list.ToRequest(), state, ct)).ToHttpResult())
            .RequirePermission(Permissions.Fraud.FlagView).Produces<PagedResult<ReviewItemDto>>()
            .WithSummary("The review queue: orders held for review (Flagged, InReview, AdvanceRequested), riskiest first, with their flags and the customer's history.");

        g.MapPost("/reviews/{orderId:guid}/decide", async (Guid orderId, FraudDecisionRequest r, FraudService s, CancellationToken ct) => (await s.DecideAsync(orderId, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Fraud.ReviewDecide).Produces<IReadOnlyList<FraudLogDto>>()
            .WithSummary("Decide a held order: Approve, RequestAdvance, Call (note) or Reject (reason; cancels the order). Logged with the staff name.");

        g.MapGet("/customers/{customerId:guid}/stats", async (Guid customerId, FraudService s, CancellationToken ct) => Results.Ok(await s.StatsAsync(customerId, ct)))
            .RequirePermission(Permissions.Fraud.FlagView).Produces<CustomerOrderStatsDto>().WithSummary("Order history of a customer: delivered, cancelled, returned or failed, spend, history score.");

        g.MapGet("/blacklist", async ([AsParameters] ListParams list, BlacklistKind? kind, FraudService s, CancellationToken ct) => (await s.ListBlacklistAsync(list.ToRequest(), kind, ct)).ToHttpResult())
            .RequirePermission(Permissions.Fraud.BlacklistView).Produces<PagedResult<BlacklistDto>>().WithSummary("Blocked mobile numbers, e-mails, address texts and devices.");

        g.MapPost("/blacklist", async (AddBlacklistRequest r, FraudService s, CancellationToken ct) =>
                (await s.AddBlacklistAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/fraud/blacklist/{v.Id}", v)))
            .RequirePermission(Permissions.Fraud.BlacklistCreate).Produces<BlacklistDto>(StatusCodes.Status201Created).WithSummary("Block a mobile, e-mail, address text or device.");

        g.MapDelete("/blacklist/{id:guid}", async (Guid id, FraudService s, CancellationToken ct) => (await s.RemoveBlacklistAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Fraud.BlacklistDelete).Produces(StatusCodes.Status204NoContent).WithSummary("Remove an entry (who and when stay in the audit log).");

        g.MapGet("/rules", async (FraudService s, CancellationToken ct) => (await s.ListRulesAsync(ct)).ToHttpResult())
            .RequirePermission(Permissions.Fraud.RuleView).Produces<IReadOnlyList<FraudRuleDto>>().WithSummary("The fraud rules with their switch, threshold and action.");

        g.MapPut("/rules/{code}", async (string code, UpdateFraudRuleRequest r, FraudService s, CancellationToken ct) => (await s.UpdateRuleAsync(code, r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Fraud.RuleEdit).Produces<FraudRuleDto>().WithSummary("Switch a rule on or off, set its threshold and its action (Flag, Hold, RequireAdvance, Reject).");
    }
}
