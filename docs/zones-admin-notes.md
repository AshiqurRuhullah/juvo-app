# Step 12b: delivery zones admin screens (notes of the `zones` builder)

Branch `feature/12b-zones-admin` (from `main`). Front end only; no backend change. Module `src/modules/delivery`, texts `src/messages/delivery.{en,bn}.json`, specs `e2e/delivery.spec.ts` and `e2e/delivery-ui-checks.spec.ts`, helpers `e2e/support/delivery.ts`, unit tests `tests/delivery.test.ts`.

## Built (routes)

| Route | Permission | What |
| --- | --- | --- |
| `/admin/delivery/zones` | `delivery.zone.view` | Zone list (name, code, type, fallback badge, area count or country codes, base fee, free-from amount, slab count, status), search, shared pager (client-side slice: the API returns the whole list), cards on phones. Button "New zone" (`delivery.zone.create`), button "Add upazilas" (`delivery.zone.edit`). Tab "Countries" with `delivery.country.view`. |
| `/admin/delivery/zones/new` | `delivery.zone.create` | Details form only (code, type, names, days, order, on/off, fallback). Rates and areas open on the zone page after creation (the API creates a zone without rates). |
| `/admin/delivery/zones/[id]` | `delivery.zone.view` | Tabs Details, Areas (domestic only), Rates, Try a quote. All panels stay mounted so unsaved edits survive a tab switch; the quote tab warns when anything is unsaved. Delete zone (`delivery.zone.delete`). Menu item `zones` is `ready: true`. |

Countries tab: list with search and pager, edit dialog (names, phone code, on/off, international zone), add country (`delivery.country.enable`); read-only note without it. Bangladesh: no switch, no zone (API rule). Upazila import: dialog, paste or CSV file, `district, English name, Bengali name`, local checks per line (unknown district, no name, name over 100, repeated, extra columns, over 1,000), then one request; result says added and skipped.

## Decisions (reason, how to change)

1. **Money is typed in taka, weights in kilograms.** Money allows Latin or Bengali digits, commas, a `৳` sign, at most 2 decimals, 0 to 1,000,000 (the API limit); weights up to 3 decimals and are converted to whole grams (the API stores grams). Reason: hard to mis-enter (a 3-decimal fee or `7o` is refused with a plain message before anything is sent). Change: `parseMoney`, `parseKgToGrams` in `logic.ts`.
2. **Slabs cannot overlap or leave gaps in this model.** A slab is only an upper limit; each covers the weight above the previous limit. The form therefore checks what can go wrong: a limit that is not above zero, a repeated limit (error), and warnings for a heavier slab that is cheaper, a slab above the heaviest allowed parcel, a per-kg fee that does nothing without slabs (the API ignores it then), free delivery from 0. A table "What these slabs mean" spells out the ranges (`1 kg to 2 kg: adds ৳25`, `Above 2 kg: last fee + per started kg`). Slabs are sent sorted.
3. **Areas editor works on the whole saved list.** The API replaces all areas of a zone; the draft starts from the zone's rows. District row = whole district; an upazila row inside it is an override that wins (API quote rule); Remote switch per row. Other zones' holders come from `GET /delivery/zones`, so the owner sees "In <zone>" per district and upazila, an alert counting what will move, and a confirmation dialog before saving areas that other zones hold. Bulk: tick a whole division, "Tick all N shown" after a search or division filter, "Untick all shown".
4. **Try a quote calls the public shop endpoint** `POST /store/delivery/quote` (same call as the storefront), so the numbers include a courier flat charge (first switched-on courier) and a running free-delivery offer. The result compares itself with the saved rates of the zone and says when another zone, a courier or an offer changed the numbers. Browser spec skips the exact-number steps (it does not fake them) when such a modifier exists in the database.
5. **Delete** is disabled-in-effect with an explanation for the fallback zone and for a zone a country uses (the API refuses both with `DELIVERY.NOT_DELIVERABLE`); otherwise the dialog states that the zone's areas fall back to the fallback zone and placed orders keep their charge.
6. **Zone type and code are fixed after creation** (the update request has neither).
7. Error texts for `DELIVERY.*` added to `errors.*` in `en.json` and `bn.json` (small additive edit). `DELIVERY_NOT_DELIVERABLE` has one text covering both the quote and the delete case because the API uses one code for both.

## Contract gaps (exact change needed)

- **G1 No dry run for the upazila import.** The API cannot say which names exist already; the preview checks only what the browser can know. Needed: `POST /delivery/upazilas/import?dryRun=true` returning per-row results (added, skipped, error). Also the district lookup is by name only; a per-row `districtId` is what the API takes.
- **G2 `GET /delivery/zones` is not paged and carries every area.** The list pages client-side. Fine for dozens of zones; for many zones a `page,pageSize,search` contract would be needed.
- **G3 No upazila update or delete endpoint.** A wrongly imported upazila cannot be removed or renamed from the UI. Needed: `PUT/DELETE /delivery/upazilas/{id}` (refuse when a zone area uses it).
- **G4 Per-courier override (FR-G08) lives on the courier**, not the zone, and applies to the first switched-on courier only (decision S1). The zone screens cannot show or edit it; the quote reflects it. Needed for per-zone/per-courier rate cards: a rate table keyed by zone and courier.
- **G5 The quote does not say whether the district was remote** (only the surcharge amount) or which area row matched. The panel infers "remote" from a non-zero surcharge. Needed: `isRemote` and `matchedAreaId` in `DeliveryQuoteDto`.
- **G6 The API accepts `freeDeliveryThreshold = 0`** (every order free) and slabs whose fee drops; the screen warns, the API does not refuse.
- **G7 Quote fields of unrelated promotions** (`promotionLabelEn`) are shown in English only, because the DTO also has `promotionLabelBn` but the staff panel follows the page language only for place names.
- Not part of this step (customer or map features): FR-G03 pin map, FR-G04 autocomplete, FR-G05 pin-in-district check, FR-G11 admin map of destinations, FR-G12 saved pins.

## Partial / not built

- International zones have no areas (API: areas are for domestic zones); their countries are chosen on the Countries tab. The zone page shows which country codes use it.
- No zone reorder by drag; the "order in lists" number is typed.
- Country delete is not offered (API has none).
- Union/area level of an address is free text in the API; no screen needed here.

## Tests

- Vitest `tests/delivery.test.ts`: 32 tests (parsing, rates form and warnings, slab ranges, expected-charge mirror of the backend, details, area selection and moves, quote, countries, upazila paste).
- Full vitest at the end of the work: see the final report.
- Browser: `e2e/delivery.spec.ts` 13 tests (create zone, assign areas incl. remote, rates with slabs and a free threshold, quote numbers equal the rates, free-delivery waiver keeps the remote surcharge, overweight message, move from another zone with confirmation, upazila override, edit, delete, fallback delete explanation, validation of money/slabs/code, countries on/off with zone, Bangladesh fixed, upazila import with per-line errors and skip, view-only, zone-edit-without-rate-edit incl. API refusal, no permission, not found) and `e2e/delivery-ui-checks.spec.ts` (7 tests: 360, 768, 1280 staff, read-only at the same widths, dark theme; every page, tab and dialog with axe WCAG 2.1 AA, overflow, 44 px targets, console errors). Run on ports API 5111, web 3111, database `JuvoDb_E2E_zones`, desktop project only.

## Traceability suggestions

| FR | Status | Notes |
| --- | --- | --- |
| FR-G01 | Done | Countries tab: enable/disable, zone per country, add country (`/delivery/countries`). Selector for customers was already done. |
| FR-G02 | Partial | Upazila paste/CSV import UI (no dry run, G1); no edit/delete of upazilas (G3). |
| FR-G07 | Done | Zone screens: create, edit, areas (district and upazila, remote), delete with rules. Seeded zones are placeholders until the owner types real rates. |
| FR-G08 | Partial | Base, slabs, per-kg, free threshold, remote surcharge, max weight, try-a-quote. Per-courier override stays on the courier (G4). |
| FR-G09 | Partial | Quote panel shows the delivery window; storefront display was done earlier. |
| FR-G10 | Partial | Quote panel shows "duties paid by receiver" for international zones. |
| FR-G03/G04/G05/G11/G12 | Unchanged | Not part of the zone screens. |
