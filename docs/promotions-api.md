# Promotions, pre-order API (modules Z and AA)

Base `/api/v1`. Enums travel as strings. Times carry a UTC offset; answers return Dhaka time (`+06:00`). Errors are RFC 9457 problem details with `code` and `correlationId`. Promotions are global like coupons (not per branch). Every staff endpoint names one permission (the endpoint policy test fails otherwise). The coupon endpoints stay under `/promotions/coupons` and gained one flag (`stacksWithPromotions`, default false).

## Staff: promotions (`/promotions`)

| Method and path | Permission | What |
| --- | --- | --- |
| `GET /promotions` | `promotions.promotion.view` | List, newest first. Query `type`, `status` (Draft, Scheduled, Active, Ended, Archived), `liveNow`, `search`, `page`, `pageSize`, `sort`. |
| `GET /promotions/{id}` | view | Detail with settings and targets (names resolved). |
| `POST /promotions` | `promotions.promotion.create` | Create as Draft. |
| `PUT /promotions/{id}` | `promotions.promotion.edit` | Change settings and targets (type cannot change). |
| `POST /promotions/{id}/activate` | edit | Switch on. Runs inside its schedule. |
| `POST /promotions/{id}/deactivate` | edit | Switch off (Draft). |
| `POST /promotions/{id}/archive` and `DELETE /promotions/{id}` | `promotions.promotion.delete` | Archive (never erased). |
| `POST /promotions/{id}/duplicate` | create | Copy as a new Draft ("... (copy)", campaign address `-copy`). |
| `POST /promotions/{id}/images/{kind}` | edit | Multipart `file`; `kind` = `hero` or `banner`; JPEG, PNG or WebP, at least 1000 px long side, at most 10 MB; stored through `IFileStorage` as WebP. |
| `POST /promotions/preview` | view | Price a sample cart with a chosen promotion set. |
| `GET /promotions/report` | view | Campaign report for every promotion (paged; `from`, `to`, `type`). |
| `GET /promotions/{id}/report` | view | Report of one promotion (`from`, `to`). |

Status is derived: Archived if archived; Draft if never switched on or switched off; otherwise Scheduled before `startsAt`, Active from `startsAt` to `endsAt` (both ends included), Ended after.

### Create / update body (`SavePromotionRequest`)

Common: `nameEn` (required, 150), `nameBn`, `type` (`FlashSale`, `ProductDiscount`, `Bundle`, `BuyXGetY`, `FreeDelivery`, `FestivalCampaign`), `startsAt`, `endsAt`, `priority` (-1000..1000, higher wins ties), `stackMode` (`Exclusive`, `StacksWithOrderPromotions` default, `StacksWithCoupon`), `channels` (`Web`, `SocialDesk`, `Pos`, combinable, default all, JSON string `"Web, SocialDesk"`), `minCartValue`, `usageLimit`, `labelEn`/`labelBn` (short customer label, default the name), `targets[]` (`{ kind: Product|Variant|Category|Brand|Everything, targetId, role: Main|Buy|Get|BundleItem, quantity }`).

| Type | Fields that count |
| --- | --- |
| FlashSale | `endsAt` required (countdown); `discountKind` (`Percent`, `Fixed`, `FixedPrice`), `discountValue`, optional `maxDiscount`; targets role Main. |
| ProductDiscount | `discountKind`, `discountValue`, `maxDiscount`; targets role Main (or `Everything`). |
| Bundle | `bundlePrice`; targets role BundleItem with `quantity`; at least two units in total. |
| BuyXGetY | `buyQuantity`, `getQuantity`, `getDiscountPercent` (100 = free); targets role Buy; targets role Get optional (without it the Buy set is used). |
| FreeDelivery | `minCartValue` (0 = always), optional `zoneCodes[]`; no targets. |
| FestivalCampaign | `slug` (lower-case letters, digits, hyphen), `titleEn`, `titleBn`, `bodyEn`, `bodyBn`, `bannerSlot` (`HomeTop`, `ShopTop`, `Category`), `bannerCategoryId`; targets role Main (the product set); optional `discountKind`/`discountValue`. Pictures through the images endpoint. |

```json
POST /api/v1/promotions
{ "nameEn": "Eid flash", "nameBn": "ঈদ ফ্ল্যাশ", "type": "FlashSale",
  "startsAt": "2026-12-01T10:00:00+06:00", "endsAt": "2026-12-01T18:00:00+06:00",
  "priority": 5, "stackMode": "StacksWithOrderPromotions", "channels": "Web, SocialDesk",
  "discountKind": "Percent", "discountValue": 20, "labelEn": "Eid flash", "labelBn": "ঈদ ফ্ল্যাশ",
  "targets": [ { "kind": "Product", "targetId": "01a1...", "role": "Main", "quantity": 1 } ] }
```

Answer `201` (`PromotionDetailDto`): `id`, `status`, `isEnabled`, `isArchived`, `usedCount`, all fields above, `heroImageUrl`, `bannerImageUrl`, `targets[]` with `name`, `createdAt`.

```json
{ "bundle": { "nameEn": "Tee and cap", "type": "Bundle", "bundlePrice": 1000,
  "targets": [ { "kind": "Product", "targetId": "...", "role": "BundleItem", "quantity": 1 },
               { "kind": "Product", "targetId": "...", "role": "BundleItem", "quantity": 1 } ] },
  "buyXGetY": { "nameEn": "Buy 2 get 1", "type": "BuyXGetY", "buyQuantity": 2, "getQuantity": 1, "getDiscountPercent": 100,
  "targets": [ { "kind": "Category", "targetId": "...", "role": "Buy" } ] },
  "freeDelivery": { "nameEn": "Free delivery from 2500", "type": "FreeDelivery", "minCartValue": 2500, "zoneCodes": ["DHAKA"], "stackMode": "StacksWithCoupon" } }
```

### Preview

`POST /promotions/preview` (permission view)

```json
{ "lines": [ { "sku": "TEE-RED-M", "quantity": 2 } ], "couponCode": "SAVE10",
  "promotionIds": ["<draft id>"], "at": "2026-12-01T12:00:00+06:00", "channel": "Web" }
```

`promotionIds` omitted: the promotions running at `at` (default now). `promotionIds` given (even empty): exactly that set, as if running now (drafts included, schedule ignored). Answer: `lines[]` (`listPrice`, `baseUnitPrice`, `unitPrice`, `lineTotal`, `orderDiscount`, `netTotal`, `adjustments[]`), `totals`, `promotions[]`, `coupon`, `freeDelivery[]`, `explanation`, `couponErrorCode`, `couponErrorMessage`. Nothing is saved or counted.

### Report

`GET /promotions/{id}/report` ->

```json
{ "promotionId": "...", "nameEn": "Eid flash", "type": "FlashSale", "status": "Active", "orders": 2, "units": 3, "revenue": 2400.00, "discountCost": 600.00 }
```

From the immutable order line breakdown: `orders` = distinct non-cancelled orders with a line that carries the promotion, `units` = quantity of those lines, `revenue` = sum of `lineTotal - promotionDiscount` of those lines, `discountCost` = sum of the promotion's adjustment amounts. A person with the permission only in some branches gets the orders of those branches.

## Staff: pre-orders

| Method and path | Permission | What |
| --- | --- | --- |
| `GET /orders/preorders` | `orders.order.view` (per branch) | Queue, oldest first. Query `state` (Waiting, Ready), `branchId`, `variantId`, `expectedBefore` (date), `search` (number, mobile, name, SKU). Cancelled orders are not listed. |
| `POST /orders/preorders/allocate` | `orders.order.edit` (in the branch asked for) | Body `{ "branchId": null, "variantId": null }`. Allocates stock to waiting lines, oldest first. Answer `{ "linesAllocated": 1, "ordersReady": 1, "readyOrderIds": ["..."] }`. |
| `GET /catalog/products/{id}/preorder` | `catalog.product.view` | Switch of the product and of each variant, with the effective values. |
| `PUT /catalog/products/{id}/preorder` | `catalog.product.edit` | `{ "allowPreorder": true, "expectedShipDate": "2026-12-20", "advancePercent": 50 }` |
| `PUT /catalog/products/{id}/variants/{variantId}/preorder` | `catalog.product.edit` | Same body; `allowPreorder: null` inherits the product. |

Queue item: `orderId`, `number`, `branchId`, `branchCode`, `status`, `state`, `customerName`, `customerMobile`, `placedAt`, `grandTotal`, `advanceRequired`, `advancePaid`, `lines[]` (`itemId`, `variantId`, `sku`, `name`, `quantity`, `waiting`, `expectedShipDate`, `allocatedAt`).

Rules: a line is a pre-order line when its variant allows pre-orders, tracks stock and the shop has NO available stock of it (all selling branches together). A variant with some stock is sold normally and limited to what is there. A pre-order line reserves nothing; the order is `Waiting` and cannot be confirmed (`PREORDER.AWAITING_STOCK`) or packed. Allocation is also run automatically after each purchase receipt in the receiving branch. FIFO per branch and variant by order time (then order number), all or nothing per line, strictly in order (a younger line does not jump an older one that does not fit). When the last waiting line of an order is served the order becomes `Ready` (normal confirm and pack rules) and the customer gets template `order.preorder-ready` once (deduplicated by order). Advance: `advancePercent` of the line value after promotions (before coupon), added to the order's `advanceRequired` (cash on delivery) and paid through the usual advance payments (`POST /payments/orders/{id}/advance`); online methods are paid in full anyway.

## Shop (anonymous)

- Product cards and details (`GET /store/products`, `/store/products/{slug}`, `/store/home`): `price` is the price now (promotion included), `regularPrice` the struck-through list price, `onSale`, `discountPercent`, `promotion` (`promotionId`, `type`, `price`, `listPrice`, `labelEn`, `labelBn`, `endsAt` for the countdown), `allowPreorder`, `isPreorder`, `preorderExpectedShipDate`, `preorderLabelEn`, `preorderLabelBn`. Variants carry `price`, `listPrice`, `promotion`, `isPreorder` and the same pre-order fields. Cards show promotions without a minimum cart value; price filter and sort use the sale price.
- `GET /store/campaigns/{slug}`: `slug`, `titleEn`, `titleBn`, `bodyEn`, `bodyBn`, `heroImageUrl`, `bannerImageUrl`, `labelEn`, `labelBn`, `startsAt`, `endsAt`, `products[]` (cards, at most 60). Only while Active, else `404 PROMO.CAMPAIGN_NOT_FOUND`.
- `GET /store/banners?slot=HomeTop|ShopTop|Category&category=<category slug>`: active banners (`promotionId`, `slot`, `imageUrl`, `titleEn`, `titleBn`, `slug`, `labelEn`, `labelBn`, `endsAt`), highest priority first. A Category banner shows on its category and below it.
- Cart: `GET /store/cart` and every cart answer now carry per line `listPrice`, `adjustments[]`, `orderDiscount`, `promotionLabelEn/Bn`, `isPreorder`, `preorderExpectedShipDate`, `preorderLabelEn/Bn`; cart `promotionDiscount`, `listSubtotal`, `explanation`, `freeDelivery[]`, `hasPreorder`, `preorderAdvance`; `coupon` gained `applied`, `notAppliedReasonEn`, `notAppliedReasonBn`. `subtotal` is the sum of line totals, `discount` is the coupon discount, `total = subtotal - promotionDiscount - discount`.
- `POST /store/checkout/quote`: lines with the same new fields, `promotionDiscount`, `listSubtotal`, `explanation`, `coupon`, `hasPreorder`; `advanceRequired` includes the pre-order advance.
- `POST /store/cart/delivery-quote` and `POST /store/delivery/quote`: a running free-delivery promotion makes `total` 0, `isFree` true, `freeDeliveryWaiver` the waived charge, `promotionLabelEn/Bn`. The items-based public quote judges the threshold on catalog (sale) prices.
- Orders (`/me/orders`, tracking, staff detail): lines carry `listPrice`, `promotionDiscount`, `adjustments[]`, `isPreorder`, `preorderExpectedShipDate`, `preorderLabelEn/Bn`, `waitingForStock`; `money` has `promotionDiscount` and `listSubtotal`; the order has `preorderState` (None, Waiting, Ready). Invoices: lines carry `listPrice`, `promotionDiscount`, `isPreorder`, `preorderExpectedShipDate`, labels; the invoice has `orderPromotionDiscount`, `listSubtotal`, `hasPreorder`; `discount` includes the promotion discount.
- Public cacheable answers send `Cache-Control: max-age` cut at the next promotion start or end.

## Error codes

| Code | Meaning |
| --- | --- |
| `PROMO.NOT_FOUND` (404) | The promotion does not exist (or a promotion chosen in a preview does not exist or is archived). |
| `PROMO.MULTIPLE_COUPONS` (409) | More than one coupon was sent; two coupons never combine. |
| `PROMO.SLUG_TAKEN` (409) | Another campaign already uses this web address. |
| `PROMO.ARCHIVED` (409) | An archived promotion cannot be changed, switched on or off. |
| `PROMO.INCOMPLETE` (409) | The promotion misses something it needs before it can be switched on. |
| `PROMO.CAMPAIGN_NOT_FOUND` (404) | The campaign page does not exist or is not running. |
| `PROMO.TARGET_NOT_FOUND` (404) | A product, variant, category, brand or the banner category in the request does not exist. |
| `PROMO.USED_UP` (409) | A promotion reached its usage limit between pricing and placing the order; check the order and confirm again. |
| `PREORDER.AWAITING_STOCK` (409) | A pre-order that waits for stock cannot be confirmed or packed. |

Validation problems use `GENERAL.VALIDATION` with field errors (`nameEn`, `discountValue`, `targets`, `endsAt`, `slug`, `bundlePrice`, `buyQuantity`, `lines`).
