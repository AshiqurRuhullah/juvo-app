using Juvo.Application.Abstractions;
using Juvo.Application.Billing;
using Juvo.Application.Orders;
using Juvo.Domain.Loyalty;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Loyalty;

/// <summary>What was checked and decided for a tender: whole points and their value, store credit, and the gift card with the amount it will pay.</summary>
public sealed record TenderPlan(int Points, decimal PointsAmount, decimal PointValue, decimal Credit, decimal GiftCardAmount, Guid? GiftCardId, string? GiftCardHint)
{
    public decimal Total => PointsAmount + Credit + GiftCardAmount;
}

/// <summary>
/// Points, store credit and gift cards as a TENDER (FR-AB01, FR-AB03). A tender is money the customer already holds with the shop: it reduces what is left to pay but never changes a line price,
/// a discount, the tax or the order total. Placement writes it in the same transaction as the order and the stock reservation (ledger entries, one Paid payment record of kind
/// <see cref="PaymentKind.StoreCredit"/> per tender, the <see cref="OrderTender"/> row, and <see cref="Order.ApplyTender"/>, which counts it as received so cash on delivery and the online
/// amount shrink by it). Cancelling an open order, a failed delivery that comes back, and refunds give it back to the ledger, never as cash.
/// </summary>
public sealed class TenderService(IApplicationDbContext db, LoyaltyLedger ledger, LoyaltySettingsService loyalty, GiftCardService giftCards, InvoiceService invoices, TimeProvider clock)
{
    public const string PointsMethod = "POINTS";
    public const string CreditMethod = "CREDIT";
    public const string GiftCardMethod = "GIFTCARD";

    public static readonly IReadOnlyList<string> Methods = [PointsMethod, CreditMethod, GiftCardMethod];

    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    // ---- Plan (checks only, nothing is written) ---------------------------------------------------------

    /// <summary>
    /// Checks a tender request against the balances and the rules and returns what would be used, or null when nothing is requested. <paramref name="customerId"/> is the signed-in customer
    /// (null for a guest: a guest can pay with a gift card but has no points or credit). The order total is <paramref name="orderTotal"/>.
    /// </summary>
    public async Task<Result<TenderPlan?>> PlanAsync(Guid? customerId, decimal orderTotal, TenderRequest? request, CancellationToken cancellationToken)
    {
        if (request is null || (request.Points <= 0 && request.Credit <= 0 && string.IsNullOrWhiteSpace(request.GiftCardCode)))
        {
            return Result.Success<TenderPlan?>(null);
        }

        var enabled = await loyalty.RequireEnabledAsync(cancellationToken);
        if (enabled.IsFailure)
        {
            return enabled.Error!;
        }

        var s = enabled.Value;
        if (request.Points < 0 || request.Credit < 0 || decimal.Round(request.Credit, 2) != request.Credit)
        {
            return Invalid("tender", "Points are whole numbers and credit has at most 2 decimals, both zero or more.");
        }

        var points = 0;
        var pointsAmount = 0m;
        var credit = 0m;
        if (request.Points > 0 || request.Credit > 0)
        {
            if (customerId is not { } id)
            {
                return Error.Unauthorized(ErrorCodes.Loyalty.TenderNeedsSignIn, "Sign in to use points or store credit.");
            }

            var (pointsBalance, creditBalance) = await ledger.BalancesAsync(id, cancellationToken);
            if (request.Points > 0)
            {
                if (request.Points < s.MinPointsToRedeem)
                {
                    return Error.Conflict(ErrorCodes.Loyalty.BelowMinimumPoints, $"At least {s.MinPointsToRedeem} points are needed to pay with points.");
                }

                if (request.Points > pointsBalance)
                {
                    return Error.Conflict(ErrorCodes.Loyalty.InsufficientPoints, $"You have {pointsBalance} points.");
                }

                points = request.Points;
                pointsAmount = PointsMath.ValueOf(points, s.PointValue);
            }

            if (request.Credit > 0)
            {
                if (request.Credit > creditBalance)
                {
                    return Error.Conflict(ErrorCodes.Loyalty.InsufficientCredit, $"You have BDT {creditBalance:0.##} of store credit.");
                }

                credit = request.Credit;
            }

            var cap = PointsMath.MaxTender(orderTotal, s.MaxTenderPercent);
            if (pointsAmount + credit > cap)
            {
                return Error.Conflict(ErrorCodes.Loyalty.TenderExceedsCap, $"Points and store credit can pay at most {s.MaxTenderPercent}% of the order (BDT {cap:0.##}).");
            }
        }

        decimal giftCardAmount = 0m;
        Guid? cardId = null;
        string? hint = null;
        if (!string.IsNullOrWhiteSpace(request.GiftCardCode))
        {
            var found = await giftCards.FindUsableAsync(request.GiftCardCode, customerId, cancellationToken);
            if (found.IsFailure)
            {
                return found.Error!;
            }

            giftCardAmount = Math.Min(found.Value.Balance, orderTotal - pointsAmount - credit);
            cardId = found.Value.Id;
            hint = found.Value.CodeHint;
        }

        if (pointsAmount + credit + giftCardAmount > orderTotal)
        {
            return Error.Conflict(ErrorCodes.Loyalty.TenderExceedsTotal, "Points, credit and the gift card together are more than the order total. Use less.");
        }

        if (giftCardAmount <= 0m && cardId is not null)
        {
            giftCardAmount = 0m;
            cardId = null;
            hint = null;
        }

        return Result.Success<TenderPlan?>(new TenderPlan(points, pointsAmount, s.PointValue, credit, giftCardAmount, cardId, hint));
    }

    /// <summary>The tender as the checkout quote shows it. A refused tender is reported in the DTO (code and message) instead of failing the quote.</summary>
    public async Task<TenderQuoteDto?> QuoteAsync(Guid? customerId, decimal orderTotal, TenderRequest? request, CancellationToken cancellationToken)
    {
        var s = await loyalty.ReadAsync(cancellationToken);
        if (!s.Enabled)
        {
            return request is null ? null : new TenderQuoteDto(false, 0, 0m, 0m, 0m, 0m, orderTotal, 0, 0m, ErrorCodes.Loyalty.FeatureOff, LoyaltySettingsService.FeatureOff().Message);
        }

        var (pointsBalance, creditBalance) = customerId is { } id ? await ledger.BalancesAsync(id, cancellationToken) : (0, 0m);
        var plan = await PlanAsync(customerId, orderTotal, request, cancellationToken);
        if (plan.IsFailure)
        {
            return new TenderQuoteDto(true, 0, 0m, 0m, 0m, 0m, orderTotal, pointsBalance, creditBalance, plan.Error!.Code, plan.Error.Message);
        }

        var p = plan.Value;
        return p is null
            ? new TenderQuoteDto(true, 0, 0m, 0m, 0m, 0m, orderTotal, pointsBalance, creditBalance, null, null)
            : new TenderQuoteDto(true, p.Points, p.PointsAmount, p.Credit, p.GiftCardAmount, p.Total, orderTotal - p.Total, pointsBalance, creditBalance, null, null);
    }

    // ---- Apply at placement (inside the order transaction) ------------------------------------------------

    /// <summary>
    /// Writes the tender of a new order: ledger entries (the same transaction as the order, so a failure here undoes the order and its stock reservation), one Paid payment record per tender,
    /// the order's tender row, and the order's received amount. Returns a failure when a balance changed since the quote (nothing is kept then: the caller throws and the transaction rolls back).
    /// </summary>
    public async Task<Result> ApplyAsync(Order order, Guid customerId, TenderPlan plan, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var ownerId = (Guid?)customerId;
        if (plan.Points > 0)
        {
            var posted = await ledger.PostPointsAsync(new PointsPosting(customerId, PointsEntryKind.Redeem, -plan.Points, $"Paid part of order {order.Number}", $"order:{order.Id:N}:points", order.Id, order.Number), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }
        }

        if (plan.Credit > 0)
        {
            var posted = await ledger.PostCreditAsync(new CreditPosting(customerId, CreditEntryKind.Spend, -plan.Credit, $"Paid part of order {order.Number}", $"order:{order.Id:N}:credit", order.Id, order.Number), cancellationToken);
            if (posted.IsFailure)
            {
                return posted.Error!;
            }
        }

        if (plan.GiftCardAmount > 0 && plan.GiftCardId is { } cardId)
        {
            var spent = await ledger.SpendGiftCardAsync(cardId, plan.GiftCardAmount, GiftCardEntryKind.Spend, $"order:{order.Id:N}", ownerId, order.Id, order.Number, null, $"Paid part of order {order.Number}", cancellationToken);
            if (spent.IsFailure)
            {
                return spent.Error!;
            }
        }

        db.OrderTenders.Add(new OrderTender(order.Id, ownerId, plan.Points, plan.PointValue, plan.PointsAmount, plan.Credit, plan.GiftCardAmount, plan.GiftCardId, plan.GiftCardHint));
        foreach (var (method, amount, detail) in new[]
                 {
                     (PointsMethod, plan.PointsAmount, $"{plan.Points} points"),
                     (CreditMethod, plan.Credit, "Store credit"),
                     (GiftCardMethod, plan.GiftCardAmount, $"Gift card …{plan.GiftCardHint}"),
                 })
        {
            if (amount <= 0)
            {
                continue;
            }

            var payment = Payment.Start(new PaymentInit(order.Id, order.Number, order.BranchId, PaymentKind.StoreCredit, method, null, amount, order.Currency, null, null, null, null, null, null, null), now, null, "System");
            payment.ConfirmPaid(null, now, null, $"Paid from the customer's rewards: {detail}", null, "System");
            db.Payments.Add(payment);
        }

        order.ApplyTender(plan.Total, now, null, "System");
        return Result.Success();
    }

    // ---- Giving it back ------------------------------------------------------------------------------------

    /// <summary>
    /// Gives <paramref name="amount"/> of a payment back into the customer's ledger (never as cash): a tender of points returns points, store credit and gift card tenders return store credit, and a cash
    /// payment refunded "to store credit" returns store credit. Creates a Refund of mode StoreCredit that completes at once, with the credit note when the order has an invoice (or the refund
    /// belongs to a return). Runs in the caller's transaction; the caller saves.
    /// </summary>
    public async Task<Result<Guid>> RefundToLedgerAsync(Order order, Payment payment, decimal amount, Guid? returnRequestId, string reason, Who who, bool cancelled, CancellationToken cancellationToken)
    {
        if (amount <= 0 || amount > payment.Refundable)
        {
            return Error.Conflict(ErrorCodes.Payment.RefundExceedsPaid, "The refund is more than what is left to refund on this payment.");
        }

        var now = clock.GetUtcNow();
        var refundableBefore = payment.Refundable;
        var refund = new Refund(payment, amount, RefundMode.StoreCredit, reason, returnRequestId, who.Id, who.Name, now);
        payment.ReserveRefund(amount, now, who.Id, who.Name, reason);
        db.Refunds.Add(refund);

        if (payment.Kind == PaymentKind.StoreCredit && payment.Method == PointsMethod)
        {
            var tender = await db.OrderTenders.FirstAsync(t => t.OrderId == order.Id, cancellationToken);
            var left = tender.PointsUsed - tender.PointsReturned;
            var points = amount >= refundableBefore ? left : Math.Min(left, (int)decimal.Round(tender.PointsUsed * amount / payment.Amount, 0, MidpointRounding.AwayFromZero));
            if (points > 0)
            {
                var s = await loyalty.ReadAsync(cancellationToken);
                var back = await ledger.PostPointsAsync(new PointsPosting(order.CustomerId, PointsEntryKind.Release, points, cancelled ? $"Order {order.Number} cancelled" : $"Refund on order {order.Number}",
                    $"refund:{refund.Id:N}", order.Id, order.Number, returnRequestId, s.PointsExpiryMonths > 0 ? now.AddMonths(s.PointsExpiryMonths) : null), cancellationToken);
                if (back.IsFailure)
                {
                    return back.Error!;
                }

                tender.ReturnPoints(points);
            }
        }
        else
        {
            var back = await ledger.PostCreditAsync(new CreditPosting(order.CustomerId, cancelled ? CreditEntryKind.Release : CreditEntryKind.Refund, amount,
                cancelled ? $"Order {order.Number} cancelled" : $"Refund on order {order.Number}", $"refund:{refund.Id:N}", order.Id, order.Number, returnRequestId), cancellationToken);
            if (back.IsFailure)
            {
                return back.Error!;
            }
        }

        payment.CompleteRefund(amount, now, who.Id, who.Name, $"Refund of BDT {amount:0.##} to the customer's rewards");
        refund.Complete("LEDGER", now);
        var hasInvoice = await db.Invoices.IgnoreQueryFilters(["BranchScope"]).AnyAsync(i => i.OrderId == order.Id, cancellationToken);
        if (returnRequestId is not null || hasInvoice)
        {
            await invoices.IssueCreditNoteAsync(refund, order, who.Id, who.Name, cancellationToken);
        }

        return refund.Id;
    }

    /// <summary>
    /// The order was cancelled (or a delivery failed for good): everything it was paid with from the rewards goes back to the ledger. Idempotent: a payment with nothing left to refund is skipped.
    /// A fully tendered order's payment track moves to Refunded. The caller saves.
    /// </summary>
    public async Task ReleaseForOrderAsync(Order order, Who who, string reason, CancellationToken cancellationToken)
    {
        var payments = await db.Payments.IgnoreQueryFilters(["BranchScope"]).Include(p => p.Events).Where(p => p.OrderId == order.Id && p.Kind == PaymentKind.StoreCredit).ToListAsync(cancellationToken);
        var any = false;
        foreach (var payment in payments.Where(p => p.HoldsMoney && p.Refundable > 0))
        {
            var done = await RefundToLedgerAsync(order, payment, payment.Refundable, null, reason, who, true, cancellationToken);
            any |= done.IsSuccess;
        }

        if (any && order.PaymentStatus == PaymentStatus.Paid && !await db.Payments.IgnoreQueryFilters(["BranchScope"]).AnyAsync(p => p.OrderId == order.Id && p.Kind != PaymentKind.StoreCredit &&
                (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.CollectedByCourier || p.Status == PaymentStatus.Settled || p.Status == PaymentStatus.PartiallyRefunded), cancellationToken))
        {
            order.TransitionPaymentTo(PaymentStatus.Refunded, clock.GetUtcNow(), who.Id, who.Name, "Paid with rewards; given back to the customer's rewards");
        }
    }

    /// <summary>
    /// The tender part of a refund from a return, in proportion to what the tender payments and the cash payments can still give back. Gives it back to the ledger and returns the amount done.
    /// The rest of the refund follows the usual refund methods.
    /// </summary>
    public async Task<Result<decimal>> RefundTenderShareAsync(Order order, Guid returnRequestId, string returnNumber, decimal refundTotal, IReadOnlyList<Payment> payments, Who who, CancellationToken cancellationToken)
    {
        var tender = payments.Where(p => p.Kind == PaymentKind.StoreCredit && p.HoldsMoney && p.Refundable > 0).ToList();
        if (tender.Count == 0)
        {
            return 0m;
        }

        var cash = payments.Where(p => p.Kind != PaymentKind.StoreCredit && p.HoldsMoney).Sum(p => p.Refundable);
        var share = PointsMath.TenderShareOfRefund(refundTotal, tender.Sum(p => p.Refundable), cash);
        var done = 0m;
        foreach (var payment in tender)
        {
            var take = Math.Min(share - done, payment.Refundable);
            if (take <= 0)
            {
                break;
            }

            var result = await RefundToLedgerAsync(order, payment, take, returnRequestId, $"Return {returnNumber}", who, false, cancellationToken);
            if (result.IsFailure)
            {
                return result.Error!;
            }

            done += take;
        }

        return done;
    }
}
