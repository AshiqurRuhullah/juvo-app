import type { Schemas } from "@/lib/api/types";

/** Pure rules of the support screens (no React, no network), so they can be tested on their own (docs/support-api.md). */

export type TicketStatus = Schemas["TicketStatus"];
export type TicketPriority = Schemas["TicketPriority"];
export type TicketChannel = Schemas["TicketChannel"];
export type MessageKind = Schemas["TicketMessageKind"];

export const TICKET_STATUSES: TicketStatus[] = ["Open", "InProgress", "Waiting", "Resolved", "Closed"];
export const TICKET_PRIORITIES: TicketPriority[] = ["Low", "Normal", "High", "Urgent"];
export const TICKET_CHANNELS: TicketChannel[] = ["Web", "Phone", "WhatsApp", "Facebook", "Email", "OrderPage"];
/** Channels staff can pick when they open a ticket for a customer (web and order page come from the shop itself). */
export const STAFF_CHANNELS: TicketChannel[] = ["Phone", "WhatsApp", "Facebook", "Email"];
export const TICKET_CATEGORIES = ["order", "delivery", "payment", "product", "return", "account", "other"] as const;
export type TicketCategory = (typeof TICKET_CATEGORIES)[number];

export const SUBJECT_MAX = 200;
export const MESSAGE_MAX = 4000;
export const NAME_MAX = 120;
export const BULK_LIMIT = 50;
export const ATTACHMENT_MAX_FILES = 5;
export const ATTACHMENT_MAX_BYTES = 8 * 1024 * 1024;
export const ATTACHMENT_TYPES = ["image/jpeg", "image/png", "image/webp", "application/pdf"] as const;

export const isClosedStatus = (s: TicketStatus) => s === "Closed";
export const isActiveStatus = (s: TicketStatus) => s !== "Resolved" && s !== "Closed";

export type Tone = "info" | "warning" | "success" | "danger" | "outline" | "neutral";

export function statusTone(status: TicketStatus): Tone {
  switch (status) {
    case "Open":
      return "warning";
    case "InProgress":
      return "info";
    case "Waiting":
      return "neutral";
    case "Resolved":
      return "success";
    case "Closed":
      return "outline";
  }
}

export function priorityTone(priority: TicketPriority): Tone {
  switch (priority) {
    case "Urgent":
      return "danger";
    case "High":
      return "warning";
    case "Normal":
      return "neutral";
    case "Low":
      return "outline";
  }
}

/** Client-side check of one attachment before upload (the API checks the first bytes again). */
export function attachmentProblem(file: { type: string; size: number }): "type" | "size" | null {
  if (!(ATTACHMENT_TYPES as readonly string[]).includes(file.type)) return "type";
  if (file.size > ATTACHMENT_MAX_BYTES) return "size";
  return null;
}

/** True when adding `incoming` files to `current` would pass the limit of 5 per message. */
export const tooManyFiles = (current: number, incoming: number) => current + incoming > ATTACHMENT_MAX_FILES;

/* ---- Staff actions ---------------------------------------------------------------------------------------------------------- */

export type StaffRights = { reply: boolean; close: boolean; assign: boolean };
export type TicketAction = "inProgress" | "waiting" | "resolve" | "close" | "reopen";
export type ActionState = { action: TicketAction; target: TicketStatus; allowed: boolean; reason?: "permission" | "window" };

const ACTION_TARGET: Record<TicketAction, TicketStatus> = { inProgress: "InProgress", waiting: "Waiting", resolve: "Resolved", close: "Closed", reopen: "Open" };

/** Ending the conversation (resolve, close) is confirmed first. */
export const needsConfirmation = (action: TicketAction) => action === "resolve" || action === "close";

/**
 * The status actions of one ticket, from the API's `nextStatuses` and `canReopen` and the person's rights. Rules (docs/support-api.md Y7):
 * closing and any reopening need `support.ticket.close`; the other moves need `support.ticket.reply`. A reopening the window forbids
 * is listed as not allowed with the reason, so the screen can explain it instead of hiding the button. "Open" from an active ticket is
 * not offered as reopen (reopen means leaving Resolved or Closed).
 */
export function allowedActions(ticket: { status: TicketStatus; nextStatuses: readonly string[]; canReopen: boolean }, rights: StaffRights): ActionState[] {
  const next = new Set(ticket.nextStatuses);
  const out: ActionState[] = [];
  const add = (action: TicketAction) => {
    const target = ACTION_TARGET[action];
    if (action === "reopen") {
      if (!next.has("Open") || isActiveStatus(ticket.status)) return;
      if (!rights.close) out.push({ action, target, allowed: false, reason: "permission" });
      else if (!ticket.canReopen) out.push({ action, target, allowed: false, reason: "window" });
      else out.push({ action, target, allowed: true });
      return;
    }
    if (!next.has(target)) return;
    const permitted = action === "close" ? rights.close : rights.reply;
    out.push(permitted ? { action, target, allowed: true } : { action, target, allowed: false, reason: "permission" });
  };
  (["inProgress", "waiting", "resolve", "close", "reopen"] as const).forEach(add);
  return out;
}

/** Statuses a reply may set together with the message (Waiting or Resolved; Closed needs the close right). */
export function replyStatusChoices(ticket: { status: TicketStatus; nextStatuses: readonly string[] }, rights: StaffRights): TicketStatus[] {
  const next = new Set(ticket.nextStatuses);
  const choices: TicketStatus[] = [];
  if (next.has("Waiting")) choices.push("Waiting");
  if (next.has("InProgress") && ticket.status !== "InProgress") choices.push("InProgress");
  if (next.has("Resolved")) choices.push("Resolved");
  if (next.has("Closed") && rights.close) choices.push("Closed");
  return choices;
}

/** Whether a public reply is possible: not on a closed ticket. An internal note is always possible. */
export const canPublicReply = (status: TicketStatus) => status !== "Closed";

/* ---- Thread ----------------------------------------------------------------------------------------------------------------- */

export type ThreadMessage = {
  id: string;
  kind: MessageKind;
  text: string;
  authorName: string | null;
  at: string;
  attachments: { id: string; fileName: string; contentType: string; size: number | string; url: string }[];
};

export type ThreadView = "customer" | "staff" | "internal" | "system";

/** What a message looks like in the staff view: customer and staff replies are customer-visible; internal notes are a different box. */
export function threadView(kind: MessageKind): ThreadView {
  switch (kind) {
    case "Customer":
      return "customer";
    case "Staff":
      return "staff";
    case "Internal":
      return "internal";
    case "System":
      return "system";
  }
}

export const isInternal = (kind: MessageKind) => kind === "Internal";

export type ThreadDay = { day: string; messages: ThreadMessage[] };

/** Groups messages (oldest first) by local calendar day, so a long thread shows a date line instead of a date on every message. */
export function groupByDay(messages: readonly ThreadMessage[], toDay: (iso: string) => string = (iso) => iso.slice(0, 10)): ThreadDay[] {
  const groups: ThreadDay[] = [];
  for (const message of messages) {
    const day = toDay(message.at);
    const last = groups[groups.length - 1];
    if (last && last.day === day) last.messages.push(message);
    else groups.push({ day, messages: [message] });
  }
  return groups;
}

/** Counts public and internal messages of a thread. */
export function threadCounts(messages: readonly { kind: MessageKind }[]) {
  let internal = 0;
  let publicCount = 0;
  for (const m of messages) {
    if (m.kind === "Internal") internal++;
    else if (m.kind !== "System") publicCount++;
  }
  return { internal, public: publicCount };
}

/* ---- Canned replies --------------------------------------------------------------------------------------------------------- */

export const PLACEHOLDERS = ["customerName", "orderNumber", "ticketNumber"] as const;
export type PlaceholderName = (typeof PLACEHOLDERS)[number];
export type PlaceholderValues = Partial<Record<PlaceholderName, string | null | undefined>>;

/** Names of every `{word}` in a text that are not one of the three allowed ones (the API refuses them when saving). */
export function unknownPlaceholders(body: string): string[] {
  const found = new Set<string>();
  for (const m of body.matchAll(/\{([^{}\s]+)\}/g)) {
    if (!(PLACEHOLDERS as readonly string[]).includes(m[1]!)) found.add(m[1]!);
  }
  return [...found];
}

/** The text with the known placeholders filled in (an empty value leaves the placeholder visible so nobody sends it blank by mistake). */
export function renderPreview(body: string, values: PlaceholderValues): { text: string; empty: PlaceholderName[] } {
  const empty: PlaceholderName[] = [];
  const text = body.replace(/\{(customerName|orderNumber|ticketNumber)\}/g, (whole, name: PlaceholderName) => {
    const value = values[name];
    if (value === undefined || value === null || value === "") {
      if (!empty.includes(name)) empty.push(name);
      return whole;
    }
    return value;
  });
  return { text, empty };
}

export const SAMPLE_VALUES: Required<PlaceholderValues> = { customerName: "Nadia Rahman", orderNumber: "JV-DHK01-2611-000123", ticketNumber: "TK-2611-000007" };

export type CannedForm = { title: string; body: string; language: string; category: string; isActive: boolean };
export type CannedError = "title" | "body" | "placeholder" | null;

export function cannedProblem(form: CannedForm): { field: "title" | "body"; error: "title" | "body" | "placeholder" | "tooLong" } | null {
  if (form.title.trim() === "") return { field: "title", error: "title" };
  if (form.title.trim().length > 120) return { field: "title", error: "tooLong" };
  if (form.body.trim() === "") return { field: "body", error: "body" };
  if (form.body.length > MESSAGE_MAX) return { field: "body", error: "tooLong" };
  if (unknownPlaceholders(form.body).length > 0) return { field: "body", error: "placeholder" };
  return null;
}

/* ---- Forms ------------------------------------------------------------------------------------------------------------------ */

export type FormErrors<K extends string> = Partial<Record<K, "required" | "tooLong" | "mobile" | "email">>;

const BN_DIGITS = "০১২৩৪৫৬৭৮৯";
/** Converts Bengali digits to ASCII so the mobile check accepts both. */
export function asciiDigits(text: string): string {
  return text.replace(/[০-৯]/g, (d) => String(BN_DIGITS.indexOf(d)));
}

/** A Bangladesh mobile number as people type it: 01XXXXXXXXX, +8801XXXXXXXXX or 8801XXXXXXXXX (operators 013 to 019). */
export function validMobile(text: string): boolean {
  const compact = asciiDigits(text).replace(/[\s-]/g, "");
  return /^(?:\+?880|0)1[3-9]\d{8}$/.test(compact);
}

export const validEmail = (text: string) => /^[^\s@]+@[^\s@]+\.[^\s@]+$/.test(text.trim());

export type ContactForm = { name: string; mobile: string; email: string; subject: string; message: string; category: string; orderNumber: string };

export function validateContact(form: ContactForm): FormErrors<"name" | "mobile" | "email" | "subject" | "message" | "orderNumber"> {
  const e: FormErrors<"name" | "mobile" | "email" | "subject" | "message" | "orderNumber"> = {};
  if (form.name.trim() === "") e.name = "required";
  else if (form.name.trim().length > NAME_MAX) e.name = "tooLong";
  if (form.mobile.trim() === "") e.mobile = "required";
  else if (!validMobile(form.mobile)) e.mobile = "mobile";
  if (form.email.trim() !== "" && !validEmail(form.email)) e.email = "email";
  if (form.subject.trim() === "") e.subject = "required";
  else if (form.subject.trim().length > SUBJECT_MAX) e.subject = "tooLong";
  if (form.message.trim() === "") e.message = "required";
  else if (form.message.length > MESSAGE_MAX) e.message = "tooLong";
  return e;
}

export type MyTicketForm = { subject: string; message: string; category: string; orderNumber: string };

export function validateMyTicket(form: MyTicketForm): FormErrors<"subject" | "message"> {
  const e: FormErrors<"subject" | "message"> = {};
  if (form.subject.trim() === "") e.subject = "required";
  else if (form.subject.trim().length > SUBJECT_MAX) e.subject = "tooLong";
  if (form.message.trim() === "") e.message = "required";
  else if (form.message.length > MESSAGE_MAX) e.message = "tooLong";
  return e;
}

export type StaffTicketForm = { mobile: string; customerName: string; email: string; subject: string; message: string; channel: string; priority: string; category: string; orderNumber: string };

export function validateStaffTicket(form: StaffTicketForm): FormErrors<"mobile" | "customerName" | "email" | "subject" | "message"> {
  const e: FormErrors<"mobile" | "customerName" | "email" | "subject" | "message"> = {};
  if (form.mobile.trim() === "") e.mobile = "required";
  else if (!validMobile(form.mobile)) e.mobile = "mobile";
  if (form.customerName.trim().length > NAME_MAX) e.customerName = "tooLong";
  if (form.email.trim() !== "" && !validEmail(form.email)) e.email = "email";
  if (form.subject.trim() === "") e.subject = "required";
  else if (form.subject.trim().length > SUBJECT_MAX) e.subject = "tooLong";
  if (form.message.trim() === "") e.message = "required";
  else if (form.message.length > MESSAGE_MAX) e.message = "tooLong";
  return e;
}

export const hasErrors = (errors: object) => Object.keys(errors).length > 0;

/* ---- Filters ---------------------------------------------------------------------------------------------------------------- */

export type TicketFilters = {
  status?: string;
  priority?: string;
  assigneeId?: string;
  channel?: string;
  category?: string;
  branchId?: string;
  orderNumber?: string;
  from?: string;
  to?: string;
  chip?: string;
};

/** The summary chips above the list. `all` clears; the others set the matching filter. */
export const SUMMARY_CHIPS = ["active", "unassigned", "overdue", "mine"] as const;
export type SummaryChip = (typeof SUMMARY_CHIPS)[number];

/** List filters and screen state to the API's query: empty values are dropped; the chips become `active`, `unassigned`, `overdue` or the person's own id. */
export function toTicketQuery(
  filters: TicketFilters,
  options: { page: number; pageSize: number; search?: string; sort?: string; me?: string | null },
): Record<string, string | number | boolean> {
  const q: Record<string, string | number | boolean> = { Page: options.page, PageSize: options.pageSize };
  const put = (key: string, value: string | undefined) => {
    if (value !== undefined && value.trim() !== "") q[key] = value.trim();
  };
  put("Search", options.search);
  put("Sort", options.sort);
  put("status", filters.status);
  put("priority", filters.priority);
  put("channel", filters.channel);
  put("category", filters.category);
  put("branchId", filters.branchId);
  put("orderNumber", filters.orderNumber);
  put("from", filters.from);
  put("to", filters.to);
  if (filters.assigneeId === "__none") q.unassigned = true;
  else put("assigneeId", filters.assigneeId);
  switch (filters.chip) {
    case "active":
      q.active = true;
      break;
    case "unassigned":
      q.unassigned = true;
      break;
    case "overdue":
      q.overdue = true;
      break;
    case "mine":
      if (options.me) q.assigneeId = options.me;
      break;
  }
  return q;
}

/** Sort column ids of the list to the API's sort keys. */
export const SORT_KEYS: Record<string, string> = { number: "number", status: "status", priority: "priority", created: "created", activity: "activity", due: "due" };

/* ---- Report ----------------------------------------------------------------------------------------------------------------- */

/** Minutes as "2 h 5 min" parts, so the screen can translate the units. Null when there is nothing to measure. */
export function durationParts(minutes: number | null | undefined): { days: number; hours: number; minutes: number } | null {
  if (minutes === null || minutes === undefined || !Number.isFinite(minutes)) return null;
  const total = Math.max(0, Math.round(minutes));
  return { days: Math.floor(total / 1440), hours: Math.floor((total % 1440) / 60), minutes: total % 60 };
}

/** Age in hours as a short number of days or hours for the oldest-open table. */
export function ageParts(hours: number): { days: number; hours: number } {
  const h = Math.max(0, Math.round(hours));
  return { days: Math.floor(h / 24), hours: h % 24 };
}

/** The default report period: the last 30 days ending today (local date strings). */
export function defaultRange(today: Date): { from: string; to: string } {
  const iso = (d: Date) => `${d.getFullYear()}-${String(d.getMonth() + 1).padStart(2, "0")}-${String(d.getDate()).padStart(2, "0")}`;
  const start = new Date(today);
  start.setDate(start.getDate() - 29);
  return { from: iso(start), to: iso(today) };
}

/** A period the API accepts: from before or on to, at most 366 days. */
export function rangeProblem(from: string, to: string): "order" | "tooLong" | "missing" | null {
  if (!/^\d{4}-\d{2}-\d{2}$/.test(from) || !/^\d{4}-\d{2}-\d{2}$/.test(to)) return "missing";
  const a = Date.parse(`${from}T00:00:00Z`);
  const b = Date.parse(`${to}T00:00:00Z`);
  if (a > b) return "order";
  if ((b - a) / 86_400_000 + 1 > 366) return "tooLong";
  return null;
}

/** Total of a list of counts (the text summary and the screen-reader table use it). */
export const sumCounts = (rows: readonly { count: number | string }[]) => rows.reduce((n, r) => n + Number(r.count), 0);
