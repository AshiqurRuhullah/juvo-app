using Juvo.Application.Abstractions;
using Juvo.Application.Carts;
using Juvo.Application.Delivery;
using Juvo.Domain.Customers;
using Juvo.Domain.Orders;
using Juvo.Shared.Contacts;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Orders;

/// <summary>
/// The shop side of ordering (modules D and F): live checkout totals, placing an order as a guest (a mobile number is enough, FR-D01) or as a signed-in
/// customer, and public tracking by order number and mobile. Everything is calculated again on the server; the browser's numbers are never trusted.
/// </summary>
public sealed class CheckoutService(
    IApplicationDbContext db, ICurrentUser currentUser, CartService carts, DeliveryService delivery, OrderPlacement placement, SettingsService settings, IFileStorage storage,
    Payments.PaymentSettingsService paymentSettings, IUnitOfWork uow, OrderWorkflow workflow, Couriers.ShipmentViews shipments)
{
    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private Guid? SignedInCustomerId => currentUser is { IsAuthenticated: true, SubjectType: "customer", UserId: { } id } ? id : null;

    // ---- Address ----------------------------------------------------------------------------------------

    /// <summary>Checks a typed address (or loads a saved one) and copies it, with the place names, into a snapshot. Shared with the staff order desk.</summary>
    public async Task<Result<ShippingSnapshot>> ResolveAddressAsync(Guid? customerId, Guid? addressId, CheckoutAddress? a, string contactMobile, CancellationToken cancellationToken)
    {
        if (addressId is { } savedId)
        {
            if (customerId is not { } owner)
            {
                return Invalid("addressId", "Sign in to use a saved address.");
            }

            var saved = await db.Addresses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == savedId && x.CustomerId == owner, cancellationToken);
            return saved is null
                ? Error.NotFound(ErrorCodes.Customer.AddressNotFound, "Address not found.")
                : await SnapshotAsync(saved.RecipientName, saved.Phone, saved.CountryCode, saved.DistrictId, saved.UpazilaId, saved.Area, saved.AddressLine, saved.Landmark, saved.StateProvince,
                    saved.City, saved.PostalCode, saved.Latitude, saved.Longitude, cancellationToken);
        }

        if (a is null)
        {
            return Invalid("address", "Enter the delivery address.");
        }

        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(a.RecipientName) || a.RecipientName.Trim().Length > 120)
        {
            errors["address.recipientName"] = ["Enter the name of the person who receives the parcel."];
        }

        var phone = PhoneNumber.Normalize(string.IsNullOrWhiteSpace(a.Phone) ? contactMobile : a.Phone);
        if (phone is null)
        {
            errors["address.phone"] = ["Enter a valid phone number."];
        }

        if (string.IsNullOrWhiteSpace(a.AddressLine) || a.AddressLine.Trim().Length > 300)
        {
            errors["address.addressLine"] = ["Enter the house, road and block (up to 300 characters)."];
        }

        if (a.Latitude is { } lat && (lat is < -90 or > 90) || a.Longitude is { } lon && (lon is < -180 or > 180) || (a.Latitude is null) != (a.Longitude is null))
        {
            errors["address.latitude"] = ["Send both latitude and longitude, within the valid range."];
        }

        var country = string.IsNullOrWhiteSpace(a.CountryCode) ? "BD" : a.CountryCode.Trim().ToUpperInvariant();
        if (!await db.Countries.AnyAsync(c => c.Code == country, cancellationToken))
        {
            errors["address.countryCode"] = ["Choose a country from the list."];
        }
        else if (country == "BD" && a.DistrictId is null)
        {
            errors["address.districtId"] = ["Choose the district."];
        }
        else if (country != "BD" && string.IsNullOrWhiteSpace(a.City))
        {
            errors["address.city"] = ["Enter the city."];
        }

        if (errors.Count > 0)
        {
            return Error.Validation(errors);
        }

        return await SnapshotAsync(a.RecipientName, phone!, country, a.DistrictId, a.UpazilaId, a.Area, a.AddressLine, a.Landmark, a.StateProvince, a.City, a.PostalCode, a.Latitude, a.Longitude,
            cancellationToken);
    }

    private async Task<Result<ShippingSnapshot>> SnapshotAsync(
        string name, string phone, string country, Guid? districtId, Guid? upazilaId, string? area, string line, string? landmark, string? state, string? city, string? postal, double? lat, double? lon,
        CancellationToken cancellationToken)
    {
        Guid? divisionId = null;
        string? divisionName = null, districtName = null, upazilaName = null;
        if (country == "BD" && districtId is { } d)
        {
            var district = await db.Districts.AsNoTracking().FirstOrDefaultAsync(x => x.Id == d, cancellationToken);
            if (district is null)
            {
                return Invalid("address.districtId", "District not found.");
            }

            districtName = district.NameEn;
            divisionId = district.DivisionId;
            divisionName = await db.Divisions.AsNoTracking().Where(x => x.Id == district.DivisionId).Select(x => x.NameEn).FirstOrDefaultAsync(cancellationToken);
            if (upazilaId is { } u)
            {
                var upazila = await db.Upazilas.AsNoTracking().FirstOrDefaultAsync(x => x.Id == u && x.DistrictId == d, cancellationToken);
                if (upazila is null)
                {
                    return Invalid("address.upazilaId", "This upazila does not belong to the district.");
                }

                upazilaName = upazila.NameEn;
            }
        }

        return new ShippingSnapshot(name.Trim(), phone, country, divisionId, divisionName, country == "BD" ? districtId : null, districtName, country == "BD" ? upazilaId : null, upazilaName,
            Clean(area), line.Trim(), Clean(landmark), country == "BD" ? null : Clean(state), Clean(city), Clean(postal), lat, lon);
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // ---- Quote ------------------------------------------------------------------------------------------

    public async Task<Result<CheckoutQuoteDto>> QuoteAsync(string? cartToken, CheckoutQuoteRequest request, CancellationToken cancellationToken)
    {
        var cart = await carts.FindAsync(cartToken, cancellationToken);
        if (cart is null || cart.Items.Count == 0)
        {
            return Error.Conflict(ErrorCodes.Order.CartEmpty, "Your cart is empty.");
        }

        var view = await carts.ViewAsync(cart, null, cancellationToken);
        if (view.Lines.Count == 0)
        {
            return Error.Conflict(ErrorCodes.Order.CartEmpty, "Your cart is empty.");
        }

        string country = "BD";
        Guid? districtId = null, upazilaId = null;
        if (request.AddressId is { } savedId && SignedInCustomerId is { } owner)
        {
            var saved = await db.Addresses.AsNoTracking().FirstOrDefaultAsync(x => x.Id == savedId && x.CustomerId == owner, cancellationToken);
            if (saved is null)
            {
                return Error.NotFound(ErrorCodes.Customer.AddressNotFound, "Address not found.");
            }

            (country, districtId, upazilaId) = (saved.CountryCode, saved.DistrictId, saved.UpazilaId);
        }
        else if (request.Address is { } typed)
        {
            country = string.IsNullOrWhiteSpace(typed.CountryCode) ? "BD" : typed.CountryCode.Trim().ToUpperInvariant();
            (districtId, upazilaId) = (typed.DistrictId, typed.UpazilaId);
        }

        var config = await settings.OrdersAsync(cancellationToken);
        DeliveryQuoteDto? deliveryQuote = null;
        string? deliveryError = null;
        if (country != "BD" || districtId is not null || upazilaId is not null)
        {
            var quote = await delivery.QuoteAmountsAsync(country, districtId, upazilaId, view.Total, view.WeightGrams, cancellationToken);
            if (quote.IsSuccess)
            {
                deliveryQuote = quote.Value;
            }
            else
            {
                deliveryError = quote.Error!.Code;
            }
        }

        var method = string.IsNullOrWhiteSpace(request.PaymentMethod) ? OrderPlacement.Cod : request.PaymentMethod.Trim().ToUpperInvariant();
        var charge = deliveryQuote?.Total ?? 0m;
        var grand = view.Total + charge;
        var standardCharge = deliveryQuote is null ? 0m : deliveryQuote.Total + deliveryQuote.FreeDeliveryWaiver;
        var advance = method == OrderPlacement.Cod && deliveryQuote is not null && grand > config.CodAdvanceThreshold ? Math.Min(standardCharge, grand) : 0m;
        var payNow = method == OrderPlacement.Cod ? advance : grand;

        var verified = SignedInCustomerId is { } id && await db.Customers.AsNoTracking().AnyAsync(c => c.Id == id && c.MobileVerified, cancellationToken);
        var otpRequired = !verified && (config.OtpMode == OtpMode.Always || (config.OtpMode == OtpMode.CodOnly && method == OrderPlacement.Cod));

        var methods = (await paymentSettings.MethodsAsync(country, grand, false, cancellationToken)).Select(m => new PaymentMethodDto(m.Code, m.Name, m.Available, m.Reason)).ToList();
        var problems = view.Lines.Where(l => l.Problem is not null).Select(l => l.Problem!).Distinct().ToList();
        if (view.Coupon?.Problem is { } couponProblem)
        {
            problems.Add(couponProblem);
        }

        var lines = view.Lines.Select(l => new QuoteLineDto(l.VariantId, l.ProductId, l.Name, l.NameBn, l.Sku, l.ColorName, l.Size, l.ImageUrl, l.UnitPrice,
            Math.Min(l.Quantity, l.MaxQuantity), l.LineTotal, l.Problem)).ToList();
        var methodOk = methods.Any(m => m.Code == method && m.Available);
        return new CheckoutQuoteDto("BDT", true, lines, view.ItemCount, view.Subtotal, view.Coupon?.Code, view.Discount, deliveryQuote, deliveryError, charge, grand, method, advance, payNow,
            grand - payNow, otpRequired, methods, problems, view.ItemCount > 0 && deliveryQuote is not null && methodOk && problems.Count == 0);
    }

    // ---- Checkout ---------------------------------------------------------------------------------------

    public async Task<Result<CheckoutResultDto>> CheckoutAsync(string? cartToken, CheckoutRequest request, CancellationToken cancellationToken)
    {
        Customer? signedIn = SignedInCustomerId is { } id ? await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken) : null;
        var mobile = signedIn?.Mobile ?? PhoneNumber.Normalize(request.Contact?.Mobile);
        if (mobile is null)
        {
            return new Error(ErrorCodes.Auth.InvalidMobile, "Enter a valid mobile number, for example 01712345678.", ErrorType.Validation,
                new Dictionary<string, string[]> { ["contact.mobile"] = ["Enter a valid mobile number."] });
        }

        var name = Clean(request.Contact?.FullName) ?? signedIn?.FullName ?? Clean(request.Address?.RecipientName);
        if (name is { Length: > 120 })
        {
            return Invalid("contact.fullName", "The name can have at most 120 characters.");
        }

        var email = Clean(request.Contact?.Email) ?? signedIn?.Email;
        if (!string.IsNullOrEmpty(email) && !(email.Length <= 254 && System.Net.Mail.MailAddress.TryCreate(email, out var parsed) && parsed.Address == email && email.Contains('.', StringComparison.Ordinal)))
        {
            return Invalid("contact.email", "Enter a valid email address.");
        }

        var key = Clean(request.IdempotencyKey);
        if (key is { Length: > 80 })
        {
            return Invalid("idempotencyKey", "The idempotency key can have at most 80 characters.");
        }

        var cart = await carts.FindAsync(cartToken, cancellationToken);
        if (cart is null || cart.Items.Count == 0)
        {
            // A repeated request after success finds the cart already emptied: answer with the order the key belongs to.
            if (key is not null)
            {
                var previous = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().FirstOrDefaultAsync(o => o.IdempotencyKey == key && o.CustomerMobile == mobile, cancellationToken);
                if (previous is not null)
                {
                    return ToResult(previous, null, true, null, null);
                }
            }

            return Error.Conflict(ErrorCodes.Order.CartEmpty, "Your cart is empty.");
        }

        var shipping = await ResolveAddressAsync(signedIn?.Id, request.AddressId, request.Address, mobile, cancellationToken);
        if (shipping.IsFailure)
        {
            return shipping.Error!;
        }

        string? couponCode = null;
        if (cart.CouponId is { } couponId)
        {
            couponCode = await db.Coupons.AsNoTracking().Where(c => c.Id == couponId).Select(c => c.Code).FirstOrDefaultAsync(cancellationToken);
        }

        var lines = cart.Items.GroupBy(i => i.VariantId).Select(g => new StockLine(g.Key, g.Sum(i => i.Quantity))).ToList();
        var input = new PlacementInput(
            OrderSource.Web, signedIn is null ? Who.System with { Name = name ?? "Guest" } : Who.Customer(signedIn.Id, signedIn.FullName), false, mobile, name, email, signedIn?.Id,
            signedIn?.MobileVerified == true, shipping.Value, lines, couponCode, 0m, null, request.PaymentMethod, Clean(request.Note), null, key, request.ConfirmDuplicate, request.ExpectedTotal,
            Clean(request.DeviceId), currentUser.IpAddress, request.OtpCode, request.AcceptTerms, null, cart);
        var placed = await placement.PlaceAsync(input, cancellationToken);
        if (placed.IsFailure)
        {
            return placed.Error!;
        }

        if (signedIn is not null && request.SaveAddress && request.Address is { } toSave && !placed.Value.Replayed)
        {
            await SaveAddressAsync(signedIn.Id, shipping.Value, toSave, cancellationToken);
        }

        var p = placed.Value;
        return ToResult(p.Order, p.TrackingToken, p.Replayed, p.EarliestDelivery, p.LatestDelivery);
    }

    private async Task SaveAddressAsync(Guid customerId, ShippingSnapshot s, CheckoutAddress a, CancellationToken cancellationToken)
    {
        if (await db.Addresses.CountAsync(x => x.CustomerId == customerId, cancellationToken) >= CustomerAccountLimit)
        {
            return;
        }

        var address = new Address(customerId, s.CountryCode);
        address.Set(null, s.RecipientName, s.Phone, s.CountryCode, s.DivisionId, s.DistrictId, s.UpazilaId, s.Area, s.AddressLine, s.Landmark, s.StateProvince, s.City, s.PostalCode, a.Latitude, a.Longitude);
        address.SetDefault(!await db.Addresses.AnyAsync(x => x.CustomerId == customerId, cancellationToken));
        db.Addresses.Add(address);
        await db.SaveChangesAsync(cancellationToken);
    }

    private const int CustomerAccountLimit = Customers.CustomerAccountService.MaxAddresses;

    private CheckoutResultDto ToResult(Order o, string? token, bool replayed, DateOnly? earliest, DateOnly? latest)
    {
        var payNow = o.IsOnlinePayment ? o.GrandTotal : o.AdvanceRequired;
        return new CheckoutResultDto(o.Id, o.Number, token, o.Status.ToString(), o.PaymentStatus.ToString(), o.PaymentMethod, o.GrandTotal, o.AdvanceRequired, payNow, o.GrandTotal - payNow,
            earliest, latest, !replayed && SignedInCustomerId is null, replayed, o.PaymentHoldExpiresAt);
    }

    // ---- Public tracking --------------------------------------------------------------------------------

    /// <summary>Order number plus the mobile used at checkout, or the tracking token shown at checkout. Anything that does not match is the same neutral "not found".</summary>
    public async Task<Result<TrackingDto>> TrackAsync(TrackRequest request, CancellationToken cancellationToken)
    {
        var order = await FindTrackedAsync(request, false, cancellationToken);
        return order is null ? TrackNotFound() : await ToTrackingAsync(order, cancellationToken);
    }

    private static Error TrackNotFound() => Error.NotFound(ErrorCodes.Order.NotFound, "We could not find an order with these details.");

    private async Task<TrackingDto> ToTrackingAsync(Order order, CancellationToken cancellationToken) =>
        new(order.Number, order.Status, order.PaymentStatus, order.DeliveryStatus, order.PaymentMethod, order.PlacedAt, order.ShipDistrict ?? order.ShipCity, order.GrandTotal,
            OrderViews.Lines(order, storage), OrderViews.CustomerTimeline(order), OrderViews.CanCancel(order), await shipments.TrackingOfOrderAsync(order.Id, cancellationToken));

    /// <summary>Finds an order from what the public holds: number plus mobile, or the tracking token. Null for anything that does not match (always the same neutral answer).</summary>
    internal async Task<Order?> FindTrackedAsync(TrackRequest request, bool forUpdate, CancellationToken cancellationToken)
    {
        IQueryable<Order> query = db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).Include(o => o.History);
        if (forUpdate)
        {
            query = query.Include(o => o.Notes);
        }
        else
        {
            query = query.AsNoTracking();
        }

        if (!string.IsNullOrWhiteSpace(request.Token))
        {
            var hash = OrderPlacement.Hash(request.Token.Trim());
            query = query.Where(o => o.TrackingTokenHash == hash);
        }
        else
        {
            var mobile = PhoneNumber.Normalize(request.Mobile);
            var number = request.Number?.Trim().ToUpperInvariant();
            if (mobile is null || string.IsNullOrEmpty(number))
            {
                return null;
            }

            query = query.Where(o => o.Number == number && o.CustomerMobile == mobile);
        }

        return await query.Where(o => !o.IsDeleted).FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// A guest cancels from the tracking page (no sign-in, no code): proven by order number plus the mobile used at checkout, or by the tracking token, and only while the order may still be
    /// cancelled (Placed, Confirmed or Packed: it has not left the shop). Reserved stock goes back at once and a booked courier parcel is cancelled with it. Rate limited by the caller.
    /// </summary>
    public async Task<Result<TrackingDto>> CancelTrackedAsync(TrackCancelRequest request, CancellationToken cancellationToken)
    {
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Cancelled by the customer" : request.Reason.Trim();
        if (reason.Length > 300)
        {
            return Invalid("reason", "The reason can have at most 300 characters.");
        }

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<TrackingDto>>(async ct =>
            {
                var order = await FindTrackedAsync(new TrackRequest(request.Number, request.Mobile, request.Token), true, ct);
                if (order is null)
                {
                    return TrackNotFound();
                }

                var done = await workflow.CancelAsync(order, reason, Who.Customer(order.CustomerId, order.CustomerName), ct);
                if (done.IsFailure)
                {
                    return done.Error!;
                }

                await db.SaveChangesAsync(ct);
                return await ToTrackingAsync(order, ct);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Order.InvalidTransition, "The order changed at the same moment. Look at it again.");
        }
    }
}

/// <summary>The signed-in customer's own orders (FR-F02, FR-F04). Every query is filtered by the customer id in the token.</summary>
public sealed class CustomerOrdersService(IApplicationDbContext db, IUnitOfWork uow, ICurrentUser currentUser, OrderWorkflow workflow, IFileStorage storage, Couriers.ShipmentViews shipments)
{
    private Guid? CustomerId => currentUser is { IsAuthenticated: true, SubjectType: "customer", UserId: { } id } ? id : null;

    private static readonly OrderStatus[] ActiveStatuses =
    [
        OrderStatus.Placed, OrderStatus.Confirmed, OrderStatus.Packed, OrderStatus.HandedToCourier, OrderStatus.InTransit, OrderStatus.OutForDelivery, OrderStatus.DeliveryFailed,
    ];

    public async Task<Result<PagedResult<MyOrderListItemDto>>> ListAsync(PageRequest request, string? filter, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var query = db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Include(o => o.Items).Where(o => o.CustomerId == id && !o.IsDeleted);
        query = filter?.Trim().ToLowerInvariant() switch
        {
            "active" => query.Where(o => ActiveStatuses.Contains(o.Status)),
            "delivered" => query.Where(o => o.Status == OrderStatus.Delivered),
            "cancelled" => query.Where(o => o.Status == OrderStatus.Cancelled),
            "returned" => query.Where(o => o.Status == OrderStatus.ReturnInProgress || o.Status == OrderStatus.Returned),
            _ => query,
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(o => o.PlacedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var items = rows.Select(o =>
        {
            var first = o.Items.OrderBy(i => i.CreatedAt).ThenBy(i => i.Sku).FirstOrDefault();
            return new MyOrderListItemDto(o.Id, o.Number, o.Status, o.PaymentStatus, o.DeliveryStatus, o.PlacedAt, o.GrandTotal, o.Items.Sum(i => i.Quantity), first?.Name,
                OrderViews.ImageUrl(first?.ImagePath, storage), OrderViews.CanCancel(o));
        }).ToList();
        return new PagedResult<MyOrderListItemDto>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<MyOrderDto>> GetAsync(Guid orderId, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).AsNoTracking().Include(o => o.Items).Include(o => o.History).Include(o => o.Notes)
            .FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == id && !o.IsDeleted, cancellationToken);
        return order is null ? Error.NotFound(ErrorCodes.Order.NotFound, "Order not found.") : ToDto(order) with { Shipment = await shipments.TrackingOfOrderAsync(order.Id, cancellationToken) };
    }

    private MyOrderDto ToDto(Order o) =>
        new(o.Id, o.Number, o.Status, o.PaymentStatus, o.DeliveryStatus, o.PaymentMethod, o.PlacedAt, OrderViews.Money(o), OrderViews.Shipping(o), OrderViews.Lines(o, storage),
            OrderViews.CustomerTimeline(o), OrderViews.CustomerNotes(o), OrderViews.CanCancel(o), o.PaymentHoldExpiresAt);

    /// <summary>The customer cancels while the order is still in the shop (before courier pickup). Stock goes back at once.</summary>
    public async Task<Result<MyOrderDto>> CancelAsync(Guid orderId, CancelOrderRequest request, CancellationToken cancellationToken)
    {
        if (CustomerId is not { } id)
        {
            return Error.Unauthorized();
        }

        var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Cancelled by the customer" : request.Reason.Trim();
        if (reason.Length > 300)
        {
            return Error.Validation(new Dictionary<string, string[]> { ["reason"] = ["The reason can have at most 300 characters."] });
        }

        var result = await uow.ExecuteInTransactionAsync(async ct =>
        {
            var order = await db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items).Include(o => o.History).Include(o => o.Notes)
                .FirstOrDefaultAsync(o => o.Id == orderId && o.CustomerId == id && !o.IsDeleted, ct);
            if (order is null)
            {
                return Result.Failure<MyOrderDto>(Error.NotFound(ErrorCodes.Order.NotFound, "Order not found."));
            }

            var done = await workflow.CancelAsync(order, reason, Who.Customer(id, order.CustomerName), ct);
            if (done.IsFailure)
            {
                return Result.Failure<MyOrderDto>(done.Error!);
            }

            await db.SaveChangesAsync(ct);
            return Result.Success(ToDto(order) with { Shipment = await shipments.TrackingOfOrderAsync(order.Id, ct) });
        }, cancellationToken);
        return result;
    }
}
