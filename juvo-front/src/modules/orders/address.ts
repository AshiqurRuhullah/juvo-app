import { toNumber } from "@/modules/catalog/format";
import type { Address } from "@/modules/store/api";
import type { CheckoutAddress } from "./api";
import { isBdMobile, normalizeDigits } from "./status";

/** What a person types for a delivery address: Bangladesh (division, district, upazila) or another country. */
export type AddressDraft = {
  mode: "bd" | "abroad";
  recipientName: string;
  phone: string;
  divisionId: string;
  districtId: string;
  upazilaId: string;
  area: string;
  addressLine: string;
  landmark: string;
  countryCode: string;
  city: string;
  stateProvince: string;
  postalCode: string;
  latitude: number | null;
  longitude: number | null;
};

export const emptyDraft = (): AddressDraft => ({
  mode: "bd",
  recipientName: "",
  phone: "",
  divisionId: "",
  districtId: "",
  upazilaId: "",
  area: "",
  addressLine: "",
  landmark: "",
  countryCode: "",
  city: "",
  stateProvince: "",
  postalCode: "",
  latitude: null,
  longitude: null,
});

export function draftFromAddress(address: Address): AddressDraft {
  const abroad = address.countryCode !== "BD";
  return {
    mode: abroad ? "abroad" : "bd",
    recipientName: address.recipientName,
    phone: address.phone,
    divisionId: address.divisionId ?? "",
    districtId: address.districtId ?? "",
    upazilaId: address.upazilaId ?? "",
    area: address.area ?? "",
    addressLine: address.addressLine,
    landmark: address.landmark ?? "",
    countryCode: abroad ? address.countryCode : "",
    city: address.city ?? "",
    stateProvince: address.stateProvince ?? "",
    postalCode: address.postalCode ?? "",
    latitude: address.latitude != null ? toNumber(address.latitude) : null,
    longitude: address.longitude != null ? toNumber(address.longitude) : null,
  };
}

const orNull = (value: string) => (value.trim() ? value.trim() : null);

/** The address in the shape the order endpoints take. */
export function toCheckoutAddress(draft: AddressDraft): CheckoutAddress {
  const bd = draft.mode === "bd";
  return {
    recipientName: draft.recipientName.trim(),
    phone: orNull(normalizeDigits(draft.phone).replace(/[\s-]/g, "")),
    countryCode: bd ? "BD" : draft.countryCode || null,
    districtId: bd ? draft.districtId || null : null,
    upazilaId: bd ? draft.upazilaId || null : null,
    area: bd ? orNull(draft.area) : null,
    addressLine: draft.addressLine.trim(),
    landmark: orNull(draft.landmark),
    stateProvince: bd ? null : orNull(draft.stateProvince),
    city: bd ? null : orNull(draft.city),
    postalCode: bd ? null : orNull(draft.postalCode),
    latitude: bd ? draft.latitude : null,
    longitude: bd ? draft.longitude : null,
  };
}

/** Just the place, for the delivery estimate (the quote needs no street or name). */
export function placeOf(draft: AddressDraft): { countryCode: string | null; districtId: string | null; upazilaId: string | null } | null {
  if (draft.mode === "bd") return draft.districtId ? { countryCode: "BD", districtId: draft.districtId, upazilaId: draft.upazilaId || null } : null;
  return draft.countryCode ? { countryCode: draft.countryCode, districtId: null, upazilaId: null } : null;
}

export type AddressError = "recipient" | "phone" | "district" | "landmark" | "country" | "city" | "line";

/** Which fields are missing or wrong, as keys the form turns into its own wording. */
export function addressErrors(draft: AddressDraft, options: { needPhone: boolean }): Partial<Record<AddressError, true>> {
  const errors: Partial<Record<AddressError, true>> = {};
  if (!draft.recipientName.trim()) errors.recipient = true;
  const phone = normalizeDigits(draft.phone).replace(/[\s-]/g, "");
  if (draft.mode === "bd") {
    if ((options.needPhone || phone) && !isBdMobile(phone)) errors.phone = true;
    if (!draft.districtId) errors.district = true;
  } else {
    if ((options.needPhone || phone) && phone.replace(/\D/g, "").length < 6) errors.phone = true;
    if (!draft.countryCode) errors.country = true;
    if (!draft.city.trim()) errors.city = true;
  }
  if (!draft.addressLine.trim()) errors.line = true;
  return errors;
}
