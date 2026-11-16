# Reports, profit and loss, accountant export and daily summary API (Step 11A backend, BE-2)

Contract for module P (reports) and the profit and loss and export parts of module X. Base path `/api/v1`. Errors are RFC 9457 ProblemDetails with `code` and `correlationId`. Decisions and reasons: `docs/step11a-backend-reports.md`. Part 1 (purchasing, expenses, accounts): `docs/operations-api.md`.

## 1. Filters and branch semantics (all reports)

Query: `from`, `to` (Bangladesh days, inclusive; default the last 30 days ending today), `branchId`, `groupBy` where offered, `limit` (1 to 500, default 50), `days` (dead stock, default 60).

- The endpoint permission decides which branches are visible. Branch-limited staff get only their own branches (several are summed); asking for another branch gives an empty result. Global staff get the consolidated view, or one branch with `branchId`.
- Every answer echoes `filters {from,to,branchId,consolidated,branchCodes,groupBy,limit,days}`, `generatedAt` and `notes[]`.
- A day is a Bangladesh day (Asia/Dhaka). A week starts on Monday, a month on the 1st. Series include empty buckets.
- A **sale** is an order placed in the period that is not Cancelled and not Returned-without-delivery. `gross` = subtotal (tax-inclusive prices as stored), `discount` = coupon plus manual discount, `net` = gross minus discount, `total` = net plus delivery. Refunds count on the day they complete.
- **Cost fields** (cost of goods, gross profit, stock value at cost) need `catalog.product.view-cost` in every visible branch. Otherwise they are `null` and `costHidden` is true.
- **Gateway fees** are not recorded by the system: shown as 0 with `gatewayFeesRecorded: false`.
- No output cache. Aggregation is done in SQL.

## 2. Endpoints

| Method and path | Permission | Notes |
| --- | --- | --- |
| `GET /reports/dashboard` | `reports.dashboard.view` | Today, 14-day series, orders by status, pending actions, top products, COD pending. |
| `GET /reports/sales` | `reports.sales.view` | `groupBy` day, week, month, product, category, size, color, channel, zone, payment. |
| `GET /reports/orders` | `reports.orders.view` | Funnel, cancellation and return reasons. |
| `GET /reports/inventory` | `reports.inventory.view` | Stock value, low stock, movements, dead stock (`days`). |
| `GET /reports/finance` | `reports.finance.view` | `groupBy` day, week, month; COD collected, settled, pending. |
| `GET /reports/courier` | `reports.courier.view` | Per courier performance and COD. |
| `GET /reports/customers` | `reports.customers.view` | New versus returning, top spenders, guest-to-profile conversion. |
| `GET /reports/<name>/export` | `reports.<name>.export` | `format=csv|xlsx|pdf`, `lang=en|bn`, `section=`. |
| `GET /accounting/pnl` | `accounting.pnl.view` | Profit and loss with `groupBy` day, week, month. |
| `GET /accounting/export` | `accounting.export.run` | `kind=daybook|expenses|pnl|payables|purchases`, `format=csv|xlsx`. |

## 3. Response shapes

- **Sales** `{filters, generatedAt, totals, rows[], truncated, notes}`; row `{key,label,orders,units,gross,discount,net,delivery,total}`. Totals are exact. Product, category, size and colour rows are built from order lines (order counts overlap, the discount is shared by line value, delivery is 0).
- **Orders** `{placed, funnel[{stage,count,rateOfPlaced,rateOfPrevious}], cancelledRate, returnedRate, cancellationReasons[{code,label,count,share,fault}], returnReasons[]}`.
- **Inventory** `{summary{variants,onHand,reserved,available,valueAtPrice,valueAtCost,unitsWithoutCost}, byBranch[], lowStockCount, lowStock[], movements[{reason,movements,in,out,net}], deadStockCount, deadStockValueAtPrice, deadStock[], costHidden}`.
- **Finance** `{totals, rows[{label,start,orders,grossSales,discounts,refunds,netRevenue,deliveryCollected,courierCharges,gatewayFees,costOfGoods,grossProfit}], cod{collected,settled,pendingWithCouriers,inTransit}, deliveryBalance}`.
- **Profit and loss** `{total, series[], expensesByCategory[], costHidden}`; bucket `{label,start,orders,grossSales,discounts,refunds,netRevenue,deliveryCollected,costOfGoods,costMissingLines,grossProfit,courierCharges,gatewayFees,gatewayFeesRecorded,expenses,purchasesReceived,netProfit}`. `netRevenue` = gross - discounts - refunds; `grossProfit` = netRevenue - cost; `netProfit` = netRevenue + delivery - cost - courier - gateway - expenses. Expenses are approved ones by expense date. `purchasesReceived` is information only (stock, not an expense, not in profit). Automatic entries (courier charges, refunds, purchases) are **read** from system data, never copied into the ledger.
- **Courier** row `{courierId,courier,parcels,delivered,deliveredRate,avgDeliveryHours,failed,failedRate,returned,returnedRate,totalCost,costPerParcel,codCollected,codSettled,codPending}`.
- **Customers** `{customers,newCustomers,returningCustomers,ordersFromNew,ordersFromReturning,revenueFromNew,revenueFromReturning,guestsCreated,convertedToProfile,conversionRate,topSpenders[]}`. Customers are not branch data: for branch-limited runs only customers who ordered in the branch count in conversion; "new versus returning" looks across branches for a yes or no only.
- **Dashboard** `{today,todayTotals,series[14],ordersByStatus[],pending{toConfirm,toPack,heldByFraud,returnsWaiting,lowStock},topProducts[5],codPending,codInTransit}`. Orders by status cover 30 days; pending actions cover all open orders.

## 4. Exports

- **CSV:** UTF-8 with BOM (Excel), a leading apostrophe against formula injection, numbers plain. **Excel:** one sheet, valid Open XML. Both hold one table (the first, or the one named by `section`) plus a Total row.
- Sections: sales; orders `funnel|cancellations|returns`; inventory `summary|low|movements|dead`; finance `finance|cod`; courier; customers `summary|top|conversion`. An unknown section answers 404 `REPORT.UNKNOWN`.
- **PDF:** A4 (landscape above 7 columns), shop name from `Business.Name` (default Juvo BD), title, filters, every section with totals, notes, page numbers, `lang` English or Bengali (Bengali headers are a first pass). About 0.6 MB because fonts are embedded whole, like invoices.
- **Accountant export** kinds: `daybook`, `expenses` (approved), `pnl`, `payables` (open supplier invoices as of today, the range is ignored), `purchases` (receipt lines). Limits: 366 days, 50,000 rows (`REPORT.RANGE_TOO_LARGE`). `pnl` and `purchases` need the cost permission (`403 REPORT.COST_HIDDEN`).

## 5. Limits and errors

| Code | Status | Meaning |
| --- | --- | --- |
| `REPORT.RANGE_INVALID` | 400 | The end is before the start. |
| `REPORT.RANGE_TOO_LARGE` | 400 | More than 366 days for day grouping or for reports without grouping (1,830 otherwise), or more than 50,000 export rows. |
| `REPORT.FORMAT_INVALID` | 400 | Export format not offered. |
| `REPORT.UNKNOWN` | 404 | Unknown report or export section. |
| `REPORT.COST_HIDDEN` | 403 | The export needs cost access. |

An unknown `groupBy`, `kind` or `lang` answers 400 `GENERAL.VALIDATION`.

## 6. Daily summary (FR-P07)

`DailySummaryWorker` sends yesterday's sales, orders, cash and low stock through the sandbox email sender and notification log (template `daily-summary`, job log `reports.daily-summary`). It is idempotent per day.

| Setting | Default | Meaning |
| --- | --- | --- |
| `Reports.DailySummary.Email` (PlatformSettings) | empty = off | The owner's address. |
| `Reports.DailySummary.Hour` | 7 | Send hour in Asia/Dhaka. |
| `Reports.DailySummary.LastSentDate` | | Written by the job. |
| `Reports:DailySummaryEnabled` (config) | true | The worker switch (off in tests). |
| `Reports:DailySummaryIntervalMinutes` (config) | 15 | Check interval. |

There is no endpoint to set the address yet: a PlatformSettings row until the Step 12 settings screen.
