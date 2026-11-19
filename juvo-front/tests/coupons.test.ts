import { describe, expect, it } from "vitest";
import {
  couponStatus, couponToForm, couponToRequest, deletePlan, emptyCouponForm, exampleDiscount, filterCoupons, generateCode, hasCouponErrors, normaliseCode, usageFigures, validateCode,
  validateCoupon, windowOrderedStrict, withActive, type CouponForm, type CouponLike,
} from "@/modules/coupons/logic";

const NOW = Date.parse("2026-12-01T06:00:00+06:00");

const form = (patch: Partial<CouponForm> = {}): CouponForm => ({ ...emptyCouponForm(), code: "EID-10", value: "10", ...patch });

describe("code rules", () => {
  it("upper-cases and removes spaces", () => {
    expect(normaliseCode("  eid 10 ")).toBe("EID10");
    expect(normaliseCode("jv-ab_c")).toBe("JV-AB_C");
  });

  it("accepts 3 to 40 letters, digits, hyphen and underscore", () => {
    expect(validateCode("EID-10")).toBeNull();
    expect(validateCode("abc")).toBeNull();
    expect(validateCode("A".repeat(40))).toBeNull();
    expect(validateCode("")).toBe("required");
    expect(validateCode("   ")).toBe("required");
    expect(validateCode("AB")).toBe("length");
    expect(validateCode("A".repeat(41))).toBe("length");
    expect(validateCode("EID 10")).toBe("chars");
    expect(validateCode("EID%10")).toBe("chars");
    expect(validateCode("ঈদ১০")).toBe("chars");
  });

  it("generates a readable code from a given random source", () => {
    expect(generateCode(() => 0)).toBe("JV-AAAAAA");
    expect(generateCode(() => 0.999999)).toBe("JV-999999");
    const code = generateCode(Math.random, 8, "X");
    expect(code).toMatch(/^X-[A-HJ-NP-Z2-9]{8}$/);
    expect(validateCode(code)).toBeNull();
  });
});

describe("status", () => {
  const base = { isActive: true, startsAt: null, endsAt: null, usageLimit: null, usedCount: 0 };
  it("is Active when nothing blocks it", () => {
    expect(couponStatus(base, NOW)).toBe("Active");
  });
  it("is Off when switched off, before anything else", () => {
    expect(couponStatus({ ...base, isActive: false, endsAt: "2020-01-01T00:00:00+06:00", usageLimit: 1, usedCount: 1 }, NOW)).toBe("Off");
  });
  it("is NotStarted before the start and Expired after the end", () => {
    expect(couponStatus({ ...base, startsAt: "2026-12-02T00:00:00+06:00" }, NOW)).toBe("NotStarted");
    expect(couponStatus({ ...base, endsAt: "2026-11-30T00:00:00+06:00" }, NOW)).toBe("Expired");
    expect(couponStatus({ ...base, startsAt: "2026-11-01T00:00:00+06:00", endsAt: "2026-12-31T00:00:00+06:00" }, NOW)).toBe("Active");
  });
  it("is UsedUp when the uses reach the limit (numbers may arrive as text)", () => {
    expect(couponStatus({ ...base, usageLimit: 5, usedCount: 4 }, NOW)).toBe("Active");
    expect(couponStatus({ ...base, usageLimit: 5, usedCount: 5 }, NOW)).toBe("UsedUp");
    expect(couponStatus({ ...base, usageLimit: "5", usedCount: "6" }, NOW)).toBe("UsedUp");
  });
  it("checks the window before the usage, like the shop does", () => {
    expect(couponStatus({ ...base, endsAt: "2026-11-30T00:00:00+06:00", usageLimit: 1, usedCount: 1 }, NOW)).toBe("Expired");
  });
});

describe("filter", () => {
  const item = (code: string, patch: Partial<{ description: string | null; isActive: boolean; usedCount: number; usageLimit: number | null }> = {}) => ({
    code, description: null as string | null, isActive: true, startsAt: null, endsAt: null, usageLimit: null as number | null, usedCount: 0, ...patch,
  });
  const items = [item("EID10", { description: "Eid sale" }), item("NEW5", { isActive: false }), item("USED", { usageLimit: 1, usedCount: 1 })];
  it("searches code and description in any case", () => {
    expect(filterCoupons(items, "eid", undefined, NOW).map((c) => c.code)).toEqual(["EID10"]);
    expect(filterCoupons(items, "SALE", undefined, NOW).map((c) => c.code)).toEqual(["EID10"]);
    expect(filterCoupons(items, "  ", undefined, NOW)).toHaveLength(3);
  });
  it("filters by the derived status", () => {
    expect(filterCoupons(items, "", "Off", NOW).map((c) => c.code)).toEqual(["NEW5"]);
    expect(filterCoupons(items, "", "UsedUp", NOW).map((c) => c.code)).toEqual(["USED"]);
    expect(filterCoupons(items, "", "Active", NOW).map((c) => c.code)).toEqual(["EID10"]);
    expect(filterCoupons(items, "new", "Active", NOW)).toEqual([]);
  });
});

describe("usage figures", () => {
  it("handles unlimited and limited coupons", () => {
    expect(usageFigures(3, null)).toEqual({ used: 3, limit: null, left: null, percent: null });
    expect(usageFigures("2", "8")).toEqual({ used: 2, limit: 8, left: 6, percent: 25 });
    expect(usageFigures(9, 8)).toEqual({ used: 9, limit: 8, left: 0, percent: 100 });
  });
});

describe("delete plan", () => {
  it("deletes an unused coupon", () => {
    expect(deletePlan({ usedCount: 0, isActive: true }, { edit: false })).toBe("delete");
    expect(deletePlan({ usedCount: "0", isActive: false }, { edit: true })).toBe("delete");
  });
  it("switches a used coupon off, if the person may edit", () => {
    expect(deletePlan({ usedCount: 2, isActive: true }, { edit: true })).toBe("switchOff");
    expect(deletePlan({ usedCount: 2, isActive: true }, { edit: false })).toBe("noEditRight");
    expect(deletePlan({ usedCount: 2, isActive: false }, { edit: true })).toBe("alreadyOff");
  });
});

describe("validation", () => {
  it("accepts a plain percentage coupon", () => {
    expect(validateCoupon(form(), { isNew: true })).toEqual({});
  });

  it("checks the code only for a new coupon (the code never changes)", () => {
    expect(validateCoupon(form({ code: "" }), { isNew: true }).code).toBe("codeRequired");
    expect(validateCoupon(form({ code: "A" }), { isNew: true }).code).toBe("codeLength");
    expect(validateCoupon(form({ code: "A B" }), { isNew: true }).code).toBe("codeChars");
    expect(validateCoupon(form({ code: "" }), { isNew: false }).code).toBeUndefined();
  });

  it("checks the value by kind", () => {
    expect(validateCoupon(form({ value: "" }), { isNew: true }).value).toBe("valueRequired");
    expect(validateCoupon(form({ value: "abc" }), { isNew: true }).value).toBe("valueInvalid");
    expect(validateCoupon(form({ value: "0" }), { isNew: true }).value).toBe("valueNotPositive");
    expect(validateCoupon(form({ value: "-5" }), { isNew: true }).value).toBe("valueNotPositive");
    expect(validateCoupon(form({ value: "100.5" }), { isNew: true }).value).toBe("percentOver100");
    expect(validateCoupon(form({ value: "100" }), { isNew: true }).value).toBeUndefined();
    expect(validateCoupon(form({ value: "12.345" }), { isNew: true }).value).toBe("valueDecimals");
    expect(validateCoupon(form({ kind: "Fixed", value: "500" }), { isNew: true }).value).toBeUndefined();
    expect(validateCoupon(form({ kind: "Fixed", value: "10000001" }), { isNew: true }).value).toBe("valueTooBig");
  });

  it("accepts Bengali digits and commas in money", () => {
    expect(validateCoupon(form({ kind: "Fixed", value: "১,৫০০" }), { isNew: true })).toEqual({});
    expect(couponToRequest(form({ kind: "Fixed", value: "১,৫০০.৫০", minOrderValue: "৳২০০০" })).value).toBe(1500.5);
    expect(couponToRequest(form({ kind: "Fixed", value: "১,৫০০.৫০", minOrderValue: "৳২০০০" })).minOrderValue).toBe(2000);
  });

  it("checks the minimum order, the cap and the limits", () => {
    expect(validateCoupon(form({ minOrderValue: "-1" }), { isNew: true }).minOrderValue).toBe("minNegative");
    expect(validateCoupon(form({ minOrderValue: "x" }), { isNew: true }).minOrderValue).toBe("minInvalid");
    expect(validateCoupon(form({ maxDiscount: "0" }), { isNew: true }).maxDiscount).toBe("capNotPositive");
    expect(validateCoupon(form({ maxDiscount: "250" }), { isNew: true })).toEqual({});
    expect(validateCoupon(form({ kind: "Fixed", value: "50", maxDiscount: "abc" }), { isNew: true })).toEqual({}); // a fixed coupon has no cap
    expect(validateCoupon(form({ usageLimit: "0" }), { isNew: true }).usageLimit).toBe("limitNotPositive");
    expect(validateCoupon(form({ usageLimit: "1.5" }), { isNew: true }).usageLimit).toBe("limitInvalid");
    expect(validateCoupon(form({ usageLimit: "99999999999" }), { isNew: true }).usageLimit).toBe("limitTooBig");
    expect(validateCoupon(form({ usageLimit: "३" }), { isNew: true }).usageLimit).toBe("limitInvalid");
  });

  it("does not allow a limit below the uses already counted", () => {
    expect(validateCoupon(form({ usageLimit: "3" }), { isNew: false, usedCount: 5 }).usageLimit).toBe("limitBelowUsed");
    expect(validateCoupon(form({ usageLimit: "5" }), { isNew: false, usedCount: 5 })).toEqual({});
  });

  it("the end must be after the start", () => {
    expect(windowOrderedStrict("2026-12-01T10:00", "2026-12-01T10:00")).toBe(false);
    expect(windowOrderedStrict("2026-12-01T10:00", "2026-12-01T10:01")).toBe(true);
    expect(windowOrderedStrict("", "2026-12-01T10:00")).toBe(true);
    expect(validateCoupon(form({ startsAt: "2026-12-02T10:00", endsAt: "2026-12-01T10:00" }), { isNew: true }).endsAt).toBe("endBeforeStart");
  });

  it("limits the description to 200 characters", () => {
    expect(validateCoupon(form({ description: "x".repeat(200) }), { isNew: true })).toEqual({});
    expect(validateCoupon(form({ description: "x".repeat(201) }), { isNew: true }).description).toBe("descriptionTooLong");
    expect(hasCouponErrors({})).toBe(false);
    expect(hasCouponErrors({ code: "codeRequired" })).toBe(true);
  });
});

describe("request mapping", () => {
  it("sends a percentage coupon with the Dhaka offset", () => {
    const request = couponToRequest(form({ code: " eid 10", description: "  Eid  ", value: "10", minOrderValue: "1,000", maxDiscount: "250", startsAt: "2026-12-01T10:00", endsAt: "2026-12-31T23:59", usageLimit: "100", stacksWithPromotions: true }));
    expect(request).toEqual({
      code: "EID10", description: "Eid", kind: "Percent", value: 10, minOrderValue: 1000, maxDiscount: 250,
      startsAt: "2026-12-01T10:00:00+06:00", endsAt: "2026-12-31T23:59:00+06:00", usageLimit: 100, isActive: true, stacksWithPromotions: true,
    });
  });

  it("sends empty optional fields as null and the minimum as 0", () => {
    const request = couponToRequest(form({ description: "   " }));
    expect(request).toMatchObject({ description: null, minOrderValue: 0, maxDiscount: null, startsAt: null, endsAt: null, usageLimit: null, stacksWithPromotions: false });
  });

  it("drops the cap of a fixed coupon", () => {
    expect(couponToRequest(form({ kind: "Fixed", value: "50", maxDiscount: "100" })).maxDiscount).toBeNull();
  });

  it("round-trips a coupon from the API through the form (times shown in Dhaka)", () => {
    const dto: CouponLike = {
      code: "EID10", description: "Eid", kind: "Percent", value: 10, minOrderValue: "1000", maxDiscount: 250,
      startsAt: "2026-12-01T04:00:00+00:00", endsAt: "2026-12-31T17:59:00Z", usageLimit: 100, isActive: false, stacksWithPromotions: true,
    };
    const f = couponToForm(dto);
    expect(f).toMatchObject({ code: "EID10", value: "10", minOrderValue: "1000", maxDiscount: "250", startsAt: "2026-12-01T10:00", endsAt: "2026-12-31T23:59", usageLimit: "100", isActive: false, stacksWithPromotions: true });
    const request = couponToRequest(f);
    expect(request.startsAt).toBe("2026-12-01T10:00:00+06:00");
    expect(Date.parse(request.startsAt!)).toBe(Date.parse(dto.startsAt!));
    expect(Date.parse(request.endsAt!)).toBe(Date.parse(dto.endsAt!));
    expect(request).toMatchObject({ value: 10, minOrderValue: 1000, maxDiscount: 250, usageLimit: 100, isActive: false });
  });

  it("an unlimited coupon with no minimum shows empty boxes", () => {
    const f = couponToForm({ code: "A1B", description: null, kind: "Fixed", value: 50, minOrderValue: 0, maxDiscount: null, startsAt: null, endsAt: null, usageLimit: null, isActive: true });
    expect(f).toMatchObject({ minOrderValue: "", maxDiscount: "", startsAt: "", endsAt: "", usageLimit: "", description: "", stacksWithPromotions: false });
  });

  it("switching off keeps every other value", () => {
    const dto: CouponLike = { code: "EID10", description: "Eid", kind: "Percent", value: "10", minOrderValue: "0", maxDiscount: null, startsAt: "2026-12-01T10:00:00+06:00", endsAt: null, usageLimit: "100", isActive: true, stacksWithPromotions: true };
    expect(withActive(dto, false)).toEqual({
      code: "EID10", description: "Eid", kind: "Percent", value: 10, minOrderValue: 0, maxDiscount: null, startsAt: "2026-12-01T10:00:00+06:00", endsAt: null, usageLimit: 100, isActive: false, stacksWithPromotions: true,
    });
  });
});

describe("example discount", () => {
  it("is a percentage of the basket, capped, never above the basket", () => {
    expect(exampleDiscount({ kind: "Percent", value: "10", maxDiscount: "" }, 2000)).toBe(200);
    expect(exampleDiscount({ kind: "Percent", value: "10", maxDiscount: "150" }, 2000)).toBe(150);
    expect(exampleDiscount({ kind: "Fixed", value: "500", maxDiscount: "10" }, 2000)).toBe(500);
    expect(exampleDiscount({ kind: "Fixed", value: "500", maxDiscount: "" }, 300)).toBe(300);
    expect(exampleDiscount({ kind: "Percent", value: "", maxDiscount: "" }, 2000)).toBeNull();
    expect(exampleDiscount({ kind: "Percent", value: "12.5", maxDiscount: "" }, 999)).toBe(124.88);
  });
});
