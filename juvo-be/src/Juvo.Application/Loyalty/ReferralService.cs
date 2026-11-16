using System.Security.Cryptography;
using Juvo.Application.Abstractions;
using Juvo.Application.Notifications;
using Juvo.Application.Orders;
using Juvo.Domain.Loyalty;
using Juvo.Domain.Orders;
using Juvo.Domain.Returns;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Loyalty;

/// <summary>
/// Referrals (FR-AB02). A customer has one code, made on the first request (8 characters from an alphabet without look-alikes, about 40 bits, so it cannot be guessed by trying). A new
/// customer enters it before the first order. Both sides get their reward (store credit) only after the first order of the new customer is Delivered, and only if the simple fraud rules pass;
/// if that order is later returned in full, the rewards are taken back (as far as the balance allows).
/// </summary>
public sealed class ReferralService(IApplicationDbContext db, LoyaltyLedger ledger, LoyaltySettingsService loyalty, NotificationService notifications, TimeProvider clock)
{
    private const string Alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    private static string NewCode()
    {
        var chars = new char[8];
        for (var i = 0; i < chars.Length; i++)
        {
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        }

        return new string(chars);
    }

    private static string Normalize(string? code) => GiftCardCodes.Normalize(code);

    /// <summary>The place an order goes to: the delivery address (line, area, district, city), normalised and hashed. Two orders to the same address are the same household, whatever the phone.</summary>
    internal static string HouseholdOf(string addressLine, string? area, Guid? districtId, string? city)
    {
        static string Clean(string? s) => new((s ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return OrderPlacement.Hash($"{Clean(addressLine)}|{Clean(area)}|{districtId:N}|{Clean(city)}");
    }

    // ---- The customer's own code ----------------------------------------------------------------------

    public async Task<string> CodeOfAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var existing = await db.ReferralCodes.AsNoTracking().Where(c => c.CustomerId == customerId).Select(c => c.Code).FirstOrDefaultAsync(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        return await ledger.WithCustomerLockAsync(customerId, async ct =>
        {
            var again = await db.ReferralCodes.AsNoTracking().Where(c => c.CustomerId == customerId).Select(c => c.Code).FirstOrDefaultAsync(ct);
            if (again is not null)
            {
                return again;
            }

            for (var attempt = 0; attempt < 8; attempt++)
            {
                var code = NewCode();
                if (await db.ReferralCodes.AnyAsync(c => c.Code == code, ct))
                {
                    continue;
                }

                db.ReferralCodes.Add(new ReferralCode(customerId, code));
                await db.SaveChangesAsync(ct);
                return code;
            }

            throw new InvalidOperationException("A free referral code could not be made.");
        }, cancellationToken);
    }

    public async Task<MyReferralDto> MineAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var s = await loyalty.ReadAsync(cancellationToken);
        var code = await CodeOfAsync(customerId, cancellationToken);
        var mine = await db.Referrals.AsNoTracking().Where(r => r.ReferrerId == customerId).Select(r => new { r.Status, r.ReferrerReward }).ToListAsync(cancellationToken);
        var referredBy = await db.Referrals.AsNoTracking().Where(r => r.RefereeId == customerId).Select(r => new { r.Status, r.ReferrerId }).FirstOrDefaultAsync(cancellationToken);
        string? referrerName = null;
        if (referredBy is not null)
        {
            referrerName = await db.Customers.AsNoTracking().Where(c => c.Id == referredBy.ReferrerId).Select(c => c.FullName).FirstOrDefaultAsync(cancellationToken);
        }

        return new MyReferralDto(code, mine.Count(r => r.Status == ReferralStatus.Rewarded), mine.Count(r => r.Status == ReferralStatus.Pending), mine.Count(r => r.Status == ReferralStatus.Rejected),
            mine.Where(r => r.Status == ReferralStatus.Rewarded).Sum(r => r.ReferrerReward), s.ReferralMaxRewardsPerReferrer, referrerName, referredBy?.Status);
    }

    // ---- Entering a code -------------------------------------------------------------------------------

    public async Task<Result<MyReferralDto>> ApplyAsync(Guid customerId, string? code, CancellationToken cancellationToken)
    {
        if ((await loyalty.RequireEnabledAsync(cancellationToken)).IsFailure)
        {
            return LoyaltySettingsService.FeatureOff();
        }

        var now = clock.GetUtcNow();
        var locked = await db.LoyaltyAccounts.AsNoTracking().Where(a => a.CustomerId == customerId).Select(a => a.CodeLockedUntil).FirstOrDefaultAsync(cancellationToken);
        if (locked is { } until && until > now)
        {
            return Error.Conflict(ErrorCodes.Loyalty.CodeLocked, "Too many wrong codes. Try again in a few minutes.");
        }

        var normalized = Normalize(code);
        var owner = normalized.Length == 0 ? null : await db.ReferralCodes.AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalized, cancellationToken);
        if (owner is null)
        {
            await ledger.WithCustomerLockAsync(customerId, async ct =>
            {
                (await ledger.AccountLockedAsync(customerId, ct)).RecordCodeFailure(now, GiftCardService.FailureLimit, GiftCardService.FailureWindow, GiftCardService.LockFor);
                await db.SaveChangesAsync(ct);
                return true;
            }, cancellationToken);
            return Error.NotFound(ErrorCodes.Loyalty.ReferralCodeInvalid, "This referral code is not valid.");
        }

        if (owner.CustomerId == customerId)
        {
            return Error.Conflict(ErrorCodes.Loyalty.ReferralSelf, "You cannot use your own referral code.");
        }

        if (await db.Referrals.AnyAsync(r => r.RefereeId == customerId, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Loyalty.ReferralAlreadyApplied, "A referral code was already used on this account.");
        }

        if (await db.Orders.IgnoreQueryFilters(["BranchScope"]).AnyAsync(o => o.CustomerId == customerId && o.Status != OrderStatus.Cancelled, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Loyalty.ReferralNotEligible, "A referral code can only be used before your first order.");
        }

        var referrerActive = await db.Customers.AsNoTracking().AnyAsync(c => c.Id == owner.CustomerId && c.IsActive, cancellationToken);
        if (!referrerActive)
        {
            return Error.NotFound(ErrorCodes.Loyalty.ReferralCodeInvalid, "This referral code is not valid.");
        }

        db.Referrals.Add(new Referral(owner.CustomerId, customerId, normalized, now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            db.DiscardUnsavedAdditions(); // the unique index on the referred customer: a simultaneous call won
            return Error.Conflict(ErrorCodes.Loyalty.ReferralAlreadyApplied, "A referral code was already used on this account.");
        }

        return await MineAsync(customerId, cancellationToken);
    }

    // ---- The first delivered order -----------------------------------------------------------------------

    /// <summary>
    /// The new customer's order was delivered. Decides the referral: not the first order, same household or phone as the referrer, a household that was already rewarded, a referrer at the
    /// limit or deactivated: Rejected with the reason. Otherwise both sides receive their store credit. Idempotent (a decided referral is left alone; the ledger keys repeat safely).
    /// </summary>
    public async Task OnOrderDeliveredAsync(Order order, CancellationToken cancellationToken)
    {
        var s = await loyalty.ReadAsync(cancellationToken);
        if (!s.Enabled)
        {
            return;
        }

        var referral = await db.Referrals.FirstOrDefaultAsync(r => r.RefereeId == order.CustomerId && r.Status == ReferralStatus.Pending, cancellationToken);
        if (referral is null)
        {
            return;
        }

        var now = clock.GetUtcNow();
        var household = HouseholdOf(order.ShipAddressLine, order.ShipArea, order.ShipDistrictId, order.ShipCity);
        string? reject = null;
        var earlier = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AnyAsync(o => o.CustomerId == order.CustomerId && o.Id != order.Id && o.Status != OrderStatus.Cancelled && o.PlacedAt < order.PlacedAt, cancellationToken);
        var referrer = await db.Customers.AsNoTracking().Where(c => c.Id == referral.ReferrerId).Select(c => new { c.Mobile, c.IsActive }).FirstOrDefaultAsync(cancellationToken);
        if (earlier)
        {
            reject = "The delivered order is not the first order of the new customer.";
        }
        else if (referrer is null || !referrer.IsActive)
        {
            reject = "The referrer is not an active customer.";
        }
        else if (order.ShipPhone == referrer.Mobile || order.CustomerMobile == referrer.Mobile)
        {
            reject = "The order was sent to the mobile number of the referrer.";
        }
        else
        {
            var theirs = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(o => o.CustomerId == referral.ReferrerId)
                .Select(o => new { o.ShipPhone, o.ShipAddressLine, o.ShipArea, o.ShipDistrictId, o.ShipCity }).Take(500).ToListAsync(cancellationToken);
            if (theirs.Any(o => o.ShipPhone == order.ShipPhone))
            {
                reject = "The order was sent to a phone number the referrer used for their own orders.";
            }
            else if (theirs.Any(o => HouseholdOf(o.ShipAddressLine, o.ShipArea, o.ShipDistrictId, o.ShipCity) == household))
            {
                reject = "The order was sent to the same address as an order of the referrer.";
            }
            else if (await db.Referrals.AnyAsync(r => r.Id != referral.Id && r.HouseholdKey == household && r.Status == ReferralStatus.Rewarded, cancellationToken))
            {
                reject = "The same address was already rewarded for another referral.";
            }
            else if (s.ReferralMaxRewardsPerReferrer > 0 && await db.Referrals.CountAsync(r => r.ReferrerId == referral.ReferrerId && r.Status == ReferralStatus.Rewarded, cancellationToken) >= s.ReferralMaxRewardsPerReferrer)
            {
                reject = "The referrer reached the limit of rewarded referrals.";
            }
        }

        if (reject is not null)
        {
            referral.Reject(reject, order.Id, household, now);
            await db.SaveChangesAsync(cancellationToken);
            return;
        }

        var referrerName = await db.Customers.AsNoTracking().Where(c => c.Id == referral.ReferrerId).Select(c => c.FullName).FirstOrDefaultAsync(cancellationToken);
        var refereeName = order.CustomerName;
        foreach (var (customerId, amount, role) in new[] { (referral.ReferrerId, s.ReferralRewardReferrer, "referrer"), (referral.RefereeId, s.ReferralRewardReferee, "referee") })
        {
            if (amount <= 0)
            {
                continue;
            }

            var posted = await ledger.PostCreditAsync(new CreditPosting(customerId, CreditEntryKind.ReferralReward, amount, role == "referrer" ? "Referral reward: your friend's first order arrived" : "Welcome reward for joining with a referral code",
                $"referral:{referral.Id:N}:{role}", order.Id, order.Number), cancellationToken);
            if (posted.IsFailure)
            {
                throw new InvalidOperationException(posted.Error!.Message); // the outbox retries
            }
        }

        referral.Reward(order.Id, s.ReferralRewardReferrer, s.ReferralRewardReferee, household, now);
        await db.SaveChangesAsync(cancellationToken);

        var mobiles = await db.Customers.AsNoTracking().Where(c => c.Id == referral.ReferrerId || c.Id == referral.RefereeId).Select(c => new { c.Id, c.Mobile }).ToDictionaryAsync(c => c.Id, c => c.Mobile, cancellationToken);
        if (s.ReferralRewardReferrer > 0 && mobiles.TryGetValue(referral.ReferrerId, out var referrerMobile))
        {
            await notifications.NotifyAsync(new NotifyRequest("loyalty.referral-reward", referrerMobile, null, referral.ReferrerId,
                NotifyVariables.Of(("Amount", NotifyVariables.Money(s.ReferralRewardReferrer)), ("Friend", refereeName), ("Name", referrerName)), $"referral:{referral.Id:N}:referrer"), cancellationToken);
        }

        if (s.ReferralRewardReferee > 0 && mobiles.TryGetValue(referral.RefereeId, out var refereeMobile))
        {
            await notifications.NotifyAsync(new NotifyRequest("loyalty.referral-welcome", refereeMobile, null, referral.RefereeId,
                NotifyVariables.Of(("Amount", NotifyVariables.Money(s.ReferralRewardReferee)), ("Name", refereeName)), $"referral:{referral.Id:N}:referee"), cancellationToken);
        }
    }

    /// <summary>A return of the qualifying order was refunded. When the refunds now cover the whole goods value, the rewards are taken back (as far as each balance allows) and the referral is Reversed.</summary>
    public async Task OnReturnRefundedAsync(Order order, CancellationToken cancellationToken)
    {
        var referral = await db.Referrals.FirstOrDefaultAsync(r => r.QualifyingOrderId == order.Id && r.Status == ReferralStatus.Rewarded, cancellationToken);
        if (referral is null)
        {
            return;
        }

        var refunded = await db.ReturnRequests.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Where(r => r.OrderId == order.Id && r.Status == ReturnStatus.Refunded).SumAsync(r => (decimal?)r.RefundTotal, cancellationToken) ?? 0m;
        if (refunded < order.GrandTotal - order.DeliveryCharge)
        {
            return;
        }

        foreach (var (customerId, amount, role) in new[] { (referral.ReferrerId, referral.ReferrerReward, "referrer"), (referral.RefereeId, referral.RefereeReward, "referee") })
        {
            if (amount <= 0)
            {
                continue;
            }

            var done = await ledger.PostCreditAsync(new CreditPosting(customerId, CreditEntryKind.ReferralClawback, -amount, "Referral reward taken back: the order was returned",
                $"referral-clawback:{referral.Id:N}:{role}", order.Id, order.Number, TakeWhatIsLeft: true), cancellationToken);
            if (done.IsFailure)
            {
                throw new InvalidOperationException(done.Error!.Message);
            }
        }

        referral.MarkReversed("The qualifying order was returned and refunded.", clock.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Staff -------------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<ReferralListItemDto>>> ListAsync(PageRequest request, ReferralListQuery filter, CancellationToken cancellationToken)
    {
        var query = db.Referrals.AsNoTracking().AsQueryable();
        if (filter.Status is { } status)
        {
            query = query.Where(r => r.Status == status);
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(r => r.AppliedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var ids = rows.SelectMany(r => new[] { r.ReferrerId, r.RefereeId }).Distinct().ToList();
        var names = await db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.FullName ?? "Customer", cancellationToken);
        return new PagedResult<ReferralListItemDto>([.. rows.Select(r => new ReferralListItemDto(r.Id, r.ReferrerId, names.GetValueOrDefault(r.ReferrerId), r.RefereeId, names.GetValueOrDefault(r.RefereeId), r.CodeUsed, r.Status, r.Note,
            r.ReferrerReward, r.RefereeReward, r.AppliedAt, r.DecidedAt))], total, page.Page, page.PageSize);
    }
}
