using Juvo.Application.Abstractions;
using Juvo.Application.Operations;
using Juvo.Domain.Purchasing;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Purchasing;

/// <summary>Suppliers (module W): contact, payment terms, active or archived. A supplier is never erased, only archived, so old purchase orders keep it.</summary>
public sealed class SuppliersService(IApplicationDbContext db)
{
    private static Error NotFound() => Error.NotFound(ErrorCodes.Purchase.SupplierNotFound, "Supplier not found.");

    private static readonly IReadOnlySet<string> SortFields = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "name", "createdAt" };

    public async Task<Result<PagedResult<SupplierDto>>> ListAsync(PageRequest request, bool? active, CancellationToken cancellationToken)
    {
        var query = db.Suppliers.AsNoTracking().AsQueryable();
        if (active is { } a)
        {
            query = query.Where(s => s.IsActive == a);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(s => s.Name.Contains(term) || (s.ContactName != null && s.ContactName.Contains(term)) || (s.Phone != null && s.Phone.Contains(term)) || (s.Email != null && s.Email.Contains(term)));
        }

        var sort = request.ParseSort(SortFields) is { Count: > 0 } f ? f[0] : null;
        query = (sort?.Name.ToLowerInvariant(), sort?.Descending ?? false) switch
        {
            ("createdat", true) => query.OrderByDescending(s => s.CreatedAt),
            ("createdat", false) => query.OrderBy(s => s.CreatedAt),
            ("name", true) => query.OrderByDescending(s => s.Name),
            _ => query.OrderBy(s => s.Name),
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.Id).ToList();
        var open = await OpenCountsAsync(ids, cancellationToken);
        return new PagedResult<SupplierDto>([.. rows.Select(s => ToDto(s, open.GetValueOrDefault(s.Id)))], total, page.Page, page.PageSize);
    }

    private async Task<Dictionary<Guid, int>> OpenCountsAsync(List<Guid> ids, CancellationToken cancellationToken) =>
        await db.PurchaseOrders.AsNoTracking().Where(o => ids.Contains(o.SupplierId) && (o.Status == PurchaseStatus.Draft || o.Status == PurchaseStatus.Sent || o.Status == PurchaseStatus.PartlyReceived))
            .GroupBy(o => o.SupplierId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

    private static SupplierDto ToDto(Supplier s, int open) =>
        new(s.Id, s.Name, s.ContactName, s.Phone, s.Email, s.Address, s.PaymentTermsDays, s.TaxId, s.Notes, s.IsActive, open, s.CreatedAt);

    public async Task<Result<SupplierDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        return supplier is null ? NotFound() : ToDto(supplier, (await OpenCountsAsync([id], cancellationToken)).GetValueOrDefault(id));
    }

    private static Error? Validate(SaveSupplierRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Name) || Ops.TooLong(r.Name, 160) || Ops.TooLong(r.ContactName, 120) || Ops.TooLong(r.Phone, 40) || Ops.TooLong(r.Email, 200) || Ops.TooLong(r.Address, 400) ||
            Ops.TooLong(r.TaxId, 60) || Ops.TooLong(r.Notes, 1000))
        {
            return Ops.Invalid("name", "Give the supplier a name (up to 160 characters); contact 120, phone 40, email 200, address 400, tax id 60 and notes 1000.");
        }

        return r.PaymentTermsDays is < 0 or > 365 ? Ops.Invalid("paymentTermsDays", "Payment terms are from 0 to 365 days.") : null;
    }

    public async Task<Result<SupplierDto>> CreateAsync(SaveSupplierRequest request, CancellationToken cancellationToken)
    {
        if (Validate(request) is { } error)
        {
            return error;
        }

        var name = request.Name.Trim();
        if (await db.Suppliers.AnyAsync(s => s.Name == name, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Purchase.SupplierNameTaken, "A supplier with this name already exists.");
        }

        var supplier = new Supplier(name, request.ContactName, request.Phone, request.Email, request.Address, request.PaymentTermsDays ?? 0, request.TaxId, request.Notes);
        db.Suppliers.Add(supplier);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(supplier, 0);
    }

    public async Task<Result<SupplierDto>> UpdateAsync(Guid id, SaveSupplierRequest request, CancellationToken cancellationToken)
    {
        var supplier = await db.Suppliers.FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
        if (supplier is null)
        {
            return NotFound();
        }

        if (Validate(request) is { } error)
        {
            return error;
        }

        var name = request.Name.Trim();
        if (await db.Suppliers.AnyAsync(s => s.Name == name && s.Id != id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Purchase.SupplierNameTaken, "A supplier with this name already exists.");
        }

        supplier.Update(name, request.ContactName, request.Phone, request.Email, request.Address, request.PaymentTermsDays ?? supplier.PaymentTermsDays, request.TaxId, request.Notes);
        if (request.IsActive is { } active)
        {
            supplier.SetActive(active);
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(supplier, (await OpenCountsAsync([id], cancellationToken)).GetValueOrDefault(id));
    }
}
