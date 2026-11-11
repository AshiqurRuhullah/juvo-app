using System.Security.Cryptography;
using System.Text;
using Juvo.Application.Abstractions;
using Juvo.Application.Carts;
using Juvo.Application.Delivery;
using Juvo.Application.Identity;
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
    bool ConfirmDuplicate, decimal? ExpectedTotal, string? DeviceId, string? Ip, string? OtpCode, bool TermsAccepted, Guid? TargetBranchId, Cart? Cart);

public sealed record PlacedOrder(Order Order, string? TrackingToken, bool Replayed, DateOnly? EarliestDelivery, DateOnly? LatestDelivery);

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
    IApplicationDbContext db, IUnitOfWork uow, SettingsService settings, OrderStock stock, OrderRouter router, FraudService fraud, DeliveryService delivery, CustomerAuthService otp,
    StockAvailability availability, Payments.PaymentSettingsService paymentSettings, Billing.TaxService taxes, TimeProvider clock)
{
    public const string Cod = "COD";
    public const string SandboxOnline = "SANDBOX";

    public static readonly IReadOnlyList<string> PaymentMethods = [Cod, SandboxOnline];

    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    public static string FingerprintOf(string mobile, IEnumerable<StockLine> lines) =>
        Hash($"{mobile}|{string.Join(',', lines.OrderBy(l => l.VariantId).Select(l => $"{l.VariantId:N}:{l.Quantity}"))}");

    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string NewToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(24)).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    private sealed record Priced(ProductVariant Variant, Product Product, decimal UnitPrice, int Quantity, string? ImagePath);

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

        // 2. Prices from the catalog, never from the client.
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

        var productIds = rows.Select(r => r.Product.Id).Distinct().ToList();
        var images = await db.ProductImages.AsNoTracking().Where(m => productIds.Contains(m.ProductId)).OrderBy(m => m.SortOrder).ToListAsync(cancellationToken);
        var priced = i.Lines.Select(l =>
        {
            var row = rows.First(r => r.Variant.Id == l.VariantId);
            var image = images.Where(m => m.ProductId == row.Product.Id)
                .OrderBy(m => string.Equals(m.ColorName, row.Variant.ColorName, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ThenBy(m => m.SortOrder).FirstOrDefault();
            return new Priced(row.Variant, row.Product, row.Variant.PriceOverride ?? row.Product.EffectivePrice(now), l.Quantity, image?.Path);
        }).ToList();
        var subtotal = priced.Sum(p => p.UnitPrice * p.Quantity);
        var weight = priced.Sum(p => (p.Variant.WeightGramsOverride ?? p.Product.WeightGrams ?? 0) * p.Quantity);

        // 3. Coupon.
        Coupon? coupon = null;
        decimal couponDiscount = 0;
        if (!string.IsNullOrWhiteSpace(i.CouponCode))
        {
            var code = Coupon.Normalize(i.CouponCode);
            coupon = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code, cancellationToken);
            if (coupon is null)
            {
                return Error.NotFound(ErrorCodes.Coupon.NotFound, "This coupon code does not exist.");
            }

            var problem = coupon.Check(now, subtotal);
            if (problem != CouponProblem.None)
            {
                return CouponCodes.ToError(problem, coupon);
            }

            couponDiscount = coupon.DiscountFor(subtotal);
        }

        // 4. Delivery charge: the same calculation as the quote (BR-G2: the charge shown is the charge billed); staff may override it.
        var quote = await delivery.QuoteAmountsAsync(i.Shipping.CountryCode, i.Shipping.DistrictId, i.Shipping.UpazilaId, subtotal - couponDiscount, weight, cancellationToken);
        if (quote.IsFailure)
        {
            return quote.Error!;
        }

        var deliveryCharge = i.DeliveryOverride ?? quote.Value.Total;
        if (i.ManualDiscount < 0 || i.ManualDiscount > subtotal - couponDiscount)
        {
            return Error.Conflict(ErrorCodes.Order.DiscountTooLarge, "The discount cannot be more than the goods are worth.");
        }

        var grand = subtotal - couponDiscount - i.ManualDiscount + deliveryCharge;
        if (i.ExpectedTotal is { } expected && expected != grand)
        {
            return Error.Conflict(ErrorCodes.Order.TotalChanged, $"The total changed to BDT {grand:0.##}. Check the order and confirm again.");
        }

        // 5. Payment method.
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
        var advance = method == Cod && grand > config.CodAdvanceThreshold ? Math.Min(standardCharge, grand) : 0m; // BR-D1

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
        var lines = i.Lines.Where(l => priced.First(p => p.Variant.Id == l.VariantId).Variant.TracksStock).ToList();
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

                var data = new NewOrder(
                    number, reservedIn, i.Source, owner.Id, owner.Mobile, i.Name ?? owner.FullName, i.Email ?? owner.Email, method, rule, quote.Value.ZoneCode, "BDT", subtotal, couponDiscount, coupon?.Code,
                    coupon?.Id, i.ManualDiscount, deliveryCharge, standardCharge, advance, weight, i.Shipping, method == Cod ? null : now.AddMinutes(config.PaymentHoldMinutes), i.IdempotencyKey?.Trim(), fingerprint,
                    Hash(token), i.DeviceId, i.Ip, otpVerified, i.IsStaff ? null : now, i.IsStaff ? i.Who.Id : null, i.Who.Name);
                var items = priced.Select(p => new NewOrderItem(
                    p.Variant.Id, p.Product.Id, p.Product.NameEn, p.Product.NameBn, p.Variant.Sku, p.Variant.ColorName, p.Variant.Size, p.ImagePath, p.UnitPrice, p.Quantity,
                    p.Variant.WeightGramsOverride ?? p.Product.WeightGrams ?? 0, p.Variant.TracksStock)).ToList();
                var placed = Order.Place(data, items, now);
                var (taxRule, tax) = await taxes.ComputeAsync([.. items.Select(x => x.UnitPrice * x.Quantity)], couponDiscount + i.ManualDiscount, deliveryCharge, ct);
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
                if (i.Cart is { } cart)
                {
                    cart.Clear();
                }

                await db.SaveChangesAsync(ct);
                return placed;
            }, cancellationToken);

            return new PlacedOrder(order, token, false, quote.Value.EarliestDelivery, quote.Value.LatestDelivery);
        }
        catch (PlacementRejected rejected)
        {
            return rejected.Error;
        }
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
