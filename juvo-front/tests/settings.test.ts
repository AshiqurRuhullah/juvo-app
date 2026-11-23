import { describe, expect, it } from "vitest";
import {
  activeGroup, boolOfText, canReset, countSettings, dangerKind, draftOf, editBlock, filterGroups, historyKind, isDirty, matchesQuery, parseDraft, paymentMethodOf,
  sourceKind, titleOf, valueToDraft, type SettingLike,
} from "@/modules/settings/logic";

const base: SettingLike = {
  key: "X.Y", group: "orders", type: "text", value: "", default: "", source: "default", isSet: false, descriptionEn: "English text", descriptionBn: "বাংলা লেখা",
  allowedValues: null, min: null, max: null, maxLength: null, requiresRestart: false, sensitive: false, readOnly: false, canEdit: true, editPermission: "settings.system.edit", version: null,
};
const s = (patch: Partial<SettingLike>): SettingLike => ({ ...base, ...patch });

describe("value to draft", () => {
  it("writes booleans, numbers and nothing", () => {
    expect(valueToDraft(true)).toBe("true");
    expect(valueToDraft(false)).toBe("false");
    expect(valueToDraft(12.5)).toBe("12.5");
    expect(valueToDraft(null)).toBe("");
    expect(valueToDraft("Packing")).toBe("Packing");
    expect(draftOf(s({ value: 3 }))).toBe("3");
  });
});

describe("parseDraft", () => {
  it("booleans", () => {
    expect(parseDraft(s({ type: "bool" }), "true")).toEqual({ ok: true, value: true });
    expect(parseDraft(s({ type: "bool" }), "false")).toEqual({ ok: true, value: false });
  });

  it("whole numbers with a range", () => {
    const w = s({ type: "whole", min: 1, max: 30 });
    expect(parseDraft(w, "7")).toEqual({ ok: true, value: 7 });
    expect(parseDraft(w, " 30 ")).toEqual({ ok: true, value: 30 });
    expect(parseDraft(w, "0")).toEqual({ ok: false, error: "belowMin" });
    expect(parseDraft(w, "31")).toEqual({ ok: false, error: "aboveMax" });
    expect(parseDraft(w, "")).toEqual({ ok: false, error: "required" });
    expect(parseDraft(w, "2.5")).toEqual({ ok: false, error: "notWhole" });
    expect(parseDraft(w, "abc")).toEqual({ ok: false, error: "notNumber" });
    expect(parseDraft(w, "1e3")).toEqual({ ok: false, error: "notNumber" });
  });

  it("range limits arrive as strings too", () => {
    expect(parseDraft(s({ type: "whole", min: "0", max: "100" }), "101")).toEqual({ ok: false, error: "aboveMax" });
  });

  it("decimals have at most four places", () => {
    const n = s({ type: "number", min: 0, max: 100 });
    expect(parseDraft(n, "12.5")).toEqual({ ok: true, value: 12.5 });
    expect(parseDraft(n, "12.1234")).toEqual({ ok: true, value: 12.1234 });
    expect(parseDraft(n, "12.12345")).toEqual({ ok: false, error: "tooManyDecimals" });
    expect(parseDraft(n, "-1")).toEqual({ ok: false, error: "belowMin" });
    expect(parseDraft(n, "1,5")).toEqual({ ok: false, error: "notNumber" });
  });

  it("choices match without case and keep the canonical spelling", () => {
    const c = s({ type: "choice", allowedValues: ["Packing", "Delivery"] });
    expect(parseDraft(c, "delivery")).toEqual({ ok: true, value: "Delivery" });
    expect(parseDraft(c, "Shipping")).toEqual({ ok: false, error: "notAllowed" });
    expect(parseDraft(c, "")).toEqual({ ok: false, error: "required" });
  });

  it("text keeps spaces and may be empty, but not longer than the limit", () => {
    const t = s({ type: "text", maxLength: 5 });
    expect(parseDraft(t, "")).toEqual({ ok: true, value: "" });
    expect(parseDraft(t, "ab cd")).toEqual({ ok: true, value: "ab cd" });
    expect(parseDraft(t, "abcdef")).toEqual({ ok: false, error: "tooLong" });
    expect(parseDraft(s({ type: "text" }), "x".repeat(3000))).toEqual({ ok: true, value: "x".repeat(3000) });
  });

  it("an unknown type is treated as text", () => {
    expect(parseDraft(s({ type: "weird" }), "hello")).toEqual({ ok: true, value: "hello" });
  });
});

describe("isDirty", () => {
  it("no draft is clean", () => {
    expect(isDirty(s({ type: "whole", value: 5 }), undefined)).toBe(false);
  });
  it("same value typed another way is clean", () => {
    expect(isDirty(s({ type: "whole", value: 5 }), "5")).toBe(false);
    expect(isDirty(s({ type: "number", value: 5 }), "5.0")).toBe(false);
    expect(isDirty(s({ type: "bool", value: true }), "true")).toBe(false);
    expect(isDirty(s({ type: "choice", value: "Packing", allowedValues: ["Packing", "Delivery"] }), "packing")).toBe(false);
  });
  it("a different value is dirty", () => {
    expect(isDirty(s({ type: "whole", value: 5 }), "6")).toBe(true);
    expect(isDirty(s({ type: "bool", value: true }), "false")).toBe(true);
    expect(isDirty(s({ type: "text", value: "a" }), "a ")).toBe(true);
    expect(isDirty(s({ type: "text", value: null }), "")).toBe(false);
  });
  it("an invalid draft is dirty so Save can say what is wrong", () => {
    expect(isDirty(s({ type: "whole", value: 5, min: 1 }), "0")).toBe(true);
    expect(isDirty(s({ type: "whole", value: 5 }), "")).toBe(true);
  });
});

describe("who may do what", () => {
  it("read-only entries explain themselves", () => {
    expect(editBlock(s({ readOnly: true, source: "appsettings", canEdit: false }))).toBe("readOnlyConfig");
    expect(editBlock(s({ readOnly: true, source: "default", canEdit: true }))).toBe("readOnlyFixed");
  });
  it("without the edit permission", () => {
    expect(editBlock(s({ canEdit: false }))).toBe("noPermission");
    expect(editBlock(s({ canEdit: true }))).toBeNull();
  });
  it("reset needs a stored row, a writable setting and the right", () => {
    expect(canReset(s({ isSet: true }))).toBe(true);
    expect(canReset(s({ isSet: false }))).toBe(false);
    expect(canReset(s({ isSet: true, readOnly: true }))).toBe(false);
    expect(canReset(s({ isSet: true, canEdit: false }))).toBe(false);
  });
  it("source kinds", () => {
    expect(sourceKind("db")).toBe("changed");
    expect(sourceKind("default")).toBe("default");
    expect(sourceKind("appsettings")).toBe("config");
  });
});

describe("dangerous switches", () => {
  it("rewards, payments, auto confirm and OTP mode", () => {
    expect(dangerKind("Loyalty.Enabled", true)).toBe("loyaltyOn");
    expect(dangerKind("Loyalty.Enabled", false)).toBe("loyaltyOff");
    expect(dangerKind("Payments.BKASH.Enabled", true)).toBe("paymentOn");
    expect(dangerKind("Payments.COD.Enabled", false)).toBe("paymentOff");
    expect(dangerKind("Payments.AutoConfirmPaidOrders", true)).toBe("autoConfirm");
    expect(dangerKind("Checkout.OtpMode", "Always")).toBe("otpMode");
  });
  it("other settings are not dangerous", () => {
    expect(dangerKind("Payments.Cod.MaxAmount", 5000)).toBeNull();
    expect(dangerKind("Loyalty.PointsPerTaka", 1)).toBeNull();
    expect(dangerKind("Returns.WindowDays", 7)).toBeNull();
  });
  it("names the payment method", () => {
    expect(paymentMethodOf("Payments.NAGAD.Enabled")).toBe("NAGAD");
    expect(paymentMethodOf("Other.Key")).toBe("Other.Key");
  });
});

describe("titles, search and groups", () => {
  it("title follows the language and falls back", () => {
    expect(titleOf(s({}), "en")).toBe("English text");
    expect(titleOf(s({}), "bn")).toBe("বাংলা লেখা");
    expect(titleOf(s({ descriptionBn: " " }), "bn")).toBe("English text");
    expect(titleOf(s({ descriptionBn: "", descriptionEn: "" }), "en")).toBe("X.Y");
  });

  it("search needs every word, in the key or either description", () => {
    const one = s({ key: "Returns.WindowDays", descriptionEn: "Days a customer may return an item" });
    expect(matchesQuery(one, "")).toBe(true);
    expect(matchesQuery(one, "window")).toBe(true);
    expect(matchesQuery(one, "RETURN days")).toBe(true);
    expect(matchesQuery(one, "return refund")).toBe(false);
    expect(matchesQuery(s({ descriptionBn: "ফেরতের দিন" }), "ফেরত")).toBe(true);
  });

  const groups = [
    { group: "orders", settings: [s({ key: "A", descriptionEn: "alpha" }), s({ key: "B", descriptionEn: "beta" })] },
    { group: "returns", settings: [s({ key: "C", descriptionEn: "gamma" })] },
  ];
  it("drops groups with no match and keeps order", () => {
    expect(filterGroups(groups, "")).toHaveLength(2);
    const f = filterGroups(groups, "gam");
    expect(f.map((g) => g.group)).toEqual(["returns"]);
    expect(countSettings(f)).toBe(1);
    expect(filterGroups(groups, "zzz")).toEqual([]);
    expect(countSettings(groups)).toBe(3);
  });
  it("the active group falls back to the first", () => {
    expect(activeGroup(groups, "returns")).toBe("returns");
    expect(activeGroup(groups, "gone")).toBe("orders");
    expect(activeGroup(groups, null)).toBe("orders");
    expect(activeGroup([], "orders")).toBeNull();
  });
});

describe("history", () => {
  it("first value, change and reset", () => {
    expect(historyKind({ action: "set", oldValue: null, newValue: "5" })).toBe("first");
    expect(historyKind({ action: "set", oldValue: "5", newValue: "6" })).toBe("changed");
    expect(historyKind({ action: "reset", oldValue: "6", newValue: null })).toBe("reset");
  });
  it("booleans stored as text", () => {
    expect(boolOfText("True")).toBe(true);
    expect(boolOfText("false")).toBe(false);
    expect(boolOfText("7")).toBeNull();
    expect(boolOfText(null)).toBeNull();
  });
});
