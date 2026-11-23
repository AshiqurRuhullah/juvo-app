import { describe, expect, it } from "vitest";
import {
  MAX_BULK, anyFilter, canRequeue, failedQuery, formatMs, formatSpan, jobNames, outboxQuery, pageCheck, reasonKey, runQuery, shownStatus, statusLook, summariseResult, toggleId, togglePage,
} from "@/modules/system/logic";

const units = { d: "d", h: "h", min: "min", s: "s" };
const page = { page: 2, pageSize: 10 };

describe("status to badge", () => {
  it("never shows green for a live slot with no recorded call", () => {
    expect(shownStatus({ status: "Healthy", lastSuccessAt: null, lastFailureAt: null })).toBe("Unknown");
    expect(statusLook("Unknown").tone).toBe("neutral");
  });
  it("shows Healthy only after a recorded success", () => {
    expect(shownStatus({ status: "Healthy", lastSuccessAt: "2026-11-20T10:00:00Z", lastFailureAt: null })).toBe("Healthy");
    expect(statusLook("Healthy")).toEqual({ tone: "success", icon: "check" });
  });
  it("keeps the other statuses and gives each its own colour and icon", () => {
    expect(shownStatus({ status: "Sandbox" })).toBe("Sandbox");
    expect(shownStatus({ status: "Disabled" })).toBe("Disabled");
    expect(shownStatus({ status: "Down", lastFailureAt: "2026-11-20T10:00:00Z" })).toBe("Down");
    const looks = (["Healthy", "Degraded", "Down", "Disabled", "Sandbox", "Unknown"] as const).map((s) => statusLook(s).icon);
    expect(new Set(looks).size).toBe(6);
    expect(statusLook("Degraded").tone).toBe("warning");
    expect(statusLook("Down").tone).toBe("danger");
    expect(statusLook("Sandbox").tone).toBe("info");
  });
});

describe("requeue eligibility", () => {
  it("only dead-lettered and retrying messages", () => {
    expect(canRequeue("DeadLettered")).toBe(true);
    expect(canRequeue("Retrying")).toBe(true);
    expect(canRequeue("Pending")).toBe(false);
  });
});

describe("filters to queries", () => {
  it("leaves empty filters out", () => {
    expect(runQuery(page, {})).toEqual({ Page: 2, PageSize: 10 });
    expect(outboxQuery(page, { state: "", type: "  " })).toEqual({ Page: 2, PageSize: 10 });
    expect(failedQuery(page, {})).toEqual({ Page: 2, PageSize: 10 });
  });
  it("builds a run query with job, outcome, reference and search", () => {
    const q = runQuery({ ...page, search: " cour " }, { job: "courier.status-sync", outcome: "Failed", reference: " abc " });
    expect(q).toMatchObject({ Search: "cour", job: "courier.status-sync", outcome: "Failed", reference: "abc" });
  });
  it("turns dates into a half-open range (the day after the last day is exclusive)", () => {
    const q = runQuery(page, { from: "2026-11-01", to: "2026-11-01" });
    expect(new Date(String(q.to)).getTime() - new Date(String(q.from)).getTime()).toBeGreaterThanOrEqual(23 * 3600_000);
    expect(new Date(String(q.to)).getTime() - new Date(String(q.from)).getTime()).toBeLessThanOrEqual(25 * 3600_000);
    expect(runQuery(page, { from: "garbage" })).not.toHaveProperty("from");
  });
  it("outbox and failed-send filters", () => {
    expect(outboxQuery(page, { state: "DeadLettered", type: "order." })).toMatchObject({ state: "DeadLettered", type: "order." });
    expect(failedQuery(page, { channel: "Sms", template: "otp" })).toMatchObject({ channel: "Sms", template: "otp" });
  });
  it("knows when a filter is active", () => {
    expect(anyFilter({ a: undefined, b: "" }, "  ")).toBe(false);
    expect(anyFilter({ a: "x" }, "")).toBe(true);
    expect(anyFilter({}, "q")).toBe(true);
  });
});

describe("selection limit", () => {
  const ids = (n: number) => Array.from({ length: n }, (_, i) => `id-${i}`);
  it("toggles one id and refuses the 101st", () => {
    let sel = new Set<string>();
    for (const id of ids(MAX_BULK)) sel = toggleId(sel, id).next;
    expect(sel.size).toBe(100);
    const refused = toggleId(sel, "extra");
    expect(refused.full).toBe(true);
    expect(refused.next.size).toBe(100);
    expect(toggleId(sel, "id-3").next.size).toBe(99);
  });
  it("selects a page up to the limit and says so", () => {
    const start = new Set(ids(95));
    const r = togglePage(start, ["n1", "n2", "n3", "n4", "n5", "n6", "n7"], true);
    expect(r.next.size).toBe(100);
    expect(r.full).toBe(true);
  });
  it("selects a whole page below the limit and clears it", () => {
    const r = togglePage(new Set<string>(), ["a", "b"], true);
    expect([...r.next]).toEqual(["a", "b"]);
    expect(r.full).toBe(false);
    expect(togglePage(r.next, ["a"], false).next.has("a")).toBe(false);
  });
  it("header check state", () => {
    expect(pageCheck(new Set(), ["a", "b"])).toBe(false);
    expect(pageCheck(new Set(["a"]), ["a", "b"])).toBe("indeterminate");
    expect(pageCheck(new Set(["a", "b"]), ["a", "b"])).toBe(true);
    expect(pageCheck(new Set(["a"]), [])).toBe(false);
  });
});

describe("bulk result", () => {
  it("summarises counts and reasons (counts may arrive as text)", () => {
    const r = summariseResult({ requested: "3", requeued: 1, skipped: [{ id: "a", reason: "Not found." }, { id: "b", reason: "Not waiting or failed (already delivered)." }] });
    expect(r.requested).toBe(3);
    expect(r.requeued).toBe(1);
    expect(r.outcome).toBe("some");
    expect(r.skipped.map((s) => s.reasonKey)).toEqual(["notFound", "alreadyDelivered"]);
  });
  it("all and none", () => {
    expect(summariseResult({ requested: 2, requeued: 2, skipped: [] }).outcome).toBe("all");
    expect(summariseResult({ requested: 2, requeued: 0, skipped: [{ id: "a", reason: "x" }, { id: "b", reason: "y" }] }).outcome).toBe("none");
  });
  it("maps the known reasons and leaves unknown text to be shown as is", () => {
    expect(reasonKey("Not failed (already pending, sent or skipped).")).toBe("notFailed");
    expect(reasonKey("Something new")).toBeNull();
  });
});

describe("formatting", () => {
  it("uptime and age use the two largest units", () => {
    expect(formatSpan(42, units)).toBe("42 s");
    expect(formatSpan(125, units)).toBe("2 min");
    expect(formatSpan(7500, units)).toBe("2 h 5 min");
    expect(formatSpan("190000", units)).toBe("2 d 4 h");
    expect(formatSpan(null, units)).toBe("0 s");
  });
  it("durations", () => {
    expect(formatMs(420)).toBe("420 ms");
    expect(formatMs("1300")).toBe("1.3 s");
  });
  it("job names are distinct and sorted", () => {
    expect(jobNames([{ jobName: "b" }, { jobName: "a" }, { jobName: "b" }])).toEqual(["a", "b"]);
  });
});
