"use client";

import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { ApiError, api, unwrap } from "@/lib/api/client";
import { toProblem } from "@/lib/api/problem";
import type { Schemas } from "@/lib/api/types";

/** Typed access to expenses, categories, repeating expenses, accounts and the day book (juvo-be AccountingEndpoints). Through the BFF. */
export type ExpenseListItem = Schemas["ExpenseListItemDto"];
export type ExpenseDetail = Schemas["ExpenseDetailDto"];
export type SaveExpense = Schemas["SaveExpenseRequest"];
export type ExpenseCategory = Schemas["ExpenseCategoryDto"];
export type SaveExpenseCategory = Schemas["SaveExpenseCategoryRequest"];
export type Recurring = Schemas["RecurringExpenseDto"];
export type SaveRecurring = Schemas["SaveRecurringExpenseRequest"];
export type Account = Schemas["AccountDto"];
export type SaveAccount = Schemas["SaveAccountRequest"];
export type LedgerEntry = Schemas["LedgerEntryDto"];
export type AccountBalances = Schemas["AccountBalancesDto"];
export type Daybook = Schemas["DaybookDto"];
export type DaybookEntry = Schemas["DaybookEntryDto"];
export type AttachmentUpload = Schemas["AttachmentUploadDto"];

export const clean = <T extends Record<string, unknown>>(value: T) =>
  Object.fromEntries(Object.entries(value).filter(([, v]) => v !== undefined && v !== "" && v !== null && v !== false)) as Partial<T>;

export const ACCOUNTING_KEYS = { all: ["accounting"] as const };

export function useInvalidateAccounting() {
  const queryClient = useQueryClient();
  return () => {
    void queryClient.invalidateQueries({ queryKey: ACCOUNTING_KEYS.all });
    // A supplier payment booked from an account shows on the purchase order too.
    void queryClient.invalidateQueries({ queryKey: ["purchasing"] });
  };
}

/* ---- Expenses -------------------------------------------------------------------------------------------------------------- */

export type ExpenseFilter = {
  page: number;
  pageSize: number;
  sort?: string;
  search?: string;
  status?: string;
  categoryId?: string;
  branchId?: string;
  accountId?: string;
  from?: string;
  to?: string;
};

export function useExpenses(filter: ExpenseFilter) {
  const { page, pageSize, ...rest } = filter;
  return useQuery({
    queryKey: [...ACCOUNTING_KEYS.all, "expenses", filter],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/expenses", { params: { query: { Page: page, PageSize: pageSize, ...clean(rest) } as never } })),
  });
}

export function useExpense(id: string) {
  return useQuery({
    queryKey: [...ACCOUNTING_KEYS.all, "expense", id],
    queryFn: async () => unwrap(await api.GET("/api/v1/expenses/{id}", { params: { path: { id } } })),
  });
}

export function useSaveExpense() {
  const invalidate = useInvalidateAccounting();
  return useMutation({
    mutationFn: async (vars: { id?: string; body: SaveExpense }) =>
      vars.id
        ? unwrap(await api.PUT("/api/v1/expenses/{id}", { params: { path: { id: vars.id } }, body: vars.body }))
        : unwrap(await api.POST("/api/v1/expenses", { body: vars.body })),
    onSuccess: invalidate,
  });
}

/** Sends a draft for approval by its id (used right after saving a new expense). */
export function useSubmitExpense() {
  const invalidate = useInvalidateAccounting();
  return useMutation({
    mutationFn: async (id: string) => unwrap(await api.POST("/api/v1/expenses/{id}/submit", { params: { path: { id } } })),
    onSuccess: invalidate,
  });
}

export type ExpenseAction = { kind: "submit" } | { kind: "approve"; note?: string } | { kind: "reject"; reason: string } | { kind: "returnToDraft" } | { kind: "delete" };

export function useExpenseAction(id: string) {
  const invalidate = useInvalidateAccounting();
  return useMutation({
    mutationFn: async (action: ExpenseAction): Promise<ExpenseDetail | null> => {
      const path = { params: { path: { id } } };
      switch (action.kind) {
        case "submit":
          return unwrap(await api.POST("/api/v1/expenses/{id}/submit", path));
        case "approve":
          return unwrap(await api.POST("/api/v1/expenses/{id}/approve", { ...path, body: { note: action.note ?? null } }));
        case "reject":
          return unwrap(await api.POST("/api/v1/expenses/{id}/reject", { ...path, body: { reason: action.reason } }));
        case "returnToDraft":
          return unwrap(await api.POST("/api/v1/expenses/{id}/return-to-draft", path));
        case "delete":
          unwrap(await api.DELETE("/api/v1/expenses/{id}", path));
          return null;
      }
    },
    onSuccess: invalidate,
  });
}

/** Multipart upload of a receipt or bill through the BFF. Throws an ApiError with the API's problem. */
export async function uploadExpenseFile(file: File): Promise<AttachmentUpload> {
  const form = new FormData();
  form.append("file", file);
  const response = await fetch("/api/bff/api/v1/expenses/uploads", { method: "POST", body: form, credentials: "same-origin" });
  const text = await response.text();
  const parsed = text ? (JSON.parse(text) as unknown) : undefined;
  if (!response.ok) throw new ApiError(toProblem(parsed, response.status));
  return parsed as AttachmentUpload;
}

/* ---- Categories ------------------------------------------------------------------------------------------------------------ */

export function useExpenseCategories(includeInactive = false) {
  return useQuery({
    queryKey: [...ACCOUNTING_KEYS.all, "categories", includeInactive],
    staleTime: 60_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/expenses/categories", { params: { query: { includeInactive } } })),
  });
}

export function useSaveExpenseCategory() {
  const invalidate = useInvalidateAccounting();
  return useMutation({
    mutationFn: async (vars: { id?: string; body: SaveExpenseCategory }) =>
      vars.id
        ? unwrap(await api.PUT("/api/v1/expenses/categories/{id}", { params: { path: { id: vars.id } }, body: vars.body }))
        : unwrap(await api.POST("/api/v1/expenses/categories", { body: vars.body })),
    onSuccess: invalidate,
  });
}

/* ---- Repeating expenses ---------------------------------------------------------------------------------------------------- */

export function useRecurring() {
  return useQuery({
    queryKey: [...ACCOUNTING_KEYS.all, "recurring"],
    queryFn: async () => unwrap(await api.GET("/api/v1/expenses/recurring", { params: { query: {} } })),
  });
}

export function useSaveRecurring() {
  const invalidate = useInvalidateAccounting();
  return useMutation({
    mutationFn: async (vars: { id?: string; body: SaveRecurring }) =>
      vars.id
        ? unwrap(await api.PUT("/api/v1/expenses/recurring/{id}", { params: { path: { id: vars.id } }, body: vars.body }))
        : unwrap(await api.POST("/api/v1/expenses/recurring", { body: vars.body })),
    onSuccess: invalidate,
  });
}

export function useRunRecurring() {
  const invalidate = useInvalidateAccounting();
  return useMutation({
    mutationFn: async () => unwrap(await api.POST("/api/v1/expenses/recurring/run")),
    onSuccess: invalidate,
  });
}

/* ---- Accounts and the cash book -------------------------------------------------------------------------------------------- */

export function useAccounts(options: { includeInactive?: boolean; enabled?: boolean; branchId?: string } = {}) {
  const { includeInactive = false, enabled = true, branchId } = options;
  return useQuery({
    queryKey: [...ACCOUNTING_KEYS.all, "accounts", includeInactive, branchId ?? null],
    enabled,
    staleTime: 30_000,
    queryFn: async () => unwrap(await api.GET("/api/v1/accounts", { params: { query: { includeInactive, ...(branchId ? { branchId } : {}) } } })),
  });
}

export function useBalances(asOf: string | undefined, branchId: string | undefined) {
  return useQuery({
    queryKey: [...ACCOUNTING_KEYS.all, "balances", asOf ?? null, branchId ?? null],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/accounts/balances", { params: { query: { ...(asOf ? { asOf } : {}), ...(branchId ? { branchId } : {}) } } })),
  });
}

export function useAccountEntries(accountId: string | null, page: number, pageSize: number) {
  return useQuery({
    queryKey: [...ACCOUNTING_KEYS.all, "entries", accountId, page, pageSize],
    enabled: !!accountId,
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/accounts/{id}/entries", { params: { path: { id: accountId! }, query: { Page: page, PageSize: pageSize } as never } })),
  });
}

export function useSaveAccount() {
  const invalidate = useInvalidateAccounting();
  return useMutation({
    mutationFn: async (vars: { id?: string; create?: SaveAccount; update?: { name: string; type: "Cash" | "Bank" | "MobileBanking"; accountNumber: string | null; note: string | null; isActive: boolean } }) =>
      vars.id
        ? unwrap(await api.PUT("/api/v1/accounts/{id}", { params: { path: { id: vars.id } }, body: vars.update! }))
        : unwrap(await api.POST("/api/v1/accounts", { body: vars.create! })),
    onSuccess: invalidate,
  });
}

export function useTransfer() {
  const invalidate = useInvalidateAccounting();
  return useMutation({
    mutationFn: async (body: { fromAccountId: string; toAccountId: string; amount: number; date: string | null; note: string | null }) => unwrap(await api.POST("/api/v1/accounts/transfers", { body })),
    onSuccess: invalidate,
  });
}

export function useAdjust() {
  const invalidate = useInvalidateAccounting();
  return useMutation({
    mutationFn: async (body: { accountId: string; amount: number; date: string | null; reason: string }) => unwrap(await api.POST("/api/v1/accounts/adjustments", { body })),
    onSuccess: invalidate,
  });
}

export type DaybookFilter = { page: number; pageSize: number; from?: string; to?: string; branchId?: string; accountId?: string };

export function useDaybook(filter: DaybookFilter) {
  const { page, pageSize, ...rest } = filter;
  return useQuery({
    queryKey: [...ACCOUNTING_KEYS.all, "daybook", filter],
    placeholderData: keepPreviousData,
    queryFn: async () => unwrap(await api.GET("/api/v1/accounts/daybook", { params: { query: { Page: page, PageSize: pageSize, ...clean(rest) } as never } })),
  });
}
