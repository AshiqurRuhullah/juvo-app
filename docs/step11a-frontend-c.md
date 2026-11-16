# Step 11A front end, builder FE-C: reports, profit and loss, accountant export, real dashboard

Contract: `docs/reports-api.md`, `docs/step11a-backend-reports.md`. Code: `juvo-front/src/modules/reports/*`, `juvo-front/src/modules/dashboard/*`, pages `src/app/(admin)/admin/reports/**`, `src/app/(admin)/admin/accounting/pnl`. Texts only in `src/messages/reports.{en,bn}.json` (and the obsolete sample texts removed from `dashboard2.*.json`).

## Routes

| Route | Permission | Notes |
| --- | --- | --- |
| `/admin/reports` | any staff | Cards for the reports the person may open (+ profit and loss), empty state when none. |
| `/admin/reports/[report]` | `reports.<report>.view` | sales, orders, inventory, finance, courier, customers; unknown name is 404. |
| `/admin/accounting/pnl` | `accounting.pnl.view` | Totals, series chart/table, expenses by category, purchases as information, accountant export panel (`accounting.export.run`). |
| `/admin` | any staff | Real dashboard with `reports.dashboard.view`; otherwise quick links built from the menu. |
| `/admin/orders?status=Placed|Confirmed` | `orders.order.view` | New initial filter, used by the dashboard queue links. |

## Decisions

| Decision | Reason | How to change |
| --- | --- | --- |
| Filters (from, to, branch, groupBy, days) live in the address; the link always names both dates and `branch=` (empty = consolidated). | Bookmarkable and identical for whoever opens it. | `logic.ts` `filtersToSearch` / `parseFilters`. |
| Without `branch` in the link the default is the active branch (when one is chosen), else consolidated. | Reports follow the active branch like lists. | `useReportFilters`. |
| "All branches (consolidated)" is worded so only for people who can pick every branch (global scope or `ums.branch.switch-all`); others see "All my branches". | Contract: branch-limited staff get their branches summed. | `useBranchChoices`. |
| The range limits (366 days with day grouping, 1,830 otherwise; accountant export 366) are checked on the client first, the API answers stay the authority. | Clear message without a request. | `rangeProblem`. |
| Gateway fees are always shown as "Not recorded" in finance (its DTO has no flag) and by the flag in the profit and loss. | Contract section 1. | `finance-view.tsx`. |
| Hidden cost is `null`: tiles say "Hidden", cost and profit columns and chart lines are left out, never a 0. | FR-O04. | views. |
| The dashboard has four cards (sales, orders, average order value, orders to confirm). "New customers" is gone: the endpoint does not report it. Recent orders and stock health are gone for the same reason. | No invented numbers. | `modules/dashboard/logic.ts`. |
| Exports are fetched through the BFF and saved with an anchor under the server's file name; a refusal shows the translated API error. | Keep `Content-Disposition` name, show REPORT_COST_HIDDEN. | `api.ts` `downloadFile`. |
| Funnel is drawn with plain CSS bars (text carries every number), the other charts use lazily loaded Recharts with a text summary and a hidden table. | Speed, accessibility. | `common.tsx` `ChartCard`. |

## Traceability rows believed changed

| FR | Status | Note |
| --- | --- | --- |
| FR-P01..FR-P06 | Done | One screen per report with filters, tiles, chart, table and honest notices. |
| FR-P07 | Done (screens) | Export menu CSV, Excel, PDF (language). Daily summary address still has no screen (Step 12). |
| FR-X02 | Done by reading | Courier charges and purchases in the profit and loss; gateway fees Partial (not recorded). |
| FR-X04 | Done | Profit and loss screen. |
| FR-X05 | Done | Accountant export panel, five kinds, CSV and Excel. |

## Partial, deferred, contract gaps

- Gateway fees are not recorded by the backend (shown honestly as "Not recorded").
- Report row labels, cancellation reasons and the API's `notes[]` come from the server in English; only the PDF has a language.
- The dashboard has no "new customers" or recent orders (no endpoint data); the orders queue link only supports status Placed and Confirmed, the stock link goes to the inventory page without a low-stock filter.
- Bengali texts are a first pass (Step 12 review).
- Contract gap: the finance report has no `gatewayFeesRecorded` flag (the profit and loss has).
- Inventory report is requested with `limit=100`, customers with `limit=20`; longer lists are marked as partial for inventory only.
