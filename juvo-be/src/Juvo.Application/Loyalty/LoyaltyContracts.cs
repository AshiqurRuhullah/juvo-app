using Juvo.Domain.Loyalty;

namespace Juvo.Application.Loyalty;

// ---- Checkout tender ---------------------------------------------------------------------------------------

/// <summary>
/// What the customer wants to pay an order with besides money (added to the checkout quote and the checkout request). <c>Points</c> are whole points, <c>Credit</c> is BDT of store credit,
/// <c>GiftCardCode</c> pays as much as the card holds (up to what is left to pay). Nothing here changes a price or the order total.
/// </summary>
public sealed record TenderRequest(int Points = 0, decimal Credit = 0m, string? GiftCardCode = null);

/// <summary>The tender as the quote shows it. When <c>Problem</c> is set the tender was refused (a LOYALTY.* code) and the other numbers are zero.</summary>
public sealed record TenderQuoteDto(
    bool Enabled, int PointsUsed, decimal PointsAmount, decimal CreditUsed, decimal GiftCardUsed, decimal TenderTotal, decimal PayableAfterTender, int PointsBalance, decimal CreditBalance,
    string? Problem, string? ProblemMessage);

public sealed record UsableOnCartDto(
    bool Enabled, decimal OrderTotal, int PointsBalance, decimal PointsValue, decimal CreditBalance, decimal PointValue, int MinPointsToRedeem, int MaxTenderPercent, decimal MaxTenderAmount,
    int MaxPoints, decimal MaxCredit);

// ---- Customer (me) -----------------------------------------------------------------------------------------

public sealed record RewardRulesDto(
    decimal EarnRatePerBdt, decimal PointValue, int MinPointsToRedeem, int MaxTenderPercent, int PointsExpiryMonths, decimal ReferralRewardReferrer, decimal ReferralRewardReferee);

public sealed record RewardsSummaryDto(
    bool Enabled, int PointsBalance, decimal PointsValue, decimal CreditBalance, int PointsExpiringSoon, DateTimeOffset? NextPointsExpiry, RewardRulesDto Rules);

public sealed record PointsEntryDto(Guid Id, PointsEntryKind Kind, int Points, int BalanceAfter, string Reason, string? OrderNumber, DateTimeOffset? ExpiresAt, DateTimeOffset At);

public sealed record CreditEntryDto(Guid Id, CreditEntryKind Kind, decimal Amount, decimal BalanceAfter, string Reason, string? OrderNumber, DateTimeOffset At);

public sealed record RedeemGiftCardRequest(string Code);

public sealed record RedeemGiftCardResultDto(decimal Added, decimal CreditBalance);

public sealed record MyReferralDto(
    string Code, int Rewarded, int Pending, int Rejected, decimal CreditEarned, int MaxRewards, string? ReferredByName, ReferralStatus? MyStatus);

public sealed record ApplyReferralRequest(string Code);

// ---- Staff -------------------------------------------------------------------------------------------------

public sealed record CustomerRewardsDto(
    Guid CustomerId, string Mobile, string? Name, int PointsBalance, decimal CreditBalance, int PointsLedgerSum, decimal CreditLedgerSum, bool LedgerConsistent, int PointsExpiringSoon);

public sealed record AdjustRewardsRequest(int? Points, decimal? Credit, string Reason);

public sealed record AdjustRewardsResultDto(int PointsBalance, decimal CreditBalance);

public sealed record StaffPointsEntryDto(
    Guid Id, PointsEntryKind Kind, int Points, int BalanceAfter, string Reason, string? OrderNumber, DateTimeOffset? ExpiresAt, string? ActorName, DateTimeOffset At);

public sealed record StaffCreditEntryDto(
    Guid Id, CreditEntryKind Kind, decimal Amount, decimal BalanceAfter, string Reason, string? OrderNumber, string? ActorName, DateTimeOffset At);

public sealed record CreateGiftCardRequest(decimal Amount, DateTimeOffset? ExpiresAt, string? Note, string? RecipientMobile, string? RecipientEmail, bool SendToRecipient);

/// <summary><c>Code</c> is shown only in the answer to the create call and never again.</summary>
public sealed record GiftCardCreatedDto(GiftCardDto Card, string Code);

public sealed record GiftCardDto(
    Guid Id, string CodeHint, decimal InitialAmount, decimal Balance, string Status, DateTimeOffset? ExpiresAt, string? Note, string? RecipientMobile, string? CreatedByName, DateTimeOffset CreatedAt,
    DateTimeOffset? RedeemedAt);

public sealed record GiftCardEntryDto(Guid Id, GiftCardEntryKind Kind, decimal Amount, decimal BalanceAfter, string? OrderNumber, string? ActorName, string? Note, DateTimeOffset At);

public sealed record GiftCardDetailDto(GiftCardDto Card, IReadOnlyList<GiftCardEntryDto> Entries);

public sealed record GiftCardListQuery(string? Status);

public sealed record ReferralListItemDto(
    Guid Id, Guid ReferrerId, string? ReferrerName, Guid RefereeId, string? RefereeName, string CodeUsed, ReferralStatus Status, string? Note, decimal ReferrerReward, decimal RefereeReward,
    DateTimeOffset AppliedAt, DateTimeOffset? DecidedAt);

public sealed record ReferralListQuery(ReferralStatus? Status);
