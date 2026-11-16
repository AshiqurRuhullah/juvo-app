# Step 11A backend, builder BE-2: reports, profit and loss, accountant export

Contract: `docs/reports-api.md`; part 1 contract: `docs/operations-api.md`. Code: `juvo-be/src/Juvo.Application/Reports/*`, `juvo-be/src/Juvo.Infrastructure/Reports/*`, `juvo-be/src/Juvo.Api/Endpoints/ReportsEndpoints.cs`. No migration was added.

## Decisions

| Decision | Reason | How to change |
| --- | --- | --- |
| A sale excludes Cancelled and Returned-without-delivery orders; refunds of excluded orders are ignored so nothing is counted twice. | Revenue must not include orders that never happened. | `ReportData.Countable` |
| Cost of goods is recognised when the order is placed, from the line cost snapshot; it is not reversed for returns. | The snapshot is the only cost the system keeps per line. | Needs the restock cost on returned items. |
| Courier charge is the shipment cost by booking date, cancelled parcels excluded. | Courier fees are not deducted at COD settlement. | |
| Purchases are information only, never in profit. | Purchased goods are stock until sold. | |
| Gateway fees are shown as 0 with `gatewayFeesRecorded: false`. | The payment records never store a fee. | Add a fee column to Payment in a later migration. |
| A week starts on Monday. | Local convention. | `ReportMath.BucketStart` |
| Days are bucketed with a Unix-seconds key. | EF cannot translate `UtcDateTime.AddHours` or `DateTimeOffset` subtraction here. | |
| Cost is hidden by null fields plus `costHidden`; exports answer `REPORT.COST_HIDDEN`. | FR-O04. | |
| PDFs embed whole fonts (about 0.6 MB), like invoices. | Skia does not subset. | Needs a font handling change. |
| Daily summary keeps its last-sent date in PlatformSettings and is off until an owner email is set. | Single server worker; no surprise emails. | Step 12 settings screen. |

## Traceability

| FR | Status | Note |
| --- | --- | --- |
| FR-P01 | Done (backend) | Sales by day, week, month, product, category, size, colour, channel, zone, payment. |
| FR-P02 | Done (backend) | Funnel, cancellation and return reasons. |
| FR-P03 | Done (backend) | Value at cost and price, low stock, movements, dead stock after N days. |
| FR-P04 | Done except gateway fees | Gateway fees are not recorded. |
| FR-P05 | Done (backend) | Rates, average delivery time, cost per parcel, collected versus settled. |
| FR-P06 | Done (backend) | New versus returning, top spenders, conversion. |
| FR-P07 | Done (backend) | Exports CSV, Excel, PDF; daily summary worker, default off, no settings endpoint yet. |
| FR-X02 | Done by reading | Courier charges, refunds, purchases; gateway fees Partial. |
| FR-X04 | Done (backend) | Profit and loss with series and expenses by category. |
| FR-X05 | Done (backend) | CSV and Excel, five kinds. |

## Partial, deferred, not recorded

- Gateway fees; the cost of returned goods is not reversed; COD courier fees are not deducted at settlement.
- The payables export is an as-of-today snapshot over open purchase orders, computed in memory (no column for outstanding).
- No endpoint sets the daily summary address (Step 12).
- Part 1 gaps found by the docs pass: permissions `purchasing.receipt.create` and `purchasing.payable.view` are unused by endpoints; error codes `EXPENSE.INSUFFICIENT_BALANCE` and `EXPENSE.CATEGORY_IN_USE` are never returned; a supplier payment without `accountId` is not booked in the cash book; the day book with `accountId` excludes derived rows.
- The Bengali PDF headers and all new Bengali text are a first pass (Step 12 review).

## Gate (builder, run with the support configuration temporarily moved out)

Build 0 warnings; domain 318; application 74 (13 new helper tests); integration 464 run, 462 passed, 2 failed (existing tests, see the lead's notes); 17 report tests and 1 volume test (3,000 orders, every report, exact totals) passed; endpoint policy test passed; vulnerable packages none; no leftover test databases. Totals checked by hand: sales 5 orders, 11 units, gross 7,000, discount 150, delivery 280; P&L net revenue 6,650, cost 3,600, courier 280, expenses 550, purchases 5,500, net profit 2,500; COD collected 2,520, settled 1,960, pending 560.
