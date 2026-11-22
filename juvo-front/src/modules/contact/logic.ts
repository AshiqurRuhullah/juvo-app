/**
 * Pure rules of the contact page and the staff inbox (docs/content-api.md section 7). The checks before sending mirror the API's
 * (the API checks again and has the last word); the staff side holds the status list, which buttons make sense for which status,
 * and the list query.
 */
import { asciiDigits, validEmail, validMobile } from "@/modules/support/logic";

export const LIMITS = { name: 120, subject: 200, messageMin: 5, messageMax: 4000, note: 4000 } as const;

export const STATUSES = ["New", "Read", "Replied", "Archived"] as const;
export type MessageStatus = (typeof STATUSES)[number];

export const isStatus = (value: string | undefined | null): value is MessageStatus => !!value && (STATUSES as readonly string[]).includes(value);

// ---- the public form ---------------------------------------------------------------------------------------------

export type ContactFields = { name: string; mobile: string; email: string; subject: string; message: string };
export const emptyContact = (): ContactFields => ({ name: "", mobile: "", email: "", subject: "", message: "" });

export type FieldCode = "required" | "tooLong" | "tooShort" | "mobile" | "email" | "markup" | "contactNeeded";
export type ContactErrors = Partial<Record<keyof ContactFields, FieldCode>>;

/** The API refuses text where a `<` is followed by a letter, `/`, `!` or `?`. */
export const looksLikeMarkup = (text: string): boolean => /<[A-Za-z/!?]/.test(text);

export function validateContact(form: ContactFields): ContactErrors {
  const errors: ContactErrors = {};
  const name = form.name.trim();
  const mobile = form.mobile.trim();
  const email = form.email.trim();
  const subject = form.subject.trim();
  const message = form.message.trim();

  if (!name) errors.name = "required";
  else if (name.length > LIMITS.name) errors.name = "tooLong";
  else if (looksLikeMarkup(name)) errors.name = "markup";

  if (!mobile && !email) {
    errors.mobile = "contactNeeded";
    errors.email = "contactNeeded";
  } else {
    if (mobile && !validMobile(mobile)) errors.mobile = "mobile";
    if (email && !validEmail(email)) errors.email = "email";
  }

  if (subject.length > LIMITS.subject) errors.subject = "tooLong";
  else if (looksLikeMarkup(subject)) errors.subject = "markup";

  if (!message) errors.message = "required";
  else if (message.length < LIMITS.messageMin) errors.message = "tooShort";
  else if (message.length > LIMITS.messageMax) errors.message = "tooLong";
  else if (looksLikeMarkup(message)) errors.message = "markup";
  return errors;
}

export const hasErrors = (errors: object): boolean => Object.keys(errors).length > 0;

/** The request body. Bengali digits in the mobile become ASCII; empty optional fields are sent as null. */
export function toContactBody(form: ContactFields, sourcePage: string, honeypot: string) {
  const mobile = asciiDigits(form.mobile).replace(/[\s-]/g, "");
  return {
    name: form.name.trim(),
    mobile: mobile || null,
    email: form.email.trim() || null,
    subject: form.subject.trim() || null,
    message: form.message.trim(),
    sourcePage,
    website: honeypot,
  };
}

export type SendFailure = "invalid" | "tooMany" | "limited" | "other";

/** Which plain-words message a refused send gets: 429 with CONTENT.RATE_LIMITED (per mobile, email or address, per hour), other 429 (per minute), 400. */
export function sendFailure(status: number | undefined, code: string | undefined): SendFailure {
  if (status === 429) return code === "CONTENT.RATE_LIMITED" ? "limited" : "tooMany";
  if (status === 400) return "invalid";
  return "other";
}

/** A WhatsApp link from the brand number (digits only; a leading 0 means Bangladesh). Null when there are no digits. */
export function whatsAppHref(value: string | null | undefined): string | null {
  const digits = asciiDigits(value ?? "").replace(/\D/g, "");
  if (!digits) return null;
  return `https://wa.me/${digits.startsWith("0") ? `88${digits}` : digits}`;
}

// ---- the staff inbox ------------------------------------------------------------------------------------------

export type InboxFilters = { status?: string };

export function toInboxQuery(filters: InboxFilters, options: { page: number; pageSize: number; search?: string }): Record<string, string | number> {
  const q: Record<string, string | number> = { Page: options.page, PageSize: options.pageSize };
  if (options.search?.trim()) q.Search = options.search.trim();
  if (isStatus(filters.status)) q.status = filters.status;
  return q;
}

/** The status buttons offered for a message: every other status (the API allows any change). Archived is offered as "restore" via New/Read. */
export function statusChoices(current: MessageStatus): MessageStatus[] {
  return STATUSES.filter((s) => s !== current && s !== "Archived");
}

export const canArchive = (current: MessageStatus): boolean => current !== "Archived";

export const noteIsValid = (note: string): boolean => note.trim().length > 0 && note.trim().length <= LIMITS.note;

/** A mobile or email is masked when the API says so; it shows as sent. */
export const contactLines = (item: { mobile: string | null; email: string | null }): string[] => [item.mobile, item.email].filter((x): x is string => !!x);
