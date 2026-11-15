# Step 10 front end, builder B: COD settlements

Routes: `/admin/cod` (list and summary), `/admin/cod/new`, `/admin/cod/[id]` (detail, reconcile, settle, cancel), `/admin/cod/[id]/print`.
Code: `juvo-front/src/modules/cod/`, pages under `juvo-front/src/app/(admin)/admin/cod/`, texts `src/messages/cod.{en,bn}.json`.
Tests: `tests/cod.test.ts` (vitest), `e2e/cod.spec.ts`, `e2e/cod-ui-checks.spec.ts`, helper `e2e/support/cod.ts`. Screenshots: `juvo-front/docs/ui-checks/cod/`.

## Decisions (for docs/assumptions.md)

| Decision | Reason | How to change |
| --- | --- | --- |
| The list summary strip makes one `pending` request per courier that takes cash (all started together once the courier list is known) and four small `cod-settlements` list requests (Open and Reconciled with page size 100 for the sums, Settled and Cancelled with page size 1 for the count). | The API has no summary endpoint; this keeps the request count fixed and small. The "unsettled" sum shows a leading "at least" sign when more than 100 working batches exist. | Replace with one summary endpoint when the API has it (`src/modules/cod/queries.ts`). |
| Reconcile form lines start at the expected cash; a batch that was reconciled before starts from the saved amounts and notes. Every parcel is sent explicitly. | Contract says an omitted parcel counts as paid in full; sending all lines is clearer and makes re-saving idempotent. | `toReconcileLines` in `logic.ts`. |
| The reconcile form keeps typed text in a small external store and each row owns its own input state, so one keystroke redraws that row and the totals only. | Batches can hold many parcels. | `reconcile-form.tsx`. |
| The new-batch screen only offers branches where the person holds `couriers.cod.settle`, defaults to the active branch, and sends `shipmentIds` only when a subset is chosen. | Contract: default is every waiting parcel in the branch. | `new-settlement.tsx`. |
| Money on COD screens always shows two decimals (the shared `useMoney` drops them for whole amounts). | Cash sheets are checked digit by digit. | `useMoney2` in `common.tsx`. |
| The reconcile form, settle and cancel dialogs are loaded on demand (`next/dynamic` for the form); the print view is its own route. | Keep the client bundle of the list small. | |
| Settled batches link to `/admin/payments` only when the person holds `payments.payment.view`. | Do not show a link that leads to a forbidden page. | |

## Traceability rows (believed changed)

| FR | Status | Note |
| --- | --- | --- |
| FR-H09 (COD reconciliation: collected amount, courier fee, settlement date, Collected then Settled) | Done (backend and front end) | Screens: batch list with summary, create from waiting parcels (all or a subset), reconcile with per-parcel and batch differences and mandatory notes (short in danger tone, over in warning tone, both with icon and text), settle with a reference, cancel, print sheet. Orders become Settled through the API (checked in e2e). Courier fee is shown per parcel and in total, labelled "recorded, not deducted". |

## Partial or deferred

- The list cannot show the short and over parcel counts per batch: `SettlementListItemDto` has `parcels` but not `shortParcels` and `overParcels` (they are only on `SettlementDto`). The list shows the parcel count and the difference; the detail shows both counts.
- Cancel has no "who": `SettlementDto` has `cancelledAt` but no `cancelledByName`. The history shows the time only.

## Contract gaps found

1. `SettlementListItemDto` lacks `shortParcels`, `overParcels`, `reference` and `reconciledAt` (the list asked for short and over counts).
2. `SettlementDto` lacks `cancelledByName`.
3. The Accountant default role holds `couriers.cod.*` but not `couriers.courier.view`, and the courier list (`GET /couriers`) needs `couriers.courier.view`. The new-batch screen and the courier filter need that list, so an Accountant without it cannot choose a courier. Either give the Accountant `couriers.courier.view` or let the COD endpoints answer with the courier name and id (a small `GET /couriers/cod-settlements/couriers`).
4. `GET /couriers/cod-settlements/pending` answers with `ShipmentListItemDto[]` and no total; fine for a few hundred parcels, but there is no paging if a courier has thousands waiting.
