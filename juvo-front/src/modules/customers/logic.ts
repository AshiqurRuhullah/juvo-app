import type { Schemas } from "@/lib/api/types";

/** Pure rules of the staff customer screens (no React, no network), so they can be tested on their own (docs/storefront-api.md, staff endpoints). */

export type ProfileLevel = Schemas["ProfileLevel"];
export const PROFILE_LEVELS: ProfileLevel[] = ["Guest", "Quick", "Full"];

export type CustomerStatus = "active" | "inactive";
export const CUSTOMER_STATUSES: CustomerStatus[] = ["active", "inactive"];

/** Consent types in the order the detail shows them (the API sends one row per type, newest decision each). */
export const CONSENT_TYPES = ["MarketingSms", "MarketingEmail", "PrivacyPolicy"] as const;
export type ConsentKind = (typeof CONSENT_TYPES)[number];
export const isKnownConsent = (type: string): type is ConsentKind => (CONSENT_TYPES as readonly string[]).includes(type);

export type CustomerFilters = { status?: string; level?: string };

/** The list query for GET /customers. Only the filters the API has (isActive, level) plus paging and search; the API does not sort. */
export function toCustomerQuery(filters: CustomerFilters, options: { page: number; pageSize: number; search?: string }): Record<string, string | number | boolean> {
  const q: Record<string, string | number | boolean> = { Page: options.page, PageSize: options.pageSize };
  const search = options.search?.trim();
  if (search) q.Search = search;
  if (filters.status === "active") q.isActive = true;
  else if (filters.status === "inactive") q.isActive = false;
  if (filters.level && (PROFILE_LEVELS as string[]).includes(filters.level)) q.level = filters.level;
  return q;
}

/** True when a mobile number or email arrived masked from the API (it replaces the hidden part with asterisks). */
export const isMasked = (value: string | null | undefined): boolean => !!value && value.includes("*");

/** True when anything personal in a customer row is hidden (staff without `customers.customer.view-pii`). */
export const listIsMasked = (rows: ReadonlyArray<{ mobile: string; email?: string | null }>): boolean => rows.some((r) => isMasked(r.mobile) || isMasked(r.email));

export type Tone = "success" | "danger" | "info" | "neutral" | "outline";

export function statusTone(isActive: boolean): Tone {
  return isActive ? "success" : "danger";
}

export function levelTone(level: ProfileLevel): Tone {
  switch (level) {
    case "Full":
      return "success";
    case "Quick":
      return "info";
    case "Guest":
      return "outline";
  }
}

/** What this staff member may do on the customer screens. Display only: the API checks again. */
export type CustomerRights = { view: boolean; block: boolean; viewPii: boolean; viewOrderStats: boolean };

export type CustomerAction = "deactivate" | "activate";

/** The one action that makes sense now (and is allowed): deactivate an active customer, activate a deactivated one. */
export function allowedActions(isActive: boolean, rights: Pick<CustomerRights, "block">): CustomerAction[] {
  if (!rights.block) return [];
  return [isActive ? "deactivate" : "activate"];
}

/** Which consents are shown as "given", "not given" or "never asked" (a null time means the customer was never asked). */
export type ConsentState = "granted" | "withdrawn" | "never";
export function consentState(c: { granted: boolean; at: string | null }): ConsentState {
  if (c.granted) return "granted";
  return c.at ? "withdrawn" : "never";
}

/** First letters for the round avatar: the name's initials, or "?" when the customer has no name (guest). */
export function initials(name: string | null | undefined): string {
  const words = (name ?? "").trim().split(/\s+/).filter(Boolean);
  if (words.length === 0) return "?";
  return words
    .slice(0, 2)
    .map((w) => [...w][0]!.toUpperCase())
    .join("");
}
