/**
 * Pure logic of the COD settlement screens: totals, differences, validation and which actions a batch offers. No state and no calls,
 * unit tested in tests/cod.test.ts. The money rules follow docs/couriers-api.md section 8 (difference = received - expected,
 * negative is short, positive is over).
 */
import { isValidReceived, varianceOf, type Variance } from "@/modules/couriers/status";

export const NOTE_MAX = 300;
export const REFERENCE_MAX = 100;

export const round2 = (n: number) => Math.round((n + Number.EPSILON) * 100) / 100;

/** The API sends decimals as numbers or strings. */
export function num(value: number | string | null | undefined): number {
  if (value === null || value === undefined || value === "") return 0;
  const n = typeof value === "number" ? value : Number(value);
  return Number.isFinite(n) ? n : 0;
}

/** Received minus expected rounded to two decimals; null while the typed amount is not valid. */
export function lineDifference(expected: number, receivedText: string): number | null {
  if (!isValidReceived(receivedText)) return null;
  return round2(Number(receivedText) - expected);
}

export type LineInput = { shipmentId: string; expected: number; received: string; note: string };

export type LineCheck = {
  valid: boolean;
  difference: number | null;
  variance: Variance | null;
  /** A short or over parcel without a note. */
  noteMissing: boolean;
  noteTooLong: boolean;
};

export function checkLine(line: LineInput): LineCheck {
  const difference = lineDifference(line.expected, line.received);
  if (difference === null) return { valid: false, difference: null, variance: null, noteMissing: false, noteTooLong: false };
  const variance = varianceOf(line.expected, Number(line.received));
  const noteMissing = variance !== "exact" && line.note.trim() === "";
  const noteTooLong = line.note.trim().length > NOTE_MAX;
  return { valid: !noteMissing && !noteTooLong, difference, variance, noteMissing, noteTooLong };
}

export type BatchTotals = { expected: number; received: number; difference: number; short: number; over: number; invalid: number; noteMissing: number };

/** Totals of the whole batch from what is typed. A line with an invalid amount counts as invalid and adds nothing to the received total. */
export function batchTotals(lines: readonly LineInput[]): BatchTotals {
  let expected = 0;
  let received = 0;
  let short = 0;
  let over = 0;
  let invalid = 0;
  let noteMissing = 0;
  for (const line of lines) {
    expected += line.expected;
    const check = checkLine(line);
    if (check.difference === null) {
      invalid += 1;
      continue;
    }
    received += Number(line.received);
    if (check.variance === "short") short += 1;
    if (check.variance === "over") over += 1;
    if (check.noteMissing) noteMissing += 1;
  }
  expected = round2(expected);
  received = round2(received);
  return { expected, received, difference: round2(received - expected), short, over, invalid, noteMissing };
}

export type LineErrors = Record<string, "amount" | "note" | "noteLong">;

/** What must be fixed before the lines may be sent, keyed by parcel. Empty means ready. */
export function lineErrors(lines: readonly LineInput[]): LineErrors {
  const errors: LineErrors = {};
  for (const line of lines) {
    const check = checkLine(line);
    if (check.difference === null) errors[line.shipmentId] = "amount";
    else if (check.noteMissing) errors[line.shipmentId] = "note";
    else if (check.noteTooLong) errors[line.shipmentId] = "noteLong";
  }
  return errors;
}

/** The request body of the reconcile call: every parcel explicitly, the note only where there is one. */
export function toReconcileLines(lines: readonly LineInput[]): { shipmentId: string; received: number; note: string | null }[] {
  return lines.map((line) => ({ shipmentId: line.shipmentId, received: round2(Number(line.received)), note: line.note.trim() === "" ? null : line.note.trim() }));
}

/** Which buttons a batch offers: reconcile while open or reconciled, settle only once reconciled, cancel before it is settled. */
export function availableActions(status: string, canSettle: boolean) {
  const editable = status === "Open" || status === "Reconciled";
  return {
    reconcile: canSettle && editable,
    settle: canSettle && status === "Reconciled",
    cancel: canSettle && editable,
  };
}

/** Totals of the parcels ticked on the new-batch screen (all parcels except the ones left out). */
export function selectionTotals(items: readonly { id: string; amount: number }[], excluded: ReadonlySet<string>) {
  let count = 0;
  let total = 0;
  for (const item of items) {
    if (excluded.has(item.id)) continue;
    count += 1;
    total += item.amount;
  }
  return { count, total: round2(total) };
}

/** The ids to send: nothing when every waiting parcel is chosen (the API then takes them all), otherwise the chosen ones. */
export function shipmentIdsToSend(items: readonly { id: string }[], excluded: ReadonlySet<string>): string[] | undefined {
  const chosen = items.filter((i) => !excluded.has(i.id)).map((i) => i.id);
  return chosen.length === items.length ? undefined : chosen;
}

/** Cash expected from a waiting parcel: what the courier reported collecting, else what was to be collected. */
export const pendingAmount = (p: { codAmount: number | string; codCollectedAmount: number | string | null }) => (p.codCollectedAmount !== null && p.codCollectedAmount !== undefined ? num(p.codCollectedAmount) : num(p.codAmount));
