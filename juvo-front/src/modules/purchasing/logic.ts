/**
 * Pure rules of the purchasing screens: money maths, which actions a purchase order offers, and the checks run before a request is sent.
 * The API proves everything again (docs: PurchasingService); these only keep the buttons and the first error honest.
 */

export const PO_STATUSES = ["Draft", "Sent", "PartlyReceived", "Received", "Cancelled"] as const;
export type PoStatus = (typeof PO_STATUSES)[number];
export const PAYMENT_STATUSES = ["Unpaid", "PartlyPaid", "Paid"] as const;
export type SupplierPaymentStatus = (typeof PAYMENT_STATUSES)[number];
export const MONEY_METHODS = ["Cash", "Bank", "MobileBanking", "Cheque", "Other"] as const;
export type MoneyMethod = (typeof MONEY_METHODS)[number];

/** Rounds to two decimals the way the API does (half away from zero), working in cents to avoid 0.1 + 0.2 drift. */
export function round2(value: number): number {
  const sign = value < 0 ? -1 : 1;
  return (sign * Math.round(Math.abs(value) * 100 + 1e-9)) / 100;
}

/** Digits of either language to 0-9, then a number with at most two decimals; null when it is not a plain amount. */
export function parseAmount(text: string): number | null {
  const ascii = text
    .replace(/[০-৯]/g, (ch) => String(ch.charCodeAt(0) - 0x09e6))
    .replace(/,/g, "")
    .trim();
  if (!/^\d+(\.\d{1,2})?$/.test(ascii)) return null;
  const n = Number(ascii);
  return Number.isFinite(n) ? n : null;
}

/** Whole number from digits of either language, or null. */
export function parseQuantity(text: string): number | null {
  const ascii = text.replace(/[০-৯]/g, (ch) => String(ch.charCodeAt(0) - 0x09e6)).trim();
  if (!/^\d{1,7}$/.test(ascii)) return null;
  return Number(ascii);
}

export type DraftLine = { quantity: string; unitCost: string };

export function lineTotal(quantity: number, unitCost: number): number {
  return round2(quantity * unitCost);
}

/** Total of the lines that hold valid numbers (a half-typed line counts as zero until it is complete). */
export function draftTotal(lines: readonly DraftLine[]): number {
  let total = 0;
  for (const line of lines) {
    const q = parseQuantity(line.quantity);
    const c = parseAmount(line.unitCost);
    if (q !== null && c !== null) total += q * c;
  }
  return round2(total);
}

export type DraftProblem = "noLines" | "quantity" | "cost" | "supplier" | "branch";

/** First problem of a draft order before it is sent to the API, or null. */
export function draftProblem(input: { supplierId: string; branchId: string; lines: readonly DraftLine[] }): DraftProblem | null {
  if (!input.supplierId) return "supplier";
  if (!input.branchId) return "branch";
  if (input.lines.length === 0) return "noLines";
  for (const line of input.lines) {
    const q = parseQuantity(line.quantity);
    if (q === null || q < 1 || q > 1_000_000) return "quantity";
    const c = parseAmount(line.unitCost);
    if (c === null || c > 100_000_000) return "cost";
  }
  return null;
}

export type Permissions = {
  edit: boolean;
  send: boolean;
  receive: boolean;
  cancel: boolean;
  pay: boolean;
};

export type OrderFacts = { status: string; amountPaid: number; outstanding: number };

/** What a purchase order offers right now, from its status and what the person may do in its branch (mirrors PurchasingService). */
export function allowedActions(order: OrderFacts, can: Permissions) {
  const s = order.status;
  return {
    edit: can.edit && s === "Draft",
    send: can.send && s === "Draft",
    receive: can.receive && (s === "Sent" || s === "PartlyReceived"),
    closeShort: can.receive && s === "PartlyReceived",
    cancel: can.cancel && (s === "Draft" || s === "Sent") && order.amountPaid <= 0,
    invoice: can.edit && s !== "Draft" && s !== "Cancelled",
    pay: can.pay && s !== "Draft" && s !== "Cancelled" && order.outstanding > 0,
    attach: can.edit,
  };
}

export type ReceiveRow = { lineId: string; outstanding: number; accepted: string; rejected: string };
export type ReceiveProblem = { lineId: string; kind: "invalid" | "over" } | { lineId: null; kind: "nothing" };

/** Checks a receipt before sending: numbers, nothing above what is still expected (accepted units), at least one unit entered. */
export function receiveProblems(rows: readonly ReceiveRow[]): ReceiveProblem[] {
  const problems: ReceiveProblem[] = [];
  let any = false;
  for (const row of rows) {
    const a = row.accepted === "" ? 0 : parseQuantity(row.accepted);
    const r = row.rejected === "" ? 0 : parseQuantity(row.rejected);
    if (a === null || r === null) {
      problems.push({ lineId: row.lineId, kind: "invalid" });
      continue;
    }
    if (a > row.outstanding) problems.push({ lineId: row.lineId, kind: "over" });
    if (a + r > 0) any = true;
  }
  if (problems.length === 0 && !any) problems.push({ lineId: null, kind: "nothing" });
  return problems;
}

/** The request lines of a receipt: only rows with something entered. */
export function receiveLines(rows: readonly ReceiveRow[]) {
  return rows
    .map((row) => ({ lineId: row.lineId, acceptedQuantity: parseQuantity(row.accepted || "0") ?? 0, rejectedQuantity: parseQuantity(row.rejected || "0") ?? 0, note: null }))
    .filter((l) => l.acceptedQuantity + l.rejectedQuantity > 0);
}

export type PaymentProblem = "amount" | "exceeds" | null;

/** A supplier payment must be above zero and at most what is still due. */
export function paymentProblem(text: string, outstanding: number): PaymentProblem {
  const n = parseAmount(text);
  if (n === null || n <= 0) return "amount";
  return round2(n) > round2(outstanding) ? "exceeds" : null;
}

/** Variants at or below their reorder level become order lines at the suggested quantity and the last unit cost (empty when never ordered). */
export function suggestionToLine(s: { suggestedQuantity: number; lastUnitCost: number | string | null }): DraftLine {
  const cost = s.lastUnitCost === null || s.lastUnitCost === undefined ? "" : String(round2(Number(s.lastUnitCost)));
  return { quantity: String(Math.max(1, s.suggestedQuantity)), unitCost: cost };
}

/** Receipt lines whose cost was not yet applied to the product (booked by someone who may not change costs). */
export function receiptCostPending(lines: readonly { acceptedQuantity: number | string; costApplied: boolean }[]): boolean {
  return lines.some((l) => Number(l.acceptedQuantity) > 0 && !l.costApplied);
}

export const PO_TONE: Record<string, "neutral" | "info" | "warning" | "success" | "danger" | "outline"> = {
  Draft: "neutral",
  Sent: "info",
  PartlyReceived: "warning",
  Received: "success",
  Cancelled: "outline",
};

export const PAYMENT_TONE: Record<string, "neutral" | "warning" | "success"> = { Unpaid: "neutral", PartlyPaid: "warning", Paid: "success" };

/** Sign shown with a balance owed: owed to the supplier is positive "due", zero is "settled". */
export function dueState(outstanding: number): "due" | "settled" {
  return outstanding > 0 ? "due" : "settled";
}
