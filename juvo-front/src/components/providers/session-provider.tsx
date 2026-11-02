"use client";

import * as React from "react";
import type { Me } from "@/lib/api/types";
import { can as canCheck, canAny as canAnyCheck, type PermissionScope } from "@/lib/auth/permissions";

type SessionContextValue = {
  me: Me;
  /** Display name for headers and menus. */
  name: string;
  isStaff: boolean;
  isCustomer: boolean;
};

const SessionContext = React.createContext<SessionContextValue | null>(null);

/** Gives client components the signed-in person (loaded on the server from /auth/me). */
export function SessionProvider({ me, children }: { me: Me; children: React.ReactNode }) {
  const value = React.useMemo<SessionContextValue>(
    () => ({
      me,
      name: me.staff?.fullName ?? me.customer?.fullName ?? me.customer?.mobile ?? "",
      isStaff: me.subjectType === "staff",
      isCustomer: me.subjectType === "customer",
    }),
    [me],
  );
  return <SessionContext.Provider value={value}>{children}</SessionContext.Provider>;
}

export function useSession(): SessionContextValue {
  const value = React.useContext(SessionContext);
  if (!value) throw new Error("useSession must be used inside <SessionProvider>.");
  return value;
}

/**
 * Permission checks for the signed-in staff member, in the active branch. The same rules as the server (permission AND branch
 * scope), used to show or hide buttons and menus. The API still refuses anything not allowed.
 */
export function usePermissions() {
  const { me } = useSession();
  return React.useMemo(
    () => ({
      can: (permission: string, scope: PermissionScope = "active") => canCheck(me, permission, scope),
      canAny: (permissions: readonly string[], scope: PermissionScope = "active") => canAnyCheck(me, permissions, scope),
      isSuperAdmin: me.hasAllPermissions,
      activeBranchId: me.activeBranchId ?? null,
      isAllBranches: me.isAllBranches,
    }),
    [me],
  );
}

/** Shows its children only when the user holds the permission. `<Can permission="orders.order.confirm">...</Can>` */
export function Can({
  permission,
  anyOf,
  scope = "active",
  fallback = null,
  children,
}: {
  permission?: string;
  anyOf?: readonly string[];
  scope?: PermissionScope;
  fallback?: React.ReactNode;
  children: React.ReactNode;
}) {
  const { can, canAny } = usePermissions();
  const allowed = permission ? can(permission, scope) : anyOf ? canAny(anyOf, scope) : false;
  return <>{allowed ? children : fallback}</>;
}
