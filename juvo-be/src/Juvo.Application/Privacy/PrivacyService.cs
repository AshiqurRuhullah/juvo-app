using System.Text.Json;
using System.Text.Json.Serialization;
using Juvo.Application.Abstractions;
using Juvo.Application.Ums;
using Juvo.Domain.Customers;
using Juvo.Domain.Identity;
using Juvo.Domain.Notifications;
using Juvo.Domain.Orders;
using Juvo.Domain.Payments;
using Juvo.Domain.Platform;
using Juvo.Domain.Returns;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Privacy;

/// <summary>
/// Staff tools for the data-protection rights (FR-AF06, FR-E08, NFR-010): the consent history, a machine-readable export of everything held about a customer, and
/// irreversible anonymisation. Export and anonymisation are never branch scoped (customers are shared) and read every branch's rows. Both are audited under the entity
/// <see cref="AuditEntity"/> with who, whom, when and why; the exported content is never written to the audit trail.
/// </summary>
public sealed class PrivacyService(IApplicationDbContext db, IUnitOfWork uow, ActorContext actors, ICurrentUser currentUser, IFileStorage files, TimeProvider clock)
{
    public const string AuditEntity = "CustomerPrivacy";

    /// <summary>One staff member may export this many customers per hour (a leak guard: a stolen session cannot copy the whole customer base).</summary>
    public const int ExportsPerHour = 10;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private static Error NotFound() => Error.NotFound(ErrorCodes.Customer.NotFound, "Customer not found.");

    private static Error Invalid(string code, string field, string message) =>
        new(code, message, ErrorType.Validation, new Dictionary<string, string[]> { [field] = [message] });

    // ---- Consent history (FR-E07, FR-AF06) -------------------------------------------------------------------

    public async Task<Result<ConsentHistoryDto>> ConsentHistoryAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        if (!await db.Customers.IgnoreQueryFilters().AnyAsync(c => c.Id == customerId, cancellationToken))
        {
            return NotFound();
        }

        var masked = !actor.Access.HasAnywhere(Permissions.Customers.CustomerViewPii);
        var rows = await db.ConsentLogs.AsNoTracking().Where(c => c.CustomerId == customerId).ToListAsync(cancellationToken);
        var items = PrivacyRules.BuildConsentHistory(rows, masked);
        if (!masked && items.Any(i => i.IpAddress is not null))
        {
            db.AuditLogs.Add(new AuditLog(currentUser.UserId, currentUser.BranchId, "Customer", customerId.ToString(), AuditAction.Viewed, null, "Consent history shown with IP addresses.",
                currentUser.CorrelationId, currentUser.IpAddress, clock.GetUtcNow()));
            await db.SaveChangesAsync(cancellationToken);
        }

        return new ConsentHistoryDto(customerId, masked, items);
    }

    // ---- Export ---------------------------------------------------------------------------------------------

    public async Task<Result<PrivacyExportFile>> ExportAsync(Guid customerId, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var now = clock.GetUtcNow();
        var since = now.AddHours(-1);
        var recent = await db.AuditLogs.AsNoTracking().CountAsync(a => a.UserId == actor.Id && a.Entity == AuditEntity && a.Action == AuditAction.Viewed && a.OccurredAt >= since, cancellationToken);
        if (recent >= ExportsPerHour)
        {
            return new Error(ErrorCodes.Privacy.ExportLimit, $"You can export at most {ExportsPerHour} customers per hour. Please wait and try again.", ErrorType.TooManyRequests);
        }

        var customer = await db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);
        if (customer is null)
        {
            return NotFound();
        }

        var document = await BuildExportAsync(customer, now, cancellationToken);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, Json);

        // Who exported whose data, when and how big. Never the content.
        db.AuditLogs.Add(new AuditLog(currentUser.UserId, currentUser.BranchId, AuditEntity, customerId.ToString(), AuditAction.Viewed, null,
            JsonSerializer.Serialize(new { @event = "export", bytes = bytes.Length, format = "json" }), currentUser.CorrelationId, currentUser.IpAddress, now));
        await db.SaveChangesAsync(cancellationToken);

        return new PrivacyExportFile($"customer-data-{customerId:N}-{now:yyyyMMdd}.json", "application/json", bytes);
    }

    private async Task<object> BuildExportAsync(Customer customer, DateTimeOffset now, CancellationToken ct)
    {
        var id = customer.Id;
        var variants = PrivacyRules.MobileVariants(customer.Mobile);
        var email = customer.Email;
        var orders = db.Orders.IgnoreQueryFilters().AsNoTracking().Where(o => o.CustomerId == id);

        var addresses = await db.Addresses.IgnoreQueryFilters().AsNoTracking().Where(a => a.CustomerId == id).OrderBy(a => a.CreatedAt)
            .Select(a => new { a.Id, a.Label, a.RecipientName, a.Phone, a.CountryCode, a.Area, a.AddressLine, a.Landmark, a.StateProvince, a.City, a.PostalCode, a.Latitude, a.Longitude, a.IsDefault, removed = a.IsDeleted, a.CreatedAt })
            .ToListAsync(ct);

        var consentRows = await db.ConsentLogs.AsNoTracking().Where(c => c.CustomerId == id).ToListAsync(ct);
        var history = PrivacyRules.BuildConsentHistory(consentRows, maskIp: false);
        var current = Enum.GetValues<ConsentType>().Select(t => history.FirstOrDefault(h => h.Type == t.ToString()) is { } h ? new { type = t.ToString(), granted = h.NewValue, at = (DateTimeOffset?)h.At } : new { type = t.ToString(), granted = false, at = (DateTimeOffset?)null }).ToList();

        var orderRows = await orders.OrderBy(o => o.PlacedAt).Select(o => new
        {
            o.Id, o.Number, o.Source, o.Status, o.PaymentStatus, o.DeliveryStatus, o.PaymentMethod, o.Currency, o.Subtotal, o.CouponDiscount, o.PromotionDiscount, o.ManualDiscount, o.DeliveryCharge,
            o.TaxAmount, o.GrandTotal, o.PlacedAt, o.CancelledAt, o.CancelReason, o.DeliveredAt, o.CustomerMobile, o.CustomerName, o.CustomerEmail, o.ShipName, o.ShipPhone, o.ShipCountryCode,
            o.ShipDivision, o.ShipDistrict, o.ShipUpazila, o.ShipArea, o.ShipAddressLine, o.ShipLandmark, o.ShipStateProvince, o.ShipCity, o.ShipPostalCode, o.ClientIp, o.DeviceId,
        }).ToListAsync(ct);
        var orderIds = orderRows.Select(o => o.Id).ToList();

        var lines = await db.OrderItems.IgnoreQueryFilters().AsNoTracking().Where(i => orders.Select(o => o.Id).Contains(i.OrderId))
            .Select(i => new { i.OrderId, i.Name, i.NameBn, i.Sku, i.ColorName, i.Size, i.UnitPrice, i.Quantity, i.LineTotal }).ToListAsync(ct);
        var customerNotes = await db.OrderNotes.IgnoreQueryFilters().AsNoTracking().Where(n => n.Kind == NoteKind.Customer && orders.Select(o => o.Id).Contains(n.OrderId))
            .Select(n => new { n.OrderId, n.Text, n.At }).ToListAsync(ct);
        var payments = await db.Payments.IgnoreQueryFilters().AsNoTracking().Where(p => orders.Select(o => o.Id).Contains(p.OrderId))
            .Select(p => new { p.OrderId, p.OrderNumber, p.Kind, p.Method, p.Provider, p.Amount, p.Currency, p.Status, p.PayerName, p.PaidAt, p.CreatedAt }).ToListAsync(ct);
        var refunds = await db.Refunds.IgnoreQueryFilters().AsNoTracking().Where(r => orders.Select(o => o.Id).Contains(r.OrderId))
            .Select(r => new { r.OrderId, r.Amount, r.Currency, r.Mode, r.Status, r.Reason, r.RequestedAt, r.CompletedAt }).ToListAsync(ct);
        var invoices = await db.Invoices.IgnoreQueryFilters().AsNoTracking().Where(i => i.CustomerId == id)
            .Select(i => new { i.Number, i.OrderNumber, i.CustomerName, i.CustomerMobile, i.CustomerEmail, i.BillingAddress, i.Currency, i.Subtotal, i.Discount, i.DeliveryCharge, i.TaxTotal, i.GrandTotal, i.IssuedAt })
            .ToListAsync(ct);
        var returns = await db.ReturnRequests.IgnoreQueryFilters().AsNoTracking().Where(r => r.CustomerId == id)
            .Select(r => new { r.Number, r.OrderNumber, r.Kind, r.Status, r.ReasonCode, r.ReasonText, r.PickupNote, r.CreatedAt }).ToListAsync(ct);

        var tickets = await db.Tickets.IgnoreQueryFilters().AsNoTracking().Where(t => t.CustomerId == id || variants.Contains(t.CustomerMobile))
            .Select(t => new { t.Id, t.Number, t.Subject, t.Status, t.Category, t.OrderNumber, t.CustomerMobile, t.CustomerName, t.CustomerEmail, t.CreatedAt }).ToListAsync(ct);
        var ticketIds = tickets.Select(t => t.Id).ToList();
        // Staff replies are included as text but without the staff member's name (that is the staff member's own data).
        var ticketMessages = await db.TicketMessages.IgnoreQueryFilters().AsNoTracking().Where(m => ticketIds.Contains(m.TicketId)).OrderBy(m => m.At)
            .Select(m => new { m.TicketId, from = m.Kind == Juvo.Domain.Support.TicketMessageKind.Customer ? "customer" : "staff", m.Text, m.At }).ToListAsync(ct);

        var contactMessages = await db.ContactMessages.AsNoTracking().Where(m => (m.Mobile != null && variants.Contains(m.Mobile)) || (email != null && m.Email == email))
            .OrderBy(m => m.ReceivedAt).Select(m => new { m.Name, m.Mobile, m.Email, m.Subject, m.Message, m.Status, m.ReceivedAt }).ToListAsync(ct);

        var wishlist = await (from w in db.WishlistItems.AsNoTracking().Where(w => w.CustomerId == id)
                              join p in db.Products.IgnoreQueryFilters().AsNoTracking() on w.ProductId equals p.Id
                              orderby w.CreatedAt
                              select new { w.ProductId, product = p.NameEn, addedAt = w.CreatedAt }).ToListAsync(ct);

        var account = await db.LoyaltyAccounts.AsNoTracking().Where(a => a.CustomerId == id).Select(a => new { a.PointsBalance, a.CreditBalance }).FirstOrDefaultAsync(ct);
        var points = await db.PointsLedgerEntries.AsNoTracking().Where(e => e.CustomerId == id).OrderBy(e => e.At).Select(e => new { e.Kind, e.Points, e.BalanceAfter, e.Reason, e.OrderNumber, e.ExpiresAt, e.At }).ToListAsync(ct);
        var credit = await db.CreditLedgerEntries.AsNoTracking().Where(e => e.CustomerId == id).OrderBy(e => e.At).Select(e => new { e.Kind, e.Amount, e.BalanceAfter, e.Reason, e.OrderNumber, e.At }).ToListAsync(ct);

        // Message log: who, which template and channel, when and the outcome. Not the text (an OTP text holds a code, other texts may quote other people).
        var notifications = await db.NotificationLogs.AsNoTracking()
            .Where(n => n.CustomerId == id || variants.Contains(n.Recipient) || (email != null && n.Recipient == email)).OrderBy(n => n.SentAt)
            .Select(n => new { n.Channel, n.Recipient, n.Template, n.Category, n.Status, n.SentAt }).ToListAsync(ct);

        var backInStock = await db.BackInStockSubscriptions.AsNoTracking().Where(b => b.CustomerId == id || variants.Contains(b.Mobile))
            .Select(b => new { b.ProductId, b.VariantId, b.Mobile, b.Email, b.NotifiedAt, b.Outcome, b.CreatedAt }).ToListAsync(ct);
        var campaigns = await db.CampaignRecipients.AsNoTracking().Where(r => r.CustomerId == id).Select(r => new { r.CampaignId, r.Status, r.SkipReason, r.ProcessedAt }).ToListAsync(ct);

        var signIns = await db.SignInHistory.AsNoTracking().Where(s => s.SubjectType == SubjectType.Customer && s.SubjectId == id).OrderBy(s => s.OccurredAt)
            .Select(s => new { s.Succeeded, s.FailureReason, s.IpAddress, s.UserAgent, s.OccurredAt }).ToListAsync(ct);

        object ConsentState(ConsentType type)
        {
            var latest = history.FirstOrDefault(h => h.Type == type.ToString());
            return new { granted = latest?.NewValue ?? false, at = latest?.At, source = latest?.Source };
        }

        return new
        {
            export = new { version = 1, generatedAt = now, customerId = id, note = "Everything the shop holds about this customer. Staff-only notes, staff names and message texts of notifications are left out." },
            profile = new { customer.Id, customer.Mobile, customer.Email, customer.FullName, customer.ProfileLevel, customer.MobileVerified, customer.IsActive, customer.PreferredLanguage, customer.CreatedAt, customer.LastLoginAt, customer.AnonymisedAt },
            addresses,
            consents = new { current, history },
            orders = orderRows.Select(o => new
            {
                order = o,
                lines = lines.Where(l => l.OrderId == o.Id),
                customerNotes = customerNotes.Where(n => n.OrderId == o.Id).Select(n => new { n.Text, n.At }),
                payments = payments.Where(p => p.OrderId == o.Id),
                refunds = refunds.Where(r => r.OrderId == o.Id),
            }),
            invoices,
            returns,
            tickets = tickets.Select(t => new { ticket = t, messages = ticketMessages.Where(m => m.TicketId == t.Id).Select(m => new { m.from, m.Text, m.At }) }),
            contactMessages,
            wishlist,
            loyalty = new { account, points, credit },
            notifications,
            marketing = new { sms = ConsentState(ConsentType.MarketingSms), email = ConsentState(ConsentType.MarketingEmail), suppressedByUnsubscribeLink = history.Any(h => h.SourceKind == "UnsubscribeLink" && !h.NewValue), backInStock, campaigns },
            signIns,
        };
    }

    // ---- Anonymisation (FR-E08) ---------------------------------------------------------------------------------

    public static readonly IReadOnlyList<string> KeptRecords =
    [
        "Orders, order lines, totals, taxes and status history (name, mobile, e-mail, street address, map pin, device, IP address and free-text notes are replaced; division, district and upazila names stay).",
        "Invoices and credit notes keep the buyer name, mobile, e-mail and billing address (tax and accounting records).",
        "Payments and refunds keep amounts, method, status, dates and references; the payer name is removed.",
        "Stock movements, courier shipments (name, phone and address replaced) and cash-on-delivery settlements.",
        "Points and store-credit ledgers keep amounts and balances; the free-text reasons are replaced.",
        "The consent history stays (type, value, source, time) without IP addresses, so a withdrawn consent can still be shown.",
        "The audit trail stays (who, what, when); stored personal values in it are blanked.",
    ];

    public async Task<Result<AnonymiseResultDto>> AnonymiseAsync(Guid customerId, AnonymiseCustomerRequest request, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is null)
        {
            return Error.Unauthorized();
        }

        var reason = PrivacyRules.CleanReason(request.Reason);
        if (reason is null)
        {
            return Invalid(ErrorCodes.Privacy.ReasonRequired, "reason", "Give the reason in 5 to 300 characters.");
        }

        if (!PrivacyRules.ConfirmMatches(customerId, request.Confirm))
        {
            return Invalid(ErrorCodes.Privacy.ConfirmMismatch, "confirm", "Type the customer id to confirm. Anonymisation cannot be undone.");
        }

        if (await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == customerId, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Privacy.StaffAccount, "This is a staff account. Staff accounts are deactivated in the user screen, not anonymised here.");
        }

        var customer = await db.Customers.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId, cancellationToken);
        if (customer is null)
        {
            return NotFound();
        }

        if (customer.IsAnonymised)
        {
            return new AnonymiseResultDto(customerId, true, customer.AnonymisedAt!.Value, new AnonymiseCountsDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), KeptRecords, 0, 0m);
        }

        if (await OpenWorkAsync(customerId, cancellationToken) is { } open)
        {
            return Error.Conflict(ErrorCodes.Privacy.OpenOrders, "This customer cannot be anonymised yet: " + open + " Finish or cancel them first.");
        }

        var attachmentPaths = new List<string>();
        var result = await uow.ExecuteInTransactionAsync(ct => ScrubAsync(customerId, reason, attachmentPaths, ct), cancellationToken);

        // The rows are gone; now remove the files (best effort: a missing file is not an error).
        if (result.IsSuccess)
        {
            foreach (var path in attachmentPaths)
            {
                try
                {
                    await files.DeleteAsync(path, cancellationToken);
                }
                catch (IOException)
                {
                    // The file could not be removed; the row is already deleted, so nothing links to it.
                }
            }
        }

        return result;
    }

    /// <summary>A sentence naming what is still open (orders, unsettled cash on delivery, payments to verify, returns, refunds), or null when nothing is.</summary>
    private async Task<string?> OpenWorkAsync(Guid customerId, CancellationToken ct)
    {
        var orders = db.Orders.IgnoreQueryFilters().AsNoTracking().Where(o => o.CustomerId == customerId);
        var open = await orders.CountAsync(o => o.Status != OrderStatus.Delivered && o.Status != OrderStatus.Cancelled && o.Status != OrderStatus.Returned, ct);
        var cod = await orders.CountAsync(o => o.PaymentStatus == PaymentStatus.CollectedByCourier || (o.PaymentMethod == "COD" && o.Status == OrderStatus.Delivered && o.PaymentStatus == PaymentStatus.Pending), ct);
        var review = await orders.CountAsync(o => o.PaymentStatus == PaymentStatus.UnderReview, ct);
        var returns = await db.ReturnRequests.IgnoreQueryFilters().AsNoTracking().CountAsync(r => r.CustomerId == customerId
            && (r.Status == ReturnStatus.Requested || r.Status == ReturnStatus.Approved || r.Status == ReturnStatus.PickupScheduled || r.Status == ReturnStatus.PickedUp
                || r.Status == ReturnStatus.Received || r.Status == ReturnStatus.Inspected), ct);
        var refunds = await db.Refunds.IgnoreQueryFilters().AsNoTracking().CountAsync(r => r.Status == RefundState.Pending && orders.Select(o => o.Id).Contains(r.OrderId), ct);
        var returnRefunds = await db.ReturnRefunds.AsNoTracking().CountAsync(r => r.Status == Juvo.Domain.Returns.RefundStatus.Pending && db.ReturnRequests.IgnoreQueryFilters().Any(q => q.Id == r.ReturnRequestId && q.CustomerId == customerId), ct);

        var parts = new List<string>();
        if (open > 0)
        {
            parts.Add($"{open} open order(s)");
        }

        if (cod > 0)
        {
            parts.Add($"{cod} order(s) with cash on delivery not yet settled");
        }

        if (review > 0)
        {
            parts.Add($"{review} payment(s) waiting for verification");
        }

        if (returns > 0)
        {
            parts.Add($"{returns} open return(s)");
        }

        if (refunds + returnRefunds > 0)
        {
            parts.Add($"{refunds + returnRefunds} refund(s) not yet paid");
        }

        return parts.Count == 0 ? null : string.Join(", ", parts) + ".";
    }

    private async Task<Result<AnonymiseResultDto>> ScrubAsync(Guid id, string reason, List<string> attachmentPaths, CancellationToken ct)
    {
        attachmentPaths.Clear();
        var now = clock.GetUtcNow();
        var customer = await db.Customers.IgnoreQueryFilters().FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null)
        {
            return NotFound();
        }

        if (customer.IsAnonymised)
        {
            return new AnonymiseResultDto(id, true, customer.AnonymisedAt!.Value, new AnonymiseCountsDto(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0), KeptRecords, 0, 0m);
        }

        var oldMobile = customer.Mobile;
        var oldEmail = string.IsNullOrWhiteSpace(customer.Email) ? null : customer.Email;
        var variants = PrivacyRules.MobileVariants(oldMobile);
        var placeholder = PrivacyPlaceholders.MobileFor(id);
        const string name = PrivacyPlaceholders.Name;
        const string text = PrivacyPlaceholders.Text;
        const string address = PrivacyPlaceholders.Address;
        const string body = PrivacyPlaceholders.Body;

        var account = await db.LoyaltyAccounts.AsNoTracking().Where(a => a.CustomerId == id).Select(a => new { a.PointsBalance, a.CreditBalance }).FirstOrDefaultAsync(ct);

        // Ids kept in memory only to blank the audit trail afterwards.
        var orderIds = await db.Orders.IgnoreQueryFilters().Where(o => o.CustomerId == id).Select(o => o.Id).ToListAsync(ct);
        var orders = db.Orders.IgnoreQueryFilters().Where(o => o.CustomerId == id);
        var orderKeys = orders.Select(o => o.Id);
        var ticketIds = await db.Tickets.IgnoreQueryFilters().Where(t => t.CustomerId == id || variants.Contains(t.CustomerMobile)).Select(t => t.Id).ToListAsync(ct);
        var returnIds = await db.ReturnRequests.IgnoreQueryFilters().Where(r => r.CustomerId == id).Select(r => r.Id).ToListAsync(ct);
        var paymentIds = await db.Payments.IgnoreQueryFilters().Where(p => orderKeys.Contains(p.OrderId)).Select(p => p.Id).ToListAsync(ct);
        var addressIds = await db.Addresses.IgnoreQueryFilters().Where(a => a.CustomerId == id).Select(a => a.Id).ToListAsync(ct);
        var contactIds = await db.ContactMessages.Where(m => (m.Mobile != null && variants.Contains(m.Mobile)) || (oldEmail != null && m.Email == oldEmail)).Select(m => m.Id).ToListAsync(ct);

        // Orders keep totals, status and history; the person is removed.
        var ordersScrubbed = await orders.ExecuteUpdateAsync(s => s
            .SetProperty(o => o.CustomerMobile, placeholder).SetProperty(o => o.CustomerName, name).SetProperty(o => o.CustomerEmail, (string?)null)
            .SetProperty(o => o.ShipName, name).SetProperty(o => o.ShipPhone, placeholder).SetProperty(o => o.ShipArea, (string?)null).SetProperty(o => o.ShipAddressLine, address)
            .SetProperty(o => o.ShipLandmark, (string?)null).SetProperty(o => o.ShipCity, (string?)null).SetProperty(o => o.ShipPostalCode, (string?)null)
            .SetProperty(o => o.ShipLatitude, (double?)null).SetProperty(o => o.ShipLongitude, (double?)null)
            .SetProperty(o => o.DeviceId, (string?)null).SetProperty(o => o.ClientIp, (string?)null).SetProperty(o => o.Fingerprint, string.Empty).SetProperty(o => o.TrackingTokenHash, string.Empty)
            .SetProperty(o => o.CancelReason, o => o.CancelReason == null ? null : text), ct);
        var otherRows = 0;
        otherRows += await db.OrderNotes.IgnoreQueryFilters().Where(n => orderKeys.Contains(n.OrderId)).ExecuteUpdateAsync(s => s.SetProperty(n => n.Text, text), ct);
        otherRows += await db.FraudReviewLogs.IgnoreQueryFilters().Where(f => orderKeys.Contains(f.OrderId)).ExecuteUpdateAsync(s => s.SetProperty(f => f.Note, f => f.Note == null ? null : text), ct);

        var shipments = await db.Shipments.IgnoreQueryFilters().Where(s => orderKeys.Contains(s.OrderId))
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.DeliveryName, name).SetProperty(x => x.DeliveryPhone, placeholder).SetProperty(x => x.DeliveryAddress, address).SetProperty(x => x.Note, x => x.Note == null ? null : text), ct);
        var payments = await db.Payments.IgnoreQueryFilters().Where(p => orderKeys.Contains(p.OrderId)).ExecuteUpdateAsync(s => s.SetProperty(p => p.PayerName, (string?)null), ct);

        var returns = await db.ReturnRequests.IgnoreQueryFilters().Where(r => r.CustomerId == id)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.CustomerMobile, placeholder).SetProperty(r => r.CustomerName, name)
                .SetProperty(r => r.ReasonText, r => r.ReasonText == null ? null : text).SetProperty(r => r.PickupNote, r => r.PickupNote == null ? null : text), ct);
        otherRows += await db.ReturnNotes.IgnoreQueryFilters().Where(n => db.ReturnRequests.IgnoreQueryFilters().Any(r => r.Id == n.ReturnRequestId && r.CustomerId == id)).ExecuteUpdateAsync(s => s.SetProperty(n => n.Text, text), ct);

        var tickets = await db.Tickets.IgnoreQueryFilters().Where(t => ticketIds.Contains(t.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.CustomerMobile, placeholder).SetProperty(t => t.CustomerName, name).SetProperty(t => t.CustomerEmail, (string?)null).SetProperty(t => t.Subject, text), ct);
        var ticketMessages = await db.TicketMessages.IgnoreQueryFilters().Where(m => ticketIds.Contains(m.TicketId))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Text, text).SetProperty(m => m.AuthorName, m => m.Kind == Juvo.Domain.Support.TicketMessageKind.Customer ? name : m.AuthorName), ct);
        otherRows += await db.TicketStatusHistories.IgnoreQueryFilters().Where(h => ticketIds.Contains(h.TicketId)).ExecuteUpdateAsync(s => s.SetProperty(h => h.Reason, h => h.Reason == null ? null : text), ct);
        attachmentPaths.AddRange(await db.TicketAttachments.IgnoreQueryFilters().Where(a => db.TicketMessages.IgnoreQueryFilters().Any(m => m.Id == a.MessageId && ticketIds.Contains(m.TicketId))).Select(a => a.Path).ToListAsync(ct));
        otherRows += await db.TicketAttachments.IgnoreQueryFilters().Where(a => db.TicketMessages.IgnoreQueryFilters().Any(m => m.Id == a.MessageId && ticketIds.Contains(m.TicketId))).ExecuteDeleteAsync(ct);

        var contacts = await db.ContactMessages.Where(m => contactIds.Contains(m.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Name, name).SetProperty(m => m.Mobile, (string?)null).SetProperty(m => m.Email, (string?)null)
                .SetProperty(m => m.Subject, m => m.Subject == null ? null : text).SetProperty(m => m.Message, text).SetProperty(m => m.InternalNote, m => m.InternalNote == null ? null : text).SetProperty(m => m.IpHash, (string?)null), ct);

        // Messages: queued ones are stopped, then every record of the customer loses recipient and text.
        var notificationScope = db.NotificationLogs.Where(n => n.CustomerId == id || variants.Contains(n.Recipient) || (oldEmail != null && n.Recipient == oldEmail));
        await notificationScope.Where(n => n.Status == NotificationStatus.Pending || n.Status == NotificationStatus.Failed)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, NotificationStatus.Skipped).SetProperty(n => n.LastError, "Customer anonymised."), ct);
        var notifications = await notificationScope.ExecuteUpdateAsync(s => s.SetProperty(n => n.Recipient, "anonymised").SetProperty(n => n.Subject, n => n.Subject == null ? null : body)
            .SetProperty(n => n.Body, body).SetProperty(n => n.ProviderResponse, (string?)null).SetProperty(n => n.LastError, n => n.LastError == "Customer anonymised." ? n.LastError : null), ct);

        otherRows += await db.CampaignRecipients.Where(r => r.CustomerId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Contact, (string?)null), ct);
        otherRows += await db.BackInStockSubscriptions.Where(b => b.CustomerId == id || variants.Contains(b.Mobile)).ExecuteDeleteAsync(ct);

        var loyaltyRows = await db.PointsLedgerEntries.Where(e => e.CustomerId == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Reason, text), ct);
        loyaltyRows += await db.CreditLedgerEntries.Where(e => e.CustomerId == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Reason, text), ct);
        loyaltyRows += await db.GiftCardEntries.Where(e => e.CustomerId == id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Note, (string?)null), ct);
        loyaltyRows += await db.GiftCards.Where(g => g.RedeemedByCustomerId == id || (g.RecipientMobile != null && variants.Contains(g.RecipientMobile)))
            .ExecuteUpdateAsync(s => s.SetProperty(g => g.RecipientMobile, (string?)null).SetProperty(g => g.RecipientEmail, (string?)null).SetProperty(g => g.Note, (string?)null), ct);
        loyaltyRows += await db.Referrals.Where(r => r.ReferrerId == id || r.RefereeId == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.Note, (string?)null), ct);

        // Everything the customer saved or signed in with is removed outright.
        var wishlist = await db.WishlistItems.Where(w => w.CustomerId == id).ExecuteDeleteAsync(ct);
        var addresses = await db.Addresses.IgnoreQueryFilters().Where(a => a.CustomerId == id).ExecuteDeleteAsync(ct);
        var carts = await db.Carts.Where(c => c.CustomerId == id).ExecuteDeleteAsync(ct);
        var sessions = await db.RefreshTokens.Where(t => t.SubjectType == SubjectType.Customer && t.SubjectId == id).ExecuteDeleteAsync(ct);
        var otp = await db.OtpCodes.Where(o => variants.Contains(o.Mobile)).ExecuteDeleteAsync(ct);
        var signIns = await db.SignInHistory.Where(h => (h.SubjectType == SubjectType.Customer && h.SubjectId == id) || variants.Contains(h.Identifier) || (oldEmail != null && h.Identifier == oldEmail))
            .ExecuteUpdateAsync(s => s.SetProperty(h => h.Identifier, "anonymised").SetProperty(h => h.IpAddress, (string?)null).SetProperty(h => h.UserAgent, (string?)null), ct);
        var consents = await db.ConsentLogs.Where(c => c.CustomerId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.IpAddress, (string?)null), ct);

        var idText = id.ToString();
        var outbox = await db.OutboxMessages.Where(m => m.Status == OutboxStatus.Succeeded && m.Payload.Contains(idText)).ExecuteUpdateAsync(s => s.SetProperty(m => m.Payload, "{}"), ct);

        // The customer row last (its change writes an audit row with the old values), then the audit trail is blanked, then our own entry is written.
        customer.Anonymise(now);
        await db.SaveChangesAsync(ct);

        var auditRows = await BlankAuditAsync("Customer", [id], ct);
        auditRows += await BlankAuditAsync("Address", addressIds, ct);
        auditRows += await BlankAuditAsync("Order", orderIds, ct);
        auditRows += await BlankAuditAsync("Payment", paymentIds, ct);
        auditRows += await BlankAuditAsync("Ticket", ticketIds, ct);
        auditRows += await BlankAuditAsync("ReturnRequest", returnIds, ct);
        auditRows += await BlankAuditAsync("ContactMessage", contactIds, ct);

        var counts = new AnonymiseCountsDto(ordersScrubbed, returns, tickets, ticketMessages, contacts, notifications, shipments, payments, auditRows, outbox, addresses, wishlist, sessions, carts, otp, signIns, consents, loyaltyRows, otherRows);
        db.AuditLogs.Add(new AuditLog(currentUser.UserId, currentUser.BranchId, AuditEntity, id.ToString(), AuditAction.Updated, null,
            JsonSerializer.Serialize(new { @event = "anonymise", reason, orders = ordersScrubbed, tickets, contacts }), currentUser.CorrelationId, currentUser.IpAddress, now));
        await db.SaveChangesAsync(ct);

        return new AnonymiseResultDto(id, false, now, counts, KeptRecords, account?.PointsBalance ?? 0, account?.CreditBalance ?? 0m);
    }

    /// <summary>Overwrites the stored old and new values of audit rows about the given rows (they hold names, numbers and addresses). Who, what and when stay.</summary>
    private async Task<int> BlankAuditAsync(string entity, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        var total = 0;
        foreach (var chunk in ids.Select(i => i.ToString()).Chunk(500))
        {
            var keys = chunk.ToList();
            total += await db.AuditLogs.Where(a => a.Entity == entity && a.Action != AuditAction.Viewed && keys.Contains(a.EntityId))
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.OldValue, a => a.OldValue == null ? null : PrivacyPlaceholders.AuditJson)
                    .SetProperty(a => a.NewValue, a => a.NewValue == null ? null : PrivacyPlaceholders.AuditJson), ct);
        }

        return total;
    }
}
