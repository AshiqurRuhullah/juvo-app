import { describe, expect, it } from "vitest";
import {
  areaKey, draftFromAreas, draftToAreas, emptyDetailsForm, emptyRatesForm, expectedCharge, formatWeight, groupState, gramsToKgText, importSummary, importToRequest, matchesName, moneyToText,
  movesFromOthers, ownersOf, parseKgToGrams, parseMoney, parseUpazilaImport, quoteAddsUp, quoteRows, quoteToRequest, ratesToForm, ratesToRequest, ratesWarnings, sameAreas, setDistricts,
  setRemote, slabRanges, splitLine, toggleUpazila, validateCountry, validateDetails, validateQuote, validateRates, countryToRequest, hasRatesErrors, detailsToCreateRequest, detailsToUpdateRequest,
  type RatesForm,
} from "@/modules/delivery/logic";

const form = (over: Partial<RatesForm> = {}): RatesForm => ({ ...emptyRatesForm(), baseFee: "70", ...over });

describe("money and weight parsing", () => {
  it("accepts plain, comma and Bengali digit amounts with at most two decimals", () => {
    expect(parseMoney("70")).toEqual({ ok: true, value: 70 });
    expect(parseMoney("1,250.5")).toEqual({ ok: true, value: 1250.5 });
    expect(parseMoney("৭০.৫০")).toEqual({ ok: true, value: 70.5 });
    expect(parseMoney("৳ 100")).toEqual({ ok: true, value: 100 });
    expect(parseMoney(".5")).toEqual({ ok: true, value: 0.5 });
  });
  it("rejects empty, text, negatives, three decimals and huge amounts", () => {
    expect(parseMoney("")).toEqual({ ok: false, error: "required" });
    expect(parseMoney("abc")).toEqual({ ok: false, error: "invalid" });
    expect(parseMoney("7o")).toEqual({ ok: false, error: "invalid" });
    expect(parseMoney("-5")).toEqual({ ok: false, error: "negative" });
    expect(parseMoney("1.234")).toEqual({ ok: false, error: "decimals" });
    expect(parseMoney("1000000.01")).toEqual({ ok: false, error: "tooBig" });
    expect(parseMoney("1000000")).toEqual({ ok: true, value: 1_000_000 });
  });
  it("formats money for an input", () => {
    expect(moneyToText(70)).toBe("70");
    expect(moneyToText("70.5")).toBe("70.50");
    expect(moneyToText(null)).toBe("");
  });
  it("converts kilograms to grams and back", () => {
    expect(parseKgToGrams("1.5")).toEqual({ ok: true, grams: 1500 });
    expect(parseKgToGrams("0.25")).toEqual({ ok: true, grams: 250 });
    expect(parseKgToGrams("২০")).toEqual({ ok: true, grams: 20000 });
    expect(parseKgToGrams("0")).toEqual({ ok: false, error: "notPositive" });
    expect(parseKgToGrams("1.2345")).toEqual({ ok: false, error: "decimals" });
    expect(parseKgToGrams("x")).toEqual({ ok: false, error: "invalid" });
    expect(parseKgToGrams("")).toEqual({ ok: false, error: "required" });
    expect(gramsToKgText(20000)).toBe("20");
    expect(gramsToKgText(250)).toBe("0.25");
    expect(gramsToKgText(null)).toBe("");
    expect(formatWeight(250)).toBe("250 g");
    expect(formatWeight(1500)).toBe("1.5 kg");
  });
});

describe("rates form", () => {
  it("round-trips the zone rates and sorts the slabs by weight", () => {
    const f = ratesToForm({ baseFee: 70, freeDeliveryThreshold: 5000, remoteSurcharge: 0, perExtraKgFee: 20, maxWeightGrams: null, slabs: [{ upToGrams: 2000, extraFee: 20 }, { upToGrams: 1000, extraFee: 0 }] });
    expect(f.slabs.map((s) => s.upToKg)).toEqual(["1", "2"]);
    expect(ratesToRequest(f)).toEqual({ baseFee: 70, freeDeliveryThreshold: 5000, remoteSurcharge: 0, perExtraKgFee: 20, maxWeightGrams: null, slabs: [{ upToGrams: 1000, extraFee: 0 }, { upToGrams: 2000, extraFee: 20 }] });
  });
  it("sends an empty threshold and limit as null, not zero", () => {
    const r = ratesToRequest(form({ freeThreshold: "", maxWeightKg: "" }));
    expect(r.freeDeliveryThreshold).toBeNull();
    expect(r.maxWeightGrams).toBeNull();
    expect(ratesToRequest(form({ freeThreshold: "0" })).freeDeliveryThreshold).toBe(0);
  });
  it("validates every field with a plain key", () => {
    const e = validateRates({ baseFee: "", freeThreshold: "-1", remoteSurcharge: "1.234", perExtraKgFee: "abc", maxWeightKg: "0", slabs: [] });
    expect(e.baseFee).toBe("required");
    expect(e.freeThreshold).toBe("negative");
    expect(e.remoteSurcharge).toBe("decimals");
    expect(e.perExtraKgFee).toBe("invalid");
    expect(e.maxWeightKg).toBe("kgNotPositive");
    expect(hasRatesErrors(e)).toBe(true);
    expect(hasRatesErrors(validateRates(form()))).toBe(false);
  });
  it("finds repeated slab limits and bad slab rows", () => {
    const e = validateRates(form({ slabs: [{ upToKg: "1", extraFee: "0" }, { upToKg: "1.000", extraFee: "20" }, { upToKg: "", extraFee: "x" }] }));
    expect(e.slabRows[1]).toEqual({ upToKg: "duplicateSlab" });
    expect(e.slabRows[2]).toEqual({ upToKg: "kgRequired", extraFee: "invalid" });
    expect(e.slabRows[0]).toBeUndefined();
  });
  it("limits the number of slabs to 20", () => {
    const slabs = Array.from({ length: 21 }, (_, i) => ({ upToKg: String(i + 1), extraFee: "0" }));
    expect(validateRates(form({ slabs })).slabs).toBe("tooManySlabs");
  });
  it("warns about probable mistakes without blocking", () => {
    expect(ratesWarnings(form({ perExtraKgFee: "20" })).map((w) => w.code)).toContain("perKgIgnored");
    const drop = ratesWarnings(form({ slabs: [{ upToKg: "1", extraFee: "50" }, { upToKg: "2", extraFee: "20" }] }));
    expect(drop).toContainEqual({ code: "feeDrops", row: 1 });
    const unreachable = ratesWarnings(form({ maxWeightKg: "1.5", slabs: [{ upToKg: "1", extraFee: "0" }, { upToKg: "2", extraFee: "20" }] }));
    expect(unreachable).toContainEqual({ code: "unreachableSlab", row: 1 });
    expect(ratesWarnings(form({ freeThreshold: "0" })).map((w) => w.code)).toContain("freeBelowBase");
    expect(ratesWarnings(form())).toEqual([]);
  });
  it("lists the weight ranges the slabs make, with the open range beyond the last one", () => {
    const ranges = slabRanges(form({ slabs: [{ upToKg: "2", extraFee: "20" }, { upToKg: "1", extraFee: "0" }] }));
    expect(ranges).toEqual([
      { fromGrams: 0, toGrams: 1000, fee: 0, beyond: false },
      { fromGrams: 1000, toGrams: 2000, fee: 20, beyond: false },
      { fromGrams: 2000, toGrams: null, fee: null, beyond: true },
    ]);
    expect(slabRanges(form())).toEqual([]);
  });
});

describe("expected charge (mirror of the backend routine)", () => {
  const rates = { baseFee: 70, freeDeliveryThreshold: 5000, remoteSurcharge: 50, perExtraKgFee: 20, maxWeightGrams: 20000, slabs: [{ upToGrams: 1000, extraFee: 0 }, { upToGrams: 2000, extraFee: 20 }, { upToGrams: 5000, extraFee: 60 }] };
  it("uses the first slab that holds the weight", () => {
    expect(expectedCharge(rates, 1000, 1500, false)).toEqual({ baseFee: 70, weightFee: 20, remote: 0, waiver: 0, total: 90 });
    expect(expectedCharge(rates, 1000, 1000, false)?.weightFee).toBe(0);
  });
  it("adds the per-kilogram fee for each started kilogram beyond the last slab", () => {
    expect(expectedCharge(rates, 1000, 6200, false)?.weightFee).toBe(60 + 2 * 20);
  });
  it("adds the remote surcharge and waives base and weight (not remote) at the threshold", () => {
    expect(expectedCharge(rates, 1000, 1500, true)?.total).toBe(140);
    expect(expectedCharge(rates, 5000, 1500, true)).toEqual({ baseFee: 70, weightFee: 20, remote: 50, waiver: 90, total: 50 });
  });
  it("refuses a parcel above the maximum weight", () => {
    expect(expectedCharge(rates, 100, 20001, false)).toBeNull();
  });
  it("ignores the weight fee when no slabs exist", () => {
    expect(expectedCharge({ ...rates, slabs: [] }, 100, 9000, false)?.weightFee).toBe(0);
  });
});

describe("zone details form", () => {
  it("checks code, names, order and delivery days", () => {
    const f = emptyDetailsForm();
    expect(validateDetails(f, true)).toMatchObject({ code: "codeInvalid", nameEn: "nameRequired" });
    expect(validateDetails({ ...f, code: "ab", nameEn: "X" }, true)).toEqual({});
    expect(validateDetails({ ...f, nameEn: "X", daysMin: "5", daysMax: "2" }, false).daysMax).toBe("daysOrder");
    expect(validateDetails({ ...f, nameEn: "X", daysMax: "91" }, false).daysMax).toBe("daysTooBig");
    expect(validateDetails({ ...f, nameEn: "X", sortOrder: "a" }, false).sortOrder).toBe("sortInvalid");
    expect(validateDetails({ ...f, nameEn: "x".repeat(101) }, false).nameEn).toBe("nameTooLong");
  });
  it("builds create and update requests (fallback only for domestic; no code or kind on update)", () => {
    const f = { ...emptyDetailsForm(), code: " dhaka_x ", nameEn: " Dhaka X ", nameBn: " ", kind: "International" as const, isFallback: true };
    expect(detailsToCreateRequest(f)).toMatchObject({ code: "DHAKA_X", nameEn: "Dhaka X", nameBn: null, isFallback: false, daysMin: 1, daysMax: 3 });
    expect(Object.keys(detailsToUpdateRequest(f))).not.toContain("code");
    expect(Object.keys(detailsToUpdateRequest(f))).not.toContain("kind");
  });
});

describe("area selection", () => {
  const d1 = "d1";
  const d2 = "d2";
  const d3 = "d3";
  it("selects and clears districts in bulk and keeps remote flags", () => {
    let draft = setDistricts(draftFromAreas([]), [d1, d2], true);
    expect(groupState(draft, [d1, d2, d3])).toBe("some");
    draft = setRemote(draft, d1, null, true);
    draft = setDistricts(draft, [d1, d2, d3], true);
    expect(draft.get(areaKey(d1, null))?.isRemote).toBe(true);
    expect(groupState(draft, [d1, d2, d3])).toBe("all");
    draft = setDistricts(draft, [d1, d2, d3], false);
    expect(groupState(draft, [d1, d2, d3])).toBe("none");
    expect(groupState(draft, [])).toBe("none");
  });
  it("toggles single upazilas and writes sorted areas", () => {
    let draft = toggleUpazila(draftFromAreas([]), d1, "u1", true);
    draft = toggleUpazila(draft, d1, "u1", true);
    expect(draftToAreas(draft)).toEqual([{ districtId: d1, upazilaId: "u1", isRemote: false }]);
    draft = toggleUpazila(draft, d1, "u1", false);
    expect(draftToAreas(draft)).toEqual([]);
  });
  it("compares drafts including the remote flag", () => {
    const a = draftFromAreas([{ districtId: d1, upazilaId: null, isRemote: false }]);
    expect(sameAreas(a, draftFromAreas([{ districtId: d1, upazilaId: null, isRemote: false }]))).toBe(true);
    expect(sameAreas(a, setRemote(a, d1, null, true))).toBe(false);
  });
  it("finds the zones that lose areas when this zone saves", () => {
    const zones = [
      { id: "z1", code: "A", nameEn: "Alpha", kind: "Domestic" as const, areas: [{ districtId: d1, upazilaId: null, isRemote: false }, { districtId: d2, upazilaId: null, isRemote: false }] },
      { id: "z2", code: "B", nameEn: "Beta", kind: "Domestic" as const, areas: [{ districtId: d3, upazilaId: null, isRemote: false }] },
      { id: "z3", code: "I", nameEn: "Intl", kind: "International" as const, areas: [] },
    ];
    const owners = ownersOf(zones, "z2");
    expect(owners.has(areaKey(d3, null))).toBe(false);
    const draft = setDistricts(draftFromAreas([]), [d1, d2, d3], true);
    expect(movesFromOthers(draft, owners)).toEqual([{ zoneId: "z1", zoneName: "Alpha", count: 2 }]);
  });
  it("searches names in English and Bengali", () => {
    expect(matchesName({ nameEn: "Dhaka", nameBn: "ঢাকা" }, "dha")).toBe(true);
    expect(matchesName({ nameEn: "Dhaka", nameBn: "ঢাকা" }, "ঢা")).toBe(true);
    expect(matchesName({ nameEn: "Dhaka", nameBn: null }, "x")).toBe(false);
    expect(matchesName({ nameEn: "Dhaka", nameBn: null }, "  ")).toBe(true);
  });
});

describe("quote", () => {
  it("validates the address and the numbers", () => {
    expect(validateQuote({ countryCode: "BD", districtId: "", upazilaId: "", orderValue: "", weightKg: "" }, true).districtId).toBe("districtRequired");
    expect(validateQuote({ countryCode: "", districtId: "", upazilaId: "", orderValue: "", weightKg: "" }, false).countryCode).toBe("countryRequired");
    expect(validateQuote({ countryCode: "BD", districtId: "d", upazilaId: "", orderValue: "1.234", weightKg: "x" }, true)).toEqual({ orderValue: "valueInvalid", weightKg: "weightInvalid" });
    expect(validateQuote({ countryCode: "BD", districtId: "d", upazilaId: "", orderValue: "500", weightKg: "0" }, true)).toEqual({});
  });
  it("builds the request: subtotal and weight in grams, BD by district", () => {
    expect(quoteToRequest({ countryCode: "IN", districtId: "d", upazilaId: "u", orderValue: "1,200", weightKg: "1.5" }, true)).toEqual({ countryCode: "BD", districtId: "d", upazilaId: "u", items: null, subtotal: 1200, weightGrams: 1500 });
    expect(quoteToRequest({ countryCode: "IN", districtId: "d", upazilaId: "", orderValue: "", weightKg: "" }, false)).toEqual({ countryCode: "IN", districtId: null, upazilaId: null, items: null, subtotal: 0, weightGrams: 0 });
  });
  it("lists the breakdown lines and checks they add up", () => {
    const q = { baseFee: 70, weightFee: 20, remoteSurcharge: 50, freeDeliveryWaiver: 90, total: 50, isFree: true, amountToFreeDelivery: null };
    expect(quoteRows(q).map((r) => [r.id, r.amount])).toEqual([["base", 70], ["weight", 20], ["remote", 50], ["waiver", -90], ["total", 50]]);
    expect(quoteAddsUp(q)).toBe(true);
    expect(quoteAddsUp({ ...q, total: 51 })).toBe(false);
    expect(quoteRows({ ...q, freeDeliveryWaiver: 0, total: 140 }).some((r) => r.id === "waiver")).toBe(false);
  });
});

describe("countries", () => {
  it("needs a code, a name, a phone code and a zone for an enabled country other than Bangladesh", () => {
    expect(validateCountry({ code: "x", nameEn: "", nameBn: "", phoneCode: "", isEnabled: true, zoneId: "" }, true)).toEqual({ code: "codeInvalid", nameEn: "nameRequired", phoneCode: "phoneInvalid", zoneId: "zoneRequired" });
    expect(validateCountry({ code: "NP", nameEn: "Nepal", nameBn: "", phoneCode: "+977", isEnabled: false, zoneId: "" }, true)).toEqual({});
    expect(validateCountry({ code: "BD", nameEn: "Bangladesh", nameBn: "", phoneCode: "880", isEnabled: true, zoneId: "" }, false)).toEqual({});
  });
  it("keeps Bangladesh enabled and without a zone", () => {
    expect(countryToRequest({ code: "bd", nameEn: "Bangladesh", nameBn: "", phoneCode: "+880", isEnabled: false, zoneId: "z" })).toEqual({ code: "BD", nameEn: "Bangladesh", nameBn: null, phoneCode: "880", isEnabled: true, zoneId: null });
  });
});

describe("upazila paste import", () => {
  const districts = [{ id: "d1", nameEn: "Dhaka", nameBn: "ঢাকা" }, { id: "d2", nameEn: "Gazipur", nameBn: null }];
  it("splits commas, tabs, semicolons and quoted fields", () => {
    expect(splitLine("Dhaka, Savar ,সাভার")).toEqual(["Dhaka", "Savar", "সাভার"]);
    expect(splitLine("Dhaka\tSavar")).toEqual(["Dhaka", "Savar"]);
    expect(splitLine("Dhaka;Savar")).toEqual(["Dhaka", "Savar"]);
    expect(splitLine('Dhaka,"Savar, North"')).toEqual(["Dhaka", "Savar, North"]);
  });
  it("looks districts up by English or Bengali name and reports a reason for each bad row", () => {
    const rows = parseUpazilaImport("district,name,bangla\nDhaka,Savar,সাভার\nঢাকা,Keraniganj\nNowhere,X\n,Y\nGazipur,\nGazipur,Kapasia\ngazipur,KAPASIA\nDhaka,A,B,C", districts);
    expect(rows.map((r) => r.error)).toEqual([null, null, "unknownDistrict", "noDistrict", "noName", null, "duplicate", "tooManyColumns"]);
    expect(rows[1]!.districtId).toBe("d1");
    expect(rows[0]!.line).toBe(2);
  });
  it("summarises and builds the request from valid rows only", () => {
    const rows = parseUpazilaImport("Dhaka,Savar,সাভার\nNowhere,X", districts);
    expect(importSummary(rows)).toEqual({ total: 2, valid: 1, invalid: 1, tooMany: false });
    expect(importToRequest(rows)).toEqual({ items: [{ districtId: "d1", nameEn: "Savar", nameBn: "সাভার" }] });
    const many = parseUpazilaImport(Array.from({ length: 1001 }, (_, i) => `Dhaka,U${i}`).join("\n"), districts);
    expect(importSummary(many).tooMany).toBe(true);
  });
  it("flags names over 100 characters", () => {
    expect(parseUpazilaImport(`Dhaka,${"x".repeat(101)}`, districts)[0]!.error).toBe("nameTooLong");
  });
});
