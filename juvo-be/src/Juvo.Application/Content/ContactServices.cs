using System.Security.Cryptography;
using System.Text;
using Juvo.Application.Abstractions;
using Juvo.Application.Customers;
using Juvo.Application.Ums;
using Juvo.Domain.Content;
using Juvo.Domain.Platform;
using Juvo.Shared.Contacts;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Content;

/// <summary>Validation of the public contact form (FR-Q06). Plain text only: markup is rejected, never stored.</summary>
public static class ContactRules
{
    public const int NameMax = 120;
    public const int SubjectMax = 200;
    public const int MessageMin = 5;
    public const int MessageMax = 4000;
    public const int SourcePageMax = 200;
    public const int PerContactPerHour = 3;
    public const int PerClientPerHour = 10;

    /// <summary>Returns the field errors (empty when valid) and the cleaned mobile and email.</summary>
    public static (Dictionary<string, string[]> Errors, string? Mobile, string? Email) Validate(ContactFormRequest r)
    {
        var errors = new Dictionary<string, string[]>();
        var name = r.Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            errors["name"] = ["Enter your name."];
        }
        else if (name.Length > NameMax)
        {
            errors["name"] = [$"At most {NameMax} characters."];
        }
        else if (ContentRules.LooksLikeMarkup(name))
        {
            errors["name"] = ["HTML is not allowed."];
        }

        string? mobile = null;
        if (!string.IsNullOrWhiteSpace(r.Mobile))
        {
            mobile = PhoneNumber.Normalize(r.Mobile);
            if (mobile is null)
            {
                errors["mobile"] = ["Enter a valid mobile number."];
            }
        }

        string? email = null;
        if (!string.IsNullOrWhiteSpace(r.Email))
        {
            email = r.Email.Trim().ToLowerInvariant();
            if (!ContentRules.IsEmail(email))
            {
                errors["email"] = ["Enter a valid email address."];
                email = null;
            }
        }

        if (string.IsNullOrWhiteSpace(r.Mobile) && string.IsNullOrWhiteSpace(r.Email))
        {
            errors["contact"] = ["Give a mobile number or an email address so we can answer."];
        }

        var subject = r.Subject?.Trim();
        if (subject is { Length: > SubjectMax })
        {
            errors["subject"] = [$"At most {SubjectMax} characters."];
        }
        else if (ContentRules.LooksLikeMarkup(subject))
        {
            errors["subject"] = ["HTML is not allowed."];
        }

        var message = r.Message?.Trim();
        if (string.IsNullOrEmpty(message) || message.Length < MessageMin)
        {
            errors["message"] = [$"Write a message of at least {MessageMin} characters."];
        }
        else if (message.Length > MessageMax)
        {
            errors["message"] = [$"At most {MessageMax} characters."];
        }
        else if (ContentRules.LooksLikeMarkup(message))
        {
            errors["message"] = ["HTML is not allowed. Write plain text."];
        }

        var page = r.SourcePage?.Trim();
        if (!string.IsNullOrEmpty(page) && (page.Length > SourcePageMax || !page.StartsWith('/') || page.StartsWith("//", StringComparison.Ordinal) || ContentRules.LooksLikeMarkup(page)))
        {
            errors["sourcePage"] = ["Must be a site path such as /contact."];
        }

        return (errors, mobile, email);
    }

    /// <summary>SHA-256 of the client address with a fixed prefix; the address itself is never stored.</summary>
    public static string? HashClient(string? ip) =>
        string.IsNullOrEmpty(ip) ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("juvo-contact|" + ip)));
}

/// <summary>
/// The anonymous side of the contact form. It always answers the same receipt shape, so nothing tells a sender whether a number or an email already belongs to a customer.
/// A filled honeypot gets a receipt too (so a bot learns nothing) but stores nothing.
/// </summary>
public sealed class ContactPublicService(IApplicationDbContext db, ICurrentUser currentUser, TimeProvider clock)
{
    public async Task<Result<ContactReceiptDto>> SubmitAsync(ContactFormRequest r, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(r.Website))
        {
            return new ContactReceiptDto("CM-" + Guid.NewGuid().ToString("N")[^8..].ToUpperInvariant());
        }

        var (errors, mobile, email) = ContactRules.Validate(r);
        if (errors.Count > 0)
        {
            return ContentRules.Invalid(errors);
        }

        var now = clock.GetUtcNow();
        var since = now.AddHours(-1);
        var ipHash = ContactRules.HashClient(currentUser.IpAddress);
        var byContact = await db.ContactMessages.AsNoTracking()
            .CountAsync(m => m.ReceivedAt > since && ((mobile != null && m.Mobile == mobile) || (email != null && m.Email == email)), cancellationToken);
        var byClient = ipHash is null ? 0 : await db.ContactMessages.AsNoTracking().CountAsync(m => m.ReceivedAt > since && m.IpHash == ipHash, cancellationToken);
        if (byContact >= ContactRules.PerContactPerHour || byClient >= ContactRules.PerClientPerHour)
        {
            return new Error(ErrorCodes.Content.RateLimited, "Too many messages. Please wait a while and try again.", ErrorType.TooManyRequests);
        }

        var entity = new ContactMessage(r.Name!, mobile, email, r.Subject, r.Message!, r.SourcePage, ipHash, now);
        db.ContactMessages.Add(entity);
        await db.SaveChangesAsync(cancellationToken);
        return new ContactReceiptDto(entity.Reference);
    }
}

/// <summary>
/// The staff inbox (FR-Q06). Mobile and email are masked unless the caller holds <c>customers.customer.view-pii</c> (same rule as customers); showing them unmasked is audit-logged
/// as <see cref="AuditAction.Viewed"/>. Changes are audited by the persistence layer (personal fields excluded).
/// </summary>
public sealed class ContactInboxService(IApplicationDbContext db, ActorContext actors, ICurrentUser currentUser, TimeProvider clock)
{
    private static Error NotFound() => Error.NotFound(ErrorCodes.Content.NotFound, "Message not found.");

    private static string Preview(string text) => text.Length <= 140 ? text : text[..140] + "...";

    private static string? Mobile(ContactMessage m, bool masked) => masked && m.Mobile is not null ? CustomersAdminService.MaskMobile(m.Mobile) : m.Mobile;

    private static string? Email(ContactMessage m, bool masked) => masked ? CustomersAdminService.MaskEmail(m.Email) : m.Email;

    private static ContactMessageListItemDto ToItem(ContactMessage m, bool masked) =>
        new(m.Id, m.Reference, m.Name, Mobile(m, masked), Email(m, masked), m.Subject, Preview(m.Message), m.Status, m.ReceivedAt, masked);

    private static ContactMessageDto ToDto(ContactMessage m, bool masked) =>
        new(m.Id, m.Reference, m.Name, Mobile(m, masked), Email(m, masked), m.Subject, m.Message, m.Status, m.SourcePage, m.ReceivedAt, m.HandledBy, m.HandledAt, m.InternalNote, masked);

    private void RecordView(string entityId, string detail) =>
        db.AuditLogs.Add(new AuditLog(currentUser.UserId, currentUser.BranchId, "ContactMessage", entityId, AuditAction.Viewed, null, detail, currentUser.CorrelationId, currentUser.IpAddress, clock.GetUtcNow()));

    /// <summary>True when personal data must be masked; null when the caller is not an active staff member.</summary>
    private async Task<bool?> IsMaskedAsync(CancellationToken cancellationToken) =>
        await actors.GetAsync(cancellationToken) is { } actor ? !actor.Access.HasAnywhere(Permissions.Customers.CustomerViewPii) : null;

    public async Task<Result<PagedResult<ContactMessageListItemDto>>> ListAsync(PageRequest request, ContactMessageQuery filter, CancellationToken cancellationToken)
    {
        if (await IsMaskedAsync(cancellationToken) is not { } masked)
        {
            return Error.Unauthorized();
        }

        var query = db.ContactMessages.AsNoTracking().AsQueryable();
        if (filter.Status is { } status)
        {
            query = query.Where(m => m.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            // Searching by mobile or email needs the personal data, so it is only allowed with view-pii.
            query = masked
                ? query.Where(m => m.Name.Contains(term) || (m.Subject != null && m.Subject.Contains(term)) || m.Message.Contains(term))
                : query.Where(m => m.Name.Contains(term) || (m.Subject != null && m.Subject.Contains(term)) || m.Message.Contains(term) ||
                                   (m.Mobile != null && m.Mobile.Contains(term)) || (m.Email != null && m.Email.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(m => m.ReceivedAt).ThenBy(m => m.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        if (!masked && rows.Count > 0)
        {
            RecordView("list", $"Contact messages shown with personal data ({rows.Count} rows).");
            await db.SaveChangesAsync(cancellationToken);
        }

        return new PagedResult<ContactMessageListItemDto>([.. rows.Select(m => ToItem(m, masked))], total, page.Page, page.PageSize);
    }

    public async Task<Result<ContactMessageDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await IsMaskedAsync(cancellationToken) is not { } masked)
        {
            return Error.Unauthorized();
        }

        var m = await db.ContactMessages.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (m is null)
        {
            return NotFound();
        }

        if (!masked)
        {
            RecordView(id.ToString(), "Contact message shown with personal data.");
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToDto(m, masked);
    }

    public Task<Result<ContactMessageDto>> SetStatusAsync(Guid id, ContactMessageStatus status, CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(status))
        {
            return Task.FromResult<Result<ContactMessageDto>>(ContentRules.Invalid("status", "Unknown status."));
        }

        return MutateAsync(id, m =>
        {
            m.SetStatus(status, currentUser.UserId, clock.GetUtcNow());
            return null;
        }, cancellationToken);
    }

    public Task<Result<ContactMessageDto>> AddNoteAsync(Guid id, string? note, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            return Task.FromResult<Result<ContactMessageDto>>(ContentRules.Invalid("note", "Write a note."));
        }

        if (ContentRules.LooksLikeMarkup(note))
        {
            return Task.FromResult<Result<ContactMessageDto>>(ContentRules.Invalid("note", "HTML is not allowed."));
        }

        return MutateAsync(id, m => m.AddNote(note, currentUser.UserName, clock.GetUtcNow())
            ? null
            : ContentRules.Invalid("note", $"The notes of one message can have at most {ContactMessage.InternalNoteMax} characters."), cancellationToken);
    }

    private async Task<Result<ContactMessageDto>> MutateAsync(Guid id, Func<ContactMessage, Error?> change, CancellationToken cancellationToken)
    {
        if (await IsMaskedAsync(cancellationToken) is not { } masked)
        {
            return Error.Unauthorized();
        }

        var m = await db.ContactMessages.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        if (m is null)
        {
            return NotFound();
        }

        if (change(m) is { } error)
        {
            return error;
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(m, masked);
    }
}
