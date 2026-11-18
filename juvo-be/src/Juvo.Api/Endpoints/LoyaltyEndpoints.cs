using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Loyalty;
using Juvo.Domain.Loyalty;
using Juvo.Shared.Pagination;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Mvc;

namespace Juvo.Api.Endpoints;

/// <summary>
/// Rewards (module AB): points, store credit, gift cards and referrals. The customer side (<c>/me/rewards</c>) needs a customer token and answers 409 <c>LOYALTY.FEATURE_OFF</c> while the
/// <c>Loyalty.Enabled</c> setting is off. The staff side (<c>/loyalty</c>) names its permission on every endpoint and keeps working while the feature is off, so it can be set up first.
/// Customers are shared by every branch, so the permissions are global.
/// </summary>
internal static class LoyaltyEndpoints
{
    private const string IdempotencyHeader = "Idempotency-Key";

    public static WebApplication MapLoyaltyEndpoints(this WebApplication app)
    {
        var v1 = app.MapApiV1();
        MapMe(v1.MapGroup("/me/rewards").WithTags("Customer rewards").RequireAuthorization(Policies.Customer));
        MapStaff(v1.MapGroup("/loyalty").WithTags("Rewards (staff)"));
        return app;
    }

    private static void MapMe(RouteGroupBuilder g)
    {
        g.MapGet("", async (RewardsService s, CancellationToken ct) => (await s.SummaryAsync(ct)).ToHttpResult())
            .Produces<RewardsSummaryDto>().WithSummary("My points, store credit, the points that expire soon and the rules (earn rate, point value, minimum, cap).");

        g.MapGet("/points", async ([AsParameters] ListParams list, RewardsService s, CancellationToken ct) => (await s.PointsAsync(list.ToRequest(), ct)).ToHttpResult())
            .Produces<PagedResult<PointsEntryDto>>().WithSummary("My points ledger, newest first.");

        g.MapGet("/credit", async ([AsParameters] ListParams list, RewardsService s, CancellationToken ct) => (await s.CreditAsync(list.ToRequest(), ct)).ToHttpResult())
            .Produces<PagedResult<CreditEntryDto>>().WithSummary("My store credit ledger, newest first.");

        g.MapGet("/usable", async (decimal orderTotal, RewardsService s, CancellationToken ct) => (await s.UsableAsync(orderTotal, ct)).ToHttpResult())
            .Produces<UsableOnCartDto>().WithSummary("How many points and how much store credit I can use on an order of this total right now (balances, minimum and the share cap applied).");

        g.MapPost("/gift-cards/redeem", async (RedeemGiftCardRequest r, RewardsService s, CancellationToken ct) => (await s.RedeemGiftCardAsync(r, ct)).ToHttpResult())
            .RequireRateLimiting(RateLimitSettings.CheckoutPolicy).Produces<RedeemGiftCardResultDto>()
            .WithSummary("Move the whole balance of a gift card into my store credit. Wrong codes are counted: 5 in 15 minutes lock code entry for 15 minutes (LOYALTY.CODE_LOCKED).");

        g.MapGet("/referral", async (RewardsService s, CancellationToken ct) => (await s.ReferralAsync(ct)).ToHttpResult())
            .Produces<MyReferralDto>().WithSummary("My referral code (made on the first call), how many friends were rewarded, and who referred me.");

        g.MapPost("/referral/apply", async (ApplyReferralRequest r, RewardsService s, CancellationToken ct) => (await s.ApplyReferralAsync(r, ct)).ToHttpResult())
            .RequireRateLimiting(RateLimitSettings.CheckoutPolicy).Produces<MyReferralDto>()
            .WithSummary("Enter a friend's referral code before my first order. The reward comes after my first order is delivered.");
    }

    private static void MapStaff(RouteGroupBuilder g)
    {
        g.MapGet("/settings", async (LoyaltyAdminService s, CancellationToken ct) => Results.Ok(await s.GetSettingsAsync(ct)))
            .RequirePermission(Permissions.Loyalty.PointsView).Produces<LoyaltySettings>().WithSummary("The rewards settings (the feature flag, earn rate, point value, limits, expiry, referral rewards, gift card limits).");

        g.MapPut("/settings", async (UpdateLoyaltySettingsRequest r, LoyaltyAdminService s, CancellationToken ct) => (await s.UpdateSettingsAsync(r, ct)).ToHttpResult())
            .RequirePermission(Permissions.Loyalty.PointsAdjust).Produces<LoyaltySettings>().WithSummary("Change the rewards settings (only the fields sent). Every change is written to the audit log.");

        g.MapGet("/customers/{customerId:guid}", async (Guid customerId, LoyaltyAdminService s, CancellationToken ct) => (await s.CustomerAsync(customerId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Loyalty.PointsView).Produces<CustomerRewardsDto>().WithSummary("A customer's points and store credit balances, the sums of their ledgers and whether the two agree.");

        g.MapGet("/customers/{customerId:guid}/points", async (Guid customerId, [AsParameters] ListParams list, LoyaltyAdminService s, CancellationToken ct) =>
                (await s.PointsLedgerAsync(customerId, list.ToRequest(), ct)).ToHttpResult())
            .RequirePermission(Permissions.Loyalty.PointsView).Produces<PagedResult<StaffPointsEntryDto>>().WithSummary("A customer's points ledger.");

        g.MapGet("/customers/{customerId:guid}/credit", async (Guid customerId, [AsParameters] ListParams list, LoyaltyAdminService s, CancellationToken ct) =>
                (await s.CreditLedgerAsync(customerId, list.ToRequest(), ct)).ToHttpResult())
            .RequirePermission(Permissions.Loyalty.PointsView).Produces<PagedResult<StaffCreditEntryDto>>().WithSummary("A customer's store credit ledger.");

        g.MapPost("/customers/{customerId:guid}/adjust", async (Guid customerId, [FromHeader(Name = IdempotencyHeader)] string? key, AdjustRewardsRequest r, LoyaltyAdminService s, CancellationToken ct) =>
                (await s.AdjustAsync(customerId, r, key, ct)).ToHttpResult())
            .RequirePermission(Permissions.Loyalty.PointsAdjust).Produces<AdjustRewardsResultDto>()
            .WithSummary("Add (positive) or take (negative) points and/or store credit with a written reason. A balance never goes below zero. Send Idempotency-Key to make retries safe.");

        g.MapGet("/gift-cards", async ([AsParameters] ListParams list, string? status, GiftCardService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new GiftCardListQuery(status), ct)).ToHttpResult())
            .RequirePermission(Permissions.Loyalty.GiftCardView).Produces<PagedResult<GiftCardDto>>().WithSummary("Gift cards, newest first. status: active, used, expired, deactivated. The code is never shown.");

        g.MapGet("/gift-cards/{id:guid}", async (Guid id, GiftCardService s, CancellationToken ct) => (await s.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Loyalty.GiftCardView).Produces<GiftCardDetailDto>().WithSummary("One gift card with its balance history.");

        g.MapPost("/gift-cards", async (CreateGiftCardRequest r, GiftCardService s, CancellationToken ct) =>
                (await s.CreateAsync(r, ct)).ToHttpResult(v => Results.Created($"/api/v1/loyalty/gift-cards/{v.Card.Id}", v)))
            .RequirePermission(Permissions.Loyalty.GiftCardCreate).Produces<GiftCardCreatedDto>(StatusCodes.Status201Created)
            .WithSummary("Create a gift card. The code is in this answer only (and in the message to the recipient when sendToRecipient is true); it is stored as a hash.");

        g.MapPost("/gift-cards/{id:guid}/deactivate", async (Guid id, GiftCardService s, CancellationToken ct) => (await s.DeactivateAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Loyalty.GiftCardCreate).Produces<GiftCardDto>().WithSummary("Switch a gift card off. Its balance can no longer be used.");

        g.MapGet("/referrals", async ([AsParameters] ListParams list, ReferralStatus? status, ReferralService s, CancellationToken ct) =>
                (await s.ListAsync(list.ToRequest(), new ReferralListQuery(status), ct)).ToHttpResult())
            .RequirePermission(Permissions.Loyalty.ReferralView).Produces<PagedResult<ReferralListItemDto>>().WithSummary("Referrals with their status (Pending, Rewarded, Rejected with the reason, Reversed).");
    }
}
