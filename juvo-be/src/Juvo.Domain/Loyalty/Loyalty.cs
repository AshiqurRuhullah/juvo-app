using Juvo.Domain.Common;

namespace Juvo.Domain.Loyalty;

/// <summary>Why the points balance of a customer changed. The sign of the entry says the direction.</summary>
public enum PointsEntryKind
{
    /// <summary>Points earned by a delivered order (plus).</summary>
    Earn = 1,

    /// <summary>Points used to pay part of an order at checkout (minus).</summary>
    Redeem = 2,

    /// <summary>Points that went back because an order was cancelled or its tender refunded (plus).</summary>
    Release = 3,

    /// <summary>Earned points taken back because the order was returned and refunded (minus).</summary>
    Reverse = 4,

    /// <summary>Points that passed their expiry date (minus).</summary>
    Expire = 5,

    /// <summary>A manual correction by staff, with a reason (plus or minus).</summary>
    Adjust = 6,

    /// <summary>Reward points (plus). Reserved: referral rewards are paid as store credit.</summary>
    ReferralReward = 7,
}

/// <summary>Why the store credit balance of a customer changed.</summary>
public enum CreditEntryKind
{
    /// <summary>A gift card code moved into store credit (plus).</summary>
    GiftCardRedeem = 1,

    /// <summary>Store credit used to pay part of an order (minus).</summary>
    Spend = 2,

    /// <summary>Credit that went back because an order was cancelled (plus).</summary>
    Release = 3,

    /// <summary>A refund the customer took as store credit (plus).</summary>
    Refund = 4,

    /// <summary>A manual correction by staff, with a reason (plus or minus).</summary>
    Adjust = 5,

    /// <summary>The referral reward of either side (plus).</summary>
    ReferralReward = 6,

    /// <summary>A referral reward taken back because the qualifying order was returned (minus).</summary>
    ReferralClawback = 7,
}

/// <summary>
/// The balances of one customer. The numbers here are the running totals of the append-only ledgers: every change is written together with its ledger entry in one transaction,
/// under a per-customer lock and the row version, and the database refuses a negative balance. A customer without any activity has no row yet.
/// </summary>
public sealed class LoyaltyAccount : Entity
{
    private LoyaltyAccount()
    {
    }

    public LoyaltyAccount(Guid customerId) => CustomerId = customerId;

    public Guid CustomerId { get; private set; }

    public int PointsBalance { get; private set; }

    public decimal CreditBalance { get; private set; }

    /// <summary>Wrong gift card codes in a row; at the limit the customer is locked out of code entry for a while (guessing protection).</summary>
    public int CodeFailures { get; private set; }

    public DateTimeOffset? CodeLockedUntil { get; private set; }

    public DateTimeOffset? FirstFailureAt { get; private set; }

    public int ApplyPoints(int delta)
    {
        var next = PointsBalance + delta;
        if (next < 0)
        {
            throw new InvalidOperationException("The points balance cannot go below zero.");
        }

        PointsBalance = next;
        return next;
    }

    public decimal ApplyCredit(decimal delta)
    {
        var next = CreditBalance + delta;
        if (next < 0)
        {
            throw new InvalidOperationException("The store credit balance cannot go below zero.");
        }

        CreditBalance = next;
        return next;
    }

    public bool IsCodeLocked(DateTimeOffset now) => CodeLockedUntil is { } until && until > now;

    /// <summary>A wrong code. After <paramref name="limit"/> wrong codes inside <paramref name="window"/> the account is locked for <paramref name="lockFor"/>.</summary>
    public void RecordCodeFailure(DateTimeOffset now, int limit, TimeSpan window, TimeSpan lockFor)
    {
        if (FirstFailureAt is not { } first || now - first > window)
        {
            FirstFailureAt = now;
            CodeFailures = 0;
        }

        CodeFailures++;
        if (CodeFailures >= limit)
        {
            CodeLockedUntil = now + lockFor;
            CodeFailures = 0;
            FirstFailureAt = null;
        }
    }

    public void ClearCodeFailures()
    {
        CodeFailures = 0;
        FirstFailureAt = null;
    }
}

/// <summary>One change of the points balance. Append-only. The reference key makes a repeated event (a redelivered message, a double click) change nothing the second time.</summary>
public sealed class PointsLedgerEntry : Entity, IAppendOnly, INotAudited
{
    private PointsLedgerEntry()
    {
    }

    public PointsLedgerEntry(
        Guid customerId, PointsEntryKind kind, int points, int balanceAfter, string reason, string? referenceKey, Guid? orderId, string? orderNumber, Guid? returnRequestId, DateTimeOffset? expiresAt,
        Guid? actorId, string? actorName, DateTimeOffset at)
    {
        CustomerId = customerId;
        Kind = kind;
        Points = points;
        BalanceAfter = balanceAfter;
        Reason = reason.Length > 300 ? reason[..300] : reason;
        ReferenceKey = referenceKey;
        OrderId = orderId;
        OrderNumber = orderNumber;
        ReturnRequestId = returnRequestId;
        ExpiresAt = expiresAt;
        ActorId = actorId;
        ActorName = actorName;
        At = at;
    }

    public Guid CustomerId { get; private set; }

    public PointsEntryKind Kind { get; private set; }

    /// <summary>Signed: plus adds points, minus takes them.</summary>
    public int Points { get; private set; }

    public int BalanceAfter { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    /// <summary>Unique together with the customer when set (for example <c>order:{id}:points</c>).</summary>
    public string? ReferenceKey { get; private set; }

    public Guid? OrderId { get; private set; }

    public string? OrderNumber { get; private set; }

    public Guid? ReturnRequestId { get; private set; }

    /// <summary>For points that were added: when they expire (null = never).</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }

    public DateTimeOffset At { get; private set; }
}

/// <summary>One change of the store credit balance (BDT). Append-only, same rules as <see cref="PointsLedgerEntry"/>.</summary>
public sealed class CreditLedgerEntry : Entity, IAppendOnly, INotAudited
{
    private CreditLedgerEntry()
    {
    }

    public CreditLedgerEntry(
        Guid customerId, CreditEntryKind kind, decimal amount, decimal balanceAfter, string reason, string? referenceKey, Guid? orderId, string? orderNumber, Guid? returnRequestId, Guid? giftCardId,
        Guid? actorId, string? actorName, DateTimeOffset at)
    {
        CustomerId = customerId;
        Kind = kind;
        Amount = amount;
        BalanceAfter = balanceAfter;
        Reason = reason.Length > 300 ? reason[..300] : reason;
        ReferenceKey = referenceKey;
        OrderId = orderId;
        OrderNumber = orderNumber;
        ReturnRequestId = returnRequestId;
        GiftCardId = giftCardId;
        ActorId = actorId;
        ActorName = actorName;
        At = at;
    }

    public Guid CustomerId { get; private set; }

    public CreditEntryKind Kind { get; private set; }

    public decimal Amount { get; private set; }

    public decimal BalanceAfter { get; private set; }

    public string Reason { get; private set; } = string.Empty;

    public string? ReferenceKey { get; private set; }

    public Guid? OrderId { get; private set; }

    public string? OrderNumber { get; private set; }

    public Guid? ReturnRequestId { get; private set; }

    public Guid? GiftCardId { get; private set; }

    public Guid? ActorId { get; private set; }

    public string? ActorName { get; private set; }

    public DateTimeOffset At { get; private set; }
}

/// <summary>What the customer paid an order with besides money: points, store credit and a gift card. One row per order (written with the order); only the returned-points counter changes later.</summary>
public sealed class OrderTender : Entity
{
    private OrderTender()
    {
    }

    public OrderTender(Guid orderId, Guid? customerId, int pointsUsed, decimal pointValue, decimal pointsAmount, decimal creditAmount, decimal giftCardAmount, Guid? giftCardId, string? giftCardHint)
    {
        OrderId = orderId;
        CustomerId = customerId;
        PointsUsed = pointsUsed;
        PointValue = pointValue;
        PointsAmount = pointsAmount;
        CreditAmount = creditAmount;
        GiftCardAmount = giftCardAmount;
        GiftCardId = giftCardId;
        GiftCardHint = giftCardHint;
    }

    public Guid OrderId { get; private set; }

    public Guid? CustomerId { get; private set; }

    public int PointsUsed { get; private set; }

    /// <summary>BDT one point was worth when the order was placed.</summary>
    public decimal PointValue { get; private set; }

    public decimal PointsAmount { get; private set; }

    public decimal CreditAmount { get; private set; }

    public decimal GiftCardAmount { get; private set; }

    public Guid? GiftCardId { get; private set; }

    /// <summary>The last characters of the gift card code, safe to show on an invoice.</summary>
    public string? GiftCardHint { get; private set; }

    /// <summary>Points that already went back to the customer (partial refunds, cancel).</summary>
    public int PointsReturned { get; private set; }

    public decimal Total => PointsAmount + CreditAmount + GiftCardAmount;

    public void ReturnPoints(int points) => PointsReturned = Math.Min(PointsUsed, PointsReturned + points);
}

public enum GiftCardEntryKind
{
    Issue = 1,
    RedeemToCredit = 2,
    Spend = 3,
    Deactivate = 4,
}

/// <summary>
/// A gift card staff issued (module AB). The code is shown once when the card is created and stored only as a hash, so no screen, log or report can show it again. The balance is
/// the running total of its append-only entries and never goes below zero.
/// </summary>
public sealed class GiftCard : Entity
{
    private GiftCard()
    {
    }

    public GiftCard(string codeHash, string codeHint, decimal amount, DateTimeOffset? expiresAt, string? note, string? recipientMobile, string? recipientEmail, string? createdByName)
    {
        CodeHash = codeHash;
        CodeHint = codeHint;
        InitialAmount = amount;
        Balance = amount;
        ExpiresAt = expiresAt;
        Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        RecipientMobile = recipientMobile;
        RecipientEmail = recipientEmail;
        CreatedByName = createdByName;
    }

    /// <summary>SHA-256 (hex) of the normalised code.</summary>
    [AuditIgnore]
    public string CodeHash { get; private set; } = string.Empty;

    /// <summary>The last four characters of the code, to recognise a card in lists.</summary>
    public string CodeHint { get; private set; } = string.Empty;

    public decimal InitialAmount { get; private set; }

    public decimal Balance { get; private set; }

    public DateTimeOffset? ExpiresAt { get; private set; }

    public bool IsActive { get; private set; } = true;

    public string? Note { get; private set; }

    [AuditIgnore]
    public string? RecipientMobile { get; private set; }

    [AuditIgnore]
    public string? RecipientEmail { get; private set; }

    public string? CreatedByName { get; private set; }

    /// <summary>The customer whose store credit the card was moved into (a card can be moved once).</summary>
    public Guid? RedeemedByCustomerId { get; private set; }

    public DateTimeOffset? RedeemedAt { get; private set; }

    public bool IsExpired(DateTimeOffset now) => ExpiresAt is { } until && until <= now;

    public bool CanSpend(DateTimeOffset now) => IsActive && !IsExpired(now) && Balance > 0;

    public decimal ApplyChange(decimal delta)
    {
        var next = Balance + delta;
        if (next < 0)
        {
            throw new InvalidOperationException("The gift card balance cannot go below zero.");
        }

        Balance = next;
        return next;
    }

    public void MarkRedeemed(Guid customerId, DateTimeOffset now)
    {
        RedeemedByCustomerId = customerId;
        RedeemedAt = now;
    }

    public void Deactivate() => IsActive = false;
}

/// <summary>One change of a gift card balance. Append-only.</summary>
public sealed class GiftCardEntry : Entity, IAppendOnly, INotAudited
{
    private GiftCardEntry()
    {
    }

    public GiftCardEntry(Guid giftCardId, GiftCardEntryKind kind, decimal amount, decimal balanceAfter, string? referenceKey, Guid? customerId, Guid? orderId, string? orderNumber, string? actorName, string? note, DateTimeOffset at)
    {
        GiftCardId = giftCardId;
        Kind = kind;
        Amount = amount;
        BalanceAfter = balanceAfter;
        ReferenceKey = referenceKey;
        CustomerId = customerId;
        OrderId = orderId;
        OrderNumber = orderNumber;
        ActorName = actorName;
        Note = note is { Length: > 300 } ? note[..300] : note;
        At = at;
    }

    public Guid GiftCardId { get; private set; }

    public GiftCardEntryKind Kind { get; private set; }

    public decimal Amount { get; private set; }

    public decimal BalanceAfter { get; private set; }

    public string? ReferenceKey { get; private set; }

    public Guid? CustomerId { get; private set; }

    public Guid? OrderId { get; private set; }

    public string? OrderNumber { get; private set; }

    public string? ActorName { get; private set; }

    public string? Note { get; private set; }

    public DateTimeOffset At { get; private set; }
}

/// <summary>The personal referral code of a customer (generated on first request).</summary>
public sealed class ReferralCode : Entity
{
    private ReferralCode()
    {
    }

    public ReferralCode(Guid customerId, string code)
    {
        CustomerId = customerId;
        Code = code;
    }

    public Guid CustomerId { get; private set; }

    /// <summary>Upper case, without look-alike characters.</summary>
    public string Code { get; private set; } = string.Empty;
}

public enum ReferralStatus
{
    /// <summary>The new customer entered the code; the reward waits for the first delivered order.</summary>
    Pending = 1,

    /// <summary>Both sides got their reward.</summary>
    Rewarded = 2,

    /// <summary>Not rewarded: the fraud rules or the first order did not qualify.</summary>
    Rejected = 3,

    /// <summary>Rewarded, then the qualifying order was returned and the rewards were taken back.</summary>
    Reversed = 4,
}

/// <summary>A new customer who joined with someone's referral code. A customer can be referred only once.</summary>
public sealed class Referral : Entity
{
    private Referral()
    {
    }

    public Referral(Guid referrerId, Guid refereeId, string codeUsed, DateTimeOffset now)
    {
        ReferrerId = referrerId;
        RefereeId = refereeId;
        CodeUsed = codeUsed;
        AppliedAt = now;
    }

    public Guid ReferrerId { get; private set; }

    public Guid RefereeId { get; private set; }

    public string CodeUsed { get; private set; } = string.Empty;

    public ReferralStatus Status { get; private set; } = ReferralStatus.Pending;

    public string? Note { get; private set; }

    public Guid? QualifyingOrderId { get; private set; }

    public decimal ReferrerReward { get; private set; }

    public decimal RefereeReward { get; private set; }

    /// <summary>Hash of the delivery phone and address of the qualifying order (used to spot the same household twice).</summary>
    public string? HouseholdKey { get; private set; }

    public DateTimeOffset AppliedAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public void Reward(Guid orderId, decimal referrerReward, decimal refereeReward, string householdKey, DateTimeOffset now)
    {
        Status = ReferralStatus.Rewarded;
        QualifyingOrderId = orderId;
        ReferrerReward = referrerReward;
        RefereeReward = refereeReward;
        HouseholdKey = householdKey;
        DecidedAt = now;
        Note = null;
    }

    public void Reject(string reason, Guid? orderId, string? householdKey, DateTimeOffset now)
    {
        Status = ReferralStatus.Rejected;
        Note = reason.Length > 300 ? reason[..300] : reason;
        QualifyingOrderId = orderId;
        HouseholdKey = householdKey;
        DecidedAt = now;
    }

    public void MarkReversed(string reason, DateTimeOffset now)
    {
        Status = ReferralStatus.Reversed;
        Note = reason.Length > 300 ? reason[..300] : reason;
        DecidedAt = now;
    }
}
