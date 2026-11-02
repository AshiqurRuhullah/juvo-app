using System.Net.Mail;
using Juvo.Application.Abstractions;
using Juvo.Application.Identity;
using Juvo.Domain.Identity;
using Juvo.Shared.Pagination;
using Juvo.Shared.Results;
using Juvo.Shared.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Juvo.Application.Ums;

/// <summary>Staff accounts, their branch/role assignments, sessions and sign-in history (module O, section 4A).</summary>
public sealed class UsersService(
    IApplicationDbContext db,
    UserManager<StaffUser> users,
    ActorContext actors,
    SessionIssuer sessions,
    AuthService auth,
    IUserStateCache states,
    AccessService accessService,
    IUnitOfWork unitOfWork)
{
    private static Error NotFound() => Error.NotFound(ErrorCodes.Ums.UserNotFound, "User not found.");

    private static Error Validation(string field, string message) =>
        Error.Validation(new Dictionary<string, string[]> { [field] = [message] });

    // ---- read ---------------------------------------------------------------------------------------

    public async Task<Result<PagedResult<UserListItemDto>>> ListAsync(PageRequest request, UserListQuery filter, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var query = VisibleUsers(actor);
        if (filter.IsActive is { } active)
        {
            query = query.Where(u => u.IsActive == active);
        }

        if (filter.RoleId is { } roleId)
        {
            query = query.Where(u => db.UserBranchRoles.Any(x => x.UserId == u.Id && x.RoleId == roleId));
        }

        if (filter.BranchId is { } branchId)
        {
            query = query.Where(u => db.UserBranchRoles.Any(x => x.UserId == u.Id && x.BranchId == branchId));
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(u => u.FullName.Contains(term) || (u.Email != null && u.Email.Contains(term))
                                     || (u.UserName != null && u.UserName.Contains(term)) || (u.PhoneNumber != null && u.PhoneNumber.Contains(term)));
        }

        var sort = request.ParseSort(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "fullName", "email", "createdAt", "lastLoginAt" }) is { Count: > 0 } fields ? fields[0] : null;
        query = (sort?.Name.ToLowerInvariant(), sort?.Descending) switch
        {
            ("email", false) => query.OrderBy(u => u.Email),
            ("email", true) => query.OrderByDescending(u => u.Email),
            ("createdat", false) => query.OrderBy(u => u.CreatedAt),
            ("createdat", true) => query.OrderByDescending(u => u.CreatedAt),
            ("lastloginat", false) => query.OrderBy(u => u.LastLoginAt),
            ("lastloginat", true) => query.OrderByDescending(u => u.LastLoginAt),
            ("fullname", true) => query.OrderByDescending(u => u.FullName),
            _ => query.OrderBy(u => u.FullName),
        };

        var page = request.Normalized;
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.Skip(page.Skip).Take(page.PageSize).ToListAsync(cancellationToken);
        var ids = rows.Select(r => r.Id).ToList();
        var roles = await AssignmentLabelsAsync(ids, cancellationToken);
        var now = DateTimeOffset.UtcNow;

        var items = rows.Select(u => new UserListItemDto(u.Id, u.Email ?? string.Empty, u.UserName ?? string.Empty, u.FullName,
            u.PhoneNumber, u.IsActive, u.IsGlobalScope, u.TwoFactorEnabled, u.LockoutEnd > now, u.LastLoginAt,
            roles.GetValueOrDefault(u.Id) ?? [])).ToList();
        return new PagedResult<UserListItemDto>(items, total, page.Page, page.PageSize);
    }

    public async Task<Result<UserDetailDto>> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var user = await VisibleUsers(actor).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        return user is null ? NotFound() : await DetailAsync(user, cancellationToken);
    }

    public async Task<Result<IReadOnlyList<RoleAssignmentDto>>> ListAssignmentsAsync(Guid id, CancellationToken cancellationToken)
    {
        var detail = await GetAsync(id, cancellationToken);
        return detail.IsFailure ? detail.Error! : Result.Success(detail.Value.Assignments);
    }

    public async Task<Result<PagedResult<SignInHistoryDto>>> SignInsAsync(Guid? userId, SignInQuery filter, PageRequest request, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var query = db.SignInHistory.AsNoTracking().AsQueryable();
        if (!actor.IsGlobal)
        {
            // Branch-scoped admins see only sign-ins of staff they can see.
            var visible = VisibleUsers(actor).Select(u => (Guid?)u.Id);
            query = query.Where(s => s.SubjectType == SubjectType.Staff && visible.Contains(s.SubjectId));
        }

        var subject = userId ?? filter.SubjectId;
        if (subject is { } sid)
        {
            query = query.Where(s => s.SubjectId == sid);
        }

        if (filter.Succeeded is { } ok)
        {
            query = query.Where(s => s.Succeeded == ok);
        }

        if (Enum.TryParse<SubjectType>(filter.SubjectType, true, out var type))
        {
            query = query.Where(s => s.SubjectType == type);
        }

        if (filter.From is { } from)
        {
            query = query.Where(s => s.OccurredAt >= from);
        }

        if (filter.To is { } to)
        {
            query = query.Where(s => s.OccurredAt <= to);
        }

        if (!string.IsNullOrWhiteSpace(request.Search))
        {
            var term = request.Search.Trim();
            query = query.Where(s => s.Identifier.Contains(term) || (s.IpAddress != null && s.IpAddress.Contains(term)));
        }

        return await query.OrderByDescending(s => s.OccurredAt).ToPagedAsync(request,
            s => new SignInHistoryDto(s.Id, s.SubjectType.ToString(), s.SubjectId, s.Identifier, s.Succeeded, s.FailureReason,
                s.IpAddress, s.UserAgent, s.OccurredAt), cancellationToken);
    }

    public async Task<Result<IReadOnlyList<EffectivePermissionsDto>>> EffectivePermissionsAsync(Guid id, Guid? branchId, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var user = await VisibleUsers(actor).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        var access = await accessService.GetAsync(user.Id, user.PermissionVersion, cancellationToken);
        var branches = await db.Branches.AsNoTracking().ToDictionaryAsync(b => b.Id, b => b.Code, cancellationToken);
        var result = new List<EffectivePermissionsDto>();

        IEnumerable<Guid> targets = branchId is { } one ? [one] : access.AssignedBranchIds;
        foreach (var target in targets)
        {
            var codes = PermissionCatalog.All.Select(p => p.Code).Where(c => access.Has(c, target)).ToList();
            result.Add(new EffectivePermissionsDto(target, branches.GetValueOrDefault(target), access.HasAllPermissions, codes));
        }

        if (branchId is null)
        {
            var globalCodes = PermissionCatalog.All.Select(p => p.Code).Where(c => access.Has(c, null)).ToList();
            result.Insert(0, new EffectivePermissionsDto(null, null, access.HasAllPermissions, globalCodes));
        }

        return Result.Success<IReadOnlyList<EffectivePermissionsDto>>(result);
    }

    // ---- create and update --------------------------------------------------------------------------

    public async Task<Result<CreatedUserResponse>> CreateAsync(CreateUserRequest request, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var email = request.Email?.Trim() ?? string.Empty;
        if (!IsEmail(email))
        {
            return Validation("email", "Enter a valid email address.");
        }

        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length > 150)
        {
            return Validation("fullName", "Full name is required (up to 150 characters).");
        }

        if (request.IsGlobalScope && !actor.IsGlobal)
        {
            return Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "Only a global-scope user can create a global-scope account.");
        }

        var assignments = request.Assignments ?? [];
        var assignmentCheck = await CheckAssignmentsAsync(actor, request.IsGlobalScope, assignments, cancellationToken);
        if (assignmentCheck is not null)
        {
            return assignmentCheck;
        }

        if (!actor.IsGlobal && assignments.Count == 0)
        {
            return Validation("assignments", "Assign the new user to at least one of your branches.");
        }

        var userName = string.IsNullOrWhiteSpace(request.UserName) ? email : request.UserName.Trim();
        if (await users.FindByEmailAsync(email) is not null || await users.FindByNameAsync(userName) is not null)
        {
            return Error.Conflict(ErrorCodes.Ums.EmailTaken, "An account with this email or user name already exists.");
        }

        var generated = string.IsNullOrEmpty(request.Password);
        var password = generated ? TemporaryPassword.Generate() : request.Password!;
        var user = new StaffUser
        {
            UserName = userName,
            Email = email,
            EmailConfirmed = true,
            FullName = request.FullName.Trim(),
            PhoneNumber = request.Phone?.Trim(),
            IsGlobalScope = request.IsGlobalScope,
            MustChangePassword = true,
        };

        var created = await unitOfWork.ExecuteInTransactionAsync<Result<StaffUser>>(async ct =>
        {
            var identity = await users.CreateAsync(user, password);
            if (!identity.Succeeded)
            {
                return IdentityError(identity);
            }

            foreach (var a in assignments)
            {
                db.UserBranchRoles.Add(new UserBranchRole(user.Id, a.BranchId, a.RoleId));
            }

            await db.SaveChangesAsync(ct);
            return user;
        }, cancellationToken);

        if (created.IsFailure)
        {
            return created.Error!;
        }

        var detail = await DetailAsync(user, cancellationToken);
        return new CreatedUserResponse(detail, generated ? password : null);
    }

    public async Task<Result<UserDetailDto>> UpdateAsync(Guid id, UpdateUserRequest request, CancellationToken cancellationToken)
    {
        var target = await LoadManageableAsync(id, Permissions.Ums.UserEdit, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var (actor, user, _) = target.Value;
        if (string.IsNullOrWhiteSpace(request.FullName) || request.FullName.Trim().Length > 150)
        {
            return Validation("fullName", "Full name is required (up to 150 characters).");
        }

        if (request.IsGlobalScope != user.IsGlobalScope)
        {
            if (!actor.IsGlobal)
            {
                return Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "Only a global-scope user can change global scope.");
            }

            if (!request.IsGlobalScope && id == actor.Id)
            {
                return Error.Forbidden(ErrorCodes.Ums.SelfAction, "You cannot remove your own global scope.");
            }

            user.IsGlobalScope = request.IsGlobalScope;
            user.BumpPermissionVersion();
        }

        user.FullName = request.FullName.Trim();
        user.PhoneNumber = request.Phone?.Trim();
        await users.UpdateAsync(user);
        await InvalidateAsync(user.Id);
        return await DetailAsync(user, cancellationToken);
    }

    // ---- activate, deactivate, passwords, sessions --------------------------------------------------

    public async Task<Result> ActivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var target = await LoadManageableAsync(id, Permissions.Ums.UserActivate, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        target.Value.User.Activate();
        await users.UpdateAsync(target.Value.User);
        await InvalidateAsync(id);
        return Result.Success();
    }

    public async Task<Result> DeactivateAsync(Guid id, CancellationToken cancellationToken)
    {
        var target = await LoadManageableAsync(id, Permissions.Ums.UserDeactivate, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var (actor, user, _) = target.Value;
        if (id == actor.Id)
        {
            return Error.Forbidden(ErrorCodes.Ums.SelfAction, "You cannot deactivate your own account.");
        }

        if (await IsLastActiveSuperAdminAsync(id, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Ums.LastSuperAdmin, "At least one active Super Admin must remain.");
        }

        user.Deactivate();
        await users.UpdateAsync(user);
        await sessions.RevokeAllAsync(SubjectType.Staff, id, "deactivated", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(id);
        return Result.Success();
    }

    public async Task<Result<TemporaryPasswordResponse>> ResetPasswordAsync(Guid id, CancellationToken cancellationToken)
    {
        var target = await LoadManageableAsync(id, Permissions.Ums.UserResetPassword, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var (actor, user, _) = target.Value;
        if (id == actor.Id)
        {
            return Error.Forbidden(ErrorCodes.Ums.SelfAction, "Use change password for your own account.");
        }

        var password = TemporaryPassword.Generate();
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var result = await users.ResetPasswordAsync(user, token, password);
        if (!result.Succeeded)
        {
            return IdentityError(result);
        }

        user.MustChangePassword = true;
        user.BumpPermissionVersion();
        await users.SetLockoutEndDateAsync(user, null);
        await users.ResetAccessFailedCountAsync(user);
        await users.UpdateAsync(user);
        await sessions.RevokeAllAsync(SubjectType.Staff, id, "password-reset-by-admin", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(id);
        return new TemporaryPasswordResponse(password);
    }

    public async Task<Result> ForceLogoutAsync(Guid id, CancellationToken cancellationToken)
    {
        var target = await LoadManageableAsync(id, Permissions.Ums.UserForceLogout, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        await sessions.RevokeAllAsync(SubjectType.Staff, id, "forced-logout", cancellationToken);
        target.Value.User.BumpPermissionVersion(); // access tokens stop working right away too
        await users.UpdateAsync(target.Value.User);
        await InvalidateAsync(id);
        return Result.Success();
    }

    public async Task<Result> ResetTwoFactorAsync(Guid id, CancellationToken cancellationToken)
    {
        var target = await LoadManageableAsync(id, Permissions.Ums.UserManageTwoFactor, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var user = target.Value.User;
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAuthenticatorKeyAsync(user);
        await sessions.RevokeAllAsync(SubjectType.Staff, id, "two-factor-reset", cancellationToken);
        user.BumpPermissionVersion();
        await users.UpdateAsync(user);
        await InvalidateAsync(id);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<SessionDto>>> ListSessionsAsync(Guid id, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        if (await VisibleUsers(actor).AnyAsync(u => u.Id == id, cancellationToken) is false)
        {
            return NotFound();
        }

        return Result.Success(await auth.ListSessionsAsync(SubjectType.Staff, id, null, cancellationToken));
    }

    public async Task<Result> RevokeSessionAsync(Guid id, Guid sessionId, CancellationToken cancellationToken)
    {
        var target = await LoadManageableAsync(id, Permissions.Ums.SessionRevoke, cancellationToken);
        return target.IsFailure ? target.Error! : await auth.RevokeSessionAsync(SubjectType.Staff, id, sessionId, cancellationToken);
    }

    // ---- assignments --------------------------------------------------------------------------------

    public async Task<Result<RoleAssignmentDto>> AddAssignmentAsync(Guid id, AssignmentRequest request, CancellationToken cancellationToken)
    {
        var target = await LoadManageableAsync(id, Permissions.Ums.BranchAssignUser, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var (actor, user, existing) = target.Value;
        var check = await CheckAssignmentsAsync(actor, user.IsGlobalScope, [request], cancellationToken);
        if (check is not null)
        {
            return check;
        }

        if (existing.Any(a => a.BranchId == request.BranchId && a.RoleId == request.RoleId))
        {
            return Error.Conflict(ErrorCodes.Ums.AssignmentExists, "The user already has this role in this branch.");
        }

        var assignment = new UserBranchRole(id, request.BranchId, request.RoleId);
        db.UserBranchRoles.Add(assignment);
        user.BumpPermissionVersion();
        await db.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(id);

        var label = await db.UserBranchRoles.AsNoTracking().Where(x => x.Id == assignment.Id)
            .Select(x => new RoleAssignmentDto(x.Id, x.BranchId,
                db.Branches.Where(b => b.Id == x.BranchId).Select(b => b.Code).FirstOrDefault(), x.RoleId,
                db.Roles.Where(r => r.Id == x.RoleId).Select(r => r.Name).First()))
            .FirstAsync(cancellationToken);
        return label;
    }

    public async Task<Result> RemoveAssignmentAsync(Guid id, Guid assignmentId, CancellationToken cancellationToken)
    {
        var target = await LoadManageableAsync(id, Permissions.Ums.BranchAssignUser, cancellationToken);
        if (target.IsFailure)
        {
            return target.Error!;
        }

        var (actor, user, existing) = target.Value;
        var assignment = existing.FirstOrDefault(a => a.Id == assignmentId);
        if (assignment is null)
        {
            return Error.NotFound(ErrorCodes.Ums.AssignmentNotFound, "Assignment not found.");
        }

        if (!actor.IsGlobal && (assignment.BranchId is null || !actor.Can(Permissions.Ums.BranchAssignUser, assignment.BranchId)))
        {
            return Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "You cannot change assignments in this branch.");
        }

        var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == assignment.RoleId, cancellationToken);
        if (role is { IsSuperAdmin: true } && await IsLastSuperAdminAssignmentAsync(assignmentId, cancellationToken))
        {
            return Error.Conflict(ErrorCodes.Ums.LastSuperAdmin, "At least one active Super Admin must remain.");
        }

        db.UserBranchRoles.Remove(assignment);
        user.BumpPermissionVersion();
        await db.SaveChangesAsync(cancellationToken);
        await InvalidateAsync(id);
        return Result.Success();
    }

    // ---- helpers ------------------------------------------------------------------------------------

    /// <summary>Users the actor may see: everyone for global actors; otherwise users sharing one of the actor's branches (and themselves).</summary>
    private IQueryable<StaffUser> VisibleUsers(Actor actor)
    {
        if (actor.IsGlobal)
        {
            return db.Users;
        }

        var branches = actor.Access.BranchPermissions
            .Where(kv => kv.Value.Contains(Permissions.Ums.UserView))
            .Select(kv => kv.Key)
            .ToList();
        return db.Users.Where(u => u.Id == actor.Id
                                   || (!u.IsGlobalScope && db.UserBranchRoles.Any(x => x.UserId == u.Id && x.BranchId != null && branches.Contains(x.BranchId.Value))));
    }

    private async Task<Result<(Actor Actor, StaffUser User, List<UserBranchRole> Assignments)>> LoadManageableAsync(
        Guid id, string permission, CancellationToken cancellationToken)
    {
        if (await actors.GetAsync(cancellationToken) is not { } actor)
        {
            return Error.Unauthorized();
        }

        var user = await VisibleUsers(actor).FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
        if (user is null)
        {
            return NotFound();
        }

        var assignments = await db.UserBranchRoles.Where(x => x.UserId == id).ToListAsync(cancellationToken);
        if (!actor.IsGlobal)
        {
            // A branch-scoped admin may only manage users who work entirely inside branches where the admin holds the permission.
            var inside = assignments.Count > 0 && !user.IsGlobalScope
                         && assignments.All(a => a.BranchId is not null && actor.Can(permission, a.BranchId));
            if (!inside && id != actor.Id)
            {
                return Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "You can only manage users who work in your own branches.");
            }
        }

        // Only a Super Admin may manage Super Admins.
        var targetIsSuperAdmin = await (from ubr in db.UserBranchRoles
                                        where ubr.UserId == id
                                        join role in db.Roles on ubr.RoleId equals role.Id
                                        where role.IsSuperAdmin
                                        select ubr.Id).AnyAsync(cancellationToken);
        if (targetIsSuperAdmin && !actor.IsSuperAdmin && id != actor.Id)
        {
            return Error.Forbidden(ErrorCodes.Auth.Forbidden, "Only a Super Admin can manage a Super Admin.");
        }

        return (actor, user, assignments);
    }

    /// <summary>Validates assignments: roles and branches exist, branch-less roles only for global users, and no privilege escalation.</summary>
    private async Task<Error?> CheckAssignmentsAsync(Actor actor, bool targetIsGlobal, IReadOnlyList<AssignmentRequest> assignments, CancellationToken cancellationToken)
    {
        foreach (var a in assignments)
        {
            var role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == a.RoleId, cancellationToken);
            if (role is null)
            {
                return Error.NotFound(ErrorCodes.Ums.RoleNotFound, "Role not found.");
            }

            if (a.BranchId is null && !targetIsGlobal)
            {
                return Validation("assignments", "A role without a branch is only for global-scope users.");
            }

            if (a.BranchId is { } branchId)
            {
                var branch = await db.Branches.AsNoTracking().FirstOrDefaultAsync(b => b.Id == branchId, cancellationToken);
                if (branch is null)
                {
                    return Error.NotFound(ErrorCodes.Ums.BranchNotFound, "Branch not found.");
                }

                if (!branch.IsActive)
                {
                    return Error.Conflict(ErrorCodes.Ums.BranchInactive, "This branch is deactivated.");
                }
            }

            if (!actor.IsGlobal && (a.BranchId is null || !actor.Can(Permissions.Ums.BranchAssignUser, a.BranchId)))
            {
                return Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "You can only assign roles in your own branches.");
            }

            if (a.BranchId is null && !actor.IsGlobal)
            {
                return Error.Forbidden(ErrorCodes.Auth.BranchForbidden, "Only a global-scope user can assign a role without a branch.");
            }

            if (role.IsSuperAdmin && !actor.IsSuperAdmin)
            {
                return Error.Forbidden(ErrorCodes.Auth.Forbidden, "Only a Super Admin can grant the Super Admin role.");
            }

            if (!role.IsSuperAdmin && !actor.IsSuperAdmin)
            {
                // No privilege escalation: you cannot hand out permissions you do not hold yourself.
                var codes = await (from rp in db.RolePermissions.AsNoTracking()
                                   where rp.RoleId == role.Id
                                   join p in db.Permissions.AsNoTracking() on rp.PermissionId equals p.Id
                                   select p.Code).ToListAsync(cancellationToken);
                var missing = codes.FirstOrDefault(code => !actor.Can(code, a.BranchId));
                if (missing is not null)
                {
                    return Error.Forbidden(ErrorCodes.Auth.Forbidden, $"You cannot grant a role that includes '{missing}', which you do not hold.");
                }
            }
        }

        return null;
    }

    private async Task<bool> IsLastActiveSuperAdminAsync(Guid userId, CancellationToken cancellationToken)
    {
        var isSuper = await (from ubr in db.UserBranchRoles where ubr.UserId == userId
                             join role in db.Roles on ubr.RoleId equals role.Id where role.IsSuperAdmin select ubr.Id).AnyAsync(cancellationToken);
        if (!isSuper)
        {
            return false;
        }

        var others = await (from ubr in db.UserBranchRoles
                            join role in db.Roles on ubr.RoleId equals role.Id
                            join user in db.Users on ubr.UserId equals user.Id
                            where role.IsSuperAdmin && user.IsActive && user.Id != userId
                            select ubr.Id).AnyAsync(cancellationToken);
        return !others;
    }

    private async Task<bool> IsLastSuperAdminAssignmentAsync(Guid assignmentId, CancellationToken cancellationToken) =>
        !await (from ubr in db.UserBranchRoles
                join role in db.Roles on ubr.RoleId equals role.Id
                join user in db.Users on ubr.UserId equals user.Id
                where role.IsSuperAdmin && user.IsActive && ubr.Id != assignmentId
                select ubr.Id).AnyAsync(cancellationToken);

    /// <summary>Access changed: drop the cached state so the new permission version is read on the next request.</summary>
    private Task InvalidateAsync(Guid userId)
    {
        states.Invalidate(userId);
        return Task.CompletedTask;
    }

    private async Task<Dictionary<Guid, IReadOnlyList<string>>> AssignmentLabelsAsync(List<Guid> ids, CancellationToken cancellationToken)
    {
        var rows = await (from ubr in db.UserBranchRoles.AsNoTracking()
                          where ids.Contains(ubr.UserId)
                          join role in db.Roles.AsNoTracking() on ubr.RoleId equals role.Id
                          join branch in db.Branches.AsNoTracking() on ubr.BranchId equals branch.Id into b
                          from branch in b.DefaultIfEmpty()
                          select new { ubr.UserId, Label = (branch == null ? "All" : branch.Code) + ": " + role.Name })
            .ToListAsync(cancellationToken);
        return rows.GroupBy(r => r.UserId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(x => x.Label).Order().ToList());
    }

    private async Task<UserDetailDto> DetailAsync(StaffUser user, CancellationToken cancellationToken)
    {
        var assignments = await (from ubr in db.UserBranchRoles.AsNoTracking()
                                 where ubr.UserId == user.Id
                                 join role in db.Roles.AsNoTracking() on ubr.RoleId equals role.Id
                                 join branch in db.Branches.AsNoTracking() on ubr.BranchId equals branch.Id into b
                                 from branch in b.DefaultIfEmpty()
                                 select new RoleAssignmentDto(ubr.Id, ubr.BranchId, branch == null ? null : branch.Code, role.Id, role.Name))
            .ToListAsync(cancellationToken);
        var lockedOut = user.LockoutEnd > DateTimeOffset.UtcNow;
        return new UserDetailDto(user.Id, user.Email ?? string.Empty, user.UserName ?? string.Empty, user.FullName, user.PhoneNumber,
            user.IsActive, user.IsGlobalScope, user.TwoFactorEnabled, lockedOut, user.MustChangePassword, user.LastLoginAt,
            user.CreatedAt, assignments);
    }

    private static bool IsEmail(string value) =>
        value.Length <= 254 && MailAddress.TryCreate(value, out var address) && address.Address == value;

    private static Error IdentityError(IdentityResult result)
    {
        var duplicate = result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName");
        return duplicate
            ? Error.Conflict(ErrorCodes.Ums.EmailTaken, "An account with this email or user name already exists.")
            : new Error(ErrorCodes.Auth.WeakPassword, "The password does not meet the requirements.", ErrorType.Validation,
                new Dictionary<string, string[]> { ["password"] = [.. result.Errors.Select(e => e.Description)] });
    }
}
