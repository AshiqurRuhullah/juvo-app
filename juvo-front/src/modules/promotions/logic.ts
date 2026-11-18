import type { Schemas } from "@/lib/api/types";

/**
 * Pure rules of the promotions screens (module Z, docs/promotions-api.md and docs/pricing-engine.md): the form state, the mapping
 * to the API request, Dhaka time handling, which actions a status allows and the plain-language stacking explanation.
 * Nothing here touches React or the network, so it is unit-tested in tests/promotions.test.ts.
 */

export type PromotionType = Schemas["PromotionType"];
export type PromotionStatus = Schemas["PromotionStatus"];
export type StackMode = Schemas["StackMode"];
export type DiscountKind = NonNullable<Schemas["DiscountKind"]>;
export type TargetKind = Schemas["PromotionTargetKind"];
export type TargetRole = Schemas["TargetRole"];
export type BannerSlot = Schemas["BannerSlot"];
export type PromotionDetail = Schemas["PromotionDetailDto"];
export type SavePromotion = Schemas["SavePromotionRequest"];
export type Channel = "Web" | "SocialDesk" | "Pos";

export const PROMOTION_TYPES: PromotionType[] = ["FlashSale", "ProductDiscount", "Bundle", "BuyXGetY", "FreeDelivery", "FestivalCampaign"];
export const PROMOTION_STATUSES: PromotionStatus[] = ["Draft", "Scheduled", "Active", "Ended", "Archived"];
export const STACK_MODES: StackMode[] = ["StacksWithOrderPromotions", "StacksWithCoupon", "Exclusive"];
export const CHANNELS: Channel[] = ["Web", "SocialDesk", "Pos"];
export const DISCOUNT_KINDS: DiscountKind[] = ["Percent", "Fixed", "FixedPrice"];
export const BANNER_SLOTS: BannerSlot[] = ["HomeTop", "ShopTop", "Category"];
export const SLUG_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*$/;
export const PRIORITY_MIN = -1000;
export const PRIORITY_MAX = 1000;
export const BANNER_MIN_SIDE = 1000;
export const BANNER_MAX_BYTES = 10 * 1024 * 1024;

/* ---- Which fields belong to which type ------------------------------------------------------------------------------------ */

export type TypeRule = {
  /** Shows the discount kind, value and cap. `optional`: a campaign may carry a discount but does not need one. */
  discount: "required" | "optional" | false;
  endsRequired: boolean;
  bundle: boolean;
  buyXGetY: boolean;
  freeDelivery: boolean;
  campaign: boolean;
  /** Roles the targets can have, in the order the screen shows them. Empty: the type has no targets. */
  roles: TargetRole[];
  /** `Everything` is an allowed target (a product discount for the whole shop). */
  everything: boolean;
  /** A target is needed to save the draft (a campaign may be saved without a product set, but not switched on). */
  targetsRequired: boolean;
  /** Which stacking sentence applies: price types compete per line, order types combine per cart. */
  level: "line" | "order" | "delivery";
};

export const TYPE_RULES: Record<PromotionType, TypeRule> = {
  FlashSale: { discount: "required", endsRequired: true, bundle: false, buyXGetY: false, freeDelivery: false, campaign: false, roles: ["Main"], everything: true, targetsRequired: true, level: "line" },
  ProductDiscount: { discount: "required", endsRequired: false, bundle: false, buyXGetY: false, freeDelivery: false, campaign: false, roles: ["Main"], everything: true, targetsRequired: true, level: "line" },
  Bundle: { discount: false, endsRequired: false, bundle: true, buyXGetY: false, freeDelivery: false, campaign: false, roles: ["BundleItem"], everything: false, targetsRequired: true, level: "order" },
  BuyXGetY: { discount: false, endsRequired: false, bundle: false, buyXGetY: true, freeDelivery: false, campaign: false, roles: ["Buy", "Get"], everything: false, targetsRequired: true, level: "order" },
  FreeDelivery: { discount: false, endsRequired: false, bundle: false, buyXGetY: false, freeDelivery: true, campaign: false, roles: [], everything: false, targetsRequired: false, level: "delivery" },
  FestivalCampaign: { discount: "optional", endsRequired: false, bundle: false, buyXGetY: false, freeDelivery: false, campaign: true, roles: ["Main"], everything: false, targetsRequired: false, level: "line" },
};

/* ---- Dhaka time ------------------------------------------------------------------------------------------------------------ */

const DHAKA_MS = 6 * 3600_000;
const LOCAL = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2})$/;

/** `2026-12-01T10:00` typed as Dhaka time becomes `2026-12-01T10:00:00+06:00`; an empty or broken value becomes null. */
export function dhakaLocalToIso(local: string | null | undefined): string | null {
  if (!local || !LOCAL.test(local)) return null;
  return `${local}:00+06:00`;
}

/** The Dhaka wall-clock time (`YYYY-MM-DDTHH:mm`) of a moment the API sent, whatever offset it carries. Empty when there is none. */
export function isoToDhakaLocal(iso: string | null | undefined): string {
  if (!iso) return "";
  const ms = Date.parse(iso);
  if (!Number.isFinite(ms)) return "";
  return new Date(ms + DHAKA_MS).toISOString().slice(0, 16);
}

/** First or last moment of a Dhaka calendar day (`YYYY-MM-DD`), for the report's from and to. */
export function dhakaDayBound(day: string | undefined, end: boolean): string | undefined {
  if (!day || !/^\d{4}-\d{2}-\d{2}$/.test(day)) return undefined;
  return end ? `${day}T23:59:59+06:00` : `${day}T00:00:00+06:00`;
}

/** Whole days are not needed here; this says whether a window is in the right order (equal is allowed: a one-moment window). */
export function windowOrdered(startsLocal: string, endsLocal: string): boolean {
  if (!startsLocal || !endsLocal) return true;
  return Date.parse(`${startsLocal}:00+06:00`) <= Date.parse(`${endsLocal}:00+06:00`);
}

/* ---- Form state ------------------------------------------------------------------------------------------------------------ */

export type TargetRow = { kind: TargetKind; targetId: string | null; role: TargetRole; quantity: string; name: string };

export type PromotionForm = {
  nameEn: string;
  nameBn: string;
  type: PromotionType;
  startsAt: string;
  endsAt: string;
  priority: string;
  stackMode: StackMode;
  channels: Channel[];
  minCartValue: string;
  usageLimit: string;
  labelEn: string;
  labelBn: string;
  discountKind: DiscountKind;
  discountValue: string;
  maxDiscount: string;
  buyQuantity: string;
  getQuantity: string;
  getDiscountPercent: string;
  bundlePrice: string;
  zoneCodes: string;
  slug: string;
  titleEn: string;
  titleBn: string;
  bodyEn: string;
  bodyBn: string;
  bannerSlot: BannerSlot;
  bannerCategoryId: string;
  targets: TargetRow[];
};

export function emptyForm(type: PromotionType = "FlashSale"): PromotionForm {
  return {
    nameEn: "",
    nameBn: "",
    type,
    startsAt: "",
    endsAt: "",
    priority: "0",
    stackMode: "StacksWithOrderPromotions",
    channels: [...CHANNELS],
    minCartValue: "",
    usageLimit: "",
    labelEn: "",
    labelBn: "",
    discountKind: "Percent",
    discountValue: "",
    maxDiscount: "",
    buyQuantity: "2",
    getQuantity: "1",
    getDiscountPercent: "100",
    bundlePrice: "",
    zoneCodes: "",
    slug: "",
    titleEn: "",
    titleBn: "",
    bodyEn: "",
    bodyBn: "",
    bannerSlot: "HomeTop",
    bannerCategoryId: "",
    targets: [],
  };
}

const text = (value: string | null | undefined) => value ?? "";
const num = (value: number | string | null | undefined) => (value === null || value === undefined ? "" : String(Number(value)));

/** The API sends channels as `"Web, SocialDesk"`; an empty or missing value means every channel. */
export function parseChannels(value: string | null | undefined): Channel[] {
  const found = CHANNELS.filter((c) => (value ?? "").split(",").map((p) => p.trim()).includes(c));
  return found.length ? found : [...CHANNELS];
}

export function formatChannels(channels: readonly Channel[]): string {
  return CHANNELS.filter((c) => channels.includes(c)).join(", ");
}

export function parseZoneCodes(value: string): string[] {
  const seen = new Set<string>();
  for (const part of value.split(/[,\s]+/)) {
    const code = part.trim().toUpperCase();
    if (code) seen.add(code);
  }
  return [...seen];
}

export function formFromDetail(d: PromotionDetail): PromotionForm {
  const base = emptyForm(d.type);
  return {
    ...base,
    nameEn: d.nameEn,
    nameBn: text(d.nameBn),
    startsAt: isoToDhakaLocal(d.startsAt),
    endsAt: isoToDhakaLocal(d.endsAt),
    priority: num(d.priority) || "0",
    stackMode: d.stackMode,
    channels: parseChannels(d.channels),
    minCartValue: num(d.minCartValue),
    usageLimit: num(d.usageLimit),
    labelEn: text(d.labelEn),
    labelBn: text(d.labelBn),
    discountKind: d.discountKind ?? "Percent",
    discountValue: num(d.discountValue),
    maxDiscount: num(d.maxDiscount),
    buyQuantity: num(d.buyQuantity),
    getQuantity: num(d.getQuantity),
    getDiscountPercent: num(d.getDiscountPercent),
    bundlePrice: num(d.bundlePrice),
    zoneCodes: d.zoneCodes.join(", "),
    slug: text(d.slug),
    titleEn: text(d.titleEn),
    titleBn: text(d.titleBn),
    bodyEn: text(d.bodyEn),
    bodyBn: text(d.bodyBn),
    bannerSlot: d.bannerSlot === "None" ? "HomeTop" : d.bannerSlot,
    bannerCategoryId: text(d.bannerCategoryId),
    targets: d.targets.map((t) => ({ kind: t.kind, targetId: t.targetId, role: t.role, quantity: num(t.quantity) || "1", name: text(t.name) })),
  };
}

/* ---- Validation and the request -------------------------------------------------------------------------------------------- */

/** Parses a typed number ("1,250.50" or "1250"); NaN when it is not one. Empty text is NaN too. */
export function parseNumber(value: string): number {
  const cleaned = value.trim().replaceAll(",", "");
  if (cleaned === "") return Number.NaN;
  return Number(cleaned);
}

const isWhole = (n: number) => Number.isInteger(n);

/** Which targets of a form count for its type (the others are dropped from the request). */
export function activeTargets(form: PromotionForm): TargetRow[] {
  const rule = TYPE_RULES[form.type];
  return form.targets.filter((t) => rule.roles.includes(t.role) && (t.kind !== "Everything" || rule.everything));
}

export function targetCount(form: PromotionForm, role: TargetRole): number {
  return activeTargets(form).filter((t) => t.role === role).length;
}

/** Units a bundle needs: the sum of the quantities of its items. */
export function bundleUnits(form: PromotionForm): number {
  return activeTargets(form)
    .filter((t) => t.role === "BundleItem")
    .reduce((sum, t) => sum + (Number.isFinite(parseNumber(t.quantity)) ? parseNumber(t.quantity) : 0), 0);
}

/**
 * Problems found before sending: { field: errorKey }. The keys are texts under `promotions.editor.errors`. The API checks everything
 * again and its own field errors are shown too; this only saves a round trip for the obvious ones.
 */
export function validateForm(form: PromotionForm): Record<string, string> {
  const e: Record<string, string> = {};
  const rule = TYPE_RULES[form.type];
  if (!form.nameEn.trim()) e.nameEn = "nameRequired";
  else if (form.nameEn.trim().length > 150) e.nameEn = "nameLong";
  if (form.startsAt && !LOCAL.test(form.startsAt)) e.startsAt = "dateInvalid";
  if (form.endsAt && !LOCAL.test(form.endsAt)) e.endsAt = "dateInvalid";
  if (rule.endsRequired && !form.endsAt) e.endsAt = "endsRequired";
  if (!e.endsAt && !windowOrdered(form.startsAt, form.endsAt)) e.endsAt = "endsBeforeStarts";

  const priority = parseNumber(form.priority);
  if (!isWhole(priority) || priority < PRIORITY_MIN || priority > PRIORITY_MAX) e.priority = "priorityRange";
  if (form.channels.length === 0) e.channels = "channelsRequired";
  if (form.usageLimit.trim() !== "") {
    const n = parseNumber(form.usageLimit);
    if (!isWhole(n) || n < 1) e.usageLimit = "usageLimitInvalid";
  }
  if (form.minCartValue.trim() !== "") {
    const n = parseNumber(form.minCartValue);
    if (!Number.isFinite(n) || n < 0) e.minCartValue = "amountInvalid";
  }

  const wantsDiscount = rule.discount === "required" || (rule.discount === "optional" && form.discountValue.trim() !== "");
  if (wantsDiscount) {
    const n = parseNumber(form.discountValue);
    if (!Number.isFinite(n) || n <= 0) e.discountValue = "discountValueInvalid";
    else if (form.discountKind === "Percent" && n > 100) e.discountValue = "percentTooHigh";
    if (form.maxDiscount.trim() !== "") {
      const cap = parseNumber(form.maxDiscount);
      if (!Number.isFinite(cap) || cap <= 0) e.maxDiscount = "amountInvalid";
    }
  }

  if (rule.bundle) {
    const price = parseNumber(form.bundlePrice);
    if (!Number.isFinite(price) || price < 0) e.bundlePrice = "bundlePriceInvalid";
    if (bundleUnits(form) < 2) e.targets = "bundleNeedsTwo";
  }
  if (rule.buyXGetY) {
    const buy = parseNumber(form.buyQuantity);
    const get = parseNumber(form.getQuantity);
    const pct = parseNumber(form.getDiscountPercent);
    if (!isWhole(buy) || buy < 1) e.buyQuantity = "quantityInvalid";
    if (!isWhole(get) || get < 1) e.getQuantity = "quantityInvalid";
    if (!Number.isFinite(pct) || pct <= 0 || pct > 100) e.getDiscountPercent = "percentInvalid";
    if (targetCount(form, "Buy") < 1) e.targets = "buyNeedsTarget";
  }
  if (rule.freeDelivery && form.minCartValue.trim() === "") e.minCartValue = "minCartRequired";
  if (rule.campaign) {
    if (!form.slug.trim()) e.slug = "slugRequired";
    else if (form.slug.trim().length < 2 || form.slug.trim().length > 100 || !SLUG_PATTERN.test(form.slug.trim())) e.slug = "slugInvalid";
    if (!form.titleEn.trim()) e.titleEn = "titleRequired";
    if (form.bannerSlot === "Category" && !form.bannerCategoryId) e.bannerCategoryId = "bannerCategoryRequired";
  }
  if (!e.targets && rule.targetsRequired && rule.roles.includes("Main") && targetCount(form, "Main") < 1) e.targets = "targetsRequired";
  for (const t of activeTargets(form)) {
    if (t.role === "BundleItem") {
      const q = parseNumber(t.quantity);
      if (!isWhole(q) || q < 1) e.targets = e.targets ?? "quantityInvalid";
    }
  }
  return e;
}

const orNull = (value: string) => (value.trim() === "" ? null : value.trim());
const numberOrNull = (value: string) => (value.trim() === "" ? null : parseNumber(value));

/** The body of POST /promotions and PUT /promotions/{id}: only the fields that count for the chosen type. */
export function toRequest(form: PromotionForm): SavePromotion {
  const rule = TYPE_RULES[form.type];
  const request: SavePromotion = {
    nameEn: form.nameEn.trim(),
    nameBn: orNull(form.nameBn),
    type: form.type,
    startsAt: dhakaLocalToIso(form.startsAt),
    endsAt: dhakaLocalToIso(form.endsAt),
    priority: parseNumber(form.priority),
    stackMode: form.stackMode,
    channels: formatChannels(form.channels),
    minCartValue: numberOrNull(form.minCartValue),
    usageLimit: numberOrNull(form.usageLimit),
    labelEn: orNull(form.labelEn),
    labelBn: orNull(form.labelBn),
    buyQuantity: 0,
    getQuantity: 0,
    getDiscountPercent: 100,
    targets: activeTargets(form).map((t) => ({
      kind: t.kind,
      targetId: t.kind === "Everything" ? null : t.targetId,
      role: t.role,
      quantity: t.role === "BundleItem" ? parseNumber(t.quantity) : 1,
    })),
  };
  const discounted = rule.discount === "required" || (rule.discount === "optional" && form.discountValue.trim() !== "");
  if (discounted) {
    request.discountKind = form.discountKind;
    request.discountValue = parseNumber(form.discountValue);
    request.maxDiscount = form.discountKind === "Percent" ? numberOrNull(form.maxDiscount) : null;
  }
  if (rule.bundle) request.bundlePrice = parseNumber(form.bundlePrice);
  if (rule.buyXGetY) {
    request.buyQuantity = parseNumber(form.buyQuantity);
    request.getQuantity = parseNumber(form.getQuantity);
    request.getDiscountPercent = parseNumber(form.getDiscountPercent);
  }
  if (rule.freeDelivery) request.zoneCodes = parseZoneCodes(form.zoneCodes);
  if (rule.campaign) {
    request.slug = form.slug.trim();
    request.titleEn = orNull(form.titleEn);
    request.titleBn = orNull(form.titleBn);
    request.bodyEn = orNull(form.bodyEn);
    request.bodyBn = orNull(form.bodyBn);
    request.bannerSlot = form.bannerSlot;
    request.bannerCategoryId = form.bannerSlot === "Category" ? orNull(form.bannerCategoryId) : null;
  }
  return request;
}

/* ---- Status and actions ---------------------------------------------------------------------------------------------------- */

export type PromotionPerms = { view: boolean; create: boolean; edit: boolean; remove: boolean };
export type PromotionActions = { save: boolean; activate: boolean; deactivate: boolean; archive: boolean; duplicate: boolean; upload: boolean };

/**
 * The actions a status allows for this person. An archived promotion can only be copied. A Draft is switched on; anything else that
 * has been switched on (Scheduled, Active, Ended) is switched off. The API checks again.
 */
export function allowedActions(status: PromotionStatus, perms: PromotionPerms): PromotionActions {
  const archived = status === "Archived";
  return {
    save: !archived && perms.edit,
    activate: status === "Draft" && perms.edit,
    deactivate: (status === "Scheduled" || status === "Active" || status === "Ended") && perms.edit,
    archive: !archived && perms.remove,
    duplicate: perms.create,
    upload: !archived && perms.edit,
  };
}

export type Tone = "neutral" | "brand" | "success" | "warning" | "danger" | "info" | "outline";

export function statusTone(status: PromotionStatus): Tone {
  switch (status) {
    case "Active":
      return "success";
    case "Scheduled":
      return "info";
    case "Ended":
      return "neutral";
    case "Archived":
      return "outline";
    default:
      return "warning";
  }
}

/* ---- Stacking explanation (docs/pricing-engine.md section 3) ---------------------------------------------------------------- */

/** Message keys under `promotions.stacking.*`. */
export type StackingText = { headline: string; points: string[] };

/**
 * Which plain-language sentences describe the choice of stack mode for this type. Free delivery is judged on its own and ignores the
 * mode; price types (flash sale, product discount, campaign) compete per line with the lowest price winning; bundle and buy X get Y
 * combine at cart level in priority order. A coupon is applied after promotions in every case.
 */
export function stackingText(type: PromotionType, mode: StackMode): StackingText {
  const level = TYPE_RULES[type].level;
  if (level === "delivery") return { headline: "delivery", points: ["deliveryIndependent"] };
  const points: string[] = [level === "line" ? "lineLowestWins" : "orderPriority"];
  if (mode === "Exclusive") points.push("exclusiveAlone", "exclusiveNoCoupon");
  else if (mode === "StacksWithCoupon") points.push(level === "order" ? "combinesOrder" : "combinesNone", "couponStacks", "couponNeedsFlag");
  else points.push(level === "order" ? "combinesOrder" : "combinesNone", "couponBetterOf");
  return { headline: mode, points };
}

/* ---- List and report helpers ----------------------------------------------------------------------------------------------- */

export type PromotionFilters = { type?: string; status?: string };

export type ReportRow = { orders: number | string; units: number | string; revenue: number | string; discountCost: number | string };

const asNumber = (v: number | string | null | undefined) => {
  const n = typeof v === "number" ? v : Number(v);
  return Number.isFinite(n) ? n : 0;
};

/** Totals over report rows. An order that carries two promotions is counted under each, so `orders` is a sum per promotion. */
export function summarizeReport(rows: readonly ReportRow[]) {
  return rows.reduce<{ promotions: number; orders: number; units: number; revenue: number; discountCost: number }>(
    (sum, r) => ({ promotions: sum.promotions + 1, orders: sum.orders + asNumber(r.orders), units: sum.units + asNumber(r.units), revenue: sum.revenue + asNumber(r.revenue), discountCost: sum.discountCost + asNumber(r.discountCost) }),
    { promotions: 0, orders: 0, units: 0, revenue: 0, discountCost: 0 },
  );
}

/** Used count and limit of a list row as numbers (limit null: unlimited). */
export function usageText(used: number | string, limit: number | string | null): { used: number; limit: number | null } {
  return { used: asNumber(used), limit: limit === null || limit === undefined ? null : asNumber(limit) };
}
