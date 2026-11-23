# Step 12i slice 2: customer privacy screens (notes)

Branch `feature/12i-privacy-ui`. Contract: `docs/privacy-api.md`, backend notes `docs/step12i-privacy-notes.md`. No backend change; the client was regenerated first (`openapi/v1.json`, `src/lib/api/schema.d.ts`).

## Built
All on the existing `/admin/customers/[id]` page (no new route, no new menu item). Each part is hidden unless the caller holds the permission.
- **Consent history card** (`customers.customer.view`): `GET /customers/{id}/consents/history`. Channel (SMS, Email, Privacy policy), change (given, withdrawn, first record given or declined), "also covers WhatsApp" for SMS, source (friendly name plus the stored text), IP exactly as sent, time. Ten per page with the shared pager when there are more. Empty state "No consent changes recorded". A note says IPs are partly hidden when the API says `piiMasked`.
- **Privacy card** (`customers.privacy.export` and/or `customers.privacy.anonymise`; shown if either is held):
  - Export: confirm dialog (personal data, logged under your name), then `POST .../privacy/export` through the BFF as a download (`downloadFile` in `src/modules/reports/api.ts` got an optional `init` for the POST; the BFF already passes `Content-Disposition`). The hourly limit (429 `PRIVACY.EXPORT_LIMIT`) is shown inside the dialog, which stays open.
  - Anonymise: dialog with "This cannot be undone", three lists (replaced, deleted, kept; wording from the backend notes), "Invoices and order totals are kept for the books.", the points/credit note and "same mobile can sign in again as a new customer". Needs a reason (5 to 300 characters, counted after trimming) and the customer id typed (any case, dashes optional, like the API). The button stays disabled until both are valid. First focus is the reason box, never the danger button. Blocking refusals (`PRIVACY.OPEN_ORDERS`, `PRIVACY.STAFF_ACCOUNT`) show a danger alert with icon and text inside the open dialog (the open-work case also shows the API's detail line). Result view: "Customer anonymised" with counts, or "Already anonymised" for the idempotent answer.
  - After success the page shows an "Anonymised on <date>" banner, the privacy card becomes a short note, deactivate/activate is not offered, and the personal fields show what the API returns (`Anonymised`, `ANON...` mobile).
- Code: `src/modules/customers/{privacy-logic.ts,privacy-cards.tsx}`, `api.ts` (hooks), `common.tsx` (`usePrivacyRights`), `customer-detail.tsx` (placement), texts `src/messages/privacy.{en,bn}.json` (new fragment `privacy`, registered in `load.ts`; admin scope is "all" so `scopes.ts` needed no change) and `errors.PRIVACY_*` in `en.json`/`bn.json`.

## Decisions (reason, how to change)
| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| U1 | An anonymised customer is recognised by the placeholder mobile starting with `ANON` (`isAnonymisedMobile`). | `CustomerListItemDto`/`CustomerDetailDto` have no `anonymisedAt` (see gaps). Real numbers start with `+`. | Use `anonymisedAt` when the API adds it. |
| U2 | The "Anonymised on <date>" date is known only in the session that ran the action; after a reload the banner says "This customer is anonymised" without a date. | Same gap. | Same. |
| U3 | Whole Privacy card is replaced by a note for an anonymised customer (export too, though the API would still export the empty shell). | Brief. | `PrivacyCard`. |
| U4 | Deactivate/activate is not offered for an anonymised customer. | Backend decision P12: activating would re-enable an empty shell. | `actions` in `Loaded`. |
| U5 | Focus goes back to the opening button by code (`closing` in `PrivacyCard`). | Radix returns focus only to a `DialogTrigger`; these dialogs open from plain buttons. The same gap exists for the existing deactivate dialog and `ConfirmDialog` generally (not changed here). | A `triggerRef` prop on `ConfirmDialog` would fix it everywhere. |
| U6 | The consent history is loaded in full and paged on the client (10 per page). | The API has no paging for it. | Switch when the API pages it. |
| U7 | The open-work refusal shows the API's detail line in the language the server sends (English). | It names what is open, which staff need. | Drop `blocked-detail` if only translated text is wanted. |

## Contract gaps
1. Customer DTO lacks `anonymisedAt` (U1, U2).
2. Consent history has no paging.
3. No per-customer list of the open orders that block anonymisation (only the detail text).
4. Nothing built for FR-E08 customer self-service deletion (backend gap).

## Not built / partial
Customer-side delete request flow, retention rules, legal wording review (owner decisions O1 to O5 in the backend notes still apply). The texts claim nothing about legal retention periods.

## Tests (numbers actually run, on this branch)
- vitest: `tests/privacy.test.ts` 16 pass; full suite 34 files, 607 tests pass (includes message key parity and scope tests).
- `npx tsc --noEmit` clean; eslint clean on `src/modules/customers`, `src/modules/reports/api.ts`, the new specs and test.
- `npm run build` completes (needed a second run after the known advisory notice).
- Playwright desktop project, own ports (API 5093, web 3193, DB `JuvoDb_E2E_Privacy`): `privacy.spec.ts` 10 tests pass; `privacy-ui-checks.spec.ts` 6 tests pass (360, 768, 1280, dark, Bengali at 360, anonymise result and anonymised page; axe, overflow, 44 px, no console errors); re-run `customers.spec.ts` (6) and `customers-ui-checks.spec.ts` (7) pass.
- Not run: tablet, Pixel 7 and iPhone 14 projects (the new specs are desktop-project only, the viewport is set in the spec), the rest of the suite.
- Flow spec covers: history after consent changes through the shop API, export download (JSON file containing the mobile and name), hourly limit message, anonymise blocked by an open order, wrong id keeps the button off, success with banner and gone actions, same mobile signs in again as a new customer (API), permission cases (view only, export only, anonymise only, Super Admin, Manager sees no privacy card), focus return and Escape.
- Screenshots kept: `docs/ui-checks/privacy/` (detail 1280, anonymise dialog 360 and Bengali, anonymised page 360).

## Traceability suggestions
- FR-AF06: Implemented (backend and screens): consent history, export, anonymise on the customer detail.
- FR-E08: Partial: staff-side anonymise screen done; customer self-service deletion not built.
- FR-E07: Implemented: consent history visible to staff.
- NFR-010: stays Partial (no legal review, retention rules or self-service).
- Module N customers screen: adds consent history and privacy tools.
