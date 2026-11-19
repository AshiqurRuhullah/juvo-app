# Step 12d slice 2: site content screens (front end), notes

Branch `feature/12d-content-ui`. Contract: `docs/content-api.md`. FR-Q01, Q02, Q03, Q07, Q08.

## Built

Staff (menu group Content, permission codes from the contract, all `anywhere` scope):
- `/admin/content/pages` list of six pages with Draft/Published; `/admin/content/pages/[key]` editor (English and Bengali title, markdown body, meta fields, live safe preview with language tabs, save, publish and unpublish with consequence dialogs, API message for `CONTENT.BODY_UNSAFE` shown plainly).
- `/admin/content/brand` form (name, phone, WhatsApp, e-mail, address and hours en/bn, optional map point, logo reference read-only).
- `/admin/content/social` five platforms (https check, WhatsApp number accepted, show/hide, remove with confirmation).
- `/admin/content/badges` four fixed badges (text en/bn, on/off).
- Without the edit permission each screen is read-only; social and badges redirect to `/forbidden` without `content.social.edit` / `content.badge.edit`.

Shop: `/about`, `/faq`, `/delivery-policy`, `/return-policy`, `/privacy` (key `privacy-policy`), `/terms`. Unpublished or empty pages are a real 404 with a plain "not available yet" state (per-route `not-found.tsx`). `generateMetadata` uses meta title/description. Footer: links to published pages, brand block (address, phone, WhatsApp, e-mail, hours), social links; home page: trust badges row. All hidden when empty.

Code: `src/modules/content` (`logic.ts`, `markdown.ts` own renderer, `markdown-view.tsx`, `api.ts`, `public.ts`, `site-info.tsx`, `content-page.tsx`, screens), messages `content.en|bn.json`.

## Decisions (reason, how to change)
1. Own tiny markdown reader (headings, paragraphs, lists, bold, italic, links), no package, so no licence question. It builds a tree and React elements, never HTML text; links only https/http/mailto/tel/own path. Change: `markdown.ts`.
2. Headings typed in the body render one level lower (page title is the only h1).
3. Page bodies are read fresh on the server; footer, brand, social and badges are cached `CONTENT_REVALIDATE_SECONDS` (default 60, e2e sets 0). Change: env var.
4. The hard-coded home trust block (COD, delivery, 7-day returns, Facebook line) and the hard-coded footer e-mail and Facebook link were removed: content now comes only from the API. `e2e/store.spec.ts` line 43 now expects the footer instead of the Facebook link text.
5. Bengali page text is used only when the Bengali body exists, so title and body share one language; otherwise English.
6. Social "icons": lucide 1.66 has no brand icons, so links show a generic icon plus the platform name.
7. The logo reference is sent back unchanged on brand save (the API would clear it otherwise).
8. Publish is disabled while there are unsaved changes or the saved page lacks English title and text.

## Contract gaps
- OpenAPI names the content brand DTO `BrandDto`, clashing with the catalogue `BrandDto`; the generated type of `/content/brand` is wrong. Local type `BrandProfile` in `api.ts` is used with a cast. Fix: rename the DTOs on the backend (e.g. `BrandProfileDto`).
- No public endpoint lists published pages: the footer makes six cached page reads.
- No logo upload endpoint (the screen shows the reference only).

## Not built
Logo upload, banners, Facebook feed, Messenger/WhatsApp order button, contact page admin inbox (later slices of module Q); social brand icons; Bengali text needs native review.

## Tests
vitest `tests/content.test.ts` (25), full suite 500 passed; Playwright `content.spec.ts` 12 passed; `content-ui-checks.spec.ts` (360/768/1280, dark, Bengali, read-only; axe, overflow, 44 px, console) all passed on the desktop project after two fixes; Pixel 7 and iPhone 14 not run. tsc clean, eslint clean on own files, `next build` ok (first run ends at the known advisory notice).

## Traceability suggestions
FR-Q01 Done (about page screen and shop page); FR-Q02 Done (brand screen, footer block; map point stored, no map shown); FR-Q03 Done (social screen, footer links); FR-Q07 Done (badges screen, home row); FR-Q08 Done (five policy pages, meta fields, en/bn).
