# Pricing engine (module Z)

One engine prices every cart. Cart, checkout quote, placement, the staff order desk (quote and create), the invoice and the refund maths all use its numbers, so the customer is billed exactly what was shown (BR-004: the full total is shown first; the `expectedTotal` guard of checkout still applies).

Code: `juvo-be/src/Juvo.Application/Pricing/` (`PricingContracts.cs`, `PricingCalculator.cs` pure core, `PricingEngine.cs` loader, `PricedDelivery.cs`).

## 1. Where each place prices

| Place | How it uses the engine |
| --- | --- |
| Cart (`CartService.ViewAsync`) | `IPricingEngine.PriceAsync` with the effective quantities (what can be ordered now), the cart's coupon, channel Web. Line `unitPrice`, `listPrice`, `adjustments`, `orderDiscount`, cart `subtotal`, `promotionDiscount`, `discount` (coupon), `total`, `explanation`, `freeDelivery` offers. The unit price is also stored when a line changes (`priceChanged` marker). |
| Applying a coupon | The engine decides if the coupon is valid for this cart and whether it combines with the promotions; errors are the usual `COUPON.*` codes. A valid coupon that loses to better promotions stays on the cart with `applied = false` and a reason. |
| Checkout quote | Same cart view, plus `PricedDelivery` for the delivery charge. `subtotal`, `promotionDiscount`, `couponDiscount`, `grandTotal` are the cart numbers plus delivery. |
| Placement (`OrderPlacement.PriceAsync`) | Engine with `RequireSellable = true`, the typed coupon, channel Web (customer) or SocialDesk (staff). The order stores `Subtotal` (sum of line totals), `PromotionDiscount`, `CouponDiscount` (only when applied), delivery. |
| Staff desk `POST /orders/manual/quote` and create | Same `PriceAsync` as placement (quote is a dry run of the same function), channel SocialDesk. |
| Invoice | Built from the order's snapshot (lines, list price, promotion discount, coupon, manual discount). It never reprices from live promotions. Discount on the invoice = promotion + coupon + manual discount. |
| Returns refund | `ReturnMath.NetLine` reads the order lines only (see section 5). |

Delivery stays with the existing `DeliveryService` (zone, weight, courier override, remote surcharge, zone threshold). The engine only reports free-delivery offers; `PricedDelivery.QuoteAsync` calls the delivery code and, when an offer qualifies for the zone, sets the charge to zero and reports the waived amount as `FreeDeliveryWaiver` (so the advance of a large cash on delivery order, which is the standard charge, keeps working).

## 2. Engine contract (for the reseller price list and POS)

```csharp
public interface IPricingEngine { Task<Result<PricingResult>> PriceAsync(PricingRequest request, CancellationToken ct); }

public sealed record PricingRequest(
    IReadOnlyList<PricingLineRequest> Lines,            // (VariantId, Quantity >= 0; quantity 0 is priced but costs nothing)
    PricingCustomer? Customer = null,                    // (CustomerId?, Mobile?) passed to price sources
    IReadOnlyList<string>? CouponCodes = null,           // more than one code: 409 PROMO.MULTIPLE_COUPONS
    PricingChannel Channel = Web,                        // Web | SocialDesk | Pos (promotions can be limited to channels)
    DateTimeOffset? At = null,                           // default: TimeProvider now
    IReadOnlyCollection<string>? PriceSourceHints = null,// e.g. "reseller:<id>", read by IBasePriceSource implementations
    string? ZoneCode = null,                             // reserved; free delivery is judged per zone by PricedDelivery
    IReadOnlyCollection<Guid>? PromotionSet = null,      // preview: exactly these promotions, schedule/switch ignored
    bool RequireSellable = false);                       // placement: refuse inactive/unpublished variants
```

`PricingResult`:

- `Lines[]` (`PricedLine`): `ListPrice` (struck through), `BaseUnitPrice` (sale price or price list), `UnitPrice` (after the line-level promotion; what the order line stores), `LineTotal = UnitPrice * Quantity`, `OrderDiscount` (the line's share of bundle and buy-X-get-Y savings), `NetTotal = LineTotal - OrderDiscount`, `Adjustments[]`.
- `PriceAdjustment`: `SourceType` (`Sale`, `Promotion`, `Bundle`, `BuyXGetY`, later `Reseller`), `SourceId` (promotion id), `LabelEn`, `LabelBn`, `Amount` (positive, for the whole line), `Level` (`Line` lowered the unit price, `Order` was shared out from an order-level promotion).
- `Totals`: `ListSubtotal`, `Subtotal` (sum of line totals), `PromotionDiscount` (order-level), `CouponDiscount`, `GoodsTotal = Subtotal - PromotionDiscount - CouponDiscount`. Delivery and the staff manual discount are added by the caller.
- `Promotions[]` applied (id, type, names, labels, discount, stack mode), `Coupon` outcome (`Applied`, `Discount`, `Problem`, `NotAppliedReasonEn/Bn`), `FreeDelivery[]` offers (`Qualifies`, `AmountToQualify`, `ZoneCodes`), `CouponError` (an `Error` for invalid coupons; the cart treats it softly, placement fails with it).
- `Explanation` (`PriceExplanation`): `ListTotal` (struck through), `FinalTotal`, `Savings`, `Reasons[]` (Sale price, each promotion, coupon: label EN/BN and amount), per line `ListPrice`, `UnitPrice`, `ListTotal`, `FinalTotal`, `Saving`, `ReasonsEn/Bn`, and `NotesEn/Bn` (why a coupon was or was not applied, free delivery).

### Plugging in another base price (reseller)

```csharp
public interface IBasePriceSource { int Order { get; } Task<IReadOnlyDictionary<Guid, BasePrice>> PricesAsync(BasePriceRequest request, CancellationToken ct); }
public sealed record BasePrice(decimal ListPrice, decimal UnitPrice, string SourceType, Guid? SourceId, string LabelEn, string LabelBn);
```

Register another `IBasePriceSource` (DI `AddScoped<IBasePriceSource, ...>()`) with an `Order` above 0. Sources run in ascending `Order`; for a variant the last source that answers wins (the catalog source, `Order = 0`, answers for every variant). The request carries the customer, the channel, `Hints` (copied from `PricingRequest.PriceSourceHints`) and the variant facts (regular price, sale price and dates, override). Promotions then compete on top of the winning base price, so a reseller list does not need engine changes. Give the source a `SourceType` that is not `Promotion`, `Bundle` or `BuyXGetY` (those ids are counted as promotions for usage and reports).

POS: call `PriceAsync` with `Channel = Pos`; a promotion applies there only if its `Channels` contain `Pos`.

## 3. Stacking policy (default; per promotion `StackMode`)

1. Base price = the variant's price override, else the product's sale price inside its dates, else the regular price. `ListPrice` = override or regular price.
2. Per line, price-type promotions (flash sale, product discount, campaign with a discount) do not stack with each other or with the sale price: the LOWEST unit price wins. A promotion only counts when it is strictly lower than the base price. Ties: higher `priority`, then older `createdAt`, then id. A promotion's minimum cart value is checked against the cart at base prices.
3. Order-level promotions (buy X get Y, bundle) run on the prices of step 2 in priority order (higher first, then older). Each consumes the units it uses, per kind: a unit is used by at most one bundle and at most one buy-X-get-Y. `StackMode`:
   - `Exclusive`: alone, no other order-level promotion, no coupon.
   - `StacksWithOrderPromotions` (default): combines with other order-level promotions that are not Exclusive, not with a coupon.
   - `StacksWithCoupon`: combines with order-level promotions and with a coupon that allows it.
   When two promotions cannot combine, the larger saving wins. Order-level savings never exceed the value of a line.
4. A coupon applies after promotions. If promotions applied: it stacks only if the coupon has `stacksWithPromotions = true` AND every winning promotion has `StackMode = StacksWithCoupon`. Otherwise the customer gets the BETTER of the options "promotions only" and "coupon only on the normal prices" (never both); the explanation says which was applied and why the other was not. A tie keeps the promotions. The coupon's minimum order is checked against the value the coupon would apply to (after promotions when stacked, the normal prices when alone). If no promotion applied, the coupon is simply applied.
5. Two coupons never combine (`PROMO.MULTIPLE_COUPONS`).
6. Free delivery promotions are independent of the stacking choice: they qualify on the goods value after promotions and coupon (threshold included), and optional zone codes. They are never "dropped" when a coupon wins.
7. A unit price never goes below 0; the total discount never exceeds the line value.

### Worked examples

Base 1000, 10 percent product discount (default stack mode):

| Coupon | Options | Result |
| --- | --- | --- |
| none | promotion | 900 |
| fixed 50, does not stack | promotion 900, coupon alone 950 | 900, coupon not applied ("the promotions already give a better price") |
| fixed 300, does not stack | promotion 900, coupon alone 700 | 700, the promotion is dropped, coupon applied |
| fixed 50, stacks, promotion `StacksWithCoupon` | promotion + coupon | 850 |
| fixed 50, stacks, promotion default mode | promotion 900, coupon alone 950 | 900 |

Sale price 900 on a product listed at 1000, flash price 850, 25 percent off 900 (=675) twice with priorities 1 and 9: unit price 675, the priority 9 promotion is named; savings from the list price 325.

Buy 2 get 1 free, same product, unit 100: 3 units give 100 off, 5 units 100 (two units are leftovers), 6 units 200. Free units are the cheapest of the set.

Bundle of A (500) and B (700) at 1000: one set saves 200; A x2 and B x1 still saves 200 (the second A has no partner). The saving is shared over the lines by price and the last line takes the remainder, so no cent is lost.

## 4. Rounding rule

All amounts are BDT with two decimals, `MidpointRounding.AwayFromZero`, rounded once per unit:

- a percentage discount is rounded per unit (`unit * percent / 100`), the unit price is rounded, the line total is `unit price * quantity` (exact);
- bundle and buy-X-get-Y savings are computed per unit and shared per line with the last unit taking the remainder;
- the coupon discount uses the existing `Coupon` rule (percent, cap, never more than the goods) rounded once;
- the numbers in the cart, the quote, the order and the invoice are the same decimals, never recomputed with a different rounding.

## 5. Order snapshot, invoices and refunds

Order lines store (immutably): `UnitPrice`, `LineTotal`, `ListPrice`, `PromotionDiscount` (the line's share of order-level savings), and `OrderItemAdjustment` rows (source type, promotion id, labels EN/BN, amount, level). The order stores `Subtotal` (sum of line totals), `PromotionDiscount`, `CouponDiscount`, `ManualDiscount`, `DeliveryCharge`; `GrandTotal = Subtotal - PromotionDiscount - CouponDiscount - ManualDiscount + DeliveryCharge`.

Refund of a line (`ReturnMath.NetLine`): `(LineTotal - PromotionDiscount) * (S - D) / S` where `S` = sum over lines of `LineTotal - PromotionDiscount` and `D` = `min(S, CouponDiscount + ManualDiscount)`. The unit price already holds the line-level promotions; the line's own bundle/BxGy share comes off next; coupon and manual discounts are shared by value. For orders placed before module Z `PromotionDiscount` is 0 and the formula is the old one. Partial returns still add up to exactly the net line value (`LineRefund`).

## 6. Time and cache

All times use `TimeProvider` (UTC offsets; Asia/Dhaka is +06:00). A promotion window includes both ends: a flash sale that ends at 18:00:00 prices at 18:00:00 and stops after. Public catalog answers are cached with `Cache-Control: max-age = min(configured, seconds to the next promotion start or end)`, so a cached answer never shows a flash price after its window. Cards and details show promotions without a minimum cart value; the cart and checkout always use the full engine.
