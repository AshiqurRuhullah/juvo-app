using System.Text.Json;
using Juvo.Application.Abstractions;
using Juvo.Application.Notifications;
using Juvo.Domain.Loyalty;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Loyalty;

/// <summary>The result of one expiry run.</summary>
public sealed record ExpiryRun(int Customers, int PointsExpired, int Warned);

/// <summary>
/// What delivered and returned orders do to the points ledger (FR-AB01): points are earned when an order is Delivered (on the goods value, not on delivery, and not on the part paid with
/// points or store credit), and taken back in proportion when a return of that order is refunded. Reversal never makes a balance negative: it takes what is left. All posting is idempotent
/// (ledger keys), so a redelivered outbox message changes nothing. Everything is skipped while the feature flag is off.
/// </summary>
public sealed class RewardsEngine(IApplicationDbContext db, LoyaltyLedger ledger, LoyaltySettingsService loyalty, ReferralService referrals, NotificationService notifications, TimeProvider clock)
{
    public const int BatchSize = 200;

    private async Task<Order?> OrderAsync(Guid orderId, CancellationToken cancellationToken) =>
        await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId, cancellationToken);

    /// <summary>The goods value an order earns points on: the total without delivery and without what was paid with points or store credit.</summary>
    public async Task<decimal> EarnBaseAsync(Order order, CancellationToken cancellationToken)
    {
        var rewards = await db.OrderTenders.AsNoTracking().Where(t => t.OrderId == order.Id).Select(t => t.PointsAmount + t.CreditAmount).FirstOrDefaultAsync(cancellationToken);
        return Math.Max(0m, order.GrandTotal - order.DeliveryCharge - rewards);
    }

    public async Task OnOrderDeliveredAsync(Guid orderId, CancellationToken cancellationToken)
    {
        var s = await loyalty.ReadAsync(cancellationToken);
        if (!s.Enabled || await OrderAsync(orderId, cancellationToken) is not { Status: OrderStatus.Delivered } order)
        {
            return;
        }

        var points = PointsMath.Earn(await EarnBaseAsync(order, cancellationToken), s.EarnRatePerBdt, s.EarnRounding);
        if (points > 0)
        {
            var now = clock.GetUtcNow();
            var posted = await ledger.PostPointsAsync(new PointsPosting(order.CustomerId, PointsEntryKind.Earn, points, $"Earned on order {order.Number}", $"earn:{order.Id:N}", order.Id, order.Number,
                ExpiresAt: s.PointsExpiryMonths > 0 ? now.AddMonths(s.PointsExpiryMonths) : null), cancellationToken);
            if (posted.IsFailure)
            {
                throw new InvalidOperationException(posted.Error!.Message); // the outbox retries
            }

            if (!posted.Value.Replayed)
            {
                await notifications.NotifyAsync(new NotifyRequest("loyalty.points-earned", order.CustomerMobile, null, order.CustomerId,
                    NotifyVariables.Of(("OrderNumber", order.Number), ("Points", points.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Balance", posted.Value.Balance.ToString(System.Globalization.CultureInfo.InvariantCulture)),
                        ("Name", order.CustomerName)), $"earn:{order.Id:N}"), cancellationToken);
            }
        }

        await referrals.OnOrderDeliveredAsync(order, cancellationToken);
    }

    /// <summary>A return was refunded: take back the matching share of the points the order earned, and (when the whole order is returned) the referral rewards.</summary>
    public async Task OnReturnRefundedAsync(Guid returnId, CancellationToken cancellationToken)
    {
        var ret = await db.ReturnRequests.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().FirstOrDefaultAsync(r => r.Id == returnId, cancellationToken);
        if (ret is null || ret.Status != ReturnStatus.Refunded || await OrderAsync(ret.OrderId, cancellationToken) is not { } order)
        {
            return;
        }

        var s = await loyalty.ReadAsync(cancellationToken);
        if (!s.Enabled)
        {
            return;
        }

        var earned = await db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == order.CustomerId && e.ReferenceKey == $"earn:{order.Id:N}").Select(e => (int?)e.Points).FirstOrDefaultAsync(cancellationToken);
        if (earned is > 0)
        {
            var already = -(await db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == order.CustomerId && e.OrderId == order.Id && e.Kind == PointsEntryKind.Reverse).SumAsync(e => (int?)e.Points, cancellationToken) ?? 0);
            var take = PointsMath.ReverseShare(earned.Value, already, ret.RefundTotal, order.GrandTotal - order.DeliveryCharge);
            if (take > 0)
            {
                var done = await ledger.PostPointsAsync(new PointsPosting(order.CustomerId, PointsEntryKind.Reverse, -take, $"Order {order.Number} returned ({ret.Number})", $"reverse:{ret.Id:N}", order.Id, order.Number, ret.Id,
                    TakeWhatIsLeft: true), cancellationToken);
                if (done.IsFailure)
                {
                    throw new InvalidOperationException(done.Error!.Message);
                }
            }
        }

        await referrals.OnReturnRefundedAsync(order, cancellationToken);
    }

    // ---- Expiry --------------------------------------------------------------------------------------------

    /// <summary>
    /// Expires the points whose date has passed and warns customers whose points expire soon. Safe to run twice or from two servers: each customer is recomputed inside their own lock, so the second run
    /// finds nothing left to expire, and a warning is written once per customer and expiry date.
    /// </summary>
    public async Task<ExpiryRun> ExpireDueAsync(CancellationToken cancellationToken)
    {
        var s = await loyalty.ReadAsync(cancellationToken);
        var now = clock.GetUtcNow();
        int customers = 0, expired = 0, warned = 0;

        var due = await db.PointsLedgerEntries.AsNoTracking().Where(e => e.Points > 0 && e.ExpiresAt != null && e.ExpiresAt <= now)
            .Join(db.LoyaltyAccounts.AsNoTracking().Where(a => a.PointsBalance > 0), e => e.CustomerId, a => a.CustomerId, (e, a) => e.CustomerId).Distinct().Take(BatchSize).ToListAsync(cancellationToken);
        foreach (var customerId in due)
        {
            var points = await ledger.WithCustomerLockAsync(customerId, async ct =>
            {
                var (lots, takenOut, alreadyExpired, balance) = await SumsAsync(customerId, now, ct);
                var take = PointsMath.Expirable(lots, takenOut, alreadyExpired, balance);
                if (take <= 0)
                {
                    return 0;
                }

                var posted = await ledger.PostPointsAsync(new PointsPosting(customerId, PointsEntryKind.Expire, -take, "Points expired", null, ActorName: "System", TakeWhatIsLeft: true), ct);
                return posted.IsSuccess ? -posted.Value.Changed : 0;
            }, cancellationToken);
            if (points > 0)
            {
                customers++;
                expired += points;
            }
        }

        if (s.Enabled && s.ExpiryWarningDays > 0)
        {
            warned = await WarnAsync(now, s.ExpiryWarningDays, cancellationToken);
        }

        return new ExpiryRun(customers, expired, warned);
    }

    private async Task<(long Lots, long TakenOut, long AlreadyExpired, int Balance)> SumsAsync(Guid customerId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var entries = db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == customerId);
        var lots = await entries.Where(e => e.Points > 0 && e.ExpiresAt != null && e.ExpiresAt <= at).SumAsync(e => (long?)e.Points, cancellationToken) ?? 0L;
        var takenOut = -(await entries.Where(e => e.Points < 0 && e.Kind != PointsEntryKind.Expire).SumAsync(e => (long?)e.Points, cancellationToken) ?? 0L);
        var alreadyExpired = -(await entries.Where(e => e.Kind == PointsEntryKind.Expire).SumAsync(e => (long?)e.Points, cancellationToken) ?? 0L);
        var balance = await db.LoyaltyAccounts.AsNoTracking().Where(a => a.CustomerId == customerId).Select(a => a.PointsBalance).FirstOrDefaultAsync(cancellationToken);
        return (lots, takenOut, alreadyExpired, balance);
    }

    /// <summary>How many points of a customer expire by <paramref name="at"/>.</summary>
    public async Task<int> ExpiringByAsync(Guid customerId, DateTimeOffset at, CancellationToken cancellationToken)
    {
        var (lots, takenOut, alreadyExpired, balance) = await SumsAsync(customerId, at, cancellationToken);
        return PointsMath.Expirable(lots, takenOut, alreadyExpired, balance);
    }

    private async Task<int> WarnAsync(DateTimeOffset now, int days, CancellationToken cancellationToken)
    {
        var horizon = now.AddDays(days);
        var soon = await db.PointsLedgerEntries.AsNoTracking().Where(e => e.Points > 0 && e.ExpiresAt != null && e.ExpiresAt > now && e.ExpiresAt <= horizon)
            .Join(db.LoyaltyAccounts.AsNoTracking().Where(a => a.PointsBalance > 0), e => e.CustomerId, a => a.CustomerId, (e, a) => e.CustomerId).Distinct().Take(BatchSize).ToListAsync(cancellationToken);
        var warned = 0;
        foreach (var customerId in soon)
        {
            var points = await ExpiringByAsync(customerId, horizon, cancellationToken);
            if (points <= 0)
            {
                continue;
            }

            var first = await db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == customerId && e.Points > 0 && e.ExpiresAt > now && e.ExpiresAt <= horizon).MinAsync(e => e.ExpiresAt, cancellationToken);
            var customer = await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => new { c.Mobile, c.FullName }).FirstOrDefaultAsync(cancellationToken);
            if (customer is null || first is null)
            {
                continue;
            }

            var key = $"points-expiring:{customerId:N}:{first.Value.UtcDateTime:yyyyMMdd}";
            if (await db.NotificationLogs.AnyAsync(n => n.DedupeKey == key, cancellationToken))
            {
                continue;
            }

            await notifications.NotifyAsync(new NotifyRequest("loyalty.points-expiring", customer.Mobile, null, customerId,
                NotifyVariables.Of(("Points", points.ToString(System.Globalization.CultureInfo.InvariantCulture)), ("Date", first.Value.UtcDateTime.ToString("d MMM yyyy", System.Globalization.CultureInfo.InvariantCulture)), ("Name", customer.FullName)), key),
                cancellationToken);
            warned++;
        }

        return warned;
    }
}

/// <summary>Earns points and decides the referral when an order is Delivered (outbox message <c>domain.OrderStatusChanged</c>, at-least-once and idempotent).</summary>
public sealed class LoyaltyOrderDeliveredHandler(RewardsEngine engine) : IOutboxMessageHandler
{
    public string MessageType => "domain.OrderStatusChanged";

    public Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = JsonSerializer.Deserialize<OrderStatusChanged>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("The OrderStatusChanged message has no content.");
        return e.Track == OrderTrack.Order && e.To == nameof(OrderStatus.Delivered) && e.From != nameof(OrderStatus.ReturnInProgress) ? engine.OnOrderDeliveredAsync(e.OrderId, cancellationToken) : Task.CompletedTask;
    }
}

/// <summary>Takes points (and referral rewards) back when a return is refunded (outbox message <c>domain.ReturnStatusChanged</c>).</summary>
public sealed class LoyaltyReturnRefundedHandler(RewardsEngine engine) : IOutboxMessageHandler
{
    public string MessageType => "domain.ReturnStatusChanged";

    public Task HandleAsync(OutboxMessageContext context, CancellationToken cancellationToken)
    {
        var e = JsonSerializer.Deserialize<ReturnStatusChanged>(context.Payload, JsonSerializerOptions.Web) ?? throw new InvalidOperationException("The ReturnStatusChanged message has no content.");
        return e.To == nameof(ReturnStatus.Refunded) ? engine.OnReturnRefundedAsync(e.ReturnId, cancellationToken) : Task.CompletedTask;
    }
}
