# Site content API (module Q, slice 1)

Pages, brand profile, social links and trust badges. Base path `/api/v1`. Errors are RFC 9457 ProblemDetails with a stable `code` (section 6). Enums travel as strings. Content is a global module: the staff permissions are not branch scoped. Slice 1 covers FR-Q01, Q02, Q03 and Q07; banners, SEO fields beyond page meta, contact messages and the rest of module Q are later slices.

## 1. Permissions

| Code | Allows |
| --- | --- |
| `content.page.view` | list and read pages (drafts included) |
| `content.page.edit` | save page texts |
| `content.page.publish` | publish and unpublish a page |
| `content.brand.view` | read the brand profile |
| `content.brand.edit` | change the brand profile |
| `content.social.edit` | read and change social links (one right for both) |
| `content.badge.edit` | read and change trust badges (one right for both) |

Super Admin holds them automatically; Manager and Content Editor hold `content.*`. The older `cms.*` codes stay in the catalog and are not used by these endpoints. The public endpoints (section 3) are anonymous and use the storefront rate limit.

## 2. Staff endpoints (`/content`)

| Method and path | Permission | Notes |
| --- | --- | --- |
| `GET /content/pages` | page.view | six pages in fixed order |
| `GET /content/pages/{key}` | page.view | `key` is one of `about`, `faq`, `delivery-policy`, `return-policy`, `privacy-policy`, `terms`; other keys give 404 `CONTENT.NOT_FOUND` |
| `PUT /content/pages/{key}` | page.edit | body below; emptying a published page takes it down |
| `POST /content/pages/{key}/publish` | page.publish | needs English title and English body, else 400 `CONTENT.PAGE_INCOMPLETE` |
| `POST /content/pages/{key}/unpublish` | page.publish | back to draft |
| `GET /content/brand` | brand.view | the single profile |
| `PUT /content/brand` | brand.edit | body below |
| `GET /content/social` | social.edit | all links, enabled or not |
| `PUT /content/social/{platform}` | social.edit | create or change; `platform` is `facebook`, `instagram`, `tiktok`, `youtube`, `whatsapp` (any case) |
| `DELETE /content/social/{platform}` | social.edit | 204; 404 when there is none |
| `GET /content/badges` | badge.edit | the four badges |
| `PUT /content/badges/{key}` | badge.edit | `secure-payment`, `delivery-coverage`, `return-policy`, `support-hours` |

Page save: `{ titleEn, titleBn, bodyEn, bodyBn, metaTitleEn, metaTitleBn, metaDescriptionEn, metaDescriptionBn }` (all optional strings; blank becomes null). Page answer adds `key`, `isPublished`, `canPublish`, `publishedAt`, `updatedAt`.

Brand save: `{ businessName (required), logoFileRef, addressEn, addressBn, phone, whatsApp, email, workingHoursEn, workingHoursBn, latitude, longitude }`. `whatsApp` is normalised with the shared mobile rules (`01712345678` becomes `+8801712345678`). `phone` is only checked loosely (digits, space, `+ - ( )`, 6 to 20 characters) because a shop line can be a landline. `latitude` and `longitude` come together or not at all. `logoFileRef` is the relative file reference returned by an upload (no `..`, no scheme, no leading slash).

Social save: `{ url, isEnabled, sortOrder }`. `url` must be an absolute `https` address with a dotted host and no user info; for `whatsapp` a mobile number is accepted and stored as `https://wa.me/<digits>`. Invalid address gives 400 `CONTENT.URL_INVALID`.

Badge save: `{ textEn, textBn, isEnabled, sortOrder }`. Enabling needs English text (400 `CONTENT.INVALID`). Text is plain, at most 160 characters.

Limits: title and meta title 200, meta description 400, body 30 000 characters, address 500, working hours 300, business name 120, sort order 0 to 1000.

## 3. Public endpoints (`/store/content`)

| Path | Answers |
| --- | --- |
| `GET /store/content/pages/{key}` | a published page: `{ key, title, body, metaTitle, metaDescription, updatedAt }`, each text as `{ en, bn }` (a side can be null: fall back to English). A draft, an emptied page or an unknown key gives 404 `CONTENT.NOT_FOUND` |
| `GET /store/content/brand` | `{ businessName, logoUrl, address, phone, whatsApp, email, workingHours, latitude, longitude }`; `logoUrl` is `/files/<ref>` or null (the front end serves it through `/media`) |
| `GET /store/content/social` | enabled links `[{ platform, url, sortOrder }]` in display order |
| `GET /store/content/badges` | enabled badges `[{ key, text: { en, bn }, sortOrder }]` in display order |

Successful answers carry `Cache-Control: public, max-age=60`, a strong `ETag` (hash of the body) and `Vary: Accept-Language`; a matching `If-None-Match` gets 304. Staff-only fields (`isPublished`, `createdBy`, ids) are never returned.

## 4. Body safety

Page bodies are markdown. Raw HTML is not accepted at all: any `<` followed by a letter, `/`, `!` or `?` (a tag, closing tag, comment) or a link or image whose address starts with `javascript:`, `data:` or `vbscript:` is rejected with 400 `CONTENT.BODY_UNSAFE` and a message per field (`bodyEn`, `bodyBn`); nothing is stored. Rejecting is chosen over stripping so what staff typed is exactly what is stored and shown. The shop must still render markdown with raw HTML off. Short text fields (titles, meta, address, hours, badge text) reject tags with 400 `CONTENT.INVALID`. Line endings are normalised to `\n`.

## 5. Seed and audit

On start-up (idempotent, never touches an existing row): six pages as empty unpublished drafts, one brand profile (`Juvo BD`, email `juvobd@gmail.com`), one social link (Facebook `https://facebook.com/juvobd`, enabled; created only when the brand profile is first created, so a removed link stays removed), four trust badges with no text and switched off. No story, policy or badge text is written by the system.

Every row is an `Entity`, so each create, change and delete (including publish and unpublish) is written to the audit log with old and new values by the persistence layer.

## 6. Error codes

`CONTENT.NOT_FOUND` (404), `CONTENT.INVALID` (400, field map), `CONTENT.BODY_UNSAFE` (400), `CONTENT.PAGE_INCOMPLETE` (400), `CONTENT.URL_INVALID` (400). Missing permission gives 403, no token 401.
