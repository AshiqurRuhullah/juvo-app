# Storefront API (Step 7-a)

Base path `/api/v1`. Prices are tax-inclusive BDT. Errors are RFC 7807 problem documents with a stable `code` (listed at the end). The live, typed contract is the OpenAPI document (`/scalar/v1` in development); this page explains how the parts fit together for the front end.

## Public shop `/store/*`

Anonymous, rate limited per client address (`RateLimiting:StorefrontPerMinute`, 120). A customer token is understood but not needed. Read endpoints send a short `Cache-Control: public, max-age=...`.

| Method and path | What it gives |
| --- | --- |
| `GET /store/categories` | Active categories as a tree with `productCount` of published products. |
| `GET /store/home` | `newArrivals`, `bestSellers`, `featured`, `onSale`, `categories`. |
| `GET /store/products` | Paged cards. Filters: `category` (slug, includes children), `minPrice`, `maxPrice`, `size`, `colour`, `brand` (name or slug), `inStock`, `onSale`; `sort`: `newest` (default), `price-asc`, `price-desc`, `popular`; search `q` (small spelling mistakes tolerated); `page`, `pageSize`. |
| `GET /store/products/facets?category=` | Sizes, colours, brands and price range for the filter panel. |
| `GET /store/products/{slug}` | Detail: variants with `available` (capped at 10 = "10 or more"), `inStock`, `lowStock`; pictures; size chart; breadcrumb; related products. Never contains cost price or branch names. Unpublished products answer 404. |
| `GET /store/search/suggest?q=` | Up to a few products and categories while typing. |
| `GET /store/geo/countries` | Countries the shop delivers to. |
| `GET /store/geo/divisions`, `GET /store/geo/districts?divisionId=`, `GET /store/geo/districts/{id}/upazilas` | Bangladesh geography (8 / 64 / 494) with Bengali names and the district centre point. |
| `POST /store/delivery/quote` | Body `{ districtId | countryCode, upazilaId?, items[] | (subtotal, weightGrams) }`. Returns zone, base fee, weight fee, remote surcharge, free-delivery waiver, `total`, `amountToFreeDelivery`, estimated days and dates. |
| `POST /store/coupons/validate` | `{ code, subtotal }` gives the discount without a cart (stricter rate limit). |

### Cart `/store/cart`

A guest identifies the cart with the header **`X-Cart-Token`**. The first `POST /store/cart/items` without a token creates a cart and the answer contains `token` once; keep it (local storage) and send it with every cart call. Only its SHA-256 hash is stored. An unknown or missing token gives an empty cart, never an error. A signed-in customer's cart is found by their token claim (no header needed).

| Method and path | Notes |
| --- | --- |
| `GET /store/cart` | Whole cart: lines (today's `unitPrice`, `previousUnitPrice` and `priceChanged`, `maxQuantity`, `problem`: `REDUCED` or `OUT_OF_STOCK`), `subtotal`, `discount`, `total`, `weightGrams`, `coupon` (with `problem` code if it no longer applies), `hasProblems`, `priceChanged`. Totals count only what can be ordered now. |
| `POST /store/cart/items` | `{ variantId, quantity }`; adds to an existing line. |
| `PUT /store/cart/items/{variantId}` | `{ quantity }`; 0 removes. |
| `DELETE /store/cart/items/{variantId}`, `DELETE /store/cart` | Remove a line / empty the cart. |
| `POST /store/cart/coupon`, `DELETE /store/cart/coupon` | Apply or remove a coupon code. |
| `POST /store/cart/delivery-quote` | `{ countryCode?, districtId?, upazilaId? }`: delivery charge for the cart's payable goods (after coupon). |
| `POST /store/cart/merge` | **Customer token required.** `{ token }` moves a guest cart into the customer's cart; quantities are clamped to what is available; the guest cart is spent. Call it right after sign-in. |

Limits: 10 units per line, 50 lines, cart expires 30 days after its last change (configurable in `Storefront`).

## Customer account `/me` (customer token only; staff tokens get 403)

| Method and path | Notes |
| --- | --- |
| `GET /me`, `PUT /me` | Profile; change `fullName` and `email` (the mobile number is the identity; it is stored as `+8801...`). |
| `GET /me/summary` | `addressCount`, `wishlistCount`, `cartItemCount`, `defaultAddress`; order counts are 0 until Step 8. |
| `GET /me/consent`, `PUT /me/consent` | `{ type: MarketingSms | MarketingEmail | PrivacyPolicy, granted }`; each change is logged with its time. |
| `GET/POST /me/addresses`, `PUT/DELETE /me/addresses/{id}`, `POST /me/addresses/{id}/default` | Bangladesh: `districtId` required, `upazilaId` optional (must belong to the district). Abroad: `countryCode` and `city`. `phone` is normalised. Optional `latitude` and `longitude` come together. The first address is the default; deleting the default promotes another. Another customer's address answers `CUSTOMER.ADDRESS_NOT_FOUND`. |
| `GET/POST /me/wishlist`, `DELETE /me/wishlist/{productId}`, `POST /me/wishlist/{productId}/move-to-cart` | Saving twice changes nothing; move takes `{ variantId, quantity }`. |

Sign-in (`/auth/otp/request` and `/auth/otp/verify`) is described in `docs/auth-and-access.md` section 8. In sandbox mode (`Auth:Otp:ShowCodeInResponse`) the request answer carries `devCode`.

## Staff endpoints (permissions)

| Area | Endpoints | Permission |
| --- | --- | --- |
| Customers | `GET /customers`, `GET /customers/{id}` | `customers.customer.view` (details unmasked and audit-logged with `customers.customer.view-pii`) |
| | `POST /customers/{id}/deactivate`, `/activate` | `customers.customer.block` |
| Coupons | `GET /promotions/coupons`, `GET .../{id}` | `promotions.coupon.view` |
| | `POST`, `PUT .../{id}`, `DELETE .../{id}` | `promotions.coupon.create`, `.edit`, `.delete` (an unused coupon is deleted, a used one is switched off) |
| Delivery zones | `GET /delivery/zones`, `GET .../{id}` | `delivery.zone.view` |
| | `POST /delivery/zones`, `PUT .../{id}`, `PUT .../{id}/areas`, `DELETE .../{id}` | `delivery.zone.create`, `.edit`, `.edit`, `.delete` |
| | `PUT /delivery/zones/{id}/rates` | `delivery.rate.edit` |
| Countries | `GET /delivery/countries`, `POST`, `PUT .../{id}` | `delivery.country.view`, `delivery.country.enable` |
| Geography | `POST /delivery/upazilas/import` (max 1,000 items) | `delivery.zone.edit` |

## Error codes added in this step

`CART.NOT_FOUND`, `CART.ITEM_NOT_FOUND`, `CART.VARIANT_UNAVAILABLE`, `CART.STOCK_INSUFFICIENT`, `CART.TOO_MANY_LINES`, `CART.EMPTY`; `COUPON.NOT_FOUND`, `COUPON.INACTIVE`, `COUPON.NOT_STARTED`, `COUPON.EXPIRED`, `COUPON.USED_UP`, `COUPON.BELOW_MINIMUM`, `COUPON.CODE_TAKEN`; `CUSTOMER.NOT_FOUND`, `CUSTOMER.ADDRESS_NOT_FOUND`, `CUSTOMER.WISHLIST_FULL`, `CUSTOMER.WISHLIST_ITEM_NOT_FOUND`; `DELIVERY.ZONE_NOT_FOUND`, `DELIVERY.ZONE_CODE_TAKEN`, `DELIVERY.NOT_DELIVERABLE`, `DELIVERY.OVERWEIGHT`, `DELIVERY.COUNTRY_NOT_FOUND`, `DELIVERY.COUNTRY_DISABLED`, `DELIVERY.COUNTRY_CODE_TAKEN`, `DELIVERY.AREA_NOT_FOUND`, `DELIVERY.UPAZILA_TAKEN`; `AUTH.INVALID_EMAIL`, `AUTH.ACCOUNT_DISABLED`.

## Settings (`Storefront` section)

`FulfilmentBranchCodes` (empty = all active branches), `LowStockThreshold` 3, `MaxQuantityShown` 10, `MaxQuantityPerLine` 10, `MaxCartLines` 50, `CartDays` 30, `MaxWishlistItems` 200. Rate limit: `RateLimiting:StorefrontPerMinute` 120.
