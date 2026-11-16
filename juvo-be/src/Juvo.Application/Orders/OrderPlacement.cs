using System.Security.Cryptography;
using System.Text;
using Juvo.Application.Abstractions;
using Juvo.Application.Carts;
using Juvo.Application.Delivery;
using Juvo.Application.Identity;
using Juvo.Application.Preorders;
using Juvo.Application.Pricing;
using Juvo.Application.Storefront;
using Juvo.Domain.Carts;
using Juvo.Domain.Catalog;
using Juvo.Domain.Customers;
using Juvo.Domain.Inventory;
using Juvo.Domain.Orders;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Orders;

/// <summary>Everything a placement needs, whether it comes from the shop, a signed-in customer or the staff order desk.</summary>
public sealed record PlacementInput(
    OrderSource Source, Who Who, bool IsStaff, string Mobile, string? Name, string? Email, Guid? SignedInCustomerId, bool OtpAlreadyVerified, ShippingSnapshot Shipping,
    IReadOnlyList<StockLine> Lines, string? CouponCode, decimal ManualDiscount, decimal? DeliveryOverride, string PaymentMethod, string? CustomerNote, string? InternalNote, string? IdempotencyKey,
    bool ConfirmDuplicate, decimal? ExpectedTotal, string? DeviceId, string? Ip, string? OtpCode, bool TermsAccepted, Guid? TargetBranchId, Cart? Cart)
{
    /// <summary>Points, store credit and gift card the customer pays part of the order with (module AB). Ignored for staff orders.</summary>
    public Loyalty.TenderRequest? Tender { get; init; }
}

public sealed record PlacedOrder(Order Order, string? TrackingToken, bool Replayed, DateOnly? EarliestDelivery, DateOnly? LatestDelivery);

/// <summary>The result of a dry run of placing an order (no stock reserved, nothing saved).</summary>
public sealed record PlacementQuote(
    IReadOnlyList<(ProductVariant Variant, Product Product, decimal UnitPrice, int Quantity, decimal ListPrice, bool IsPreorder, IReadOnlyList<PriceAdjustment> Adjustments)> Lines, decimal Subtotal, int Weight,
    Coupon? Coupon, decimal CouponDiscount, decimal ManualDiscount, decimal DeliveryCharge, decimal StandardCharge, decimal Grand, decimal Advance, string Method, Guid BranchId, string Rule, string? ZoneCode,
    DateOnly? Earliest, DateOnly? Latest, IReadOnlyDictionary<Guid, int> Available, decimal PromotionDiscount = 0m, PricingResult? Pricing = null);

/// <summary>Thrown inside the order transaction to undo everything (stock, sequence, coupon use) and answer with <see cref="Error"/>.</summary>
internal sealed class PlacementRejected(Error error) : Exception(error.Message)
{
    public Error Error { get; } = error;
}

/// <summary>
/// Places an order (module D): idempotency, price and stock re-check, coupon, delivery charge, fraud rules, duplicate guard, optional OTP, then ONE database
/// transaction that finds or creates the customer by mobile (BR-006), reserves stock in the fulfilling branch with atomic conditional updates, takes the next
/// gap-free order number, writes the order with its snapshots and history, and clears the cart. Any failure rolls all of it back.
/// </summary>
public sealed class OrderPlacement(
    IApplicationDbContext db, IUnitOfWork uow, SettingsService settings, OrderStock stock, OrderRouter router, FraudService fraud, PricedDelivery delivery, CustomerAuthService otp,
    StockAvailability availability, Payments.PaymentSettingsService paymentSettings, Billing.TaxService taxes, IPricingEngine pricing, Loyalty.TenderService tenders, TimeProvider clock)
{
    public const string Cod = "COD";
    public const string SandboxOnline = "SANDBOX";

    public static readonly IReadOnlyList<string> PaymentMethods = [Cod, SandboxOnline];

    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static string FingerprintOf(string mobile, IEnumerable<StockLine> lines) =>
        Hash($"{mobile}|{string.Join(',', lines.OrderBy(l => l.VariantId).Select(l => $"{l.VariantId:N}:{l.Quantity}"))}");

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private sealed record Priced(ProductVariant Variant, Product Product, PricedLine Line, int Quantity, string? ImagePath, bool IsPreorder, PreorderInfo Pre)
    {
        public decimal UnitPrice => Line.UnitPrice;
    }

    public async Task<Result<PlacedOrder>> PlaceAsync(PlacementInput i, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var config = await settings.OrdersAsync(cancellationToken);
        var fingerprint = FingerprintOf(i.Mobile, i.Lines);

        // 1. Idempotency: the same key returns the same order; a key used for a different request is an error.
        if (!string.IsNullOrWhiteSpace(i.IdempotencyKey))
        {
            var key = i.IdempotencyKey.Trim();
            var existing = await db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).FirstOrDefaultAsync(o => o.IdempotencyKey == key, cancellationToken);
            if (existing is not null)
            {
                return existing.Fingerprint == fingerprint
                    ? new PlacedOrder(existing, null, true, null, null)
                    : Error.Conflict(ErrorCodes.General.IdempotencyKeyReused, "This idempotency key was already used for a different order.");
            }
        }

        if (i.Lines.Count == 0 || i.Lines.Any(l => l.Quantity < 1))
        {
            return Error.Conflict(ErrorCodes.Order.CartEmpty, "There is nothing to order.");
        }

        var maxPerLine = i.IsStaff ? config.MaxManualQuantity : availability.Options.MaxQuantityPerLine;
        if (i.Lines.Any(l => l.Quantity > maxPerLine))
        {
            return Error.Conflict(ErrorCodes.Cart.StockInsufficient, $"At most {maxPerLine} of one item can be ordered.");
        }

        if (!i.IsStaff && !i.TermsAccepted)
        {
            return Invalid("acceptTerms", "Accept the terms, privacy and return policy to place the order.");
        }

        // 2-5. Prices, coupon, delivery charge, payment method and advance: one calculation shared with the staff desk preview (QuoteAsync), so the preview equals the order.
        var pricing = await PriceAsync(i, config, now, cancellationToken);
        if (pricing.IsFailure)
        {
            return pricing.Error!;
        }

        var (priced, subtotal, weight, coupon, couponDiscount, quote, deliveryCharge, grand, method, standardCharge, advance, promotionDiscount, engine) = pricing.Value;


        // 6. Customer (read only here), blacklist and fraud rules.
        var customer = i.SignedInCustomerId is { } signedIn
            ? await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == signedIn, cancellationToken)
            : await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Mobile == i.Mobile, cancellationToken);
        if (customer is { IsActive: false })
        {
            return Error.Forbidden(ErrorCodes.Order.CheckoutBlocked, BlockedMessage(i.IsStaff, "The customer is deactivated."));
        }

        var verdict = await fraud.EvaluateAsync(
            new FraudContext(customer?.Id ?? Guid.Empty, i.Mobile, i.Email ?? customer?.Email, i.DeviceId, i.Ip, grand, method == Cod, i.Shipping.Phone, i.Shipping.AddressLine, i.IsStaff), cancellationToken);
        if (verdict.IsBlocked)
        {
            return Error.Forbidden(ErrorCodes.Order.CheckoutBlocked, BlockedMessage(i.IsStaff, verdict.BlockedBecause!));
        }

        // 6b. Rewards tender (module AB): checked here, written inside the order transaction below. Changes no price and no total.
        var tenderPlan = await tenders.PlanAsync(i.IsStaff ? null : i.SignedInCustomerId, grand, i.IsStaff ? null : i.Tender, cancellationToken);
        if (tenderPlan.IsFailure)
        {
            return tenderPlan.Error!;
        }

        // 7. Duplicate guard (FR-D09).
        if (!i.ConfirmDuplicate && config.DuplicateWindowMinutes > 0)
        {
            var since = now.AddMinutes(-config.DuplicateWindowMinutes);
            var twin = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking()
                .Where(o => o.Fingerprint == fingerprint && o.CustomerMobile == i.Mobile && o.PlacedAt >= since && o.Status != OrderStatus.Cancelled)
                .OrderByDescending(o => o.PlacedAt).Select(o => new { o.Number, o.PlacedAt }).FirstOrDefaultAsync(cancellationToken);
            if (twin is not null)
            {
                return Error.Conflict(ErrorCodes.Order.DuplicateSuspected,
                    $"An identical order ({twin.Number}) was placed {Math.Max(1, (int)(now - twin.PlacedAt).TotalMinutes)} minute(s) ago. Confirm if you want to order again.");
            }
        }

        // 8. Where the goods come from. Orders are not split: one branch must hold every line.
        // A pre-order line (module AA) holds no stock now: it is left out of routing and reservation and waits in the pre-order queue.
        var lines = i.Lines.Where(l => priced.First(p => p.Variant.Id == l.VariantId) is { IsPreorder: false, Variant.TracksStock: true }).ToList();
        IReadOnlyList<Guid> candidates;
        string rule;
        if (i.TargetBranchId is { } target)
        {
            candidates = [target];
            rule = "Staff";
        }
        else
        {
            (candidates, rule) = await router.CandidatesAsync(lines, config.RoutingMode, i.Shipping.Latitude, i.Shipping.Longitude, i.Shipping.DistrictId, cancellationToken);
        }

        if (candidates.Count == 0)
        {
            return Error.Conflict(ErrorCodes.Order.StockInsufficient, "Not every item of your order is available together right now. Reduce the quantity or remove an item.");
        }

        // 9. OTP before confirming (FR-D04): checked and used up before the transaction, because wrong guesses must be saved even when the order fails.
        var otpRequired = !i.IsStaff && !i.OtpAlreadyVerified && (config.OtpMode == OtpMode.Always || (config.OtpMode == OtpMode.CodOnly && method == Cod));
        var otpVerified = i.OtpAlreadyVerified;
        if (otpRequired)
        {
            if (string.IsNullOrWhiteSpace(i.OtpCode))
            {
                return Error.Conflict(ErrorCodes.Order.OtpRequired, "Verify your mobile number with the code we send you before placing the order.");
            }

            var checkedOtp = await otp.ConsumeOtpAsync(i.Mobile, i.OtpCode, cancellationToken);
            if (checkedOtp.IsFailure)
            {
                return checkedOtp.Error!;
            }

            otpVerified = true;
        }

        // 10. The transaction. Anything thrown as PlacementRejected rolls back stock, sequence, coupon use and customer.
        var token = NewToken();
        try
        {
            var order = await uow.ExecuteInTransactionAsync(async ct =>
            {
                var owner = await FindOrCreateCustomerAsync(i, ct);
                var reservedIn = Guid.Empty;
                foreach (var branchId in candidates)
                {
                    if (lines.Count == 0 || await stock.TryReserveAsync(branchId, lines, ct) is null)
                    {
                        reservedIn = branchId;
                        break;
                    }
                }

                if (reservedIn == Guid.Empty)
                {
                    throw new PlacementRejected(Error.Conflict(ErrorCodes.Order.StockInsufficient, "Someone just bought the last items. Reduce the quantity or remove an item."));
                }

                var branch = await db.Branches.AsNoTracking().FirstAsync(b => b.Id == reservedIn, ct);
                var number = await NextNumberAsync(branch, now, ct);

                if (coupon is not null)
                {
                    var couponId = coupon.Id;
                    var used = await db.Coupons.Where(c => c.Id == couponId && (c.UsageLimit == null || c.UsedCount < c.UsageLimit))
                        .ExecuteUpdateAsync(set => set.SetProperty(c => c.UsedCount, c => c.UsedCount + 1), ct);
                    if (used == 0)
                    {
                        throw new PlacementRejected(Error.Conflict(ErrorCodes.Coupon.UsedUp, "This coupon has been used up."));
                    }
                }

                foreach (var applied in engine.Promotions)
                {
                    var promotionId = applied.Id;
                    var counted = await db.Promotions.Where(p => p.Id == promotionId && (p.UsageLimit == null || p.UsedCount < p.UsageLimit))
                        .ExecuteUpdateAsync(set => set.SetProperty(p => p.UsedCount, p => p.UsedCount + 1), ct);
                    if (counted == 0)
                    {
                        throw new PlacementRejected(Error.Conflict(ErrorCodes.Promo.UsedUp, "An offer in your order has just been used up. Check the order and confirm again."));
                    }
                }

                var data = new NewOrder(
                    number, reservedIn, i.Source, owner.Id, owner.Mobile, i.Name ?? owner.FullName, i.Email ?? owner.Email, method, rule, quote.ZoneCode, "BDT", subtotal, couponDiscount, coupon?.Code,
                    coupon?.Id, i.ManualDiscount, deliveryCharge, standardCharge, advance, weight, i.Shipping, method == Cod ? null : now.AddMinutes(config.PaymentHoldMinutes), i.IdempotencyKey?.Trim(), fingerprint,
                    Hash(token), i.DeviceId, i.Ip, otpVerified, i.IsStaff ? null : now, i.IsStaff ? i.Who.Id : null, i.Who.Name, promotionDiscount);
                var items = priced.Select(p => new NewOrderItem(
                    p.Variant.Id, p.Product.Id, p.Product.NameEn, p.Product.NameBn, p.Variant.Sku, p.Variant.ColorName, p.Variant.Size, p.ImagePath, p.UnitPrice, p.Quantity,
                    p.Variant.WeightGramsOverride ?? p.Product.WeightGrams ?? 0, p.Variant.TracksStock, p.Product.CostPrice, p.Line.ListPrice, p.Line.OrderDiscount,
                    [.. p.Line.Adjustments.Where(a => a.Amount > 0m).Select(a => new NewItemAdjustment(a.SourceType, a.SourceId, a.LabelEn, a.LabelBn, a.Amount, a.Level))], p.IsPreorder,
                    p.IsPreorder ? p.Pre.ExpectedShipDate : null)).ToList();
                var placed = Order.Place(data, items, now);
                var (taxRule, tax) = await taxes.ComputeAsync([.. items.Select(x => x.UnitPrice * x.Quantity)], promotionDiscount + couponDiscount + i.ManualDiscount, deliveryCharge, ct);
                if (taxRule is not null)
                {
                    placed.ApplyTax(taxRule.Code, taxRule.RatePercent, tax.TaxTotal); // the tax inside the prices: totals do not change
                }

                if (!string.IsNullOrWhiteSpace(i.CustomerNote))
                {
                    placed.AddNote(NoteKind.Customer, i.CustomerNote, i.Who.Id, i.Who.Name, now);
                }

                if (!string.IsNullOrWhiteSpace(i.InternalNote))
                {
                    placed.AddNote(NoteKind.Internal, i.InternalNote, i.Who.Id, i.Who.Name, now);
                }

                if (verdict.Findings.Count > 0)
                {
                    placed.RecordRisk(verdict.Findings, now, standardCharge);
                }

                db.Orders.Add(placed);
                await stock.RecordAsync(reservedIn, lines, StockReason.OrderReserved, 0, placed, ct);
                if (tenderPlan.Value is { } plan)
                {
                    var applied = await tenders.ApplyAsync(placed, owner.Id, plan, ct); // same transaction: a failure undoes the order and its stock reservation
                    if (applied.IsFailure)
                    {
                        throw new PlacementRejected(applied.Error!);
                    }
                }

                if (i.Cart is { } cart)
                {
                    cart.Clear();
                }

                await db.SaveChangesAsync(ct);
                return placed;
            }, cancellationToken);

            return new PlacedOrder(order, token, false, quote.EarliestDelivery, quote.LatestDelivery);
        }
        catch (PlacementRejected rejected)
        {
            return rejected.Error;
        }
    }


    /// <summary>The prices of an order, worked out by the pricing engine, before anything is reserved or saved.</summary>
    private sealed record Pricing(
        List<Priced> Priced, decimal Subtotal, int Weight, Coupon? Coupon, decimal CouponDiscount, DeliveryQuoteDto Quote, decimal DeliveryCharge, decimal Grand, string Method, decimal StandardCharge, decimal Advance,
        decimal PromotionDiscount, PricingResult Engine);

    /// <summary>
    /// Prices from the pricing engine (never from the client: list price, sale, promotions, coupon), the delivery charge (the same calculation as the quote, BR-G2; staff may override it; a free-delivery
    /// promotion waives it), the payment method and the advance of a large cash on delivery order (BR-D1) or of pre-order lines (module AA). Changes nothing. Used by <see cref="PlaceAsync"/> and by
    /// the order desk preview <see cref="QuoteAsync"/>.
    /// </summary>
    private async Task<Result<Pricing>> PriceAsync(PlacementInput i, OrderSettings config, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var variantIds = i.Lines.Select(l => l.VariantId).ToList();
        var rows = await (from v in db.ProductVariants.AsNoTracking()
                          join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                          join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                          where variantIds.Contains(v.Id) && v.IsActive && p.Status == ProductStatus.Published && c.IsActive
                          select new { Variant = v, Product = p }).ToListAsync(cancellationToken);
        if (rows.Count != variantIds.Count)
        {
            return Error.NotFound(ErrorCodes.Cart.VariantUnavailable, "An item in your order is no longer available.");
        }

        var request = new PricingRequest([.. i.Lines.Select(l => new PricingLineRequest(l.VariantId, l.Quantity))], new PricingCustomer(i.SignedInCustomerId, i.Mobile),
            string.IsNullOrWhiteSpace(i.CouponCode) ? null : [i.CouponCode], i.Source == OrderSource.Web ? PricingChannel.Web : PricingChannel.SocialDesk, now, RequireSellable: true);
        var engineResult = await pricing.PriceAsync(request, cancellationToken);
        if (engineResult.IsFailure)
        {
            return engineResult.Error!;
        }

        var engine = engineResult.Value;
        if (engine.CouponError is { } couponError)
        {
            return couponError;
        }

        var productIds = rows.Select(r => r.Product.Id).Distinct().ToList();
        var images = await db.ProductImages.AsNoTracking().Where(m => productIds.Contains(m.ProductId)).OrderBy(m => m.SortOrder).ToListAsync(cancellationToken);
        var available = await availability.AvailableAsync(variantIds, cancellationToken);
        var priced = i.Lines.Select(l =>
        {
            var row = rows.First(r => r.Variant.Id == l.VariantId);
            var image = images.Where(m => m.ProductId == row.Product.Id)
                .OrderBy(m => string.Equals(m.ColorName, row.Variant.ColorName, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(m => m.SortOrder).FirstOrDefault();
            var pre = PreorderRules.For(row.Product, row.Variant);
            return new Priced(row.Variant, row.Product, engine.Lines.First(x => x.VariantId == l.VariantId), l.Quantity, image?.Path,
                PreorderRules.IsPreorderLine(pre, row.Variant, available.GetValueOrDefault(l.VariantId)), pre);
        }).ToList();
        var totals = engine.Totals;
        var weight = priced.Sum(p => (p.Variant.WeightGramsOverride ?? p.Product.WeightGrams ?? 0) * p.Quantity);
        Coupon? coupon = engine.Coupon is { Applied: true, CouponId: { } couponId } ? await db.Coupons.AsNoTracking().FirstAsync(c => c.Id == couponId, cancellationToken) : null;

        // Delivery charge: the same calculation as the quote (BR-G2: the charge shown is the charge billed); staff may override it.
        var quote = await delivery.QuoteAsync(i.Shipping.CountryCode, i.Shipping.DistrictId, i.Shipping.UpazilaId, totals.GoodsTotal, weight, engine.FreeDelivery, cancellationToken);
        if (quote.IsFailure)
        {
            return quote.Error!;
        }

        var deliveryCharge = i.DeliveryOverride ?? quote.Value.Total;
        if (i.ManualDiscount < 0 || i.ManualDiscount > totals.GoodsTotal)
        {
            return Error.Conflict(ErrorCodes.Order.DiscountTooLarge, "The discount cannot be more than the goods are worth.");
        }

        var grand = totals.GoodsTotal - i.ManualDiscount + deliveryCharge;
        if (i.ExpectedTotal is { } expected && expected != grand)
        {
            return Error.Conflict(ErrorCodes.Order.TotalChanged, $"The total changed to BDT {grand:0.##}. Check the order and confirm again.");
        }

        // Payment method.
        var method = (i.PaymentMethod ?? string.Empty).Trim().ToUpperInvariant();
        if (Payments.PaymentMethodCatalog.Find(method) is null)
        {
            return Error.Conflict(ErrorCodes.Order.PaymentMethodUnavailable, "This payment method is not available.");
        }

        if (method == Cod && i.Shipping.CountryCode != "BD")
        {
            return Error.Conflict(ErrorCodes.Order.CodNotAvailable, "Cash on delivery is only available inside Bangladesh.");
        }

        if (await paymentSettings.ProblemAsync(method, i.Shipping.CountryCode, grand, cancellationToken) is { } methodProblem)
        {
            return Error.Conflict(method == Cod ? ErrorCodes.Order.CodNotAvailable : ErrorCodes.Order.PaymentMethodUnavailable, methodProblem);
        }

        var standardCharge = quote.Value.Total + quote.Value.FreeDeliveryWaiver; // what the courier costs, even when the customer got free delivery
        var codAdvance = method == Cod && grand > config.CodAdvanceThreshold ? Math.Min(standardCharge, grand) : 0m; // BR-D1
        var preorderAdvance = priced.Where(p => p.IsPreorder).Sum(p => PreorderRules.Advance(p.Line.NetTotal, p.Pre.AdvancePercent)); // FR-AA02, paid through the same advance mechanism
        var advance = method == Cod ? Math.Max(codAdvance, Math.Min(preorderAdvance, grand)) : 0m;

        return new Pricing(priced, totals.Subtotal, weight, coupon, totals.CouponDiscount, quote.Value, deliveryCharge, grand, method, standardCharge, advance, totals.PromotionDiscount, engine);
    }

    /// <summary>
    /// Dry run of <see cref="PlaceAsync"/> for the order desk: the same checks and the same calculation, nothing reserved, saved or counted (no coupon use, no number, no customer).
    /// Idempotency, the duplicate guard, fraud rules and stock reservation are decided only when the order is really created.
    /// </summary>
    public async Task<Result<PlacementQuote>> QuoteAsync(PlacementInput i, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var config = await settings.OrdersAsync(cancellationToken);
        if (i.Lines.Count == 0 || i.Lines.Any(l => l.Quantity < 1))
        {
            return Error.Conflict(ErrorCodes.Order.CartEmpty, "There is nothing to order.");
        }

        var maxPerLine = i.IsStaff ? config.MaxManualQuantity : availability.Options.MaxQuantityPerLine;
        if (i.Lines.Any(l => l.Quantity > maxPerLine))
        {
            return Error.Conflict(ErrorCodes.Cart.StockInsufficient, $"At most {maxPerLine} of one item can be ordered.");
        }

        var pricing = await PriceAsync(i, config, now, cancellationToken);
        if (pricing.IsFailure)
        {
            return pricing.Error!;
        }

        var p = pricing.Value;
        var lines = i.Lines.Where(l => p.Priced.First(x => x.Variant.Id == l.VariantId) is { IsPreorder: false, Variant.TracksStock: true }).ToList();
        IReadOnlyList<Guid> candidates;
        string rule;
        if (i.TargetBranchId is { } target)
        {
            candidates = [target];
            rule = "Staff";
        }
        else
        {
            (candidates, rule) = await router.CandidatesAsync(lines, config.RoutingMode, i.Shipping.Latitude, i.Shipping.Longitude, i.Shipping.DistrictId, cancellationToken);
        }

        if (candidates.Count == 0)
        {
            return Error.Conflict(ErrorCodes.Order.StockInsufficient, "Not every item of this order is available together right now. Reduce the quantity or remove an item.");
        }

        var branchId = candidates[0];
        var variantIds = p.Priced.Select(x => x.Variant.Id).ToList();
        var available = await db.StockLevels.AsNoTracking().Where(s => s.BranchId == branchId && variantIds.Contains(s.VariantId)).ToDictionaryAsync(s => s.VariantId, s => s.OnHand - s.Reserved, cancellationToken);
        if (lines.Any(l => available.GetValueOrDefault(l.VariantId) < l.Quantity))
        {
            return Error.Conflict(ErrorCodes.Order.StockInsufficient, "The branch does not have every item of this order in stock.");
        }

        return new PlacementQuote(p.Priced.Select(x => (x.Variant, x.Product, x.UnitPrice, x.Quantity, x.Line.ListPrice, x.IsPreorder, x.Line.Adjustments)).ToList(), p.Subtotal, p.Weight, p.Coupon, p.CouponDiscount,
            i.ManualDiscount, p.DeliveryCharge, p.StandardCharge, p.Grand, p.Advance, p.Method, branchId, rule, p.Quote.ZoneCode, p.Quote.EarliestDelivery, p.Quote.LatestDelivery, available, p.PromotionDiscount, p.Engine);
    }

    private static string BlockedMessage(bool staff, string reason) =>
        staff ? $"This order cannot be placed: {reason}" : "We could not accept this order online. Please contact the shop.";

    /// <summary>The mobile number is the customer's identity (BR-006): an existing customer is reused, otherwise a guest profile is created.</summary>
    private async Task<Customer> FindOrCreateCustomerAsync(PlacementInput i, CancellationToken cancellationToken)
    {
        var customer = i.SignedInCustomerId is { } id
            ? await db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken)
            : await db.Customers.FirstOrDefaultAsync(c => c.Mobile == i.Mobile, cancellationToken);
        if (customer is null)
        {
            customer = new Customer(i.Mobile, ProfileLevel.Guest);
            customer.UpdateProfile(i.Name, i.Email);
            db.Customers.Add(customer);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                // Two checkouts with the same new mobile at the same moment: the other one won, use that profile.
                db.Customers.Remove(customer);
                customer = await db.Customers.FirstOrDefaultAsync(c => c.Mobile == i.Mobile, cancellationToken)
                           ?? throw new PlacementRejected(Error.Conflict(ErrorCodes.General.Conflict, "Please try again."));
            }

            return customer;
        }

        if (string.IsNullOrWhiteSpace(customer.FullName) && !string.IsNullOrWhiteSpace(i.Name))
        {
            customer.UpdateProfile(i.Name, customer.Email ?? i.Email);
        }

        return customer;
    }

    /// <summary>
    /// <c>JV-&lt;BRANCHCODE&gt;-&lt;YYMM&gt;-&lt;6-digit sequence&gt;</c>. The counter row is incremented inside the order transaction (and locked until it ends), so numbers are
    /// unique and gap-free per branch and month: a rolled-back order gives its number back.
    /// </summary>
    internal async Task<string> NextNumberAsync(Domain.Branches.Branch branch, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var period = LocalTime(branch, now).ToString("yyMM", System.Globalization.CultureInfo.InvariantCulture);
        var branchId = branch.Id;
        var bumped = await db.OrderSequences.Where(s => s.BranchId == branchId && s.Period == period)
            .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastNumber, s => s.LastNumber + 1), cancellationToken);
        if (bumped == 0)
        {
            var row = new OrderSequence(branchId, period, 0);
            db.OrderSequences.Add(row);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException)
            {
                db.OrderSequences.Remove(row); // someone created the row first
            }

            await db.OrderSequences.Where(s => s.BranchId == branchId && s.Period == period)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.LastNumber, s => s.LastNumber + 1), cancellationToken);
        }

        var next = await db.OrderSequences.AsNoTracking().Where(s => s.BranchId == branchId && s.Period == period).Select(s => s.LastNumber).FirstAsync(cancellationToken);
        return $"JV-{branch.OrderPrefix}-{period}-{next:D6}";
    }

    internal static DateTimeOffset LocalTime(Domain.Branches.Branch branch, DateTimeOffset now)
    {
        try
        {
            return TimeZoneInfo.ConvertTime(now, TimeZoneInfo.FindSystemTimeZoneById(branch.TimeZoneId));
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return now.ToOffset(TimeSpan.FromHours(6)); // Bangladesh Standard Time
        }
    }
}
