using Juvo.Api.Configuration;
using Juvo.Api.Http;
using Juvo.Application.Identity;
using Juvo.Application.Ums;
using Juvo.Shared.Pagination;
using Juvo.Domain.Platform;
using Juvo.Shared.Security;

namespace Juvo.Api.Endpoints;

internal static class UmsEndpoints
{
    public static WebApplication MapUmsEndpoints(this WebApplication app)
    {
        var ums = app.MapApiV1().MapGroup("/ums").WithTags("UMS");

        MapUsers(ums.MapGroup("/users"));
        MapRoles(ums);
        MapBranches(ums.MapGroup("/branches"));

        ums.MapGet("/audit-logs", async ([AsParameters] ListParams list, Guid? userId, Guid? branchId, string? entity, string? entityId,
                    AuditAction? action, DateTimeOffset? from, DateTimeOffset? to, AuditQueryService service, CancellationToken ct) =>
                (await service.SearchAsync(list.ToRequest(), new AuditQuery(entity, entityId, userId, branchId, action, from, to), ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.AuditView)
            .Produces<PagedResult<AuditLogDto>>().WithSummary("Search the audit log (who changed what, when, in which branch).");

        ums.MapGet("/sign-in-history", async ([AsParameters] ListParams list, Guid? subjectId, bool? succeeded, string? subjectType,
                    DateTimeOffset? from, DateTimeOffset? to, UsersService service, CancellationToken ct) =>
                (await service.SignInsAsync(null, new SignInQuery(subjectId, succeeded, subjectType, from, to), list.ToRequest(), ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.SignInView)
            .Produces<PagedResult<SignInHistoryDto>>().WithSummary("Search sign-in attempts of staff and customers.");

        return app;
    }

    private static void MapUsers(RouteGroupBuilder users)
    {
        users.MapGet("", async ([AsParameters] ListParams list, bool? isActive, Guid? roleId, Guid? branchId, UsersService service, CancellationToken ct) =>
                (await service.ListAsync(list.ToRequest(), new UserListQuery(isActive, roleId, branchId), ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.UserView).Produces<PagedResult<UserListItemDto>>().WithSummary("List staff users.");

        users.MapPost("", async (CreateUserRequest request, UsersService service, CancellationToken ct) =>
                (await service.CreateAsync(request, ct)).ToHttpResult(v => Results.Created($"/api/v1/ums/users/{v.User.Id}", v)))
            .RequirePermission(Permissions.Ums.UserCreate)
            .Produces<CreatedUserResponse>(StatusCodes.Status201Created).WithSummary("Create a staff user. A temporary password is returned once when none is given; the user must change it.");

        users.MapGet("/{id:guid}", async (Guid id, UsersService service, CancellationToken ct) => (await service.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.UserView).Produces<UserDetailDto>().WithSummary("User details with branch roles.");

        users.MapPut("/{id:guid}", async (Guid id, UpdateUserRequest request, UsersService service, CancellationToken ct) =>
                (await service.UpdateAsync(id, request, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.UserEdit).Produces<UserDetailDto>().WithSummary("Edit name, phone and global scope.");

        users.MapPost("/{id:guid}/activate", async (Guid id, UsersService service, CancellationToken ct) => (await service.ActivateAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.UserActivate).Produces(StatusCodes.Status204NoContent).WithSummary("Activate an account.");

        users.MapPost("/{id:guid}/deactivate", async (Guid id, UsersService service, CancellationToken ct) => (await service.DeactivateAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.UserDeactivate).Produces(StatusCodes.Status204NoContent).WithSummary("Deactivate an account at once (history is kept).");

        users.MapPost("/{id:guid}/reset-password", async (Guid id, UsersService service, CancellationToken ct) => (await service.ResetPasswordAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.UserResetPassword).Produces<TemporaryPasswordResponse>().WithSummary("Set a new temporary password and end all sessions.");

        users.MapPost("/{id:guid}/force-logout", async (Guid id, UsersService service, CancellationToken ct) => (await service.ForceLogoutAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.UserForceLogout).Produces(StatusCodes.Status204NoContent).WithSummary("End all sessions of a user.");

        users.MapPost("/{id:guid}/reset-two-factor", async (Guid id, UsersService service, CancellationToken ct) => (await service.ResetTwoFactorAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.UserManageTwoFactor).Produces(StatusCodes.Status204NoContent).WithSummary("Turn off a user's two-step verification (lost phone).");

        users.MapGet("/{id:guid}/sessions", async (Guid id, UsersService service, CancellationToken ct) => (await service.ListSessionsAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.SessionView).Produces<IReadOnlyList<SessionDto>>().WithSummary("Active sessions of a user.");

        users.MapDelete("/{id:guid}/sessions/{sessionId:guid}", async (Guid id, Guid sessionId, UsersService service, CancellationToken ct) =>
                (await service.RevokeSessionAsync(id, sessionId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.SessionRevoke).Produces(StatusCodes.Status204NoContent).WithSummary("End one session of a user.");

        users.MapGet("/{id:guid}/sign-ins", async (Guid id, [AsParameters] ListParams list, UsersService service, CancellationToken ct) =>
                (await service.SignInsAsync(id, new SignInQuery(null, null, null, null, null), list.ToRequest(), ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.SignInView).Produces<PagedResult<SignInHistoryDto>>().WithSummary("Sign-in history of a user.");

        users.MapGet("/{id:guid}/effective-permissions", async (Guid id, Guid? branchId, UsersService service, CancellationToken ct) =>
                (await service.EffectivePermissionsAsync(id, branchId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.UserView).Produces<IReadOnlyList<EffectivePermissionsDto>>().WithSummary("What a user can do, per branch.");

        users.MapGet("/{id:guid}/branch-roles", async (Guid id, UsersService service, CancellationToken ct) => (await service.ListAssignmentsAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.UserView).Produces<IReadOnlyList<RoleAssignmentDto>>().WithSummary("Branch and role assignments of a user.");

        users.MapPost("/{id:guid}/branch-roles", async (Guid id, AssignmentRequest request, UsersService service, CancellationToken ct) =>
                (await service.AddAssignmentAsync(id, request, ct)).ToHttpResult(v => Results.Created($"/api/v1/ums/users/{id}/branch-roles/{v.AssignmentId}", v)))
            .RequirePermission(Permissions.Ums.BranchAssignUser).Produces<RoleAssignmentDto>(StatusCodes.Status201Created).WithSummary("Give a user a role in a branch (or everywhere for global users).");

        users.MapDelete("/{id:guid}/branch-roles/{assignmentId:guid}", async (Guid id, Guid assignmentId, UsersService service, CancellationToken ct) =>
                (await service.RemoveAssignmentAsync(id, assignmentId, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.BranchAssignUser).Produces(StatusCodes.Status204NoContent).WithSummary("Remove a role from a user in a branch.");
    }

    private static void MapRoles(RouteGroupBuilder ums)
    {
        ums.MapGet("/permissions", () => RolesService.Catalog().ToHttpResult())
            .RequirePermission(Permissions.Ums.PermissionView)
            .Produces<PermissionCatalogDto>().WithSummary("The permission catalog grouped by module, with its version.");

        var roles = ums.MapGroup("/roles");
        roles.MapGet("", async ([AsParameters] ListParams list, RolesService service, CancellationToken ct) => (await service.ListAsync(list.ToRequest(), ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.RoleView).Produces<PagedResult<RoleListItemDto>>().WithSummary("List roles.");

        roles.MapPost("", async (CreateRoleRequest request, RolesService service, CancellationToken ct) =>
                (await service.CreateAsync(request, ct)).ToHttpResult(v => Results.Created($"/api/v1/ums/roles/{v.Id}", v)))
            .RequirePermission(Permissions.Ums.RoleCreate).Produces<RoleDetailDto>(StatusCodes.Status201Created).WithSummary("Create a role with a set of permissions.");

        roles.MapGet("/{id:guid}", async (Guid id, RolesService service, CancellationToken ct) => (await service.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.RoleView).Produces<RoleDetailDto>().WithSummary("A role with its permissions.");

        roles.MapPut("/{id:guid}", async (Guid id, UpdateRoleRequest request, RolesService service, CancellationToken ct) =>
                (await service.UpdateAsync(id, request, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.RoleEdit).Produces<RoleDetailDto>().WithSummary("Rename a role or change its description (system roles keep their name).");

        roles.MapDelete("/{id:guid}", async (Guid id, RolesService service, CancellationToken ct) => (await service.DeleteAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.RoleDelete).Produces(StatusCodes.Status204NoContent).WithSummary("Delete a role that is not a system role and not in use.");

        roles.MapPost("/{id:guid}/clone", async (Guid id, CloneRoleRequest request, RolesService service, CancellationToken ct) =>
                (await service.CloneAsync(id, request, ct)).ToHttpResult(v => Results.Created($"/api/v1/ums/roles/{v.Id}", v)))
            .RequirePermission(Permissions.Ums.RoleClone).Produces<RoleDetailDto>(StatusCodes.Status201Created).WithSummary("Copy a role and its permissions under a new name.");

        roles.MapPut("/{id:guid}/permissions", async (Guid id, SetRolePermissionsRequest request, RolesService service, CancellationToken ct) =>
                (await service.SetPermissionsAsync(id, request, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.RoleAssignPermissions)
            .Produces<RoleDetailDto>().WithSummary("Replace the permissions of a role. Users of the role get the change within seconds.");
    }

    private static void MapBranches(RouteGroupBuilder branches)
    {
        branches.MapGet("", async ([AsParameters] ListParams list, bool? isActive, BranchesService service, CancellationToken ct) =>
                (await service.ListAsync(list.ToRequest(), new BranchListQuery(isActive), ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.BranchView).Produces<PagedResult<BranchDto>>().WithSummary("Branches the user may see.");

        branches.MapPost("", async (CreateBranchRequest request, BranchesService service, CancellationToken ct) =>
                (await service.CreateAsync(request, ct)).ToHttpResult(v => Results.Created($"/api/v1/ums/branches/{v.Id}", v)))
            .RequirePermission(Permissions.Ums.BranchCreate).Produces<BranchDto>(StatusCodes.Status201Created).WithSummary("Create a branch.");

        branches.MapGet("/{id:guid}", async (Guid id, BranchesService service, CancellationToken ct) => (await service.GetAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.BranchView).Produces<BranchDto>().WithSummary("A branch. Branches outside the user's scope answer 404.");

        branches.MapPut("/{id:guid}", async (Guid id, UpdateBranchRequest request, BranchesService service, CancellationToken ct) =>
                (await service.UpdateAsync(id, request, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.BranchEdit).Produces<BranchDto>().WithSummary("Edit a branch.");

        branches.MapPost("/{id:guid}/activate", async (Guid id, BranchesService service, CancellationToken ct) => (await service.ActivateAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.BranchDeactivate).Produces(StatusCodes.Status204NoContent).WithSummary("Activate a branch.");

        branches.MapPost("/{id:guid}/deactivate", async (Guid id, BranchesService service, CancellationToken ct) => (await service.DeactivateAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.BranchDeactivate)
            .Produces(StatusCodes.Status204NoContent).WithSummary("Deactivate a branch (never the default or the last active one). Users lose access to it at once.");

        branches.MapPost("/{id:guid}/set-default", async (Guid id, BranchesService service, CancellationToken ct) => (await service.SetDefaultAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.BranchSetDefault).Produces(StatusCodes.Status204NoContent).WithSummary("Make a branch the default.");

        branches.MapGet("/{id:guid}/users", async (Guid id, BranchesService service, CancellationToken ct) => (await service.ListUsersAsync(id, ct)).ToHttpResult())
            .RequirePermission(Permissions.Ums.BranchView).Produces<IReadOnlyList<BranchUserDto>>().WithSummary("Users working in a branch, with their role.");
    }
}
