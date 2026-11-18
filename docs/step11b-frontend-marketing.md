# Step 11B front end: marketing (module AC)

Builder D ("marketing"). Branch `feature/11b-front-marketing`. Contracts: `docs/marketing-api.md`, `docs/step11b-backend-marketing.md`. Front-end module: `juvo-front/src/modules/marketing`. Messages: `src/messages/marketing.{en,bn}.json` (admin) and `src/messages/marketingStore.{en,bn}.json` (the two public pieces).

## 1. What was built

Admin routes (all staff, permission checked on the server with scope "anywhere", because the marketing permissions are global):

| Route | Permission to open | What it does |
| --- | --- | --- |
| `/admin/marketing/segments` | `marketing.segment.view` | Segment list (built-in first) with live total and per-channel reachable counts; rule builder dialog for the 9 rule types with AND/OR and per-field errors from the API; read-only view of a built-in segment with copy; members dialog (paged, sortable, search; mobile and email shown as the API gives them); copy; delete with the in-use refusal explained. |
| `/admin/marketing/campaigns` | `marketing.campaign.view` | Campaign report: status, channel, recipients, sent, failed, skipped, unsubscribed, attributed orders and revenue; filters status, channel, segment; search. |
| `/admin/marketing/campaigns/new` | `marketing.campaign.create` | Builder: name, segment (with live size), SMS or email (no WhatsApp), subjects and texts in English and Bengali, placeholder buttons, character and SMS-part counter, unknown placeholders flagged. |
| `/admin/marketing/campaigns/[id]` | `marketing.campaign.view` | Status and dates (scheduled time as returned in Dhaka time), editor while draft or scheduled, preview (real counts, sample messages in both languages), send test to myself, schedule (entered as Dhaka time, sent with `+06:00`), send now (confirmation states the recipient count from a fresh preview, warns about quiet hours), cancel (says queued messages are not recalled), delete, results (sent, delivered, failed, in flight, skipped by reason, unsubscribed, attributed orders and revenue with the last-touch window), recipient list (masked, filters). Polls every 5 s while the campaign is Sending. |
| `/admin/marketing/automations` | `marketing.automation.view` | The four automations: on/off switch (confirmation before switching on), channel, delay (with what it means per automation and a readable unit), window (only where the API uses it), cap days, texts in both languages with the placeholders the API reports, dry run, per-automation results. |
| `/admin/marketing/settings` | `marketing.campaign.view` (change: `marketing.campaign.send`) | Throttle, quiet hours (Dhaka), frequency cap, attribution window. |

Public:

* Product page: `NotifyMe` (in `modules/marketing/notify-me.tsx`) under the availability line. Shown for a sold-out variant that cannot be pre-ordered; when nothing is chosen yet and everything is sold out, the visitor picks the option. Guest gives a mobile number, a signed-in customer only confirms. Handles "already subscribed" (a success), the per-number limit (429 `MARKETING.SUBSCRIPTION_LIMIT`), the per-address rate limit (429 without a marketing code), contact, in stock, not found, network.
* `/unsubscribe?token=...` (storefront frame, no sign-in, `noindex`): confirm screen with one button that POSTs the token, success, already-unsubscribed, invalid, missing and failure states; English or Bengali by the locale cookie.

Menu (`src/lib/nav.ts`, marketing group): "Segments", "Campaigns" (now `ready: true`), "Automations". The sending rules are reached from a button on the campaigns and automations pages.

## 2. Shared files touched (all additive)

* `src/lib/nav.ts`: `Zap` icon import, two new items (segments, automations), `campaigns` flipped to ready.
* `src/messages/load.ts`: `"marketing", "marketingStore"` appended to `MESSAGE_FRAGMENTS`.
* `src/messages/scopes.ts`: `"marketingStore"` added to the storefront scope (admin scope is "all").
* `src/messages/en.json`, `bn.json`: `nav.items.segments` and `nav.items.automations`; the 19 `errors.MARKETING_*` texts inserted before `TICKET_NOT_FOUND`.
* `playwright.config.ts`: `marketing|marketing-ui-checks` added to the desktop `testMatch`.
* `src/modules/store/product-view.tsx`: one import and one `<NotifyMe ... />` line between the availability line and the quantity row.
* `.gitignore`: screenshot pattern for `docs/ui-checks/marketing/` and four kept files.

## 3. Decisions

| # | Decision | Reason | How to change |
| --- | --- | --- | --- |
| 1 | Two message fragments: `marketing` (admin) and `marketingStore` (public). | The storefront scope would otherwise ship all admin texts (about 15 kB compressed) to every shopper. | Merge the files and rename the namespace. |
| 2 | The rule form shows only the fields of the chosen rule type and the request carries only those (`RULE_FIELDS` in `logic.ts`). | The API refuses a field its type does not use (backend decision 4). | Edit `RULE_FIELDS`. |
| 3 | Segment count is one request per visible row (`GET /segments/{id}/count`), cached 15 s. | The list endpoint carries no counts; ten rows per page keeps it at ten set-based queries. | Ask for counts in the list DTO. |
| 4 | The live count in the editor calls `POST /segments/preview-count` after a 500 ms pause and only when every rule is complete. | Avoids requests for half-typed rules (the API would answer a validation error). | `usePreviewCount` and the `complete` check in `segment-editor.tsx`. |
| 5 | The editor is a dialog (bottom sheet on phones). | One list route, as asked. | A full page route could reuse `SegmentEditorDialog`'s body. |
| 6 | Wishlist rule: optional product picker (search by name, needs `catalog.product.view`); ids from a saved segment show a short id because the API returns no names. | The API holds ids only. | Resolve names by id when a bulk lookup exists. |
| 7 | Schedule entered as Dhaka time (`datetime-local`, read as UTC+06:00 whatever the browser's zone), sent as `YYYY-MM-DDTHH:mm:00+06:00`; the displayed schedule is the API's `scheduledAtDhaka`. | Bangladesh has one offset all year, so a fixed offset is exact; the API wants an offset. | `DHAKA_OFFSET_HOURS` in `logic.ts`. |
| 8 | "Send now" first calls preview and states `recipients` from it in the confirmation. The campaign must be saved (buttons disabled while the form has unsaved changes). | Preview, test, schedule and send use the saved text. | `askSend` in `campaign-detail.tsx`. |
| 9 | Switching an automation on asks first (consent wording differs for back in stock); switching off is immediate. The switch sends only `isEnabled`, the Save button sends the other fields. | Switching on starts real messages. | `AutomationCard`. |
| 10 | Settings screen edits only throttle, quiet hours, frequency cap and attribution window; the other setting fields (shop name, link address, max notify-me per number, SMS opt-out text) are sent as `null` ("leave as is"). | Brief scope. | `settingsToRequest`. |
| 11 | Quiet hours are described as SMS-only in the automations page note and the settings screen. | Backend decision 8. | Message keys `quietHoursScope`, `quietScope`. |
| 12 | The unsubscribe page never acts on open: the button POSTs the token. | Backend notes: a mail scanner opening a GET link would unsubscribe a customer. | The links in messages still point at the API's GET address until `Marketing:UnsubscribeUrl` is set (see merge notes). |
| 13 | SMS counter is labelled as an estimate: GSM letters 160/153, any other letter (Bengali) makes it Unicode 70/67; the footer note says the opt-out text is added. | The real provider rules are not in the API. | `smsLength` in `logic.ts`. |
| 14 | The campaign detail polls results and the campaign every 5 s while the status is Sending. | Sending is carried on by a worker. | `useCampaign`, `useCampaignResults`. |

## 4. Contract gaps

* The campaign list and the segment list carry no segment size or channel reach: counts are one request per row (decision 3).
* The API returns product ids only in a saved wishlist rule: the picker cannot show names for them.
* Delivery receipts do not exist: "delivered" equals "sent" and the UI says so next to the number.
* `MarketingSettingsDto` has `shopName`, `linkUrl`, `maxNotifyMePerMobile` and `smsOptOutText`, but the brief limits this screen to four groups. They stay editable only through the API.
* A request with `isEnabled` only is typed by the generated client as requiring every field (nullable); the call passes the partial body through a cast. The API accepts partial bodies (documented: "only the fields you send").
* Per-address rate limit for notify-me answers 429 without a marketing code: the UI shows the generic "too many requests" text. The e2e API runs with rate limiting off, so this path is covered by the unit test of `notifyErrorKey` only.

## 5. Partial and not built

* No WhatsApp channel, A/B tests or click tracking (the backend has none). Nothing fakes them.
* "Notify me" for a variant that can be pre-ordered is hidden through `isPreorder` (the field exists on `StoreVariant`); the pre-order screens are builder B's, so that case has a unit test (`canNotify`) but no browser test.
* Pixel 7 and iPhone 14 runs of the UI-check spec: see section 6.

## 6. Tests

Last run on the final tree: `tsc --noEmit` clean, `eslint src e2e tests` clean, vitest 20 files / 293 tests passed (36 new in `tests/marketing.test.ts`), `next build` compiles and type-checks and writes every route (see the note below), Playwright desktop project `e2e/marketing.spec.ts` + `e2e/marketing-ui-checks.spec.ts` 23 of 23 passed (setup and cleanup included: 14 flow tests, 8 UI-check tests: 360, 768, 1280 px for the admin and the public pages, plus dark theme). Pixel 7 and iPhone 14 were not run for the UI checks: the `android`/`iphone` projects only match `shell|devices` specs and the config was not widened. Note on the build: `next build` ends with exit code 1 after writing the output, because Next 16.4.4 runs a known-vulnerability advisory check (`SecurityFatalError`); the compile, the TypeScript step and the route table succeed and the e2e runs use that output. No dependency was changed. Files: `tests/marketing.test.ts` (vitest, 36 tests: rule mapping and visible fields, validation, API field-error parsing, SMS counter, Dhaka time and schedule rules, status to actions, automation and settings validation, notify-me error mapping, unsubscribe states, message key parity), `e2e/marketing.spec.ts` (flows against the real API), `e2e/marketing-ui-checks.spec.ts` (360, 768, 1280 px, dark theme, screenshots in `docs/ui-checks/marketing/`).

## 7. Merge notes for the lead

* Set `Marketing:UnsubscribeUrl` to `<web address>/unsubscribe` in the deployment configuration so the link in messages opens the confirm page (the default points at the API's GET endpoint, which unsubscribes at once).
* `load.ts`, `scopes.ts`, `nav.ts`, `en.json`, `bn.json` and `playwright.config.ts` edits are single-line additions; keep both sides when merging.
* Nothing under `juvo-be/` was touched.

## 8. Traceability suggestions (`docs/understanding.md`)

| ID | Status | Implemented in | Notes |
| --- | --- | --- | --- |
| FR-AC01 Customer segments | Done | `modules/marketing/segments-view.tsx`, `segment-editor.tsx`, `/admin/marketing/segments` | 9 rule types, AND/OR, live and per-channel counts, members, copy of built-ins. |
| FR-AC02 Campaigns SMS and email with consent | Done (SMS and email) | `campaigns-list.tsx`, `campaign-form.tsx`, `campaign-detail.tsx`, `/admin/marketing/campaigns*` | Preview, test, schedule (Dhaka time), send now, cancel, recipients. WhatsApp not available in the API. Public unsubscribe page `/unsubscribe`. |
| FR-AC03 Automations | Done | `automations-view.tsx`, `notify-me.tsx`, `/admin/marketing/automations`, product page | Four automations with switch, delay, window, cap, texts, dry run, results; back-in-stock form on the product page. Bengali texts first pass. |
| FR-AC04 Campaign results | Done, Partial for delivery | `campaign-detail.tsx` (results panel), `campaigns-list.tsx`, `settings-view.tsx` | Delivered equals sent in the sandbox; last-touch attribution window shown and editable. |
