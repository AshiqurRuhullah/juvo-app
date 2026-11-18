import { describe, expect, it } from "vitest";
import {
  clockText,
  countdownParts,
  countdownSegments,
  couponView,
  explanationNotes,
  freeDeliveryView,
  hasSavings,
  lineOffer,
  priceView,
  promotionLabel,
  savingRows,
  tickStepMs,
  whyThisPrice,
} from "@/modules/promotions-store/logic";

const T0 = Date.parse("2026-12-01T10:00:00+06:00");
const at = (offsetMs: number) => new Date(T0 + offsetMs).toISOString();
const SEC = 1000;
const MIN = 60 * SEC;
const HOUR = 60 * MIN;
const DAY = 24 * HOUR;

describe("countdown maths", () => {
  it("splits what is left into days, hours, minutes and seconds", () => {
    const parts = countdownParts(at(2 * DAY + 3 * HOUR + 4 * MIN + 5 * SEC), T0)!;
    expect(parts).toMatchObject({ ended: false, days: 2, hours: 3, minutes: 4, seconds: 5 });
  });

  it("has no countdown without an end time", () => {
    expect(countdownParts(null, T0)).toBeNull();
    expect(countdownParts(undefined, T0)).toBeNull();
    expect(countdownParts("", T0)).toBeNull();
    expect(countdownParts("not a date", T0)).toBeNull();
  });

  it("is not over at the last second (the API prices up to and including it) and is over after it", () => {
    expect(countdownParts(at(0), T0)).toMatchObject({ ended: false, seconds: 0 });
    expect(countdownParts(at(0), T0 + 1)).toMatchObject({ ended: true, totalMs: 0 });
    expect(countdownParts(at(-DAY), T0)).toMatchObject({ ended: true });
  });

  it("rounds down so the clock never shows a second too many", () => {
    const parts = countdownParts(at(59 * SEC + 999), T0)!;
    expect(parts.seconds).toBe(59);
    expect(parts.minutes).toBe(0);
  });

  it("ticks once a minute while more than an hour is left and once a second after that", () => {
    expect(tickStepMs(3 * DAY)).toBe(MIN);
    expect(tickStepMs(HOUR + 2 * MIN)).toBe(MIN);
    expect(tickStepMs(HOUR + MIN)).toBe(SEC);
    expect(tickStepMs(5 * MIN)).toBe(SEC);
    expect(tickStepMs(0)).toBe(SEC);
  });

  it("prints days only from one day and seconds only inside the last hour", () => {
    const units = (ms: number) => countdownSegments(countdownParts(at(ms), T0)!).map((s) => s.unit).join("");
    expect(units(2 * DAY + 5 * HOUR)).toBe("dhm");
    expect(units(5 * HOUR)).toBe("hm");
    expect(units(HOUR)).toBe("hms");
    expect(units(30 * MIN)).toBe("hms");
    expect(countdownSegments(countdownParts(at(-1), T0)!)).toEqual([]);
  });

  it("writes a plain clock text", () => {
    expect(clockText(countdownParts(at(2 * HOUR + 14 * MIN + 9 * SEC), T0)!)).toBe("02:14");
    expect(clockText(countdownParts(at(14 * MIN + 9 * SEC), T0)!)).toBe("00:14:09");
    expect(clockText(countdownParts(at(DAY + 2 * HOUR), T0)!)).toBe("1d 02:00");
  });
});

describe("price on a card or product page", () => {
  it("shows the saving and the percentage when the price is below the list price", () => {
    expect(priceView(800, 1000)).toEqual({ price: 800, regular: 1000, saving: 200, percent: 20, onSale: true });
    expect(priceView("674.50", "1000")).toMatchObject({ saving: 325.5, percent: 33, onSale: true });
  });

  it("shows nothing when the price equals or exceeds the list price, or the list price is missing", () => {
    expect(priceView(1000, 1000).onSale).toBe(false);
    expect(priceView(1200, 1000).saving).toBe(0);
    expect(priceView(500, 0).onSale).toBe(false);
    expect(priceView(null, undefined)).toMatchObject({ price: 0, saving: 0, onSale: false });
  });

  it("picks the label in the page language and falls back to the other one", () => {
    const promo = { labelEn: "Eid flash", labelBn: "ঈদ ফ্ল্যাশ" };
    expect(promotionLabel(promo, "en")).toBe("Eid flash");
    expect(promotionLabel(promo, "bn")).toBe("ঈদ ফ্ল্যাশ");
    expect(promotionLabel({ labelEn: "Eid flash", labelBn: "" }, "bn")).toBe("Eid flash");
    expect(promotionLabel({ labelEn: " ", labelBn: "" }, "en")).toBeNull();
    expect(promotionLabel(null, "en")).toBeNull();
  });
});

describe("price lines of a cart or quote", () => {
  const explanation = {
    reasons: [
      { sourceType: "Sale", labelEn: "Sale price", labelBn: "বিক্রয় মূল্য", amount: 100 },
      { sourceType: "Promotion", labelEn: "Eid flash", labelBn: "ঈদ ফ্ল্যাশ", amount: 150 },
      { sourceType: "Coupon", labelEn: "Coupon SAVE10", labelBn: "কুপন SAVE10", amount: 50 },
    ],
  };

  it("lists the list price, then one row per offer, never the coupon (it has its own row)", () => {
    const rows = savingRows({ explanation, listSubtotal: 2000, subtotal: 1750, promotionDiscount: 0 }, "en", "Offers");
    expect(rows.map((r) => [r.key, r.kind, r.label, r.value])).toEqual([
      ["list", "list", "", 2000],
      ["promo-0", "reason", "Sale price", 100],
      ["promo-1", "reason", "Eid flash", 150],
    ]);
    expect(savingRows({ explanation, listSubtotal: 2000, subtotal: 1750, promotionDiscount: 0 }, "bn", "অফার")[2]!.label).toBe("ঈদ ফ্ল্যাশ");
  });

  it("counts line-level and order-level savings together", () => {
    const e = { reasons: [{ sourceType: "Bundle", labelEn: "Tee and cap", labelBn: "", amount: 200 }, { sourceType: "Sale", labelEn: "Sale price", labelBn: "", amount: 100 }] };
    const rows = savingRows({ explanation: e, listSubtotal: 2000, subtotal: 1900, promotionDiscount: 200 }, "en", "Offers");
    expect(rows.slice(1).reduce((n, r) => n + r.value, 0)).toBe(300);
  });

  it("falls back to one 'Offers' row when the explanation is missing or does not add up", () => {
    const rows = savingRows({ explanation: null, listSubtotal: 1000, subtotal: 900, promotionDiscount: 50 }, "en", "Offers");
    expect(rows).toHaveLength(2);
    expect(rows[1]).toMatchObject({ label: "Offers", value: 150 });
    const odd = savingRows({ explanation, listSubtotal: 2000, subtotal: 1000, promotionDiscount: 0 }, "en", "Offers");
    expect(odd[1]).toMatchObject({ label: "Offers", value: 1000 });
  });

  it("returns nothing when nothing was saved", () => {
    expect(savingRows({ explanation, listSubtotal: 1000, subtotal: 1000, promotionDiscount: 0 }, "en", "Offers")).toEqual([]);
    expect(savingRows({ explanation: null, listSubtotal: 0, subtotal: 0, promotionDiscount: 0 }, "en", "Offers")).toEqual([]);
    expect(hasSavings({ listSubtotal: 1000, subtotal: 1000, promotionDiscount: 0 })).toBe(false);
    expect(hasSavings({ listSubtotal: 1000, subtotal: 900, promotionDiscount: 0 })).toBe(true);
    expect(hasSavings({ listSubtotal: 1000, subtotal: 1000, promotionDiscount: 80 })).toBe(true);
  });
});

describe("coupon outcome", () => {
  it("tells applied, kept-but-lost (decision P4) and no-longer-valid apart", () => {
    expect(couponView(null, "en")).toEqual({ state: "none" });
    expect(couponView({ code: "SAVE10", discount: 50 }, "en")).toEqual({ state: "applied", code: "SAVE10", discount: 50 });
    expect(couponView({ code: "SAVE10", discount: 50, applied: true }, "en")).toMatchObject({ state: "applied" });
    expect(couponView({ code: "SAVE10", discount: 0, applied: false, notAppliedReasonEn: "The promotions already give a better price.", notAppliedReasonBn: "অফারে ভালো দাম" }, "en")).toEqual({
      state: "lost",
      code: "SAVE10",
      reason: "The promotions already give a better price.",
    });
    expect(couponView({ code: "SAVE10", discount: 0, applied: false, notAppliedReasonEn: "x", notAppliedReasonBn: "অফারে ভালো দাম" }, "bn")).toMatchObject({ reason: "অফারে ভালো দাম" });
    expect(couponView({ code: "SAVE10", discount: 0, applied: false }, "en")).toEqual({ state: "lost", code: "SAVE10", reason: null });
    expect(couponView({ code: "OLD", discount: 0, problem: "COUPON.EXPIRED" }, "en")).toEqual({ state: "invalid", code: "OLD" });
  });
});

describe("why this price", () => {
  it("names each source once, with the total saving and the notes of the answer", () => {
    const why = whyThisPrice(
      {
        savings: 250,
        reasons: [
          { sourceType: "Promotion", labelEn: "Eid flash", labelBn: "ঈদ", amount: 150 },
          { sourceType: "Promotion", labelEn: "Eid flash", labelBn: "ঈদ", amount: 50 },
          { sourceType: "Coupon", labelEn: "Coupon A", labelBn: "কুপন A", amount: 50 },
        ],
        notesEn: ["Coupon A was applied."],
        notesBn: ["কুপন A প্রযোজ্য হয়েছে।"],
      },
      "en",
    );
    expect(why).toEqual({ labels: ["Eid flash", "Coupon A"], saving: 250, notes: ["Coupon A was applied."] });
  });

  it("has nothing to say without reasons and notes", () => {
    expect(whyThisPrice(null, "en")).toBeNull();
    expect(whyThisPrice({ savings: 0, reasons: [], notesEn: [], notesBn: [] }, "en")).toBeNull();
  });

  it("keeps a note about a lost coupon even when no promotion has an amount", () => {
    expect(whyThisPrice({ savings: 0, reasons: [], notesEn: ["Coupon X was not applied: the promotions already give a better price."] }, "en")?.notes).toHaveLength(1);
  });

  it("uses the other language when the page language has no notes", () => {
    expect(explanationNotes({ notesEn: ["A"], notesBn: [] }, "bn")).toEqual(["A"]);
    expect(explanationNotes({ notesEn: ["A"], notesBn: ["অ"] }, "bn")).toEqual(["অ"]);
  });
});

describe("free delivery in the cart", () => {
  it("says free when an offer qualifies", () => {
    expect(freeDeliveryView([{ qualifies: true, amountToQualify: 0, labelEn: "Free over 2500", labelBn: "২৫০০-র উপরে ফ্রি" }], "en")).toEqual({ state: "free", label: "Free over 2500" });
  });

  it("names the smallest amount still missing and whether the offer only holds in some areas", () => {
    const view = freeDeliveryView(
      [
        { qualifies: false, amountToQualify: 900, labelEn: "Big", labelBn: "", zoneCodes: [] },
        { qualifies: false, amountToQualify: 300, labelEn: "Dhaka only", labelBn: "ঢাকা", zoneCodes: ["DHAKA"] },
      ],
      "bn",
    );
    expect(view).toEqual({ state: "more", label: "ঢাকা", amount: 300, zoneLimited: true });
  });

  it("is silent without offers", () => {
    expect(freeDeliveryView(null, "en")).toBeNull();
    expect(freeDeliveryView([], "en")).toBeNull();
  });
});

describe("a cart line's offer", () => {
  it("shows the struck-through list price and the label when the unit price is lower", () => {
    expect(lineOffer({ unitPrice: 800, listPrice: 1000, promotionLabelEn: "Eid flash", promotionLabelBn: "ঈদ" }, "en")).toEqual({ list: 1000, saving: 200, label: "Eid flash" });
  });

  it("shows nothing for a normal price, and something for an order-level share even at the list price", () => {
    expect(lineOffer({ unitPrice: 1000, listPrice: 1000 }, "en")).toBeNull();
    expect(lineOffer({ unitPrice: 1000, listPrice: 0 }, "en")).toBeNull();
    expect(lineOffer({ unitPrice: 1000, listPrice: 1000, orderDiscount: 100 }, "en")).toMatchObject({ saving: 0 });
  });
});
