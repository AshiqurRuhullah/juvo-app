# Step 11B backend: rewards (module AB)

Builder BE-L. Branch `worktree-agent-a046159906456ad46`. Contract: `docs/loyalty-api.md`.

## Decisions (reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| L1 | One flag `Loyalty.Enabled`, default off. Off means: no earning, no referral reward, every customer endpoint 409 `LOYALTY.FEATURE_OFF`, tender refused; staff screens work. Refunds of tenders that were already taken still go back to the ledger when the flag is off (the money is owed). | CLAUDE.md section 7; the shop sets rules up before opening. | Staff `PUT /loyalty/settings {enabled}`. |
| L2 | Referral rewards are **store credit in BDT** (both sides), not points. | The SRS says "reward amounts"; credit has one unit and one ledger. `PointsEntryKind.ReferralReward` is reserved. | Change `ReferralService.OnOrderDeliveredAsync` to post points instead. |
| L3 | Tender = Paid `Payment` of new kind `StoreCredit` (methods POINTS, CREDIT, GIFTCARD) plus `Order.AdvancePaid += tender`. | All "total minus advance" maths (COD cash, packing slip, courier COD, reports, advance rule) already reads `GrandTotal - AdvancePaid`, so no payment rule had to be rewritten; refunds and credit notes work on a real payment record. | Replace `Order.ApplyTender` and the two `GrandTotal - AdvancePaid` hooks in `CustomerPaymentsService` if a dedicated `TenderTotal` column is preferred. |
| L4 | Cash reports (cash received, cash refunded, ledger, accounts) skip `PaymentKind.StoreCredit` payments and `RefundMode.StoreCredit` refunds. Sales reports keep counting them (a refund to credit still reduces sales). | A tender or a credit refund is not cash. | `AccountsService`, `ReportData`, `ReportExports`, `StaffPaymentsService` one filter each. |
| L5 | Points and store credit together are capped at `maxTenderPercent` of the grand total; **gift cards are not capped**. | Spec says the cap is for "points or credit"; a gift card is a prepaid instrument. | `TenderService.PlanAsync`. |
| L6 | Points are earned on delivered goods value: `grandTotal - deliveryCharge - (points + credit paid)`; earned at **Delivered**, reversed in proportion when a return refund is recorded. | "Delivered and not returned" without a waiting period; no loops from spending points to earn points. | `RewardsEngine.EarnBaseAsync`. |
| L7 | Reversals (`Reverse`, referral clawback) take what is left and never go negative; the shortfall is not chased. | The ledger must never be negative (NFR). | Add a debt ledger if the owner wants shortfalls tracked. |
| L8 | Concurrency by a per-customer SQL application lock + rowversion + unique (customer, key) index + check constraints. | Same pattern as invoice numbers; no isolation-level tricks. | `LoyaltyLedger`. |
| L9 | Expiry without lots: every positive entry has `expiresAt`; expirable = due lots minus all points taken out minus already expired (oldest expiry is used first). | Keeps the ledger append-only with no mutable lot table. | `PointsMath.Expirable`, `RewardsEngine.ExpireDueAsync`. |
| L10 | Gift card codes: 16 characters, about 80 bits, shown once, stored as SHA-256 + last four. Customer code entry locks after 5 wrong codes in 15 minutes for 15 minutes (per customer) and endpoints use the checkout rate limit (per address). Guests at checkout are protected by the address rate limit only. | Guessing protection without a new table. | `GiftCardService.FailureLimit`, `LockFor`. |
| L11 | A gift card refunded after an order is cancelled or returned goes back as **store credit**, not onto the card. | The card may have expired or been deactivated meanwhile. | `TenderService.RefundToLedgerAsync`. |
| L12 | Referral fraud rules: referee's first non-cancelled order; delivery phone must not be the referrer's mobile or a phone of their own orders; delivery address (normalised) must not equal an address of the referrer's orders nor an address already rewarded; referrer active and under `referralMaxRewardsPerReferrer`; reversal when refunds reach the goods value. | Simple rules the owner can read. | `ReferralService.OnOrderDeliveredAsync`. |
| L13 | Customers enter a referral code through `POST /me/rewards/referral/apply` (before the first order). The checkout request has no referral field. | A guest has no account before the order; a signed-in customer can apply the code at sign-up. | Add a field to `CheckoutRequest` later. |
| L14 | Settings changes and manual credit adjustments use permission `loyalty.points.adjust`; deactivating a card uses `loyalty.gift-card.create`. No new permission codes. | "Add codes only if truly needed." | Add `loyalty.settings.manage` if the owner wants it split. |
| L15 | Staff refunds can use `mode: StoreCredit`; a return refund can use `method: StoreCredit` (reference optional). The customer's preference at request time is not stored. | Smallest additive change to the return model. | Add a preference field to `CreateReturnRequest`. |

## Traceability

| ID | Requirement | Status | Implemented in | Notes |
| --- | --- | --- | --- | --- |
| FR-AB01 | Points: earn rate, redeem value, minimum, cap, expiry, earn on delivery, reversal on return | Done (backend) | `LoyaltySettingsService`, `RewardsEngine`, `TenderService`, `PointsMath`, `PointsExpiryWorker`, `/loyalty/settings`, `/me/rewards*` | Flag off by default. No UI (BE only). |
| FR-AB02 | Referral codes and rewards after the first delivered order, abuse rules | Done (backend) | `ReferralService`, `/me/rewards/referral*`, `/loyalty/referrals` | Code entry at checkout is a separate call (L13). |
| FR-AB03 | Gift cards and store credit; refund to store credit | Done (backend) | `GiftCardService`, `TenderService`, `RefundService` hooks, `/loyalty/gift-cards*` | Customer preference for credit refunds not stored (L15). Gift card code appears in the one outgoing message (and its log row) when staff send it. |
| FR-AB04 | Append-only ledgers, never negative, never double spent | Done (backend) | `LoyaltyLedger`, `LoyaltyConfigurations`, unique and check constraints | Concurrency tested at ledger level (8 parallel postings) and HTTP level (gift card). |

## Partial and deferred

* No front-end (builder scope). The quote and checkout contracts gained optional fields (`tender`), so the generated client needs `npm run gen:api`.
* Checkout-level concurrency from one customer is limited by the single cart per customer; the double-spend race is tested at the ledger service (parallel scopes), the gift card endpoint (parallel customers), and by the unique key (idempotent replay at checkout).
* The gift card code is in the notification log row of the message that carries it (a rendered body is stored). Redacting it needs delivery-time rendering in `NotificationService` (deferred).
* Customer's choice of "refund to store credit" at return request time is not captured; staff choose the method at refund time.
* Shortfalls when points or rewards were already spent before a reversal are not chased (L7).
* Referral reward on points (instead of credit) and tiered loyalty are not built.
* Existing tests are unchanged; the combined migration is the lead's job (this branch has `LoyaltyRewards` for its own tests).

## Hooks in shared code (all small and additive)

| File | Hook |
| --- | --- |
| `Orders/OrderPlacement.cs` | `PlacementInput.Tender` (init property), ctor `TenderService`, `PlanAsync` before the duplicate guard, `ApplyAsync` inside the transaction after the stock movements |
| `Orders/CheckoutService.cs` | ctor `TenderService`; quote computes `tender`, nets `payNow`/`payOnDelivery`, `canPlace` false on a tender problem; checkout passes `request.Tender`; result nets `payNow` and adds `tenderTotal` |
| `Orders/OrderContracts.cs` | trailing optional `Tender` on quote request, quote answer, checkout request; `TenderTotal` on the result |
| `Orders/OrderWorkflow.cs` | ctor `TenderService`; `ReleaseForOrderAsync` after cancel and when a failed delivery is `Returned` |
| `Orders/OrdersService.cs` | packing slip "collect" uses `max(advance required, advance paid)` |
| `Payments/CustomerPaymentsService.cs` | online and manual payment amounts are `GrandTotal - AdvancePaid` |
| `Payments/RefundService.cs` | ctor `TenderService`; tender share of a return refund goes to the ledger; `RefundMethod.StoreCredit`; staff `RefundMode.StoreCredit` |
| `Returns/ReturnsService.cs` | `StoreCredit` method accepted with an empty reference |
| `Billing/InvoiceService.cs`, `BillingContracts.cs` | `InvoiceDto.Tenders`, PDF note with the tender lines |
| `Accounting`, `Reports`, `StaffPaymentsService` | cash filters leave `StoreCredit` payments and refunds out |
| `Notifications/NotificationCatalog.cs` | five `loyalty.*` templates |

## Gate numbers

Filled in by the final run (see the builder report): build 0 warnings and 0 errors; domain tests 392, application tests 102, integration tests: see report; `dotnet ef migrations has-pending-model-changes` clean; `dotnet list package --vulnerable --include-transitive` clean.
