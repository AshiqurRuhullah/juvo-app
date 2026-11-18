# Step 11B front end: rewards (module AB), builder `rewards`

Branch `feature/11b-front-rewards`. Contracts: `docs/loyalty-api.md`, `docs/step11b-backend-loyalty.md` (L1..L15), `docs/orders-api.md`, `docs/payments-api.md`. Everything is behind the setting `Loyalty.Enabled` (default off).

## What was built

### Staff `/admin/loyalty` (menu: Marketing group, item "Loyalty", permission `loyalty.points.view`, anywhere scope)
Tabs, each only for people holding its permission (`src/modules/loyalty/loyalty-center.tsx`):
* **Settings** (`settings-view.tsx`): on/off switch with the plain consequence in words, every field the API has (earn rate with a plain example, rounding, point value, minimum points, cap percent `maxTenderPercent`, expiry months, warning days, both referral rewards, referral limit, gift card min/max/default validity). Client check with the ranges of the API, only changed fields are sent, read-only without `loyalty.points.adjust`.
* **Gift cards** (`gift-cards-view.tsx`): list by status, create (amount, expiry date, note, recipient mobile/email, "send now"), the code appears **once** in a dialog and never again (list and details show `----LAST4` only), details dialog with the card's entries, deactivate (needs `loyalty.gift-card.create`).
* **Referrals** (`referrals-view.tsx`): list with state (icon + word), the code used, referrer, outcome note from the API (the fraud rule for a rejected one), rewards.
* **Customer rewards** (`customer-lookup.tsx`): find a customer by mobile or name (needs `customers.customer.view`) or paste a customer id (works without that permission), both balances, expiring points, `ledgerConsistent` check, both ledgers, manual adjustment of points and/or credit with a required reason (`loyalty.points.adjust`, `Idempotency-Key` per dialog opening).

### Customer `/account/rewards` (account menu "My rewards", shown only while the feature is on)
`rewards-view.tsx`: points balance with worth and next expiry (warning when points expire soon), store credit, how it works (from the live rules), referral code with copy and share (Web Share when the browser has it) and "apply a code" (before the first order, L13), redeem a gift card by code (the API's refusal and lockout texts), paged points and credit ledgers.
While the feature is off: no menu entry, no tile, and a direct visit shows a calm "Rewards are not open yet" note (not an error box).

### Checkout `TenderSection` (`tender-section.tsx`)
Drawn only when `quote.tender.enabled`. Signed in: balances, points and store credit fields with "Use most", the cap sentence (from `GET /me/rewards/usable`), amounts are lowered to what the cap and balance allow (with a notice) before they are sent. Guest: gift card field only plus a sign-in hint. The amounts go in `tender` of `POST /store/checkout/quote` and `POST /store/checkout`; the plan the quote returns (points, credit, gift card, left to pay, "total stays X") is shown inside the section. A refused tender shows the API's `LOYALTY.*` text in crimson with an icon (the quote already makes `canPlace` false).
Shared-file edits: `orders/checkout-view.tsx` (one state variable, `tender` in the quote input and the checkout body and in the idempotency fingerprint, ONE `<TenderSection .../>` line between the payment and OTP sections), `orders/api.ts` (`tender` in `QuoteInput` and the quote body). The price summary was not touched.

### Where tender amounts show
* Order confirmation: a "Paid with rewards" row from `tenderTotal` (`orders/confirmation-view.tsx`).
* Invoice (customer My order and guest): the tender lines and "Amount due" from `InvoiceDto.tenders` (`payments/customer-panels.tsx`).
* Staff order page payment panel: the reward payments are listed like other payments; labels added (`payments.kind.StoreCredit` "Rewards", methods `POINTS`, `CREDIT`, `GIFTCARD`). The payments list kind filter has the new kind.

### Refund to store credit (decision L15)
* Payment page refund dialog: third choice "To store credit" (`mode: StoreCredit`), shown as a mode in the refund lists.
* Return refund dialog: method "Store credit" in the method list; the reference is not required for it.

## Decisions (reason, how to change)
| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| R1 | The customer menu entry uses a new optional `feature: "loyalty"` flag on `NavItem`; `visibleNav(groups, access, features)` hides such items unless the feature is on. `AppShell` gets the flag from `useNavFeatures` (`GET /me/rewards`, cached one minute, only in the account area). | The menu must hide rewards while the flag is off, and the only customer-side signal is the API's 409 `LOYALTY.FEATURE_OFF`. | `modules/loyalty/nav-features.ts`, `lib/nav.ts`. |
| R2 | `useMyRewards` turns the 409 into `data: null` (not an error). It also logs one browser "409" resource line on account pages while the feature is off. | Not-an-error for the UI. | The few UI-check specs that visit `/account` with a strict console filter must allow 409 (see merge notes). |
| R3 | The tender fields apply on a button ("Apply"), not on every keystroke. | Each apply is one quote call; typing would flood the API and flicker the totals. | `tender-section.tsx`. |
| R4 | Points and credit are lowered on the client to what `usable` allows before the quote; the server still decides. | Avoids refused tenders from typing a big number. | `clampPoints`, `creditRoom` in `logic.ts`. |
| R5 | The customer lookup accepts a pasted customer id as well as a search. | Staff without `customers.customer.view` can still reach the screen from a ticket or an order. | `customer-lookup.tsx`. |
| R6 | Settings are sent as a patch of changed fields; unchanged fields are `null`. | The API changes only the fields it receives. | `toSettingsPatch`. |

## Contract gaps (not changed in the backend)
* **My order (customer) shows no tender amounts** except on the invoice: `OrderDetailDto`/`MoneyDto` carry no `advancePaid`/tender field and the API has no customer list of payments (the payment page only shows the order's payment track). The checkout confirmation (`tenderTotal`) and the invoice (`tenders`) are the places they show.
* **No customer-side flag endpoint**: the menu has to probe `GET /me/rewards` and sees a 409 while the feature is off (R1, R2).
* **The customer's wish for "refund to store credit" is not stored** (L15), so staff choose it at refund time; the customer screens do not offer it.
* The gift card code is in the notification log row of a message sent with "send now" (backend note); the UI says so next to the checkbox.
* Referral rewards are store credit (L2), so the referral screens speak of credit, not points.

## Partial / not built
* No separate rewards report or export (not in the API).
* No Bengali native review (first pass, Step 12).

## Tests
* `tests/loyalty.test.ts` (vitest): tender maths and cap, typed numbers, request building, plan rows, code validation and formatting, settings form mapping, validation and patch, earn example, ledger formatting, adjustment and gift card forms, EN/BN key parity.
* `e2e/loyalty.spec.ts` (real browser, real API): off by default and hidden from customers, staff set the rules (validation, save, reload), gift card created with the code shown once, customer balances/redeem/refusals, referral code flows, staff lookup and adjustment, checkout with points and credit (cap lowering, plan, confirmation, staff payment list, refund dialog choice, cancel gives it back), guest gift card, return refunded to store credit without a reference.
* `e2e/loyalty-ui-checks.spec.ts`: every new page at 360/768/1280 (overflow, 44 px, axe, console), dark theme; screenshots in `docs/ui-checks/loyalty/`.

## Traceability suggestions
| ID | Status | Implemented in | Notes |
| --- | --- | --- | --- |
| FR-AB01 | Done | `/admin/loyalty` settings, `/account/rewards`, checkout `TenderSection` | Earn, redeem, minimum, cap, expiry settings; customer balances and next expiry; checkout redemption. |
| FR-AB02 | Done | `/account/rewards` referral card, `/admin/loyalty` referrals tab | Code, share, apply before the first order; staff list shows state and fraud-rule outcome. Code entry is not in the checkout request (L13). |
| FR-AB03 | Done (partial) | gift cards tab, redeem on `/account/rewards`, guest gift card at checkout, refund to store credit | Customer preference for credit refunds is not captured (L15). |
| FR-AB04 | Done | ledger tables for customer and staff, `ledgerConsistent` badge | Append-only ledgers are read-only in the UI; adjustments need a reason. |

## Gate results (final tree)
* `npx tsc --noEmit`: clean. `npx eslint src e2e tests`: clean. `npm test`: 20 files, 281 tests passed (24 of them in `tests/loyalty.test.ts`).
* `npm run build`: compiles, type-checks and generates all 77 pages, then the process exits 1 with Next.js `SecurityFatalError` ("your version of Next.js is affected by a known security vulnerability", advisory check against the npm registry, Next 16.4.4). That is about the installed Next version, not about this change. The first attempts also failed twice with a transient `@tailwindcss/turbopack` Node subprocess timeout; later runs passed that step. The lead may want to look at the Next.js upgrade note the build prints.
* Playwright (desktop project, ports 5113/3113, db `JuvoDb_E2E_rewards`, dropped by the cleanup project): `loyalty.spec.ts` 10 of 10 passed (the first run found a duplicate form label, fixed; two clean runs after); `loyalty-ui-checks.spec.ts` 8 of 8 passed (360, 768, 1280 for customer and staff screens, dark theme). Regression run of `myaccount`, `store-ui-checks`, `shell`, `returns`, `payments` specs on the final build: 59 passed, 1 skipped (the existing tablet-only shell test). The Pixel 7 and iPhone 14 projects were NOT run for the UI-check spec (their `testMatch` only covers `shell` and `devices`).

## Merge notes for the lead
* Shared files touched additively: `src/lib/nav.ts` (Gift icon, `feature` field and optional third argument of `visibleNav`, one admin item, one account item), `src/components/layout/app-shell.tsx` (`useNavFeatures`, two lines), `src/messages/{en,bn}.json` (nav items `loyalty`, `myRewards`; `LOYALTY_*` errors), `load.ts`, `scopes.ts` (`loyalty` in storefront and account), `playwright.config.ts` testMatch, `.gitignore`, and small additive edits in `orders/{api,checkout-view,confirmation-view}`, `payments/{api,payment-detail,customer-panels}`, `returns/{logic,return-actions}` plus their message files.
* Builder B edits the price summary in `orders/checkout-view.tsx`: my edits there are one `useState`, `tender` in `quoteInput`/body/fingerprint and one `<TenderSection quote={quote} onChange={setTender} />` line before the OTP section.
* While the feature is off the account area makes one `GET /me/rewards` that answers 409 (browser console shows a 409 resource line). A UI-check spec that visits `/account` with a console filter that does not allow 409 would flag it; none of the specs run here did.
