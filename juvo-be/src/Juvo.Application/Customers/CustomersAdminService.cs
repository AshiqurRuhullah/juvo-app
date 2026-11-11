using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Application.Ums;
using Juvo.Domain.Customers;
using Juvo.Domain.Identity;
using Juvo.Domain.Platform;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Customers;

/// <summary>
/// Staff view of customers (module N). Customers are shared by every branch, so the permissions are global
/// (<c>customers.customer.*</c>). Mobile, email and address details are masked unless the staff member holds <c>view-pii</c>, and every
/// time they are shown unmasked an audit entry (<see cref="AuditAction.Viewed"/>) records who looked (NFR-012).
/// </summary>
public sealed class CustomersAdminService(IApplicationDbContext db, ActorContext actors, ICurrentUser currentUser, SessionIssuer sessions, TimeProvider clock)
{
    private static Error NotFound() => Error.NotFound(ErrorCodes.Customer.NotFound, "Customer not found.");

    public static string MaskMobile(string mobile) => mobile.Length <= 6 ? "***" : string.Concat(mobile.AsSpan(0, mobile.Length - 6), "****", mobile.AsSpan(mobile.Length - 2));

    public static string? MaskEmail(string? email)
    {
        if (string.IsNullOrEmpty(email))
        {
            return null;
        }

        var at = email.IndexOf('@', StringComparison.Ordinal);
        return at <= 1 ? "***" + email[at..] : email[0] + "***" + email[at..];
    }

    private static CustomerListItemDto ToItem(Customer c, bool masked) =>
        new(c.Id, masked ? MaskMobile(c.Mobile) : c.Mobile, masked ? MaskEmail(c.Email) : c.Email, c.FullName, c.ProfileLevel, c.MobileVerified, c.IsActive, c.LastLoginAt, c.CreatedAt);

    private void RecordView(string entityId, string detail) =>
        db.AuditLogs.Add(new AuditLog(currentUser.UserId, currentUser.BranchId, "Customer", entityId, AuditAction.Viewed, null, detail, currentUser.CorrelationId, currentUser.IpAddress, clock.GetUtcNow()));

    public async Task<Result<PagedResult<CustomerListItemDto>>> ListAsync(PageRequest request, CustomerListQuery filter, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var masked = !actor.Access.HasAnywhere(Permissions.Customers.CustomerViewPii);
        var query = db.Customers.AsNoTracking().AsQueryable();
        if (filter.IsActive is { } active)
        {
            query = query.Where(c => c.IsActive == active);
        }

        if (filter.Level is { } level)
        {
            query = query.Where(c => c.ProfileLevel == level);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            // Searching by mobile or email needs the personal data, so it is only allowed with view-pii (otherwise name only).
            query = masked
                ? query.Where(c => c.FullName != null && c.FullName.Contains(term))
                : query.Where(c => c.Mobile.Contains(term) || (c.Email != null && c.Email.Contains(term)) || (c.FullName != null && c.FullName.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        if (!masked && rows.Count > 0)
        {
            RecordView("list", $"Customer list shown with personal data ({rows.Count} rows).");
            await db.SaveChangesAsync(cancellationToken);
        }

        return new PagedResult<CustomerListItemDto>([.. rows.Select(c => ToItem(c, masked))], total, page.Page, page.PageSize);
    }

    public async Task<Result<CustomerDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer is null)
        {
            return NotFound();
        }

        var masked = !actor.Access.HasAnywhere(Permissions.Customers.CustomerViewPii);
        var addresses = await db.Addresses.AsNoTracking().Where(a => a.CustomerId == id).OrderByDescending(a => a.IsDefault).ThenBy(a => a.CreatedAt).ToListAsync(cancellationToken);
        var consentLogs = await db.ConsentLogs.AsNoTracking().Where(c => c.CustomerId == id).OrderByDescending(c => c.At).ThenByDescending(c => c.Id).ToListAsync(cancellationToken);
        var consents = Enum.GetValues<ConsentType>().Select(type => consentLogs.FirstOrDefault(r => r.Type == type) is { } l ? new ConsentDto(type.ToString(), l.Granted, l.At) : new ConsentDto(type.ToString(), false, null)).ToList();
        var wishlist = await db.WishlistItems.CountAsync(w => w.CustomerId == id, cancellationToken);
        var signIns = await db.SignInHistory.AsNoTracking().Where(s => s.SubjectType == SubjectType.Customer && s.SubjectId == id && s.Succeeded).Select(s => s.OccurredAt).ToListAsync(cancellationToken);

        var dtos = addresses.Select(a => new AddressDto(a.Id, a.Label, masked ? a.RecipientName[..1] + "***" : a.RecipientName, masked ? MaskMobile(a.Phone) : a.Phone, a.CountryCode, a.DivisionId, a.DistrictId, a.UpazilaId,
            a.Area, masked ? "***" : a.AddressLine, masked ? null : a.Landmark, a.StateProvince, masked ? null : a.City, masked ? null : a.PostalCode, masked ? null : a.Latitude, masked ? null : a.Longitude, a.IsDefault, false)).ToList();

        if (!masked)
        {
            RecordView(id.ToString(), "Customer details shown with personal data.");
            await db.SaveChangesAsync(cancellationToken);
        }

        return new CustomerDetailDto(ToItem(customer, masked), masked, dtos, consents, wishlist, signIns.Count, signIns.Count == 0 ? null : signIns.Max());
    }

    /// <summary>A deactivated customer cannot sign in and their sessions end at once. Orders and history stay (soft rule).</summary>
    public async Task<Result<CustomerListItemDto>> SetActiveAsync(Guid id, bool active, CancellationToken cancellationToken)
    {
        var customer = await db.Customers.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (customer is null)
        {
            return NotFound();
        }

        if (active)
        {
            customer.Activate();
        }
        else
        {
            customer.Deactivate();
        }

        await db.SaveChangesAsync(cancellationToken);
        if (!active)
        {
            await sessions.RevokeAllAsync(SubjectType.Customer, id, "customer-deactivated", cancellationToken);
        }

        var actor = await actors.GetAsync(cancellationToken);
        return ToItem(customer, actor is null || !actor.Access.HasAnywhere(Permissions.Customers.CustomerViewPii));
    }
}
