import { describe, expect, it } from "vitest";
import {
  BADGE_KEYS, PAGE_KEYS, brandToForm, brandToRequest, canPublishForm, emptyBrandForm, emptyPageForm, hasErrors, keyFromPublicSegment, nextSortOrder, pageIsDirty, pageToForm, pickText,
  publicPath, socialRows, validateBadge, validateBrand, validatePage, validateSocialInput,
} from "@/modules/content/logic";
import { markdownToText, parseInline, parseMarkdown, safeHref } from "@/modules/content/markdown";

describe("page addresses", () => {
  it("maps the six keys to public paths", () => {
    expect(PAGE_KEYS.map(publicPath)).toEqual(["/about", "/faq", "/delivery-policy", "/return-policy", "/privacy", "/terms"]);
  });
  it("reads a public segment back to its key", () => {
    expect(keyFromPublicSegment("privacy")).toBe("privacy-policy");
    expect(keyFromPublicSegment("terms")).toBe("terms");
    expect(keyFromPublicSegment("privacy-policy")).toBeNull();
    expect(keyFromPublicSegment("shop")).toBeNull();
  });
});

describe("page form", () => {
  it("turns nulls into empty strings and detects changes", () => {
    const saved = pageToForm({ titleEn: "About", titleBn: null, bodyEn: "x", bodyBn: null, metaTitleEn: null, metaTitleBn: null, metaDescriptionEn: null, metaDescriptionBn: null });
    expect(saved.titleBn).toBe("");
    expect(pageIsDirty(saved, saved)).toBe(false);
    expect(pageIsDirty({ ...saved, bodyEn: "y" }, saved)).toBe(true);
  });
  it("publishes only with an English title and body", () => {
    expect(canPublishForm(emptyPageForm())).toBe(false);
    expect(canPublishForm({ ...emptyPageForm(), titleEn: "A", bodyEn: "  " })).toBe(false);
    expect(canPublishForm({ ...emptyPageForm(), titleEn: "A", bodyEn: "B" })).toBe(true);
  });
  it("checks the limits", () => {
    expect(hasErrors(validatePage(emptyPageForm()))).toBe(false);
    expect(validatePage({ ...emptyPageForm(), titleEn: "a".repeat(201) }).titleEn).toBe("tooLong");
    expect(validatePage({ ...emptyPageForm(), metaDescriptionBn: "a".repeat(401) }).metaDescriptionBn).toBe("tooLong");
    expect(validatePage({ ...emptyPageForm(), bodyEn: "a".repeat(30001) }).bodyEn).toBe("tooLong");
    expect(hasErrors(validatePage({ ...emptyPageForm(), bodyEn: "a".repeat(30000) }))).toBe(false);
  });
});

describe("pickText", () => {
  it("uses the language and falls back to English", () => {
    expect(pickText({ en: "Hello", bn: "হ্যালো" }, "bn")).toBe("হ্যালো");
    expect(pickText({ en: "Hello", bn: null }, "bn")).toBe("Hello");
    expect(pickText({ en: "Hello", bn: "  " }, "bn")).toBe("Hello");
    expect(pickText({ en: "Hello", bn: "x" }, "en")).toBe("Hello");
  });
  it("is null when empty", () => {
    expect(pickText({ en: null, bn: "x" }, "en")).toBeNull();
    expect(pickText({ en: " ", bn: null }, "bn")).toBeNull();
    expect(pickText(null, "en")).toBeNull();
  });
});

describe("brand form", () => {
  const ok = { ...emptyBrandForm(), businessName: "Juvo BD" };
  it("needs a business name", () => {
    expect(validateBrand(emptyBrandForm()).businessName).toBe("required");
    expect(hasErrors(validateBrand(ok))).toBe(false);
    expect(validateBrand({ ...ok, businessName: "a".repeat(121) }).businessName).toBe("tooLong");
  });
  it("checks phone and e-mail loosely", () => {
    expect(validateBrand({ ...ok, phone: "02-5551234" }).phone).toBeUndefined();
    expect(validateBrand({ ...ok, phone: "+880 (2) 555" }).phone).toBeUndefined();
    expect(validateBrand({ ...ok, phone: "call me" }).phone).toBe("phone");
    expect(validateBrand({ ...ok, phone: "12345" }).phone).toBe("phone");
    expect(validateBrand({ ...ok, email: "a@b.co" }).email).toBeUndefined();
    expect(validateBrand({ ...ok, email: "nope" }).email).toBe("email");
  });
  it("wants latitude and longitude together and in range", () => {
    expect(validateBrand({ ...ok, latitude: "23.8" }).longitude).toBe("pair");
    expect(validateBrand({ ...ok, longitude: "90.4" }).latitude).toBe("pair");
    expect(hasErrors(validateBrand({ ...ok, latitude: "23.8103", longitude: "90.4125" }))).toBe(false);
    expect(validateBrand({ ...ok, latitude: "91", longitude: "90" }).latitude).toBe("coordinate");
    expect(validateBrand({ ...ok, latitude: "23", longitude: "181" }).longitude).toBe("coordinate");
    expect(validateBrand({ ...ok, latitude: "abc", longitude: "90" }).latitude).toBe("coordinate");
  });
  it("builds the save body: blanks become null, the logo is kept", () => {
    const body = brandToRequest({ ...ok, phone: " 0255 5123 ", addressEn: "", latitude: "23.5", longitude: "90.5" }, "logos/a.webp");
    expect(body).toMatchObject({ businessName: "Juvo BD", phone: "0255 5123", addressEn: null, latitude: 23.5, longitude: 90.5, logoFileRef: "logos/a.webp" });
    expect(brandToRequest(ok, null)).toMatchObject({ latitude: null, longitude: null, logoFileRef: null });
  });
  it("fills the form from a saved profile", () => {
    const form = brandToForm({ businessName: "J", logoFileRef: null, addressEn: null, addressBn: "ঢাকা", phone: null, whatsApp: null, email: "a@b.co", workingHoursEn: null, workingHoursBn: null, latitude: 23.8, longitude: "90.4" });
    expect(form).toMatchObject({ businessName: "J", addressEn: "", addressBn: "ঢাকা", latitude: "23.8", longitude: "90.4" });
  });
});

describe("social links", () => {
  it("accepts https addresses with a dotted host", () => {
    expect(validateSocialInput("Facebook", "https://facebook.com/juvobd")).toBeNull();
    expect(validateSocialInput("Instagram", "  https://instagram.com/juvo ")).toBeNull();
  });
  it("refuses http, other schemes, hosts without a dot and user info", () => {
    expect(validateSocialInput("Facebook", "http://facebook.com/x")).toBe("https");
    expect(validateSocialInput("Facebook", "javascript:alert(1)")).toBe("invalid");
    expect(validateSocialInput("Facebook", "facebook.com/x")).toBe("invalid");
    expect(validateSocialInput("Facebook", "https://localhost/x")).toBe("invalid");
    expect(validateSocialInput("Facebook", "https://user:pw@facebook.com")).toBe("invalid");
    expect(validateSocialInput("Facebook", "")).toBe("required");
  });
  it("lets WhatsApp take a mobile number", () => {
    expect(validateSocialInput("WhatsApp", "01712345678")).toBeNull();
    expect(validateSocialInput("WhatsApp", "+880 1712-345678")).toBeNull();
    expect(validateSocialInput("Facebook", "01712345678")).toBe("invalid");
    expect(validateSocialInput("WhatsApp", "12")).toBe("invalid");
  });
  it("always lists the five platforms, saved ones first in their order", () => {
    const rows = socialRows([{ platform: "Instagram", sortOrder: 0 }, { platform: "Facebook", sortOrder: 1 }]);
    expect(rows.map((r) => r.platform)).toEqual(["Instagram", "Facebook", "TikTok", "YouTube", "WhatsApp"]);
    expect(rows[0]!.saved).not.toBeNull();
    expect(rows[2]!.saved).toBeNull();
    expect(socialRows([]).map((r) => r.platform)).toEqual(["Facebook", "Instagram", "TikTok", "YouTube", "WhatsApp"]);
  });
  it("puts a new link after the last one", () => {
    expect(nextSortOrder([])).toBe(0);
    expect(nextSortOrder([{ sortOrder: 0 }, { sortOrder: "3" }])).toBe(4);
    expect(nextSortOrder([{ sortOrder: 1000 }])).toBe(1000);
  });
});

describe("badges", () => {
  it("has the four fixed badges", () => {
    expect(BADGE_KEYS).toHaveLength(4);
  });
  it("needs English text only when switched on", () => {
    expect(hasErrors(validateBadge({ textEn: "", textBn: "", isEnabled: false }))).toBe(false);
    expect(validateBadge({ textEn: " ", textBn: "বাংলা", isEnabled: true }).textEn).toBe("needEnglish");
    expect(hasErrors(validateBadge({ textEn: "Secure", textBn: "", isEnabled: true }))).toBe(false);
    expect(validateBadge({ textEn: "a".repeat(161), textBn: "", isEnabled: false }).textEn).toBe("tooLong");
    expect(validateBadge({ textEn: "ok", textBn: "b".repeat(161), isEnabled: true }).textBn).toBe("tooLong");
  });
});

describe("markdown", () => {
  it("reads headings, paragraphs and lists", () => {
    const blocks = parseMarkdown("# Title\n\nFirst line\nsecond line\n\n- one\n- two\n\n1. a\n2. b\n\n### Small");
    expect(blocks.map((b) => b.type)).toEqual(["heading", "paragraph", "list", "list", "heading"]);
    expect(blocks[0]).toMatchObject({ level: 1 });
    expect(blocks[1]).toMatchObject({ children: [{ type: "text", text: "First line second line" }] });
    expect(blocks[2]).toMatchObject({ ordered: false });
    expect((blocks[2] as { items: unknown[] }).items).toHaveLength(2);
    expect(blocks[3]).toMatchObject({ ordered: true });
    expect(blocks[4]).toMatchObject({ level: 3 });
  });
  it("reads bold, italic and links", () => {
    expect(parseInline("a **b** *c* [d](https://x.com/y)")).toEqual([
      { type: "text", text: "a " },
      { type: "strong", children: [{ type: "text", text: "b" }] },
      { type: "text", text: " " },
      { type: "em", children: [{ type: "text", text: "c" }] },
      { type: "text", text: " " },
      { type: "link", href: "https://x.com/y", children: [{ type: "text", text: "d" }] },
    ]);
  });
  it("shows an unmatched marker as typed and keeps snake_case", () => {
    expect(parseInline("2 * 3 and snake_case_name")).toEqual([{ type: "text", text: "2 * 3 and snake_case_name" }]);
    expect(parseInline("**open")).toEqual([{ type: "text", text: "**open" }]);
  });
  it("keeps only safe link addresses", () => {
    expect(safeHref("https://a.com")).toBe("https://a.com");
    expect(safeHref("/faq")).toBe("/faq");
    expect(safeHref("mailto:a@b.co")).toBe("mailto:a@b.co");
    expect(safeHref("tel:+8801712345678")).toBe("tel:+8801712345678");
    for (const bad of ["javascript:alert(1)", " JavaScript:alert(1)", "data:text/html,x", "vbscript:x", "//evil.com", "file:///c", "https://a.com/\"x", ""]) {
      expect(safeHref(bad), bad).toBeNull();
    }
    expect(parseInline("[x](javascript:alert(1))")).toEqual([{ type: "text", text: "x" }]);
  });
  it("never turns raw HTML into markup (it stays text for the renderer to escape)", () => {
    const blocks = parseMarkdown("<script>alert(1)</script>\n\n<img src=x onerror=alert(1)>");
    expect(blocks.every((b) => b.type === "paragraph")).toBe(true);
    expect(JSON.stringify(blocks)).not.toContain('"link"');
  });
  it("gives plain text without markers", () => {
    expect(markdownToText("# Hi\n\nSome **bold** [link](https://a.com)\n\n- x\n- y")).toBe("Hi Some bold link x y");
  });
});
