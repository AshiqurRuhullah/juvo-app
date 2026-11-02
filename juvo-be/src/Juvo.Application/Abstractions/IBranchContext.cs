namespace Juvo.Application.Abstractions;

/// <summary>
/// The branch scope of the current request, resolved from the <c>X-Branch-Id</c> header and the user's assignments.
/// Branch-scoped data is filtered and guarded centrally by the persistence layer using this.
/// </summary>
public interface IBranchContext
{
    /// <summary>The branch the user works in, or null in "All branches" mode or when there is none.</summary>
    Guid? ActiveBranchId { get; }

    /// <summary>The user chose "All branches" (allowed for global-scope users only).</summary>
    bool IsAllBranches { get; }

    /// <summary>
    /// True for signed-in, branch-scoped staff: their reads are filtered and their writes are checked. False for global-scope
    /// staff, customers, anonymous visitors and background jobs, which see and write across branches by design.
    /// </summary>
    bool EnforceScope { get; }

    /// <summary>The branches the filters and write guard allow: the active branch, or all the user's assigned branches.</summary>
    IReadOnlyCollection<Guid> AllowedBranchIds { get; }
}

/// <summary>A branch scope that never filters. Used by background jobs, tests and design-time tools.</summary>
public sealed class UnscopedBranchContext : IBranchContext
{
    public static readonly UnscopedBranchContext Instance = new();

    public Guid? ActiveBranchId => null;

    public bool IsAllBranches => true;

    public bool EnforceScope => false;

    public IReadOnlyCollection<Guid> AllowedBranchIds => [];
}

/// <summary>A write was attempted in a branch the user does not hold. Mapped to 403 AUTH.BRANCH_FORBIDDEN and audit-logged.</summary>
public sealed class BranchAccessDeniedException(string entity, Guid branchId)
    : InvalidOperationException($"Access to branch {branchId} is not allowed for {entity}.")
{
    public string Entity { get; } = entity;

    public Guid BranchId { get; } = branchId;
}
