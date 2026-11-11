using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Application.Orders;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Juvo.Application.Payments;

/// <summary>Thrown inside a payment transaction to undo everything and answer with <see cref="Error"/> (the transaction commits on a normal return).</summary>
internal sealed class PaymentRejected(Error error) : Exception(error.Message)
{
    public Error Error { get; } = error;
}

/// <summary>
/// The customer's side of paying (module I): start an online payment (hosted gateway page), follow its status, let the shop check the result when the browser returns, and
/// report a manual bank or mobile transfer. The customer is the signed-in owner of the order, or anyone holding the tracking token shown at checkout. A payment only ever
/// becomes Paid after the gateway itself confirmed it to the server: the browser return just triggers that check.
/// </summary>
public sealed class CustomerPaymentsService(
    IApplicationDbContext db, IUnitOfWork uow, IPaymentGatewayRegistry gateways, PaymentSettingsService methods, PaymentOutcomes outcomes, OrderWorkflow workflow,
    ICurrentUser currentUser, IFileStorage storage, IOptions<AuthOptions> auth, TimeProvider clock)
{
    private const int MaxProofBytes = 5 * 1024 * 1024;

    private static Error NotFound() => Error.NotFound(ErrorCodes.Payment.NotFound, "We could not find this payment or order.");

    private static Error Invalid(string field, string message) => Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private Guid? CustomerId => currentUser is { IsAuthenticated: true, SubjectType: "customer", UserId: { } id } ? id : null;

    private Who WhoIs(Order order) => CustomerId is { } id ? Who.Customer(id, order.CustomerName) : new Who(null, "Customer");

    private async Task<Order?> FindOrderAsync(Guid? id, string? number, string? token, CancellationToken cancellationToken)
    {
        var customerId = CustomerId;
        var hash = string.IsNullOrWhiteSpace(token) ? null : OrderPlacement.Hash(token.Trim());
        if (customerId is null && hash is null)
        {
            return null;
        }

        var query = db.Orders.IgnoreQueryFilters(["BranchScope"]).Include(o => o.Items)
            .Where(o => (customerId != null && o.CustomerId == customerId) || (hash != null && o.TrackingTokenHash == hash));
        if (id is { } orderId)
        {
            query = query.Where(o => o.Id == orderId);
        }
        else if (!string.IsNullOrWhiteSpace(number))
        {
            var normalized = number.Trim().ToUpperInvariant();
            query = query.Where(o => o.Number == normalized);
        }
        else
        {
            return null;
        }

        return await query.FirstOrDefaultAsync(cancellationToken);
    }

    private async Task<(Payment Payment, Order Order)?> FindPaymentAsync(Guid paymentId, string? token, CancellationToken cancellationToken)
    {
        var payment = await db.Payments.IgnoreQueryFilters(["BranchScope"]).Include(p => p.Events).FirstOrDefaultAsync(p => p.Id == paymentId, cancellationToken);
        if (payment is null)
        {
            return null;
        }

        var order = await FindOrderAsync(payment.OrderId, null, token, cancellationToken);
        return order is null ? null : (payment, order);
    }

    internal static PaymentSessionDto Session(Payment p, Order o) =>
        new(p.Id, p.OrderNumber, p.Kind, p.Method, p.Amount, p.Currency, p.Status, p.Status == PaymentStatus.Pending ? p.RedirectUrl : null, p.ExpiresAt, p.ReturnUrl, p.FailureReason,
            o.Status, o.PaymentStatus,
            o.Status == OrderStatus.Placed && o.PaymentStatus is PaymentStatus.Pending or PaymentStatus.Failed && p.Status is PaymentStatus.Failed or PaymentStatus.Cancelled,
            p.Provider == PaymentMethodCatalog.Sandbox && p.Status == PaymentStatus.Pending ? p.ProviderReference : null);

    private string? ValidReturnUrl(string? url, out Error? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        url = url.Trim();
        if (url.Length > 400)
        {
            error = Error.Validation(new Dictionary<string, string[]> { ["returnUrl"] = ["The return address is too long."] });
            return null;
        }

        // A path on the shop site, or an address on the configured front-end origin. Nothing else (no open redirect).
        if (url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal) && !url.Contains('\\', StringComparison.Ordinal))
        {
            return url;
        }

        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && Uri.TryCreate(auth.Value.FrontendBaseUrl, UriKind.Absolute, out var front) &&
            string.Equals(absolute.GetLeftPart(UriPartial.Authority), front.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        error = new Error(ErrorCodes.Payment.ReturnUrlInvalid, "The return address is not allowed. Use a path on the shop (starting with /).", ErrorType.Validation,
            new Dictionary<string, string[]> { ["returnUrl"] = ["Use a path on the shop (starting with /) or an address on the shop website."] });
        return null;
    }

    // ---- Start an online payment ----------------------------------------------------------------------------

    public async Task<Result<PaymentSessionDto>> InitiateAsync(InitiatePaymentRequest request, CancellationToken cancellationToken)
    {
        var returnUrl = ValidReturnUrl(request.ReturnUrl, out var urlError);
        if (urlError is not null)
        {
            return urlError;
        }

        Payment? created = null;
        Order? orderAfter = null;
        try
        {
            var step = await uow.ExecuteInTransactionAsync<Result<(Payment Payment, Order Order, bool NewAttempt)>>(async ct =>
            {
                var order = await FindOrderAsync(request.OrderId, request.OrderNumber, request.TrackingToken, ct);
                if (order is null)
                {
                    return NotFound();
                }

                if (order.Status != OrderStatus.Placed || !order.IsOnlinePayment || order.PaymentStatus is not (PaymentStatus.Pending or PaymentStatus.Failed))
                {
                    return order.PaymentStatus is PaymentStatus.Paid or PaymentStatus.UnderReview
                        ? Error.Conflict(ErrorCodes.Payment.AlreadyPaid, "This order is already paid or its payment is being checked.")
                        : Error.Conflict(ErrorCodes.Payment.OrderNotPayable, "This order cannot be paid online any more.");
                }

                var method = string.IsNullOrWhiteSpace(request.Method) ? order.PaymentMethod : request.Method.Trim().ToUpperInvariant();
                var info = PaymentMethodCatalog.Find(method);
                if (info is not { Kind: PaymentMethodKind.Gateway })
                {
                    return Error.Conflict(ErrorCodes.Payment.MethodUnavailable, "Choose an online payment method.");
                }

                var problem = await methods.ProblemAsync(info.Code, order.ShipCountryCode, order.GrandTotal, ct);
                if (problem is not null)
                {
                    var notSetUp = gateways.Find(info.Code) is not { IsConfigured: true };
                    return Error.Conflict(notSetUp ? ErrorCodes.Payment.NotConfigured : ErrorCodes.Payment.MethodUnavailable, problem);
                }

                var now = clock.GetUtcNow();
                var open = await db.Payments.IgnoreQueryFilters(["BranchScope"]).Include(p => p.Events)
                    .Where(p => p.OrderId == order.Id && p.Status == PaymentStatus.Pending && p.Kind == PaymentKind.Online).OrderByDescending(p => p.CreatedAt).ToListAsync(ct);
                foreach (var attempt in open)
                {
                    if (attempt.Method == info.Code && attempt.RedirectUrl is not null && (attempt.ExpiresAt is null || attempt.ExpiresAt > now))
                    {
                        return (attempt, order, false); // the same payment page is still valid: resume it
                    }

                    attempt.MoveTo(PaymentStatus.Cancelled, "Superseded", "A new payment attempt replaced this one", now, null, "Customer");
                }

                var who = WhoIs(order);
                if (order.PaymentStatus == PaymentStatus.Failed)
                {
                    workflow.ChangePayment(order, PaymentStatus.Pending, "Customer is paying again", who);
                }

                order.ChangePaymentMethod(info.Code, now, who.Id, who.Name);
                var payment = Payment.Start(
                    new PaymentInit(order.Id, order.Number, order.BranchId, PaymentKind.Online, info.Code, info.Code, order.GrandTotal, order.Currency, null, null, returnUrl, order.PaymentHoldExpiresAt ?? now.AddMinutes(30), null, null, null),
                    now, who.Id, who.Name);
                db.Payments.Add(payment);
                await db.SaveChangesAsync(ct);
                return (payment, order, true);
            }, cancellationToken);

            if (step.IsFailure)
            {
                return step.Error!;
            }

            var (payment, order, isNew) = step.Value;
            if (!isNew)
            {
                return Session(payment, order);
            }

            created = payment;
            orderAfter = order;
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Payment.ConcurrentUpdate, "The order was changed at the same moment. Try again.");
        }

        // The gateway call happens outside the database transaction (a real provider is a network call).
        var gateway = gateways.Find(created.Method)!;
        var started = await gateway.InitiateAsync(new GatewayInitiateRequest(created.Id, created.OrderNumber, created.Amount, created.Currency, created.Method, created.ReturnUrl, orderAfter.CustomerMobile, orderAfter.CustomerEmail,
            created.ExpiresAt ?? clock.GetUtcNow().AddMinutes(30)), cancellationToken);
        if (started.IsFailure)
        {
            created.MoveTo(PaymentStatus.Cancelled, "InitiateFailed", started.Error!.Message, clock.GetUtcNow(), null, "System");
            await db.SaveChangesAsync(cancellationToken);
            return Error.Conflict(started.Error.Code == ErrorCodes.Payment.NotConfigured ? ErrorCodes.Payment.NotConfigured : ErrorCodes.Payment.GatewayError, started.Error.Message);
        }

        created.AttachGateway(started.Value.ProviderReference, started.Value.RedirectUrl);
        created.Note("Initiated", $"Sent to {gateway.DisplayName}", clock.GetUtcNow(), null, "System");
        await db.SaveChangesAsync(cancellationToken);
        return Session(created, orderAfter);
    }

    // ---- Status and the browser return ----------------------------------------------------------------------

    public async Task<Result<PaymentSessionDto>> StatusAsync(Guid paymentId, string? token, CancellationToken cancellationToken)
    {
        var found = await FindPaymentAsync(paymentId, token, cancellationToken);
        return found is null ? NotFound() : Session(found.Value.Payment, found.Value.Order);
    }

    /// <summary>
    /// The browser came back from the gateway page. The shop does not believe the browser: it asks the gateway server to server and only a confirmed success makes the payment
    /// Paid. A payment the gateway still calls pending stays pending.
    /// </summary>
    public async Task<Result<PaymentSessionDto>> ConfirmAsync(Guid paymentId, string? token, CancellationToken cancellationToken)
    {
        var found = await FindPaymentAsync(paymentId, token, cancellationToken);
        if (found is null)
        {
            return NotFound();
        }

        var (payment, order) = found.Value;
        if (payment.Status != PaymentStatus.Pending || payment.Provider is null || payment.ProviderReference is null)
        {
            return Session(payment, order);
        }

        var gateway = gateways.Find(payment.Provider);
        if (gateway is not { IsConfigured: true })
        {
            return Error.Conflict(ErrorCodes.Payment.NotConfigured, "This payment method is not set up.");
        }

        var verified = await gateway.VerifyAsync(payment.ProviderReference, cancellationToken);
        if (verified.IsFailure)
        {
            return Error.Conflict(ErrorCodes.Payment.GatewayError, verified.Error!.Message);
        }

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<PaymentSessionDto>>(async ct =>
            {
                var p = await db.Payments.IgnoreQueryFilters(["BranchScope"]).Include(x => x.Events).FirstAsync(x => x.Id == paymentId, ct);
                var o = await outcomes.LoadOrderAsync(p.OrderId, ct) ?? throw new InvalidOperationException("The order of a payment is missing.");
                var who = WhoIs(o);
                var v = verified.Value;
                Result applied = Result.Success();
                switch (v.Outcome)
                {
                    case GatewayOutcome.Succeeded when v.Amount != p.Amount || !string.Equals(v.Currency, p.Currency, StringComparison.OrdinalIgnoreCase):
                        p.Note("AmountMismatch", $"The gateway confirmed {v.Amount} {v.Currency}, expected {p.Amount} {p.Currency}. Not marked paid.", clock.GetUtcNow(), null, "System");
                        applied = Error.Conflict(ErrorCodes.Payment.AmountMismatch, "The amount the gateway received does not match the order. The shop will check it.");
                        break;
                    case GatewayOutcome.Succeeded:
                        applied = await outcomes.PaidAsync(p, o, v.TransactionId, null, "Confirmed by the gateway (browser return)", who, ct);
                        break;
                    case GatewayOutcome.Failed:
                        applied = outcomes.Failed(p, o, v.Detail ?? "The payment failed", null, who);
                        break;
                    case GatewayOutcome.Cancelled:
                        applied = outcomes.Cancelled(p, v.Detail ?? "The payment was cancelled", null, who);
                        break;
                    default:
                        break; // still pending at the gateway
                }

                await db.SaveChangesAsync(ct);
                return applied.IsFailure && applied.Error!.Code == ErrorCodes.Payment.AmountMismatch ? applied.Error : Session(p, o);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            var again = await FindPaymentAsync(paymentId, token, cancellationToken); // a webhook finished first: show what it did
            return again is null ? NotFound() : Session(again.Value.Payment, again.Value.Order);
        }
    }

    // ---- Manual bank or mobile transfer ---------------------------------------------------------------------

    public async Task<Result<PaymentSessionDto>> SubmitManualAsync(ManualPaymentRequest request, CancellationToken cancellationToken)
    {
        var reference = request.Reference?.Trim();
        if (string.IsNullOrEmpty(reference) || reference.Length is < 4 or > 60)
        {
            return new Error(ErrorCodes.Payment.ReferenceRequired, "Write the transaction id of your transfer (4 to 60 characters).", ErrorType.Validation,
                new Dictionary<string, string[]> { ["reference"] = ["Write the transaction id of your transfer (4 to 60 characters)."] });
        }

        var payer = request.PayerName?.Trim();
        if (payer is { Length: > 120 })
        {
            return Invalid("payerName", "Up to 120 characters.");
        }

        try
        {
            return await uow.ExecuteInTransactionAsync<Result<PaymentSessionDto>>(async ct =>
            {
                var order = await FindOrderAsync(request.OrderId, request.OrderNumber, request.TrackingToken, ct);
                if (order is null)
                {
                    return NotFound();
                }

                if (order.Status != OrderStatus.Placed || !order.IsOnlinePayment || order.PaymentStatus is not (PaymentStatus.Pending or PaymentStatus.Failed))
                {
                    return order.PaymentStatus is PaymentStatus.Paid or PaymentStatus.UnderReview
                        ? Error.Conflict(ErrorCodes.Payment.AlreadyPaid, "This order is already paid or its payment is being checked.")
                        : Error.Conflict(ErrorCodes.Payment.OrderNotPayable, "This order cannot be paid any more.");
                }

                var problem = await methods.ProblemAsync(PaymentMethodCatalog.Manual, order.ShipCountryCode, order.GrandTotal, ct);
                if (problem is not null)
                {
                    return Error.Conflict(ErrorCodes.Payment.MethodUnavailable, problem);
                }

                var upper = reference.ToUpperInvariant();
                if (await db.Payments.IgnoreQueryFilters(["BranchScope"]).AnyAsync(p => p.Method == PaymentMethodCatalog.Manual && p.SubmittedReference == upper && p.Status != PaymentStatus.Rejected && p.Status != PaymentStatus.Cancelled, ct))
                {
                    return Error.Conflict(ErrorCodes.Payment.ReferenceReused, "This transaction id was already used for another payment.");
                }

                var now = clock.GetUtcNow();
                var who = WhoIs(order);
                foreach (var attempt in await db.Payments.IgnoreQueryFilters(["BranchScope"]).Where(p => p.OrderId == order.Id && p.Status == PaymentStatus.Pending).ToListAsync(ct))
                {
                    attempt.MoveTo(PaymentStatus.Cancelled, "Superseded", "The customer reported a manual transfer instead", now, who.Id, who.Name);
                }

                if (order.PaymentStatus == PaymentStatus.Failed)
                {
                    workflow.ChangePayment(order, PaymentStatus.Pending, "Customer is paying again", who);
                }

                order.ChangePaymentMethod(PaymentMethodCatalog.Manual, now, who.Id, who.Name);
                var payment = Payment.Start(new PaymentInit(order.Id, order.Number, order.BranchId, PaymentKind.Manual, PaymentMethodCatalog.Manual, null, order.GrandTotal, order.Currency, null, null, null, null, null, null, null), now, who.Id, who.Name);
                payment.SubmitForReview(upper, payer, now, who.Id, who.Name);
                db.Payments.Add(payment);
                workflow.ChangePayment(order, PaymentStatus.UnderReview, $"Transfer reference {upper}", who);
                await db.SaveChangesAsync(ct);
                return Session(payment, order);
            }, cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Payment.ConcurrentUpdate, "The order was changed at the same moment. Try again.");
        }
    }

    /// <summary>Attach a screenshot or receipt (JPEG, PNG, WebP or PDF, up to 5 MB) to a manual payment that is waiting for review.</summary>
    public async Task<Result<PaymentSessionDto>> UploadProofAsync(Guid paymentId, string? token, Stream content, CancellationToken cancellationToken)
    {
        var found = await FindPaymentAsync(paymentId, token, cancellationToken);
        if (found is null)
        {
            return NotFound();
        }

        var (payment, order) = found.Value;
        if (payment.Kind != PaymentKind.Manual || payment.Status != PaymentStatus.UnderReview)
        {
            return Error.Conflict(ErrorCodes.Payment.NotUnderReview, "A proof can only be added to a transfer that is waiting for review.");
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > MaxProofBytes)
            {
                return new Error(ErrorCodes.Payment.ProofInvalid, "The file must be 5 MB or smaller.", ErrorType.Validation, new Dictionary<string, string[]> { ["file"] = ["The file must be 5 MB or smaller."] });
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        var (extension, type) = Sniff(bytes);
        if (extension is null)
        {
            return new Error(ErrorCodes.Payment.ProofInvalid, "The file must be a JPEG, PNG, WebP or PDF.", ErrorType.Validation, new Dictionary<string, string[]> { ["file"] = ["The file must be a JPEG, PNG, WebP or PDF."] });
        }

        var path = $"payment-proofs/{clock.GetUtcNow():yyMM}/{payment.Id:N}-{Guid.NewGuid():N}.{extension}";
        await storage.SaveAsync(path, new MemoryStream(bytes), type!, cancellationToken);
        payment.AttachProof(path, clock.GetUtcNow(), CustomerId, order.CustomerName);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict(ErrorCodes.Payment.ConcurrentUpdate, "The payment was changed at the same moment. Try again.");
        }

        return Session(payment, order);
    }

    internal static (string? Extension, string? ContentType) Sniff(byte[] b) => b switch
    {
        [0xFF, 0xD8, 0xFF, ..] => ("jpg", "image/jpeg"),
        [0x89, 0x50, 0x4E, 0x47, ..] => ("png", "image/png"),
        [0x25, 0x50, 0x44, 0x46, ..] => ("pdf", "application/pdf"),
        [0x52, 0x49, 0x46, 0x46, _, _, _, _, 0x57, 0x45, 0x42, 0x50, ..] => ("webp", "image/webp"),
        _ => (null, null),
    };

    // ---- Public list of methods -----------------------------------------------------------------------------

    public async Task<List<PaymentMethodView>> MethodsAsync(string? country, decimal? amount, CancellationToken cancellationToken) =>
        await methods.MethodsAsync(string.IsNullOrWhiteSpace(country) ? "BD" : country.Trim().ToUpperInvariant(), amount ?? 0m, false, cancellationToken);
}
