# Step 11A front end A: purchasing, expenses and cash book (builder FE-A)

Branch: the builder's worktree branch (see the final report for the last commit). Backend: untouched (merged `feature/11a-operations`).

## Gate (what was actually run)

- `npx tsc --noEmit`: clean. `npx eslint src e2e tests`: 0 problems. `npm test` (vitest): 16 files, 177 tests pass (includes `tests/purchasing.test.ts` and `tests/accounting.test.ts`). `npm run build`: ok.
- `dotnet build` inside `juvo-be`: 0 errors.
- Playwright (`purchasing`, `accounting` and the two UI-check specs) was written but could NOT be run: the API does not start on `feature/11a-operations` because `ApplyConfigurationsFromAssembly` pulls the support ticket EF configurations into the model while the `SupportTickets` migration only exists on BE-1's branch, so EF throws `PendingModelChangesWarning` at start (`dotnet run` exits with "Process from config.webServer was not able to start"). Backend files were not changed. After the support migration is merged, run: `E2E_API_PORT=5181 E2E_WEB_PORT=3200 npx playwright test purchasing accounting --project=setup --project=cleanup --project=desktop` (after `npm run build`). The specs have not been seen passing and may need small selector fixes; screenshots under `docs/ui-checks/purchasing` and `docs/ui-checks/accounting` do not exist yet.

## What was built

Routes (all server shells with `requireSession` and `requirePermission`; interactive parts are client components, dialogs and the heavy tabs are `next/dynamic`):

| Route | Permission | Content |
| --- | --- | --- |
| `/admin/purchasing` | `purchasing.po.view` | tabs Purchase orders, Suppliers, Receipts, Suggestions (a tab shows only when its permission is held) |
| `/admin/purchasing/orders/new` | `purchasing.po.create` | draft editor (supplier, branch, expected date, lines from the variant picker, notes, attachments) |
| `/admin/purchasing/orders/[id]` | `purchasing.po.view` | summary, lines, receipts, payments, attachments, history, actions per status and permission in the order's branch |
| `/admin/purchasing/orders/[id]/edit` | `purchasing.po.edit` | the same editor for a draft |
| `/admin/purchasing/orders/[id]/print` | `purchasing.po.view` | print sheet from `GET /{id}/print` (admin frame hidden in print) |
| `/admin/accounting/expenses` | `accounting.expense.view` | tabs Expenses, Categories, Repeating |
| `/admin/accounting/expenses/[id]` | `accounting.expense.view` | workflow: edit, submit, approve, reject (reason), return to draft, delete draft |
| `/admin/accounting/cash` | `accounting.cashbook.view` | tabs Accounts (balances as of a date, create/edit, entries, transfer, adjustment) and Day book |

Code: `juvo-front/src/modules/purchasing/*`, `juvo-front/src/modules/accounting/*`, shared `juvo-front/src/components/data/attachment-picker.tsx`, texts only in `src/messages/{purchasing,accounting}.{en,bn}.json`.
Tests: `tests/purchasing.test.ts`, `tests/accounting.test.ts` (pure logic), `e2e/purchasing.spec.ts`, `e2e/accounting.spec.ts`, `e2e/purchasing-ui-checks.spec.ts`, `e2e/accounting-ui-checks.spec.ts`, helper `e2e/support/ops.ts`. Screenshots: `juvo-front/docs/ui-checks/purchasing/`, `.../accounting/`.

Performance: lists use server paging (20), `keepPreviousData`, debounced search, memoised columns; the PO line rows are `React.memo` keyed by variant (typing in one line re-renders only that line and the total); the variant picker, dialogs, supplier/receipt/suggestion tabs and categories/repeating tabs are dynamically imported; no polling; the supplier and account lists are fetched only by the screens that need them (filters mount them only for people who may see them).

## Decisions (to be added to docs/assumptions.md)

1. Supplier purchase history (FR-W05): the supplier row links to the purchase order list filtered by that supplier. Receipts are reached from each order (the receipts API has no supplier filter). A per product purchase history is not offered by the API; the screen says so in a note.
2. Costs on purchase orders: the API sends unit costs and totals to everyone who holds `purchasing.po.view` (they are the supplier's prices, not the product cost price). The screens therefore show them to those people and never render a cost the API did not send. What the cost permission (`catalog.product.view-cost`) gates is changing the product cost: a receipt booked by someone without it leaves the product cost alone (`costApplied=false`), shown as "Cost not applied yet"; only a person with the permission gets the "Apply cost" button (`POST /receipts/{id}/apply-cost`).
3. "Choose how cost updates": the receive request has no such choice; the method is the setting `Purchasing.CostMethod` (default last cost). The receive dialog says so; the only manual step is apply-cost.
4. Receive dialog starts with the full rest of the order as accepted units (the usual delivery); the client refuses accepted units above what is still expected with its own text before sending, and the API refuses the same with `PURCHASE.OVER_RECEIPT` (tested through the API too). Damaged or refused units are not limited.
5. Expense self approval: the API does not name who submitted an expense. The screen remembers on the device (localStorage, per user id, last 200 ids) which expenses this person sent for approval and hides Approve for those; if storage is empty or another device is used the button shows and the API refuses with `EXPENSE.SELF_APPROVAL` (text shown inside the dialog). Super Admin may approve their own (API rule).
6. Expense and repeating forms need an account, and the accounts list needs `accounting.cashbook.view`. A person with expense rights but no cash book view sees a warning instead of the account choice (cannot create).
7. Adjustments accept a leading minus sign; transfers and expenses are positive amounts only. Money is always shown with two decimals on these screens (`useMoney2`), as BDT in the current language.
8. Suggestions hand their lines to the new order screen through `sessionStorage` (`juvo.po.seed`, read once); the supplier group (when known) becomes the supplier of the draft.
9. Day book: totals strip is the API's whole-range totals; "Net of this page" is computed from the rows on screen so a page can be checked by hand. With an account chosen the API returns that account's own lines only (derived payment money belongs to no account); the screen says so.

## Traceability rows believed changed

| ID | Status | Note |
| --- | --- | --- |
| FR-W01 | Done | Suppliers: list, create, edit, activate/deactivate, payment terms. |
| FR-W02 | Done | Purchase orders: create/edit draft, lines by variant search with quantity and unit cost, send, cancel with reason, attachments, print view; statuses per `PurchaseStateMachine`. |
| FR-W03 | Done | Receiving: per line accepted and damaged quantities, partial receipts, over-receipt guard, close short, receipts list and detail, apply cost for cost people. Cost method is a setting, not a per receipt choice (decision 3). |
| FR-W04 | Done | Reorder suggestions per branch, grouped by supplier, start draft with suggested lines. |
| FR-W05 | Partial | Purchase orders per supplier (link with filter); receipts per order; no per product history (API has none). |
| FR-X01 | Done | Expenses: categories, repeating expenses with run now, amount, date, method, account, notes, receipt upload, approval workflow with reasons, filters and totals. |
| FR-X02 | Done | Cash book: accounts with balances as of a date, entries, transfers, adjustments with reason, day book with derived customer receipts, COD settlements and refunds. |
| FR-X03 | Partial | Supplier payments are recorded from the purchase order (with optional account booked in the cash book); a separate payables list screen is not built (the order list filters payment status, overdue and payable are API filters not exposed as a screen). |

## Partial / Deferred

- No payables aging screen: `overdue` and `payable` filters of `GET /purchasing/orders` exist but only the payment status filter and the overdue badge are on screen.
- No `docs/operations-api.md` was available while building (BE-2 writes it); the contract used is code, tests and `schema.d.ts`.
- Bengali texts are a first pass and need native review (Step 12).

## Contract gaps found in the API

- `ExpenseDetailDto` / `ExpenseListItemDto` do not say who submitted the expense (`SubmittedBy` exists in the domain). The screens cannot hide Approve for the author reliably; a `submittedByName`/`isMine` field would fix it (decision 5).
- `GET /accounts` needs `accounting.cashbook.view`, so staff who may create expenses but not view the cash book cannot choose an account (decision 6). An endpoint returning only active account names and ids per branch for expense creators would fix it.
- `GET /purchasing/receipts` has no supplier filter (FR-W05).
- The receive endpoint has no per receipt cost method choice (decision 3).
- PO line unit costs are not hidden from people without the cost permission (decision 2); if the owner wants them hidden, the print and detail DTOs would need to omit them.
