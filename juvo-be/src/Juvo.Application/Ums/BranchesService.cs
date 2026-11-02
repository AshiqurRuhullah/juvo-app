using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Domain.Branches;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Ums;

/// <summary>Branch management. Reads and changes are limited to the branches where the actor holds the permission.</summary>
public sealed class BranchesService(IApplicationDbContext db, ActorContext actors, AccessChanges changes, IAccessDeniedAuditor auditor, IUnitOfWork unitOfWork)
{
    private static Error NotFound() => Error.NotFound(ErrorCodes.Ums.BranchNotFound, "Branch not found.");

    private static Error Invalid(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    public async Task<Result<PagedResult<BranchDto>>> ListAsync(PageRequest request, BranchListQuery filter, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var query = db.Branches.AsNoTracking().AsQueryable();
        if (!actor.IsGlobal)
        {
            var visible = actor.Access.BranchPermissions.Where(kv => kv.Value.Contains(Permissions.Ums.BranchView) || actor.Access.GlobalPermissions.Contains(Permissions.Ums.BranchView))
                .Select(kv => kv.Key).ToList();
            query = query.Where(b => visible.Contains(b.Id));
        }

        if (filter.IsActive is { } active)
        {
            query = query.Where(b => b.IsActive == active);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(b => b.Code.Contains(term) || b.NameEn.Contains(term) || (b.NameBn != null && b.NameBn.Contains(term)));
        }

        var sort = request.ParseSort(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "code", "name" }) is { Count: > 0 } fields ? fields[0] : null;
        query = (sort?.Name.ToLowerInvariant(), sort?.Descending) switch
        {
            ("name", false) => query.OrderBy(b => b.NameEn),
            ("name", true) => query.OrderByDescending(b => b.NameEn),
            ("code", true) => query.OrderByDescending(b => b.Code),
            _ => query.OrderBy(b => b.Code),
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var counts = await UserCountsAsync(rows.Select(b => b.Id).ToList(), cancellationToken);
        return new PagedResult<BranchDto>([.. rows.Select(b => ToDto(b, counts.GetValueOrDefault(b.Id)))], total, page.Page, page.PageSize);
    }

    public async Task<Result<BranchDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, Permissions.Ums.BranchView, tracking: false, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var counts = await UserCountsAsync([id], cancellationToken);
        return ToDto(loaded.Value.Branch, counts.GetValueOrDefault(id));
    }

    public async Task<Result<BranchDto>> CreateAsync(CreateBranchRequest request, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        if (!actor.IsGlobal)
        {
            return Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "Only a global-scope user can create branches.");
        }

        if (!Branch.IsValidCode(request.Code))
        {
            return Invalid("code", "Code must be 2-20 characters: letters, digits and hyphen.");
        }

        var detailsError = ValidateDetails(request.NameEn, request.DefaultCurrency, request.TimeZoneId, request.Latitude, request.Longitude);
        if (detailsError is not null)
        {
            return detailsError;
        }

        var code = Branch.NormalizeCode(request.Code);
        if (await db.Branches.IgnoreQueryFilters().AnyAsync(b => b.Code == code, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Ums.BranchCodeTaken, "A branch with this code already exists.");
        }

        if (request.ParentBranchId is { } parent && !await db.Branches.AnyAsync(b => b.Id == parent, cancellationToken))
        {
            return Error.NotFound(ErrorCodes.Ums.BranchNotFound, "Parent branch not found.");
        }

        var branch = new Branch(code, request.NameEn, request.NameBn, request.Type);
        ApplyDetails(branch, request.NameEn, request.NameBn, request.Type, request.ParentBranchId, request.AddressLine, request.Latitude,
            request.Longitude, request.Phone, request.Email, request.WorkingHours, request.TimeZoneId, request.DefaultCurrency,
            request.PickupAddress, request.CourierStoreIds, request.DefaultDeliveryZoneId);
        if (!string.IsNullOrWhiteSpace(request.OrderPrefix))
        {
            try
            {
                branch.SetOrderPrefix(request.OrderPrefix);
            }
            catch (ArgumentException)
            {
                return Invalid("orderPrefix", "Order prefix must be 1-10 letters or digits.");
            }
        }

        if (!await db.Branches.AnyAsync(cancellationToken))
        {
            branch.MarkDefault();
        }

        db.Branches.Add(branch);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(branch, 0);
    }

    public async Task<Result<BranchDto>> UpdateAsync(Guid id, UpdateBranchRequest request, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, Permissions.Ums.BranchEdit, tracking: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var branch = loaded.Value.Branch;
        var detailsError = ValidateDetails(request.NameEn, request.DefaultCurrency, request.TimeZoneId, request.Latitude, request.Longitude);
        if (detailsError is not null)
        {
            return detailsError;
        }

        if (request.ParentBranchId is { } parent)
        {
            if (parent == id)
            {
                return Invalid("parentBranchId", "A branch cannot be its own parent.");
            }

            if (!await db.Branches.AnyAsync(b => b.Id == parent, cancellationToken))
            {
                return Error.NotFound(ErrorCodes.Ums.BranchNotFound, "Parent branch not found.");
            }
        }

        ApplyDetails(branch, request.NameEn, request.NameBn, request.Type, request.ParentBranchId, request.AddressLine, request.Latitude,
            request.Longitude, request.Phone, request.Email, request.WorkingHours, request.TimeZoneId, request.DefaultCurrency,
            request.PickupAddress, request.CourierStoreIds, request.DefaultDeliveryZoneId);
        if (!string.IsNullOrWhiteSpace(request.OrderPrefix) && !string.Equals(request.OrderPrefix.Trim(), branch.OrderPrefix, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                branch.SetOrderPrefix(request.OrderPrefix);
            }
            catch (ArgumentException)
            {
                return Invalid("orderPrefix", "Order prefix must be 1-10 letters or digits.");
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var counts = await UserCountsAsync([id], cancellationToken);
        return ToDto(branch, counts.GetValueOrDefault(id));
    }

    public async Task<Result> ActivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, Permissions.Ums.BranchDeactivate, tracking: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        loaded.Value.Branch.Activate();
        await db.SaveChangesAsync(cancellationToken);
        await changes.BumpUsersOfBranchAsync(id, cancellationToken);
        return Result.Success();
    }

    public async Task<Result> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, Permissions.Ums.BranchDeactivate, tracking: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        var branch = loaded.Value.Branch;
        if (branch.IsDefault)
        {
            return Error.Conflict(ErrorCodes.Ums.BranchDefaultProtected, "The default branch cannot be deactivated. Make another branch the default first.");
        }

        if (!await db.Branches.AnyAsync(b => b.Id != id && b.IsActive, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Ums.BranchLastActive, "At least one branch must stay active.");
        }

        branch.Deactivate();
        await db.SaveChangesAsync(cancellationToken);
        await changes.BumpUsersOfBranchAsync(id, cancellationToken); // their access to this branch stops at once
        return Result.Success();
    }

    public async Task<Result> SetDefaultAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, Permissions.Ums.BranchSetDefault, tracking: true, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        if (!loaded.Value.Actor.IsGlobal)
        {
            return Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "Only a global-scope user can set the default branch.");
        }

        var branch = loaded.Value.Branch;
        if (!branch.IsActive)
        {
            return Error.Conflict(ErrorCodes.Ums.BranchInactive, "An inactive branch cannot be the default.");
        }

        // A unique index allows exactly one default, so the old one is cleared (and saved) before the new one is set.
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            foreach (var other in await db.Branches.Where(b => b.IsDefault && b.Id != id).ToListAsync(ct))
            {
                other.UnmarkDefault();
            }

            await db.SaveChangesAsync(ct);
            branch.MarkDefault();
            await db.SaveChangesAsync(ct);
            return true;
        }, cancellationToken);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<BranchUserDto>>> ListUsersAsync(Guid id, CancellationToken cancellationToken)
    {
        var loaded = await LoadAsync(id, Permissions.Ums.BranchView, tracking: false, cancellationToken);
        if (loaded.IsFailure)
        {
            return loaded.Error!;
        }

        if (!loaded.Value.Actor.Can(Permissions.Ums.UserView, id))
        {
            return Error.Forbidden(ErrorCodes.Auth.Forbidden, "You cannot view users of this branch.");
        }

        var rows = await (from ubr in db.UserBranchRoles.AsNoTracking()
                          where ubr.BranchId == id
                          join user in db.Users.AsNoTracking() on ubr.UserId equals user.Id
                          join role in db.Roles.AsNoTracking() on ubr.RoleId equals role.Id
                          orderby user.FullName
                          select new BranchUserDto(ubr.Id, user.Id, user.FullName, user.Email ?? string.Empty, user.IsActive, role.Id, role.Name))
            .ToListAsync(cancellationToken);
        return Result.Success<IReadOnlyList<BranchUserDto>>(rows);
    }

    // ---- helpers ------------------------------------------------------------------------------------

    /// <summary>
    /// Loads a branch the actor may use with <paramref name="permission"/>. A branch that exists but is out of scope gives
    /// 404 (nothing is revealed) and the attempt is written to the audit log.
    /// </summary>
    private async Task<Result<(Actor Actor, Branch Branch)>> LoadAsync(Guid id, string permission, bool tracking, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var query = tracking ? db.Branches.AsQueryable() : db.Branches.AsNoTracking();
        var branch = await query.FirstOrDefaultAsync(b => b.Id == id, cancellationToken);
        if (branch is null)
        {
            return NotFound();
        }

        // Viewing is allowed to anyone who holds BranchView in it; other actions need their own permission there.
        if (!actor.Can(permission, id) && !(actor.IsGlobal && actor.Access.HasAnywhere(permission)))
        {
            await auditor.RecordAsync("Branch", $"{permission} denied for branch {branch.Code} ({id})", cancellationToken);
            return actor.Can(Permissions.Ums.BranchView, id) ? Error.Forbidden() : NotFound();
        }

        return (actor, branch);
    }

    private static Error? ValidateDetails(string? nameEn, string? currency, string? timeZone, double? lat, double? lng)
    {
        if (string.IsNullOrWhiteSpace(nameEn) || nameEn.Trim().Length > 150)
        {
            return Invalid("nameEn", "Name is required (up to 150 characters).");
        }

        if (!string.IsNullOrWhiteSpace(currency) && currency.Trim().Length != 3)
        {
            return Invalid("defaultCurrency", "Currency must be a 3-letter code, for example BDT.");
        }

        if (!string.IsNullOrWhiteSpace(timeZone))
        {
            try
            {
                _ = TimeZoneInfo.FindSystemTimeZoneById(timeZone.Trim());
            }
            catch (TimeZoneNotFoundException)
            {
                return Invalid("timeZoneId", "Unknown time zone.");
            }
            catch (InvalidTimeZoneException)
            {
                return Invalid("timeZoneId", "Unknown time zone.");
            }
        }

        if (lat is < -90 or > 90)
        {
            return Invalid("latitude", "Latitude must be between -90 and 90.");
        }

        if (lng is < -180 or > 180)
        {
            return Invalid("longitude", "Longitude must be between -180 and 180.");
        }

        return null;
    }

    private static void ApplyDetails(Branch branch, string nameEn, string? nameBn, BranchType type, Guid? parent, string? address,
        double? lat, double? lng, string? phone, string? email, string? hours, string? timeZone, string? currency, string? pickup,
        string? courierStoreIds, Guid? zone) =>
        branch.UpdateDetails(nameEn, nameBn, type, parent, address, lat, lng, phone, email, hours,
            string.IsNullOrWhiteSpace(timeZone) ? "Asia/Dhaka" : timeZone, string.IsNullOrWhiteSpace(currency) ? "BDT" : currency,
            pickup, courierStoreIds, zone);

    private async Task<Dictionary<Guid, int>> UserCountsAsync(List<Guid> ids, CancellationToken cancellationToken) =>
        await db.UserBranchRoles.AsNoTracking().Where(x => x.BranchId != null && ids.Contains(x.BranchId.Value))
            .GroupBy(x => x.BranchId!.Value).Select(g => new { g.Key, Count = g.Select(x => x.UserId).Distinct().Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

    private static BranchDto ToDto(Branch b, int users) =>
        new(b.Id, b.Code, b.NameEn, b.NameBn, b.Type, b.ParentBranchId, b.AddressLine, b.Latitude, b.Longitude, b.Phone, b.Email,
            b.WorkingHours, b.TimeZoneId, b.DefaultCurrency, b.OrderPrefix, b.PickupAddress, b.CourierStoreIds,
            b.DefaultDeliveryZoneId, b.IsActive, b.IsDefault, users);
}
