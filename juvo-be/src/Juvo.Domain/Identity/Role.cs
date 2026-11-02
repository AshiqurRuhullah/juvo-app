using Juvo.Domain.Common;

namespace Juvo.Domain.Identity;

/// <summary>A named set of permissions. System roles cannot be deleted or renamed; Super Admin always has every permission.</summary>
public sealed class Role : SoftDeletableEntity
{
    private Role()
    {
    }

    public Role(string name, string? description, bool isSystem = false, bool isSuperAdmin = false)
    {
        Name = name.Trim();
        Description = description?.Trim();
        IsSystem = isSystem;
        IsSuperAdmin = isSuperAdmin;
    }

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public bool IsSystem { get; private set; }

    /// <summary>Holds every permission, including ones added to the catalog later, without stored rows.</summary>
    public bool IsSuperAdmin { get; private set; }

    public void Update(string name, string? description)
    {
        Name = name.Trim();
        Description = description?.Trim();
    }
}

/// <summary>One permission code from the catalog, stored so roles can reference it. Retired codes stay with IsActive = false.</summary>
public sealed class Permission : Entity
{
    private Permission()
    {
    }

    public Permission(string code, string module, string resource, string action, string description)
    {
        Code = code;
        Module = module;
        Resource = resource;
        Action = action;
        Description = description;
    }

    public string Code { get; private set; } = string.Empty;

    public string Module { get; private set; } = string.Empty;

    public string Resource { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;

    public string Description { get; private set; } = string.Empty;

    /// <summary>False when the code was removed from the catalog. It is kept so old audit rows still make sense.</summary>
    public bool IsActive { get; private set; } = true;

    public void Refresh(string description, bool isActive)
    {
        Description = description;
        IsActive = isActive;
    }
}

public sealed class RolePermission : Entity
{
    private RolePermission()
    {
    }

    public RolePermission(Guid roleId, Guid permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
    }

    public Guid RoleId { get; private set; }

    public Guid PermissionId { get; private set; }
}

/// <summary>
/// User, role and branch. The same person can be Order Manager in branch A and Support in branch B.
/// <see cref="BranchId"/> = null means the role applies everywhere, and only counts for users with global scope.
/// </summary>
public sealed class UserBranchRole : Entity
{
    private UserBranchRole()
    {
    }

    public UserBranchRole(Guid userId, Guid? branchId, Guid roleId)
    {
        UserId = userId;
        BranchId = branchId;
        RoleId = roleId;
    }

    public Guid UserId { get; private set; }

    public Guid? BranchId { get; private set; }

    public Guid RoleId { get; private set; }
}
