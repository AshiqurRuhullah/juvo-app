# Rewards API (module AB): points, store credit, gift cards, referrals

Step 11B backend. Everything is behind one feature flag, `Loyalty.Enabled` in `PlatformSettings`, which is **off by default**. While it is off nothing accrues, every customer endpoint answers `409 LOYALTY.FEATURE_OFF`, a tender in checkout is refused with the same code, and the staff screens keep working so the shop can be configured first. Money is BDT `decimal(18,2)`. Errors are RFC 9457 ProblemDetails with a stable `code`.

## 1. Settings (FR-AB01)

`GET /api/v1/loyalty/settings` (`loyalty.points.view`), `PUT /api/v1/loyalty/settings` (`loyalty.points.adjust`, only the fields sent change, validated, every change audited through the generic `PlatformSetting` audit).

| Field | Meaning | Default | Valid |
| --- | --- | --- | --- |
| `enabled` | The feature flag | `false` | |
| `earnRatePerBdt` | Points earned per BDT of goods value (0.01 = 1 point per 100 BDT) | 0.01 | 0 to 100, 4 decimals |
| `earnRounding` | `Down`, `Nearest`, `Up` | `Down` | |
| `pointValue` | BDT one point is worth when redeemed | 1.00 | 0.01 to 1000 |
| `minPointsToRedeem` | Least points a customer can use on one order | 100 | 0 to 1,000,000 |
| `maxTenderPercent` | Most of the order total that points and store credit together may pay | 50 | 0 to 100 |
| `pointsExpiryMonths` | Points expire this many months after they are added (0 = never) | 12 | 0 to 120 |
| `expiryWarningDays` | Warn this many days before points expire (0 = no warning) | 14 | 0 to 180 |
| `referralRewardReferrer` | Store credit for the customer who referred | 100 | 0 to 100,000 |
| `referralRewardReferee` | Store credit for the new customer | 100 | 0 to 100,000 |
| `referralMaxRewardsPerReferrer` | Rewarded referrals per referrer (0 = no limit) | 20 | 0 to 10,000 |
| `giftCardMinAmount`, `giftCardMaxAmount` | Limits of one gift card | 100 / 20,000 | min at least 0.01, max at least min |
| `giftCardDefaultValidityMonths` | Expiry of a card created without a date (0 = never) | 12 | 0 to 120 |

```json
PUT /api/v1/loyalty/settings
{ "enabled": true, "earnRatePerBdt": 0.02, "maxTenderPercent": 40 }
```

## 2. Ledgers (FR-AB04)

* `PointsLedgerEntry` and `CreditLedgerEntry` are append-only (`IAppendOnly`): kind, signed amount, `balanceAfter`, reason, order or return reference, actor, time, and an optional unique `referenceKey`.
* Points kinds: `Earn`, `Redeem`, `Release`, `Reverse`, `Expire`, `Adjust`, `ReferralReward` (reserved). Credit kinds: `GiftCardRedeem`, `Spend`, `Release`, `Refund`, `Adjust`, `ReferralReward`, `ReferralClawback`.
* The balance lives in `LoyaltyAccount` (one row per customer, created on first activity) and always equals the sum of the entries; the staff customer view shows both numbers and `ledgerConsistent`.
* **Never negative, never double spent**, in three layers: (1) every posting runs in one transaction under a per-customer SQL application lock (`sp_getapplock`, the same one used for invoice numbers), so postings of one customer are serial; (2) the account row has a `rowversion`, a stale write fails; (3) the database has a check `PointsBalance >= 0 AND CreditBalance >= 0`, a check `BalanceAfter >= 0` on both ledgers and a **unique index on (customer, referenceKey)**.
* Keys: redeem `order:{orderId}:points|credit`, earn `earn:{orderId}`, reverse `reverse:{returnId}`, refunds `refund:{refundId}`, referral `referral:{id}:referrer|referee`, gift card to credit `giftcard:{cardId}` (card side `redeem:{cardId}:{customerId}`). A repeated event or request changes nothing the second time.
* Taking points back after a return (`Reverse`) and clawing back referral rewards take **what is left** (never below zero); the shortfall is not chased.
* Expiry: every added points entry carries `expiresAt`. Points leave oldest-expiry first, so what expires now is `due lots - everything taken out - already expired`, never more than the balance (see `PointsMath.Expirable`). `PointsExpiryWorker` (section `Loyalty`, `ExpiryEnabled`, `ExpiryIntervalMinutes`, default hourly) writes `Expire` entries and sends one "points expiring soon" message per customer and expiry date; a run that did something is in the job log. It is idempotent and safe on two servers (each customer is recomputed inside their lock).

## 3. The tender design (checkout)

A tender is money the customer already holds with the shop. **It never changes a line price, a discount, the tax or the order total**; it only reduces what is still to be paid.

**Quote.** `POST /store/checkout/quote` and `POST /store/checkout` accept an extra `tender`:

```json
{ "tender": { "points": 200, "credit": 0, "giftCardCode": "ABCD-EFGH-JKLM-NPQR" } }
```

`points` are whole points (at least `minPointsToRedeem`, not more than the balance), `credit` is BDT of store credit, `giftCardCode` pays as much as the card holds up to what is left. Points and credit together are capped at `maxTenderPercent` of the grand total; a gift card is not capped (it is a prepaid instrument). Points and credit need a signed-in customer; a guest can pay with a gift card. Staff orders ignore `tender`.

The quote answers with an extra `tender` object and adjusted `payNow` and `payOnDelivery`; `grandTotal` is unchanged:

```json
"grandTotal": 1060.00, "payNow": 0.00, "payOnDelivery": 860.00, "canPlace": true,
"tender": { "enabled": true, "pointsUsed": 200, "pointsAmount": 200.00, "creditUsed": 0.00, "giftCardUsed": 0.00,
            "tenderTotal": 200.00, "payableAfterTender": 860.00, "pointsBalance": 500, "creditBalance": 0.00,
            "problem": null, "problemMessage": null }
```

A refused tender does not fail the quote: `problem` carries the `LOYALTY.*` code, `canPlace` is false. The checkout itself answers 409 with the code.

**Placement.** In the same transaction as the order number, the stock reservation and the order itself (`OrderPlacement`): one ledger posting per tender (idempotency key per order, so one order can never redeem twice), the gift card entry, one `OrderTender` row (unique per order), one **Paid payment record of kind `StoreCredit`** per tender (methods `POINTS`, `CREDIT`, `GIFTCARD`), and `Order.ApplyTender`, which adds the amount to `AdvancePaid`. If a balance changed since the quote, the whole order (and its stock reservation) rolls back with the `LOYALTY.*` code. When the tender covers the whole total the order's payment track becomes `Paid` at once.

**Why a payment record and `AdvancePaid`.** Every "total minus what was received" calculation (cash on delivery amount, packing slip, courier COD amount, COD collected and settled reports, advance rule BR-D1) already reads `GrandTotal - AdvancePaid`, so the remainder follows the existing methods with no change in them: COD collects less, an online or manual payment is created for `GrandTotal - AdvancePaid`, and a tender counts toward the required advance of a large COD order. The `StoreCredit` payment record lets refunds, credit notes and the payment list work unchanged. It is not cash: the cash received, cash refunded and ledger reports skip `PaymentKind.StoreCredit` payments and `RefundMode.StoreCredit` refunds.

`checkout` answer: `payNow`, `payOnDelivery` are net of the tender and a new `tenderTotal` is added.

**Giving it back.** Always to the ledger, never as cash:

| Event | What happens |
| --- | --- |
| Order cancelled (customer, guest, staff, payment timeout) | Each tender payment is refunded to the ledger (points back as points with a new expiry, store credit and gift card value back as store credit) as a `Refund` of mode `StoreCredit`, completed at once. A fully tendered order's payment track moves to `Refunded`. |
| Delivery failed and the parcel is `Returned` | The same release. A return after delivery is not released here: it follows the return refund. |
| Return refund | The refund is split in proportion between what the tender payments and the cash payments can still give back. The tender share goes to the ledger (points: `round(pointsUsed * share / paymentAmount)`, the last share returns the rest), the cash share follows the chosen refund method. Both get a credit note. |
| Refund method `StoreCredit` (return refund, or `POST /payments/{id}/refunds` with `mode: StoreCredit`) | The money goes to the customer's store credit, completes at once, gets a credit note, and no cash leaves the shop. For a return the reference may be left empty. |

**Invoices.** `InvoiceDto.tenders` lists the tender lines (`method`, `description`, `amount`); the PDF note shows them and the amount due (grand total minus tenders). The grand total and the tax are unchanged.

## 4. Earning and returns

Points are earned when an order becomes **Delivered** (outbox handler on `domain.OrderStatusChanged`, idempotent): `Earn(max(0, grandTotal - deliveryCharge - pointsAmount - creditAmount), earnRatePerBdt, earnRounding)`. Delivery is not counted, and the part paid with points or store credit does not earn again. When a return of the order is refunded (handler on `domain.ReturnStatusChanged` to `Refunded`), the share `round(earned * min(1, refundTotal / goodsValue))` is taken back, capped at what is left to reverse and at the balance.

## 5. Customer endpoints (`/api/v1/me/rewards`, customer token)

| Method and path | Purpose |
| --- | --- |
| `GET /me/rewards` | `pointsBalance`, `pointsValue`, `creditBalance`, `pointsExpiringSoon`, `nextPointsExpiry`, `rules` |
| `GET /me/rewards/points?page&pageSize` | My points ledger (paged `{items,total,page,pageSize}`) |
| `GET /me/rewards/credit?page&pageSize` | My store credit ledger |
| `GET /me/rewards/usable?orderTotal=1000` | `maxPoints`, `maxCredit`, `maxTenderAmount` for a cart total (rules and balances applied) |
| `POST /me/rewards/gift-cards/redeem` `{code}` | Move a card into store credit. Rate limited (checkout policy); 5 wrong codes in 15 minutes lock code entry for 15 minutes |
| `GET /me/rewards/referral` | `code`, `rewarded`, `pending`, `rejected`, `creditEarned`, `maxRewards`, `referredByName`, `myStatus` |
| `POST /me/rewards/referral/apply` `{code}` | Enter a friend's code before my first order. Rate limited and counted like gift card codes |

```json
GET /api/v1/me/rewards
{ "enabled": true, "pointsBalance": 300, "pointsValue": 300.00, "creditBalance": 50.00, "pointsExpiringSoon": 0, "nextPointsExpiry": null,
  "rules": { "earnRatePerBdt": 1.0, "pointValue": 1.00, "minPointsToRedeem": 100, "maxTenderPercent": 50, "pointsExpiryMonths": 12,
             "referralRewardReferrer": 100.00, "referralRewardReferee": 100.00 } }
```

A customer only ever reads their own ledger (the customer id comes from the token); a staff token or another customer's id is not accepted on these routes, and a customer token is refused on `/loyalty`.

## 6. Staff endpoints (`/api/v1/loyalty`, global permissions)

| Method and path | Permission |
| --- | --- |
| `GET /loyalty/settings` | `loyalty.points.view` |
| `PUT /loyalty/settings` | `loyalty.points.adjust` |
| `GET /loyalty/customers/{id}` balances, ledger sums, `ledgerConsistent` | `loyalty.points.view` |
| `GET /loyalty/customers/{id}/points`, `/credit` ledgers | `loyalty.points.view` |
| `POST /loyalty/customers/{id}/adjust` `{points?, credit?, reason}` (header `Idempotency-Key`) | `loyalty.points.adjust` |
| `GET /loyalty/gift-cards?status=active|used|expired|deactivated` | `loyalty.gift-card.view` |
| `GET /loyalty/gift-cards/{id}` with its entries | `loyalty.gift-card.view` |
| `POST /loyalty/gift-cards` | `loyalty.gift-card.create` |
| `POST /loyalty/gift-cards/{id}/deactivate` | `loyalty.gift-card.create` |
| `GET /loyalty/referrals?status=` | `loyalty.referral.view` |

No new permission code was needed: settings changes and store credit adjustments use `loyalty.points.adjust`, deactivating a card uses `loyalty.gift-card.create`.

```json
POST /api/v1/loyalty/gift-cards
{ "amount": 500, "expiresAt": null, "note": "Eid gift", "recipientMobile": "01712345678", "sendToRecipient": true }
201 { "card": { "id": "...", "codeHint": "NPQR", "initialAmount": 500.00, "balance": 500.00, "status": "Active",
                "expiresAt": "2027-11-16T09:00:00+00:00", "createdByName": "Admin" }, "code": "ABCD-EFGH-JKLM-NPQR" }
```

The code (16 characters, about 80 bits, no look-alikes) is in this answer **only**; the database holds its SHA-256 hash and the last four characters. If `sendToRecipient` is true one `loyalty.gift-card-received` message (SMS and email as available) carries the code; that message and its notification log row necessarily contain it. Customers type codes in any case with or without dashes or spaces.

```json
POST /api/v1/loyalty/customers/{id}/adjust
{ "points": -50, "credit": 20.00, "reason": "Goodwill correction after call 4412" }
200 { "pointsBalance": 150, "creditBalance": 70.00 }
```

## 7. Referral rules (FR-AB02)

1. Each customer has one code (8 characters from `ABCDEFGHJKLMNPQRSTUVWXYZ23456789`, about 40 bits), made on the first `GET /me/rewards/referral`, unique and case insensitive.
2. A customer can be referred once (unique index), not by themselves, and only **before their first non-cancelled order**.
3. Both sides receive the reward (store credit, `referralRewardReferrer` and `referralRewardReferee`) only when the new customer's **first order is Delivered** (checked at delivery: it must be their first non-cancelled order).
4. Fraud rules applied at that moment; any hit marks the referral `Rejected` with the reason (no reward): the order was sent to the referrer's mobile number, or to a phone number the referrer used on their own orders; the delivery address (line, area, district, city, normalised) equals an address of one of the referrer's orders; the same address was already rewarded for another referral; the referrer is deactivated; the referrer already has `referralMaxRewardsPerReferrer` rewarded referrals.
5. If the order is later returned and the refunds reach the whole goods value, both rewards are taken back (as far as each balance allows) and the referral becomes `Reversed`.
6. Rewards and the reversal are ledger postings with fixed keys, so a redelivered event pays or reverses once.

## 8. Notifications (transactional, English and Bengali, SMS and email)

`loyalty.points-earned`, `loyalty.referral-reward`, `loyalty.referral-welcome`, `loyalty.gift-card-received`, `loyalty.points-expiring`. They go through `NotificationService` (customer language, channel switches, deduplication by key), so each is written once: earned `earn:{orderId}`, referral `referral:{id}:referrer|referee`, gift card `giftcard-sent:{cardId}`, expiring `points-expiring:{customerId}:{yyyyMMdd}`. Bengali texts are a first pass for the Step 12 native review.

## 9. Error codes

| Code | HTTP | Meaning |
| --- | --- | --- |
| `LOYALTY.FEATURE_OFF` | 409 | The rewards feature flag is off. |
| `LOYALTY.CUSTOMER_NOT_FOUND` | 404 | A staff call named a customer that does not exist. |
| `LOYALTY.INSUFFICIENT_POINTS` | 409 | The points balance is lower than the points asked for. |
| `LOYALTY.INSUFFICIENT_CREDIT` | 409 | The store credit balance is lower than the amount asked for. |
| `LOYALTY.BELOW_MINIMUM_POINTS` | 409 | Fewer points than `minPointsToRedeem` were offered. |
| `LOYALTY.TENDER_EXCEEDS_CAP` | 409 | Points plus credit are more than `maxTenderPercent` of the order. |
| `LOYALTY.TENDER_EXCEEDS_TOTAL` | 409 | The whole tender is more than the order total. |
| `LOYALTY.TENDER_NEEDS_SIGN_IN` | 401 | Points or credit were offered without a customer sign-in. |
| `LOYALTY.GIFT_CARD_INVALID` | 404 | The code matches no gift card (the same neutral answer for everybody; counts as a wrong code). |
| `LOYALTY.GIFT_CARD_EXPIRED` | 409 | The gift card is past its expiry date. |
| `LOYALTY.GIFT_CARD_INACTIVE` | 409 | Staff switched the gift card off. |
| `LOYALTY.GIFT_CARD_EMPTY` | 409 | The gift card has no balance left (or too little for the amount asked). |
| `LOYALTY.GIFT_CARD_NOT_FOUND` | 404 | A staff call named a gift card id that does not exist. |
| `LOYALTY.CODE_LOCKED` | 409 | Too many wrong codes; code entry is locked for 15 minutes. |
| `LOYALTY.REFERRAL_CODE_INVALID` | 404 | The referral code matches no active customer. |
| `LOYALTY.REFERRAL_SELF` | 409 | A customer cannot use their own referral code. |
| `LOYALTY.REFERRAL_ALREADY_APPLIED` | 409 | A referral code was already used on this account. |
| `LOYALTY.REFERRAL_NOT_ELIGIBLE` | 409 | The customer already has an order, so a code can no longer be used. |

Validation failures of settings, gift card creation and adjustments use the standard `400` validation ProblemDetails with an `errors` map.

## 10. Workers and switches

| Worker | Switch | Does |
| --- | --- | --- |
| `PointsExpiryWorker` | `Loyalty:ExpiryEnabled` (default true), `Loyalty:ExpiryIntervalMinutes` (default 60) | Expires due points and sends expiry warnings; idempotent |
| Outbox handlers `LoyaltyOrderDeliveredHandler`, `LoyaltyReturnRefundedHandler` | with the outbox (`Outbox:Enabled`) | Earn, referral rewards, reversal |

Integration tests turn the expiry loop off (`ApiFactory`) and call the worker and the outbox processor by hand.
