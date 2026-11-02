namespace Juvo.Application.Abstractions;

public enum PermissionScope
{
    /// <summary>The permission must be held in the active branch (or globally).</summary>
    ActiveBranch = 0,

    /// <summary>The permission may be held in any of the user's assignments. For global features such as users and roles.</summary>
    Anywhere = 1,
}

/// <summary>Marks a request that needs a permission code such as <c>orders.order.confirm</c>.</summary>
public interface IRequirePermission
{
    string Permission { get; }

    PermissionScope Scope => PermissionScope.ActiveBranch;
}

/// <summary>
/// Answers "may the current user do this?". Permission and branch scope are combined: a permission is only effective in
/// the branches where the user holds a role that includes it; global-scope users act in every branch.
/// </summary>
public interface IPermissionChecker
{
    /// <summary>In the active branch (<see cref="IBranchContext"/>), or anywhere for a global-scope user in "All branches" mode.</summary>
    Task<bool> HasPermissionAsync(string permission, CancellationToken cancellationToken = default);

    Task<bool> HasPermissionAsync(string permission, PermissionScope scope, CancellationToken cancellationToken = default);

    /// <summary>In one specific branch, regardless of the active branch.</summary>
    Task<bool> HasPermissionInBranchAsync(string permission, Guid branchId, CancellationToken cancellationToken = default);
}
