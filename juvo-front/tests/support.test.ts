import { describe, expect, it } from "vitest";
import {
  ATTACHMENT_MAX_BYTES,
  allowedActions,
  ageParts,
  asciiDigits,
  attachmentProblem,
  cannedProblem,
  canPublicReply,
  defaultRange,
  durationParts,
  groupByDay,
  needsConfirmation,
  priorityTone,
  rangeProblem,
  renderPreview,
  replyStatusChoices,
  statusTone,
  sumCounts,
  threadCounts,
  threadView,
  toTicketQuery,
  tooManyFiles,
  unknownPlaceholders,
  validMobile,
  validateContact,
  validateMyTicket,
  validateStaffTicket,
  type ThreadMessage,
} from "@/modules/support/logic";

const all = { reply: true, close: true, assign: true };

describe("allowed actions", () => {
  it("offers exactly the moves the API lists, for a person who may do everything", () => {
    const actions = allowedActions({ status: "Open", nextStatuses: ["InProgress", "Waiting", "Resolved", "Closed"], canReopen: false }, all);
    expect(actions.map((a) => a.action)).toEqual(["inProgress", "waiting", "resolve", "close"]);
    expect(actions.every((a) => a.allowed)).toBe(true);
  });

  it("never offers a move the state machine does not list", () => {
    const actions = allowedActions({ status: "Resolved", nextStatuses: ["Closed", "Open"], canReopen: true }, all);
    expect(actions.map((a) => a.action)).toEqual(["close", "reopen"]);
  });

  it("marks closing and reopening as not allowed without the close right, and the others without the reply right", () => {
    const noClose = allowedActions({ status: "Open", nextStatuses: ["InProgress", "Resolved", "Closed"], canReopen: false }, { reply: true, close: false, assign: false });
    expect(noClose.find((a) => a.action === "close")).toMatchObject({ allowed: false, reason: "permission" });
    expect(noClose.find((a) => a.action === "resolve")?.allowed).toBe(true);
    const noReply = allowedActions({ status: "Open", nextStatuses: ["InProgress", "Resolved", "Closed"], canReopen: false }, { reply: false, close: true, assign: false });
    expect(noReply.find((a) => a.action === "resolve")).toMatchObject({ allowed: false, reason: "permission" });
    expect(noReply.find((a) => a.action === "close")?.allowed).toBe(true);
  });

  it("explains a reopening the window forbids instead of hiding it", () => {
    const actions = allowedActions({ status: "Closed", nextStatuses: ["Open"], canReopen: false }, all);
    expect(actions).toEqual([{ action: "reopen", target: "Open", allowed: false, reason: "window" }]);
  });

  it("does not call Open from an active ticket a reopening", () => {
    const actions = allowedActions({ status: "Waiting", nextStatuses: ["InProgress", "Resolved", "Closed", "Open"], canReopen: false }, all);
    expect(actions.some((a) => a.action === "reopen")).toBe(false);
  });

  it("asks before ending the conversation", () => {
    expect(needsConfirmation("resolve")).toBe(true);
    expect(needsConfirmation("close")).toBe(true);
    expect(needsConfirmation("waiting")).toBe(false);
  });

  it("offers reply statuses by the same rules (closed only with the close right)", () => {
    const ticket = { status: "InProgress" as const, nextStatuses: ["Waiting", "Resolved", "Closed", "Open"] };
    expect(replyStatusChoices(ticket, all)).toEqual(["Waiting", "Resolved", "Closed"]);
    expect(replyStatusChoices(ticket, { reply: true, close: false, assign: false })).toEqual(["Waiting", "Resolved"]);
  });

  it("allows a public reply everywhere except on a closed ticket", () => {
    expect(canPublicReply("Open")).toBe(true);
    expect(canPublicReply("Resolved")).toBe(true);
    expect(canPublicReply("Closed")).toBe(false);
  });
});

describe("tones", () => {
  it("gives every status and priority a tone, and urgent is danger", () => {
    expect(statusTone("Open")).toBe("warning");
    expect(statusTone("Resolved")).toBe("success");
    expect(priorityTone("Urgent")).toBe("danger");
    expect(priorityTone("Normal")).toBe("neutral");
  });
});

const msg = (id: string, kind: ThreadMessage["kind"], at: string): ThreadMessage => ({ id, kind, text: id, authorName: null, at, attachments: [] });

describe("thread", () => {
  it("maps message kinds to views, so internal notes are never drawn like replies", () => {
    expect(threadView("Internal")).toBe("internal");
    expect(threadView("Staff")).toBe("staff");
    expect(threadView("Customer")).toBe("customer");
    expect(threadView("System")).toBe("system");
  });

  it("groups messages by day in order", () => {
    const groups = groupByDay([msg("a", "Customer", "2026-11-01T08:00:00Z"), msg("b", "Staff", "2026-11-01T09:00:00Z"), msg("c", "Internal", "2026-11-03T09:00:00Z")]);
    expect(groups.map((g) => [g.day, g.messages.map((m) => m.id)])).toEqual([
      ["2026-11-01", ["a", "b"]],
      ["2026-11-03", ["c"]],
    ]);
  });

  it("counts customer-visible and internal messages (system lines are neither)", () => {
    expect(threadCounts([{ kind: "Customer" }, { kind: "Staff" }, { kind: "Internal" }, { kind: "Internal" }, { kind: "System" }])).toEqual({ public: 2, internal: 2 });
  });
});

describe("canned reply placeholders", () => {
  it("fills the three known placeholders", () => {
    const out = renderPreview("Hello {customerName}, order {orderNumber}, ticket {ticketNumber}.", { customerName: "Nadia", orderNumber: "JV-1", ticketNumber: "TK-1" });
    expect(out).toEqual({ text: "Hello Nadia, order JV-1, ticket TK-1.", empty: [] });
  });

  it("leaves a placeholder visible when its value is unknown and reports it once", () => {
    const out = renderPreview("{orderNumber} and {orderNumber}", { orderNumber: null });
    expect(out.text).toBe("{orderNumber} and {orderNumber}");
    expect(out.empty).toEqual(["orderNumber"]);
  });

  it("finds placeholders the API would refuse", () => {
    expect(unknownPlaceholders("Hi {customerName} {foo} {bar} {foo}")).toEqual(["foo", "bar"]);
    expect(unknownPlaceholders("plain text")).toEqual([]);
  });

  it("validates a canned reply form", () => {
    const ok = { title: "Late parcel", body: "Hello {customerName}", language: "en", category: "", isActive: true };
    expect(cannedProblem(ok)).toBeNull();
    expect(cannedProblem({ ...ok, title: " " })).toEqual({ field: "title", error: "title" });
    expect(cannedProblem({ ...ok, body: "" })).toEqual({ field: "body", error: "body" });
    expect(cannedProblem({ ...ok, body: "Hi {nope}" })).toEqual({ field: "body", error: "placeholder" });
    expect(cannedProblem({ ...ok, body: "x".repeat(4001) })).toEqual({ field: "body", error: "tooLong" });
  });
});

describe("forms", () => {
  it("accepts Bangladesh mobile numbers in common spellings, including Bengali digits", () => {
    for (const ok of ["01712345678", "+8801712345678", "8801712345678", "০১৭১২৩৪৫৬৭৮", "01712-345678", "017 1234 5678"]) expect(validMobile(ok), ok).toBe(true);
    for (const bad of ["", "0171234567", "02712345678", "abc", "+8800712345678"]) expect(validMobile(bad), bad).toBe(false);
    expect(asciiDigits("০১২৩")).toBe("0123");
  });

  it("validates the guest contact form", () => {
    const ok = { name: "Nadia", mobile: "01712345678", email: "", subject: "Late", message: "Where is it", category: "delivery", orderNumber: "" };
    expect(validateContact(ok)).toEqual({});
    expect(validateContact({ ...ok, name: "", mobile: "12", email: "nope", subject: " ", message: "" })).toEqual({ name: "required", mobile: "mobile", email: "email", subject: "required", message: "required" });
    expect(validateContact({ ...ok, subject: "s".repeat(201), message: "m".repeat(4001) })).toEqual({ subject: "tooLong", message: "tooLong" });
  });

  it("validates the customer and staff forms", () => {
    expect(validateMyTicket({ subject: "", message: "", category: "other", orderNumber: "" })).toEqual({ subject: "required", message: "required" });
    expect(validateMyTicket({ subject: "a", message: "b", category: "other", orderNumber: "" })).toEqual({});
    const staff = { mobile: "01712345678", customerName: "", email: "", subject: "Call", message: "Wants to swap", channel: "Phone", priority: "Normal", category: "return", orderNumber: "" };
    expect(validateStaffTicket(staff)).toEqual({});
    expect(validateStaffTicket({ ...staff, mobile: "" }).mobile).toBe("required");
    expect(validateStaffTicket({ ...staff, mobile: "123" }).mobile).toBe("mobile");
  });
});

describe("attachments", () => {
  it("accepts pictures and PDF up to 8 MB and refuses the rest", () => {
    expect(attachmentProblem({ type: "image/png", size: 1000 })).toBeNull();
    expect(attachmentProblem({ type: "application/pdf", size: ATTACHMENT_MAX_BYTES })).toBeNull();
    expect(attachmentProblem({ type: "application/pdf", size: ATTACHMENT_MAX_BYTES + 1 })).toBe("size");
    expect(attachmentProblem({ type: "text/html", size: 10 })).toBe("type");
    expect(attachmentProblem({ type: "image/gif", size: 10 })).toBe("type");
  });

  it("stops at five files per message", () => {
    expect(tooManyFiles(4, 1)).toBe(false);
    expect(tooManyFiles(4, 2)).toBe(true);
  });
});

describe("list filters to query", () => {
  const base = { page: 1, pageSize: 20 };

  it("drops empty values and keeps paging", () => {
    expect(toTicketQuery({}, base)).toEqual({ Page: 1, PageSize: 20 });
    expect(toTicketQuery({ status: "", priority: "  " }, { ...base, search: "" })).toEqual({ Page: 1, PageSize: 20 });
  });

  it("maps every filter to its API name", () => {
    const q = toTicketQuery({ status: "Open", priority: "Urgent", channel: "Phone", category: "return", branchId: "b1", orderNumber: "JV-1", from: "2026-11-01", to: "2026-11-15", assigneeId: "u1" }, { ...base, search: " nadia ", sort: "-activity" });
    expect(q).toEqual({ Page: 1, PageSize: 20, Search: "nadia", Sort: "-activity", status: "Open", priority: "Urgent", channel: "Phone", category: "return", branchId: "b1", orderNumber: "JV-1", from: "2026-11-01", to: "2026-11-15", assigneeId: "u1" });
  });

  it("turns the chips and the unassigned choice into the API flags", () => {
    expect(toTicketQuery({ chip: "active" }, base).active).toBe(true);
    expect(toTicketQuery({ chip: "overdue" }, base).overdue).toBe(true);
    expect(toTicketQuery({ chip: "unassigned" }, base).unassigned).toBe(true);
    expect(toTicketQuery({ assigneeId: "__none" }, base)).toMatchObject({ unassigned: true });
    expect(toTicketQuery({ assigneeId: "__none" }, base).assigneeId).toBeUndefined();
    expect(toTicketQuery({ chip: "mine" }, { ...base, me: "me-id" }).assigneeId).toBe("me-id");
    expect(toTicketQuery({ chip: "mine" }, base).assigneeId).toBeUndefined();
  });
});

describe("report helpers", () => {
  it("splits minutes into days, hours and minutes, and nothing into null", () => {
    expect(durationParts(null)).toBeNull();
    expect(durationParts(undefined)).toBeNull();
    expect(durationParts(125)).toEqual({ days: 0, hours: 2, minutes: 5 });
    expect(durationParts(1440 + 61)).toEqual({ days: 1, hours: 1, minutes: 1 });
    expect(durationParts(0)).toEqual({ days: 0, hours: 0, minutes: 0 });
  });

  it("splits ages into days and hours", () => {
    expect(ageParts(98.5)).toEqual({ days: 4, hours: 3 });
    expect(ageParts(5)).toEqual({ days: 0, hours: 5 });
  });

  it("starts the default period 29 days before today (30 days including today)", () => {
    expect(defaultRange(new Date(2026, 10, 30))).toEqual({ from: "2026-11-01", to: "2026-11-30" });
    expect(defaultRange(new Date(2026, 0, 5))).toEqual({ from: "2025-12-07", to: "2026-01-05" });
  });

  it("accepts only periods the API accepts", () => {
    expect(rangeProblem("2026-11-01", "2026-11-15")).toBeNull();
    expect(rangeProblem("2026-11-15", "2026-11-01")).toBe("order");
    expect(rangeProblem("", "2026-11-01")).toBe("missing");
    expect(rangeProblem("2025-01-01", "2025-12-31")).toBeNull();
    expect(rangeProblem("2025-01-01", "2026-01-02")).toBe("tooLong");
  });

  it("adds up counts that may arrive as strings", () => {
    expect(sumCounts([{ count: 3 }, { count: "4" }])).toBe(7);
  });
});
