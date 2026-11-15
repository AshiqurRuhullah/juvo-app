import { describe, expect, it } from "vitest";
import { QUIET_ZONE, START_B, START_C, STOP, canEncode128, checkValue, code128Bars, code128Values, code128Widths, patternOf } from "@/modules/couriers/code128";
import {
  courierToForm,
  emptyCourierForm,
  formToRequest,
  isValidCourierCode,
  isValidTrackingTemplate,
  labelsHref,
  looksLikeSecret,
  parseIds,
  rawForStatus,
  rowsToSettings,
  validateCourierForm,
} from "@/modules/couriers/admin-logic";
import type { Courier } from "@/modules/couriers/api";

describe("Code 128 encoder", () => {
  it("has a well-formed symbol table: every symbol is 11 modules (stop 13) and no two are alike", () => {
    const seen = new Set<string>();
    for (let v = 0; v <= 106; v++) {
      const p = patternOf(v);
      expect(p).toHaveLength(v === STOP ? 7 : 6);
      expect([...p].reduce((s, d) => s + Number(d), 0)).toBe(v === STOP ? 13 : 11);
      expect(seen.has(p)).toBe(false);
      seen.add(p);
    }
    // Known anchors of the standard table.
    expect(patternOf(0)).toBe("212222"); // space
    expect(patternOf(START_B)).toBe("211214");
    expect(patternOf(START_C)).toBe("211232");
    expect(patternOf(STOP)).toBe("2331112");
  });

  it("calculates the check symbol as (start + sum of position x value) mod 103", () => {
    // "Hi" in set B: H = 72-32 = 40, i = 105-32 = 73: 104 + 40*1 + 73*2 = 290; 290 mod 103 = 84.
    expect(checkValue(START_B, [40, 73])).toBe(84);
    // Digits "1234" in set C: pairs 12 and 34: 105 + 12*1 + 34*2 = 185; 185 mod 103 = 82.
    expect(checkValue(START_C, [12, 34])).toBe(82);
    // Wikipedia's "PJJ123C" in set B: 104 + 48 + 42*2 + 42*3 + 17*4 + 18*5 + 19*6 + 35*7 = 879; 879 mod 103 = 55.
    expect(checkValue(START_B, [48, 42, 42, 17, 18, 19, 35])).toBe(55);
  });

  it("uses set B for text and set C for long even digit strings", () => {
    expect(code128Values("Hi")).toEqual([START_B, 40, 73, 84, STOP]);
    expect(code128Values("1234")).toEqual([START_C, 12, 34, 82, STOP]);
    // An odd length or a short number stays in set B.
    expect(code128Values("123")[0]).toBe(START_B);
    expect(code128Values("12")[0]).toBe(START_B);
    expect(code128Values("SBX-1A2B3C4D01")[0]).toBe(START_B);
  });

  it("draws 11 modules per symbol plus 13 for the stop, between two quiet zones of 10 modules", () => {
    const text = "SBX-1A2B3C4D01";
    const symbols = code128Values(text).length; // includes the stop
    const widths = code128Widths(text);
    const modules = widths.reduce((a, b) => a + b, 0);
    expect(modules).toBe((symbols - 1) * 11 + 13);
    const { bars, total } = code128Bars(text);
    expect(total).toBe(modules + 2 * QUIET_ZONE);
    expect(bars[0]!.x).toBe(QUIET_ZONE); // starts after the quiet zone
    const last = bars[bars.length - 1]!;
    expect(last.x + last.width).toBe(total - QUIET_ZONE); // the stop ends with a bar, then the quiet zone
    // Bars never overlap and widths are 1 to 4 modules.
    for (let i = 1; i < bars.length; i++) expect(bars[i]!.x).toBeGreaterThan(bars[i - 1]!.x + bars[i - 1]!.width - 1);
    for (const b of bars) {
      expect(b.width).toBeGreaterThanOrEqual(1);
      expect(b.width).toBeLessThanOrEqual(4);
    }
  });

  it("starts with the start B pattern and ends with the stop pattern", () => {
    const widths = code128Widths("A").join("");
    expect(widths.startsWith("211214")).toBe(true);
    expect(widths.endsWith("2331112")).toBe(true);
  });

  it("refuses text it cannot draw", () => {
    expect(canEncode128("SBX-1A2B")).toBe(true);
    expect(canEncode128("")).toBe(false);
    expect(canEncode128("বাংলা")).toBe(false);
    expect(canEncode128("x".repeat(81))).toBe(false);
    expect(() => code128Values("tab\there")).toThrow();
  });
});

describe("label ids", () => {
  const a = "0192f6a2-1b3c-7d4e-8f50-123456789abc";
  const b = "0192f6a2-1b3c-7d4e-8f50-123456789abd";

  it("keeps only well-formed ids, once, in order", () => {
    expect(parseIds(`${a}, nope ,${b},${a}`)).toEqual([a, b]);
    expect(parseIds(undefined)).toEqual([]);
    expect(parseIds([a, b])).toEqual([a, b]);
  });

  it("stops at the limit", () => {
    const many = Array.from({ length: 80 }, (_, i) => `0192f6a2-1b3c-7d4e-8f50-${String(i).padStart(12, "0")}`);
    expect(parseIds(many.join(","))).toHaveLength(50);
    expect(parseIds(many.join(","), 5)).toHaveLength(5);
  });

  it("builds the bulk link with at most 50 ids", () => {
    expect(labelsHref([a, b])).toBe(`/admin/shipments/labels?ids=${a},${b}`);
    expect(labelsHref(Array.from({ length: 60 }, () => a)).split(",")).toHaveLength(50);
  });
});

describe("courier form", () => {
  const base = () => ({ ...emptyCourierForm("SANDBOX"), code: "SB2", name: "Second sandbox" });

  it("accepts a normal new courier and builds the request", () => {
    const v = base();
    expect(validateCourierForm(v, true)).toEqual({});
    const body = formToRequest({ ...v, minWeightGrams: "100", chargeOverride: "80.50", trackingUrlTemplate: "https://t.example/{consignment}", settings: [{ key: " store ", value: " 12 " }, { key: "", value: "x" }] });
    expect(body).toMatchObject({ name: "Second sandbox", priority: 100, minWeightGrams: 100, maxWeightGrams: null, chargeOverride: 80.5, baseRateUpToGrams: 1000, settings: { store: "12" } });
    expect(body.trackingUrlTemplate).toBe("https://t.example/{consignment}");
    expect(body.notes).toBeNull();
  });

  it("checks the code only on create", () => {
    expect(isValidCourierCode("PATHAO_2")).toBe(true);
    for (const bad of ["a", "ab", "has space", "x".repeat(31), "low"]) expect(isValidCourierCode(bad)).toBe(false);
    expect(validateCourierForm({ ...base(), code: "x" }, true).code).toBe("code");
    expect(validateCourierForm({ ...base(), code: "" }, false).code).toBeUndefined();
  });

  it("flags priority, weights, money and the tracking link", () => {
    const e = validateCourierForm({ ...base(), priority: "0", minWeightGrams: "900", maxWeightGrams: "100", baseRate: "-1", perExtraKgRate: "1.234", baseRateUpToGrams: "0", trackingUrlTemplate: "http://x/{consignment}" }, true);
    expect(e).toMatchObject({ priority: "priority", maxWeightGrams: "weightRange", baseRate: "money", perExtraKgRate: "money", baseRateUpToGrams: "weight", trackingUrlTemplate: "tracking" });
    expect(validateCourierForm({ ...base(), priority: "1001" }, true).priority).toBe("priority");
  });

  it("tracking templates need https and the placeholder, or stay empty", () => {
    expect(isValidTrackingTemplate("")).toBe(true);
    expect(isValidTrackingTemplate("https://x.example/t/{consignment}")).toBe(true);
    expect(isValidTrackingTemplate("https://x.example/t")).toBe(false);
    expect(isValidTrackingTemplate("http://x.example/{consignment}")).toBe(false);
  });

  it("refuses setting names that look like secrets, and duplicates", () => {
    for (const key of ["api_key", "apiKey", "Client-Secret", "password", "AccessToken", "private key", "credentials"]) expect(looksLikeSecret(key)).toBe(true);
    for (const key of ["store_id", "area_code", "merchant"]) expect(looksLikeSecret(key)).toBe(false);
    expect(validateCourierForm({ ...base(), settings: [{ key: "api_key", value: "x" }] }, true).settings).toBe("secret");
    expect(validateCourierForm({ ...base(), settings: [{ key: "a", value: "1" }, { key: "A", value: "2" }] }, true).settings).toBe("duplicate");
    expect(rowsToSettings([{ key: "a", value: "1" }, { key: " ", value: "2" }])).toEqual({ a: "1" });
  });

  it("round-trips an existing courier through the form", () => {
    const courier = {
      id: "x", code: "SANDBOX", name: "Sandbox courier", provider: "SANDBOX", providerName: "Sandbox courier", providerConfigured: true, isEnabled: true, priority: 100,
      codSupported: true, supportsInternational: true, minWeightGrams: null, maxWeightGrams: "5000", supportedZoneCodes: ["DHAKA"], chargeOverride: null, baseRate: 60, perExtraKgRate: 20,
      baseRateUpToGrams: 1000, trackingUrlTemplate: null, notes: null, settings: { store: "1" }, isSimulator: true, openShipments: 0, createdAt: "2026-11-01T00:00:00Z",
    } as unknown as Courier;
    const form = courierToForm(courier);
    expect(validateCourierForm(form, false)).toEqual({});
    expect(formToRequest(form)).toMatchObject({ maxWeightGrams: 5000, minWeightGrams: null, supportedZoneCodes: ["DHAKA"], baseRate: 60, settings: { store: "1" } });
  });
});

describe("sandbox advance", () => {
  it("maps next statuses to the simulator's words", () => {
    expect(rawForStatus("PickedUp")).toBe("picked_up");
    expect(rawForStatus("OutForDelivery")).toBe("out_for_delivery");
    expect(rawForStatus("Cancelled")).toBeNull();
  });
});
