import { describe, expect, it } from "vitest";
import { allowedActions, consentState, initials, isKnownConsent, isMasked, levelTone, listIsMasked, statusTone, toCustomerQuery } from "@/modules/customers/logic";

describe("toCustomerQuery", () => {
  it("sends paging only when nothing is chosen", () => {
    expect(toCustomerQuery({}, { page: 1, pageSize: 10 })).toEqual({ Page: 1, PageSize: 10 });
  });
  it("maps the status filter to isActive", () => {
    expect(toCustomerQuery({ status: "active" }, { page: 2, pageSize: 10 })).toEqual({ Page: 2, PageSize: 10, isActive: true });
    expect(toCustomerQuery({ status: "inactive" }, { page: 1, pageSize: 10 }).isActive).toBe(false);
  });
  it("passes a known level and drops an unknown one", () => {
    expect(toCustomerQuery({ level: "Quick" }, { page: 1, pageSize: 10 }).level).toBe("Quick");
    expect(toCustomerQuery({ level: "Vip" }, { page: 1, pageSize: 10 }).level).toBeUndefined();
  });
  it("trims the search and leaves out an empty one", () => {
    expect(toCustomerQuery({}, { page: 1, pageSize: 10, search: "  Rina " }).Search).toBe("Rina");
    expect(toCustomerQuery({}, { page: 1, pageSize: 10, search: "   " }).Search).toBeUndefined();
  });
});

describe("masked values", () => {
  it("detects the API's masks", () => {
    expect(isMasked("+88017****45")).toBe(true);
    expect(isMasked("a***@example.com")).toBe(true);
    expect(isMasked("+8801712345645")).toBe(false);
    expect(isMasked(null)).toBe(false);
    expect(isMasked("")).toBe(false);
  });
  it("says a list is masked when any row is", () => {
    expect(listIsMasked([{ mobile: "+88017****45", email: null }])).toBe(true);
    expect(listIsMasked([{ mobile: "+8801712345645", email: "a***@x.com" }])).toBe(true);
    expect(listIsMasked([{ mobile: "+8801712345645", email: "a@x.com" }])).toBe(false);
    expect(listIsMasked([])).toBe(false);
  });
});

describe("status and level", () => {
  it("uses a danger tone for a deactivated customer", () => {
    expect(statusTone(true)).toBe("success");
    expect(statusTone(false)).toBe("danger");
  });
  it("gives each profile level a tone", () => {
    expect(levelTone("Full")).toBe("success");
    expect(levelTone("Quick")).toBe("info");
    expect(levelTone("Guest")).toBe("outline");
  });
});

describe("allowedActions", () => {
  it("offers nothing without the block permission", () => {
    expect(allowedActions(true, { block: false })).toEqual([]);
    expect(allowedActions(false, { block: false })).toEqual([]);
  });
  it("offers the opposite of the current state", () => {
    expect(allowedActions(true, { block: true })).toEqual(["deactivate"]);
    expect(allowedActions(false, { block: true })).toEqual(["activate"]);
  });
});

describe("consent", () => {
  it("tells given, withdrawn and never asked apart", () => {
    expect(consentState({ granted: true, at: "2026-01-01T00:00:00Z" })).toBe("granted");
    expect(consentState({ granted: false, at: "2026-01-01T00:00:00Z" })).toBe("withdrawn");
    expect(consentState({ granted: false, at: null })).toBe("never");
  });
  it("knows the three consent types", () => {
    expect(isKnownConsent("MarketingSms")).toBe(true);
    expect(isKnownConsent("Other")).toBe(false);
  });
});

describe("initials", () => {
  it("uses two letters, or a question mark for a guest without a name", () => {
    expect(initials("Rina Akter Khan")).toBe("RA");
    expect(initials("rina")).toBe("R");
    expect(initials(null)).toBe("?");
    expect(initials("  ")).toBe("?");
  });
});
