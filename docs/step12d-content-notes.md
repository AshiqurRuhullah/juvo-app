# Step 12d slice 1: site content backend, decisions and notes

Contract: `docs/content-api.md`. Branch `feature/12d-content-backend`, migration `SiteContent`. These decisions are for the owner to confirm and for the lead to copy into `docs/assumptions.md` and `docs/understanding.md` (not edited here).

## Decisions (decision, reason, how to change)

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| 1 | New permission module `content.*` (page view/edit/publish, brand view/edit, social edit, badge edit); the older unused `cms.*` codes stay | The brief asked for `content.*`; removing `cms.*` would orphan role definitions and stored role rows | Drop `cms` lines from `PermissionCatalog` and `DefaultRoles` once nothing references them |
| 2 | Manager and Content Editor get `content.*`; Super Admin gets all | Same pattern as `cms.*` today | Edit `DefaultRoles.All` (verified on a fresh database only; whether an already-seeded system role receives new codes was not checked) |
| 3 | Page body = markdown only, raw HTML rejected (`CONTENT.BODY_UNSAFE`), not stripped | Stored text equals typed text; no sanitiser library needed (no package added) | Relax `ContentRules.CheckBody` if an HTML sanitiser package is approved later |
| 4 | Autolinks `<https://...>` count as HTML and are rejected; use `[text](https://...)` | A `<` followed by a letter is the single, simple rule | Narrow the `HtmlTag` pattern |
| 5 | About page is a normal markdown page (no structured sections list) | One format for all six pages; headings give sections | Add a sections table in a later slice if the owner wants blocks |
| 6 | A page publishes only with English title and body; Bengali optional, shop falls back to English | English is the base language for every other module | Change `ContentPage.CanPublish` |
| 7 | Emptying a published page unpublishes it | Never show a blank page | Remove the check in `ContentPage.Update` |
| 8 | Social URL: https only, dotted host, no user info; WhatsApp accepts a mobile number and stores `https://wa.me/<digits>` | FR-Q03 plus the shared phone normaliser | `ContentRules.CheckSocialUrl` |
| 9 | Brand `whatsApp` is strictly normalised (`+8801...`); brand `phone` only loosely checked | A shop line may be a landline | `ContentRules.IsPhoneText` |
| 10 | Social links created only when the brand profile is first seeded; the seeded Facebook link is enabled | The URL is an SRS fact; a link the owner deletes must not come back | `ContentSeeder` |
| 11 | Seed business name `Juvo BD` and email `juvobd@gmail.com`; nothing else written (no story, policy or badge text) | Never invent brand content | Owner edits in the admin screen |
| 12 | `content.social.edit` and `content.badge.edit` also cover the GET lists (no separate view code) | The brief defined only edit codes for these two | Add `view` codes to the catalog and split |
| 13 | Public reads: `Cache-Control: public, max-age=60`, strong ETag (SHA-256 of the JSON), 304 on match, storefront rate-limit policy | Cache friendly, edits visible within a minute | `ContentEndpoints.Cached` |
| 14 | Public text fields are `{ en, bn }` pairs; the client picks and falls back to English | Works with the cookie locale of the front end and keeps responses cacheable for everyone | Add `?lang=` later if needed |
| 15 | Logo is a file reference string (from the existing upload service), served as `/files/<ref>`; no new upload endpoint | Slice 1 scope | Add an upload endpoint or reuse the media one in slice 2 |
| 16 | Content rows are hard rows (no soft delete); social links can be deleted, pages, brand and badges cannot | Brief: no archive needed; audit log keeps history | n/a |

## Not done in this slice

Facebook feed block (FR-Q04), Messenger/WhatsApp order button (FR-Q05), contact page form and admin inbox (FR-Q06; the brand profile already supplies phone, email and map point), a logo upload endpoint, any front-end screen or storefront page, OpenAPI client regeneration (`npm run gen:api` is needed when the front end starts).

## Traceability suggestions

| ID | Suggested status | Implemented in |
| --- | --- | --- |
| FR-Q01 | Done (back end; About page as sectioned markdown in en and bn, publish control) | `ContentPage`, `ContentAdminService`, `/content/pages`, `/store/content/pages/{key}` |
| FR-Q02 | Done (back end; brand profile, contact, hours, map point) | `BrandProfile`, `/content/brand`, `/store/content/brand` |
| FR-Q03 | Done (back end; five platforms, https only) | `SocialLink`, `/content/social`, `/store/content/social` |
| FR-Q07 | Done (back end; four badges, en and bn, on/off, order) | `TrustBadge`, `/content/badges`, `/store/content/badges` |
| FR-Q08 | Done (back end; FAQ, delivery, return, privacy and terms pages editable, en and bn, with meta fields) | `ContentPage` keys, `/content/pages`, `/store/content/pages/{key}` |
| NFR security | Markdown body rejects script and raw HTML; https-only links | `ContentRules` |

## Gate numbers (this branch)

Build 0 warnings, 0 errors. See the commit message and the hand-off report for the test counts.
