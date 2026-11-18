import { describe, expect, it } from "vitest";
import {
  RULE_FIELDS,
  RULE_TYPES,
  allowedActions,
  automationPatch,
  campaignToRequest,
  canNotify,
  changeRuleType,
  delayParts,
  dhakaInputToInstant,
  dhakaInputToOffset,
  emptyCampaignForm,
  emptyRule,
  emptySegmentForm,
  fieldKey,
  hasFormErrors,
  instantToDhakaInput,
  looksLikeMobile,
  notifyErrorKey,
  parseRuleErrorKey,
  placeholdersIn,
  ruleFromApi,
  ruleSummary,
  ruleToRequest,
  scheduleProblem,
  segmentToRequest,
  smsLength,
  splitSegmentErrors,
  unknownPlaceholders,
  unsubscribeOutcome,
  validateAutomationForm,
  validateCampaignForm,
  validateSegmentForm,
  validateSettingsForm,
  type AutomationForm,
  type RuleForm,
} from "@/modules/marketing/logic";
import en from "@/messages/marketing.en.json";
import bn from "@/messages/marketing.bn.json";

const rule = (type: RuleForm["type"], patch: Partial<RuleForm> = {}): RuleForm => ({ ...emptyRule(type), ...patch });

describe("segment rules: visible fields and request mapping", () => {
  it("every rule type uses the fields the API documents", () => {
    expect(RULE_FIELDS.newCustomers).toEqual(["days"]);
    expect(RULE_FIELDS.repeatBuyers).toEqual(["minOrders", "periodDays"]);
    expect(RULE_FIELDS.highSpenders).toEqual(["minSpend", "periodDays"]);
    expect(RULE_FIELDS.inactive).toEqual(["days"]);
    expect(RULE_FIELDS.location).toEqual(["level", "ids"]);
    expect(RULE_FIELDS.consent).toEqual(["channel", "granted"]);
    expect(RULE_FIELDS.lastOrderStatus).toEqual(["status"]);
    expect(RULE_FIELDS.wishlist).toEqual(["productIds"]);
    expect(RULE_FIELDS.cart).toEqual(["idleHours"]);
    expect(RULE_TYPES).toHaveLength(9);
  });

  it("sends only the type and the fields the type uses (the API refuses the others)", () => {
    // A leftover value in a field the type does not use must never reach the request.
    const r = rule("newCustomers", { days: "30", minSpend: "5000", minOrders: "3", ids: ["x"], channel: "email" });
    expect(ruleToRequest(r)).toEqual({ type: "newCustomers", days: 30 });
  });

  it("leaves optional blanks out and keeps typed numbers", () => {
    expect(ruleToRequest(rule("highSpenders", { minSpend: "10,000", periodDays: "" }))).toEqual({ type: "highSpenders", minSpend: 10000 });
    expect(ruleToRequest(rule("highSpenders", { minSpend: "2500.5", periodDays: "90" }))).toEqual({ type: "highSpenders", minSpend: 2500.5, periodDays: 90 });
    expect(ruleToRequest(rule("repeatBuyers", { minOrders: "2" }))).toEqual({ type: "repeatBuyers", minOrders: 2 });
    expect(ruleToRequest(rule("cart"))).toEqual({ type: "cart" });
    expect(ruleToRequest(rule("wishlist"))).toEqual({ type: "wishlist" });
    expect(ruleToRequest(rule("wishlist", { productIds: ["a", "b"] }))).toEqual({ type: "wishlist", productIds: ["a", "b"] });
  });

  it("maps location, consent and order status rules", () => {
    expect(ruleToRequest(rule("location", { level: "division", ids: ["d1"] }))).toEqual({ type: "location", level: "division", ids: ["d1"] });
    expect(ruleToRequest(rule("consent", { channel: "email", granted: false }))).toEqual({ type: "consent", channel: "email", granted: false });
    expect(ruleToRequest(rule("lastOrderStatus", { status: "Delivered" }))).toEqual({ type: "lastOrderStatus", status: "Delivered" });
  });

  it("changing the type keeps shared fields and drops the rest", () => {
    const from = rule("highSpenders", { minSpend: "500", periodDays: "30" });
    const to = changeRuleType(from, "repeatBuyers");
    expect(to.key).toBe(from.key);
    expect(to.periodDays).toBe("30");
    expect(to.minSpend).toBe("");
    expect(changeRuleType(rule("newCustomers", { days: "7" }), "inactive").days).toBe("7");
  });

  it("builds the whole request with name, combinator and rules", () => {
    const form = { ...emptySegmentForm(), name: "  Dhaka big spenders ", description: " ", combinator: "or" as const, rules: [rule("highSpenders", { minSpend: "10000", periodDays: "90" }), rule("location", { ids: ["z"] })] };
    expect(segmentToRequest(form)).toEqual({
      name: "Dhaka big spenders",
      description: null,
      definition: { combinator: "or", rules: [{ type: "highSpenders", minSpend: 10000, periodDays: 90 }, { type: "location", level: "district", ids: ["z"] }] },
    });
  });

  it("reads a rule from the API back into the form", () => {
    const form = ruleFromApi({ type: "highSpenders", minSpend: 10000, periodDays: null });
    expect(form.type).toBe("highSpenders");
    expect(form.minSpend).toBe("10000");
    expect(form.periodDays).toBe("");
    expect(ruleFromApi({ type: "consent", channel: "email", granted: false })).toMatchObject({ channel: "email", granted: false });
    expect(ruleFromApi({ type: "location", level: "division", ids: ["a"] })).toMatchObject({ level: "division", ids: ["a"] });
  });
});

describe("segment validation", () => {
  it("needs a name and the required field of each rule", () => {
    const errors = validateSegmentForm({ ...emptySegmentForm(), name: "", rules: [rule("newCustomers"), rule("location"), rule("highSpenders", { minSpend: "0" })] });
    expect(errors.name).toBe("required");
    expect(errors.fields[fieldKey(0, "days")]).toBe("required");
    expect(errors.fields[fieldKey(1, "ids")]).toBe("pick");
    expect(errors.fields[fieldKey(2, "minSpend")]).toBe("positive");
    expect(hasFormErrors(errors)).toBe(true);
  });

  it("is happy with a complete form and checks optional numbers only when filled", () => {
    const good = { ...emptySegmentForm(), name: "Ok", rules: [rule("newCustomers", { days: "30" }), rule("cart"), rule("highSpenders", { minSpend: "100" })] };
    expect(hasFormErrors(validateSegmentForm(good))).toBe(false);
    const badOptional = validateSegmentForm({ ...good, rules: [rule("cart", { idleHours: "abc" })] });
    expect(badOptional.fields[fieldKey(0, "idleHours")]).toBe("number");
    expect(validateSegmentForm({ ...good, rules: [rule("newCustomers", { days: "1.5" })] }).fields[fieldKey(0, "days")]).toBe("integer");
  });

  it("refuses no rules and too many rules", () => {
    expect(validateSegmentForm({ ...emptySegmentForm(), name: "x", rules: [] }).rules).toBe("none");
    expect(validateSegmentForm({ ...emptySegmentForm(), name: "x", rules: Array.from({ length: 21 }, () => rule("cart")) }).rules).toBe("tooMany");
  });

  it("recognises the API's per-rule field errors", () => {
    expect(parseRuleErrorKey("rules[1].minSpend")).toEqual({ index: 1, field: "minSpend" });
    expect(parseRuleErrorKey("definition.rules[0].Days")).toEqual({ index: 0, field: "days" });
    expect(parseRuleErrorKey("name")).toBeNull();
    const split = splitSegmentErrors({ "rules[2].ids": "Choose at least one.", name: "Name taken." });
    expect(split.fields["2.ids"]).toBe("Choose at least one.");
    expect(split.other).toEqual(["Name taken."]);
  });

  it("describes rules for the list", () => {
    expect(ruleSummary({ type: "newCustomers", days: 30 })).toEqual({ key: "newCustomers", values: { days: 30 } });
    expect(ruleSummary({ type: "highSpenders", minSpend: 10000 }).key).toBe("highSpenders");
    expect(ruleSummary({ type: "highSpenders", minSpend: 10000, periodDays: 90 }).key).toBe("highSpendersIn");
    expect(ruleSummary({ type: "consent", channel: "email", granted: false }).key).toBe("noEmail");
    expect(ruleSummary({ type: "wishlist", productIds: ["a"] })).toEqual({ key: "wishlistOf", values: { count: 1 } });
  });
});

describe("SMS length counter", () => {
  it("counts plain text as 160 per SMS and 153 per part after that", () => {
    expect(smsLength("")).toMatchObject({ parts: 0, characters: 0 });
    expect(smsLength("Hello")).toMatchObject({ encoding: "gsm", parts: 1, characters: 5, perPart: 160 });
    expect(smsLength("a".repeat(160))).toMatchObject({ parts: 1 });
    expect(smsLength("a".repeat(161))).toMatchObject({ parts: 2, perPart: 153 });
    expect(smsLength("a".repeat(307))).toMatchObject({ parts: 3 });
  });

  it("counts extension characters twice", () => {
    expect(smsLength("[x]").characters).toBe(5);
    expect(smsLength("€".repeat(81)).parts).toBe(2);
  });

  it("switches to Unicode (70, then 67 per part) for Bengali", () => {
    expect(smsLength("হ্যালো")).toMatchObject({ encoding: "unicode", parts: 1, perPart: 70 });
    expect(smsLength("অ".repeat(70))).toMatchObject({ parts: 1 });
    expect(smsLength("অ".repeat(71))).toMatchObject({ parts: 2, perPart: 67 });
    expect(smsLength("অ".repeat(135))).toMatchObject({ parts: 3 });
    expect(smsLength("Hello হ্যালো").encoding).toBe("unicode");
  });

  it("finds placeholders and the unknown ones", () => {
    expect(placeholdersIn("Hi {customerName}, {link} and {customerName}")).toEqual(["customerName", "link"]);
    expect(unknownPlaceholders("Hi {customerName} {oops}")).toEqual(["oops"]);
    expect(unknownPlaceholders("{itemCount}", ["itemCount"])).toEqual([]);
  });
});

describe("schedule and Dhaka time", () => {
  it("turns Dhaka local time into the instant and the offset text", () => {
    expect(dhakaInputToInstant("2026-12-01T10:00")?.toISOString()).toBe("2026-12-01T04:00:00.000Z");
    expect(dhakaInputToOffset("2026-12-01T10:00")).toBe("2026-12-01T10:00:00+06:00");
    expect(dhakaInputToInstant("2026-12-01T00:30")?.toISOString()).toBe("2026-11-30T18:30:00.000Z");
  });

  it("rejects impossible dates and bad text", () => {
    expect(dhakaInputToInstant("2026-02-30T10:00")).toBeNull();
    expect(dhakaInputToInstant("2026-12-01T25:00")).toBeNull();
    expect(dhakaInputToInstant("not a date")).toBeNull();
    expect(dhakaInputToOffset("")).toBeNull();
  });

  it("shows an instant as Dhaka time and round-trips", () => {
    expect(instantToDhakaInput("2026-12-01T04:00:00Z")).toBe("2026-12-01T10:00");
    expect(instantToDhakaInput("2026-12-01T20:30:00+00:00")).toBe("2026-12-02T02:30");
    const local = "2027-01-15T09:45";
    expect(instantToDhakaInput(dhakaInputToInstant(local)!)).toBe(local);
  });

  it("explains why a time is refused, like the API does", () => {
    const now = new Date("2026-12-01T04:00:00Z"); // 10:00 in Dhaka
    expect(scheduleProblem("", now)).toBe("missing");
    expect(scheduleProblem("garbage", now)).toBe("invalid");
    expect(scheduleProblem("2026-12-01T10:00", now)).toBe("past");
    expect(scheduleProblem("2026-12-01T09:59", now)).toBe("past");
    expect(scheduleProblem("2026-12-01T10:01", now)).toBeNull();
    expect(scheduleProblem("2027-12-01T10:01", now)).toBe("tooFar");
    expect(scheduleProblem("2027-11-30T10:00", now)).toBeNull();
  });
});

describe("campaign status and actions", () => {
  it("draft and scheduled campaigns can be changed, sent and cancelled", () => {
    for (const status of ["Draft", "Scheduled"] as const) {
      expect(allowedActions(status, false)).toEqual(["edit", "schedule", "sendNow", "test", "cancel", "delete"]);
    }
  });

  it("a sending campaign can only be cancelled (and tested)", () => {
    expect(allowedActions("Sending", true)).toEqual(["test", "cancel"]);
  });

  it("a sent campaign is kept", () => {
    expect(allowedActions("Sent", true)).toEqual(["test"]);
  });

  it("a cancelled campaign can be deleted only when it never started", () => {
    expect(allowedActions("Cancelled", false)).toContain("delete");
    expect(allowedActions("Cancelled", true)).not.toContain("delete");
    expect(allowedActions("Cancelled", true)).not.toContain("sendNow");
  });
});

describe("campaign form", () => {
  it("needs name, segment and an English text; email needs a subject", () => {
    const e = validateCampaignForm(emptyCampaignForm());
    expect(e).toEqual({ name: "required", segmentId: "required", bodyEn: "required" });
    expect(validateCampaignForm({ ...emptyCampaignForm(), name: "n", segmentId: "s", channel: "Email", bodyEn: "hi" }).subjectEn).toBe("required");
    expect(validateCampaignForm({ ...emptyCampaignForm(), name: "n", segmentId: "s", bodyEn: "hi {customerName} {link}" })).toEqual({});
  });

  it("refuses placeholders the API does not know", () => {
    expect(validateCampaignForm({ ...emptyCampaignForm(), name: "n", segmentId: "s", bodyEn: "hi {nickname}" }).bodyEn).toBe("placeholder");
    expect(validateCampaignForm({ ...emptyCampaignForm(), name: "n", segmentId: "s", bodyEn: "hi", bodyBn: "{x}" }).bodyEn).toBe("placeholder");
  });

  it("builds the request: SMS carries no subject, blank Bengali is null", () => {
    const sms = campaignToRequest({ ...emptyCampaignForm(), name: " A ", segmentId: "s", channel: "Sms", subjectEn: "ignored", bodyEn: " hello ", bodyBn: " " });
    expect(sms).toEqual({ name: "A", segmentId: "s", channel: "Sms", subjectEn: null, subjectBn: null, bodyEn: "hello", bodyBn: null });
    const email = campaignToRequest({ ...emptyCampaignForm(), name: "B", segmentId: "s", channel: "Email", subjectEn: " Sale ", subjectBn: "", bodyEn: "x", bodyBn: "y" });
    expect(email).toMatchObject({ channel: "Email", subjectEn: "Sale", subjectBn: null, bodyBn: "y" });
  });
});

describe("automations and settings", () => {
  const base: AutomationForm = { isEnabled: false, channel: "Sms", delayMinutes: "180", windowDays: "7", capDays: "7", subjectEn: "", subjectBn: "", bodyEn: "Hi {customerName} {link}", bodyBn: "", };
  const allowed = ["customerName", "shopName", "link", "itemCount", "unsubscribeLink"];

  it("validates numbers and placeholders", () => {
    expect(validateAutomationForm("AbandonedCart", base, allowed)).toEqual({});
    expect(validateAutomationForm("AbandonedCart", { ...base, delayMinutes: "-1" }, allowed).delayMinutes).toBe("negative");
    expect(validateAutomationForm("AbandonedCart", { ...base, capDays: "x" }, allowed).capDays).toBe("number");
    expect(validateAutomationForm("AbandonedCart", { ...base, bodyEn: "{productName}" }, allowed).bodyEn).toBe("placeholder");
    expect(validateAutomationForm("BackInStock", { ...base, windowDays: "oops" }, ["productName"]).windowDays).toBeUndefined();
    expect(validateAutomationForm("WinBack", { ...base, channel: "Email" }, allowed).subjectEn).toBe("required");
  });

  it("sends the window only for automations that use it", () => {
    expect(automationPatch("AbandonedCart", base).windowDays).toBe(7);
    expect(automationPatch("WinBack", base).windowDays).toBeUndefined();
    expect(automationPatch("WinBack", { ...base, channel: "Email", subjectEn: " S " }).subjectEn).toBe("S");
    expect(automationPatch("WinBack", base).subjectEn).toBeUndefined();
  });

  it("shows a delay as days, hours or minutes", () => {
    expect(delayParts(10080)).toEqual({ unit: "days", count: 7 });
    expect(delayParts(180)).toEqual({ unit: "hours", count: 3 });
    expect(delayParts(45)).toEqual({ unit: "minutes", count: 45 });
    expect(delayParts(0)).toEqual({ unit: "minutes", count: 0 });
  });

  it("validates the sending rules against the API's ranges", () => {
    const ok = { quietHoursEnabled: true, quietHoursStart: "21:00", quietHoursEnd: "09:00", frequencyCapMessages: "3", frequencyCapDays: "7", throttlePerMinute: "600", attributionWindowDays: "7" };
    expect(validateSettingsForm(ok)).toEqual({});
    expect(validateSettingsForm({ ...ok, frequencyCapMessages: "0" })).toEqual({});
    expect(validateSettingsForm({ ...ok, frequencyCapMessages: "101" }).frequencyCapMessages).toBe("range");
    expect(validateSettingsForm({ ...ok, frequencyCapDays: "0" }).frequencyCapDays).toBe("range");
    expect(validateSettingsForm({ ...ok, throttlePerMinute: "100001" }).throttlePerMinute).toBe("range");
    expect(validateSettingsForm({ ...ok, attributionWindowDays: "91" }).attributionWindowDays).toBe("range");
    expect(validateSettingsForm({ ...ok, attributionWindowDays: "1.5" }).attributionWindowDays).toBe("integer");
    expect(validateSettingsForm({ ...ok, quietHoursStart: "9pm" }).quietHoursStart).toBe("time");
    expect(validateSettingsForm({ ...ok, quietHoursEnd: "24:00" }).quietHoursEnd).toBe("time");
  });
});

describe("storefront: notify me and unsubscribe", () => {
  it("maps every failure of the notify-me request to a text", () => {
    expect(notifyErrorKey({ status: 409, code: "MARKETING.VARIANT_IN_STOCK" })).toBe("inStock");
    expect(notifyErrorKey({ status: 429, code: "MARKETING.SUBSCRIPTION_LIMIT" })).toBe("limit");
    expect(notifyErrorKey({ status: 429, code: "GENERAL.TOO_MANY_REQUESTS" })).toBe("rateLimited");
    expect(notifyErrorKey({ status: 429 })).toBe("rateLimited");
    expect(notifyErrorKey({ status: 400, code: "MARKETING.CONTACT_REQUIRED" })).toBe("contact");
    expect(notifyErrorKey({ status: 404, code: "MARKETING.VARIANT_NOT_FOUND" })).toBe("notFound");
    expect(notifyErrorKey({ status: 0, code: "NETWORK" })).toBe("network");
    expect(notifyErrorKey({ status: 500 })).toBe("generic");
    expect(notifyErrorKey(null)).toBe("generic");
  });

  it("offers the form only for a sold-out variant that cannot be pre-ordered", () => {
    expect(canNotify({ inStock: false })).toBe(true);
    expect(canNotify({ inStock: false, isPreorder: false })).toBe(true);
    expect(canNotify({ inStock: false, isPreorder: true })).toBe(false);
    expect(canNotify({ inStock: true })).toBe(false);
    expect(canNotify(undefined)).toBe(false);
  });

  it("recognises mobile numbers as people type them", () => {
    expect(looksLikeMobile("01712345678")).toBe(true);
    expect(looksLikeMobile("+880 1712-345678")).toBe(true);
    expect(looksLikeMobile("০১৭১২৩৪৫৬৭৮")).toBe(true);
    expect(looksLikeMobile("12345")).toBe(false);
    expect(looksLikeMobile("0212345678")).toBe(false);
    expect(looksLikeMobile("")).toBe(false);
  });

  it("decides what the unsubscribe page shows", () => {
    expect(unsubscribeOutcome({ token: null })).toBe("missing");
    expect(unsubscribeOutcome({ token: "  " })).toBe("missing");
    expect(unsubscribeOutcome({ token: "abc" })).toBe("ready");
    expect(unsubscribeOutcome({ token: "abc", working: true })).toBe("working");
    expect(unsubscribeOutcome({ token: "abc", result: { unsubscribed: true, alreadyUnsubscribed: false } })).toBe("done");
    expect(unsubscribeOutcome({ token: "abc", result: { unsubscribed: true, alreadyUnsubscribed: true } })).toBe("already");
    expect(unsubscribeOutcome({ token: "abc", problem: { status: 400, code: "MARKETING.UNSUBSCRIBE_TOKEN_INVALID" } })).toBe("invalid");
    expect(unsubscribeOutcome({ token: "abc", problem: { status: 500 } })).toBe("failed");
  });
});

describe("message files", () => {
  const collect = (o: unknown, prefix = ""): string[] =>
    typeof o === "object" && o !== null ? Object.entries(o).flatMap(([k, v]) => collect(v, prefix ? `${prefix}.${k}` : k)) : [prefix];

  it("keeps every message in Bengali without English leftovers in the rule type names", () => {
    expect(collect(bn)).toEqual(collect(en));
    expect(JSON.stringify(bn.segments.ruleTypes)).not.toMatch(/Repeat|spenders|Inactive/);
  });
});
