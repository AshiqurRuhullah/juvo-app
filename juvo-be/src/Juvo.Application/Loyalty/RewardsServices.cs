using Juvo.Application.Abstractions;
using Juvo.Application.Ums;
using Juvo.Domain.Loyalty;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Loyalty;

/// <summary>The signed-in customer's rewards (FR-AB01..AB04): balances, ledgers, referral code, gift card redemption, and how much of a cart can be paid with them. Every call needs the feature to be on.</summary>
public sealed class RewardsService(
    IApplicationDbContext db, LoyaltyLedger ledger, LoyaltySettingsService loyalty, RewardsEngine engine, ReferralService referrals, GiftCardService giftCards, ICurrentUser currentUser, TimeProvider clock)
{
    private Guid? CustomerId => currentUser is { IsAuthenticated: true, SubjectType: "customer", UserId: { } id } ? id : null;

    private async Task<Result<(Guid Id, LoyaltySettings Settings)>> GateAsync(CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var s = await loyalty.RequireEnabledAsync(cancellationToken);
        return s.IsFailure ? s.Error! : (id, s.Value);
    }

    public async Task<Result<RewardsSummaryDto>> SummaryAsync(CancellationToken cancellationToken)
    {
        var gate = await GateAsync(cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error!;
        }

        var (id, s) = gate.Value;
        var (points, credit) = await ledger.BalancesAsync(id, cancellationToken);
        var now = clock.GetUtcNow();
        var soon = s.ExpiryWarningDays > 0 ? await engine.ExpiringByAsync(id, now.AddDays(s.ExpiryWarningDays), cancellationToken) : 0;
        var next = soon > 0 ? await db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == id && e.Points > 0 && e.ExpiresAt > now).MinAsync(e => e.ExpiresAt, cancellationToken) : null;
        return new RewardsSummaryDto(true, points, PointsMath.ValueOf(points, s.PointValue), credit, soon, next, new RewardRulesDto(s.EarnRatePerBdt, s.PointValue, s.MinPointsToRedeem, s.MaxTenderPercent, s.PointsExpiryMonths,
            s.ReferralRewardReferrer, s.ReferralRewardReferee));
    }

    public async Task<Result<PagedResult<PointsEntryDto>>> PointsAsync(PageRequest request, CancellationToken cancellationToken)
    {
        var gate = await GateAsync(cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error!;
        }

        var id = gate.Value.Id;
        var query = db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == id);
        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<PointsEntryDto>([.. rows.Select(e => new PointsEntryDto(e.Id, e.Kind, e.Points, e.BalanceAfter, e.Reason, e.OrderNumber, e.ExpiresAt, e.At))], total, page.Page, page.PageSize);
    }

    public async Task<Result<PagedResult<CreditEntryDto>>> CreditAsync(PageRequest request, CancellationToken cancellationToken)
    {
        var gate = await GateAsync(cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error!;
        }

        var id = gate.Value.Id;
        var query = db.CreditLedgerEntries.AsNoTracking().Where(e => e.CustomerId == id);
        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<CreditEntryDto>([.. rows.Select(e => new CreditEntryDto(e.Id, e.Kind, e.Amount, e.BalanceAfter, e.Reason, e.OrderNumber, e.At))], total, page.Page, page.PageSize);
    }

    public async Task<Result<RedeemGiftCardResultDto>> RedeemGiftCardAsync(RedeemGiftCardRequest request, CancellationToken cancellationToken)
    {
        var gate = await GateAsync(cancellationToken);
        return gate.IsFailure ? gate.Error! : await giftCards.RedeemToCreditAsync(gate.Value.Id, request.Code, cancellationToken);
    }

    public async Task<Result<MyReferralDto>> ReferralAsync(CancellationToken cancellationToken)
    {
        var gate = await GateAsync(cancellationToken);
        return gate.IsFailure ? gate.Error! : await referrals.MineAsync(gate.Value.Id, cancellationToken);
    }

    public async Task<Result<MyReferralDto>> ApplyReferralAsync(ApplyReferralRequest request, CancellationToken cancellationToken)
    {
        var gate = await GateAsync(cancellationToken);
        return gate.IsFailure ? gate.Error! : await referrals.ApplyAsync(gate.Value.Id, request.Code, cancellationToken);
    }

    /// <summary>How much of an order of <paramref name="orderTotal"/> the customer can pay with points and store credit right now (the cap, the minimum and the balances applied).</summary>
    public async Task<Result<UsableOnCartDto>> UsableAsync(decimal orderTotal, CancellationToken cancellationToken)
    {
        var gate = await GateAsync(cancellationToken);
        if (gate.IsFailure)
        {
            return gate.Error!;
        }

        if (orderTotal < 0 || orderTotal > 100_000_000m)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["orderTotal"] = ["Enter the order total."] });
        }

        var (id, s) = gate.Value;
        var (points, credit) = await ledger.BalancesAsync(id, cancellationToken);
        var cap = PointsMath.MaxTender(orderTotal, s.MaxTenderPercent);
        var maxPoints = Math.Min(points, PointsMath.MaxPointsFor(cap, s.PointValue));
        if (maxPoints < s.MinPointsToRedeem)
        {
            maxPoints = 0;
        }

        var maxCredit = Math.Min(credit, Math.Max(0m, cap - PointsMath.ValueOf(maxPoints, s.PointValue)));
        return new UsableOnCartDto(true, orderTotal, points, PointsMath.ValueOf(points, s.PointValue), credit, s.PointValue, s.MinPointsToRedeem, s.MaxTenderPercent, cap, maxPoints, maxCredit);
    }
}

/// <summary>Staff side of the rewards (FR-AB01, AB04): settings, a customer's balances and ledgers, and manual adjustments with a reason. Staff screens work while the feature flag is off, so the shop can be set up before it opens.</summary>
public sealed class LoyaltyAdminService(
    IApplicationDbContext db, LoyaltyLedger ledger, LoyaltySettingsService loyalty, RewardsEngine engine, ActorContext actors, ICurrentUser currentUser, TimeProvider clock)
{
    public Task<LoyaltySettings> GetSettingsAsync(CancellationToken cancellationToken) => loyalty.ReadAsync(cancellationToken);

    public Task<Result<LoyaltySettings>> UpdateSettingsAsync(UpdateLoyaltySettingsRequest request, CancellationToken cancellationToken) => loyalty.UpdateAsync(request, cancellationToken);

    private async Task<Result<CustomerRewardsDto>> LoadAsync(Guid customerId, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => new { c.Id, c.Mobile, c.FullName }).FirstOrDefaultAsync(cancellationToken);
        if (customer is null)
        {
            return Error.NotFound(ErrorCodes.Loyalty.CustomerNotFound, "Customer not found.");
        }

        var (points, credit) = await ledger.BalancesAsync(customerId, cancellationToken);
        var (sumPoints, sumCredit) = await ledger.LedgerSumsAsync(customerId, cancellationToken);
        var s = await loyalty.ReadAsync(cancellationToken);
        var soon = s.ExpiryWarningDays > 0 ? await engine.ExpiringByAsync(customerId, clock.GetUtcNow().AddDays(s.ExpiryWarningDays), cancellationToken) : 0;
        return new CustomerRewardsDto(customer.Id, customer.Mobile, customer.FullName, points, credit, sumPoints, sumCredit, points == sumPoints && credit == sumCredit, soon);
    }

    public Task<Result<CustomerRewardsDto>> CustomerAsync(Guid customerId, CancellationToken cancellationToken) => LoadAsync(customerId, cancellationToken);

    public async Task<Result<PagedResult<StaffPointsEntryDto>>> PointsLedgerAsync(Guid customerId, PageRequest request, CancellationToken cancellationToken)
    {
        if (!await db.Customers.AsNoTracking().AnyAsync(c => c.Id == customerId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Loyalty.CustomerNotFound, "Customer not found.");
        }

        var query = db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == customerId);
        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<StaffPointsEntryDto>([.. rows.Select(e => new StaffPointsEntryDto(e.Id, e.Kind, e.Points, e.BalanceAfter, e.Reason, e.OrderNumber, e.ExpiresAt, e.ActorName, e.At))], total, page.Page, page.PageSize);
    }

    public async Task<Result<PagedResult<StaffCreditEntryDto>>> CreditLedgerAsync(Guid customerId, PageRequest request, CancellationToken cancellationToken)
    {
        if (!await db.Customers.AsNoTracking().AnyAsync(c => c.Id == customerId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Loyalty.CustomerNotFound, "Customer not found.");
        }

        var query = db.CreditLedgerEntries.AsNoTracking().Where(e => e.CustomerId == customerId);
        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(e => e.At).ThenByDescending(e => e.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<StaffCreditEntryDto>([.. rows.Select(e => new StaffCreditEntryDto(e.Id, e.Kind, e.Amount, e.BalanceAfter, e.Reason, e.OrderNumber, e.ActorName, e.At))], total, page.Page, page.PageSize);
    }

    /// <summary>
    /// Adds or takes points and store credit with a written reason (FR-AB04). A balance can never go below zero. Each call is its own ledger entry with the staff member's name; <c>idempotencyKey</c>
    /// (the header) makes a repeated call change nothing.
    /// </summary>
    public async Task<Result<AdjustRewardsResultDto>> AdjustAsync(Guid customerId, AdjustRewardsRequest request, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var reason = request.Reason?.Trim();
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrEmpty(reason) || reason.Length > 300)
        {
            errors["reason"] = ["Write the reason for the change (up to 300 characters)."];
        }

        if ((request.Points is null or 0) && (request.Credit is null or 0m))
        {
            errors["points"] = ["Give the points or the credit to add (positive) or take (negative)."];
        }

        if (request.Points is { } p && Math.Abs((long)p) > 1_000_000)
        {
            errors["points"] = ["Use at most 1,000,000 points at a time."];
        }

        if (request.Credit is { } c && (Math.Abs(c) > 1_000_000m || decimal.Round(c, 2) != c))
        {
            errors["credit"] = ["Use at most BDT 1,000,000 with 2 decimals."];
        }

        if (idempotencyKey is { Length: > 80 })
        {
            errors["idempotencyKey"] = ["The idempotency key can have at most 80 characters."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation(errors);
        }

        if (!await db.Customers.AsNoTracking().AnyAsync(x => x.Id == customerId, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Loyalty.CustomerNotFound, "Customer not found.");
        }

        var actor = await actors.GetAsync(cancellationToken);
        var name = currentUser.UserName ?? "Staff";
        var key = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim();
        var now = clock.GetUtcNow();
        var s = await loyalty.ReadAsync(cancellationToken);
        return await ledger.WithCustomerLockAsync<Result<AdjustRewardsResultDto>>(customerId, async ct =>
        {
            // Check both balances first so a refused half leaves nothing behind.
            var (haveP, haveC) = await ledger.BalancesAsync(customerId, ct);
            if (request.Points is < 0 && haveP < -request.Points)
            {
                return Error.Conflict(ErrorCodes.Loyalty.InsufficientPoints, $"The customer has {haveP} points, which is not enough.");
            }

            if (request.Credit is < 0 && haveC < -request.Credit)
            {
                return Error.Conflict(ErrorCodes.Loyalty.InsufficientCredit, $"The customer has BDT {haveC:0.##} of store credit, which is not enough.");
            }

            if (request.Points is { } points && points != 0)
            {
                var done = await ledger.PostPointsAsync(new PointsPosting(customerId, PointsEntryKind.Adjust, points, reason!, key is null ? null : $"adjust:{key}:points", null, null, null,
                    points > 0 && s.PointsExpiryMonths > 0 ? now.AddMonths(s.PointsExpiryMonths) : null, actor?.Id, name), ct);
                if (done.IsFailure)
                {
                    return done.Error!;
                }
            }

            if (request.Credit is { } credit && credit != 0)
            {
                var done = await ledger.PostCreditAsync(new CreditPosting(customerId, CreditEntryKind.Adjust, credit, reason!, key is null ? null : $"adjust:{key}:credit", null, null, null, null, actor?.Id, name), ct);
                if (done.IsFailure)
                {
                    return done.Error!;
                }
            }

            var (pointsBalance, creditBalance) = await ledger.BalancesAsync(customerId, ct);
            return new AdjustRewardsResultDto(pointsBalance, creditBalance);
        }, cancellationToken);
    }
}
