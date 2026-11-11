using Juvo.Application.Abstractions;
using Juvo.Domain.Carts;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Carts;

/// <summary>Coupon management for staff (FR-C07). Coupons are global; the permission is promotions.coupon.*.</summary>
public sealed class CouponsService(IApplicationDbContext db)
{
    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    private static CouponDto ToDto(Coupon c) => new(c.Id, c.Code, c.Description, c.Kind, c.Value, c.MinOrderValue, c.MaxDiscount, c.StartsAt, c.EndsAt, c.UsageLimit, c.UsedCount, c.IsActive);

    public async Task<Result<PagedResult<CouponDto>>> ListAsync(PageRequest request, CouponListQuery filter, CancellationToken cancellationToken)
    {
        var query = db.Coupons.AsNoTracking().AsQueryable();
        if (filter.IsActive is { } active)
        {
            query = query.Where(c => c.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(c => c.Code.Contains(term) || (c.Description != null && c.Description.Contains(term)));
        }

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderByDescending(c => c.CreatedAt).Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        return new PagedResult<CouponDto>([.. rows.Select(ToDto)], total, page.Page, page.PageSize);
    }

    public async Task<Result<CouponDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var coupon = await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        return coupon is null ? Error.NotFound(ErrorCodes.Coupon.NotFound, "Coupon not found.") : ToDto(coupon);
    }

    public async Task<Result<CouponDto>> CreateAsync(SaveCouponRequest request, CancellationToken cancellationToken)
    {
        var problem = Validate(request);
        if (problem is not null)
        {
            return problem;
        }

        var code = Coupon.Normalize(request.Code);
        if (await db.Coupons.AnyAsync(c => c.Code == code, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Coupon.CodeTaken, "A coupon with this code already exists.");
        }

        var coupon = new Coupon(code, request.Kind, request.Value);
        coupon.Update(request.Description, request.Kind, request.Value, request.MinOrderValue, request.MaxDiscount, request.StartsAt, request.EndsAt, request.UsageLimit, request.IsActive);
        db.Coupons.Add(coupon);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(coupon);
    }

    /// <summary>The code itself never changes (customers may have it); everything else can.</summary>
    public async Task<Result<CouponDto>> UpdateAsync(Guid id, SaveCouponRequest request, CancellationToken cancellationToken)
    {
        var coupon = await db.Coupons.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (coupon is null)
        {
            return Error.NotFound(ErrorCodes.Coupon.NotFound, "Coupon not found.");
        }

        var problem = Validate(request);
        if (problem is not null)
        {
            return problem;
        }

        if (request.UsageLimit is { } limit && limit < coupon.UsedCount)
        {
            return Invalid("usageLimit", "The limit cannot be lower than the number of times the coupon was already used.");
        }

        coupon.Update(request.Description, request.Kind, request.Value, request.MinOrderValue, request.MaxDiscount, request.StartsAt, request.EndsAt, request.UsageLimit, request.IsActive);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(coupon);
    }

    /// <summary>A coupon that was used keeps its record: it is switched off instead (the API refuses to delete it).</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken cancellationToken)
    {
        var coupon = await db.Coupons.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (coupon is null)
        {
            return Error.NotFound(ErrorCodes.Coupon.NotFound, "Coupon not found.");
        }

        if (coupon.UsedCount > 0 || await db.Carts.AnyAsync(c => c.CouponId == id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Coupon.Inactive, "This coupon was used or is in a cart. Switch it off instead of deleting it.");
        }

        db.Coupons.Remove(coupon);
        await db.SaveChangesAsync(cancellationToken);
        return Result.Success();
    }

    private static Error? Validate(SaveCouponRequest r)
    {
        var code = r.Code?.Trim() ?? string.Empty;
        if (code.Length is < 3 or > 40 || !code.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-'))
        {
            return Invalid("code", "Use 3 to 40 letters, digits, hyphen or underscore.");
        }

        if (!Enum.IsDefined(r.Kind) || r.Value <= 0 || (r.Kind == CouponKind.Percent && r.Value > 100) || r.Value > 10_000_000)
        {
            return Invalid("value", r.Kind == CouponKind.Percent ? "A percentage must be above 0 and at most 100." : "The amount must be above 0.");
        }

        if (r.MinOrderValue < 0 || r.MinOrderValue > 100_000_000 || r.MaxDiscount is <= 0 || r.UsageLimit is <= 0)
        {
            return Invalid("minOrderValue", "The minimum order, the cap and the usage limit must be positive.");
        }

        if (r.StartsAt is { } start && r.EndsAt is { } end && end <= start)
        {
            return Invalid("endsAt", "The coupon must end after it starts.");
        }

        return r.Description is { Length: > 200 } ? Invalid("description", "Up to 200 characters.") : null;
    }
}
