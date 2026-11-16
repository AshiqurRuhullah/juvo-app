using System.Net.Mail;
using Juvo.Application.Abstractions;
using Juvo.Application.Storefront;
using Juvo.Domain.Customers;
using Juvo.Domain.Marketing;
using Juvo.Domain.Notifications;
using Juvo.Shared.Contacts;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Marketing;

/// <summary>The public unsubscribe link (FR-AC02): turns the marketing consent of one channel off, with the time, and is idempotent.</summary>
public sealed class UnsubscribeService(IApplicationDbContext db, IUnsubscribeTokens tokens, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task<Result<UnsubscribeDto>> ApplyAsync(string? token, CancellationToken cancellationToken)
    {
        if (tokens.Verify(token) is not { } claims)
        {
            return new Error(ErrorCodes.Marketing.UnsubscribeTokenInvalid, "This unsubscribe link is not valid.", ErrorType.Validation, new Dictionary<string, string[]> { ["token"] = ["The unsubscribe link is damaged or incomplete."] });
        }

        var type = claims.Channel == NotificationChannel.Email ? ConsentType.MarketingEmail : ConsentType.MarketingSms;
        var channel = claims.Channel == NotificationChannel.Email ? "email" : "sms";
        if (!await db.Customers.IgnoreQueryFilters().AnyAsync(c => c.Id == claims.CustomerId, cancellationToken))
        {
            return new Error(ErrorCodes.Marketing.UnsubscribeTokenInvalid, "This unsubscribe link is not valid.", ErrorType.Validation, new Dictionary<string, string[]> { ["token"] = ["The unsubscribe link is damaged or incomplete."] });
        }

        var latest = await db.ConsentLogs.AsNoTracking().Where(l => l.CustomerId == claims.CustomerId && l.Type == type).OrderByDescending(l => l.At).ThenByDescending(l => l.Id)
            .Select(l => (bool?)l.Granted).FirstOrDefaultAsync(cancellationToken);
        if (latest == false)
        {
            return new UnsubscribeDto(channel, true, true);
        }

        var source = claims.CampaignId is { } campaign ? $"unsubscribe:{campaign:N}" : "unsubscribe-link";
        db.ConsentLogs.Add(new ConsentLog(claims.CustomerId, type, false, source, currentUser.IpAddress, clock.GetUtcNow()));
        await db.SaveChangesAsync(cancellationToken);
        return new UnsubscribeDto(channel, true, false);
    }
}

/// <summary>"Tell me when it is back" for a sold-out variant (FR-AC03 b). The message itself is sent by the automation run once stock is available.</summary>
public sealed class BackInStockService(IApplicationDbContext db, StockAvailability stock, MarketingSettingsService settingsService, ICurrentUser currentUser)
{
    public async Task<Result<NotifyMeDto>> SubscribeAsync(string slug, NotifyMeRequest r, CancellationToken cancellationToken)
    {
        var normalized = (slug ?? string.Empty).Trim().ToLowerInvariant();
        var product = await db.Products.AsNoTracking().Where(p => p.Slug == normalized && p.Status == Juvo.Domain.Catalog.ProductStatus.Published)
            .Select(p => new { p.Id, Variants = p.Variants.Where(v => v.IsActive).Select(v => new { v.Id, v.TracksStock }).ToList() }).FirstOrDefaultAsync(cancellationToken);
        if (product is null)
        {
            return Error.NotFound(ErrorCodes.Marketing.VariantNotFound, "Product not found.");
        }

        var variant = r.VariantId is { } wanted
            ? product.Variants.FirstOrDefault(v => v.Id == wanted)
            : product.Variants.Count == 1 ? product.Variants[0] : null;
        if (variant is null)
        {
            return Error.NotFound(ErrorCodes.Marketing.VariantNotFound, r.VariantId is null ? "Choose the size and colour you want to be told about." : "This variant does not belong to the product.");
        }

        var available = (await stock.AvailableAsync([variant.Id], cancellationToken)).GetValueOrDefault(variant.Id);
        if (!variant.TracksStock || available > 0)
        {
            return Error.Conflict(ErrorCodes.Marketing.VariantInStock, "This item is in stock, so you can order it now.");
        }

        string? mobile;
        string? email = string.IsNullOrWhiteSpace(r.Email) ? null : r.Email.Trim();
        Guid? customerId = null;
        if (currentUser is { SubjectType: "customer", UserId: { } me })
        {
            var customer = await db.Customers.AsNoTracking().Where(c => c.Id == me).Select(c => new { c.Id, c.Mobile, c.Email }).FirstOrDefaultAsync(cancellationToken);
            if (customer is null)
            {
                return Error.Unauthorized();
            }

            customerId = customer.Id;
            mobile = customer.Mobile;
            email ??= customer.Email;
        }
        else
        {
            mobile = PhoneNumber.Normalize(r.Mobile);
            if (mobile is null)
            {
                return new Error(ErrorCodes.Marketing.ContactRequired, "Give the mobile number to tell.", ErrorType.Validation, new Dictionary<string, string[]> { ["mobile"] = ["Give a valid mobile number."] });
            }

            customerId = await db.Customers.AsNoTracking().Where(c => c.Mobile == mobile).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(cancellationToken);
        }

        if (email is not null && (email.Length > 200 || !MailAddress.TryCreate(email, out _)))
        {
            return new Error(ErrorCodes.Marketing.ContactRequired, "The email address is not valid.", ErrorType.Validation, new Dictionary<string, string[]> { ["email"] = ["Give a valid email address."] });
        }

        var existing = await db.BackInStockSubscriptions.FirstOrDefaultAsync(s => s.VariantId == variant.Id && s.Mobile == mobile && s.NotifiedAt == null, cancellationToken);
        if (existing is not null)
        {
            existing.SetEmail(email);
            await db.SaveChangesAsync(cancellationToken);
            return new NotifyMeDto(existing.Id, variant.Id, true);
        }

        var limit = (await settingsService.ReadAsync(cancellationToken)).MaxNotifyMePerMobile;
        if (await db.BackInStockSubscriptions.CountAsync(s => s.Mobile == mobile && s.NotifiedAt == null, cancellationToken) >= limit)
        {
            return new Error(ErrorCodes.Marketing.SubscriptionLimit, $"This number already waits for {limit} items. Please wait until some are back.", ErrorType.TooManyRequests);
        }

        var created = new BackInStockSubscription(product.Id, variant.Id, mobile, email, customerId);
        db.BackInStockSubscriptions.Add(created);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Two identical requests at once: the unique index kept one row, report that one.
            db.DiscardUnsavedAdditions();
            var winner = await db.BackInStockSubscriptions.AsNoTracking().FirstAsync(s => s.VariantId == variant.Id && s.Mobile == mobile && s.NotifiedAt == null, cancellationToken);
            return new NotifyMeDto(winner.Id, variant.Id, true);
        }

        return new NotifyMeDto(created.Id, variant.Id, false);
    }
}
