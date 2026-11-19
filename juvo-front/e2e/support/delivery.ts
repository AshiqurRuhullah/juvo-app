import { call } from "./cod";
import { adminAccess } from "./returns";

/**
 * Helpers of the delivery-zone specs: zones, districts and countries made and read through the real API. Specs create their own zones with
 * a random code and only use districts that no zone holds (so the seeded Dhaka and city zones keep pricing the sample orders of other specs).
 */

export type ZoneRef = { id: string; code: string; nameEn: string };
export type ZoneApi = {
  id: string; code: string; nameEn: string; nameBn: string | null; kind: string; isActive: boolean; isFallback: boolean; daysMin: number; daysMax: number;
  baseFee: number; freeDeliveryThreshold: number | null; remoteSurcharge: number; perExtraKgFee: number; maxWeightGrams: number | null;
  slabs: { upToGrams: number; extraFee: number }[]; areas: { districtId: string; upazilaId: string | null; isRemote: boolean }[]; countryCodes: string[];
};
export type DistrictRef = { id: string; nameEn: string };

const token = () => adminAccess();

export async function listZones(): Promise<ZoneApi[]> {
  return (await call<ZoneApi[]>("GET", "/api/v1/delivery/zones", { token: await token(), expect: 200 })).json;
}

export async function readZone(id: string): Promise<ZoneApi> {
  return (await call<ZoneApi>("GET", `/api/v1/delivery/zones/${id}`, { token: await token(), expect: 200 })).json;
}

export async function zoneExists(id: string): Promise<boolean> {
  return (await call("GET", `/api/v1/delivery/zones/${id}`, { token: await token() })).status === 200;
}

/** Districts held by no zone, picked at random so reruns do not collide. */
export async function freeDistricts(count: number): Promise<DistrictRef[]> {
  const zones = await listZones();
  const taken = new Set(zones.flatMap((z) => z.areas.map((a) => a.districtId)));
  const all = (await call<DistrictRef[]>("GET", "/api/v1/store/geo/districts", { expect: 200 })).json;
  const free = all.filter((d) => !taken.has(d.id)).sort(() => Math.random() - 0.5);
  if (free.length < count) throw new Error(`Only ${free.length} districts are held by no zone.`);
  return free.slice(0, count);
}

export async function makeZone(options: { kind?: "Domestic" | "International"; name?: string; districts?: { id: string; remote?: boolean }[]; rates?: Partial<Pick<ZoneApi, "baseFee" | "freeDeliveryThreshold" | "remoteSurcharge" | "perExtraKgFee" | "maxWeightGrams" | "slabs">> } = {}): Promise<ZoneRef> {
  const suffix = Math.random().toString(36).slice(2, 7).toUpperCase();
  const code = `E2E_${suffix}`;
  const nameEn = options.name ?? `E2E Zone ${suffix}`;
  const created = await call<{ id: string }>("POST", "/api/v1/delivery/zones", { token: await token(), body: { code, nameEn, nameBn: null, kind: options.kind ?? "Domestic", isActive: true, isFallback: false, sortOrder: 90, daysMin: 1, daysMax: 3 }, expect: 201 });
  const id = created.json.id;
  if (options.districts?.length) {
    await call("PUT", `/api/v1/delivery/zones/${id}/areas`, { token: await token(), body: { areas: options.districts.map((d) => ({ districtId: d.id, upazilaId: null, isRemote: d.remote ?? false })) }, expect: 200 });
  }
  if (options.rates) {
    const r = { baseFee: 60, freeDeliveryThreshold: null, remoteSurcharge: 0, perExtraKgFee: 0, maxWeightGrams: null, slabs: [], ...options.rates };
    await call("PUT", `/api/v1/delivery/zones/${id}/rates`, { token: await token(), body: r, expect: 200 });
  }
  return { id, code, nameEn };
}

export async function deleteZone(id: string) {
  await call("DELETE", `/api/v1/delivery/zones/${id}`, { token: await token() });
}

export type CountryApi = { id: string; code: string; nameEn: string; nameBn: string | null; phoneCode: string; isEnabled: boolean; zoneId: string | null };

export async function listCountries(): Promise<CountryApi[]> {
  return (await call<CountryApi[]>("GET", "/api/v1/delivery/countries", { token: await token(), expect: 200 })).json;
}

export async function saveCountry(c: CountryApi, patch: Partial<CountryApi>) {
  const body = { code: c.code, nameEn: c.nameEn, nameBn: c.nameBn, phoneCode: c.phoneCode, isEnabled: c.isEnabled, zoneId: c.zoneId, ...patch };
  await call("PUT", `/api/v1/delivery/countries/${c.id}`, { token: await token(), body, expect: 200 });
}

export type QuoteApi = { zoneCode: string; baseFee: number; weightFee: number; remoteSurcharge: number; freeDeliveryWaiver: number; total: number };

export async function quoteApi(districtId: string, subtotal: number, weightGrams: number) {
  return (await call<QuoteApi>("POST", "/api/v1/store/delivery/quote", { body: { countryCode: "BD", districtId, upazilaId: null, items: null, subtotal, weightGrams } })).json;
}

/** True when a courier flat charge or a running free-delivery offer would change quotes (then exact-number checks are skipped, not faked). */
export async function quotesAreModified(zoneDistrictId: string, expectedTotal: number, subtotal: number, weightGrams: number): Promise<boolean> {
  const q = await quoteApi(zoneDistrictId, subtotal, weightGrams);
  return Math.abs(Number(q.total) - expectedTotal) > 0.005;
}
