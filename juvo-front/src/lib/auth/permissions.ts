/**
 * Permission checks for the UI. They mirror the API exactly (Juvo.Application/Identity: UserAccess and PermissionChecker), so a
 * button is shown if and only if the server would allow the action. The server still enforces every request; this is for display.
 *
 * Permission = permission AND branch scope: a role held in branch A is not effective in branch B.
 */
export type AccessView = {
  /** Super Admin: holds every permission, including ones added later. */
  hasAllPermissions: boolean;
  /** Permissions from roles assigned without a branch (global-scope users only). */
  globalPermissions: readonly string[];
  /** Permission codes by branch id. */
  branchPermissions: Readonly<Record<string, readonly string[]>>;
  /** The branch chosen with X-Branch-Id, if any. */
  activeBranchId?: string | null;
  /** "All branches" mode. */
  isAllBranches: boolean;
};

export type PermissionScope = "active" | "anywhere";

/** Held in any of the user's assignments (global or any branch). */
export function canAnywhere(access: AccessView, permission: string): boolean {
  if (access.hasAllPermissions) return true;
  if (access.globalPermissions.includes(permission)) return true;
  return Object.values(access.branchPermissions).some((codes) => codes.includes(permission));
}

/** Held in one specific branch (or globally). */
export function canInBranch(access: AccessView, permission: string, branchId: string | null | undefined): boolean {
  if (access.hasAllPermissions) return true;
  if (access.globalPermissions.includes(permission)) return true;
  return branchId ? (access.branchPermissions[branchId]?.includes(permission) ?? false) : false;
}

/**
 * The normal check. "active" means in the active branch; in "All branches" mode, or for scope "anywhere" (global features such as
 * users and roles), any assignment counts. Same rules as PermissionChecker.HasPermissionAsync on the server.
 */
export function can(access: AccessView, permission: string, scope: PermissionScope = "active"): boolean {
  if (scope === "anywhere" || access.isAllBranches) return canAnywhere(access, permission);
  return canInBranch(access, permission, access.activeBranchId);
}

export function canAny(access: AccessView, permissions: readonly string[], scope: PermissionScope = "active"): boolean {
  return permissions.some((p) => can(access, p, scope));
}

export function canAll(access: AccessView, permissions: readonly string[], scope: PermissionScope = "active"): boolean {
  return permissions.every((p) => can(access, p, scope));
}
