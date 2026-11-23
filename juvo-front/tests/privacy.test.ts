import { describe, expect, it } from "vitest";
import {
  canAnonymise,
  confirmMatches,
  consentChange,
  exportFallbackName,
  isAnonymisedMobile,
  isBlockingKind,
  normaliseConfirm,
  privacyErrorKind,
  privacySections,
  reasonProblem,
  toConsentRow,
} from "@/modules/customers/privacy-logic";

const ID = "0192f3a4-7c1e-7b52-9d10-3a5f6e7d8c9b";

describe("confirmation by customer id", () => {
  it("accepts the id in any case and with or without dashes, like the API", () => {
    expect(confirmMatches(ID, ID)).toBe(true);
    expect(confirmMatches(ID.toUpperCase(), ID)).toBe(true);
    expect(confirmMatches(ID.replaceAll("-", ""), ID)).toBe(true);
    expect(confirmMatches(`  ${ID}  `, ID)).toBe(true);
  });
  it("refuses a different, partial or empty id", () => {
    expect(confirmMatches(ID.slice(0, 20), ID)).toBe(false);
    expect(confirmMatches(ID.replace("9b", "9c"), ID)).toBe(false);
    expect(confirmMatches("", ID)).toBe(false);
    expect(confirmMatches("", "")).toBe(false);
  });
  it("normalises dashes, spaces and case", () => {
    expect(normaliseConfirm(" AB-cd 12 ")).toBe("abcd12");
  });
});

describe("reason", () => {
  it("needs 5 to 300 characters after trimming", () => {
    expect(reasonProblem("")).toBe("tooShort");
    expect(reasonProblem("  abcd  ")).toBe("tooShort");
    expect(reasonProblem("abcde")).toBeNull();
    expect(reasonProblem("x".repeat(300))).toBeNull();
    expect(reasonProblem("x".repeat(301))).toBe("tooLong");
  });
  it("counts characters, not UTF-16 units (Bengali and emoji)", () => {
    expect(reasonProblem("গ্রাহক")).toBeNull();
    expect(reasonProblem("😀".repeat(5))).toBeNull();
    expect(reasonProblem("😀".repeat(4))).toBe("tooShort");
  });
});

describe("canAnonymise", () => {
  const ok = { reason: "Customer asked by phone", confirm: ID, customerId: ID };
  it("needs the reason and the id together", () => {
    expect(canAnonymise(ok)).toBe(true);
    expect(canAnonymise({ ...ok, reason: "no" })).toBe(false);
    expect(canAnonymise({ ...ok, confirm: "" })).toBe(false);
  });
  it("is off while a request runs", () => {
    expect(canAnonymise({ ...ok, busy: true })).toBe(false);
  });
});

describe("anonymised customers and sections", () => {
  it("recognises the placeholder mobile", () => {
    expect(isAnonymisedMobile("ANON0123456789ABCDEF")).toBe(true);
    expect(isAnonymisedMobile("anon0123456789abcdef")).toBe(true);
    expect(isAnonymisedMobile("+8801712345678")).toBe(false);
    expect(isAnonymisedMobile(null)).toBe(false);
  });
  it("shows the history with view and the privacy card with either privacy permission", () => {
    expect(privacySections({ view: true, exportData: false, anonymise: false })).toEqual({ history: true, privacyCard: false });
    expect(privacySections({ view: true, exportData: true, anonymise: false })).toEqual({ history: true, privacyCard: true });
    expect(privacySections({ view: true, exportData: false, anonymise: true })).toEqual({ history: true, privacyCard: true });
    expect(privacySections({ view: false, exportData: false, anonymise: false })).toEqual({ history: false, privacyCard: false });
  });
});

describe("error codes", () => {
  it("groups the privacy codes", () => {
    expect(privacyErrorKind({ code: "PRIVACY.OPEN_ORDERS", status: 409 })).toBe("openWork");
    expect(privacyErrorKind({ code: "PRIVACY.STAFF_ACCOUNT", status: 409 })).toBe("staffAccount");
    expect(privacyErrorKind({ code: "PRIVACY.EXPORT_LIMIT", status: 429 })).toBe("exportLimit");
    expect(privacyErrorKind({ code: "PRIVACY.REASON_REQUIRED", status: 400 })).toBe("reason");
    expect(privacyErrorKind({ code: "PRIVACY.CONFIRM_MISMATCH", status: 400 })).toBe("confirm");
    expect(privacyErrorKind({ code: "GENERAL.NOT_FOUND", status: 404 })).toBe("other");
    expect(privacyErrorKind(null)).toBe("other");
  });
  it("keeps the dialog open for the two blocking refusals only", () => {
    expect(isBlockingKind("openWork")).toBe(true);
    expect(isBlockingKind("staffAccount")).toBe(true);
    expect(isBlockingKind("exportLimit")).toBe(false);
    expect(isBlockingKind("other")).toBe(false);
  });
});

describe("consent history rows", () => {
  const entry = { id: "1", type: "MarketingSms", channel: "Sms", appliesTo: ["Sms", "WhatsApp"], oldValue: null as boolean | null, newValue: true, sourceKind: "Customer", source: "customer-profile", ipAddress: "203.0.*.*", at: "2026-11-20T10:00:00Z" };
  it("tells first records from changes", () => {
    expect(consentChange(null, true)).toBe("firstGiven");
    expect(consentChange(null, false)).toBe("firstDeclined");
    expect(consentChange(false, true)).toBe("given");
    expect(consentChange(true, false)).toBe("withdrawn");
  });
  it("shows the IP exactly as the API sends it and the WhatsApp coverage", () => {
    const row = toConsentRow(entry);
    expect(row).toMatchObject({ channel: "Sms", coversWhatsApp: true, change: "firstGiven", sourceKind: "Customer", ip: "203.0.*.*", source: "customer-profile" });
  });
  it("has no IP when the API sends none or blank, and no WhatsApp for e-mail", () => {
    expect(toConsentRow({ ...entry, ipAddress: null }).ip).toBeNull();
    expect(toConsentRow({ ...entry, ipAddress: "  " }).ip).toBeNull();
    expect(toConsentRow({ ...entry, channel: "Email", appliesTo: ["Email"], oldValue: true, newValue: false }).coversWhatsApp).toBe(false);
  });
  it("falls back for unknown channel and source kinds", () => {
    const row = toConsentRow({ ...entry, channel: "Pigeon", sourceKind: "Telepathy" });
    expect(row.channel).toBe("unknown");
    expect(row.sourceKind).toBe("Other");
  });
});

describe("export file name", () => {
  it("falls back to the customer id", () => {
    expect(exportFallbackName(ID)).toBe(`customer-data-${ID}.json`);
  });
});
