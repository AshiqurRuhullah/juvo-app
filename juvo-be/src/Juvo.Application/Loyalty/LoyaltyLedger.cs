using Juvo.Application.Abstractions;
using Juvo.Domain.Loyalty;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Loyalty;

/// <summary>What to write into the points ledger. <c>Points</c> is signed. <c>Key</c> makes the posting idempotent (the same key for the same customer is applied once).</summary>
public sealed record PointsPosting(
    Guid CustomerId, PointsEntryKind Kind, int Points, string Reason, string? Key, Guid? OrderId = null, string? OrderNumber = null, Guid? ReturnRequestId = null, DateTimeOffset? ExpiresAt = null,
    Guid? ActorId = null, string? ActorName = null, bool TakeWhatIsLeft = false);

public sealed record CreditPosting(
    Guid CustomerId, CreditEntryKind Kind, decimal Amount, string Reason, string? Key, Guid? OrderId = null, string? OrderNumber = null, Guid? ReturnRequestId = null, Guid? GiftCardId = null,
    Guid? ActorId = null, string? ActorName = null, bool TakeWhatIsLeft = false);

/// <summary>The effect of a posting: what really changed (0 when nothing did), the balance after it, and whether the key had been used before (nothing was changed then).</summary>
public sealed record PointsResult(int Changed, int Balance, bool Replayed);

public sealed record CreditResult(decimal Changed, decimal Balance, bool Replayed);

/// <summary>
/// The only writer of the points, store credit and gift card ledgers (FR-AB04). Every posting runs in one transaction, takes an application lock for the customer (or the card), checks
/// the balance, updates the running balance and appends the ledger entry. Three layers stop a double spend: the lock (one writer at a time), the row version on the balance row
/// (a stale write fails), and the database (a unique index on customer + key, and a check that the balance is never negative).
/// </summary>
public sealed class LoyaltyLedger(IApplicationDbContext db, IUnitOfWork uow, TimeProvider clock)
{
    private static string Lock(Guid customerId) => $"loyalty:{customerId:N}";

    private async Task<LoyaltyAccount> AccountAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var account = await db.LoyaltyAccounts.FirstOrDefaultAsync(a => a.CustomerId == customerId, cancellationToken);
        if (account is null)
        {
            account = new LoyaltyAccount(customerId);
            db.LoyaltyAccounts.Add(account);
        }

        return account;
    }

    /// <summary>The account row, created when missing. The caller must already be inside the customer's lock.</summary>
    internal Task<LoyaltyAccount> AccountLockedAsync(Guid customerId, CancellationToken cancellationToken) => AccountAsync(customerId, cancellationToken);

    /// <summary>Runs <paramref name="work"/> in a transaction while holding the customer's lock (also used for gift card code failures).</summary>
    public Task<T> WithCustomerLockAsync<T>(Guid customerId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        uow.ExecuteInTransactionAsync(async ct =>
        {
            await db.AcquireTransactionLockAsync(Lock(customerId), ct);
            return await work(ct);
        }, cancellationToken);

    public Task<T> WithCardLockAsync<T>(Guid cardId, Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) =>
        uow.ExecuteInTransactionAsync(async ct =>
        {
            await db.AcquireTransactionLockAsync($"giftcard:{cardId:N}", ct);
            return await work(ct);
        }, cancellationToken);

    public Task<Result<PointsResult>> PostPointsAsync(PointsPosting p, CancellationToken cancellationToken) =>
        WithCustomerLockAsync<Result<PointsResult>>(p.CustomerId, async ct =>
        {
            if (p.Points == 0)
            {
                return Error.Validation(new Dictionary<string, string[]> { ["points"] = ["The points change cannot be zero."] });
            }

            if (p.Key is not null)
            {
                var previous = await db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == p.CustomerId && e.ReferenceKey == p.Key).Select(e => new { e.Points, e.BalanceAfter }).FirstOrDefaultAsync(ct);
                if (previous is not null)
                {
                    return new PointsResult(previous.Points, previous.BalanceAfter, true);
                }
            }

            var account = await AccountAsync(p.CustomerId, ct);
            var delta = p.Points;
            if (delta < 0 && account.PointsBalance < -delta)
            {
                if (!p.TakeWhatIsLeft)
                {
                    return Error.Conflict(ErrorCodes.Loyalty.InsufficientPoints, $"The customer has {account.PointsBalance} points, which is not enough.");
                }

                delta = -account.PointsBalance;
                if (delta == 0)
                {
                    return new PointsResult(0, 0, false);
                }
            }

            var after = account.ApplyPoints(delta);
            db.PointsLedgerEntries.Add(new PointsLedgerEntry(p.CustomerId, p.Kind, delta, after, p.Reason, p.Key, p.OrderId, p.OrderNumber, p.ReturnRequestId, delta > 0 ? p.ExpiresAt : null, p.ActorId, p.ActorName,
                clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return new PointsResult(delta, after, false);
        }, cancellationToken);

    public Task<Result<CreditResult>> PostCreditAsync(CreditPosting p, CancellationToken cancellationToken) =>
        WithCustomerLockAsync<Result<CreditResult>>(p.CustomerId, async ct =>
        {
            if (p.Amount == 0 || decimal.Round(p.Amount, 2) != p.Amount)
            {
                return Error.Validation(new Dictionary<string, string[]> { ["amount"] = ["Use an amount that is not zero, with at most 2 decimals."] });
            }

            if (p.Key is not null)
            {
                var previous = await db.CreditLedgerEntries.AsNoTracking().Where(e => e.CustomerId == p.CustomerId && e.ReferenceKey == p.Key).Select(e => new { e.Amount, e.BalanceAfter }).FirstOrDefaultAsync(ct);
                if (previous is not null)
                {
                    return new CreditResult(previous.Amount, previous.BalanceAfter, true);
                }
            }

            var account = await AccountAsync(p.CustomerId, ct);
            var delta = p.Amount;
            if (delta < 0 && account.CreditBalance < -delta)
            {
                if (!p.TakeWhatIsLeft)
                {
                    return Error.Conflict(ErrorCodes.Loyalty.InsufficientCredit, $"The customer has BDT {account.CreditBalance:0.##} of store credit, which is not enough.");
                }

                delta = -account.CreditBalance;
                if (delta == 0)
                {
                    return new CreditResult(0m, 0m, false);
                }
            }

            var after = account.ApplyCredit(delta);
            db.CreditLedgerEntries.Add(new CreditLedgerEntry(p.CustomerId, p.Kind, delta, after, p.Reason, p.Key, p.OrderId, p.OrderNumber, p.ReturnRequestId, p.GiftCardId, p.ActorId, p.ActorName, clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
            return new CreditResult(delta, after, false);
        }, cancellationToken);

    /// <summary>Takes <paramref name="amount"/> off a gift card (a payment at checkout, or the move into store credit). One lock per card, idempotent by key.</summary>
    public Task<Result<decimal>> SpendGiftCardAsync(
        Guid cardId, decimal amount, GiftCardEntryKind kind, string key, Guid? customerId, Guid? orderId, string? orderNumber, string? actorName, string? note, CancellationToken cancellationToken) =>
        uow.ExecuteInTransactionAsync<Result<decimal>>(async ct =>
        {
            await db.AcquireTransactionLockAsync($"giftcard:{cardId:N}", ct);
            var previous = await db.GiftCardEntries.AsNoTracking().Where(e => e.GiftCardId == cardId && e.ReferenceKey == key).Select(e => (decimal?)e.BalanceAfter).FirstOrDefaultAsync(ct);
            if (previous is not null)
            {
                return previous.Value;
            }

            var card = await db.GiftCards.FirstOrDefaultAsync(c => c.Id == cardId, ct);
            if (card is null)
            {
                return Error.NotFound(ErrorCodes.Loyalty.GiftCardNotFound, "Gift card not found.");
            }

            var now = clock.GetUtcNow();
            if (!card.IsActive)
            {
                return Error.Conflict(ErrorCodes.Loyalty.GiftCardInactive, "This gift card has been switched off.");
            }

            if (card.IsExpired(now))
            {
                return Error.Conflict(ErrorCodes.Loyalty.GiftCardExpired, "This gift card has expired.");
            }

            if (amount <= 0 || amount > card.Balance)
            {
                return Error.Conflict(ErrorCodes.Loyalty.GiftCardEmpty, $"The gift card holds BDT {card.Balance:0.##}, which is not enough.");
            }

            var after = card.ApplyChange(-amount);
            db.GiftCardEntries.Add(new GiftCardEntry(cardId, kind, -amount, after, key, customerId, orderId, orderNumber, actorName, note, now));
            await db.SaveChangesAsync(ct);
            return after;
        }, cancellationToken);

    // ---- Reading ------------------------------------------------------------------------------------------

    public async Task<(int Points, decimal Credit)> BalancesAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var row = await db.LoyaltyAccounts.AsNoTracking().Where(a => a.CustomerId == customerId).Select(a => new { a.PointsBalance, a.CreditBalance }).FirstOrDefaultAsync(cancellationToken);
        return row is null ? (0, 0m) : (row.PointsBalance, row.CreditBalance);
    }

    /// <summary>The balances rebuilt from the ledgers. They always equal <see cref="BalancesAsync"/>; staff screens and the tests compare the two.</summary>
    public async Task<(int Points, decimal Credit)> LedgerSumsAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var points = await db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == customerId).SumAsync(e => (int?)e.Points, cancellationToken) ?? 0;
        var credit = await db.CreditLedgerEntries.AsNoTracking().Where(e => e.CustomerId == customerId).SumAsync(e => (decimal?)e.Amount, cancellationToken) ?? 0m;
        return (points, credit);
    }
}
