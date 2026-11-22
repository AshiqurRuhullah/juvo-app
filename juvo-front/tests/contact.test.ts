import { describe, expect, it } from "vitest";
import { canArchive, contactLines, emptyContact, hasErrors, isStatus, looksLikeMarkup, noteIsValid, sendFailure, statusChoices, toContactBody, toInboxQuery, validateContact, whatsAppHref } from "@/modules/contact/logic";

const good = { name: "Nadia Rahman", mobile: "01712345678", email: "", subject: "", message: "Where is my parcel?" };

describe("contact form checks", () => {
  it("accepts a mobile only, an email only, or both", () => {
    expect(hasErrors(validateContact(good))).toBe(false);
    expect(hasErrors(validateContact({ ...good, mobile: "", email: "a@b.co" }))).toBe(false);
    expect(hasErrors(validateContact({ ...good, email: "a@b.co" }))).toBe(false);
  });
  it("needs at least one of mobile or email", () => {
    const e = validateContact({ ...good, mobile: " ", email: "" });
    expect(e.mobile).toBe("contactNeeded");
    expect(e.email).toBe("contactNeeded");
  });
  it("accepts Bengali digits in the mobile and sends ASCII", () => {
    const form = { ...good, mobile: "০১৭১২-৩৪৫৬৭৮" };
    expect(validateContact(form).mobile).toBeUndefined();
    expect(toContactBody(form, "/contact", "").mobile).toBe("01712345678");
  });
  it("rejects bad mobile and email", () => {
    expect(validateContact({ ...good, mobile: "12345" }).mobile).toBe("mobile");
    expect(validateContact({ ...good, email: "nope" }).email).toBe("email");
  });
  it("limits the message to 5 to 4000 characters", () => {
    expect(validateContact({ ...good, message: "   " }).message).toBe("required");
    expect(validateContact({ ...good, message: "hi" }).message).toBe("tooShort");
    expect(validateContact({ ...good, message: "x".repeat(4001) }).message).toBe("tooLong");
    expect(validateContact({ ...good, message: "x".repeat(4000) }).message).toBeUndefined();
  });
  it("limits name and subject", () => {
    expect(validateContact({ ...good, name: "" }).name).toBe("required");
    expect(validateContact({ ...good, name: "n".repeat(121) }).name).toBe("tooLong");
    expect(validateContact({ ...good, subject: "s".repeat(201) }).subject).toBe("tooLong");
  });
  it("rejects markup the way the API does", () => {
    for (const text of ["<b>hi</b>", "a </script>", "<!-- x", "<?php", "x <a href>"]) expect(looksLikeMarkup(text)).toBe(true);
    for (const text of ["2 < 3", "a<3", "I <3 this", "price <5 taka"]) expect(looksLikeMarkup(text)).toBe(false);
    expect(validateContact({ ...good, message: "hello <script>" }).message).toBe("markup");
    expect(validateContact({ ...good, name: "<b>x" }).name).toBe("markup");
    expect(validateContact({ ...good, subject: "<i>x" }).subject).toBe("markup");
  });
  it("builds the body with nulls for empty optional fields and the honeypot", () => {
    expect(toContactBody({ ...emptyContact(), name: " A ", email: " a@b.co ", message: " hello there " }, "/contact", "")).toEqual({
      name: "A", mobile: null, email: "a@b.co", subject: null, message: "hello there", sourcePage: "/contact", website: "",
    });
  });
});

describe("send failures", () => {
  it("tells the three refusals apart", () => {
    expect(sendFailure(429, "CONTENT.RATE_LIMITED")).toBe("limited");
    expect(sendFailure(429, "GENERAL.TOO_MANY_REQUESTS")).toBe("tooMany");
    expect(sendFailure(400, "CONTENT.INVALID")).toBe("invalid");
    expect(sendFailure(500, undefined)).toBe("other");
    expect(sendFailure(undefined, undefined)).toBe("other");
  });
});

describe("WhatsApp link", () => {
  it("makes a Bangladesh link from a local number", () => {
    expect(whatsAppHref("01712-345678")).toBe("https://wa.me/8801712345678");
    expect(whatsAppHref("+880 1712 345678")).toBe("https://wa.me/8801712345678");
    expect(whatsAppHref("")).toBeNull();
    expect(whatsAppHref(null)).toBeNull();
  });
});

describe("inbox", () => {
  it("builds the list query", () => {
    expect(toInboxQuery({}, { page: 1, pageSize: 10 })).toEqual({ Page: 1, PageSize: 10 });
    expect(toInboxQuery({ status: "New" }, { page: 2, pageSize: 10, search: " late " })).toEqual({ Page: 2, PageSize: 10, Search: "late", status: "New" });
    expect(toInboxQuery({ status: "bogus" }, { page: 1, pageSize: 10 })).toEqual({ Page: 1, PageSize: 10 });
  });
  it("knows the statuses", () => {
    expect(isStatus("Read")).toBe(true);
    expect(isStatus("Done")).toBe(false);
    expect(isStatus(undefined)).toBe(false);
  });
  it("offers the other statuses and archive only when not archived", () => {
    expect(statusChoices("New")).toEqual(["Read", "Replied"]);
    expect(statusChoices("Archived")).toEqual(["New", "Read", "Replied"]);
    expect(canArchive("Read")).toBe(true);
    expect(canArchive("Archived")).toBe(false);
  });
  it("checks a note", () => {
    expect(noteIsValid("  ")).toBe(false);
    expect(noteIsValid("called her")).toBe(true);
    expect(noteIsValid("x".repeat(4001))).toBe(false);
  });
  it("lists contact lines that exist", () => {
    expect(contactLines({ mobile: "+8801*****678", email: null })).toEqual(["+8801*****678"]);
    expect(contactLines({ mobile: null, email: null })).toEqual([]);
  });
});
