# SEO basics (Step 12e) - notes

Covers FR-R01 (metadata), FR-R02 (sitemap and robots), FR-R03 (structured data and social cards). Front end only; no backend change.

## What is built

| Piece | Where | Notes |
| --- | --- | --- |
| Helpers (site URL, absolute URLs, private path list, JSON-LD builders, safe JSON escaping) | `src/lib/seo.ts` | Pure, covered by `tests/seo.test.ts` (17 tests). |
| `robots.txt` | `src/app/robots.ts` | Allows `/`; disallows `/admin /account /cart /checkout /api /login /first-login /forgot-password /reset-password /track /order/ /pay/ /search /unsubscribe /contact/order-help /design /forbidden /unauthorized`; announces `<site>/sitemap.xml`. `/media` stays crawlable (product pictures). |
| `sitemap.xml` | `src/app/sitemap.ts` | Home, `/shop`, `/contact`, every category in the tree, every published product, every published content page (with the page's real `updatedAt` as `lastmod`). Built on request. |
| Site address | `NEXT_PUBLIC_SITE_URL` (existing), optional runtime `SITE_URL` | Documented in `.env.example`. Fallback is `http://localhost:3000`; no production domain is in the code. |
| Title template and site name | `src/app/layout.tsx` | `%s | <business name>` from the public brand (`/store/content/brand`), else "Juvo BD". |
| Canonical links | home, `/shop`, category, product, content pages, `/contact` | Relative values resolved against `metadataBase`. Filter/sort query strings on `/shop` and categories point back to the plain address. |
| noindex | cart, checkout, track, pay, order confirmation, unsubscribe, search, order-help, admin layout, account layout, sign-in/password pages (new: `(auth)/layout.tsx`, order-help), unpublished content page, unknown product/category | Page-level guard on top of robots.txt. |
| Open Graph and Twitter | product, category, content pages | Product image is the first picture through `/media/...` as an absolute URL; products without a picture fall back to the shop's own `/brand/og-image.png`. |
| JSON-LD | product page: `Product` (name, image list, description, sku, brand if present, `offers` with BDT price, availability, url) and `BreadcrumbList`; category page: `BreadcrumbList`; home: `Organization` | JSON escaped with `<` as `<` (and line separators). Empty fields are left out. |

## Decisions

- **No `aggregateRating`.** The public store API has no reviews endpoint yet; the builder supports a rating but the product page passes none. When reviews ship (FR on reviews), pass `{ value, count }` from approved reviews in `src/app/(storefront)/product/[slug]/page.tsx`.
- **Availability** is `InStock` if any variant is in stock, else `PreOrder` if any variant is a pre-order, else `OutOfStock` (same rule as before this step).
- **Price in JSON-LD** is `card.price` (the current selling price, promotion included), currency BDT (BR-005).
- **Organization** uses the brand's business name, the site address, the brand logo only when set (served via `/media` if it is an API file), and `sameAs` from the public (enabled) social links with `http(s)` only. Nothing is written for missing values.
- **Product title/description** use the product's own meta title/description, then name, tagline, first 160 characters of the description. No invented text. Category description uses the existing translated sentence "Shop <name>" because the API has no category description.
- **Sitemap size**: products are read 100 per request (API maximum), at most 50 requests, so at most 5,000 products are listed (a sitemap file may hold 50,000). Raise `MAX_PRODUCT_PAGES` in `src/app/sitemap.ts` or split with `generateSitemaps` if the catalogue grows past this.
- **robots.ts and sitemap.ts are dynamic** (`force-dynamic`) so the site address and catalogue are read when the server runs, not frozen at build time. The sitemap reads the API through the same one-minute cache as the shop pages; a page published a moment ago can take up to a minute to appear (content pages are read fresh and appear at once).
- **Brand pages**: the storefront has no brand pages (brand is a filter on `/shop`), so none are in the sitemap.
- **hreflang is not applicable**: language is a cookie with no URL prefix (`/product/x` is the same address in English and Bengali), so there are no alternate-language URLs to declare.

## Contract gaps (public API)

- Products and categories have no update time, so their sitemap entries have no `lastmod`.
- No public reviews endpoint (no `aggregateRating`).
- No public list of campaigns, so campaign pages (`/campaign/<slug>`) are not in the sitemap.
- Category has no description field; the brand DTO has no tagline or site name separate from the business name.

## Not part of this step

- FR-R04 analytics hooks and cookie banner, FR-R05 catalogue feed (Facebook/Google), FR-R06 newsletter sign-up.
- FR-R07 page speed: stays a note. Product pages are server-rendered with the data in the first response; no measurement was taken in this step.

## Tests

- Vitest `tests/seo.test.ts` (helpers, escaping, noindex path rules, JSON-LD shape).
- Playwright `e2e/seo.spec.ts` (desktop project only): robots.txt, sitemap with a sample product and without private routes, unpublished content page absent then present after publishing, product title/canonical/og/twitter/JSON-LD price and availability, home Organization, noindex on cart, checkout, login, search, track.
- The e2e web server sets `NEXT_PUBLIC_SITE_URL` to its own address.

## How to change

- Site address: set `NEXT_PUBLIC_SITE_URL` (build) or `SITE_URL` (run) to the real https address before go-live (Step 14).
- Private routes: edit `PRIVATE_PATHS` in `src/lib/seo.ts` (robots.txt and the sitemap guard both use it).
