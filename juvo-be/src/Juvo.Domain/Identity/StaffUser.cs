using Juvo.Domain.Common;
using Microsoft.AspNetCore.Identity;

namespace Juvo.Domain.Identity;

/// <summary>
/// A staff account (ASP.NET Core Identity user). Roles are assigned per branch through <see cref="UserBranchRole"/>.
/// Deactivation keeps the history; the soft-delete flag is for erasing accounts from lists only.
/// </summary>
public sealed class StaffUser : IdentityUser<Guid>, IAuditable, ISoftDeletable
{
    public StaffUser()
    {
        Id = Guid.CreateVersion7();
    }

    public string FullName { get; set; } = string.Empty;

    /// <summary>Inactive staff cannot sign in; existing tokens stop working immediately.</summary>
    public bool IsActive { get; private set; } = true;

    /// <summary>
    /// Global scope: the user may act in every branch ("All branches"). Roles assigned without a branch apply everywhere
    /// for such users. Super Admin always has global scope.
    /// </summary>
    public bool IsGlobalScope { get; set; }

    /// <summary>
    /// Bumped whenever roles, permissions, branch assignments or active state change. Access tokens carry the value
    /// they were issued with, so a stale token is rejected and the client refreshes silently.
    /// </summary>
    public int PermissionVersion { get; private set; } = 1;

    /// <summary>Set for new accounts and after an admin reset: only the change-password endpoint works until it is done.</summary>
    public bool MustChangePassword { get; set; }

    public DateTimeOffset? LastLoginAt { get; set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid? CreatedBy { get; private set; }

    public DateTimeOffset? UpdatedAt { get; private set; }

    public Guid? UpdatedBy { get; private set; }

    public bool IsDeleted { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public void Activate()
    {
        IsActive = true;
        BumpPermissionVersion();
    }

    public void Deactivate()
    {
        IsActive = false;
        BumpPermissionVersion();
    }

    public void BumpPermissionVersion() => PermissionVersion++;

    public void StampCreated(DateTimeOffset now, Guid? userId)
    {
        CreatedAt = now;
        CreatedBy = userId;
    }

    public void StampUpdated(DateTimeOffset now, Guid? userId)
    {
        UpdatedAt = now;
        UpdatedBy = userId;
    }

    public void MarkDeleted(DateTimeOffset now, Guid? userId)
    {
        IsDeleted = true;
        DeletedAt = now;
        DeletedBy = userId;
        IsActive = false;
        BumpPermissionVersion();
    }

    public void Restore()
    {
        IsDeleted = false;
        DeletedAt = null;
        DeletedBy = null;
    }
}
