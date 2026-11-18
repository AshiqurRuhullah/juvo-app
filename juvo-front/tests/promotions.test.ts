import { describe, expect, it } from "vitest";
import {
  activeTargets,
  allowedActions,
  bundleUnits,
  dhakaDayBound,
  dhakaLocalToIso,
  emptyForm,
  formFromDetail,
  formatChannels,
  isoToDhakaLocal,
  parseChannels,
  parseNumber,
  parseZoneCodes,
  stackingText,
  statusTone,
  summarizeReport,
  toRequest,
  validateForm,
  windowOrdered,
  type PromotionDetail,
  type PromotionForm,
  type PromotionPerms,
  type PromotionStatus,
  type TargetRow,
} from "@/modules/promotions/logic";

const all: PromotionPerms = { view: true, create: true, edit: true, remove: true };
const viewOnly: PromotionPerms = { view: true, create: false, edit: false, remove: false };
const id = "01a1b2c3-0000-7000-8000-000000000001";

const target = (over: Partial<TargetRow> = {}): TargetRow => ({ kind: "Product", targetId: id, role: "Main", quantity: "1", name: "Tee", ...over });
const form = (over: Partial<PromotionForm> = {}): PromotionForm => ({ ...emptyForm(), nameEn: "Eid flash", ...over });

describe("Dhaka time", () => {
  it("types are Dhaka time with a +06:00 offset", () => {
    expect(dhakaLocalToIso("2026-12-01T10:00")).toBe("2026-12-01T10:00:00+06:00");
  });
  it("empty or broken input is null", () => {
    expect(dhakaLocalToIso("")).toBeNull();
    expect(dhakaLocalToIso("2026-12-01")).toBeNull();
    expect(dhakaLocalToIso(null)).toBeNull();
  });
  it("any offset the API answers with is shown as Dhaka wall clock", () => {
    expect(isoToDhakaLocal("2026-12-01T10:00:00+06:00")).toBe("2026-12-01T10:00");
    expect(isoToDhakaLocal("2026-12-01T04:00:00Z")).toBe("2026-12-01T10:00");
    expect(isoToDhakaLocal("2026-12-01T23:30:00-05:00")).toBe("2026-12-02T10:30");
    expect(isoToDhakaLocal(null)).toBe("");
    expect(isoToDhakaLocal("nonsense")).toBe("");
  });
  it("round trips regardless of the device time zone", () => {
    const local = "2026-03-15T00:00";
    expect(isoToDhakaLocal(dhakaLocalToIso(local))).toBe(local);
  });
  it("report days become Dhaka day bounds", () => {
    expect(dhakaDayBound("2026-11-01", false)).toBe("2026-11-01T00:00:00+06:00");
    expect(dhakaDayBound("2026-11-30", true)).toBe("2026-11-30T23:59:59+06:00");
    expect(dhakaDayBound("", true)).toBeUndefined();
    expect(dhakaDayBound(undefined, false)).toBeUndefined();
  });
  it("a window may be one moment long but not backwards", () => {
    expect(windowOrdered("2026-12-01T10:00", "2026-12-01T10:00")).toBe(true);
    expect(windowOrdered("2026-12-01T10:00", "2026-12-01T09:59")).toBe(false);
    expect(windowOrdered("", "2026-12-01T09:59")).toBe(true);
  });
});

describe("channels and zones", () => {
  it("parses the API string and falls back to every channel", () => {
    expect(parseChannels("Web, SocialDesk")).toEqual(["Web", "SocialDesk"]);
    expect(parseChannels("")).toEqual(["Web", "SocialDesk", "Pos"]);
    expect(parseChannels(null)).toEqual(["Web", "SocialDesk", "Pos"]);
  });
  it("writes channels in a fixed order", () => {
    expect(formatChannels(["Pos", "Web"])).toBe("Web, Pos");
  });
  it("normalises zone codes", () => {
    expect(parseZoneCodes("dhaka, ctg  dhaka")).toEqual(["DHAKA", "CTG"]);
    expect(parseZoneCodes("")).toEqual([]);
  });
  it("parses typed numbers", () => {
    expect(parseNumber("1,250.50")).toBe(1250.5);
    expect(Number.isNaN(parseNumber(""))).toBe(true);
    expect(Number.isNaN(parseNumber("abc"))).toBe(true);
  });
});

describe("validation per type", () => {
  it("a flash sale needs a name, an end, a discount and a target", () => {
    const e = validateForm(emptyForm("FlashSale"));
    expect(e.nameEn).toBe("nameRequired");
    expect(e.endsAt).toBe("endsRequired");
    expect(e.discountValue).toBe("discountValueInvalid");
    expect(e.targets).toBe("targetsRequired");
  });
  it("a complete flash sale is valid", () => {
    expect(validateForm(form({ endsAt: "2026-12-01T18:00", discountValue: "20", targets: [target()] }))).toEqual({});
  });
  it("a percentage above 100 is refused, a fixed amount is not", () => {
    const base = { endsAt: "2026-12-01T18:00", targets: [target()] };
    expect(validateForm(form({ ...base, discountValue: "120" })).discountValue).toBe("percentTooHigh");
    expect(validateForm(form({ ...base, discountKind: "Fixed", discountValue: "120" }))).toEqual({});
  });
  it("the end must not be before the start", () => {
    const e = validateForm(form({ startsAt: "2026-12-02T10:00", endsAt: "2026-12-01T10:00", discountValue: "5", targets: [target()] }));
    expect(e.endsAt).toBe("endsBeforeStarts");
  });
  it("priority is a whole number from -1000 to 1000", () => {
    const base = { endsAt: "2026-12-01T18:00", discountValue: "5", targets: [target()] };
    expect(validateForm(form({ ...base, priority: "1001" })).priority).toBe("priorityRange");
    expect(validateForm(form({ ...base, priority: "-1000" })).priority).toBeUndefined();
    expect(validateForm(form({ ...base, priority: "1.5" })).priority).toBe("priorityRange");
  });
  it("at least one channel is needed", () => {
    expect(validateForm(form({ type: "ProductDiscount", discountValue: "5", targets: [target()], channels: [] })).channels).toBe("channelsRequired");
  });
  it("a bundle needs two units and a price of 0 or more", () => {
    const one = form({ type: "Bundle", bundlePrice: "1000", targets: [target({ role: "BundleItem" })] });
    expect(validateForm(one).targets).toBe("bundleNeedsTwo");
    const two = form({ type: "Bundle", bundlePrice: "1000", targets: [target({ role: "BundleItem", quantity: "2" })] });
    expect(validateForm(two)).toEqual({});
    expect(validateForm(form({ type: "Bundle", bundlePrice: "0", targets: two.targets })).bundlePrice).toBeUndefined();
    expect(validateForm(form({ type: "Bundle", bundlePrice: "", targets: two.targets })).bundlePrice).toBe("bundlePriceInvalid");
  });
  it("buy X get Y needs a buy set; the get set is optional", () => {
    const f = form({ type: "BuyXGetY", targets: [] });
    expect(validateForm(f).targets).toBe("buyNeedsTarget");
    expect(validateForm({ ...f, targets: [target({ role: "Buy" })] })).toEqual({});
    expect(validateForm({ ...f, targets: [target({ role: "Buy" })], getDiscountPercent: "0" }).getDiscountPercent).toBe("percentInvalid");
  });
  it("free delivery needs a cart value (0 is allowed) and no targets", () => {
    expect(validateForm(form({ type: "FreeDelivery" })).minCartValue).toBe("minCartRequired");
    expect(validateForm(form({ type: "FreeDelivery", minCartValue: "0" }))).toEqual({});
  });
  it("a campaign needs a clean address and an English title, but not products yet", () => {
    const e = validateForm(form({ type: "FestivalCampaign", slug: "Eid 2026" }));
    expect(e.slug).toBe("slugInvalid");
    expect(e.titleEn).toBe("titleRequired");
    expect(e.targets).toBeUndefined();
    expect(validateForm(form({ type: "FestivalCampaign", slug: "eid-2026", titleEn: "Eid" }))).toEqual({});
    expect(validateForm(form({ type: "FestivalCampaign", slug: "a", titleEn: "Eid" })).slug).toBe("slugInvalid");
  });
  it("a category banner needs its category", () => {
    const e = validateForm(form({ type: "FestivalCampaign", slug: "eid", titleEn: "Eid", bannerSlot: "Category" }));
    expect(e.bannerCategoryId).toBe("bannerCategoryRequired");
  });
  it("a campaign discount value is only checked when typed", () => {
    expect(validateForm(form({ type: "FestivalCampaign", slug: "eid", titleEn: "Eid", discountValue: "abc" })).discountValue).toBe("discountValueInvalid");
  });
});

describe("form to request", () => {
  it("maps a flash sale with Dhaka times and only its own fields", () => {
    const r = toRequest(form({ endsAt: "2026-12-01T18:00", startsAt: "2026-12-01T10:00", discountValue: "20", maxDiscount: "500", priority: "5", channels: ["Web", "SocialDesk"], slug: "ignored", bundlePrice: "9", targets: [target()] }));
    expect(r).toMatchObject({
      nameEn: "Eid flash",
      type: "FlashSale",
      startsAt: "2026-12-01T10:00:00+06:00",
      endsAt: "2026-12-01T18:00:00+06:00",
      priority: 5,
      stackMode: "StacksWithOrderPromotions",
      channels: "Web, SocialDesk",
      discountKind: "Percent",
      discountValue: 20,
      maxDiscount: 500,
      buyQuantity: 0,
      getQuantity: 0,
      targets: [{ kind: "Product", targetId: id, role: "Main", quantity: 1 }],
    });
    expect(r.slug).toBeUndefined();
    expect(r.bundlePrice).toBeUndefined();
  });
  it("drops the cap for a fixed discount and empty optional numbers become null", () => {
    const r = toRequest(form({ endsAt: "2026-12-01T18:00", discountKind: "Fixed", discountValue: "50", maxDiscount: "10", usageLimit: "", minCartValue: "", targets: [target()] }));
    expect(r.maxDiscount).toBeNull();
    expect(r.usageLimit).toBeNull();
    expect(r.minCartValue).toBeNull();
    expect(r.nameBn).toBeNull();
  });
  it("keeps bundle quantities and ignores the quantity of other roles", () => {
    const r = toRequest(form({ type: "Bundle", bundlePrice: "1000", targets: [target({ role: "BundleItem", quantity: "2" }), target({ kind: "Category", targetId: "c1", role: "BundleItem", quantity: "1" }), target({ role: "Main", quantity: "9" })] }));
    expect(r.bundlePrice).toBe(1000);
    expect(r.targets).toEqual([
      { kind: "Product", targetId: id, role: "BundleItem", quantity: 2 },
      { kind: "Category", targetId: "c1", role: "BundleItem", quantity: 1 },
    ]);
  });
  it("maps buy X get Y with both roles", () => {
    const r = toRequest(form({ type: "BuyXGetY", buyQuantity: "2", getQuantity: "1", getDiscountPercent: "100", targets: [target({ role: "Buy" }), target({ kind: "Brand", targetId: "b1", role: "Get" })] }));
    expect(r).toMatchObject({ buyQuantity: 2, getQuantity: 1, getDiscountPercent: 100 });
    expect(r.targets?.map((t) => t.role)).toEqual(["Buy", "Get"]);
  });
  it("sends zone codes for free delivery and no targets", () => {
    const r = toRequest(form({ type: "FreeDelivery", minCartValue: "2500", zoneCodes: "dhaka", stackMode: "StacksWithCoupon", targets: [target()] }));
    expect(r).toMatchObject({ minCartValue: 2500, zoneCodes: ["DHAKA"], stackMode: "StacksWithCoupon", targets: [] });
  });
  it("sends the campaign page fields and the Everything target only where allowed", () => {
    const camp = toRequest(form({ type: "FestivalCampaign", slug: " eid-2026 ", titleEn: "Eid", titleBn: "ঈদ", bannerSlot: "Category", bannerCategoryId: "cat1", targets: [target({ kind: "Everything", targetId: null })] }));
    expect(camp).toMatchObject({ slug: "eid-2026", titleEn: "Eid", titleBn: "ঈদ", bannerSlot: "Category", bannerCategoryId: "cat1", targets: [] });
    expect(camp.discountKind).toBeUndefined();
    const all = toRequest(form({ type: "ProductDiscount", discountValue: "10", targets: [target({ kind: "Everything", targetId: "ignored" })] }));
    expect(all.targets).toEqual([{ kind: "Everything", targetId: null, role: "Main", quantity: 1 }]);
  });
  it("a campaign without a discount value sends none", () => {
    const camp = toRequest(form({ type: "FestivalCampaign", slug: "eid", titleEn: "Eid" }));
    expect(camp.discountKind).toBeUndefined();
    expect(camp.discountValue).toBeUndefined();
  });
  it("only counts targets that belong to the type", () => {
    expect(activeTargets(form({ type: "FlashSale", targets: [target(), target({ role: "Buy" })] }))).toHaveLength(1);
    expect(bundleUnits(form({ type: "Bundle", targets: [target({ role: "BundleItem", quantity: "2" }), target({ role: "BundleItem", quantity: "x" })] }))).toBe(2);
  });
});

describe("detail to form", () => {
  const detail = {
    id,
    nameEn: "Tee and cap",
    nameBn: null,
    type: "Bundle",
    status: "Draft",
    isEnabled: false,
    isArchived: false,
    startsAt: "2026-12-01T10:00:00+06:00",
    endsAt: null,
    priority: "3",
    stackMode: "Exclusive",
    channels: "Web, Pos",
    minCartValue: null,
    usageLimit: 40,
    usedCount: 0,
    discountKind: null,
    discountValue: null,
    maxDiscount: null,
    buyQuantity: 0,
    getQuantity: 0,
    getDiscountPercent: 100,
    bundlePrice: "1000.00",
    zoneCodes: [],
    labelEn: null,
    labelBn: null,
    slug: null,
    titleEn: null,
    titleBn: null,
    bodyEn: null,
    bodyBn: null,
    heroImageUrl: null,
    bannerImageUrl: null,
    bannerSlot: "None",
    bannerCategoryId: null,
    targets: [{ id: "t1", kind: "Product", targetId: id, role: "BundleItem", quantity: 2, name: "Tee" }],
    createdAt: "2026-11-01T00:00:00+06:00",
  } as unknown as PromotionDetail;

  it("fills the form from the API answer", () => {
    const f = formFromDetail(detail);
    expect(f).toMatchObject({ nameEn: "Tee and cap", type: "Bundle", startsAt: "2026-12-01T10:00", endsAt: "", priority: "3", stackMode: "Exclusive", channels: ["Web", "Pos"], usageLimit: "40", bundlePrice: "1000", bannerSlot: "HomeTop" });
    expect(f.targets).toEqual([{ kind: "Product", targetId: id, role: "BundleItem", quantity: "2", name: "Tee" }]);
  });
  it("sending the unchanged form back reproduces the saved values", () => {
    const r = toRequest(formFromDetail(detail));
    expect(r).toMatchObject({ type: "Bundle", bundlePrice: 1000, usageLimit: 40, priority: 3, startsAt: "2026-12-01T10:00:00+06:00", endsAt: null });
  });
});

describe("status to allowed actions", () => {
  const actions = (status: PromotionStatus, perms = all) => allowedActions(status, perms);
  it("a draft can be saved, activated, archived and copied, not switched off", () => {
    expect(actions("Draft")).toEqual({ save: true, activate: true, deactivate: false, archive: true, duplicate: true, upload: true });
  });
  it("scheduled, active and ended can be switched off but not activated", () => {
    for (const s of ["Scheduled", "Active", "Ended"] as const) {
      expect(actions(s)).toMatchObject({ save: true, activate: false, deactivate: true, archive: true });
    }
  });
  it("an archived promotion can only be copied", () => {
    expect(actions("Archived")).toEqual({ save: false, activate: false, deactivate: false, archive: false, duplicate: true, upload: false });
  });
  it("a view-only person gets nothing", () => {
    for (const s of ["Draft", "Scheduled", "Active", "Ended", "Archived"] as const) {
      expect(Object.values(actions(s, viewOnly)).some(Boolean)).toBe(false);
    }
  });
  it("edit and delete are separate permissions", () => {
    const editOnly = actions("Draft", { view: true, create: false, edit: true, remove: false });
    expect(editOnly).toMatchObject({ save: true, activate: true, archive: false, duplicate: false });
    const deleteOnly = actions("Active", { view: true, create: false, edit: false, remove: true });
    expect(deleteOnly).toMatchObject({ save: false, deactivate: false, archive: true });
  });
  it("each status has a tone with its own meaning", () => {
    expect(statusTone("Active")).toBe("success");
    expect(statusTone("Scheduled")).toBe("info");
    expect(statusTone("Draft")).toBe("warning");
    expect(statusTone("Ended")).toBe("neutral");
    expect(statusTone("Archived")).toBe("outline");
  });
});

describe("stacking explanation", () => {
  it("free delivery ignores the stack mode", () => {
    for (const mode of ["Exclusive", "StacksWithCoupon", "StacksWithOrderPromotions"] as const) {
      expect(stackingText("FreeDelivery", mode)).toEqual({ headline: "delivery", points: ["deliveryIndependent"] });
    }
  });
  it("price types explain that the lowest price wins", () => {
    for (const type of ["FlashSale", "ProductDiscount", "FestivalCampaign"] as const) {
      expect(stackingText(type, "StacksWithOrderPromotions").points).toEqual(["lineLowestWins", "combinesNone", "couponBetterOf"]);
    }
  });
  it("cart types explain priority order and combining", () => {
    expect(stackingText("Bundle", "StacksWithOrderPromotions")).toEqual({ headline: "StacksWithOrderPromotions", points: ["orderPriority", "combinesOrder", "couponBetterOf"] });
    expect(stackingText("BuyXGetY", "StacksWithCoupon").points).toEqual(["orderPriority", "combinesOrder", "couponStacks", "couponNeedsFlag"]);
  });
  it("alone means no other cart promotion and no coupon", () => {
    expect(stackingText("Bundle", "Exclusive").points).toEqual(["orderPriority", "exclusiveAlone", "exclusiveNoCoupon"]);
    expect(stackingText("FlashSale", "Exclusive").headline).toBe("Exclusive");
  });
});

describe("report summary", () => {
  it("adds every row, accepting numbers sent as text", () => {
    expect(summarizeReport([{ orders: 2, units: "3", revenue: 2400, discountCost: "600.50" }, { orders: 1, units: 1, revenue: "100", discountCost: 0 }])).toEqual({ promotions: 2, orders: 3, units: 4, revenue: 2500, discountCost: 600.5 });
  });
  it("is zero for no rows", () => {
    expect(summarizeReport([])).toEqual({ promotions: 0, orders: 0, units: 0, revenue: 0, discountCost: 0 });
  });
});
