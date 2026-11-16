import { parseAmount, parseQuantity, round2 } from "@/modules/purchasing/logic";

/**
 * Pure rules of the expense and cash book screens: which workflow steps an expense offers, form checks, and how signed amounts are shown.
 * The API proves everything again; these only keep buttons and the first error honest.
 */

export const EXPENSE_STATUSES = ["Draft", "Submitted", "Approved", "Rejected"] as const;
export type ExpenseStatus = (typeof EXPENSE_STATUSES)[number];
export const ACCOUNT_TYPES = ["Cash", "Bank", "MobileBanking"] as const;
export type AccountType = (typeof ACCOUNT_TYPES)[number];
export { MONEY_METHODS } from "@/modules/purchasing/logic";

export const EXPENSE_TONE: Record<string, "neutral" | "info" | "success" | "danger"> = { Draft: "neutral", Submitted: "info", Approved: "success", Rejected: "danger" };

export type ExpensePermissions = { create: boolean; edit: boolean; delete: boolean; approve: boolean };

/**
 * Steps an expense offers. `author` is true when this person is known to have sent it for approval (the API does not name the author, so
 * the screen remembers what this person submitted); a known author does not get the approve button. The API refuses self approval anyway.
 */
export function expenseActions(status: string, can: ExpensePermissions, author: boolean) {
  return {
    edit: can.edit && status === "Draft",
    submit: can.create && status === "Draft",
    delete: can.delete && status === "Draft",
    approve: can.approve && status === "Submitted" && !author,
    reject: can.approve && status === "Submitted",
    returnToDraft: can.create && (status === "Submitted" || status === "Rejected"),
  };
}

export type ExpenseForm = { categoryId: string; accountId: string; amount: string; date: string; method: string; branchId: string };
export type ExpenseProblem = "branch" | "category" | "account" | "amount" | "date" | null;

export function expenseProblem(form: ExpenseForm): ExpenseProblem {
  if (!form.branchId) return "branch";
  if (!form.categoryId) return "category";
  if (!form.accountId) return "account";
  const n = parseAmount(form.amount);
  if (n === null || n <= 0 || n > 1_000_000_000) return "amount";
  if (!/^\d{4}-\d{2}-\d{2}$/.test(form.date)) return "date";
  return null;
}

export const RECEIPT_TYPES = ["image/jpeg", "image/png", "image/webp", "application/pdf"] as const;
export const RECEIPT_MAX_BYTES = 8 * 1024 * 1024;

/** Client-side check of a receipt before upload (the API checks again). */
export function receiptProblem(file: { type: string; size: number }): "type" | "size" | null {
  if (!(RECEIPT_TYPES as readonly string[]).includes(file.type)) return "type";
  if (file.size > RECEIPT_MAX_BYTES) return "size";
  return null;
}

/** Day 1 to 28 as the API allows for a repeating expense. */
export function dayOfMonthProblem(text: string): boolean {
  const n = parseQuantity(text);
  return n === null || n < 1 || n > 28;
}

export type FlowDirection = "in" | "out" | "zero";

/** Whether a signed cash amount brought money in or took it out; the screen shows words and a sign, never colour alone. */
export function flowOf(amount: number): FlowDirection {
  const a = round2(amount);
  return a > 0 ? "in" : a < 0 ? "out" : "zero";
}

/** An account balance below zero is shown as "overdrawn" so a negative number is not mistaken for a typo. */
export function balanceState(balance: number): "positive" | "zero" | "overdrawn" {
  const b = round2(balance);
  return b > 0 ? "positive" : b < 0 ? "overdrawn" : "zero";
}

export type TransferProblem = "same" | "amount" | "from" | "to" | null;

export function transferProblem(from: string, to: string, amount: string): TransferProblem {
  if (!from) return "from";
  if (!to) return "to";
  if (from === to) return "same";
  const n = parseAmount(amount);
  return n === null || n <= 0 ? "amount" : null;
}

/** An adjustment is a signed amount (a minus sign takes money out) with a reason. */
export function parseSigned(text: string): number | null {
  const t = text.trim();
  if (t.startsWith("-") || t.startsWith("−")) {
    const n = parseAmount(t.slice(1));
    return n === null ? null : -n;
  }
  return parseAmount(t.startsWith("+") ? t.slice(1) : t);
}

export type AdjustmentProblem = "amount" | "reason" | null;

export function adjustmentProblem(amount: string, reason: string): AdjustmentProblem {
  const n = parseSigned(amount);
  if (n === null || n === 0) return "amount";
  return reason.trim() ? null : "reason";
}

/** Net of a list of signed amounts: the "running total" shown under a page of the day book. */
export function netOf(amounts: readonly number[]): number {
  return round2(amounts.reduce((sum, a) => sum + a, 0));
}

/** Ids of expenses this person submitted on this device, kept so the approve button can be hidden for their own expenses. */
export function addSubmitted(stored: string | null, id: string, limit = 200): string {
  let list: string[] = [];
  try {
    const parsed = stored ? (JSON.parse(stored) as unknown) : [];
    list = Array.isArray(parsed) ? parsed.filter((x): x is string => typeof x === "string") : [];
  } catch {
    list = [];
  }
  return JSON.stringify([id, ...list.filter((x) => x !== id)].slice(0, limit));
}

export function wasSubmitted(stored: string | null, id: string): boolean {
  try {
    const parsed = stored ? (JSON.parse(stored) as unknown) : [];
    return Array.isArray(parsed) && parsed.includes(id);
  } catch {
    return false;
  }
}

/** Today in Bangladesh (UTC+6) as yyyy-mm-dd, the default date of new entries (the API's own day is the Dhaka day). */
export function todayDhaka(now: Date = new Date()): string {
  return new Date(now.getTime() + 6 * 3600_000).toISOString().slice(0, 10);
}
