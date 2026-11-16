"use client";

import * as React from "react";
import { useSession } from "@/components/providers/session-provider";
import { canAnywhere, canInBranch } from "@/lib/auth/permissions";

/** What this staff member may do with expenses and the cash book. Pass a record's branch id to check in that branch. Display only: the API still refuses. */
export function useAccountingPermissions(branchId?: string) {
  const { me } = useSession();
  return React.useMemo(() => {
    const has = (code: string) => (branchId ? canInBranch(me, code, branchId) : canAnywhere(me, code));
    return {
      expenseView: has("accounting.expense.view"),
      expenseCreate: has("accounting.expense.create"),
      expenseEdit: has("accounting.expense.edit"),
      expenseDelete: has("accounting.expense.delete"),
      expenseApprove: has("accounting.expense.approve"),
      cashbookView: has("accounting.cashbook.view"),
      cashbookEdit: has("accounting.cashbook.edit"),
      userId: me.staff?.id ?? null,
    };
  }, [me, branchId]);
}
