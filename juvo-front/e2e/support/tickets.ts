import { expect } from "@playwright/test";
import { api } from "./env";
import { adminAccess, customerAccess } from "./returns";

/** Data for the support specs, made through the real API (the browser tests then work on it). */
export type TicketRef = { id: string; number: string };

export const uniqueText = (prefix: string) => `${prefix} ${Math.random().toString(36).slice(2, 8)}`;

/** The newest staff-visible ticket matching a search text. */
export async function findTicket(search: string, token?: string): Promise<TicketRef> {
  const t = token ?? (await adminAccess());
  const result = await api<{ items: { id: string; number: string }[] }>("GET", `/api/v1/support/tickets?Search=${encodeURIComponent(search)}&PageSize=5`, undefined, t);
  expect(result.status, JSON.stringify(result.json)).toBe(200);
  expect(result.json.items.length, `no ticket found for "${search}"`).toBeGreaterThan(0);
  return result.json.items[0]!;
}

/** A ticket opened by staff (phone) for a customer mobile; returns the ticket. */
export async function staffTicket(options: { mobile: string; subject?: string; message?: string; priority?: string; category?: string; orderNumber?: string | null; name?: string; token?: string }): Promise<TicketRef & { subject: string }> {
  const token = options.token ?? (await adminAccess());
  const subject = options.subject ?? uniqueText("Phone call");
  const created = await api<{ id: string; number: string }>(
    "POST",
    "/api/v1/support/tickets",
    { mobile: options.mobile, customerName: options.name ?? "Nadia Rahman", subject, message: options.message ?? "The customer called about this.", channel: "Phone", priority: options.priority ?? "Normal", category: options.category ?? "other", orderNumber: options.orderNumber ?? null, attachmentPaths: [] },
    token,
  );
  expect(created.status, JSON.stringify(created.json)).toBe(201);
  return { id: created.json.id, number: created.json.number, subject };
}

/** A ticket written by a signed-in customer through the API. */
export async function customerTicket(mobile: string, options: { subject?: string; message?: string; orderNumber?: string | null; category?: string } = {}): Promise<TicketRef & { subject: string }> {
  const token = await customerAccess(mobile);
  const subject = options.subject ?? uniqueText("My question");
  const created = await api<{ id: string; number: string }>("POST", "/api/v1/me/tickets", { subject, message: options.message ?? "Please help me.", category: options.category ?? "other", orderNumber: options.orderNumber ?? null, attachmentPaths: [] }, token);
  expect(created.status, JSON.stringify(created.json)).toBe(201);
  return { id: created.json.id, number: created.json.number, subject };
}

/** One staff step on a ticket (reply, status, resolve, close, reopen, assign, priority) through the API. */
export async function staffStep(id: string, action: "reply" | "status" | "resolve" | "close" | "reopen" | "assign" | "priority", body: unknown = {}, token?: string): Promise<void> {
  const t = token ?? (await adminAccess());
  const method = action === "priority" ? "PUT" : "POST";
  const result = await api(method, `/api/v1/support/tickets/${id}/${action}`, body, t);
  expect(result.status, `${action}: ${JSON.stringify(result.json)}`).toBeLessThan(300);
}

export async function summary(token?: string): Promise<{ byStatus: Record<string, number>; active: number; unassigned: number; overdue: number; mine: number }> {
  const t = token ?? (await adminAccess());
  const result = await api<{ byStatus: Record<string, number | string>; active: number; unassigned: number; overdue: number; mine: number }>("GET", "/api/v1/support/tickets/summary", undefined, t);
  expect(result.status).toBe(200);
  const byStatus = Object.fromEntries(Object.entries(result.json.byStatus).map(([k, v]) => [k, Number(v)]));
  return { ...result.json, byStatus };
}

export async function canned(title: string, body: string, language: "en" | "bn", token?: string) {
  const t = token ?? (await adminAccess());
  const created = await api("POST", "/api/v1/support/canned-replies", { title, body, language, category: "delivery", isActive: true }, t);
  expect(created.status, JSON.stringify(created.json)).toBe(201);
}

export async function reportOf(from: string, to: string, token?: string) {
  const t = token ?? (await adminAccess());
  const result = await api<{ newTickets: number | string; resolvedTickets: number | string; openNow: number | string; overdueNow: number | string }>("GET", `/api/v1/support/report?from=${from}&to=${to}`, undefined, t);
  expect(result.status, JSON.stringify(result.json)).toBe(200);
  return { newTickets: Number(result.json.newTickets), resolvedTickets: Number(result.json.resolvedTickets), openNow: Number(result.json.openNow), overdueNow: Number(result.json.overdueNow) };
}

/** Bangladesh date (UTC+6) as yyyy-MM-dd, `daysAgo` days back. */
export function bdDay(daysAgo = 0): string {
  return new Date(Date.now() + 6 * 3600_000 - daysAgo * 86_400_000).toISOString().slice(0, 10);
}
