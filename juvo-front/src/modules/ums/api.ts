"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { api, ApiError, unwrap } from "@/lib/api/client";
import type { Schemas } from "@/lib/api/types";
import { toast } from "@/components/ui/toaster";
import { fieldErrors } from "@/lib/api/problem";
import { useProblemMessage } from "@/lib/api/use-problem-message";

/** Typed access to the UMS endpoints (Step 3). Every call goes through the BFF, so the token and the active branch are added there. */
export type UserListItem = Schemas["UserListItemDto"];
export type UserDetail = Schemas["UserDetailDto"];
export type RoleListItem = Schemas["RoleListItemDto"];
export type RoleDetail = Schemas["RoleDetailDto"];
export type BranchDto = Schemas["BranchDto"];
export type BranchType = Schemas["BranchType"];
export type CreateBranch = Schemas["CreateBranchRequest"];
export type AuditLog = Schemas["AuditLogDto"];
export type SignInHistory = Schemas["SignInHistoryDto"];
export type Session = Schemas["SessionDto"];
export type EffectivePermissions = Schemas["EffectivePermissionsDto"];
export type PermissionCatalog = Schemas["PermissionCatalogDto"];
export type BranchUser = Schemas["BranchUserDto"];

export const BRANCH_TYPES: BranchType[] = ["HeadOffice", "Shop", "Warehouse", "OnlineFulfilment"];

export type ListParams = { page: number; pageSize: number; sort?: string; search?: string };

const clean = <T extends Record<string, unknown>>(value: T) =>
  Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null)) as Partial<T>;

/* ---- Queries -------------------------------------------------------------------------------------------------------------- */

export function useUsers(params: ListParams & { isActive?: boolean; roleId?: string; branchId?: string }) {
  return useQuery({
    queryKey: ["ums", "users", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/ums/users", {
          params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, isActive: params.isActive, roleId: params.roleId, branchId: params.branchId }) },
        }),
      ),
  });
}

export function useUser(id: string) {
  return useQuery({ queryKey: ["ums", "user", id], enabled: !!id, queryFn: async () => unwrap(await api.GET("/api/v1/ums/users/{id}", { params: { path: { id } } })) });
}

export function useUserSessions(id: string) {
  return useQuery({ queryKey: ["ums", "user", id, "sessions"], queryFn: async () => unwrap(await api.GET("/api/v1/ums/users/{id}/sessions", { params: { path: { id } } })) });
}

export function useUserSignIns(id: string, page: number, pageSize = 10) {
  return useQuery({
    queryKey: ["ums", "user", id, "sign-ins", page, pageSize],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/ums/users/{id}/sign-ins", { params: { path: { id }, query: { Page: page, PageSize: pageSize } } })),
  });
}

export function useEffectivePermissions(id: string, enabled = true) {
  return useQuery({
    queryKey: ["ums", "user", id, "effective"],
    enabled,
    queryFn: async () => unwrap(await api.GET("/api/v1/ums/users/{id}/effective-permissions", { params: { path: { id } } })),
  });
}

export function useRoles(params: ListParams) {
  return useQuery({
    queryKey: ["ums", "roles", params],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/ums/roles", { params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search }) } })),
  });
}

/** Every role, for pickers (the API allows 100 per page and there are far fewer roles than that). */
export function useAllRoles() {
  return useQuery({
    queryKey: ["ums", "roles", "all"],
    queryFn: async () => unwrap(await api.GET("/api/v1/ums/roles", { params: { query: { Page: 1, PageSize: 100, Sort: "name" } } })),
  });
}

export function useRole(id: string) {
  return useQuery({ queryKey: ["ums", "role", id], enabled: !!id, queryFn: async () => unwrap(await api.GET("/api/v1/ums/roles/{id}", { params: { path: { id } } })) });
}

export function usePermissionCatalog() {
  return useQuery({ queryKey: ["ums", "permissions"], staleTime: 5 * 60_000, queryFn: async () => unwrap(await api.GET("/api/v1/ums/permissions")) });
}

export function useBranches(params: ListParams & { isActive?: boolean }) {
  return useQuery({
    queryKey: ["ums", "branches", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(await api.GET("/api/v1/ums/branches", { params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, isActive: params.isActive }) } })),
  });
}

export function useAllBranches() {
  return useQuery({
    queryKey: ["ums", "branches", "all"],
    queryFn: async () => unwrap(await api.GET("/api/v1/ums/branches", { params: { query: { Page: 1, PageSize: 100, Sort: "name" } } })),
  });
}

export function useBranch(id: string) {
  return useQuery({ queryKey: ["ums", "branch", id], enabled: !!id, queryFn: async () => unwrap(await api.GET("/api/v1/ums/branches/{id}", { params: { path: { id } } })) });
}

export function useBranchUsers(id: string) {
  return useQuery({ queryKey: ["ums", "branch", id, "users"], queryFn: async () => unwrap(await api.GET("/api/v1/ums/branches/{id}/users", { params: { path: { id } } })) });
}

export type AuditFilters = { userId?: string; branchId?: string; entity?: string; action?: Schemas["AuditAction"]; from?: string; to?: string };

export function useAuditLogs(params: ListParams & AuditFilters) {
  return useQuery({
    queryKey: ["ums", "audit", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/ums/audit-logs", {
          params: {
            query: clean({
              Page: params.page,
              PageSize: params.pageSize,
              Sort: params.sort,
              Search: params.search,
              userId: params.userId,
              branchId: params.branchId,
              entity: params.entity,
              action: params.action,
              from: params.from,
              to: params.to,
            }),
          },
        }),
      ),
  });
}

export type SignInFilters = { succeeded?: boolean; subjectType?: string; from?: string; to?: string };

export function useSignInHistory(params: ListParams & SignInFilters) {
  return useQuery({
    queryKey: ["ums", "sign-in-history", params],
    placeholderData: keepPreviousData,
    queryFn: async () =>
      unwrap(
        await api.GET("/api/v1/ums/sign-in-history", {
          params: { query: clean({ Page: params.page, PageSize: params.pageSize, Sort: params.sort, Search: params.search, succeeded: params.succeeded, subjectType: params.subjectType, from: params.from, to: params.to }) },
        }),
      ),
  });
}

/* ---- Mutations ------------------------------------------------------------------------------------------------------------ */

/** Anything that changes UMS data refreshes every UMS list and detail. */
function useUmsMutation<TVars, TData>(run: (vars: TVars) => Promise<TData>) {
  const queryClient = useQueryClient();
  return useMutation({
    mutationFn: run,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ["ums"] }),
  });
}

export const useCreateUser = () => useUmsMutation(async (body: Schemas["CreateUserRequest"]) => unwrap(await api.POST("/api/v1/ums/users", { body })));
export const useUpdateUser = (id: string) =>
  useUmsMutation(async (body: Schemas["UpdateUserRequest"]) => unwrap(await api.PUT("/api/v1/ums/users/{id}", { params: { path: { id } }, body })));
export const useResetPassword = (id: string) => useUmsMutation(async () => unwrap(await api.POST("/api/v1/ums/users/{id}/reset-password", { params: { path: { id } } })));

export type UserAction = "activate" | "deactivate" | "force-logout" | "reset-two-factor";
export const useUserAction = (id: string) =>
  useUmsMutation(async (action: UserAction) => {
    switch (action) {
      case "activate":
        return unwrap(await api.POST("/api/v1/ums/users/{id}/activate", { params: { path: { id } } }));
      case "deactivate":
        return unwrap(await api.POST("/api/v1/ums/users/{id}/deactivate", { params: { path: { id } } }));
      case "force-logout":
        return unwrap(await api.POST("/api/v1/ums/users/{id}/force-logout", { params: { path: { id } } }));
      case "reset-two-factor":
        return unwrap(await api.POST("/api/v1/ums/users/{id}/reset-two-factor", { params: { path: { id } } }));
    }
  });

export const useRevokeUserSession = (id: string) =>
  useUmsMutation(async (sessionId: string) => unwrap(await api.DELETE("/api/v1/ums/users/{id}/sessions/{sessionId}", { params: { path: { id, sessionId } } })));

export const useAddAssignment = (id: string) =>
  useUmsMutation(async (body: Schemas["AssignmentRequest"]) => unwrap(await api.POST("/api/v1/ums/users/{id}/branch-roles", { params: { path: { id } }, body })));
export const useRemoveAssignment = (id: string) =>
  useUmsMutation(async (assignmentId: string) => unwrap(await api.DELETE("/api/v1/ums/users/{id}/branch-roles/{assignmentId}", { params: { path: { id, assignmentId } } })));

export const useCreateRole = () => useUmsMutation(async (body: Schemas["CreateRoleRequest"]) => unwrap(await api.POST("/api/v1/ums/roles", { body })));
export const useUpdateRole = (id: string) =>
  useUmsMutation(async (body: Schemas["UpdateRoleRequest"]) => unwrap(await api.PUT("/api/v1/ums/roles/{id}", { params: { path: { id } }, body })));
export const useSetRolePermissions = (id: string) =>
  useUmsMutation(async (permissions: string[]) => unwrap(await api.PUT("/api/v1/ums/roles/{id}/permissions", { params: { path: { id } }, body: { permissions } })));
export const useCloneRole = () =>
  useUmsMutation(async (vars: { id: string; name: string }) => unwrap(await api.POST("/api/v1/ums/roles/{id}/clone", { params: { path: { id: vars.id } }, body: { name: vars.name } })));
export const useDeleteRole = () => useUmsMutation(async (id: string) => unwrap(await api.DELETE("/api/v1/ums/roles/{id}", { params: { path: { id } } })));

export const useCreateBranch = () => useUmsMutation(async (body: CreateBranch) => unwrap(await api.POST("/api/v1/ums/branches", { body })));
export const useUpdateBranch = (id: string) =>
  useUmsMutation(async (body: Schemas["UpdateBranchRequest"]) => unwrap(await api.PUT("/api/v1/ums/branches/{id}", { params: { path: { id } }, body })));

export type BranchAction = "activate" | "deactivate" | "set-default";
export const useBranchAction = () =>
  useUmsMutation(async (vars: { id: string; action: BranchAction }) => {
    const params = { params: { path: { id: vars.id } } };
    switch (vars.action) {
      case "activate":
        return unwrap(await api.POST("/api/v1/ums/branches/{id}/activate", params));
      case "deactivate":
        return unwrap(await api.POST("/api/v1/ums/branches/{id}/deactivate", params));
      case "set-default":
        return unwrap(await api.POST("/api/v1/ums/branches/{id}/set-default", params));
    }
  });

/* ---- Errors --------------------------------------------------------------------------------------------------------------- */

/** Shows an API error as a toast with friendly translated text. Returns the field errors (if any) so a form can mark the fields. */
export function useReportError() {
  const message = useProblemMessage();
  return (error: unknown): Record<string, string> => {
    const problem = error instanceof ApiError ? error.problem : null;
    toast.error(message(problem));
    return problem ? fieldErrors(problem) : {};
  };
}
