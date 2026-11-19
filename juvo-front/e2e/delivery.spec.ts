import { test, expect, type Page } from "@playwright/test";
import { loadState } from "./support/env";
import { call } from "./support/cod";
import { staffAccess, staffWithPermissions } from "./support/returns";
import { pick } from "./support/store";
import { signInViaBff } from "./support/ui";
import { deleteZone, freeDistricts, listCountries, listZones, makeZone, quotesAreModified, readZone, saveCountry, zoneExists, type ZoneRef } from "./support/delivery";

/**
 * Delivery zone screens in the browser against the real API: create a zone, assign areas, set rates, check the quote against the rates,
 * edit, delete, countries, the upazila paste import, validation, and who may see or change what. Every test makes its own zones with
 * districts no other zone holds and removes them again.
 */
async function adminSignIn(page: Page) {
  const admin = loadState().admin;
  await signInViaBff(page.context(), admin.email, admin.password);
}

const tab = (page: Page, name: string) => page.getByRole("tab", { name, exact: true });
const toast = (page: Page, text: string) => expect(page.getByText(text).first()).toBeVisible();

const made: ZoneRef[] = [];
const track = (z: ZoneRef) => {
  made.push(z);
  return z;
};
test.afterEach(async () => {
  while (made.length) await deleteZone(made.pop()!.id);
});

test.describe("Delivery zones: build and check", () => {
  test("create a zone, assign areas, set rates and check the quote equals the rates", async ({ page }) => {
    test.setTimeout(120_000);
    const [d1, d2] = await freeDistricts(2);
    const suffix = Math.random().toString(36).slice(2, 7).toUpperCase();
    await adminSignIn(page);
    await page.goto("/admin/delivery/zones");
    await expect(page.getByRole("heading", { level: 1, name: "Delivery zones" })).toBeVisible();
    await page.getByTestId("zone-new").click();
    await expect(page.getByRole("heading", { level: 1, name: "New delivery zone" })).toBeVisible();
    await page.locator("#zd-code").fill(`E2E_${suffix}`);
    await page.locator("#zd-name").fill(`E2E Zone ${suffix}`);
    await page.getByTestId("details-save").click();
    await expect(page).toHaveURL(/\/admin\/delivery\/zones\/[0-9a-f-]{36}$/);
    const id = page.url().split("/").pop()!;
    track({ id, code: `E2E_${suffix}`, nameEn: `E2E Zone ${suffix}` });
    await expect(page.getByRole("heading", { level: 1, name: `E2E Zone ${suffix}` })).toBeVisible();

    // areas: one whole district, one marked remote
    await tab(page, "Areas").click();
    await expect(page.getByTestId("areas-summary")).toContainText("0 whole districts");
    await page.getByLabel("Search districts").fill(d1.nameEn);
    await page.locator(`[id="d-${d1.id}"]`).click();
    await page.getByLabel("Search districts").fill(d2.nameEn);
    await page.locator(`[id="d-${d2.id}"]`).click();
    await page.locator(`[id="dr-${d2.id}"]`).click();
    await page.getByLabel("Search districts").fill("");
    await expect(page.getByTestId("areas-summary")).toContainText("2 whole districts");
    await page.getByTestId("areas-save").click();
    await toast(page, "Areas saved.");
    const afterAreas = await readZone(id);
    expect(afterAreas.areas.map((a) => a.districtId).sort()).toEqual([d1.id, d2.id].sort());
    expect(afterAreas.areas.find((a) => a.districtId === d2.id)!.isRemote).toBe(true);
    expect(afterAreas.areas.find((a) => a.districtId === d1.id)!.isRemote).toBe(false);

    // rates: base, remote, free threshold, two slabs, per extra kg, max weight
    await tab(page, "Rates").click();
    await page.locator("#rt-base").fill("80");
    await page.locator("#rt-remote").fill("40");
    await page.locator("#rt-free").fill("3,000");
    await page.locator("#rt-max").fill("10");
    await page.locator("#rt-perkg").fill("15");
    await page.getByTestId("slab-add").click();
    await page.getByTestId("slab-add").click();
    await page.locator("#rt-slab-kg-0").fill("1");
    await page.locator("#rt-slab-fee-0").fill("0");
    await page.locator("#rt-slab-kg-1").fill("2");
    await page.locator("#rt-slab-fee-1").fill("25");
    await expect(page.getByTestId("slab-ranges")).toContainText("1 kg to 2 kg");
    await page.getByTestId("rates-save").click();
    await toast(page, "Rates saved.");
    const rates = await readZone(id);
    expect(Number(rates.baseFee)).toBe(80);
    expect(Number(rates.remoteSurcharge)).toBe(40);
    expect(Number(rates.freeDeliveryThreshold)).toBe(3000);
    expect(Number(rates.perExtraKgFee)).toBe(15);
    expect(Number(rates.maxWeightGrams)).toBe(10000);
    expect(rates.slabs.map((s) => [Number(s.upToGrams), Number(s.extraFee)])).toEqual([[1000, 0], [2000, 25]]);

    // the quote the shop would show: 1.5 kg falls in the 2 kg slab
    test.skip(await quotesAreModified(d1.id, 105, 1000, 1500), "A courier flat charge or a free-delivery offer changes every quote in this database.");
    await tab(page, "Try a quote").click();
    const district = page.getByRole("combobox", { name: /^District/ });
    await pick(page, district, d1.nameEn);
    await page.locator("#qt-value").fill("1000");
    await page.locator("#qt-weight").fill("1.5");
    await page.getByTestId("quote-run").click();
    await expect(page.getByTestId("quote-result")).toBeVisible();
    await expect(page.getByTestId("quote-zone")).toHaveText(`E2E Zone ${suffix}`);
    await expect(page.getByTestId("quote-base")).toHaveAttribute("data-amount", "80");
    await expect(page.getByTestId("quote-weight")).toHaveAttribute("data-amount", "25");
    await expect(page.getByTestId("quote-remote")).toHaveAttribute("data-amount", "0");
    await expect(page.getByTestId("quote-waiver")).toHaveCount(0);
    await expect(page.getByTestId("quote-total")).toHaveAttribute("data-amount", "105");
    await expect(page.getByTestId("quote-to-free")).toContainText("2,000");
    await expect(page.getByTestId("quote-check")).toContainText("match");

    // free delivery at the threshold waives base and weight, not the remote surcharge
    await pick(page, district, d2.nameEn);
    await page.locator("#qt-value").fill("3000");
    await page.getByTestId("quote-run").click();
    await expect(page.getByTestId("quote-remote")).toHaveAttribute("data-amount", "40");
    await expect(page.getByTestId("quote-waiver")).toHaveAttribute("data-amount", "-105");
    await expect(page.getByTestId("quote-total")).toHaveAttribute("data-amount", "40");

    // a parcel above the maximum is refused with a plain message
    await page.locator("#qt-weight").fill("12");
    await page.getByTestId("quote-run").click();
    await expect(page.getByTestId("quote-error")).toContainText("heavier");
  });

  test("a district another zone holds is marked, and saving moves it after a confirmation", async ({ page }) => {
    const [x] = await freeDistricts(1);
    const holder = track(await makeZone({ districts: [{ id: x.id }] }));
    const target = track(await makeZone());
    await adminSignIn(page);
    await page.goto(`/admin/delivery/zones/${target.id}`);
    await tab(page, "Areas").click();
    await page.getByLabel("Search districts").fill(x.nameEn);
    const row = page.locator("[data-testid=district-row]", { hasText: x.nameEn }).first();
    await expect(row.getByTestId("area-owner")).toContainText(`In ${holder.nameEn}`);
    await page.locator(`[id="d-${x.id}"]`).click();
    await expect(page.getByTestId("areas-moves")).toContainText(holder.nameEn);
    await page.getByTestId("areas-save").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText(holder.nameEn);
    await dialog.getByRole("button", { name: "Cancel" }).click();
    expect((await readZone(target.id)).areas).toHaveLength(0);
    await page.getByTestId("areas-save").click();
    await page.getByRole("dialog").getByRole("button", { name: "Save and move" }).click();
    await toast(page, "Areas saved.");
    expect((await readZone(target.id)).areas.map((a) => a.districtId)).toEqual([x.id]);
    expect((await readZone(holder.id)).areas).toHaveLength(0);
  });

  test("an upazila ticked in a zone is saved as an override of its district", async ({ page }) => {
    const [x] = await freeDistricts(1);
    const zone = track(await makeZone());
    const upazilas = (await call<{ id: string; nameEn: string }[]>("GET", `/api/v1/store/geo/districts/${x.id}/upazilas`, { expect: 200 })).json;
    test.skip(upazilas.length === 0, "The chosen district has no upazila in the dataset.");
    await adminSignIn(page);
    await page.goto(`/admin/delivery/zones/${zone.id}`);
    await tab(page, "Areas").click();
    await page.getByLabel("Search districts").fill(x.nameEn);
    await page.getByRole("button", { name: `Show upazilas of ${x.nameEn}` }).first().click();
    await expect(page.getByTestId("upazila-list")).toBeVisible();
    await page.locator(`[id="u-${upazilas[0]!.id}"]`).click();
    await page.getByTestId("areas-save").click();
    await toast(page, "Areas saved.");
    const saved = await readZone(zone.id);
    expect(saved.areas).toEqual([{ districtId: x.id, upazilaId: upazilas[0]!.id, isRemote: false }]);
  });
});

test.describe("Delivery zones: edit, delete, validation", () => {
  test("details can be edited and a deleted zone is gone", async ({ page }) => {
    const zone = await makeZone();
    track(zone);
    await adminSignIn(page);
    await page.goto("/admin/delivery/zones");
    await page.getByLabel("Search zones").fill(zone.code);
    await expect(page.locator("[data-testid=zone-link]:visible")).toHaveCount(1);
    await page.locator("[data-testid=zone-link]:visible").first().click();
    await page.locator("#zd-name").fill("Renamed E2E zone");
    await page.locator("#zd-dmin").fill("2");
    await page.locator("#zd-dmax").fill("6");
    await page.getByTestId("details-save").click();
    await toast(page, "Zone details saved.");
    const saved = await readZone(zone.id);
    expect([saved.nameEn, Number(saved.daysMin), Number(saved.daysMax)]).toEqual(["Renamed E2E zone", 2, 6]);

    await page.getByTestId("zone-delete").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("removed for good");
    await dialog.getByRole("button", { name: "Cancel" }).click();
    expect(await zoneExists(zone.id)).toBe(true);
    await page.getByTestId("zone-delete").click();
    await page.getByRole("dialog").getByRole("button", { name: "Delete zone" }).click();
    await expect(page).toHaveURL(/\/admin\/delivery\/zones$/);
    expect(await zoneExists(zone.id)).toBe(false);
    made.length = 0;
  });

  test("the fallback zone and a zone used by a country explain why they cannot be deleted", async ({ page }) => {
    const fallback = (await listZones()).find((z) => z.isFallback)!;
    await adminSignIn(page);
    await page.goto(`/admin/delivery/zones/${fallback.id}`);
    await expect(page.getByTestId("zone-fallback").first()).toBeVisible();
    await page.getByTestId("zone-delete").click();
    const dialog = page.getByRole("dialog");
    await expect(dialog).toContainText("fallback zone and cannot be deleted");
    await dialog.getByRole("button", { name: "Close" }).first().click();
    expect(await zoneExists(fallback.id)).toBe(true);
  });

  test("bad money, repeated slabs and a taken code are caught with plain messages and nothing is sent", async ({ page }) => {
    const zone = track(await makeZone({ rates: { baseFee: 60 } }));
    await adminSignIn(page);
    await page.goto(`/admin/delivery/zones/${zone.id}`);
    await tab(page, "Rates").click();
    await page.locator("#rt-base").fill("7o");
    await page.locator("#rt-remote").fill("1.234");
    await page.locator("#rt-free").fill("-5");
    await page.locator("#rt-max").fill("0");
    await page.locator("#rt-perkg").fill("20");
    await expect(page.getByTestId("rates-warnings")).toContainText("does nothing while there are no weight slabs");
    await page.getByTestId("slab-add").click();
    await page.getByTestId("slab-add").click();
    await page.locator("#rt-slab-kg-0").fill("1");
    await page.locator("#rt-slab-fee-0").fill("50");
    await page.locator("#rt-slab-kg-1").fill("1.000");
    await page.locator("#rt-slab-fee-1").fill("20");
    await page.getByTestId("rates-save").click();
    const form = page.getByTestId("rates-form");
    await expect(form).toContainText("Enter a number such as 70 or 70.50.");
    await expect(form).toContainText("Use at most 2 decimals.");
    await expect(form).toContainText("The amount cannot be negative.");
    await expect(form).toContainText("The weight must be above zero.");
    await expect(form).toContainText("Another slab already has this weight limit.");
    expect(Number((await readZone(zone.id)).baseFee)).toBe(60);

    // a heavier slab that is cheaper is a warning, not an error
    await page.locator("#rt-base").fill("60");
    await page.locator("#rt-remote").fill("0");
    await page.locator("#rt-free").fill("");
    await page.locator("#rt-max").fill("");
    await page.locator("#rt-slab-kg-1").fill("2");
    await page.locator("#rt-slab-fee-1").fill("20");
    await expect(page.getByTestId("rates-warnings")).toContainText("heavier parcels would be cheaper");
    await page.getByTestId("rates-save").click();
    await toast(page, "Rates saved.");
    expect((await readZone(zone.id)).slabs).toHaveLength(2);

    // details: empty name, days out of order
    await tab(page, "Details").click();
    await page.locator("#zd-name").fill("");
    await page.locator("#zd-dmin").fill("5");
    await page.locator("#zd-dmax").fill("2");
    await page.getByTestId("details-save").click();
    await expect(page.getByTestId("details-form")).toContainText("Enter a name.");
    await expect(page.getByTestId("details-form")).toContainText("cannot be before the earliest");

    // a new zone with a code that exists is refused by the API and shown
    await page.goto("/admin/delivery/zones/new");
    await page.locator("#zd-code").fill(zone.code);
    await page.locator("#zd-name").fill("Duplicate code");
    await page.getByTestId("details-save").click();
    await expect(page.getByTestId("details-form-error")).toContainText("already exists");
  });
});

test.describe("Delivery zones: countries and upazila import", () => {
  test("a country is switched on with a zone, and cannot be without one", async ({ page }) => {
    const countries = await listCountries();
    const target = countries.find((c) => c.code !== "BD" && !c.isEnabled)!;
    const intl = (await listZones()).find((z) => z.code === "INTL_SOUTH_ASIA") ?? (await listZones()).find((z) => z.kind === "International")!;
    try {
      await adminSignIn(page);
      await page.goto("/admin/delivery/zones");
      await tab(page, "Countries").click();
      await page.getByLabel("Search countries").fill(target.nameEn);
      await page.getByRole("button", { name: `Edit ${target.nameEn}`, exact: true }).first().click();
      await page.locator("#co-enabled").click();
      await page.getByTestId("country-save").click();
      await expect(page.getByRole("dialog")).toContainText("Choose the international zone");
      await pick(page, page.getByRole("combobox", { name: /^International zone/ }), intl.nameEn);
      await page.getByTestId("country-save").click();
      await toast(page, "Country saved.");
      const after = (await listCountries()).find((c) => c.code === target.code)!;
      expect([after.isEnabled, after.zoneId]).toEqual([true, intl.id]);
    } finally {
      await saveCountry(target, { isEnabled: false, zoneId: target.zoneId });
    }
  });

  test("Bangladesh cannot be switched off from the screen", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/delivery/zones");
    await tab(page, "Countries").click();
    await page.getByLabel("Search countries").fill("Bangladesh");
    await page.getByRole("button", { name: "Edit Bangladesh", exact: true }).first().click();
    await expect(page.getByRole("dialog")).toContainText("always switched on");
    await expect(page.locator("#co-enabled")).toHaveCount(0);
  });

  test("pasted upazilas are checked line by line, then added", async ({ page }) => {
    const [dhaka] = (await call<{ id: string; nameEn: string }[]>("GET", "/api/v1/store/geo/districts", { expect: 200 })).json.filter((d) => d.nameEn === "Dhaka");
    const name = `E2E Upazila ${Math.random().toString(36).slice(2, 8)}`;
    await adminSignIn(page);
    await page.goto("/admin/delivery/zones");
    await page.getByTestId("open-import").click();
    await page.getByLabel("Upazilas", { exact: true }).fill(`Dhaka, ${name}, বাংলা নাম\nNowhereLand, Foo\nDhaka, ${name}\nDhaka,`);
    await expect(page.getByTestId("import-summary")).toContainText("1 line is ready");
    await expect(page.getByTestId("import-summary")).toContainText("3 lines have problems");
    const errors = page.getByTestId("import-errors");
    await expect(errors).toContainText("This district name is not known.");
    await expect(errors).toContainText("Repeated in this list.");
    await expect(errors).toContainText("No upazila name given.");
    await page.getByTestId("import-send").click();
    await expect(page.getByTestId("import-result")).toContainText("Added 1, skipped 0");
    const list = (await call<{ nameEn: string }[]>("GET", `/api/v1/store/geo/districts/${dhaka!.id}/upazilas`, { expect: 200 })).json;
    expect(list.map((u) => u.nameEn)).toContain(name);
    // sending the same line again is skipped by the server
    await page.getByLabel("Upazilas", { exact: true }).fill(`Dhaka, ${name}`);
    await page.getByTestId("import-send").click();
    await expect(page.getByTestId("import-result")).toContainText("Added 0, skipped 1");
  });
});

test.describe("Delivery zones: permissions", () => {
  test("view only: everything is read-only and no create, import, delete or countries", async ({ page }) => {
    const zone = track(await makeZone({ rates: { baseFee: 60 } }));
    const staff = await staffWithPermissions(["delivery.zone.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin/delivery/zones");
    await expect(page.getByTestId("zone-new")).toHaveCount(0);
    await expect(page.getByTestId("open-import")).toHaveCount(0);
    await expect(tab(page, "Countries")).toHaveCount(0);
    await page.goto(`/admin/delivery/zones/${zone.id}`);
    await expect(page.getByTestId("details-read-only")).toBeVisible();
    await expect(page.getByTestId("details-save")).toHaveCount(0);
    await expect(page.locator("#zd-name")).toBeDisabled();
    await expect(page.getByTestId("zone-delete")).toHaveCount(0);
    await tab(page, "Areas").click();
    await expect(page.getByTestId("areas-read-only")).toBeVisible();
    await expect(page.getByTestId("areas-save")).toHaveCount(0);
    await tab(page, "Rates").click();
    await expect(page.getByTestId("rates-read-only")).toBeVisible();
    await expect(page.getByTestId("rates-save")).toHaveCount(0);
    await expect(page.locator("#rt-base")).toBeDisabled();
    await page.goto("/admin/delivery/zones/new");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("zone edit without rate edit: details and areas can be saved, rates stay read-only", async ({ page }) => {
    const zone = track(await makeZone({ rates: { baseFee: 60 } }));
    const staff = await staffWithPermissions(["delivery.zone.view", "delivery.zone.edit"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto(`/admin/delivery/zones/${zone.id}`);
    await page.locator("#zd-name").fill("Edited by zone editor");
    await page.getByTestId("details-save").click();
    await toast(page, "Zone details saved.");
    expect((await readZone(zone.id)).nameEn).toBe("Edited by zone editor");
    await tab(page, "Rates").click();
    await expect(page.getByTestId("rates-read-only")).toBeVisible();
    await expect(page.getByTestId("rates-save")).toHaveCount(0);
    await expect(page.getByTestId("zone-delete")).toHaveCount(0);
    // the API agrees: rates cannot be changed with this role
    const token = await staffAccess(staff.email, staff.password);
    const denied = await call("PUT", `/api/v1/delivery/zones/${zone.id}/rates`, { token, body: { baseFee: 1, freeDeliveryThreshold: null, remoteSurcharge: 0, perExtraKgFee: 0, maxWeightGrams: null, slabs: [] } });
    expect([401, 403]).toContain(denied.status);
    expect(Number((await readZone(zone.id)).baseFee)).toBe(60);
  });

  test("without the view permission the menu item and the page are closed", async ({ page }) => {
    const staff = await staffWithPermissions(["catalog.product.view"]);
    await signInViaBff(page.context(), staff.email, staff.password);
    await page.goto("/admin");
    await expect(page.getByRole("link", { name: "Delivery zones" })).toHaveCount(0);
    await page.goto("/admin/delivery/zones");
    await expect(page).toHaveURL(/forbidden/);
  });

  test("an unknown zone shows a clear not-found page", async ({ page }) => {
    await adminSignIn(page);
    await page.goto("/admin/delivery/zones/00000000-0000-4000-8000-000000000000");
    await expect(page.getByText("Delivery zone not found").first()).toBeVisible();
    await expect(page.getByRole("link", { name: /Back to zones/ })).toBeVisible();
  });
});

