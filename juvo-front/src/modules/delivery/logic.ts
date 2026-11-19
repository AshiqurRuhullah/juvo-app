/**
 * Pure logic of the delivery zone screens (module G): money and weight parsing, slab checks, form <-> request mapping, area selection,
 * quote breakdown rows, country form and the upazila paste import. No React here, so every rule is unit tested (tests/delivery.test.ts).
 *
 * Charge model (backend DeliveryPricing, BR-G2): charge = base fee + weight fee + remote surcharge. The weight fee is the fee of the first
 * slab whose limit holds the parcel; beyond the last slab it is that slab's fee plus the per-extra-kg fee for each started kilogram.
 * Reaching the free-delivery threshold waives base and weight fees, never the remote surcharge.
 */

export const MAX_FEE = 1_000_000;
export const MAX_SLABS = 20;
export const MAX_IMPORT = 1000;
export const MAX_NAME = 100;
export const MAX_DAYS = 90;

export type ZoneKind = "Domestic" | "International";

// ---- money and weight -----------------------------------------------------------------------------------------

const BN_DIGITS = "০১২৩৪৫৬৭৮৯";

/** Latin digits for text typed with Bengali digits; commas and spaces removed. */
export function normaliseNumberText(text: string): string {
  return text
    .replace(/[০-৯]/g, (d) => String(BN_DIGITS.indexOf(d)))
    .replace(/[\s,]/g, "")
    .replace(/৳|tk|bdt/gi, "");
}

export type MoneyError = "required" | "invalid" | "negative" | "decimals" | "tooBig";

/** Money typed by a person: "1,250.5" -> 1250.5. At most two decimals, never negative, never above MAX_FEE. Empty is `required`. */
export function parseMoney(text: string, max = MAX_FEE): { ok: true; value: number } | { ok: false; error: MoneyError } {
  const t = normaliseNumberText(text.trim());
  if (t === "") return { ok: false, error: "required" };
  if (t.startsWith("-")) return { ok: false, error: /^-\d*\.?\d+$/.test(t) ? "negative" : "invalid" };
  if (!/^\d+(\.\d*)?$|^\.\d+$/.test(t)) return { ok: false, error: "invalid" };
  const [, frac = ""] = t.split(".");
  if (frac.length > 2) return { ok: false, error: "decimals" };
  const cents = Math.round(Number(t) * 100);
  if (!Number.isFinite(cents) || cents > max * 100) return { ok: false, error: "tooBig" };
  return { ok: true, value: cents / 100 };
}

/** Money for an input box: whole amounts without decimals, others with two ("70", "70.50"). */
export function moneyToText(value: number | string | null | undefined): string {
  if (value === null || value === undefined || value === "") return "";
  const n = Number(value);
  if (!Number.isFinite(n)) return "";
  return Number.isInteger(n) ? String(n) : n.toFixed(2);
}

export type KgError = "required" | "invalid" | "decimals" | "notPositive" | "tooBig";

/** Weight typed in kilograms ("1.5" -> 1500 g). Up to three decimals (whole grams), must be above zero. */
export function parseKgToGrams(text: string, maxGrams = 1_000_000): { ok: true; grams: number } | { ok: false; error: KgError } {
  const t = normaliseNumberText(text.trim());
  if (t === "") return { ok: false, error: "required" };
  if (!/^\d+(\.\d*)?$|^\.\d+$/.test(t)) return { ok: false, error: "invalid" };
  const [, frac = ""] = t.split(".");
  if (frac.length > 3) return { ok: false, error: "decimals" };
  const grams = Math.round(Number(t) * 1000);
  if (grams <= 0) return { ok: false, error: "notPositive" };
  if (grams > maxGrams) return { ok: false, error: "tooBig" };
  return { ok: true, grams };
}

/** 1500 -> "1.5", 20000 -> "20", 250 -> "0.25". */
export function gramsToKgText(grams: number | string | null | undefined): string {
  if (grams === null || grams === undefined || grams === "") return "";
  const g = Number(grams);
  if (!Number.isFinite(g)) return "";
  return String(Math.round(g) / 1000);
}

/** "1 kg", "1.5 kg", "250 g" for display. */
export function formatWeight(grams: number): string {
  if (grams < 1000) return `${grams} g`;
  return `${Math.round(grams) / 1000} kg`;
}

export function parseIntText(text: string): number | null {
  const t = normaliseNumberText(text.trim());
  return /^\d+$/.test(t) ? Number(t) : null;
}

// ---- rates form ------------------------------------------------------------------------------------------------

export type SlabRow = { upToKg: string; extraFee: string };

export type RatesForm = {
  baseFee: string;
  freeThreshold: string;
  remoteSurcharge: string;
  perExtraKgFee: string;
  maxWeightKg: string;
  slabs: SlabRow[];
};

export type RatesErrorKey =
  | "required" | "invalid" | "negative" | "decimals" | "tooBig"
  | "kgRequired" | "kgInvalid" | "kgDecimals" | "kgNotPositive" | "kgTooBig"
  | "duplicateSlab" | "tooManySlabs";

export type RatesErrors = {
  baseFee?: RatesErrorKey;
  freeThreshold?: RatesErrorKey;
  remoteSurcharge?: RatesErrorKey;
  perExtraKgFee?: RatesErrorKey;
  maxWeightKg?: RatesErrorKey;
  slabs?: RatesErrorKey;
  slabRows: Record<number, { upToKg?: RatesErrorKey; extraFee?: RatesErrorKey }>;
};

/** A hint that does not stop saving but is probably a mistake. */
export type RatesWarning =
  | { code: "perKgIgnored" }
  | { code: "feeDrops"; row: number }
  | { code: "unreachableSlab"; row: number }
  | { code: "freeBelowBase" }
  | { code: "noSlabsNote" };

const MONEY_KEY: Record<MoneyError, RatesErrorKey> = { required: "required", invalid: "invalid", negative: "negative", decimals: "decimals", tooBig: "tooBig" };
const KG_KEY: Record<KgError, RatesErrorKey> = { required: "kgRequired", invalid: "kgInvalid", decimals: "kgDecimals", notPositive: "kgNotPositive", tooBig: "kgTooBig" };

export function emptyRatesForm(): RatesForm {
  return { baseFee: "", freeThreshold: "", remoteSurcharge: "0", perExtraKgFee: "0", maxWeightKg: "", slabs: [] };
}

type ZoneRatesSource = {
  baseFee: number | string;
  freeDeliveryThreshold: number | string | null;
  remoteSurcharge: number | string;
  perExtraKgFee: number | string;
  maxWeightGrams: number | string | null;
  slabs: readonly { upToGrams: number | string; extraFee: number | string }[];
};

export function ratesToForm(zone: ZoneRatesSource): RatesForm {
  return {
    baseFee: moneyToText(zone.baseFee),
    freeThreshold: moneyToText(zone.freeDeliveryThreshold),
    remoteSurcharge: moneyToText(zone.remoteSurcharge),
    perExtraKgFee: moneyToText(zone.perExtraKgFee),
    maxWeightKg: gramsToKgText(zone.maxWeightGrams),
    slabs: [...zone.slabs].sort((a, b) => Number(a.upToGrams) - Number(b.upToGrams)).map((s) => ({ upToKg: gramsToKgText(s.upToGrams), extraFee: moneyToText(s.extraFee) })),
  };
}

export type RatesRequest = { baseFee: number; freeDeliveryThreshold: number | null; remoteSurcharge: number; perExtraKgFee: number; maxWeightGrams: number | null; slabs: { upToGrams: number; extraFee: number }[] };

/** Checks every field and slab. Returns the errors (empty when fine). Slabs are limits only, so they cannot overlap or leave gaps; what can go wrong is a repeated limit, a limit of zero, a fee that is not money, or a heavier slab that is cheaper. */
export function validateRates(form: RatesForm): RatesErrors {
  const errors: RatesErrors = { slabRows: {} };
  const money = (text: string, key: "baseFee" | "remoteSurcharge" | "perExtraKgFee") => {
    const r = parseMoney(text);
    if (!r.ok) errors[key] = MONEY_KEY[r.error];
  };
  money(form.baseFee, "baseFee");
  money(form.remoteSurcharge, "remoteSurcharge");
  money(form.perExtraKgFee, "perExtraKgFee");
  if (form.freeThreshold.trim() !== "") {
    const r = parseMoney(form.freeThreshold);
    if (!r.ok) errors.freeThreshold = MONEY_KEY[r.error];
  }
  if (form.maxWeightKg.trim() !== "") {
    const r = parseKgToGrams(form.maxWeightKg);
    if (!r.ok) errors.maxWeightKg = KG_KEY[r.error];
  }
  if (form.slabs.length > MAX_SLABS) errors.slabs = "tooManySlabs";
  const seen = new Map<number, number>();
  form.slabs.forEach((row, i) => {
    const rowErr: { upToKg?: RatesErrorKey; extraFee?: RatesErrorKey } = {};
    const kg = parseKgToGrams(row.upToKg);
    if (!kg.ok) rowErr.upToKg = KG_KEY[kg.error];
    else if (seen.has(kg.grams)) rowErr.upToKg = "duplicateSlab";
    else seen.set(kg.grams, i);
    const fee = parseMoney(row.extraFee);
    if (!fee.ok) rowErr.extraFee = MONEY_KEY[fee.error];
    if (rowErr.upToKg || rowErr.extraFee) errors.slabRows[i] = rowErr;
  });
  return errors;
}

export function hasRatesErrors(e: RatesErrors): boolean {
  return !!(e.baseFee || e.freeThreshold || e.remoteSurcharge || e.perExtraKgFee || e.maxWeightKg || e.slabs || Object.keys(e.slabRows).length);
}

/** Slab rows with their numbers, sorted by weight (only rows that parse). `row` is the index in the form. */
export function parsedSlabs(form: RatesForm): { row: number; upToGrams: number; extraFee: number }[] {
  const out: { row: number; upToGrams: number; extraFee: number }[] = [];
  form.slabs.forEach((s, row) => {
    const kg = parseKgToGrams(s.upToKg);
    const fee = parseMoney(s.extraFee);
    if (kg.ok && fee.ok) out.push({ row, upToGrams: kg.grams, extraFee: fee.value });
  });
  return out.sort((a, b) => a.upToGrams - b.upToGrams);
}

/** Probable mistakes that the API accepts. Shown as notes, never block saving. */
export function ratesWarnings(form: RatesForm): RatesWarning[] {
  const out: RatesWarning[] = [];
  const slabs = parsedSlabs(form);
  const perKg = parseMoney(form.perExtraKgFee);
  if (slabs.length === 0 && perKg.ok && perKg.value > 0) out.push({ code: "perKgIgnored" });
  for (let i = 1; i < slabs.length; i++) if (slabs[i]!.extraFee < slabs[i - 1]!.extraFee) out.push({ code: "feeDrops", row: slabs[i]!.row });
  const max = form.maxWeightKg.trim() === "" ? null : parseKgToGrams(form.maxWeightKg);
  if (max?.ok) for (const s of slabs) if (s.upToGrams > max.grams) out.push({ code: "unreachableSlab", row: s.row });
  const base = parseMoney(form.baseFee);
  const free = form.freeThreshold.trim() === "" ? null : parseMoney(form.freeThreshold);
  if (free?.ok && base.ok && free.value === 0) out.push({ code: "freeBelowBase" });
  return out;
}

/** The request for PUT /rates. Call only when validateRates found nothing. Slabs are sent sorted by weight. */
export function ratesToRequest(form: RatesForm): RatesRequest {
  const num = (t: string) => {
    const r = parseMoney(t);
    return r.ok ? r.value : 0;
  };
  const max = form.maxWeightKg.trim() === "" ? null : parseKgToGrams(form.maxWeightKg);
  return {
    baseFee: num(form.baseFee),
    freeDeliveryThreshold: form.freeThreshold.trim() === "" ? null : num(form.freeThreshold),
    remoteSurcharge: num(form.remoteSurcharge),
    perExtraKgFee: num(form.perExtraKgFee),
    maxWeightGrams: max?.ok ? max.grams : null,
    slabs: parsedSlabs(form).map((s) => ({ upToGrams: s.upToGrams, extraFee: s.extraFee })),
  };
}

export function sameRates(a: RatesForm, b: RatesForm): boolean {
  return JSON.stringify(ratesToRequest(a)) === JSON.stringify(ratesToRequest(b)) && a.slabs.length === b.slabs.length;
}

/** The weight ranges the slabs make, in words for the table under the form: "up to 1 kg: +৳0", "above 2 kg: last fee + per kg". */
export type SlabRange = { fromGrams: number; toGrams: number | null; fee: number | null; beyond: boolean };
export function slabRanges(form: RatesForm): SlabRange[] {
  const slabs = parsedSlabs(form);
  const out: SlabRange[] = [];
  let from = 0;
  for (const s of slabs) {
    out.push({ fromGrams: from, toGrams: s.upToGrams, fee: s.extraFee, beyond: false });
    from = s.upToGrams;
  }
  if (slabs.length > 0) out.push({ fromGrams: from, toGrams: null, fee: null, beyond: true });
  return out;
}

// ---- zone details form -----------------------------------------------------------------------------------------

export type DetailsForm = {
  code: string;
  nameEn: string;
  nameBn: string;
  kind: ZoneKind;
  isActive: boolean;
  isFallback: boolean;
  sortOrder: string;
  daysMin: string;
  daysMax: string;
};

export type DetailsErrorKey = "codeInvalid" | "nameRequired" | "nameTooLong" | "nameBnTooLong" | "sortInvalid" | "daysInvalid" | "daysOrder" | "daysTooBig";
export type DetailsErrors = Partial<Record<"code" | "nameEn" | "nameBn" | "sortOrder" | "daysMin" | "daysMax", DetailsErrorKey>>;

export function emptyDetailsForm(): DetailsForm {
  return { code: "", nameEn: "", nameBn: "", kind: "Domestic", isActive: true, isFallback: false, sortOrder: "0", daysMin: "1", daysMax: "3" };
}

export function detailsToForm(z: { code: string; nameEn: string; nameBn: string | null; kind: ZoneKind; isActive: boolean; isFallback: boolean; sortOrder: number | string; daysMin: number | string; daysMax: number | string }): DetailsForm {
  return { code: z.code, nameEn: z.nameEn, nameBn: z.nameBn ?? "", kind: z.kind, isActive: z.isActive, isFallback: z.isFallback, sortOrder: String(z.sortOrder), daysMin: String(z.daysMin), daysMax: String(z.daysMax) };
}

export const ZONE_CODE = /^[A-Za-z0-9_-]{2,30}$/;

export function validateDetails(form: DetailsForm, isNew: boolean): DetailsErrors {
  const e: DetailsErrors = {};
  if (isNew && !ZONE_CODE.test(form.code.trim())) e.code = "codeInvalid";
  if (form.nameEn.trim() === "") e.nameEn = "nameRequired";
  else if (form.nameEn.trim().length > MAX_NAME) e.nameEn = "nameTooLong";
  if (form.nameBn.trim().length > MAX_NAME) e.nameBn = "nameBnTooLong";
  if (parseSigned(form.sortOrder) === null) e.sortOrder = "sortInvalid";
  const min = parseIntText(form.daysMin);
  const max = parseIntText(form.daysMax);
  if (min === null) e.daysMin = "daysInvalid";
  if (max === null) e.daysMax = "daysInvalid";
  if (min !== null && max !== null) {
    if (max < min) e.daysMax = "daysOrder";
    else if (max > MAX_DAYS) e.daysMax = "daysTooBig";
  }
  return e;
}

function parseSigned(text: string): number | null {
  const t = normaliseNumberText(text.trim());
  return /^-?\d{1,6}$/.test(t) ? Number(t) : null;
}

export function detailsToCreateRequest(f: DetailsForm) {
  return {
    code: f.code.trim().toUpperCase(),
    nameEn: f.nameEn.trim(),
    nameBn: f.nameBn.trim() === "" ? null : f.nameBn.trim(),
    kind: f.kind,
    isActive: f.isActive,
    isFallback: f.kind === "Domestic" && f.isFallback,
    sortOrder: parseSigned(f.sortOrder) ?? 0,
    daysMin: parseIntText(f.daysMin) ?? 1,
    daysMax: parseIntText(f.daysMax) ?? 3,
  };
}

export function detailsToUpdateRequest(f: DetailsForm) {
  const { code: _code, kind: _kind, ...rest } = detailsToCreateRequest(f);
  void _code;
  void _kind;
  return rest;
}

// ---- areas -----------------------------------------------------------------------------------------------------

export type Area = { districtId: string; upazilaId: string | null; isRemote: boolean };

/** Key of one selectable area: a whole district or one upazila. */
export const areaKey = (districtId: string, upazilaId: string | null) => (upazilaId ? `u:${upazilaId}` : `d:${districtId}`);

export type AreaDraft = Map<string, Area>;

export function draftFromAreas(areas: readonly Area[]): AreaDraft {
  return new Map(areas.map((a) => [areaKey(a.districtId, a.upazilaId), { ...a }]));
}

export function draftToAreas(draft: AreaDraft): Area[] {
  return [...draft.values()].sort((a, b) => (a.districtId === b.districtId ? (a.upazilaId ?? "").localeCompare(b.upazilaId ?? "") : a.districtId.localeCompare(b.districtId)));
}

/** Add or remove areas in bulk (a division, the districts found by a search). Existing choices keep their remote flag. */
export function setDistricts(draft: AreaDraft, districtIds: readonly string[], on: boolean): AreaDraft {
  const next = new Map(draft);
  for (const id of districtIds) {
    const key = areaKey(id, null);
    if (on) {
      if (!next.has(key)) next.set(key, { districtId: id, upazilaId: null, isRemote: false });
    } else next.delete(key);
  }
  return next;
}

export function toggleUpazila(draft: AreaDraft, districtId: string, upazilaId: string, on: boolean): AreaDraft {
  const next = new Map(draft);
  const key = areaKey(districtId, upazilaId);
  if (on) next.set(key, next.get(key) ?? { districtId, upazilaId, isRemote: false });
  else next.delete(key);
  return next;
}

export function setRemote(draft: AreaDraft, districtId: string, upazilaId: string | null, remote: boolean): AreaDraft {
  const key = areaKey(districtId, upazilaId);
  const cur = draft.get(key);
  if (!cur) return draft;
  const next = new Map(draft);
  next.set(key, { ...cur, isRemote: remote });
  return next;
}

export type SelectState = "none" | "some" | "all";

/** Checkbox state of a group of districts: none, some or all of them in the draft as whole districts. */
export function groupState(draft: AreaDraft, districtIds: readonly string[]): SelectState {
  if (districtIds.length === 0) return "none";
  const n = districtIds.filter((id) => draft.has(areaKey(id, null))).length;
  return n === 0 ? "none" : n === districtIds.length ? "all" : "some";
}

/** Who holds a district or upazila now: zone id by area key, from every zone (the API list). */
export type Owners = Map<string, { zoneId: string; zoneName: string; zoneCode: string }>;

export function ownersOf(zones: readonly { id: string; code: string; nameEn: string; kind: ZoneKind; areas: readonly Area[] }[], exceptZoneId?: string): Owners {
  const map: Owners = new Map();
  for (const z of zones) {
    if (z.id === exceptZoneId || z.kind !== "Domestic") continue;
    for (const a of z.areas) map.set(areaKey(a.districtId, a.upazilaId), { zoneId: z.id, zoneName: z.nameEn, zoneCode: z.code });
  }
  return map;
}

/** Areas of the draft that another zone holds now: saving moves them here. Grouped by the zone that loses them. */
export function movesFromOthers(draft: AreaDraft, owners: Owners): { zoneId: string; zoneName: string; count: number }[] {
  const by = new Map<string, { zoneId: string; zoneName: string; count: number }>();
  for (const key of draft.keys()) {
    const o = owners.get(key);
    if (!o) continue;
    const cur = by.get(o.zoneId) ?? { zoneId: o.zoneId, zoneName: o.zoneName, count: 0 };
    cur.count += 1;
    by.set(o.zoneId, cur);
  }
  return [...by.values()].sort((a, b) => a.zoneName.localeCompare(b.zoneName));
}

export function sameAreas(a: AreaDraft, b: AreaDraft): boolean {
  if (a.size !== b.size) return false;
  for (const [k, v] of a) {
    const w = b.get(k);
    if (!w || w.isRemote !== v.isRemote) return false;
  }
  return true;
}

/** Case-insensitive match on English or Bengali name. */
export function matchesName(item: { nameEn: string; nameBn: string | null }, query: string): boolean {
  const q = query.trim().toLowerCase();
  return q === "" || item.nameEn.toLowerCase().includes(q) || (item.nameBn ?? "").toLowerCase().includes(q);
}

// ---- quote -----------------------------------------------------------------------------------------------------

export type QuoteForm = { countryCode: string; districtId: string; upazilaId: string; orderValue: string; weightKg: string };

export type QuoteErrorKey = "districtRequired" | "countryRequired" | "valueInvalid" | "weightInvalid";

export function validateQuote(f: QuoteForm, domestic: boolean): Partial<Record<"districtId" | "countryCode" | "orderValue" | "weightKg", QuoteErrorKey>> {
  const e: Partial<Record<"districtId" | "countryCode" | "orderValue" | "weightKg", QuoteErrorKey>> = {};
  if (domestic && !f.districtId) e.districtId = "districtRequired";
  if (!domestic && !f.countryCode) e.countryCode = "countryRequired";
  if (f.orderValue.trim() !== "" && !parseMoney(f.orderValue, 100_000_000).ok) e.orderValue = "valueInvalid";
  if (f.weightKg.trim() !== "") {
    const w = f.weightKg.trim() === "0" ? { ok: true as const } : parseKgToGrams(f.weightKg, 1_000_000_000);
    if (!w.ok) e.weightKg = "weightInvalid";
  }
  return e;
}

export function quoteToRequest(f: QuoteForm, domestic: boolean) {
  const value = f.orderValue.trim() === "" ? 0 : (parseMoney(f.orderValue, 100_000_000) as { ok: true; value: number }).value;
  const kg = f.weightKg.trim() === "" || f.weightKg.trim() === "0" ? null : parseKgToGrams(f.weightKg, 1_000_000_000);
  return {
    countryCode: domestic ? "BD" : f.countryCode,
    districtId: domestic ? f.districtId : null,
    upazilaId: domestic && f.upazilaId ? f.upazilaId : null,
    items: null,
    subtotal: value,
    weightGrams: kg?.ok ? kg.grams : 0,
  };
}

export type QuoteNumbers = {
  baseFee: number | string;
  weightFee: number | string;
  remoteSurcharge: number | string;
  freeDeliveryWaiver: number | string;
  total: number | string;
  isFree: boolean;
  amountToFreeDelivery: number | string | null;
};

export type QuoteRow = { id: "base" | "weight" | "remote" | "waiver" | "total"; amount: number };

/** The lines the customer sees, in order. The waiver is a negative line; the total equals base + weight + remote - waiver (BR-G2). */
export function quoteRows(q: QuoteNumbers): QuoteRow[] {
  const n = (v: number | string) => Number(v);
  const rows: QuoteRow[] = [
    { id: "base", amount: n(q.baseFee) },
    { id: "weight", amount: n(q.weightFee) },
    { id: "remote", amount: n(q.remoteSurcharge) },
  ];
  if (n(q.freeDeliveryWaiver) > 0) rows.push({ id: "waiver", amount: -n(q.freeDeliveryWaiver) });
  rows.push({ id: "total", amount: n(q.total) });
  return rows;
}

/** True when the lines add up to the total (a self-check shown to the owner). */
export function quoteAddsUp(q: QuoteNumbers): boolean {
  const n = (v: number | string) => Number(v);
  return Math.abs(n(q.baseFee) + n(q.weightFee) + n(q.remoteSurcharge) - n(q.freeDeliveryWaiver) - n(q.total)) < 0.005;
}

/** What the rates should produce for a parcel (mirror of the backend routine), so the screen can show "expected" next to "quoted". */
export function expectedCharge(rates: RatesRequest, subtotal: number, weightGrams: number, remote: boolean): { baseFee: number; weightFee: number; remote: number; waiver: number; total: number } | null {
  if (rates.maxWeightGrams !== null && weightGrams > rates.maxWeightGrams) return null;
  let weightFee = 0;
  if (rates.slabs.length > 0 && weightGrams > 0) {
    const hit = rates.slabs.find((s) => weightGrams <= s.upToGrams);
    if (hit) weightFee = hit.extraFee;
    else {
      const last = rates.slabs[rates.slabs.length - 1]!;
      weightFee = last.extraFee + Math.ceil((weightGrams - last.upToGrams) / 1000) * rates.perExtraKgFee;
    }
  }
  const free = rates.freeDeliveryThreshold !== null && subtotal >= rates.freeDeliveryThreshold;
  const waiver = free ? rates.baseFee + weightFee : 0;
  const rem = remote ? rates.remoteSurcharge : 0;
  const r2 = (x: number) => Math.round(x * 100) / 100;
  return { baseFee: r2(rates.baseFee), weightFee: r2(weightFee), remote: r2(rem), waiver: r2(waiver), total: r2(rates.baseFee + weightFee + rem - waiver) };
}

// ---- countries -------------------------------------------------------------------------------------------------

export type CountryForm = { code: string; nameEn: string; nameBn: string; phoneCode: string; isEnabled: boolean; zoneId: string };
export type CountryErrorKey = "codeInvalid" | "nameRequired" | "phoneInvalid" | "zoneRequired";
export type CountryErrors = Partial<Record<"code" | "nameEn" | "phoneCode" | "zoneId", CountryErrorKey>>;

export function emptyCountryForm(): CountryForm {
  return { code: "", nameEn: "", nameBn: "", phoneCode: "", isEnabled: false, zoneId: "" };
}

export function countryToForm(c: { code: string; nameEn: string; nameBn: string | null; phoneCode: string; isEnabled: boolean; zoneId: string | null }): CountryForm {
  return { code: c.code, nameEn: c.nameEn, nameBn: c.nameBn ?? "", phoneCode: c.phoneCode, isEnabled: c.isEnabled, zoneId: c.zoneId ?? "" };
}

export function validateCountry(f: CountryForm, isNew: boolean): CountryErrors {
  const e: CountryErrors = {};
  if (isNew && !/^[A-Za-z]{2}$/.test(f.code.trim())) e.code = "codeInvalid";
  if (f.nameEn.trim() === "") e.nameEn = "nameRequired";
  if (!/^\d{1,8}$/.test(normaliseNumberText(f.phoneCode.trim().replace(/^\+/, "")))) e.phoneCode = "phoneInvalid";
  if (f.code.trim().toUpperCase() !== "BD" && f.isEnabled && !f.zoneId) e.zoneId = "zoneRequired";
  return e;
}

export function countryToRequest(f: CountryForm) {
  const bd = f.code.trim().toUpperCase() === "BD";
  return {
    code: f.code.trim().toUpperCase(),
    nameEn: f.nameEn.trim(),
    nameBn: f.nameBn.trim() === "" ? null : f.nameBn.trim(),
    phoneCode: normaliseNumberText(f.phoneCode.trim().replace(/^\+/, "")),
    isEnabled: bd ? true : f.isEnabled,
    zoneId: bd || f.zoneId === "" ? null : f.zoneId,
  };
}

// ---- upazila paste import --------------------------------------------------------------------------------------

export type ImportDistrict = { id: string; nameEn: string; nameBn: string | null };
export type ImportRow = { line: number; districtText: string; nameEn: string; nameBn: string; districtId: string | null; error: "noDistrict" | "unknownDistrict" | "noName" | "nameTooLong" | "duplicate" | "tooManyColumns" | null };

/** Splits one pasted line on tab, comma or semicolon (a quoted field may hold a comma). */
export function splitLine(line: string): string[] {
  const sep = line.includes("\t") ? "\t" : line.includes(";") && !line.includes(",") ? ";" : ",";
  const out: string[] = [];
  let cur = "";
  let quoted = false;
  for (let i = 0; i < line.length; i++) {
    const ch = line[i]!;
    if (ch === '"') {
      if (quoted && line[i + 1] === '"') {
        cur += '"';
        i++;
      } else quoted = !quoted;
    } else if (ch === sep && !quoted) {
      out.push(cur.trim());
      cur = "";
    } else cur += ch;
  }
  out.push(cur.trim());
  return out;
}

/** Pasted lines `district, English name, Bengali name (optional)` -> rows with the district looked up by English or Bengali name. A first line that names the columns is skipped. */
export function parseUpazilaImport(text: string, districts: readonly ImportDistrict[]): ImportRow[] {
  const lookup = new Map<string, string>();
  for (const d of districts) {
    lookup.set(d.nameEn.trim().toLowerCase(), d.id);
    if (d.nameBn) lookup.set(d.nameBn.trim().toLowerCase(), d.id);
  }
  const rows: ImportRow[] = [];
  const seen = new Set<string>();
  const lines = text.split(/\r?\n/);
  lines.forEach((raw, index) => {
    if (raw.trim() === "") return;
    const cols = splitLine(raw);
    if (index === 0 && /^district/i.test(cols[0] ?? "") && /name|upazila/i.test(cols[1] ?? "")) return;
    const [districtText = "", nameEn = "", nameBn = ""] = cols;
    const districtId = lookup.get(districtText.toLowerCase()) ?? null;
    let error: ImportRow["error"] = null;
    if (cols.length > 3) error = "tooManyColumns";
    else if (districtText === "") error = "noDistrict";
    else if (!districtId) error = "unknownDistrict";
    else if (nameEn === "") error = "noName";
    else if (nameEn.length > MAX_NAME || nameBn.length > MAX_NAME) error = "nameTooLong";
    else {
      const key = `${districtId}|${nameEn.toLowerCase()}`;
      if (seen.has(key)) error = "duplicate";
      seen.add(key);
    }
    rows.push({ line: index + 1, districtText, nameEn, nameBn, districtId, error });
  });
  return rows;
}

export function importSummary(rows: readonly ImportRow[]): { total: number; valid: number; invalid: number; tooMany: boolean } {
  const valid = rows.filter((r) => !r.error).length;
  return { total: rows.length, valid, invalid: rows.length - valid, tooMany: valid > MAX_IMPORT };
}

export function importToRequest(rows: readonly ImportRow[]) {
  return { items: rows.filter((r) => !r.error).map((r) => ({ districtId: r.districtId!, nameEn: r.nameEn, nameBn: r.nameBn === "" ? null : r.nameBn })) };
}

// ---- list ------------------------------------------------------------------------------------------------------

/** "BDT 70 + weight slabs" summary used on the list: base fee, plus whether a free threshold exists. */
export function zoneSummaryParts(z: { baseFee: number | string; freeDeliveryThreshold: number | string | null; slabs: readonly unknown[] }): { base: number; free: number | null; slabCount: number } {
  return { base: Number(z.baseFee), free: z.freeDeliveryThreshold === null ? null : Number(z.freeDeliveryThreshold), slabCount: z.slabs.length };
}

export type DeliveryRights = { view: boolean; create: boolean; edit: boolean; delete: boolean; rateEdit: boolean; countryView: boolean; countryEnable: boolean };
