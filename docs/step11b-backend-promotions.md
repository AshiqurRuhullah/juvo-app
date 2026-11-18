# Step 11B backend: promotions, pricing engine and pre-orders (modules Z and AA)

Branch `worktree-agent-a80f90413308e97e8` (builder BE-P), on top of `feature/11b-growth`. Read with `docs/pricing-engine.md` (how every place prices, engine contract, stacking policy, rounding) and `docs/promotions-api.md` (endpoints, permissions, JSON, errors).

## What was built

- Domain and persistence: `Promotion` (+ `PromotionTarget`), derived status (Draft, Scheduled, Active, Ended, Archived), `StackMode`, channels, usage limit; coupon flag `StacksWithPromotions`; pre-order fields on products and variants; order lines snapshot `ListPrice`, `PromotionDiscount`, pre-order facts and immutable `OrderItemAdjustment` rows; `Order.PromotionDiscount` and `Order.PreorderState`. Migration `PromotionsPreorders` (own commit; the lead replaces it with the combined one).
- One pricing engine `IPricingEngine` used by the cart, the checkout quote, placement, the staff order desk and the invoice/refund snapshot; pure, unit-tested core `PricingCalculator`; pluggable `IBasePriceSource` for the later reseller price list.
- Staff API `/promotions` (CRUD, activate, deactivate, archive, duplicate, images, preview, reports) and the shop API (campaign page, banners, promotion info on cards and details, free delivery in the delivery quotes, cache that never outlives a price change).
- Pre-order and backorder: product and variant switch, pre-order lines in cart, checkout, order, tracking and invoice, separate queue `GET /orders/preorders`, FIFO allocation after a purchase receipt and by `POST /orders/preorders/allocate`, notification template `order.preorder-ready` (en, bn; SMS, email), advance through the existing advance mechanism.

## Decisions (reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| P1 | Default stacking policy as in `docs/pricing-engine.md` section 3: lowest price wins per line among sale price and price-type promotions; order-level promotions in priority order and consuming units; coupon after promotions, better-of unless both sides allow stacking; two coupons never combine. | The owner delegated the policy; one clear price for the customer; `StackMode` per promotion and `stacksWithPromotions` per coupon keep it configurable. | Change `StackMode` of a promotion or the coupon flag; the policy itself lives in `PricingCalculator.Run` and `Calculate`. |
| P2 | A promotion's default `StackMode` is `StacksWithOrderPromotions` (combines with other order-level promotions, not with a coupon). | Safest default: a coupon never silently piles on a promotion. | Set `StacksWithCoupon` (promotion) and `stacksWithPromotions` (coupon) to allow both. |
| P3 | `Order.Subtotal` stays the sum of line totals at the price after the line-level promotion; order-level savings are `Order.PromotionDiscount`, shared per line in `OrderItem.PromotionDiscount`. | Keeps invoices, tax, reports and the older refund maths valid; the old formulas are special cases with zero promotion discount. | `ReturnMath.NetLine` and `InvoiceService.IssueAsync` are the two readers. |
| P4 | A coupon that loses to better promotions stays attached to the cart (`applied = false` with a reason) and is not recorded on the order (no coupon use counted). | The customer can see why; no use is burnt. | Engine `CouponOutcome`. |
| P5 | A coupon's minimum order is checked on the value it applies to (after promotions when stacked, normal prices when alone). | The customer pays that value. | `PricingCalculator.Calculate`. |
| P6 | Free delivery is judged on the goods value after promotions and coupon and is independent of the stacking choice; zone-limited offers need the delivery zone, which `PricedDelivery` knows after the delivery quote. | The charge shown equals the charge billed (BR-G2); one place computes it. | `PricedDelivery`, `FreeDeliveryRules`. |
| P7 | A free-delivery promotion sets the charge to 0 and reports the waived amount in `FreeDeliveryWaiver`; the advance rule for large cash on delivery orders still uses the standard charge. | Same behaviour as the zone free-delivery threshold. | `PricedDelivery.Apply`. |
| P8 | Time windows include both ends; schedule inputs carry a UTC offset, answers are in Dhaka time. Status is derived, never stored. | Exact boundaries; Asia/Dhaka as the business time. | `Promotion.StatusAt`. |
| P9 | A promotion's use is counted per order with an atomic update against its usage limit and given back on cancel (like coupons). Orders in the staff desk use channel SocialDesk, the shop Web (POS later: Pos). | No overselling of a limited offer; channel-limited promotions. | `OrderPlacement`, `OrderWorkflow.CancelAsync`. |
| P10 | The invoice is not recomputed: it is built from the order snapshot (which came from the engine). It shows list price, promotion discount and pre-order facts from the order lines (read at view time, no schema change on invoices). | Immutable invoices; no live promotion can change a document. | `InvoiceService`. |
| P11 | Cards show only promotions without a minimum cart value; price filter and sort use the sale price; the product list `onSale` filter is unchanged. | A cart condition cannot be decided for one product; keeps list queries cheap. | `PromotionCardPricing`. |
| P12 | Public catalog answers are cached for `min(configured seconds, seconds to the next promotion start or end)`. | A cached answer must never show an expired flash price. | `CacheableWithPromotions` in `StoreEndpoints`. |
| P13 | A line is a pre-order line only when the variant allows it, tracks stock and the shop has no available stock at all. With some stock the item is sold normally and limited to it. | Simple, predictable rule; no half-and-half lines. | `PreorderRules.IsPreorderLine`. |
| P14 | A pre-order reserves nothing; the order is `Waiting`, cannot be confirmed (`PREORDER.AWAITING_STOCK`) and therefore cannot be packed; it can be cancelled. Allocation is all or nothing per line, strictly FIFO per branch and variant (order time, then order number), in one transaction under an application lock with the atomic reserve; an order becomes `Ready` when its last line is served. | Fair, no double allocation, no split orders. | `PreorderAllocator`. |
| P15 | The advance is `advancePercent` of the line value after promotions (before the coupon), added to the order's `AdvanceRequired` for cash on delivery orders and paid through the existing advance payments. | Reuses the existing mechanism (no new payment path). | `PreorderRules.Advance`, `OrderPlacement.PriceAsync`. |
| P16 | The customer notification of a served pre-order is written once per order (dedupe key `preorder-ready:{orderId}`), in the same transaction that makes the order `Ready`, not through the outbox. | Exactly-once without waiting for the outbox worker; the delivery itself still goes through the notification outbox like other messages. | `PreorderAllocator.SettleAsync`. |
| P17 | Archive instead of delete: `DELETE /promotions/{id}` archives. | Reports and order snapshots keep naming the promotion. | `PromotionsService.ArchiveAsync`. |

## Traceability (for the lead to copy into `docs/understanding.md`)

| ID | Status | Implemented in | Notes |
| --- | --- | --- | --- |
| FR-Z01 | Done (backend) | `Promotion` FlashSale, `PricingEngine`, `StorefrontCatalogService` (promotion info with `endsAt`), `CacheableWithPromotions` | Countdown data is `promotion.endsAt`; the screen is front-end. A flash sale needs an end time. |
| FR-Z02 | Done (backend) | Bundle, BuyXGetY, FreeDelivery in `PricingCalculator`, `PricedDelivery` | Bundle sets may use variants, products, categories or brands; free delivery may be limited to zones. |
| FR-Z03 | Done (backend) | FestivalCampaign, `GET /store/campaigns/{slug}`, `GET /store/banners`, images through `IFileStorage` | Pages and banners only while Active; the product set is the Main targets (cards, at most 60). |
| FR-Z04 | Done | `PricingCalculator`, `docs/pricing-engine.md` | Policy delegated by the owner; the explanation is part of every price answer. |
| FR-Z05 | Done (backend) | `PromotionReportService`, `GET /promotions/report`, `/promotions/{id}/report` | Orders, units, revenue after promotion discounts, discount cost; cancelled orders excluded; returns do not reduce the numbers (documented). |
| FR-AA01 | Done | `Product`/`ProductVariant` pre-order fields, `PUT /catalog/products/{id}/preorder`, variant override | Expected ship date and advance percent per product or variant. |
| FR-AA02 | Done (backend) | cart, quote, order, tracking and invoice DTOs (`isPreorder`, date, labels EN/BN), advance through `AdvanceRequired` | Optional advance percent; labels are texts, the badge is front-end. |
| FR-AA03 | Done | `GET /orders/preorders`, `PreorderAllocator`, hook in `PurchasingService.ReceiveAsync`, `POST /orders/preorders/allocate`, template `order.preorder-ready` | FIFO, no double allocation (tested with concurrent calls). |
| BR-004 | Done | `PricedDelivery` + `OrderPlacement` | Free-delivery promotions keep shown charge = billed charge. |
| BR-005 | Unchanged | | International display conversion is still not built. |

## Partial, not built, contract gaps

- Not built: reseller price list and POS (they plug into `IBasePriceSource` and `Channel = Pos`); customer-specific or segment promotions (the request carries the customer, no engine rule uses it); automatic stock-arrival allocation from transfers and stock counts (the staff action covers it; only purchase receipts trigger it automatically).
- Partial: the public list filters `onSale`, `minPrice`, `maxPrice` and the price sort use the sale price, not promotions (cards and details show promotions). The items-based public `POST /store/delivery/quote` judges the free-delivery threshold on catalog (sale) prices. `POST /store/coupons/validate` is unchanged (it answers for a subtotal, without promotions).
- Partial: a line's "price changed" marker compares with the unit price stored when the line was last changed, which is the price of one unit; a promotion with a minimum cart value can therefore flag a change when the cart grows.
- Partial: the campaign report does not subtract returns; `units` counts the quantity of the lines that carry the promotion (for a bundle or buy-X-get-Y that is the whole line).
- Migration note for the lead: the combined migration must add `UPDATE [OrderItems] SET [ListPrice] = [UnitPrice] WHERE [ListPrice] = 0` after adding the column (old lines had no list price); the views also fall back to the unit price.

## New error codes

`PROMO.NOT_FOUND`, `PROMO.MULTIPLE_COUPONS`, `PROMO.SLUG_TAKEN`, `PROMO.ARCHIVED`, `PROMO.INCOMPLETE`, `PROMO.CAMPAIGN_NOT_FOUND`, `PROMO.TARGET_NOT_FOUND`, `PROMO.USED_UP`, `PREORDER.AWAITING_STOCK`; meanings in `docs/promotions-api.md`.

## Gate

Run from inside `juvo-be` (the test executables directly):

- `dotnet build`: 0 warnings, 0 errors.
- Domain tests 344 passed; application tests 144 passed (40 pure pricing engine cases and pre-order rules included).
- Integration tests: 518 passed in the namespaces Storefront 61, Orders 37, Payments 54 (invoices, refunds), Returns 21, Operations 106 (purchasing), Couriers 42, Catalog 63, Ums 88 (endpoint policy inventory updated for the two public endpoints), Persistence 34, Api 12; plus the new Promotions namespace 29 (pricing consistency 17, flash sale timing 3, pre-orders 9), all passed. Those were run per namespace, not as one single invocation.
- `dotnet ef migrations has-pending-model-changes`: clean. `dotnet list package --vulnerable --include-transitive`: no vulnerable packages. No `JuvoDb_Test_*` databases left.
