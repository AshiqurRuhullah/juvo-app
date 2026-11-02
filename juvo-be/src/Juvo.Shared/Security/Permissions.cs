namespace Juvo.Shared.Security;

/// <summary>Constants for permission codes used in code. The full list lives in <see cref="PermissionCatalog"/>.</summary>
public static class Permissions
{
    public static class Ums
    {
        public const string UserView = "ums.user.view";
        public const string UserCreate = "ums.user.create";
        public const string UserEdit = "ums.user.edit";
        public const string UserActivate = "ums.user.activate";
        public const string UserDeactivate = "ums.user.deactivate";
        public const string UserResetPassword = "ums.user.reset-password";
        public const string UserForceLogout = "ums.user.force-logout";
        public const string UserManageTwoFactor = "ums.user.manage-two-factor";
        public const string RoleView = "ums.role.view";
        public const string RoleCreate = "ums.role.create";
        public const string RoleEdit = "ums.role.edit";
        public const string RoleDelete = "ums.role.delete";
        public const string RoleClone = "ums.role.clone";
        public const string RoleAssignPermissions = "ums.role.assign-permissions";
        public const string PermissionView = "ums.permission.view";
        public const string BranchView = "ums.branch.view";
        public const string BranchCreate = "ums.branch.create";
        public const string BranchEdit = "ums.branch.edit";
        public const string BranchDeactivate = "ums.branch.deactivate";
        public const string BranchSetDefault = "ums.branch.set-default";
        public const string BranchAssignUser = "ums.branch.assign-user";
        public const string BranchSwitchAll = "ums.branch.switch-all";
        public const string SessionView = "ums.session.view";
        public const string SessionRevoke = "ums.session.revoke";
        public const string SignInView = "ums.sign-in.view";
        public const string AuditView = "ums.audit.view";
    }
}
