import { describe, expect, it } from "vitest";
import {
  capSummary,
  clampPoints,
  creditRoom,
  earnExample,
  emptyTenderDraft,
  formatGiftCardCode,
  giftCardStatus,
  hasSpendable,
  hasTenderProblem,
  isSettingsChanged,
  isValidGiftCardCode,
  isValidReferralCode,
  ledgerTone,
  maxPointsToOffer,
  normalizeCode,
  parseAmount,
  parseWhole,
  payableAfterTender,
  pointsToAmount,
  signed,
  tenderRows,
  toAdjustBody,
  toGiftCardBody,
  toSettingsForm,
  toSettingsPatch,
  toTenderRequest,
  toUsable,
  validateAdjust,
  validateGiftCard,
  validateSettingsForm,
  type LoyaltySettings,
  type Usable,
} from "@/modules/loyalty/logic";
import en from "@/messages/loyalty.en.json";
import bn from "@/messages/loyalty.bn.json";

const settings: LoyaltySettings = {
  enabled: false,
  earnRatePerBdt: 0.01,
  earnRounding: "Down",
  pointValue: 1,
  minPointsToRedeem: 100,
  maxTenderPercent: 50,
  pointsExpiryMonths: 12,
  expiryWarningDays: 14,
  referralRewardReferrer: 100,
  referralRewardReferee: 100,
  referralMaxRewardsPerReferrer: 20,
  giftCardMinAmount: 100,
  giftCardMaxAmount: 20000,
  giftCardDefaultValidityMonths: 12,
};

const usable: Usable = { pointValue: 1, minPointsToRedeem: 100, maxTenderPercent: 50, maxTenderAmount: 500, maxPoints: 400, maxCredit: 300, pointsBalance: 400, creditBalance: 300 };

describe("tender maths", () => {
  it("values whole points in BDT", () => {
    expect(pointsToAmount(250, 1)).toBe(250);
    expect(pointsToAmount(250.9, 0.5)).toBe(125);
    expect(pointsToAmount(-5, 1)).toBe(0);
  });

  it("reads typed numbers safely", () => {
    expect(parseWhole("12")).toBe(12);
    expect(parseWhole("12.7")).toBe(12);
    expect(parseWhole("-3")).toBe(0);
    expect(parseWhole("abc")).toBe(0);
    expect(parseAmount("50,5")).toBe(50.5);
    expect(parseAmount("0.005")).toBe(0.01);
    expect(parseAmount("")).toBe(0);
  });

  it("offers points only when the maximum reaches the order minimum", () => {
    expect(maxPointsToOffer(usable)).toBe(400);
    expect(maxPointsToOffer({ ...usable, maxPoints: 99 })).toBe(0);
    expect(maxPointsToOffer({ ...usable, maxPoints: 0, minPointsToRedeem: 0 })).toBe(0);
  });

  it("clamps points to the cap and the minimum", () => {
    expect(clampPoints(250, usable)).toBe(250);
    expect(clampPoints(9999, usable)).toBe(400);
    expect(clampPoints(50, usable)).toBe(0);
    expect(clampPoints(0, usable)).toBe(0);
    expect(clampPoints(-4, usable)).toBe(0);
  });

  it("leaves store credit only what still fits under the share cap", () => {
    expect(creditRoom(usable, 0)).toBe(300);
    expect(creditRoom(usable, 300)).toBe(200);
    expect(creditRoom(usable, 400)).toBe(100);
    expect(creditRoom({ ...usable, maxTenderAmount: 100 }, 400)).toBe(0);
  });

  it("explains the cap", () => {
    expect(capSummary(usable)).toEqual({ percent: 50, amount: 500 });
  });

  it("maps the usable answer and floors points", () => {
    const u = toUsable({ enabled: true, orderTotal: 1000, pointsBalance: "400", pointsValue: 400, creditBalance: 300, pointValue: "1", minPointsToRedeem: 100, maxTenderPercent: 50, maxTenderAmount: 500, maxPoints: 400.9, maxCredit: "300" });
    expect(u).toMatchObject({ maxPoints: 400, maxCredit: 300, pointsBalance: 400, pointValue: 1 });
    expect(toUsable(undefined)).toBeNull();
  });

  it("builds the tender request, or none when nothing is offered", () => {
    expect(toTenderRequest(emptyTenderDraft)).toBeNull();
    expect(toTenderRequest({ points: "200", credit: "", giftCardCode: "" })).toEqual({ points: 200, credit: 0, giftCardCode: null });
    expect(toTenderRequest({ points: "", credit: "50.5", giftCardCode: "abcd-efgh jklm-npqr" })).toEqual({ points: 0, credit: 50.5, giftCardCode: "ABCDEFGHJKLMNPQR" });
  });

  const plan = { enabled: true, pointsUsed: 200, pointsAmount: 200, creditUsed: 0, giftCardUsed: 50, tenderTotal: 250, payableAfterTender: 810, pointsBalance: 500, creditBalance: 0, problem: null, problemMessage: null };

  it("lists only the used lines of the plan", () => {
    expect(tenderRows(plan)).toEqual([
      { key: "points", amount: 200, points: 200 },
      { key: "giftCard", amount: 50 },
    ]);
    expect(tenderRows(null)).toEqual([]);
  });

  it("computes what is left to pay without going below zero or above the total", () => {
    expect(payableAfterTender(1060, 250)).toBe(810);
    expect(payableAfterTender(100, 250)).toBe(0);
    expect(payableAfterTender(100, -5)).toBe(100);
  });

  it("recognises a refused tender and spendable balances", () => {
    expect(hasTenderProblem(plan)).toBe(false);
    expect(hasTenderProblem({ ...plan, problem: "LOYALTY.TENDER_EXCEEDS_CAP" })).toBe(true);
    expect(hasSpendable(plan)).toBe(true);
    expect(hasSpendable({ ...plan, pointsBalance: 0, creditBalance: 0 })).toBe(false);
  });
});

describe("codes", () => {
  it("normalises codes typed in any case with dashes or spaces", () => {
    expect(normalizeCode(" abcd-efgh ")).toBe("ABCDEFGH");
    expect(formatGiftCardCode("abcdefghjklmnpqr")).toBe("ABCD-EFGH-JKLM-NPQR");
    expect(formatGiftCardCode("abc")).toBe("ABC");
    expect(formatGiftCardCode("abcd")).toBe("ABCD");
  });

  it("validates referral codes (8 characters of the no-look-alike alphabet)", () => {
    expect(isValidReferralCode("ABCD2345")).toBe(true);
    expect(isValidReferralCode("abcd-2345")).toBe(true);
    expect(isValidReferralCode("ABCD234")).toBe(false);
    expect(isValidReferralCode("ABCD234O")).toBe(false); // O is a look-alike, not in the alphabet
    expect(isValidReferralCode("ABCD2341")).toBe(false); // 1 is not in the alphabet
  });

  it("validates gift card codes (16 characters)", () => {
    expect(isValidGiftCardCode("ABCD-EFGH-JKLM-NPQR")).toBe(true);
    expect(isValidGiftCardCode("abcdefghjklmnpqr")).toBe(true);
    expect(isValidGiftCardCode("ABCD-EFGH-JKLM")).toBe(false);
    expect(isValidGiftCardCode("ABCD-EFGH-JKLM-NPQ0")).toBe(false);
  });
});

describe("settings form", () => {
  it("round-trips the settings through the form and sends nothing when unchanged", () => {
    const form = toSettingsForm(settings);
    expect(form.earnRatePerBdt).toBe("0.01");
    expect(form.maxTenderPercent).toBe("50");
    expect(validateSettingsForm(form)).toEqual({});
    expect(isSettingsChanged(settings, form)).toBe(false);
    expect(Object.values(toSettingsPatch(settings, form)).every((v) => v === null)).toBe(true);
  });

  it("sends only the changed fields", () => {
    const form = { ...toSettingsForm(settings), enabled: true, earnRatePerBdt: "0,02", maxTenderPercent: "40" };
    expect(isSettingsChanged(settings, form)).toBe(true);
    const patch = toSettingsPatch(settings, form);
    expect(patch.enabled).toBe(true);
    expect(patch.earnRatePerBdt).toBe(0.02);
    expect(patch.maxTenderPercent).toBe(40);
    expect(patch.pointValue).toBeNull();
    expect(patch.earnRounding).toBeNull();
  });

  it("checks every field against the ranges of the API", () => {
    const base = toSettingsForm(settings);
    expect(validateSettingsForm({ ...base, maxTenderPercent: "101" }).maxTenderPercent).toBe("range");
    expect(validateSettingsForm({ ...base, maxTenderPercent: "50.5" }).maxTenderPercent).toBe("whole");
    expect(validateSettingsForm({ ...base, pointValue: "0" }).pointValue).toBe("range");
    expect(validateSettingsForm({ ...base, pointValue: "" }).pointValue).toBe("required");
    expect(validateSettingsForm({ ...base, pointValue: "abc" }).pointValue).toBe("number");
    expect(validateSettingsForm({ ...base, earnRatePerBdt: "100.5" }).earnRatePerBdt).toBe("range");
    expect(validateSettingsForm({ ...base, pointsExpiryMonths: "121" }).pointsExpiryMonths).toBe("range");
    expect(validateSettingsForm({ ...base, giftCardMinAmount: "500", giftCardMaxAmount: "100" }).giftCardMaxAmount).toBe("giftOrder");
  });

  it("explains the earn rate in plain words", () => {
    expect(earnExample(0.01)).toEqual({ points: 1, perBdt: 100 });
    expect(earnExample(2)).toEqual({ points: 2, perBdt: 1 });
    expect(earnExample(0)).toBeNull();
  });
});

describe("ledger formatting", () => {
  it("shows signs with a real minus and tones from the sign", () => {
    expect(signed(50)).toBe("+50");
    expect(signed(-20)).toBe("−20");
    expect(signed(0)).toBe("0");
    expect(signed(-12.5, (n) => `BDT ${n}`)).toBe("−BDT 12.5");
    expect(ledgerTone(5)).toBe("success");
    expect(ledgerTone(-5)).toBe("danger");
    expect(ledgerTone(0)).toBe("neutral");
  });
});

describe("staff forms", () => {
  it("validates and maps a manual adjustment", () => {
    expect(validateAdjust({ points: "", credit: "", reason: "x" })).toEqual({ none: "required" });
    expect(validateAdjust({ points: "50", credit: "", reason: "" })).toEqual({ reason: "required" });
    expect(validateAdjust({ points: "1.5", credit: "", reason: "x" }).points).toBe("whole");
    expect(validateAdjust({ points: "abc", credit: "", reason: "x" }).points).toBe("number");
    expect(validateAdjust({ points: "-50", credit: "20", reason: "ok" })).toEqual({});
    expect(toAdjustBody({ points: "-50", credit: "20.5", reason: "  Goodwill  " })).toEqual({ points: -50, credit: 20.5, reason: "Goodwill" });
    expect(toAdjustBody({ points: "", credit: "-3", reason: "r" })).toEqual({ points: null, credit: -3, reason: "r" });
  });

  it("validates a new gift card against the limits and today", () => {
    const limits = { min: 100, max: 20000 };
    const draft = { amount: "500", expiresOn: "", note: "", recipientMobile: "", recipientEmail: "", send: false };
    expect(validateGiftCard(draft, limits, "2026-11-18")).toEqual({});
    expect(validateGiftCard({ ...draft, amount: "" }, limits, "2026-11-18").amount).toBe("required");
    expect(validateGiftCard({ ...draft, amount: "50" }, limits, "2026-11-18").amount).toBe("range");
    expect(validateGiftCard({ ...draft, amount: "25000" }, limits, "2026-11-18").amount).toBe("range");
    expect(validateGiftCard({ ...draft, amount: "-1" }, limits, "2026-11-18").amount).toBe("number");
    expect(validateGiftCard({ ...draft, amount: "50" }, null, "2026-11-18")).toEqual({});
    expect(validateGiftCard({ ...draft, expiresOn: "2026-11-01" }, limits, "2026-11-18").expiresOn).toBe("past");
    expect(validateGiftCard({ ...draft, send: true }, limits, "2026-11-18").recipient).toBe("needsRecipient");
    expect(validateGiftCard({ ...draft, send: true, recipientMobile: "01712345678" }, limits, "2026-11-18")).toEqual({});
  });

  it("builds the gift card request", () => {
    const body = toGiftCardBody({ amount: "500", expiresOn: "", note: " Eid gift ", recipientMobile: " 01712345678 ", recipientEmail: "", send: true });
    expect(body).toEqual({ amount: 500, expiresAt: null, note: "Eid gift", recipientMobile: "01712345678", recipientEmail: null, sendToRecipient: true });
    expect(toGiftCardBody({ amount: "10", expiresOn: "2027-01-31", note: "", recipientMobile: "", recipientEmail: "", send: false }).expiresAt).toMatch(/^2027-01-3[01]T/);
  });

  it("reads the gift card status text in any case", () => {
    expect(giftCardStatus("Active")).toBe("active");
    expect(giftCardStatus("DEACTIVATED")).toBe("deactivated");
    expect(giftCardStatus("weird")).toBeNull();
  });
});

describe("messages", () => {
  it("has the same keys in English and Bengali", () => {
    const keys = (o: unknown, prefix = ""): string[] =>
      Object.entries(o as Record<string, unknown>).flatMap(([k, v]) => (v && typeof v === "object" ? keys(v, `${prefix}${k}.`) : [`${prefix}${k}`]));
    expect(keys(bn).sort()).toEqual(keys(en).sort());
  });
});
